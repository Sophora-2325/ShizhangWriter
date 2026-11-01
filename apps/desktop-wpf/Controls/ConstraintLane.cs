using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Models;

namespace NovelGm.Desktop.Controls;

internal sealed class ConstraintLane : FrameworkElement
{
    internal StackedConstraintSession Session { get; }
    internal string TrackId { get; }
    private ConstraintTrackDto? Track => Session.Data.Tracks.FirstOrDefault(t => t.Id == TrackId);
    private Point _origin;
    private ConstraintSegmentDto? _before, _preview;
    private int _edges; // 1 left, 2 right, 4 top, 8 bottom; 0 move; 16 draw
    private bool _dragging;
    internal ConstraintLane(StackedConstraintSession session, string trackId)
    {
        Session = session; TrackId = trackId; Focusable = true; Height = 112; MinWidth = 100;
        Cursor = Cursors.Cross; ClipToBounds = true;
        ToolTip = L.T("Stack.DrawHint");
        System.Windows.Automation.AutomationProperties.SetName(this, L.T("Stack.Canvas"));
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
    internal static Color TrackColor(string id)
    {
        Color[] colors = { Color.FromRgb(82, 111, 190), Color.FromRgb(166, 94, 132), Color.FromRgb(47, 134, 126),
            Color.FromRgb(164, 113, 49), Color.FromRgb(126, 98, 180), Color.FromRgb(68, 131, 164), Color.FromRgb(155, 103, 66) };
        uint hash = 2166136261;
        foreach (var c in id) hash = (hash ^ c) * 16777619;
        return colors[hash % colors.Length];
    }
    internal static Brush ColorBrush(string id, byte alpha = 255)
    {
        var color = TrackColor(id); color.A = alpha; var brush = new SolidColorBrush(color); brush.Freeze(); return brush;
    }
    private Brush Resource(string key) => (Brush)FindResource(key);
    private Rect Box(ConstraintSegmentDto s) => new(s.Start * ActualWidth / 100, s.Top * ActualHeight / 100,
        (s.End - s.Start) * ActualWidth / 100, (s.Bottom - s.Top) * ActualHeight / 100);
    private Point Percent(Point p) => new(Math.Clamp(p.X / Math.Max(1, ActualWidth) * 100, 0, 100),
        Math.Clamp(p.Y / Math.Max(1, ActualHeight) * 100, 0, 100));

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Resource("Bg3Brush"), new Pen(Resource(IsKeyboardFocused ? "AccentBrush" : "BorderBrushKey"), 1), new Rect(RenderSize));
        for (var tick = 25; tick < 100; tick += 25)
            dc.DrawLine(new Pen(Resource("BorderBrushKey"), 0.5), new Point(ActualWidth * tick / 100, 0), new Point(ActualWidth * tick / 100, ActualHeight));
        if (Track is not { } track) return;
        var segments = track.Segments.OrderBy(s => s.Id == Session.SelectedSegmentId ? 1 : 0).ToList();
        foreach (var segment in segments)
            DrawSegment(dc, track, _preview?.Id == segment.Id ? _preview : segment);
        if (_preview is not null && !track.Segments.Any(s => s.Id == _preview.Id)) DrawSegment(dc, track, _preview);
        if (segments.Count == 0 && _preview is null)
            DrawText(dc, L.T("Stack.DrawHint"), new Rect(12, 40, Math.Max(1, ActualWidth - 24), 35), Resource("MutedBrush"), 12);
    }
    private void DrawSegment(DrawingContext dc, ConstraintTrackDto track, ConstraintSegmentDto segment)
    {
        var box = Box(segment); if (box.Width <= 0 || box.Height <= 0) return;
        var selected = Session.SelectedTrackId == TrackId && Session.SelectedSegmentId == segment.Id;
        dc.DrawRectangle(ColorBrush(track.Id, selected ? (byte)75 : (byte)38),
            new Pen(selected ? Resource("AccentBrush") : ColorBrush(track.Id), selected ? 2 : 1), box);
        if (box.Width > 42 && box.Height > 18)
        {
            dc.PushClip(new RectangleGeometry(box));
            DrawText(dc, string.IsNullOrWhiteSpace(segment.Directive) ? $"{segment.Start:0.#}–{segment.End:0.#}%" : segment.Directive,
                new Rect(box.X + 6, box.Y + 5, Math.Max(1, box.Width - 12), Math.Max(1, box.Height - 10)), Resource("TextBrush"), 12);
            dc.Pop();
        }
        if (selected && !Session.ReadOnly)
            foreach (var p in new[] { box.TopLeft, box.TopRight, box.BottomLeft, box.BottomRight })
                dc.DrawRectangle(Resource("BgBrush"), new Pen(Resource("AccentBrush"), 1), new Rect(p.X - 3, p.Y - 3, 6, 6));
    }
    private void DrawText(DrawingContext dc, string text, Rect box, Brush color, double size)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI, Microsoft YaHei UI"), size, color, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        { MaxTextWidth = box.Width, MaxTextHeight = box.Height, Trimming = TextTrimming.CharacterEllipsis };
        dc.DrawText(formatted, box.TopLeft);
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e); Focus();
        if (Track is not { } track) return;
        var p = e.GetPosition(this);
        var hit = track.Segments.OrderBy(s => s.Id == Session.SelectedSegmentId ? 1 : 0).Reverse().FirstOrDefault(s => Box(s).Contains(p));
        Session.Select(TrackId, hit?.Id);
        if (Session.ReadOnly) { e.Handled = true; return; }
        _origin = Percent(p); _before = hit?.Copy(); _edges = hit is null ? 16 : 0;
        if (hit is not null)
        {
            var box = Box(hit);
            if (Math.Abs(p.X - box.Left) < 7) _edges |= 1;
            else if (Math.Abs(p.X - box.Right) < 7) _edges |= 2;
            if (Math.Abs(p.Y - box.Top) < 7) _edges |= 4;
            else if (Math.Abs(p.Y - box.Bottom) < 7) _edges |= 8;
        }
        _preview = hit?.Copy() ?? new ConstraintSegmentDto { Start = _origin.X, End = _origin.X, Top = _origin.Y, Bottom = _origin.Y };
        _dragging = CaptureMouse(); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging || _preview is null) return;
        if (Session.ReadOnly) { CancelGesture(); return; }
        var p = Percent(e.GetPosition(this));
        if (_edges == 16)
        {
            _preview.Start = Math.Min(_origin.X, p.X); _preview.End = Math.Max(_origin.X, p.X);
            _preview.Top = Math.Min(_origin.Y, p.Y); _preview.Bottom = Math.Max(_origin.Y, p.Y);
        }
        else if (_before is { } s)
        {
            if (_edges == 0)
            {
                _preview = Translate(s, p.X - _origin.X, p.Y - _origin.Y);
            }
            else
            {
                var minWidth = Math.Min(0.5, (s.End - s.Start) / 2);
                var minHeight = Math.Min(2, (s.Bottom - s.Top) / 2);
                if ((_edges & 1) != 0) _preview.Start = Math.Clamp(p.X, 0, s.End - minWidth);
                if ((_edges & 2) != 0) _preview.End = Math.Clamp(p.X, s.Start + minWidth, 100);
                if ((_edges & 4) != 0) _preview.Top = Math.Clamp(p.Y, 0, s.Bottom - minHeight);
                if ((_edges & 8) != 0) _preview.Bottom = Math.Clamp(p.Y, s.Top + minHeight, 100);
            }
        }
        InvalidateVisual();
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging || _preview is null || Track is not { } track) return;
        var next = _preview.Copy(); var isNew = _edges == 16;
        CancelGesture();
        if (!Session.ReadOnly && next.End - next.Start >= (isNew ? 0.5 : 0.00001) && next.Bottom - next.Top >= (isNew ? 2 : 0.00001))
        {
            Session.Edit(_ =>
            {
                if (isNew) track.Segments.Add(next);
                else if (track.Segments.FirstOrDefault(s => s.Id == next.Id) is { } existing)
                { existing.Start = next.Start; existing.End = next.End; existing.Top = next.Top; existing.Bottom = next.Bottom; }
            }, isNew);
            Session.Select(TrackId, next.Id);
        }
        e.Handled = true;
    }
    private void CancelGesture() { _dragging = false; _preview = null; _before = null; if (IsMouseCaptured) ReleaseMouseCapture(); InvalidateVisual(); }
    internal static ConstraintSegmentDto Translate(ConstraintSegmentDto source, double x, double y)
    {
        var next = source.Copy(); var dx = Math.Clamp(x, -source.Start, 100 - source.End); var dy = Math.Clamp(y, -source.Top, 100 - source.Bottom);
        next.Start += dx; next.End += dx; next.Top += dy; next.Bottom += dy; return next;
    }
    protected override void OnLostMouseCapture(MouseEventArgs e) { base.OnLostMouseCapture(e); _dragging = false; _preview = null; InvalidateVisual(); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { CancelGesture(); Session.Select(null); e.Handled = true; }
        else if (!Session.ReadOnly && Session.SelectedTrackId == TrackId && Session.SelectedSegment is { } segment && e.Key is Key.Left or Key.Right)
        {
            var dx = Math.Clamp(e.Key == Key.Left ? -1 : 1, -segment.Start, 100 - segment.End);
            Session.Edit(_ => { segment.Start += dx; segment.End += dx; }, coalesceKey: "move:" + segment.Id);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }
}
