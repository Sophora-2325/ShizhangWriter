using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace NovelGm.Desktop.Api;

/// <summary>Copies authoring content without detaching UI elements, touching selection, or serializing bitmaps.</summary>
internal static class ReadingSnapshot
{
    public static FlowDocument Clone(FlowDocument source, string? workRoot = null)
    {
        var copy = new FlowDocument { FontFamily = source.FontFamily, FontSize = source.FontSize, Foreground = source.Foreground };
        foreach (var block in source.Blocks) copy.Blocks.Add(CloneBlock(block, workRoot));
        return copy;
    }

    private static void Format(DependencyObject source, DependencyObject target)
    {
        var values = source.GetLocalValueEnumerator();
        while (values.MoveNext())
        {
            var entry = values.Current;
            if (entry.Property.ReadOnly || entry.Property == Run.TextProperty) continue;
            var name = entry.Property.Name;
            if (name is not ("FontFamily" or "FontSize" or "FontWeight" or "FontStyle" or "FontStretch" or "Foreground" or "Background"
                or "TextDecorations" or "TextAlignment" or "TextIndent" or "Margin" or "Padding" or "LineHeight" or "LineStackingStrategy"
                or "FlowDirection" or "KeepTogether" or "KeepWithNext" or "BreakPageBefore" or "MarkerStyle" or "StartIndex"
                or "BorderBrush" or "BorderThickness" or "CellSpacing" or "RowSpan" or "ColumnSpan")) continue;
            var value = source.GetValue(entry.Property);
            if (value is Freezable f) value = f.IsFrozen ? f : f.CloneCurrentValue();
            target.SetValue(entry.Property, value);
        }
    }

    private static UIElement CloneImage(UIElement source, string? workRoot)
    {
        if (source is not Image image) return new TextBlock();
        var copy = new Image { Source = image.Source, Tag = image.Tag, Stretch = Stretch.Uniform,
            HorizontalAlignment = image.HorizontalAlignment, Width = image.Width, Height = image.Height,
            MaxWidth = image.MaxWidth, MaxHeight = image.MaxHeight };
        if (copy.Source is null && BodyFigures.AsInfo(image) is { } info)
        {
            copy.ToolTip = info.Alt;
            if (!string.IsNullOrEmpty(workRoot) && BodyFigures.OriginalOrPreview(info, workRoot) is { } path)
                _ = BodyFigures.LoadIntoAsync(copy, path);
        }
        return copy;
    }

    private static Inline CloneInline(Inline source, string? workRoot)
    {
        Inline copy = source switch
        {
            Run run => new Run(run.Text), LineBreak => new LineBreak(),
            InlineUIContainer ui => new InlineUIContainer(CloneImage(ui.Child, workRoot)),
            Bold => new Bold(), Italic => new Italic(), Underline => new Underline(), _ => new Span(),
        };
        Format(source, copy);
        if (source is Span span && copy is Span target)
            foreach (var child in span.Inlines) target.Inlines.Add(CloneInline(child, workRoot));
        return copy;
    }

    private static Block CloneBlock(Block source, string? workRoot)
    {
        Block copy;
        switch (source)
        {
            case Paragraph p:
                var paragraph = new Paragraph();
                foreach (var inline in p.Inlines) paragraph.Inlines.Add(CloneInline(inline, workRoot));
                copy = paragraph; break;
            case BlockUIContainer ui: copy = new BlockUIContainer(CloneImage(ui.Child, workRoot)); break;
            case Section section:
                var s = new Section(); foreach (var child in section.Blocks) s.Blocks.Add(CloneBlock(child, workRoot)); copy = s; break;
            case System.Windows.Documents.List list:
                var l = new System.Windows.Documents.List();
                foreach (var item in list.ListItems)
                {
                    var li = new ListItem(); Format(item, li);
                    foreach (var child in item.Blocks) li.Blocks.Add(CloneBlock(child, workRoot)); l.ListItems.Add(li);
                }
                copy = l; break;
            case Table table:
                var t = new Table();
                foreach (var column in table.Columns) t.Columns.Add(new TableColumn { Width = column.Width });
                foreach (var group in table.RowGroups)
                {
                    var g = new TableRowGroup(); Format(group, g); t.RowGroups.Add(g);
                    foreach (var row in group.Rows)
                    {
                        var r = new TableRow(); Format(row, r); g.Rows.Add(r);
                        foreach (var cell in row.Cells)
                        {
                            var c = new TableCell(); Format(cell, c); r.Cells.Add(c);
                            foreach (var child in cell.Blocks) c.Blocks.Add(CloneBlock(child, workRoot));
                        }
                    }
                }
                copy = t; break;
            default: copy = new Paragraph(new Run(new TextRange(source.ContentStart, source.ContentEnd).Text)); break;
        }
        Format(source, copy); return copy;
    }
}

internal static class BodyParagraphs
{
    public static bool IsBody(Paragraph p, double bodySize)
    {
        var runs = BodyDocument.Inlines(p.Inlines).OfType<Run>().Where(r => r.Text.Length > 0).ToList();
        // Captions and headings use the existing editor presets; inline emphasis never changes the paragraph role.
        if (p.FontSize > bodySize * 1.15 || p.Margin.Left >= 20) return false;
        if (runs.Count > 0 && runs.All(r => r.FontSize > bodySize * 1.15)) return false;
        if (p.FontStyle == FontStyles.Italic && p.TextAlignment == TextAlignment.Center && p.TextIndent == 0) return false;
        if (p.TextAlignment == TextAlignment.Center && p.TextIndent == 0 && p.Margin.Bottom >= 15 && runs.Count > 0 && runs.All(r => r.FontStyle == FontStyles.Italic)) return false;
        return true;
    }

    public static void Normalize(FlowDocument doc, double bodySize)
    {
        foreach (var p in BodyDocument.Blocks(doc.Blocks).OfType<Paragraph>())
            if (IsBody(p, bodySize)) p.TextAlignment = TextAlignment.Left;
    }
}
