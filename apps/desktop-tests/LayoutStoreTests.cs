using System.Text.Json;
using NovelGm.Desktop.Api;
using Xunit;

namespace NovelGm.Desktop.Tests;

public class LayoutStoreTests
{
    [Fact]
    public void New_layout_defaults_to_tree_and_lore()
    {
        var layout = new PaneLayout();
        Assert.Equal(LayoutStore.LeftTabTree, layout.Left_Tab);
        Assert.Equal(LayoutStore.RightTabLore, layout.Right_Tab);
    }

    [Fact]
    public void Legacy_json_without_left_tab_falls_back_to_tree()
    {
        // 升级前的 layout.json：没有 Left_Tab，Right_Tab 只有 lore/tools/reader
        const string json = """
        {"Left":0.7,"Center":3.35,"Side":0.7,"Right":1.05,
         "Sidebar_Visible":true,"Lore_Visible":true,"Right_Tab":"tools"}
        """;
        var layout = JsonSerializer.Deserialize<PaneLayout>(json);
        Assert.NotNull(layout);
        Assert.Equal(LayoutStore.LeftTabTree, layout!.Left_Tab);
        Assert.Equal(LayoutStore.RightTabTools, layout.Right_Tab);
    }

    [Fact]
    public void Normalize_accepts_new_tabs_and_falls_back_on_unknown_values()
    {
        var layout = new PaneLayout
        {
            Left_Tab = "notes",
            Right_Tab = "ai",
        };
        var normalized = LayoutStore.Normalize(layout);
        Assert.Equal(LayoutStore.LeftTabNotes, normalized.Left_Tab);
        Assert.Equal(LayoutStore.RightTabAi, normalized.Right_Tab);

        normalized.Left_Tab = "find";
        normalized.Right_Tab = "reader";
        LayoutStore.Normalize(normalized);
        Assert.Equal(LayoutStore.LeftTabFind, normalized.Left_Tab);
        Assert.Equal(LayoutStore.RightTabReader, normalized.Right_Tab);

        normalized.Left_Tab = "bogus";
        normalized.Right_Tab = "";
        LayoutStore.Normalize(normalized);
        Assert.Equal(LayoutStore.LeftTabTree, normalized.Left_Tab);
        Assert.Equal(LayoutStore.RightTabLore, normalized.Right_Tab);
    }

    [Fact]
    public void Normalize_is_case_insensitive()
    {
        var layout = new PaneLayout { Left_Tab = "NOTES", Right_Tab = "AI" };
        LayoutStore.Normalize(layout);
        Assert.Equal(LayoutStore.LeftTabNotes, layout.Left_Tab);
        Assert.Equal(LayoutStore.RightTabAi, layout.Right_Tab);
    }

    [Fact]
    public void Layout_round_trips_the_new_fields()
    {
        var layout = new PaneLayout
        {
            Left_Tab = LayoutStore.LeftTabFind,
            Right_Tab = LayoutStore.RightTabAi,
            Lore_Visible = false,
        };
        var json = JsonSerializer.Serialize(layout);
        var restored = JsonSerializer.Deserialize<PaneLayout>(json);
        Assert.NotNull(restored);
        Assert.Equal(LayoutStore.LeftTabFind, restored!.Left_Tab);
        Assert.Equal(LayoutStore.RightTabAi, restored.Right_Tab);
        Assert.False(restored.Lore_Visible);
    }
}
