using System.Windows;
using System.Windows.Controls;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Models;

namespace NovelGm.Desktop.Controls;

/// <summary>
/// AI 全流程面板：按创作、检查与整理分组的 8 个操作。
/// 面板只发事件、不直接调后端；按钮可用状态由外壳通过 SetChapterState / SetBusy 驱动。
/// </summary>
public partial class AiPanel : UserControl
{
    /// <summary>本章规划：切到规划区。</summary>
    public event EventHandler? ShowPlanRequested;
    public event EventHandler? ShowStackedConstraintsRequested;

    /// <summary>让 AI 填写结构化大纲。</summary>
    public event EventHandler? AiPlanRequested;

    /// <summary>生成正文 / 在已有正文后续写。</summary>
    public event EventHandler? GenerateRequested;

    /// <summary>本章定稿。</summary>
    public event EventHandler? FinalizeRequested;

    /// <summary>一致性审计（语义审计，会计费）。</summary>
    public event EventHandler? AuditRequested;

    /// <summary>计划校验：核对计划里的要求有没有被正文照做。</summary>
    public event EventHandler? PlanCheckRequested;

    /// <summary>提取本章摘要。</summary>
    public event EventHandler? ExtractRequested;

    /// <summary>本章评注：切到评注区。</summary>
    public event EventHandler? ShowReviewRequested;
    public event EventHandler? BodyRevisionRequested;

    private bool _hasChapter;
    private bool _hasBody;
    private bool _isReadOnly;
    private bool _busy;
    private string? _busyKey;
    private ChapterDto? _workflow;
    private string? _failedKey;

    public AiPanel()
    {
        InitializeComponent();
        ProofDesk.Apply(this);
        Loaded += (_, _) => { ThemeManager.ThemeChanged -= ThemeChanged; ThemeManager.ThemeChanged += ThemeChanged; };
        Unloaded += (_, _) => ThemeManager.ThemeChanged -= ThemeChanged;
        SetChapterState(false, false, false, false, false, false);
        LanguageManager.LanguageChanged += (_, _) => RenderTexts();
    }
    private void ThemeChanged(object? sender, EventArgs e) => ProofDesk.Apply(this);

    public void SetWorkflow(ChapterDto? chapter)
    {
        if (_workflow?.Chapter_Uid != chapter?.Chapter_Uid) _failedKey = null;
        _workflow = chapter;
        ChapterHeading.Text = chapter is null ? "" : $"{chapter.No} · {chapter.Title}";
        AuditState.Text = ReportState(chapter?.Consistency_Report, chapter?.Upstream_Changed.Count > 0 || chapter?.Stale_Results.ContainsKey("consistency_report") == true);
        CheckState.Text = ReportState(chapter?.Plan_Check, chapter?.Plan_Upstream_Changed.Count > 0 || chapter?.Stale_Results.ContainsKey("plan_check") == true);
        SummaryState.Text = L.T("Ai.Flow.Summary", L.T("Ai.State." + (chapter?.Summary_Status ?? "pending")));
        if (_busyKey == "Busy.Auditing" || _failedKey == "Busy.Auditing") AuditState.Text = L.T(_busyKey is null ? "Ai.State.failed" : "Ai.Busy.Progress");
        if (_busyKey == "Busy.Checking" || _failedKey == "Busy.Checking") CheckState.Text = L.T(_busyKey is null ? "Ai.State.failed" : "Ai.Busy.Progress");
    }
    public void RecordFailure() { _failedKey = _busyKey; SetWorkflow(_workflow); }
    private static string ReportState(ConsistencyReportDto? report, bool stale) => stale ? L.T("Ai.State.stale") :
        report is null ? L.T("Ai.State.unchecked") : report.Flags.Count > 0 ? L.T("Ai.Flow.IssueCount", report.Flags.Count) :
        !report.Semantic_Checked ? L.T("Ai.State.semantic") : L.T("Ai.State.passed");

