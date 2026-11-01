using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Controls;
using NovelGm.Desktop.Models;

namespace NovelGm.Desktop;

public partial class StackedConstraintsWindow : Window
{
    internal StackedConstraintSession Session { get; }
    internal string? Domain { get; }
    internal event Action<string>? DomainRequested;
    private readonly ConstraintOverview _overview;
    private readonly Dictionary<string, FrameworkElement> _rows = new();
    private readonly List<Action> _refresh = new();
    private readonly List<Action> _editorRefresh = new();
    private readonly Dictionary<string, ContentControl> _inspectors = new();
    private bool _syncing, _closing, _closeAllowed;
    private string? _shownSelection;
    internal StackedConstraintsWindow(StackedConstraintSession session, string? domain = null)
    {
        Session = session; Domain = domain; InitializeComponent();
        _overview = new ConstraintOverview(session); OverviewHost.Content = _overview;
        if (domain is not null)
        {
            Width = 920; Height = 720; MinWidth = 640;
            OverviewPanel.Visibility = EditorSplitter.Visibility = Visibility.Collapsed;
            OverviewColumn.Width = SplitColumn.Width = new GridLength(0); EditorColumn.Width = new GridLength(1, GridUnitType.Star);
        }
        Session.Changed += SessionChanged;
        LanguageManager.LanguageChanged += LanguageChanged;
        ThemeManager.ThemeChanged += ThemeChanged;
        Closed += (_, _) =>
        {
            Session.Changed -= SessionChanged; LanguageManager.LanguageChanged -= LanguageChanged;
            ThemeManager.ThemeChanged -= ThemeChanged;
        };
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea; MaxWidth = area.Width; MaxHeight = area.Height;
            MinWidth = Math.Min(MinWidth, area.Width); MinHeight = Math.Min(MinHeight, area.Height);
            Width = Math.Min(Width, area.Width); Height = Math.Min(Height, area.Height); RenderRows();
        };
    }
    internal void RefreshView() => RenderRows();
    private void LanguageChanged(object? sender, EventArgs e) => RenderRows();
    private void ThemeChanged(object? sender, EventArgs e) { RefreshValues(); _overview.Render(); }
    internal static string DomainKey(string domain) => "Stack." + domain;
    private void SessionChanged(StackedChange kind)
    {
        if (kind == StackedChange.Structure) RenderRows();
        else
        {
            if (kind == StackedChange.Selection && _shownSelection != Session.SelectedSegmentId)
            {
                _shownSelection = Session.SelectedSegmentId; _editorRefresh.Clear();
                foreach (var pair in _inspectors)
                    pair.Value.Content = Session.SelectedTrack is { } track && pair.Key == track.Id && Session.SelectedSegment is { } segment
                        ? SegmentEditor(track, segment) : null;
            }
            RefreshValues();
        }
        _overview.Render();
        if (kind == StackedChange.Selection && Session.RevealSelection && Session.SelectedTrackId is { } id && _rows.TryGetValue(id, out var row)) row.BringIntoView();
    }
    private Brush Resource(string key) => (Brush)FindResource(key);
    private static Button ActionButton(string text, RoutedEventHandler action)
    {
        var button = new Button { Content = text, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 6, 0), MinHeight = 30, Cursor = Cursors.Hand };
        button.Click += action; return button;
    }
    private static TextBlock Label(string text, double size = 13) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
    private void RenderRows()
    {
        _syncing = true;
        try
        {
            _shownSelection = Session.SelectedSegmentId;
            var title = Domain is null ? L.T("Stack.Title") : L.T("Stack.DomainWindow", L.T(DomainKey(Domain)));
            Heading.Text = title; Title = $"{title} · {Session.ChapterNo} · {Session.ChapterTitle}";
            DomainsPanel.Children.Clear(); _rows.Clear(); _refresh.Clear(); _editorRefresh.Clear(); _inspectors.Clear();
            foreach (var domain in new[] { "thread", "character", "scene" }.Where(d => Domain is null || Domain == d))
            {
                var group = new DockPanel { Margin = new Thickness(0, 0, 0, 24) };
                var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
                if (Domain is null)
                {
                    var pop = ActionButton("", (_, _) => DomainRequested?.Invoke(domain));
                    pop.Content = new System.Windows.Shapes.Line { X1 = 0, X2 = 22, Y1 = 4, Y2 = 4, Stroke = Resource("TextBrush"), StrokeThickness = 2, Width = 24, Height = 8 };
                    pop.ToolTip = L.T("Stack.OpenDomain", L.T(DomainKey(domain)));
                    AutomationProperties.SetName(pop, pop.ToolTip.ToString()); DockPanel.SetDock(pop, Dock.Right); header.Children.Add(pop);
                }
                header.Children.Add(new TextBlock { Text = L.T(DomainKey(domain)), FontSize = 18, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
                DockPanel.SetDock(header, Dock.Top); group.Children.Add(header);
                var add = ActionButton(L.T("Stack.Add." + domain), (_, _) => AddElements(domain));
                add.HorizontalAlignment = HorizontalAlignment.Stretch; add.Margin = new Thickness(0, 10, 0, 0);
                _refresh.Add(() => add.IsEnabled = !Session.ReadOnly);
                DockPanel.SetDock(add, Dock.Bottom); group.Children.Add(add);
                var tracks = new StackPanel();
                foreach (var track in Session.Data.Tracks.Where(t => t.Domain == domain)) tracks.Children.Add(TrackRow(track));
                if (tracks.Children.Count == 0)
                {
                    var empty = Label(L.T("Stack.Empty", L.T(DomainKey(domain)))); empty.Foreground = Resource("MutedBrush"); empty.Margin = new Thickness(0, 8, 0, 12); tracks.Children.Add(empty);
                }
                group.Children.Add(tracks); DomainsPanel.Children.Add(group);
            }
        }
        finally { _syncing = false; }
        RefreshValues(); _overview.Render();
    }
    private FrameworkElement TrackRow(ConstraintTrackDto track)
    {
        var row = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 0, 0, 16), Margin = new Thickness(0, 0, 0, 16) };
        row.SetResourceReference(Border.BorderBrushProperty, "BorderBrushKey"); _rows[track.Id] = row;
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(126) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); row.Child = grid;
        var meta = new StackPanel { Margin = new Thickness(0, 0, 12, 0) }; grid.Children.Add(meta);
        var name = ActionButton(Session.DisplayName(track), (_, _) => Session.Select(track.Id));
        name.Content = new TextBlock { Text = Session.DisplayName(track), TextWrapping = TextWrapping.Wrap, FontSize = 15, FontWeight = FontWeights.SemiBold };
        name.HorizontalContentAlignment = HorizontalAlignment.Left; name.Padding = new Thickness(6); meta.Children.Add(name);
        var weightLabel = Label(L.T("Stack.Weight"), 12); weightLabel.Margin = new Thickness(0, 8, 0, 4); meta.Children.Add(weightLabel);
        var number = NumberBox(track.Weight, 65, L.T("Stack.Weight")); meta.Children.Add(number);
        var slider = new Slider { Minimum = 0.1, Maximum = 1, Value = track.Weight, TickFrequency = 0.05, IsSnapToTickEnabled = true, Margin = new Thickness(0, 6, 6, 8), ToolTip = L.T("Stack.WeightHint") }; meta.Children.Add(slider);
        slider.ValueChanged += (_, _) => { if (!_syncing) Session.Edit(_ => track.Weight = slider.Value, coalesceKey: "weight:" + track.Id); };
        number.LostKeyboardFocus += (_, _) => CommitNumber(number, track.Weight, 0.1, 1, value => Session.Edit(_ => track.Weight = value));
        var remove = ActionButton(L.T("Stack.RemoveTrack"), (_, _) =>
        {
            if (track.Segments.Count > 0 && MessageBox.Show(this, L.T("Stack.RemoveConfirm", Session.DisplayName(track)), L.T("Stack.Title"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            Session.Edit(data => data.Tracks.Remove(track), true);
        });
        remove.FontSize = 12; meta.Children.Add(remove);
        var content = new StackPanel(); Grid.SetColumn(content, 1); grid.Children.Add(content);
        var ruler = new Grid { Margin = new Thickness(0, 0, 0, 5) };
        for (var i = 0; i < 4; i++) ruler.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < 5; i++) { var tick = Label(i * 25 + "%", 11); tick.Foreground = Resource("MutedBrush"); tick.HorizontalAlignment = i == 4 ? HorizontalAlignment.Right : HorizontalAlignment.Left; Grid.SetColumn(tick, Math.Min(3, i)); ruler.Children.Add(tick); }
        content.Children.Add(ruler);
        var lane = new ConstraintLane(Session, track.Id); content.Children.Add(lane);
        var clips = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) }; content.Children.Add(clips);
        foreach (var clip in track.Segments)
        {
            var button = ActionButton($"{clip.Start:0.#}–{clip.End:0.#}%", (_, _) => Session.Select(track.Id, clip.Id));
            button.Margin = new Thickness(0, 0, 6, 6); button.FontSize = 11; button.ToolTip = clip.Directive;
            _refresh.Add(() => { button.Content = $"{clip.Start:0.#}–{clip.End:0.#}%"; button.BorderBrush = Session.SelectedSegmentId == clip.Id ? Resource("AccentBrush") : Resource("BorderBrushKey"); });
            clips.Children.Add(button);
        }
        var addClip = ActionButton(L.T("Stack.AddSegment"), (_, _) =>
        {
            var start = Math.Min(80, track.Segments.Select(s => s.End).DefaultIfEmpty(0).Max());
            var segment = new ConstraintSegmentDto { Start = start, End = start + 20 };
            Session.Edit(_ => track.Segments.Add(segment), true); Session.Select(track.Id, segment.Id);
        });
        addClip.FontSize = 11; clips.Children.Add(addClip);
        _refresh.Add(() =>
        {
            name.BorderBrush = Session.SelectedTrackId == track.Id ? Resource("AccentBrush") : Resource("BorderBrushKey");
            ((TextBlock)name.Content).Text = Session.DisplayName(track);
            if (!number.IsKeyboardFocusWithin) number.Text = Format(track.Weight);
            if (!slider.IsMouseCaptureWithin) slider.Value = track.Weight;
            number.IsReadOnly = Session.ReadOnly; slider.IsEnabled = remove.IsEnabled = addClip.IsEnabled = !Session.ReadOnly;
            lane.InvalidateVisual();
        });
        var inspector = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        _inspectors[track.Id] = inspector; content.Children.Add(inspector);
        if (Session.SelectedTrackId == track.Id && Session.SelectedSegment is { } selected)
            inspector.Content = SegmentEditor(track, selected);
        return row;
    }
    private FrameworkElement SegmentEditor(ConstraintTrackDto track, ConstraintSegmentDto segment)
    {
        var editor = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var range = new WrapPanel(); editor.Children.Add(range);
        var start = NumberBox(segment.Start, 65, L.T("Stack.Start")); var end = NumberBox(segment.End, 65, L.T("Stack.End"));
        range.Children.Add(new TextBlock { Text = L.T("Stack.Start"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) }); range.Children.Add(start);
        range.Children.Add(new TextBlock { Text = "%  →  ", VerticalAlignment = VerticalAlignment.Center }); range.Children.Add(end);
        range.Children.Add(new TextBlock { Text = "%", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0) });
        var delete = ActionButton(L.T("Stack.DeleteSegment"), (_, _) => Session.DeleteSelection()); range.Children.Add(delete);
        start.LostKeyboardFocus += (_, _) => CommitNumber(start, segment.Start, 0, segment.End - 0.01, value => Session.Edit(_ => segment.Start = value));
        end.LostKeyboardFocus += (_, _) => CommitNumber(end, segment.End, segment.Start + 0.01, 100, value => Session.Edit(_ => segment.End = value));
        var label = Label(L.T("Stack.Directive"), 12); label.Margin = new Thickness(0, 8, 0, 5); editor.Children.Add(label);
        var directive = new TextBox { Text = segment.Directive, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 80, Height = 96, Padding = new Thickness(8), FontSize = 14 };
        AutomationProperties.SetName(directive, L.T("Stack.Directive")); editor.Children.Add(directive);
        directive.TextChanged += (_, _) => { if (!_syncing) Session.Edit(_ => segment.Directive = directive.Text, coalesceKey: "directive:" + segment.Id); };
        _editorRefresh.Add(() =>
        {
            if (!start.IsKeyboardFocusWithin) start.Text = Format(segment.Start);
            if (!end.IsKeyboardFocusWithin) end.Text = Format(segment.End);
            if (!directive.IsKeyboardFocusWithin && directive.Text != segment.Directive) directive.Text = segment.Directive;
            start.IsReadOnly = end.IsReadOnly = directive.IsReadOnly = Session.ReadOnly; delete.IsEnabled = !Session.ReadOnly;
        });
        return editor;
    }
    private static string Format(double value) => value.ToString("0.##", CultureInfo.CurrentCulture);
    private static TextBox NumberBox(double value, double width, string name)
    {
        var box = new TextBox { Text = Format(value), Width = width, Padding = new Thickness(5, 3, 5, 3), HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(box, name); return box;
    }
    private void CommitNumber(TextBox box, double old, double min, double max, Action<double> apply)
    {
        if (Session.ReadOnly) { box.Text = Format(old); return; }
        if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) && double.IsFinite(value) && value >= min && value <= max)
        { box.ToolTip = null; apply(value); }
        else { box.ToolTip = L.T("Stack.InvalidNumber", Format(min), Format(max)); box.Text = Format(old); }
    }
    private void AddElements(string domain)
    {
        if (Session.ReadOnly) return;
        IReadOnlyList<PlanChoice> choices = domain switch
        {
            "character" => Session.Catalog.Characters.Select(c => new PlanChoice(c.Id, c.Name)).ToList(),
            "thread" => Session.Catalog.Threads.Select(t => new PlanChoice(t.Id, t.Name)).ToList(),
            _ => SceneTypes.All.Select(id => new PlanChoice(id, L.T("Scene." + id))).ToList(),
        };
        var picker = new PlanSelectionWindow(L.T("Stack.Add." + domain), choices,
            Session.Data.Tracks.Where(t => t.Domain == domain).Select(t => t.Element_Id).ToList(), false) { Owner = this };
        if (picker.ShowDialog() != true) return;
        Session.Edit(data =>
        {
            foreach (var id in picker.SelectedIds)
                if (!data.Tracks.Any(t => t.Domain == domain && t.Element_Id == id))
                    data.Tracks.Add(new ConstraintTrackDto { Domain = domain, Element_Id = id, Name = choices.First(c => c.Id == id).Name });
        }, true);
    }
    private void RefreshValues()
    {
        var title = Domain is null ? L.T("Stack.Title") : L.T("Stack.DomainWindow", L.T(DomainKey(Domain)));
        Title = $"{title} · {Session.ChapterNo} · {Session.ChapterTitle}";
        _syncing = true;
        try { foreach (var refresh in _refresh.Concat(_editorRefresh)) refresh(); }
        finally { _syncing = false; }
        UndoButton.IsEnabled = Session.CanUndo; RedoButton.IsEnabled = Session.CanRedo;
        SaveButton.IsEnabled = !Session.ReadOnly;
        StatusText.Text = Session.Error is { } error ? L.T("Stack.SaveFailed", error)
            : Session.ReadOnly ? L.T("Stack.ReadOnly") : Session.IsSaving ? L.T("Stack.Saving")
            : Session.IsDirty ? L.T("Stack.Unsaved") : L.T("Stack.Saved");
        if (Session.SelectedTrack is not { } track) SelectionText.Text = L.T("Stack.SelectionHint");
        else SelectionText.Text = Session.DisplayName(track) + " · " + L.T("Stack.Weight") + " " + Format(track.Weight) + "\n" +
            (Session.SelectedSegment is { } segment ? $"{segment.Start:0.#}–{segment.End:0.#}%\n{segment.Directive}" : L.T("Stack.AllSegments", track.Segments.Count));
    }
    private async void Save_Click(object sender, RoutedEventArgs e) { Keyboard.ClearFocus(); await Session.FlushAsync(); }
    private void Undo_Click(object sender, RoutedEventArgs e) => Session.Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => Session.Redo();
    private void ClearSelection_Click(object sender, RoutedEventArgs e) => Session.Select(null);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { Save_Click(sender, e); e.Handled = true; return; }
        if (Keyboard.FocusedElement is TextBoxBase) return;
        if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control) { Session.Undo(); e.Handled = true; }
        else if (e.Key == Key.Y && Keyboard.Modifiers == ModifierKeys.Control) { Session.Redo(); e.Handled = true; }
        else if (e.Key == Key.Delete) { Session.DeleteSelection(); e.Handled = true; }
        else if (e.Key == Key.Escape) { Session.Select(null); e.Handled = true; }
    }
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeAllowed) return;
        // Commit focused numeric editors before deciding whether a save is needed.
        Keyboard.ClearFocus();
        if (!Session.HasPendingSave) return;
        e.Cancel = true; if (_closing) return; _closing = true;
        try
        {
            while (Session.HasPendingSave) if (!await Session.FlushAsync()) return;
            _closeAllowed = true; await Dispatcher.BeginInvoke(Close);
        }
        finally { _closing = false; }
    }
    internal void CloseSaved() { _closeAllowed = true; Close(); }
}
