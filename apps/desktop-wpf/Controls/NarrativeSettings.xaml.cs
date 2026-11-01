using System.Windows;
using System.Windows.Controls;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Models;

namespace NovelGm.Desktop.Controls;

/// <summary>人物页的作品默认叙事设置；独立于人物与章节快照。</summary>
public partial class NarrativeSettings : UserControl
{
    private (string Id, string Pov)? _snapshot;
    public event EventHandler? SaveRequested;
    public NarrativeSettings() => InitializeComponent();
    public bool IsReadOnly { set => PovBox.IsEnabled = SaveButton.IsEnabled = !value; }
    public void Load(ProjectDto? project)
    {
        if (project is null) { _snapshot = null; Form.IsEnabled = false; return; }
        if (_snapshot?.Id == project.Id && Changes().Count > 0) return;
        SettingChoices.Fill(PovBox, new[] { "third_limited", "first", "third_omniscient" }, project.Style?.Pov ?? "third_limited");
        Form.IsEnabled = true; MarkSaved(project.Id); StatusText.Text = "";
    }
    private void MarkSaved(string id) => _snapshot = (id, SettingChoices.Read(PovBox));
    public void MarkSaved() => MarkSaved(_snapshot?.Id ?? "");
    public List<BibleEditDto> Changes()
    {
        var changes = new List<BibleEditDto>();
        if (_snapshot is not { } before) return changes;
        if (before.Pov != SettingChoices.Read(PovBox)) changes.Add(new() { Path = "style.pov", New = SettingChoices.Read(PovBox) });
        return changes;
    }
    public void ShowStatus(string text) => StatusText.Text = text;
    private void Save_Click(object sender, RoutedEventArgs e) => SaveRequested?.Invoke(this, EventArgs.Empty);
}
