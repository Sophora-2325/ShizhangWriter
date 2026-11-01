using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Models;

namespace NovelGm.Desktop.Controls;

/// <summary>
/// 历史版本窗口：左侧版本列表（时间 / 来源 / 字数 /「仅文本」），右侧只读预览，可恢复。
/// 契约见 docs/ROUND_NOTES_AI_PLAN.md §3.4。
/// </summary>
public sealed partial class VersionsWindow : Window
{
    private readonly string _projectId;
    private readonly int _chapterNo;
    private readonly string _projectTitle;
    private readonly Func<int, Task> _onRestored;
    private readonly IVersionsApi _api;

    private List<VersionDto> _versions = new();
    private VersionDetailDto? _selected;
    private bool? _isReadOnly;
    private bool _loading;
    private bool _suppressSelection;
    private int _previewGeneration;

    public VersionsWindow(string projectId, int chapterNo, string projectTitle, Func<int, Task> onRestored)
        : this(projectId, chapterNo, projectTitle, onRestored, new VersionsClient(App.Api), null)
    {
    }

    internal VersionsWindow(string projectId, int chapterNo, string projectTitle, Func<int, Task> onRestored,
        IVersionsApi api, bool? isReadOnly = null)
    {
        _projectId = projectId;
        _chapterNo = chapterNo;
        _projectTitle = projectTitle;
        _onRestored = onRestored;
        _api = api;
        _isReadOnly = isReadOnly;

        InitializeComponent();
        PreviewBox.IsReadOnly = true;
        UpdateHeader();
        ReadOnlyHint.Visibility = _isReadOnly == true ? Visibility.Visible : Visibility.Collapsed;
        Loaded += async (_, _) => await LoadAsync();
        EventHandler relabel = (_, _) => OnLanguageChanged();
        LanguageManager.LanguageChanged += relabel;
        Closed += (_, _) => LanguageManager.LanguageChanged -= relabel;
    }

    public static void Show(Window owner, string projectId, int chapterNo, string projectTitle,
        Func<int, Task> onRestored)
    {
        var window = new VersionsWindow(projectId, chapterNo, projectTitle, onRestored) { Owner = owner };
        window.ShowDialog();
    }

    internal VersionDetailDto? SelectedVersionForTest => _selected;

    // ------------------------------------------------------------------ 加载

    internal async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            StatusText.Text = L.T("Versions.Loading");
            var versions = await _api.ListAsync(_projectId, _chapterNo);
            _versions = SortVersions(versions);

            if (_isReadOnly is null)
            {
                var chapter = await _api.TryGetChapterAsync(_projectId, _chapterNo);
                _isReadOnly = chapter is not null &&
                              string.Equals(chapter.Status, "superseded", StringComparison.OrdinalIgnoreCase);
            }
            ReadOnlyHint.Visibility = _isReadOnly == true ? Visibility.Visible : Visibility.Collapsed;

