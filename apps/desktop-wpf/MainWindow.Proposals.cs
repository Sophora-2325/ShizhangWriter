using System.Windows;
using System.Windows.Controls;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Controls;
using NovelGm.Desktop.Models;

namespace NovelGm.Desktop;

public partial class MainWindow
{
    private readonly Dictionary<(string Project, string Uid), ProposalSession> _proposalSessions = new();
    private readonly Dictionary<(string Project, string Uid), PlanProposalWindow> _proposalWindows = new();
    private readonly Dictionary<(string Project, string Uid), ProposalHistoryWindow> _proposalHistoryWindows = new();
    private bool ProposalsPending => _proposalSessions.Values.Any(s => s.HasPendingSave || s.IsBusy);

    private async Task OpenProposalAsync()
    {
        if (_project is null || _busy) return;
        if (_selected is null) { ShowProposalStart(); return; }
        var pid = _project.Id; var chapter = _selected;
        try
        {
            var key = (pid, chapter.Chapter_Uid);
            if (!_proposalSessions.TryGetValue(key, out var session))
            {
                var data = await App.Api.GetProposalAsync(pid, chapter.No);
                if (_project?.Id != pid || _selected?.Chapter_Uid != chapter.Chapter_Uid) return;
                ProposalSession? created = null;
                created = new ProposalSession(pid, _project.Title, data, command => QueueProposalSave(created!, command),
                    (action, command) => action switch
                    {
                        "confirm" => App.Api.ConfirmProposalAsync(pid, chapter.No, command),
                        var a when a.StartsWith("restore:") => App.Api.RestorePlanRecordAsync(pid, chapter.No, a[8..], command),
                        var a when a.StartsWith("delete:") => App.Api.DeletePlanRecordAsync(pid, chapter.No, a[7..], command),
                        _ => App.Api.GenerateProposalAsync(pid, chapter.No, command)
                    }, () => App.Api.GetProposalAsync(pid, chapter.No));
                session = created;
                session.Saved += response => MergeProposalResponse(created, response);
                _proposalSessions[key] = session;
            }
            else
            {
                if (!await session.FlushAsync()) { ShowProposalWindow(session); return; }
                await session.RefreshAsync();
            }
            UpdateProposalsReadOnly(); ShowProposalWindow(session);
        }
        catch (Exception ex) { ReportError(ex.Message); }
    }

