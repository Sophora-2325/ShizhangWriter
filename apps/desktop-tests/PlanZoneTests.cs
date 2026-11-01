using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using NovelGm.Desktop.Controls;
using NovelGm.Desktop.Models;
using Xunit;

namespace NovelGm.Desktop.Tests;

public class PlanZoneTests
{
    private static void Sta(Action action) => StaRunner.Run(action);
    private static void Layout(PlanZone panel, double width) { panel.Measure(new Size(width, 680)); panel.Arrange(new Rect(0, 0, width, 680)); panel.UpdateLayout(); }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    [Fact]
    public void Default_menu_and_wide_narrow_navigation_preserve_draft_without_writes() => Sta(() =>
    {
        var panel = new PlanZone(); Layout(panel, 1000);
        Assert.Equal(Visibility.Collapsed, panel.DetailPanel.Visibility);
        Assert.Equal(0, panel.MenuScroll.ScrollableHeight);
        var changes = 0; panel.Changed += (_, _) => changes++;
        Click(panel.GoalButton); Layout(panel, 1000);
        Assert.Equal(384, panel.MenuColumn.ActualWidth);
        Assert.Equal(Visibility.Visible, panel.MenuScroll.Visibility);
        panel.SceneGoalBox.Text = "打开城门";
        Click(panel.MiscButton); panel.MiscBox.Text = "雨夜，不解释来信来源";
        Click(panel.GoalButton);
        Assert.Equal("打开城门", panel.SceneGoalBox.Text);
        Assert.Equal(2, changes);
        Layout(panel, 420);
        Assert.Equal(Visibility.Collapsed, panel.MenuScroll.Visibility);
        Assert.Equal(Visibility.Visible, panel.BackButton.Visibility);
        Click(panel.BackButton);
        Assert.Equal(Visibility.Visible, panel.MenuScroll.Visibility);
        Assert.Equal(Visibility.Collapsed, panel.DetailPanel.Visibility);
        Assert.Equal(2, changes);
        Assert.Equal("雨夜，不解释来信来源", panel.Read().Misc);
    });

    [Fact]
    public void Load_and_copy_roundtrip_all_fields_and_unknown_selections() => Sta(() =>
    {
        var panel = new PlanZone(); var changes = 0; panel.Changed += (_, _) => changes++;
        var plan = new ChapterPlanDto { Free_Text = "剧情", Scene_Goal = "目标", Conflict = "冲突", Stakes = "代价", Misc = "补充", Open_Decision_For_User = "是否开门", Target_Word_Count = 5000,
            Present_Character_Ids = new() { "林舟", "unknown-character" }, Required_Threads = new() { "old-thread" }, Scene_Types = new() { "daily", "unknown-scene" }, Must_Include = new() { "信" }, Must_Not = new() { "揭晓" } };
        panel.Load(plan, new LoreDto { Characters = new() { new() { Id = "c1", Name = "林舟" } } });
        var result = panel.Read().Copy();
        Assert.Equal(0, changes);
        Assert.Equal(plan.Free_Text, result.Free_Text); Assert.Equal(plan.Scene_Goal, result.Scene_Goal);
        Assert.Equal(plan.Conflict, result.Conflict); Assert.Equal(plan.Stakes, result.Stakes); Assert.Equal(plan.Misc, result.Misc);
        Assert.Equal(plan.Open_Decision_For_User, result.Open_Decision_For_User); Assert.Equal(5000, result.Target_Word_Count);
        Assert.Equal(new[] { "c1", "unknown-character" }, result.Present_Character_Ids);
        Assert.Equal(plan.Required_Threads, result.Required_Threads); Assert.Equal(plan.Scene_Types, result.Scene_Types);
        Assert.Equal(plan.Must_Include, result.Must_Include); Assert.Equal(plan.Must_Not, result.Must_Not);
        Assert.False(PlanZone.IsEmpty(new ChapterPlanDto { Misc = "仅杂项" }));
    });

    [Theory]
    [InlineData("characters")]
    [InlineData("threads")]
    [InlineData("scenes")]
    public void Lists_add_once_remove_only_association_and_respect_readonly(string kind) => Sta(() =>
    {
        var lore = new LoreDto { Characters = new() { new() { Id = "item", Name = "人物" } }, Threads = new() { new() { Id = "item", Name = "脉络" } } };
        var panel = new PlanZone(); panel.Load(new(), lore); panel.OpenSection(kind);
        var id = kind == "scenes" ? "daily" : "item";
        var changes = 0; panel.Changed += (_, _) => changes++;
        panel.AddSelection(kind, new[] { id, id }); panel.AddSelection(kind, new[] { id });
        Assert.Equal(1, changes);
        Assert.Single(panel.ItemsPanel.Children);
        panel.IsReadOnly = true; panel.RemoveSelection(kind, id); panel.AddSelection(kind, new[] { "other" });
        Assert.Equal(1, changes); Assert.Equal(Visibility.Collapsed, panel.AddButton.Visibility);
        Assert.True(panel.MiscBox.IsReadOnly);
        panel.IsReadOnly = false; panel.RemoveSelection(kind, id);
        Assert.Equal(2, changes);
        Assert.Single(lore.Characters); Assert.Single(lore.Threads);
    });

