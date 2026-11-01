using System.Windows;
using System.Windows.Controls;
using NovelGm.Desktop.Api;

namespace NovelGm.Desktop.Controls;

internal sealed record PlanChoice(string Id, string Name);

/// <summary>选择器的勾选只在确认时提交，取消和新建均不修改原列表。</summary>
internal sealed class PlanSelectionWindow : Window
{
    private readonly List<CheckBox> _checks = new();
    public IReadOnlyList<string> SelectedIds => _checks.Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();
    public bool CreateNew { get; private set; }
    internal IReadOnlyList<CheckBox> Checks => _checks;
    public PlanSelectionWindow(string title, IReadOnlyList<PlanChoice> choices, IReadOnlyList<string> selected, bool allowCreate)
    {
        Title = title; Width = 440; Height = 520; MinWidth = 320; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "BgBrush"); SetResourceReference(ForegroundProperty, "TextBrush");
        var root = new DockPanel { Margin = new Thickness(16) }; Content = root;
        var footer = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        if (allowCreate)
        {
            var create = new Button { Content = L.T("Plan.CreateNew"), Padding = new Thickness(12, 6, 12, 6) };
            create.Click += (_, _) => { CreateNew = true; DialogResult = false; }; footer.Children.Add(create);
        }
        var cancel = new Button { Content = L.T("Common.Cancel"), IsCancel = true, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(6, 0, 0, 0) };
        cancel.Click += (_, _) => DialogResult = false; footer.Children.Add(cancel);
        var add = new Button { Content = L.T("Plan.JoinChapter"), IsDefault = true, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(6, 0, 0, 0) };
        add.Click += (_, _) => DialogResult = true; footer.Children.Add(add);
        var list = new StackPanel(); root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        foreach (var item in choices.GroupBy(c => c.Id).Select(g => g.First()))
        {
            var check = new CheckBox { Content = new TextBlock { Text = item.Name, TextWrapping = TextWrapping.Wrap }, Tag = item.Id, Margin = new Thickness(2, 9, 2, 9), IsEnabled = !selected.Contains(item.Id), IsChecked = selected.Contains(item.Id) };
            _checks.Add(check); list.Children.Add(check);
        }
        if (_checks.Count == 0) list.Children.Add(new TextBlock { Text = L.T("Plan.NoCandidates"), TextWrapping = TextWrapping.Wrap });
    }
}
