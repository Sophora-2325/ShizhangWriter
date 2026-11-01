using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NovelGm.Desktop.Api;
using Xunit;

namespace NovelGm.Desktop.Tests;

[Collection("serial-body-editing")]
public class BodyEditorRegressionTests
{
    private static readonly BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Static;
    private static RichTextBox Editor(string text) => new() { Document = RichBody.FromPlain(text) };
    private static Run First(RichTextBox e) => BodyDocument.Blocks(e.Document.Blocks).OfType<Paragraph>()
        .SelectMany(p => BodyDocument.Inlines(p.Inlines)).OfType<Run>().First(r => r.Text.Length > 0);
    private static string Text(RichTextBox e) => RichBody.PlainText(e.Document);

    [Fact]
    public void Plain_paste_replaces_selected_text_and_is_one_undo() => Sta(() =>
    {
        var e = Editor("甲乙丙"); var run = First(e);
        using var shown = new EditorWindow(e);
        e.Selection.Select(run.ContentStart.GetPositionAtOffset(1)!, run.ContentStart.GetPositionAtOffset(2)!);
        BodyEditing.PasteText(e, "新\r\n段");
        Assert.Equal("甲新\n段丙", Text(e));
        e.Undo(); Assert.Equal("甲乙丙", Text(e));
    });

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void Resolve_revision_changes_only_selected_characters(bool accept) => Sta(() =>
    {
        var e = Editor("甲乙丙丁"); var run = First(e);
        if (accept) BodyReview.MarkDeleted(new(run.ContentStart, run.ContentEnd));
        else BodyReview.MarkInserted(new(run.ContentStart, run.ContentEnd));
        using var shown = new EditorWindow(e);
        run = First(e);
        e.Selection.Select(run.ContentStart.GetPositionAtOffset(1)!, run.ContentStart.GetPositionAtOffset(2)!);
        if (accept) BodyReview.Accept(e); else BodyReview.Reject(e);
        Assert.Equal("甲丙丁", Text(e));
        e.Undo(); Assert.Equal("甲乙丙丁", Text(e));
    });

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void Clearing_revision_format_preserves_unselected_revision(bool accept) => Sta(() =>
    {
        var e = Editor("甲乙丙"); var run = First(e);
        if (accept) BodyReview.MarkInserted(new(run.ContentStart, run.ContentEnd));
        else BodyReview.MarkDeleted(new(run.ContentStart, run.ContentEnd));
        run = First(e);
        e.Selection.Select(run.ContentStart.GetPositionAtOffset(1)!, run.ContentStart.GetPositionAtOffset(2)!);
        if (accept) BodyReview.Accept(e); else BodyReview.Reject(e);
        Assert.Equal("甲乙丙", Text(e));
        var runs = BodyDocument.Inlines(((Paragraph)e.Document.Blocks.FirstBlock).Inlines).OfType<Run>().ToList();
        Assert.False(accept ? BodyReview.IsInsertion(runs.Single(r => r.Text == "乙")) : BodyReview.IsDeletion(runs.Single(r => r.Text == "乙")));
        Assert.True(accept ? BodyReview.IsInsertion(runs.Single(r => r.Text == "甲")) : BodyReview.IsDeletion(runs.Single(r => r.Text == "甲")));
    });

    [Fact]
    public void Delete_image_never_deletes_neighbouring_prose() => Sta(() =>
    {
        var e = Editor("前文"); var image = new Image { Tag = BodyFigures.Marker(new BodyFigures.FigureInfo { Preview = "x.png", Caption = true }) };
        e.Document.Blocks.Add(new BlockUIContainer(image));
        e.Document.Blocks.Add(new Paragraph(new Run("不能删除的正文")));
        BodyFigures.Delete(e, image);
        Assert.Equal("前文\n不能删除的正文", Text(e));
    });

