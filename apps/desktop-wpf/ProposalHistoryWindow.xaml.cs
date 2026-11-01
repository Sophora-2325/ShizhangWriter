using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Controls;
using NovelGm.Desktop.Models;

namespace NovelGm.Desktop;

public partial class ProposalHistoryWindow : Window
{
    internal ProposalSession Session { get; }
    private readonly Func<Task<List<ProposalHistoryDto>>> _list;
    private readonly Func<string, Task<ProposalRecordDto>> _read;
    private readonly Func<string, Task> _action;
    private bool _loading, _closing, _closeAllowed;
    private long _selection;
    private string? _error;
    internal ProposalHistoryWindow(ProposalSession session, Func<Task<List<ProposalHistoryDto>>> list,
        Func<string, Task<ProposalRecordDto>> read, Func<string, Task> action)
    {
        Session = session; _list = list; _read = read; _action = action; InitializeComponent();
        Session.Changed += Changed;
        PreviewKeyDown += async (_, e) => { if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; await Session.FlushAsync(); } };
        Loaded += async (_, _) => await ReloadAsync();
        Closed += (_, _) => Session.Changed -= Changed;
        Heading.Text = $"{Session.ProjectTitle} · {Session.ChapterNo} · {Session.Data.Chapter.Title}";
        UpdateState();
    }
    private void Changed(bool structure) => UpdateState();
    private void UpdateState()
    {
        var selected = Records.SelectedItem as ProposalHistoryDto;
        RestoreButton.IsEnabled = !Session.ReadOnly && !_loading && selected is not null;
        DeleteButton.IsEnabled = RestoreButton.IsEnabled && !IsProtected(selected!);
        DeleteButton.ToolTip = selected is not null && IsProtected(selected) ? L.T("Proposal.ProtectedHistory") : null;
        Status.Text = Session.Error ?? _error ?? (selected is not null && IsProtected(selected) ? L.T("Proposal.ProtectedHistory") : L.T("Proposal.HistoryHint"));
    }
    private bool IsProtected(ProposalHistoryDto record) => new[] { Session.Data.Workspace.Draft?.Active_History_Id,
        Session.Data.Workspace.Adopted?.Active_History_Id }.Contains(record.Record_Id);
    internal async Task ReloadAsync()
    {
        if (_loading) return;
        _loading = true; UpdateState();
        _error = null;
        try
        {
            var old = (Records.SelectedItem as ProposalHistoryDto)?.Record_Id;
            var rows = await _list(); Records.ItemsSource = rows;
            Records.SelectedItem = rows.FirstOrDefault(r => r.Record_Id == old) ?? rows.FirstOrDefault();
        }
        catch (Exception ex) { _error = ex.Message; }
        finally { _loading = false; UpdateState(); }
    }
    private async void Records_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selection = ++_selection;
        if (Records.SelectedItem is not ProposalHistoryDto row) { Preview.Text = ""; UpdateState(); return; }
        Preview.Text = L.T("Proposal.Working"); _error = null;
        try
        {
            var record = await _read(row.Record_Id);
            if (selection != _selection) return;
            var p = record.Proposal;
            var current = record.Record_Id == Session.Data.Workspace.Adopted?.Active_History_Id
                ? PlanProposalWindow.StatusText(Session.Data.Adopted_Status) : L.T("Proposal.RecordNotUsed");
            var text = new StringBuilder().AppendLine(L.T("Proposal.HistoricalState", p.Adopted_At is null
                ? L.T("Proposal.State.draft") : L.T("Proposal.WasAdopted")))
                .AppendLine(L.T("Proposal.CurrentState", current))
                .AppendLine($"{p.Provider} · {p.Model} · " + (p.Usage.Cost_Usd is { } cost ? $"${cost:0.####}" : L.T("Proposal.UnknownCost")))
                .AppendLine().AppendLine(p.Plot_Outline).AppendLine();
            foreach (var q in p.Questions) text.AppendLine(q.Question).AppendLine(q.Answer.Length == 0 ? L.T("Proposal.UnansweredSingle") : q.Answer).AppendLine();
            foreach (var d in p.Author_Decisions) text.AppendLine(d.Question).AppendLine(d.Answer).AppendLine();
            text.AppendLine(L.T("Proposal.Suggestions")).AppendLine(JsonSerializer.Serialize(p.Proposed_Plan, new JsonSerializerOptions { WriteIndented = true }));
            if (p.Source_Manifest.ValueKind == JsonValueKind.Object)
                text.AppendLine().AppendLine(L.T("Proposal.Sources")).AppendLine(JsonSerializer.Serialize(p.Source_Manifest, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            Preview.Text = text.ToString();
        }
        catch (Exception ex) { if (selection == _selection) _error = ex.Message; }
        UpdateState();
    }
    private async void Restore_Click(object sender, RoutedEventArgs e)
    { if (Records.SelectedItem is ProposalHistoryDto row) { await _action("restore:" + row.Record_Id); await ReloadAsync(); } }
    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Records.SelectedItem is not ProposalHistoryDto row || IsProtected(row)) return;
        if (MessageBox.Show(this, L.T("Proposal.DeleteConfirm"), L.T("Proposal.History"), MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await _action("delete:" + row.Record_Id); await ReloadAsync();
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ReloadAsync();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeAllowed || !Session.HasPendingSave) return;
        e.Cancel = true; if (_closing) return; _closing = true;
        try { if (await Session.FlushAsync()) { _closeAllowed = true; await Dispatcher.BeginInvoke(Close); } }
        finally { _closing = false; }
    }
    internal void CloseSaved() { _closeAllowed = true; Close(); }
}
