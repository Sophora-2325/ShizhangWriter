using System.Windows.Controls;
using System.Windows.Input;
using Xunit;

namespace NovelGm.Desktop.Tests;

public class ShellMenuTests
{
    [Fact]
    public void Work_commands_match_for_menu_and_shortcut()
    {
        Assert.False(ShellCommandState.CanUseWork(false));
        Assert.True(ShellCommandState.CanUseWork(true));

        var ran = false;
        var blocked = new RelayCommand(() => ran = true, () => ShellCommandState.CanUseWork(false));
        Assert.False(blocked.CanExecute(null));
        Assert.False(ran);

        var allowed = new RelayCommand(() => ran = true, () => ShellCommandState.CanUseWork(true));
        Assert.True(allowed.CanExecute(null));
        allowed.Execute(null);
        Assert.True(ran);
    }

    [Theory]
    [InlineData("", null, -1, false)]
    [InlineData("", null, 1, false)]
    [InlineData("3", 3, -1, false)]
    [InlineData("3", 3, 1, false)]
    [InlineData("2,5,9", 2, -1, false)]
    [InlineData("2,5,9", 2, 1, true)]
    [InlineData("2,5,9", 9, 1, false)]
    [InlineData("2,5,9", 9, -1, true)]
    [InlineData("2,5", 4, 1, false)]
    public void Chapter_step_matches_for_menu_and_shortcut(string nos, int? selected, int delta, bool can)
    {
        var ordered = string.IsNullOrEmpty(nos)
            ? Array.Empty<int>()
            : nos.Split(',').Select(int.Parse).ToArray();
        Assert.Equal(can, ShellCommandState.CanStep(ordered, selected, delta));
    }

    [Fact]
    public void Edit_target_keeps_selection_and_save_target_when_the_menu_opens() => StaRunner.Run(() =>
    {
        var body = new TextBox { Text = "正文目标" };
        body.Select(1, 2);
        var plan = new TextBox { Text = "规划目标" };
        plan.Select(0, 2);

        var kept = EditTargetState.Remember(body, new TextBox(), insideMenu: true);
        Assert.Same(body, kept);
        Assert.Equal(1, body.SelectionStart);
        Assert.Equal(2, body.SelectionLength);

        var moved = EditTargetState.Remember(body, plan, insideMenu: false);
        Assert.Same(plan, moved);
        Assert.Equal(1, body.SelectionStart);
        Assert.Equal(2, body.SelectionLength);
        Assert.Equal(0, plan.SelectionStart);

        Assert.True(ShellCommandState.CanCopyText(kept is not null));
        Assert.False(ShellCommandState.CanModifyText(true, readOnly: true));
        Assert.False(ShellCommandState.CanCopyText(false));
        Assert.False(ShellCommandState.CanModifyText(false, readOnly: false));
    });

    [Fact]
    public void Layout_width_and_usage_checks_are_exclusive_where_required()
    {
        var pure = MenuMarks.Layout(true);
        var assist = MenuMarks.Layout(false);
        Assert.True(pure.First);
        Assert.False(pure.Second);
        Assert.Equal("Mode.Pure", pure.LabelKey);
        Assert.False(assist.First);
        Assert.True(assist.Second);
        Assert.Equal("Mode.Assist", assist.LabelKey);

        var paper = MenuMarks.Width(true);
        var full = MenuMarks.Width(false);
        Assert.True(paper.First);
        Assert.False(paper.Second);
        Assert.Equal("Menu.WidthPaper", paper.LabelKey);
        Assert.False(full.First);
        Assert.True(full.Second);
        Assert.Equal("Menu.WidthFull", full.LabelKey);

        var shown = MenuMarks.Usage(true);
        var hidden = MenuMarks.Usage(false);
        Assert.True(shown.First);
        Assert.Equal("Menu.UsageOn", shown.LabelKey);
        Assert.False(hidden.First);
        Assert.Equal("Menu.UsageOff", hidden.LabelKey);
    }

    [Fact]
    public void Settings_pages_use_stable_tags_and_save_failure_restores_the_previous_value()
    {
        var xaml = File.ReadAllText(Path.Combine(DesktopRoot(), "SettingsWindow.xaml"));
        Assert.Contains("Tag=\"general\"", xaml);
        Assert.Contains("Tag=\"appearance\"", xaml);
        Assert.Contains("Tag=\"models\"", xaml);
        Assert.Contains("Tag=\"advanced\"", xaml);
        Assert.DoesNotContain("SelectedIndex", xaml);
        Assert.DoesNotContain("BodyWidthCombo", xaml);
        Assert.Contains("BackAdvanced_Click", xaml);

        Assert.Equal("general", SettingsPage.General);
        Assert.True(SettingsPage.IsPrimary(SettingsPage.Appearance));
        Assert.True(SettingsPage.IsPrimary(SettingsPage.Models));
        Assert.True(SettingsPage.IsPrimary(SettingsPage.Advanced));
        Assert.False(SettingsPage.IsPrimary(SettingsPage.Skills));
        Assert.True(SettingsPage.IsKnown(SettingsPage.Plugins));

        Assert.False(ModelChoiceRules.AllowLocalOnly(turningOn: true, alreadyOn: false, hasLocal: false));
        Assert.True(ModelChoiceRules.AllowLocalOnly(turningOn: true, alreadyOn: true, hasLocal: false));
        Assert.True(ModelChoiceRules.AllowLocalOnly(turningOn: true, alreadyOn: false, hasLocal: true));
        Assert.Equal("kept", ModelChoiceRules.Confirmed(saved: false, requested: "cloud", previous: "kept"));
        Assert.Equal("cloud", ModelChoiceRules.Confirmed(saved: true, requested: "cloud", previous: "kept"));
    }

    private static string DesktopRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "desktop-wpf", "SettingsWindow.xaml")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "desktop-wpf");
    }
}
