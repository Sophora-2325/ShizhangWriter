using System.Windows;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Models;
using Xunit;

namespace NovelGm.Desktop.Tests;

public class ResourceLoadingTests
{
    // 独立 testhost 未创建 Application；先注册 pack URI，避免首项依赖其他测试的初始化。
    static ResourceLoadingTests() => System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
        typeof(Application).TypeHandle);

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en-US")]
    [InlineData("ja-JP")]
    public void Language_resources_load_when_the_entry_assembly_is_a_test_host(string language) => StaRunner.Run(() =>
    {
        var strings = LanguageManager.LoadStrings(language);
        Assert.Contains("novel-gm;component", strings.Source.ToString());
        Assert.IsType<string>(strings["Common.Save"]);
        Assert.IsType<string>(strings["Char.Misc"]);
        Assert.IsType<string>(strings["Find.Title"]);
        foreach (var category in LoreCategories.All)
            Assert.IsType<string>(strings["Lore.Category." + category]);
    });

    [Theory]
    [InlineData("zhusha", false)] [InlineData("zhusha", true)]
    [InlineData("dianqing", false)] [InlineData("dianqing", true)]
    [InlineData("songlv", false)] [InlineData("songlv", true)]
    public void Theme_palette_loads_for_every_accent_and_mode(string accent, bool dark) => StaRunner.Run(() =>
    {
        var palette = ThemeManager.CreatePalette(dark, accent);
        var baseDict = palette.MergedDictionaries[0];
        var accentDict = palette.MergedDictionaries[1];
        Assert.NotNull(baseDict["BgBrush"]);
        Assert.NotNull(baseDict["TextBrush"]);
        Assert.NotNull(baseDict["ReviewBrush"]);
        Assert.NotNull(accentDict["AccentBrush"]);
    });
}