    [Fact]
    public void Nested_inline_image_roundtrips_with_formats_and_relative_resource() => Sta(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "novel-gm-nested-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "source.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(new WriteableBitmap(30, 20, 96, 96, PixelFormats.Bgr32, null)));
            using (var output = File.Create(file)) encoder.Save(output);
            var e = Editor(""); var run = new Run("前后") { Foreground = Brushes.Red, TextDecorations = TextDecorations.Underline };
            var bold = new Bold(run); var paragraph = new Paragraph(bold);
            e.Document.Blocks.Clear();
            e.Document.Blocks.Add(new Section(paragraph));
            e.CaretPosition = run.ContentStart.GetPositionAtOffset(1)!;
            BodyFigures.InsertAtCaret(e, BodyFigures.Store(root, file));
            var capture = RichBody.Capture(e.Document);
            Assert.Contains(BodyFigures.MarkerPrefix, capture.Rich);
            Assert.DoesNotContain("file:///", capture.Rich);
            Assert.Single(BodyDocument.Inlines(paragraph.Inlines).OfType<InlineUIContainer>());
            var loaded = RichBody.Build(capture.Text, capture.Rich, root);
            var p = BodyDocument.Blocks(loaded.Blocks).OfType<Paragraph>().Single();
            Assert.Single(BodyDocument.Inlines(p.Inlines).OfType<InlineUIContainer>());
            var prose = BodyDocument.Inlines(p.Inlines).OfType<Run>().Where(r => r.Text.Length > 0).ToList();
            Assert.All(prose, r => Assert.Equal(Colors.Red, ((SolidColorBrush)r.Foreground).Color));
            Assert.All(prose, r => Assert.Equal(FontWeights.Bold, r.FontWeight));
            Assert.All(prose, r => Assert.NotEmpty(r.TextDecorations));
            WpfPump.Until(() => BodyDocument.Inlines(p.Inlines).OfType<InlineUIContainer>().All(i => ((Image)i.Child).Source is not null));
        }
        finally { Directory.Delete(root, recursive: true); }
    });

    [Fact]
    public void Block_image_in_nested_section_uses_its_own_block_collection() => Sta(() =>
    {
        var e = Editor("");
        var p = new Paragraph(new Run("正文"));
        var section = new Section(p);
        e.Document.Blocks.Clear(); e.Document.Blocks.Add(section);
        e.CaretPosition = p.ContentEnd;
        BodyFigures.Insert(e, new BodyFigures.FigureInfo { Preview = "missing.png" }, "missing.png", true);
        Assert.Equal(3, section.Blocks.Count);
        Assert.IsType<BlockUIContainer>(p.NextBlock);
        Assert.Equal("正文", new TextRange(p.ContentStart, p.ContentEnd).Text);
        e.IsReadOnly = true;
        BodyFigures.Insert(e, new BodyFigures.FigureInfo { Preview = "missing.png" }, "missing.png", true);
        Assert.Equal(3, section.Blocks.Count);
    });

    [Theory]
    [InlineData("a", "a", "aa", "aa", 1)]
    [InlineData("A a", "a", "b", "b b", 2)]
    [InlineData("aaaa", "aa", "", "", 2)]
    [InlineData("a a", "a", "a", "a a", 2)]
    public void Replace_all_consumes_original_matches_once(string original, string query, string replacement, string expected, int count) => Sta(() =>
    {
        var e = Editor(original);
        using var shown = new EditorWindow(e);
        Assert.Equal(count, BodyEditing.ReplaceAll(e, query, replacement));
        Assert.Equal(expected, Text(e));
        if (original != expected) { e.Undo(); Assert.Equal(original, Text(e)); }
    });

    [Fact]
    public void Selected_replacements_handle_unicode_offsets_and_can_undo_as_one_change() => Sta(() =>
    {
        var e = Editor("😀雨 雨 雨");
        using var shown = new EditorWindow(e);
        var source = RichBody.PlainText(e.Document);
        Assert.Equal(2, BodyEditing.ReplaceSelected(e, "雨", "晴天", source, new[] { 1, 5 }));
        Assert.Equal("😀晴天 雨 晴天", Text(e));
        e.Undo(); Assert.Equal("😀雨 雨 雨", Text(e));
        Assert.Equal(-1, BodyEditing.ReplaceSelected(e, "雨", "晴天", source, new[] { 1, 4 }));
        Assert.Equal("😀雨 雨 雨", Text(e));
        Assert.Equal(-1, BodyEditing.ReplaceSelected(e, "雨", "晴天", "旧正文", new[] { 1 }));
        Assert.Equal("😀雨 雨 雨", Text(e));
    });

    [Fact]
    public void Replacement_respects_case_sensitive_search() => Sta(() =>
    {
        var e = Editor("A a");
        using var shown = new EditorWindow(e);
        Assert.Equal(1, BodyEditing.ReplaceAll(e, "a", "b", caseSensitive: true));
        Assert.Equal("A b", Text(e));
    });

    [Fact]
    public void Search_crosses_format_boundaries_but_not_pictures_and_ignores_stale_cursor() => Sta(() =>
    {
        var e = Editor(""); var p = new Paragraph(new Run("林")); p.Inlines.Add(new Bold(new Run("舟")));
        e.Document.Blocks.Clear(); e.Document.Blocks.Add(p);
        var find = typeof(BodyEditing).GetMethod("FindNext", Private)!;
        Assert.NotNull(find.Invoke(null, new object?[] { e, "林舟", e.Document.ContentStart }));
        Assert.Equal("林舟", e.Selection.Text);
        var old = e.Document.ContentStart;
        e.Document = RichBody.FromPlain("新章");
        Assert.NotNull(find.Invoke(null, new object?[] { e, "新", old }));
        p = (Paragraph)e.Document.Blocks.FirstBlock;
        p.Inlines.Add(new InlineUIContainer(new Image())); p.Inlines.Add(new Run("尾"));
        Assert.Empty(new BodyTextMap(e.Document).Matches("章尾"));
    });

    [Fact]
    public void Display_helpers_do_not_change_text_format_serialization_or_undo() => Sta(() =>
    {
        var e = Editor(""); var p = new Paragraph(new Run("甲")); p.Inlines.Add(new LineBreak());
        var original = new Run(" 乙") { Foreground = Brushes.Red, TextDecorations = TextDecorations.Underline };
        p.Inlines.Add(new Bold(original)); e.Document.Blocks.Clear(); e.Document.Blocks.Add(p);
        var before = Text(e); var rich = RichBody.SaveXaml(e.Document);
        BodyReview.SetInvisibles(e.Document, true);
        BodyReview.Highlight(e.Document, new[] { new BodyReview.Note { Quote = "乙", Start = 3 } });
        Assert.Equal(before, Text(e)); Assert.Equal(rich, RichBody.SaveXaml(e.Document));
        BodyReview.SetInvisibles(e.Document, false); BodyReview.ClearComments(e.Document);
        Assert.Equal(before, Text(e)); Assert.Equal(rich, RichBody.SaveXaml(e.Document));
        Assert.Equal(Colors.Red, ((SolidColorBrush)original.Foreground).Color);
        Assert.NotEmpty(original.TextDecorations);
    });

    [Fact]
    public void Explicit_local_font_size_survives_save_and_default_changes() => Sta(() =>
    {
        var e = Editor("手动字号"); e.FontSize = 17; First(e).FontSize = 16;
        var capture = RichBody.Capture(e.Document);
        e.Document = RichBody.Build(capture.Text, capture.Rich);
        Assert.Equal(16, First(e).FontSize);
    });

    [Fact]
    public void Repeated_comment_uses_selected_occurrence_across_formats() => Sta(() =>
    {
        var e = Editor("同一句\n同一句"); var map = new BodyTextMap(e.Document);
        Assert.Equal(4, BodyPresentation.ResolveNote(map, new() { Quote = "同一句", Start = 4 }));
        Assert.Equal(0, BodyPresentation.ResolveNote(map, new() { Quote = "同一句", Start = 0 }));
        var p = new Paragraph(new Run("林")); p.Inlines.Add(new Bold(new Run("舟")));
        e.Document.Blocks.Clear(); e.Document.Blocks.Add(p);
        Assert.Equal(0, BodyPresentation.ResolveNote(new(e.Document), new() { Quote = "林舟" }));
    });

    [Fact]
    public void Backspace_after_repeated_clicks_deletes_the_figure() => Sta(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "novel-gm-backspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var e = Editor("上文还在。");
        BodyEditing.Attach(e, () => true, () => root, () => 1, () => null, () => { });
        using var shown = new EditorWindow(e);
        try
        {
            var path = Path.Combine(root, "source.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(new WriteableBitmap(2, 2, 96, 96, PixelFormats.Bgr32, null)));
            using (var output = File.Create(path)) encoder.Save(output);
            InsertFigure(e, root, path);
            EditingCommands.Backspace.Execute(null, e);
            Assert.Empty(e.Document.Blocks.OfType<BlockUIContainer>());
            Assert.Contains("上文还在。", Text(e));

            InsertFigure(e, root, path);
            var image = e.Document.Blocks.OfType<BlockUIContainer>().Select(b => b.Child).OfType<Image>().Single();
            for (var n = 1; n <= 5; n++)
            {
                var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                {
                    RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
                };
                if (n is 2 or 4)
                {
                    var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                    {
                        RoutedEvent = UIElement.MouseLeftButtonDownEvent,
                    };
                    typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!
                        .GetSetMethod(true)!.Invoke(down, new object[] { 2 });
                    image.RaiseEvent(args);
                    image.RaiseEvent(down);
                }
                else image.RaiseEvent(args);
            }
            var source = PresentationSource.FromVisual(e);
            e.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Back) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Assert.Empty(e.Document.Blocks.OfType<BlockUIContainer>());
            Assert.Contains("上文还在。", Text(e));

            InsertFigure(e, root, path);
            var run = e.Document.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<Run>()).First(r => r.Text.Contains("上文"));
            e.CaretPosition = run.ContentStart.GetPositionAtOffset(2, LogicalDirection.Forward);
            EditingCommands.Backspace.Execute(null, e);
            Assert.Single(e.Document.Blocks.OfType<BlockUIContainer>());
            Assert.Equal("上还在。", run.Text);
        }
        finally
        {
            foreach (PresentationSource open in PresentationSource.CurrentSources)
                if (open.RootVisual is Window window && window != Window.GetWindow(e)) window.Close();
            Directory.Delete(root, true);
        }
    });

    [Fact]
    public void Saving_never_touches_the_live_document() => Sta(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "novel-gm-capture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "source.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(new WriteableBitmap(2, 2, 96, 96, PixelFormats.Bgr32, null)));
            using (var output = File.Create(path)) encoder.Save(output);
            var e = Editor("上文还在。");
            using var shown = new EditorWindow(e);
            InsertFigure(e, root, path);
            var prose = e.Document.Blocks.OfType<Paragraph>().First();
            e.CaretPosition = prose.ContentStart.GetPositionAtOffset(2);
            BodyFigures.InsertAtCaret(e, BodyFigures.Store(root, path));
            BodyReview.MarkDeleted(new TextRange(First(e).ContentStart, First(e).ContentStart.GetPositionAtOffset(1)));
            var blocks = e.Document.Blocks.ToList();
            var changes = 0;
            e.TextChanged += (_, _) => changes++;

            var (text, xaml) = RichBody.Capture(e.Document);

            Assert.Equal(0, changes);
            Assert.Equal(blocks, e.Document.Blocks.ToList());
            Assert.Contains(BodyFigures.MarkerPrefix, xaml);
            Assert.Contains(BodyReview.DelPrefix, xaml);
            Assert.DoesNotContain(BodyFigures.MarkerPrefix, text);
            Assert.All(e.Document.Blocks.OfType<BlockUIContainer>(), b => Assert.NotNull(((Image)b.Child).Source));
            var reopened = RichBody.Build(text, xaml, root);
            WpfPump.Images(reopened);
            Assert.Single(reopened.Blocks.OfType<BlockUIContainer>());
            Assert.Contains(reopened.Blocks.OfType<Paragraph>(), p => p.Inlines.OfType<InlineUIContainer>().Any());
        }
        finally { Directory.Delete(root, true); }
    });

    private static void InsertFigure(RichTextBox editor, string root, string path)
    {
        editor.CaretPosition = editor.Document.ContentEnd;
        BodyFigures.Insert(editor, BodyFigures.Store(root, path));
        WpfPump.Images(editor.Document);
    }

    [Fact]
    public void Rapid_image_clicks_reuse_selection_without_changing_document() => Sta(() =>
    {
        var e = Editor("正文不能改变");
        var image = TestImage();
        e.Document.Blocks.Add(new BlockUIContainer(image));
        BodyEditing.Attach(e, () => true, () => null, () => 1, () => null, () => { });
        using var shown = new EditorWindow(e);
        var original = RichBody.Capture(e.Document);
        var changes = 0;
        e.TextChanged += (_, _) => changes++;
        ClickImage(image);
        var frame = FigureFrame(e);
        Assert.NotNull(frame);
        for (var i = 0; i < 500; i++)
        {
            ClickImage(image, i % 2 + 1);
            Assert.Same(frame, FigureFrame(e));
        }
        Assert.Equal(0, changes);
        Assert.False(e.CanUndo);
        Assert.Equal(original, RichBody.Capture(e.Document));
    });

    [Theory]
    [InlineData(Key.Back)] [InlineData(Key.Delete)]
    public void Switching_images_then_deleting_keeps_other_image_and_prose(Key key) => Sta(() =>
    {
        var e = Editor("保留的正文");
        var first = TestImage(); var second = TestImage();
        e.Document.Blocks.Add(new BlockUIContainer(first));
        e.Document.Blocks.Add(new BlockUIContainer(second));
        BodyEditing.Attach(e, () => true, () => null, () => 1, () => null, () => { });
        using var shown = new EditorWindow(e);
        for (var i = 0; i < 100; i++)
        {
            ClickImage(first); ClickImage(second);
            Assert.NotNull(FigureFrame(e));
            var outside = i % 2 == 0 ? (UIElement)e : Window.GetWindow(e);
            outside.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
            Assert.Null(FigureFrame(e));
        }
        ClickImage(second);
        e.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(e), 0, key)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        Assert.Same(first, Assert.Single(e.Document.Blocks.OfType<BlockUIContainer>()).Child);
        Assert.Null(FigureFrame(e));
        Assert.Equal("保留的正文", First(e).Text);
        e.Undo();
        Assert.Equal(2, e.Document.Blocks.OfType<BlockUIContainer>().Count());
    });

    [Fact]
    public void Deselection_discards_a_queued_resize_and_document_switch_clears_frame() => Sta(() =>
    {
        var e = Editor("正文"); var image = TestImage();
        e.Document.Blocks.Add(new BlockUIContainer(image));
        BodyEditing.Attach(e, () => true, () => Path.GetTempPath(), () => 1, () => null, () => { });
        using var shown = new EditorWindow(e);
        ClickImage(image); e.UpdateLayout();
        var thumb = (System.Windows.Controls.Primitives.Thumb)VisualTreeHelper.GetChild(FigureFrame(e)!, 3);
        thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragStartedEventArgs(0, 0)
            { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragStartedEvent });
        thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(100, 0)
            { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragDeltaEvent });
        thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragCompletedEventArgs(100, 0, false)
            { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragCompletedEvent });
        Window.GetWindow(e).RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
        e.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        Assert.Same(image, Assert.Single(e.Document.Blocks.OfType<BlockUIContainer>()).Child);
        Assert.True(double.IsNaN(image.Width));
        Assert.Null(FigureFrame(e));
        ClickImage(image);
        e.Document = RichBody.FromPlain("新章");
        Assert.Null(FigureFrame(e));
    });

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void Pending_preview_does_not_open_after_delete_or_chapter_switch(bool delete) => Sta(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "novel-gm-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var entered = new CountdownEvent(2);
        using var release = new ManualResetEventSlim();
        var blockers = Enumerable.Range(0, 2).Select(_ => ImageAssets.Background(() =>
        {
            entered.Signal(); release.Wait(TimeSpan.FromSeconds(10)); return true;
        })).ToArray();
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var image = TestImage();
            var path = Path.Combine(root, "test.png");
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create((BitmapSource)image.Source));
            using (var output = File.Create(path)) encoder.Save(output);
            var e = Editor("正文"); e.Document.Blocks.Add(new BlockUIContainer(image));
            BodyEditing.Attach(e, () => true, () => root, () => 1, () => null, () => { });
            using var shown = new EditorWindow(e);
            var host = Window.GetWindow(e);
            var pending = BodyEditing.PreviewFigureAsync(image);
            Assert.False(pending.IsCompleted);
            if (delete) BodyFigures.Delete(e, image);
            else e.Document = RichBody.FromPlain("新章");
            release.Set(); WpfPump.Until(() => pending.IsCompleted); pending.GetAwaiter().GetResult();
            Assert.DoesNotContain(PresentationSource.CurrentSources.Cast<PresentationSource>()
                .Select(s => s.RootVisual).OfType<Window>(), w => w.Owner == host);
        }
        finally { release.Set(); Task.WaitAll(blockers); Directory.Delete(root, true); }
    });

    private static Image TestImage() => new()
    {
        Source = new WriteableBitmap(20, 20, 96, 96, PixelFormats.Bgr32, null),
        Tag = BodyFigures.Marker("test.png"),
    };

    [Fact]
    public void Embedded_picture_is_enabled_and_reachable_by_real_mouse_hit_testing() => Sta(() =>
    {
        var e = Editor("正文"); var image = TestImage();
        image.Width = 240;
        e.Document.Blocks.Add(new BlockUIContainer(image));
        BodyEditing.Attach(e, () => true, () => null, () => 1, () => null, () => { });
        using var shown = new EditorWindow(e);
        e.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        e.UpdateLayout();
        Assert.True(image.IsEnabled, "内嵌图片被 RichTextBox 禁用，真实鼠标事件无法到达图片上的选中处理。");
        var center = e.PointFromScreen(image.PointToScreen(new Point(image.ActualWidth / 2, image.ActualHeight / 2)));
        var hit = e.InputHitTest(center) as DependencyObject;
        Assert.Same(image, BodyFigures.ImageOf(hit));
    });

    [Fact]
    public void Selected_image_handles_follow_the_visible_image_bounds() => Sta(() =>
    {
        var e = Editor("图片选中效果");
        var image = TestImage(); image.Width = 240; image.Height = 150;
        e.Document.Blocks.Add(new BlockUIContainer(image));
        BodyEditing.Attach(e, () => true, () => null, () => 1, () => null, () => { });
        using var shown = new EditorWindow(e);
        ClickImage(image);
        e.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        e.UpdateLayout();
        var frame = FigureFrame(e)!;
        var topLeft = e.PointFromScreen(image.PointToScreen(new Point()));
        var bottomRight = e.PointFromScreen(image.PointToScreen(new Point(image.ActualWidth, image.ActualHeight)));
        var expected = new[] { topLeft, new Point(bottomRight.X, topLeft.Y), new Point(topLeft.X, bottomRight.Y), bottomRight,
            new Point(topLeft.X, (topLeft.Y + bottomRight.Y) / 2), new Point(bottomRight.X, (topLeft.Y + bottomRight.Y) / 2),
            new Point((topLeft.X + bottomRight.X) / 2, topLeft.Y), new Point((topLeft.X + bottomRight.X) / 2, bottomRight.Y) };
        var capturePath = Environment.GetEnvironmentVariable("NOVEL_GM_SELECTION_CAPTURE");
        if (!string.IsNullOrEmpty(capturePath))
        {
            var host = Window.GetWindow(e);
            var bitmap = new RenderTargetBitmap((int)host.ActualWidth, (int)host.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(host);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(capturePath); encoder.Save(output);
        }
        for (var i = 0; i < 8; i++)
        {
            var handle = (FrameworkElement)VisualTreeHelper.GetChild(frame, i);
            var center = handle.TranslatePoint(new Point(handle.ActualWidth / 2, handle.ActualHeight / 2), e);
            Assert.InRange(center.X, expected[i].X - 1, expected[i].X + 1);
            Assert.InRange(center.Y, expected[i].Y - 1, expected[i].Y + 1);
        }
    });

    private static Adorner? FigureFrame(RichTextBox editor) =>
        AdornerLayer.GetAdornerLayer(editor)?.GetAdorners(editor)?
            .SingleOrDefault(a => VisualTreeHelper.GetChildrenCount(a) == 8);

    private static void ClickImage(Image image, int count = 1)
    {
        image.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
        var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            { RoutedEvent = UIElement.MouseLeftButtonDownEvent };
        typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!
            .GetSetMethod(true)!.Invoke(down, new object[] { count });
        image.RaiseEvent(down);
    }

    [Fact]
    public void Picture_mutations_reject_readonly_or_detached_targets() => Sta(() =>
    {
        var e = Editor("旧章"); var image = new Image { Tag = BodyFigures.Marker("old.png") };
        e.Document.Blocks.Add(new BlockUIContainer(image));
        e.IsReadOnly = true;
        BodyFigures.Delete(e, image);
        Assert.Single(e.Document.Blocks.OfType<BlockUIContainer>());
        Assert.Null(BodyFigures.SetWidth(e, image, 100, ""));
        e.IsReadOnly = false; e.Document = RichBody.FromPlain("新章");
        BodyFigures.Delete(e, image);
        Assert.Equal("新章", Text(e));
        Assert.Null(BodyFigures.SetAlign(e, image, TextAlignment.Left, ""));
    });

    [Fact]
    public void Entire_image_batch_is_cancelled_when_chapter_changes() => Sta(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "novel-gm-batch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var entered = new CountdownEvent(2);
        using var release = new ManualResetEventSlim();
        var blockers = Enumerable.Range(0, 2).Select(_ => ImageAssets.Background(() =>
        {
            entered.Signal(); release.Wait(TimeSpan.FromSeconds(10)); return true;
        })).ToArray();
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var path = Path.Combine(root, "source.png");
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(new WriteableBitmap(10, 10, 96, 96, PixelFormats.Bgr32, null)));
            using (var output = File.Create(path)) encoder.Save(output);
            var e = Editor("旧章"); var chapter = 1;
            BodyEditing.Attach(e, () => true, () => root, () => chapter, () => null, () => { });
            var pending = BodyEditing.ImportFilesAsync(root, new[] { path, path });
            chapter = 2; e.Document = RichBody.FromPlain("新章");
            release.Set(); WpfPump.Until(() => pending.IsCompleted); pending.GetAwaiter().GetResult();
            Assert.Equal("新章", Text(e));
            Assert.Empty(e.Document.Blocks.OfType<BlockUIContainer>());
        }
        finally { release.Set(); Task.WaitAll(blockers); Directory.Delete(root, true); }
    });

    [Fact]
    public void Note_storage_is_atomic_and_recovers_backup_without_overwriting_damage() => Sta(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "novel-gm-notes-" + Guid.NewGuid().ToString("N"));
        try
        {
            var note = new BodyReview.Note { Id = "one", Quote = "引用", Text = "批注" };
            BodyReview.SaveNotes(root, 1, new[] { note });
            BodyReview.SaveNotes(root, 1, Array.Empty<BodyReview.Note>());
            var path = BodyReview.NotesPath(root, 1);
            Assert.Empty(BodyReview.LoadNotes(root, 1));
            File.WriteAllText(path, "damaged");
            Assert.Single(BodyReview.LoadNotes(root, 1));
            Assert.Throws<System.Text.Json.JsonException>(() => BodyReview.SaveNotes(root, 1, Array.Empty<BodyReview.Note>()));
            Assert.Equal("damaged", File.ReadAllText(path));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        }
        finally { Directory.Delete(root, true); }
    });

    [Fact]
    public void Display_adorners_preserve_authoring_document_in_a_real_window() => Sta(() =>
    {
        var e = Editor("甲 乙\n同一句\n同一句"); BodyPresentation.Attach(e);
        using var shown = new EditorWindow(e);
        var original = RichBody.SaveXaml(e.Document);
        BodyReview.SetInvisibles(e.Document, true);
        BodyReview.Highlight(e.Document, new[] { new BodyReview.Note { Quote = "同一句", Start = 9 } });
        e.UpdateLayout();
        var image = new RenderTargetBitmap(600, 400, 96, 96, PixelFormats.Pbgra32);
        image.Render(e);
        Assert.Equal(original, RichBody.SaveXaml(e.Document));
        BodyReview.SetInvisibles(e.Document, false);
    });

    [Fact]
    public void Saved_body_drops_theme_ink_so_chapters_follow_light_and_dark() => Sta(() =>
    {
        var e = new RichTextBox
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0x16, 0x18, 0x1B)),
            Document = RichBody.FromPlain("雨落在山门前。"),
        };
        using var shown = new EditorWindow(e);
        // 「正文」预设会把当时的主题字色写成选区本地值；换肤时必须不被钉住。
        var range = new TextRange(e.Document.ContentStart, e.Document.ContentEnd);
        range.ApplyPropertyValue(TextElement.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0xE9, 0xE6, 0xDE)));
        var rich = RichBody.SaveXaml(e.Document);
        Assert.DoesNotContain("16181B", rich, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("E9E6DE", rich, StringComparison.OrdinalIgnoreCase);
    });

    [Fact]
    public void Live_prose_drops_review_markers_for_copy_and_stats() => Sta(() =>
    {
        var doc = RichBody.FromPlain("正文«删:被删的内容»");
        Assert.Equal("正文", RichBody.LiveProse(doc));
    });

    private sealed class EditorWindow : IDisposable
    {
        private readonly Window _window;
        internal Window Window => _window;
        public EditorWindow(RichTextBox editor)
        {
            _window = new Window { Content = editor, Width = 600, Height = 400, ShowInTaskbar = false };
            _window.Show(); _window.UpdateLayout();
            editor.IsUndoEnabled = false; editor.IsUndoEnabled = true;
        }
        public void Dispose() => _window.Close();
    }

    private static void Sta(Action run) => StaRunner.Run(run);

    [Fact]
    public void Comment_loading_never_adds_persistent_underlines_but_location_highlight_still_works() => Sta(() =>
    {
        var editor = Editor("那是她的位置。他膝盖上摊着一沓广播稿。\n纸角被风翻得哗哗响。");
        editor.Focusable = false;
        First(editor).TextDecorations = TextDecorations.Underline; // 作者真正的下划线保留
        BodyPresentation.Attach(editor);
        using var host = new EditorWindow(editor);
        byte[] Pixels()
        {
            host.Window.UpdateLayout();
            host.Window.Dispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));
            var bitmap = new RenderTargetBitmap(600, 400, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(host.Window);
            var pixels = new byte[600 * 400 * 4]; bitmap.CopyPixels(pixels, 600 * 4, 0); return pixels;
        }
        var before = Pixels(); var saved = RichBody.SaveXaml(editor.Document);
        BodyReview.Highlight(editor.Document, new[] { new BodyReview.Note { Id = "n", Quote = "她的位置", Start = 2, Text = "作者批注" } });
        Assert.Equal(saved, RichBody.SaveXaml(editor.Document));
        Assert.Equal(180, editor.Padding.Right);
        Assert.False(before.SequenceEqual(Pixels()));
        Assert.Contains(First(editor).TextDecorations, d => d.Location == TextDecorationLocation.Underline);
        BodyReview.ClearComments(editor.Document);
        Assert.Equal(before, Pixels());
        Assert.Equal(0, editor.Padding.Right);
        BodyPresentation.ShowLocate(editor.Document, 2, 5);
        Assert.False(before.SequenceEqual(Pixels()));
        BodyPresentation.ClearLocate(editor.Document); Assert.Equal(before, Pixels());
    });

    [Fact]
    public void Text_menu_comment_is_enabled_only_for_an_editable_selection() => Sta(() =>
    {
        var editor = Editor("甲乙丙");
        var editable = true;
        BodyEditing.Attach(editor, () => editable, () => null, () => 1, () => null, () => { });
        using var host = new EditorWindow(editor);
        MenuItem Comment(ContextMenu menu) =>
            menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, L.T("Fmt.CommentAdd")));
        Assert.False(Comment(BodyEditing.BuildTextMenu(editor)).IsEnabled);
        var run = First(editor);
        editor.Selection.Select(run.ContentStart, run.ContentStart.GetPositionAtOffset(1)!);
        Assert.True(Comment(BodyEditing.BuildTextMenu(editor)).IsEnabled);
        editable = false;
        Assert.False(Comment(BodyEditing.BuildTextMenu(editor)).IsEnabled);
        editable = true;
        editor.IsReadOnly = true;
        Assert.False(Comment(BodyEditing.BuildTextMenu(editor)).IsEnabled);
        editor.IsReadOnly = false;
        editor.Selection.Select(run.ContentStart, run.ContentStart.GetPositionAtOffset(1)!);
        var menu = BodyEditing.BuildTextMenu(editor);
        menu.PlacementTarget = editor;
        menu.IsOpen = true;
        try
        {
            host.Window.Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
            Assert.Equal("甲", editor.Selection.Text);
            menu.IsOpen = false;
            host.Window.Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
            Assert.Equal("甲", editor.Selection.Text);
        }
        finally { menu.IsOpen = false; }
    });

    [Fact]
    public void Comment_list_selection_centers_the_quoted_line() => Sta(() =>
    {
        var quote = "这里是批注锚点句子。";
        var lines = Enumerable.Range(0, 80).Select(i => i == 40 ? quote : $"填充第{i:00}行的正文内容用来把锚点推到中部。");
        var editor = Editor(string.Join("\n", lines));
        var root = Path.Combine(Path.GetTempPath(), "novel-gm-comment-locate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Window? listWindow = null;
        try
        {
            BodyReview.SaveNotes(root, 1, new[] { new BodyReview.Note { Id = "n", Quote = quote, Text = "旁注内容" } });
            using var host = new EditorWindow(editor);
            BodyEditing.Attach(editor, () => true, () => root, () => 1, () => null, () => { });
            BodyEditing.ShowNotes();
            Assert.Equal(180, editor.Padding.Right);
            BodyEditing.ShowComments();
            listWindow = host.Window.OwnedWindows.OfType<Window>().Single();
            var list = ((DockPanel)listWindow.Content).Children.OfType<ListBox>().Single();
            list.SelectedIndex = 0;
            editor.Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
            editor.UpdateLayout();
            Assert.Equal(quote, editor.Selection.Text.Replace("\r\n", "\n"));
            var rect = editor.Selection.Start.GetCharacterRect(LogicalDirection.Forward);
            var viewport = editor.ViewportHeight;
            Assert.True(viewport > 1, "正文视口还没有高度");
            var delta = Math.Abs(rect.Top + rect.Height / 2 - viewport / 2);
            Assert.True(delta < 48, $"批注行离正文中部 {delta:0.#}px");
            editor.ScrollToVerticalOffset(0);
            editor.UpdateLayout();
            Assert.Equal(0, editor.VerticalOffset);
            list.SelectedIndex = -1;
            list.SelectedIndex = 0;
            editor.Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
            editor.UpdateLayout();
            var again = editor.Selection.Start.GetCharacterRect(LogicalDirection.Forward);
            var recentered = Math.Abs(again.Top + again.Height / 2 - editor.ViewportHeight / 2);
            Assert.True(editor.VerticalOffset > 100, $"再次定位后滚动偏移只有 {editor.VerticalOffset:0.#}");
            Assert.True(recentered < 48, $"再次定位后离中部 {recentered:0.#}px");
        }
        finally
        {
            listWindow?.Close();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    });
}
