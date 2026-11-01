using System.Windows;
using System.Windows.Media;
using NovelGm.Desktop.Api;

namespace NovelGm.Desktop;

public partial class MainWindow
{
    private readonly Dictionary<(string, int), SaveFeedback> _saveFeedback = new();
    private SaveFeedback? CurrentSaveFeedback => _project is null ? null : FeedbackFor(_project.Id, _selected?.No ?? 0);

    private SaveFeedback FeedbackFor(string project, int chapter)
    {
        var key = (project, chapter);
        if (!_saveFeedback.TryGetValue(key, out var feedback))
            _saveFeedback[key] = feedback = new();
        return feedback;
    }

    private void UpdateSaveFeedback()
    {
        if (SaveStatusText is null) return;
        SaveMenu.IsEnabled = _project is not null;
        var feedback = CurrentSaveFeedback;
        if (feedback is null) { SaveStatusText.Visibility = Visibility.Collapsed; return; }
        feedback.Dirty = AnyDirty;
        SaveStatusText.Visibility = _selected is not null || AnyDirty || feedback.Pending > 0 || feedback.Failed
            ? Visibility.Visible : Visibility.Collapsed;
        SaveStatusText.Text = L.T(feedback.Key);
        SaveStatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
            feedback.Failed ? "DangerBrush" : "MutedBrush");
    }
}
