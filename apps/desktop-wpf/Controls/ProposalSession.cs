using System.Windows.Threading;
using System.Net.Http;
using System.Text.Json;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Models;

namespace NovelGm.Desktop.Controls;

/// <summary>One UID-bound draft and ordered answer queue, shared by proposal/history views.</summary>
internal sealed class ProposalSession : IDisposable
{
    internal string ProjectId { get; }
    internal string ProjectTitle { get; }
    internal string ChapterUid { get; }
    internal int ChapterNo { get; }
    internal ProposalViewDto Data { get; private set; }
    internal ProposalDto? Displayed => Data.Workspace.Draft ?? Data.Workspace.Adopted;
    internal bool HasDraft => Data.Workspace.Draft is not null;
    internal bool HasPendingSave => _answers.Count + _decisions.Count > 0 || OutlinePending || _saving;
    internal bool IsBusy { get; private set; }
    internal bool ReadOnly { get => _externalReadOnly || !Data.Writable || IsBusy; set { _externalReadOnly = value; Notify(false); } }
    internal string? Error { get; private set; }
    internal Exception? LastException { get; private set; }
    internal IReadOnlyList<ProposalIssueDto> Issues { get; private set; } = Array.Empty<ProposalIssueDto>();
    internal event Action<bool>? Changed;
    internal event Action<ProposalViewDto>? Saved;
    private bool _externalReadOnly, _saving;
    private long _version;
    private readonly Dictionary<string, (string Value, long Version)> _answers = new(), _decisions = new();
    private (string Value, long Version)? _outline;
    private bool OutlinePending => _outline is { Value.Length: > 0 };
    private readonly SemaphoreSlim _gate = new(1);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private readonly Func<ProposalCommandDto, Task<ProposalViewDto>> _save;
    private readonly Func<string, ProposalCommandDto, Task<ProposalViewDto>> _operate;
    private readonly Func<Task<ProposalViewDto>> _read;
    private ProposalCommandDto? _retry;
    private long _retryVersion;
    private (string Action, ProposalCommandDto Command)? _operationRetry;

    internal ProposalSession(string projectId, string projectTitle, ProposalViewDto initial,
        Func<ProposalCommandDto, Task<ProposalViewDto>> save,
        Func<string, ProposalCommandDto, Task<ProposalViewDto>> operate, Func<Task<ProposalViewDto>> read)
    {
        ProjectId = projectId; ProjectTitle = projectTitle; Data = initial;
        ChapterNo = initial.Chapter.No; ChapterUid = initial.Chapter.Chapter_Uid;
        _save = save; _operate = operate; _read = read; _timer.Tick += TimerTick;
    }

    private async void TimerTick(object? sender, EventArgs e) { _timer.Stop(); await FlushAsync(); }
    private void Notify(bool structure) => Changed?.Invoke(structure);
    internal ProposalCommandDto Command() => new() { Chapter_Uid = ChapterUid,
        Expected_Revision = Data.Workspace.Revision, Input_Token = Data.Input_Token };

    internal void EditAnswer(string id, string text, bool decision = false)
    {
        if (ReadOnly) return;
        var proposal = Displayed;
        if (proposal is null) return;
        var current = decision ? proposal.Author_Decisions.FirstOrDefault(d => d.Id == id)?.Answer
                               : proposal.Questions.FirstOrDefault(q => q.Id == id)?.Answer;
        if (current is null || current == text) return;
        var forked = !HasDraft;
        if (forked)
        {
            proposal = ForkLocalDraft(proposal);
        }
        if (decision)
        {
            var item = proposal.Author_Decisions.FirstOrDefault(x => x.Id == id);
            if (item is null) return;
            item.Answer = text;
        }
        else
        {
            var item = proposal.Questions.FirstOrDefault(x => x.Id == id);
            if (item is null) return;
            item.Answer = text;
        }
        // The UI never edits the adopted DTO. Before any local answer edit, fork its
        // display copy too; the actual server fork is revision-checked on save.
        (decision ? _decisions : _answers)[id] = (text, ++_version);
        Data.Answers_Changed = true; Error = null; Issues = Array.Empty<ProposalIssueDto>();
        _timer.Stop(); _timer.Start(); Notify(false);
    }

    internal void EditPlotOutline(string text)
    {
        if (ReadOnly) return;
        var proposal = Displayed;
        if (proposal is null) return;
        if (text.Length > 60000) text = text[..60000];
        // 空串不能写进方案（后端最短 1 字）；保留上一份有效剧情，避免界面清空后采用到旧稿。
        if (string.IsNullOrWhiteSpace(text)) return;
        if (proposal.Plot_Outline == text) return;
        if (!HasDraft) proposal = ForkLocalDraft(proposal);
        proposal.Plot_Outline = text;
        _outline = text.Length == 0 ? null : (text, ++_version);
        Data.Answers_Changed = true; Error = null; Issues = Array.Empty<ProposalIssueDto>();
        _timer.Stop(); _timer.Start(); Notify(false);
    }

    private ProposalDto ForkLocalDraft(ProposalDto source)
    {
        var draft = JsonSerializer.Deserialize<ProposalDto>(JsonSerializer.Serialize(source))!;
        draft.Adopted_At = null;
        Data.Workspace.Draft = draft;
        Data.Draft_Status = new() { State = "draft" };
        return draft;
    }

