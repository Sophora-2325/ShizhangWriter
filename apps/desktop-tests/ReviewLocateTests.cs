using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Controls;
using NovelGm.Desktop.Models;
using Xunit;
namespace NovelGm.Desktop.Tests;

[Collection("serial-body-editing")]
public class ReviewLocateTests
{
    [Fact]
    public void Review_paper_is_centered_and_scales_down_keeping_page_size() => Sta(() =>
    {
        var path = TempPath();
        try
        {
            var reader = new ReadingView(new ReadingPositionStore(path));
            using var host = new Host(reader, 420, 850);
            var chapter = new ChapterDto { No = 1, Title = "没有寄出的信", Body_Text = SampleText() };
            reader.ShowChapter(new ProjectDto { Id = "test", Title = "山雨" }, chapter, 0, 2);
            WpfPump.Until(() => reader.PageCount > 1);
            host.Window.UpdateLayout();

            var area = reader.ReadingAreaBounds;
            var wide = reader.PaperBounds;
            Assert.False(wide.IsEmpty, "paper bounds should be measured");
            Assert.False(area.IsEmpty, "reading area should be measured");
            AssertCentered(wide, area);
            Assert.Equal(360, wide.Width, 1);
            Assert.Equal(320, reader.Snapshot.PageWidth);
            Assert.Equal(560, reader.Snapshot.PageHeight);

            host.Window.Width = 280;
            host.Window.UpdateLayout();
            WpfPump.Until(() => reader.PaperBounds.Width > 0 && reader.PaperBounds.Width < 359);
            var narrow = reader.PaperBounds;
            Assert.True(narrow.Width < 360, $"narrow host should scale the paper, got {narrow.Width}");
            Assert.True(narrow.Width > 200, $"narrow host should keep the paper readable, got {narrow.Width}");
            Assert.Equal(360.0 / 648.0, narrow.Width / narrow.Height, 2);
            AssertCentered(narrow, reader.ReadingAreaBounds);
            Assert.Equal(320, reader.Snapshot.PageWidth);
            Assert.Equal(560, reader.Snapshot.PageHeight);
            reader.DisposeReader();
        }
        finally { TryDelete(path); }
    });

    [Fact]
    public void Selected_review_text_becomes_the_locate_quote() => Sta(() =>
    {
        var path = TempPath();
        try
        {
            var reader = new ReadingView(new ReadingPositionStore(path));
            using var host = new Host(reader, 420, 850);
            var text = "第一段第一句。第一段第二句。\n第二段第一句。第二段第二句。\n第三段第一句。";
            var chapter = new ChapterDto { No = 3, Title = "雨夜", Body_Text = text };
            reader.ShowChapter(new ProjectDto { Id = "proj-1", Title = "山雨" }, chapter, 0, 1);
            WpfPump.Until(() => reader.PageCount > 0 && reader.PageNumber >= 1);
            host.Window.UpdateLayout();

            var quote = "第一段第二句。";
            var at = text.IndexOf(quote, StringComparison.Ordinal);
            reader.SelectForTest(at, quote.Length);
            var request = reader.BuildLocateRequest();

            Assert.NotNull(request);
            Assert.Equal("proj-1", request!.ProjectId);
            Assert.Equal(3, request.ChapterNo);
            Assert.Equal(quote, request.Quote);
            Assert.Equal(at, request.Start.Offset);
            Assert.Equal(at, request.End.Offset - quote.Length);
            reader.DisposeReader();
        }
        finally { TryDelete(path); }
    });

    [Fact]
    public void Without_selection_the_first_body_paragraph_of_the_page_is_used() => Sta(() =>
    {
        var path = TempPath();
        try
        {
            var reader = new ReadingView(new ReadingPositionStore(path));
            using var host = new Host(reader, 420, 850);
            var text = "开头第一段。\n第二段的内容。\n第三段的内容。";
            var chapter = new ChapterDto { No = 1, Title = "没有寄出的信", Body_Text = text };
            reader.ShowChapter(new ProjectDto { Id = "test", Title = "山雨" }, chapter, 0, 1);
            WpfPump.Until(() => reader.PageCount > 0 && reader.PageNumber >= 1);
            host.Window.UpdateLayout();

            var request = reader.BuildLocateRequest();

            Assert.NotNull(request);
            Assert.Equal("开头第一段。", request!.Quote);
            Assert.Equal(0, request.Start.Offset);
            Assert.DoesNotContain("没有寄出的信", request.Quote);
            reader.DisposeReader();
        }
        finally { TryDelete(path); }
    });