    /// <summary>按当前章节与流程状态刷新按钮可用性。busy 期间保持全部禁用，结束后自动恢复。</summary>
    public void SetChapterState(bool hasChapter, bool atFirstChapter, bool isLatestChapter, bool hasBody, bool isFinal, bool isReadOnly)
    {
        // 保留外壳调用契约；章节位置 / isFinal 目前不单独改变可用性，生成的边界由事件方判断，
        // 这样不会因为状态推断把本可执行的操作提前禁掉。
        _hasChapter = hasChapter;
        _hasBody = hasBody;
        _isReadOnly = isReadOnly;
        ApplyEnabled();
    }

    /// <summary>非空表示运行中：显示进度文字并禁用全部操作，防止重复触发。</summary>
    public void SetBusy(string? busyKey)
    {
        _busyKey = busyKey;
        if (busyKey is not null) _failedKey = null;
        _busy = !string.IsNullOrWhiteSpace(busyKey);
        BusyBox.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
        BusyText.Text = _busy ? BusyLabel(busyKey!) : "";
        ApplyEnabled();
        SetWorkflow(_workflow);
    }

    public void SetProposalStatus(string? status, bool hasDraft = false)
    {
        ProposalState.Text = status is null ? "" : L.T("Proposal.BodyUses", status);
        ProposalState.Visibility = string.IsNullOrEmpty(status) ? Visibility.Collapsed : Visibility.Visible;
        DraftState.Visibility = hasDraft ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderTexts()
    {
        if (_busy && _busyKey is not null) BusyText.Text = BusyLabel(_busyKey);
        SetWorkflow(_workflow);
    }

    private static string BusyLabel(string key)
    {
        var text = L.T(key);
        // 传进来的键在当前语言里不存在时，退回面板自带的进度文字，避免直接暴露键名。
        // 已经翻译好的文字（不含「.」）原样显示。
        return string.Equals(text, key, StringComparison.Ordinal) && key.Contains('.') ? L.T("Ai.Busy.Progress") : text;
    }

    /// <summary>所有按钮的唯一开关入口；忙碌时一律禁用。</summary>
    private void ApplyEnabled()
    {
        var ready = _hasChapter && !_busy;
        var canRun = ready && !_isReadOnly;

        PlanButton.IsEnabled = ready;
        StackedConstraintsButton.IsEnabled = ready;
        AiPlanButton.IsEnabled = !_busy && (_hasChapter || !_isReadOnly);
        GenerateButton.IsEnabled = canRun;
        FinalizeButton.IsEnabled = canRun && _hasBody;
        AuditButton.IsEnabled = ready && _hasBody && (!_isReadOnly || AuditZone.StoredReport(_workflow, "consistency_report") is not null);
        PlanCheckButton.IsEnabled = ready && _hasBody && (!_isReadOnly || AuditZone.StoredReport(_workflow, "plan_check") is not null);
        ExtractButton.IsEnabled = canRun && _hasBody;
        ReviewButton.IsEnabled = ready;
        BodyRevisionButton.IsEnabled = ready && _hasBody;

        ReadOnlyHint.Visibility = _isReadOnly && _hasChapter ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Raise(EventHandler? handler)
    {
        if (_busy || !_hasChapter) return;
        handler?.Invoke(this, EventArgs.Empty);
    }

    private void Plan_Click(object sender, RoutedEventArgs e) => Raise(ShowPlanRequested);
    private void StackedConstraints_Click(object sender, RoutedEventArgs e) => Raise(ShowStackedConstraintsRequested);

    private void AiPlan_Click(object sender, RoutedEventArgs e) { if (!_busy) AiPlanRequested?.Invoke(this, EventArgs.Empty); }

    private void Generate_Click(object sender, RoutedEventArgs e) => Raise(GenerateRequested);

    private void Finalize_Click(object sender, RoutedEventArgs e) => Raise(FinalizeRequested);

    private void Audit_Click(object sender, RoutedEventArgs e) => Raise(AuditRequested);

    private void PlanCheck_Click(object sender, RoutedEventArgs e) => Raise(PlanCheckRequested);

    private void Extract_Click(object sender, RoutedEventArgs e) => Raise(ExtractRequested);

    private void Review_Click(object sender, RoutedEventArgs e) => Raise(ShowReviewRequested);
    private void BodyRevision_Click(object sender, RoutedEventArgs e) => Raise(BodyRevisionRequested);

}
