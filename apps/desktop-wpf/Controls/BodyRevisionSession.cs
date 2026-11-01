using System.Net.Http;
using System.Windows.Threading;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Models;

namespace NovelGm.Desktop.Controls;

internal sealed class BodyRevisionSession : IDisposable
{
    internal string ProjectId { get; }
    internal string ChapterUid { get; }
    internal int ChapterNo { get; }
    internal BodyRevisionViewDto Data { get; private set; }
    internal string Instruction { get; private set; }
    internal List<string> SelectedIssues { get; private set; }
    internal bool IsBusy { get; private set; }
    internal bool ExternalReadOnly { get; set; }
    internal bool ReadOnly => ExternalReadOnly || IsBusy || !Data.Writable;
    internal bool HasPendingSave => _version != _savedVersion || _saving;
    internal string? Error { get; private set; }
    internal Exception? LastException { get; private set; }
    internal event Action<bool>? Changed;
    internal event Action<BodyRevisionViewDto>? Saved;
    private long _version, _savedVersion;
    private bool _saving;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private readonly Func<string, BodyRevisionCommandDto, Task<BodyRevisionViewDto>> _operate;
    private readonly Func<Task<BodyRevisionViewDto>> _read;
    private (string Action, BodyRevisionCommandDto Command)? _retry;
    private long _retrySaveVersion;

    internal BodyRevisionSession(string projectId, BodyRevisionViewDto initial,
        Func<string, BodyRevisionCommandDto, Task<BodyRevisionViewDto>> operate, Func<Task<BodyRevisionViewDto>> read)
    {
        ProjectId = projectId; Data = initial; ChapterUid = initial.Chapter.Chapter_Uid; ChapterNo = initial.Chapter.No;
        Instruction = initial.Workspace.Instruction; SelectedIssues = initial.Workspace.Selected_Issues.ToList();
        _operate = operate; _read = read; _timer.Tick += Tick;
    }
    private async void Tick(object? sender, EventArgs e) { _timer.Stop(); await FlushAsync(); }
    internal void Edit(string instruction, IEnumerable<string> selected)
    {
        if (ReadOnly) return;
        var keys = selected.ToList();
        if (instruction == Instruction && keys.SequenceEqual(SelectedIssues)) return;
        Instruction = instruction; SelectedIssues = keys; _version++; Error = null; Data.Preview_Valid = false;
        _timer.Stop(); _timer.Start(); Changed?.Invoke(false);
    }
    internal BodyRevisionCommandDto Command() => new() { Chapter_Uid = ChapterUid,
        Expected_Revision = Data.Workspace.Revision, Input_Token = Data.Input_Token,
        Instruction = Instruction, Selected_Issues = SelectedIssues.ToList(), Preview_Id = Data.Workspace.Preview_Id };
    private void Accept(BodyRevisionViewDto response, bool inputs)
    {
        if (response.Chapter.Chapter_Uid != ChapterUid) throw new InvalidOperationException(L.T("Proposal.IdentityChanged"));
        Data = response;
        if (inputs) { Instruction = response.Workspace.Instruction; SelectedIssues = response.Workspace.Selected_Issues.ToList(); }
        Error = null; LastException = null; Saved?.Invoke(response); Changed?.Invoke(true);
    }
    private void Fail(Exception ex) { LastException = ex; Error = ex.Message; Changed?.Invoke(false); }
    internal async Task<bool> FlushAsync()
    {
        _timer.Stop(); await _gate.WaitAsync();
        try
        {
            while (_savedVersion != _version)
            {
                var version = _retry is { Action: "save" } ? _retrySaveVersion : _version;
                var command = _retry is { Action: "save" } retry ? retry.Command : Command();
                _saving = true;
                try
                {
                    var response = await _operate("save", command);
                    _retry = null; _savedVersion = version; Accept(response, version == _version);
                    if (_version != version) Data.Preview_Valid = false;
                }
                catch (Exception ex)
                {
                    _retry = ex is HttpRequestException or TaskCanceledException or ProposalException { Code: "REQUEST_IN_PROGRESS" }
                        ? ("save", command) : null;
                    _retrySaveVersion = version;
                    Fail(ex); return false;
                }
                finally { _saving = false; Changed?.Invoke(false); }
            }
            return true;
        }
        finally { _gate.Release(); }
    }
    internal async Task<bool> RefreshAsync()
    {
        if (IsBusy || !await FlushAsync()) return false;
        await _gate.WaitAsync();
        try { Accept(await _read(), true); return true; }
        catch (Exception ex) { Fail(ex); return false; }
        finally { _gate.Release(); }
    }
    internal async Task<bool> ExecuteAsync(string action, IEnumerable<string>? ids = null)
    {
        if (!Data.Writable || IsBusy || !await FlushAsync()) return false;
        await _gate.WaitAsync(); IsBusy = true; Error = null; Changed?.Invoke(false);
        try
        {
            var command = _retry is { } retry && retry.Action == action ? retry.Command : Command();
            if (_retry is null || _retry.Value.Action != action)
            {
                command.Patch_Ids = ids?.ToList() ?? new();
                if (action == "apply")
                {
                    if (!Data.Preview_Valid || Data.Chapter.Status == "final") throw new InvalidOperationException(L.T("Ai.Revision.Stale"));
                    var result = BodyRevisionEdits.Apply(Data.Chapter,
                        Data.Workspace.Patches.Where(p => command.Patch_Ids.Contains(p.Id)));
                    command.Body_Text = result.Text; command.Body_Rich = result.Rich;
                }
            }
            try { Accept(await _operate(action, command), true); _retry = null; return true; }
            catch (Exception ex)
            {
                _retry = ex is HttpRequestException or TaskCanceledException or ProposalException { Code: "REQUEST_IN_PROGRESS" }
                    ? (action, command) : null;
                throw;
            }
        }
        catch (Exception ex) { Fail(ex); return false; }
        finally { IsBusy = false; _gate.Release(); Changed?.Invoke(false); }
    }
    internal void Notify() => Changed?.Invoke(false);
    public void Dispose() { _timer.Stop(); _timer.Tick -= Tick; }
}