            RenderList();
            StatusText.Text = _versions.Count == 0 ? L.T("Versions.Empty") : "";
            if (_versions.Count == 0)
            {
                _selected = null;
                ShowPreview(null);
            }
            else
            {
                StatusText.Text = "";
                await SelectVersionAsync(_versions[0]);
            }
            UpdateButtons();
        }
        catch (Exception ex)
        {
            StatusText.Text = L.T("Versions.LoadFailed", ex.Message);
            _versions = new List<VersionDto>();
            RenderList();
            ShowPreview(null);
        }
        finally
        {
            _loading = false;
        }
    }

    private void RenderList(string? selectId = null)
    {
        _suppressSelection = true;
        try
        {
            VersionList.Items.Clear();
            foreach (var version in _versions) VersionList.Items.Add(BuildRow(version));
            var index = 0;
            if (selectId is not null)
            {
                for (var i = 0; i < _versions.Count; i++)
                {
                    if (_versions[i].Id != selectId) continue;
                    index = i;
                    break;
                }
            }
            if (_versions.Count > 0) VersionList.SelectedIndex = index;
        }
        finally
        {
            _suppressSelection = false;
        }
    }

    private ListBoxItem BuildRow(VersionDto version)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = FormatTime(version.Saved_At),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushOr("TextBrush", Brushes.Black),
        });

        var meta = new TextBlock
        {
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = BrushOr("MutedBrush", Brushes.Gray),
        };
        meta.Inlines.Add(new Run(SourceLabel(version.Source)));
        meta.Inlines.Add(new Run(" · " + L.T("Versions.WordCount", version.Word_Count)));
        if (!version.Format_Available)
        {
            meta.Inlines.Add(new Run(" · " + L.T("Versions.TextOnly"))
            {
                Foreground = BrushOr("DangerBrush", Brushes.IndianRed),
            });
        }
        panel.Children.Add(meta);

        return new ListBoxItem { Content = panel, Tag = version };
    }

    private void VersionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || _loading) return;
        var version = (VersionList.SelectedItem as ListBoxItem)?.Tag as VersionDto;
        _ = SelectVersionAsync(version);
    }

    internal async Task SelectVersionAsync(VersionDto? version)
    {
        var generation = ++_previewGeneration;
        if (version is null)
        {
            _selected = null;
            ShowPreview(null);
            UpdateButtons();
            return;
        }

        try
        {
            var detail = await _api.GetAsync(_projectId, _chapterNo, version.Id);
            if (generation != _previewGeneration) return; // 用户已经点了别的版本
            _selected = detail;
            ShowPreview(detail);
            StatusText.Text = "";
        }
        catch (Exception ex)
        {
            if (generation != _previewGeneration) return;
            _selected = null;
            ShowPreview(null);
            StatusText.Text = L.T("Versions.PreviewFailed", ex.Message);
        }
        UpdateButtons();
    }

    private void ShowPreview(VersionDetailDto? detail)
    {
        if (detail is null)
        {
            PreviewHeader.Text = L.T("Versions.PreviewTitle");
            PreviewBox.Document = new FlowDocument();
            HintText.Text = L.T("Versions.NoSelection");
            HintText.Visibility = Visibility.Visible;
            return;
        }

        PreviewHeader.Text = L.T("Versions.PreviewTitle") + " · " + FormatTime(detail.Saved_At) + " · " +
                             SourceLabel(detail.Source);
        var hasRich = detail.Format_Available && !string.IsNullOrWhiteSpace(detail.Body_Rich);
        PreviewBox.Document = RichBody.Build(detail.Body_Text, hasRich ? detail.Body_Rich : "");
        PreviewBox.IsReadOnly = true;

        if (!hasRich)
        {
            HintText.Text = detail.Format_Available
                ? L.T("Versions.PreviewEmpty")
                : L.T("Versions.FormatUnavailable");
            HintText.Visibility = Visibility.Visible;
        }
        else if (string.IsNullOrWhiteSpace(detail.Body_Text))
        {
            HintText.Text = L.T("Versions.PreviewEmpty");
            HintText.Visibility = Visibility.Visible;
        }
        else
        {
            HintText.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateButtons()
    {
        RestoreButton.IsEnabled = _selected is not null && _isReadOnly != true;
    }

    // ------------------------------------------------------------------ 恢复

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        if (_isReadOnly == true)
        {
            StatusText.Text = L.T("Versions.ReadOnly");
            return;
        }

        var confirm = L.T("Versions.RestoreConfirm");
        if (!_selected.Format_Available) confirm += "\n\n" + L.T("Versions.RestoreFormatWarn");
        if (MessageBox.Show(this, confirm, L.T("Versions.Restore"), MessageBoxButton.OKCancel,
                MessageBoxImage.Question) != MessageBoxResult.OK) return;

        RestoreButton.IsEnabled = false;
        StatusText.Text = L.T("Versions.Loading");
        try
        {
            await _api.RestoreAsync(_projectId, _chapterNo, _selected.Id);
            MessageBox.Show(this, L.T("Versions.RestoreDone"), "novel-gm",
                MessageBoxButton.OK, MessageBoxImage.Information);
            try { await _onRestored(_chapterNo); }
            catch (Exception ex)
            {
                MessageBox.Show(this, L.T("Versions.RestoredReloadFailed", ex.Message), "novel-gm",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            await LoadAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, L.T("Versions.RestoreFailed", ex.Message), "novel-gm",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            UpdateButtons();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ------------------------------------------------------------------ 显示辅助（测试可直接调）

    /// <summary>按保存时间倒序；时间相同再按 id 倒序，保证稳定。</summary>
    internal static List<VersionDto> SortVersions(IEnumerable<VersionDto>? versions) =>
        (versions ?? Enumerable.Empty<VersionDto>())
        .OrderByDescending(v => ParseTime(v.Saved_At))
        .ThenByDescending(v => v.Id, StringComparer.Ordinal)
        .ToList();

    internal static string FormatTime(string? savedAt)
    {
        if (string.IsNullOrWhiteSpace(savedAt)) return "";
        return DateTimeOffset.TryParse(savedAt, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal, out var time)
            ? time.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)
            : savedAt;
    }

    /// <summary>后端 source 值 → 三语文案；未知来源原样显示。</summary>
    internal static string SourceLabel(string? source)
    {
        var key = (source ?? "").Trim().ToLowerInvariant() switch
        {
            "original" => "Versions.Source.Original",
            "autosave" => "Versions.Source.Autosave",
            "ai" => "Versions.Source.Ai",
            "finalize" => "Versions.Source.Finalize",
            "restore-backup" => "Versions.Source.RestoreBackup",
            "manual" => "Versions.Source.Manual",
            "legacy-text" => "Versions.Source.LegacyText",
            _ => null,
        };
        if (key is not null) return L.T(key);
        return string.IsNullOrWhiteSpace(source) ? L.T("Versions.Source.Unknown") : source;
    }

    private static DateTimeOffset ParseTime(string? savedAt) =>
        DateTimeOffset.TryParse(savedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var time)
            ? time
            : DateTimeOffset.MinValue;

    private Brush BrushOr(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    private void UpdateHeader()
    {
        var header = L.T("Versions.WindowTitle", _projectTitle);
        Title = header;
        HeaderText.Text = header;
    }

    private void OnLanguageChanged()
    {
        UpdateHeader();
        RenderList(_selected?.Id);
        ShowPreview(_selected);
        UpdateButtons();
        if (_versions.Count == 0) StatusText.Text = L.T("Versions.Empty");
    }
}
