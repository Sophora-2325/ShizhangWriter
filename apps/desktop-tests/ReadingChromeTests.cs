using NovelGm.Desktop.Api;
using Xunit;

namespace NovelGm.Desktop.Tests;

public class ReadingChromeTests
{
    [Fact]
    public void Reading_page_names_the_book_and_sits_place()
    {
        var off = ReadingChrome.Build(false, "山雨", "第2章 夜谈", 1, 5);
        Assert.False(off.On);
        Assert.Equal("", off.Work);
        Assert.Equal("", off.Progress);

        var on = ReadingChrome.Build(true, "山雨", "第2章 夜谈", 1, 5);
        Assert.True(on.On);
        Assert.Equal("山雨", on.Work);
        Assert.Equal("第2章 夜谈", on.Chapter);
        Assert.Equal("2 / 5", on.Progress);
        Assert.True(on.CanPrev);
        Assert.True(on.CanNext);
    }

    [Fact]
    public void First_and_last_chapters_close_the_matching_edge()
    {
        var first = ReadingChrome.Build(true, "山雨", "第1章", 0, 3);
        Assert.False(first.CanPrev);
        Assert.True(first.CanNext);
        Assert.Equal("1 / 3", first.Progress);

        var last = ReadingChrome.Build(true, "山雨", "第3章", 2, 3);
        Assert.True(last.CanPrev);
        Assert.False(last.CanNext);
    }

    [Fact]
    public void Sustained_reading_column_uses_full_width_character_measure()
    {
        Assert.InRange(ReadingChrome.LongFormChars, 28, 38);
        Assert.Equal(640, ReadingChrome.ColumnWidth(20));
        Assert.Equal(760, ReadingChrome.ColumnWidth(20, 100));
        Assert.True(ReadingChrome.ReadingFontSize > 17);
    }
}
