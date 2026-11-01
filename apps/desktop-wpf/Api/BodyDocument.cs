using System.Text;
using System.Windows;
using System.Windows.Documents;

namespace NovelGm.Desktop.Api;

internal static class BodyDocument
{
    public static Run CopyRun(Run source, string text)
    {
        var result = new Run(text);
        var values = source.GetLocalValueEnumerator();
        while (values.MoveNext())
        {
            var entry = values.Current;
            if (entry.Property.ReadOnly || entry.Property == Run.TextProperty) continue;
            result.SetValue(entry.Property, entry.Value);
        }
        return result;
    }
    public static IEnumerable<Block> Blocks(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            yield return block;
            if (block is Section section)
                foreach (var child in Blocks(section.Blocks)) yield return child;
            if (block is List list)
                foreach (var item in list.ListItems)
                    foreach (var child in Blocks(item.Blocks)) yield return child;
            if (block is Table table)
                foreach (var group in table.RowGroups)
                    foreach (var row in group.Rows)
                        foreach (var cell in row.Cells)
                            foreach (var child in Blocks(cell.Blocks)) yield return child;
        }
    }

    public static IEnumerable<Inline> Inlines(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            yield return inline;
            if (inline is Span span)
                foreach (var child in Inlines(span.Inlines)) yield return child;
        }
    }

    public static InlineCollection? ParentInlines(Inline inline) => inline.Parent switch
    {
        Paragraph paragraph => paragraph.Inlines,
        Span span => span.Inlines,
        _ => null,
    };

    public static BlockCollection? ParentBlocks(Block block) => block.Parent switch
    {
        FlowDocument doc => doc.Blocks, Section section => section.Blocks,
        ListItem item => item.Blocks, TableCell cell => cell.Blocks, _ => null,
    };
}

/// <summary>Searchable text with an exact pointer map. Images are barriers, styles are not.</summary>
internal sealed class BodyTextMap
{
    private sealed record Piece(int Offset, string Text, TextPointer Start, TextPointer End, bool Literal);
    private readonly List<Piece> _pieces = new();
    private readonly FlowDocument _document;
    public string Text { get; }

    public BodyTextMap(FlowDocument document)
    {
        _document = document;
        var text = new StringBuilder();
        for (var p = document.ContentStart; p is not null && p.CompareTo(document.ContentEnd) < 0;)
        {
            var next = p.GetNextContextPosition(LogicalDirection.Forward);
            if (next is null || next.CompareTo(p) <= 0) break;
            var context = p.GetPointerContext(LogicalDirection.Forward);
            string value = "";
            var literal = false;
            if (context == TextPointerContext.Text)
            {
                value = p.GetTextInRun(LogicalDirection.Forward);
                next = p.GetPositionAtOffset(value.Length)!;
                literal = true;
            }
            else if (context == TextPointerContext.EmbeddedElement) value = "\uFFFC";
            else if (context == TextPointerContext.ElementStart && p.GetAdjacentElement(LogicalDirection.Forward) is LineBreak) value = "\n";
            else if (context == TextPointerContext.ElementEnd && p.GetAdjacentElement(LogicalDirection.Forward) is Paragraph) value = "\n";
            if (value.Length > 0) { _pieces.Add(new(text.Length, value, p, next, literal)); text.Append(value); }
            if (next is null || next.CompareTo(p) <= 0) break;
            p = next;
        }
        Text = text.ToString();
    }

    public TextPointer Pointer(int offset)
    {
        offset = Math.Clamp(offset, 0, Text.Length);
        foreach (var piece in _pieces)
            if (offset < piece.Offset + piece.Text.Length)
                return piece.Literal ? piece.Start.GetPositionAtOffset(offset - piece.Offset)! : piece.Start;
        return _document.ContentEnd;
    }

    public int Offset(TextPointer point)
    {
        foreach (var piece in _pieces)
        {
            if (point.CompareTo(piece.Start) <= 0) return piece.Offset;
            if (point.CompareTo(piece.End) < 0)
                return piece.Offset + (piece.Literal ? piece.Start.GetOffsetToPosition(point) : 0);
        }
        return Text.Length;
    }

    public List<int> Matches(string query, bool caseSensitive = false)
    {
        var hits = new List<int>();
        if (string.IsNullOrEmpty(query) || query.Contains('\uFFFC')) return hits;
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        for (var i = Text.IndexOf(query, comparison); i >= 0;
             i = Text.IndexOf(query, i + query.Length, comparison)) hits.Add(i);
        return hits;
    }

    public TextRange Range(int offset, int length) => new(Pointer(offset), Pointer(offset + length));
}
