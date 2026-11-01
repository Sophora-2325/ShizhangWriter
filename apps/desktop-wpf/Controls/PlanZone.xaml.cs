using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Models;

namespace NovelGm.Desktop.Controls;

/// <summary>本章规划：分区菜单、就地详情和章节关联列表。导航不改变草稿。</summary>
public partial class PlanZone : UserControl
{
    private bool _loading, _readOnly;
    private double _cornerInset;
    private string _validWordCount = "";
    private LoreDto _catalog = new();
    private List<string> _characters = new(), _threads = new(), _scenes = new();
    private readonly Dictionary<string, string> _checkpoints = new();
    private readonly Dictionary<string, long> _pendingActions = new();
    private long _session, _operation;
    private string? _ruleTarget;
    /// <summary>复用主窗口保存队列；只有本次写入成功才确认撤销起点。</summary>
    public Func<Task<bool>>? PersistAsync { get; set; }
    internal Func<string, string, bool>? MemberLocked { get; set; }
    internal void RefreshMemberLocks() { if (ActiveSection is "characters" or "threads" or "scenes") RenderItems(); }
    internal void MergeStackedMembers(ChapterPlanDto plan, LoreDto catalog)
    {
        _catalog = catalog;
        foreach (var (kind, ids) in new[] { ("characters", plan.Present_Character_Ids), ("threads", plan.Required_Threads), ("scenes", plan.Scene_Types) })
            foreach (var id in ids)
                if (!Selected(kind).Contains(id)) Selected(kind).Add(id);
        RefreshMemberLocks();
    }
    internal string? ActiveSection { get; private set; }
    public event EventHandler? Changed;
    public event EventHandler? ProposalRequested;
    internal bool CanOpenProposal { set => ProposalButton.IsEnabled = value; }
    private void Proposal_Click(object sender, RoutedEventArgs e) => ProposalRequested?.Invoke(this, EventArgs.Empty);
    public event EventHandler? AddCharacterRequested;
    public event EventHandler? AddThreadRequested;

    public PlanZone()
    {
        InitializeComponent();
        LanguageManager.LanguageChanged += (_, _) => { if (ActiveSection is not null) RenderSection(); };
    }

    private IEnumerable<TextBox> Boxes => new[] { FreeBox, SceneGoalBox, ConflictBox, StakesBox, DecisionBox, MiscBox, MustIncludeBox, MustNotBox, WordCountBox };
    private Dictionary<string, Button> Buttons => new()
    {
        ["free"] = FreeButton, ["goal"] = GoalButton, ["rules"] = RulesButton, ["conflict"] = ConflictButton,
        ["stakes"] = StakesButton, ["scenes"] = ScenesButton, ["characters"] = CharactersButton,
        ["threads"] = ThreadsButton, ["length"] = LengthButton, ["decision"] = DecisionButton, ["misc"] = MiscButton,
    };
    private static string LabelKey(string section) => section switch
    {
        "free" => "Plan.PlotOutline", "goal" => "Plan.Core", "rules" => "Plan.Rules", "conflict" => "Plan.ConflictPoint",
        "stakes" => "Plan.Cost", "scenes" => "Plan.ScenesShort", "characters" => "Plan.CharactersShort",
        "threads" => "Plan.ThreadsShort", "length" => "Plan.Length", "decision" => "Plan.OpenDecision", _ => "Plan.Misc",
    };

    public double CornerInset
    {
        get => _cornerInset;
        set { _cornerInset = Math.Max(0, value); DetailTitle.Margin = new Thickness(0, 0, _cornerInset, 0); FreeButton.Padding = new Thickness(12, 8, 12 + _cornerInset, 8); }
    }

    public bool IsReadOnly
    {
        get => _readOnly;
        set
        {
            _readOnly = value;
            foreach (var box in Boxes) box.IsReadOnly = value;
            LengthPanel.IsEnabled = !value;
            AddButton.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            UpdateActionState();
            if (ActiveSection is "characters" or "threads" or "scenes") RenderItems();
        }
    }

