using System.Windows;
using System.Windows.Documents;
using NovelGm.Desktop.Api;
using Xunit;

namespace NovelGm.Desktop.Tests;

public class ReadingSessionTests
{
    [Fact]
    public void Anchor_survives_insertions_and_does_not_jump_to_an_unrelated_chapter()
    {
        var original = "山门外的雨一直没有停。\n林舟把信收进怀里，转身走入夜色。\n天亮以前，他不会回来。";
        var anchor = ReadingAnchor.Capture(original, original.IndexOf("林舟", StringComparison.Ordinal));
        var updated = "新加的一段。\n" + original;
        Assert.Equal(updated.IndexOf("林舟", StringComparison.Ordinal), anchor.Resolve(updated));
        Assert.Equal(0, anchor.Resolve("另一章完全不同的正文。"));
    }

    [Fact]
    public void Preferences_reject_invalid_or_unbounded_values()
    {
        var prefs = new ReadingPreferences { FontSize = double.NaN, LineSpacing = 99, Characters = 1 };
        prefs.Normalize();
        Assert.Equal(26, prefs.FontSize);
        Assert.Equal(2.4, prefs.LineSpacing);
        Assert.Equal(28, prefs.Characters);
    }

    [Fact]
    public void Chapter_search_supports_chinese_and_case_insensitive_english()
    {
        Assert.Equal(new[] { 0, 5 }, ReadingText.FindMatches("林舟在雨中林舟又回来了", "林舟"));
        Assert.Equal(new[] { 0, 7 }, ReadingText.FindMatches("Letter LETTER", "letter"));
        Assert.Empty(ReadingText.FindMatches("正文", ""));
        Assert.Empty(ReadingText.FindMatches("正文", "不存在"));
    }

    [Fact]
    public void Find_marks_every_match_and_clears_without_changing_the_words()
    {
        RunSta(() =>
        {
            var doc = new FlowDocument(new Paragraph(new Run("林舟在雨中林舟又回来了")));
            var plain = new TextRange(doc.ContentStart, doc.ContentEnd).Text;
            var matches = ReadingText.FindMatches(plain, "林舟");
            var marks = ReadingText.MarkMatches(doc.ContentStart, doc.ContentEnd, matches, 1, 2);
            Assert.Equal(2, marks.Count);
            Assert.Equal(plain, new TextRange(doc.ContentStart, doc.ContentEnd).Text);
            Assert.Equal(ReadingText.FindCurrentBrush, BackgroundAt(doc, matches[1]));
            Assert.Equal(ReadingText.FindMarkBrush, BackgroundAt(doc, matches[0]));
            ReadingText.ClearMarks(doc);
            Assert.NotEqual(ReadingText.FindMarkBrush, BackgroundAt(doc, matches[0]));
            Assert.NotEqual(ReadingText.FindCurrentBrush, BackgroundAt(doc, matches[1]));
        });
    }

    private static object BackgroundAt(FlowDocument doc, int offset)
    {
        var point = ReadingText.Pointer(doc.ContentStart, doc.ContentEnd, offset);
        return new TextRange(point, point.GetPositionAtOffset(1, LogicalDirection.Forward) ?? doc.ContentEnd)
            .GetPropertyValue(TextElement.BackgroundProperty);
    }

    [Fact]
    public void Reading_scale_uses_reading_font_for_line_height_and_preserves_emphasis()
    {
        RunSta(() =>
        {
            var doc = RichBody.FromPlain("正文第一段。\n第二段。");
            var first = (Paragraph)doc.Blocks.FirstBlock;
            ((Run)first.Inlines.FirstInline).FontWeight = FontWeights.Bold;
            ReadingText.Scale(doc, new ReadingPreferences());
            Assert.Equal(26 * 1.3, first.LineHeight);
            Assert.Equal(FontWeights.Bold, ((Run)first.Inlines.FirstInline).FontWeight);
            Assert.Equal(52, first.TextIndent);
            var pointer = ReadingText.Pointer(doc.ContentStart, doc.ContentEnd, 3);
            Assert.Equal(3, ReadingText.Offset(doc.ContentStart, pointer));
        });
    }

    [Fact]
    public void Position_store_roundtrips_and_recovers_from_corrupt_state()
    {
        var root = Path.Combine(Path.GetTempPath(), "novelgm-reading-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(root, "reading.json");
        try
        {
            var store = new ReadingPositionStore(file);
            var anchor = ReadingAnchor.Capture("原文的一段话", 3);
            store.Remember("work:1", anchor);
            Assert.True(store.Save());
            Assert.Equal(anchor, new ReadingPositionStore(file).Find("work:1"));
            File.WriteAllText(file, "broken json");
            Assert.Null(new ReadingPositionStore(file).Find("work:1"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Reader_snapshot_never_changes_the_editor_document_or_source_fields()
    {
        RunSta(() =>
        {
            var editor = RichBody.FromPlain("第一段。\n需要修改的一段。");
            ((Run)((Paragraph)editor.Blocks.FirstBlock).Inlines.FirstInline).FontWeight = FontWeights.Bold;
            var rich = RichBody.SaveXaml(editor);
            var text = RichBody.PlainText(editor);
            var chapter = new NovelGm.Desktop.Models.ChapterDto { No = 1, Body_Text = text, Body_Rich = rich, Status = "final" };
            var reader = new NovelGm.Desktop.Controls.ReadingView(new ReadingPositionStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")));
            reader.ShowChapter(new NovelGm.Desktop.Models.ProjectDto { Id = "test", Title = "测试" }, chapter, 0, 1);
            Assert.NotSame(editor, reader.Snapshot);
            Assert.Equal(text, RichBody.PlainText(editor));
            Assert.Equal(rich, RichBody.SaveXaml(editor));
            Assert.Equal(rich, chapter.Body_Rich);
            Assert.Equal("final", chapter.Status);
            Assert.Equal(26, reader.Snapshot.FontSize);
        });
    }

    [Fact]
    public void Centered_caption_keeps_its_alignment_and_zero_indent()
    {
        RunSta(() =>
        {
            var caption = new Paragraph(new Run("题注")) { TextAlignment = TextAlignment.Center, TextIndent = 0 };
            var doc = new FlowDocument(caption);
            ReadingText.Scale(doc, new ReadingPreferences { FontSize = 24 });
            Assert.Equal(TextAlignment.Center, caption.TextAlignment);
            Assert.Equal(0, caption.TextIndent);
            Assert.Equal(24 * 1.3, caption.LineHeight);
        });
    }

    internal static void RunSta(Action action) => StaRunner.Run(action);
}
