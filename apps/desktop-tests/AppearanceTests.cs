using NovelGm.Desktop.Api;
using Xunit;

namespace NovelGm.Desktop.Tests;

public class AppearanceTests
{
    [Theory]
    [InlineData("classic", "zhusha", "Cambria, SimSun, 宋体")]
    [InlineData("sakura", "dianqing", "Cambria, KaiTi, 楷体")]
    [InlineData("campus", "dianqing", "Segoe UI, DengXian, 等线")]
    [InlineData("pastoral", "songlv", "Cambria, FangSong, 仿宋")]
    public void Old_skins_map_to_accent_and_body_font(string skin, string accent, string font)
    {
        var prefs = new UiPrefs { Skin = skin, AppearanceVersion = 0 };
        Assert.True(AppearanceMigration.Apply(prefs));
        Assert.Equal(accent, prefs.Accent);
        Assert.Equal(font, prefs.BodyFont);
        Assert.Equal(PaperLayout.ModePaper, prefs.BodyWidthMode);
        Assert.Equal(AppearanceMigration.CurrentVersion, prefs.AppearanceVersion);
        Assert.Equal(skin, prefs.Skin);
    }

    [Fact]
    public void Migration_runs_only_once()
    {
        var prefs = new UiPrefs
        {
            Skin = "sakura",
            AppearanceVersion = AppearanceMigration.CurrentVersion,
            Accent = "songlv",
            BodyFont = ThemeManager.FontFangSong,
        };
        Assert.False(AppearanceMigration.Apply(prefs));
        Assert.Equal("songlv", prefs.Accent);
        Assert.Equal(ThemeManager.FontFangSong, prefs.BodyFont);
    }

    [Theory]
    [InlineData(null, "zhusha")]
    [InlineData("nope", "zhusha")]
    [InlineData("songlv", "songlv")]
    public void Unknown_accent_falls_back(string? accent, string expected) =>
        Assert.Equal(expected, ThemeManager.NormalizeAccent(accent));

    [Fact]
    public void Unknown_body_font_falls_back_to_simsun() =>
        Assert.Equal(ThemeManager.FontSimSun, ThemeManager.NormalizeFont("Papyrus"));
}
