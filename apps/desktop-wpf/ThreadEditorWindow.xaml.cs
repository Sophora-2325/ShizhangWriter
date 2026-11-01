using System.Windows;
using System.Windows.Controls;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Models;

namespace NovelGm.Desktop;

public partial class ThreadEditorWindow : Window
{
    /// <summary>内置类型，与后端 PlotThread.kind 的既有取值一致。</summary>
    private static readonly string[] BuiltinKinds = { "main", "sub", "romance", "mystery" };

    private const string OtherTag = "__other__";
    private const string CustomPrefix = "custom:";

    private readonly PlotThreadDto? _existing;
    private bool _syncing;
    private string _selectedKind = "sub";
    private readonly List<ThreadMilestoneDto> _milestones = new();
    private string? _section;
    private bool _saving;
    private readonly string _id;
    public bool SavedSuccessfully { get; private set; }
    public Func<PlotThreadDto, Task>? SaveAsync { get; set; }

    public PlotThreadDto Result { get; private set; } = new();

    public ThreadEditorWindow(PlotThreadDto? existing)
    {
        InitializeComponent();
        _existing = existing;
        _id = string.IsNullOrWhiteSpace(existing?.Id) ? Guid.NewGuid().ToString("N")[..12] : existing.Id;
        Title = existing is null ? L.T("Thread.TitleNew") : L.T("Thread.TitleEdit", existing.Name);
        NameBox.Text = existing?.Name ?? "";
        DirectiveBox.Text = existing?.User_Directive ?? "";
        ToneBox.Text = existing?.Tone ?? "";
        MiscBox.Text = existing?.Misc ?? "";
        _milestones.AddRange(existing?.Milestones.Select(m => m.Copy()) ?? Enumerable.Empty<ThreadMilestoneDto>());
        if (existing is not null && _milestones.Count == 0 && !string.IsNullOrWhiteSpace(existing.Next_Milestone))
            _milestones.Add(new() { Id = "legacy-next", Name = existing.Next_Milestone });
        Select(TimeCombo, existing?.Time_Position ?? "present");
        Select(StateCombo, existing?.State ?? "seed");
        PopulateKinds(existing?.Kind ?? "sub");
        RenderMilestones();
        Loaded += (_, _) => FitWindow(640);
        Closing += (_, e) => { if (_saving) e.Cancel = true; };
    }

    /// <summary>填类型下拉：4 个内置 + 本机存过的自定义 + 末尾「其他…」。</summary>
    private void PopulateKinds(string current)
    {
        var before = _syncing;
        _syncing = true;
        try
        {
            // 编辑一条类型不在列表里的旧脉络时，把它的类型补进来，免得一打开就被改掉
            if (!IsBuiltin(current)
                && !UiPrefs.Current.ThreadKinds.Contains(current, StringComparer.OrdinalIgnoreCase))
            {
                UiPrefs.Current.ThreadKinds.Add(current);
                UiPrefs.Save();
            }

            KindCombo.Items.Clear();
            foreach (var kind in BuiltinKinds)
                KindCombo.Items.Add(new ComboBoxItem { Content = L.T("ThreadKind." + kind), Tag = kind });
            foreach (var kind in UiPrefs.Current.ThreadKinds)
                KindCombo.Items.Add(new ComboBoxItem { Content = kind, Tag = CustomPrefix + kind });
            KindCombo.Items.Add(new ComboBoxItem { Content = L.T("Thread.KindOther"), Tag = OtherTag });

            _selectedKind = current;
            SelectKind(current);
        }
        finally
        {
            _syncing = before;
        }
    }

    private static bool IsBuiltin(string kind) =>
        BuiltinKinds.Contains(kind, StringComparer.OrdinalIgnoreCase);

    /// <summary>选中某个类型；找不到对应项时退回第一项。</summary>
    private void SelectKind(string kind)
    {
        var wanted = IsBuiltin(kind) ? kind : CustomPrefix + kind;
        KindCombo.SelectedItem = KindCombo.Items.Cast<ComboBoxItem>()
                                     .FirstOrDefault(i => (string)i.Tag == wanted)
                                 ?? KindCombo.Items[0];
    }

    private void Kind_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        if (KindCombo.SelectedItem is not ComboBoxItem item) return;