    public void ShowNotice(string? key)
    {
        if (key is not null) ReadOnlyText.SetResourceReference(TextBlock.TextProperty, key);
        ReadOnlyBanner.Visibility = key is null ? Visibility.Collapsed : Visibility.Visible;
    }

    public void Load(ChapterPlanDto? plan, LoreDto lore)
    {
        _loading = true;
        try
        {
            var p = plan ?? new ChapterPlanDto();
            _catalog = lore;
            FreeBox.Text = p.Free_Text; SceneGoalBox.Text = p.Scene_Goal; ConflictBox.Text = p.Conflict;
            StakesBox.Text = p.Stakes; DecisionBox.Text = p.Open_Decision_For_User ?? ""; MiscBox.Text = p.Misc;
            MustIncludeBox.Text = string.Join("\n", p.Must_Include); MustNotBox.Text = string.Join("\n", p.Must_Not);
            _validWordCount = plan is null || p.Target_Word_Count <= 0 ? "" : p.Target_Word_Count.ToString();
            WordCountBox.Text = _validWordCount; WordCountError.Visibility = Visibility.Collapsed;
            _characters = Canonical(p.Present_Character_Ids, Choices("characters"));
            _threads = Canonical(p.Required_Threads, Choices("threads"));
            _scenes = p.Scene_Types.Distinct(StringComparer.Ordinal).ToList();
        }
        finally { _loading = false; }
        IsReadOnly = _readOnly;
        BeginSection();
        RenderSection();
    }

    internal static LoreDto Catalog(ProjectDto project, LoreDto current) => new()
    {
        Characters = project.Characters.Concat(current.Characters).GroupBy(c => c.Id).Select(g => g.First()).ToList(),
        Threads = current.Threads.Concat(project.Threads).GroupBy(t => t.Id).Select(g => g.First()).ToList(),
        Entries = current.Entries,
    };

    private static List<string> Canonical(IEnumerable<string> selected, IReadOnlyList<PlanChoice> choices) => selected
        .Select(id => choices.FirstOrDefault(c => c.Id == id)?.Id ??
            (choices.Count(c => c.Name == id) == 1 ? choices.First(c => c.Name == id).Id : id))
        .Distinct(StringComparer.Ordinal).ToList();

    private static string Norm(string value) => value.Replace("\r\n", "\n");
    private static List<string> Lines(string value) => Norm(value).Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
    public ChapterPlanDto Read() => new()
    {
        Free_Text = Norm(FreeBox.Text), Scene_Goal = Norm(SceneGoalBox.Text), Conflict = Norm(ConflictBox.Text),
        Stakes = Norm(StakesBox.Text), Misc = Norm(MiscBox.Text),
        Open_Decision_For_User = string.IsNullOrWhiteSpace(DecisionBox.Text) ? null : Norm(DecisionBox.Text),
        Must_Include = Lines(MustIncludeBox.Text), Must_Not = Lines(MustNotBox.Text),
        Target_Word_Count = int.TryParse(_validWordCount, out var n) ? n : 3000,
        Present_Character_Ids = _characters.ToList(), Required_Threads = _threads.ToList(), Scene_Types = _scenes.ToList(),
    };

    internal void LocateField(string field, string quote)
    {
        var (section, box) = field switch
        {
            "scene_goal" => ("goal", SceneGoalBox), "conflict" => ("conflict", ConflictBox),
            "stakes" => ("stakes", StakesBox), "misc" => ("misc", MiscBox),
            "open_decision_for_user" => ("decision", DecisionBox),
            "must_include" => ("rules", MustIncludeBox), "must_not" => ("rules", MustNotBox),
            _ => ("free", FreeBox),
        };
        OpenSection(section);
        box.BringIntoView(); box.Focus();
        var offset = box.Text.IndexOf(quote, StringComparison.Ordinal);
        if (offset >= 0) box.Select(offset, quote.Length);
    }
    public static bool IsEmpty(ChapterPlanDto p) =>
        string.IsNullOrWhiteSpace(p.Free_Text) && string.IsNullOrWhiteSpace(p.Scene_Goal) && string.IsNullOrWhiteSpace(p.Conflict)
        && string.IsNullOrWhiteSpace(p.Stakes) && string.IsNullOrWhiteSpace(p.Misc) && string.IsNullOrWhiteSpace(p.Open_Decision_For_User)
        && p.Present_Character_Ids.Count == 0 && p.Required_Threads.Count == 0 && p.Scene_Types.Count == 0
        && p.Must_Include.Count == 0 && p.Must_Not.Count == 0;