    [Fact]
    public void Empty_chapter_has_no_locate_target() => Sta(() =>
    {
        var path = TempPath();
        try
        {
            var reader = new ReadingView(new ReadingPositionStore(path));
            using var host = new Host(reader, 420, 850);
            var chapter = new ChapterDto { No = 1, Title = "空章", Body_Text = "" };
            reader.ShowChapter(new ProjectDto { Id = "test", Title = "山雨" }, chapter, 0, 1);
            WpfPump.Until(() => reader.PageCount > 0 && reader.PageNumber >= 1);
            host.Window.UpdateLayout();
            Assert.Null(reader.BuildLocateRequest());
            reader.DisposeReader();
        }
        finally { TryDelete(path); }
    });

    [Fact]
    public void Unique_quote_resolves_immediately()
    {
        var body = "甲乙丙丁戊己";
        var request = Request(body, 2, 3, "丙");
        Assert.True(ReviewLocator.TryResolve(body, request, out var start, out var length, out var exact));
        Assert.Equal(2, start);
        Assert.Equal(1, length);
        Assert.True(exact);
    }

    [Fact]
    public void Repeated_quote_uses_the_original_offset_while_the_revision_matches()
    {
        var body = "重复的句子。中间隔开。重复的句子。末尾。";
        var quote = "重复的句子。";
        var second = body.LastIndexOf(quote, StringComparison.Ordinal);
        Assert.True(second > 0);
        var request = Request(body, second, second + quote.Length, quote);
        Assert.True(ReviewLocator.TryResolve(body, request, out var start, out var length, out var exact));
        Assert.Equal(second, start);
        Assert.Equal(quote.Length, length);
        Assert.True(exact);
    }

    [Fact]
    public void Repeated_quote_stays_unresolved_when_the_revision_changed_and_context_is_ambiguous()
    {
        var body = "重复。中间。重复。";
        var request = new ReviewLocateRequest("p", 1,
            new ReadingAnchor(0, "", "", "stale-revision"),
            new ReadingAnchor(6, "", "", "stale-revision"), "重复。");
        Assert.False(ReviewLocator.TryResolve(body, request, out _, out _, out _));
    }

    [Fact]
    public void Repeated_quote_resolves_when_the_context_identifies_one_occurrence()
    {
        var body = "甲。重复。乙。丙。重复。丁。";
        var quote = "重复。";
        var first = body.IndexOf(quote, StringComparison.Ordinal);
        Assert.Equal(2, first);
        var request = new ReviewLocateRequest("p", 1,
            ReadingAnchor.Capture(body, first) with { Revision = "stale-revision" },
            ReadingAnchor.Capture(body, first + quote.Length) with { Revision = "stale-revision" }, quote);
        Assert.True(ReviewLocator.TryResolve(body, request, out var start, out var length, out var exact));
        Assert.Equal(first, start);
        Assert.Equal(quote.Length, length);
        Assert.False(exact);
    }

    [Fact]
    public void Missing_quote_is_rejected()
    {
        var body = "正文里没有这句话。";
        var request = Request(body, 0, 1, "不存在的片段");
        Assert.False(ReviewLocator.TryResolve(body, request, out _, out _, out _));
        Assert.False(ReviewLocator.TryResolve(body, Request(body, 0, 1, ""), out _, out _, out _));
    }

