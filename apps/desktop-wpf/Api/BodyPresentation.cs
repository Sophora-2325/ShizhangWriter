using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace NovelGm.Desktop.Api;

/// <summary>Temporary authoring marks live outside the document and undo/save streams.</summary>
internal static class BodyPresentation
{
    private sealed class State
    {
        public bool Marks;
        public int LocateStart;
        public int LocateLength;
        public IReadOnlyList<BodyReview.Note> Notes = Array.Empty<BodyReview.Note>();
        public WeakReference<MarksAdorner>? View;
        public WeakReference<RichTextBox>? Editor;
        public List<(Rect Bounds, BodyReview.Note Note)> Cards = new();
    }
    private const double NoteGutter = 180;
    private static readonly ConditionalWeakTable<FlowDocument, State> States = new();

    public static void ShowMarks(FlowDocument document, bool value)
    {
        var state = States.GetOrCreateValue(document); state.Marks = value; Refresh(state);
    }
    public static void SetNotes(FlowDocument document, IReadOnlyList<BodyReview.Note> notes)
    {
        var state = States.GetOrCreateValue(document);
        state.Notes = notes ?? Array.Empty<BodyReview.Note>();
        Refresh(state);
    }

    /// <summary>审阅→正文定位的临时高亮。只画在 Adorner 上：不改正文格式、不写文件、不进撤销。</summary>
    public static void ShowLocate(FlowDocument document, int start, int length)
    {
        var state = States.GetOrCreateValue(document);
        state.LocateStart = Math.Max(0, start);
        state.LocateLength = Math.Max(0, length);
        Refresh(state);
    }

    public static void ClearLocate(FlowDocument document)
    {
        if (!States.TryGetValue(document, out var state) || state.LocateLength == 0) return;
        state.LocateStart = 0;
        state.LocateLength = 0;
        Refresh(state);
    }

    public static bool HasLocate(FlowDocument document) =>
        States.TryGetValue(document, out var state) && state.LocateLength > 0;

