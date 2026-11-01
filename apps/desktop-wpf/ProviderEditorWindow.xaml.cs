using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Models;

namespace NovelGm.Desktop;

/// <summary>添加或编辑一条模型配置：新建时先选预设厂商，再填名称、密钥、地址和模型。
/// 「测试连接」和「获取模型列表」用表单里还没保存的内容；保存成功后 DialogResult = true。</summary>
public partial class ProviderEditorWindow : Window
{
    private static readonly string[] Groups = { "intl", "cn", "local" };

    private readonly ProviderDto? _existing;
    private readonly List<ProviderPresetDto> _presets;
    private readonly HashSet<string> _takenNames;
    private ProviderPresetDto? _preset;
    private string _autoName = "";
    private bool _hasKey;

    public ProviderDto? Saved { get; private set; }

    public ProviderEditorWindow(List<ProviderPresetDto> presets, IEnumerable<ProviderDto> configs, ProviderDto? existing = null)
    {
        InitializeComponent();
        _presets = presets;
        _existing = existing;
        _takenNames = configs.Where(c => c.Id != existing?.Id)
            .Select(c => c.DisplayName.ToLowerInvariant()).ToHashSet();

        if (existing is null)
        {
            BuildPresetChips();
            FormPanel.IsEnabled = false;
            TestButton.IsEnabled = FetchButton.IsEnabled = SaveButton.IsEnabled = false;
        }
        else
        {
            SetResourceReference(TitleProperty, "Prov.TitleEdit");
            PresetSection.Visibility = Visibility.Collapsed;
            _preset = presets.FirstOrDefault(p => p.Id == existing.Preset) ?? new ProviderPresetDto
            {
                Id = existing.Preset, Name = existing.Preset_Name, Kind = existing.Kind, Needs_Key = existing.Needs_Key,
                Key_Url = existing.Key_Url, Base_Url = existing.Default_Base_Url, Base_Url_Required = existing.Kind == "custom",
                Model_Strong = existing.Default_Model_Strong, Model_Fast = existing.Default_Model_Fast,
                Supports_Thinking = existing.Supports_Thinking,
            };
            PresetFixedText.Text = L.T("Prov.PresetFixed", _preset.DisplayName);
            PresetFixedText.Visibility = Visibility.Visible;
            Fill(existing);
        }
        if (existing is not null) Loaded += (_, _) => NameBox.Focus();
    }

    private void BuildPresetChips()
    {
        foreach (var group in Groups)
        {
            var items = _presets.Where(p => p.Group == group).ToList();
            if (items.Count == 0) continue;
            PresetPanel.Children.Add(new TextBlock { Text = L.T($"Prov.Group.{group}"), Style = (Style)FindResource("GroupTitle") });
            var wrap = new WrapPanel();
            foreach (var p in items)
            {
                var chip = new RadioButton { Content = p.DisplayName, Tag = p, Style = (Style)FindResource("PresetChip") };
                chip.Checked += (_, _) => SelectPreset(p);
                wrap.Children.Add(chip);
            }
            PresetPanel.Children.Add(wrap);
        }
    }

    /// <summary>新建时切换预设：带出默认地址和模型；名称没手动改过就换成新预设的名字。</summary>
    private void SelectPreset(ProviderPresetDto p)
    {
        _preset = p;
        FormPanel.IsEnabled = TestButton.IsEnabled = FetchButton.IsEnabled = SaveButton.IsEnabled = true;
        var name = NameBox.Text.Trim();
        if (name.Length == 0 || name == _autoName)
        {
            _autoName = UniqueName(p.DisplayName);
            NameBox.Text = _autoName;
        }
        BaseBox.Text = p.Base_Url;
        SetModels(new List<string>(), p.Model_Strong, p.Model_Fast);
        ThinkingBox.IsChecked = true;
        PriceInBox.Text = PriceHitBox.Text = PriceOutBox.Text = "";
        _hasKey = false;
        KeyBox.Password = "";
        UpdatePresetHints(null);
        ShowResult("");
        ShowError("");
    }

    private void Fill(ProviderDto p)
    {
        NameBox.Text = p.DisplayName;
        BaseBox.Text = p.Base_Url;
        SetModels(new List<string>(), p.Model_Strong, p.Model_Fast);
        ThinkingBox.IsChecked = p.Thinking;
        PriceInBox.Text = Num(p.Custom_Price?.Input_Miss);
        PriceHitBox.Text = Num(p.Custom_Price?.Input_Cache_Hit);
        PriceOutBox.Text = Num(p.Custom_Price?.Output);
        _hasKey = p.Has_Key;
        UpdatePresetHints(p);
        AdvancedExpander.IsExpanded = p.Custom_Price is not null;
    }

    private void UpdatePresetHints(ProviderDto? saved)
    {
        if (_preset is null) return;
        KeySection.Visibility = _preset.Kind == "local" ? Visibility.Collapsed : Visibility.Visible;
        KeyHint.Inlines.Clear();
        if (_hasKey)
            KeyHint.Inlines.Add(L.T("Prov.KeyKeep", saved?.Key_Hint ?? ""));
        else if (!_preset.Needs_Key)
            KeyHint.Inlines.Add(L.T("Prov.KeyOptional"));
        if (!string.IsNullOrEmpty(_preset.Key_Url))
        {
            var link = new Hyperlink(new Run(L.T("Set.GetKey"))) { NavigateUri = new Uri(_preset.Key_Url) };
            link.RequestNavigate += (_, e) => Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            if (KeyHint.Inlines.Count > 0) KeyHint.Inlines.Add("　");
            KeyHint.Inlines.Add(link);
        }
        var env = saved is null ? _preset.Env_Vars : saved.Env_Vars;
        if (env.Count > 0)
            KeyHint.Inlines.Add((KeyHint.Inlines.Count > 0 ? "　" : "") + L.T("Set.EnvHint", string.Join(" / ", env)));
        ClearKeyButton.Visibility = _hasKey ? Visibility.Visible : Visibility.Collapsed;

        BaseHint.Text = _preset.Base_Url_Required ? L.T("Set.BaseUrlRequired")
            : string.IsNullOrEmpty(_preset.Base_Url) ? L.T("Set.BaseUrlOfficial")
            : L.T("Set.BaseUrlDefault", _preset.Base_Url);
        ThinkingBox.Visibility = _preset.Supports_Thinking ? Visibility.Visible : Visibility.Collapsed;
    }

