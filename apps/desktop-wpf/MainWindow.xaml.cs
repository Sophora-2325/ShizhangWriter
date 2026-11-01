using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Controls;
using NovelGm.Desktop.Models;

namespace NovelGm.Desktop;

public partial class MainWindow : Window
{
    private ProjectDto? _project;
    private List<ChapterDto> _chapters = new();
    private ChapterDto? _selected;
    private bool _busy;
    private bool _miscSaving;
    private ChapterStatsWindow? _statsWindow;
    private SensitiveScanState _sensitiveState = SensitiveScanState.NotRun;
    private bool _focusMode;
    private bool _focusLeftVisible;
    private bool _focusRightVisible;
    private bool _focusSideVisible;
    private double _focusLeftPx;
    private double _focusRightPx;
    private double _focusCenterPx;
    private GridLength _focusSideWidth = new(0);
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private Task<bool> _saveTask = Task.FromResult(true);
    private bool _loadingBody;
    private bool _bodyDirty;
    private bool _storingFigure;
    private bool _planDirty;
    private bool _reviewDirty;
    private bool _closeAfterSave;
    private bool _closingInProgress;
    private bool _suppressTreeSelection;
    private readonly ObservableCollection<WorkNode> _works = new();
    /// <summary>侧边对照栏宽度 = 正文列宽度 × 该值。</summary>
    private GridLength _sideWidth = new(0.7, GridUnitType.Star);
    // 目录区 / 设定区被折叠前记住的「像素宽度」。CaptureLayout 一律按像素归一化：
    // ActualWidth 恒为像素，被折叠的栏用这两个值。以前混用了 Column.Width.Value（拖过分隔条后是
    // 像素权重、没拖过是星值）和折叠时被塞进 Star 的像素数 —— 三个不同量纲相加会算出失真比例。
    private double _leftPx = SidebarOpenWidth;
    private double _rightPx = LoreOpenWidth;
    private const int MaxRecent = 8;
    /// <summary>最近打开的作品 id，最近的在前。</summary>
    private readonly List<string> _recentIds = new();
    private string? _sideProjectId;
    private int? _loreChapter;
    private bool _loreReadOnly;
    private ChapterSummaryWindow? _summaryWindow;

    /// <summary>创作区的四个区，顺序即流程顺序。审计区排在最后：它是只读的报告面板，不参与阶段判定。</summary>
    private enum Zone { Plan, Body, Review, Audit }

    private static readonly Zone[] Zones = { Zone.Plan, Zone.Body, Zone.Review, Zone.Audit };
    /// <summary>当前展示的区（含已弹出到子窗口的）。</summary>
    private readonly HashSet<Zone> _openZones = new();
    private readonly Dictionary<Zone, ZoneWindow> _popped = new();
    private readonly List<GridSplitter> _zoneSplitters = new();
    /// <summary>正文区弹出后子窗口里的 Word 栏。</summary>
    private FormatToolbar? _poppedBar;
    private readonly AudioRecorder _recorder = new();
    private readonly SttClient _stt = new();
    private bool _dictating;
    private bool _transcribing;
    /// <summary>上次按阶段自动展示时的（作品, 章节, 阶段）；不变时保留用户自己打开的区。</summary>
    private (string? ProjectId, int? No, Zone Stage)? _stageKey;

    /// <summary>已为该作品弹过「文件夹缺失」提示，避免每次重载重复弹窗。</summary>
    private string? _warnedMissingRootId;
    /// <summary>启动时要恢复的章节号（来自 layout.json）；打开后清空。</summary>
    private int? _startupChapterNo;

    public MainWindow()
    {
        InitializeComponent();
        ThemeManager.AppearanceChanged += OnAppearanceChanged;
        Closed += (_, _) => ThemeManager.AppearanceChanged -= OnAppearanceChanged;
        BodyEditor.SelectionChanged += (_, _) => { SyncToolsContext(); SyncFormatBars(); };
        InitializeReview();
        WorkTree.ItemsSource = _works;
        FormatBar.Target = BodyEditor;
        WireFormatBar(FormatBar, isPopped: false);
        BodyEditing.Attach(BodyEditor, () => EditorsEditable , () => _project?.Root_Path, () => _selected?.No, () => _poppedBar ?? FormatBar, () =>
        {
            _bodyDirty = true;
            RestartSaveTimer();
        });
        BodyEditing.FindRequested += () =>
        {
            ShowLeftTab(LeftTab.Find);
            SetSidebarVisible(true);
            FindPanelView.FocusScope(false);
        };
        BodyEditing.EnsureBodyVisible += () =>
        {
            if (_popped.ContainsKey(Zone.Body) || _openZones.Contains(Zone.Body)) return;
            ShowOnlyKeepAudit(Zone.Body);
        };
        _saveTimer.Tick += async (_, _) => await FlushEditsAsync();
        PlanZoneView.Changed += (_, _) =>
        {
            if (!EditorsEditable) return;
            _planDirty = true;
            RestartSaveTimer();
        };
        PlanZoneView.ProposalRequested += AiPlan_Requested;
        PlanZoneView.PersistAsync = async () =>
        {
            if (!EditorsEditable) return false;
            var projectId = _project!.Id;
            var chapter = _selected;
            _planDirty = true;
            var saved = await FlushEditsAsync();
            return saved && _project?.Id == projectId &&
                (ReferenceEquals(chapter, _selected) || (chapter is null && _blankChapter is { } blank &&
                    blank.ProjectId == projectId && ReferenceEquals(blank.Chapter, _selected)));
        };
        ReviewZoneView.Changed += (_, _) =>
        {
            if (!EditorsEditable) return;
            _reviewDirty = true;
            RestartSaveTimer();
        };
        ReviewZoneView.SubmitRequested += async (_, _) => await SubmitReviewAsync();
        LoreMiscView.SaveRequested += async (_, _) => await SaveMiscAsync();
        AuditZoneView.ReauditRequested += async (_, _) => await ReauditAsync();
        AuditZoneView.LocateRequested += (_, evidence) =>
        {
            if (!TrySelectInBody(evidence)) ReportError(L.T("Status.EvidenceNotFound"));
        };
        PlanZoneView.AddCharacterRequested += async (_, _) =>
        {
            await CreatePlanMemberAsync(true);
        };
        PlanZoneView.AddThreadRequested += async (_, _) =>
        {
            await CreatePlanMemberAsync(false);
        };
        HookPanels();
        SizeChanged += AlignLoreButtons;
        foreach (var z in Zones)
        {
            HostOf(z).SizeChanged += SyncZoneToggles;
            ToggleOf(z).SizeChanged += SyncZoneToggles;
        }
        var layout = LayoutStore.Load();
        ApplyLayout(layout);
        _recentIds.AddRange((layout.Recent_Project_Ids ?? new()).Distinct().Take(MaxRecent));
        if (_recentIds.Count == 0 && !string.IsNullOrEmpty(layout.Last_Project_Id)) _recentIds.Add(layout.Last_Project_Id);
        _startupChapterNo = layout.Last_Chapter_No;
        ClearChapterView();
        SetupShortcuts();
        ApplyCornerInsets();
        LanguageManager.LanguageChanged += (_, _) => RefreshTexts();
        ApplyWorkMode();
        InitializeUsage();
    }

    /// <summary>把 NotesPanel / FindPanel / AiPanel / ToolsPanel 的事件接到外壳现有流程。</summary>
    private void HookPanels()
    {
        AiPanelView.BodyRevisionRequested += async (_, _) => await OpenBodyRevisionAsync();
        AiPanelView.ShowStackedConstraintsRequested += async (_, _) => await OpenStackedConstraintsAsync();
        FindPanelView.LocateRequested += async (_, hit) => await LocateFindHitAsync(hit);
        FindPanelView.ReplaceRequested += async (_, request) => await ReplaceInChapterAsync(request);

        ToolsPanelView.ActionRequested += ToolsPanel_ActionRequested;

        AiPanelView.ShowPlanRequested += async (_, _) =>
        {
            if (await FlushNotesAsync()) RevealZone(Zone.Plan);
        };
        AiPanelView.AiPlanRequested += async (_, _) =>
        {
            if (await PrepareAiAsync()) await AiPlanCoreAsync();
        };
        AiPanelView.GenerateRequested += async (_, _) =>
        {
            if (await PrepareAiAsync()) await RunStep("Busy.Generating", GenerateCoreAsync);
        };
        AiPanelView.FinalizeRequested += async (_, _) =>
        {
            if (await PrepareAiAsync()) await RunStep("Busy.Finalizing", FinalizeCoreAsync);
        };
        AiPanelView.AuditRequested += async (_, _) =>
        {
            if (AuditZone.StoredReport(_selected, "consistency_report") is not null) RevealZone(Zone.Audit);
            else await RequestAuditAsync();
        };
        AiPanelView.PlanCheckRequested += async (_, _) =>
        {
            if (!await PrepareAiAsync()) return;
            if (AuditZone.StoredReport(_selected, "plan_check") is not null) RevealZone(Zone.Audit);
            else await RunStep("Busy.Checking", PlanCheckCoreAsync);
        };
        AiPanelView.ExtractRequested += async (_, _) =>
        {
            if (await PrepareAiAsync()) await RunStep("Busy.Extracting", ExtractCoreAsync);
        };
        AiPanelView.ShowReviewRequested += async (_, _) =>
        {
            if (await FlushNotesAsync()) RevealZone(Zone.Review);
        };

    }

    private void WireFormatBar(FormatToolbar bar, bool isPopped)
    {
        BindDictation(bar);
        bar.InsertImageRequested += (_, _) => InsertBodyImage();
        bar.CommentAddRequested += (_, _) => AddCommentFromToolbar();
        bar.HistoryRequested += async (_, _) => await OpenVersionsWindowAsync();
        bar.PublishRequested += async (_, _) => await OpenPublishAsync();
        bar.ParagraphSettingsRequested += (_, _) => BodyEditing.ShowSpacing();
        bar.WidthModeToggleRequested += (_, _) => ToggleBodyWidthMode();
        if (!isPopped) bar.FocusModeToggleRequested += (_, _) => ToggleFocusMode();
    }

    private void AddCommentFromToolbar()
    {
        if (!BodyEditing.CanAddComment) return;
        BodyEditing.AddComment();
        SyncFormatBars();
    }

    private void ToggleBodyWidthMode()
    {
        var next = PaperLayout.IsPaper(UiPrefs.Current.BodyWidthMode) ? PaperLayout.ModeFull : PaperLayout.ModePaper;
        ThemeManager.ApplyBodyWidthMode(next);
    }

    /// <summary>把章节、可编辑性和忙碌状态同步给主窗与弹出正文窗的两条工具栏。</summary>
    private void SyncFormatBars()
    {
        var state = new ToolbarState(
            HasWork: _project is not null,
            HasChapter: _selected is not null,
            Editable: EditorsEditable,
            CanComment: EditorsEditable && _selected is not null && !BodyEditor.Selection.IsEmpty,
            Busy: _busy);
        var paper = PaperLayout.IsPaper(UiPrefs.Current.BodyWidthMode);
        FormatBar.SetState(state);
        FormatBar.SetFocusMode(_focusMode);
        FormatBar.SetWidthMode(paper);
        FormatBar.Sync();
        if (_poppedBar is { } popped)
        {
            popped.SetState(state);
            popped.SetWidthMode(paper);
            popped.Sync();
        }
    }

    private void ToggleFocusMode()
    {
        FormatBar.CloseOverlays();
        _poppedBar?.CloseOverlays();
        if (_focusMode) ExitFocusMode();
        else EnterFocusMode();
    }

    /// <summary>临时隐藏左右辅助栏：不改纯净/辅助模式、不关子窗、不写布局偏好。</summary>
    private void EnterFocusMode()
    {
        if (_focusMode) return;
        _focusLeftVisible = LeftPane.Visibility == Visibility.Visible;
        _focusRightVisible = RightPane.Visibility == Visibility.Visible;
        _focusSideVisible = SidePane.Visibility == Visibility.Visible;
        _focusLeftPx = _focusLeftVisible ? LeftColumn.ActualWidth : _leftPx;
        _focusRightPx = _focusRightVisible ? RightColumn.ActualWidth : _rightPx;
        _focusCenterPx = CenterColumn.ActualWidth;
        _focusSideWidth = SideColumn.Width;
        _focusMode = true;

        LeftRail.Visibility = Visibility.Collapsed;
        LeftPane.Visibility = Visibility.Collapsed;
        LeftSplitter.Visibility = Visibility.Collapsed;
        OptionRail.Visibility = Visibility.Collapsed;
        RightPane.Visibility = Visibility.Collapsed;
        RightSplitter.Visibility = Visibility.Collapsed;
        SidePane.Visibility = Visibility.Collapsed;
        SideSplitter.Visibility = Visibility.Collapsed;

        LeftRailColumn.Width = new GridLength(0);
        LeftColumn.MinWidth = 0;
        LeftColumn.Width = new GridLength(0);
        LeftSplitterColumn.Width = new GridLength(0);
        RightColumn.MinWidth = 0;
        RightColumn.Width = new GridLength(0);
        RightSplitterColumn.Width = new GridLength(0);
        RailColumn.Width = new GridLength(0);
        SideColumn.Width = new GridLength(0);
        SideSplitterColumn.Width = new GridLength(0);

        FormatBar.SetFocusMode(true);
        FocusModeItem.IsChecked = true;
        BodyEditor.Focus();
    }

    private void ExitFocusMode()
    {
        if (!_focusMode) return;
        _focusMode = false;

        LeftRailColumn.Width = new GridLength(56);
        LeftRail.Visibility = Visibility.Visible;
        OptionRail.Visibility = Visibility.Visible;
        RailColumn.Width = GridLength.Auto;

        LeftColumn.MinWidth = _focusLeftVisible ? SidebarMinWidth : 0;
        LeftColumn.Width = _focusLeftVisible
            ? new GridLength(Math.Max(SidebarMinWidth, _focusLeftPx))
            : new GridLength(0);
        LeftPane.Visibility = _focusLeftVisible ? Visibility.Visible : Visibility.Collapsed;
        LeftSplitterColumn.Width = new GridLength(_focusLeftVisible ? 5 : 0);
        LeftSplitter.Visibility = _focusLeftVisible ? Visibility.Visible : Visibility.Collapsed;

        RightColumn.MinWidth = _focusRightVisible ? LoreMinWidth : 0;
        RightColumn.Width = _focusRightVisible
            ? new GridLength(Math.Max(LoreMinWidth, _focusRightPx))
            : new GridLength(0);
        RightPane.Visibility = _focusRightVisible ? Visibility.Visible : Visibility.Collapsed;
        RightSplitterColumn.Width = new GridLength(_focusRightVisible ? 5 : 0);
        RightSplitter.Visibility = _focusRightVisible ? Visibility.Visible : Visibility.Collapsed;

        SideColumn.Width = _focusSideWidth;
        SidePane.Visibility = _focusSideVisible ? Visibility.Visible : Visibility.Collapsed;
        SideSplitterColumn.Width = new GridLength(_focusSideVisible ? 5 : 0);
        SideSplitter.Visibility = _focusSideVisible ? Visibility.Visible : Visibility.Collapsed;

        FormatBar.SetFocusMode(false);
        FocusModeItem.IsChecked = false;
        SyncRail();
        BodyEditor.Focus();
    }



    private bool IsPureMode => UiPrefs.IsPureMode;

    private void ModePure_Click(object sender, RoutedEventArgs e) => SetWorkMode(UiPrefs.ModePure);

    private void ModeAssist_Click(object sender, RoutedEventArgs e) => SetWorkMode(UiPrefs.ModeAssist);

    private void SetWorkMode(string mode)
    {
        if (string.Equals(UiPrefs.Current.WorkMode, mode, StringComparison.OrdinalIgnoreCase))
        {
            SyncModeMenuChecks();
            return;
        }
        UiPrefs.Current.WorkMode = mode;
        UiPrefs.Save();
        ApplyWorkMode();
    }

    private void SyncModeMenuChecks()
    {
        var mark = MenuMarks.Layout(IsPureMode);
        ModePureItem.IsChecked = mark.First;
        ModeAssistItem.IsChecked = mark.Second;
        WorkLayoutMenu.InputGestureText = L.T(mark.LabelKey);
    }

    /// <summary>纯净：藏区名开关与底部步骤条，固定正文区；右栏（含 AI全流程面板）不再强制收起。
    /// AI全流程：按阶段展示创作区。打开/关闭右栏 AI 面板不影响工作模式。</summary>
    private void ApplyWorkMode()
    {
        var pure = IsPureMode;
        SyncModeMenuChecks();
        ZoneToggleBar.Visibility = pure ? Visibility.Collapsed : Visibility.Visible;

        if (pure)
        {
            ShowOnly(Zone.Body);
        }
        else
        {
            _stageKey = null;
            ApplyStage();
        }
        AlignLoreButtons(null, EventArgs.Empty);
    }

    private void ReadingMode_Click(object sender, RoutedEventArgs e) => ToggleRightTab(RightTab.Reader);
    private void SyncReadingChrome()
    {
        ReadingModeItem.IsEnabled = _selected is not null;
        ReadingModeItem.IsChecked = ReviewVisible;
        ReaderView.SetChapters(_chapters);
        if (ReviewVisible && (_previewFollowsEditor || ReaderView.ProjectId != _project?.Id)) ShowCurrentReview(false);
    }

    /// <summary>切换语言后，重新生成由代码设置的文字（XAML 里的文字由 DynamicResource 自动刷新）。</summary>
    private void RefreshTexts()
    {
        Window_StateChanged(null, EventArgs.Empty);
        foreach (var w in _works) w.RefreshTexts();
        if (_works.FirstOrDefault(w => w.Project.Id == _project?.Id) is { } current)
            TitleProjectText.Text = $"{L.T("App.Title")} · {current.Title}";
        else TitleProjectText.Text = L.T("App.Title");
        UpdateSummaryText();
        UpdateBodyInfo();
        RenderLore();
        LayoutZones();
        UpdateZoneTitles();
        FormatBar.RefreshTexts();
        _poppedBar?.RefreshTexts();
        SyncReadingChrome();
        UpdateStepButtons();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        StartUsage();
        UpdatePaperWidth();
        await Safe(RefreshProjectsAsync);
        await PromptMigrationsAsync();
        await OpenStartupWorkAsync();
    }

    /// <summary>按当前字号与「稿纸/铺满」偏好重算正文列宽度；只动呈现，不改正文数据。</summary>
    private void UpdatePaperWidth()
    {
        var paper = PaperLayout.IsPaper(UiPrefs.Current.BodyWidthMode);
        ReadingPaper.MaxWidth = PaperLayout.Width(BodyEditor.FontSize, paper);
        var mark = MenuMarks.Width(paper);
        PaperPaperItem.IsChecked = mark.First;
        PaperFullItem.IsChecked = mark.Second;
        BodyWidthMenu.InputGestureText = L.T(mark.LabelKey);
        FormatBar.SetWidthMode(paper);
        _poppedBar?.SetWidthMode(paper);
    }

    private void PaperPaper_Click(object sender, RoutedEventArgs e) => ThemeManager.ApplyBodyWidthMode(PaperLayout.ModePaper);

