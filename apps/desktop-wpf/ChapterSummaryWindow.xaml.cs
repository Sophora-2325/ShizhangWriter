using System.Windows;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Models;

namespace NovelGm.Desktop;

/// <summary>摘要独立查看，不占正文高度。窗口内容绑定明确的作品和章节。</summary>
public partial class ChapterSummaryWindow : Window
{
    internal string ProjectId { get; }
    internal int ChapterNo { get; }
    private ChapterDto _chapter;
    private string? _status;

    public ChapterSummaryWindow(string projectId, ChapterDto chapter)
    {
        InitializeComponent();
        ProjectId = projectId; ChapterNo = chapter.No; _chapter = chapter;
        ShowChapter(chapter);
        LanguageManager.LanguageChanged += LanguageChanged;
        Closed += (_, _) => LanguageManager.LanguageChanged -= LanguageChanged;
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            MaxWidth = area.Width; MaxHeight = area.Height;
            MinWidth = Math.Min(MinWidth, area.Width); MinHeight = Math.Min(MinHeight, area.Height);
            Width = Math.Min(Width, area.Width); Height = Math.Min(Height, area.Height);
        };
    }

    internal void ShowChapter(ChapterDto chapter, string? status = null)
    {
        if (chapter.No != ChapterNo) throw new ArgumentException("Summary chapter mismatch", nameof(chapter));
        _chapter = chapter; _status = status;
        Render();
    }

    private void LanguageChanged(object? sender, EventArgs e) => Render();
    private void Render()
    {
        Title = L.T("Main.ChapterSummary") + " · " + ChapterNo + (string.IsNullOrWhiteSpace(_chapter.Title) ? "" : " · " + _chapter.Title);
        ChapterTitle.Text = Title;
        var notices = new List<string>();
        var status = _status ?? _chapter.Summary_Status;
        if (_chapter.Summary is null || status is "failed" or "stale" or "extracting")
            notices.Add(L.T("Memory.Summary." + status));
        var changed = _chapter.Upstream_Changed.Union(_chapter.Plan_Upstream_Changed).OrderBy(n => n).ToList();
        if (changed.Count > 0) notices.Add(L.T("Memory.Upstream", string.Join(", ", changed)));
        NoticeText.Text = string.Join("\n", notices);
        NoticeText.Visibility = notices.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        SummaryBox.Text = _chapter.Summary is not { } summary ? "" :
            summary.One_Line + "\n\n" + string.Join("\n\n", summary.Beats.OrderBy(b => b.Order).Select(b =>
                "· " + b.What_Happened + (string.IsNullOrEmpty(b.Evidence) ? "" : "\n「" + b.Evidence + "」")));
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
