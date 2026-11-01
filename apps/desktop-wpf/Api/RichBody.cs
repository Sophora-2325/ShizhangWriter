using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Windows;
using System.Windows.Documents;
using NovelGm.Desktop.Controls;

namespace NovelGm.Desktop.Api;

/// <summary>正文在 FlowDocument（带格式）与纯文本 .md 之间的转换。</summary>
public static class RichBody
{
    internal static FlowDocument BuildVerified(string text, string rich)
    {
        if (string.IsNullOrEmpty(rich)) return FromPlain(text);
        var doc = NewDocument();
        if (!TryLoadXaml(doc, rich) || !SameProse(PlainText(doc), text))
            throw new InvalidOperationException(L.T("Ai.Revision.Invalid"));
        return doc;
    }
    public static FlowDocument Build(string text, string rich, string? workRoot = null)
    {
        if (!string.IsNullOrEmpty(rich))
        {
            var doc = NewDocument();
            if (TryLoadXaml(doc, rich) && SameProse(PlainText(doc), text))
            {
                BodyReview.Hydrate(doc);
                if (!string.IsNullOrWhiteSpace(workRoot)) BodyFigures.Hydrate(doc, workRoot);
                return doc;
            }
        }
        return FromPlain(text);
    }

    public static FlowDocument FromPlain(string text)
    {
        var doc = NewDocument();
        foreach (var line in (text ?? "").Replace("\r\n", "\n").Split('\n'))
            doc.Blocks.Add(new Paragraph(new Run(line)) { Margin = FormatToolbar.BodyMargin });
        return doc;
    }

    /// <summary>把续写内容接在文档末尾：第一行并入最后一段，其余各行各成一段，原有格式不动。</summary>
    public static void AppendText(FlowDocument doc, string rest)
    {
        var lines = (rest ?? "").Replace("\r\n", "\n").Split('\n');
        if (doc.Blocks.LastBlock is Paragraph last)
            last.Inlines.Add(new Run(lines[0]));
        else
            doc.Blocks.Add(new Paragraph(new Run(lines[0])) { Margin = FormatToolbar.BodyMargin });
        foreach (var line in lines.Skip(1))
            doc.Blocks.Add(new Paragraph(new Run(line)) { Margin = FormatToolbar.BodyMargin });
    }

    public static string PlainText(FlowDocument doc) =>
        Normalize(new TextRange(doc.ContentStart, doc.ContentEnd).Text);

    /// <summary>编辑器里的实时纯正文：去掉插图标记与未接受的删除标记，供复制与统计使用。</summary>
    public static string LiveProse(FlowDocument doc) => Prose(PlainText(doc));

    /// <summary>取出要写入章节的纯文本和格式。插画、题注标记和未接受的删除都不进纯文本。</summary>
    public static (string Text, string Rich) Capture(FlowDocument doc)
    {
        if (Snapshot(doc) is { } copy)
        {
            BodyFigures.TakeForSave(copy.Document);
            BodyReview.TakeForSave(copy.Document);
            return (Prose(PlainText(copy.Document)), Serialize(copy.Document));
        }
        // 快照失败时只能临时改正在编辑的文档；组字期间这样做会让输入法字数失步而 FailFast，改为下次再存。
        if (BodyEditing.IsComposingIn(doc)) throw new CaptureDeferredException();
        using (BodyFigures.SuspendUndo(doc))
        {
            var figures = BodyFigures.TakeForSave(doc);
            var review = BodyReview.TakeForSave(doc);
            try
            {
                return (Prose(PlainText(doc)), Serialize(doc));
            }
            finally
            {
                BodyReview.RestoreAfterSave(doc, review);
                BodyFigures.RestoreAfterSave(doc, figures);
            }
        }
    }

    public static string SaveXaml(FlowDocument doc) => Capture(doc).Rich;

    /// <summary>本次不能安全取出正文（正在组字），调用方保留脏标记稍后重试。</summary>
    internal sealed class CaptureDeferredException : Exception
    {
    }

    private static string Serialize(FlowDocument doc)
    {
        using var ms = new MemoryStream();
        new TextRange(doc.ContentStart, doc.ContentEnd).Save(ms, DataFormats.Xaml);
        return StripInheritedDefaults(Encoding.UTF8.GetString(ms.ToArray()));
    }

