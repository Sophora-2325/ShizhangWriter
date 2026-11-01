using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Models;

namespace NovelGm.Desktop.Controls;

/// <summary>点击一条查找结果时发给外壳的定位请求；Offset 相对传给后端的纯文本。</summary>
public sealed record FindHit(int ChapterNo, int Offset, string Query, bool InDraft);

/// <summary>本章替换请求。面板不自己改正文，由外壳在 RichTextBox 上执行并复核。</summary>
public sealed record FindReplaceRequest(string Query, string Replacement, bool ReplaceAll,
    int ChapterNo = 0, string? SourceText = null, IReadOnlyList<int>? SelectedOffsets = null, bool CaseSensitive = false);

/// <summary>
/// 左栏「查找」面板：本章 / 全书范围，350ms 防抖异步搜索，结果按章分组，点击定位。
/// 契约见 docs/ROUND_NOTES_AI_PLAN.md §3.2。
/// </summary>
public partial class FindPanel : UserControl
{
    private readonly ISearchApi _api;
    private readonly DispatcherTimer _debounce;

    private string? _projectId;
    private string? _workTitle;
    private List<ChapterDto> _chapters = new();
    private int _currentChapterNo;
    private string _draft = "";
    private bool _bookScope;
    private bool _replaceEnabled = true;
    private bool _loading;
    private int _generation;
    private string _activeQuery = "";
    private SearchResponseDto? _lastResponse;
    private List<FindHit> _hits = new();
    private int _hitIndex = -1;
    private readonly HashSet<FindHit> _selectedHits = new();
    private readonly List<(CheckBox Box, FindHit Hit)> _hitChecks = new();
    private readonly List<(CheckBox Box, List<FindHit> Hits)> _chapterChecks = new();
    private string _resultDraft = "";

    private string? _statusKey;
    private object?[] _statusArgs = Array.Empty<object?>();
    private bool _statusError;

    public FindPanel() : this(new SearchClient(App.Api))
    {
    }