    private void PaperFull_Click(object sender, RoutedEventArgs e) => ThemeManager.ApplyBodyWidthMode(PaperLayout.ModeFull);

    private void FocusMode_Click(object sender, RoutedEventArgs e)
    {
        ToggleFocusMode();
        FocusModeItem.IsChecked = _focusMode;
    }

    private void OnAppearanceChanged(object? sender, EventArgs e) => UpdatePaperWidth();

    /// <summary>启动时打开上次的作品与章节（找不到作品就打开第一部），并固定展示正文区。</summary>
    private async Task OpenStartupWorkAsync()
    {
        if (_project is not null) return;
        var last = _recentIds.FirstOrDefault();
        var work = _works.FirstOrDefault(w => w.Project.Id == last) ?? _works.FirstOrDefault();
        if (work is null) return;
        var chapterNo = _startupChapterNo;
        _startupChapterNo = null;
        await Safe(async () =>
        {
            await LoadProjectAsync(work.Project.Id, chapterNo, expand: true);
            ShowOnly(Zone.Body);
        });
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        var maximized = WindowState == WindowState.Maximized;
        // WindowChrome windows overhang the screen by the resize frame when maximized.
        var t = SystemParameters.WindowResizeBorderThickness;
        RootBorder.Padding = maximized
            ? new Thickness(t.Left + 4, t.Top + 4, t.Right + 4, t.Bottom + 4)
            : new Thickness(0);
        RootBorder.BorderThickness = new Thickness(maximized ? 0 : 1);
        MaxIcon.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        RestoreIcon.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
        MaxRestoreButton.ToolTip = L.T(maximized ? "Main.Restore" : "Main.Maximize");
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void MaxRestore_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(_project) { Owner = this };
        dlg.ShowDialog();
        if (_project is null) return;
        // Safe() skips the reload while another request is running, so keep the local copy in sync.
        if (dlg.UpdatedProject is { } updated && updated.Id == _project.Id)
        {
            _project.Provider = updated.Provider;
            _project.Offline_Mode = updated.Offline_Mode;
        }
        await Safe(() => LoadProjectAsync(_project.Id));
    }

    private async Task RefreshProjectsAsync()
    {
        var list = await App.Api.ListProjectsAsync();
        foreach (var p in list)
        {
            var node = _works.FirstOrDefault(w => w.Project.Id == p.Id);
            if (node is null) _works.Add(new WorkNode(p));
            else node.Project = p;
        }
        foreach (var stale in _works.Where(w => list.All(p => p.Id != w.Project.Id)).ToList())
            _works.Remove(stale);
        EmptyWorksText.Visibility = _works.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>旧版作品只存在集中存储里，启动时逐个询问是否迁移到作品文件夹。</summary>
    private async Task PromptMigrationsAsync()
    {
        var legacy = _works.Where(w => string.IsNullOrEmpty(w.Project.Root_Path)).Select(w => w.Project).ToList();
        if (legacy.Count == 0) return;
        var migrated = false;
        foreach (var p in legacy)
        {
            var answer = MessageBox.Show(this,
                L.T("Migrate.Prompt", p.Title),
                L.T("Migrate.Title"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) break;
            if (answer != MessageBoxResult.Yes) continue;
            var parent = FolderPicker.Pick(L.T("Migrate.PickFolder", p.Title), this);
            if (string.IsNullOrEmpty(parent)) continue;
            try
            {
                await App.Api.MigrateProjectAsync(p.Id, parent);
                migrated = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, L.T("Migrate.Failed", p.Title, ex.Message), L.T("App.Title"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        if (!migrated) return;
        await Safe(async () =>
        {
            await RefreshProjectsAsync();
            if (_project is not null) await LoadProjectAsync(_project.Id);
        });
    }

    /// <param name="selectNo">要选中的章节号；为 null 时保留当前选中章，否则选最后一章。</param>
    /// <param name="expand">强制展开该作品节点。</param>
    private async Task LoadProjectAsync(string id, int? selectNo = null, bool expand = false)
    {
        var discardNotesOnSwitch = false;
        // 切作品前先把笔记草稿落盘；保存失败时让作者决定是否继续，避免静默丢失
        if (_project is not null && _project.Id != id)
        {
            if (!await ConfirmMiscDraftAsync()) return;
            await NotesPanelView.FlushAsync();
            if (NotesPanelView.HasUnsavedDraft
                && MessageBox.Show(this, L.T("Notes.UnsavedSwitch"), L.T("App.Title"),
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            discardNotesOnSwitch = NotesPanelView.HasUnsavedDraft;
        }
        var keepNo = selectNo ?? (_project?.Id == id ? _selected?.No : null);
        _project = await App.Api.GetProjectAsync(id);
        _chapters = await App.Api.ListChaptersAsync(id);
        if (discardNotesOnSwitch) NotesPanelView.DiscardDraft();

        var work = _works.FirstOrDefault(w => w.Project.Id == id);
        if (work is null)
        {
            work = new WorkNode(_project);
            _works.Add(work);
            EmptyWorksText.Visibility = Visibility.Collapsed;
        }
        else work.Project = _project;
        TitleProjectText.Text = $"{L.T("App.Title")} · {work.Title}";
        NotesPanelView.SetProject(id, _chapters, null);
        FindPanelView.SetProject(id, work.Title);
        FindPanelView.SetChapters(_chapters);
        _recentIds.Remove(id);
        _recentIds.Insert(0, id);
        if (_recentIds.Count > MaxRecent) _recentIds.RemoveRange(MaxRecent, _recentIds.Count - MaxRecent);

        _suppressTreeSelection = true;
        try
        {
            foreach (var w in _works)
            {
                w.IsCurrent = ReferenceEquals(w, work);
                foreach (var c in w.Chapters) c.IsSelected = false;
            }
            work.SetChapters(_chapters);
            if (expand || selectNo is not null) work.IsExpanded = true;

            var target = (keepNo is int n ? work.Chapters.FirstOrDefault(c => c.Chapter.No == n) : null)
                         ?? work.Chapters.LastOrDefault();
            if (target is not null)
            {
                target.IsSelected = true;
                ShowChapter(target.Chapter);
            }
            else
            {
                ClearChapterView();
            }
        }
        finally
        {
            _suppressTreeSelection = false;
        }

        if (_project.Root_Missing)
        {
            if (_warnedMissingRootId != _project.Id)
            {
                _warnedMissingRootId = _project.Id;
                ReportError(L.T("Main.RootMissing", _project.Root_Path));
            }
        }
        else _warnedMissingRootId = null;
        await NotifyStackedWeightMigrationAsync();
    }

    private void ResetProjectView()
    {
        ClearChapterView();
        _project = null;
        _chapters = new List<ChapterDto>();
        TitleProjectText.Text = L.T("App.Title");
        NotesPanelView.SetProject(null, new List<ChapterDto>(), null);
        FindPanelView.Reset();
        RenderLore();
        _stageKey = null;
        // ClearChapterView 刷新审阅栏时 _project 还在，审阅栏会继续显示已关闭作品的章节
        _previewRequest++;
        ReaderView.Clear();
    }

    private async void WorkHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (e.ClickCount > 1) return;
        if ((sender as FrameworkElement)?.DataContext is not WorkNode node) return;
        node.IsExpanded = !node.IsExpanded;
        if (_project?.Id != node.Project.Id || !node.Loaded)
            await Safe(() => LoadProjectAsync(node.Project.Id));
    }

    private async void WorkTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_suppressTreeSelection || e.NewValue is not ChapterNode node) return;
        if (_project?.Id == node.Work.Project.Id)
        {
            ShowChapter(node.Chapter);
            return;
        }
        var no = node.Chapter.No;
        await Safe(() => LoadProjectAsync(node.Work.Project.Id, no));
    }

    /// <summary>左栏出场默认宽度（= MinWidth），之后可拖宽或关闭。</summary>
    /// <summary>新配置的目录区默认宽度；已有布局按 layout.json 的原宽度恢复。</summary>
    private const double SidebarOpenWidth = 240;
    /// <summary>目录区最小宽度，沿用 0.3.11 的下限，避免把老用户保存的窄栏顶宽。</summary>
    private const double SidebarMinWidth = 200;
    /// <summary>右栏出场默认宽度。</summary>
    private const double LoreOpenWidth = 300;
    /// <summary>右栏最小宽度：窗口变窄时允许收到 240，但不再挤占创作区。</summary>
    private const double LoreMinWidth = 240;

    private void SetSidebarVisible(bool visible)
    {
        if (visible && _focusMode) ExitFocusMode();
        if (visible)
        {
            LeftColumn.MinWidth = SidebarMinWidth;
            // 已经开着就保留用户拖出来的宽度；重新展开时回到收起前的宽度
            if (!(LeftPaneVisible && LeftColumn.Width.IsAbsolute && LeftColumn.Width.Value > 0))
                LeftColumn.Width = new GridLength(Math.Max(SidebarMinWidth, _leftPx));
            LeftSplitterColumn.Width = new GridLength(5);
        }
        else
        {
            if (LeftColumn.ActualWidth > 0) _leftPx = LeftColumn.ActualWidth;
            LeftColumn.MinWidth = 0;
            LeftColumn.Width = new GridLength(0);
            LeftSplitterColumn.Width = new GridLength(0);
            if (_leftTab == LeftTab.Notes) _ = NotesPanelView.FlushAsync();
        }
        var v = visible ? Visibility.Visible : Visibility.Collapsed;
        LeftPane.Visibility = v;
        LeftSplitter.Visibility = v;
        SyncLeftRail();
    }

    private enum LeftTab { Tree, Notes, Find }

    private LeftTab _leftTab = LeftTab.Tree;

    private bool LeftPaneVisible => LeftPane.Visibility == Visibility.Visible;

    private void RailLibrary_Click(object sender, RoutedEventArgs e) => ToggleLeftTab(LeftTab.Tree);

    private void RailNotes_Click(object sender, RoutedEventArgs e) => ToggleLeftTab(LeftTab.Notes);

    private void RailFind_Click(object sender, RoutedEventArgs e) => ToggleLeftTab(LeftTab.Find);

    /// <summary>左选项列：点未激活的项切过去并打开左面板；点当前项收起左面板（选项列常驻）。</summary>
    private void ToggleLeftTab(LeftTab tab)
    {
        if (LeftPaneVisible && _leftTab == tab)
        {
            SetSidebarVisible(false);
            return;
        }
        ShowLeftTab(tab);
        SetSidebarVisible(true);
    }

    /// <summary>只切换左面板内容，不改变左栏的显示/隐藏（布局恢复时用）。</summary>
    private void ShowLeftTab(LeftTab tab)
    {
        if (_leftTab == LeftTab.Notes && tab != LeftTab.Notes) _ = NotesPanelView.FlushAsync();
        _leftTab = tab;
        WorkTreeHost.Visibility = tab == LeftTab.Tree ? Visibility.Visible : Visibility.Collapsed;
        NotesPanelView.Visibility = tab == LeftTab.Notes ? Visibility.Visible : Visibility.Collapsed;
        FindPanelView.Visibility = tab == LeftTab.Find ? Visibility.Visible : Visibility.Collapsed;
        if (tab == LeftTab.Find && _selected is not null)
            FindPanelView.SetDraft(_selected.No, RichBody.PlainText(BodyEditor.Document));
        SyncLeftRail();
    }

    private void SyncLeftRail()
    {
        var open = LeftPaneVisible;
        RailLibrary.Tag = open && _leftTab == LeftTab.Tree ? "active" : null;
        RailNotes.Tag = open && _leftTab == LeftTab.Notes ? "active" : null;
        RailFind.Tag = open && _leftTab == LeftTab.Find ? "active" : null;
    }

    private enum RightTab { Lore, Tools, Ai, Reader }

    private RightTab _rightTab = RightTab.Lore;

    private void RailLore_Click(object sender, RoutedEventArgs e) => ToggleRightTab(RightTab.Lore);

    private void RailReader_Click(object sender, RoutedEventArgs e) => ToggleRightTab(RightTab.Reader);

    private void RailTools_Click(object sender, RoutedEventArgs e) => ToggleRightTab(RightTab.Tools);

    private void RailAi_Click(object sender, RoutedEventArgs e) => ToggleRightTab(RightTab.Ai);

    /// <summary>选项列：点未激活的项切过去并打开右栏；点当前项收起右栏。</summary>
    private void ToggleRightTab(RightTab tab)
    {
        if (RightPane.Visibility == Visibility.Visible && _rightTab == tab)
        {
            SetLorePaneVisible(false);
            return;
        }
        ShowRightTab(tab);
        SetLorePaneVisible(true);
    }

    private void ShowRightTab(RightTab tab)
    {
        if (_rightTab == RightTab.Reader && tab != RightTab.Reader) { _previewTimer.Stop(); _previewRequest++; ReaderView.Pause(); ReaderView.SetNavigating(false); }
        _rightTab = tab;
        LorePanel.Visibility = tab == RightTab.Lore ? Visibility.Visible : Visibility.Collapsed;
        ToolsPanelView.Visibility = tab == RightTab.Tools ? Visibility.Visible : Visibility.Collapsed;
        AiPanelView.Visibility = tab == RightTab.Ai ? Visibility.Visible : Visibility.Collapsed;
        ReaderView.Visibility = tab == RightTab.Reader ? Visibility.Visible : Visibility.Collapsed;
        if (tab == RightTab.Tools) ToolsPanelView.Refresh();
        SyncToolsContext();
        SyncRail();
    }

    private void SyncRail()
    {
        var open = RightPane.Visibility == Visibility.Visible;
        RailLore.Tag = open && _rightTab == RightTab.Lore ? "active" : null;
        RailTools.Tag = open && _rightTab == RightTab.Tools ? "active" : null;
        RailAi.Tag = open && _rightTab == RightTab.Ai ? "active" : null;
        RailReader.Tag = open && _rightTab == RightTab.Reader ? "active" : null;
        ReadingModeItem.IsChecked = ReviewVisible;
    }

    private void SetLorePaneVisible(bool visible)
    {
        if (visible && _focusMode) ExitFocusMode();
        if (visible)
        {
            // MinWidth 与出场宽度分开：窄窗口下仍允许拖到 240，但出厂保持 300。
            RightColumn.MinWidth = LoreMinWidth;
            if (!(RightPane.Visibility == Visibility.Visible && RightColumn.Width.IsAbsolute && RightColumn.Width.Value > 0))
                RightColumn.Width = new GridLength(Math.Max(LoreMinWidth, _rightPx));
            RightSplitterColumn.Width = new GridLength(5);
        }
        else
        {
            if (RightColumn.ActualWidth > 0) _rightPx = RightColumn.ActualWidth;
            RightColumn.MinWidth = 0;
            RightColumn.Width = new GridLength(0);
            RightSplitterColumn.Width = new GridLength(0);
        }
        var v = visible ? Visibility.Visible : Visibility.Collapsed;
        RightPane.Visibility = v;
        if (!visible) { _previewTimer.Stop(); _previewRequest++; ReaderView.Pause(); ReaderView.SetNavigating(false); }
        else if (_rightTab == RightTab.Reader) ShowCurrentReview(false);
        RightSplitter.Visibility = v;
        SyncRail();
    }

    // ---------------------------------------------------------------- pane proportions

    private void ApplyLayout(PaneLayout layout)
    {
        // 纯净/AI全流程共用保存的列宽：纯净模式已不再强制折叠右栏，读到的宽度就是用户想要的布局。
        var src = layout;
        // 左右栏按像素显示：把保存的比例换回像素，作为两栏展开时的宽度（中间列吃掉剩余空间）
        var defaults = new PaneLayout();
        var total = Math.Max(800, (WindowState == WindowState.Maximized ? SystemParameters.WorkArea.Width : Width) - 120);
        var sum = Math.Max(src.Left + src.Center + src.Right, 0.001);
        _leftPx = src.Left == defaults.Left ? SidebarOpenWidth : Math.Clamp(src.Left / sum * total, SidebarMinWidth, 600);
        _rightPx = src.Right == defaults.Right ? LoreOpenWidth : Math.Clamp(src.Right / sum * total, LoreMinWidth, 720);
        CenterColumn.Width = new GridLength(1, GridUnitType.Star);
        _sideWidth = new GridLength(src.Side, GridUnitType.Star);
        // 让 Set…Visible(true) 按 _leftPx/_rightPx 重新设宽
        LeftColumn.Width = new GridLength(0);
        RightColumn.Width = new GridLength(0);
        SetSidebarVisible(layout.Sidebar_Visible);
        ShowLeftTab(layout.Left_Tab switch
        {
            LayoutStore.LeftTabNotes => LeftTab.Notes,
            LayoutStore.LeftTabFind => LeftTab.Find,
            _ => LeftTab.Tree,
        });
        ShowRightTab(layout.Right_Tab switch
        {
            LayoutStore.RightTabReader => RightTab.Reader,
            LayoutStore.RightTabTools => RightTab.Tools,
            LayoutStore.RightTabAi => RightTab.Ai,
            _ => RightTab.Lore,
        });
        SetLorePaneVisible(layout.Lore_Visible);
    }

    /// <summary>把三栏宽度换算成相对比例保存。一律按**像素**归一化：ActualWidth 恒为像素，
    /// 被折叠的栏用它折叠前记住的像素宽度（`_leftPx` / `_rightPx`）。</summary>
    private PaneLayout CaptureLayout()
    {
        var sidebarOpen = _focusMode ? _focusLeftVisible : LeftPane.Visibility == Visibility.Visible;
        var loreOpen = _focusMode ? _focusRightVisible : RightPane.Visibility == Visibility.Visible;
        var left = _focusMode
            ? (_focusLeftVisible ? _focusLeftPx : _leftPx)
            : (sidebarOpen ? LeftColumn.ActualWidth : _leftPx);
        var center = _focusMode ? _focusCenterPx : CenterColumn.ActualWidth;
        var right = _focusMode
            ? (_focusRightVisible ? _focusRightPx : _rightPx)
            : (loreOpen ? RightColumn.ActualWidth : _rightPx);
        var total = Math.Max(left + center + right, 1);
        var side = _focusMode
            ? (_focusSideVisible ? _focusSideWidth.Value : _sideWidth.Value)
            : (SidePane.Visibility == Visibility.Visible
                ? SideColumn.Width.Value / Math.Max(MainColumn.Width.Value, 0.001)
                : _sideWidth.Value);
        return new PaneLayout
        {
            Left = ClampRatio(left / total * 5),
            Center = ClampRatio(center / total * 5),
            Right = ClampRatio(right / total * 5),
            Side = Math.Round(Math.Clamp(side, 0.15, 2.0), 3),
            Sidebar_Visible = sidebarOpen,
            Lore_Visible = loreOpen,
            Left_Tab = _leftTab switch
            {
                LeftTab.Notes => LayoutStore.LeftTabNotes,
                LeftTab.Find => LayoutStore.LeftTabFind,
                _ => LayoutStore.LeftTabTree,
            },
            Right_Tab = _rightTab switch
            {
                RightTab.Reader => LayoutStore.RightTabReader,
                RightTab.Tools => LayoutStore.RightTabTools,
                RightTab.Ai => LayoutStore.RightTabAi,
                _ => LayoutStore.RightTabLore,
            },
            Last_Project_Id = _recentIds.FirstOrDefault(),
            Last_Chapter_No = _selected?.No,
            Recent_Project_Ids = _recentIds.ToList(),
        };
    }

    /// <summary>单栏比例的下限/上限：挡住「三栏之和正常、但某一栏被算成 0.0x」这种不可读结果。</summary>
    private static double ClampRatio(double value) => Math.Round(Math.Clamp(value, 0.25, 4.5), 3);

    private void ShowChapter(ChapterDto ch)
    {
        LoadEditors(ch);
        UpdateSummaryText();
        UpdateConsistencyFlags();
        AuditZoneView.Render(ch);
        RenderLore();
        ApplyStage();
        UpdateZoneTitles();
        SyncReadingChrome();
        NotesPanelView.SetCurrentChapter(ch.No);
        FindPanelView.SetDraft(ch.No, RichBody.PlainText(BodyEditor.Document));
        UpdateStepButtons();
        _statsWindow?.Close();
        SetSensitiveState(SensitiveScanState.NotRun);
        FormatBar.CloseOverlays();
        _poppedBar?.CloseOverlays();
        SyncToolsContext();
    }

    private void UpdateSummaryText()
    {
        if (_summaryWindow is not { } window) return;
        if (_project?.Id != window.ProjectId) { window.Close(); return; }
        if (_selected?.No == window.ChapterNo) window.ShowChapter(_selected);
    }

    private ChapterSummaryWindow ShowSummaryWindow(string projectId, ChapterDto chapter, string? status = null)
    {
        if (_summaryWindow is { } old && (old.ProjectId != projectId || old.ChapterNo != chapter.No)) old.Close();
        if (_summaryWindow is null)
        {
            var created = new ChapterSummaryWindow(projectId, chapter) { Owner = this };
            created.Closed += (_, _) => { if (_summaryWindow == created) _summaryWindow = null; };
            _summaryWindow = created;
            created.Show();
        }
        _summaryWindow.ShowChapter(chapter, status);
        if (_summaryWindow.WindowState == WindowState.Minimized) _summaryWindow.WindowState = WindowState.Normal;
        _summaryWindow.Activate();
        return _summaryWindow;
    }

    private void UpdateConsistencyFlags()
    {
        ConsistencyFlagsPanel.Children.Clear();
        var flags = _selected?.Consistency_Report?.Flags;
        if (flags is null || flags.Count == 0)
        {
            ConsistencyCard.Visibility = Visibility.Collapsed;
            return;
        }
        ConsistencyCard.Visibility = Visibility.Visible;
        ConsistencyCard.IsExpanded = true;
        foreach (var f in flags)
        {
            var line = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6),
                FontSize = 13,
                LineHeight = 20,
            };
            var sev = string.IsNullOrWhiteSpace(f.Severity) ? "info" : f.Severity;
            line.Inlines.Add(new Run($"[{sev}] {f.Point}") { Foreground = (Brush)FindResource("TextBrush") });
            if (!string.IsNullOrWhiteSpace(f.Evidence))
            {
                line.Inlines.Add(new Run(" "));
                var link = new Hyperlink(new Run($"「{f.Evidence}」"))
                {
                    Foreground = (Brush)FindResource("AccentBrush"),
                    Cursor = Cursors.Hand,
                    Tag = f.Evidence,
                };
                link.Click += ConsistencyEvidence_Click;
                line.Inlines.Add(link);
            }
            if (!string.IsNullOrWhiteSpace(f.Suggestion))
                line.Inlines.Add(new Run($" — {f.Suggestion}") { Foreground = (Brush)FindResource("MutedBrush") });
            ConsistencyFlagsPanel.Children.Add(line);
        }
    }

    private void ConsistencyEvidence_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Hyperlink { Tag: string evidence } || string.IsNullOrWhiteSpace(evidence)) return;
        if (!TrySelectInBody(evidence))
            ReportError(L.T("Status.EvidenceNotFound"));
    }

