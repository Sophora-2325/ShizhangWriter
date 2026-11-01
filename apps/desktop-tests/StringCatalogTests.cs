using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace NovelGm.Desktop.Tests;

/// <summary>
/// 文案目录的静态契约：三种语言键集合一致，XAML 的 DynamicResource 与代码里的 L.T 字面量都有定义。
/// 跳过主题画刷等非文案资源（只检查已知文案前缀），避免把配色键当成缺失文案。
/// </summary>
public class StringCatalogTests
{
    private static readonly string[] StringPrefixes =
    {
        "App.", "Menu.", "Common.", "Edit.", "Main.", "Mode.", "Rail.", "Tools.",
        "Ai.", "Notes.", "Find.", "Versions.", "Sens.", "Publish.", "Status.", "Busy.",
        "Step.", "Zone.", "Lore.", "Plan.", "Review.", "Audit.", "Read.", "Fmt.",
        "Settings.", "Memory.", "Misc.", "Rename.", "Migrate.", "Backend.", "Lang.",
        "Char.", "Thread.", "Entry.", "NewWork.", "Project.", "Publish.", "Import.",
        "Export.", "PublishInfo.", "Reading.", "Stt.", "Phases.", "Phase.",
    };

    private static readonly string[] Languages = { "zh-CN", "en-US", "ja-JP" };

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string DesktopRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "desktop-wpf", "Strings")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "desktop-wpf");
    }

    private static Dictionary<string, string> Keys(string language)
    {
        var dir = Path.Combine(DesktopRoot(), "Strings");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        // 聚合字典叫 zh-CN.xaml，功能字典叫 Ai.zh-CN.xaml；两种都要读，否则外壳键会被当成缺失。
        var files = Directory.GetFiles(dir, "*." + language + ".xaml").ToList();
        var aggregate = Path.Combine(dir, language + ".xaml");
        if (File.Exists(aggregate)) files.Add(aggregate);
        foreach (var file in files)
        {
            var doc = XDocument.Load(file);
            foreach (var element in doc.Descendants().Where(e => e.Attribute(X + "Key") is not null))
                result[(string)element.Attribute(X + "Key")!] = element.Value;
        }
        return result;
    }

    private static bool LooksLikeTextKey(string key) =>
        // 以「.」结尾的键是代码动态拼接的前缀（如 L.T("Memory.Summary." + status)），不做字面量校验
        !key.EndsWith('.') && StringPrefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal));

    [Fact]
    public void Every_language_defines_the_same_string_keys()
    {
        var reference = Keys(Languages[0]).Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var language in Languages.Skip(1))
        {
            var other = Keys(language).Keys.ToHashSet(StringComparer.Ordinal);
            var missing = reference.Except(other).OrderBy(k => k).ToList();
            var extra = other.Except(reference).OrderBy(k => k).ToList();
            Assert.True(missing.Count == 0, $"{language} 缺少文案键：{string.Join(", ", missing)}");
            Assert.True(extra.Count == 0, $"{language} 多出文案键：{string.Join(", ", extra)}");
        }
    }

    [Fact]
    public void Dynamic_resource_string_keys_are_defined_in_every_language()
    {
        var dictionaries = Languages.ToDictionary(l => l, Keys);
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(DesktopRoot(), "*.xaml", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            var text = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(text, @"\{DynamicResource\s+([A-Za-z0-9_.]+)\}"))
            {
                var key = match.Groups[1].Value;
                if (LooksLikeTextKey(key)) referenced.Add(key);
            }
        }
        Assert.NotEmpty(referenced);
        foreach (var language in Languages)
        {
            var missing = referenced.Where(k => !dictionaries[language].ContainsKey(k)).OrderBy(k => k).ToList();
            Assert.True(missing.Count == 0, $"{language} 的 XAML 引用了未定义文案：{string.Join(", ", missing)}");
        }
    }

    [Fact]
    public void Code_literal_L_T_keys_are_defined_in_every_language()
    {
        var dictionaries = Languages.ToDictionary(l => l, Keys);
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(DesktopRoot(), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            var text = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(text, @"L\.T\(\s*""([A-Za-z0-9_.]+)"""))
            {
                var key = match.Groups[1].Value;
                if (LooksLikeTextKey(key)) referenced.Add(key);
            }
        }
        Assert.NotEmpty(referenced);
        foreach (var language in Languages)
        {
            var missing = referenced.Where(k => !dictionaries[language].ContainsKey(k)).OrderBy(k => k).ToList();
            Assert.True(missing.Count == 0, $"{language} 的代码引用了未定义文案：{string.Join(", ", missing)}");
        }
    }
}
