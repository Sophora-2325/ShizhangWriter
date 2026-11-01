using System.Windows;
using System.Windows.Controls;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Models;

namespace NovelGm.Desktop;

/// <summary>新建或编辑一个 Skill；点保存时直接写到后端，成功后 DialogResult = true。
/// 传入 draft（例如 AI 提炼的草稿）时按新建处理，表单预先填好。</summary>
public partial class SkillEditorWindow : Window
{
    private readonly SkillDto? _skill;

    public SkillEditorWindow(SkillDto? skill, SkillDto? draft = null)
    {
        InitializeComponent();
        _skill = skill;
        var fill = skill ?? draft;
        if (skill is not null) SetResourceReference(TitleProperty, "Skill.EditorEdit");
        else if (draft is not null) SetResourceReference(TitleProperty, "Skill.EditorDraft");
        if (fill is not null)
        {
            NameBox.Text = fill.Name;
            DescriptionBox.Text = fill.Description;
            SourceBox.Text = fill.Source;
            InstructionsBox.Text = fill.Instructions;
        }
        foreach (var scene in SceneTypes.All)
        {
            var c = new CheckBox
            {
                Tag = scene,
                IsChecked = fill?.Scenes.Contains(scene) == true,
                Margin = new Thickness(0, 2, 14, 2),
            };
            c.SetResourceReference(ContentControl.ContentProperty, $"Scene.{scene}");
            ScenesPanel.Children.Add(c);
        }
        ScopeCombo.SelectedIndex = 0;
        foreach (ComboBoxItem item in ScopeCombo.Items)
        {
            if ((item.Tag as string) == fill?.Applies_To) ScopeCombo.SelectedItem = item;
        }
        Loaded += (_, _) => NameBox.Focus();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var dto = new SkillDto
        {
            Id = _skill?.Id ?? "",
            Name = NameBox.Text.Trim(),
            Description = DescriptionBox.Text.Trim(),
            Applies_To = (ScopeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "both",
            Scenes = ScenesPanel.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList(),
            Instructions = InstructionsBox.Text.Trim(),
            Source = SourceBox.Text.Trim(),
        };
        if (dto.Name.Length == 0)
        {
            ShowError(L.T("Skill.NeedName"));
            NameBox.Focus();
            return;
        }
        if (dto.Instructions.Length == 0)
        {
            ShowError(L.T("Skill.NeedInstructions"));
            InstructionsBox.Focus();
            return;
        }

        IsEnabled = false;
        try
        {
            if (_skill is null) await App.Api.CreateSkillAsync(dto);
            else await App.Api.UpdateSkillAsync(dto);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
