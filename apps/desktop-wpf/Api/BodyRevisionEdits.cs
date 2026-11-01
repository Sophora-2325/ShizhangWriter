using System.Windows;
using System.Windows.Documents;
using NovelGm.Desktop.Models;

namespace NovelGm.Desktop.Api;

internal static class BodyRevisionEdits
{
    internal static (string Text, string Rich) Apply(ChapterDto chapter, IEnumerable<BodyRevisionPatchDto> selected)
    {
        var patches = selected.OrderByDescending(p => p.Start).ToList();
        if (patches.Count == 0 || patches.Select(p => p.Id).Distinct().Count() != patches.Count)
            throw new InvalidOperationException(L.T("Ai.Revision.Invalid"));
        var document = RichBody.BuildVerified(chapter.Body_Text, chapter.Body_Rich);
        var source = chapter.Body_Text.Replace("\r\n", "\n").Replace('\r', '\n');
        var right = int.MaxValue;
        foreach (var p in patches)
        {
            var start = Utf16Offset(source, p.Start); var end = Utf16Offset(source, p.End);
            if (p.Unavailable.Length > 0 || end > right || source[start..end] != p.Before ||
                p.Before.Contains('\n') || p.After.Contains('\n') || p.After.Contains('\r'))
                throw new InvalidOperationException(L.T("Ai.Revision.Invalid"));
            var map = new BodyTextMap(document);
            var hits = map.Matches(p.Before, true);
            if (hits.Count != 1) throw new InvalidOperationException(L.T("Ai.Revision.Invalid"));
            var from = map.Pointer(hits[0]); var to = map.Pointer(hits[0] + p.Before.Length);
            var paragraph = from.Paragraph;
            if (paragraph is null || paragraph != to.Paragraph || Protected(paragraph))
                throw new InvalidOperationException(L.T("Ai.Revision.Protected"));
            var range = new TextRange(from, to);
            var properties = new[] { TextElement.FontFamilyProperty, TextElement.FontSizeProperty,
                TextElement.FontWeightProperty, TextElement.FontStyleProperty, TextElement.ForegroundProperty };
            var initial = new TextRange(from, from.GetNextInsertionPosition(LogicalDirection.Forward) ?? to);
            var values = properties.Select(initial.GetPropertyValue).ToArray();
            range.Text = p.After;
            for (var i = 0; i < properties.Length; i++)
                if (values[i] != DependencyProperty.UnsetValue) range.ApplyPropertyValue(properties[i], values[i]);
            source = source[..start] + p.After + source[end..]; right = start;
        }
        var result = RichBody.Capture(document);
        if (result.Text != source) throw new InvalidOperationException(L.T("Ai.Revision.Invalid"));
        return result;
    }
    internal static int Utf16Offset(string value, int codepoints)
    {
        if (codepoints < 0) throw new InvalidOperationException(L.T("Ai.Revision.Invalid"));
        var offset = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (codepoints-- == 0) return offset;
            offset += rune.Utf16SequenceLength;
        }
        if (codepoints != 0) throw new InvalidOperationException(L.T("Ai.Revision.Invalid"));
        return offset;
    }
    private static bool Protected(Paragraph paragraph) =>
        paragraph.Tag?.ToString()?.Contains("heading", StringComparison.OrdinalIgnoreCase) == true ||
        (paragraph.TextAlignment == TextAlignment.Center && paragraph.FontStyle == FontStyles.Italic) ||
        (paragraph.FontWeight.ToOpenTypeWeight() >= 600 && paragraph.FontSize >= 20.8) ||
        BodyDocument.Inlines(paragraph.Inlines).Any(i => i is InlineUIContainer or Figure or Floater ||
            (i.FontWeight.ToOpenTypeWeight() >= 600 && i.FontSize >= 20.8) ||
            i.Tag?.ToString() is "rev-del" or "rev-ins" ||
            (i is Run run && (run.Text.Contains(BodyFigures.MarkerPrefix) || run.Text.Contains("«"))));
}
