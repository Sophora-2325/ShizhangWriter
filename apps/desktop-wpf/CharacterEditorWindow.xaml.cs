using System.Globalization;
using System.Windows;
using System.Windows.Media;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Models;

namespace NovelGm.Desktop;

/// <summary>
/// 人物卡：默认单列。点普通项目在第二列展开输入区；点「杂项」第二列换成五项菜单，
/// 再点其中一项在第三列展开输入区。所有草稿先落进字典，切换项目不丢。
/// </summary>
public partial class CharacterEditorWindow : Window
{
    private const string KeyPersonality = "personality";
    private const string KeyAppearance = "appearance";
    private const string KeyOutfit = "outfit";
    private const string KeyHobbies = "hobbies";
    private const string KeyVoice = "voice";
    private const string KeySecret = "secret";
    private const string KeyRole = "role";
    private const string KeyGoals = "goals";
    private const string KeyAction = "action";
    private const string KeyJoin = "join";
    private const string KeyRelations = "relations";
    private const string KeyOrganization = "organization";
    private const string KeyThinkingHabits = "thinking_habits";
    private const string KeyNotes = "notes";

    private static readonly Dictionary<string, string> FieldLabels = new(StringComparer.Ordinal)
    {
        [KeyPersonality] = "Field.Personality",
        [KeyAppearance] = "Field.Appearance",
        [KeyOutfit] = "Field.Outfit",
        [KeyHobbies] = "Field.Hobbies",
        [KeyVoice] = "Char.Voice",
        [KeySecret] = "Char.Secret",
        [KeyRole] = "Char.Role",
        [KeyGoals] = "Char.Goals",
        [KeyAction] = "Char.Action",
    };

    private static readonly Dictionary<string, string> MiscLabels = new(StringComparer.Ordinal)
    {
        [KeyJoin] = "Char.Join",
        [KeyRelations] = "Char.Relations",
        [KeyOrganization] = "Field.Organization",
        [KeyThinkingHabits] = "Field.ThinkingHabits",
        [KeyNotes] = "Char.Other",
    };

    private const double FirstColumnWidth = 384;
    private const double SecondColumnWidth = 352;
    private const double ThirdColumnWidth = 332;

    private readonly string _id;
    private readonly IReadOnlyList<CharacterDto> _others;
    private readonly string _rootPath;
    /// <summary>载入时的原始关系，用于重建时保留 type / intensity。</summary>
    private readonly IReadOnlyList<RelationDto> _originalRelations;
    private readonly CharacterDto? _existing;
    private readonly int? _chapterNo;
    private readonly bool _canEditAction;
    /// <summary>所有输入项目的草稿：切换项目只是换编辑器里的文本，不丢。</summary>
    private readonly Dictionary<string, string> _drafts = new(StringComparer.Ordinal);
    private string _portrait;
    private string? _activeKey;
    private string? _activeMiscKey;
    private bool _miscMode;
    private bool _saving;

    /// <summary>最近一次点「保存」构建出的请求（含失败那次），供集成与测试取用。</summary>
    public CharacterSaveRequestDto Result { get; private set; } = new();

    /// <summary>保存成功后为 true；测试里窗口不必真的以模态方式显示。</summary>
    public bool SavedSuccessfully { get; private set; }

    /// <summary>新选的本地图片；保存人物前需先复制进作品文件夹。</summary>
    public string? PendingPortraitSource { get; private set; }

    /// <summary>保存委托：成功返回 Success，失败返回 Fail（窗口保持打开并保留草稿）。</summary>
    public Func<CharacterSaveRequestDto, Task<CharacterSaveResult>>? SaveHandler { get; set; }

