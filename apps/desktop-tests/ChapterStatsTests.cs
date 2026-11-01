using NovelGm.Desktop.Api;
using Xunit;

namespace NovelGm.Desktop.Tests;

public class ChapterStatsTests
{
    [Fact]
    public void Counts_words_paragraphs_and_selection_without_line_breaks()
    {
        const string text = "第一段\n\n第二段\r\n第三段";
        var stats = ChapterStats.From(text, "第二段\r\n第三");
        Assert.Equal(text.Length, stats.Words);
        Assert.Equal(3, stats.Paragraphs);
        Assert.Equal("第二段第三".Length, stats.Selected);
    }

    [Fact]
    public void Empty_or_whitespace_only_text_has_no_paragraphs()
    {
        Assert.Equal(0, ChapterStats.From("", "").Paragraphs);
        Assert.Equal(0, ChapterStats.From(" \n \r\n", null).Paragraphs);
        Assert.Equal(0, ChapterStats.From(null, null).Words);
    }

    [Fact]
    public void Selection_is_zero_when_the_editor_has_no_selection()
    {
        Assert.Equal(0, ChapterStats.From("正文", "").Selected);
    }
}
