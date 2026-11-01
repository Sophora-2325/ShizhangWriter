using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Controls;
using Xunit;

namespace NovelGm.Desktop.Tests;

public class FormatToolbarStateTests
{
    [Fact]
    public void Toolbar_state_matrix_controls_readonly_busy_and_comment() => StaRunner.Run(() =>
    {
        var bar = new FormatToolbar();
        bar.SetState(new ToolbarState(true, true, true, false, false));
        Assert.True(bar.FocusButton.IsEnabled);
        Assert.True(bar.WidthButton.IsEnabled);
        Assert.True(bar.HistoryButton.IsEnabled);
        Assert.True(bar.PublishButton.IsEnabled);
        Assert.False(bar.CommentButton.IsEnabled);
        Assert.True(bar.BoldButton.IsEnabled);
        Assert.True(bar.ImageButton.IsEnabled);
        Assert.True(bar.MicButton.IsEnabled);

        bar.SetState(new ToolbarState(true, true, true, true, false));
        Assert.True(bar.CommentButton.IsEnabled);

        bar.SetState(new ToolbarState(true, true, false, false, false));
        Assert.True(bar.HistoryButton.IsEnabled);
        Assert.True(bar.PublishButton.IsEnabled);
        Assert.False(bar.CommentButton.IsEnabled);
        Assert.False(bar.BoldButton.IsEnabled);
        Assert.False(bar.ImageButton.IsEnabled);
        Assert.False(bar.MicButton.IsEnabled);
        Assert.True(bar.FocusButton.IsEnabled);
        Assert.True(bar.WidthButton.IsEnabled);

        bar.SetState(new ToolbarState(true, true, true, true, true));
        Assert.False(bar.HistoryButton.IsEnabled);
        Assert.False(bar.PublishButton.IsEnabled);
        Assert.False(bar.CommentButton.IsEnabled);
        Assert.False(bar.BoldButton.IsEnabled);
        Assert.False(bar.ImageButton.IsEnabled);
        Assert.False(bar.MicButton.IsEnabled);
        Assert.True(bar.FocusButton.IsEnabled);
        Assert.True(bar.WidthButton.IsEnabled);

        bar.SetState(new ToolbarState(true, false, false, false, false));
        Assert.False(bar.HistoryButton.IsEnabled);
        Assert.True(bar.PublishButton.IsEnabled);
    }, TimeSpan.FromSeconds(30), "工具条状态矩阵超时");

    [Fact]
    public void Toolbar_width_focus_and_dictation_states_are_visible() => StaRunner.Run(() =>
    {
        var bar = new FormatToolbar();
        bar.SetWidthMode(true);
        Assert.Equal(L.T("Fmt.WidthPaper"), bar.WidthText.Text);
        bar.SetWidthMode(false);
        Assert.Equal(L.T("Fmt.WidthFull"), bar.WidthText.Text);

        bar.SetFocusMode(true);
        Assert.True(bar.FocusButton.IsChecked == true);
        Assert.Equal(L.T("Fmt.ExitFocus"), bar.FocusText.Text);
        bar.SetFocusMode(false);
        Assert.False(bar.FocusButton.IsChecked == true);
        Assert.Equal(L.T("Fmt.Focus"), bar.FocusText.Text);

        bar.SetState(new ToolbarState(true, true, true, true, false));
        bar.SetDictationState(DictationState.Recording);
        Assert.Equal(Visibility.Visible, bar.StopButton.Visibility);
        Assert.True(bar.StopButton.IsEnabled);
        Assert.Equal(Visibility.Visible, bar.RecIndicator.Visibility);
        Assert.True(bar.MicButton.IsChecked);

        bar.SetDictationState(DictationState.Transcribing);
        Assert.Equal(Visibility.Collapsed, bar.StopButton.Visibility);
        Assert.Equal(L.T("Stt.Transcribing"), bar.DictationStatus.Text);

        bar.SetDictationState(DictationState.Failed, "麦克风不可用");
        Assert.Equal("麦克风不可用", bar.DictationStatus.Text);
        Assert.Equal(Visibility.Visible, bar.DictationStatus.Visibility);

        var popped = new FormatToolbar(showFocus: false);
        Assert.Equal(Visibility.Collapsed, popped.FocusGroup.Visibility);
    }, TimeSpan.FromSeconds(30), "工具条状态显示超时");

    [Fact]
    public void Toolbar_collapses_style_image_comment_history_in_that_order_at_minimum_width() => StaRunner.Run(() =>
    {
        var bar = new FormatToolbar { Target = new RichTextBox { FontSize = 17 } };
        var panel = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        panel.Children.Add(bar);
        panel.Children.Add(new Border());
        using var host = new Host(panel, 1000, 260);
        bar.SetState(new ToolbarState(true, true, true, true, false));

        bar.ApplyOverflowForTest(1000);
        Assert.Equal(Visibility.Visible, bar.StyleGroup.Visibility);
        Assert.Equal(Visibility.Visible, bar.ImageGroup.Visibility);
        Assert.Equal(Visibility.Visible, bar.CommentGroup.Visibility);
        Assert.Equal(Visibility.Visible, bar.HistoryGroup.Visibility);
        Assert.Equal(Visibility.Collapsed, bar.MoreStyleGroup.Visibility);

        bar.ApplyOverflowForTest(420);
        Assert.Equal(Visibility.Collapsed, bar.StyleGroup.Visibility);
        Assert.Equal(Visibility.Collapsed, bar.ImageGroup.Visibility);
        Assert.Equal(Visibility.Collapsed, bar.CommentGroup.Visibility);
        Assert.Equal(Visibility.Collapsed, bar.HistoryGroup.Visibility);
        Assert.Equal(Visibility.Visible, bar.MoreStyleGroup.Visibility);
        Assert.Equal(Visibility.Visible, bar.MoreImageButton.Visibility);
        Assert.Equal(Visibility.Visible, bar.MoreCommentButton.Visibility);
        Assert.Equal(Visibility.Visible, bar.MoreHistoryButton.Visibility);
        Assert.Equal(Visibility.Visible, bar.DictationGroup.Visibility);
        Assert.Equal(Visibility.Visible, bar.MoreGroup.Visibility);

        bar.SetDictationState(DictationState.Recording);
        bar.ApplyOverflowForTest(420);
        Assert.Equal(Visibility.Visible, bar.DictationGroup.Visibility);
        Assert.Equal(Visibility.Visible, bar.StopButton.Visibility);

        host.Window.Width = 420;
        host.Window.UpdateLayout();
        bar.ApplyOverflowForTest(420);
        bar.UpdateLayout();
        Assert.True(bar.ToolRow.ActualWidth <= bar.RootBar.ActualWidth + 0.5,
            $"工具行 {bar.ToolRow.ActualWidth:0.#} 超出 {bar.RootBar.ActualWidth:0.#}");
    }, TimeSpan.FromSeconds(30), "工具条最窄布局超时");

    private sealed class Host : IDisposable
    {
        public Window Window { get; }

        public Host(UIElement content, double width, double height)
        {
            Window = new Window { Content = content, Width = width, Height = height, ShowInTaskbar = false };
            Window.Show();
            Window.UpdateLayout();
            Window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        }

        public void Dispose() => Window.Close();
    }
}
