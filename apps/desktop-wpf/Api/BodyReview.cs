using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace NovelGm.Desktop.Api;

/// <summary>修订、非打印字符和批注。修订与段落标记在存盘时换成标记或直接拿掉，不写进正文纯文本。</summary>
internal static class BodyReview
{
    public const string DelPrefix = "«删:";
    public const string NpTag = "np";
    public const string SpaceTag = "sp";
    public const string DelTag = "rev-del";
    public const string InsTag = "rev-ins";

    public static readonly SolidColorBrush InsertBrush = Frozen(Color.FromRgb(0x15, 0x65, 0xC0));
    public static readonly SolidColorBrush DeleteBrush = Frozen(Color.FromRgb(0xC6, 0x28, 0x28));
    public static readonly SolidColorBrush CommentBrush = Frozen(Color.FromRgb(0x6A, 0x1B, 0x9A));
    public static readonly SolidColorBrush SpaceBrush = Frozen(Color.FromRgb(0xE6, 0xDC, 0xF2));
    public static readonly SolidColorBrush MarkBrush = Frozen(Color.FromRgb(0x9E, 0x9E, 0x9E));

    public static bool ShowInvisibles { get; set; }

    private static readonly Regex DelToken = new(@"«删:[^»]*»", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static string StripMarkers(string text) => DelToken.Replace(text ?? "", "");

    public sealed class Held
    {
        public List<(Run Marker, Run Original)> Deletions { get; } = new();
    }

    public sealed class Note
    {
        public string Id { get; set; } = "";
        public string Quote { get; set; } = "";
        public string Text { get; set; } = "";
        public int Start { get; set; }
        public ReadingAnchor? Anchor { get; set; }
    }

    public static Held TakeForSave(FlowDocument doc)
    {
        var held = new Held();
        RemoveInvisibles(doc);
        foreach (var paragraph in BodyDocument.Blocks(doc.Blocks).OfType<Paragraph>().ToList())
        {
            foreach (var run in BodyDocument.Inlines(paragraph.Inlines).OfType<Run>().ToList())
            {
                if (!IsDeletion(run)) continue;
                var marker = new Run(DelPrefix + Escape(run.Text) + "»");
                var parent = BodyDocument.ParentInlines(run)!;
                parent.InsertBefore(run, marker);
                parent.Remove(run);
                held.Deletions.Add((marker, run));
            }
        }
        return held;
    }

    public static void RestoreAfterSave(FlowDocument doc, Held held)
    {
        var used = new HashSet<Run>();
        foreach (var (marker, original) in held.Deletions)
        {
            var live = BodyDocument.ParentInlines(marker) is not null ? marker : FindRun(doc, marker.Text, used);
            if (live is null || BodyDocument.ParentInlines(live) is not { } parent) continue;
            used.Add(live);
            parent.InsertBefore(live, original);
            parent.Remove(live);
        }
        BodyPresentation.ShowMarks(doc, ShowInvisibles);
    }

    public static void Hydrate(FlowDocument doc)
    {
        foreach (var paragraph in BodyDocument.Blocks(doc.Blocks).OfType<Paragraph>().ToList())
        {
            foreach (var run in BodyDocument.Inlines(paragraph.Inlines).OfType<Run>().ToList())
            {
                var text = run.Text ?? "";
                if (!text.Contains(DelPrefix, StringComparison.Ordinal)) continue;
                var pieces = new List<Inline>();
                var rest = text;
                while (rest.Length > 0)
                {
                    var start = rest.IndexOf(DelPrefix, StringComparison.Ordinal);
                    if (start < 0) { pieces.Add(Copy(run, rest)); break; }
                    if (start > 0) pieces.Add(Copy(run, rest[..start]));
                    var end = rest.IndexOf('»', start + DelPrefix.Length);
                    if (end < 0) { pieces.Add(Copy(run, rest[start..])); break; }
                    pieces.Add(DeletionRun(Unescape(rest[(start + DelPrefix.Length)..end])));
                    rest = rest[(end + 1)..];
                }
                var parent = BodyDocument.ParentInlines(run)!;
                foreach (var piece in pieces) parent.InsertBefore(run, piece);
                parent.Remove(run);
            }
        }
    }

    public static void SetInvisibles(FlowDocument doc, bool on)
    {
        ShowInvisibles = on;
        BodyPresentation.ShowMarks(doc, on);
    }

    public static bool IsBusy => _depth > 0;

    private static int _depth;

    public static void MarkInserted(TextRange range)
    {
        _depth++;
        try
        {
            range.ApplyPropertyValue(TextElement.ForegroundProperty, InsertBrush);
            range.ApplyPropertyValue(Inline.TextDecorationsProperty, TextDecorations.Underline);
            TagRuns(range, InsTag);
        }
        finally { _depth--; }
    }

    public static void MarkDeleted(TextRange range)
    {
        _depth++;
        try
        {
            range.ApplyPropertyValue(TextElement.ForegroundProperty, DeleteBrush);
            range.ApplyPropertyValue(Inline.TextDecorationsProperty, TextDecorations.Strikethrough);
            TagRuns(range, DelTag);
        }
        finally { _depth--; }
    }

    public static void Accept(RichTextBox editor) => Resolve(editor, accept: true);
    public static void Reject(RichTextBox editor) => Resolve(editor, accept: false);

    private static void Resolve(RichTextBox editor, bool accept)
    {
        if (editor.IsReadOnly || editor.Selection.IsEmpty) return;
        _depth++;
        try
        {
        var selection = editor.Selection;
        var parts = RunsIn(selection).ToList().Select(run => new
        {
            Run = run,
            Start = Math.Max(0, run.ContentStart.GetOffsetToPosition(selection.Start.CompareTo(run.ContentStart) > 0 ? selection.Start : run.ContentStart)),
            End = run.ContentStart.GetOffsetToPosition(selection.End.CompareTo(run.ContentEnd) < 0 ? selection.End : run.ContentEnd),
            Delete = accept ? IsDeletion(run) : IsInsertion(run),
            Clear = accept ? IsInsertion(run) : IsDeletion(run),
        }).Where(p => p.End > p.Start && (p.Delete || p.Clear)).ToList();
        if (parts.Count == 0) return;
        editor.BeginChange();
        try
        {
            foreach (var part in parts.AsEnumerable().Reverse())
            {
                var run = part.Run;
                var text = run.Text;
                if (part.Delete) { run.Text = text[..part.Start] + text[part.End..]; continue; }
                var parent = BodyDocument.ParentInlines(run)!;
                var middle = Copy(run, text[part.Start..part.End]);
                var tail = Copy(run, text[part.End..]);
                ClearRun(middle);
                run.Text = text[..part.Start];
                parent.InsertAfter(run, middle);
                if (tail.Text.Length > 0) parent.InsertAfter(middle, tail);
            }
            RemoveEmpty(editor.Document);
        }
        finally { editor.EndChange(); }
        }
        finally { _depth--; }
    }

    public static bool IsDeletion(Run run) =>
        run.Tag as string == DelTag || (Has(run, TextDecorationLocation.Strikethrough) && SameColor(run, DeleteBrush));

    public static bool IsInsertion(Run run) =>
        run.Tag as string == InsTag || (Has(run, TextDecorationLocation.Underline) && SameColor(run, InsertBrush));

    public static string NotesPath(string workRoot, int chapter) =>
        Path.Combine(workRoot, ".novel-gm", "notes", chapter.ToString("0000") + ".json");

    public static List<Note> LoadNotes(string workRoot, int chapter)
    {
        var path = NotesPath(workRoot, chapter);
        if (!File.Exists(path)) return new List<Note>();
        try
        {
            return JsonSerializer.Deserialize<List<Note>>(File.ReadAllText(path)) ?? new List<Note>();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            var backup = path + ".bak";
            if (File.Exists(backup))
            {
                try { return JsonSerializer.Deserialize<List<Note>>(File.ReadAllText(backup)) ?? new(); }
                catch (Exception backupError) when (backupError is IOException or JsonException) { }
            }
            throw new InvalidDataException("批注文件无法读取，已保留原文件：" + path, ex);
        }
    }

    public static void SaveNotes(string workRoot, int chapter, IReadOnlyList<Note> notes)
    {
        var path = NotesPath(workRoot, chapter);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (File.Exists(path))
            {
                // Never overwrite an unreadable original with an empty in-memory list.
                JsonSerializer.Deserialize<List<Note>>(File.ReadAllText(path));
                File.Copy(path, path + ".bak", overwrite: true);
            }
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(JsonSerializer.SerializeToUtf8Bytes(notes, JsonOpts));
                output.Flush(true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static void Highlight(FlowDocument doc, IReadOnlyList<Note> notes)
    {
        BodyPresentation.SetNotes(doc, notes);
        BodyPresentation.ShowMarks(doc, ShowInvisibles);
    }
    public static void ClearComments(FlowDocument doc) => BodyPresentation.SetNotes(doc, Array.Empty<Note>());

    // Only clean up legacy in-document markers. New marks are drawn on an adorner.
    private static void RemoveInvisibles(FlowDocument doc)
    {
        foreach (var run in BodyDocument.Blocks(doc.Blocks).OfType<Paragraph>()
                     .SelectMany(p => BodyDocument.Inlines(p.Inlines)).OfType<Run>().ToList())
        {
            if (run.Tag as string == NpTag) BodyDocument.ParentInlines(run)?.Remove(run);
            else if (run.Tag as string == SpaceTag)
            {
                run.Tag = null;
                run.ClearValue(TextElement.BackgroundProperty);
            }
        }
    }

    private static void TagRuns(TextRange range, string tag)
    {
        foreach (var run in RunsIn(range)) run.Tag = tag;
    }

    private static void ClearRun(Run run)
    {
        run.Tag = null;
        run.ClearValue(TextElement.ForegroundProperty);
        run.ClearValue(Inline.TextDecorationsProperty);
    }

    private static void RemoveEmpty(FlowDocument doc)
    {
        foreach (var paragraph in BodyDocument.Blocks(doc.Blocks).OfType<Paragraph>().ToList())
            foreach (var run in BodyDocument.Inlines(paragraph.Inlines).OfType<Run>().Where(r => r.Text?.Length == 0).ToList())
                BodyDocument.ParentInlines(run)?.Remove(run);
    }

    private static IEnumerable<Run> RunsIn(TextRange range)
    {
        var seen = new HashSet<Run>();
        var pos = range.Start;
        while (pos is not null && pos.CompareTo(range.End) < 0)
        {
            if (pos.Parent is Run run && seen.Add(run)) yield return run;
            var next = pos.GetNextContextPosition(LogicalDirection.Forward);
            if (next is null || next.CompareTo(pos) == 0) break;
            pos = next;
        }
    }

    private static Run DeletionRun(string text) => new(text)
    {
        Tag = DelTag,
        Foreground = DeleteBrush,
        TextDecorations = TextDecorations.Strikethrough,
    };

    private static Run Copy(Run source, string text) => BodyDocument.CopyRun(source, text);

    private static bool Has(Run run, TextDecorationLocation location) =>
        run.TextDecorations?.Any(d => d.Location == location) == true;

    private static bool SameColor(Run run, SolidColorBrush brush) =>
        run.Foreground is SolidColorBrush solid && solid.Color == brush.Color;

    private static Run? FindRun(FlowDocument doc, string text, HashSet<Run> used)
    {
        foreach (var paragraph in BodyDocument.Blocks(doc.Blocks).OfType<Paragraph>())
            foreach (var run in BodyDocument.Inlines(paragraph.Inlines).OfType<Run>())
                if (!used.Contains(run) && run.Text == text) return run;
        return null;
    }

    private static TextPointer? FindQuote(FlowDocument doc, string quote)
    {
        var pos = doc.ContentStart;
        while (pos is not null && pos.CompareTo(doc.ContentEnd) < 0)
        {
            var run = pos.GetTextInRun(LogicalDirection.Forward);
            var at = run.IndexOf(quote, StringComparison.Ordinal);
            if (at >= 0) return pos.GetPositionAtOffset(at);
            var next = pos.GetNextContextPosition(LogicalDirection.Forward);
            if (next is null || next.CompareTo(pos) == 0) break;
            pos = next;
        }
        return null;
    }

    private static string Escape(string text) => (text ?? "").Replace("»", "");

    private static string Unescape(string text) => text;

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
