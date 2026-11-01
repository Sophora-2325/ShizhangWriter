using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using NovelGm.Desktop.Api;

namespace NovelGm.Desktop.Controls;

/// <summary>
/// 右侧「功能」栏：当前章工具 / 查找与检测 / 排版与批注 / 版本与发布。
/// 发布、版本、查找、敏感词、统计、复制、导出、备份只发事件（外壳负责执行）；编辑批注四项在这里直接执行。
/// </summary>
public partial class ToolsPanel : UserControl
{
    /// <summary>外壳入口。参数见 <see cref="ToolRules"/> 的常量。</summary>
    public event EventHandler<string>? ActionRequested;

    private readonly Dictionary<string, Button> _buttons = new();
    private readonly Dictionary<string, TextBlock> _statuses = new();
    private ToolContext _context;
    private SensitiveScanState _sensitive = SensitiveScanState.NotRun;
    private string? _chapterTitle;
    private bool _copied;
    private DispatcherTimer? _copyTimer;

    public ToolsPanel()
    {
        InitializeComponent();
        Build();
        Loaded += (_, _) =>
        {
            LanguageManager.LanguageChanged -= OnLanguageChanged;
            LanguageManager.LanguageChanged += OnLanguageChanged;
            Build();
        };
        Unloaded += (_, _) => LanguageManager.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => Build();

    /// <summary>重建按钮，让当前语言与状态文案保持最新。</summary>
    public void Refresh() => Build();

    /// <summary>工具区的固定条目：GroupKey / Key 都是三语文案键，Action 是事件参数。</summary>
    internal sealed record Entry(string GroupKey, string Key, string Action);

    internal static IReadOnlyList<Entry> Entries() => new Entry[]
    {
        new("Tools.GroupChapter", "Tools.Stats", ToolRules.Stats),
        new("Tools.GroupChapter", "Tools.CopyBody", ToolRules.Copy),
        new("Tools.GroupChapter", "Tools.ExportChapter", ToolRules.ExportChapter),
        new("Tools.GroupFind", "Tools.FindChapter", ToolRules.FindChapter),
        new("Tools.GroupFind", "Tools.FindBook", ToolRules.FindBook),
        new("Tools.GroupFind", "Tools.Sensitive", ToolRules.Sensitive),
        new("Tools.GroupPublish", "Tools.Versions", ToolRules.Versions),
        new("Tools.GroupPublish", "Tools.Backup", ToolRules.Backup),
        new("Tools.GroupPublish", "Tools.Publish", ToolRules.Publish),
    };

    internal static readonly string[] GroupOrder =
    {
        "Tools.GroupChapter", "Tools.GroupFind", "Tools.GroupEditComment", "Tools.GroupPublish",
    };

    /// <summary>更新标题与可用性；只改状态，不重建列表。</summary>
    public void SetContext(string? chapterTitle, ToolContext context)
    {
        _chapterTitle = chapterTitle;
        _context = context;
        ChapterTitleText.Text = string.IsNullOrWhiteSpace(chapterTitle) ? L.T("Tools.NoChapter") : chapterTitle;
        ApplyStates();
    }

    /// <summary>敏感词行状态（未检测 / 检测中 / 发现问题 / 未发现问题 / 结果过期）。</summary>
    public void SetSensitive(SensitiveScanState state)
    {
        _sensitive = state;
        ApplyStates();
    }

    /// <summary>复制成功后在「复制正文」行短暂显示「已复制」。</summary>
    public void ShowCopied()
    {
        _copied = true;
        ApplyStates();
        _copyTimer?.Stop();
        _copyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _copyTimer.Tick += (_, _) =>
        {
            _copyTimer?.Stop();
            _copied = false;
            ApplyStates();
        };
        _copyTimer.Start();
    }

    private void Build()
    {
        _copyTimer?.Stop();
        _copied = false;
        Items.Children.Clear();
        _buttons.Clear();
        _statuses.Clear();
        var byGroup = Entries().ToLookup(e => e.GroupKey);
        var commands = BodyEditing.Commands();

        foreach (var group in GroupOrder)
        {
            if (group == "Tools.GroupEditComment")
            {
                AddGroup(group);
                foreach (var command in commands)
                {
                    var action = ActionOf(command.Key);
                    AddRow(action, command.Key, command.Label?.Invoke() ?? L.T(command.Key),
                        () => { command.Run(_buttons[action]); ApplyStates(); });
                }
                continue;
            }

            var entries = byGroup[group].ToList();
            if (entries.Count == 0) continue;
            AddGroup(group);
            foreach (var entry in entries)
            {
                var action = entry.Action;
                AddRow(action, entry.Key, L.T(entry.Key), () => ActionRequested?.Invoke(this, action));
            }
        }

        ChapterTitleText.Text = string.IsNullOrWhiteSpace(_chapterTitle) ? L.T("Tools.NoChapter") : _chapterTitle;
        ApplyStates();
    }

    private void AddGroup(string key)
    {
        Items.Children.Add(new TextBlock { Text = L.T(key), Style = (Style)FindResource("ToolsGroup") });
    }

    private void AddRow(string action, string key, string label, Action run)
    {
        var icon = CreateIcon(action);
        var name = new TextBlock { Text = label, FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var status = new TextBlock { FontSize = 12, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        status.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        status.Tag = "MutedBrush";

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(name);
        text.Children.Add(status);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        icon.Margin = new Thickness(0, 0, 8, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(icon);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var button = new Button { Content = grid, Style = (Style)FindResource("ToolsItem"), Tag = action };
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => run();
        _buttons[action] = button;
        _statuses[action] = status;
        Items.Children.Add(button);
    }

    private static FrameworkElement CreateIcon(string action)
    {
        if (action == ToolRules.Invisibles)
        {
            var pilcrow = new TextBlock { Text = "¶", FontSize = 16, Width = 16, TextAlignment = TextAlignment.Center };
            pilcrow.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            return pilcrow;
        }

        var path = new Path
        {
            Data = Geometry.Parse(IconPath[action]),
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
        path.SetResourceReference(Shape.StrokeProperty, "MutedBrush");
        return path;
    }

    private void ApplyStates()
    {
        foreach (var (action, button) in _buttons)
        {
            var enabled = ToolRules.IsEnabled(action, _context);
            button.IsEnabled = enabled;
            var key = KeyOf(action);
            button.ToolTip = !enabled && action == ToolRules.CommentAdd ? L.T("Tools.NeedSelection") : L.T(key);
        }

        SetStatus(ToolRules.Copy, _copied ? L.T("Tools.Copied") : "");
        SetStatus(ToolRules.Invisibles, BodyEditing.InvisiblesOn ? L.T("Tools.InvisiblesOn") : "");
        SetStatus(ToolRules.Sensitive, SensitiveText(), _sensitive == SensitiveScanState.Found ? "ReviewBrush" : "MutedBrush");
    }

    private void SetStatus(string action, string text, string brushKey = "MutedBrush")
    {
        if (!_statuses.TryGetValue(action, out var status)) return;
        status.Text = text;
        status.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        // 状态每次按键都会刷新；只有颜色键变化时才重挂资源，避免无谓的资源查找。
        if (Equals(status.Tag, brushKey)) return;
        status.Tag = brushKey;
        status.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
    }

    private string SensitiveText() => _sensitive switch
    {
        SensitiveScanState.Scanning => L.T("Tools.Sensitive.Scanning"),
        SensitiveScanState.Clean => L.T("Tools.Sensitive.Clean"),
        SensitiveScanState.Found => L.T("Tools.Sensitive.Found"),
        SensitiveScanState.Stale => L.T("Tools.Sensitive.Stale"),
        _ => L.T("Tools.Sensitive.NotRun"),
    };

    private static string ActionOf(string key) => key switch
    {
        "Tools.Spacing" => ToolRules.Spacing,
        "Tools.Invisibles" => ToolRules.Invisibles,
        "Tools.CommentAdd" => ToolRules.CommentAdd,
        "Tools.CommentList" => ToolRules.CommentList,
        _ => key,
    };

    internal static string KeyOf(string action) => action switch
    {
        ToolRules.Stats => "Tools.Stats",
        ToolRules.Copy => "Tools.CopyBody",
        ToolRules.ExportChapter => "Tools.ExportChapter",
        ToolRules.FindChapter => "Tools.FindChapter",
        ToolRules.FindBook => "Tools.FindBook",
        ToolRules.Sensitive => "Tools.Sensitive",
        ToolRules.Spacing => "Tools.Spacing",
        ToolRules.Invisibles => "Tools.Invisibles",
        ToolRules.CommentAdd => "Tools.CommentAdd",
        ToolRules.CommentList => "Tools.CommentList",
        ToolRules.Versions => "Tools.Versions",
        ToolRules.Backup => "Tools.Backup",
        ToolRules.Publish => "Tools.Publish",
        _ => action,
    };

    // 16×16 单线图标；不依赖图标字体，避免缺字。
    private static readonly Dictionary<string, string> IconPath = new()
    {
        [ToolRules.Stats] = "M2,13 L2,8 M6,13 L6,5 M10,13 L10,9 M14,13 L14,3",
        [ToolRules.Copy] = "M5,2 H12 A1,1 0 0 1 13,3 V10 M2,5 H9 A1,1 0 0 1 10,6 V13 A1,1 0 0 1 9,14 H2 A1,1 0 0 1 1,13 V6 A1,1 0 0 1 2,5 Z",
        [ToolRules.ExportChapter] = "M8,10 V2 M5,5 L8,2 L11,5 M2,10 V14 H14 V10",
        [ToolRules.FindChapter] = "M7,2 A5,5 0 1 0 7,12 A5,5 0 1 0 7,2 M10.5,10.5 L14,14",
        [ToolRules.FindBook] = "M2,3 H7 A1,1 0 0 1 8,4 V13 A1,1 0 0 0 7,12 H2 Z M14,3 H9 A1,1 0 0 0 8,4 V13 A1,1 0 0 1 9,12 H14 Z",
        [ToolRules.Sensitive] = "M8,2 L14,4 V8 C14,11 11,13.5 8,14.5 C5,13.5 2,11 2,8 V4 Z M5.5,8 L7.5,10 L10.5,6",
        [ToolRules.Spacing] = "M1,3 H15 M1,7 H11 M1,11 H15 M1,15 H11",
        [ToolRules.CommentAdd] = "M2,3 H14 V11 H8 L5,14 V11 H2 Z M8,5 V9 M6,7 H10",
        [ToolRules.CommentList] = "M2,3 H14 V11 H8 L5,14 V11 H2 Z M5,6 H11 M5,8.5 H9",
        [ToolRules.Versions] = "M8,2 A6,6 0 1 1 2,8 M8,5 V8 L10.5,9.5",
        [ToolRules.Backup] = "M2,5 H14 V14 H2 Z M1,2 H15 V5 H1 Z M6,8 H10",
        [ToolRules.Publish] = "M3,8 A2,2 0 1 0 3,12 A2,2 0 1 0 3,8 M13,2 A2,2 0 1 0 13,6 A2,2 0 1 0 13,2 M13,10 A2,2 0 1 0 13,14 A2,2 0 1 0 13,10 M5,9 L11,5 M5,11 L11,12",
    };
}
