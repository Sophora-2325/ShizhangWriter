using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Text.Json;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Models;

namespace NovelGm.Desktop.Controls;

/// <summary>
/// 审计区：把章节的 consistency_report 摊开成一列卡片，让用户一眼看出「正文哪里和设定冲突」。
/// 严重度用左侧色条 + 徽标编码（红=硬冲突、蓝=警告、灰=提示），每条可「定位」跳到正文对应位置。
/// 界面按严重度排序，硬冲突永远在最上面。
/// </summary>
public partial class AuditZone : UserControl
{
    internal static ConsistencyReportDto? StoredReport(ChapterDto? chapter, string key)
    {
        var current = key == "plan_check" ? chapter?.Plan_Check : chapter?.Consistency_Report;
        if (current is not null) return current;
        if (chapter?.Stale_Results.TryGetValue(key, out var saved) != true) return null;
        try { return saved.Deserialize<ConsistencyReportDto>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (JsonException) { return null; }
    }

    public AuditZone()
    {
        InitializeComponent();
    }

    /// <summary>用户点了「重新审计」；由主窗口执行真正付费的语义审计。</summary>
    public event EventHandler? ReauditRequested;

    /// <summary>用户点了某条的「定位」，参数是正文逐字引文。</summary>
    public event EventHandler<string>? LocateRequested;

    private static string Sev(ConsistencyFlagDto f) =>
        string.IsNullOrWhiteSpace(f.Severity) ? "info" : f.Severity.Trim().ToLowerInvariant();

    private static int Rank(string sev) => sev switch { "fail" => 0, "warn" => 1, _ => 2 };

    private Brush Res(string key) => (Brush)FindResource(key);

    /// <summary>严重度 → 主色 / 徽标底色 / 标签。只用主题里已有的画刷，8 套主题都可见。</summary>
    private (Brush Accent, Brush BadgeBg, string Label) Look(string sev) => sev switch
    {
        "fail" => (Res("DangerBrush"), Res("WarningBgBrush"), L.T("Audit.Severity.Fail")),
        "warn" => (Res("AccentBrush"), Res("InfoBgBrush"), L.T("Audit.Severity.Warn")),
        _ => (Res("MutedBrush"), Res("Bg2Brush"), L.T("Audit.Severity.Info")),
    };

    private static string KindLabel(string kind) => kind switch
    {
        "rule" => L.T("Audit.Kind.Rule"),
        "state" => L.T("Audit.Kind.State"),
        "semantic" => L.T("Audit.Kind.Semantic"),
        _ => kind,
    };

    /// <summary>按当前章节重画整块面板。传 null 表示没有选中章节。
    /// 面板有两段：① 一致性审计（语义审计的产物）② 本章校验（计划约束有没有被照做）。
    /// 两者在数据层就分开存（consistency_report / plan_check），这里也分开显示，互不覆盖。</summary>
    public void Render(ChapterDto? ch)
    {
        FlagList.Children.Clear();

        var report = StoredReport(ch, "consistency_report");
        var flags = report?.Flags ?? new List<ConsistencyFlagDto>();
        var planReport = StoredReport(ch, "plan_check");
        var planFlags = planReport?.Flags ?? new List<ConsistencyFlagDto>();

        ChapterText.Text = ch is null ? "" : L.T("Audit.Chapter", ch.No, ch.Title);
        ReauditButton.IsEnabled = ch is not null && !string.IsNullOrWhiteSpace(ch.Body_Text);
        StatsText.Text = flags.Count > 0 ? Counts(flags) : "";
        PlanStatsText.Text = planFlags.Count > 0 ? Counts(planFlags) : "";

        // ① 一致性审计
        if (report is not null && ch?.Consistency_Report is null)
            FlagList.Children.Add(Hint(L.T("Ai.State.stale")));
        if (report is null)
        {
            FlagList.Children.Add(Hint(L.T("Audit.EmptyNotRun")));
        }
        else if (flags.Count == 0)
        {
            // 「0 条 flag」有两种完全不同的含义，必须分开说：
            // 只跑过规则检查 ≠ 语义审计查过且干净。旧数据没有这个标记，会落到「还没查过」那一侧。
            FlagList.Children.Add(Hint(L.T(report.Semantic_Checked ? "Audit.EmptyClean" : "Audit.EmptyRuleOnly")));
        }
        else
        {
            foreach (var f in Sorted(flags)) FlagList.Children.Add(BuildCard(f));
        }

        // ② 本章校验
        FlagList.Children.Add(SectionHeader(L.T("Audit.PlanCheck")));
        if (planReport is not null && ch?.Plan_Check is null)
            FlagList.Children.Add(Hint(L.T("Ai.State.stale")));
        if (planReport is null)
        {
            FlagList.Children.Add(Hint(L.T("Audit.PlanNotRun")));
        }
        else if (planFlags.Count == 0)
        {
            FlagList.Children.Add(Hint(L.T("Audit.PlanClean")));
        }
        else
        {
            foreach (var f in Sorted(planFlags)) FlagList.Children.Add(BuildCard(f));
        }

        EmptyText.Visibility = Visibility.Collapsed;
        FlagScroll.Visibility = Visibility.Visible;
    }

    private string Counts(List<ConsistencyFlagDto> flags) => L.T(
        "Audit.Stats",
        flags.Count(f => Sev(f) == "fail"),
        flags.Count(f => Sev(f) == "warn"),
        flags.Count(f => Sev(f) == "info"));

    private static List<ConsistencyFlagDto> Sorted(List<ConsistencyFlagDto> flags) => flags
        .OrderBy(f => Rank(Sev(f)))
        .ThenBy(f => f.Kind, StringComparer.Ordinal)
        .ToList();

    private TextBlock Hint(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12,
        LineHeight = 18,
        Margin = new Thickness(0, 0, 0, 8),
        Foreground = Res("MutedBrush"),
    };

    private TextBlock SectionHeader(string text) => new()
    {
        Text = text,
        FontSize = 13,
        FontWeight = FontWeights.Bold,
        Margin = new Thickness(0, 10, 0, 6),
        Foreground = Res("TextBrush"),
    };

    private void Reaudit_Click(object sender, RoutedEventArgs e) => ReauditRequested?.Invoke(this, EventArgs.Empty);

    private void Locate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string evidence } && !string.IsNullOrWhiteSpace(evidence))
            LocateRequested?.Invoke(this, evidence);
    }

    /// <summary>一条问题 = 左侧严重度色条 + 正文 + 右上「定位」；依据与建议只在有值时出现。</summary>
    private Border BuildCard(ConsistencyFlagDto f)
    {
        var sev = Sev(f);
        var (accent, badgeBg, label) = Look(sev);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var stripe = new Border { Background = accent, CornerRadius = new CornerRadius(2) };
        Grid.SetColumn(stripe, 0);
        grid.Children.Add(stripe);

        var body = new StackPanel { Margin = new Thickness(10, 0, 8, 0) };

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new Border
        {
            Background = badgeBg,
            BorderBrush = accent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 0, 6, 0),
            Margin = new Thickness(0, 0, 6, 0),
            Child = new TextBlock { Text = label, FontSize = 11, Foreground = accent },
        });
        head.Children.Add(new TextBlock
        {
            Text = KindLabel(f.Kind),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Res("MutedBrush"),
        });
        body.Children.Add(head);

        if (!string.IsNullOrWhiteSpace(f.Point))
        {
            body.Children.Add(new TextBlock
            {
                Text = f.Point,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                LineHeight = 20,
                Margin = new Thickness(0, 5, 0, 0),
                Foreground = Res("TextBrush"),
            });
        }

        if (!string.IsNullOrWhiteSpace(f.Evidence))
        {
            body.Children.Add(new Border
            {
                Margin = new Thickness(0, 6, 0, 0),
                Padding = new Thickness(8, 5, 8, 5),
                CornerRadius = new CornerRadius(4),
                Background = Res("Bg3Brush"),
                BorderBrush = Res("BorderBrushKey"),
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = L.T("Audit.Evidence", f.Evidence),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    LineHeight = 19,
                    Foreground = accent,
                },
            });
        }

        if (!string.IsNullOrWhiteSpace(f.Suggestion))
        {
            body.Children.Add(new TextBlock
            {
                Text = L.T("Audit.Suggestion", f.Suggestion),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                LineHeight = 19,
                Margin = new Thickness(0, 5, 0, 0),
                Foreground = Res("MutedBrush"),
            });
        }

        Grid.SetColumn(body, 1);
        grid.Children.Add(body);

        var hasEvidence = !string.IsNullOrWhiteSpace(f.Evidence);
        var locate = new Button
        {
            Content = L.T("Audit.Locate"),
            Padding = new Thickness(8, 2, 8, 2),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Top,
            Tag = f.Evidence ?? "",
            IsEnabled = hasEvidence,
            ToolTip = hasEvidence ? L.T("Audit.LocateTip") : L.T("Audit.LocateNone"),
        };
        locate.Click += Locate_Click;
        Grid.SetColumn(locate, 2);
        grid.Children.Add(locate);

        return new Border
        {
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = new CornerRadius(6),
            Background = Res("Bg2Brush"),
            BorderBrush = Res("BorderBrushKey"),
            BorderThickness = new Thickness(1),
            Child = grid,
        };
    }
}
