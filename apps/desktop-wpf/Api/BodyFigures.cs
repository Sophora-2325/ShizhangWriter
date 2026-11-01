using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NovelGm.Desktop.Api;

/// <summary>章节正文里的插画及独立布局；资源存储与解码交给 ImageAssets。</summary>
internal static class BodyFigures
{
    public const string FolderName = "插图";
    public const double MaxWidth = 480;

    /// <summary>存进作品目录的长边上限。正文栏只有 480，1920 在高分屏上仍然清楚。</summary>
    public const int MaxEdge = 1920;

    /// <summary>放进编辑器时再解码的长边。比存盘略小，避免高清原图把界面撑满内存。</summary>
    public const int DisplayEdge = 1600;

    public const string MarkerPrefix = "«插图:";
    public const string MarkerSuffix = "»";

    public readonly record struct Stored(string Absolute, string Relative, string OriginalRelative);

    public readonly record struct Swap(Block Marker, Block Figure);

    public readonly record struct InlineSwap(Run Marker, InlineUIContainer Figure);

    public const string ClipboardFormat = "NovelGmFigure";

    /// <summary>插画在正文里的全部可保存信息。Tag 和存盘标记都用它。</summary>
    internal sealed class FigureInfo
    {
        public string Preview = "";
        public string Original = "";
        public double Width;
        public TextAlignment Align = TextAlignment.Center;
        public string Alt = "";
        public bool Caption;
        public bool Inline;

        public FigureInfo Clone() => new()
        {
            Preview = Preview, Original = Original, Width = Width, Align = Align,
            Alt = Alt, Caption = Caption, Inline = Inline,
        };
    }

    public static string Marker(FigureInfo info)
    {
        var text = MarkerPrefix + info.Preview.Replace('\\', '/');
        if (!string.IsNullOrEmpty(info.Original)) text += "|o=" + info.Original.Replace('\\', '/');
        if (info.Width > 0) text += "|w=" + info.Width.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        if (info.Align != TextAlignment.Center) text += "|a=" + info.Align.ToString().ToLowerInvariant();
        if (!string.IsNullOrEmpty(info.Alt)) text += "|alt=" + info.Alt.Replace("|", " ").Replace("»", "");
        if (info.Caption) text += "|cap=1";
        if (info.Inline) text += "|in=1";
        return text + MarkerSuffix;
    }

    public static string Marker(string relative) => Marker(new FigureInfo { Preview = relative });

