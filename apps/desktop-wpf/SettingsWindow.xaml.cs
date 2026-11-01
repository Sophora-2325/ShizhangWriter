using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Models;

namespace NovelGm.Desktop;

public partial class SettingsWindow : Window
{
    private readonly ProjectDto? _project;
    private SettingsDto _settings = new();
    private List<ProviderDto> _providers = new();
    private List<ProviderPresetDto> _presets = new();
    private List<SkillDto> _skills = new();
    private List<PluginDto> _plugins = new();
    private bool _loading = true;
    private string _page = SettingsPage.General;
    private bool _navSync;
    private bool _sttLoaded;
    private bool _modelsLoaded;
    private bool _skillsLoaded;
    private bool _pluginsLoaded;
    private int _pending;
    private bool _closeAfter;
    private bool _stacked;

    public ProjectDto? UpdatedProject { get; private set; }

    public SettingsWindow(ProjectDto? project)
    {
        InitializeComponent();
        _project = project;
        FitWorkArea();
        Select(LanguageCombo, LanguageManager.Current);
        SelectTheme(ThemeManager.Mode);
        SelectAccent(ThemeManager.Accent);
        SelectBodyFont(UiPrefs.Current.BodyFont);
        if (_project is null) ProjectRow.Visibility = Visibility.Collapsed;
        LanguageManager.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => LanguageManager.LanguageChanged -= OnLanguageChanged;
        Closing += Window_Closing;
        ShowPage(SettingsPage.General);
        _loading = false;
    }

    private void FitWorkArea()
    {
        var area = SystemParameters.WorkArea;
        MaxWidth = Math.Max(520, area.Width - 16);
        MaxHeight = Math.Max(420, area.Height - 16);
        Width = Math.Min(Width, MaxWidth);
        Height = Math.Min(Height, MaxHeight);
    }

    private async Task RefreshSttStatusAsync()
    {
        try
        {
            using var stt = new SttClient();
            var info = await stt.StatusAsync();
            SttStatusText.Text = info.Available ? L.T("Stt.Available") : L.T("Stt.NotInstalled");
        }
        catch (Exception)
        {
            SttStatusText.Text = L.T("Stt.NotInstalled");
        }
    }

