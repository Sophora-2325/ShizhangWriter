using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using NovelGm.Desktop.Controls;

namespace NovelGm.Desktop.Api;

/// <summary>正文编辑器上的粘贴、拖入、插画选中、查找定位、格式和批注。</summary>
internal static class BodyEditing
{
    /// <summary>Ctrl+H 请求打开统一查找面板（本章范围）；外壳订阅后切左栏查找。</summary>
    public static event Action? FindRequested;

    /// <summary>批注定位前由外壳把正文区打开。正文区被收起时视口高度是 0，滚动会被跳过。</summary>
    public static event Action? EnsureBodyVisible;

    private static RichTextBox? _editor;
    private static Func<bool>? _canEdit;
    private static Func<string?>? _workRoot;
    private static Func<int?>? _chapter;
    private static Func<FormatToolbar?>? _bar;
    private static Action? _changed;
    private static Image? _selected;
    private static FigureAdorner? _adorner;
    private static AdornerLayer? _selectionLayer;
    private static object? _previewRequest;
    private static Window? _previewWindow;
    private static bool _plainPaste;
    private static bool _matchPaste;
    private static bool _tracking;

    internal static bool Tracking
    {
        get => _tracking;
        set => _tracking = value;
    }
    private static bool _quiet;
    private static readonly SemaphoreSlim ImageQueue = new(1);
    private static List<BodyReview.Note> _notes = new();

    public static bool Quiet
    {
        get => _quiet;
        set => _quiet = value;
    }

