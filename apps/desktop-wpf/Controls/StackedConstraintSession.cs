using System.Windows.Threading;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Models;

namespace NovelGm.Desktop.Controls;

internal enum StackedChange { Structure, Values, Selection, State }

/// <summary>One author draft, undo history and ordered save queue shared by every view of a chapter.</summary>
internal sealed class StackedConstraintSession : IDisposable
{
    internal string ProjectId { get; }
    internal int ChapterNo { get; }
    internal string ChapterTitle { get; set; }
    internal StackedConstraintsDto Data { get; private set; }
    internal LoreDto Catalog { get; set; }
    internal string? SelectedTrackId { get; private set; }
    internal string? SelectedSegmentId { get; private set; }
    internal bool RevealSelection { get; private set; }
    internal ConstraintTrackDto? SelectedTrack => Data.Tracks.FirstOrDefault(t => t.Id == SelectedTrackId);
    internal ConstraintSegmentDto? SelectedSegment => SelectedTrack?.Segments.FirstOrDefault(s => s.Id == SelectedSegmentId);
    private bool _readOnly;
    internal bool ReadOnly
    {
        get => _readOnly;
        set { if (_readOnly == value) return; _readOnly = value; Notify(StackedChange.State); }
    }
    internal string? Error { get; private set; }
    internal bool IsDirty => _version != _savedVersion;
    internal bool IsSaving => !_saveTail.IsCompleted;
    internal bool HasPendingSave => IsDirty || IsSaving;
    internal bool CanUndo => !ReadOnly && _undo.Count > 0;
    internal bool CanRedo => !ReadOnly && _redo.Count > 0;
    internal event Action<StackedChange>? Changed;
    private readonly Func<StackedConstraintsDto, Task<ChapterDto>> _persist;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private readonly Stack<StackedConstraintsDto> _undo = new(), _redo = new();
    private long _version, _savedVersion, _queuedVersion = -1;
    private Task<bool> _saveTail = Task.FromResult(true);
    private string? _coalesceKey;
    private bool _disposed;

    internal StackedConstraintSession(string projectId, ChapterDto chapter, LoreDto catalog,
        Func<StackedConstraintsDto, Task<ChapterDto>> persist)
    {
        ProjectId = projectId; ChapterNo = chapter.No; ChapterTitle = chapter.Title;
        Data = chapter.Stacked_Constraints.Copy(); Catalog = catalog; _persist = persist;
        _timer.Tick += TimerTick;
    }

    private async void TimerTick(object? sender, EventArgs e) { _timer.Stop(); await FlushAsync(); }
    private void Notify(StackedChange kind) => Changed?.Invoke(kind);

    internal void Select(string? track, string? segment = null, bool reveal = false)
    {
        SelectedTrackId = track; SelectedSegmentId = segment; RevealSelection = reveal; _coalesceKey = null;
        Notify(StackedChange.Selection);
    }

    internal void Edit(Action<StackedConstraintsDto> action, bool structure = false, string? coalesceKey = null)
    {
        if (ReadOnly) return;
        var before = Data.Copy(); action(Data);
        if (before.Fingerprint() == Data.Fingerprint()) return;
        if (coalesceKey is null || coalesceKey != _coalesceKey) _undo.Push(before);
        _coalesceKey = coalesceKey; _redo.Clear(); MarkChanged(structure);
    }

    private void MarkChanged(bool structure)
    {
        _version++; Error = null; _timer.Stop(); _timer.Start();
        if (SelectedTrack is null) { SelectedTrackId = null; SelectedSegmentId = null; }
        else if (SelectedSegment is null) SelectedSegmentId = null;
        Notify(structure ? StackedChange.Structure : StackedChange.Values);
    }

    internal void Undo()
    {
        if (!CanUndo) return;
        _redo.Push(Data.Copy()); Data = _undo.Pop(); _coalesceKey = null; MarkChanged(true);
    }
    internal void Redo()
    {
        if (!CanRedo) return;
        _undo.Push(Data.Copy()); Data = _redo.Pop(); _coalesceKey = null; MarkChanged(true);
    }
    internal void DeleteSelection()
    {
        if (SelectedTrack is not { } track || SelectedSegment is not { } segment) return;
        Edit(_ => track.Segments.Remove(segment), true);
    }

    internal Task<bool> FlushAsync()
    {
        _timer.Stop();
        if (!IsDirty || (_queuedVersion == _version && IsSaving)) return _saveTail;
        var snapshot = Data.Copy(); var version = _version; _queuedVersion = version;
        _saveTail = SaveAfter(_saveTail, snapshot, version);
        Notify(StackedChange.State);
        return _saveTail;
    }
    private async Task<bool> SaveAfter(Task<bool> previous, StackedConstraintsDto snapshot, long version)
    {
        // Always yield once: callbacks can complete synchronously in tests and preview hosts.
        await Task.Yield(); await previous;
        try
        {
            var saved = await _persist(snapshot);
            _savedVersion = version; Error = null;
            if (_version == version)
                foreach (var track in Data.Tracks)
                    track.Name = saved.Stacked_Constraints.Tracks.FirstOrDefault(t => t.Id == track.Id)?.Name ?? track.Name;
            Notify(StackedChange.Values);
            return true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            if (_queuedVersion == version) _queuedVersion = -1;
            Notify(StackedChange.State);
            return false;
        }
        finally
        {
            if (!_disposed) _ = _timer.Dispatcher.BeginInvoke(() => Notify(StackedChange.State));
        }
    }

    internal string DisplayName(ConstraintTrackDto track) => track.Domain switch
    {
        "scene" when SceneTypes.All.Contains(track.Element_Id) => L.T("Scene." + track.Element_Id),
        "character" => Catalog.Characters.FirstOrDefault(c => c.Id == track.Element_Id)?.Name ?? track.Name,
        "thread" => Catalog.Threads.FirstOrDefault(t => t.Id == track.Element_Id)?.Name ?? track.Name,
        _ => track.Name,
    };

    public void Dispose() { _disposed = true; _timer.Stop(); _timer.Tick -= TimerTick; }
    internal bool IsDisposed => _disposed;
}
