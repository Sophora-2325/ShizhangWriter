using System.Xml.Linq;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Controls;
using Xunit;

namespace NovelGm.Desktop.Tests;

public class ToolsPanelTests
{
    [Fact]
    public void Tools_panel_groups_follow_chapter_find_edit_publish_order()
    {
        var commands = BodyEditing.Commands();
        Assert.Equal(4, commands.Count);
        Assert.Equal(4, commands.Select(c => c.Key).Distinct().Count());
        Assert.All(commands, c => Assert.Equal("Tools.GroupEditComment", c.Group));
        Assert.Equal(new[] { "Tools.Spacing", "Tools.Invisibles", "Tools.CommentAdd", "Tools.CommentList" },
            commands.Select(c => c.Key));

        var entries = ToolsPanel.Entries();
        Assert.Equal(9, entries.Count);
        Assert.Equal(new[] { "stats", "copy", "export-chapter", "find-chapter", "find-book", "sensitive", "versions", "backup", "publish" },
            entries.Select(e => e.Action));
        Assert.Equal(new[] { "Tools.GroupChapter", "Tools.GroupChapter", "Tools.GroupChapter",
            "Tools.GroupFind", "Tools.GroupFind", "Tools.GroupFind",
            "Tools.GroupPublish", "Tools.GroupPublish", "Tools.GroupPublish" },
            entries.Select(e => e.GroupKey));

        // 工具台按固定顺序渲染四组；编辑批注命令插在第三组。
        Assert.Equal(new[] { "Tools.GroupChapter", "Tools.GroupFind", "Tools.GroupEditComment", "Tools.GroupPublish" },
            ToolsPanel.GroupOrder);
        var renderedGroups = entries.Select(e => e.GroupKey)
            .Concat(commands.Select(c => c.Group))
            .Distinct()
            .OrderBy(g => Array.IndexOf(ToolsPanel.GroupOrder, g))
            .ToList();
        Assert.Equal(ToolsPanel.GroupOrder, renderedGroups);
    }

    [Fact]
    public void Tools_panel_and_edit_commands_have_translations_in_every_language()
    {
        var commands = BodyEditing.Commands();
        var entries = ToolsPanel.Entries();
        var keys = commands.SelectMany(c => new[] { c.Group, c.Key })
            .Concat(entries.SelectMany(e => new[] { e.GroupKey, e.Key }))
            .Append("Rail.Tools")
            .Distinct()
            .ToList();

        foreach (var lang in new[] { "zh-CN", "en-US", "ja-JP" })
        {
            var defined = StringKeys(lang);
            var missing = keys.Where(k => !defined.Contains(k)).ToList();
            Assert.True(missing.Count == 0, $"{lang} 缺少文案：{string.Join(", ", missing)}");
        }
    }

    /// <summary>汇总 Strings 目录下当前语言的所有字典（聚合字典 + 各功能字典）。</summary>
    private static HashSet<string> StringKeys(string lang)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "desktop-wpf", "Strings")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var folder = Path.Combine(dir!.FullName, "desktop-wpf", "Strings");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var keys = new HashSet<string>();
        foreach (var file in Directory.GetFiles(folder, "*.xaml"))
        {
            var name = Path.GetFileName(file);
            if (name != lang + ".xaml" && !name.EndsWith("." + lang + ".xaml", StringComparison.OrdinalIgnoreCase)) continue;
            keys.UnionWith(XDocument.Load(file).Descendants()
                .Select(e => (string?)e.Attribute(x + "Key"))
                .OfType<string>());
        }
        return keys;
    }
}