    public CharacterEditorWindow(CharacterDto? existing, IReadOnlyList<CharacterDto> others, string rootPath,
        int? chapterNo = null, string chapterAction = "", bool canEditAction = true)
    {
        InitializeComponent();
        _existing = existing;
        _others = others;
        _rootPath = rootPath;
        _chapterNo = chapterNo;
        _canEditAction = canEditAction;
        _originalRelations = existing?.Relations ?? new List<RelationDto>();
        _id = string.IsNullOrEmpty(existing?.Id) ? Guid.NewGuid().ToString("N")[..12] : existing!.Id;
        _portrait = existing?.Portrait ?? "";
        Title = existing is null ? L.T("Char.TitleNew") : L.T("Char.TitleEdit", existing.Name);
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            PickPortraitButton.IsEnabled = false;
            PortraitHint.Text = L.T("Char.PortraitNeedsFolder");
        }
        ShowPortrait(Portraits.Resolve(rootPath, _portrait));

        _drafts[KeyPersonality] = existing?.Personality ?? "";
        _drafts[KeyAppearance] = existing?.Appearance ?? "";
        _drafts[KeyOutfit] = existing?.Outfit ?? "";
        _drafts[KeyHobbies] = existing?.Hobbies ?? "";
        _drafts[KeyVoice] = existing?.Voice ?? "";
        _drafts[KeySecret] = existing?.Secret ?? "";
        _drafts[KeyRole] = existing?.Role ?? "";
        _drafts[KeyGoals] = string.Join(Environment.NewLine, existing?.Goals ?? new List<string>());
        _drafts[KeyAction] = chapterAction ?? "";
        _drafts[KeyJoin] = existing?.Join_Reason ?? "";
        _drafts[KeyRelations] = BuildRelationsText(existing);
        _drafts[KeyOrganization] = existing?.Organization ?? "";
        _drafts[KeyThinkingHabits] = existing?.Thinking_Habits ?? "";
        _drafts[KeyNotes] = existing?.Notes ?? "";

        if (existing is not null)
        {
            NameBox.Text = existing.Name;
            AgeBox.Text = existing.Age;
            GenderBox.Text = existing.Gender;
        }