    public static bool TryParseMarker(string text, out FigureInfo info)
    {
        info = new FigureInfo();
        text = text.Trim();
        if (!text.StartsWith(MarkerPrefix, StringComparison.Ordinal) || !text.EndsWith(MarkerSuffix, StringComparison.Ordinal)
            || text.Length <= MarkerPrefix.Length + MarkerSuffix.Length)
            return false;
        var body = text[MarkerPrefix.Length..^MarkerSuffix.Length];
        var parts = body.Split('|');
        info.Preview = parts[0].Replace('\\', '/');
        if (info.Preview.Length == 0) return false;
        foreach (var part in parts.Skip(1))
        {
            var cut = part.IndexOf('=');
            if (cut <= 0) continue;
            var key = part[..cut];
            var value = part[(cut + 1)..];
            switch (key)
            {
                case "o": info.Original = value; break;
                case "w": if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var w)) info.Width = w; break;
                case "a": info.Align = value switch { "left" => TextAlignment.Left, "right" => TextAlignment.Right, "justify" => TextAlignment.Justify, _ => TextAlignment.Center }; break;
                case "alt": info.Alt = value; break;
                case "cap": info.Caption = value == "1"; break;
                case "in": info.Inline = value == "1"; break;
            }
        }
        return true;
    }

    public static string StripMarkers(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text ?? "", @"«插图:[^»]*»", "");

    /// <summary>按内容去重保存原图，必要时生成独立预览；小图不重复占用磁盘。</summary>
    public static Stored Store(string workRoot, string sourcePath) => ImageAssets.Store(workRoot, sourcePath);

    /// <summary>同作品内复用资源，跨作品导入到目标目录。排版信息始终按插图实例保存。</summary>
    public static Stored Duplicate(string workRoot, string previewAbsolute, string? originalAbsolute) =>
        Store(workRoot, !string.IsNullOrEmpty(originalAbsolute) && File.Exists(originalAbsolute) ? originalAbsolute : previewAbsolute);

    /// <summary>缩图走单独的界面线程，主窗口在这段时间里还能动。</summary>
    public static Task<Stored> StoreAsync(string workRoot, string sourcePath)
    {
        return ImageAssets.Background(() => Store(workRoot, sourcePath));
    }

    /// <summary>工具栏插入：段后独立插画，并留一段关联题注。</summary>
    public static void Insert(RichTextBox editor, Stored stored) =>
        Insert(editor, InfoFrom(stored, inline: false), stored.Absolute, forceBlock: true);

    /// <summary>粘贴或拖入：光标在段中则是行内图，在段尾则是段后插画。</summary>
    public static void InsertAtCaret(RichTextBox editor, Stored stored) =>
        Insert(editor, InfoFrom(stored, inline: !CaretAtParagraphEnd(editor)), stored.Absolute, forceBlock: false);

    public static void Insert(RichTextBox editor, FigureInfo info, string previewAbsolute, bool forceBlock)
    {
        if (editor.IsReadOnly) return;
        var inline = !forceBlock && info.Inline && !CaretAtParagraphEnd(editor);
        info.Inline = inline;
        info.Caption = !inline;
        editor.BeginChange();
        try
        {
            if (inline && editor.CaretPosition is not null)
            {
                var image = MakeImage(info, previewAbsolute);
                new InlineUIContainer(image, editor.CaretPosition);
            }
            else
            {
                var figure = CreateBlock(info, previewAbsolute);
                var aside = CaptionParagraph();
                var doc = editor.Document;
                var current = editor.CaretPosition?.Paragraph;
                if (current is null)
                {
                    doc.Blocks.Add(figure);
                    doc.Blocks.Add(aside);
                }
                else
                {
                    var blocks = BodyDocument.ParentBlocks(current)!;
                    blocks.InsertAfter(current, figure);
                    blocks.InsertAfter(figure, aside);
                }
                editor.CaretPosition = aside.ContentStart;
            }
        }
        finally { editor.EndChange(); }
        editor.Focus();
    }

    public static bool CaretAtParagraphEnd(RichTextBox editor)
    {
        var caret = editor.CaretPosition;
        var paragraph = caret?.Paragraph;
        if (paragraph is null) return true;
        return new TextRange(caret, paragraph.ContentEnd).Text.Trim().Length == 0;
    }

    /// <summary>存盘前把插画换成标记。FlowDocument 的文字保存会丢掉图片控件。这次替换不进撤销记录。</summary>
    public static Held TakeForSave(FlowDocument doc)
    {
        var held = new Held();
        foreach (var block in BodyDocument.Blocks(doc.Blocks).OfType<Paragraph>().ToList())
        {
            foreach (var inline in BodyDocument.Inlines(block.Inlines).OfType<InlineUIContainer>().ToList())
            {
                if (inline.Child is not Image image) continue;
                var info = AsInfo(image);
                if (info is null) continue;
                info.Inline = true;
                var marker = new Run(Marker(info));
                var parent = BodyDocument.ParentInlines(inline)!;
                parent.InsertBefore(inline, marker);
                parent.Remove(inline);
                held.Inlines.Add(new InlineSwap(marker, inline));
            }
        }
        foreach (var block in BodyDocument.Blocks(doc.Blocks).ToList())
        {
            if (block is not BlockUIContainer { Child: Image image } figure) continue;
            var info = AsInfo(image);
            if (info is null) continue;
            info.Inline = false;
            var marker = new Paragraph(new Run(Marker(info)))
            {
                TextAlignment = TextAlignment.Center,
                TextIndent = 0,
                Margin = figure.Margin,
            };
            var parent = BodyDocument.ParentBlocks(figure)!;
            parent.InsertBefore(figure, marker);
            parent.Remove(figure);
            held.Blocks.Add(new Swap(marker, figure));
        }
        return held;
    }

    public static void RestoreAfterSave(FlowDocument doc, Held held)
    {
        var markers = BodyDocument.Blocks(doc.Blocks).OfType<Paragraph>()
            .Where(p => TryParseMarker(new TextRange(p.ContentStart, p.ContentEnd).Text, out var info) && !info.Inline)
            .ToList();
        var index = 0;
        foreach (var swap in held.Blocks)
        {
            Block? marker = swap.Marker.Parent is not null ? swap.Marker : null;
            if (marker is null && index < markers.Count) marker = markers[index];
            index++;
            if (marker is null) continue;
            var parent = BodyDocument.ParentBlocks(marker)!;
            parent.InsertBefore(marker, swap.Figure);
            parent.Remove(marker);
        }
        var used = new HashSet<Run>();
        foreach (var swap in held.Inlines)
        {
            var marker = BodyDocument.ParentInlines(swap.Marker) is not null ? swap.Marker : FindMarkerRun(doc, swap.Marker.Text, used);
            if (marker is null || BodyDocument.ParentInlines(marker) is not { } parent) continue;
            used.Add(marker);
            parent.InsertBefore(marker, swap.Figure);
            parent.Remove(marker);
        }
    }

    /// <summary>读回时把标记还原成插画。文件不在了就留下标记，免得路径凭空消失。</summary>
    public static void Hydrate(FlowDocument doc, string workRoot)
    {
        foreach (var block in BodyDocument.Blocks(doc.Blocks).ToList())
        {
            if (block is not Paragraph paragraph) continue;
            var text = new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text;
            if (TryParseMarker(text, out var whole))
            {
                var abs = PreviewOrOriginal(whole, workRoot);
                if (!File.Exists(abs)) continue;
                try
                {
                    if (whole.Inline)
                    {
                        paragraph.Inlines.Clear();
                        paragraph.Inlines.Add(CreateInline(whole, abs));
                    }
                    else
                    {
                        var figure = CreateBlock(whole, abs);
                        var parent = BodyDocument.ParentBlocks(paragraph)!;
                        parent.InsertBefore(paragraph, figure);
                        parent.Remove(paragraph);
                    }
                }
                catch (Exception) { /* 这张图读不出来就留下标记 */ }
                continue;
            }
            foreach (var run in BodyDocument.Inlines(paragraph.Inlines).OfType<Run>().ToList())
                SplitRun(paragraph, run, workRoot);
        }
    }

    public static Image? ImageOf(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Image image && AsInfo(image) is not null) return image;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return null;
    }

    public static Image? SetWidth(RichTextBox editor, Image image, double width, string workRoot)
    {
        if (editor.IsReadOnly || !IsAttached(editor, image)) return null;
        var info = AsInfo(image);
        if (info is null) return null;
        var neu = info.Clone();
        neu.Width = width <= 0 ? 0 : Math.Clamp(width, 40, 1600);
        return SwapImage(editor, image, neu, workRoot);
    }

    public static Image? SetAlign(RichTextBox editor, Image image, TextAlignment align, string workRoot)
    {
        if (editor.IsReadOnly || !IsAttached(editor, image)) return null;
        var info = AsInfo(image);
        if (info is null || info.Inline) return null;
        var neu = info.Clone();
        neu.Align = align;
        return SwapImage(editor, image, neu, workRoot);
    }

    public static void SetAlt(Image image, string alt)
    {
        if (AsInfo(image) is not FigureInfo info) return;
        info.Alt = alt.Replace("|", " ").Replace("»", "");
        image.Tag = Marker(info);
        image.ToolTip = info.Alt.Length == 0 ? null : info.Alt;
    }

    public static void Delete(RichTextBox editor, Image image)
    {
        if (editor.IsReadOnly || !IsAttached(editor, image)) return;
        editor.BeginChange();
        try
        {
            if (image.Parent is BlockUIContainer block)
            {
                var next = block.NextBlock as Paragraph;
                var caption = AsInfo(image)?.Caption == true && next is not null && IsCaptionParagraph(next);
                BodyDocument.ParentBlocks(block)?.Remove(block);
                if (caption) BodyDocument.ParentBlocks(next!)?.Remove(next);
            }
            else if (image.Parent is InlineUIContainer inline)
                BodyDocument.ParentInlines(inline)?.Remove(inline);
        }
        finally { editor.EndChange(); }
    }

    public static void ReplaceFile(RichTextBox editor, Image image, Stored stored, string workRoot)
    {
        if (editor.IsReadOnly || !IsAttached(editor, image)) return;
        var info = AsInfo(image)?.Clone() ?? InfoFrom(stored, inline: image.Parent is InlineUIContainer);
        info.Preview = stored.Relative;
        info.Original = stored.OriginalRelative;
        info.Inline = image.Parent is InlineUIContainer;
        SwapImage(editor, image, info, workRoot);
    }

    /// <summary>复制插图到剪贴板。剪贴板被其他程序占用时重试几次，仍失败返回 false（不抛异常）。</summary>
    public static bool Copy(Image image, string workRoot)
    {
        var info = AsInfo(image);
        if (info is null) return false;
        var data = new DataObject();
        data.SetData(ClipboardFormat, Marker(info) + "\n" + Absolute(workRoot, info.Preview) + "\n" + Absolute(workRoot, info.Original));
        if (image.Source is BitmapSource bitmap) data.SetImage(bitmap);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data);
                return true;
            }
            catch (System.Runtime.InteropServices.ExternalException) when (attempt < 3)
            {
                Thread.Sleep(50);
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                return false;
            }
        }
    }

    public static async Task<bool> TryPasteClipboardFigureAsync(RichTextBox editor, string workRoot, IDataObject data,
        Func<bool>? stillCurrent = null, Func<Task>? beforeInsert = null)
    {
        if (!data.GetDataPresent(ClipboardFormat) || data.GetData(ClipboardFormat) is not string payload) return false;
        var lines = payload.Replace("\r\n", "\n").Split('\n');
        if (lines.Length < 2 || !TryParseMarker(lines[0], out var info)) return false;
        var preview = lines[1];
        var original = lines.Length > 2 ? lines[2] : "";
        if (!File.Exists(preview) && !File.Exists(original)) return false;
        var document = editor.Document;
        // Read the clipboard on its owning thread, but copy/hash/encode away from it.
        var stored = await ImageAssets.Background(() => Duplicate(workRoot, preview, original));
        if (beforeInsert is not null) await editor.Dispatcher.InvokeAsync(beforeInsert).Task.Unwrap();
        return await editor.Dispatcher.InvokeAsync(() =>
        {
            if (editor.IsReadOnly || editor.Document != document || stillCurrent?.Invoke() == false) return false;
            info.Preview = stored.Relative;
            info.Original = stored.OriginalRelative;
            Insert(editor, info, stored.Absolute, forceBlock: !info.Inline);
            return true;
        });
    }

    public static string? OriginalOrPreview(FigureInfo info, string workRoot)
    {
        var original = Absolute(workRoot, info.Original);
        if (File.Exists(original)) return original;
        var preview = Absolute(workRoot, info.Preview);
        return File.Exists(preview) ? preview : null;
    }

    public static FigureInfo? AsInfo(Image image)
    {
        if (image.Tag is FigureInfo info) return info;
        if (image.Tag is string rel && rel.Length > 0)
        {
            if (TryParseMarker(rel, out var parsed)) return parsed;
            info = new FigureInfo { Preview = rel.Replace('\\', '/') };
            return info;
        }
        return null;
    }

    internal static bool IsAttached(RichTextBox editor, Image image)
    {
        if (image.Parent is not TextElement element) return false;
        try { element.ContentStart.CompareTo(editor.Document.ContentStart); return true; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    public sealed class Held
    {
        public List<Swap> Blocks { get; } = new();
        public List<InlineSwap> Inlines { get; } = new();
    }

    private static FigureInfo InfoFrom(Stored stored, bool inline) => new()
    {
        Preview = stored.Relative,
        Original = stored.OriginalRelative,
        Inline = inline,
        Caption = !inline,
        Align = TextAlignment.Center,
    };

    private static Image? SwapImage(RichTextBox editor, Image image, FigureInfo neu, string workRoot)
    {
        var abs = PreviewOrOriginal(neu, workRoot);
        if (image.Source is null && !File.Exists(abs)) return null;
        Image? replacement = null;
        editor.BeginChange();
        try
        {
            if (image.Parent is BlockUIContainer block)
            {
                var figure = CreateBlock(neu, abs, neu.Preview == AsInfo(image)?.Preview ? image.Source : null);
                replacement = (Image)figure.Child;
                var parent = BodyDocument.ParentBlocks(block)!;
                parent.InsertBefore(block, figure);
                parent.Remove(block);
            }
            else if (image.Parent is InlineUIContainer inline && BodyDocument.ParentInlines(inline) is { } parent)
            {
                var box = CreateInline(neu, abs, neu.Preview == AsInfo(image)?.Preview ? image.Source : null);
                replacement = (Image)box.Child;
                parent.InsertBefore(inline, box);
                parent.Remove(inline);
            }
        }
        finally { editor.EndChange(); }
        return replacement;
    }

    private static bool TryImage(string workRoot, FigureInfo info, out InlineUIContainer box)
    {
        box = null!;
        var abs = PreviewOrOriginal(info, workRoot);
        if (!File.Exists(abs)) return false;
        try { box = CreateInline(info, abs); return true; }
        catch (Exception) { return false; }
    }

    private static void SplitRun(Paragraph paragraph, Run run, string workRoot)
    {
        var text = run.Text ?? "";
        if (!text.Contains(MarkerPrefix, StringComparison.Ordinal)) return;
        var pieces = new List<Inline>();
        var rest = text;
        while (rest.Length > 0)
        {
            var start = rest.IndexOf(MarkerPrefix, StringComparison.Ordinal);
            if (start < 0) { pieces.Add(BodyDocument.CopyRun(run, rest)); break; }
            if (start > 0) pieces.Add(BodyDocument.CopyRun(run, rest[..start]));
            var end = rest.IndexOf(MarkerSuffix, start + MarkerPrefix.Length, StringComparison.Ordinal);
            if (end < 0) { pieces.Add(BodyDocument.CopyRun(run, rest[start..])); break; }
            var token = rest[start..(end + MarkerSuffix.Length)];
            if (TryParseMarker(token, out var info) && TryImage(workRoot, info, out var box)) pieces.Add(box);
            else pieces.Add(BodyDocument.CopyRun(run, token));
            rest = rest[(end + MarkerSuffix.Length)..];
        }
        if (pieces.Count == 0) return;
        var parent = BodyDocument.ParentInlines(run)!;
        foreach (var piece in pieces) parent.InsertBefore(run, piece);
        parent.Remove(run);
    }

    private static Run? FindMarkerRun(FlowDocument doc, string marker, HashSet<Run> used)
    {
        foreach (var paragraph in BodyDocument.Blocks(doc.Blocks).OfType<Paragraph>())
            foreach (var run in BodyDocument.Inlines(paragraph.Inlines).OfType<Run>())
                if (!used.Contains(run) && run.Text == marker) return run;
        return null;
    }

    private static BlockUIContainer CreateBlock(FigureInfo info, string absolute, ImageSource? source = null)
    {
        var image = MakeImage(info, absolute, source);
        image.HorizontalAlignment = info.Align switch
        {
            TextAlignment.Left => HorizontalAlignment.Left,
            TextAlignment.Right => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Center,
        };
        return new BlockUIContainer(image) { Margin = new Thickness(0, 18, 0, 8) };
    }

    private static InlineUIContainer CreateInline(FigureInfo info, string absolute, ImageSource? source = null) =>
        new(MakeImage(info, absolute, source));

    private static Image MakeImage(FigureInfo info, string absolute, ImageSource? source = null)
    {
        var image = new Image
        {
            Source = source,
            Stretch = Stretch.Uniform,
            // WPF serializes UI elements into its undo stack. Tags must be XAML-safe values.
            Tag = Marker(info),
            ToolTip = string.IsNullOrEmpty(info.Alt) ? null : info.Alt,
        };
        if (info.Width > 0) image.Width = info.Width;
        else image.MaxWidth = info.Inline ? 240 : MaxWidth;
        if (source is null) _ = LoadIntoAsync(image, absolute);
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        return image;
    }

    internal static async Task LoadIntoAsync(Image image, string path)
    {
        image.MinHeight = 48;
        BitmapImage? bitmap = null;
        try
        {
            bitmap = await ImageAssets.LoadAsync(path, DisplayEdge).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Image decode failed: {0}", ex.Message);
        }
        if (image.Dispatcher.HasShutdownStarted) return;
        await image.Dispatcher.InvokeAsync(() =>
        {
            if (bitmap is not null) image.Source = bitmap;
            else image.ToolTip = L.T("Read.MissingImage");
            image.MinHeight = 0;
        });
    }

    internal static Paragraph CaptionParagraph() => new()
    {
        TextAlignment = TextAlignment.Center,
        TextIndent = 0,
        FontStyle = FontStyles.Italic,
        Margin = new Thickness(0, 0, 0, 16),
    };

    private static bool IsCaptionParagraph(Paragraph paragraph) =>
        paragraph.TextAlignment == TextAlignment.Center
        && paragraph.FontStyle == FontStyles.Italic
        && paragraph.TextIndent == 0;

    private static string Absolute(string workRoot, string relative)
    {
        if (string.IsNullOrEmpty(relative)) return "";
        try
        {
            if (Path.IsPathRooted(relative)) return "";
            var root = Path.GetFullPath(workRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var absolute = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            return absolute.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? absolute : "";
        }
        catch (ArgumentException) { return ""; }
        catch (NotSupportedException) { return ""; }
    }

    private static string PreviewOrOriginal(FigureInfo info, string root)
    {
        var preview = Absolute(root, info.Preview);
        return File.Exists(preview) ? preview : Absolute(root, info.Original);
    }

    /// <summary>保存过程里替换插画不能记进撤销，否则用户一撤销，插画就变成标记行。</summary>
    public static IDisposable SuspendUndo(FlowDocument doc)
    {
        var undo = UndoManager(doc);
        var enabled = undo?.GetType().GetProperty("IsEnabled", BindingFlags.Instance | BindingFlags.NonPublic);
        if (undo is null || enabled is null || enabled.PropertyType != typeof(bool)) return EmptyDisposable.Instance;
        var was = (bool)enabled.GetValue(undo)!;
        if (was) enabled.SetValue(undo, false);
        return new UndoScope(enabled, undo, was);
    }

    private static object? UndoManager(FlowDocument doc)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var container = doc.GetType().GetProperty("TextContainer", flags)?.GetValue(doc);
        return container?.GetType().GetProperty("UndoManager", flags)?.GetValue(container);
    }

    private sealed class UndoScope : IDisposable
    {
        private readonly PropertyInfo _enabled;
        private readonly object _undo;
        private readonly bool _was;
        private bool _done;

        public UndoScope(PropertyInfo enabled, object undo, bool was)
        {
            _enabled = enabled;
            _undo = undo;
            _was = was;
        }

        public void Dispose()
        {
            if (_done || !_was) return;
            _done = true;
            _enabled.SetValue(_undo, true);
        }
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();
        public void Dispose() { }
    }

    public static BitmapImage Load(string absolutePath) => Load(absolutePath, DisplayEdge);

    public static BitmapImage Load(string absolutePath, int cap) => ImageAssets.Load(absolutePath, cap);
}
