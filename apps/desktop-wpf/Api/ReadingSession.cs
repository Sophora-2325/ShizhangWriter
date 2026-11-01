using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace NovelGm.Desktop.Api;

public sealed record ReadingAnchor(int Offset, string Before, string After, string Revision)
{
    public static string Normalize(string text) => text.Replace("\r\n", "\n").TrimEnd('\n');
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(text))));

    public static ReadingAnchor Capture(string text, int offset)
    {
        text = Normalize(text);
        offset = Math.Clamp(offset, 0, text.Length);
        return new(offset, text[Math.Max(0, offset - 48)..offset],
            text[offset..Math.Min(text.Length, offset + 64)], Hash(text));
    }

    public int Resolve(string text)
    {
        text = Normalize(text);
        var fallback = Math.Clamp(Offset, 0, text.Length);
        if (Hash(text) == Revision) return fallback;
        var needle = Before + After;
        var best = -1;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); needle.Length > 0 && i >= 0;
             i = text.IndexOf(needle, i + 1, StringComparison.Ordinal))
        {
            var candidate = i + Before.Length;
            if (best < 0 || Math.Abs(candidate - fallback) < Math.Abs(best - fallback)) best = candidate;
        }
        if (best >= 0) return best;
        // The surrounding sentence may have been edited; use either surviving side.
        foreach (var fragment in new[] { After, Before })
        {
            if (fragment.Length < 8) continue;
            for (var i = text.IndexOf(fragment, StringComparison.Ordinal); i >= 0;
                 i = text.IndexOf(fragment, i + 1, StringComparison.Ordinal))
            {
                var candidate = i + (fragment == Before ? fragment.Length : 0);
                if (best < 0 || Math.Abs(candidate - fallback) < Math.Abs(best - fallback)) best = candidate;
            }
            if (best >= 0) return best;
        }
        // A completely replaced or renumbered chapter must not inherit a random old offset.
        return 0;
    }
}

public sealed class ReadingPreferences
{
    public double FontSize { get; set; } = 26;
    public double LineSpacing { get; set; } = 1;
    public int Characters { get; set; } = 32;
    public int Version { get; set; } = 2;
    public string FontFamily { get; set; } = "";
    public TextAlignment TitleAlignment { get; set; } = TextAlignment.Center;
    public double ParagraphSpacing { get; set; } = 1;
    public double PageMargin { get; set; } = 16;
    public double Width { get; set; } = 360;
    public double Height { get; set; } = 648;
    public string TurnEffect { get; set; } = "scroll";
    public bool FirstLineIndent { get; set; } = true;
    public string Voice { get; set; } = "";
    public int SpeechRate { get; set; }
    public int SpeechVolume { get; set; } = 100;
    public bool AutoNext { get; set; }

    public void Normalize()
    {
        FontSize = double.IsFinite(FontSize) ? Math.Clamp(FontSize, 12, 40) : 26;
        LineSpacing = double.IsFinite(LineSpacing) ? Math.Clamp(LineSpacing, .6, 2.4) : 1;
        Characters = Math.Clamp(Characters, 28, 38);
        ParagraphSpacing = double.IsFinite(ParagraphSpacing) ? Math.Clamp(ParagraphSpacing, 0, 3) : 1;
        PageMargin = double.IsFinite(PageMargin) ? Math.Clamp(PageMargin, 0, 48) : 16;
        Width = double.IsFinite(Width) ? Math.Clamp(Width, 240, 600) : 360;
        Height = double.IsFinite(Height) ? Math.Clamp(Height, 360, 1200) : 648;
        if (TurnEffect is not ("scroll" or "cover" or "none")) TurnEffect = "scroll";
        if (!Enum.IsDefined(TitleAlignment)) TitleAlignment = TextAlignment.Center;
        SpeechRate = Math.Clamp(SpeechRate, -10, 10);
        SpeechVolume = Math.Clamp(SpeechVolume, 0, 100);
    }
}

public sealed class ReadingPositionStore
{
    public sealed class State
    {
        public ReadingPreferences Preferences { get; set; } = new();
        public Dictionary<string, ReadingAnchor> Positions { get; set; } = new();
    }

