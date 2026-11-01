using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Controls;
using NovelGm.Desktop.Models;
using Xunit;

namespace NovelGm.Desktop.Tests;

public class LoreMiscLayoutTests
{
    static LoreMiscLayoutTests() =>
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(Application).TypeHandle);

    [Fact]
    public void Wide_and_narrow_layouts_keep_the_save_bar_and_the_open_editor() => Sta(() =>
    {
        EnsureStrings();
        var misc = new LoreMisc();
        misc.Load(new ProjectDto
        {
            Id = "a",
            Forbidden = new() { "不要写死" },
            Style = new StyleProfileDto
            {
                Sentence_Length = "medium",
                Reference_Samples = new() { new string('文', 40) + "\n第二行完整保留", "二", "三", "四" },
            },
        });
        Assert.Equal(new string('文', 40) + "\n第二行完整保留", ((TextBlock)misc.FindName("SamplePreviewText")).Text);
        Assert.Equal(Visibility.Collapsed, ((UIElement)misc.FindName("SamplesBody")).Visibility);
        Assert.NotEqual(Visibility.Visible, ((UIElement)misc.FindName("ExtraHost")).Visibility);
        Assert.Contains("仅保存", misc.SampleCaption(3));

        ((Button)misc.FindName("SampleEditButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var row = (Panel)((Panel)misc.FindName("SampleList")).Children[0];
        var buttons = row.Children.OfType<Panel>().SelectMany(panel => panel.Children.OfType<Button>()).ToList();
        Assert.Equal(2, buttons.Count);
        Assert.Equal(3, buttons[1].ContextMenu!.Items.Count);
        buttons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var editor = row.Children.OfType<TextBox>().Single();
        editor.Select(2, 3);

        Arrange(misc, 640, 480);
        var rules = (FrameworkElement)misc.FindName("RulesColumn");
        var style = (FrameworkElement)misc.FindName("StyleColumn");
        Assert.Equal(0, Grid.GetRow(rules));
        Assert.Equal(2, Grid.GetColumn(rules));
        Assert.True(Math.Abs(style.ActualWidth - rules.ActualWidth) < 2);
        Assert.Equal(HorizontalAlignment.Right, ((FrameworkElement)misc.FindName("SaveButton")).HorizontalAlignment);
        Assert.Same(editor, row.Children.OfType<TextBox>().Single());
        Assert.Equal(2, editor.SelectionStart);
        Assert.Equal(3, editor.SelectionLength);

        Arrange(misc, 300, 480);
        Assert.Equal(1, Grid.GetRow(rules));
        Assert.Equal(0, Grid.GetColumn(rules));
        Arrange(misc, 240, 180);
        Assert.Equal(1, Grid.GetRow(rules));
        Assert.Equal(HorizontalAlignment.Stretch, ((FrameworkElement)misc.FindName("SaveButton")).HorizontalAlignment);
        var scroll = (ScrollViewer)misc.FindName("PageScroll");
        Assert.Equal(ScrollBarVisibility.Disabled, scroll.HorizontalScrollBarVisibility);
        Assert.True(scroll.ScrollableWidth < 1);
        Assert.True(((FrameworkElement)misc.FindName("SaveButton")).ActualHeight > 20);
        Assert.True(((FrameworkElement)misc.FindName("Form")).ActualHeight > scroll.ViewportHeight);
        Assert.Same(editor, row.Children.OfType<TextBox>().Single());
        Assert.Equal(2, editor.SelectionStart);
        Assert.Equal(3, editor.SelectionLength);
    });

    [Fact]
    public void Rule_sections_expand_in_place_without_counting_as_edits() => Sta(() =>
    {
        EnsureStrings();
        var project = new ProjectDto
        {
            Id = "a",
            Forbidden = new() { "不要写死" },
            Style = new StyleProfileDto { Sentence_Length = "medium" },
        };
        var misc = new LoreMisc();
        misc.Load(project);
        Assert.Equal(Visibility.Visible, ((UIElement)misc.FindName("EmptySamples")).Visibility);

        ((Button)misc.FindName("ForbiddenToggle")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(misc.HasUnsaved);
        Assert.Equal(Visibility.Visible, ((UIElement)misc.FindName("ForbiddenBody")).Visibility);
        ((Button)misc.FindName("BannedToggle")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(Visibility.Collapsed, ((UIElement)misc.FindName("ForbiddenBody")).Visibility);
        Assert.Equal(Visibility.Visible, ((UIElement)misc.FindName("BannedBody")).Visibility);
        ((TextBox)misc.FindName("BannedBox")).Text = "草稿";
        ((Button)misc.FindName("ForbiddenToggle")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("草稿", ((TextBox)misc.FindName("BannedBox")).Text);
        Assert.Equal(Visibility.Collapsed, ((UIElement)misc.FindName("BannedBody")).Visibility);
        Assert.True(misc.HasUnsaved);

        misc.DiscardDraft();
        Assert.False(misc.HasUnsaved);
        Assert.Equal(Visibility.Visible, ((UIElement)misc.FindName("ForbiddenBody")).Visibility);
        misc.Load(project);
        Assert.Equal(Visibility.Visible, ((UIElement)misc.FindName("ForbiddenBody")).Visibility);
        misc.Load(new ProjectDto { Id = "b", Style = new StyleProfileDto { Sentence_Length = "medium" } });
        Assert.Equal(Visibility.Collapsed, ((UIElement)misc.FindName("ForbiddenBody")).Visibility);

        misc.Load(project);
        misc.RevealForbidden("不要写死");
        var forbidden = (TextBox)misc.FindName("ForbiddenBox");
        Assert.Equal(Visibility.Visible, ((UIElement)misc.FindName("ForbiddenBody")).Visibility);
        Assert.Equal("不要写死", forbidden.SelectedText);
        ((Button)misc.FindName("ForbiddenHelp")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(Visibility.Visible, ((UIElement)misc.FindName("ForbiddenDetail")).Visibility);
        Assert.False(misc.HasUnsaved);
    });

    [Fact]
    public void Themes_and_languages_expose_the_overview_copy() => Sta(() =>
    {
        Assert.Equal(Color.FromRgb(0xA6, 0x40, 0x2E), Brush(false, "zhusha"));
        Assert.Equal(Color.FromRgb(0xD0, 0x72, 0x5C), Brush(true, "zhusha"));
        Assert.Equal(Color.FromRgb(0x2C, 0x4A, 0x73), Brush(false, "dianqing"));
        Assert.Equal(Color.FromRgb(0x8F, 0xBF, 0x9F), Brush(true, "songlv"));

        var zh = LanguageManager.LoadStrings("zh-CN");
        var en = LanguageManager.LoadStrings("en-US");
        var ja = LanguageManager.LoadStrings("ja-JP");
        Assert.Equal("全书生效", zh["Misc.ScopeBadge"]);
        Assert.Equal("前三段用于生成", zh["Misc.PreviewUse"]);
        Assert.Equal("留空会恢复内置默认。", zh["Misc.KeepDefault"]);
        Assert.NotEqual(zh["Misc.ScopeBadge"], en["Misc.ScopeBadge"]);
        Assert.NotEqual(zh["Misc.PreviewUse"], ja["Misc.PreviewUse"]);
        Assert.False(string.IsNullOrWhiteSpace((string)en["Misc.ScopeLine"]));
        Assert.False(string.IsNullOrWhiteSpace((string)ja["Misc.NoAutoReplace"]));
    });

    private static Color Brush(bool dark, string accent) =>
        ((SolidColorBrush)ThemeManager.CreatePalette(dark, accent)["AccentBrush"]).Color;

    private static void Arrange(FrameworkElement element, double width, double height)
    {
        element.Width = width;
        element.Height = height;
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    private static void EnsureStrings()
    {
        var app = Application.Current;
        if (app is null) app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (app.TryFindResource("Misc.Builtin") is string) return;
        app.Resources.MergedDictionaries.Add(LanguageManager.LoadStrings("zh-CN"));
    }

    private static void Sta(Action action) => StaRunner.Run(action, TimeSpan.FromSeconds(20));
}
