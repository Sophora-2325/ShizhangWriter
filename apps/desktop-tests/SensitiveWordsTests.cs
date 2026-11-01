using System.Runtime.ExceptionServices;
using System.Text;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Controls;
using NovelGm.Desktop.Models;
using Xunit;

namespace NovelGm.Desktop.Tests;

public class SensitiveWordsTests
{
    [Fact]
    public void Loading_trims_blank_lines_and_dedupes_ignoring_case()
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, " 甲 \r\n\r\n乙\r\n甲\r\ntest\r\nTEST\r\n", new UTF8Encoding(false));
            var words = new SensitiveWords(path);

            Assert.True(words.HasWords);
            Assert.Equal(new[] { "甲", "乙", "test" }, words.Words);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Add_rejects_duplicates_and_remove_drops_the_word()
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, "甲\r\n乙\r\n", new UTF8Encoding(false));
            var words = new SensitiveWords(path);

            Assert.False(words.Add(""));
            Assert.False(words.Add("  "));
            Assert.False(words.Add("甲"));
            Assert.False(words.Add("甲 "));
            Assert.True(words.Add("丙"));
            Assert.Equal(3, words.Words.Count);

            Assert.True(words.Remove("乙"));
            Assert.False(words.Remove("不存在"));
            Assert.Equal(new[] { "甲", "丙" }, words.Words);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Import_uses_strict_utf8_and_reports_invalid_bytes()
    {
        var path = TempFile();
        var bad = TempFile();
        try
        {
            File.WriteAllText(path, "甲\r\n乙\r\n", new UTF8Encoding(false));
            var words = new SensitiveWords(path);

            File.WriteAllBytes(bad, new byte[] { 0xC3, 0x28, 0x00 });
            var invalid = words.Import(bad);
            Assert.Equal(SensitiveImportStatus.InvalidUtf8, invalid.Status);
            Assert.Equal(new[] { "甲", "乙" }, words.Words);

            File.WriteAllText(bad, "丙\r\n乙\r\ntest\r\n", new UTF8Encoding(false));
            var ok = words.Import(bad);
            Assert.Equal(SensitiveImportStatus.Ok, ok.Status);
            Assert.Equal(2, ok.Added);
            Assert.Contains("丙", words.Words);
            Assert.Contains("test", words.Words);

            File.WriteAllText(bad, "  \r\n\r\n", new UTF8Encoding(false));
            var empty = words.Import(bad);
            Assert.Equal(SensitiveImportStatus.Empty, empty.Status);
        }
        finally
        {
            File.Delete(path);
            File.Delete(bad);
        }
    }

    [Fact]
    public void Export_writes_utf8_with_crlf_and_no_bom()
    {
        var path = TempFile();
        var export = TempFile();
        try
        {
            File.WriteAllText(path, "甲\r\n乙\r\n", new UTF8Encoding(false));
            var words = new SensitiveWords(path);

            words.Export(export);
            var bytes = File.ReadAllBytes(export);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
            var text = new UTF8Encoding(false, true).GetString(bytes);
            Assert.Equal("甲\r\n乙\r\n", text);

            Assert.True(words.TrySave(out var error), error);
            Assert.Equal("甲\r\n乙\r\n", File.ReadAllText(path, new UTF8Encoding(false, true)));
        }
        finally
        {
            File.Delete(path);
            File.Delete(export);
        }
    }

    [Fact]
    public void Scanner_reports_offsets_context_and_skips_superseded_chapters()
    {
        const string text = "他在雨中等了很久，雨终于停了。";
        var hits = SensitiveScanner.Scan(text, new[] { "雨" }, 3, "夜谈");
        Assert.Equal(2, hits.Count);
        Assert.Equal(text.IndexOf('雨'), hits[0].Offset);
        Assert.Equal(3, hits[0].ChapterNo);
        Assert.Equal("夜谈", hits[0].ChapterTitle);
        Assert.Contains("雨", hits[0].Context);

        var english = SensitiveScanner.Scan("Letter LETTER", new[] { "letter" });
        Assert.Equal(2, english.Count);

        var chapters = new List<ChapterDto>
        {
            new() { No = 1, Title = "A", Body_Text = "敏感" },
            new() { No = 2, Title = "B", Body_Text = "敏感", Status = "superseded" },
            new() { No = 3, Title = "C", Body_Text = "敏感" },
        };
        var bookHits = SensitiveScanner.ScanChapters(chapters, new[] { "敏感" });
        Assert.Equal(2, bookHits.Count);
        Assert.DoesNotContain(bookHits, hit => hit.ChapterNo == 2);
        Assert.Equal(new[] { 1, 3 }, bookHits.Select(hit => hit.ChapterNo));

        Assert.Empty(SensitiveScanner.Scan("正文", Array.Empty<string>()));
        Assert.Empty(SensitiveScanner.ScanChapters(chapters, Array.Empty<string>()));
    }

    [Fact]
    public void Window_scans_the_current_draft_and_excludes_superseded_chapters_in_book_scope() => Sta(() =>
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, "敏感\r\n", new UTF8Encoding(false));
            var library = new SensitiveWords(path);
            var chapters = new List<ChapterDto>
            {
                new() { No = 1, Title = "一", Body_Text = "第一章正文也敏感" },
                new() { No = 2, Title = "二", Body_Text = "第二章有敏感" },
                new() { No = 3, Title = "三", Body_Text = "第三章也有敏感", Status = "superseded" },
            };
            var window = new SensitiveWordsWindow(
                () => chapters,
                no => no == 1 ? (1, "第一章草稿里有敏感") : ((int No, string Text)?)null,
                _ => { },
                1,
                library);

            window.RefreshForTest();
            var hit = Assert.Single(window.HitsForTest);
            Assert.Equal(1, hit.ChapterNo);
            Assert.Contains("草稿", hit.Context);
            Assert.False(window.IsBookScopeForTest);

            window.ScopeBookButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.True(window.IsBookScopeForTest);
            Assert.Equal(new[] { 1, 2 }, window.HitsForTest.Select(h => h.ChapterNo).Distinct());

            Assert.Contains("Sens.Hits", window.StatusTextForTest);
        }
        finally
        {
            File.Delete(path);
        }
    });

    [Fact]
    public void Window_reports_scan_start_and_result_to_the_tool_panel() => Sta(() =>
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, "敏感\r\n", new UTF8Encoding(false));
            var library = new SensitiveWords(path);
            var chapters = new List<ChapterDto> { new() { No = 1, Title = "一", Body_Text = "正文敏感" } };
            var window = new SensitiveWordsWindow(
                () => chapters,
                _ => ((int No, string Text)?)null,
                _ => { },
                1,
                library);

            var started = 0;
            SensitiveScanResult? result = null;
            window.ScanStarted += (_, _) => started++;
            window.ScanFinished += (_, r) => result = r;

            window.RefreshForTest();

            Assert.Equal(1, started);
            Assert.NotNull(result);
            Assert.Equal(SensitiveScanOutcome.Found, result!.Outcome);
            Assert.Equal(1, result.HitCount);
            Assert.False(result.BookScope);
        }
        finally
        {
            File.Delete(path);
        }
    });

    [Fact]
    public void Sensitive_three_language_dictionaries_define_the_same_keys()
    {
        var sets = NotesPanelTests.PanelStringKeys.Sets("Sensitive");
        Assert.NotEmpty(sets["zh-CN"]);
        Assert.Equal(sets["zh-CN"], sets["en-US"]);
        Assert.Equal(sets["zh-CN"], sets["ja-JP"]);
        Assert.All(sets["zh-CN"], key => Assert.StartsWith("Sens.", key));
    }

    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), "novel-gm-sensitive-" + Guid.NewGuid().ToString("N") + ".txt");

    private static void Sta(Action action) =>
        StaRunner.Run(action, TimeSpan.FromSeconds(35), "WPF sensitive operation timed out");
}
