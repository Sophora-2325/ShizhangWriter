using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Models;

namespace NovelGm.Desktop.Controls;

public sealed partial class ReadingView
{
    private readonly Popup _directoryPopup = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom };
    private readonly Popup _findPopup = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom };
    private Button _directoryButton = null!;
    private Button _findButton = null!;
    private TextBox? _findInput;
    private TextBlock? _findStatus;
    private List<ChapterDto> _catalog = new();
    private List<int> _matches = new();
    private int _matchIndex = -1;
    private string _findQuery = "";

    public void SetChapters(IEnumerable<ChapterDto> chapters) =>
        _catalog = chapters.Where(c => c.Status != "superseded").OrderBy(c => c.No).ToList();

    private void AddReadingTools(Panel actions)
    {
        _directoryButton = Button("Read.Directory", (_, _) => OpenDirectory());
        _findButton = Button("Read.Find", (_, _) => OpenFind());
        actions.Children.Add(_directoryButton);
        actions.Children.Add(_findButton);
    }

    private void SetPopup(Popup popup, UIElement content, FrameworkElement target)
    {
        var border = new Border { Child = content, Padding = new Thickness(14), BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BackgroundProperty, "BgBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "BorderBrushKey");
        border.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { CloseSettings(); target.Focus(); e.Handled = true; }
        };
        popup.Child = border;
        popup.PlacementTarget = target;
        popup.IsOpen = true;
    }

    private void OpenDirectory()
    {
        var wasOpen = _directoryPopup.IsOpen;
        CloseSettings();
        if (wasOpen) return;
        var list = new ListBox { Width = 300, MaxHeight = 420 };
        System.Windows.Automation.AutomationProperties.SetName(list, L.T("Read.Directory"));
        foreach (var chapter in _catalog)
            list.Items.Add(new ListBoxItem { Content = HtmlPreview.Heading(chapter), Tag = chapter.No,
                Padding = new Thickness(8), IsSelected = chapter.No == _chapter?.No });
        void Choose()
        {
            if (list.SelectedItem is not ListBoxItem { Tag: int no }) return;
            _directoryPopup.IsOpen = false;
            if (no != _chapter?.No) Jump?.Invoke(no);
            else FocusReading();
        }
        list.MouseLeftButtonUp += (_, _) => Choose();
        list.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Choose(); e.Handled = true; } };
        SetPopup(_directoryPopup, list, _directoryButton);
        if (list.SelectedItem is { } selected) list.ScrollIntoView(selected);
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
        {
            if (_directoryPopup.IsOpen) (list.SelectedItem as ListBoxItem)?.Focus();
        }));
    }

    private void OpenFind()
    {
        if (_findPopup.IsOpen) { _findInput?.Focus(); return; }
        CloseSettings();
        var panel = new StackPanel { Width = 300 };
        _findInput = new TextBox { Text = _findQuery, MinHeight = 32, Margin = new Thickness(0, 0, 0, 10) };
        System.Windows.Automation.AutomationProperties.SetName(_findInput, L.T("Read.Find"));
        panel.Children.Add(_findInput);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Button("Read.FindPrev", (_, _) => FindMatch(-1)));
        row.Children.Add(Button("Read.FindNext", (_, _) => FindMatch(1)));
        panel.Children.Add(row);
        _findStatus = new TextBlock { Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(_findStatus);
        _findInput.TextChanged += (_, _) => { _findQuery = _findInput.Text; _matchIndex = -1; FindMatch(1); };
        _findInput.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { FindMatch(Keyboard.Modifiers == ModifierKeys.Shift ? -1 : 1); e.Handled = true; }
        };
        SetPopup(_findPopup, panel, _findButton);
        var input = _findInput;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
        {
            if (_findPopup.IsOpen) { input.Focus(); input.SelectAll(); }
        }));
        UpdateFindStatus();
    }

    private void FindMatch(int direction)
    {
        if (_start is null || _end is null) return;
        ClearFindMarks();
        _matches = ReadingText.FindMatches(_text, _findQuery);
        if (_matches.Count == 0) { _matchIndex = -1; UpdateFindStatus(); return; }
        _matchIndex = _matchIndex < 0 ? (direction < 0 ? _matches.Count - 1 : 0)
            : (_matchIndex + direction + _matches.Count) % _matches.Count;
        ReadingText.MarkMatches(_start, _end, _matches, _matchIndex, _findQuery.Length);
        var start = ReadingText.Pointer(_start, _end, _matches[_matchIndex]);
        var end = ReadingText.Pointer(_start, _end, _matches[_matchIndex] + _findQuery.Length);
        _viewer.Selection?.Select(start, end);
        GoToOffset(_matches[_matchIndex]);
        UpdateFindStatus();
    }

    private void ClearFindMarks()
    {
        if (_viewer.Document is FlowDocument document) ReadingText.ClearMarks(document);
    }

    private void UpdateFindStatus()
    {
        if (_findStatus is null) return;
        _findStatus.Text = string.IsNullOrEmpty(_findQuery) ? L.T("Read.FindHint")
            : _matches.Count == 0 ? L.T("Read.FindNone") : L.T("Read.FindCount", Math.Max(0, _matchIndex + 1), _matches.Count);
    }

    private void ResetFindForChapter()
    {
        ClearFindMarks();
        _matches = ReadingText.FindMatches(_text, _findQuery);
        _matchIndex = -1;
        _findPopup.IsOpen = false;
        _directoryPopup.IsOpen = false;
    }
}