    private void ShowPage(string page)
    {
        if (!SettingsPage.IsKnown(page) || GeneralPage is null) return;
        _page = page;
        GeneralPage.Visibility = page == SettingsPage.General ? Visibility.Visible : Visibility.Collapsed;
        ThemePage.Visibility = page == SettingsPage.Appearance ? Visibility.Visible : Visibility.Collapsed;
        ModelPage.Visibility = page == SettingsPage.Models ? Visibility.Visible : Visibility.Collapsed;
        AdvancedPage.Visibility = page == SettingsPage.Advanced ? Visibility.Visible : Visibility.Collapsed;
        SkillsPage.Visibility = page == SettingsPage.Skills ? Visibility.Visible : Visibility.Collapsed;
        PluginsPage.Visibility = page == SettingsPage.Plugins ? Visibility.Visible : Visibility.Collapsed;
        if (SettingsPage.IsPrimary(page)) SelectNav(page);
        ShowError("");
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ApplyPairLayout));
        _ = EnsurePage(page);
    }

    private void SelectNav(string page)
    {
        _navSync = true;
        foreach (var item in NavList.Items.OfType<FrameworkElement>())
        {
            if ((item.Tag as string) == page)
            {
                NavList.SelectedItem = item;
                break;
            }
        }
        _navSync = false;
    }

    private async Task EnsurePage(string page)
    {
        try
        {
            if (page == SettingsPage.General && !_sttLoaded)
            {
                await RefreshSttStatusAsync();
                _sttLoaded = true;
            }
            else if (page == SettingsPage.Models && !_modelsLoaded)
            {
                _providers = await App.Api.ListProvidersAsync();
                Apply(await App.Api.GetSettingsAsync());
                FillProviderCombos();
                RenderProviders();
                _modelsLoaded = true;
            }
            else if (page == SettingsPage.Skills && !_skillsLoaded)
            {
                await ReloadSkills();
                _skillsLoaded = true;
            }
            else if (page == SettingsPage.Plugins && !_pluginsLoaded)
            {
                await ReloadPlugins();
                _pluginsLoaded = true;
            }
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void OpenSkills_Click(object sender, RoutedEventArgs e) => ShowPage(SettingsPage.Skills);

    private void OpenPlugins_Click(object sender, RoutedEventArgs e) => ShowPage(SettingsPage.Plugins);

    private void BackAdvanced_Click(object sender, RoutedEventArgs e) => ShowPage(SettingsPage.Advanced);

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (_page == SettingsPage.General) _sttLoaded = false;
        else if (_page == SettingsPage.Models) _modelsLoaded = false;
        else if (_page == SettingsPage.Skills) _skillsLoaded = false;
        else if (_page == SettingsPage.Plugins) _pluginsLoaded = false;
        else return;
        ShowError("");
        await EnsurePage(_page);
    }

    private void ContentHost_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyPairLayout();

    private void ApplyPairLayout()
    {
        if (ContentHost is null) return;
        _stacked = ContentHost.ActualWidth > 0 && ContentHost.ActualWidth < 560;
        foreach (var grid in FindPairs(ContentHost))
        {
            if (grid.Children.Count < 2 || grid.Children[1] is not FrameworkElement control) continue;
            if (grid.RowDefinitions.Count == 0)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (_stacked)
            {
                Grid.SetRow(control, 1);
                Grid.SetColumn(control, 0);
                control.Margin = new Thickness(0, 8, 0, 0);
                control.HorizontalAlignment = HorizontalAlignment.Left;
            }
            else
            {
                Grid.SetRow(control, 0);
                Grid.SetColumn(control, 1);
                control.Margin = new Thickness(0);
                control.HorizontalAlignment = HorizontalAlignment.Right;
            }
        }
    }

    private static IEnumerable<Grid> FindPairs(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Grid grid && Equals(grid.Tag, "pair")) yield return grid;
            foreach (var nested in FindPairs(child)) yield return nested;
        }
    }

    private void Apply(SettingsDto s)
    {
        var wasLoading = _loading;
        _loading = true;
        _settings = s;
        OfflineToggle.IsChecked = s.Offline_Mode;
        UpdateProviderControls();
        _loading = wasLoading;
    }

    /// <summary>按已添加的模型配置重建两个下拉框；还没配置完整的标「（未配置）」。</summary>
    private void FillProviderCombos()
    {
        var loading = _loading;
        _loading = true;
        FillProviders(DefaultProviderCombo, withInherit: false);
        FillProviders(ProjectProviderCombo, withInherit: true);
        Select(ProjectProviderCombo, UpdatedProject?.Provider ?? _project?.Provider ?? "inherit");
        UpdateProviderControls();
        _loading = loading;
    }

    private void FillProviders(ComboBox combo, bool withInherit)
    {
        combo.Items.Clear();
        if (withInherit) combo.Items.Add(new ComboBoxItem { Content = L.T("Set.ProviderInherit"), Tag = "inherit" });
        foreach (var p in _providers)
        {
            var text = p.Configured ? p.DisplayName : L.T("Set.NotConfiguredName", p.DisplayName);
            combo.Items.Add(new ComboBoxItem { Content = text, Tag = p.Id });
        }
        combo.Items.Add(new ComboBoxItem { Content = L.T("Set.ProviderMock"), Tag = "mock" });
    }

    private void UpdateProviderControls()
    {
        var offline = _settings.Offline_Mode;
        Select(DefaultProviderCombo, _settings.Provider);
        DefaultProviderCombo.IsEnabled = !offline;
        var current = _providers.FirstOrDefault(p => p.Id == _settings.Provider);
        var ready = current?.Configured ?? _settings.Provider == "mock";
        var inheritName = _settings.Provider == "mock"
            ? L.T("Set.ProviderMock")
            : current?.DisplayName ?? _settings.Provider;
        DefaultProviderDesc.Text = L.T(offline ? "Set.OfflineFixed"
            : _providers.Count == 0 && _settings.Provider != "mock" ? "Set.NoModelsHint"
            : ready ? "Set.DefaultProviderDesc" : "Set.ProviderNotReady");

        ProjectRow.Visibility = _project is null ? Visibility.Collapsed : Visibility.Visible;
        ProjectProviderCombo.IsEnabled = _project is not null && !offline;
        if (_project is null) return;
        var selected = UpdatedProject?.Provider ?? _project.Provider;
        ProjectProviderDesc.Text = offline
            ? L.T("Set.ProjectOffline")
            : selected == "inherit"
                ? L.T("Set.ProjectInherit", _project.Title, inheritName)
                : L.T("Set.ProjectOwn", _project.Title);
    }

    private bool HasConfiguredLocal() => _providers.Any(p => p.Kind == "local" && p.Configured);

    private async void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (_modelsLoaded)
        {
            FillProviderCombos();
            RenderProviders();
        }
        if (_skillsLoaded) RenderSkills();
        if (_pluginsLoaded) RenderPlugins();
        if (_sttLoaded) await RefreshSttStatusAsync();
    }

    private static string SelectedTag(ComboBox combo) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    private static void Select(ComboBox combo, string tag)
    {
        if (combo.Items.Count == 0) return;
        foreach (ComboBoxItem item in combo.Items)
        {
            if ((item.Tag as string) == tag)
            {
                combo.SelectedItem = item;
                return;
            }
        }
        combo.SelectedIndex = 0;
    }

    private void SelectTheme(string mode)
    {
        var card = mode switch
        {
            ThemeManager.Dark => ThemeDarkCard,
            ThemeManager.System => ThemeSystemCard,
            _ => ThemeLightCard,
        };
        card.IsChecked = true;
    }

    private void SelectAccent(string accent)
    {
        var card = accent switch
        {
            ThemeManager.Dianqing => AccentDianqingCard,
            ThemeManager.Songlv => AccentSonglvCard,
            _ => AccentZhushaCard,
        };
        card.IsChecked = true;
    }

    private void SelectBodyFont(string font)
    {
        foreach (ComboBoxItem item in BodyFontCombo.Items)
            if ((string?)item.Tag == font) { BodyFontCombo.SelectedItem = item; return; }
        BodyFontCombo.SelectedIndex = 0;
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_navSync || GeneralPage is null || NavList.SelectedItem is not FrameworkElement item) return;
        if (item.Tag is string page && SettingsPage.IsPrimary(page)) ShowPage(page);
    }

    // ---- general ----

    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var lang = SelectedTag(LanguageCombo);
        if (!string.IsNullOrEmpty(lang)) LanguageManager.Apply(lang);
    }

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: string mode }) return;
        if (mode != ThemeManager.Mode) ThemeManager.Apply(mode);
    }

    // ---- theme ----

    private void Accent_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: string accent }) return;
        if (accent != ThemeManager.Accent) ThemeManager.ApplyAccent(accent);
    }

    private void BodyFont_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || BodyFontCombo.SelectedItem is not ComboBoxItem { Tag: string font }) return;
        if (font == UiPrefs.Current.BodyFont) return;
        UiPrefs.Current.BodyFont = font;
        UiPrefs.Save();
        ThemeManager.ApplyBodyFont();
    }

    private async void DefaultProvider_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var provider = SelectedTag(DefaultProviderCombo);
        if (string.IsNullOrEmpty(provider) || provider == _settings.Provider) return;
        var previous = _settings.Provider;
        var ok = await Run(async () => Apply(await App.Api.UpdateSettingsAsync(new { provider })), DefaultProviderCombo);
        if (!ok)
        {
            _loading = true;
            Select(DefaultProviderCombo, previous);
            _loading = false;
        }
        RenderProviders();
    }

    private async void Offline_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var want = OfflineToggle.IsChecked == true;
        if (!ModelChoiceRules.AllowLocalOnly(want, _settings.Offline_Mode, HasConfiguredLocal()))
        {
            _loading = true;
            OfflineToggle.IsChecked = _settings.Offline_Mode;
            _loading = false;
            ShowError(L.T("Set.NeedLocal"));
            return;
        }
        var ok = await Run(async () => Apply(await App.Api.UpdateSettingsAsync(new { offline_mode = want })), OfflineToggle);
        if (!ok)
        {
            _loading = true;
            OfflineToggle.IsChecked = _settings.Offline_Mode;
            _loading = false;
        }
        UpdateProviderControls();
        RenderProviders();
    }

    private async void ProjectProvider_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _project is null) return;
        var provider = SelectedTag(ProjectProviderCombo);
        var current = UpdatedProject?.Provider ?? _project.Provider;
        if (provider == current) return;
        var ok = await Run(async () =>
            UpdatedProject = await App.Api.PatchProviderAsync(_project.Id, provider), ProjectProviderCombo);
        if (!ok)
        {
            _loading = true;
            Select(ProjectProviderCombo, current);
            _loading = false;
        }
    }

    // ---- models ----

    private void RenderProviders()
    {
        ProviderList.Children.Clear();
        foreach (var p in _providers) ProviderList.Children.Add(ProviderCard(p));
        ProvidersEmpty.Visibility = _providers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string PriceText(ProviderDto p)
    {
        if (!p.Price_Known || p.Price is null) return L.T("Set.PriceUnknown");
        if (p.Price.Input_Miss == 0 && p.Price.Output == 0) return L.T("Set.PriceFree");
        return L.T("Set.PriceShow", p.Price.Input_Miss, p.Price.Output);
    }

    private string ProviderStatus(ProviderDto p) => p.Kind switch
    {
        "local" => p.Base_Url,
        "custom" when string.IsNullOrEmpty(p.Base_Url) => L.T("Set.NeedBaseUrl"),
        "custom" => p.Base_Url,
        _ => p.Has_Key ? L.T("Set.KeySaved", p.Key_Hint) : L.T("Set.KeyMissing"),
    };

    private Border ProviderCard(ProviderDto p)
    {
        var isDefault = p.Id == _settings.Provider;
        var tag = p.PresetDisplayName + (isDefault ? " · " + L.T("Set.IsDefault") : "");
        var lines = new List<string> { (p.Configured ? "● " : "") + ProviderStatus(p) };
        if (!string.IsNullOrEmpty(p.Model_Strong)) lines.Add($"{p.Model_Strong} / {p.Model_Fast} · {PriceText(p)}");
        var actions = new List<FrameworkElement>
        {
            CardButton(L.T("Common.Edit"), (_, _) => EditProvider(p)),
            CardButton(L.T("Common.Delete"), (_, _) => DeleteProvider(p)),
        };
        return Card(p.DisplayName, tag, string.Join("\n", lines), actions.ToArray());
    }

    private async Task<List<ProviderPresetDto>?> Presets()
    {
        if (_presets.Count == 0) await Run(async () => _presets = await App.Api.ListPresetsAsync());
        return _presets.Count == 0 ? null : _presets;
    }

    private async void AddModel_Click(object sender, RoutedEventArgs e)
    {
        var presets = await Presets();
        if (presets is null) return;
        var w = new ProviderEditorWindow(presets, _providers) { Owner = this };
        if (w.ShowDialog() != true || w.Saved is null) return;
        var first = _providers.Count == 0;
        ReplaceProvider(w.Saved);
        // 第一条配置自动成为默认，免得还要去通用页再选一次
        if (first && OfflineToggle.IsChecked != true)
            await Run(async () => Apply(await App.Api.UpdateSettingsAsync(new { provider = w.Saved.Id })));
        RefreshProviders();
    }

    private async void EditProvider(ProviderDto p)
    {
        var presets = await Presets();
        if (presets is null) return;
        var w = new ProviderEditorWindow(presets, _providers, p) { Owner = this };
        if (w.ShowDialog() != true) return;
        await Run(async () => _providers = await App.Api.ListProvidersAsync());
        RefreshProviders();
    }

    private async void DeleteProvider(ProviderDto p)
    {
        if (!Confirm(L.T("Set.DeleteModelConfirm", p.DisplayName))) return;
        await Run(async () =>
        {
            await App.Api.DeleteProviderAsync(p.Id);
            _providers = await App.Api.ListProvidersAsync();
            Apply(await App.Api.GetSettingsAsync());
        });
        RefreshProviders();
    }

    private void ReplaceProvider(ProviderDto updated)
    {
        var i = _providers.FindIndex(x => x.Id == updated.Id);
        if (i >= 0) _providers[i] = updated; else _providers.Add(updated);
    }

    private void RefreshProviders()
    {
        RenderProviders();
        FillProviderCombos();
    }

    // ---- skills ----

    private void RenderSkills()
    {
        SkillList.Children.Clear();
        foreach (var skill in _skills)
        {
            var scope = L.T(skill.Applies_To switch
            {
                "plan" => "Skill.ScopePlan",
                "generate" => "Skill.ScopeGenerate",
                _ => "Skill.ScopeBoth",
            });
            if (skill.Scenes.Count > 0)
                scope += " · " + string.Join(" / ", skill.Scenes.Select(s => L.T($"Scene.{s}")));
            if (skill.Builtin) scope += " · " + L.T("Skill.Builtin");
            if (skill.Update_Available) scope += " · " + L.T("Skill.UpdateAvailable");
            var actions = new List<FrameworkElement>();
            if (skill.Update_Available)
                actions.Add(CardButton(L.T("Skill.Reset"), (_, _) => ResetSkill(skill)));
            actions.Add(CardButton(L.T("Common.Edit"), (_, _) => EditSkill(skill)));
            actions.Add(CardButton(L.T("Common.Delete"), (_, _) => DeleteSkill(skill)));
            actions.Add(CardSwitch(skill.Enabled, async on =>
            {
                var updated = await App.Api.SetSkillEnabledAsync(skill.Id, on);
                skill.Enabled = updated.Enabled;
            }));
            SkillList.Children.Add(Card(skill.Name, scope, skill.Description, skill.Source, actions.ToArray()));
        }
        SkillsEmpty.Visibility = _skills.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task ReloadSkills()
    {
        _skills = await App.Api.ListSkillsAsync();
        RenderSkills();
    }

    private async void NewSkill_Click(object sender, RoutedEventArgs e)
    {
        if (new SkillEditorWindow(null) { Owner = this }.ShowDialog() == true)
            await Run(ReloadSkills);
    }

    private async void DiscoverSkills_Click(object sender, RoutedEventArgs e)
    {
        var w = new SkillDiscoverWindow { Owner = this };
        w.ShowDialog();
        if (w.Changed) await Run(ReloadSkills);
    }

    private async void EditSkill(SkillDto skill)
    {
        if (new SkillEditorWindow(skill) { Owner = this }.ShowDialog() == true)
            await Run(ReloadSkills);
    }

    private async void ResetSkill(SkillDto skill)
    {
        if (!Confirm(L.T("Skill.ResetConfirm", skill.Name))) return;
        await Run(async () =>
        {
            await App.Api.ResetSkillAsync(skill.Id);
            await ReloadSkills();
        });
    }

    private async void DeleteSkill(SkillDto skill)
    {
        if (!Confirm(L.T("Skill.DeleteConfirm", skill.Name))) return;
        await Run(async () =>
        {
            await App.Api.DeleteSkillAsync(skill.Id);
            await ReloadSkills();
        });
    }

    private async void ImportSkill_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = L.T("Skill.ImportTitle"), Filter = L.T("Skill.ImportFilter") };
        if (dlg.ShowDialog(this) != true) return;
        var path = dlg.FileName;
        if (string.Equals(Path.GetFileName(path), "SKILL.md", StringComparison.OrdinalIgnoreCase))
            path = Path.GetDirectoryName(path) ?? path;
        await Run(async () =>
        {
            await App.Api.ImportSkillAsync(path);
            await ReloadSkills();
        });
    }

    private void OpenSkillsFolder_Click(object sender, RoutedEventArgs e) => OpenDataFolder("skills");

    // ---- plugins ----

    private void RenderPlugins()
    {
        PluginList.Children.Clear();
        foreach (var plugin in _plugins)
        {
            var desc = string.Join("\n", new[]
            {
                string.IsNullOrEmpty(plugin.Author) ? "" : L.T("Plugin.Author", plugin.Author),
                plugin.Description,
            }.Where(s => !string.IsNullOrEmpty(s)));
            var delete = CardButton(L.T("Common.Delete"), (_, _) => DeletePlugin(plugin));
            var toggle = CardSwitch(plugin.Enabled, async on =>
            {
                var updated = await App.Api.SetPluginEnabledAsync(plugin.Id, on);
                plugin.Enabled = updated.Enabled;
            });
            PluginList.Children.Add(Card(plugin.Name, L.T("Plugin.Version", plugin.Version), desc, delete, toggle));
        }
        PluginsEmpty.Visibility = _plugins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task ReloadPlugins()
    {
        _plugins = await App.Api.ListPluginsAsync();
        RenderPlugins();
    }

    private async void ImportPlugin_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = L.T("Plugin.ImportTitle"), Filter = L.T("Plugin.ImportFilter") };
        if (dlg.ShowDialog(this) != true) return;
        await Run(async () =>
        {
            await App.Api.ImportPluginAsync(dlg.FileName);
            await ReloadPlugins();
        });
    }

    private async void DeletePlugin(PluginDto plugin)
    {
        if (!Confirm(L.T("Plugin.DeleteConfirm", plugin.Name))) return;
        await Run(async () =>
        {
            await App.Api.DeletePluginAsync(plugin.Id);
            await ReloadPlugins();
        });
    }

    private void OpenPluginsFolder_Click(object sender, RoutedEventArgs e) => OpenDataFolder("plugins");

    // ---- cards ----

    private Border Card(string title, string tag, string description, params FrameworkElement[] actions)
        => Card(title, tag, description, null, actions);

    private Border Card(string title, string tag, string description, string? source, params FrameworkElement[] actions)
    {
        var name = new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var tagText = new TextBlock { Text = tag, FontSize = 12 };
        tagText.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(name);
        header.Children.Add(new Border { Style = (Style)FindResource("SetTag"), Child = tagText });

        var left = new StackPanel { Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(header);
        if (!string.IsNullOrWhiteSpace(description))
            left.Children.Add(new TextBlock { Text = description, Style = (Style)FindResource("SetRowDesc") });
        if (!string.IsNullOrWhiteSpace(source))
        {
            var text = new TextBox { Text = source, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0), Style = (Style)FindResource("SelectableText") };
            text.SetResourceReference(Control.ForegroundProperty, "MutedBrush");
            var details = new Expander { IsExpanded = false, Content = text, Margin = new Thickness(0, 6, 0, 0) };
            details.SetResourceReference(HeaderedContentControl.HeaderProperty, "Skill.ViewSource");
            System.Windows.Automation.AutomationProperties.SetName(text, L.T("Skill.ViewSource"));
            left.Children.Add(details);
        }

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var a in actions) right.Children.Add(a);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return new Border { Style = (Style)FindResource("SetCard"), Child = grid };
    }

    private Button CardButton(string text, RoutedEventHandler click)
    {
        var b = new Button { Content = text, Style = (Style)FindResource("SetButton"), Margin = new Thickness(0, 0, 8, 0) };
        b.Click += click;
        return b;
    }

    private ToggleButton CardSwitch(bool on, Func<bool, Task> apply)
    {
        var t = new ToggleButton
        {
            Style = (Style)FindResource("SetSwitch"),
            IsChecked = on,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var reverting = false;
        async void Changed(object sender, RoutedEventArgs e)
        {
            if (reverting) return;
            var want = t.IsChecked == true;
            if (await Run(() => apply(want), t)) return;
            reverting = true;
            t.IsChecked = !want;
            reverting = false;
        }
        t.Checked += Changed;
        t.Unchecked += Changed;
        return t;
    }

    private bool Confirm(string message) =>
        MessageBox.Show(this, message, L.T("Set.Title"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    // ---- window ----

    private void OpenConfig_Click(object sender, RoutedEventArgs e) => OpenDataFolder(null);

    private void OpenSttModelDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(local, "novel-gm", "stt");
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private async void OpenDataFolder(string? sub)
    {
        await Run(async () =>
        {
            var dir = (await App.Api.HealthAsync()).Paths.Storage;
            if (!Directory.Exists(dir)) throw new InvalidOperationException(L.T("Set.NoDataDir"));
            if (sub is not null)
            {
                dir = Path.Combine(dir, sub);
                Directory.CreateDirectory(dir);
            }
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        });
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => RequestClose();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) RequestClose();
    }

    private void RequestClose()
    {
        if (_pending > 0)
        {
            _closeAfter = true;
            return;
        }
        Close();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_pending == 0) return;
        e.Cancel = true;
        _closeAfter = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        PageError.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task<bool> Run(Func<Task> action, UIElement? scope = null)
    {
        _pending++;
        if (scope is not null) scope.IsEnabled = false;
        try
        {
            ShowError("");
            await action();
            return true;
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            return false;
        }
        finally
        {
            if (scope is not null) scope.IsEnabled = true;
            _pending--;
            if (_pending == 0 && _closeAfter)
            {
                _closeAfter = false;
                Close();
            }
        }
    }
}
