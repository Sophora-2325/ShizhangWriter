using System.IO;
using System.Windows;
using System.Windows.Controls;
using NovelGm.Desktop.Api;

namespace NovelGm.Desktop;

public partial class NewProjectWindow : Window
{
    public string ProjectTitle => TitleBox.Text.Trim();
    /// <summary>开篇背景文本；不叫 Background，免得遮蔽 Control.Background。</summary>
    public string OpeningBackground => BackgroundBox.Text.Trim();
    public string Sample => SampleBox.Text.Trim();
    public string ParentDir => ParentBox.Text.Trim();

    public NewProjectWindow(string defaultParent)
    {
        InitializeComponent();
        ParentBox.Text = defaultParent;
        UpdateHint();
    }

    private void PathInput_Changed(object sender, TextChangedEventArgs e)
    {
        if (IsInitialized) UpdateHint();
    }

    private void UpdateHint()
    {
        if (PathHint is null) return;
        PathHint.Text = string.IsNullOrWhiteSpace(ParentDir)
            ? L.T("New.PickLocationHint")
            : L.T("New.WillCreate", Path.Combine(ParentDir, string.IsNullOrWhiteSpace(ProjectTitle) ? L.T("Work.Untitled") : ProjectTitle));
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picked = FolderPicker.Pick(L.T("New.PickLocation"), this);
        if (picked is not null) ParentBox.Text = picked;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ProjectTitle))
        {
            MessageBox.Show(L.T("New.NeedTitle"), L.T("App.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(ParentDir) || !Path.IsPathRooted(ParentDir))
        {
            MessageBox.Show(L.T("New.NeedLocation"), L.T("App.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
