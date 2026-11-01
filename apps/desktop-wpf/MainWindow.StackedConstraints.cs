using System.Windows;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Controls;
using NovelGm.Desktop.Models;

namespace NovelGm.Desktop;

public partial class MainWindow
{
    private readonly Dictionary<(string Project, int Chapter), StackedConstraintSession> _stackSessions = new();
    private readonly Dictionary<(string Project, int Chapter, string Domain), StackedConstraintsWindow> _stackWindows = new();
    private readonly HashSet<(string Project, int Chapter)> _stackReadOnly = new();
    private bool StacksPending => _stackSessions.Values.Any(s => s.HasPendingSave);
    private readonly HashSet<(string Project, int Chapter)> _weightNoticesShown = new();

    private async Task NotifyStackedWeightMigrationAsync()
    {
        if (_project is null) return;
        var projectId = _project.Id;
        var migrated = _chapters.Where(c => c.Stacked_Weight_Migration_Pending).ToList();
        var unseen = migrated.Where(c => !_weightNoticesShown.Contains((projectId, c.No))).ToList();
        if (unseen.Count > 0)
        {
            MessageBox.Show(this, L.T("Stack.WeightMigrated", string.Join(", ", unseen.Select(c => c.No))),
                L.T("Stack.Title"), MessageBoxButton.OK, MessageBoxImage.Information);
            foreach (var chapter in unseen) _weightNoticesShown.Add((projectId, chapter.No));
        }
        foreach (var chapter in migrated)
        {
            try
            {
                await App.Api.AcknowledgeStackedWeightMigrationAsync(projectId, chapter.No);
                chapter.Stacked_Weight_Migration_Pending = false;
            }
            catch { /* Keep the persisted notice pending; retry acknowledgment on the next load. */ }
        }
    }

    private void ShowRhythmMismatch(RhythmMismatchException error)
    {
        var window = new RhythmMismatchWindow(error.Issues, async (issue, stacked) =>
        {
            if (_busy) return;
            if (!await PrepareAiAsync()) return;
            if (_project?.Id != error.ProjectId || _selected?.No != error.ChapterNo)
                await Safe(() => LoadProjectAsync(error.ProjectId, error.ChapterNo));
            if (_project?.Id != error.ProjectId || _selected?.No != error.ChapterNo) return;
            if (stacked)
            {
                await OpenStackedConstraintsAsync();
                if (_stackSessions.TryGetValue((error.ProjectId, error.ChapterNo), out var session))
                    session.Select(issue.Track_Id, issue.Segment_Id, true);
            }
            else
            {
                RevealZone(Zone.Plan);
                PlanZoneView.LocateField(issue.Plan_Field, issue.Plan_Quote);
            }
        }) { Owner = this };
        window.Show();
    }

    private async Task OpenStackedConstraintsAsync()
    {
        if (_busy || _project is null || _selected is null) return;
        var projectId = _project.Id; var chapter = _selected;
        if (!await FlushEditsAsync() || _project?.Id != projectId || !ReferenceEquals(_selected, chapter)) return;
        var key = (projectId, chapter.No);
        if (!_stackSessions.TryGetValue(key, out var session))
        {
            StackedConstraintSession? created = null;
            created = new StackedConstraintSession(projectId, chapter, PlanZone.Catalog(_project, CurrentLore()),
                value => QueueStackedSave(created!, value));
            session = created; _stackSessions.Add(key, session);
            if (chapter.Status == "superseded") _stackReadOnly.Add(key);
            session.Changed += _ => RefreshStackedPlanLocks();
        }
        session.Catalog = PlanZone.Catalog(_project, CurrentLore());
        UpdateStackedReadOnly(); ShowStackedWindow(session, null);
    }

