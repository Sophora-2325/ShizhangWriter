using System.Windows;
using System.Windows.Threading;
using NovelGm.Desktop.Api;

namespace NovelGm.Desktop;

public partial class MainWindow
{
    private readonly SessionUsage _sessionUsage = new(App.StartedAt);
    private readonly DispatcherTimer _usageTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
    private readonly CancellationTokenSource _usageCancellation = new();
    private bool _usageGenerating, _usageStarted;

    private void InitializeUsage()
    {
        UsageBubbleView.Hidden += (_, _) => SetUsageVisible(false);
        UsageBubbleView.SizeChanged += (_, _) => UpdateUsageDefault();
        UsageBubbleView.CapsuleResized += (_, _) => UpdateUsageDefault();
        CenterPane.SizeChanged += (_, _) => UpdateUsageDefault();
        BodyWordCountText.SizeChanged += (_, _) => UpdateUsageDefault();
        BodyHost.IsVisibleChanged += (_, _) => Dispatcher.BeginInvoke(UpdateUsageDefault, DispatcherPriority.Loaded);
        Closed += (_, _) =>
        {
            _usageTimer.Stop();
            _usageCancellation.Cancel();
            _usageCancellation.Dispose();
        };
        SetUsageVisible(UiPrefs.Current.UsageVisible, false);
    }

    private void StartUsage()
    {
        if (_usageStarted) return;
        _usageStarted = true;
        _usageTimer.Tick += async (_, _) => await RefreshUsageAsync();
        _usageTimer.Start();
        UpdateUsageDefault();
        _ = RefreshUsageAsync();
    }

    private async Task RefreshUsageAsync()
    {
        if (_usageCancellation.IsCancellationRequested || App.Api is null) return;
        var cancellation = _usageCancellation.Token;
        await _sessionUsage.RefreshAsync(App.Api.GetSessionUsageAsync, cancellation);
        if (cancellation.IsCancellationRequested) return;
        UpdateUsageNumbers();
    }

    private void UpdateUsageNumbers() => UsageBubbleView.SetUsage(_sessionUsage.Snapshot,
        _sessionUsage.HasRead, _sessionUsage.IsStale, _usageGenerating);

    private void SetUsageGenerating(bool value)
    {
        _usageGenerating = value;
        UpdateUsageNumbers();
        if (!value) _ = RefreshUsageAsync();
    }

    private void SetUsageVisible(bool visible, bool persist = true)
    {
        UiPrefs.Current.UsageVisible = visible;
        var mark = MenuMarks.Usage(visible);
        UsageVisibleItem.IsChecked = mark.First;
        UsageMenu.InputGestureText = L.T(mark.LabelKey);
        UsageBubbleView.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        UsageClearance.Height = visible ? 64 : 0;
        if (persist) UiPrefs.Save();
        Dispatcher.BeginInvoke(UpdateUsageDefault, DispatcherPriority.Loaded);
    }

    private void UpdateUsageDefault()
    {
        if (UsageBubbleView.ActualWidth <= 0 || CenterPane.ActualWidth <= 0) return;
        // Position outside the text column and 20 DIP above its existing save/word-count row.
        var right = CenterPane.TranslatePoint(new Point(CenterPane.ActualWidth, 0), UsageBubbleView).X;
        var statusTop = BodyHost.IsVisible && BodyWordCountText.ActualHeight > 0
            ? BodyWordCountText.TranslatePoint(new Point(), UsageBubbleView).Y
            : UsageBubbleView.ActualHeight - 24;
        UsageBubbleView.SetDefaultPosition(new Point(right - UsageBubbleView.CapsuleWidth - 20, statusTop - 44 - 20));
    }

    private void UsageVisible_Click(object sender, RoutedEventArgs e) => SetUsageVisible(UsageVisibleItem.IsChecked);
    private void UsageReset_Click(object sender, RoutedEventArgs e)
    {
        SetUsageVisible(true);
        UsageBubbleView.ResetPosition();
    }
}
