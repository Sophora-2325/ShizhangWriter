using System.IO;
using System.Text.Json;

namespace NovelGm.Desktop.Api;

/// <summary>三栏比例（星号权重）保存在 %LOCALAPPDATA%\novel-gm\layout.json。</summary>
public sealed class PaneLayout
{
    /// <summary>默认偏窄目录 + 较宽创作区，打开设定时顶栏仍能完整排布。</summary>
    public double Left { get; set; } = 0.7;
    public double Center { get; set; } = 3.35;
    /// <summary>侧边对照栏相对正文列的宽度倍数。</summary>
    public double Side { get; set; } = 0.7;
    public double Right { get; set; } = 1.05;
    public bool Sidebar_Visible { get; set; } = true;
    /// <summary>右栏是否展开（沿用旧字段名；语义已改为「右栏是否可见」，不再单指设定面板）。</summary>
    public bool Lore_Visible { get; set; } = true;
    /// <summary>右栏当前显示的面板："lore"（设定）、"tools"（功能）、"ai"（AI全流程）或 "reader"（审阅）。</summary>
    public string Right_Tab { get; set; } = "lore";
    /// <summary>左栏当前显示的面板："tree"（目录）/ "notes"（笔记）/ "find"（查找）；旧配置缺字段时为目录。</summary>
    public string Left_Tab { get; set; } = "tree";
    /// <summary>上次打开的作品，启动时自动打开；等于 Recent_Project_Ids 的第一项。</summary>
    public string? Last_Project_Id { get; set; }
    /// <summary>上次打开的章节号；启动时与 Last_Project_Id 一起恢复，并展示正文区。</summary>
    public int? Last_Chapter_No { get; set; }
    /// <summary>「文件 → 最近打开」，最近的在前。</summary>
    public List<string>? Recent_Project_Ids { get; set; }
}

public static class LayoutStore
{
    /// <summary>右栏面板取值（layout.json 的 Right_Tab）。</summary>
    public const string RightTabLore = "lore";
    public const string RightTabTools = "tools";
    public const string RightTabAi = "ai";
    public const string RightTabReader = "reader";

    /// <summary>左栏面板取值（layout.json 的 Left_Tab）。</summary>
    public const string LeftTabTree = "tree";
    public const string LeftTabNotes = "notes";
    public const string LeftTabFind = "find";

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "novel-gm", "layout.json");

    public static PaneLayout Load()
    {
        try
        {
            // 除了「正数」，还要求落在合理区间：曾经把像素数与星值混算出 Center=0.044 这类
            // 「校验通过但完全不可读」的比例并持久化，只能手动改 JSON。超出区间一律回落默认比例。
            if (File.Exists(FilePath)
                && JsonSerializer.Deserialize<PaneLayout>(File.ReadAllText(FilePath)) is { } layout
                && new[] { layout.Left, layout.Center, layout.Side, layout.Right }
                    .All(v => double.IsFinite(v) && v is >= 0.15 and <= 5.0))
            {
                // 旧配置没有 Left_Tab；改名/未知的标签值一律回落到默认面板。
                return Normalize(layout);
            }
        }
        catch (Exception)
        {
            // 文件损坏时回到默认比例
        }
        return new PaneLayout();
    }

    /// <summary>把从文件读出的标签值修正为合法值；供 <see cref="Load"/> 与测试共用。</summary>
    public static PaneLayout Normalize(PaneLayout layout)
    {
        layout.Left_Tab = NormalizeTab(layout.Left_Tab, LeftTabTree, LeftTabTree, LeftTabNotes, LeftTabFind);
        layout.Right_Tab = NormalizeTab(layout.Right_Tab, RightTabLore, RightTabLore, RightTabTools, RightTabAi, RightTabReader);
        return layout;
    }

    private static string NormalizeTab(string? value, string fallback, params string[] allowed) =>
        allowed.FirstOrDefault(a => string.Equals(a, value, StringComparison.OrdinalIgnoreCase)) ?? fallback;


    public static void Save(PaneLayout layout)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(layout, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // 布局只是偏好，写不进去不影响使用
        }
    }
}