    internal async Task<bool> FlushAsync()
    {
        _timer.Stop();
        await _gate.WaitAsync();
        try
        {
            var rebased = false;
            while (_answers.Count + _decisions.Count > 0 || OutlinePending)
            {
                _saving = true; Notify(false);
                var version = _retry is not null ? _retryVersion : _version;
                var command = _retry ?? Command();
                if (_retry is null)
                {
                    command.Answers = _answers.ToDictionary(p => p.Key, p => p.Value.Value);
                    command.Decisions = _decisions.ToDictionary(p => p.Key, p => p.Value.Value);
                    if (_outline is { Value.Length: > 0 } outline) command.Plot_Outline = outline.Value;
                }
                try
                {
                    var response = await _save(command);
                    foreach (var key in _answers.Where(p => p.Value.Version <= version).Select(p => p.Key).ToList()) _answers.Remove(key);
                    foreach (var key in _decisions.Where(p => p.Value.Version <= version).Select(p => p.Key).ToList()) _decisions.Remove(key);
                    if (_outline is { } pending && pending.Version <= version) _outline = null;
                    _retry = null; Apply(response);
                }
                catch (Exception ex)
                {
                    if (ex is ProposalException { Code: "PROPOSAL_CHANGED" } && !rebased)
                    {
                        try
                        {
                            var latest = await _read(); var target = latest.Workspace.Draft ?? latest.Workspace.Adopted;
                            if (latest.Chapter.Chapter_Uid == ChapterUid && target is not null &&
                                _answers.Keys.All(id => target.Questions.Any(q => q.Id == id)) &&
                                _decisions.Keys.All(id => target.Author_Decisions.Any(d => d.Id == id)) &&
                                (!OutlinePending || target.Plot_Outline is not null))
                            {
                                _retry = null; rebased = true; Apply(latest); continue;
                            }
                        }
                        catch { /* Keep every unsaved answer in the current view. */ }
                    }
                    // HTTP failures have a persisted failure receipt; a fresh explicit
                    // retry uses a new id. A lost response keeps the exact request id.
                    _retry = ex is HttpRequestException or TaskCanceledException ? command : null;
                    _retryVersion = version;
                    Fail(ex); return false;
                }
                finally { _saving = false; Notify(false); }
            }
            return true;
        }
        finally { _gate.Release(); }
    }

    private void Apply(ProposalViewDto response)
    {
        if (response.Chapter.Chapter_Uid != ChapterUid) throw new InvalidOperationException(L.T("Proposal.IdentityChanged"));
        var old = Structure(Displayed);
        Data = response;
        // A concurrent confirmation may have consumed the server draft. Pending
        // local answers still belong to a new draft, never to its adopted snapshot.
        if (!HasDraft && Data.Workspace.Adopted is { } adopted &&
            (adopted.Questions.Any(q => _answers.ContainsKey(q.Id)) ||
             adopted.Author_Decisions.Any(d => _decisions.ContainsKey(d.Id)) ||
             OutlinePending))
            ForkLocalDraft(adopted);
        foreach (var question in Displayed?.Questions ?? new())
            if (_answers.TryGetValue(question.Id, out var edit)) question.Answer = edit.Value;
        foreach (var decision in Displayed?.Author_Decisions ?? new())
            if (_decisions.TryGetValue(decision.Id, out var edit)) decision.Answer = edit.Value;
        if (_outline is { } outline && Displayed is { } shown) shown.Plot_Outline = outline.Value;
        Error = null; LastException = null; Issues = Array.Empty<ProposalIssueDto>(); Saved?.Invoke(response);
        Notify(old != Structure(Displayed));
    }

    private static string Structure(ProposalDto? value) => value is null ? "" : JsonSerializer.Serialize(new {
        value.Plot_Outline,
        Questions = value.Questions.Select(q => new { q.Id, q.Question, q.Why, q.Options }),
        Decisions = value.Author_Decisions.Select(d => new { d.Id, d.Question })
    });

    internal async Task<bool> RefreshAsync()
    {
        if (HasPendingSave || IsBusy) return false;
        await _gate.WaitAsync();
        try
        {
            if (HasPendingSave || IsBusy) return false;
            Apply(await _read()); return true;
        }
        catch (Exception ex) { Fail(ex); return false; }
        finally { _gate.Release(); }
    }

    internal async Task<bool> ExecuteAsync(string action)
    {
        if (!Data.Writable || IsBusy || !await FlushAsync()) return false;
        await _gate.WaitAsync();
        IsBusy = true; Error = null; Issues = Array.Empty<ProposalIssueDto>(); Notify(false);
        try
        {
            var command = Command(); command.Mode = action == "answers" ? "answers" : "regenerate";
            if (_operationRetry is { } retry && retry.Action == action)
            {
                // Only the persisted receipt for this exact request can establish
                // success. A newer revision could belong to an unrelated save.
                // The server replays a completed receipt before checking old tokens.
                command = retry.Command;
            }
            try
            {
                Apply(await _operate(action, command)); _operationRetry = null; return true;
            }
            catch (Exception ex)
            {
                _operationRetry = ex is HttpRequestException or TaskCanceledException or ProposalException { Code: "REQUEST_IN_PROGRESS" }
                    ? (action, command) : null;
                throw;
            }
        }
        catch (Exception ex) { Fail(ex); return false; }
        finally { IsBusy = false; _gate.Release(); Notify(false); }
    }

    private void Fail(Exception ex)
    {
        Error = ex.Message;
        LastException = ex;
        Issues = ex is ProposalException proposal ? proposal.Issues : Array.Empty<ProposalIssueDto>();
        Notify(false);
    }

    public void Dispose() { _timer.Stop(); _timer.Tick -= TimerTick; }
}
