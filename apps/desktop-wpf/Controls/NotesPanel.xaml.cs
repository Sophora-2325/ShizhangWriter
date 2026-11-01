using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Models;

namespace NovelGm.Desktop.Controls;

/// <summary>作品级独立笔记条目。自动保护草稿，明确保存后回到统一列表，不调用模型。</summary>
public partial class NotesPanel : UserControl
{
    private readonly INotesApi _api;
    private readonly Func<string?> _requestNewTitle;
    private readonly DispatcherTimer _debounce;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly object _sortIcon;
    private Func<string, Task<string?>>? _retryOperation;
    private string? _projectId;
    private List<NoteDto> _notes = new();
    private List<NoteDto> _visibleNotes = new();
    private NoteDto? _selected;
    private bool _dirty, _loading, _switching, _mutating, _ready;
    public static readonly DependencyProperty IsSortingProperty = DependencyProperty.Register(nameof(IsSorting), typeof(bool), typeof(NotesPanel), new PropertyMetadata(false));
    public bool IsSorting { get => (bool)GetValue(IsSortingProperty); set => SetValue(IsSortingProperty, value); }
    private int _generation;
    private string? _statusKey;
    private object?[] _statusArgs = Array.Empty<object?>();
    private bool _statusError;

    public NotesPanel() : this(new NotesClient(App.Api)) { }
    internal NotesPanel(INotesApi api, Func<string?>? requestNewTitle = null)
    {
        _api = api;
        _requestNewTitle = requestNewTitle ?? PromptNewTitle;
        InitializeComponent();
        _sortIcon = SortButton.Content;
        _debounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(600) };
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await SaveDraftAsync(); };
        Unloaded += async (_, _) => { _debounce.Stop(); await FlushAsync(); };
        LanguageManager.LanguageChanged += (_, _) =>
        {
            RefreshList(_selected?.Id, true); RenderStatus(); RenderEmpty();
        };
        RenderEmpty();
    }

    public event EventHandler<string>? StatusChanged;
    public bool HasUnsavedDraft => _dirty;
    /// <summary>仅在用户明确确认放弃未保存草稿后调用。</summary>
    public void DiscardDraft() { _debounce.Stop(); _dirty = false; }
    internal int DebounceMilliseconds { get => (int)_debounce.Interval.TotalMilliseconds; set => _debounce.Interval = TimeSpan.FromMilliseconds(Math.Max(1, value)); }
    internal string StatusTextForTest => StatusText.Text;
    internal bool StatusIsErrorForTest => _statusError;
    internal NoteDto? SelectedNoteForTest => _selected;
    internal IReadOnlyList<NoteDto> VisibleNotesForTest => _visibleNotes;

    public void SetProject(string? id, IReadOnlyList<ChapterDto> chapters, int? currentChapter)
    {
        if (_projectId == id) return;
        // 外壳先 Flush 并检查 HasUnsavedDraft。失败时拒绝切换，保留编辑区。
        if (_dirty) return;
        _generation++; _projectId = id; _ready = false; IsSorting = false; _retryOperation = null;
        _notes.Clear(); ShowNote(null); RefreshList();
        SetStatus(null);
        if (id is not null) _ = ReloadAsync();
    }

    // 保留外壳调用契约；章节切换不再改变笔记视图或归属。
    public void SetChapters(IReadOnlyList<ChapterDto> chapters) { }
    public void SetCurrentChapter(int? number) { }

    public async Task FlushAsync()
    {
        _debounce.Stop();
        while (_dirty) if (!await SaveDraftAsync()) break;
    }

    public async Task ReloadAsync()
    {
        await FlushAsync();
        if (_dirty || _projectId is null) return;
        var project = _projectId; var generation = ++_generation;
        try
        {
            var notes = await _api.ListAsync(project);
            if (project != _projectId || generation != _generation || _dirty) return;
            _notes = notes.Select(Normalize).ToList();
            _ready = true;
            var editing = _selected?.Id;
            RefreshList(editing ?? (NoteList.SelectedItem as NoteDto)?.Id);
            if (editing is not null) ShowNote(_notes.FirstOrDefault(n => n.Id == editing));
            SetStatus(null);
        }
        catch (Exception ex)
        {
            if (project == _projectId && generation == _generation)
            {
                SetStatus("Notes.LoadFailed", true, ex.Message);
                RetryButton.Visibility = Visibility.Visible;
            }
        }
    }

    private static NoteDto Normalize(NoteDto note)
    {
        note.Title ??= ""; note.Body ??= "";
        // 历史分类统一展示；下一次保存时写回默认归属，保留每条笔记的 ID 与正文。
        note.Folder = "default";
        return note;
    }

    private void RefreshList(string? id = null, bool preserveEditor = false)
    {
        _visibleNotes = _notes.OrderByDescending(n => n.Pinned).ThenBy(n => n.Sort_Order).ThenBy(n => n.Id, StringComparer.Ordinal).ToList();
        for (var i = 0; i < _visibleNotes.Count; i++)
        {
            _visibleNotes[i].CanMoveUp = i > 0 && _visibleNotes[i - 1].Pinned == _visibleNotes[i].Pinned;
            _visibleNotes[i].CanMoveDown = i + 1 < _visibleNotes.Count && _visibleNotes[i + 1].Pinned == _visibleNotes[i].Pinned;
        }
        var target = _visibleNotes.FirstOrDefault(n => n.Id == id);
        _loading = true;
        try { NoteList.ItemsSource = _visibleNotes; NoteList.SelectedItem = target; }
        finally { _loading = false; }
        if (!preserveEditor) ShowNote(null);
        RenderEmpty();
    }

    private void ShowNote(NoteDto? note)
    {
        _loading = true;
        try
        {
            _selected = note; BodyBox.Text = note?.Body ?? "";
            _dirty = false;
        }
        finally { _loading = false; }
        RetryButton.Visibility = Visibility.Collapsed;
        RenderEmpty();
    }

    private void RenderEmpty()
    {
        if (EmptyState is null) return;
        var editing = _selected is not null;
        EmptyState.Visibility = !editing && _visibleNotes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoteList.Visibility = !editing && _visibleNotes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EditorGrid.Visibility = BackButton.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        EditorGrid.IsEnabled = BackButton.IsEnabled = !_mutating;
        DeleteButton.IsEnabled = (editing || NoteList.SelectedItem is NoteDto) && !_mutating;
        NoteList.IsEnabled = !_mutating && !_switching;
        NewButton.IsEnabled = EmptyCreateButton.IsEnabled = _projectId is not null && _ready && !_mutating;
        SortButton.IsEnabled = !editing && _notes.Count > 1 && !_mutating;
        SortButton.ToolTip = L.T(IsSorting ? "Notes.SortDone" : "Notes.Sort");
        SortButton.Content = IsSorting ? L.T("Notes.SortDone") : _sortIcon;
        SortButton.Width = IsSorting ? double.NaN : 30;
        System.Windows.Automation.AutomationProperties.SetName(SortButton, (string)SortButton.ToolTip);
        SortButton.SetResourceReference(BackgroundProperty, IsSorting ? "InfoBgBrush" : "BgBrush");
        SortButton.SetResourceReference(ForegroundProperty, "TextBrush");
    }

    private void NoteList_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_loading) RenderEmpty(); }

    internal async Task OpenSelectedAsync()
    {
        if (_loading || _switching || _mutating || NoteList.SelectedItem is not NoteDto note || note.Id == _selected?.Id) return;
        _switching = true; NoteList.IsEnabled = false; var project = _projectId;
        try
        {
            await FlushAsync();
            if (project != _projectId) return;
            if (_dirty) { _loading = true; NoteList.SelectedItem = _selected; _loading = false; return; }
            IsSorting = false; _retryOperation = null;
            ShowNote(_notes.FirstOrDefault(n => n.Id == note.Id) ?? note);
        }
        finally { _switching = false; NoteList.IsEnabled = true; }
    }

    private async void NoteList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (ItemsControl.ContainerFromElement(NoteList, e.OriginalSource as DependencyObject) is not ListBoxItem) return;
        if (FindButton(e.OriginalSource as DependencyObject) is not null) return;
        e.Handled = true; await OpenSelectedAsync();
    }

    private static Button? FindButton(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is Button button) return button;
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return null;
    }

    private async void NoteList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && FindButton(e.OriginalSource as DependencyObject) is null)
        { e.Handled = true; await OpenSelectedAsync(); }
        else if (e.Key == Key.Apps || (e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift))
        {
            if (NoteList.SelectedItem is NoteDto note && NoteList.ItemContainerGenerator.ContainerFromItem(note) is ListBoxItem item)
            { ShowMenu(item, note); e.Handled = true; }
        }
    }

    private void NoteItem_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (_mutating || sender is not ListBoxItem { DataContext: NoteDto note } item) return;
        NoteList.SelectedItem = note; item.Focus(); ShowMenu(item, note); e.Handled = true;
    }

    internal ContextMenu CreateNoteMenu(NoteDto note)
    {
        var menu = new ContextMenu();
        void Item(string key, Action action)
        {
            var item = new MenuItem { Header = L.T(key), IsEnabled = !_mutating };
            item.Click += (_, _) => action(); menu.Items.Add(item);
        }
        Item("Notes.Open", async () => { NoteList.SelectedItem = note; await OpenSelectedAsync(); });
        Item("Notes.Rename", async () =>
        {
            var dialog = new RenameWindow(L.T("Notes.Rename"), L.T("Notes.RenamePrompt"), note.Title) { Owner = Window.GetWindow(this) };
            dialog.NameBox.MaxLength = 120;
            if (dialog.ShowDialog() == true) await RenameNoteAsync(note, dialog.Value);
        });
        Item(note.Pinned ? "Notes.Unpin" : "Notes.Pin", async () => await PinNoteAsync(note));
        Item("Notes.Copy", async () => await CopyNoteAsync(note));
        menu.Items.Add(new Separator());
        Item("Notes.Delete", async () => await DeleteNoteAsync(note));
        return menu;
    }

    private void ShowMenu(ListBoxItem item, NoteDto note)
    {
        var menu = CreateNoteMenu(note); menu.PlacementTarget = item; menu.Placement = PlacementMode.MousePoint; menu.IsOpen = true;
    }

    private async Task MutateListAsync(Func<string, Task<string?>> operation)
    {
        if (_projectId is null || _mutating || !_ready) return;
        var project = _projectId; _mutating = true; _generation++; _retryOperation = null; RenderEmpty();
        try
        {
            await FlushAsync(); if (_dirty || project != _projectId) return;
            var selected = await operation(project);
            if (project != _projectId) return;
            RefreshList(selected); SetStatus(null);
        }
        catch (Exception ex)
        {
            if (project == _projectId)
            { _retryOperation = operation; SetStatus("Notes.OperationFailed", true, ex.Message); RetryButton.Visibility = Visibility.Visible; }
        }
        finally { _mutating = false; RenderEmpty(); }
    }

    internal Task PinNoteAsync(NoteDto note) => MutateListAsync(async project =>
    {
        var notes = await _api.PinAsync(project, note.Id, !note.Pinned);
        if (project == _projectId) _notes = notes.Select(Normalize).ToList();
        return note.Id;
    });

    internal Task CopyNoteAsync(NoteDto note)
    {
        // Allocate once for this user action; the retry closure retains the key.
        var operationId = Guid.NewGuid().ToString("N");
        var title = L.T("Notes.CopyTitle", note.DisplayTitle);
        return MutateListAsync(async project =>
        {
            var copy = Normalize(await _api.CopyAsync(project, note.Id, title, operationId));
            if (project == _projectId)
            {
                var index = _notes.FindIndex(n => n.Id == copy.Id);
                if (index >= 0) _notes[index] = copy;
                else _notes.Add(copy);
            }
            return copy.Id;
        });
    }

    internal Task RenameNoteAsync(NoteDto note, string title) => MutateListAsync(async project =>
    {
        var updated = Normalize(await _api.UpdateAsync(project, note.Id, title, note.Body, note.Content_Version, "default"));
        if (project == _projectId) { var index = _notes.FindIndex(n => n.Id == note.Id); if (index >= 0) _notes[index] = updated; }
        return note.Id;
    });

    internal Task MoveNoteAsync(NoteDto note, string direction)
    {
        var operationId = Guid.NewGuid().ToString("N");
        return MutateListAsync(async project =>
        {
            var notes = await _api.MoveAsync(project, note.Id, direction, operationId);
            if (project == _projectId) _notes = notes.Select(Normalize).ToList();
            return note.Id;
        });
    }

    private async void Move_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { DataContext: NoteDto note, Tag: string direction }) await MoveNoteAsync(note, direction);
    }

    private void Editor_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading || _selected is null) return;
        // 刷新请求可能早于本次编辑发出，自动保存完成后 dirty 会恢复为 false；
        // 仍需让该旧响应失效，避免它覆盖新正文/版本号或显示过期错误。
        _generation++;
        _dirty = true; RetryButton.Visibility = Visibility.Collapsed;
        SetStatus("Notes.Editing"); _debounce.Stop(); _debounce.Start();
    }

    private async Task<bool> SaveDraftAsync()
    {
        if (!_dirty || _selected is null || _projectId is null) return true;
        var project = _projectId; var id = _selected.Id;
        await _saveGate.WaitAsync();
        try
        {
            if (project != _projectId || id != _selected?.Id || !_dirty) return true;
            var title = _selected.Title; var body = BodyBox.Text;
            var basis = _notes.First(n => n.Id == id);
            SetStatus("Notes.Saving");
            var updated = Normalize(await _api.UpdateAsync(project, id, title, body, basis.Content_Version, "default"));
            if (project != _projectId) return true;
            var index = _notes.FindIndex(n => n.Id == id);
            if (index >= 0) _notes[index] = updated;
            if (_selected?.Id == id)
            {
                _selected = updated;
                _dirty = BodyBox.Text != body;
                if (!_dirty) { RetryButton.Visibility = Visibility.Collapsed; SetStatus("Notes.DraftSaved"); }
                else { _debounce.Stop(); _debounce.Start(); }
            }
            if (!_dirty) RefreshList(id, preserveEditor: true);
            return true;
        }
        catch (Exception ex)
        {
            if (project == _projectId) { _dirty = true; SetStatus("Notes.SaveFailed", true, ex.Message); RetryButton.Visibility = Visibility.Visible; }
            return false;
        }
        finally { _saveGate.Release(); }
    }

    private async void Finish_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _mutating) return;
        var project = _projectId;
        _mutating = true; RenderEmpty();
        try
        {
            await FlushAsync();
            if (_dirty || project != _projectId) return;
            RefreshList(); SetStatus("Notes.Saved"); NoteList.Focus();
        }
        finally { _mutating = false; RenderEmpty(); }
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (_dirty) await FlushAsync();
        else if (_retryOperation is { } operation) await MutateListAsync(operation);
        else await ReloadAsync();
    }
    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        IsSorting = !IsSorting; RenderEmpty();
    }

    private async void New_Click(object sender, RoutedEventArgs e)
    {
        if (_projectId is null || !_ready || _mutating) return;
        var project = _projectId; _mutating = true; RenderEmpty();
        try
        {
            await FlushAsync(); if (_dirty || project != _projectId) return;
            var title = _requestNewTitle()?.Trim();
            if (string.IsNullOrWhiteSpace(title) || title.Length > 120 || project != _projectId) return;
            _generation++;
            var created = Normalize(await _api.CreateAsync(project, "default", title, ""));
            if (project != _projectId) return;
            _notes.Add(created); IsSorting = false; RefreshList(created.Id); ShowNote(created); SetStatus("Notes.Created");
        }
        catch (Exception ex) { if (project == _projectId) SetStatus("Notes.CreateFailed", true, ex.Message); }
        finally { _mutating = false; RenderEmpty(); if (_selected is not null) BodyBox.Focus(); }
    }

    private string? PromptNewTitle()
    {
        var dialog = new RenameWindow(L.T("Notes.New"), L.T("Notes.RenamePrompt"), "") { Owner = Window.GetWindow(this) };
        dialog.NameBox.MaxLength = 120;
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if ((_selected ?? NoteList.SelectedItem as NoteDto) is { } note) await DeleteNoteAsync(note);
    }

    private async Task DeleteNoteAsync(NoteDto note)
    {
        if (_projectId is null || _mutating) return;
        var project = _projectId; var id = note.Id;
        var choice = MessageBox.Show(L.T("Notes.DeleteConfirm"), L.T("Notes.Title"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (choice != MessageBoxResult.Yes) return;
        _mutating = true; RenderEmpty();
        try
        {
            await FlushAsync(); if (_dirty || project != _projectId) return;
            _generation++;
            await _api.DeleteAsync(project, id);
            if (project != _projectId) return;
            _notes.RemoveAll(n => n.Id == id); RefreshList(); SetStatus("Notes.Deleted");
        }
        catch (Exception ex) { if (project == _projectId) SetStatus("Notes.DeleteFailed", true, ex.Message); }
        finally { _mutating = false; RenderEmpty(); }
    }

    private void SetStatus(string? key, bool error = false, params object?[] args)
    {
        _statusKey = key; _statusError = error; _statusArgs = args; RenderStatus();
        if (error) StatusChanged?.Invoke(this, StatusText.Text);
    }
    private void RenderStatus()
    {
        StatusText.Text = _statusKey is null ? "" : L.T(_statusKey, _statusArgs);
        StatusText.Visibility = _statusError ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Foreground = TryFindResource(_statusError ? "DangerBrush" : "MutedBrush") as Brush ?? Brushes.Gray;
    }
}
