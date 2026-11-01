using System.Windows;
using System.Windows.Controls;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Models;

namespace NovelGm.Desktop;

public partial class LoreEntryEditorWindow : Window
{
    private readonly LoreEntryDto? _existing;

    public LoreEntryDto Result { get; private set; } = new();

    public LoreEntryEditorWindow(LoreEntryDto? existing)
    {
        InitializeComponent();
        _existing = existing;
        Title = existing is null
            ? L.T("Entry.TitleNew")
            : L.T("Entry.TitleEdit", existing.Display);
        TitleBox.Text = existing?.Title ?? "";
        ContentBox.Text = existing?.Content ?? "";
        HardCheck.IsChecked = existing?.Hard ?? false;
        Select(CategoryCombo, existing?.Category ?? "world");
        UpdateHardVisibility();
    }

    private static void Select(ComboBox combo, string tag)
    {
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag)
                             ?? combo.Items[0];
    }

    private static string SelectedTag(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? "world";

    private void UpdateHardVisibility() =>
        HardCheck.Visibility = SelectedTag(CategoryCombo) == "rule" ? Visibility.Visible : Visibility.Collapsed;

    private void Category_Changed(object sender, SelectionChangedEventArgs e) => UpdateHardVisibility();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var title = TitleBox.Text.Trim();
        var content = ContentBox.Text.Trim();
        if (title.Length == 0 && content.Length == 0)
        {
            MessageBox.Show(L.T("Entry.EmptyWarning"), L.T("App.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var category = SelectedTag(CategoryCombo);
        Result = new LoreEntryDto
        {
            Id = _existing?.Id ?? "",
            Category = category,
            Title = title,
            Content = content,
            Hard = category == "rule" && HardCheck.IsChecked == true,
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