    private string UniqueName(string baseName)
    {
        if (!_takenNames.Contains(baseName.ToLowerInvariant())) return baseName;
        for (var n = 2; ; n++)
        {
            var name = $"{baseName} {n}";
            if (!_takenNames.Contains(name.ToLowerInvariant())) return name;
        }
    }

    private void SetModels(List<string> list, string strong, string fast)
    {
        StrongBox.ItemsSource = list;
        FastBox.ItemsSource = list;
        StrongBox.Text = strong;
        FastBox.Text = fast;
    }

    private static string Num(double? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "";

    /// <summary>和预设默认值相同就传空字符串，后端会按「沿用默认」保存。</summary>
    private static string OrDefault(string value, string preset) => value == preset ? "" : value;

    private object Draft() => new
    {
        id = _existing?.Id ?? "",
        preset = _preset?.Id ?? "",
        name = NameBox.Text.Trim(),
        api_key = KeyBox.Password.Trim(),
        base_url = BaseBox.Text.Trim(),
        model_strong = StrongBox.Text.Trim(),
        model_fast = FastBox.Text.Trim(),
    };

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (_preset is null) return;
        ProviderTestDto? r = null;
        await Busy(L.T("Prov.Testing"), async () => r = await App.Api.TestProviderDraftAsync(Draft()));
        if (r is not null) ShowResult(L.T("Set.TestOk", r.Model, (int)r.Latency_Ms, r.Reply));
    }

    private async void Fetch_Click(object sender, RoutedEventArgs e)
    {
        if (_preset is null) return;
        List<string>? list = null;
        await Busy(L.T("Prov.Fetching"), async () => list = await App.Api.ListModelsDraftAsync(Draft()));
        if (list is null) return;
        SetModels(list, StrongBox.Text, FastBox.Text);
        ShowResult(L.T("Set.ModelsFetched", list.Count));
    }

    private async void ClearKey_Click(object sender, RoutedEventArgs e)
    {
        if (_existing is null) return;
        var ok = MessageBox.Show(this, L.T("Set.ClearKeyConfirm", _existing.DisplayName), Title,
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        if (!ok) return;
        await Busy("", async () =>
        {
            await App.Api.DeleteProviderKeyAsync(_existing.Id);
            _hasKey = false;
            UpdatePresetHints(null);
        });
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_preset is null)
        {
            ShowError(L.T("Prov.NeedPreset"));
            return;
        }
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            ShowError(L.T("Prov.NeedName"));
            NameBox.Focus();
            return;
        }
        var baseUrl = BaseBox.Text.Trim();
        if (_preset.Base_Url_Required && baseUrl.Length == 0)
        {
            ShowError(L.T("Prov.NeedBaseUrl"));
            BaseBox.Focus();
            return;
        }

        var body = new Dictionary<string, object?>
        {
            // 名称没改过时保存预设的原名，界面会按当前语言显示
            ["name"] = name == _preset.DisplayName ? _preset.Name : name,
            ["base_url"] = OrDefault(baseUrl, _preset.Base_Url),
            ["model_strong"] = OrDefault(StrongBox.Text.Trim(), _preset.Model_Strong),
            ["model_fast"] = OrDefault(FastBox.Text.Trim(), _preset.Model_Fast),
        };
        var key = KeyBox.Password.Trim();
        if (key.Length > 0) body["api_key"] = key;
        if (_preset.Supports_Thinking) body["thinking"] = ThinkingBox.IsChecked == true;
        var raw = new[] { PriceInBox.Text.Trim(), PriceHitBox.Text.Trim(), PriceOutBox.Text.Trim() };
        if (raw.All(string.IsNullOrEmpty))
        {
            if (_existing is not null) body["clear_price"] = true;
        }
        else
        {
            var values = raw.Select(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0
                ? v : (double?)null).ToArray();
            if (values.Any(v => v is null))
            {
                AdvancedExpander.IsExpanded = true;
                ShowError(L.T("Set.BadPrice"));
                return;
            }
            body["price"] = new { input_miss = values[0], input_cache_hit = values[1], output = values[2] };
        }

        await Busy("", async () =>
        {
            if (_existing is null)
            {
                body["preset"] = _preset.Id;
                Saved = await App.Api.CreateProviderAsync(body);
            }
            else
            {
                Saved = await App.Api.UpdateProviderAsync(_existing.Id, body);
            }
            DialogResult = true;
        });
    }

    private async Task Busy(string status, Func<Task> action)
    {
        IsEnabled = false;
        ShowError("");
        if (status.Length > 0) ShowResult(status);
        try
        {
            await action();
            if (status.Length > 0) ShowResult("");
        }
        catch (Exception ex)
        {
            ShowResult("");
            ShowError(ex.Message);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private void ShowResult(string text)
    {
        ResultText.Text = text;
        ResultText.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
