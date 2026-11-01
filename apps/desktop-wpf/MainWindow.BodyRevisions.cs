using System.Windows;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Controls;
using NovelGm.Desktop.Models;

namespace NovelGm.Desktop;

public partial class MainWindow
{
    private readonly Dictionary<(string Project, string Uid), BodyRevisionSession> _bodyRevisionSessions = new();
    private readonly Dictionary<(string Project, string Uid), BodyRevisionWindow> _bodyRevisionWindows = new();
    private bool BodyRevisionsPending => _bodyRevisionSessions.Values.Any(s => s.HasPendingSave || s.IsBusy);
    private async Task OpenBodyRevisionAsync()
    {
        if (_project is null || _selected is null || _busy || !await PrepareAiAsync()) return;
        var pid = _project.Id; var chapter = _selected; var key = (pid, chapter.Chapter_Uid);
        try
        {
            if (!_bodyRevisionSessions.TryGetValue(key, out var session))
            {
                var data = await App.Api.GetBodyRevisionAsync(pid, chapter.No);
                if (data.Chapter.Chapter_Uid != chapter.Chapter_Uid || _project?.Id != pid) return;
                session = new BodyRevisionSession(pid, data, (action, command) => action switch {
                    "save" => App.Api.SaveBodyRevisionAsync(pid, chapter.No, command),
                    "preview" => App.Api.PreviewBodyRevisionAsync(pid, chapter.No, command),
                    _ => App.Api.ApplyBodyRevisionAsync(pid, chapter.No, command)
                }, () => App.Api.GetBodyRevisionAsync(pid, chapter.No));
                _bodyRevisionSessions[key] = session;
            }
            else if (!await session.RefreshAsync()) { /* Keep the unsaved local draft visible. */ }
            UpdateBodyRevisionsReadOnly();
            if (!_bodyRevisionWindows.TryGetValue(key, out var window))
            {
                window = new BodyRevisionWindow(session, (action, ids) => RunBodyRevisionAsync(session, action, ids)) { Owner = this };
                window.Closed += (_, _) => _bodyRevisionWindows.Remove(key);
                _bodyRevisionWindows[key] = window; window.Show();
            }
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
        }
        catch (Exception ex) { ReportError(ex.Message); }
    }
    private async Task RunBodyRevisionAsync(BodyRevisionSession session, string action, IEnumerable<string>? ids)
    {
        if (_busy || session.ReadOnly || !await PrepareAiAsync()) return;
        if (!await session.RefreshAsync()) return;
        await RunStep(action == "apply" ? "Busy.Saving" : "Busy.Generating", async () =>
        {
            if (!await session.ExecuteAsync(action, ids)) return;
            if (action == "apply" && _project?.Id == session.ProjectId)
            {
                if (_selected?.Chapter_Uid == session.ChapterUid) await LoadProjectAsync(session.ProjectId, session.ChapterNo);
                else
                {
                    var index = _chapters.FindIndex(c => c.Chapter_Uid == session.ChapterUid);
                    if (index >= 0) _chapters[index] = session.Data.Chapter;
                }
            }
        });
        if (session.LastException is RhythmMismatchException mismatch) ShowRhythmMismatch(mismatch);
    }
    private void UpdateBodyRevisionsReadOnly()
    {
        foreach (var session in _bodyRevisionSessions.Values)
        {
            session.ExternalReadOnly = _busy || (_project?.Id == session.ProjectId &&
                _chapters.FirstOrDefault(c => c.Chapter_Uid == session.ChapterUid)?.Status == "superseded");
            session.Notify();
        }
    }
    private async Task<bool> FlushBodyRevisionsAsync()
    {
        foreach (var session in _bodyRevisionSessions.Values.ToList())
            if (session.IsBusy || !await session.FlushAsync()) return false;
        return true;
    }
    private async Task<bool> CloseBodyRevisionsForProjectAsync(string pid)
    {
        foreach (var session in _bodyRevisionSessions.Values.Where(s => s.ProjectId == pid))
            if (session.IsBusy || !await session.FlushAsync()) return false;
        foreach (var p in _bodyRevisionWindows.Where(p => p.Key.Project == pid).ToList()) p.Value.CloseSaved();
        foreach (var p in _bodyRevisionSessions.Where(p => p.Key.Project == pid).ToList()) { p.Value.Dispose(); _bodyRevisionSessions.Remove(p.Key); }
        return true;
    }
}
