using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Controls;
using NovelGm.Desktop.Models;
using Xunit;

namespace NovelGm.Desktop.Tests;

public class VersionsWindowTests
{
    [Fact]
    public void Versions_sort_newest_first_and_unknown_sources_fall_back()
    {
        var versions = new List<VersionDto>
        {
            new() { Id = "a", Saved_At = "2026-10-18T10:00:00+08:00", Source = "autosave" },
            new() { Id = "b", Saved_At = "2026-10-18T12:00:00+08:00", Source = "ai" },
            new() { Id = "c", Saved_At = "not-a-time", Source = "legacy-text" },
        };

        var sorted = VersionsWindow.SortVersions(versions);
        Assert.Equal(new[] { "b", "a", "c" }, sorted.Select(v => v.Id));

        var auto = VersionsWindow.SourceLabel("autosave");
        Assert.False(string.IsNullOrWhiteSpace(auto));
        Assert.NotEqual("autosave", auto);
        Assert.NotEqual(VersionsWindow.SourceLabel("ai"), auto);
        Assert.Equal("weird-source", VersionsWindow.SourceLabel("weird-source"));
        Assert.NotEmpty(VersionsWindow.FormatTime("2026-10-18T12:00:00+08:00"));
        Assert.Equal("not-a-time", VersionsWindow.FormatTime("not-a-time"));
    }

    [Fact]
    public void Window_lists_sorted_versions_and_previews_rich_and_legacy_bodies() => Sta(() =>
    {
        var api = new FakeVersionsApi();
        api.Versions.Add(new VersionDto
        {
            Id = "old", Chapter_No = 1, Saved_At = "2026-10-18T10:00:00+08:00",
            Source = "autosave", Word_Count = 100, Format_Available = true,
        });
        api.Versions.Add(new VersionDto
        {
            Id = "new", Chapter_No = 1, Saved_At = "2026-10-18T12:00:00+08:00",
            Source = "ai", Word_Count = 200, Format_Available = true,
        });
        api.Versions.Add(new VersionDto
        {
            Id = "legacy", Chapter_No = 1, Saved_At = "2026-10-18T09:00:00+08:00",
            Source = "legacy-text", Word_Count = 50, Format_Available = false,
        });

        var rich = RichBody.SaveXaml(RichBody.FromPlain("带格式正文"));
        api.Details["new"] = new VersionDetailDto
        {
            Id = "new", Chapter_No = 1, Saved_At = "2026-10-18T12:00:00+08:00", Source = "ai",
            Body_Text = "带格式正文", Body_Rich = rich, Format_Available = true, Word_Count = 200,
        };
        api.Details["legacy"] = new VersionDetailDto
        {
            Id = "legacy", Chapter_No = 1, Saved_At = "2026-10-18T09:00:00+08:00", Source = "legacy-text",
            Body_Text = "旧纯文本", Body_Rich = "", Format_Available = false, Word_Count = 50,
        };

        var window = new VersionsWindow("p1", 1, "作品", _ => Task.CompletedTask, api);
        Wait(window.LoadAsync());

        Assert.Equal(3, window.VersionList.Items.Count);
        var first = (VersionDto)((ListBoxItem)window.VersionList.Items[0]).Tag!;
        Assert.Equal("new", first.Id);
        Assert.Equal("new", window.SelectedVersionForTest?.Id);
        Assert.True(window.RestoreButton.IsEnabled);
        Assert.Equal(Visibility.Collapsed, window.HintText.Visibility);
        var preview = new TextRange(window.PreviewBox.Document.ContentStart, window.PreviewBox.Document.ContentEnd).Text;
        Assert.Contains("带格式正文", preview);

        var legacy = (VersionDto)((ListBoxItem)window.VersionList.Items[2]).Tag!;
        Assert.Equal("legacy", legacy.Id);
        Wait(window.SelectVersionAsync(legacy));

        Assert.Equal("legacy", window.SelectedVersionForTest?.Id);
        Assert.Equal(Visibility.Visible, window.HintText.Visibility);
        Assert.Contains("Versions.FormatUnavailable", window.HintText.Text);
        var legacyText = new TextRange(window.PreviewBox.Document.ContentStart, window.PreviewBox.Document.ContentEnd).Text;
        Assert.Contains("旧纯文本", legacyText);
    });

    [Fact]
    public void Superseded_chapter_is_viewable_but_restore_is_disabled() => Sta(() =>
    {
        var api = new FakeVersionsApi { ChapterStatus = "superseded" };
        api.Versions.Add(new VersionDto
        {
            Id = "v1", Chapter_No = 2, Saved_At = "2026-10-18T10:00:00+08:00",
            Source = "manual", Word_Count = 12, Format_Available = true,
        });
        api.Details["v1"] = new VersionDetailDto
        {
            Id = "v1", Chapter_No = 2, Saved_At = "2026-10-18T10:00:00+08:00", Source = "manual",
            Body_Text = "作废章节正文", Body_Rich = "", Format_Available = true, Word_Count = 12,
        };

        var window = new VersionsWindow("p1", 2, "作品", _ => Task.CompletedTask, api);
        Wait(window.LoadAsync());

        Assert.Equal(1, window.VersionList.Items.Count);
        Assert.True(window.SelectedVersionForTest is not null);
        Assert.Equal(Visibility.Visible, window.ReadOnlyHint.Visibility);
        Assert.False(window.RestoreButton.IsEnabled);
    });

    [Fact]
    public void Versions_three_language_dictionaries_define_the_same_keys()
    {
        var sets = NotesPanelTests.PanelStringKeys.Sets("Versions");
        Assert.NotEmpty(sets["zh-CN"]);
        Assert.Equal(sets["zh-CN"], sets["en-US"]);
        Assert.Equal(sets["zh-CN"], sets["ja-JP"]);
        Assert.All(sets["zh-CN"], key => Assert.StartsWith("Versions.", key));
    }

    private sealed class FakeVersionsApi : IVersionsApi
    {
        public List<VersionDto> Versions { get; } = new();
        public Dictionary<string, VersionDetailDto> Details { get; } = new();
        public string ChapterStatus { get; set; } = "draft";

        public Task<List<VersionDto>> ListAsync(string projectId, int chapterNo) =>
            Task.FromResult(Versions.ToList());

        public Task<VersionDetailDto> GetAsync(string projectId, int chapterNo, string versionId) =>
            Task.FromResult(Details[versionId]);

        public Task<ChapterDto> RestoreAsync(string projectId, int chapterNo, string versionId) =>
            Task.FromResult(new ChapterDto { No = chapterNo });

        public Task<ChapterDto?> TryGetChapterAsync(string projectId, int chapterNo) =>
            Task.FromResult<ChapterDto?>(new ChapterDto { No = chapterNo, Status = ChapterStatus });
    }

    private static void Wait(Task task)
    {
        WpfPump.Until(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }

    private static void Sta(Action action) =>
        StaRunner.Run(action, TimeSpan.FromSeconds(35), "WPF versions operation timed out");
}
