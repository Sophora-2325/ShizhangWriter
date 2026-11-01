using System.Xml.Linq;
using Xunit;

namespace NovelGm.Desktop.Tests;

/// <summary>外壳 XAML 的静态契约：左右选项列、宿主面板、最小宽度与已删除的顶栏元素。</summary>
public class ShellLayoutTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static string DesktopRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "desktop-wpf", "Strings")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static XDocument MainXaml() =>
        XDocument.Load(Path.Combine(DesktopRoot(), "desktop-wpf", "MainWindow.xaml"));

    private static HashSet<string> Names(XDocument doc) =>
        doc.Descendants()
            .Select(e => (string?)e.Attribute(X + "Name"))
            .OfType<string>()
            .ToHashSet();

    private static XElement Named(XDocument doc, string name) =>
        Assert.Single(doc.Descendants().Where(e => (string?)e.Attribute(X + "Name") == name));

    [Fact]
    public void Left_rail_has_tree_notes_find_in_order()
    {
        var doc = MainXaml();
        var rail = Named(doc, "LeftRail");
        Assert.Equal("0", (string?)rail.Attribute("Grid.Column"));
        Assert.Equal("56", (string?)rail.Attribute("Width"));

        var labels = rail.Descendants(P + "Button")
            .Select(b => (string?)b.Attribute(X + "Name"))
            .OfType<string>()
            .ToList();
        Assert.Equal(new[] { "RailLibrary", "RailNotes", "RailFind" }, labels);
        Assert.All(rail.Descendants(P + "Button"), b =>
            Assert.Equal("RailButton", ((string?)b.Attribute("Style"))?.Replace("{StaticResource ", "").Replace("}", "")));
    }

    [Fact]
    public void Right_rail_has_lore_tools_ai_review_in_order()
    {
        var doc = MainXaml();
        var rail = Named(doc, "OptionRail");
        Assert.Equal("6", (string?)rail.Attribute("Grid.Column"));

        var labels = rail.Descendants(P + "Button")
            .Select(b => (string?)b.Attribute(X + "Name"))
            .OfType<string>()
            .ToList();
        Assert.Equal(new[] { "RailLore", "RailTools", "RailAi", "RailReader" }, labels);
    }

    [Fact]
    public void Left_pane_hosts_tree_notes_and_find_panels()
    {
        var doc = MainXaml();
        var names = Names(doc);
        Assert.Contains("WorkTreeHost", names);
        Assert.Contains("NotesPanelView", names);
        Assert.Contains("FindPanelView", names);
        Assert.Contains("AiPanelView", names);

        // 三个宿主同一个 Grid：由左选项列切换可见性
        Assert.Single(doc.Descendants().Where(e => e.Name.LocalName == "NotesPanel"));
        Assert.Single(doc.Descendants().Where(e => e.Name.LocalName == "FindPanel"));
        Assert.Single(doc.Descendants().Where(e => e.Name.LocalName == "AiPanel"));
        Assert.Equal("Collapsed", (string?)Named(doc, "NotesPanelView").Attribute("Visibility"));
        Assert.Equal("Collapsed", (string?)Named(doc, "FindPanelView").Attribute("Visibility"));
        Assert.Equal("Collapsed", (string?)Named(doc, "AiPanelView").Attribute("Visibility"));
    }

    [Fact]
    public void Pane_columns_use_narrow_safe_minimum_widths()
    {
        var doc = MainXaml();
        var defs = Named(doc, "PaneGrid").Element(P + "Grid.ColumnDefinitions")!
            .Elements(P + "ColumnDefinition")
            .ToList();
        Assert.Equal(7, defs.Count);
        Assert.Equal("56", (string?)defs[0].Attribute("Width"));
        Assert.Equal("200", (string?)defs[1].Attribute("MinWidth"));
        Assert.Equal("420", (string?)defs[3].Attribute("MinWidth"));
        Assert.Equal("240", (string?)defs[5].Attribute("MinWidth"));
    }

    [Fact]
    public void Removed_shell_chrome_stays_removed()
    {
        var doc = MainXaml();
        var names = Names(doc);
        foreach (var gone in new[]
                 {
                     "AiAssistButton", "AiAssistPopup", "LoreToggle", "LorePaneFill", "PhaseText",
                     "StepBar", "StepButtonsPanel", "PrevButton", "GenerateButton", "FinalizeButton",
                     "ExtractButton", "AuditButton", "NextButton",
                     "SidebarToggle", "SidebarPaneFill", "StatusText",
                 })
            Assert.DoesNotContain(gone, names);

        // XAML 里不应再出现这些已删除的处理函数
        var handlers = doc.Descendants()
            .SelectMany(e => new[] { "Click", "SizeChanged" }
                .Select(a => (string?)e.Attribute(a)))
            .OfType<string>()
            .ToHashSet();
        foreach (var gone in new[] { "AiAssistButton_Click", "AiOpenPlan_Click", "AiOpenReview_Click", "LoreToggle_Click", "Prev_Click", "Next_Click" })
            Assert.DoesNotContain(gone, handlers);
        Assert.DoesNotContain("StepBar", doc.ToString());
    }

    [Fact]
    public void Shell_strings_and_feature_dictionaries_exist_in_every_language()
    {
        var expectedMode = new Dictionary<string, string>
        {
            ["zh-CN"] = "分步创作",
            ["en-US"] = "Guided writing",
            ["ja-JP"] = "段階執筆",
        };
        var shellKeys = new[]
        {
            "Mode.Pure", "Mode.Assist", "Rail.Library", "Rail.Notes", "Rail.Find", "Rail.Ai",
            "Main.ToggleAi", "Main.ToggleNotes", "Main.ToggleFind", "Main.ToggleSidebar",
            "Tools.GroupPublish", "Tools.GroupFind", "Tools.GroupEditComment",
            "Tools.Publish", "Tools.Versions", "Tools.FindChapter", "Tools.FindBook", "Tools.Sensitive",
            "Tools.Spacing", "Tools.Invisibles", "Tools.CommentAdd", "Tools.CommentList",
        };

        foreach (var lang in new[] { "zh-CN", "en-US", "ja-JP" })
        {
            var path = Path.Combine(DesktopRoot(), "desktop-wpf", "Strings", lang + ".xaml");
            var doc = XDocument.Load(path);
            var values = doc.Descendants()
                .Select(e => ((string?)e.Attribute(X + "Key"), e.Value))
                .Where(t => t.Item1 is not null)
                .ToDictionary(t => t.Item1!, t => t.Item2);

            foreach (var key in shellKeys)
                Assert.True(values.ContainsKey(key), $"{lang} 缺少外壳键 {key}");
            Assert.Equal(expectedMode[lang], values["Mode.Assist"]);

            var merged = doc.Descendants(P + "ResourceDictionary.MergedDictionaries")
                .Elements(P + "ResourceDictionary")
                .Select(e => (string?)e.Attribute("Source"))
                .OfType<string>()
                .ToHashSet();
            foreach (var feature in new[] { "Ai", "Notes", "Find", "Versions", "Sensitive" })
                Assert.Contains($"{feature}.{lang}.xaml", merged);
        }
    }
}