    /// <summary>在 BodyEditor 中选中 evidence 首次出现并滚入视野。</summary>
    private bool TrySelectInBody(string needle)
    {
        if (string.IsNullOrEmpty(needle)) return false;
        ShowOnlyKeepAudit(Zone.Body);
        var pointer = BodyEditor.Document.ContentStart;
        while (pointer is not null && pointer.CompareTo(BodyEditor.Document.ContentEnd) < 0)
        {
            if (pointer.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                var run = pointer.GetTextInRun(LogicalDirection.Forward);
                var idx = run.IndexOf(needle, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    var start = pointer.GetPositionAtOffset(idx);
                    var end = start?.GetPositionAtOffset(needle.Length);
                    if (start is not null && end is not null)
                    {
                        BodyEditor.Focus();
                        BodyEditor.Selection.Select(start, end);
                        var rect = end.GetCharacterRect(LogicalDirection.Forward);
                        BodyEditor.ScrollToVerticalOffset(Math.Max(0, BodyEditor.VerticalOffset + rect.Top - 40));
                        return true;
                    }
                }
            }
            pointer = pointer.GetNextContextPosition(LogicalDirection.Forward);
        }
        // 跨 run 时退化为整篇纯文本偏移（忽略 \r）
        var plain = new TextRange(BodyEditor.Document.ContentStart, BodyEditor.Document.ContentEnd).Text
            .Replace("\r", "");
        var flatNeedle = needle.Replace("\r", "");
        var at = plain.IndexOf(flatNeedle, StringComparison.Ordinal);
        if (at < 0) return false;
        var nav = BodyEditor.Document.ContentStart.GetPositionAtOffset(at, LogicalDirection.Forward)
                  ?? BodyEditor.Document.ContentStart;
        // GetPositionAtOffset 按符号计，与 TextRange.Text 略有偏差；再向前搜一小段
        for (var i = 0; i < 64 && nav is not null; i++)
        {
            var probe = new TextRange(nav, BodyEditor.Document.ContentEnd).Text.Replace("\r", "");
            if (probe.StartsWith(flatNeedle, StringComparison.Ordinal))
            {
                var end = nav.GetPositionAtOffset(flatNeedle.Length);
                if (end is not null)
                {
                    BodyEditor.Focus();
                    BodyEditor.Selection.Select(nav, end);
                    var rect = end.GetCharacterRect(LogicalDirection.Forward);
                    BodyEditor.ScrollToVerticalOffset(Math.Max(0, BodyEditor.VerticalOffset + rect.Top - 40));
                    return true;
                }
                break;
            }
            nav = nav.GetNextInsertionPosition(LogicalDirection.Forward);
        }
        return false;
    }

    private void ClearChapterView()
    {
        LoadEditors(null);
        _summaryWindow?.Close();
        ConsistencyFlagsPanel.Children.Clear();
        ConsistencyCard.Visibility = Visibility.Collapsed;
        RenderLore();
        ApplyStage();
        UpdateZoneTitles();
        SyncReadingChrome();
        NotesPanelView.SetCurrentChapter(null);
        UpdateStepButtons();
        _statsWindow?.Close();
        SetSensitiveState(SensitiveScanState.NotRun);
        FormatBar.CloseOverlays();
        _poppedBar?.CloseOverlays();
        SyncToolsContext();
    }

    // ---------------------------------------------------------------- editors (plan / body / review) and autosave

    /// <param name="Chapter">为 null 表示在还没有章节的空白页上书写，保存时新建下一章。</param>
    /// <param name="Text">为 null 的部分本次不保存。</param>
    private sealed record PendingEdits(string ProjectId, ChapterDto? Chapter, string? Text, string? Rich,
        ChapterPlanDto? Plan, ReviewDraftDto? Review, SaveFeedback Feedback);

    /// <summary>空白页首次保存时新建的章节；新建请求返回前排队的后续保存也写入这一章。</summary>
    private (string ProjectId, ChapterDto Chapter)? _blankChapter;

    private bool EditorsEditable => !_busy && _project is not null && _selected?.Status != "superseded";

    private bool AnyDirty => _bodyDirty || _planDirty || _reviewDirty;

    /// <summary>切换章节前先把上一章未保存的编辑发出去，再换内容。</summary>
    private void LoadEditors(ChapterDto? ch)
    {
        _ = FlushEditsAsync();
        _selected = ch;
        _loadingBody = true;
        try
        {
            BodyEditor.Document = RichBody.Build(ch?.Body_Text ?? "", ch?.Body_Rich ?? "", _project?.Root_Path);
            using (BodyFigures.SuspendUndo(BodyEditor.Document)) BodyParagraphs.Normalize(BodyEditor.Document, BodyEditor.FontSize);
            BodyEditing.Quiet = true;
            try { BodyEditing.ShowNotes(); }
            finally { BodyEditing.Quiet = false; }
        }
        finally
        {
            _loadingBody = false;
        }
        PlanZoneView.Load(ch?.Plan, _project is null ? CurrentLore() : PlanZone.Catalog(_project, CurrentLore()));
        ReviewZoneView.Load(ch?.Review?.User_Annotations ?? "");
        UpdateEditorState();
        UpdateBodyInfo();
    }

    private void UpdateEditorState()
    {
        var readOnly = !EditorsEditable;
        BodyEditor.IsReadOnly = readOnly;
        PlanZoneView.IsReadOnly = readOnly;
        PlanZoneView.CanOpenProposal = _project is not null && !_busy;
        PlanZoneView.ShowNotice(
            _project is null ? "Plan.NoWorkBanner"
            : _selected?.Status == "superseded" ? "Plan.ReadOnlyBanner"
            : null);
        ReviewZoneView.IsReadOnly = readOnly;
        // 杂项是作品级字段：已作废章节不该锁住它，只看是否忙碌 / 是否打开了作品
        LoreMiscView.IsReadOnly = _busy || _project is null;
        // 作废章节 / 忙碌期间不允许在查找面板里替换正文
        FindPanelView.SetReplaceEnabled(!readOnly);
        UpdateStackedReadOnly();
        UpdateProposalsReadOnly();
        UpdateStepButtons();
        SyncToolsContext();
        SyncFormatBars();
        CommandManager.InvalidateRequerySuggested();
    }

    private void UpdateBodyInfo()
    {
        BodyWordCountText.Text = _selected is null ? "" : L.T("Main.WordCount", _selected.Word_Count);
        UpdateSaveFeedback();
    }