    /// <summary>定位提示随下一次点击或窗口失活消失；不拦截原点击，也不改变正文与正常选区。</summary>
    public static void DismissLocateOnInteraction(Window window, RichTextBox editor, Action? dismissed = null)
    {
        void Dismiss()
        {
            ClearLocate(editor.Document);
            dismissed?.Invoke();
        }
        // 在同步按钮的 Click 之前触发，因此同步产生的新高亮不会被同一次点击清除。
        window.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler((_, _) => Dismiss()), true);
        window.Deactivated += (_, _) => Dismiss();
        editor.SelectionChanged += (_, _) => { if (HasLocate(editor.Document)) Dismiss(); };
    }

    private static void Refresh(State state)
    {
        if (state.Editor?.TryGetTarget(out var editor) == true) ApplyGutter(editor, state);
        if (state.View?.TryGetTarget(out var view) == true) view.InvalidateVisual();
    }

    /// <summary>批注旁注画在控件留白里，不写进 FlowDocument，因此不会进存盘或撤销。</summary>
    private static void ApplyGutter(RichTextBox editor, State state)
    {
        var right = state.Notes.Count > 0 ? NoteGutter : 0;
        if (Math.Abs(editor.Padding.Right - right) < 0.1 && editor.Padding.Left == 0 && editor.Padding.Top == 0 && editor.Padding.Bottom == 0)
            return;
        editor.Padding = new Thickness(0, 0, right, 0);
    }

    public static void Attach(RichTextBox editor)
    {
        MarksAdorner? view = null;
        AdornerLayer? layer = null;
        FlowDocument? document = null;
        BodyReview.Note? hovered = null;
        void Ensure()
        {
            var currentLayer = AdornerLayer.GetAdornerLayer(editor);
            if (document != editor.Document || layer != currentLayer)
            {
                if (view is not null) layer?.Remove(view);
                document = editor.Document; layer = currentLayer;
                view = layer is null ? null : new MarksAdorner(editor, States.GetOrCreateValue(document)) { IsHitTestVisible = false };
                if (view is not null)
                {
                    layer!.Add(view);
                    var state = States.GetOrCreateValue(document);
                    state.View = new(view);
                    state.Editor = new(editor);
                    ApplyGutter(editor, state);
                }
                hovered = null; editor.ToolTip = null;
            }
            view?.InvalidateVisual();
        }
        editor.Loaded += (_, _) => Ensure();
        editor.TextChanged += (_, _) => Ensure();
        editor.SizeChanged += (_, _) => Ensure();
        editor.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => Ensure()));
        editor.PreviewMouseMove += (_, e) =>
        {
            if (!States.TryGetValue(editor.Document, out var state) || state.Notes.Count == 0)
            {
                if (hovered is not null) { hovered = null; editor.ToolTip = null; }
                return;
            }
            var note = HitNote(editor, state, e.GetPosition(editor));
            if (ReferenceEquals(note, hovered)) return;
            hovered = note;
            editor.ToolTip = note is null ? null : NoteTip(note);
        };
        editor.MouseLeave += (_, _) => { hovered = null; editor.ToolTip = null; };
        editor.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (!States.TryGetValue(editor.Document, out var state) || state.Notes.Count == 0) return;
            var note = HitNote(editor, state, e.GetPosition(editor));
            if (note is null || string.IsNullOrEmpty(note.Quote)) return;
            var map = new BodyTextMap(editor.Document);
            var at = ResolveNote(map, note);
            if (at is null) return;
            SelectNote(editor, map, at.Value, note.Quote.Length);
            e.Handled = true;
        };
        editor.Unloaded += (_, _) => { if (view is not null) layer?.Remove(view); view = null; layer = null; document = null; hovered = null; editor.ToolTip = null; };
        Ensure();
    }

    private static string NoteTip(BodyReview.Note note)
    {
        var quote = note.Quote.Replace("\r\n", "\n").Trim();
        var text = note.Text.Replace("\r\n", "\n").Trim();
        if (quote.Length == 0) return text;
        if (text.Length == 0) return quote;
        return quote + "\n" + text;
    }

    private static BodyReview.Note? HitNote(RichTextBox editor, State state, Point pos)
    {
        var map = new BodyTextMap(editor.Document);
        foreach (var note in state.Notes)
        {
            if (string.IsNullOrEmpty(note.Quote)) continue;
            var at = ResolveNote(map, note);
            if (at is null) continue;
            if (SpanContains(map, at.Value, note.Quote.Length, pos)) return note;
        }
        foreach (var card in state.Cards)
            if (card.Bounds.Contains(pos)) return card.Note;
        return null;
    }

    private static bool SpanContains(BodyTextMap map, int start, int length, Point pos)
    {
        foreach (var rect in SpanRects(map, start, start + length))
            if (rect.Contains(pos)) return true;
        return false;
    }

    private static void SelectNote(RichTextBox editor, BodyTextMap map, int start, int length)
    {
        var range = map.Range(start, length);
        editor.Focus();
        editor.Selection.Select(range.Start, range.End);
        ScrollLineToCenter(editor, range.Start);
    }

    /// <summary>把指针所在行滚到正文可视区的垂直中央。靠近文首/文末时停在滚动范围两端。</summary>
    internal static void ScrollLineToCenter(RichTextBox editor, TextPointer pointer)
    {
        editor.UpdateLayout();
        var rect = LineRect(pointer);
        if (rect.IsEmpty)
        {
            pointer.Paragraph?.BringIntoView();
            editor.UpdateLayout();
            rect = LineRect(pointer);
            if (rect.IsEmpty) return;
        }
        var viewport = editor.ViewportHeight > 1 ? editor.ViewportHeight : editor.ActualHeight;
        if (viewport <= 1) return;
        var offset = editor.VerticalOffset + rect.Top - viewport / 2 + rect.Height / 2;
        var max = Math.Max(0, editor.ExtentHeight - viewport);
        editor.ScrollToVerticalOffset(Math.Clamp(offset, 0, max));
    }

    private static Rect LineRect(TextPointer pointer)
    {
        var forward = pointer.GetCharacterRect(LogicalDirection.Forward);
        return forward.IsEmpty ? pointer.GetCharacterRect(LogicalDirection.Backward) : forward;
    }

    internal static int? ResolveNote(BodyTextMap map, BodyReview.Note note)
    {
        var hits = map.Matches(note.Quote);
        if (hits.Count == 0) return null;
        var target = note.Anchor?.Resolve(map.Text) ?? note.Start;
        return hits.MinBy(i => Math.Abs(i - target));
    }

    private static IEnumerable<Rect> SpanRects(BodyTextMap map, int start, int end)
    {
        start = Math.Clamp(start, 0, map.Text.Length);
        end = Math.Clamp(end, 0, map.Text.Length);
        if (end <= start) yield break;
        var p = map.Pointer(start);
        var stop = map.Pointer(end);
        while (p.CompareTo(stop) < 0)
        {
            var rect = p.GetCharacterRect(LogicalDirection.Forward);
            var lineEnd = p.GetLineStartPosition(1) ?? stop;
            if (lineEnd.CompareTo(p) <= 0 || lineEnd.CompareTo(stop) > 0) lineEnd = stop;
            var right = lineEnd.GetCharacterRect(LogicalDirection.Backward);
            if (!rect.IsEmpty && !right.IsEmpty)
                yield return new Rect(rect.Left, rect.Top, Math.Max(2, right.Right - rect.Left), Math.Max(1, right.Height));
            p = lineEnd;
        }
    }

    private sealed class MarksAdorner(RichTextBox editor, State state) : Adorner(editor)
    {
        private static readonly Brush LocateFill = Frozen(Color.FromArgb(70, 90, 160, 255));
        private static readonly Pen LocateEdge = FrozenPen(Color.FromArgb(180, 90, 160, 255), 1);
        private static readonly Brush NoteFill = Frozen(Color.FromArgb(80, 230, 160, 40));
        private static readonly Pen NoteEdge = FrozenPen(Color.FromArgb(200, 200, 110, 20), 1);
        private static readonly Pen NoteWave = FrozenPen(Color.FromArgb(230, 180, 90, 10), 1.2);
        private static readonly Brush CardFill = Frozen(Color.FromArgb(235, 255, 246, 220));
        private static readonly Pen CardEdge = FrozenPen(Color.FromArgb(180, 180, 120, 40), 1);
        private static readonly Brush CardInk = Frozen(Color.FromRgb(90, 50, 10));

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color); brush.Freeze(); return brush;
        }
        private static Pen FrozenPen(Color color, double thickness)
        {
            var pen = new Pen(Frozen(color), thickness); pen.Freeze(); return pen;
        }

        protected override void OnRender(DrawingContext dc)
        {
            var hasLocate = state.LocateLength > 0;
            var hasNotes = state.Notes.Count > 0;
            if (!state.Marks && !hasLocate && !hasNotes) return;
            var map = new BodyTextMap(editor.Document);
            var ink = editor.Foreground;
            if (hasNotes) DrawNotes(dc, map);
            else state.Cards.Clear();
            if (hasLocate) DrawLocate(dc, map);
            if (!state.Marks) return;
            for (var i = 0; i < map.Text.Length; i++)
            {
                var ch = map.Text[i];
                if (ch != ' ' && ch != '\n' && ch != '\t') continue;
                var rect = map.Pointer(i).GetCharacterRect(LogicalDirection.Forward);
                if (rect.IsEmpty || rect.Top < 0 || rect.Bottom > ActualHeight) continue;
                var glyph = ch == ' ' ? "·" : ch == '\t' ? "→" : "¶";
                var text = new FormattedText(glyph, System.Globalization.CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight, new Typeface(editor.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                    Math.Max(9, editor.FontSize * .7), ink, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.PushOpacity(.45); dc.DrawText(text, new Point(rect.Left, rect.Top)); dc.Pop();
            }
        }

        private void DrawNotes(DrawingContext dc, BodyTextMap map)
        {
            var anchors = new List<(BodyReview.Note Note, double LineTop, bool Visible)>();
            foreach (var note in state.Notes)
            {
                if (string.IsNullOrEmpty(note.Quote)) continue;
                var at = ResolveNote(map, note);
                if (at is null) continue;
                Rect? first = null;
                var visible = false;
                foreach (var span in SpanRects(map, at.Value, at.Value + note.Quote.Length))
                {
                    first ??= span;
                    if (span.Bottom < 0 || span.Top > ActualHeight) continue;
                    visible = true;
                    dc.DrawRectangle(NoteFill, NoteEdge, span);
                    DrawWave(dc, span);
                }
                if (first is { } line) anchors.Add((note, line.Top, visible));
            }
            var cards = new List<(Rect Bounds, BodyReview.Note Note)>();
            var floor = double.NegativeInfinity;
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var typeface = new Typeface(editor.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var left = Math.Max(8, ActualWidth - NoteGutter + 6);
            var width = Math.Max(48, ActualWidth - 16 - left);
            foreach (var anchor in anchors.OrderBy(a => a.LineTop))
            {
                if (!anchor.Visible) continue;
                var body = string.IsNullOrWhiteSpace(anchor.Note.Text) ? anchor.Note.Quote : anchor.Note.Text;
                body = body.Replace("\r\n", "\n").Trim();
                if (body.Length == 0) continue;
                var text = new FormattedText(body, System.Globalization.CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight, typeface, 12, CardInk, dpi)
                {
                    MaxTextWidth = Math.Max(24, width - 12),
                    MaxLineCount = 6,
                    Trimming = TextTrimming.CharacterEllipsis,
                };
                var top = anchor.Visible && anchor.LineTop < 4 ? 4 : anchor.LineTop;
                if (top < floor) top = floor;
                var bounds = new Rect(left, top, width, text.Height + 10);
                floor = bounds.Bottom + 6;
                if (bounds.Bottom < 0 || bounds.Top > ActualHeight) continue;
                dc.DrawRoundedRectangle(CardFill, CardEdge, bounds, 4, 4);
                dc.DrawText(text, new Point(bounds.Left + 6, bounds.Top + 4));
                cards.Add((bounds, anchor.Note));
            }
            state.Cards = cards;
        }

        private static void DrawWave(DrawingContext dc, Rect span)
        {
            var y = span.Bottom - 1.5;
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(span.Left, y), false, false);
                var up = true;
                for (var x = span.Left + 3; x <= span.Right + 0.1; x += 3)
                {
                    ctx.LineTo(new Point(x, y + (up ? -2 : 2)), true, false);
                    up = !up;
                }
            }
            geometry.Freeze();
            dc.DrawGeometry(null, NoteWave, geometry);
        }

        /// <summary>半透明色块 + 边线，按视觉行逐段画在正文之上；文档本身不动。</summary>
        private void DrawLocate(DrawingContext dc, BodyTextMap map)
        {
            foreach (var span in SpanRects(map, state.LocateStart, state.LocateStart + state.LocateLength))
            {
                if (span.Bottom < 0 || span.Top > ActualHeight) continue;
                dc.DrawRectangle(LocateFill, LocateEdge, span);
            }
        }
    }
}
