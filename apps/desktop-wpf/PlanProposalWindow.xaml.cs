using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Controls;
using NovelGm.Desktop.Models;

namespace NovelGm.Desktop;

public partial class PlanProposalWindow : Window
{
    internal ProposalSession Session { get; }
    private readonly Func<string, Task> _action;
    private readonly Action _history;
    private readonly Func<ProposalEvidenceDto?, bool, Task> _locate;
    private readonly List<(TextBox Box, string Id, bool Decision)> _editors = new();
    private bool _closing, _closeAllowed, _syncing;

    internal PlanProposalWindow(ProposalSession session, Func<string, Task> action, Action history,
        Func<ProposalEvidenceDto?, bool, Task> locate)
    {
        Session = session; _action = action; _history = history; _locate = locate; InitializeComponent();
        Session.Changed += Changed; LanguageManager.LanguageChanged += LanguageChanged;
        Closed += (_, _) => { Session.Changed -= Changed; LanguageManager.LanguageChanged -= LanguageChanged; };
        Loaded += (_, _) => { Width = Math.Min(Width, SystemParameters.WorkArea.Width); Height = Math.Min(Height, SystemParameters.WorkArea.Height); };
        Render();
    }

    private void Changed(bool structure) { if (structure) Render(); else RefreshValues(); }
    private void LanguageChanged(object? sender, EventArgs e) => Render();
    private static TextBlock Text(string value, bool muted = false)
    {
        var block = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        block.SetResourceReference(TextBlock.ForegroundProperty, muted ? "MutedBrush" : "TextBrush"); return block;
    }
    internal static string StatusText(ProposalStatusDto status) => L.T("Proposal.State." + status.State) +
        (status.Reasons.Count == 0 ? "" : " · " + string.Join(" / ", status.Reasons.Select(r => L.T("Proposal.Reason." + r))));
    internal static string WritingStatusText(ProposalStatusDto status) => StatusText(status) +
        (status.State == "adopted" ? "" : " · " + L.T("Proposal.Fallback"));