    internal void OpenSection(string? section)
    {
        if (ActiveSection != section)
        {
            ActiveSection = section;
            BeginSection();
        }
        RenderSection();
    }
    private TextBox? SectionBox(string? key) => key switch
    {
        "free" => FreeBox, "goal" => SceneGoalBox, "conflict" => ConflictBox, "stakes" => StakesBox,
        "decision" => DecisionBox, "misc" => MiscBox, "length" => WordCountBox,
        "include" => MustIncludeBox, "exclude" => MustNotBox, _ => null,
    };
    private void BeginSection()
    {
        _session++;
        _pendingActions.Clear(); _checkpoints.Clear(); _ruleTarget = null;
        foreach (var key in ActiveSection == "rules" ? new[] { "include", "exclude" } : new[] { ActiveSection })
            if (key is not null && SectionBox(key) is { } box)
                _checkpoints[key] = key == "length" ? _validWordCount : box.Text;
    }
    internal string? SaveTarget => ActiveSection == "rules" ? _ruleTarget : SectionBox(ActiveSection) is null ? null : ActiveSection;

    internal async Task<bool> SaveSectionAsync(string? key)
    {
        if (_readOnly || PersistAsync is null) return false;
        if (key is null) return await PersistAsync(); // 无焦点目标：只保存，不确认其他分项。
        if (!_checkpoints.ContainsKey(key) || _pendingActions.ContainsKey(key) || SectionBox(key) is not { } box) return false;
        if (key == "length" && WordCountError.Visibility == Visibility.Visible) { WordCountBox.Focus(); return false; }
        var submitted = key == "length" ? _validWordCount : box.Text;
        var session = _session; var operation = ++_operation;
        _pendingActions[key] = operation; UpdateActionState();
        try
        {
            var saved = await PersistAsync();
            if (saved && _session == session && _pendingActions.GetValueOrDefault(key) == operation)
                _checkpoints[key] = submitted;
            return saved;
        }
        finally
        {
            if (_session == session && _pendingActions.GetValueOrDefault(key) == operation) _pendingActions.Remove(key);
            UpdateActionState();
        }
    }
    internal async Task CancelSectionAsync(string? key)
    {
        if (_readOnly || key is null || _pendingActions.ContainsKey(key) || !_checkpoints.TryGetValue(key, out var original) || SectionBox(key) is not { } box) return;
        box.Text = original;
        // 也要通知未改变可见文本的情况（例如无效篇幅），确保恢复值真正落盘。
        Changed?.Invoke(this, EventArgs.Empty);
        if (PersistAsync is not null) await PersistAsync();
    }
    private void UpdateActionState()
    {
        bool Enabled(string? key) => !_readOnly && key is not null && !_pendingActions.ContainsKey(key);
        SaveSectionButton.IsEnabled = Enabled(ActiveSection) && (ActiveSection != "length" || WordCountError.Visibility != Visibility.Visible);
        CancelSectionButton.IsEnabled = Enabled(ActiveSection);
        SaveIncludeButton.IsEnabled = CancelIncludeButton.IsEnabled = Enabled("include");
        SaveExcludeButton.IsEnabled = CancelExcludeButton.IsEnabled = Enabled("exclude");
    }
    private void Rule_Focused(object sender, KeyboardFocusChangedEventArgs e) =>
        _ruleTarget = sender is Button { Tag: string key } ? key : sender == MustIncludeBox ? "include" : "exclude";
    private string? ActionTarget(object sender)
    {
        if (sender is Button { Tag: string key }) { _ruleTarget = key; return key; }
        return SaveTarget;
    }
    private async void SaveSection_Click(object sender, RoutedEventArgs e) => await SaveSectionAsync(ActionTarget(sender));
    private async void CancelSection_Click(object sender, RoutedEventArgs e) => await CancelSectionAsync(ActionTarget(sender));
    private async void Plan_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.S || Keyboard.Modifiers != ModifierKeys.Control) return;
        e.Handled = true;
        await SaveSectionAsync(SaveTarget);
    }
    private void Toggle(string section) => OpenSection(ActiveSection == section ? null : section);
    private void RenderSection()
    {
        foreach (var (key, button) in Buttons) button.Tag = key == ActiveSection ? "active" : null;
        foreach (UIElement editor in Editors.Children) editor.Visibility = Visibility.Collapsed;
        DetailActions.Visibility = SectionBox(ActiveSection) is not null ? Visibility.Visible : Visibility.Collapsed;
        UpdateActionState();
        if (ActiveSection is { } section)
        {
            DetailTitle.SetResourceReference(TextBlock.TextProperty, LabelKey(section));
            var editor = section switch
            {
                "free" => (UIElement)FreeBox, "goal" => SceneGoalBox, "conflict" => ConflictBox, "stakes" => StakesBox,
                "decision" => DecisionBox, "misc" => MiscBox, "rules" => RulesPanel, "length" => LengthPanel, _ => ListPanel,
            };
            editor.Visibility = Visibility.Visible;
            if (editor == ListPanel) RenderItems();
        }
        ApplyLayout();
    }
    private void ApplyLayout()
    {
        var open = ActiveSection is not null;
        var wide = BodyGrid.ActualWidth >= 704;
        MenuScroll.Visibility = !open || wide ? Visibility.Visible : Visibility.Collapsed;
        DetailPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = open && !wide ? Visibility.Visible : Visibility.Collapsed;
        MenuColumn.Width = !open ? new GridLength(1, GridUnitType.Star) : new GridLength(wide ? 384 : 0);
        DetailColumn.Width = open ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        DetailPanel.BorderThickness = new Thickness(open && wide ? 0 : 1, 1, 1, 1);
        DetailActions.Margin = new Thickness(8, 12, 8, Math.Max(0, DetailPanel.ActualHeight * 0.05));
    }

    internal IReadOnlyList<PlanChoice> Choices(string kind) => kind switch
    {
        "characters" => _catalog.Characters.Select(c => new PlanChoice(c.Id, string.IsNullOrWhiteSpace(c.Name) ? c.Id : c.Name)).ToList(),
        "threads" => _catalog.Threads.Select(t => new PlanChoice(t.Id, string.IsNullOrWhiteSpace(t.Name) ? t.Id : t.Name)).ToList(),
        _ => SceneTypes.All.Select(id => new PlanChoice(id, L.T("Scene." + id))).ToList(),
    };
    private List<string> Selected(string kind) => kind switch { "characters" => _characters, "threads" => _threads, _ => _scenes };
    internal void AddSelection(string kind, IEnumerable<string> ids)
    {
        if (_readOnly) return;
        var list = Selected(kind); var before = list.Count;
        foreach (var id in Canonical(ids, Choices(kind))) if (!list.Contains(id, StringComparer.Ordinal)) list.Add(id);
        if (list.Count == before) return;
        RenderItems(); Changed?.Invoke(this, EventArgs.Empty);
    }
    internal void RemoveSelection(string kind, string id)
    {
        if (_readOnly || MemberLocked?.Invoke(kind, id) == true || !Selected(kind).Remove(id)) return;
        RenderItems(); Changed?.Invoke(this, EventArgs.Empty);
    }
    private void RenderItems()
    {
        ItemsPanel.Children.Clear();
        if (ActiveSection is not ("characters" or "threads" or "scenes")) return;
        var kind = ActiveSection;
        AddButton.SetResourceReference(ContentProperty, kind switch { "characters" => "Plan.AddCharacter", "threads" => "Plan.AddThread", _ => "Plan.AddScene" });
        var choices = Choices(kind);
        foreach (var id in Selected(kind))
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = true };
            var remove = new Button { Content = L.T("Plan.Remove"), Padding = new Thickness(8, 5, 8, 5), Visibility = _readOnly ? Visibility.Collapsed : Visibility.Visible, Tag = id };
            if (MemberLocked?.Invoke(kind, id) == true)
            {
                remove.IsEnabled = false; remove.ToolTip = L.T("Stack.PlanLocked");
                ToolTipService.SetShowOnDisabled(remove, true);
            }
            System.Windows.Automation.AutomationProperties.SetName(remove, L.T("Plan.RemoveNamed", choices.FirstOrDefault(c => c.Id == id)?.Name ?? id));
            remove.Click += (_, _) => RemoveSelection(kind, id);
            DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove);
            row.Children.Add(new TextBlock { Text = choices.FirstOrDefault(c => c.Id == id)?.Name ?? id, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 12, 5) });
            ItemsPanel.Children.Add(row);
        }
        if (Selected(kind).Count == 0)
        {
            var empty = new TextBlock { Text = L.T("Plan.ListEmpty"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); ItemsPanel.Children.Add(empty);
        }
    }
    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_readOnly || ActiveSection is null) return;
        var kind = ActiveSection;
        var picker = new PlanSelectionWindow(L.T(LabelKey(kind)), Choices(kind), Selected(kind), kind != "scenes") { Owner = Window.GetWindow(this) };
        if (picker.ShowDialog() == true) AddSelection(kind, picker.SelectedIds);
        else if (picker.CreateNew)
        {
            if (kind == "characters") AddCharacterRequested?.Invoke(this, EventArgs.Empty);
            else AddThreadRequested?.Invoke(this, EventArgs.Empty);
        }
    }
    private void Field_Changed(object sender, TextChangedEventArgs e) { if (!_loading) Changed?.Invoke(this, EventArgs.Empty); }
    private void WordCount_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading || WordCountError is null) return;
        var text = WordCountBox.Text.Trim();
        var valid = text.Length == 0 || (text.All(char.IsAsciiDigit) && int.TryParse(text, out var n) && n > 0);
        WordCountError.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
        UpdateActionState();
        if (!valid) return;
        _validWordCount = text; Changed?.Invoke(this, EventArgs.Empty);
    }
    private void WordCount_PreviewTextInput(object sender, TextCompositionEventArgs e) => e.Handled = !e.Text.All(char.IsAsciiDigit);
    private void Preset_Click(object sender, RoutedEventArgs e) { if (!_readOnly && sender is Button { Tag: string count }) WordCountBox.Text = count; }
    private void BodyGrid_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyLayout();
    private void DetailPanel_SizeChanged(object sender, SizeChangedEventArgs e) =>
        DetailActions.Margin = new Thickness(8, 12, 8, Math.Max(0, DetailPanel.ActualHeight * 0.05));
    private void Back_Click(object sender, RoutedEventArgs e) => OpenSection(null);
    public void ShowForm() => OpenSection("goal");
    private void Free_Click(object sender, RoutedEventArgs e) => Toggle("free");
    private void Goal_Click(object sender, RoutedEventArgs e) => Toggle("goal");
    private void Rules_Click(object sender, RoutedEventArgs e) => Toggle("rules");
    private void Conflict_Click(object sender, RoutedEventArgs e) => Toggle("conflict");
    private void Stakes_Click(object sender, RoutedEventArgs e) => Toggle("stakes");
    private void Scenes_Click(object sender, RoutedEventArgs e) => Toggle("scenes");
    private void Characters_Click(object sender, RoutedEventArgs e) => Toggle("characters");
    private void Threads_Click(object sender, RoutedEventArgs e) => Toggle("threads");
    private void Length_Click(object sender, RoutedEventArgs e) => Toggle("length");
    private void Decision_Click(object sender, RoutedEventArgs e) => Toggle("decision");
    private void Misc_Click(object sender, RoutedEventArgs e) => Toggle("misc");
}
