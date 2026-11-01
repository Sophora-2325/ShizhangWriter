using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Models;

namespace NovelGm.Desktop;

/// <summary>Evidence and navigation only; edits stay in the two authoring surfaces.</summary>
internal sealed class RhythmMismatchWindow : Window
{
    private readonly IReadOnlyList<RhythmIssueDto> _issues;
    private readonly Func<RhythmIssueDto, bool, Task> _locate;
    internal RhythmMismatchWindow(IReadOnlyList<RhythmIssueDto> issues, Func<RhythmIssueDto, bool, Task> locate)
    {
        _issues = issues; _locate = locate;
        Width = 840; Height = 680; MinWidth = 560; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(StyleProperty, typeof(Window));
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        LanguageManager.LanguageChanged += LanguageChanged;
        Closed += (_, _) => LanguageManager.LanguageChanged -= LanguageChanged;
        Render();
    }
    private void LanguageChanged(object? sender, EventArgs e) => Render();
    private static TextBlock Text(string text, double size = 13) => new()
        { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = size, Margin = new Thickness(0, 0, 0, 8) };
    private void Render()
    {
        Title = L.T("Stack.MismatchTitle");
        var root = new DockPanel { Margin = new Thickness(22, 18, 22, 18) }; Content = root;
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var heading = Text(Title, 20); heading.FontWeight = FontWeights.SemiBold; header.Children.Add(heading);
        header.Children.Add(Text(L.T("Stack.MismatchHint")));
        var close = new Button { Content = L.T("Common.Close"), HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(0, 14, 0, 0) };
        close.Click += (_, _) => Close(); DockPanel.SetDock(close, Dock.Bottom); root.Children.Add(close);
        var items = new StackPanel();
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = items });
        foreach (var issue in _issues)
        {
            var item = new StackPanel { Margin = new Thickness(0, 16, 10, 10) };
            var name = issue.Domain == "scene" && SceneTypes.All.Contains(issue.Element_Id)
                ? L.T("Scene." + issue.Element_Id) : issue.Name;
            var label = Text($"{L.T(StackedConstraintsWindow.DomainKey(issue.Domain))} · {name} · {issue.Start:0.#}–{issue.End:0.#}%", 15);
            label.FontWeight = FontWeights.SemiBold; item.Children.Add(label);
            item.Children.Add(Text(L.T("Stack.PlanQuote")));
            item.Children.Add(new TextBox { Text = issue.Plan_Quote, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true, MaxHeight = 160, Padding = new Thickness(10),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 0, 10) });
            item.Children.Add(Text(L.T("Stack.SegmentDirection", string.IsNullOrWhiteSpace(issue.Directive) ? L.T("Stack.PresenceDirection") : issue.Directive)));
            item.Children.Add(Text(L.T("Stack.MismatchReason", issue.Reason)));
            var actions = new WrapPanel(); item.Children.Add(actions);
            foreach (var stacked in new[] { false, true })
            {
                var button = new Button { Content = L.T(stacked ? "Stack.EditRhythm" : "Stack.EditPlan"),
                    Margin = new Thickness(0, 0, 8, 6), Padding = new Thickness(12, 6, 12, 6) };
                button.Click += async (_, _) => { button.IsEnabled = false; try { await _locate(issue, stacked); } finally { button.IsEnabled = true; } };
                actions.Children.Add(button);
            }
            var rule = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 0, 0, 16), Child = item };
            rule.SetResourceReference(Border.BorderBrushProperty, "BorderBrushKey");
            items.Children.Add(rule);
        }
    }
}
