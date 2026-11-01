using NovelGm.Desktop.Api;
using Xunit;

namespace NovelGm.Desktop.Tests;

public class ToolRulesTests
{
    private static ToolContext Ready => new(
        HasWork: true, HasChapter: true, HasBody: true, Editable: true,
        CanComment: true, HasFolder: true, Busy: false);

    [Fact]
    public void Empty_state_only_leaves_nothing_enabled()
    {
        var empty = new ToolContext(false, false, false, false, false, false, false);
        foreach (var action in new[]
                 {
                     ToolRules.Stats, ToolRules.Copy, ToolRules.ExportChapter, ToolRules.FindChapter,
                     ToolRules.FindBook, ToolRules.Sensitive, ToolRules.Spacing, ToolRules.Invisibles,
                     ToolRules.CommentAdd, ToolRules.CommentList, ToolRules.Versions, ToolRules.Backup,
                     ToolRules.Publish,
                 })
            Assert.False(ToolRules.IsEnabled(action, empty), action);
    }

    [Fact]
    public void Ready_state_enables_every_action()
    {
        foreach (var action in new[]
                 {
                     ToolRules.Stats, ToolRules.Copy, ToolRules.ExportChapter, ToolRules.FindChapter,
                     ToolRules.FindBook, ToolRules.Sensitive, ToolRules.Spacing, ToolRules.Invisibles,
                     ToolRules.CommentAdd, ToolRules.CommentList, ToolRules.Versions, ToolRules.Backup,
                     ToolRules.Publish,
                 })
            Assert.True(ToolRules.IsEnabled(action, Ready), action);
    }

    [Fact]
    public void Read_only_still_allows_view_copy_export_and_stats()
    {
        var readOnly = Ready with { Editable = false, CanComment = false };
        Assert.True(ToolRules.IsEnabled(ToolRules.Stats, readOnly));
        Assert.True(ToolRules.IsEnabled(ToolRules.Copy, readOnly));
        Assert.True(ToolRules.IsEnabled(ToolRules.ExportChapter, readOnly));
        Assert.True(ToolRules.IsEnabled(ToolRules.Invisibles, readOnly));
        Assert.True(ToolRules.IsEnabled(ToolRules.CommentList, readOnly));
        Assert.False(ToolRules.IsEnabled(ToolRules.Spacing, readOnly));
        Assert.False(ToolRules.IsEnabled(ToolRules.CommentAdd, readOnly));
    }

    [Fact]
    public void Busy_disables_saving_actions_but_keeps_stats_and_copy()
    {
        var busy = Ready with { Editable = false, Busy = true };
        Assert.True(ToolRules.IsEnabled(ToolRules.Stats, busy));
        Assert.True(ToolRules.IsEnabled(ToolRules.Copy, busy));
        Assert.False(ToolRules.IsEnabled(ToolRules.ExportChapter, busy));
        Assert.False(ToolRules.IsEnabled(ToolRules.FindChapter, busy));
        Assert.False(ToolRules.IsEnabled(ToolRules.Versions, busy));
        Assert.False(ToolRules.IsEnabled(ToolRules.Backup, busy));
        Assert.False(ToolRules.IsEnabled(ToolRules.Publish, busy));
    }

    [Fact]
    public void Comment_add_requires_a_selection()
    {
        Assert.False(ToolRules.IsEnabled(ToolRules.CommentAdd, Ready with { CanComment = false }));
        Assert.True(ToolRules.IsEnabled(ToolRules.CommentAdd, Ready));
    }

    [Fact]
    public void Backup_requires_a_work_folder()
    {
        Assert.False(ToolRules.IsEnabled(ToolRules.Backup, Ready with { HasFolder = false }));
    }
}