    [Fact]
    public void Picker_is_a_private_selection_until_confirm_and_existing_members_are_disabled() => Sta(() =>
    {
        var original = new List<string> { "c1" };
        var picker = new PlanSelectionWindow("人物", new[] { new PlanChoice("c1", "甲"), new PlanChoice("c2", "乙") }, original, true);
        Assert.False(picker.Checks[0].IsEnabled);
        picker.Checks[1].IsChecked = true;
        Assert.Equal(new[] { "c1", "c2" }, picker.SelectedIds);
        Assert.Equal(new[] { "c1" }, original);
        Assert.False(picker.CreateNew);
        picker.Close();
    });

    [Fact]
    public void Word_count_rejects_invalid_pastes_without_changing_saved_value() => Sta(() =>
    {
        var panel = new PlanZone(); panel.Load(new() { Target_Word_Count = 5000 }, new());
        foreach (var invalid in new[] { "-2", "0", "1.5", "abc", "999999999999999" })
        {
            panel.WordCountBox.Text = invalid;
            Assert.Equal(5000, panel.Read().Target_Word_Count);
            Assert.Equal(Visibility.Visible, panel.WordCountError.Visibility);
        }
        panel.WordCountBox.Text = ""; Assert.Equal(3000, panel.Read().Target_Word_Count);
        panel.WordCountBox.Text = "2000"; Assert.Equal(2000, panel.Read().Target_Word_Count);
        panel.Load(new() { Target_Word_Count = 0 }, new()); Assert.Equal(3000, panel.Read().Target_Word_Count);
    });

    [Fact]
    public void Catalog_uses_master_characters_and_chapter_threads_without_duplicates()
    {
        var master = new ProjectDto { Characters = new() { new() { Id = "c1", Name = "新姓名" } }, Threads = new() { new() { Id = "t1", Name = "作品脉络" } } };
        var chapter = new LoreDto { Characters = new() { new() { Id = "c1", Name = "旧姓名" }, new() { Id = "legacy" } }, Threads = new() { new() { Id = "t1", Name = "本章脉络" } } };
        var catalog = PlanZone.Catalog(master, chapter);
        Assert.Equal(2, catalog.Characters.Count); Assert.Equal("新姓名", catalog.Characters[0].Name);
        Assert.Equal("本章脉络", Assert.Single(catalog.Threads).Name);
    }

    [Fact]
    public void Autosave_does_not_confirm_checkpoint_and_manual_save_does() => Sta(() =>
    {
        var panel = new PlanZone(); panel.Load(new() { Scene_Goal = "进入时", Misc = "别项" }, new()); panel.OpenSection("goal");
        var disk = panel.Read(); var writes = 0;
        panel.Changed += (_, _) => disk = panel.Read().Copy(); // 模拟自动保存
        panel.PersistAsync = () => { disk = panel.Read().Copy(); writes++; return Task.FromResult(true); };
        panel.SceneGoalBox.Text = "自动保存的改动";
        Assert.Equal("自动保存的改动", disk.Scene_Goal);
        panel.CancelSectionAsync("goal").GetAwaiter().GetResult();
        Assert.Equal("进入时", disk.Scene_Goal); Assert.Equal("别项", disk.Misc);
        panel.SceneGoalBox.Text = "手动确认"; Click(panel.SaveSectionButton);
        panel.SceneGoalBox.Text = "再次修改"; Click(panel.CancelSectionButton);
        Assert.Equal("手动确认", disk.Scene_Goal); Assert.Equal(3, writes);
        panel.SceneGoalBox.Text = "离开时"; panel.OpenSection("misc"); panel.OpenSection("goal");
        panel.SceneGoalBox.Text = "重新进入后修改"; Click(panel.CancelSectionButton);
        Assert.Equal("离开时", disk.Scene_Goal);
    });

    [Fact]
    public void Rules_confirm_only_the_focused_group_and_buttons_remember_target() => Sta(() =>
    {
        var panel = new PlanZone(); panel.Load(new() { Must_Include = new() { "原出现" }, Must_Not = new() { "原禁止" } }, new()); panel.OpenSection("rules");
        panel.PersistAsync = () => Task.FromResult(true);
        panel.MustIncludeBox.Text = "新出现"; panel.MustNotBox.Text = "新禁止";
        Assert.Null(panel.SaveTarget);
        panel.SaveSectionAsync(panel.SaveTarget).GetAwaiter().GetResult(); // 无焦点不建立新起点
        Focus(panel.MustIncludeBox); Assert.Equal("include", panel.SaveTarget);
        panel.SaveSectionAsync(panel.SaveTarget).GetAwaiter().GetResult();
        Click(panel.CancelExcludeButton); Assert.Equal("原禁止", panel.MustNotBox.Text);
        Assert.Equal("exclude", panel.SaveTarget);
        panel.MustIncludeBox.Text = "再次出现"; Click(panel.CancelIncludeButton);
        Assert.Equal("新出现", panel.MustIncludeBox.Text);
        Focus(panel.SaveExcludeButton); Assert.Equal("exclude", panel.SaveTarget);
    });

