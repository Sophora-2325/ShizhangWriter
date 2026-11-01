namespace NovelGm.Desktop.Api;

/// <summary>
/// 0.3.11 及更早的四套皮肤迁移到「校样台」：旧皮肤同时决定配色与正文字体，
/// 新模型把两者拆开——强调色进 Accent，原来的正文字体快照进 BodyFont。
/// 只改 ui.json，不触碰 layout.json 或任何章节数据；迁移版本最后写入，中断可重跑。
/// </summary>
public static class AppearanceMigration
{
    public const int CurrentVersion = 1;

    /// <summary>执行迁移；返回是否发生了改动。</summary>
    public static bool Apply(UiPrefs prefs)
    {
        if (prefs.AppearanceVersion >= CurrentVersion) return false;

        var (accent, font) = Map(prefs.Skin);
        prefs.Accent = accent;
        prefs.BodyFont = font;
        if (string.IsNullOrWhiteSpace(prefs.BodyWidthMode))
            prefs.BodyWidthMode = PaperLayout.ModePaper;
        prefs.AppearanceVersion = CurrentVersion;
        return true;
    }

    /// <summary>旧皮肤 → (强调色, 正文字体栈)。未知皮肤按经典处理。</summary>
    public static (string Accent, string BodyFont) Map(string? skin) => skin switch
    {
        "sakura" => (ThemeManager.Dianqing, ThemeManager.FontKaiTi),
        "campus" => (ThemeManager.Dianqing, ThemeManager.FontDengXian),
        "pastoral" => (ThemeManager.Songlv, ThemeManager.FontFangSong),
        _ => (ThemeManager.Zhusha, ThemeManager.FontSimSun),
    };
}