    private void ShowProposalWindow(ProposalSession session)
    {
        var key = (session.ProjectId, session.ChapterUid);
        if (!_proposalWindows.TryGetValue(key, out var window))
        {
            window = new PlanProposalWindow(session, action => RunProposalActionAsync(session, action),
                () => ShowProposalHistory(session), (evidence, stacked) => LocateProposalSourceAsync(session, evidence, stacked)) { Owner = this };
            window.Closed += (_, _) => _proposalWindows.Remove(key);
            _proposalWindows[key] = window; window.Show();
        }
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private void ShowProposalHistory(ProposalSession session)
    {
        var key = (session.ProjectId, session.ChapterUid);
        if (!_proposalHistoryWindows.TryGetValue(key, out var window))
        {
            window = new ProposalHistoryWindow(session, () => App.Api.ListPlansAsync(session.ProjectId, session.ChapterNo),
                id => App.Api.GetPlanRecordAsync(session.ProjectId, session.ChapterNo, id), action => RunProposalActionAsync(session, action)) { Owner = this };
            window.Closed += (_, _) => _proposalHistoryWindows.Remove(key);
            _proposalHistoryWindows[key] = window; window.Show();
        }
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private Task<ProposalViewDto> QueueProposalSave(ProposalSession session, ProposalCommandDto command)
    {
        var previous = FlushEditsAsync();
        var operation = SaveProposalAfter(previous, session, command);
        _saveTask = ObserveProposalSave(operation); return operation;
    }
    private static async Task<bool> ObserveProposalSave(Task<ProposalViewDto> operation)
    { try { await operation; return true; } catch { return false; } }
    private async Task<ProposalViewDto> SaveProposalAfter(Task<bool> previous, ProposalSession session, ProposalCommandDto command)
    {
        if (!await previous && AnyDirty) throw new InvalidOperationException(L.T("Stack.SaveBlocked"));
        return await App.Api.SaveProposalAnswersAsync(session.ProjectId, session.ChapterNo, command);
    }

    private void MergeProposalResponse(ProposalSession session, ProposalViewDto response)
    {
        if (_project?.Id != session.ProjectId) return;
        var chapter = _chapters.FirstOrDefault(c => c.Chapter_Uid == session.ChapterUid);
        if (chapter is null) return;
        chapter.Proposal = response.Workspace;
        if (response.Operation == "confirm")
        {
            chapter.Plan = response.Chapter.Plan; chapter.Lore = response.Chapter.Lore;
            chapter.Plan_Check = null;
            if (_selected?.Chapter_Uid == session.ChapterUid && !_planDirty)
            { PlanZoneView.Load(chapter.Plan, PlanZone.Catalog(_project, chapter.Lore ?? CurrentLore())); RefreshStackedPlanLocks(); RenderLore(); }
        }
        if (_selected?.Chapter_Uid == session.ChapterUid)
            AiPanelView.SetProposalStatus(PlanProposalWindow.WritingStatusText(response.Adopted_Status), response.Workspace.Draft is not null);
    }

    private async Task RunProposalActionAsync(ProposalSession session, string action)
    {
        if (_busy || session.ReadOnly || !await PrepareAiAsync()) return;
        if (!await session.RefreshAsync()) return;
        await RunStep(action == "confirm" ? "Busy.Auditing" : action.Contains(':') ? "Busy.Saving" : "Busy.Planning", async () =>
        {
            if (await session.ExecuteAsync(action) && action.StartsWith("restore:")) ShowProposalWindow(session);
        });
        if (session.LastException is RhythmMismatchException mismatch) ShowRhythmMismatch(mismatch);
    }

    private async Task LocateProposalSourceAsync(ProposalSession session, ProposalEvidenceDto? evidence, bool stacked)
    {
        if (_busy || !await PrepareAiAsync()) return;
        if (_project?.Id != session.ProjectId || _selected?.Chapter_Uid != session.ChapterUid)
            await Safe(() => LoadProjectAsync(session.ProjectId, session.ChapterNo));
        if (_selected?.Chapter_Uid != session.ChapterUid) return;
        if (stacked)
        {
            await OpenStackedConstraintsAsync();
            if (evidence is not null && _stackSessions.TryGetValue((session.ProjectId, session.ChapterNo), out var stack))
                stack.Select(evidence.Track_Id, evidence.Segment_Id, true);
        }
        else if (evidence?.Kind is "answer" or "proposal")
        {
            ShowProposalWindow(session);
            _proposalWindows[(session.ProjectId, session.ChapterUid)].Locate(evidence);
        }
        else if (evidence?.Kind == "rule")
        {
            ShowRightTab(RightTab.Lore); SetLorePaneVisible(true); Activate();
            if (evidence.Id.StartsWith("rule."))
            {
                LoreTabs.SelectedIndex = 2;
                var entry = CurrentLore().Entries.FirstOrDefault(e => e.Id == evidence.Id[5..]);
                if (entry is not null) EditEntry(entry, evidence.Quote);
            }
            else if (evidence.Id.StartsWith("forbidden."))
            {
                LoreTabs.SelectedIndex = 3;
                LoreMiscView.RevealForbidden(evidence.Quote);
            }
            else { RevealZone(Zone.Plan); PlanZoneView.LocateField("open_decision_for_user", ""); }
        }
        else if (evidence?.Kind == "action")
        {
            ShowRightTab(RightTab.Lore); SetLorePaneVisible(true); LoreTabs.SelectedIndex = 0; Activate();
            var id = evidence.Id.StartsWith("action.") ? evidence.Id[7..] : "";
            var character = CurrentLore().Characters.FirstOrDefault(c => c.Id == id);
            if (character is not null) EditCharacter(character, locateAction: true);
        }
        else { Activate(); RevealZone(Zone.Plan); if (evidence is not null) PlanZoneView.LocateField(evidence.Field, evidence.Quote); }
    }

    private void ShowProposalStart()
    {
        if (_project is null) return;
        var pid = _project.Id;
        var window = new Window { Owner = this, Title = L.T("Proposal.Title"), Width = 620, Height = 280,
            MinWidth = 420, MinHeight = 240, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = L.T("Proposal.CreateHint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 24) });
        var create = new Button { Content = L.T("Proposal.Create"), HorizontalAlignment = HorizontalAlignment.Left };
        create.SetResourceReference(StyleProperty, "PrimaryButton"); panel.Children.Add(create);
        var close = new Button { Content = L.T("Common.Close"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        close.Click += (_, _) => window.Close(); panel.Children.Add(close); window.Content = panel;
        create.Click += async (_, _) =>
        {
            if (_busy || _project?.Id != pid) return;
            create.IsEnabled = false;
            await RunStep("Busy.Planning", async () =>
            {
                if (_selected is null)
                {
                    var chapter = await App.Api.CreateChapterAsync(pid, "", "", PlanZoneView.Read());
                    await LoadProjectAsync(pid, chapter.No);
                }
            });
            if (_project?.Id == pid && _selected is not null) { window.Close(); await OpenProposalAsync(); }
            else create.IsEnabled = true;
        };
        window.Show();
    }

    private void UpdateProposalsReadOnly()
    {
        UpdateBodyRevisionsReadOnly();
        foreach (var session in _proposalSessions.Values)
            session.ReadOnly = _busy || (_project?.Id == session.ProjectId && _chapters.FirstOrDefault(c => c.Chapter_Uid == session.ChapterUid)?.Status == "superseded");
    }
    private async Task<bool> FlushProposalsAsync()
    {
        foreach (var session in _proposalSessions.Values.ToList()) if (!await session.FlushAsync()) return false;
        return true;
    }
    private async Task RefreshProposalStateAsync()
    {
        if (_busy || _project is null || _selected is null) { if (_selected is null) AiPanelView.SetProposalStatus(null); return; }
        var key = (Project: _project.Id, Uid: _selected.Chapter_Uid);
        if (_proposalSessions.TryGetValue(key, out var session)) { await session.RefreshAsync(); return; }
        var no = _selected.No;
        try
        {
            var data = await App.Api.GetProposalAsync(key.Project, no);
            if (_project?.Id == key.Project && _selected?.Chapter_Uid == key.Uid)
                AiPanelView.SetProposalStatus(PlanProposalWindow.WritingStatusText(data.Adopted_Status), data.Workspace.Draft is not null);
        }
        catch { /* Normal editor operation must remain available when status refresh fails. */ }
    }
    private async Task<bool> CloseProposalsForProjectAsync(string pid)
    {
        if (!await CloseBodyRevisionsForProjectAsync(pid)) return false;
        foreach (var session in _proposalSessions.Values.Where(s => s.ProjectId == pid))
            if (session.IsBusy || !await session.FlushAsync()) return false;
        foreach (var p in _proposalWindows.Where(p => p.Key.Project == pid).ToList()) p.Value.CloseSaved();
        foreach (var p in _proposalHistoryWindows.Where(p => p.Key.Project == pid).ToList()) p.Value.CloseSaved();
        foreach (var p in _proposalSessions.Where(p => p.Key.Project == pid).ToList()) { p.Value.Dispose(); _proposalSessions.Remove(p.Key); }
        return true;
    }
}