    private void RestartSaveTimer()
    {
        UpdateSaveFeedback();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void BodyEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingBody || BodyEditor.IsReadOnly) return;
        _bodyDirty = true;
        RestartSaveTimer();
        ScheduleReviewUpdate();
        // 查找面板开着时同步未保存草稿，保证本章/全书查找都使用眼前正文
        if (_selected is not null && FindPanelView.Visibility == Visibility.Visible)
            FindPanelView.SetDraft(_selected.No, RichBody.PlainText(BodyEditor.Document));
        MarkSensitiveStale();
        SyncToolsContext();
    }

    private PendingEdits? TakePendingEdits()
    {
        _saveTimer.Stop();
        if (!AnyDirty || _project is null) return null;
        string? text = null, rich = null;
        ChapterPlanDto? plan = null;
        ReviewDraftDto? review = null;
        var deferBody = false;
        if (_bodyDirty)
        {
            try
            {
                var captured = RichBody.Capture(BodyEditor.Document);
                text = captured.Text;
                rich = captured.Rich;
            }
            catch (RichBody.CaptureDeferredException) { deferBody = true; }
        }
        if (_planDirty) plan = PlanZoneView.Read();
        if (_reviewDirty)
            review = new ReviewDraftDto { User_Annotations = ReviewZoneView.Notes };
        _bodyDirty = deferBody;
        _planDirty = _reviewDirty = false;
        if (deferBody) RestartSaveTimer();

        if (_selected is null)
        {
            if (string.IsNullOrWhiteSpace(text)) text = rich = null;
            if (plan is not null && PlanZone.IsEmpty(plan)) plan = null;
            if (review is not null && string.IsNullOrWhiteSpace(review.User_Annotations)) review = null;
            if (text is null && plan is null && review is null) return null;
        }
        return new PendingEdits(_project.Id, _selected, text, rich, plan, review, CurrentSaveFeedback!);
    }

    /// <summary>保存按顺序排队，避免旧内容晚到覆盖新内容或生成结果。</summary>
    private Task<bool> FlushEditsAsync()
    {
        if (TakePendingEdits() is { } pending)
        {
            pending.Feedback.Queue();
            _saveTask = SaveEditsAfter(_saveTask, pending);
        }
        UpdateSaveFeedback();
        return _saveTask;
    }

    private async Task<bool> SaveEditsAfter(Task previous, PendingEdits p)
    {
        try { await previous; } catch { /* 上一次失败已提示 */ }
        var ch = p.Chapter ?? (_blankChapter is { } b && b.ProjectId == p.ProjectId ? b.Chapter : null);
        var part = L.T("Save.PartBody");
        var success = false;
        try
        {
            if (ch is null)
            {
                ch = await CreateChapterFromBlank(p);
            }
            else
            {
                if (p.Text is not null)
                {
                    var saved = await App.Api.SaveBodyAsync(p.ProjectId, ch.No, p.Text, p.Rich ?? "");
                    ch.Body_Text = saved.Body_Text;
                    ch.Body_Rich = saved.Body_Rich;
                    ch.Word_Count = saved.Word_Count;
                    ch.Status = saved.Status;
                    ch.Summary = saved.Summary;
                    ch.Summary_Status = saved.Summary_Status;
                    ch.Summary_Error = saved.Summary_Error;
                    ch.Body_Hash = saved.Body_Hash;
                    ch.Upstream_Changed = saved.Upstream_Changed;
                    ch.Plan_Upstream_Changed = saved.Plan_Upstream_Changed;
                    ch.Consistency_Report = saved.Consistency_Report;
                    ch.Plan_Check = saved.Plan_Check;
                    if (_selected == ch)
                    {
                        UpdateSummaryText();
                        UpdateConsistencyFlags();
                    }
                }
                if (p.Plan is not null)
                {
                    part = L.T("Save.PartPlan");
                    PreserveStackedMembers(p.Plan, ch.Stacked_Constraints);
                    var planned = await App.Api.SavePlanAsync(p.ProjectId, ch.No, p.Plan);
                    ch.Plan = p.Plan;
                    ch.Lore = planned.Lore;
                    ch.Plan_Upstream_Changed = planned.Plan_Upstream_Changed;
                    ch.Plan_Check = planned.Plan_Check;
                    if (_selected == ch) { UpdateSummaryText(); RenderLore(); }
                }
            }
            if (p.Review is not null)
            {
                part = L.T("Save.PartReview");
                await App.Api.SaveReviewAsync(p.ProjectId, ch.No, p.Review.User_Annotations);
                ch.Review = p.Review;
            }
            if (ReferenceEquals(ch, _selected)) UpdateBodyInfo();
            if (p.Plan is not null) _ = RefreshProposalStateAsync();
            success = true;
            return true;
        }
        catch (Exception ex)
        {
            if (_project?.Id == p.ProjectId && (ch is null ? _selected is null : ReferenceEquals(ch, _selected)))
            {
                _bodyDirty |= p.Text is not null;
                _planDirty |= p.Plan is not null;
                _reviewDirty |= p.Review is not null;
            }
            ReportError(ch is null
                ? L.T("Save.CreateChapterFailed", ex.Message)
                : L.T("Save.Failed", ch.No, part, ex.Message));
            return false;
        }
        finally
        {
            p.Feedback.Dirty = ReferenceEquals(p.Feedback, CurrentSaveFeedback) && AnyDirty;
            p.Feedback.Complete(success);
            UpdateSaveFeedback();
        }
    }

    /// <summary>新建章节后不重载各区（用户可能还在输入），只把它挂到当前页和目录树上。</summary>
    private async Task<ChapterDto> CreateChapterFromBlank(PendingEdits p)
    {
        var created = await App.Api.CreateChapterAsync(p.ProjectId, p.Text ?? "", p.Rich ?? "", p.Plan);
        _saveFeedback[(p.ProjectId, created.No)] = p.Feedback;
        _saveFeedback.Remove((p.ProjectId, 0));
        _blankChapter = (p.ProjectId, created);
        if (_project?.Id != p.ProjectId || _selected is not null) return created;

        _selected = created;
        _chapters.Add(created);
        _project.Current_Chapter = created.No;
        _project.Phase = "PLAN";
        NotesPanelView.SetCurrentChapter(created.No);
        FindPanelView.SetChapters(_chapters);
        FindPanelView.SetDraft(created.No, p.Text ?? "");
        if (_works.FirstOrDefault(w => w.Project.Id == p.ProjectId) is { } work)
        {
            _suppressTreeSelection = true;
            try
            {
                work.SetChapters(_chapters);
                work.IsExpanded = true;
                if (work.Chapters.LastOrDefault() is { } node) node.IsSelected = true;
            }
            finally
            {
                _suppressTreeSelection = false;
            }
        }
        _stageKey = (p.ProjectId, created.No, StageOf(created));
        UpdateBodyInfo();
        RenderLore();
        UpdateStepButtons();
        UpdateZoneTitles();
        return created;
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        // A second click/Alt+F4 must not escape while the first close awaits IO.
        // Only the final, explicitly authorized Close below may re-enter.
        if (_closingInProgress && !_closeAfterSave)
        {
            e.Cancel = true;
            return;
        }
        // Auxiliary numeric editors commit on focus loss; include them in the
        // main close's dirty/save decision before WPF closes owned windows.
        Keyboard.ClearFocus();
        ReaderView.Pause();
        LayoutStore.Save(CaptureLayout());
        // 是否取消关闭必须在第一个 await 之前决定：Closing 一旦同步返回，之后再设 e.Cancel 无效。
        if (_closeAfterSave || (!AnyDirty && _saveTask.IsCompleted && !NotesPanelView.HasUnsavedDraft
                && !LoreMiscView.HasUnsaved && !StacksPending && !ProposalsPending && !BodyRevisionsPending))
        {
            if (_dictating) _recorder.Cancel();
            ReaderView.DisposeReader();
            _recorder.Dispose();
            _stt.Dispose();
            return;
        }
        e.Cancel = true;
        _closingInProgress = true;
        try
        {
            try { await NotesPanelView.FlushAsync(); }
            catch (Exception) { /* 笔记保存失败已由面板自行提示，下面按未保存处理 */ }
            if (!await ConfirmMiscDraftAsync()) return;
            await FlushEditsAsync();
            if (!await FlushStackedAsync()) return;
            if (!await FlushProposalsAsync()) return;
            if (!await FlushBodyRevisionsAsync()) return;
            if ((AnyDirty || NotesPanelView.HasUnsavedDraft) && MessageBox.Show(this, L.T("Main.CloseUnsaved"), L.T("App.Title"),
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            _closeAfterSave = true;
            await Dispatcher.BeginInvoke(Close);
        }
        finally { _closingInProgress = false; }
    }

    // ---------------------------------------------------------------- creation zones

    private static string ZoneName(Zone z) => z switch
    {
        Zone.Plan => L.T("Zone.Plan"),
        Zone.Body => L.T("Zone.Body"),
        Zone.Review => L.T("Zone.Review"),
        _ => L.T("Zone.Audit"),
    };

    private ToggleButton ToggleOf(Zone z) => z switch
    {
        Zone.Plan => PlanToggle,
        Zone.Body => BodyToggle,
        Zone.Review => ReviewToggle,
        _ => AuditToggle,
    };

    private FrameworkElement HostOf(Zone z) => z switch
    {
        Zone.Plan => PlanHost,
        Zone.Body => BodyHost,
        Zone.Review => ReviewHost,
        _ => AuditHost,
    };

    private Border SlotOf(Zone z) => z switch
    {
        Zone.Plan => PlanSlot,
        Zone.Body => BodySlot,
        Zone.Review => ReviewSlot,
        _ => AuditSlot,
    };

    private FrameworkElement ContentOf(Zone z) => z switch
    {
        Zone.Plan => PlanZoneView,
        Zone.Body => ReadingPaper,
        Zone.Review => ReviewZoneView,
        _ => AuditZoneView,
    };

    /// <summary>章节所处阶段：未写正文在规划区，写作中在正文区，定稿后（尚未进入下一章时）在评注区。</summary>
    private Zone StageOf(ChapterDto? ch)
    {
        if (ch is null || (ch.Status != "superseded" && string.IsNullOrWhiteSpace(ch.Body_Text))) return Zone.Plan;
        var latest = _chapters.Where(c => c.Status != "superseded").Select(c => c.No).DefaultIfEmpty(0).Max();
        return ch.Status == "final" && ch.No >= latest ? Zone.Review : Zone.Body;
    }

    /// <summary>「上一步 / 下一步」以最靠后的已展示区为当前步骤。审计区是伴随面板，不参与判定，
    /// 否则它一旦停靠就会顶掉正文区 / 评注区成为「当前步骤」。</summary>
    private Zone ViewStage()
    {
        var docked = _openZones.Where(z => z != Zone.Audit && !_popped.ContainsKey(z)).ToList();
        if (docked.Count > 0) return docked.Max();
        var open = _openZones.Where(z => z != Zone.Audit).ToList();
        return open.Count > 0 ? open.Max() : StageOf(_selected);
    }

    /// <summary>换了章节或章节进入新阶段时，只展示该阶段对应的区；否则保留用户的选择。
    /// 刚打开作品时最新一章已定稿的话先展示正文区，评注区要点「下一步」才进入。
    /// 纯净模式不跟阶段切区，只保证正文在。</summary>
    private void ApplyStage()
    {
        if (IsPureMode)
        {
            _openZones.Add(Zone.Body);
            LayoutZones();
            return;
        }
        var stage = StageOf(_selected);
        var key = (_project?.Id, _selected?.No, stage);
        if (_stageKey == key)
        {
            LayoutZones();
            return;
        }
        var opening = _stageKey?.ProjectId != _project?.Id;
        _stageKey = key;
        ShowOnlyKeepAudit(opening && stage == Zone.Review ? Zone.Body : stage);
    }

    /// <summary>创作区里只留这一个区（已弹出的子窗口不受影响）。</summary>
    private void ShowOnly(Zone zone)
    {
        _openZones.RemoveWhere(z => !_popped.ContainsKey(z));
        _openZones.Add(zone);
        if (_popped.TryGetValue(zone, out var w)) w.Activate();
        LayoutZones();
    }

    /// <summary>同 ShowOnly，但保留已停靠的审计区。审计区是「陪着看正文」的伴随面板：
    /// 逐条点「定位」时要一直看得见，不能被切到正文区的动作关掉。</summary>
    private void ShowOnlyKeepAudit(Zone zone)
    {
        var keepAudit = !IsPureMode && zone != Zone.Audit
            && _openZones.Contains(Zone.Audit) && !_popped.ContainsKey(Zone.Audit);
        ShowOnly(zone);
        if (keepAudit && _openZones.Add(Zone.Audit)) LayoutZones();
    }

    /// <summary>把某个区亮出来：纯净模式弹成子窗口（与「本章规划 / 本章评注」一致），
    /// 辅助模式则停靠进创作区，不打扰已经展开的其它区。</summary>
    private void RevealZone(Zone zone)
    {
        if (IsPureMode)
        {
            PopOut(zone);
            return;
        }
        if (_popped.TryGetValue(zone, out var w))
        {
            w.Activate();
            return;
        }
        if (_openZones.Add(zone)) LayoutZones();
    }

    private void ZoneToggle_Click(object sender, RoutedEventArgs e)
    {
        var zone = Zones.First(z => ReferenceEquals(ToggleOf(z), sender));
        if (_popped.TryGetValue(zone, out var w))
        {
            w.Activate();
        }
        else if (!_openZones.Remove(zone))
        {
            _openZones.Add(zone);
        }
        LayoutZones();
    }

    /// <summary>按流程顺序左右排开留在创作区的各区，每次重新等分。</summary>
    private void LayoutZones()
    {
        foreach (var s in _zoneSplitters) ZoneGrid.Children.Remove(s);
        _zoneSplitters.Clear();
        ZoneGrid.ColumnDefinitions.Clear();

        var docked = Zones.Where(z => _openZones.Contains(z) && !_popped.ContainsKey(z)).ToList();
        foreach (var z in Zones)
        {
            HostOf(z).Visibility = docked.Contains(z) ? Visibility.Visible : Visibility.Collapsed;
            ToggleOf(z).IsChecked = _openZones.Contains(z);
        }
        for (var i = 0; i < docked.Count; i++)
        {
            if (i > 0)
            {
                ZoneGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
                var splitter = new GridSplitter
                {
                    Style = (Style)FindResource("PaneSplitter"),
                    ResizeDirection = GridResizeDirection.Columns,
                    ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                };
                Grid.SetColumn(splitter, ZoneGrid.ColumnDefinitions.Count - 1);
                ZoneGrid.Children.Add(splitter);
                _zoneSplitters.Add(splitter);
            }
            ZoneGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 140 });
            Grid.SetColumn(HostOf(docked[i]), ZoneGrid.ColumnDefinitions.Count - 1);
        }
        NoZoneHint.Visibility = docked.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoZoneHint.SetResourceReference(TextBlock.TextProperty, _popped.Count > 0 ? "Main.ZonesPopped" : "Main.NoZoneHint");
        Grid.SetColumnSpan(NoZoneHint, Math.Max(1, ZoneGrid.ColumnDefinitions.Count));
        UpdateStepButtons();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => SyncZoneToggles(null, EventArgs.Empty));
    }

    /// <summary>
    /// 区名按钮居中于所属区上方；未停靠的区按流程顺序贴在相邻边界处（前面无区贴最左，后面无区贴最右），
    /// 三区都不在时三等分。最后按顺序避让，不重叠、不越界。
    /// </summary>
    private void SyncZoneToggles(object? sender, EventArgs e)
    {
        var rowWidth = ZoneToggleRow.ActualWidth;
        if (rowWidth <= 0) return;
        const double gap = 6;
        var docked = Zones.Where(z => HostOf(z).Visibility == Visibility.Visible && HostOf(z).ActualWidth > 0).ToList();
        double EdgeX(Zone z, bool right) =>
            HostOf(z).TranslatePoint(new Point(right ? HostOf(z).ActualWidth : 0, 0), ZoneToggleRow).X;

        var widths = Zones.Select(z => ToggleOf(z).ActualWidth).ToArray();
        var lefts = new double[Zones.Length];
        for (var i = 0; i < Zones.Length; i++)
        {
            var z = Zones[i];
            if (docked.Count == 0)
            {
                lefts[i] = rowWidth * (2 * i + 1) / (2.0 * Zones.Length) - widths[i] / 2;
                continue;
            }
            if (docked.Contains(z))
            {
                lefts[i] = (EdgeX(z, false) + EdgeX(z, true)) / 2 - widths[i] / 2;
                continue;
            }
            var prev = docked.Where(d => d < z).Select(d => (Zone?)d).LastOrDefault();
            var next = docked.Where(d => d > z).Select(d => (Zone?)d).FirstOrDefault();
            lefts[i] = prev is null ? 0
                : next is null ? rowWidth - widths[i]
                : (EdgeX(prev.Value, true) + EdgeX(next.Value, false)) / 2 - widths[i] / 2;
        }

        for (var i = 0; i < lefts.Length; i++)
        {
            if (i > 0) lefts[i] = Math.Max(lefts[i], lefts[i - 1] + widths[i - 1] + gap);
            lefts[i] = Math.Max(lefts[i], 0);
        }
        for (var i = lefts.Length - 1; i >= 0; i--)
        {
            var limit = i == lefts.Length - 1 ? rowWidth : lefts[i + 1] - gap;
            lefts[i] = Math.Max(0, Math.Min(lefts[i], limit - widths[i]));
        }
        for (var i = 0; i < Zones.Length; i++)
            Canvas.SetLeft(ToggleOf(Zones[i]), Math.Round(lefts[i]));
    }

    private void PopOut_Click(object sender, RoutedEventArgs e)
    {
        var zone = (string)((FrameworkElement)sender).Tag switch
        {
            "plan" => Zone.Plan,
            "body" => Zone.Body,
            "review" => Zone.Review,
            _ => Zone.Audit,
        };
        PopOut(zone);
    }

    /// <summary>把该区的内容移到子窗口；正文区的子窗口自带一条 Word 栏。</summary>
    private void PopOut(Zone zone)
    {
        if (_popped.TryGetValue(zone, out var existing))
        {
            existing.Activate();
            return;
        }
        FormatBar.CloseOverlays();
        _poppedBar?.CloseOverlays();
        var content = ContentOf(zone);
        SlotOf(zone).Child = null;
        FormatToolbar? bar = null;
        if (zone == Zone.Body)
        {
            FormatBar.Target = null;
            bar = new FormatToolbar(showFocus: false);
            _poppedBar = bar;
            bar.Target = BodyEditor;
            WireFormatBar(bar, isPopped: true);
            bar.SelectedDictationLang = FormatBar.SelectedDictationLang;
            bar.IsDictating = _dictating;
        }
        ApplyCornerInsets();
        var win = new ZoneWindow(zone.ToString(), ZoneTitle(zone), content, bar) { Owner = this };
        if (zone == Zone.Plan)
            win.InputBindings.Add(new KeyBinding(new RelayCommand(() => Save_Click(this, new RoutedEventArgs()), () => ShellCommandState.CanUseWork(_project is not null)), Key.S, ModifierKeys.Control));
        win.Closed += (_, _) => DockBack(zone, win);
        _popped[zone] = win;
        _openZones.Add(zone);
        LayoutZones();
        UpdateEditorState();
        UpdateBodyInfo();
        win.Show();
    }

    /// <summary>停靠在主窗口的区给右上角的悬浮弹出按钮让位（规划区的「AI 规划」、正文区的滚动条）；弹出到子窗口后收回。</summary>
    private void ApplyCornerInsets()
    {
        PlanZoneView.CornerInset = _popped.ContainsKey(Zone.Plan) ? 0 : 30;
        var right = _popped.ContainsKey(Zone.Body) ? 16 : 38;
        BodyZoneView.Margin = new Thickness(24, 8, right, 8);
    }

    private void DockBack(Zone zone, ZoneWindow win)
    {
        if (!_popped.Remove(zone)) return;
        _poppedBar?.CloseOverlays();
        win.Detach();
        SlotOf(zone).Child = ContentOf(zone);
        ApplyCornerInsets();
        if (zone == Zone.Body && _poppedBar is not null)
        {
            _poppedBar.Target = null;
            _poppedBar = null;
            FormatBar.Target = BodyEditor;
        }
        if (IsPureMode && zone != Zone.Body)
            ShowOnly(Zone.Body);
        else
        {
            _openZones.Add(zone);
            LayoutZones();
        }
        UpdateEditorState();
    }

    /// <summary>在光标所在段落后插入插画。大图在后台缩好再放进来，避免界面卡住。</summary>
    private async void InsertBodyImage()
    {
        if (!EditorsEditable || _storingFigure) return;
        var root = _project?.Root_Path;
        if (string.IsNullOrWhiteSpace(root))
        {
            ReportError(L.T("Fmt.NeedFolder"));
            return;
        }
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = L.T("Fmt.InsertImage"),
            Filter = L.T("Char.ImageFilter"),
        };
        if (dlg.ShowDialog(this) != true) return;

        var chapter = _selected?.No;
        var document = BodyEditor.Document;
        var insertion = BodyEditor.CaretPosition.GetPositionAtOffset(0, LogicalDirection.Forward);
        var label = L.T("Fmt.InsertImageBusy");
        _storingFigure = true;
        ShowBusyHint(label);
        try
        {
            var stored = await BodyFigures.StoreAsync(root, dlg.FileName);
            await BodyEditing.CompositionIdleAsync();
            if (!EditorsEditable || _project?.Root_Path != root || _selected?.No != chapter
                || !ReferenceEquals(BodyEditor.Document, document))
            {
                // Content-addressed resources can be shared by other chapters and undo history.
                return;
            }
            if (insertion is not null) BodyEditor.CaretPosition = insertion;
            BodyFigures.Insert(BodyEditor, stored);
            _bodyDirty = true;
            RestartSaveTimer();
        }
        catch (Exception ex)
        {
            ReportError(ex.Message);
        }
        finally
        {
            _storingFigure = false;
            HideBusyHint();
        }
    }

    private void BindDictation(FormatToolbar bar)
    {
        bar.DictationToggled += async (_, start) => await OnDictationToggledAsync(start);
        bar.DictationLangChanged += (_, lang) => SyncDictationLang(lang);
    }

    private void SyncDictationLang(string lang)
    {
        var code = FormatToolbar.NormalizeLang(lang);
        FormatBar.SelectedDictationLang = code;
        if (_poppedBar is not null) _poppedBar.SelectedDictationLang = code;
    }

    private void SetDictationUi(DictationState state, string? message = null)
    {
        FormatBar.SetDictationState(state, message);
        _poppedBar?.SetDictationState(state, message);
    }

    private void SetMicChecked(bool on) => SetDictationUi(on ? DictationState.Recording : DictationState.Idle);

    private async Task OnDictationToggledAsync(bool start)
    {
        if (_transcribing)
        {
            SetMicChecked(_dictating);
            return;
        }
        if (start)
        {
            if (BodyEditor.IsReadOnly)
            {
                SetDictationUi(DictationState.Failed, L.T("Stt.ReadOnly"));
                return;
            }
            try
            {
                var status = await _stt.StatusAsync();
                if (!status.Available)
                {
                    SetDictationUi(DictationState.Failed,
                        string.IsNullOrWhiteSpace(status.Message) ? L.T("Stt.Missing") : status.Message);
                    return;
                }
                ReaderView.StopSpeech();
                _recorder.Start();
                _dictating = true;
                SetDictationUi(DictationState.Recording);
            }
            catch (Exception ex)
            {
                _dictating = false;
                SetDictationUi(DictationState.Failed,
                    string.IsNullOrWhiteSpace(ex.Message) ? L.T("Stt.MicFailed") : ex.Message);
            }
            return;
        }

        _dictating = false;
        SetDictationUi(DictationState.Transcribing);
        string? path = null;
        try
        {
            path = _recorder.Stop();
            if (path is null)
            {
                SetDictationUi(DictationState.Failed, L.T("Stt.Empty"));
                return;
            }
            _transcribing = true;
            var lang = FormatBar.SelectedDictationLang;
            var document = BodyEditor.Document;
            var chapter = _selected?.No;
            var result = await _stt.TranscribeAsync(path, lang);
            if (string.IsNullOrWhiteSpace(result.Text))
            {
                SetDictationUi(DictationState.Failed, L.T("Stt.Empty"));
                return;
            }
            await BodyEditing.CompositionIdleAsync();
            // 转写期间换了章：不能把这段话插进别的章节
            if (!ReferenceEquals(BodyEditor.Document, document) || _selected?.No != chapter)
            {
                SetDictationUi(DictationState.Failed, L.T("Stt.ChapterChanged"));
                return;
            }
            InsertDictationText(result.Text);
            SetDictationUi(DictationState.Idle);
        }
        catch (Exception ex)
        {
            SetDictationUi(DictationState.Failed,
                string.IsNullOrWhiteSpace(ex.Message) ? L.T("Stt.Failed") : ex.Message);
        }
        finally
        {
            _transcribing = false;
            if (path is not null)
            {
                try { File.Delete(path); } catch { /* ignore */ }
            }
        }
    }

    private void InsertDictationText(string text)
    {
        if (BodyEditor.IsReadOnly) return;
        BodyEditor.Focus();
        BodyEditor.BeginChange();
        try
        {
            var caret = BodyEditor.CaretPosition;
            if (BodyEditor.Document.Blocks.Count == 0)
                BodyEditor.Document.Blocks.Add(new Paragraph(new Run(text)) { Margin = FormatToolbar.BodyMargin });
            else
                new TextRange(caret, caret).Text = text;
            BodyEditor.CaretPosition = BodyEditor.CaretPosition.GetPositionAtOffset(0, LogicalDirection.Forward)
                                       ?? BodyEditor.Document.ContentEnd;
        }
        finally { BodyEditor.EndChange(); }
    }

    private string ZoneTitle(Zone zone) =>
        $"{ZoneName(zone)} · {(_selected is null ? TitleProjectText.Text : HtmlPreview.Heading(_selected))}";

    private void UpdateZoneTitles()
    {
        foreach (var (zone, win) in _popped) win.Title = ZoneTitle(zone);
    }

    // ---------------------------------------------------------------- side pane

    private void OpenSide(string projectId, string workTitle, ChapterDto ch)
    {
        _sideProjectId = projectId;
        SideHeader.Text = L.T("Side.Header", HtmlPreview.Heading(ch), workTitle);
        SideHeader.ToolTip = SideHeader.Text;
        SideBody.Text = string.IsNullOrEmpty(ch.Body_Text) ? L.T("Side.NoText") : ch.Body_Text;
        SideBody.ScrollToHome();
        SetCompareVisible(true);
    }

    private void CloseSide()
    {
        if (SidePane.Visibility != Visibility.Visible) return;
        SetCompareVisible(false);
        _sideProjectId = null;
    }

    /// <summary>只折叠或展开对照栏，不改对照内容。阅读模式用它临时让开正文。</summary>
    private void SetCompareVisible(bool visible)
    {
        if (visible == (SidePane.Visibility == Visibility.Visible)) return;
        if (visible)
        {
            MainColumn.Width = new GridLength(1, GridUnitType.Star);
            SideColumn.Width = new GridLength(_sideWidth.Value, GridUnitType.Star);
            SideColumn.MinWidth = 260;
            SideSplitterColumn.Width = new GridLength(5);
            CenterColumn.MinWidth = MainColumn.MinWidth + 5 + SideColumn.MinWidth;
            SidePane.Visibility = Visibility.Visible;
            SideSplitter.Visibility = Visibility.Visible;
            return;
        }
        _sideWidth = new GridLength(SideColumn.Width.Value / Math.Max(MainColumn.Width.Value, 0.001), GridUnitType.Star);
        SideColumn.MinWidth = 0;
        SideColumn.Width = new GridLength(0);
        SideSplitterColumn.Width = new GridLength(0);
        MainColumn.Width = new GridLength(1, GridUnitType.Star);
        CenterColumn.MinWidth = MainColumn.MinWidth;
        SidePane.Visibility = Visibility.Collapsed;
        SideSplitter.Visibility = Visibility.Collapsed;
    }

    private void CloseSide_Click(object sender, RoutedEventArgs e) => CloseSide();

    private void CloseSideIfShowing(string projectId)
    {
        if (_sideProjectId == projectId) CloseSide();
    }

    // ---------------------------------------------------------------- lore (right pane)

    /// <summary>右栏底部按钮（添加人物/添加脉络/添加条目）的中线对齐创作区底部，
    /// 取代已删除的底部步骤条（原基准是 StepBar 的中线）。</summary>
    private void AlignLoreButtons(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            var buttons = new[] { AddCharacterButton, AddThreadButton, AddEntryButton };
            if (CenterPane.ActualHeight <= 0 || buttons.FirstOrDefault(b => b.IsVisible) is not { } shown
                || shown.Parent is not FrameworkElement dock)
                return;
            // 创作区底边向上 18px 作为对齐基线
            var anchor = CenterPane.TranslatePoint(new Point(0, CenterPane.ActualHeight - 18), PaneGrid).Y;
            var dockBottom = dock.TranslatePoint(new Point(0, dock.ActualHeight), PaneGrid).Y;
            var bottom = Math.Max(0, dockBottom - anchor - shown.ActualHeight / 2);
            foreach (var b in buttons)
                b.Margin = new Thickness(0, 8, 0, bottom);
        });

    /// <summary>章节 lore 的人物名单不变，但每个 id 用作品主档里的最新公共资料覆盖；
    /// 主档里已经没有的历史人物才用章节快照。没有选中章节时直接用作品主档。</summary>
    private LoreDto CurrentLore()
    {
        if (_selected?.Lore is not { } snapshot || _project is null)
            return _project is null ? new LoreDto() : LoreDto.From(_project);
        return new LoreDto
        {
            Entries = snapshot.Entries,
            Threads = snapshot.Threads,
            Characters = snapshot.Characters
                .Select(c => _project.Characters.FirstOrDefault(m => m.Id == c.Id) ?? c)
                .ToList(),
        };
    }

    private void RenderLore()
    {
        if (_project is null)
        {
            _loreChapter = null;
            _loreReadOnly = true;

        }
        else if (_selected is null)
        {
            _loreChapter = null;
            _loreReadOnly = false;

        }
        else if (_selected.Status == "superseded")
        {
            _loreChapter = null;
            _loreReadOnly = true;

        }
        else
        {
            var latest = _chapters.Where(c => c.Status != "superseded").Select(c => c.No).DefaultIfEmpty(0).Max();
            _loreChapter = _selected.No;
            _loreReadOnly = false;
            var from = _selected.Lore_From is int m ? L.T("Lore.InheritedFrom", m) : "";

        }

        RenderLoreSources();

        var lore = CurrentLore();
        var editable = _project is not null && !_loreReadOnly;
        AddCharacterButton.IsEnabled = editable;
        AddThreadButton.IsEnabled = editable;
        AddEntryButton.IsEnabled = editable;
        RenderEntries(lore.Entries, editable);
        RenderCharacters(lore.Characters, editable);
        RenderThreads(lore.Threads, editable);
        // 杂项是作品级字段，不属于章节设定快照，因此只依赖 _project
        LoreMiscView.Load(_project);
        LoreMiscView.IsReadOnly = _project is null || _busy;
    }

    private sealed record LoreSource(int No, string Label);

    /// <summary>第 2 章起可以从之前任一未作废章节继承设定。</summary>
    private void RenderLoreSources()
    {
        var sources = _selected is { No: > 1 } ch && !_loreReadOnly
            ? _chapters.Where(c => c.No < ch.No && c.Status != "superseded")
                .OrderBy(c => c.No)
                .Select(c => new LoreSource(c.No, L.T("Lore.ChapterLabel", c.No)))
                .ToList()
            : new List<LoreSource>();
        LoreFromBox.ItemsSource = sources;
        LoreFromBox.SelectedItem = sources.FirstOrDefault(s => s.No == _selected?.Lore_From) ?? sources.LastOrDefault();
        ApplyInheritVisibility();
    }

    private void LoreTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender)) return;
        AlignLoreButtons(sender, e);
        ApplyInheritVisibility();
    }

    private void ApplyInheritVisibility()
    {
        var onMisc = LoreTabs.SelectedIndex == 3;
        LoreInheritBar.Visibility = !onMisc && LoreFromBox.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void InheritLore_Click(object sender, RoutedEventArgs e)
    {
        if (LoreFromBox.SelectedItem is not LoreSource src || _project is null || _selected is null) return;
        var ch = _selected;
        if (MessageBox.Show(this,
                L.T("Lore.InheritConfirm", src.No, ch.No),
                L.T("Lore.InheritTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        var pid = _project.Id;
        await Safe(async () =>
        {
            await App.Api.InheritLoreAsync(pid, ch.No, src.No);
            await LoadProjectAsync(pid, ch.No);
        });
    }

    private async Task SaveLoreAsync(Action<LoreDto> mutate)
    {
        if (_project is null || _loreReadOnly) return;
        // 人物已经改走共享主档接口（SaveCharacterAsync）；这里只提交章节快照原本的条目/脉络，
        // 避免把主档里的公共资料顺手写回章节快照。
        var lore = (_selected?.Lore ?? LoreDto.From(_project)).Copy();
        mutate(lore);
        var pid = _project.Id;
        var no = _loreChapter;
        await App.Api.SaveLoreAsync(pid, lore, no);
        await LoadProjectAsync(pid, no);
    }

    /// <summary>用资源引用而不是取值，切换主题时颜色会跟着变。</summary>
    private static T Themed<T>(T el, DependencyProperty property, string key) where T : FrameworkElement
    {
        el.SetResourceReference(property, key);
        return el;
    }

    private Button SmallButton(string text, Action onClick)
    {
        var b = new Button
        {
            Content = text,
            Style = (Style)FindResource("QuietButton"),
            Padding = new Thickness(8, 2, 8, 2),
            MinHeight = 24,
            FontSize = 12,
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    private void RenderCharacters(IReadOnlyList<CharacterDto> characters, bool editable)
    {
        CharacterPanel.Children.Clear();
        if (_project is null) return;
        var root = _project.Root_Missing ? "" : _project.Root_Path;
        foreach (var c in characters)
            CharacterPanel.Children.Add(BuildCharacterCard(c, root, editable));
        if (characters.Count == 0)
        {
            CharacterPanel.Children.Add(Themed(new TextBlock
            {
                Text = L.T("Lore.NoCharacters"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 12, 0, 0),
            }, TextBlock.ForegroundProperty, "MutedBrush"));
        }
    }

    private Border BuildCharacterCard(CharacterDto c, string root, bool editable)
    {
        var grid = new CharacterSummaryIdentity(c, root);

        var stack = new StackPanel();
        stack.Children.Add(grid);
        var border = new Border { Style = (Style)FindResource("CardBorder"), Child = stack,
            Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 0, 4) };
        if (editable)
        {
            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0),
            };
            actions.Children.Add(SmallButton(L.T("Common.Edit"), () => EditCharacter(c)));
            actions.Children.Add(SmallButton(L.T("Common.Delete"), () => DeleteCharacter(c)));
            stack.Children.Add(actions);
            border.Cursor = Cursors.Hand;
            border.ToolTip = L.T("Lore.EditCharacterTip");
            border.MouseLeftButtonUp += (_, _) =>
            {
                if (!SelectingText()) EditCharacter(c);
            };
        }
        return border;
    }

    /// <summary>卡片里的只读文字框，可以用鼠标选中复制。</summary>
    private TextBox Selectable(string text, string brush = "TextBrush") => Themed(new TextBox
    {
        Text = text,
        Style = (Style)FindResource("SelectableText"),
    }, Control.ForegroundProperty, brush);

    /// <summary>在卡片上拖选文字时不触发「点击编辑」。</summary>
    private static bool SelectingText() => Keyboard.FocusedElement is TextBox { IsReadOnly: true, SelectionLength: > 0 };

    private TextBox FieldText(string value)
    {
        var box = Selectable(string.IsNullOrWhiteSpace(value) ? "—" : value,
            string.IsNullOrWhiteSpace(value) ? "MutedBrush" : "TextBrush");
        box.VerticalAlignment = VerticalAlignment.Center;
        return box;
    }

    private void AddField(Grid grid, string label, FrameworkElement value, bool center = false)
    {
        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var lbl = Selectable(label, "MutedBrush");
        lbl.TextWrapping = TextWrapping.NoWrap;
        lbl.Margin = new Thickness(0, 3, 12, 3);
        lbl.VerticalAlignment = center ? VerticalAlignment.Center : VerticalAlignment.Top;
        Grid.SetRow(lbl, row);
        grid.Children.Add(lbl);
        value.Margin = new Thickness(0, 3, 0, 3);
        Grid.SetRow(value, row);
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
    }

    private void RenderThreads(IReadOnlyList<PlotThreadDto> threads, bool editable)
    {
        ThreadPanel.Children.Clear();
        if (_project is null) return;
        foreach (var t in threads)
        {
            var stack = new StackPanel();
            var head = new WrapPanel();
            var name = Selectable(t.Name);
            name.FontWeight = FontWeights.SemiBold;
            name.TextWrapping = TextWrapping.NoWrap;
            name.VerticalAlignment = VerticalAlignment.Center;
            var meta = Selectable($"{ThreadKindLabel(t.Kind)} · {Translate("ThreadState.", t.State.ToLowerInvariant())} · {L.T("ThreadTime." + t.Time_Position)}", "MutedBrush");
            meta.FontSize = 12;
            meta.TextWrapping = TextWrapping.NoWrap;
            meta.Margin = new Thickness(10, 0, 0, 0);
            meta.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(name);
            head.Children.Add(meta);
            stack.Children.Add(head);
            if (!string.IsNullOrWhiteSpace(t.User_Directive))
            {
                var directive = Selectable(t.User_Directive);
                directive.Margin = new Thickness(0, 4, 0, 0);
                stack.Children.Add(directive);
            }
            var milestone = Selectable(
                L.T("Lore.NextMilestone", string.IsNullOrEmpty(t.Next_Milestone) ? "—" : t.Next_Milestone), "MutedBrush");
            milestone.Margin = new Thickness(0, 4, 0, 0);
            stack.Children.Add(milestone);
            var progress = Selectable(L.T("Thread.Progress", t.Milestones.Count(m => m.Completed), t.Milestones.Count), "MutedBrush");
            progress.Margin = new Thickness(0, 4, 0, 0);
            stack.Children.Add(progress);
            var border = new Border { Style = (Style)FindResource("CardBorder"), Child = stack };
            if (editable)
            {
                var actions = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 4, 0, 0),
                };
                actions.Children.Add(SmallButton(L.T("Common.Edit"), () => EditThread(t)));
                actions.Children.Add(SmallButton(L.T("Common.Delete"), () => DeleteThread(t)));
                stack.Children.Add(actions);
                border.Cursor = Cursors.Hand;
                border.ToolTip = L.T("Lore.EditThreadTip");
                border.MouseLeftButtonUp += (_, _) =>
                {
                    if (!SelectingText()) EditThread(t);
                };
            }
            ThreadPanel.Children.Add(border);
        }
        if (threads.Count == 0)
        {
            ThreadPanel.Children.Add(Themed(new TextBlock
            {
                Text = L.T("Lore.NoThreads"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 12, 0, 0),
            }, TextBlock.ForegroundProperty, "MutedBrush"));
        }
    }

    private void RenderEntries(IReadOnlyList<LoreEntryDto> entries, bool editable)
    {
        EntryPanel.Children.Clear();
        if (_project is null) return;
        foreach (var e in entries)
        {
            var stack = new StackPanel();
            var head = new WrapPanel();
            var title = Selectable(e.Display);
            title.FontWeight = FontWeights.SemiBold;
            title.TextWrapping = TextWrapping.NoWrap;
            title.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(title);

            var meta = Selectable(
                Translate("Lore.Category.", e.Category.ToLowerInvariant())
                + (e.Category == "rule" && e.Hard ? " · " + L.T("Lore.HardBadge") : ""),
                "MutedBrush");
            meta.FontSize = 12;
            meta.TextWrapping = TextWrapping.NoWrap;
            meta.Margin = new Thickness(10, 0, 0, 0);
            meta.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(meta);
            stack.Children.Add(head);

            if (!string.IsNullOrWhiteSpace(e.Content))
            {
                var body = Selectable(e.Content);
                body.Margin = new Thickness(0, 4, 0, 0);
                body.TextWrapping = TextWrapping.Wrap;
                body.MaxHeight = 44;
                stack.Children.Add(body);
            }

            var border = new Border { Style = (Style)FindResource("CardBorder"), Child = stack };
            if (editable)
            {
                var actions = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 4, 0, 0),
                };
                actions.Children.Add(SmallButton(L.T("Common.Edit"), () => EditEntry(e)));
                actions.Children.Add(SmallButton(L.T("Common.Delete"), () => DeleteEntry(e)));
                stack.Children.Add(actions);
                border.Cursor = Cursors.Hand;
                border.ToolTip = L.T("Lore.EditEntryTip");
                border.MouseLeftButtonUp += (_, _) =>
                {
                    if (!SelectingText()) EditEntry(e);
                };
            }
            EntryPanel.Children.Add(border);
        }
        if (entries.Count == 0)
        {
            EntryPanel.Children.Add(Themed(new TextBlock
            {
                Text = L.T("Lore.NoEntries"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 12, 0, 0),
            }, TextBlock.ForegroundProperty, "MutedBrush"));
        }
    }

    /// <summary>后端的枚举值转成界面文字；没有对应译文时原样显示。</summary>
    private static string Translate(string prefix, string value)
    {
        var text = L.T(prefix + value);
        return text == prefix + value ? value : text;
    }

    /// <summary>脉络类型：内置的查译文，用户自定义的原样显示（不转小写）。</summary>
    private static string ThreadKindLabel(string kind)
    {
        var key = "ThreadKind." + kind.ToLowerInvariant();
        var text = L.T(key);
        return text == key ? kind : text;
    }

    /// <summary>规划中新建资料：稳定 ID 保存到设定，随后加入原章节；失败在原窗口重试。</summary>
    private async Task CreatePlanMemberAsync(bool character)
    {
        if (_project is null || !EditorsEditable || _busy) return;
        var projectId = _project.Id;
        await FlushEditsAsync();
        if (_project?.Id != projectId || AnyDirty || !EditorsEditable) return;
        var project = _project;
        int? chapterNo = _selected?.No;
        var originalChapter = _selected;
        var draft = PlanZoneView.Read().Copy();
        var lore = (_selected?.Lore ?? LoreDto.From(project)).Copy();
        bool Current() => _project?.Id == projectId &&
            (_selected?.No == chapterNo || ReferenceEquals(_selected, originalChapter));

        async Task JoinAsync(string id)
        {
            if (!Current()) throw new InvalidOperationException(L.T("Plan.ContextChanged"));
            var updated = draft.Copy();
            var ids = character ? updated.Present_Character_Ids : updated.Required_Threads;
            if (!ids.Contains(id, StringComparer.Ordinal)) ids.Add(id);
            if (chapterNo is int no) await App.Api.SavePlanAsync(projectId, no, updated);
            else
            {
                var created = await App.Api.CreateChapterAsync(projectId, "", "", updated);
                chapterNo = created.No;
            }
            if (Current()) await LoadProjectAsync(projectId, chapterNo);
        }

        if (character)
        {
            var dialog = new CharacterEditorWindow(null, project.Characters, project.Root_Path, chapterNo) { Owner = this };
            dialog.SaveHandler = async request =>
            {
                if (!Current()) return CharacterSaveResult.Fail(L.T("Plan.ContextChanged"));
                _busy = true; UpdateEditorState();
                try
                {
                    request.Chapter_No = chapterNo;
                    if (dialog.PendingPortraitSource is { } source)
                        request.Portrait = await App.Api.CopyPortraitAsync(projectId, request.Id, source);
                    await App.Api.SaveCharacterAsync(projectId, request);
                    await JoinAsync(request.Id);
                    return CharacterSaveResult.Success();
                }
                catch (Exception ex) { return CharacterSaveResult.Fail(ex.Message); }
                finally { _busy = false; UpdateEditorState(); }
            };
            dialog.ShowDialog();
        }
        else
        {
            var dialog = new ThreadEditorWindow(null) { Owner = this };
            dialog.SaveAsync = async result =>
            {
                if (!Current()) throw new InvalidOperationException(L.T("Plan.ContextChanged"));
                _busy = true; UpdateEditorState();
                try
                {
                    var index = lore.Threads.FindIndex(t => t.Id == result.Id);
                    if (index < 0) lore.Threads.Add(result); else lore.Threads[index] = result;
                    await App.Api.SaveLoreAsync(projectId, lore, chapterNo);
                    await JoinAsync(result.Id);
                }
                finally { _busy = false; UpdateEditorState(); }
            };
            dialog.ShowDialog();
        }
    }

    private async void EditCharacter(CharacterDto? existing, bool locateAction = false)
    {
        if (_project is null || _loreReadOnly || _busy) return;
        // 编辑时始终用作品主档里的最新公共资料；章节快照只提供当前章的名单与行动。
        var current = existing is null
            ? null
            : _project.Characters.FirstOrDefault(c => c.Id == existing.Id) ?? existing;
        var others = (_project.Characters.Count > 0 ? _project.Characters : CurrentLore().Characters)
            .Where(c => existing is null || c.Id != existing.Id)
            .ToList();
        var root = _project.Root_Missing ? "" : _project.Root_Path;
        var chapterNo = _loreChapter;
        var action = "";
        if (existing is not null && chapterNo is not null
            && _selected?.Character_Actions is { } actions
            && actions.TryGetValue(existing.Id, out var savedAction))
            action = savedAction;
        var projectId = _project.Id;
        var dlg = new CharacterEditorWindow(current, others, root, chapterNo, action) { Owner = this };
        if (locateAction) dlg.Loaded += (_, _) => dlg.ActionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        dlg.SaveHandler = async request =>
        {
            _busy = true;
            UpdateEditorState();
            try
            {
                if (dlg.PendingPortraitSource is { } source)
                    request.Portrait = await App.Api.CopyPortraitAsync(projectId, request.Id, source);
                await App.Api.SaveCharacterAsync(projectId, request);
                await LoadProjectAsync(projectId, chapterNo ?? _selected?.No);
                return CharacterSaveResult.Success();
            }
            catch (Exception ex)
            {
                // 窗口保持打开并可重试；这里不能吞异常后假装成功。
                return CharacterSaveResult.Fail(ex.Message);
            }
            finally
            {
                _busy = false;
                UpdateEditorState();
            }
        };
        dlg.ShowDialog();
    }

    private async void DeleteCharacter(CharacterDto c)
    {
        if (_project is null || _loreReadOnly || _busy) return;
        if (MessageBox.Show(this, L.T("Lore.DeleteCharacter", ScopeName(), c.Name), L.T("App.Title"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        var projectId = _project.Id;
        var chapterNo = _loreChapter;
        await Safe(async () =>
        {
            await App.Api.DeleteCharacterAsync(projectId, c.Id, chapterNo);
            await LoadProjectAsync(projectId, chapterNo ?? _selected?.No);
        });
    }

    private void EditThread(PlotThreadDto? existing)
    {
        if (_project is null || _loreReadOnly || _busy) return;
        var dlg = new ThreadEditorWindow(existing) { Owner = this };
        dlg.SaveAsync = result => SaveLoreAsync(l =>
        {
            var i = string.IsNullOrEmpty(result.Id) ? -1 : l.Threads.FindIndex(x => x.Id == result.Id);
            if (i >= 0) l.Threads[i] = result;
            else l.Threads.Add(result);
        });
        dlg.ShowDialog();
    }

    private async void DeleteThread(PlotThreadDto t)
    {
        if (MessageBox.Show(this, L.T("Lore.DeleteThread", ScopeName(), t.Name), L.T("App.Title"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        await Safe(() => SaveLoreAsync(l => l.Threads.RemoveAll(x => x.Id == t.Id)));
    }

    private async void EditEntry(LoreEntryDto? existing, string? sourceQuote = null)
    {
        if (_project is null || _loreReadOnly || _busy) return;
        var dlg = new LoreEntryEditorWindow(existing) { Owner = this };
        if (!string.IsNullOrEmpty(sourceQuote)) dlg.Loaded += (_, _) =>
        {
            var at = dlg.ContentBox.Text.IndexOf(sourceQuote, StringComparison.Ordinal);
            dlg.ContentBox.Focus(); if (at >= 0) dlg.ContentBox.Select(at, sourceQuote.Length);
        };
        if (dlg.ShowDialog() != true) return;
        var result = dlg.Result;
        await Safe(() => SaveLoreAsync(l =>
        {
            var i = string.IsNullOrEmpty(result.Id) ? -1 : l.Entries.FindIndex(x => x.Id == result.Id);
            if (i >= 0) l.Entries[i] = result;
            else l.Entries.Add(result);
        }));
    }

    private async void DeleteEntry(LoreEntryDto e)
    {
        if (MessageBox.Show(this, L.T("Lore.DeleteEntry", ScopeName(), e.Display), L.T("App.Title"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        await Safe(() => SaveLoreAsync(l => l.Entries.RemoveAll(x => x.Id == e.Id)));
    }

    private string ScopeName() => _loreChapter is int n ? L.T("Lore.ScopeChapter", n) : L.T("Lore.ScopeWork");

    private void AddCharacter_Click(object sender, RoutedEventArgs e) => EditCharacter(null);

    private void AddThread_Click(object sender, RoutedEventArgs e) => EditThread(null);

    private void AddEntry_Click(object sender, RoutedEventArgs e) => EditEntry(null);

    /// <summary>故障 / 错误用弹窗提示；成功不再往状态条写字。</summary>
    private void ReportError(string message) =>
        MessageBox.Show(this, message, L.T("App.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);

    /// <summary>先把未保存的编辑写回后端，避免随后的重新载入把它们覆盖掉。</summary>
    private async Task Safe(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        UpdateStackedReadOnly();
        UpdateProposalsReadOnly();
        UpdateEditorState();
        try
        {
            if (!await FlushEditsAsync() || !await FlushStackedAsync() || !await FlushProposalsAsync() || !await FlushBodyRevisionsAsync()) return;
            await action();
        }
        catch (RhythmMismatchException ex)
        {
            ShowRhythmMismatch(ex);
        }
        catch (Exception ex)
        {
            AiPanelView.RecordFailure();
            ReportError(ex.Message);
        }
        finally
        {
            _busy = false;
            UpdateEditorState();
        }
    }

    // ---------------------------------------------------------------- file menu

    private async void NewProject_Click(object sender, RoutedEventArgs e)
    {
        var defaultParent = "";
        try
        {
            defaultParent = (await App.Api.GetSettingsAsync()).Last_Parent_Dir;
        }
        catch { /* 后端不可用时仍允许手动填写 */ }
        if (string.IsNullOrWhiteSpace(defaultParent) || !Directory.Exists(defaultParent))
            defaultParent = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        var dlg = new NewProjectWindow(defaultParent) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        await Safe(async () =>
        {
            var p = await App.Api.CreateProjectAsync(dlg.ProjectTitle, dlg.OpeningBackground, dlg.Sample, dlg.ParentDir);
            await RefreshProjectsAsync();
            await LoadProjectAsync(p.Id, expand: true);
        });
    }

    private async void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        var path = FolderPicker.Pick(L.T("Open.PickFolder"), this);
        if (string.IsNullOrEmpty(path)) return;
        await Safe(async () =>
        {
            var p = await App.Api.OpenProjectAsync(path);
            await RefreshProjectsAsync();
            await LoadProjectAsync(p.Id, expand: true);
        });
    }

    /// <summary>「文件」菜单打开时按当前作品刷新可用项，并现场生成「最近打开」。</summary>
    private void FileMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender)) return;
        var work = ShellCommandState.CanUseWork(_project is not null);
        CloseWorkMenu.IsEnabled = work;
        ImportMenu.IsEnabled = work;
        ExportMenu.IsEnabled = work;
        BackupMenu.IsEnabled = work;
        OpenFolderMenu.IsEnabled = _project is { Root_Missing: false } p && !string.IsNullOrEmpty(p.Root_Path);
        CommandManager.InvalidateRequerySuggested();

        RecentMenu.Items.Clear();
        foreach (var id in _recentIds)
        {
            if (_works.FirstOrDefault(w => w.Project.Id == id) is not { } item) continue;
            var entry = new MenuItem { Header = new TextBlock { Text = item.Title }, Tag = id };
            entry.Click += RecentItem_Click;
            RecentMenu.Items.Add(entry);
        }
        if (RecentMenu.Items.Count == 0)
            RecentMenu.Items.Add(new MenuItem { Header = L.T("Menu.RecentEmpty"), IsEnabled = false });
    }

    private void EditMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender)) return;
        ApplyEditTarget();
    }

    private void ViewMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender)) return;
        SyncModeMenuChecks();
        FocusModeItem.IsChecked = _focusMode;
        UpdatePaperWidth();
        UsageMenu.InputGestureText = L.T(MenuMarks.Usage(UiPrefs.Current.UsageVisible).LabelKey);
        CommandManager.InvalidateRequerySuggested();
    }

    private async void RecentItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is string id) await Safe(() => LoadProjectAsync(id, expand: true));
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!ShellCommandState.CanUseWork(_project is not null)) return;
        if (IsDescendantOf(PlanZoneView, _editTarget as DependencyObject) || PlanZoneView.IsKeyboardFocusWithin)
            await PlanZoneView.SaveSectionAsync(PlanZoneView.SaveTarget);
        else
            await FlushEditsAsync();
        await FlushStackedAsync();
        // 保存失败时 SaveEditsAfter 已弹窗提示，并重新标记为待保存
    }

    private async void BackupMenu_Click(object sender, RoutedEventArgs e)
    {
        if (!ShellCommandState.CanUseWork(_project is not null)) return;
        await BackupCurrentWorkAsync();
    }

    private void ImportMenu_Click(object sender, RoutedEventArgs e)
    {
        if (!ShellCommandState.CanUseWork(_project is not null)) return;
        Import_Click(sender, e);
    }

    private async void CloseWork_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null) return;
        if (!await ConfirmMiscDraftAsync()) return;
        var pid = _project.Id;
        await Safe(() =>
        {
            CloseSideIfShowing(pid);
            _suppressTreeSelection = true;
            try
            {
                foreach (var w in _works)
                {
                    w.IsCurrent = false;
                    foreach (var c in w.Chapters) c.IsSelected = false;
                }
            }
            finally
            {
                _suppressTreeSelection = false;
            }
            ResetProjectView();
            return Task.CompletedTask;
        });
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_project is { Root_Missing: false } p && !string.IsNullOrEmpty(p.Root_Path)) ShowInExplorer(p.Root_Path);
    }

    /// <summary>把 TXT / Markdown 按「第X章」拆开，追加到当前作品末尾；拆不出章节的文件整篇作为一章。</summary>
    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null) { ReportError(L.T("Status.SelectWork")); return; }
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = L.T("Import.Title"),
            Filter = L.T("Import.Filter"),
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        List<ImportedChapter> chapters;
        try
        {
            chapters = dlg.FileNames.SelectMany(TextImport.Split).ToList();
        }
        catch (Exception ex)
        {
            ReportError(ex.Message);
            return;
        }
        if (chapters.Count == 0) { ReportError(L.T("Import.Empty")); return; }
        var preview = string.Join("\n", chapters.Take(6).Select(c => c.Heading));
        if (chapters.Count > 6) preview += "\n…";
        if (MessageBox.Show(this, L.T("Import.Confirm", chapters.Count, _project.Title, preview), L.T("Import.Title"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        var pid = _project.Id;
        await Safe(async () =>
        {
            var created = await App.Api.ImportChaptersAsync(pid, chapters.Select(c => (c.Title, c.Body)));
            CloseSideIfShowing(pid);
            await LoadProjectAsync(pid, created.FirstOrDefault()?.No, expand: true);
        });
    }

    // ---------------------------------------------------------------- shortcuts

    private TextBoxBase? _editTarget;

    /// <summary>Ctrl+N 新建作品，Ctrl+Shift+N 新建章节，Ctrl+, 打开设置。Esc 不再打开设置。F2 / Delete 仅目录树有焦点时生效。</summary>
    private async void PreviousChapter_Click(object sender, RoutedEventArgs e) => await GoAdjacentChapterAsync(-1);
    private async void NextChapter_Click(object sender, RoutedEventArgs e) => await GoAdjacentChapterAsync(1);

    private void SetupShortcuts()
    {
        var newWork = new RelayCommand(() => NewProject_Click(this, new RoutedEventArgs()), static () => true);
        var newChapter = new RelayCommand(() => _ = NewChapterAsync(null), () => ShellCommandState.CanUseWork(_project is not null));
        var save = new RelayCommand(() => Save_Click(this, new RoutedEventArgs()), () => ShellCommandState.CanUseWork(_project is not null));
        var previous = new RelayCommand(() => PreviousChapter_Click(this, new RoutedEventArgs()), () => CanStepChapter(-1));
        var next = new RelayCommand(() => NextChapter_Click(this, new RoutedEventArgs()), () => CanStepChapter(1));
        var settings = new RelayCommand(() => Settings_Click(this, new RoutedEventArgs()), static () => true);
        NewWorkMenu.Command = newWork;
        NewChapterMenu.Command = newChapter;
        SaveMenu.Command = save;
        PreviousChapterItem.Command = previous;
        NextChapterItem.Command = next;
        SettingsMenu.Command = settings;
        CloseWorkMenu.Click += CloseWork_Click;
        OpenFolderMenu.Click += OpenFolder_Click;
        ImportMenu.Click += ImportMenu_Click;
        BackupMenu.Click += BackupMenu_Click;
        InputBindings.Add(new KeyBinding(newWork, Key.N, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(newChapter, Key.N, ModifierKeys.Control | ModifierKeys.Shift));
        InputBindings.Add(new KeyBinding(settings, Key.OemComma, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => OpenProject_Click(this, new RoutedEventArgs()), static () => true), Key.O, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(save, Key.S, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(previous, Key.PageUp, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(next, Key.PageDown, ModifierKeys.Control));
        PreviewGotKeyboardFocus += (_, e) =>
        {
            var box = e.NewFocus as TextBoxBase;
            _editTarget = EditTargetState.Remember(_editTarget, box, box is not null && IsDescendantOf(MainMenu, box));
            ApplyEditTarget();
        };
        // Esc：先关菜单和浮层，再退出专注。不打开设置。
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || Keyboard.Modifiers != ModifierKeys.None) return;
            if (CloseOpenMenus()) { e.Handled = true; return; }
            if (FormatBar.CloseOverlays() || (_poppedBar?.CloseOverlays() ?? false))
            {
                e.Handled = true;
                return;
            }
            if (ReviewVisible && ReaderView.CloseSettings()) { e.Handled = true; return; }
            if (!_focusMode) return;
            e.Handled = true;
            ExitFocusMode();
        };
        WorkTree.InputBindings.Add(new KeyBinding(new RelayCommand(() =>
        {
            switch (FocusedTreeNode())
            {
                case ChapterNode c: _ = RenameChapterAsync(c); break;
                case WorkNode { IsMissing: false } w: _ = RenameWorkAsync(w); break;
            }
        }, static () => true), Key.F2, ModifierKeys.None));
        WorkTree.InputBindings.Add(new KeyBinding(new RelayCommand(() =>
        {
            if (FocusedTreeNode() is ChapterNode c) _ = DeleteChapterAsync(c);
        }, static () => true), Key.Delete, ModifierKeys.None));
    }

    private bool CanStepChapter(int delta) =>
        ShellCommandState.CanStep(LiveChapterNos(), _selected?.No, delta);

    private List<int> LiveChapterNos() =>
        _chapters.Where(c => c.Status != "superseded").OrderBy(c => c.No).Select(c => c.No).ToList();

    private void ApplyEditTarget()
    {
        IInputElement? target = _editTarget;
        foreach (var item in new[] { UndoItem, RedoItem, CutItem, CopyItem, PasteItem, SelectAllItem })
            item.CommandTarget = target;
    }

    private bool CloseOpenMenus()
    {
        var closed = false;
        foreach (var item in MainMenu.Items.OfType<MenuItem>())
            if (item.IsSubmenuOpen) { item.IsSubmenuOpen = false; closed = true; }
        return closed;
    }

    private static bool IsDescendantOf(DependencyObject? root, DependencyObject? node)
    {
        if (root is null || node is null) return false;
        for (DependencyObject? n = node; n is not null; n = n is Visual or System.Windows.Media.Media3D.Visual3D
                 ? VisualTreeHelper.GetParent(n) : LogicalTreeHelper.GetParent(n))
            if (ReferenceEquals(n, root)) return true;
        return false;
    }

    /// <summary>Ctrl+PageUp 上一章 / Ctrl+PageDown 下一章；末章再前进则提示。</summary>
    private async Task GoAdjacentChapterAsync(int delta)
    {
        if (_project is null) { ReportError(L.T("Status.SelectWork")); return; }
        if (_selected is null) { ReportError(L.T("Status.SelectChapter")); return; }
        var live = _chapters.Where(c => c.Status != "superseded").OrderBy(c => c.No).ToList();
        var i = live.FindIndex(c => c.No == _selected.No);
        if (i < 0) return;
        var next = i + delta;
        if (next < 0) return;
        if (next >= live.Count)
        {
            ReportError(L.T("Status.LastChapter"));
            return;
        }
        var no = live[next].No;
        var pid = _project.Id;
        await Safe(() => LoadProjectAsync(pid, no));
    }

    /// <summary>在指定作品末尾新建空白章并打开；未指定作品时用当前作品。</summary>
    private async Task NewChapterAsync(string? projectId)
    {
        var pid = projectId ?? _project?.Id;
        if (pid is null) { ReportError(L.T("Status.SelectWork")); return; }
        await Safe(async () =>
        {
            var ch = await App.Api.CreateChapterAsync(pid, "", "");
            await LoadProjectAsync(pid, ch.No, expand: true);
            if (!IsPureMode) ShowOnly(Zone.Plan);
        });
    }

    /// <summary>目录树里有键盘焦点的节点；作品节点点击时不会被选中，所以先看焦点再看选中项。</summary>
    private object? FocusedTreeNode() =>
        (Keyboard.FocusedElement as FrameworkElement)?.DataContext is { } dc and (WorkNode or ChapterNode)
            ? dc
            : WorkTree.SelectedItem;

    // ---------------------------------------------------------------- work context menu

    private static T? NodeOf<T>(object sender) where T : class =>
        (sender as FrameworkElement)?.DataContext as T;

    private async void WorkOpen_Click(object sender, RoutedEventArgs e)
    {
        if (NodeOf<WorkNode>(sender) is not { } node) return;
        await Safe(() => LoadProjectAsync(node.Project.Id, expand: true));
    }

    private async Task<List<ChapterDto>> ChaptersOfAsync(WorkNode node) =>
        _project?.Id == node.Project.Id ? _chapters : await App.Api.ListChaptersAsync(node.Project.Id);

    private async void WorkSide_Click(object sender, RoutedEventArgs e)
    {
        if (NodeOf<WorkNode>(sender) is not { } node) return;
        await Safe(async () =>
        {
            var last = (await ChaptersOfAsync(node)).Where(c => c.Status != "superseded").MaxBy(c => c.No);
            if (last is null)
            {
                ReportError(L.T("Work.NoChapters", node.Title));
                return;
            }
            OpenSide(node.Project.Id, node.Title, last);
        });
    }

    private async void WorkBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (NodeOf<WorkNode>(sender) is not { } node) return;
        await Safe(async () => HtmlPreview.OpenWork(node.Title, await ChaptersOfAsync(node)));
    }

    private async void WorkNewChapter_Click(object sender, RoutedEventArgs e)
    {
        if (NodeOf<WorkNode>(sender) is not { } node) return;
        await NewChapterAsync(node.Project.Id);
    }

    private void WorkExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (NodeOf<WorkNode>(sender) is { HasFolder: true } node) ShowInExplorer(node.Project.Root_Path);
    }

    private void WorkCopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (NodeOf<WorkNode>(sender) is not { HasFolder: true } node) return;
        TrySetClipboard(node.Project.Root_Path);
    }

    private async void WorkInfo_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _storingFigure || NodeOf<WorkNode>(sender) is not { } node) return;
        await Safe(async () =>
        {
            await FlushEditsAsync();
            var pid = node.Project.Id;
            var no = _project?.Id == pid ? _selected?.No : null;
            var project = await App.Api.GetProjectAsync(pid);
            var chapters = await App.Api.ListChaptersAsync(pid);
            var window = new WorkInfoWindow(project, chapters) { Owner = this };
            window.ShowDialog();
            if (!window.Updated) return;
            await RefreshProjectsAsync();
            if (_project?.Id == pid) await LoadProjectAsync(pid, no);
        });
    }

    private async void WorkExport_Click(object sender, RoutedEventArgs e)
    {
        if (NodeOf<WorkNode>(sender) is not { } node || (sender as FrameworkElement)?.Tag is not string fmt) return;
        await ExportWorkAsync(node.Project.Id, fmt);
    }

    private async void WorkBackup_Click(object sender, RoutedEventArgs e)
    {
        if (NodeOf<WorkNode>(sender) is not { HasFolder: true } node) return;
        await BackupWorkAsync(node.Project.Id, saveFirst: _project?.Id == node.Project.Id);
    }

    /// <summary>工具台：备份当前作品，先保存再备份；切作品后放弃。</summary>
    private async Task BackupCurrentWorkAsync()
    {
        if (_project is null || _project.Root_Missing || string.IsNullOrWhiteSpace(_project.Root_Path)) return;
        await BackupWorkAsync(_project.Id, saveFirst: true);
    }

    private async Task BackupWorkAsync(string projectId, bool saveFirst)
    {
        if (saveFirst && !await FlushEditsAsync()) return;
        if (saveFirst && _project?.Id != projectId) return;
        await Safe(async () =>
        {
            var path = await App.Api.BackupAsync(projectId);
            ShowInExplorer(path);
        });
    }

    /// <summary>文件存在时在资源管理器里选中它，否则打开它（或它所在的）文件夹。</summary>
    private void ShowInExplorer(string path)
    {
        try
        {
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else
            {
                var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) { ReportError(L.T("Main.RootMissing", path)); return; }
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            ReportError(ex.Message);
        }
    }

    /// <summary>剪贴板被别的程序占用时会抛异常，改为弹窗提示。</summary>
    private bool TrySetClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (Exception ex)
        {
            ReportError(ex.Message);
            return false;
        }
    }

    // ---------------------------------------------------------------- 发布到其他平台

    /// <summary>工具栏分享按钮与功能区「发布到第三方」共用：先保存当前章，保存成功再打开发布窗口。</summary>
    private bool _openingPublish;

    private async Task OpenPublishAsync()
    {
        if (_openingPublish) return;
        if (_project is null) { ReportError(L.T("Status.SelectExport")); return; }
        _openingPublish = true;
        try
        {
            await FlushEditsAsync();
            // 保存失败时脏标记会被恢复，此时不能带着旧正文去发布
            if (AnyDirty) { ReportError(L.T("Publish.SaveFirst")); return; }
            // 等保存期间作品可能已被关闭
            if (_project is not { } project) { ReportError(L.T("Status.SelectExport")); return; }
            new PublishWindow(App.Api, project, _selected) { Owner = this }.ShowDialog();
        }
        finally { _openingPublish = false; }
    }

    /// <summary>功能区的动作入口（ToolsPanel.ActionRequested）。</summary>
    private async void ToolsPanel_ActionRequested(object? sender, string action)
    {
        switch (action)
        {
            case ToolRules.Stats:
                OpenChapterStats();
                break;
            case ToolRules.Copy:
                CopyCurrentChapter();
                break;
            case ToolRules.ExportChapter:
                await ExportCurrentChapterAsync();
                break;
            case ToolRules.Backup:
                await BackupCurrentWorkAsync();
                break;
            case ToolRules.Publish:
                await OpenPublishAsync();
                break;
            case ToolRules.Versions:
                await OpenVersionsWindowAsync();
                break;
            case ToolRules.FindChapter:
                await OpenFindPanelAsync(bookScope: false);
                break;
            case ToolRules.FindBook:
                await OpenFindPanelAsync(bookScope: true);
                break;
            case ToolRules.Sensitive:
                OpenSensitiveWordsWindow();
                break;
        }
    }

    /// <summary>把当前章节/编辑器状态同步给工具台；只更新启用与状态，不重建列表。</summary>
    private void SyncToolsContext()
    {
        if (ToolsPanelView is null) return;
        var hasWork = _project is not null;
        var hasChapter = _selected is not null;
        var hasBody = hasChapter && (_bodyDirty || !string.IsNullOrWhiteSpace(_selected!.Body_Text));
        var hasFolder = hasWork && !_project!.Root_Missing && !string.IsNullOrWhiteSpace(_project.Root_Path);
        var context = new ToolContext(
            HasWork: hasWork,
            HasChapter: hasChapter,
            HasBody: hasBody,
            Editable: EditorsEditable,
            CanComment: EditorsEditable && hasChapter && !BodyEditor.Selection.IsEmpty,
            HasFolder: hasFolder,
            Busy: _busy);
        ToolsPanelView.SetContext(hasChapter ? _selected!.Title : null, context);
    }

    private void SetSensitiveState(SensitiveScanState state)
    {
        _sensitiveState = state;
        ToolsPanelView.SetSensitive(state);
    }

    private void MarkSensitiveStale()
    {
        if (_sensitiveState is SensitiveScanState.Found or SensitiveScanState.Clean)
            SetSensitiveState(SensitiveScanState.Stale);
    }

    /// <summary>非模态统计窗：读实时正文，不保存、不建历史；切章时关闭。</summary>
    private void OpenChapterStats()
    {
        if (_project is null || _selected is null) return;
        var projectId = _project.Id;
        var chapterNo = _selected.No;
        _statsWindow?.Close();
        var window = new ChapterStatsWindow(
            () => _project?.Id == projectId && _selected?.No == chapterNo ? _selected.Title : null,
            () => ChapterStats.From(
                RichBody.LiveProse(BodyEditor.Document),
                BodyReview.StripMarkers(BodyEditor.Selection.Text)))
        {
            Owner = this,
        };
        window.Closed += (_, _) => { if (_statsWindow == window) _statsWindow = null; };
        _statsWindow = window;
        window.Show();
    }

    /// <summary>复制当前章纯正文，包含尚未保存的编辑；不含标题、插图与修订标记。</summary>
    private void CopyCurrentChapter()
    {
        if (_selected is null) return;
        var text = RichBody.LiveProse(BodyEditor.Document);
        if (string.IsNullOrWhiteSpace(text))
        {
            ReportError(L.T("Chapter.NoText", HtmlPreview.Heading(_selected)));
            return;
        }
        if (TrySetClipboard(text.Replace("\r\n", "\n").Replace("\n", "\r\n")))
            ToolsPanelView.ShowCopied();
    }

    /// <summary>打开左栏查找面板，并把当前未保存正文带过去。</summary>
    private async Task OpenFindPanelAsync(bool bookScope)
    {
        if (_project is null) { ReportError(L.T("Status.SelectWork")); return; }
        await FlushEditsAsync();
        ShowLeftTab(LeftTab.Find);
        SetSidebarVisible(true);
        if (_selected is not null)
            FindPanelView.SetDraft(_selected.No, RichBody.PlainText(BodyEditor.Document));
        FindPanelView.FocusScope(bookScope);
    }

    /// <summary>先把未保存的编辑发出去：否则恢复后重载章节时，旧正文会被 LoadEditors 再存回去盖掉恢复结果。</summary>
    private async Task OpenVersionsWindowAsync()
    {
        if (_project is null || _selected is null) { ReportError(L.T("Status.SelectChapter")); return; }
        await FlushEditsAsync();
        if (AnyDirty) { ReportError(L.T("Versions.SaveFirst")); return; }
        if (_project is null || _selected is null) { ReportError(L.T("Status.SelectChapter")); return; }
        var pid = _project.Id;
        var no = _selected.No;
        VersionsWindow.Show(this, pid, no, CurrentWorkTitle(), async restoredNo =>
        {
            _saveTimer.Stop();
            _bodyDirty = _planDirty = _reviewDirty = false;
            await LoadProjectAsync(pid, restoredNo);
        });
    }

    private SensitiveWordsWindow? _sensitiveWindow;

    /// <summary>非模态：点结果后主窗口能真正把焦点和选区放到正文里，用户可以逐条看。</summary>
    private void OpenSensitiveWordsWindow()
    {
        if (_project is null) { ReportError(L.T("Status.SelectWork")); return; }
        if (_sensitiveWindow is not null) { _sensitiveWindow.Activate(); return; }

        SetSensitiveState(SensitiveScanState.Scanning);
        var window = new SensitiveWordsWindow(
            () => _chapters,
            chapterNo => _selected is { } shown && shown.No == chapterNo
                ? (shown.No, RichBody.PlainText(BodyEditor.Document))
                : null,
            hit => _ = LocateOffsetAsync(hit),
            _selected?.No ?? 0) { Owner = this };

        void Started(object? _, EventArgs __) => SetSensitiveState(SensitiveScanState.Scanning);
        void Finished(object? _, SensitiveScanResult result) => SetSensitiveState(result.Outcome switch
        {
            SensitiveScanOutcome.Found => SensitiveScanState.Found,
            SensitiveScanOutcome.Clean => SensitiveScanState.Clean,
            _ => SensitiveScanState.NotRun,
        });

        window.ScanStarted += Started;
        window.ScanFinished += Finished;
        window.Closed += (_, _) =>
        {
            window.ScanStarted -= Started;
            window.ScanFinished -= Finished;
            if (_sensitiveWindow == window) _sensitiveWindow = null;
            // 没等到结果就关窗：不要停在「检测中」。
            if (_sensitiveState == SensitiveScanState.Scanning) SetSensitiveState(SensitiveScanState.NotRun);
        };
        _sensitiveWindow = window;
        window.Show();
    }

    private string CurrentWorkTitle() =>
        _works.FirstOrDefault(w => w.Project.Id == _project?.Id)?.Title ?? _project?.Title ?? "";

    /// <summary>敏感词窗口点结果：保存当前章 → 切章 → 在正文里按词复核后选中。
    /// 偏移来自已保存正文或纯文本，和编辑器的字符计数不完全一致，只作为「找最近一处」的参考。</summary>
    private async Task LocateOffsetAsync(SensitiveHit hit)
    {
        if (_project is null) return;
        await FlushEditsAsync();
        if (_project is null) return;
        if (_selected?.No != hit.ChapterNo)
            await Safe(() => LoadProjectAsync(_project.Id, hit.ChapterNo));
        if (_selected?.No != hit.ChapterNo) return;
        ShowOnlyKeepAudit(Zone.Body);
        // 等正文列重新布局后再选中，避免 ScrollToVerticalOffset 用旧高度
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (_selected?.No != hit.ChapterNo) return;
            if (!BodyEditing.TryLocate(BodyEditor, hit.Word, hit.Offset))
                ReportError(L.T("Main.LocateStale"));
        }));
    }

    // ---------------------------------------------------------------- 笔记 / 查找面板挂钩

    /// <summary>保存独立笔记；保存失败时阻止丢弃草稿。</summary>
    private async Task<bool> FlushNotesAsync()
    {
        try
        {
            await NotesPanelView.FlushAsync();
            return !NotesPanelView.HasUnsavedDraft;
        }
        catch (Exception ex)
        {
            ReportError(ex.Message);
            return false;
        }
    }

    /// <summary>AI 创作与个人笔记独立，不提炼或读取笔记。</summary>
    private async Task<bool> PrepareAiAsync() => await FlushEditsAsync() && await FlushStackedAsync() && await FlushProposalsAsync() && await FlushBodyRevisionsAsync();

    private void ShowTransientStatus(string message) => FindPanelView.ShowMessage(message);

    /// <summary>查找面板点结果：先保存当前章，再切章，最后在正文里复核并选中。</summary>
    private async Task LocateFindHitAsync(FindHit hit)
    {
        if (_project is null) return;
        await FlushEditsAsync();
        if (_selected?.No != hit.ChapterNo)
            await Safe(() => LoadProjectAsync(_project.Id, hit.ChapterNo));
        if (_selected?.No != hit.ChapterNo) return;
        ShowOnlyKeepAudit(Zone.Body);
        if (!BodyEditing.TryLocate(BodyEditor, hit.Query, hit.Offset))
            ShowTransientStatus(L.T("Main.LocateStale"));
        FindPanelView.SetDraft(hit.ChapterNo, RichBody.PlainText(BodyEditor.Document));
    }

    /// <summary>查找面板的替换：本章当前命中 / 全部替换。面板只发请求，正文由外壳改。</summary>
    private async Task ReplaceInChapterAsync(FindReplaceRequest request)
    {
        if (_project is null || _selected is null || !EditorsEditable)
        {
            ShowTransientStatus(L.T("Status.SelectChapter"));
            return;
        }
        var projectId = _project.Id;
        await FlushEditsAsync();
        if (_project?.Id != projectId || _selected is null || !EditorsEditable ||
            request.ChapterNo != _selected.No || request.SourceText != RichBody.PlainText(BodyEditor.Document))
        {
            FindPanelView.ShowMessage(L.T("Main.LocateStale"));
            return;
        }
        var replaced = 0;
        if (request.SelectedOffsets is { Count: > 0 } offsets)
        {
            replaced = BodyEditing.ReplaceSelected(BodyEditor, request.Query, request.Replacement,
                request.SourceText, offsets, request.CaseSensitive);
            if (replaced < 0) FindPanelView.ShowMessage(L.T("Main.LocateStale"));
        }
        else if (request.ReplaceAll)
        {
            replaced = BodyEditing.ReplaceAll(BodyEditor, request.Query, request.Replacement, request.CaseSensitive);
        }
        else if (BodyEditor.Selection.Text.Length > 0 &&
                 string.Equals(BodyEditor.Selection.Text, request.Query, request.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
        {
            BodyEditor.Selection.Text = request.Replacement;
            replaced = 1;
        }
        else if (BodyEditing.TryLocate(BodyEditor, request.Query, 0, request.CaseSensitive) &&
                 string.Equals(BodyEditor.Selection.Text, request.Query, request.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
        {
            BodyEditor.Selection.Text = request.Replacement;
            replaced = 1;
        }
        if (replaced > 0)
        {
            _bodyDirty = true;
            RestartSaveTimer();
            FindPanelView.SetDraft(_selected.No, RichBody.PlainText(BodyEditor.Document));
        }
    }

    /// <summary>
    /// 发布窗口把整理好的正文交回剪贴板：纯文本一定有；HTML（给富文本编辑器）与
    /// RTF（给 Word / WPS）有来源就一起放。RTF 只有「本章」才有——它取的是写作面当前文档。
    /// </summary>
    public void PublishClipboard(string plain, string html, bool withRtf)
    {
        try
        {
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, plain);
            if (!string.IsNullOrEmpty(html))
                data.SetData(DataFormats.Html, ClipboardPayload.BuildCfHtml(html));
            if (withRtf && TryRtf(out var rtf))
                data.SetData(DataFormats.Rtf, rtf);
            Clipboard.SetDataObject(data, true);  // true：程序退出后剪贴板内容依然在
        }
        catch (Exception ex)
        {
            ReportError(ex.Message);
        }
    }

    private bool TryRtf(out string rtf)
    {
        rtf = "";
        try
        {
            var range = new TextRange(BodyEditor.Document.ContentStart, BodyEditor.Document.ContentEnd);
            using var ms = new MemoryStream();
            range.Save(ms, DataFormats.Rtf);
            rtf = Encoding.UTF8.GetString(ms.ToArray());
            return rtf.Length > 0;
        }
        catch (Exception)
        {
            return false;  // 拿不到富文本不是错误：纯文本那一路照常可用
        }
    }

    private async void WorkRename_Click(object sender, RoutedEventArgs e)
    {
        if (NodeOf<WorkNode>(sender) is { } node) await RenameWorkAsync(node);
    }

    private async Task RenameWorkAsync(WorkNode node)
    {
        var dlg = new RenameWindow(L.T("Rename.WorkTitle"), L.T("Rename.WorkPrompt"), node.Title,
            string.IsNullOrEmpty(node.Project.Root_Path) ? "" : L.T("Rename.WorkHint")) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Value == node.Title) return;
        var name = dlg.Value;
        await Safe(async () =>
        {
            await App.Api.RenameProjectAsync(node.Project.Id, name);
            await RefreshProjectsAsync();
            if (_project?.Id == node.Project.Id) await LoadProjectAsync(node.Project.Id);
        });
    }

    private async void WorkDelete_Click(object sender, RoutedEventArgs e)
    {
        if (NodeOf<WorkNode>(sender) is not { } node) return;
        var root = node.Project.Root_Path;
        var where = string.IsNullOrEmpty(root)
            ? L.T("Delete.WorkLegacy")
            : L.T("Delete.WorkFolder", root);
        if (MessageBox.Show(this, L.T("Delete.WorkConfirm", node.Title, where), L.T("Delete.WorkTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        if (MessageBox.Show(this,
                L.T("Delete.WorkConfirm2", node.Title),
                L.T("Delete.WorkTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        await Safe(async () =>
        {
            if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
                RecycleBin.Send(root);
            await RemoveWorkAsync(node);
        });
    }

    private async void WorkRemove_Click(object sender, RoutedEventArgs e)
    {
        if (NodeOf<WorkNode>(sender) is not { } node) return;
        if (MessageBox.Show(this, L.T("Remove.WorkConfirm", node.Title),
                L.T("App.Title"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        await Safe(() => RemoveWorkAsync(node));
    }

    private async Task RemoveWorkAsync(WorkNode node)
    {
        if (!await CloseStackedForProjectAsync(node.Project.Id)) return;
        await App.Api.DeleteProjectAsync(node.Project.Id);
        _recentIds.Remove(node.Project.Id);
        CloseSideIfShowing(node.Project.Id);
        if (_project?.Id == node.Project.Id) ResetProjectView();
        await RefreshProjectsAsync();
    }

    // ---------------------------------------------------------------- chapter context menu

    private async void ChapterOpen_Click(object sender, RoutedEventArgs e)
    {
        if (NodeOf<ChapterNode>(sender) is not { } node) return;
        node.Work.IsExpanded = true;
        if (_project?.Id == node.Work.Project.Id)
        {
            node.IsSelected = true;
            ShowChapter(node.Chapter);
            return;
        }
        var no = node.Chapter.No;
        await Safe(() => LoadProjectAsync(node.Work.Project.Id, no));
    }

    private void ChapterSide_Click(object sender, RoutedEventArgs e)
    {
        if (NodeOf<ChapterNode>(sender) is not { } node) return;
        OpenSide(node.Work.Project.Id, node.Work.Title, node.Chapter);
    }

    private async void ChapterBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (NodeOf<ChapterNode>(sender) is not { } node) return;
        await Safe(() =>
        {
            HtmlPreview.OpenChapter(node.Work.Title, node.Chapter);
            return Task.CompletedTask;
        });
    }

    private async void ChapterInsert_Click(object sender, RoutedEventArgs e)
    {
        if (NodeOf<ChapterNode>(sender) is not { IsSuperseded: false } node) return;
        var pid = node.Work.Project.Id;
        var no = node.Chapter.No;
        if (!await CloseStackedForProjectAsync(pid)) return;
        await Safe(async () =>
        {
            var created = await App.Api.InsertChapterAfterAsync(pid, no);
            CloseSideIfShowing(pid);
            await LoadProjectAsync(pid, created.No, expand: true);
            if (!IsPureMode) ShowOnly(Zone.Plan);
        });
    }

    private async void ChapterExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (NodeOf<ChapterNode>(sender) is not { } node) return;
        var pid = node.Work.Project.Id;
        var no = node.Chapter.No;
        await Safe(async () => ShowInExplorer(await App.Api.ChapterFileAsync(pid, no)));
    }

    private async void ChapterCopyText_Click(object sender, RoutedEventArgs e)
    {
        if (NodeOf<ChapterNode>(sender) is not { } node) return;
        await FlushEditsAsync();
        var ch = node.Chapter;
        if (string.IsNullOrWhiteSpace(ch.Body_Text)) { ReportError(L.T("Chapter.NoText", HtmlPreview.Heading(ch))); return; }
        var text = ch.Body_Text.Replace("\r\n", "\n").Replace("\n", "\r\n");
        TrySetClipboard(text);
    }

    /// <summary>导出单章在客户端直接写文件：.md 为「# 标题」加正文，.txt 为纯文本（带 BOM，记事本不乱码）。</summary>
    private async void ChapterExport_Click(object sender, RoutedEventArgs e)
    {
        if (NodeOf<ChapterNode>(sender) is not { } node) return;
        if (!await FlushEditsAsync()) return;
        await ExportChapterAsync(node.Chapter, node.Work.Title,
            node.Work.HasFolder ? node.Work.Project.Root_Path : null);
    }

    /// <summary>工具台：导出当前章，先保存并在切章后放弃。</summary>
    private async Task ExportCurrentChapterAsync()
    {
        if (_project is null || _selected is null) return;
        var pid = _project.Id;
        var no = _selected.No;
        var title = CurrentWorkTitle();
        var root = _project.Root_Path;
        if (!await FlushEditsAsync()) return;
        if (_project?.Id != pid || _selected?.No != no) return;
        await ExportChapterAsync(_selected, title, root);
    }

    private Task ExportChapterAsync(ChapterDto ch, string workTitle, string? rootPath)
    {
        var heading = HtmlPreview.Heading(ch);
        var invalid = Path.GetInvalidFileNameChars();
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = L.T("Chapter.ExportTitle"),
            Filter = L.T("Chapter.ExportFilter"),
            FileName = new string($"{workTitle} - {heading}".Select(c => invalid.Contains(c) ? '_' : c).ToArray()),
            DefaultExt = ".md",
            AddExtension = true,
        };
        if (!string.IsNullOrEmpty(rootPath) && Directory.Exists(rootPath)) dlg.InitialDirectory = rootPath;
        if (dlg.ShowDialog(this) != true) return Task.CompletedTask;
        var paragraphs = (ch.Body_Text ?? "").Replace("\r\n", "\n").Split('\n')
            .Select(l => l.TrimEnd()).Where(l => l.Trim().Length > 0);
        var txt = Path.GetExtension(dlg.FileName).Equals(".txt", StringComparison.OrdinalIgnoreCase);
        var content = (txt ? heading : $"# {heading}") + "\n\n" + string.Join("\n\n", paragraphs) + "\n";
        try
        {
            if (txt) File.WriteAllText(dlg.FileName, content.Replace("\n", "\r\n"), new UTF8Encoding(true));
            else File.WriteAllText(dlg.FileName, content, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            ReportError(ex.Message);
        }
        return Task.CompletedTask;
    }

    private async void ChapterUnfinalize_Click(object sender, RoutedEventArgs e)
    {
        if (NodeOf<ChapterNode>(sender) is not { IsFinal: true } node) return;
        var ch = node.Chapter;
        if (MessageBox.Show(this, L.T("Chapter.UnfinalizeConfirm", HtmlPreview.Heading(ch)),
                L.T("Menu.Unfinalize"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        var pid = node.Work.Project.Id;
        await Safe(async () =>
        {
            await App.Api.UnfinalizeAsync(pid, ch.No);
            await LoadProjectAsync(pid, ch.No);
        });
    }

    private async void ChapterRename_Click(object sender, RoutedEventArgs e)
    {
        if (NodeOf<ChapterNode>(sender) is { } node) await RenameChapterAsync(node);
    }

    private async Task RenameChapterAsync(ChapterNode node)
    {
        var ch = node.Chapter;
        var hint = string.IsNullOrEmpty(node.Work.Project.Root_Path) ? "" : L.T("Rename.ChapterHint");
        var dlg = new RenameWindow(L.T("Rename.ChapterTitle"), L.T("Rename.ChapterPrompt", ch.No), ch.Title, hint) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Value == ch.Title) return;
        var title = dlg.Value;
        var pid = node.Work.Project.Id;
        await Safe(async () =>
        {
            await App.Api.RenameChapterAsync(pid, ch.No, title);
            await LoadProjectAsync(pid, ch.No);
        });
    }

    private async void ChapterClear_Click(object sender, RoutedEventArgs e)
    {
        if (NodeOf<ChapterNode>(sender) is not { } node) return;
        var ch = node.Chapter;
        if (MessageBox.Show(this,
                L.T("Chapter.ClearConfirm", HtmlPreview.Heading(ch)),
                L.T("Menu.ClearContent"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        var pid = node.Work.Project.Id;
        await Safe(async () =>
        {
            await App.Api.ClearChapterAsync(pid, ch.No);
            CloseSideIfShowing(pid);
            await LoadProjectAsync(pid, ch.No);
        });
    }

    private async void ChapterDelete_Click(object sender, RoutedEventArgs e)
    {
        if (NodeOf<ChapterNode>(sender) is { } node) await DeleteChapterAsync(node);
    }

    private async Task DeleteChapterAsync(ChapterNode node)
    {
        var ch = node.Chapter;
        if (MessageBox.Show(this,
                L.T("Chapter.DeleteConfirm", HtmlPreview.Heading(ch)),
                L.T("Menu.DeleteChapter"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        var pid = node.Work.Project.Id;
        if (!await CloseStackedForProjectAsync(pid)) return;
        await Safe(async () =>
        {
            await App.Api.DeleteChapterAsync(pid, ch.No);
            CloseSideIfShowing(pid);
            await LoadProjectAsync(pid, ch.No);
        });
    }

    private async void ChapterRollback_Click(object sender, RoutedEventArgs e)
    {
        if (NodeOf<ChapterNode>(sender) is not { } node) return;
        var ch = node.Chapter;
        if (MessageBox.Show(this,
                L.T("Chapter.RollbackConfirm", HtmlPreview.Heading(ch)),
                L.T("Chapter.RollbackTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        var pid = node.Work.Project.Id;
        await Safe(async () =>
        {
            await App.Api.RollbackAsync(pid, ch.No);
            CloseSideIfShowing(pid);
            await LoadProjectAsync(pid, ch.No, expand: true);
        });
    }

    // ---------------------------------------------------------------- chapter workflow

    private bool BodyHasText => !string.IsNullOrWhiteSpace(RichBody.PlainText(BodyEditor.Document));

    private ChapterDto? PreviousChapter() => _selected is null
        ? null
        : _chapters.Where(c => c.No < _selected.No && c.Status != "superseded").MaxBy(c => c.No);

    /// <summary>把当前章节/阶段状态推给 AI 面板，取代原底部步骤按钮的可用状态。</summary>
    private void UpdateStepButtons()
    {
        var hasChapter = _selected is not null;
        var prev = PreviousChapter();
        var latest = _chapters.Where(c => c.Status != "superseded").Select(c => c.No).DefaultIfEmpty(0).Max();
        AiPanelView.SetChapterState(
            hasChapter,
            hasChapter && prev is null,
            hasChapter && _selected!.No >= latest,
            hasChapter && BodyHasText,
            _selected?.Status == "final",
            !EditorsEditable);
        AiPanelView.SetWorkflow(_selected);
        _ = RefreshProposalStateAsync();
    }

    /// <param name="busyKey">忙碌文案的 L 资源键（如 "Busy.Generating"）；null 表示不显示忙碌文字。</param>
    private async Task RunStep(string? busyKey, Func<Task> action)
    {
        if (_busy) return;
        if (busyKey is not null) ShowBusyHint(L.T(busyKey));
        // 忙碌进度统一显示在 AI 面板；工具栏只保留左上角的忙碌提示
        AiPanelView.SetBusy(busyKey);
        SetUsageGenerating(true);
        BodyEditor.IsReadOnly = true;
        PlanZoneView.IsReadOnly = true;
        ReviewZoneView.IsReadOnly = true;
        LoreMiscView.IsReadOnly = true;
        try
        {
            await Safe(action);
        }
        finally
        {
            AiPanelView.SetBusy(null);
            SetUsageGenerating(false);
            HideBusyHint();
            UpdateEditorState();
            UpdateStepButtons();
        }
    }

    private void ShowBusyHint(string label)
    {
        FormatBar.ShowBusy(label);
        _poppedBar?.ShowBusy(label);
    }

    private void HideBusyHint()
    {
        FormatBar.HideBusy();
        _poppedBar?.HideBusy();
    }

    /// <summary>保留供后续 AI全流程模式接入：规划区 → 生成正文 → 正文区 → 定稿 → 评注区 → 提交评注并规划下一章。</summary>
    private async Task NextCoreAsync()
    {
        if (_project is null) { ReportError(L.T("Status.SelectWork")); return; }
        switch (ViewStage())
        {
            case Zone.Plan:
                if (BodyHasText) ShowOnly(Zone.Body);
                else await RunStep("Busy.Generating", GenerateCoreAsync);
                break;
            case Zone.Body:
                if (_selected?.Status == "final") ShowOnly(Zone.Review);
                else await RunStep("Busy.Finalizing", FinalizeCoreAsync);
                break;
            default:
                await SubmitReviewAsync();
                break;
        }
    }

    /// <summary>保留供后续 AI全流程模式接入。上一步只切换展示的区，不删除任何内容；在规划区时回到上一章的评注区。</summary>
    private async Task PrevCoreAsync()
    {
        switch (ViewStage())
        {
            case Zone.Review:
                ShowOnly(Zone.Body);
                break;
            case Zone.Body:
                ShowOnly(Zone.Plan);
                break;
            default:
                if (_project is null || PreviousChapter() is not { } prev) return;
                var pid = _project.Id;
                await Safe(() => LoadProjectAsync(pid, prev.No));
                if (_selected?.No == prev.No) ShowOnly(Zone.Review);
                break;
        }
    }

    /// <summary>生成正文：空白页先规划出新章；已有正文时 AI 接着写，原有格式保留、续写部分按普通段落追加。</summary>
    private async Task GenerateCoreAsync()
    {
        if (_project is null) return;
        var pid = _project.Id;
        // 已先保存各区，空白页上写过内容的话此时 _selected 已是新建的章节
        var current = _selected;
        if (current is null)
        {
            await App.Api.PlanAsync(pid);
        }
        else if (current.No != _project.Current_Chapter)
        {
            ReportError(L.T("Status.LatestOnly", _project.Current_Chapter));
            return;
        }
        var oldText = current?.Body_Text ?? "";
        var oldRich = current?.Body_Rich ?? "";

        var memoryPolicy = "extract";
        var targetNo = current?.No ?? _project.Current_Chapter;
        var history = await App.Api.ListChaptersAsync(pid);
        if (history.Any(c => c.No < targetNo && (c.Status is "final" or "drafted")
            && (c.Summary is null || c.Upstream_Changed.Count > 0 || (c.Summary_Status is "stale" or "pending"))))
        {
            var choice = MessageBox.Show(this, L.T("Memory.BeforeGenerate"), L.T("Memory.Title"),
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
            if (choice == MessageBoxResult.Cancel) return;
            memoryPolicy = choice == MessageBoxResult.Yes ? "extract" : "raw";
        }
        var ch = await App.Api.GenerateAsync(pid, memoryPolicy);
        var text = ch.Body_Text ?? "";
        if (oldText.Length > 0 && !string.IsNullOrEmpty(oldRich) && text.Length > oldText.Length
            && text.StartsWith(oldText, StringComparison.Ordinal))
        {
            var doc = RichBody.Build(oldText, oldRich, _project.Root_Path);
            RichBody.AppendText(doc, text[oldText.Length..]);
            await App.Api.SaveBodyAsync(pid, ch.No, text, RichBody.SaveXaml(doc));
        }
        await LoadProjectAsync(pid, ch.No);
    }

    private async Task FinalizeCoreAsync()
    {
        if (_project is null) return;
        if (_selected is null || string.IsNullOrWhiteSpace(_selected.Body_Text))
        {
            ReportError(L.T("Status.EmptyBody"));
            return;
        }
        var pid = _project.Id;
        var no = _selected.No;
        var finalized = await App.Api.FinalizeAsync(pid, no);
        if (_project?.Id != pid || _selected?.No != no) return;
        await LoadProjectAsync(pid, no);
        MessageBox.Show(this, L.T("Status.Finalized"), L.T("App.Title"), MessageBoxButton.OK, MessageBoxImage.Information);
        ShowSummaryWindow(pid, finalized);
    }

    private async Task ExtractCoreAsync()
    {
        if (_project is null || _selected is null) return;
        var pid = _project.Id; var chapter = _selected;
        var window = ShowSummaryWindow(pid, chapter, "extracting");
        try
        {
            var extracted = await App.Api.ExtractAsync(pid, chapter.No);
            if (_summaryWindow == window) window.ShowChapter(extracted);
            if (_project?.Id == pid && _selected?.No == chapter.No) await LoadProjectAsync(pid, chapter.No);
        }
        catch
        {
            if (_summaryWindow == window) window.ShowChapter(chapter, "failed");
            throw;
        }
    }

    private async Task AuditCoreAsync()
    {
        if (_project is null || _selected is null) return;
        if (string.IsNullOrWhiteSpace(_selected.Body_Text))
        {
            ReportError(L.T("Status.EmptyBody"));
            return;
        }
        await App.Api.AuditAsync(_project.Id, _selected.No, semantic: true);
        await LoadProjectAsync(_project.Id);
        UpdateConsistencyFlags();
        // 审计的意义在于看得见结果：跑完直接把审计区亮出来，而不是回到正文区
        RevealZone(Zone.Audit);
    }

    /// <summary>「本章校验」：核对计划里的 must_include / must_not / required_threads 有没有被正文照做。
    /// 结果存 chapter.plan_check，与语义审计的报告各写各的。</summary>
    private async Task PlanCheckCoreAsync()
    {
        if (_project is null || _selected is null) return;
        if (string.IsNullOrWhiteSpace(_selected.Body_Text))
        {
            ReportError(L.T("Status.EmptyBody"));
            return;
        }
        if (_selected.Plan is null)
        {
            ReportError(L.T("Status.NoPlanToCheck"));
            return;
        }
        await App.Api.PlanCheckAsync(_project.Id, _selected.No, semantic: true);
        await LoadProjectAsync(_project.Id);
        RevealZone(Zone.Audit);
    }

    /// <summary>审计区面板上的「重新审计」：与「语义审计」同一条付费路径，只是入口在面板里。</summary>
    private Task ReauditAsync() => RequestAuditAsync();

    private async Task RequestAuditAsync()
    {
        if (_project is null || _selected is null)
        {
            ReportError(L.T("Status.SelectChapter"));
            return;
        }
        if (string.IsNullOrWhiteSpace(_selected.Body_Text))
        {
            ReportError(L.T("Status.EmptyBody"));
            return;
        }
        var pid = _project.Id;
        var chapter = _selected;
        await AuditConsent.ExecuteAsync(() => MessageBox.Show(
            this, L.T("Audit.Confirm"), L.T("Audit.RunTitle"),
            MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK, async () =>
        {
            if (!await PrepareAiAsync() || _project?.Id != pid || !ReferenceEquals(_selected, chapter)) return;
            await RunStep("Busy.Auditing", AuditCoreAsync);
        });
    }

    /// <summary>提交本章评注，然后直接规划下一章。</summary>
    private async Task SubmitReviewAsync()
    {
        if (_project is null || _selected is null) { ReportError(L.T("Status.SelectChapter")); return; }
        var pid = _project.Id;
        var ch = _selected;
        await RunStep("Busy.Writing", async () =>
        {
            await App.Api.RefineAsync(pid, ch.No, ReviewZoneView.Notes);
            ch.Review = null;
            ReviewZoneView.Load("");
            var next = await App.Api.PlanAsync(pid);
            await LoadProjectAsync(pid, next.No);
        });
    }

    /// <summary>杂项：只提交改过的偏好。范文走完整片段数组。失败时保留草稿。</summary>
    private async Task<bool> SaveMiscAsync(bool reload = true)
    {
        if (_project is null) { ReportError(L.T("Status.SelectWork")); return false; }
        if (!LoreMiscView.HasUnsaved)
        {
            LoreMiscView.ShowStatus(L.T("Misc.NoChange"));
            return true;
        }
        if (_miscSaving) return false;
        var changes = LoreMiscView.Changes();
        var samples = LoreMiscView.SamplePayload();
        if (changes.Count == 0 && samples is null)
        {
            LoreMiscView.ShowStatus(L.T("Misc.Dirty"));
            return true;
        }
        var pid = _project.Id;
        var no = _selected?.No;
        _miscSaving = true;
        LoreMiscView.BeginSave();
        var saved = false;
        try
        {
            if (!await FlushEditsAsync() || _project?.Id != pid)
            {
                LoreMiscView.FailSave();
                return false;
            }
            await App.Api.BibleEditAsync(pid, changes, samples);
            LoreMiscView.MarkSaved();
            saved = true;
            if (reload && _project?.Id == pid) await LoadProjectAsync(pid, no);
            LoreMiscView.ShowStatus(L.T("Misc.SavedState"));
            return true;
        }
        catch (Exception ex)
        {
            if (saved)
            {
                LoreMiscView.ShowStatus(L.T("Misc.SavedState"));
                ReportError(ex.Message);
                return true;
            }
            LoreMiscView.FailSave();
            ReportError(ex.Message);
            return false;
        }
        finally
        {
            _miscSaving = false;
        }
    }

    private async Task<bool> ConfirmMiscDraftAsync()
    {
        if (!LoreMiscView.HasUnsaved) return true;
        var choice = AskMiscLeave();
        if (choice == MiscLeaveChoice.Cancel) return false;
        if (choice == MiscLeaveChoice.Discard)
        {
            LoreMiscView.DiscardDraft();
            return true;
        }
        return await SaveMiscAsync(reload: false);
    }

    private MiscLeaveChoice AskMiscLeave()
    {
        var choice = MiscLeaveChoice.Cancel;
        var win = new Window
        {
            Title = L.T("Misc.LeaveTitle"),
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            ShowInTaskbar = false,
            Background = (System.Windows.Media.Brush)FindResource("BgBrush"),
        };
        var root = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
        root.Children.Add(new TextBlock
        {
            Text = L.T("Misc.LeavePrompt"),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 360,
            Margin = new Thickness(0, 0, 0, 14),
            Foreground = (System.Windows.Media.Brush)FindResource("TextBrush"),
        });
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var save = new Button { Content = L.T("Common.Save"), MinWidth = 72, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var discard = new Button { Content = L.T("Misc.Discard"), MinWidth = 72, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = L.T("Common.Cancel"), MinWidth = 72, IsCancel = true };
        save.Click += (_, _) => { choice = MiscLeaveChoice.Save; win.DialogResult = true; };
        discard.Click += (_, _) => { choice = MiscLeaveChoice.Discard; win.DialogResult = true; };
        row.Children.Add(save);
        row.Children.Add(discard);
        row.Children.Add(cancel);
        root.Children.Add(row);
        win.Content = root;
        win.ShowDialog();
        return choice;
    }

    private enum MiscLeaveChoice { Save, Discard, Cancel }

    /// <summary>规划区的「AI 规划」：按自由规划的文字填写表单；还没有章节时直接规划出新的一章。</summary>
    private async void AiPlan_Requested(object? sender, EventArgs e)
    {
        if (await PrepareAiAsync()) await AiPlanCoreAsync();
    }

    private async Task AiPlanCoreAsync()
    {
        if (_project is null) { ReportError(L.T("Status.SelectWork")); return; }
        await OpenProposalAsync();
    }

    /// <summary>写作面当前主题的正文字体；导出 EPUB / Word 时交给后端，和界面同款。</summary>
    private string BodyFontSource() =>
        (TryFindResource("BodyFontFamily") as FontFamily)?.Source ?? "";

    private async void ExportFile_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null) { ReportError(L.T("Status.SelectExport")); return; }
        if ((sender as FrameworkElement)?.Tag is string fmt) await ExportWorkAsync(_project.Id, fmt);
    }

    /// <param name="fmt">markdown / epub / txt / docx</param>
    private async Task ExportWorkAsync(string projectId, string fmt)
    {
        await Safe(async () =>
        {
            var r = await App.Api.ExportAsync(projectId, fmt, BodyFontSource());
            ShowInExplorer(r.Path);
        });
    }
}