    /// <summary>
    /// 存盘只动副本。正在编辑的文档一旦被临时换出插画再换回，输入法记的字数就和文档对不上，
    /// 下一次删除或剪切时 WPF 会直接终止进程。
    /// </summary>
    private static System.Windows.Controls.RichTextBox? Snapshot(FlowDocument doc)
    {
        var images = BodyDocument.Blocks(doc.Blocks)
            .SelectMany(block => block switch
            {
                BlockUIContainer { Child: System.Windows.Controls.Image image } => new[] { image },
                Paragraph paragraph => BodyDocument.Inlines(paragraph.Inlines).OfType<InlineUIContainer>()
                    .Select(inline => inline.Child).OfType<System.Windows.Controls.Image>(),
                _ => Enumerable.Empty<System.Windows.Controls.Image>(),
            })
            .ToList();
        var sources = images.Select(image => image.Source).ToList();
        string xaml;
        try
        {
            foreach (var image in images) image.Source = null;
            xaml = System.Windows.Markup.XamlWriter.Save(doc);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            for (var i = 0; i < images.Count; i++) images[i].Source = sources[i];
        }
        try
        {
            var copy = (FlowDocument)System.Windows.Markup.XamlReader.Parse(xaml);
            var box = new System.Windows.Controls.RichTextBox { Document = copy };
            if (doc.Parent is System.Windows.Controls.RichTextBox host)
            {
                box.Resources = host.Resources;
                box.FontFamily = host.FontFamily;
                box.FontSize = host.FontSize;
                box.Foreground = host.Foreground;
                box.FlowDirection = host.FlowDirection;
            }
            return box;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// TextRange.Save 会把当时从编辑器继承到的字色写进 XAML；去掉它，正文颜色才能随浅色/深色主题变化。
    /// 用户自己设的其它字色不在这里，会原样保留。0.3.12 起主题字色换成纸墨色，四种默认字色都要认。
    /// </summary>
    private static readonly Regex ThemeForeground =
        new(@"\s+Foreground=""#FF(262626|E6E6E6|16181B|E9E6DE)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 字体和字号也是「继承到的」会被一起写进 XAML，同样得剥掉，否则：
    /// 换主题不会换正文字体、改默认字号对老章节无效、格式栏还会因此认不出「正文」预设。
    /// 只剥应用自己用过的默认值——用户手动选过的字体/字号取值不同，原样保留。
    /// </summary>
    private static readonly Regex InheritedFont = new(
        @"\s+(?:FontFamily=""(?:Microsoft YaHei|Cambria,?\s*(?:SimSun|KaiTi|FangSong),?\s*(?:宋体|楷体|仿宋)?|Segoe UI,?\s*(?:DengXian|等线)?)""|FontSize=""1[67]"")",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string StripInheritedDefaults(string xaml)
    {
        // Only the root carries the editor's inherited defaults. Local overrides are author data,
        // except a Foreground equal to the theme ink: the "正文" preset writes it, and keeping it
        // would pin that paragraph to the light (or dark) palette when the theme changes.
        var document = XDocument.Parse(xaml, LoadOptions.PreserveWhitespace);
        if (document.Root is { } root)
        {
            foreach (var attribute in root.Attributes().ToList())
                if (InheritedFont.IsMatch(" " + attribute) || ThemeForeground.IsMatch(" " + attribute)) attribute.Remove();
            foreach (var element in root.Descendants())
                foreach (var attribute in element.Attributes().ToList())
                    if (ThemeForeground.IsMatch(" " + attribute)) attribute.Remove();
        }
        return document.ToString(SaveOptions.DisableFormatting);
    }

    private static bool TryLoadXaml(FlowDocument doc, string xaml)
    {
        try
        {
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(StripInheritedDefaults(xaml)));
            new TextRange(doc.ContentStart, doc.ContentEnd).Load(ms, DataFormats.Xaml);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static FlowDocument NewDocument() => new() { PagePadding = new Thickness(4, 2, 4, 2) };

    private static string Normalize(string s) => (s ?? "").Replace("\r\n", "\n").TrimEnd('\n');

    /// <summary>插画在存盘的格式里是一行标记，纯文本里没有这行。比较时两边都去掉。</summary>
    private static bool SameProse(string richText, string plain)
    {
        return Prose(richText) == Prose(plain);
    }

    private static string Prose(string s)
    {
        var lines = Normalize(s).Replace("\uFFFC", "").Split('\n')
            .Select(line => line.Trim())
            .Select(BodyFigures.StripMarkers)
            .Select(BodyReview.StripMarkers);
        return string.Join("\n", lines).TrimEnd('\n');
    }
}