    private void ShowStackedWindow(StackedConstraintSession session, string? domain)
    {
        var key = (session.ProjectId, session.ChapterNo, domain ?? "");
        if (!_stackWindows.TryGetValue(key, out var window))
        {
            window = new StackedConstraintsWindow(session, domain) { Owner = this };
            var created = window;
            window.DomainRequested += requested => ShowStackedWindow(session, requested);
            window.Closed += (_, _) =>
            {
                if (_stackWindows.GetValueOrDefault(key) == created) _stackWindows.Remove(key);
            };
            _stackWindows[key] = window; window.Show();
        }
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private Task<ChapterDto> QueueStackedSave(StackedConstraintSession session, StackedConstraintsDto snapshot)
    {
        // All three chapter editors serialize through the main save queue.
        var previous = FlushEditsAsync();
        var operation = SaveStackedAfter(previous, session, snapshot);
        _saveTask = ObserveStackedSave(operation);
        return operation;
    }
    private static async Task<bool> ObserveStackedSave(Task<ChapterDto> operation)
    {
        try { await operation; return true; } catch { return false; }
    }
    private async Task<ChapterDto> SaveStackedAfter(Task<bool> previous, StackedConstraintSession session, StackedConstraintsDto snapshot)
    {
        if (!await previous && AnyDirty) throw new InvalidOperationException(L.T("Stack.SaveBlocked"));
        var saved = await App.Api.SaveStackedConstraintsAsync(session.ProjectId, session.ChapterNo, snapshot);
        if (_project?.Id == session.ProjectId)
        {
            var chapter = _chapters.FirstOrDefault(c => c.No == session.ChapterNo);
            if (chapter is not null)
            {
                chapter.Stacked_Constraints = saved.Stacked_Constraints;
                chapter.Plan = saved.Plan; chapter.Lore = saved.Lore; chapter.Plan_Check = saved.Plan_Check;
            }
            if (_selected?.No == session.ChapterNo)
            {
                if (saved.Plan is not null) PlanZoneView.MergeStackedMembers(saved.Plan, PlanZone.Catalog(_project, saved.Lore ?? CurrentLore()));
                RefreshStackedPlanLocks(); RenderLore();
            }
        }
        return saved;
    }

    private void RefreshStackedPlanLocks()
    {
        PlanZoneView.MemberLocked = (kind, id) =>
        {
            if (_project is null || _selected is null) return false;
            var domain = kind == "threads" ? "thread" : kind == "characters" ? "character" : "scene";
            var draft = _stackSessions.GetValueOrDefault((_project.Id, _selected.No));
            return _selected.Stacked_Constraints.Tracks.Concat(draft?.Data.Tracks ?? Enumerable.Empty<ConstraintTrackDto>())
                .Any(t => t.Domain == domain && t.Element_Id == id);
        };
        PlanZoneView.RefreshMemberLocks();
    }
    private static void PreserveStackedMembers(ChapterPlanDto plan, StackedConstraintsDto constraints)
    {
        foreach (var track in constraints.Tracks)
        {
            var list = track.Domain == "thread" ? plan.Required_Threads : track.Domain == "character" ? plan.Present_Character_Ids : plan.Scene_Types;
            if (!list.Contains(track.Element_Id)) list.Add(track.Element_Id);
        }
    }
    private void UpdateStackedReadOnly()
    {
        foreach (var pair in _stackSessions)
        {
            if (pair.Key.Project == _project?.Id && _chapters.FirstOrDefault(c => c.No == pair.Key.Chapter) is { } chapter)
            {
                if (chapter.Status == "superseded") _stackReadOnly.Add(pair.Key); else _stackReadOnly.Remove(pair.Key);
                pair.Value.ChapterTitle = chapter.Title;
                pair.Value.Catalog = PlanZone.Catalog(_project, chapter.Lore ?? CurrentLore());
            }
            pair.Value.ReadOnly = _busy || _stackReadOnly.Contains(pair.Key);
        }
        RefreshStackedPlanLocks();
    }
    private async Task<bool> FlushStackedAsync()
    {
        foreach (var session in _stackSessions.Values.ToList())
            while (session.HasPendingSave) if (!await session.FlushAsync()) return false;
        return true;
    }

    /// <summary>Chapter numbers can shift on insert/delete. Close bound views before changing identity.</summary>
    private async Task<bool> CloseStackedForProjectAsync(string projectId)
    {
        if (!await CloseProposalsForProjectAsync(projectId)) return false;
        var sessions = _stackSessions.Values.Where(s => s.ProjectId == projectId).ToList();
        foreach (var session in sessions) session.ReadOnly = true;
        foreach (var session in sessions)
            if (!await session.FlushAsync()) { UpdateStackedReadOnly(); return false; }
        foreach (var pair in _stackWindows.Where(p => p.Key.Project == projectId).ToList()) pair.Value.CloseSaved();
        if (_stackWindows.Keys.Any(k => k.Project == projectId)) return false;
        foreach (var pair in _stackSessions.Where(p => p.Key.Project == projectId).ToList())
        { pair.Value.Dispose(); _stackSessions.Remove(pair.Key); _stackReadOnly.Remove(pair.Key); }
        return true;
    }
}
