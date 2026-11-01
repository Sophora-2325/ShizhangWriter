using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Models;

namespace NovelGm.Desktop.Controls;

/// <summary>全书写作偏好。只提交本页改过的句长、禁写、避免表达、替代写法和范文；隐藏字段不在这里提交。</summary>
public partial class LoreMisc : UserControl
{
    private const double WideWidth = 560;
    private Snapshot? _snapshot;
    private readonly List<string> _samples = new();
    private readonly List<SampleSlot> _slots = new();
    private int _editing = -1;
    private bool _samplesOpen;
    private bool _extrasOpen;
    private bool _diagnosticsOpen;
    private string? _openRule;
    private bool _focusEditor;
    private bool _loading = true;
    private bool _syncing;
    private bool _readOnly;
    private bool _saving;
    private bool? _narrow;
    private sealed record Snapshot(string ProjectId, string Sentence, string Forbidden, string Banned,
        string Replace, IReadOnlyList<string> Samples);

    public event EventHandler? SaveRequested;

    public LoreMisc()
    {
        InitializeComponent();
        _loading = false;
        SizeChanged += (_, _) => ApplyWidth();
        RefreshSummaries();
    }

    public bool HasUnsaved => _snapshot is not null && (FieldChanges().Count > 0 || SamplesDirty());

    public bool IsReadOnly
    {
        set
        {
            if (_readOnly == value)
            {
                SaveButton.IsEnabled = HasUnsaved && !Locked;
                return;
            }
            _readOnly = value;
            RefreshInteraction();
        }
    }

    public void Load(ProjectDto? project)
    {
        if (project is null)
        {
            _snapshot = null;
            _samples.Clear();
            _editing = -1;
            ResetChrome();
            Form.IsEnabled = false;
            SaveButton.IsEnabled = false;
            SetStatus(L.T("Misc.NoWork"));
            RefreshSummaries();
            return;
        }
        if (_snapshot?.ProjectId == project.Id && HasUnsaved) return;
        var sameWork = _snapshot?.ProjectId == project.Id;
        var keepSaved = sameWork && StatusText.Text == L.T("Misc.SavedState");
        var style = project.Style;
        _loading = true;
        if (!sameWork) ResetChrome();
        SelectSentence(style?.Sentence_Length ?? "medium");
        ForbiddenBox.Text = string.Join("\n", project.Forbidden);
        BannedBox.Text = string.Join("\n", style?.Banned_Words ?? new());
        ReplaceBox.Text = string.Join("\n", (style?.Preferred_Replacements ?? new()).Select(kv => $"{kv.Key}→{kv.Value}"));
        _samples.Clear();
        _samples.AddRange(style?.Reference_Samples ?? new());
        if (_editing >= _samples.Count) _editing = -1;
        MetricsBox.Text = style is null || style.Metrics.Count == 0
            ? L.T("Misc.MetricsEmpty")
            : string.Join("　", style.Metrics.Select(kv => $"{kv.Key}={kv.Value:0.###}"));
        CompiledBox.Text = style?.Compiled_System_Constraints ?? "";
        Form.IsEnabled = true;
        _snapshot = ReadForm(project.Id);
        _loading = false;
        RefreshSummaries();
        if (!_saving && !keepSaved) SetStatus("");
        UpdateSaveEnabled();
    }

    public void DiscardDraft()
    {
        if (_snapshot is null) return;
        _loading = true;
        SelectSentence(_snapshot.Sentence);
        ForbiddenBox.Text = _snapshot.Forbidden;
        BannedBox.Text = _snapshot.Banned;
        ReplaceBox.Text = _snapshot.Replace;
        _samples.Clear();
        _samples.AddRange(_snapshot.Samples);
        if (_editing >= _samples.Count) _editing = -1;
        _loading = false;
        RefreshSummaries();
        SetStatus("");
        UpdateSaveEnabled();
    }

    public void BeginSave()
    {
        _saving = true;
        SetStatus(L.T("Misc.Saving"));
        RefreshInteraction();
    }

    public void FailSave()
    {
        _saving = false;
        SetStatus(L.T("Misc.SaveFailed"));
        RefreshInteraction();
    }