    [Fact]
    public void Failed_save_preserves_checkpoint_and_success_confirms_submitted_text_not_later_typing() => Sta(() =>
    {
        var panel = new PlanZone(); panel.Load(new() { Misc = "原文" }, new()); panel.OpenSection("misc");
        panel.PersistAsync = () => Task.FromResult(false);
        panel.MiscBox.Text = "失败稿"; Assert.False(panel.SaveSectionAsync("misc").Result);
        panel.CancelSectionAsync("misc").GetAwaiter().GetResult(); Assert.Equal("原文", panel.MiscBox.Text);
        var result = new TaskCompletionSource<bool>(); panel.PersistAsync = () => result.Task;
        panel.MiscBox.Text = "提交稿"; var save = panel.SaveSectionAsync("misc");
        Assert.False(panel.SaveSectionButton.IsEnabled); Assert.False(panel.MiscBox.IsReadOnly);
        panel.MiscBox.Text = "等待期间继续输入";
        result.SetResult(true); WpfPump.Until(() => save.IsCompleted);
        panel.PersistAsync = () => Task.FromResult(true);
        Click(panel.CancelSectionButton); Assert.Equal("提交稿", panel.MiscBox.Text);
    });

    [Fact]
    public void Late_success_cannot_confirm_a_different_chapter_or_reentered_section() => Sta(() =>
    {
        var panel = new PlanZone(); panel.Load(new() { Misc = "旧章" }, new()); panel.OpenSection("misc");
        var result = new TaskCompletionSource<bool>(); panel.PersistAsync = () => result.Task;
        panel.MiscBox.Text = "旧章提交"; var save = panel.SaveSectionAsync("misc");
        panel.Load(new() { Misc = "新章" }, new()); panel.MiscBox.Text = "新章修改";
        result.SetResult(true); WpfPump.Until(() => save.IsCompleted);
        panel.PersistAsync = () => Task.FromResult(true); Click(panel.CancelSectionButton);
        Assert.Equal("新章", panel.MiscBox.Text);
        result = new TaskCompletionSource<bool>(); panel.PersistAsync = () => result.Task;
        panel.MiscBox.Text = "提交"; save = panel.SaveSectionAsync("misc");
        panel.MiscBox.Text = "离开"; panel.OpenSection("goal"); panel.OpenSection("misc");
        result.SetResult(true); WpfPump.Until(() => save.IsCompleted);
        panel.PersistAsync = () => Task.FromResult(true); panel.MiscBox.Text = "再修改";
        Click(panel.CancelSectionButton); Assert.Equal("离开", panel.MiscBox.Text);
    });

    [Fact]
    public void Word_count_validation_readonly_and_list_sections_limit_actions() => Sta(() =>
    {
        var panel = new PlanZone(); panel.Load(new() { Target_Word_Count = 5000 }, new()); panel.OpenSection("length");
        var writes = 0; panel.PersistAsync = () => { writes++; return Task.FromResult(true); };
        panel.WordCountBox.Text = "invalid"; Assert.False(panel.SaveSectionButton.IsEnabled);
        Assert.False(panel.SaveSectionAsync("length").Result); Assert.Equal(0, writes);
        Click(panel.CancelSectionButton); Assert.Equal("5000", panel.WordCountBox.Text); Assert.Equal(1, writes);
        panel.WordCountBox.Text = "3000"; panel.IsReadOnly = true;
        panel.CancelSectionAsync("length").GetAwaiter().GetResult();
        Assert.False(panel.SaveSectionAsync("length").Result); Assert.Equal("3000", panel.WordCountBox.Text);
        foreach (var section in new[] { "scenes", "characters", "threads", "rules" })
        { panel.OpenSection(section); Assert.Equal(Visibility.Collapsed, panel.DetailActions.Visibility); }
    });

    [Theory]
    [InlineData(420)] [InlineData(1000)]
    public void Ordinary_actions_are_visible_above_five_percent_bottom_inset(double width) => Sta(() =>
    {
        var panel = new PlanZone(); Layout(panel, width); panel.OpenSection("goal"); Layout(panel, width);
        Assert.Equal(Visibility.Visible, panel.DetailActions.Visibility);
        var bounds = panel.DetailActions.TransformToAncestor(panel.DetailPanel).TransformBounds(new Rect(panel.DetailActions.RenderSize));
        Assert.InRange(panel.DetailPanel.ActualHeight - bounds.Bottom, panel.DetailPanel.ActualHeight * .05, panel.DetailPanel.ActualHeight * .05 + 2);
        Assert.True(bounds.Top > 100);
        Assert.True(panel.SaveSectionButton.TransformToAncestor(panel.DetailActions).Transform(new Point()).X <
            panel.CancelSectionButton.TransformToAncestor(panel.DetailActions).Transform(new Point()).X);
    });

    private static void Focus(UIElement element) => element.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, null, element)
        { RoutedEvent = Keyboard.GotKeyboardFocusEvent });
}