    internal FindPanel(ISearchApi api)
    {
        _api = api;
        InitializeComponent();
        _debounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(350),
        };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _ = RunSearchAsync();
        };
        LanguageManager.LanguageChanged += (_, _) => OnLanguageChanged();
        Unloaded += (_, _) => _debounce.Stop();
        UpdateScopeButtons();
        UpdateReplaceState();
        UpdateNavState();
        EmptyHint.Text = L.T("Find.EmptyQuery");
        EmptyHint.Visibility = Visibility.Collapsed;
    }

    public event EventHandler<FindHit>? LocateRequested;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<FindReplaceRequest>? ReplaceRequested;

    internal int DebounceMilliseconds
    {
        get => (int)_debounce.Interval.TotalMilliseconds;
        set => _debounce.Interval = TimeSpan.FromMilliseconds(Math.Max(1, value));
    }

    internal IReadOnlyList<FindHit> VisibleHitsForTest => _hits;
    internal string StatusTextForTest => StatusText.Text;
    internal bool StatusIsErrorForTest => _statusError;
    internal bool IsBookScopeForTest => _bookScope;
    internal IReadOnlyList<CheckBox> HitChecksForTest => _hitChecks.Select(x => x.Box).ToList();
    internal IReadOnlyList<CheckBox> ChapterChecksForTest => _chapterChecks.Select(x => x.Box).ToList();

    public void ShowMessage(string message) => SetStatus("Find.Message", true, message);

    // ------------------------------------------------------------------ 公开契约

    public void SetProject(string? projectId, string? workTitle)
    {
        var changed = !string.Equals(_projectId, projectId, StringComparison.Ordinal);
        _projectId = projectId;
        _workTitle = workTitle;
        if (!changed) return;
        _generation++;
        // 切作品后旧的编辑章草稿不能带到新作品上（否则本章/全书查找会拿错正文）
        _draft = "";
        _currentChapterNo = 0;
        ClearResults();
        ClearStatus();
        UpdateReplaceState();
    }

    /// <summary>当前编辑章未保存正文；切章时外壳在 ShowChapter 里喂一次。</summary>
    public void SetDraft(int chapterNo, string bodyText)
    {
        bodyText ??= "";
        // 同一章里改过字（含替换）也算变化：旧命中的偏移已经对不上
        var changed = chapterNo != _currentChapterNo || !string.Equals(_draft, bodyText, StringComparison.Ordinal);
        _currentChapterNo = chapterNo;
        _draft = bodyText;
        if (!changed) return;
        if (string.IsNullOrWhiteSpace(QueryBox.Text)) return;
        // 本章/全书搜索均以当前草稿覆盖该章：编辑或切章后旧结果失效并重搜。
        _generation++;
        ClearResults();
        _debounce.Stop();
        _debounce.Start();
    }

    public void SetChapters(IReadOnlyList<ChapterDto> chapters)
    {
        _chapters = chapters is null ? new List<ChapterDto>() : new List<ChapterDto>(chapters);
        if (_lastResponse is not null) RenderResults();
    }

    /// <summary>右侧入口打开时选范围；同时把焦点放到查找框。</summary>
    public void FocusScope(bool bookScope)
    {
        SetScope(bookScope);
        QueryBox.Focus();
        QueryBox.SelectAll();
    }

    public void Reset()
    {
        _generation++;
        _projectId = null;
        _workTitle = null;
        _chapters = new List<ChapterDto>();
        _draft = "";
        _currentChapterNo = 0;
        _loading = true;
        try
        {
            QueryBox.Text = "";
            ReplaceBox.Text = "";
            ReplaceRow.Visibility = Visibility.Collapsed;
            ToggleReplaceIcon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextBrush");
        }
        finally
        {
            _loading = false;
        }
        ClearResults();
        ClearStatus();
        UpdateReplaceState();
        UpdateNavState();
        EmptyHint.Text = L.T("Find.EmptyQuery");
        EmptyHint.Visibility = Visibility.Collapsed;
    }

    public void SetReplaceEnabled(bool enabled)
    {
        _replaceEnabled = enabled;
        UpdateReplaceState();
    }

    // ------------------------------------------------------------------ 范围 / 输入

    private void ScopeChapter_Click(object sender, RoutedEventArgs e) => SetScope(false);

    private void ScopeBook_Click(object sender, RoutedEventArgs e) => SetScope(true);

    private void SetScope(bool book)
    {
        var changed = _bookScope != book;
        _bookScope = book;
        UpdateScopeButtons();
        UpdateReplaceState();
        if (!changed) return;
        // 新范围：旧结果直接失效，重新搜
        _generation++;
        ClearResults();
        if (!string.IsNullOrWhiteSpace(QueryBox.Text)) _ = RunSearchAsync();
    }

    private void UpdateScopeButtons()
    {
        _loading = true;
        try
        {
            ScopeChapterButton.IsChecked = !_bookScope;
            ScopeBookButton.IsChecked = _bookScope;
        }
        finally
        {
            _loading = false;
        }
    }

    private void Query_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        ClearQueryButton.IsEnabled = !string.IsNullOrEmpty(QueryBox.Text);
        ClearResults();
        ClearStatus();
        UpdateReplaceState();
        _debounce.Stop();
        if (string.IsNullOrWhiteSpace(QueryBox.Text))
        {
            _generation++;
            ClearResults();
            SetStatus("Find.EmptyQuery", false);
            return;
        }
        // 查询一变就让在途的旧结果失效，不等防抖到点
        _generation++;
        _debounce.Start();
    }

    private void Query_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _debounce.Stop();
        _ = RunSearchAsync();
    }

    private void Case_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (string.IsNullOrWhiteSpace(QueryBox.Text)) return;
        _generation++;
        ClearResults();
        _debounce.Stop();
        _debounce.Start();
    }

    private void Replace_Changed(object sender, TextChangedEventArgs e) => UpdateReplaceState();

    private void ClearQuery_Click(object sender, RoutedEventArgs e)
    {
        QueryBox.Clear(); QueryBox.Focus();
    }

    private void ToggleReplace_Click(object sender, RoutedEventArgs e)
    {
        var visible = ReplaceRow.Visibility != Visibility.Visible;
        ReplaceRow.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ToggleReplaceIcon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, visible ? "AccentBrush" : "TextBrush");
        if (visible) ReplaceBox.Focus(); else QueryBox.Focus();
    }

    private void UpdateReplaceState()
    {
        ClearQueryButton.IsEnabled = !string.IsNullOrEmpty(QueryBox.Text);
        var canReplace = _replaceEnabled && !_bookScope && !string.IsNullOrWhiteSpace(QueryBox.Text);
        ReplaceBox.IsEnabled = _replaceEnabled && !_bookScope;
        ReplaceButton.IsEnabled = canReplace;
        ReplaceAllButton.IsEnabled = canReplace;
        ReplaceAllButton.ToolTip = _selectedHits.Count == 0 ? L.T("Find.ReplaceAllTip") : L.T("Find.ReplaceSelectedTip", _selectedHits.Count);
        System.Windows.Automation.AutomationProperties.SetName(ReplaceAllButton,
            _selectedHits.Count == 0 ? L.T("Find.ReplaceAll") : L.T("Find.ReplaceSelectedTip", _selectedHits.Count));
    }

    // ------------------------------------------------------------------ 搜索

    private async Task RunSearchAsync()
    {
        var query = QueryBox.Text;
        if (string.IsNullOrWhiteSpace(query))
        {
            _generation++;
            ClearResults();
            SetStatus("Find.EmptyQuery", false);
            return;
        }
        if (_projectId is null)
        {
            ClearResults();
            SetStatus("Find.NoProject", true);
            return;
        }
        if (!_bookScope && _currentChapterNo <= 0)
        {
            ClearResults();
            SetStatus("Find.NoChapter", true);
            return;
        }

        var project = _projectId;
        var generation = ++_generation;
        var caseSensitive = CaseBox.IsChecked == true;
        var request = new SearchRequestDto
        {
            Query = query,
            Case_Sensitive = caseSensitive,
            Scope = _bookScope ? "book" : "chapter",
            Chapter_No = _currentChapterNo,
            Draft_Text = _draft,
            Page = 1,
            Page_Size = 50,
        };

        SetStatus("Find.Searching", false);
        try
        {
            var response = await _api.SearchAsync(project, request);
            if (generation != _generation || project != _projectId) return; // 过期结果直接丢弃
            _lastResponse = response;
            _resultDraft = request.Draft_Text ?? "";
            _selectedHits.Clear();
            _activeQuery = query;
            _hits = new List<FindHit>();
            foreach (var chapter in response.Chapters ?? new List<SearchChapterDto>())
            {
                foreach (var match in chapter.Matches ?? new List<SearchHitDto>())
                    _hits.Add(new FindHit(chapter.Chapter_No, match.Offset, query, match.In_Draft));
            }
            _hitIndex = -1;
            RenderResults();
            UpdateNavState();
            if (_hits.Count == 0) ClearStatus();
            else RenderResultStatus();
        }
        catch (Exception ex)
        {
            if (generation != _generation || project != _projectId) return;
            ClearResults();
            SetStatus("Find.Failed", true, ex.Message);
        }
    }

    private void RenderResults()
    {
        ResultsPanel.Children.Clear();
        _hitChecks.Clear(); _chapterChecks.Clear();
        if (_lastResponse is null || _hits.Count == 0)
        {
            EmptyHint.Text = L.T("Find.NoResult");
            EmptyHint.Visibility = Visibility.Visible;
            return;
        }

        EmptyHint.Visibility = Visibility.Collapsed;
        foreach (var chapter in _lastResponse.Chapters ?? new List<SearchChapterDto>())
        {
            var matches = chapter.Matches ?? new List<SearchHitDto>();
            if (matches.Count == 0) continue;

            var chapterHits = matches.Select(m => new FindHit(chapter.Chapter_No, m.Offset, _activeQuery, m.In_Draft)).ToList();
            var header = new Grid { Margin = new Thickness(0, 8, 0, 4) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            var collapse = new Button { Style = (Style)FindResource("FindIcon"), Width = 24, Height = 28, Padding = new Thickness(2),
                ToolTip = L.T("Find.ToggleGroup") };
            System.Windows.Automation.AutomationProperties.SetName(collapse, L.T("Find.ToggleGroup"));
            var foldIcon = new System.Windows.Shapes.Path { Data = Geometry.Parse("M1,1 L13,1 L13,13 L1,13 Z M4,7 L10,7"),
                Width = 13, Height = 13, Stretch = Stretch.Uniform, StrokeThickness = 1.2 };
            foldIcon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextBrush");
            collapse.Content = foldIcon; header.Children.Add(collapse);
            var title = new TextBlock
            {
                Text = L.T("Find.ChapterHeader", DisplayChapter(chapter), chapter.Count),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = DisplayChapter(chapter),
                Foreground = BrushOr("TextBrush", Brushes.Black),
            };
            Grid.SetColumn(title, 1); header.Children.Add(title);
            var chapterCheck = new CheckBox { Style = (Style)FindResource("FindCheck"), IsThreeState = true, VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center, ToolTip = L.T("Find.SelectChapter") };
            System.Windows.Automation.AutomationProperties.SetName(chapterCheck, L.T("Find.SelectChapter"));
            chapterCheck.Click += (_, _) =>
            {
                var select = !chapterHits.All(_selectedHits.Contains);
                foreach (var hit in chapterHits) { if (select) _selectedHits.Add(hit); else _selectedHits.Remove(hit); }
                UpdateSelectionState();
            };
            _chapterChecks.Add((chapterCheck, chapterHits));
            Grid.SetColumn(chapterCheck, 2); header.Children.Add(chapterCheck);
            ResultsPanel.Children.Add(header);
            var rows = new List<Grid>();
            collapse.Click += (_, _) =>
            {
                var expand = rows.FirstOrDefault()?.Visibility != Visibility.Visible;
                foreach (var row in rows) row.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
                foldIcon.Data = Geometry.Parse(expand ? "M1,1 L13,1 L13,13 L1,13 Z M4,7 L10,7" : "M1,1 L13,1 L13,13 L1,13 Z M4,7 L10,7 M7,4 L7,10");
            };

            foreach (var match in matches)
            {
                var hit = new FindHit(chapter.Chapter_No, match.Offset, _activeQuery, match.In_Draft);
                var row = new Grid { Margin = new Thickness(24, 1, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
                var context = BuildContextText(match, _activeQuery);
                var button = new Button
                {
                    Content = context,
                    Style = (Style)FindResource("FindIcon"),
                    Width = double.NaN, Height = double.NaN,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(8, 4, 8, 4),
                    Margin = new Thickness(0, 1, 0, 2),
                    MinHeight = 24,
                    FontSize = 12,
                    Tag = hit,
                    ToolTip = new TextBlock { Text = match.Context, TextWrapping = TextWrapping.Wrap, MaxWidth = 480 },
                };
                System.Windows.Automation.AutomationProperties.SetName(button, L.T("Find.LocateTip") + ": " + match.Context);
                button.Click += Hit_Click;
                row.Children.Add(button);
                var check = new CheckBox { Style = (Style)FindResource("FindCheck"), Tag = hit, VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center, ToolTip = L.T("Find.SelectHit") };
                System.Windows.Automation.AutomationProperties.SetName(check, L.T("Find.SelectHit") + ": " + match.Context);
                check.Click += (_, _) =>
                {
                    if (check.IsChecked == true) _selectedHits.Add(hit); else _selectedHits.Remove(hit);
                    UpdateSelectionState();
                };
                _hitChecks.Add((check, hit));
                Grid.SetColumn(check, 1); row.Children.Add(check);
                ResultsPanel.Children.Add(row); rows.Add(row);
            }
        }
        UpdateSelectionState();
    }

    private void UpdateSelectionState()
    {
        foreach (var (box, hit) in _hitChecks) box.IsChecked = _selectedHits.Contains(hit);
        foreach (var (box, hits) in _chapterChecks)
        {
            var count = hits.Count(_selectedHits.Contains);
            box.IsChecked = count == 0 ? false : count == hits.Count ? true : null;
        }
        UpdateReplaceState();
        if (_lastResponse is not null && _hits.Count > 0) RenderResultStatus();
    }

    private TextBlock BuildContextText(SearchHitDto match, string query)
    {
        var block = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            LineHeight = 16, MaxHeight = 48,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Foreground = BrushOr("TextBrush", Brushes.Black),
        };
        void RenderSnippet()
        {
            var width = block.ActualWidth > 0 ? block.ActualWidth : 120;
            var snippet = SearchSnippet.Create(match.Context ?? "", match.Context_Offset, query.Length,
                Math.Max(4, (int)(width / 12) * 3 - 2));
            var context = snippet.Text;
            var offset = snippet.MatchOffset;
            block.Inlines.Clear();
            if (offset < 0 || snippet.MatchLength == 0) { block.Inlines.Add(new Run(context)); return; }
            if (offset > 0) block.Inlines.Add(new Run(context[..offset]));
            block.Inlines.Add(new Run(context.Substring(offset, snippet.MatchLength))
            {
                FontWeight = FontWeights.Bold,
                Foreground = BrushOr("AccentBrush", Brushes.Blue),
            });
            if (offset + snippet.MatchLength < context.Length)
                block.Inlines.Add(new Run(context[(offset + snippet.MatchLength)..]));
        }
        block.SizeChanged += (_, _) => RenderSnippet();
        RenderSnippet();
        return block;
    }

    private string DisplayChapter(SearchChapterDto chapter)
    {
        var title = _chapters.FirstOrDefault(c => c.No == chapter.Chapter_No)?.Title;
        if (string.IsNullOrWhiteSpace(title)) title = chapter.Title;
        return string.IsNullOrWhiteSpace(title)
            ? L.T("Find.ChapterOnly", chapter.Chapter_No)
            : title;
    }

    private void RenderResultStatus()
    {
        if (_lastResponse is null) return;
        if (_selectedHits.Count > 0)
        {
            SetStatus("Find.ResultsSelected", false, _lastResponse.Total, _selectedHits.Count);
            return;
        }
        if (_lastResponse.Truncated || _lastResponse.Total > _hits.Count)
            SetStatus("Find.ResultsTruncated", false, _lastResponse.Total, _hits.Count);
        else
            SetStatus("Find.Results", false, _lastResponse.Total, _lastResponse.Chapters?.Count ?? 0);
    }

    private void ClearResults()
    {
        _lastResponse = null;
        _hits = new List<FindHit>();
        _selectedHits.Clear(); _hitChecks.Clear(); _chapterChecks.Clear();
        _hitIndex = -1;
        ResultsPanel.Children.Clear();
        EmptyHint.Text = L.T("Find.EmptyQuery");
        EmptyHint.Visibility = Visibility.Collapsed;
        UpdateNavState();
        UpdateReplaceState();
    }

    // ------------------------------------------------------------------ 结果导航 / 替换

    private void Hit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: FindHit hit }) return;
        RaiseLocate(hit);
    }

    private void RaiseLocate(FindHit hit)
    {
        _hitIndex = _hits.FindIndex(h =>
            h.ChapterNo == hit.ChapterNo && h.Offset == hit.Offset && h.InDraft == hit.InDraft &&
            string.Equals(h.Query, hit.Query, StringComparison.Ordinal));
        if (_hitIndex < 0) _hitIndex = 0;
        LocateRequested?.Invoke(this, hit);
        SetStatus("Find.Position", false, _hitIndex + 1, _hits.Count);
    }

    private void Prev_Click(object sender, RoutedEventArgs e) => Move(-1);

    private void Next_Click(object sender, RoutedEventArgs e) => Move(1);

    private void Move(int delta)
    {
        if (_hits.Count == 0) return;
        _hitIndex = _hitIndex < 0
            ? (delta > 0 ? 0 : _hits.Count - 1)
            : ((_hitIndex + delta) % _hits.Count + _hits.Count) % _hits.Count;
        RaiseLocate(_hits[_hitIndex]);
    }

    private void Replace_Click(object sender, RoutedEventArgs e) => RequestReplace(replaceAll: false);

    private void ReplaceAll_Click(object sender, RoutedEventArgs e) => RequestReplace(replaceAll: true);

    private void RequestReplace(bool replaceAll)
    {
        if (string.IsNullOrWhiteSpace(QueryBox.Text)) return;
        if (_bookScope)
        {
            SetStatus("Find.BookReplaceDisabled", true);
            return;
        }
        if (!_replaceEnabled)
        {
            SetStatus("Find.ReadOnly", true);
            return;
        }
        var offsets = replaceAll && _selectedHits.Count > 0
            ? _selectedHits.Select(h => h.Offset).OrderBy(x => x).ToList() : null;
        ReplaceRequested?.Invoke(this, new FindReplaceRequest(QueryBox.Text, ReplaceBox.Text, replaceAll,
            _currentChapterNo, offsets is null ? _draft : _resultDraft, offsets, CaseBox.IsChecked == true));
    }

    private void UpdateNavState()
    {
        PrevButton.IsEnabled = _hits.Count > 0;
        NextButton.IsEnabled = _hits.Count > 0;
    }

    // ------------------------------------------------------------------ 状态行 / 语言

    private Brush BrushOr(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    private void SetStatus(string? key, bool error, params object?[] args)
    {
        var changed = key != _statusKey || error != _statusError;
        _statusKey = string.IsNullOrEmpty(key) || key == "Find.EmptyQuery" ? null : key;
        _statusArgs = args ?? Array.Empty<object?>();
        _statusError = error;
        RenderStatus();
        if (changed && _statusKey is not null)
        {
            var text = _statusArgs.Length == 0 ? L.T(_statusKey) : L.T(_statusKey, _statusArgs);
            StatusChanged?.Invoke(this, text);
        }
    }

    private void ClearStatus()
    {
        _statusKey = null;
        _statusArgs = Array.Empty<object?>();
        _statusError = false;
        RenderStatus();
    }

    private void RenderStatus()
    {
        StatusText.Text = _statusKey is null
            ? ""
            : (_statusArgs.Length == 0 ? L.T(_statusKey) : L.T(_statusKey, _statusArgs));
        StatusText.Visibility = string.IsNullOrWhiteSpace(StatusText.Text) ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Foreground = _statusError
            ? BrushOr("DangerBrush", Brushes.IndianRed)
            : BrushOr("TextBrush", Brushes.Black);
        StatusText.Opacity = _statusError ? 1 : 0.75;
    }

    private void OnLanguageChanged()
    {
        if (_lastResponse is not null) RenderResults();
        RenderStatus();
    }
}
