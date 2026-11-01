using System.Windows;
using NovelGm.Desktop.Api;

namespace NovelGm.Desktop;

public partial class RenameWindow : Window
{
    public string Value => NameBox.Text.Trim();

    public RenameWindow(string title, string prompt, string current, string hint = "")
    {
        InitializeComponent();
        Title = title;
        PromptLabel.Content = prompt;
        NameBox.Text = current;
        HintText.Text = hint;
        HintText.Visibility = string.IsNullOrEmpty(hint) ? Visibility.Collapsed : Visibility.Visible;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Value))
        {
            MessageBox.Show(L.T("Rename.Empty"), L.T("App.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