        // 默认只显示第一列
        SecondPanel.Visibility = Visibility.Collapsed;
        ThirdPanel.Visibility = Visibility.Collapsed;
        FieldEditorPanel.Visibility = Visibility.Collapsed;
        MiscMenuPanel.Visibility = Visibility.Collapsed;
        ApplyLayout();
        FitHeight();
    }

    // ---------------------------------------------------------------- 布局

    /// <summary>展开的列数决定窗口宽度；超出工作区时压到工作区宽度，由外层 ScrollViewer 横向滚动。</summary>
    private void ApplyLayout()
    {
        UpdateActiveButtons();
        var secondVisible = SecondPanel.Visibility == Visibility.Visible;
        var thirdVisible = ThirdPanel.Visibility == Visibility.Visible;
        ColumnsGrid.ColumnDefinitions[1].Width =
            new GridLength(secondVisible ? SecondColumnWidth : 0);
        ColumnsGrid.ColumnDefinitions[2].Width =
            new GridLength(thirdVisible ? ThirdColumnWidth : 0);

        var desired = 44 + FirstColumnWidth
            + (secondVisible ? SecondColumnWidth : 0)
            + (thirdVisible ? ThirdColumnWidth : 0);
        var work = SystemParameters.WorkArea;
        var available = work.Width > 0 ? work.Width - 32 : desired;
        var width = Math.Min(desired, Math.Max(MinWidth, available));
        Width = Math.Max(MinWidth, width);
        if (IsLoaded && double.IsFinite(Left))
            Left = Math.Max(work.Left + 16, Math.Min(Left, work.Right - Width - 16));
    }

    private void UpdateActiveButtons()
    {
        foreach (var (button, key) in new[]
        {
            (PersonalityButton, KeyPersonality), (AppearanceButton, KeyAppearance),
            (OutfitButton, KeyOutfit), (HobbiesButton, KeyHobbies), (VoiceButton, KeyVoice),
            (SecretButton, KeySecret), (RoleButton, KeyRole), (GoalsButton, KeyGoals), (ActionButton, KeyAction),
        }) button.Tag = !_miscMode && SecondPanel.Visibility == Visibility.Visible && _activeKey == key ? "active" : null;
        MiscButton.Tag = _miscMode ? "active" : null;
        foreach (var (button, key) in new[]
        {
            (JoinReasonButton, KeyJoin), (RelationsButton, KeyRelations),
            (OrganizationButton, KeyOrganization), (ThinkingHabitsButton, KeyThinkingHabits), (OtherNotesButton, KeyNotes),
        }) button.Tag = _miscMode && _activeMiscKey == key ? "active" : null;
    }

    private void FitHeight()
    {
        var work = SystemParameters.WorkArea;
        if (work.Height > 0)
            Height = Math.Min(680, Math.Max(MinHeight, work.Height - 32));
    }

    // ---------------------------------------------------------------- 项目切换

    private void ToggleField(string key)
    {
        CommitAll();
        HideThird();
        if (!_miscMode && _activeKey == key)
        {
            // 再次点击当前项目：收起第二列
            _activeKey = null;
            SecondPanel.Visibility = Visibility.Collapsed;
            FieldEditorPanel.Visibility = Visibility.Collapsed;
            MiscMenuPanel.Visibility = Visibility.Collapsed;
            ApplyLayout();
            return;
        }
        _miscMode = false;
        _activeKey = key;
        MiscMenuPanel.Visibility = Visibility.Collapsed;
        FieldEditorPanel.Visibility = Visibility.Visible;
        SecondPanel.Visibility = Visibility.Visible;
        ShowFieldEditor(key);
        ApplyLayout();
    }

    private void HideThird()
    {
        _activeMiscKey = null;
        ThirdPanel.Visibility = Visibility.Collapsed;
    }

    private void ShowFieldEditor(string key)
    {
        FieldTitle.Text = L.T(FieldLabels[key]);
        FieldEditor.Text = _drafts.TryGetValue(key, out var text) ? text : "";
        var actionLocked = key == KeyAction && (_chapterNo is null || !_canEditAction);
        FieldEditor.IsReadOnly = actionLocked;
        var hint = key switch
        {
            KeyAction => L.T(_chapterNo is null ? "Char.ActionNeedsChapter" : "Char.ActionHint"),
            KeyGoals => L.T("Char.GoalsHint"),
            _ => "",
        };
        FieldHint.Text = hint;
        FieldHint.Visibility = string.IsNullOrEmpty(hint) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ToggleMisc()
    {
        CommitAll();
        if (_miscMode)
        {
            _miscMode = false;
            _activeMiscKey = null;
            SecondPanel.Visibility = Visibility.Collapsed;
            ThirdPanel.Visibility = Visibility.Collapsed;
            FieldEditorPanel.Visibility = Visibility.Collapsed;
            MiscMenuPanel.Visibility = Visibility.Collapsed;
            ApplyLayout();
            return;
        }
        HideThird();
        _activeKey = null;
        _miscMode = true;
        FieldEditorPanel.Visibility = Visibility.Collapsed;
        MiscMenuPanel.Visibility = Visibility.Visible;
        SecondPanel.Visibility = Visibility.Visible;
        ApplyLayout();
    }

    private void ToggleMiscItem(string key)
    {
        if (!_miscMode) ToggleMisc();
        if (_activeMiscKey == key)
        {
            // 再次点击当前杂项项：收起第三列
            CommitMiscEditor();
            _activeMiscKey = null;
            ThirdPanel.Visibility = Visibility.Collapsed;
            ApplyLayout();
            return;
        }
        CommitMiscEditor();
        _activeMiscKey = key;
        MiscFieldTitle.Text = L.T(MiscLabels[key]);
        MiscFieldEditor.Text = _drafts.TryGetValue(key, out var text) ? text : "";
        if (key == KeyRelations)
        {
            MiscFieldHint.Text = L.T("Char.RelationsHint");
            MiscFieldHint.Visibility = Visibility.Visible;
        }
        else
        {
            MiscFieldHint.Visibility = Visibility.Collapsed;
        }
        ThirdPanel.Visibility = Visibility.Visible;
        ApplyLayout();
    }

    private void CommitAll()
    {
        CommitFieldEditor();
        CommitMiscEditor();
    }

    private void CommitFieldEditor()
    {
        if (_activeKey is { } key) _drafts[key] = FieldEditor.Text;
    }

    private void CommitMiscEditor()
    {
        if (_activeMiscKey is { } key) _drafts[key] = MiscFieldEditor.Text;
    }

    private void Personality_Click(object sender, RoutedEventArgs e) => ToggleField(KeyPersonality);
    private void Appearance_Click(object sender, RoutedEventArgs e) => ToggleField(KeyAppearance);
    private void Outfit_Click(object sender, RoutedEventArgs e) => ToggleField(KeyOutfit);
    private void Hobbies_Click(object sender, RoutedEventArgs e) => ToggleField(KeyHobbies);
    private void Voice_Click(object sender, RoutedEventArgs e) => ToggleField(KeyVoice);
    private void Secret_Click(object sender, RoutedEventArgs e) => ToggleField(KeySecret);
    private void Role_Click(object sender, RoutedEventArgs e) => ToggleField(KeyRole);
    private void Goals_Click(object sender, RoutedEventArgs e) => ToggleField(KeyGoals);
    private void Action_Click(object sender, RoutedEventArgs e) => ToggleField(KeyAction);
    private void Misc_Click(object sender, RoutedEventArgs e) => ToggleMisc();
    private void JoinReason_Click(object sender, RoutedEventArgs e) => ToggleMiscItem(KeyJoin);
    private void Relations_Click(object sender, RoutedEventArgs e) => ToggleMiscItem(KeyRelations);
    private void Organization_Click(object sender, RoutedEventArgs e) => ToggleMiscItem(KeyOrganization);
    private void ThinkingHabits_Click(object sender, RoutedEventArgs e) => ToggleMiscItem(KeyThinkingHabits);
    private void OtherNotes_Click(object sender, RoutedEventArgs e) => ToggleMiscItem(KeyNotes);

    // ---------------------------------------------------------------- 保存 / 取消

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_saving) return;
        ErrorText.Visibility = Visibility.Collapsed;
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            ShowError(L.T("Char.MissingRequired", L.T("Field.Name")));
            NameBox.Focus();
            return;
        }

        var request = BuildRequest(name);
        Result = request;
        if (SaveHandler is null)
        {
            Finish(true);
            return;
        }

        _saving = true;
        SaveButton.IsEnabled = false;
        try
        {
            var outcome = await SaveHandler(request);
            if (outcome.Ok)
            {
                Finish(true);
                return;
            }
            ShowError(L.T("Char.SaveFailed", outcome.Error));
        }
        catch (Exception ex)
        {
            ShowError(L.T("Char.SaveFailed", ex.Message));
        }
        finally
        {
            _saving = false;
            SaveButton.IsEnabled = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Finish(false);

    private void Finish(bool saved)
    {
        CommitAll();
        SavedSuccessfully = saved;
        try
        {
            DialogResult = saved;
        }
        catch (InvalidOperationException)
        {
            // 测试/预览环境里窗口没有以 ShowDialog 打开：跳过 DialogResult，直接关掉。
            try { Close(); } catch (InvalidOperationException) { /* 已经关闭 */ }
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private CharacterSaveRequestDto BuildRequest(string name)
    {
        CommitAll();
        var request = new CharacterSaveRequestDto
        {
            Id = _id,
            Name = name,
            Age = AgeBox.Text.Trim(),
            Gender = GenderBox.Text.Trim(),
            Portrait = _portrait,
            Personality = Draft(KeyPersonality),
            Appearance = Draft(KeyAppearance),
            Outfit = Draft(KeyOutfit),
            Hobbies = Draft(KeyHobbies),
            Voice = Draft(KeyVoice),
            Secret = Draft(KeySecret),
            Role = Draft(KeyRole),
            Goals = SplitLines(Draft(KeyGoals)),
            Join_Reason = Draft(KeyJoin),
            Relations = ParseRelations(Draft(KeyRelations)),
            Organization = Draft(KeyOrganization),
            Thinking_Habits = Draft(KeyThinkingHabits),
            Notes = Draft(KeyNotes),
            // 新布局不显示这些字段，但必须原样保留，保存时不能丢。
            Recent_Thoughts = _existing?.Recent_Thoughts ?? "",
            Public_Face = _existing?.Public_Face ?? "",
            Known_Facts = _existing?.Known_Facts?.ToList() ?? new List<string>(),
            Status = _existing?.Status is { } status
                ? new Dictionary<string, string>(status)
                : new Dictionary<string, string>(),
        };
        if (_chapterNo is int no)
        {
            request.Chapter_No = no;
            // 空串 = 清空该章行动；省略 = 不改。
            request.Chapter_Action = Draft(KeyAction);
        }
        return request;
    }

    private string Draft(string key) => _drafts.TryGetValue(key, out var text) ? text.Trim() : "";

    private static List<string> SplitLines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private string BuildRelationsText(CharacterDto? existing) =>
        string.Join(Environment.NewLine, (existing?.Relations ?? new List<RelationDto>()).Select(r =>
        {
            var name = _others.FirstOrDefault(o => o.Id == r.Target_Id)?.Name ?? r.Target_Id;
            var type = string.IsNullOrWhiteSpace(r.Type) ? "related" : r.Type;
            return $"{name}：{r.Label} | {type} | {Fmt(r.Intensity)}";
        }));

    private List<RelationDto> ParseRelations(string text)
    {
        var relations = new List<RelationDto>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // 行格式：「名字：描述」；后两段可选，写成「 | 类型 | 强度」
            var seg = line.Split('|');
            var head = seg[0];
            var type = seg.Length > 1 ? seg[1].Trim() : "";
            var intensity = seg.Length > 2 ? ParseIntensity(seg[2]) : null;

            var parts = head.Split(new[] { '：', ':' }, 2);
            var nameOrId = parts[0].Trim();
            var label = parts.Length > 1 ? parts[1].Trim() : head.Trim();
            var hit = _others.FirstOrDefault(o => o.Name == nameOrId || o.Id == nameOrId);
            var targetId = hit?.Id ?? nameOrId;
            var previous = _originalRelations.FirstOrDefault(r => r.Target_Id == targetId);
            relations.Add(new RelationDto
            {
                Target_Id = targetId,
                // 段留空时沿用原值，填了就按填的来——不能每编辑一次就抹平已有类型/强度。
                Type = string.IsNullOrWhiteSpace(type) ? previous?.Type ?? "related" : type,
                Label = label,
                Intensity = intensity ?? previous?.Intensity ?? 0.5,
            });
        }
        return relations;
    }

    /// <summary>强度按不变文化格式化，免得小数点在别的区域设置下变成逗号。</summary>
    private static string Fmt(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>强度解析：不变文化优先，再看当前区域；解析不出来返回 null（表示沿用原值）。</summary>
    private static double? ParseIntensity(string text)
    {
        var s = (text ?? "").Trim();
        if (s.Length == 0) return null;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var invariant))
            return invariant;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out var current)
            ? current
            : null;
    }

    // ---------------------------------------------------------------- 图片

    private void PickPortrait_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = L.T("Char.PickPortraitTitle"),
            Filter = L.T("Char.ImageFilter"),
        };
        if (dlg.ShowDialog(this) != true) return;
        // 只记住本地来源；真正写进作品是保存时由 MainWindow 复制并回填 Portrait。
        PendingPortraitSource = dlg.FileName;
        ShowPortrait(dlg.FileName);
    }

    private void ClearPortrait_Click(object sender, RoutedEventArgs e)
    {
        PendingPortraitSource = null;
        _portrait = "";
        ShowPortrait(null);
    }

    private void ShowPortrait(string? path)
    {
        var img = Portraits.Load(path, 360);
        PortraitPreview.Background = img is null ? null : new ImageBrush(img) { Stretch = Stretch.UniformToFill };
        PortraitEmpty.Visibility = img is null ? Visibility.Visible : Visibility.Collapsed;
        ClearPortraitButton.IsEnabled = img is not null;
    }
}