    public static void Attach(RichTextBox editor, Func<bool> canEdit, Func<string?> workRoot, Func<int?> chapter, Func<FormatToolbar?> bar, Action changed)
    {
        ClearSelection();
        _editor = editor;
        _canEdit = canEdit;
        _workRoot = workRoot;
        _chapter = chapter;
        _bar = bar;
        _changed = changed;
        // 默认 false 会禁用文档内的 Image，真实鼠标命中停在编辑器，图片处理器永远收不到点击。
        editor.IsDocumentEnabled = true;
        BodyPresentation.Attach(editor);
        DataObject.AddPastingHandler(editor, OnPaste);
        editor.PreviewKeyDown += OnKey;
        editor.PreviewTextInput += OnTrackedInput;
        TextCompositionManager.AddPreviewTextInputStartHandler(editor, (_, _) => MarkComposing());
        TextCompositionManager.AddPreviewTextInputUpdateHandler(editor, (_, _) => MarkComposing());
        editor.AddHandler(UIElement.PreviewTextInputEvent, new TextCompositionEventHandler((_, _) => _composing = false), true);
        editor.LostKeyboardFocus += (_, _) => _composing = false;
        CommandManager.AddPreviewExecutedHandler(editor, OnTrackedCommand);
        editor.PreviewMouseLeftButtonDown += OnMouseDown;
        Window? host = null;
        editor.Loaded += (_, _) =>
        {
            if (host is not null) host.PreviewMouseLeftButtonDown -= OnMouseDown;
            host = Window.GetWindow(editor);
            if (host is not null) host.PreviewMouseLeftButtonDown += OnMouseDown;
        };
        editor.Unloaded += (_, _) =>
        {
            if (host is not null) host.PreviewMouseLeftButtonDown -= OnMouseDown;
            host = null;
            if (_editor != editor) return;
            ClearSelection();
            _previewRequest = null;
        };
        HookImageInput();
        editor.AllowDrop = true;
        editor.PreviewDragOver += (_, e) => { if (CanTake(e.Data)) { e.Effects = DragDropEffects.Copy; e.Handled = true; } };
        editor.Drop += async (_, e) =>
        {
            e.Handled = true;
            try { await OnDrop(e); }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or OutOfMemoryException or IOException)
            {
                Warn(L.T("Fmt.ClipboardBusy"));
            }
        };
        editor.TextChanged += OnText;
        editor.TextChanged += (_, _) => { if (_selected is not null && !BodyFigures.IsAttached(editor, _selected)) ClearSelection(); };
        editor.ContextMenu = new ContextMenu();
        editor.ContextMenuOpening += (_, e) =>
        {
            if (_selected is not null) { ShowFigureMenu(); e.Handled = true; }
            else
            {
                editor.Focus(); var menu = BuildTextMenu(editor); menu.PlacementTarget = editor; menu.IsOpen = true; e.Handled = true;
            }
        };
        editor.PreviewKeyUp += (_, e) =>
        {
            if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || editor.IsReadOnly) return;
            var doc = editor.Document;
            editor.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (editor.Document != doc || editor.IsReadOnly || editor.CaretPosition.Paragraph is not { } p) return;
                if (!BodyParagraphs.IsBody(p, editor.FontSize) && new TextRange(p.ContentStart, p.ContentEnd).Text.Trim().Length != 0) return;
                editor.BeginChange();
                try
                {
                    var size = editor.Selection.GetPropertyValue(TextElement.FontSizeProperty) is double current ? current : p.FontSize;
                    p.TextAlignment = TextAlignment.Left; p.TextIndent = size * 2;
                }
                finally { editor.EndChange(); }
            }));
        };
        editor.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) =>
        {
            _adorner?.InvalidateArrange();
            _adorner?.InvalidateVisual();
        }));
    }

    internal static ContextMenu BuildTextMenu(RichTextBox editor)
    {
        var menu = new ContextMenu();
        if (_bar?.Invoke() is { } bar)
        {
            menu.Items.Add(new MenuItem { Header = bar.BuildFormattingRow(), StaysOpenOnClick = true, Padding = new Thickness(2) });
            menu.Items.Add(new Separator());
        }
        foreach (var spec in new (string Key, RoutedUICommand Command)[]
            { ("Edit.Cut", ApplicationCommands.Cut), ("Read.Copy", ApplicationCommands.Copy), ("Fmt.PasteKeep", ApplicationCommands.Paste), ("Edit.SelectAll", ApplicationCommands.SelectAll) })
            menu.Items.Add(new MenuItem { Header = L.T(spec.Key), Command = spec.Command, CommandTarget = editor });
        menu.Items.Add(new Separator());
        var insert = Item(L.T("Fmt.InsertImage"), () => _bar?.Invoke()?.RequestImage()); insert.IsEnabled = !editor.IsReadOnly; menu.Items.Add(insert);
        menu.Items.Add(new Separator());
        var comment = Item(L.T("Fmt.CommentAdd"), () => QueueCommentFromMenu());
        comment.IsEnabled = CanAddComment && !editor.IsReadOnly;
        menu.Items.Add(comment);
        return menu;
    }

    /// <summary>菜单关闭时会把焦点交回去，同步弹窗可能丢掉选区。先记下引用，等菜单关掉再问批注。</summary>
    private static void QueueCommentFromMenu()
    {
        if (!TryCaptureComment(out var quote, out var offset, out var anchor)) return;
        var editor = _editor;
        var root = Root();
        var no = _chapter?.Invoke();
        if (editor is null || root is null || no is not int chapter) return;
        editor.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (_editor != editor || Root() != root || _chapter?.Invoke() != chapter) return;
            SaveComment(editor, root, chapter, quote, offset, anchor);
        });
    }

    /// <summary>右侧「功能」栏里「编辑与批注」组的命令。Key 是文案键；Label 非空时按当前状态给出文案。</summary>
    public sealed record Command(string Group, string Key, Action<FrameworkElement> Run, Func<string>? Label = null);

    /// <summary>功能区只剩编辑与批注四项；发布/版本/查找/敏感词由 ToolsPanel 自己发事件。</summary>
    public static IReadOnlyList<Command> Commands() => new Command[]
    {
        new("Tools.GroupEditComment", "Tools.Spacing", _ => ShowSpacing()),
        new("Tools.GroupEditComment", "Tools.Invisibles", _ => ToggleInvisibles()),
        new("Tools.GroupEditComment", "Tools.CommentAdd", _ => AddComment()),
        new("Tools.GroupEditComment", "Tools.CommentList", _ => ShowComments()),
    };

    public static void ShowNotes()
    {
        var root = _workRoot?.Invoke();
        var no = _chapter?.Invoke();
        _commentsWindow?.Close();
        _notes = new List<BodyReview.Note>();
        if (root is not null && no is not null)
        {
            try { _notes = BodyReview.LoadNotes(root, no.Value); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                Warn(L.T("Fmt.NotesUnreadable", ex.Message));
            }
        }
        if (_editor is not null) BodyReview.Highlight(_editor.Document, _notes);
    }

    /// <summary>批注写盘；失败时提示并返回 false，调用方据此回滚内存中的列表。</summary>
    private static bool TrySaveNotes(string root, int chapter, IReadOnlyList<BodyReview.Note> notes)
    {
        try
        {
            BodyReview.SaveNotes(root, chapter, notes);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Warn(L.T("Fmt.NotesSaveFailed", ex.Message));
            return false;
        }
    }

    private static void Warn(string message)
    {
        var owner = _editor is null ? null : Window.GetWindow(_editor);
        if (owner is null) MessageBox.Show(message, L.T("App.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        else MessageBox.Show(owner, message, L.T("App.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static bool Editable => _editor is not null && _canEdit?.Invoke() == true;

    /// <summary>「添加批注」可用性：可编辑且正文当前有选区。</summary>
    public static bool CanAddComment => Editable && _editor is not null && !_editor.Selection.IsEmpty;

    /// <summary>非打印字符当前是否开启，供工具台状态位读取。</summary>
    public static bool InvisiblesOn => BodyReview.ShowInvisibles;

    // 输入法组字期间改文档或挪光标，会让 WPF TextStore 的字数和输入法对不上而 FailFast。
    private static bool _composing;
    private static DateTime _composeSeen;

    internal static bool IsComposing => _composing && DateTime.UtcNow - _composeSeen < TimeSpan.FromSeconds(15);

    internal static bool IsComposingIn(FlowDocument document) => IsComposing && _editor?.Document == document;

    private static void MarkComposing()
    {
        _composing = true;
        _composeSeen = DateTime.UtcNow;
    }

    /// <summary>异步操作（插图、听写）回到界面线程后、动正文之前调用：等当前组字结束。
    /// 组字被放弃且没有任何事件时，15 秒无更新视为已结束。</summary>
    internal static async Task CompositionIdleAsync()
    {
        var waited = false;
        while (_composing && DateTime.UtcNow - _composeSeen < TimeSpan.FromSeconds(15))
        {
            waited = true;
            await Task.Delay(100);
        }
        _composing = false;
        if (waited) await Dispatcher.Yield(DispatcherPriority.Background);
    }

    /// <summary>在记录的位置插入且不打断用户：等组字结束；用户光标已经去了别处时，插完把原选区放回去。
    /// 返回插入后的位置（供连续插入），目标失效返回 null。</summary>
    private static async Task<TextPointer?> InsertAtTargetAsync(ImportTarget target, Action insert)
    {
        await CompositionIdleAsync();
        if (!target.Current) return null;
        var editor = target.Editor;
        var start = editor.Selection.Start.GetPositionAtOffset(0, LogicalDirection.Backward);
        var end = editor.Selection.End.GetPositionAtOffset(0, LogicalDirection.Forward);
        var elsewhere = start is not null && end is not null
            && (!editor.Selection.IsEmpty || start.CompareTo(target.Position) != 0);
        if (!target.Restore()) return null;
        insert();
        var after = editor.CaretPosition.GetPositionAtOffset(0, LogicalDirection.Backward);
        if (elsewhere)
        {
            try { editor.Selection.Select(start!, end!); }
            catch (ArgumentException) { /* 原选区所在段落已被删掉 */ }
        }
        return after;
    }

    private static void Touch()
    {
        _changed?.Invoke();
    }

    private static void OnKey(object sender, KeyEventArgs e)
    {
        if (_editor is null) return;
        var mods = Keyboard.Modifiers;
        // 查找不依赖可编辑状态：只读章节（已作废）也应该能查
        if (mods == ModifierKeys.Control && e.Key == Key.H)
        {
            // 内嵌查找窗口已删除：交给统一查找面板（本章范围）
            FindRequested?.Invoke();
            e.Handled = true;
            return;
        }
        // 光标用键盘移走后图片选中就失效，否则后续 Back/Delete 会删掉看不见选中的图
        if (_selected is not null && e.Key is Key.Left or Key.Right or Key.Up or Key.Down
                or Key.Home or Key.End or Key.PageUp or Key.PageDown)
            ClearSelection();
        if (!Editable) return;
        if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.V)
        {
            PastePlain();
            e.Handled = true;
            return;
        }
        if ((e.Key == Key.Back || e.Key == Key.Delete) && TryDeleteFigure(e.Key == Key.Back))
        {
            e.Handled = true;
            return;
        }
        if (_selected is not null)
        {
            if (mods == ModifierKeys.Control && e.Key == Key.C && Root() is { } copyRoot)
            {
                if (!BodyFigures.Copy(_selected, copyRoot)) Warn(L.T("Fmt.ClipboardBusy"));
                e.Handled = true;
                return;
            }
            if (mods == ModifierKeys.Control && e.Key == Key.X && Root() is { } cutRoot)
            {
                e.Handled = true;
                if (!BodyFigures.Copy(_selected, cutRoot)) { Warn(L.T("Fmt.ClipboardBusy")); return; }
                var image = _selected;
                ClearSelection();
                BodyFigures.Delete(_editor, image);
                Touch();
                return;
            }
        }
    }

    private static bool Strike(bool backward)
    {
        if (_editor is null) return false;
        var sel = _editor.Selection;
        if (sel.IsEmpty)
        {
            var dir = backward ? LogicalDirection.Backward : LogicalDirection.Forward;
            var other = sel.Start.GetNextInsertionPosition(dir);
            if (other is null) return false;
            sel.Select(backward ? other : sel.Start, backward ? sel.Start : other);
        }
        var runs = sel.Text;
        if (runs.Length == 0) return false;
        var allDeleted = true;
        var allInserted = true;
        var pos = sel.Start;
        while (pos is not null && pos.CompareTo(sel.End) < 0)
        {
            if (pos.Parent is Run run && run.Text?.Length > 0)
            {
                allDeleted &= BodyReview.IsDeletion(run);
                allInserted &= BodyReview.IsInsertion(run);
            }
            var next = pos.GetNextContextPosition(LogicalDirection.Forward);
            if (next is null || next.CompareTo(pos) == 0) break;
            pos = next;
        }
        if (allDeleted || allInserted)
        {
            sel.Text = "";
            Touch();
            return true;
        }
        _quiet = true;
        try { BodyReview.MarkDeleted(sel); }
        finally { _quiet = false; }
        _editor.CaretPosition = sel.End;
        Touch();
        return true;
    }

    private static readonly List<TextRange> _pendingMarks = new();
    private static bool _markScheduled;

    private static void OnTrackedInput(object sender, TextCompositionEventArgs e)
    {
        if (_quiet || !_tracking || !Editable || _editor is null || e.Text.Length == 0) return;
        e.Handled = true;
        _quiet = true;
        try
        {
            _editor.BeginChange();
            try
            {
                _editor.Selection.Text = e.Text;
                BodyReview.MarkInserted(_editor.Selection);
                _editor.CaretPosition = _editor.Selection.End;
            }
            finally { _editor.EndChange(); }
        }
        finally { _quiet = false; }
        Touch();
    }

    private static void OnTrackedCommand(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command != EditingCommands.Backspace && e.Command != EditingCommands.Delete) return;
        var backward = e.Command == EditingCommands.Backspace;
        if (TryDeleteFigure(backward))
        {
            e.Handled = true;
            return;
        }
        if (_quiet || !_tracking || !Editable) return;
        if (Strike(backward)) e.Handled = true;
    }

    /// <summary>选中的插画，或光标紧挨着的插画。交给系统退格/删除会触发文字锁重入。</summary>
    private static bool TryDeleteFigure(bool backward)
    {
        if (!Editable || _editor is null) return false;
        var image = _selected is not null && BodyFigures.IsAttached(_editor, _selected) ? _selected : FigureBesideCaret(backward);
        if (image is null) return false;
        ClearSelection();
        BodyFigures.Delete(_editor, image);
        Touch();
        return true;
    }

    private static Image? FigureBesideCaret(bool backward)
    {
        if (_editor is null || !_editor.Selection.IsEmpty) return null;
        var caret = _editor.CaretPosition;
        if (caret is null) return null;
        var dir = backward ? LogicalDirection.Backward : LogicalDirection.Forward;
        var image = ImageChild(caret.GetAdjacentElement(dir));
        if (image is not null) return image;
        if (caret.Paragraph is not { } paragraph) return null;
        var atStart = new TextRange(paragraph.ContentStart, caret).Text.Length == 0;
        var atEnd = new TextRange(caret, paragraph.ContentEnd).Text.Length == 0;
        var block = backward ? (atStart ? paragraph.PreviousBlock : null) : (atEnd ? paragraph.NextBlock : null);
        return ImageChild(block);
    }

    private static Image? ImageChild(DependencyObject? element)
    {
        Image? image = element switch
        {
            Image direct => direct,
            InlineUIContainer { Child: Image inline } => inline,
            BlockUIContainer { Child: Image block } => block,
            _ => null,
        };
        return image is not null && BodyFigures.AsInfo(image) is not null ? image : null;
    }

    private static void OnText(object sender, TextChangedEventArgs e)
    {
        // Formatting inside TextChanged re-enters the text store and WPF terminates the process.
        if (_quiet || BodyReview.IsBusy || !_tracking || !Editable || _editor is null) return;
        foreach (var change in e.Changes)
        {
            if (change.AddedLength <= 0) continue;
            var start = _editor.Document.ContentStart.GetPositionAtOffset(change.Offset);
            var end = _editor.Document.ContentStart.GetPositionAtOffset(change.Offset + change.AddedLength);
            if (start is null || end is null) continue;
            _pendingMarks.Add(new TextRange(start, end));
        }
        if (_markScheduled || _pendingMarks.Count == 0) return;
        _markScheduled = true;
        var editor = _editor;
        editor.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            _markScheduled = false;
            var marks = _pendingMarks.ToList();
            _pendingMarks.Clear();
            if (editor != _editor || !_tracking || editor.IsReadOnly) return;
            _quiet = true;
            try
            {
                editor.BeginChange();
                try
                {
                    foreach (var range in marks)
                        try { BodyReview.MarkInserted(range); } catch (Exception) { /* 这段在换章时已经失效 */ }
                }
                finally { editor.EndChange(); }
            }
            finally { _quiet = false; }
        });
    }

    private static void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        // 读剪贴板数据可能抛 COM 异常（延迟渲染的源程序已退出、剪贴板被占用）
        try { HandlePaste(e); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or OutOfMemoryException)
        {
            e.CancelCommand();
            Warn(L.T("Fmt.ClipboardBusy"));
        }
    }

    private static void HandlePaste(DataObjectPastingEventArgs e)
    {
        if (!Editable || _editor is null) return;
        var plain = _plainPaste;
        var match = _matchPaste;
        _plainPaste = _matchPaste = false;
        if (plain || match)
        {
            e.CancelCommand();
            if (e.DataObject.GetDataPresent(DataFormats.UnicodeText))
                PasteText(_editor, e.DataObject.GetData(DataFormats.UnicodeText) as string ?? "", match);
            return;
        }
        var root = Root();
        if (root is not null && e.DataObject.GetDataPresent(BodyFigures.ClipboardFormat))
        {
            e.CancelCommand();
            var editor = _editor;
            var target = CaptureImport(root);
            var data = new DataObject(BodyFigures.ClipboardFormat, e.DataObject.GetData(BodyFigures.ClipboardFormat));
            _ = ImageOperation(async () =>
            {
                if (target?.Current != true) return;
                if (await BodyFigures.TryPasteClipboardFigureAsync(editor, root, data,
                        () => target?.Restore() == true, CompositionIdleAsync)) Touch();
            });
            return;
        }
        if (root is not null && e.DataObject.GetDataPresent(DataFormats.Bitmap)
            && !e.DataObject.GetDataPresent(DataFormats.UnicodeText))
        {
            e.CancelCommand();
            if (e.DataObject.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
            {
                var target = CaptureImport(root);
                if (!bitmap.IsFrozen) { bitmap = bitmap.CloneCurrentValue(); bitmap.Freeze(); }
                if (target is not null) _ = ImageOperation(() => StoreBitmap(target, bitmap));
            }
            return;
        }
        if (root is not null && (e.DataObject.GetDataPresent(DataFormats.Rtf)
            || e.DataObject.GetDataPresent(DataFormats.Xaml) || e.DataObject.GetDataPresent(DataFormats.XamlPackage)))
        {
            var editor = _editor;
            var document = editor.Document;
            editor.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => AdoptForeignImages(editor, document, root));
        }
    }

    /// <summary>从 Word/网页粘贴进来的图片没有插图标记，保存时会被丢掉：存成插图文件并换上标记，存不了的移除。</summary>
    private static void AdoptForeignImages(RichTextBox editor, FlowDocument document, string root)
    {
        if (editor.IsReadOnly || editor.Document != document || _editor != editor || Root() != root) return;
        var foreign = new List<Image>();
        CollectForeign(document, foreign);
        if (foreign.Count == 0) return;
        foreach (var image in foreign)
        {
            if (!BodyFigures.IsAttached(editor, image)) continue;
            BodyFigures.Stored? stored = null;
            if (image.Source is BitmapSource bitmap)
            {
                var temp = Path.Combine(Path.GetTempPath(), "novel-gm-paste-" + Guid.NewGuid().ToString("N") + ".png");
                try
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var stream = File.Create(temp)) encoder.Save(stream);
                    stored = BodyFigures.Store(root, temp);
                }
                catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or UnauthorizedAccessException) { }
                finally { try { File.Delete(temp); } catch (Exception) { /* 临时文件 */ } }
            }
            if (stored is { } file) BodyFigures.ReplaceFile(editor, image, file, root);
            else BodyFigures.Delete(editor, image);
        }
        Touch();
    }

    private static void CollectForeign(DependencyObject node, List<Image> into)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
        {
            if (child is Image image) { if (BodyFigures.AsInfo(image) is null) into.Add(image); }
            else CollectForeign(child, into);
        }
    }

    internal static void PasteText(RichTextBox editor, string text, bool matchBody = false)
    {
        if (editor.IsReadOnly) return;
        editor.BeginChange();
        try
        {
            editor.Selection.Text = text;
            if (matchBody) _bar?.Invoke()?.MatchBody(editor.Selection);
            editor.CaretPosition = editor.Selection.End;
        }
        finally { editor.EndChange(); }
        Touch();
    }

    private static void Paste(bool keep)
    {
        if (!Editable || _editor is null) return;
        _matchPaste = !keep;
        try { _editor.Paste(); }
        finally { _plainPaste = _matchPaste = false; }
    }

    private static void PastePlain()
    {
        if (!Editable || _editor is null) return;
        _plainPaste = true;
        try { _editor.Paste(); }
        finally { _plainPaste = _matchPaste = false; }
    }

    private static async Task OnDrop(DragEventArgs e)
    {
        if (!Editable || _editor is null) return;
        var root = Root();
        if (root is null) return;
        e.Handled = true;
        var point = e.GetPosition(_editor);
        var pos = _editor.GetPositionFromPoint(point, snapToText: true);
        if (pos is not null) _editor.CaretPosition = pos;
        var target = CaptureImport(root);
        if (target is null) return;
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            var paths = files.Where(IsImageFile).ToArray();
            await ImageOperation(() => ImportFilesTargetAsync(target, paths));
            return;
        }
        if (e.Data.GetDataPresent(DataFormats.Bitmap) && e.Data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
        {
            if (!bitmap.IsFrozen) { bitmap = bitmap.CloneCurrentValue(); bitmap.Freeze(); }
            await ImageOperation(() => StoreBitmap(target, bitmap));
        }
    }

    private sealed record ImportTarget(RichTextBox Editor, FlowDocument Document, string RootPath, int? Chapter, TextPointer Position)
    {
        public bool Current => Editable && _editor == Editor && Root() == RootPath && _chapter?.Invoke() == Chapter && Editor.Document == Document;
        public bool Restore()
        {
            if (!Current) return false;
            Editor.CaretPosition = Position;
            return true;
        }
    }

    private static ImportTarget? CaptureImport(string root) => _editor is null ? null :
        new(_editor, _editor.Document, root, _chapter?.Invoke(), _editor.CaretPosition.GetPositionAtOffset(0, LogicalDirection.Backward)!);

    internal static async Task ImportFilesAsync(string root, IEnumerable<string> files)
    {
        var target = CaptureImport(root);
        if (target is null) return;
        await ImportFilesTargetAsync(target, files);
    }

    private static async Task ImportFilesTargetAsync(ImportTarget target, IEnumerable<string> files)
    {
        foreach (var path in files.ToArray())
        {
            if (!target.Current) break;
            var stored = await BodyFigures.StoreAsync(target.RootPath, path);
            var after = await InsertAtTargetAsync(target, () => BodyFigures.InsertAtCaret(target.Editor, stored));
            if (after is null) break;
            Touch();
            target = target with { Position = after };
        }
    }

    private static async Task StoreBitmap(ImportTarget target, BitmapSource bitmap)
    {
        if (!target.Current) return;
        // Clipboard data belongs to the UI thread. Freeze a snapshot before handing it off.
        if (!bitmap.IsFrozen) { bitmap = bitmap.CloneCurrentValue(); bitmap.Freeze(); }
        var snapshot = bitmap;
        var temp = Path.Combine(Path.GetTempPath(), "novel-gm-clip-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            var stored = await ImageAssets.Background(() =>
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(snapshot));
                using (var stream = File.Create(temp)) encoder.Save(stream);
                return BodyFigures.Store(target.RootPath, temp);
            });
            if (await InsertAtTargetAsync(target, () => BodyFigures.InsertAtCaret(target.Editor, stored)) is null) return;
            Touch();
        }
        finally
        {
            try { File.Delete(temp); } catch (Exception) { /* 临时文件 */ }
        }
    }

    private static async Task ImageOperation(Func<Task> operation)
    {
        await ImageQueue.WaitAsync();
        var bar = _bar?.Invoke();
        bar?.ShowBusy(L.T("Fmt.InsertImageBusy"));
        try { await operation(); }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(_editor), ex.Message, L.T("Fmt.InsertImage"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { bar?.HideBusy(); ImageQueue.Release(); }
    }

    private static bool _imageHooks;

    /// <summary>
    /// 插画放在文档孤岛里，鼠标路由到不了外层编辑器。直接接管图片点击，阻止系统文字选区处理。
    /// </summary>
    private static void HookImageInput()
    {
        if (_imageHooks) return;
        _imageHooks = true;
        EventManager.RegisterClassHandler(typeof(Image), UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnImageMouseDown), true);
        EventManager.RegisterClassHandler(typeof(Image), UIElement.MouseLeftButtonDownEvent, new MouseButtonEventHandler(OnImageClick), true);
    }

    private static void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_editor is null || _selected is null || e.OriginalSource is not DependencyObject hit) return;
        for (DependencyObject? node = hit; node is not null;)
        {
            // 缩放把手属于选中框，点击它仍然是在操作当前图片。
            if (node == _adorner) return;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        if (BodyFigures.ImageOf(hit) is { } image && BodyFigures.IsAttached(_editor, image)) return;
        ClearSelection();
    }

    private static void OnImageMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_editor is null) return;
        var image = BodyFigures.ImageOf(e.OriginalSource as DependencyObject);
        if (image is null || !BelongsToEditor(image)) return;
        e.Handled = true;
        Select(image);
    }

    private static void OnImageClick(object sender, MouseButtonEventArgs e)
    {
        if (_editor is null) return;
        var image = BodyFigures.ImageOf(e.OriginalSource as DependencyObject);
        if (image is null || !BelongsToEditor(image)) return;
        // 连点只保持选中。预览由显式菜单命令触发，不能在双击时排队加载和开窗。
        e.Handled = true;
    }

    private static bool BelongsToEditor(DependencyObject source)
    {
        while (true)
        {
            if (source == _editor) return true;
            if (source is Visual or System.Windows.Media.Media3D.Visual3D)
            {
                var parent = VisualTreeHelper.GetParent(source) ?? LogicalTreeHelper.GetParent(source);
                if (parent is null) return false;
                source = parent;
                continue;
            }
            var logical = LogicalTreeHelper.GetParent(source);
            if (logical is null) return false;
            source = logical;
        }
    }

    internal static async Task PreviewFigureAsync(Image image)
    {
        var editor = _editor;
        var root = Root();
        if (editor is null || root is null || !BodyFigures.IsAttached(editor, image)
            || BodyFigures.AsInfo(image) is not { } info) return;
        if (_previewWindow is { IsVisible: true }) { _previewWindow.Activate(); return; }
        if (_previewRequest is not null) return;
        var path = BodyFigures.OriginalOrPreview(info, root);
        if (path is null) return;
        var owner = Window.GetWindow(editor);
        var document = editor.Document;
        var chapter = _chapter?.Invoke();
        var request = _previewRequest = new object();
        try
        {
            var bitmap = await ImageAssets.LoadAsync(path, 2560).ConfigureAwait(false);
            if (editor.Dispatcher.HasShutdownStarted) return;
            await editor.Dispatcher.InvokeAsync(() =>
            {
                if (_previewRequest != request || Root() != root || _editor != editor
                    || editor.Document != document || _chapter?.Invoke() != chapter
                    || owner?.IsVisible != true || !BodyFigures.IsAttached(editor, image)) return;
                var window = new Window
                {
                    Title = string.IsNullOrEmpty(info.Alt) ? L.T("Fmt.InsertImage") : info.Alt,
                    Content = new ScrollViewer
                    {
                        Content = new Image { Source = bitmap, Stretch = Stretch.Uniform },
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    },
                    Width = 960,
                    Height = 720,
                    Owner = owner,
                };
                _previewWindow = window;
                window.Closed += (_, _) => { if (_previewWindow == window) _previewWindow = null; };
                window.Show();
            });
        }
        catch (Exception ex) { System.Diagnostics.Trace.TraceWarning("Image preview failed: {0}", ex.Message); }
        finally { if (_previewRequest == request) _previewRequest = null; }
    }

    private static void Select(Image image)
    {
        if (_editor is null || !BodyFigures.IsAttached(_editor, image) || ReferenceEquals(_selected, image)) return;
        ClearSelection();
        // null 表示未选中；保存图片身份也适用于没有装饰层的编辑器，不另设容易失步的 bool。
        _selected = image;
        _editor.Focus();
        var layer = AdornerLayer.GetAdornerLayer(_editor);
        if (layer is null) return;
        FigureAdorner? adorner = null;
        adorner = new FigureAdorner(_editor, image, width =>
        {
            var root = Root();
            if (root is null || _editor is null || !Editable || _adorner != adorner
                || _selected != image || !BodyFigures.IsAttached(_editor, image)) return;
            var replacement = BodyFigures.SetWidth(_editor, image, width, root);
            Touch();
            if (replacement is not null) Select(replacement);
        });
        _adorner = adorner;
        _selectionLayer = layer;
        layer.Add(_adorner);
    }

    private static void ClearSelection()
    {
        var adorner = _adorner;
        var layer = _selectionLayer;
        _adorner = null;
        _selectionLayer = null;
        _selected = null;
        if (adorner is null || layer is null) return;
        // 卸载通知可能滞后；旧窗口的装饰层必须由它自己的界面线程清理。
        if (layer.Dispatcher.CheckAccess()) layer.Remove(adorner);
        else if (!layer.Dispatcher.HasShutdownStarted)
            layer.Dispatcher.BeginInvoke(new Action(() => layer.Remove(adorner)));
    }

    private static void ShowFigureMenu()
    {
        if (_selected is null || _editor is null || !BodyFigures.IsAttached(_editor, _selected)) { ClearSelection(); return; }
        var image = _selected;
        var menu = new ContextMenu();
        menu.Items.Add(Item(L.T("Fmt.PreviewFigure"), () => _ = PreviewFigureAsync(image)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(L.T("Fmt.CopyFigure"), () =>
        {
            if (Root() is { } root && !BodyFigures.Copy(image, root)) Warn(L.T("Fmt.ClipboardBusy"));
        }));
        menu.Items.Add(Item(L.T("Fmt.CutFigure"), () =>
        {
            if (Root() is not { } root) return;
            if (!BodyFigures.Copy(image, root)) { Warn(L.T("Fmt.ClipboardBusy")); return; }
            ClearSelection();
            BodyFigures.Delete(_editor, image);
            Touch();
        }));
        menu.Items.Add(Item(L.T("Fmt.DeleteFigure"), () => { ClearSelection(); BodyFigures.Delete(_editor, image); Touch(); }));
        menu.Items.Add(Item(L.T("Fmt.ReplaceFigure"), () => _ = ImageOperation(() => Replace(image))));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(L.T("Fmt.AlignLeft"), () => Align(image, TextAlignment.Left)));
        menu.Items.Add(Item(L.T("Fmt.AlignCenter"), () => Align(image, TextAlignment.Center)));
        menu.Items.Add(Item(L.T("Fmt.AlignRight"), () => Align(image, TextAlignment.Right)));
        menu.Items.Add(Item(L.T("Fmt.Width"), () => AskWidth(image)));
        menu.Items.Add(Item(L.T("Fmt.ResetSize"), () => Resize(image, 0)));
        menu.Items.Add(Item(L.T("Fmt.AltText"), () => AskAlt(image)));
        menu.PlacementTarget = _editor;
        menu.IsOpen = true;
    }

    private static void Align(Image image, TextAlignment align)
    {
        var root = Root();
        if (root is null || _editor is null || !Editable) return;
        var replacement = BodyFigures.SetAlign(_editor, image, align, root);
        Touch();
        if (replacement is not null) Select(replacement);
    }

    private static async Task Replace(Image image)
    {
        var root = Root();
        if (root is null || _editor is null || !Editable || !BodyFigures.IsAttached(_editor, image)) return;
        var dlg = new OpenFileDialog { Filter = L.T("Char.ImageFilter") };
        if (dlg.ShowDialog(Window.GetWindow(_editor)) != true) return;
        var chapter = _chapter?.Invoke();
        var editor = _editor;
        var stored = await BodyFigures.StoreAsync(root, dlg.FileName);
        if (!Editable || Root() != root || _chapter?.Invoke() != chapter || _editor != editor) return;
        var attached = BodyFigures.IsAttached(editor, image);
        if (!attached) return;
        BodyFigures.ReplaceFile(_editor, image, stored, root);
        ClearSelection();
        Touch();
    }

    private static void AskWidth(Image image)
    {
        var root = Root();
        if (root is null || _editor is null) return;
        var current = image.Width > 0 ? image.Width : image.ActualWidth;
        if (!Ask(L.T("Fmt.Width"), current.ToString("0"), out var text)) return;
        if (!double.TryParse(text, out var width)) return;
        Resize(image, width);
    }

    private static void Resize(Image image, double width)
    {
        if (!Editable || _editor is null || Root() is not { } root) return;
        var replacement = BodyFigures.SetWidth(_editor, image, width, root);
        if (replacement is null) return;
        Select(replacement); Touch();
    }

    private static void AskAlt(Image image)
    {
        if (!Editable || _editor is null || !BodyFigures.IsAttached(_editor, image)) return;
        var info = BodyFigures.AsInfo(image);
        if (!Ask(L.T("Fmt.AltText"), info?.Alt ?? "", out var text)) return;
        BodyFigures.SetAlt(image, text);
        image.ToolTip = text.Length == 0 ? null : text;
        Touch();
    }

    internal static int ReplaceAll(RichTextBox editor, string query, string replacement, bool caseSensitive = false)
    {
        if (editor.IsReadOnly) return 0;
        var map = new BodyTextMap(editor.Document);
        var hits = map.Matches(query, caseSensitive);
        var ranges = hits.Select(at => map.Range(at, query.Length))
            .Where(r => !caseSensitive || r.Text.Replace("\r\n", "\n") == query.Replace("\r\n", "\n")).ToList();
        editor.BeginChange();
        try { for (var i = ranges.Count - 1; i >= 0; i--) ranges[i].Text = replacement; }
        finally { editor.EndChange(); }
        return ranges.Count;
    }

    /// <summary>按搜索快照中的 Unicode 字符偏移替换勾选结果。先复核全部范围，再从后往前修改；过期时一处也不改。</summary>
    internal static int ReplaceSelected(RichTextBox editor, string query, string replacement,
        string source, IReadOnlyList<int> offsets, bool caseSensitive = false)
    {
        if (editor.IsReadOnly || string.IsNullOrEmpty(query) || RichBody.PlainText(editor.Document) != source) return -1;
        // Python 搜索返回 Unicode 码点下标；WPF 的字符串和指针按 UTF-16 计数。
        var positions = new List<int> { 0 };
        foreach (var rune in source.EnumerateRunes()) positions.Add(positions[^1] + rune.Utf16SequenceLength);
        if (offsets.Any(i => i < 0 || i >= positions.Count)) return -1;
        var wanted = offsets.Select(i => positions[i]).ToHashSet();
        var map = new BodyTextMap(editor.Document);
        var ranges = new List<TextRange>();
        foreach (var at in map.Matches(query, caseSensitive))
        {
            var range = map.Range(at, query.Length);
            if (caseSensitive && range.Text.Replace("\r\n", "\n") != query.Replace("\r\n", "\n")) continue;
            var plainOffset = new TextRange(editor.Document.ContentStart, range.Start).Text.Replace("\r\n", "\n").Length;
            if (wanted.Contains(plainOffset)) ranges.Add(range);
        }
        if (ranges.Count != wanted.Count) return -1;
        editor.BeginChange();
        try { for (var i = ranges.Count - 1; i >= 0; i--) ranges[i].Text = replacement; }
        finally { editor.EndChange(); }
        return ranges.Count;
    }

    /// <summary>统一查找面板的「定位」：在 offset 附近选中最接近的命中并滚动到可见处。
    /// 定位前复核该处仍是 query，文档被改过时返回 false（由外壳提示「结果已失效」）。</summary>
    internal static bool TryLocate(RichTextBox editor, string query, int offset, bool caseSensitive = false)
    {
        if (string.IsNullOrEmpty(query)) return false;
        var map = new BodyTextMap(editor.Document);
        var hits = map.Matches(query, caseSensitive);
        if (hits.Count == 0) return false;
        // offset 来自纯文本或已保存正文，和 map 的计数（插图记一个字符）会有小偏差：取离它最近的一处
        var at = hits.MinBy(i => Math.Abs(i - offset));
        var range = map.Range(at, query.Length);
        // 复核：命中区间必须仍是查询词，否则说明查找结果已过期
        var found = new TextRange(range.Start, range.End).Text.Replace("\r\n", "\n");
        if (!string.Equals(found, query.Replace("\r\n", "\n"), StringComparison.OrdinalIgnoreCase)) return false;
        editor.Focus();
        editor.Selection.Select(range.Start, range.End);
        var rect = range.End.GetCharacterRect(LogicalDirection.Forward);
        editor.ScrollToVerticalOffset(Math.Max(0, editor.VerticalOffset + rect.Top - 40));
        return true;
    }

    /// <summary>正文查找的下一处（保留给旧回归测试与工具栏路径）。</summary>
    private static TextPointer? FindNext(RichTextBox editor, string query, TextPointer? from)
    {
        var map = new BodyTextMap(editor.Document);
        var hits = map.Matches(query);
        if (hits.Count == 0) return null;
        var offset = 0;
        try { offset = map.Offset(from ?? editor.Selection.End); }
        catch (ArgumentException) { /* A find window can outlive its old chapter. */ }
        catch (InvalidOperationException) { }
        var at = hits.FirstOrDefault(i => i >= offset, -1);
        if (at < 0) at = hits[0];
        var range = map.Range(at, query.Length);
        editor.Selection.Select(range.Start, range.End);
        editor.Focus();
        return range.End;
    }

    internal static void ShowSpacing()
    {
        var bar = _bar?.Invoke();
        if (bar is null) return;
        var before = Box("0");
        var after = Box("8");
        var left = Box("0");
        var right = Box("0");
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(Labeled(L.T("Fmt.SpaceBefore"), before));
        panel.Children.Add(Labeled(L.T("Fmt.SpaceAfter"), after));
        panel.Children.Add(Labeled(L.T("Fmt.IndentLeft"), left));
        panel.Children.Add(Labeled(L.T("Fmt.IndentRight"), right));
        var ok = new Button { Content = L.T("Common.OK"), HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true };
        panel.Children.Add(ok);
        var window = new Window
        {
            Title = L.T("Fmt.SpacingBox"),
            Content = panel,
            Width = 280,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(_editor),
            ResizeMode = ResizeMode.NoResize,
        };
        ok.Click += (_, _) =>
        {
            bar.ApplyBox(Num(before), Num(after), Num(left), Num(right));
            Touch();
            window.Close();
        };
        window.ShowDialog();
    }

    internal static void ToggleInvisibles()
    {
        if (_editor is null) return;
        _quiet = true;
        try { BodyReview.SetInvisibles(_editor.Document, !BodyReview.ShowInvisibles); }
        finally { _quiet = false; }
    }

    internal static void AddComment()
    {
        if (_editor is null || !TryCaptureComment(out var quote, out var offset, out var anchor)) return;
        var root = Root();
        var no = _chapter?.Invoke();
        if (root is null || no is null) return;
        SaveComment(_editor, root, no.Value, quote, offset, anchor);
    }

    private static bool TryCaptureComment(out string quote, out int offset, out ReadingAnchor? anchor)
    {
        quote = "";
        offset = 0;
        anchor = null;
        if (!Editable || _editor is null || _editor.IsReadOnly || _editor.Selection.IsEmpty) return false;
        if (Root() is null || _chapter?.Invoke() is null) return false;
        quote = _editor.Selection.Text.Replace("\r\n", "\n");
        if (quote.Length == 0) return false;
        var map = new BodyTextMap(_editor.Document);
        offset = map.Offset(_editor.Selection.Start);
        anchor = ReadingAnchor.Capture(map.Text, offset);
        return true;
    }

    private static void SaveComment(RichTextBox editor, string root, int chapter, string quote, int offset, ReadingAnchor? anchor)
    {
        if (!Editable || _editor != editor || Root() != root || _chapter?.Invoke() != chapter) return;
        if (!Ask(L.T("Fmt.CommentAdd"), "", out var text) || text.Length == 0) return;
        if (_editor != editor || Root() != root || _chapter?.Invoke() != chapter) return;
        var next = _notes.Append(new BodyReview.Note
        {
            Id = Guid.NewGuid().ToString("N"),
            Quote = quote,
            Text = text,
            Start = offset,
            Anchor = anchor,
        }).ToList();
        if (!TrySaveNotes(root, chapter, next)) return;
        _notes = next;
        BodyReview.Highlight(editor.Document, _notes);
    }

    private static Window? _commentsWindow;

    internal static void ShowComments()
    {
        var root = Root();
        var no = _chapter?.Invoke();
        if (root is null || no is null || _editor is null) return;
        _commentsWindow?.Close();
        var notes = _notes;
        var editor = _editor;
        var document = editor.Document;
        var list = new ListBox { DisplayMemberPath = "Text", ItemsSource = notes.ToList(), MinHeight = 160 };
        var remove = new Button { Content = L.T("Fmt.CommentDelete"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var panel = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(remove, Dock.Bottom);
        panel.Children.Add(remove);
        panel.Children.Add(list);
        var window = new Window
        {
            Title = L.T("Fmt.CommentList"),
            Content = panel,
            Width = 320,
            Height = 360,
            Owner = Window.GetWindow(_editor),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        void LocateSelected()
        {
            if (list.SelectedItem is not BodyReview.Note note) return;
            if (_editor?.Document != document) return;
            var chosen = note;
            editor.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (_editor?.Document != document || !ReferenceEquals(list.SelectedItem, chosen)) return;
                LocateComment(editor, chosen);
            });
        }
        list.SelectionChanged += (_, _) =>
        {
            if (Mouse.LeftButton == MouseButtonState.Pressed) return;
            LocateSelected();
        };
        list.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) is not ListBoxItem) return;
            LocateSelected();
        };
        remove.Click += (_, _) =>
        {
            if (list.SelectedItem is not BodyReview.Note note) return;
            if (!Editable || Root() != root || _chapter?.Invoke() != no || _editor?.Document != document) return;
            var next = notes.Where(n => n != note).ToList();
            if (!TrySaveNotes(root, no.Value, next)) return;
            notes = next;
            _notes = next;
            list.ItemsSource = notes.ToList();
            BodyReview.Highlight(document, notes);
        };
        window.Closed += (_, _) => { if (_commentsWindow == window) _commentsWindow = null; };
        _commentsWindow = window;
        window.Show();
    }

    private static void LocateComment(RichTextBox editor, BodyReview.Note note)
    {
        if (string.IsNullOrEmpty(note.Quote)) return;
        EnsureBodyVisible?.Invoke();
        var map = new BodyTextMap(editor.Document);
        var at = BodyPresentation.ResolveNote(map, note);
        if (at is null) return;
        var range = map.Range(at.Value, note.Quote.Length);
        editor.Focus();
        editor.Selection.Select(range.Start, range.End);
        BodyPresentation.ScrollLineToCenter(editor, range.Start);
    }

    private static bool Ask(string title, string initial, out string value)
    {
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 0, 0, 8) };
        var ok = new Button { Content = L.T("Common.OK"), HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(box);
        panel.Children.Add(ok);
        var window = new Window
        {
            Title = title,
            Content = panel,
            Width = 320,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(_editor),
            ResizeMode = ResizeMode.NoResize,
        };
        var accepted = false;
        ok.Click += (_, _) => { accepted = true; window.Close(); };
        window.ShowDialog();
        value = box.Text ?? "";
        return accepted;
    }

    private static string? Root() => _workRoot?.Invoke();

    private static bool CanTake(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) || data.GetDataPresent(DataFormats.Bitmap);

    private static bool IsImageFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".tif" or ".tiff";
    }

    private static MenuItem Item(string text, Action action)
    {
        var item = new MenuItem { Header = text };
        item.Click += (_, _) => action();
        return item;
    }

    private static TextBox Box(string text) => new() { Text = text, Margin = new Thickness(8, 0, 0, 8), Width = 80 };

    private static UIElement Labeled(string label, TextBox box)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = label, Width = 88, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(box);
        return row;
    }

    private static double Num(TextBox box) => double.TryParse(box.Text, out var n) ? n : 0;

    private sealed class FigureAdorner : Adorner
    {
        private const double HandleDiameter = 20;
        private const double HandleTarget = 28;
        private readonly RichTextBox _box;
        private readonly Image _image;
        private readonly Action<double> _commit;
        private readonly VisualCollection _visuals;
        private double _width;
        private bool _dragging;

        public FigureAdorner(RichTextBox box, Image image, Action<double> commit) : base(box)
        {
            _box = box;
            _image = image;
            _commit = commit;
            _width = image.ActualWidth > 1 ? image.ActualWidth : image.Width;
            _visuals = new VisualCollection(this);
            IsHitTestVisible = true;
            SnapsToDevicePixels = true;
            // 四角在前，四边中点在后。拖角的测试按这个顺序取把手。
            for (var i = 0; i < 8; i++)
            {
                var handle = i;
                var thumb = new Thumb
                {
                    Width = HandleTarget,
                    Height = HandleTarget,
                    Cursor = handle switch
                    {
                        0 or 3 => Cursors.SizeNWSE,
                        1 or 2 => Cursors.SizeNESW,
                        4 or 5 => Cursors.SizeWE,
                        _ => Cursors.SizeNS,
                    },
                    Template = HandleTemplate,
                };
                thumb.DragStarted += (_, _) => { _dragging = true; _width = _image.ActualWidth > 1 ? _image.ActualWidth : 120; };
                thumb.DragDelta += (_, e) =>
                {
                    var (horizontal, vertical) = handle switch
                    {
                        0 => (-e.HorizontalChange, -e.VerticalChange),
                        1 => (e.HorizontalChange, -e.VerticalChange),
                        2 => (-e.HorizontalChange, e.VerticalChange),
                        3 => (e.HorizontalChange, e.VerticalChange),
                        4 => (-e.HorizontalChange, 0d),
                        5 => (e.HorizontalChange, 0d),
                        6 => (0d, -e.VerticalChange),
                        _ => (0d, e.VerticalChange),
                    };
                    var delta = Math.Abs(horizontal) >= Math.Abs(vertical) ? horizontal : vertical;
                    _width = Math.Clamp(_width + delta, 40, 1600);
                    InvalidateArrange();
                    InvalidateVisual();
                };
                thumb.DragCompleted += (_, e) =>
                {
                    _dragging = false;
                    if (!e.Canceled) _box.Dispatcher.BeginInvoke(new Action(() => _commit(_width)));
                };
                _visuals.Add(thumb);
            }
        }

        private static readonly Brush FrameBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x00, 0xA8, 0xFF)));
        private static readonly Pen FramePen = Freeze(new Pen(FrameBrush, 2));
        private static readonly ControlTemplate HandleTemplate = BuildHandleTemplate();

        private static ControlTemplate BuildHandleTemplate()
        {
            // 可点击区域比圆点大，边缘处容易抓住，但视觉上仍是白底蓝边的圆点。
            var target = new FrameworkElementFactory(typeof(Border));
            target.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            var mark = new FrameworkElementFactory(typeof(System.Windows.Shapes.Ellipse));
            mark.SetValue(FrameworkElement.WidthProperty, HandleDiameter);
            mark.SetValue(FrameworkElement.HeightProperty, HandleDiameter);
            mark.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            mark.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            mark.SetValue(System.Windows.Shapes.Ellipse.FillProperty, Brushes.White);
            mark.SetValue(System.Windows.Shapes.Ellipse.StrokeProperty, FrameBrush);
            mark.SetValue(System.Windows.Shapes.Ellipse.StrokeThicknessProperty, 2.0);
            target.AppendChild(mark);
            return new ControlTemplate(typeof(Thumb)) { VisualTree = target };
        }

        private static T Freeze<T>(T freezable) where T : Freezable
        {
            freezable.Freeze();
            return freezable;
        }

        protected override int VisualChildrenCount => _visuals.Count;

        protected override Visual GetVisualChild(int index) => _visuals[index];

        protected override Size ArrangeOverride(Size finalSize)
        {
            if (!_image.IsVisible) return finalSize;
            Rect rect;
            try
            {
                var transform = _image.TransformToAncestor(_box);
                var size = _dragging ? new Size(_width, _image.ActualHeight > 1 ? _image.ActualHeight * (_width / Math.Max(1, _image.ActualWidth)) : _width) : _image.RenderSize;
                rect = transform.TransformBounds(new Rect(size));
            }
            catch (InvalidOperationException)
            {
                return finalSize;
            }
            var points = new[]
            {
                rect.TopLeft, rect.TopRight, rect.BottomLeft, rect.BottomRight,
                new Point(rect.Left, rect.Top + rect.Height / 2),
                new Point(rect.Right, rect.Top + rect.Height / 2),
                new Point(rect.Left + rect.Width / 2, rect.Top),
                new Point(rect.Left + rect.Width / 2, rect.Bottom),
            };
            for (var i = 0; i < points.Length; i++)
                ((Thumb)_visuals[i]).Arrange(new Rect(points[i].X - HandleTarget / 2, points[i].Y - HandleTarget / 2, HandleTarget, HandleTarget));
            return finalSize;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            if (!_image.IsVisible) return;
            try
            {
                var transform = _image.TransformToAncestor(_box);
                var size = _dragging ? new Size(_width, Math.Max(8, _image.ActualHeight * (_width / Math.Max(1, _image.ActualWidth)))) : _image.RenderSize;
                var rect = transform.TransformBounds(new Rect(size));
                drawingContext.DrawRectangle(null, FramePen, rect);
            }
            catch (InvalidOperationException)
            {
                /* 图片已经不在树上 */
            }
        }
    }
}
