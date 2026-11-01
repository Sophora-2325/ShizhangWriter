using System.Windows;
using NovelGm.Desktop.Api;

namespace NovelGm.Desktop.Controls;

/// <summary>
/// 本章统计：打开时读取编辑器实时内容；不保存、不生成历史。
/// 主窗切章时会关闭；窗口再次激活时重算一次，避免放着不动读到旧数字。
/// </summary>
public partial class ChapterStatsWindow : Window
{
    private readonly Func<string?> _title;
    private readonly Func<ChapterStats> _stats;

    public ChapterStatsWindow(Func<string?> title, Func<ChapterStats> stats)
    {
        _title = title;
        _stats = stats;
        InitializeComponent();
        Loaded += (_, _) => Render();
        Activated += (_, _) => Render();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Escape) return;
            e.Handled = true;
            Close();
        };
    }

    private void Render()
    {
        var stats = _stats();
        var title = _title();
        ChapterText.Text = string.IsNullOrWhiteSpace(title) ? L.T("Tools.NoChapter") : title;
        WordsText.Text = stats.Words.ToString("N0");
        ParagraphsText.Text = stats.Paragraphs.ToString("N0");
        SelectionText.Text = stats.Selected.ToString("N0");
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
