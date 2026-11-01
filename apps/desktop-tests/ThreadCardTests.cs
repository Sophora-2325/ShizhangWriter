using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using NovelGm.Desktop.Models;
using Xunit;

namespace NovelGm.Desktop.Tests;

public class ThreadCardTests
{
    [Fact]
    public void Default_sections_follow_reference_and_switch_without_losing_drafts() => StaRunner.Run(() =>
    {
        var window = new ThreadEditorWindow(new() { Id = "t", Name = "主线", Priority = 7 });
        Assert.Equal(Visibility.Collapsed, window.DetailPane.Visibility);
        Assert.Null(window.FindName("PriorityBox"));
        var properties = Assert.IsType<UniformGrid>(((StackPanel)window.KindCombo.Parent).Parent);
        Assert.Equal(3, properties.Columns);
        Click(window.DevelopmentButton); window.DirectiveBox.Text = "天台相遇";
        Assert.Equal(Visibility.Visible, window.DetailPane.Visibility);
        Assert.Equal(new GridLength(1, GridUnitType.Star), window.DetailColumn.Width);
        Click(window.ToneButton); window.ToneBox.Text = "温柔克制";
        Assert.Equal(Visibility.Collapsed, window.DirectiveBox.Visibility);
        Click(window.DevelopmentButton); Assert.Equal("天台相遇", window.DirectiveBox.Text);
        Click(window.ToneButton); Assert.Equal("温柔克制", window.ToneBox.Text);
        Click(window.ToneButton); Assert.Equal(Visibility.Collapsed, window.DetailPane.Visibility);
        window.Close();
    });

    [Fact]
    public void Milestone_edits_are_isolated_and_save_failure_can_retry() => StaRunner.Run(() =>
    {
        var original = new PlotThreadDto { Id = "t", Name = "主线", Priority = 9,
            Milestones = new() { new() { Id = "m1", Name = "相遇" }, new() { Id = "m2", Name = "告白" } } };
        var window = new ThreadEditorWindow(original);
        Click(window.MilestonesButton);
        var rows = window.MilestoneList.Children.OfType<Border>().Select(b => (StackPanel)b.Child).ToList();
        ((CheckBox)rows[0].Children[0]).IsChecked = true;
        rows[1].Children.OfType<TextBox>().First().Text = "毕业告白";
        Assert.False(original.Milestones[0].Completed);
        Assert.Equal("告白", original.Milestones[1].Name);
        window.TimeCombo.SelectedIndex = 2; window.ToneBox.Text = "清新";
        window.SaveAsync = _ => Task.FromException(new IOException("offline"));
        Click(window.SaveButton);
        WpfPump.Until(() => window.SaveButton.IsEnabled);
        Assert.False(window.SavedSuccessfully); Assert.NotEmpty(window.ErrorText.Text);
        Assert.Equal("毕业告白", window.Result.Next_Milestone);
        Assert.Equal("future", window.Result.Time_Position); Assert.Equal(9, window.Result.Priority);
        window.SaveAsync = _ => Task.CompletedTask;
        Click(window.SaveButton);
        WpfPump.Until(() => window.SavedSuccessfully);
        Assert.Equal("m1", window.Result.Milestones[0].Id);
        Assert.True(window.Result.Milestones[0].Completed);
    });

    [Fact]
    public void Adding_reordering_and_deleting_nodes_updates_next_pending_node() => StaRunner.Run(() =>
    {
        var window = new ThreadEditorWindow(new() { Name = "主线", Next_Milestone = "旧节点" });
        Click(window.MilestonesButton); Click(window.AddMilestoneButton);
        var second = (StackPanel)((Border)window.MilestoneList.Children[1]).Child;
        second.Children.OfType<TextBox>().First().Text = "新节点";
        var actions = second.Children.OfType<WrapPanel>().Single();
        Click((Button)actions.Children[1]); // move up
        var first = (StackPanel)((Border)window.MilestoneList.Children[0]).Child;
        Assert.Equal("新节点", first.Children.OfType<TextBox>().First().Text);
        Click((Button)first.Children.OfType<WrapPanel>().Single().Children[3]); // delete
        Assert.Single(window.MilestoneList.Children.OfType<Border>());
        window.SaveAsync = _ => Task.FromException(new IOException("retain for assertions"));
        Click(window.SaveButton); WpfPump.Until(() => window.SaveButton.IsEnabled);
        Assert.Equal("旧节点", window.Result.Next_Milestone);
        window.Close();
    });

    [Theory]
    [InlineData("tone")]
    [InlineData("development")]
    [InlineData("misc")]
    public void Empty_text_editor_fills_detail_pane_before_typing(string section) => StaRunner.Run(() =>
    {
        var window = new ThreadEditorWindow(new() { Id = "t", Name = "主线" });
        window.ShowSection(section);
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        void Layout()
        {
            root.Measure(new Size(928, 576));
            root.Arrange(new Rect(0, 0, 928, 576));
            root.UpdateLayout();
        }
        Layout();
        var editor = section == "tone" ? window.ToneBox : section == "misc" ? window.MiscBox : window.DirectiveBox;
        var content = (FrameworkElement)window.DetailPane.Child;
        Assert.True(content.ActualWidth > 200);
        Assert.InRange(editor.ActualWidth, content.ActualWidth - 1, content.ActualWidth + 1);
        Assert.True(editor.ActualHeight > 200);
        editor.Text = new string('长', 120); Layout();
        editor.Clear(); Layout();
        Assert.InRange(editor.ActualWidth, content.ActualWidth - 1, content.ActualWidth + 1);
        window.Close();
    });

    [Fact]
    public void Tone_title_is_centered_in_its_own_row() => StaRunner.Run(() =>
    {
        var window = new ThreadEditorWindow(new() { Name = "主线" });
        window.ShowSection("tone");
        Assert.Equal(TextAlignment.Center, window.DetailTitle.TextAlignment);
        window.Close();
    });

    [Fact]
    public void Misc_switching_cancel_failure_retry_and_clear_preserve_original() => StaRunner.Run(() =>
    {
        var original = new PlotThreadDto { Id = "t", Name = "主线", Misc = "原备注" };
        var window = new ThreadEditorWindow(original);
        Click(window.MiscButton); window.MiscBox.Text = "改稿\r\n第二行";
        Click(window.ToneButton); Click(window.MiscButton);
        Assert.Equal("改稿\r\n第二行", window.MiscBox.Text);
        window.SaveAsync = _ => Task.FromException(new IOException("offline"));
        Click(window.SaveButton); WpfPump.Until(() => window.SaveButton.IsEnabled);
        Assert.Equal("改稿\n第二行", window.Result.Misc); Assert.Equal("原备注", original.Misc);
        Assert.False(window.SavedSuccessfully); Assert.Equal(Visibility.Visible, window.MiscBox.Visibility);
        window.MiscBox.Clear(); window.SaveAsync = _ => Task.CompletedTask;
        Click(window.SaveButton); WpfPump.Until(() => window.SavedSuccessfully);
        Assert.Equal("", window.Result.Misc);
        var canceled = new ThreadEditorWindow(original); Click(canceled.MiscButton); canceled.MiscBox.Text = "不要保存";
        Click(canceled.CancelButton); Assert.False(canceled.SavedSuccessfully); Assert.Equal("原备注", original.Misc);
    });

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
}
