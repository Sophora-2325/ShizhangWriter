using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Models;

namespace NovelGm.Desktop.Controls;

/// <summary>A single shared progress band, with collision-packed labels above and below.</summary>
internal sealed class ConstraintOverview : Canvas
{
    private readonly StackedConstraintSession _session;
    private readonly Dictionary<string, Button> _labels = new();
    internal ConstraintOverview(StackedConstraintSession session)
    {
        _session = session; MinHeight = 250; ClipToBounds = true;
        Background = Brushes.Transparent;
        SizeChanged += (_, e) => { if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 0.5) Render(); };
        MouseLeftButtonDown += (_, e) => { if (e.OriginalSource == this) _session.Select(null); };
    }
    private Brush Resource(string key) => (Brush)FindResource(key);
    private sealed record Label(ConstraintTrackDto Track, double X, double Width, int Row, bool Above, double Anchor);
    internal void Render()
    {
        // Keep live label controls in the tree: selection, autosave and drag refreshes
        // must not discard keyboard focus. Everything else is a display-only layer.
        var validLabels = _session.Data.Tracks.Where(t => t.Segments.Count > 0).Select(t => t.Id).ToHashSet();
        foreach (var child in Children.Cast<UIElement>().ToList())
            if (child is not Button button || button.Tag is not string id || !validLabels.Contains(id)) Children.Remove(child);
        foreach (var id in _labels.Keys.Where(id => !validLabels.Contains(id)).ToList()) _labels.Remove(id);
        var width = Math.Max(1, ActualWidth - 24); const double left = 12, pitch = 50, bandHeight = 84;
        var labels = new List<Label>(); var topRows = new List<List<(double, double)>>(); var bottomRows = new List<List<(double, double)>>();
        var ordered = _session.Data.Tracks.OrderBy(t => t.Domain == "thread" ? 0 : t.Domain == "character" ? 1 : 2).ToList();
        foreach (var track in ordered.Where(t => t.Segments.Count > 0))
        {
            var text = _session.DisplayName(track);
            var measured = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI, Microsoft YaHei UI"), 12, Resource("TextBrush"), VisualTreeHelper.GetDpi(this).PixelsPerDip);
            var labelWidth = Math.Min(width, Math.Clamp(measured.Width + 24, 76, 156));
            var first = track.Segments.MinBy(s => s.Start)!;
            var anchor = (first.Start + first.End) / 200 * width;
            var x = Math.Clamp(anchor - labelWidth / 2, 0, Math.Max(0, width - labelWidth));
            var above = track.Domain == "scene" || (track.Domain == "character" && labels.Count % 2 == 0);
            var rows = above ? topRows : bottomRows; var row = 0;
            while (row < rows.Count && rows[row].Any(r => x < r.Item2 + 6 && x + labelWidth + 6 > r.Item1)) row++;
            if (row == rows.Count) rows.Add(new());
            rows[row].Add((x, x + labelWidth)); labels.Add(new(track, x + left, labelWidth, row, above, anchor + left));
        }
        var bandY = Math.Max(1, topRows.Count) * pitch + 35;
        Height = bandY + bandHeight + Math.Max(1, bottomRows.Count) * pitch + 35;
        var band = new Border { Width = width, Height = bandHeight, BorderThickness = new Thickness(1) };
        band.SetResourceReference(Border.BackgroundProperty, "Bg3Brush"); band.SetResourceReference(Border.BorderBrushProperty, "BorderBrushKey");
        Place(band, left, bandY);
        for (var tick = 0; tick <= 100; tick += 25)
        {
            var x = left + width * tick / 100;
            Place(new Line { X1 = 0, X2 = 0, Y1 = 0, Y2 = bandHeight, Stroke = Resource("BorderBrushKey"), StrokeThickness = 0.5, IsHitTestVisible = false }, x, bandY);
            var text = new TextBlock { Text = tick + "%", FontSize = 11, Foreground = Resource("MutedBrush") };
            Place(text, Math.Clamp(x - 10, left, Math.Max(left, left + width - 32)), bandY - 20);
        }
        foreach (var track in ordered.OrderBy(t => t.Id == _session.SelectedTrackId ? 1 : 0))
        foreach (var segment in track.Segments.OrderBy(s => s.Id == _session.SelectedSegmentId ? 1 : 0))
        {
            var selected = track.Id == _session.SelectedTrackId && (_session.SelectedSegmentId is null || segment.Id == _session.SelectedSegmentId);
            var rectangle = new Border
            {
                Width = Math.Max(1, width * (segment.End - segment.Start) / 100), Height = bandHeight - 12,
                Background = ConstraintLane.ColorBrush(track.Id, selected ? (byte)105 : (byte)45),
                BorderBrush = selected ? Resource("AccentBrush") : ConstraintLane.ColorBrush(track.Id),
                BorderThickness = new Thickness(selected ? 2 : 1), Cursor = Cursors.Hand,
                ToolTip = $"{_session.DisplayName(track)} · {segment.Start:0.#}–{segment.End:0.#}%\n{segment.Directive}",
            };
            rectangle.MouseLeftButtonDown += (_, e) => { _session.Select(track.Id, segment.Id, true); e.Handled = true; };
            Place(rectangle, left + width * segment.Start / 100, bandY + 6);
        }
        foreach (var label in labels)
        {
            var y = label.Above ? bandY - 30 - (label.Row + 1) * pitch : bandY + bandHeight + 12 + label.Row * pitch;
            var endpoint = label.Above ? y + 40 : y;
            Place(new Line { X1 = label.X + label.Width / 2, X2 = label.Anchor, Y1 = endpoint, Y2 = label.Above ? bandY : bandY + bandHeight,
                Stroke = ConstraintLane.ColorBrush(label.Track.Id), StrokeThickness = 1, IsHitTestVisible = false }, 0, 0);
            if (!_labels.TryGetValue(label.Track.Id, out var button))
            {
                var id = label.Track.Id;
                button = new Button { Height = 40, Padding = new Thickness(6, 2, 6, 2), Cursor = Cursors.Hand, Tag = id };
                button.SetResourceReference(Control.BackgroundProperty, "BgBrush"); button.SetResourceReference(Control.ForegroundProperty, "TextBrush");
                button.Click += (_, _) => _session.Select(id, reveal: true);
                _labels[id] = button; Children.Add(button);
            }
            button.Width = label.Width; button.ToolTip = _session.DisplayName(label.Track);
            button.BorderThickness = new Thickness(label.Track.Id == _session.SelectedTrackId ? 2 : 1);
            button.BorderBrush = label.Track.Id == _session.SelectedTrackId ? Resource("AccentBrush") : ConstraintLane.ColorBrush(label.Track.Id);
            button.Content = new TextBlock { Text = _session.DisplayName(label.Track), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 34 };
            // Every label is above every leader, even when label controls are reused.
            SetZIndex(button, 100);
            System.Windows.Automation.AutomationProperties.SetName(button, _session.DisplayName(label.Track));
            SetLeft(button, label.X); SetTop(button, y);
        }
        if (labels.Count == 0)
            Place(new TextBlock { Text = L.T("Stack.OverviewEmpty"), TextWrapping = TextWrapping.Wrap, Width = Math.Max(1, width - 24),
                Foreground = Resource("MutedBrush"), FontSize = 13 }, 24, bandY + 20);
    }
    private void Place(UIElement element, double x, double y) { SetLeft(element, x); SetTop(element, y); Children.Add(element); }
}
