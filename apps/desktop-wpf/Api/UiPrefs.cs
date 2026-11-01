using System.IO;
using System.Text.Json;

namespace NovelGm.Desktop.Api;

/// <summary>
/// 本机界面偏好（语言、外观、主题），存在 %APPDATA%\novel-gm\ui.json。
/// 后端启动前就要用到，所以不放在后端的设置里。
/// </summary>
public sealed class UiPrefs
{
    public string Language { get; set; } = "";

    /// <summary>工作模式：pure（纯净）/ assist（AI 辅助）。缺省或未知值按 pure。</summary>
    public string WorkMode { get; set; } = "pure";

    public const string ModePure = "pure";
    public const string ModeAssist = "assist";

    public static bool IsPureMode =>
        !string.Equals(Current.WorkMode, ModeAssist, StringComparison.OrdinalIgnoreCase);

    /// <summary>语音听写语言：zh / en / ja。</summary>
    public string SttLanguage { get; set; } = "zh";

    /// <summary>外观：浅色 / 深色 / 跟随系统。</summary>
    public string Theme { get; set; } = ThemeManager.Light;

    /// <summary>主题配色：经典 / 樱紫 / 蓝白 / 青绿。只读遗留字段，0.3.12 起供回退使用，不再驱动界面。</summary>
    public string Skin { get; set; } = "classic";

    /// <summary>强调色：zhusha / dianqing / songlv。旧配置文件没有这个字段，由 AppearanceMigration 从 Skin 迁移。</summary>
    public string Accent { get; set; } = ThemeManager.Zhusha;

    /// <summary>正文字体栈；与强调色脱钩。未知值回落宋体。</summary>
    public string BodyFont { get; set; } = ThemeManager.FontSimSun;

    /// <summary>正文列模式：paper（稿纸列）/ full（铺满）。</summary>
    public string BodyWidthMode { get; set; } = PaperLayout.ModePaper;

    /// <summary>外观偏好迁移版本；0 = 尚未从旧皮肤迁移。</summary>
    public int AppearanceVersion { get; set; }

    /// <summary>
    /// 用户在脉络「类型 → 其他…」里自己填过的类型。
    /// 只存在本机，不写进作品文件；旧配置文件没有这个字段，回落到空列表。
    /// </summary>
    public List<string> ThreadKinds { get; set; } = new();

    /// <summary>
    /// 小说设定「杂项」里是否展开「进阶」字段（人称 / 时态 / 句长 / 范文 / 标点 / 基线与编译产物）。
    /// 默认收起；旧配置文件没有这个字段，回落到 false。
    /// </summary>
    public bool MiscShowAdvanced { get; set; }

    /// <summary>
    /// 「发布到其他平台」上次勾选的平台 key。只在本机记着，不写进作品文件；
    /// 旧配置文件没有这个字段，回落到空列表（对话框会按平台表自己选一个默认）。
    /// </summary>
    public List<string> PublishPlatforms { get; set; } = new();

    /// <summary>发布范围：chapter（本章）/ book（全书）。</summary>
    public string PublishScope { get; set; } = "chapter";

    /// <summary>发布时填的作者名；只写进「发布信息.txt」，不动作品文件。</summary>
    public string PublishAuthor { get; set; } = "";

    /// <summary>发布包段首缩进：空串 = 按平台默认；none / full。</summary>
    public string PublishIndent { get; set; } = "";

    public bool UsageVisible { get; set; } = true;
    /// <summary>0..1 positions inside the workspace; null restores the editor-footer default.</summary>
    public double? UsageX { get; set; }
    public double? UsageY { get; set; }
    public decimal UsageCnyRate { get; set; } = UsageDisplay.DefaultRate;

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "novel-gm", "ui.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static UiPrefs Current { get; private set; } = new();
    internal static bool PersistenceEnabled { get; set; } = true;

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<UiPrefs>(File.ReadAllText(FilePath), Json) ?? new UiPrefs();
        }
        catch (Exception)
        {
            Current = new UiPrefs();
        }
        // 0.3.11 及更早的四套皮肤 → 强调色 + 独立正文字体；只执行一次并落盘。
        if (AppearanceMigration.Apply(Current)) Save();
    }

    public static void Save()
    {
        if (!PersistenceEnabled) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, Json));
        }
        catch (Exception)
        {
            // 偏好写不进去不影响使用，下次启动回到默认值
        }
    }
}
