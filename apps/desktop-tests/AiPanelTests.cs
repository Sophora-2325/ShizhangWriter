using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using System.Xml.Linq;
using NovelGm.Desktop.Controls;
using Xunit;

namespace NovelGm.Desktop.Tests;

public class AiPanelTests
{
    [Fact]
    public void Generate_is_the_single_primary_action_in_the_writing_flow() => Sta(() =>
    {
        var panel = new AiPanel();
        foreach (var button in FlowButtons(panel).Where(b => b != panel.GenerateButton))
            Assert.Same(panel.PlanButton.Style, button.Style);
        Assert.NotSame(panel.PlanButton.Style, panel.GenerateButton.Style);
        Assert.Equal(panel.PlanButton.Padding, panel.GenerateButton.Padding);
    });

    [Fact]
    public void Ai_panel_raises_all_nine_flow_events_once() => Sta(() =>
    {
        var panel = new AiPanel();
        panel.SetChapterState(true, false, true, true, false, false);

        var plan = 0; var aiPlan = 0; var generate = 0; var finalize = 0; var audit = 0;
        var planCheck = 0; var extract = 0; var review = 0;
        panel.ShowPlanRequested += (_, _) => plan++;
        var stacked = 0;
        panel.ShowStackedConstraintsRequested += (_, _) => stacked++;
        panel.AiPlanRequested += (_, _) => aiPlan++;
        panel.GenerateRequested += (_, _) => generate++;
        panel.FinalizeRequested += (_, _) => finalize++;
        panel.AuditRequested += (_, _) => audit++;
        panel.PlanCheckRequested += (_, _) => planCheck++;
        panel.ExtractRequested += (_, _) => extract++;
        panel.ShowReviewRequested += (_, _) => review++;

        foreach (var button in FlowButtons(panel))
        {
            Assert.True(button.IsEnabled, $"{button.Name} 应该可点");
            Click(button);
        }

        Assert.Equal(1, plan);
        Assert.Equal(1, stacked);
        Assert.Equal(1, aiPlan);
        Assert.Equal(1, generate);
        Assert.Equal(1, finalize);
        Assert.Equal(1, audit);
        Assert.Equal(1, planCheck);
        Assert.Equal(1, extract);
        Assert.Equal(1, review);
        Assert.Null(panel.FindName("PrevButton"));
        Assert.Null(panel.FindName("NextButton"));
    });

    [Fact]
    public void Busy_disables_every_action_and_blocks_events() => Sta(() =>
    {
        var panel = new AiPanel();
        panel.SetChapterState(true, false, true, true, false, false);
        var clicks = 0;
        panel.GenerateRequested += (_, _) => clicks++;

        panel.SetBusy("Busy.Generating");
        Assert.Equal(Visibility.Visible, panel.BusyBox.Visibility);
        Assert.False(string.IsNullOrWhiteSpace(panel.BusyText.Text));
        Assert.All(AllButtons(panel), button => Assert.False(button.IsEnabled, $"{button.Name} 忙碌时应禁用"));
        Click(panel.GenerateButton);
        Assert.Equal(0, clicks);

        panel.SetBusy(null);
        Assert.Equal(Visibility.Collapsed, panel.BusyBox.Visibility);
        Assert.True(panel.GenerateButton.IsEnabled);
    });

    [Fact]
    public void Chapter_state_controls_button_matrix() => Sta(() =>
    {
        var panel = new AiPanel();

        panel.SetChapterState(false, false, false, false, false, false);
        Assert.True(panel.AiPlanButton.IsEnabled, "无章节时允许打开显式创建方案的空态");
        Assert.All(AllButtons(panel).Where(b => b != panel.AiPlanButton), button => Assert.False(button.IsEnabled));

        panel.SetChapterState(true, true, true, false, false, false);
        Assert.True(panel.PlanButton.IsEnabled);
        Assert.True(panel.ReviewButton.IsEnabled);
        Assert.True(panel.StackedConstraintsButton.IsEnabled);
        Assert.True(panel.GenerateButton.IsEnabled);
        Assert.False(panel.FinalizeButton.IsEnabled, "无正文不能定稿");
        Assert.False(panel.AuditButton.IsEnabled, "无正文不能审计");
        Assert.False(panel.PlanCheckButton.IsEnabled, "无正文不能计划校验");
        Assert.False(panel.ExtractButton.IsEnabled, "无正文不能提取");

        panel.SetChapterState(true, false, true, true, false, true);
        Assert.Equal(Visibility.Visible, panel.ReadOnlyHint.Visibility);
        Assert.True(panel.AiPlanButton.IsEnabled, "只读章允许查看已有方案");
        Assert.False(panel.GenerateButton.IsEnabled);
        Assert.False(panel.FinalizeButton.IsEnabled);
        Assert.False(panel.AuditButton.IsEnabled);
        Assert.False(panel.PlanCheckButton.IsEnabled);
        Assert.False(panel.ExtractButton.IsEnabled);
        Assert.True(panel.PlanButton.IsEnabled);
        Assert.True(panel.ReviewButton.IsEnabled);
    });

    [Fact]
    public void Writing_reference_section_is_removed() => Sta(() =>
    {
        var panel = new AiPanel();
        Assert.Null(panel.FindName("ReferenceExpander"));
        Assert.Null(panel.FindName("ReferenceText"));
    });

    [Fact]
    public void Ai_dictionary_uses_the_same_key_set_in_every_language()
    {
        var sets = new[] { "zh-CN", "en-US", "ja-JP" }
            .Select(lang => FeatureKeys(lang).Where(k => k.StartsWith("Ai.", StringComparison.Ordinal)).OrderBy(k => k).ToList())
            .ToList();
        Assert.NotEmpty(sets[0]);
        Assert.True(sets[0].SequenceEqual(sets[1]), "zh-CN / en-US 键集合不一致");
        Assert.True(sets[0].SequenceEqual(sets[2]), "zh-CN / ja-JP 键集合不一致");
        Assert.Contains("Ai.Title", sets[0]);
        Assert.Contains("Ai.Step.Next", sets[0]);
        Assert.Contains("Ai.Group.Create", sets[0]);
        Assert.DoesNotContain(sets[0], key => key.StartsWith("Ai.Reference."));
    }

    private static IEnumerable<Button> FlowButtons(AiPanel panel) => new[]
    {
        panel.PlanButton, panel.StackedConstraintsButton, panel.AiPlanButton, panel.GenerateButton, panel.FinalizeButton,
        panel.AuditButton, panel.PlanCheckButton, panel.ExtractButton, panel.ReviewButton,
    };

    private static IEnumerable<Button> AllButtons(AiPanel panel) => FlowButtons(panel);

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    /// <summary>只读面板自己的 Ai.<lang>.xaml，校验三语键集合一致（聚合字典由外壳维护）。</summary>
    private static HashSet<string> FeatureKeys(string lang)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "desktop-wpf", "Strings")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var file = Path.Combine(dir!.FullName, "desktop-wpf", "Strings", "Ai." + lang + ".xaml");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        return XDocument.Load(file).Descendants()
            .Select(e => (string?)e.Attribute(x + "Key"))
            .OfType<string>()
            .ToHashSet();
    }

    private static void Sta(Action action) =>
        StaRunner.Run(action, TimeSpan.FromSeconds(35), "AiPanel 测试超时");
}