    public void MarkSaved()
    {
        _saving = false;
        if (_snapshot is not null) _snapshot = ReadForm(_snapshot.ProjectId);
        SetStatus(L.T("Misc.SavedState"));
        RefreshInteraction();
    }

    public List<BibleEditDto> Changes() => FieldChanges();

    /// <summary>范文有改动时返回要提交的完整片段；没有改动时返回 null。纯空白片段不包含在内。</summary>
    public List<string>? SamplePayload()
    {
        if (_snapshot is null || !SamplesDirty()) return null;
        var now = Clean(_samples);
        var before = Clean(_snapshot.Samples);
        return now.SequenceEqual(before) ? null : now;
    }

    public string SampleCaption(int index)
    {
        if (index < 0 || index >= _samples.Count) return "";
        var role = index < 3 ? L.T("Misc.Participates") : L.T("Misc.StoredSample");
        return role + " " + _samples[index];
    }

    public void RevealForbidden(string quote)
    {
        _openRule = "forbidden";
        ApplyRuleVisibility();
        UpdateLayout();
        ForbiddenBox.Focus();
        var at = ForbiddenBox.Text.IndexOf(quote, StringComparison.Ordinal);
        if (at >= 0) ForbiddenBox.Select(at, quote.Length);
        ForbiddenBox.BringIntoView();
    }

    public void ShowStatus(string text) => SetStatus(text);

    private List<BibleEditDto> FieldChanges()
    {
        var result = new List<BibleEditDto>();
        if (_snapshot is null) return result;
        var now = ReadForm(_snapshot.ProjectId);
        void Add(string path, string before, string after)
        {
            if (before != after) result.Add(new BibleEditDto { Path = path, New = after });
        }
        Add("style.sentence_length", _snapshot.Sentence, now.Sentence);
        Add("forbidden", _snapshot.Forbidden, now.Forbidden);
        Add("style.banned_words", _snapshot.Banned, now.Banned);
        Add("style.preferred_replacements", _snapshot.Replace, now.Replace);
        return result;
    }

    private Snapshot ReadForm(string id) => new(id, ReadSentence(), ForbiddenBox.Text, BannedBox.Text,
        ReplaceBox.Text, _samples.ToList());

    private bool SamplesDirty() => _snapshot is not null && !_samples.SequenceEqual(_snapshot.Samples);

    private string ReadSentence()
    {
        if (SentenceLong.IsChecked == true) return "long";
        if (SentenceTerse.IsChecked == true) return "terse";
        return "medium";
    }

    private void SelectSentence(string value)
    {
        var match = value switch
        {
            "terse" => SentenceTerse,
            "long" => SentenceLong,
            _ => SentenceMedium,
        };
        match.IsChecked = true;
    }

    private void ResetChrome()
    {
        _samplesOpen = false;
        _extrasOpen = false;
        _diagnosticsOpen = false;
        _openRule = null;
        _editing = -1;
    }

    private void RefreshSummaries()
    {
        var forbidden = Lines(ForbiddenBox.Text);
        ForbiddenHeader.Text = forbidden.Count == 0 ? "" : L.T("Misc.RuleCount", forbidden.Count);
        ForbiddenPreview.Text = forbidden.Count == 0 ? L.T("Misc.ForbiddenEmpty") : forbidden[0];

        var banned = Lines(BannedBox.Text);
        BannedHeader.Text = banned.Count == 0 ? L.T("Misc.Builtin") : L.T("Misc.BannedCustom", banned.Count);
        BannedPreview.Text = banned.Count == 0 ? L.T("Misc.BuiltinList") : banned[0];

        var replacements = Lines(ReplaceBox.Text);
        ReplaceHeader.Text = replacements.Count == 0 ? L.T("Misc.Builtin") : L.T("Misc.ReplaceCustom", replacements.Count);
        ReplacePreview.Text = replacements.Count == 0 ? L.T("Misc.ReplaceBuiltinLine") : replacements[0];

        var kept = Clean(_samples);
        SampleCountText.Text = kept.Count == 0 ? "" : L.T("Misc.SampleSegments", kept.Count);
        SamplePreviewText.Text = kept.Count == 0 ? "" : kept[0];
        ApplySampleVisibility();
        ApplyRuleVisibility();
        ApplyDiagnostics();
        SyncSlots();
    }

