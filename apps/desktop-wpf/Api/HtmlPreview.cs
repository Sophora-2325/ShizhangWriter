using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Media;
using NovelGm.Desktop.Models;

namespace NovelGm.Desktop.Api;

/// <summary>Renders chapters to a temporary HTML page and opens it in the default browser.</summary>
internal static class HtmlPreview
{
    public static string Heading(ChapterDto ch) =>
        ch.Title == $"第{ch.No}章" || string.IsNullOrWhiteSpace(ch.Title) ? $"第{ch.No}章" : $"第{ch.No}章 {ch.Title}";

    public static void OpenChapter(string workTitle, ChapterDto chapter) =>
        Open($"{workTitle} · {Heading(chapter)}", workTitle, new[] { chapter });

    public static void OpenWork(string workTitle, IEnumerable<ChapterDto> chapters) =>
        Open(workTitle, workTitle, chapters.Where(c => c.Status != "superseded").ToList());

    private static void Open(string docTitle, string workTitle, IReadOnlyList<ChapterDto> chapters)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><title>")
          .Append(WebUtility.HtmlEncode(docTitle))
          .Append("</title><style>")
          .Append("body{max-width:760px;margin:40px auto;padding:0 24px;color:#1f1f1f;background:#fafaf7;")
          .Append("font-family:").Append(CssBodyFont()).Append(";font-size:17px;line-height:1.95}")
          .Append("h1{font-size:26px;text-align:center;margin-bottom:40px}")
          .Append("h2{font-size:21px;margin:48px 0 20px;border-bottom:1px solid #e5e5e5;padding-bottom:8px}")
          .Append("p{text-indent:2em;margin:0 0 .6em}.empty{color:#999;text-indent:0}")
          .Append("</style></head><body>");
        sb.Append("<h1>").Append(WebUtility.HtmlEncode(workTitle)).Append("</h1>");
        if (chapters.Count == 0)
            sb.Append("<p class=\"empty\">（还没有章节）</p>");
        foreach (var ch in chapters)
        {
            sb.Append("<h2>").Append(WebUtility.HtmlEncode(Heading(ch))).Append("</h2>");
            var lines = (ch.Body_Text ?? "").Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToList();
            if (lines.Count == 0)
                sb.Append("<p class=\"empty\">（尚未生成正文）</p>");
            foreach (var line in lines)
                sb.Append("<p>").Append(WebUtility.HtmlEncode(line)).Append("</p>");
        }
        sb.Append("</body></html>");

        var dir = Path.Combine(Path.GetTempPath(), "novel-gm-preview");
        Directory.CreateDirectory(dir);
        var name = string.Concat(docTitle.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var file = Path.Combine(dir, name + ".html");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
        Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
    }

    /// <summary>
    /// 预览是在系统浏览器里打开的，用的是这台机器上已装的字体；
    /// 直接把当前主题的正文字体转成 CSS，编辑器和预览就不会呈现成两种字。
    /// </summary>
    private static string CssBodyFont()
    {
        var source = (Application.Current?.TryFindResource("BodyFontFamily") as FontFamily)?.Source;
        var names = source?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names is null || names.Length == 0) return "'Microsoft YaHei',serif";
        return string.Join(",", names.Select(n => "\"" + n.Replace("\"", "") + "\"")) + ",serif";
    }
}