    private readonly string _path;
    public State Data { get; private set; } = new();
    public ReadingPositionStore(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "novel-gm", "reading.json");
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                Data = JsonSerializer.Deserialize<State>(json) ?? new();
                Data.Preferences ??= new();
                using var parsed = JsonDocument.Parse(json);
                if (parsed.RootElement.TryGetProperty("Preferences", out var old) && old.ValueKind == JsonValueKind.Object && !old.TryGetProperty("Version", out _))
                    Data.Preferences.LineSpacing /= 1.3; // old value was an absolute font-size multiplier
            }
            Data.Preferences ??= new();
            Data.Positions ??= new();
            Data.Preferences.Normalize();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            Data = new();
        }
    }

    public ReadingAnchor? Find(string key) => Data.Positions.GetValueOrDefault(key);
    public void Remember(string key, ReadingAnchor anchor)
    {
        Data.Positions.Remove(key);
        Data.Positions[key] = anchor;
        while (Data.Positions.Count > 500) Data.Positions.Remove(Data.Positions.Keys.First());
    }

    public bool Save()
    {
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(temp, JsonSerializer.Serialize(Data));
            File.Move(temp, _path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        finally
        {
            try { File.Delete(temp); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}

public static class ReadingText
{
    public static List<int> FindMatches(string text, string query)
    {
        var matches = new List<int>();
        if (string.IsNullOrEmpty(query)) return matches;
        for (var offset = text.IndexOf(query, StringComparison.OrdinalIgnoreCase); offset >= 0;
             offset = text.IndexOf(query, offset + query.Length, StringComparison.OrdinalIgnoreCase))
            matches.Add(offset);
        return matches;
    }

    /// <summary>全部匹配的浅黄底。深色正文上也能看清。</summary>
    public static readonly Brush FindMarkBrush = Frozen(Color.FromRgb(255, 243, 176));

    /// <summary>当前这一处的深黄底。</summary>
    public static readonly Brush FindCurrentBrush = Frozen(Color.FromRgb(245, 197, 24));

    private static readonly Brush FindInkBrush = Frozen(Color.FromRgb(26, 26, 26));

    /// <summary>在阅读快照上标出每一处匹配。从后往前写，避免前面的拆段弄偏后面的位置。</summary>
    public static List<TextRange> MarkMatches(TextPointer start, TextPointer end, IReadOnlyList<int> matches, int current, int length)
    {
        var spans = new List<(TextPointer Start, TextPointer End, bool Current)>(matches.Count);
        for (var i = 0; i < matches.Count; i++)
        {
            if (length <= 0) break;
            spans.Add((Pointer(start, end, matches[i]), Pointer(start, end, matches[i] + length), i == current));
        }
        var ranges = new List<TextRange>(spans.Count);
        for (var i = spans.Count - 1; i >= 0; i--)
        {
            var range = new TextRange(spans[i].Start, spans[i].End);
            if (range.IsEmpty) continue;
            range.ApplyPropertyValue(TextElement.BackgroundProperty, spans[i].Current ? FindCurrentBrush : FindMarkBrush);
            range.ApplyPropertyValue(TextElement.ForegroundProperty, FindInkBrush);
            ranges.Add(range);
        }
        return ranges;
    }

    public static void ClearMarks(FlowDocument document)
    {
        var mark = ((SolidColorBrush)FindMarkBrush).Color;
        var current = ((SolidColorBrush)FindCurrentBrush).Color;
        for (var pointer = document.ContentStart; pointer is not null && pointer.CompareTo(document.ContentEnd) < 0;
             pointer = pointer.GetNextContextPosition(LogicalDirection.Forward))
        {
            if (pointer.GetPointerContext(LogicalDirection.Forward) != TextPointerContext.ElementStart
                || pointer.GetAdjacentElement(LogicalDirection.Forward) is not TextElement element) continue;
            if (element.ReadLocalValue(TextElement.BackgroundProperty) is not SolidColorBrush brush) continue;
            if (brush.Color != mark && brush.Color != current) continue;
            element.ClearValue(TextElement.BackgroundProperty);
            element.ClearValue(TextElement.ForegroundProperty);
        }
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public static int Offset(TextPointer start, TextPointer point) =>
        new TextRange(start, point).Text.Replace("\r\n", "\n").Length;

    public static TextPointer Pointer(TextPointer start, TextPointer end, int offset)
    {
        var low = 0;
        var high = start.GetOffsetToPosition(end);
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            var point = start.GetPositionAtOffset(mid, LogicalDirection.Forward)!;
            if (Offset(start, point) < offset) low = mid + 1;
            else high = mid;
        }
        return start.GetPositionAtOffset(low, LogicalDirection.Forward)!.GetInsertionPosition(LogicalDirection.Forward);
    }

    public static ReadingAnchor Capture(FlowDocument document, TextPointer point) =>
        ReadingAnchor.Capture(RichBody.PlainText(document), Offset(document.ContentStart, point));

    public static void Scale(FlowDocument document, ReadingPreferences preferences)
    {
        preferences.Normalize();
        var ratio = preferences.FontSize / 17;
        document.FontSize = preferences.FontSize;
        if (document.ReadLocalValue(Block.TextAlignmentProperty) == DependencyProperty.UnsetValue)
            document.TextAlignment = TextAlignment.Left;
        // Snapshot only: author-supplied relative sizes and emphasis survive.
        for (var pointer = document.ContentStart; pointer is not null && pointer.CompareTo(document.ContentEnd) < 0;
             pointer = pointer.GetNextContextPosition(LogicalDirection.Forward))
        {
            if (pointer.GetPointerContext(LogicalDirection.Forward) != TextPointerContext.ElementStart
                || pointer.GetAdjacentElement(LogicalDirection.Forward) is not TextElement element) continue;
            if (element.ReadLocalValue(TextElement.FontSizeProperty) is double size)
                element.FontSize = size * ratio;
            element.ClearValue(TextElement.FontFamilyProperty);
            if (element is Paragraph paragraph)
            {
                paragraph.LineHeight = paragraph.FontSize * 1.3 * preferences.LineSpacing;
                var explicitIndent = paragraph.ReadLocalValue(Paragraph.TextIndentProperty);
                if (!preferences.FirstLineIndent) paragraph.TextIndent = 0;
                else if (explicitIndent is double indent) paragraph.TextIndent = indent * ratio;
                else if (paragraph.TextAlignment is not TextAlignment.Center and not TextAlignment.Right)
                    paragraph.TextIndent = preferences.FontSize * 2;
                // Keep source alignment and intentional paragraph spacing, including captions.
                var margin = paragraph.Margin;
                paragraph.Margin = new Thickness(margin.Left, margin.Top, margin.Right, preferences.FontSize * 1.3 * preferences.ParagraphSpacing);
            }
        }
        document.FontSize = preferences.FontSize;
    }
}