    [Fact]
    public void Locate_highlight_leaves_the_document_and_undo_alone() => Sta(() =>
    {
        var editor = new RichTextBox { Document = RichBody.FromPlain("甲乙丙丁戊己庚辛") };
        using var host = new Host(editor);
        BodyPresentation.Attach(editor);
        host.Window.UpdateLayout();

        var text = Plain(editor.Document);
        var undo = editor.CanUndo;
        BodyPresentation.ShowLocate(editor.Document, 2, 3);
        host.Window.UpdateLayout();

        Assert.Equal(text, Plain(editor.Document));
        Assert.Equal(undo, editor.CanUndo);
        Assert.DoesNotContain(
            editor.Document.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines).OfType<Run>(),
            r => r.ReadLocalValue(TextElement.BackgroundProperty) != DependencyProperty.UnsetValue);
        Assert.True(BodyPresentation.HasLocate(editor.Document));

        BodyPresentation.ClearLocate(editor.Document);
        Assert.False(BodyPresentation.HasLocate(editor.Document));
        Assert.Equal(text, Plain(editor.Document));
        Assert.Equal(undo, editor.CanUndo);
    });

    [Fact]
    public void Clicking_elsewhere_clears_locate_without_consuming_the_click_or_changing_text() => Sta(() =>
    {
        var editor = new RichTextBox { Document = RichBody.FromPlain("审阅同步提示不应长期保留") };
        var other = new Button { Content = "其他操作" };
        var layout = new DockPanel(); DockPanel.SetDock(other, Dock.Top);
        layout.Children.Add(other); layout.Children.Add(editor);
        using var host = new Host(layout);
        BodyPresentation.Attach(editor);
        var dismissed = 0;
        BodyPresentation.DismissLocateOnInteraction(host.Window, editor, () => dismissed++);
        var text = RichBody.SaveXaml(editor.Document); var undo = editor.CanUndo;
        var changes = 0; editor.TextChanged += (_, _) => changes++;
        BodyPresentation.ShowLocate(editor.Document, 0, 4);
        var click = PointerDown(other);
        Assert.False(BodyPresentation.HasLocate(editor.Document));
        Assert.False(click.Handled);
        Assert.Equal(1, dismissed);
        Assert.Equal(text, RichBody.SaveXaml(editor.Document));
        Assert.Equal(undo, editor.CanUndo); Assert.Equal(0, changes);
    });

    [Fact]
    public void Sync_click_can_show_a_new_mark_and_next_click_dismisses_it() => Sta(() =>
    {
        var editor = new RichTextBox { Document = RichBody.FromPlain("甲乙丙丁戊己庚辛") };
        var sync = new Button { Content = "同步" };
        var layout = new DockPanel(); DockPanel.SetDock(sync, Dock.Top);
        layout.Children.Add(sync); layout.Children.Add(editor);
        using var host = new Host(layout);
        BodyPresentation.DismissLocateOnInteraction(host.Window, editor);
        sync.Click += (_, _) => BodyPresentation.ShowLocate(editor.Document, 2, 3);
        PointerDown(sync);
        sync.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Assert.True(BodyPresentation.HasLocate(editor.Document));
        PointerDown(editor);
        Assert.False(BodyPresentation.HasLocate(editor.Document));
        BodyPresentation.ShowLocate(editor.Document, 2, 3);
        var map = new BodyTextMap(editor.Document);
        editor.Selection.Select(map.Pointer(4), map.Pointer(4));
        Assert.False(BodyPresentation.HasLocate(editor.Document));
    });

    private static MouseButtonEventArgs PointerDown(UIElement target)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        { RoutedEvent = Mouse.PreviewMouseDownEvent };
        target.RaiseEvent(args); return args;
    }

    private static ReviewLocateRequest Request(string body, int start, int end, string quote) =>
        new("p", 1, ReadingAnchor.Capture(body, start), ReadingAnchor.Capture(body, end), quote);

    private static string Plain(FlowDocument document) =>
        new TextRange(document.ContentStart, document.ContentEnd).Text;

    private static void AssertCentered(Rect paper, Rect area)
    {
        var dx = Math.Abs(paper.Left + paper.Width / 2 - (area.Left + area.Width / 2));
        var dy = Math.Abs(paper.Top + paper.Height / 2 - (area.Top + area.Height / 2));
        Assert.True(dx < 2, $"paper center x is {dx:0.##}px off the reading area center");
        Assert.True(dy < 2, $"paper center y is {dy:0.##}px off the reading area center");
    }

    private static string SampleText() => string.Join("\n",
        Enumerable.Repeat("雨落在山门前。他收起没有寄出的信，沿着灯光往前走。", 900));

    private static string TempPath() => Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class Host : IDisposable
    {
        public Window Window { get; }
        public Host(UIElement content, double width = 600, double height = 400)
        {
            Window = new Window { Content = content, Width = width, Height = height, ShowInTaskbar = false };
            Window.Show();
            Window.UpdateLayout();
        }
        public void Dispose()
        {
            Window.Close();
            Window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        }
    }

    private static void Sta(Action action) => StaRunner.Run(action, TimeSpan.FromSeconds(35), "WPF review locate operation timed out");
}