    private void ApplySampleVisibility()
    {
        var hasStored = Clean(_samples).Count > 0;
        var hasRows = _samples.Count > 0;
        EmptySamples.Visibility = hasRows ? Visibility.Collapsed : Visibility.Visible;
        SamplePreviewCard.Visibility = hasStored && !_samplesOpen ? Visibility.Visible : Visibility.Collapsed;
        SampleFooter.Visibility = hasRows ? Visibility.Visible : Visibility.Collapsed;
        SamplesBody.Visibility = hasRows && _samplesOpen ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyRuleVisibility()
    {
        ShowRule("forbidden", ForbiddenBody, ForbiddenPreview, ForbiddenChevron);
        ShowRule("banned", BannedBody, BannedPreview, BannedChevron);
        ShowRule("replace", ReplaceBody, ReplacePreview, ReplaceChevron);
    }

    private void ShowRule(string name, UIElement body, UIElement preview, System.Windows.Shapes.Path chevron)
    {
        var open = _openRule == name;
        body.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        preview.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        chevron.Data = Geometry.Parse(open ? "M1,1 L5,5 L9,1" : "M1,1 L5,5 L1,9");
    }

    private void ApplyDiagnostics()
    {
        DiagnosticsBody.Visibility = _diagnosticsOpen ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsChevron.Data = Geometry.Parse(_diagnosticsOpen ? "M1,1 L5,5 L9,1" : "M1,1 L5,5 L1,9");
    }

    private void SyncSlots()
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            while (_slots.Count < _samples.Count) _slots.Add(CreateSlot());
            while (_slots.Count > _samples.Count)
            {
                var last = _slots[^1];
                if (last.Root.Parent is Panel parent) parent.Children.Remove(last.Root);
                _slots.RemoveAt(_slots.Count - 1);
            }
            if (_editing >= _slots.Count) _editing = -1;
            for (var i = 0; i < _slots.Count; i++) BindSlot(_slots[i], i);
            var shown = Math.Min(3, _slots.Count);
            Place(SampleList, 0, shown);
            Place(ExtraList, shown, _slots.Count - shown);
            var extras = Math.Max(0, _slots.Count - 3);
            ExtraToggle.Visibility = extras > 0 ? Visibility.Visible : Visibility.Collapsed;
            ExtraToggle.Content = L.T("Misc.MoreSamples", extras);
            ExtraHost.Visibility = extras > 0 && _extrasOpen ? Visibility.Visible : Visibility.Collapsed;
            if (_focusEditor && _editing >= 0 && _editing < _slots.Count)
            {
                _slots[_editing].Editor.Focus();
                _focusEditor = false;
            }
        }
        finally { _syncing = false; }
    }

    private void BindSlot(SampleSlot slot, int index)
    {
        var text = _samples[index];
        slot.Role.Text = index < 3 ? L.T("Misc.Participates") : L.T("Misc.StoredSample");
        slot.Role.SetResourceReference(TextBlock.ForegroundProperty, index < 3 ? "AccentBrush" : "MutedBrush");
        slot.Words.Text = L.T("Misc.SampleWords", CountWords(text));
        slot.Preview.Text = text;
        if (slot.Editor.Text != text) slot.Editor.Text = text;
        var editing = _editing == index;
        slot.Editor.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        slot.Preview.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
        slot.Editor.IsReadOnly = Locked;
        slot.Up.IsEnabled = !Locked && index > 0;
        slot.Down.IsEnabled = !Locked && index < _slots.Count - 1;
        slot.Delete.IsEnabled = !Locked;
    }

    private SampleSlot CreateSlot()
    {
        var slot = new SampleSlot();
        slot.Edit.Click += (_, _) =>
        {
            var index = _slots.IndexOf(slot);
            if (index < 0) return;
            var opening = _editing != index;
            _editing = opening ? index : -1;
            _focusEditor = opening;
            SyncSlots();
        };
        slot.Up.Click += (_, _) => Move(_slots.IndexOf(slot), -1);
        slot.Down.Click += (_, _) => Move(_slots.IndexOf(slot), 1);
        slot.Delete.Click += (_, _) =>
        {
            var index = _slots.IndexOf(slot);
            if (index < 0) return;
            _samples.RemoveAt(index);
            if (slot.Root.Parent is Panel parent) parent.Children.Remove(slot.Root);
            _slots.RemoveAt(index);
            if (_editing == index) _editing = -1;
            else if (_editing > index) _editing--;
            OnEdited();
        };
        slot.More.Click += (_, _) => OpenMenu(slot.More);
        if (FindResource("TextAction") is Style action)
        {
            slot.Edit.Style = action;
            slot.More.Style = action;
        }
        slot.Editor.TextChanged += (_, _) =>
        {
            if (_syncing || _loading) return;
            var index = _slots.IndexOf(slot);
            if (index < 0) return;
            _samples[index] = slot.Editor.Text;
            OnEdited();
        };
        return slot;
    }

    private void Move(int index, int delta)
    {
        var next = index + delta;
        if (index < 0 || next < 0 || next >= _samples.Count) return;
        (_samples[index], _samples[next]) = (_samples[next], _samples[index]);
        (_slots[index], _slots[next]) = (_slots[next], _slots[index]);
        if (_editing == index) _editing = next;
        else if (_editing == next) _editing = index;
        OnEdited();
    }

    private void OnEdited()
    {
        if (_loading || _syncing) return;
        RefreshSummaries();
        if (_saving) return;
        if (HasUnsaved) SetStatus(L.T("Misc.Dirty"));
        else if (StatusText.Text == L.T("Misc.Dirty")) SetStatus("");
        UpdateSaveEnabled();
    }

    private bool Locked => _readOnly || _saving;

    private void RefreshInteraction()
    {
        foreach (var box in new[] { ForbiddenBox, BannedBox, ReplaceBox })
            box.IsReadOnly = Locked;
        SentenceTerse.IsEnabled = SentenceMedium.IsEnabled = SentenceLong.IsEnabled = !Locked;
        AddSampleButton.IsEnabled = EmptyAddButton.IsEnabled = !Locked;
        SampleAddMenu.IsEnabled = !Locked;
        SaveButton.IsEnabled = HasUnsaved && !Locked;
        for (var i = 0; i < _slots.Count; i++) BindSlot(_slots[i], i);
    }

    private void UpdateSaveEnabled() => RefreshInteraction();

    private void SetStatus(string text)
    {
        StatusText.Text = text;
        var saved = text == L.T("Misc.SavedState");
        var attention = text == L.T("Misc.Dirty") || text == L.T("Misc.SaveFailed");
        StatusDot.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,
            saved ? "OkBrush" : attention ? "AccentBrush" : "MutedBrush");
    }

    private void ApplyWidth()
    {
        if (ActualWidth <= 0) return;
        var narrow = ActualWidth < WideWidth;
        if (_narrow == narrow) return;
        _narrow = narrow;
        if (narrow)
        {
            ColumnGap.Width = new GridLength(0);
            RulesColumnDef.Width = new GridLength(0);
            Grid.SetColumn(RulesColumn, 0);
            Grid.SetRow(RulesColumn, 1);
            RulesColumn.Margin = new Thickness(0, 16, 0, 0);
            Grid.SetRow(SaveButton, 1);
            Grid.SetColumn(SaveButton, 0);
            Grid.SetColumnSpan(SaveButton, 2);
            SaveButton.HorizontalAlignment = HorizontalAlignment.Stretch;
            SaveButton.Margin = new Thickness(0, 8, 0, 0);
        }
        else
        {
            ColumnGap.Width = new GridLength(16);
            RulesColumnDef.Width = new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(RulesColumn, 2);
            Grid.SetRow(RulesColumn, 0);
            RulesColumn.Margin = new Thickness(0);
            Grid.SetRow(SaveButton, 0);
            Grid.SetColumn(SaveButton, 1);
            Grid.SetColumnSpan(SaveButton, 1);
            SaveButton.HorizontalAlignment = HorizontalAlignment.Right;
            SaveButton.Margin = new Thickness(12, 0, 0, 0);
        }
    }

    private void Field_Changed(object sender, TextChangedEventArgs e) => OnEdited();

    private void Sentence_Checked(object sender, RoutedEventArgs e)
    {
        if (StatusText is null) return;
        OnEdited();
    }

    private void Rule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name }) return;
        _openRule = _openRule == name ? null : name;
        ApplyRuleVisibility();
    }

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name } || FindName(name) is not UIElement body) return;
        body.Visibility = body.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        _diagnosticsOpen = !_diagnosticsOpen;
        ApplyDiagnostics();
    }

    private void ToggleSamples_Click(object sender, RoutedEventArgs e)
    {
        _samplesOpen = !_samplesOpen;
        ApplySampleVisibility();
        if (_samplesOpen) SyncSlots();
    }

    private void ToggleExtras_Click(object sender, RoutedEventArgs e)
    {
        _extrasOpen = !_extrasOpen;
        ExtraHost.Visibility = _extrasOpen ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddSample_Click(object sender, RoutedEventArgs e)
    {
        _samples.Add("");
        _editing = _samples.Count - 1;
        _samplesOpen = true;
        if (_editing >= 3) _extrasOpen = true;
        _focusEditor = true;
        OnEdited();
    }

    private void OpenMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button) OpenMenu(button);
    }

    private static void OpenMenu(Button button)
    {
        if (button.ContextMenu is null) return;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveRequested?.Invoke(this, EventArgs.Empty);

    private static List<string> Lines(string text) =>
        text.Replace("\r\n", "\n").Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToList();

    private static List<string> Clean(IEnumerable<string> samples)
    {
        var result = new List<string>();
        foreach (var sample in samples)
        {
            var text = (sample ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
            if (text.Trim().Length > 0) result.Add(text);
        }
        return result;
    }

    private static int CountWords(string text) => text.Count(ch => !char.IsWhiteSpace(ch));

    private void Place(Panel host, int start, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var root = _slots[start + i].Root;
            if (i < host.Children.Count && ReferenceEquals(host.Children[i], root)) continue;
            if (root.Parent is Panel parent) parent.Children.Remove(root);
            host.Children.Insert(Math.Min(i, host.Children.Count), root);
        }
        while (host.Children.Count > count) host.Children.RemoveAt(host.Children.Count - 1);
    }

    private sealed class SampleSlot
    {
        public StackPanel Root { get; } = new() { Margin = new Thickness(0, 0, 0, 8) };
        public TextBlock Role { get; } = new() { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        public TextBlock Words { get; } = new();
        public TextBlock Preview { get; } = new() { TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 44 };
        public TextBox Editor { get; } = new()
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 72,
            MaxHeight = 160,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Visibility = Visibility.Collapsed,
        };
        public Button Edit { get; } = new() { Margin = new Thickness(0, 2, 8, 0), Padding = new Thickness(0), MinHeight = 28 };
        public Button More { get; } = new() { Margin = new Thickness(0, 2, 0, 0), Padding = new Thickness(0), MinHeight = 28 };
        public MenuItem Up { get; } = new();
        public MenuItem Down { get; } = new();
        public MenuItem Delete { get; } = new();

        public SampleSlot()
        {
            Role.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            Words.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            Preview.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            Editor.SetResourceReference(Control.BackgroundProperty, "ControlBgBrush");
            Editor.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            Editor.SetResourceReference(Control.BorderBrushProperty, "BorderBrushKey");
            Editor.SetResourceReference(TextBox.CaretBrushProperty, "TextBrush");
            Edit.SetResourceReference(Control.ForegroundProperty, "AccentBrush");
            More.SetResourceReference(Control.ForegroundProperty, "AccentBrush");
            Edit.Content = L.T("Misc.Edit");
            More.Content = L.T("Misc.More");
            Up.Header = L.T("Misc.MoveUp");
            Down.Header = L.T("Misc.MoveDown");
            Delete.Header = L.T("Common.Delete");
            var menu = new ContextMenu();
            menu.Items.Add(Up);
            menu.Items.Add(Down);
            menu.Items.Add(Delete);
            More.ContextMenu = menu;

            var head = new Grid { Margin = new Thickness(0, 8, 0, 2) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(Words, 1);
            Words.Margin = new Thickness(8, 0, 0, 0);
            head.Children.Add(Role);
            head.Children.Add(Words);
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            actions.Children.Add(Edit);
            actions.Children.Add(More);
            Root.Children.Add(head);
            Root.Children.Add(Preview);
            Root.Children.Add(Editor);
            Root.Children.Add(actions);
        }
    }
}