    private void Render()
    {
        _syncing = true;
        try
        {
            Heading.Text = $"{Session.ProjectTitle} · {Session.ChapterNo} · {Session.Data.Chapter.Title}";
            Title = $"{L.T("Proposal.Title")} · {Session.ChapterNo}";
            var proposal = Session.Displayed;
            Outline.Text = proposal?.Plot_Outline ?? "";
            EmptyHint.Visibility = proposal is null ? Visibility.Visible : Visibility.Collapsed;
            OutlineLabel.Text = L.T(Session.HasDraft ? "Proposal.DraftOutline" : "Proposal.AdoptedOutline");
            QuestionsPanel.Children.Clear(); DecisionsPanel.Children.Clear(); _editors.Clear();
            if (proposal is not null)
            {
                foreach (var question in proposal.Questions)
                {
                    var group = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };
                    group.Children.Add(Text(question.Question));
                    if (!string.IsNullOrWhiteSpace(question.Why)) group.Children.Add(Text(question.Why, true));
                    var options = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
                    foreach (var option in question.Options)
                    {
                        var button = new Button { Content = new TextBlock { Text = option, TextWrapping = TextWrapping.Wrap },
                            MaxWidth = 400, Tag = question.Id, Margin = new Thickness(0, 0, 6, 6) };
                        button.Click += (_, _) => Session.EditAnswer(question.Id, option);
                        options.Children.Add(button);
                    }
                    group.Children.Add(options); AddEditor(group, question.Id, question.Question, question.Answer, false);
                    QuestionsPanel.Children.Add(group);
                }
                if (proposal.Questions.Count == 0) QuestionsPanel.Children.Add(Text(L.T("Proposal.NoQuestions"), true));
                foreach (var decision in proposal.Author_Decisions)
                {
                    var group = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
                    group.Children.Add(Text(decision.Question)); AddEditor(group, decision.Id, decision.Question, decision.Answer, true);
                    DecisionsPanel.Children.Add(group);
                }
            }
            DecisionsLabel.Visibility = proposal?.Author_Decisions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            RefreshValues();
        }
        finally { _syncing = false; }
    }

    private void AddEditor(Panel panel, string id, string label, string value, bool decision)
    {
        var box = new TextBox { Text = value, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
            MinHeight = 72, MaxHeight = 220, MaxLength = 4000, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(10) };
        AutomationProperties.SetName(box, label);
        box.TextChanged += (_, _) => { if (!_syncing) Session.EditAnswer(id, box.Text, decision); };
        _editors.Add((box, id, decision)); panel.Children.Add(box);
    }

    private void RefreshValues()
    {
        var wasSyncing = _syncing; _syncing = true;
        try
        {
            AdoptedState.Text = L.T("Proposal.BodyUses", WritingStatusText(Session.Data.Adopted_Status));
            OutlineLabel.Text = L.T(Session.HasDraft ? "Proposal.DraftOutline" : "Proposal.AdoptedOutline");
            DraftState.Text = L.T("Proposal.DraftState", StatusText(Session.Data.Draft_Status));
            var unanswered = Session.Displayed?.Questions.Count(q => string.IsNullOrWhiteSpace(q.Answer)) ?? 0;
            SaveState.Text = (Session.HasPendingSave ? L.T("Proposal.Saving") : L.T("Proposal.Saved")) + " · " + L.T("Proposal.Unanswered", unanswered);
            if (Session.IsBusy) SaveState.Text = L.T("Proposal.Working");
            ErrorText.Text = Session.Error ?? "";
            Progress.Visibility = Session.IsBusy ? Visibility.Visible : Visibility.Collapsed;
            GenerateButton.Content = L.T("Proposal.Generate");
            GenerateButton.IsEnabled = !Session.ReadOnly;
            Outline.IsReadOnly = Session.ReadOnly || Session.Displayed is null;
            var outline = Session.Displayed?.Plot_Outline ?? "";
            if (Outline.Text != outline) Outline.Text = outline;
            AnswersButton.IsEnabled = !Session.ReadOnly && (Session.Data.Answers_Changed || Session.HasPendingSave);
            ConfirmButton.IsEnabled = CanAdopt();
            HistoryButton.IsEnabled = !Session.IsBusy;
            RetryButton.Visibility = Session.Error is not null ? Visibility.Visible : Visibility.Collapsed;
            RetryButton.IsEnabled = !Session.IsBusy;
            foreach (var editor in _editors)
            {
                editor.Box.IsReadOnly = Session.ReadOnly;
                var text = editor.Decision ? Session.Displayed?.Author_Decisions.FirstOrDefault(d => d.Id == editor.Id)?.Answer
                                          : Session.Displayed?.Questions.FirstOrDefault(q => q.Id == editor.Id)?.Answer;
                if (text is not null && editor.Box.Text != text) editor.Box.Text = text;
            }
            foreach (var panel in QuestionsPanel.Children.OfType<StackPanel>())
                foreach (var option in panel.Children.OfType<WrapPanel>().SelectMany(p => p.Children.OfType<Button>())) option.IsEnabled = !Session.ReadOnly;
            DiffPanel.Children.Clear();
            if (Session.Data.Diff.Count == 0) DiffPanel.Children.Add(Text(L.T("Proposal.NoDiff"), true));
            foreach (var diff in Session.Data.Diff)
            {
                DiffPanel.Children.Add(Text(L.T("Proposal.Field." + diff.Field)));
                DiffPanel.Children.Add(Text(L.T("Proposal.Before", DisplayJson(diff.Before)), true));
                DiffPanel.Children.Add(Text(L.T("Proposal.After", DisplayJson(diff.After))));
            }
            IssuesPanel.Children.Clear();
            foreach (var issue in Session.Issues)
            {
                IssuesPanel.Children.Add(Text(issue.Reason));
                foreach (var evidence in new[] { issue.Left, issue.Right })
                {
                    IssuesPanel.Children.Add(Text($"“{evidence.Quote}”"));
                    var locate = new Button { Content = L.T("Proposal.Locate." + evidence.Kind), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) };
                    locate.Click += async (_, _) => await _locate(evidence, evidence.Kind == "stack");
                    IssuesPanel.Children.Add(locate);
                }
            }
        }
        finally { _syncing = wasSyncing; }
    }

    private static string DisplayJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? "", JsonValueKind.Null => "—",
        JsonValueKind.Array => string.Join(" · ", element.EnumerateArray().Select(e => e.ToString())), _ => element.ToString()
    };
    private bool CanAdopt() => !Session.ReadOnly && Session.HasDraft && Outline.Text.Trim().Length > 0;
    private void Outline_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_syncing) Session.EditPlotOutline(Outline.Text);
        ConfirmButton.IsEnabled = CanAdopt();
    }
    private async void Generate_Click(object sender, RoutedEventArgs e) => await _action("generate");
    private async void Answers_Click(object sender, RoutedEventArgs e) => await _action("answers");
    private async void Confirm_Click(object sender, RoutedEventArgs e) => await _action("confirm");
    private void History_Click(object sender, RoutedEventArgs e) => _history();
    private async void Retry_Click(object sender, RoutedEventArgs e) { if (!await Session.FlushAsync()) return; await Session.RefreshAsync(); }
    private async void Refresh_Click(object sender, RoutedEventArgs e) { if (await Session.FlushAsync()) await Session.RefreshAsync(); }
    private async void EditPlan_Click(object sender, RoutedEventArgs e) => await _locate(null, false);
    private async void EditRhythm_Click(object sender, RoutedEventArgs e) => await _locate(null, true);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; await Session.FlushAsync(); } }
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeAllowed || !Session.HasPendingSave) return;
        e.Cancel = true; if (_closing) return; _closing = true;
        try { if (await Session.FlushAsync()) { _closeAllowed = true; await Dispatcher.BeginInvoke(Close); } }
        finally { _closing = false; }
    }
    internal void CloseSaved() { _closeAllowed = true; Close(); }
    internal void Locate(ProposalEvidenceDto evidence)
    {
        if (evidence.Kind == "answer")
        {
            var id = evidence.Id.Split('.').Last();
            var editor = _editors.FirstOrDefault(e => e.Id == id).Box;
            if (editor is not null) { editor.BringIntoView(); editor.Focus(); editor.SelectAll(); }
        }
        else { Outline.BringIntoView(); Outline.Focus(); var at = Outline.Text.IndexOf(evidence.Quote, StringComparison.Ordinal); if (at >= 0) Outline.Select(at, evidence.Quote.Length); }
    }
}
