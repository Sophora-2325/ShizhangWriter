using System.Windows;
using System.Xml.Linq;
using NovelGm.Desktop.Models;
using Xunit;

namespace NovelGm.Desktop.Tests;

public class ChapterSummaryTests
{
    [Fact]
    public void Summary_window_displays_chapter_evidence_and_refreshes_independently() => StaRunner.Run(() =>
    {
        var chapter = new ChapterDto { No = 4, Title = "黄昏没有走远", Summary_Status = "ready", Summary = new()
        { One_Line = "在天台重逢", Beats = new() { new() { Order = 2, What_Happened = "牵手", Evidence = "伸出手" }, new() { Order = 1, What_Happened = "相遇" } } } };
        var window = new ChapterSummaryWindow("p", chapter);
        Assert.Equal("p", window.ProjectId); Assert.Equal(4, window.ChapterNo);
        Assert.Contains("黄昏没有走远", window.Title);
        Assert.Contains("在天台重逢", window.SummaryBox.Text); Assert.Contains("「伸出手」", window.SummaryBox.Text);
        Assert.True(window.SummaryBox.Text.IndexOf("相遇", StringComparison.Ordinal) < window.SummaryBox.Text.IndexOf("牵手", StringComparison.Ordinal));
        Assert.True(window.SummaryBox.IsReadOnly); Assert.Equal(Visibility.Collapsed, window.NoticeText.Visibility);
        window.ShowChapter(chapter, "extracting"); Assert.Equal(Visibility.Visible, window.NoticeText.Visibility);
        Assert.Contains("在天台重逢", window.SummaryBox.Text); // 重试时保留上次摘要
        chapter.Summary_Status = "failed"; chapter.Upstream_Changed = new() { 1 }; chapter.Plan_Upstream_Changed = new() { 1, 2 };
        window.ShowChapter(chapter); Assert.Equal(Visibility.Visible, window.NoticeText.Visibility);
        Assert.NotEmpty(window.NoticeText.Text);
        Assert.Throws<ArgumentException>(() => window.ShowChapter(new() { No = 5 }));
        window.Close();
    });

    [Fact]
    public void Pending_empty_summary_and_long_results_fit_resizable_window() => StaRunner.Run(() =>
    {
        var chapter = new ChapterDto { No = 1 };
        var window = new ChapterSummaryWindow("p", chapter);
        Assert.Empty(window.SummaryBox.Text); Assert.Equal(Visibility.Visible, window.NoticeText.Visibility);
        chapter.Summary_Status = "ready"; chapter.Summary = new() { One_Line = new string('长', 6000) };
        window.ShowChapter(chapter);
        var root = (FrameworkElement)window.Content; window.Content = null;
        root.Measure(new Size(320, 300)); root.Arrange(new Rect(0, 0, 320, 300)); root.UpdateLayout();
        Assert.InRange(window.SummaryBox.ActualWidth, 270, 320); Assert.True(window.SummaryBox.ActualHeight > 100);
        Assert.True(window.SummaryBox.ExtentHeight > window.SummaryBox.ViewportHeight);
        window.SummaryBox.ScrollToEnd(); root.UpdateLayout(); Assert.True(window.SummaryBox.VerticalOffset > 0);
        window.Close();
    });

    [Fact]
    public void Main_shell_has_no_bottom_summary_or_narrative_settings_panel()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "desktop-wpf", "MainWindow.xaml"))) folder = folder.Parent;
        Assert.NotNull(folder);
        var xaml = XDocument.Load(Path.Combine(folder!.FullName, "desktop-wpf", "MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var names = xaml.Descendants().Select(e => (string?)e.Attribute(x + "Name")).ToHashSet();
        Assert.DoesNotContain("SummaryCard", names); Assert.DoesNotContain("SummaryText", names);
        Assert.DoesNotContain("NarrativeSettingsView", names); Assert.Contains("CharacterPanel", names);
    }
}
