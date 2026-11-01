using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using System.Windows.Threading;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Controls;
using NovelGm.Desktop.Models;
using Xunit;

namespace NovelGm.Desktop.Tests;

public class LoreOrganizationTests
{
    [Fact]
    public void Work_information_patch_only_contains_changed_fields_and_clones_cover()
    {
        var project = new ProjectDto { Id = "a", Genre = new() { "奇幻" }, Tone = "温暖", Audience = "青年",
            Cover = new() { Preview = "插图/a.jpg", Original = "插图/original.png" } };
        var draft = new WorkInfoDraft(project);
        Assert.False(draft.Changes().HasChanges);
        draft.Genre = " 奇幻，冒险,冒险\n";
        draft.Cover.Preview = "插图/b.jpg";
        var changes = draft.Changes();
        Assert.Equal(new[] { "奇幻", "冒险" }, changes.Genre);
        Assert.Null(changes.Tone); Assert.Null(changes.Audience);
        Assert.Equal("插图/a.jpg", project.Cover.Preview);
        draft.Cover = new();
        Assert.Equal("", draft.Changes().Cover!.Preview);
    }

    [Fact]
    public void Misc_and_narration_preserve_each_others_drafts_when_project_reloads() => Sta(() =>
    {
        var project = new ProjectDto { Id = "a", Style = new() { Pov = "third_limited", Tense = "past", Sentence_Length = "medium" } };
        var misc = new LoreMisc(); var narrative = new NarrativeSettings();
        misc.Load(project); narrative.Load(project);
        ((TextBox)misc.FindName("BannedBox")).Text = "保留的草稿";
        ((ComboBox)narrative.FindName("PovBox")).SelectedIndex = 1;
        Assert.Single(narrative.Changes());
        Assert.Equal("style.pov", narrative.Changes()[0].Path);
        Assert.Equal("first", narrative.Changes()[0].New);
        Assert.Single(misc.Changes());
        Assert.Equal("style.banned_words", misc.Changes()[0].Path);
        project.Style.Pov = "first";
        narrative.MarkSaved(); narrative.Load(project); misc.Load(project);
        Assert.Empty(narrative.Changes());
        Assert.Equal("保留的草稿", misc.Changes()[0].New);
        Assert.Null(narrative.FindName("TenseBox"));
        misc.MarkSaved(); misc.Load(project); narrative.Load(project);
        Assert.Empty(narrative.Changes());
        Assert.Equal("past", project.Style.Tense);
    });

    [Fact]
    public void Moved_fields_have_one_editing_owner_and_readonly_state_is_enforced() => Sta(() =>
    {
        var misc = new LoreMisc(); var narrative = new NarrativeSettings();
        var project = new ProjectDto { Id = "a", Style = new() };
        misc.Load(project); narrative.Load(project);
        Assert.Null(misc.FindName("PovBox")); Assert.Null(misc.FindName("GenreBox"));
        Assert.Null(misc.FindName("AudienceBox")); Assert.Null(misc.FindName("ToneBox"));
        misc.IsReadOnly = true; narrative.IsReadOnly = true;
        Assert.True(((TextBox)misc.FindName("BannedBox")).IsReadOnly);
        Assert.False(((Button)misc.FindName("SaveButton")).IsEnabled);
        Assert.False(((ComboBox)narrative.FindName("PovBox")).IsEnabled);
    });

    [Fact]
    public void Work_window_filters_superseded_chapters_and_supports_missing_folders() => Sta(() =>
    {
        var window = new WorkInfoWindow(new ProjectDto { Id = "a", Title = "作品", Root_Missing = true },
            new[] { new ChapterDto { No = 3, Title = "旧章", Status = "superseded", Word_Count = 999 },
                new ChapterDto { No = 2, Title = "第二章", Status = "drafted", Word_Count = 20 },
                new ChapterDto { No = 1, Title = "第一章", Status = "final", Word_Count = 10 } });
        var grid = (DataGrid)window.FindName("ChapterGrid");
        var rows = grid.ItemsSource.Cast<WorkInfoWindow.ChapterRow>().ToList();
        Assert.Equal(2, rows.Count); Assert.StartsWith("1.", rows[0].Name);
        Assert.False(((Button)window.FindName("SaveButton")).IsEnabled);
        Assert.False(((Button)window.FindName("PickCoverButton")).IsEnabled);
        window.Close();
    });

    private static void Sta(Action action) => StaRunner.Run(action, TimeSpan.FromSeconds(20));
}
