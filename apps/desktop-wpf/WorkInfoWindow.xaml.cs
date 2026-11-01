using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Models;

namespace NovelGm.Desktop;

public partial class WorkInfoWindow : Window
{
    private ProjectDto _project;
    private WorkInfoDraft _draft;
    private bool _busy;
    private int _coverRequest;
    public bool Updated { get; private set; }
    internal sealed record ChapterRow(string Name, string State, string Words);

    public WorkInfoWindow(ProjectDto project, IReadOnlyList<ChapterDto> chapters)
    {
        InitializeComponent();
        _project = project; _draft = new(project);
        FillFields(); SetBusy(false);
        var live = chapters.Where(c => c.Status != "superseded").OrderBy(c => c.No).ToList();
        StatsText.Text = L.T("WorkInfo.Stats", live.Count, live.Count(c => c.Status == "final"), live.Sum(c => (long)c.Word_Count));
        ChapterGrid.ItemsSource = live.Select(c => new ChapterRow($"{c.No}. {c.Title}", StateLabel(c.Status), c.Word_Count.ToString("N0")));
        EmptyChapters.Visibility = live.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ChapterGrid.Visibility = live.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Loaded += async (_, _) =>
        {
            MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height * .92);
            MaxWidth = Math.Max(MinWidth, SystemParameters.WorkArea.Width * .94);
            await ShowCoverAsync();
        };
    }
    private static string StateLabel(string state)
    { var key = "WorkInfo.State." + state; var label = L.T(key); return label == key ? state : label; }
    private void FillFields()
    {
        BookTitle.Text = _project.Title;
        FolderText.Text = string.IsNullOrEmpty(_project.Root_Path) ? L.T("Work.InfoLegacy") : _project.Root_Path;
        GenreBox.Text = _draft.Genre; ToneBox.Text = _draft.Tone; AudienceBox.Text = _draft.Audience;
        CoverHint.Text = _project.Root_Missing ? L.T("WorkInfo.RootMissing")
            : string.IsNullOrEmpty(_project.Root_Path) ? L.T("Char.PortraitNeedsFolder") : L.T("WorkInfo.CoverHint");
    }
    private void ReadFields()
    { _draft.Genre = GenreBox.Text; _draft.Tone = ToneBox.Text; _draft.Audience = AudienceBox.Text; }
    private void SetBusy(bool value)
    {
        _busy = value;
        var editable = !value && !_project.Root_Missing;
        GenreBox.IsReadOnly = ToneBox.IsReadOnly = AudienceBox.IsReadOnly = !editable;
        PickCoverButton.IsEnabled = editable && Directory.Exists(_project.Root_Path);
        RemoveCoverButton.IsEnabled = editable && !string.IsNullOrEmpty(_draft.Cover.Preview);
        SaveButton.IsEnabled = RenameButton.IsEnabled = editable;
        CloseButton.IsEnabled = !value;
        FolderButton.IsEnabled = !value && Directory.Exists(_project.Root_Path);
    }
    private async Task ShowCoverAsync()
    {
        var request = ++_coverRequest;
        CoverImage.Source = null; CoverPlaceholder.Visibility = Visibility.Visible;
        if (string.IsNullOrEmpty(_draft.Cover.Preview)) return;
        try
        {
            var path = Path.Combine(_project.Root_Path, _draft.Cover.Preview.Replace('/', Path.DirectorySeparatorChar));
            var bitmap = await ImageAssets.LoadAsync(path, 480);
            if (request != _coverRequest) return;
            CoverImage.Source = bitmap; CoverPlaceholder.Visibility = Visibility.Collapsed;
        }
        catch { if (request == _coverRequest) StatusText.Text = L.T("WorkInfo.CoverUnavailable"); }
    }
    private async void PickCover_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new OpenFileDialog { Title = L.T("WorkInfo.PickCover"), Filter = L.T("Char.ImageFilter") };
        if (dialog.ShowDialog(this) != true) return;
        SetBusy(true); StatusText.Text = L.T("Fmt.InsertImageBusy");
        try
        {
            var stored = await BodyFigures.StoreAsync(_project.Root_Path, dialog.FileName);
            _draft.Cover = new() { Preview = stored.Relative, Original = stored.OriginalRelative };
            await ShowCoverAsync();
            if (CoverImage.Source is not null) StatusText.Text = L.T("WorkInfo.Pending");
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { SetBusy(false); }
    }
    private void RemoveCover_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _draft.Cover = new(); ++_coverRequest;
        CoverImage.Source = null; CoverPlaceholder.Visibility = Visibility.Visible;
        StatusText.Text = L.T("WorkInfo.Pending"); SetBusy(false);
    }
    private async Task<bool> SaveAsync()
    {
        ReadFields(); var changes = _draft.Changes();
        if (!changes.HasChanges) { StatusText.Text = L.T("Misc.NoChange"); return true; }
        SetBusy(true); StatusText.Text = L.T("Busy.Saving");
        try
        {
            _project = await App.Api.UpdateWorkInfoAsync(_project.Id, changes);
            _draft = new(_project); Updated = true; FillFields();
            StatusText.Text = L.T("WorkInfo.Saved"); return true;
        }
        catch (Exception ex) { StatusText.Text = ex.Message; return false; }
        finally { SetBusy(false); }
    }
    private async void Save_Click(object sender, RoutedEventArgs e) { if (!_busy) await SaveAsync(); }
    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new RenameWindow(L.T("Rename.WorkTitle"), L.T("Rename.WorkPrompt"), _project.Title,
            string.IsNullOrEmpty(_project.Root_Path) ? "" : L.T("Rename.WorkHint")) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Value == _project.Title) return;
        // Complete the metadata save before the folder can move; a failed save keeps the old name.
        if (!await SaveAsync()) return;
        SetBusy(true);
        try
        {
            _project = await App.Api.RenameProjectAsync(_project.Id, dialog.Value);
            _draft = new(_project); Updated = true; FillFields(); await ShowCoverAsync();
            StatusText.Text = L.T("WorkInfo.Saved");
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { SetBusy(false); }
    }
    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", _project.Root_Path) { UseShellExecute = true }); }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_busy) { e.Cancel = true; return; }
        ReadFields();
        if (_draft.Changes().HasChanges && MessageBox.Show(this, L.T("WorkInfo.Discard"), L.T("WorkInfo.Title"),
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        { e.Cancel = true; return; }
        ++_coverRequest;
    }
}
