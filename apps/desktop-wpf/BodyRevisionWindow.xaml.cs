using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Controls;

namespace NovelGm.Desktop;

public partial class BodyRevisionWindow : Window
{
    internal BodyRevisionSession Session { get; }
    private readonly Func<string, IEnumerable<string>?, Task> _action;
    private readonly HashSet<string> _selected = new();
    private string _previewId = "";
    private bool _syncing, _closing, _allowClose;
    internal BodyRevisionWindow(BodyRevisionSession session, Func<string, IEnumerable<string>?, Task> action)
    {
        Session = session; _action = action; InitializeComponent(); ProofDesk.Apply(this);
        Session.Changed += Changed; ThemeManager.ThemeChanged += ThemeChanged; LanguageManager.LanguageChanged += LanguageChanged;
        Closed += (_, _) => { Session.Changed -= Changed; ThemeManager.ThemeChanged -= ThemeChanged; LanguageManager.LanguageChanged -= LanguageChanged; };
        Loaded += (_, _) => { Width = Math.Min(Width, SystemParameters.WorkArea.Width); Height = Math.Min(Height, SystemParameters.WorkArea.Height); };
        PreviewKeyDown += async (_, e) => { if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; await Session.FlushAsync(); } };
        Render();
    }
    private void ThemeChanged(object? sender, EventArgs e) => ProofDesk.Apply(this);
    private void LanguageChanged(object? sender, EventArgs e) => Render();
    private void Changed(bool structure) { if (structure) Render(); else State(); }
    private static TextBlock Text(string value, bool muted = false)
    {
        var t = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 7) };
        t.SetResourceReference(TextBlock.ForegroundProperty, muted ? "Desk.Muted" : "Desk.Ink"); return t;
    }
    private void Render()
    {
        _syncing = true;
        try
        {
            Title = L.T("Ai.Revision.Title"); ChapterHeading.Text = $"{Session.ChapterNo} · {Session.Data.Chapter.Title}";
            if (Instruction.Text != Session.Instruction) Instruction.Text = Session.Instruction;
            Issues.Children.Clear();
            foreach (var issue in Session.Data.Issues)
            {
                var content = new StackPanel(); content.Children.Add(Text(issue.Point));
                if (issue.Evidence.Length > 0) content.Children.Add(Text(issue.Evidence, true));
                if (issue.Suggestion.Length > 0) content.Children.Add(Text(issue.Suggestion, true));
                var check = new CheckBox { Content = content, IsChecked = Session.SelectedIssues.Contains(issue.Id), Tag = issue.Id, Margin = new Thickness(0, 4, 0, 8) };
                AutomationProperties.SetName(check, issue.Point);
                check.Checked += IssueChanged; check.Unchecked += IssueChanged; Issues.Children.Add(check);
            }
            if (Session.Data.Issues.Count == 0) Issues.Children.Add(Text(L.T("Ai.Revision.NoIssues"), true));
            if (Session.SelectedIssues.Any(id => Session.Data.Issues.All(i => i.Id != id)))
            {
                Issues.Children.Add(Text(L.T("Ai.Revision.OldIssues"), true));
                var clear = new Button { Content = L.T("Ai.Revision.ClearIssues") };
                clear.Click += (_, _) => Session.Edit(Instruction.Text, Array.Empty<string>()); Issues.Children.Add(clear);
            }
            if (_previewId != Session.Data.Workspace.Preview_Id)
            { _selected.Clear(); foreach (var p in Session.Data.Workspace.Patches.Where(p => p.Unavailable.Length == 0)) _selected.Add(p.Id); _previewId = Session.Data.Workspace.Preview_Id; }
            Patches.Children.Clear();
            if (Session.Data.Workspace.Patches.Count == 0)
                Patches.Children.Add(Text(L.T(Session.Data.Workspace.Preview_Id.Length == 0 ? "Ai.Revision.Empty" : "Ai.Revision.NoEdits"), true));
            foreach (var patch in Session.Data.Workspace.Patches)
            {
                var content = new StackPanel();
                var check = new CheckBox { Content = Text(patch.Reason.Length > 0 ? patch.Reason : L.T("Ai.Revision.Patch")),
                    IsChecked = _selected.Contains(patch.Id), IsEnabled = patch.Unavailable.Length == 0 && !Session.ReadOnly };
                check.Checked += (_, _) => { _selected.Add(patch.Id); State(); };
                check.Unchecked += (_, _) => { _selected.Remove(patch.Id); State(); };
                content.Children.Add(check);
                AddProse(content, "Ai.Revision.Before", patch.Before, false);
                AddProse(content, "Ai.Revision.After", patch.After, true);
                if (patch.Unavailable.Length > 0) content.Children.Add(Text(L.T("Ai.Revision.Unavailable." + patch.Unavailable), true));
                var card = new Border { Child = content, Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 12), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3) };
                card.SetResourceReference(Border.BackgroundProperty, "Desk.Page"); card.SetResourceReference(Border.BorderBrushProperty, "Desk.Line"); Patches.Children.Add(card);
            }
        }
        finally { _syncing = false; State(); }
    }
    private static void AddProse(Panel parent, string label, string value, bool after)
    {
        parent.Children.Add(Text(L.T(label), true));
        var prose = Text(value); prose.FontFamily = new FontFamily("Cambria, SimSun"); prose.FontSize = 15;
        prose.Margin = new Thickness(0);
        var strip = new Border { Child = prose, Padding = new Thickness(9), Margin = new Thickness(0, 0, 0, 10), BorderThickness = new Thickness(1, 0, 0, 0) };
        strip.SetResourceReference(Border.BackgroundProperty, after ? "Desk.GreenTint" : "Desk.Tint");
        strip.SetResourceReference(Border.BorderBrushProperty, after ? "Desk.Green" : "Desk.Accent"); parent.Children.Add(strip);
    }
    private void State()
    {
        Instruction.IsReadOnly = Session.ReadOnly; Import.IsEnabled = !Session.ReadOnly;
        foreach (var item in Issues.Children.OfType<Control>()) item.IsEnabled = !Session.ReadOnly;
        Generate.IsEnabled = !Session.ReadOnly && (Session.Instruction.Trim().Length > 0 || Session.SelectedIssues.Count > 0);
        Apply.IsEnabled = !Session.ReadOnly && !Session.HasPendingSave && Session.Data.Preview_Valid && _selected.Count > 0 && Session.Data.Chapter.Status != "final";
        RetrySave.IsEnabled = !Session.IsBusy;
        Status.Text = Session.Error ?? (Session.IsBusy ? L.T("Ai.Busy.Progress") : Session.HasPendingSave ? L.T("Ai.Revision.Unsaved") :
            Session.Data.Chapter.Status == "final" ? L.T("Ai.Revision.Final") : !Session.Data.Preview_Valid && Session.Data.Workspace.Preview_Id.Length > 0 ? L.T("Ai.Revision.Stale") : L.T("Ai.Revision.Selected", _selected.Count));
    }
    private void InputChanged(object sender, TextChangedEventArgs e) { if (!_syncing) Session.Edit(Instruction.Text, Session.SelectedIssues); }
    private void IssueChanged(object sender, RoutedEventArgs e)
    { if (!_syncing) Session.Edit(Instruction.Text, Issues.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag)); }
    private void ImportClick(object sender, RoutedEventArgs e)
    {
        var notes = Session.Data.Chapter.Review?.User_Annotations ?? "";
        if (notes.Trim().Length > 0) Instruction.Text = Instruction.Text.TrimEnd() + (Instruction.Text.Length > 0 ? "\n\n" : "") + notes;
    }
    private async void GenerateClick(object sender, RoutedEventArgs e) => await _action("preview", null);
    private async void ApplyClick(object sender, RoutedEventArgs e) => await _action("apply", _selected.ToArray());
    private async void SaveClick(object sender, RoutedEventArgs e) => await Session.FlushAsync();
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    internal void CloseSaved() { _allowClose = true; Close(); }
    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closing || Session.IsBusy) return;
        _closing = true;
        try
        {
            if (await Session.FlushAsync())
            {
                // Even a completed save must leave the current Closing event first.
                await System.Windows.Threading.Dispatcher.Yield();
                _allowClose = true;
                Close();
            }
        }
        finally { _closing = false; }
    }
}