        var tag = item.Tag as string ?? "";
        if (tag != OtherTag)
        {
            _selectedKind = tag.StartsWith(CustomPrefix, StringComparison.Ordinal)
                ? tag[CustomPrefix.Length..]
                : tag;
            return;
        }

        // 选了「其他…」：弹单行输入框，填完加进本机列表
        var typed = AskCustomKind();
        var previous = _selectedKind;
        if (string.IsNullOrWhiteSpace(typed))
        {
            _syncing = true;
            try { SelectKind(previous); } finally { _syncing = false; }
            return;
        }
        if (IsBuiltin(typed))
        {
            _selectedKind = typed;
            _syncing = true;
            try { SelectKind(typed); } finally { _syncing = false; }
            return;
        }

        var saved = UiPrefs.Current.ThreadKinds
            .FirstOrDefault(t => string.Equals(t, typed, StringComparison.OrdinalIgnoreCase));
        if (saved is null)
        {
            saved = typed;
            UiPrefs.Current.ThreadKinds.Add(saved);
            UiPrefs.Save();
        }
        // 首次输入的类型还不在下拉里，重建一次再选中
        PopulateKinds(saved);
    }

    private string? AskCustomKind()
    {
        var dlg = new RenameWindow(
            L.T("Thread.KindOtherTitle"),
            L.T("Thread.KindOtherPrompt"),
            "",
            L.T("Thread.KindOtherHint")) { Owner = this };
        return dlg.ShowDialog() == true ? dlg.Value : null;
    }

    private static void Select(ComboBox combo, string tag)
    {
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag)
                             ?? combo.Items[0];
    }

    private static string SelectedTag(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_saving) return;
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            ErrorText.Text = L.T("Thread.NameRequired");
            NameBox.Focus();
            return;
        }
        if (_milestones.Any(m => string.IsNullOrWhiteSpace(m.Name)))
        {
            ErrorText.Text = L.T("Thread.MilestoneNameRequired");
            if (_section != "milestones") ShowSection("milestones");
            return;
        }
        Result = new PlotThreadDto
        {
            Id = _id,
            Priority = _existing?.Priority ?? 1,
            Name = NameBox.Text.Trim(),
            Kind = _selectedKind,
            State = SelectedTag(StateCombo),
            User_Directive = DirectiveBox.Text.Trim(),
            Time_Position = SelectedTag(TimeCombo),
            Tone = ToneBox.Text.Trim(),
            Misc = MiscBox.Text.Replace("\r\n", "\n").Trim(),
            Milestones = _milestones.Select(m => { var copy = m.Copy(); copy.Name = copy.Name.Trim(); return copy; }).ToList(),
            Next_Milestone = _milestones.FirstOrDefault(m => !m.Completed)?.Name.Trim() ?? "",
        };
        _saving = true;
        EditorForm.IsEnabled = SaveButton.IsEnabled = CancelButton.IsEnabled = false;
        ErrorText.Text = "";
        try
        {
            if (SaveAsync is not null) await SaveAsync(Result);
            _saving = false;
            CloseWithResult(true);
        }
        catch (Exception ex) { ErrorText.Text = L.T("Thread.SaveFailed", ex.Message); }
        finally
        {
            _saving = false;
            EditorForm.IsEnabled = SaveButton.IsEnabled = CancelButton.IsEnabled = true;
        }
    }

    private void FitWindow(double width)
    {
        var area = SystemParameters.WorkArea;
        MinWidth = Math.Min(480, area.Width);
        MinHeight = Math.Min(400, area.Height);
        MaxWidth = area.Width; MaxHeight = area.Height;
        Width = Math.Min(width, area.Width); Height = Math.Min(Height, area.Height);
        Left = Math.Clamp(Left, area.Left, area.Right - Width);
        Top = Math.Clamp(Top, area.Top, area.Bottom - Height);
    }

    private void Section_Click(object sender, RoutedEventArgs e) => ShowSection((string)((Button)sender).Tag);

    internal void ShowSection(string section)
    {
        _section = _section == section ? null : section;
        DetailColumn.Width = _section is null ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        DetailPane.Visibility = _section is null ? Visibility.Collapsed : Visibility.Visible;
        DirectiveBox.Visibility = _section == "development" ? Visibility.Visible : Visibility.Collapsed;
        ToneBox.Visibility = _section == "tone" ? Visibility.Visible : Visibility.Collapsed;
        MiscBox.Visibility = _section == "misc" ? Visibility.Visible : Visibility.Collapsed;
        MilestonePane.Visibility = _section == "milestones" ? Visibility.Visible : Visibility.Collapsed;
        DetailTitle.Text = L.T(_section switch { "development" => "Thread.Development", "tone" => "Thread.Tone", "misc" => "Thread.Misc", _ => "Thread.Milestones" });
        DetailTitle.TextAlignment = _section == "tone" ? TextAlignment.Center : TextAlignment.Left;
        foreach (var button in new[] { DevelopmentButton, MilestonesButton, ToneButton, MiscButton })
            button.SetResourceReference(Control.BackgroundProperty, (string)button.Tag == _section ? "InfoBgBrush" : "Bg3Brush");
        FitWindow(_section is null ? 640 : 960);
    }

    private void AddMilestone_Click(object sender, RoutedEventArgs e)
    {
        _milestones.Add(new()); RenderMilestones();
        if (MilestoneList.Children[^1] is Border { Child: StackPanel stack } && stack.Children.OfType<TextBox>().FirstOrDefault() is { } name)
        { name.Focus(); name.BringIntoView(); }
    }

    private void RenderMilestones()
    {
        MilestoneList.Children.Clear();
        if (_milestones.Count == 0)
        {
            var empty = new TextBlock { Text = L.T("Thread.NoMilestones"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            MilestoneList.Children.Add(empty);
        }
        for (var i = 0; i < _milestones.Count; i++)
        {
            var node = _milestones[i]; var index = i;
            var stack = new StackPanel();
            var done = new CheckBox { Content = L.T("Thread.NodeStatus", i + 1), IsChecked = node.Completed, Margin = new Thickness(0, 0, 0, 6) };
            done.Checked += (_, _) => node.Completed = true; done.Unchecked += (_, _) => node.Completed = false;
            stack.Children.Add(done);
            stack.Children.Add(new TextBlock { Text = L.T("Thread.NodeName"), Margin = new Thickness(0, 0, 0, 4), FontSize = 12 });
            var name = new TextBox { Text = node.Name, Margin = new Thickness(0, 0, 0, 6) };
            System.Windows.Automation.AutomationProperties.SetName(name, L.T("Thread.NodeName"));
            name.TextChanged += (_, _) => node.Name = name.Text;
            stack.Children.Add(name);
            stack.Children.Add(new TextBlock { Text = L.T("Thread.NodeDescription"), Margin = new Thickness(0, 0, 0, 4), FontSize = 12 });
            var description = new TextBox { Text = node.Description, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                MinHeight = 64, MaxHeight = 160, VerticalContentAlignment = VerticalAlignment.Top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            System.Windows.Automation.AutomationProperties.SetName(description, L.T("Thread.NodeDescription"));
            description.TextChanged += (_, _) => node.Description = description.Text;
            stack.Children.Add(description);
            var actions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            void Action(string key, Action action, bool enabled = true)
            {
                var button = new Button { Content = L.T(key), IsEnabled = enabled, Padding = new Thickness(8, 4, 8, 4) };
                button.Click += (_, _) => action(); actions.Children.Add(button);
            }
            Action("Common.Edit", () => { name.Focus(); name.SelectAll(); });
            Action("Thread.MoveUp", () => { (_milestones[index - 1], _milestones[index]) = (_milestones[index], _milestones[index - 1]); RenderMilestones(); }, i > 0);
            Action("Thread.MoveDown", () => { (_milestones[index + 1], _milestones[index]) = (_milestones[index], _milestones[index + 1]); RenderMilestones(); }, i < _milestones.Count - 1);
            Action("Common.Delete", () => { _milestones.Remove(node); RenderMilestones(); });
            stack.Children.Add(actions);
            var border = new Border { Child = stack, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 8, 0, 12), Margin = new Thickness(0, 0, 0, 4) };
            border.SetResourceReference(Border.BorderBrushProperty, "BorderBrushKey");
            MilestoneList.Children.Add(border);
        }
    }

    private void CloseWithResult(bool saved)
    {
        SavedSuccessfully = saved;
        try { DialogResult = saved; }
        catch (InvalidOperationException) { Close(); }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) { if (!_saving) CloseWithResult(false); }
}
