using System.Windows;
using System.Windows.Controls;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Controls;
using NovelGm.Desktop.Models;
using Xunit;

namespace NovelGm.Desktop.Tests;

public class LoreMiscPreferenceTests
{
    static LoreMiscPreferenceTests() =>
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(Application).TypeHandle);

    [Fact]
    public void Preferences_keep_samples_folded_and_do_not_submit_hidden_fields() => Sta(() =>
    {
        EnsureStrings();
        var project = new ProjectDto
        {
            Id = "a",
            Forbidden = new() { "不要写死" },
            Npc_Auto_Act = true,
            Style = new StyleProfileDto
            {
                Sentence_Length = "medium",
                Diction = new() { "文言" },
                Punctuation_Policy = new() { ["dash"] = System.Text.Json.JsonDocument.Parse("\"keep\"").RootElement },
                Reference_Samples = new() { "甲\n\n乙，句。", "二", "三", "四" },
            },
        };
        var misc = new LoreMisc();
        misc.Load(project);
        Assert.False(((Button)misc.FindName("SaveButton")).IsEnabled);
        Assert.Contains("使用内置默认", ((TextBlock)misc.FindName("BannedHeader")).Text);
        Assert.Contains("参与生成", misc.SampleCaption(0));
        Assert.Contains("仅保存", misc.SampleCaption(3));
        Assert.Contains("甲", misc.SampleCaption(0));
        Assert.NotEqual(Visibility.Visible, ((UIElement)misc.FindName("ExtraHost")).Visibility);
        Assert.Equal(Visibility.Collapsed, ((UIElement)misc.FindName("SamplesBody")).Visibility);

        ((RadioButton)misc.FindName("SentenceLong")).IsChecked = true;
        var changes = misc.Changes();
        Assert.Equal(new[] { "style.sentence_length" }, changes.Select(item => item.Path));
        Assert.Equal("long", changes[0].New);
        Assert.Null(misc.SamplePayload());
        Assert.DoesNotContain(changes, item => item.Path is "style.diction" or "style.punctuation_policy" or "npc_auto_act");

        ((Button)misc.FindName("AddSampleButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(misc.HasUnsaved);
        Assert.Null(misc.SamplePayload());
        misc.DiscardDraft();
        Assert.False(misc.HasUnsaved);
        Assert.Null(misc.SamplePayload());

        ((TextBox)misc.FindName("BannedBox")).Text = "";
        Assert.Contains("使用内置默认", ((TextBlock)misc.FindName("BannedHeader")).Text);
        misc.IsReadOnly = true;
        var banned = (TextBox)misc.FindName("BannedBox");
        Assert.True(banned.IsReadOnly);
        Assert.Equal("", banned.Text);
        Assert.False(((Button)misc.FindName("SaveButton")).IsEnabled);
    });

    [Fact]
    public void Same_work_reload_keeps_the_misc_draft() => Sta(() =>
    {
        EnsureStrings();
        var style = new StyleProfileDto { Sentence_Length = "medium" };
        var project = new ProjectDto { Id = "a", Style = style };
        var misc = new LoreMisc();
        misc.Load(project);
        ((TextBox)misc.FindName("ForbiddenBox")).Text = "草稿长句，带逗号";
        style.Sentence_Length = "long";
        misc.Load(project);
        Assert.Equal("草稿长句，带逗号", ((TextBox)misc.FindName("ForbiddenBox")).Text);
        Assert.Contains("forbidden", misc.Changes().Select(item => item.Path));
    });

    [Fact]
    public void Unchanged_read_only_flag_keeps_the_open_sample_editor() => Sta(() =>
    {
        EnsureStrings();
        var misc = new LoreMisc();
        misc.Load(new ProjectDto
        {
            Id = "a",
            Style = new StyleProfileDto { Sentence_Length = "medium", Reference_Samples = new() { "甲" } },
        });
        ((Button)misc.FindName("SampleEditButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        EditButton(misc).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var editor = SampleEditor(misc);
        editor.Text = "甲改";
        editor.Select(1, 1);
        misc.IsReadOnly = false;
        Assert.Same(editor, SampleEditor(misc));
        Assert.Equal(1, editor.SelectionStart);
        Assert.Equal(1, editor.SelectionLength);
        Assert.Equal("甲改", editor.Text);

        misc.IsReadOnly = true;
        Assert.Same(editor, SampleEditor(misc));
        Assert.True(editor.IsReadOnly);
        Assert.Equal("甲改", editor.Text);
        Assert.Equal(1, editor.SelectionStart);
        Assert.Equal(1, editor.SelectionLength);
        misc.IsReadOnly = false;
        Assert.Same(editor, SampleEditor(misc));
        Assert.False(editor.IsReadOnly);
        misc.BeginSave();
        Assert.Same(editor, SampleEditor(misc));
        Assert.True(editor.IsReadOnly);
        Assert.Equal(1, editor.SelectionStart);
        misc.FailSave();
        Assert.Same(editor, SampleEditor(misc));
        Assert.False(editor.IsReadOnly);
        Assert.Equal("甲改", editor.Text);
        Assert.Equal(1, editor.SelectionLength);
    });

    [Fact]
    public void Saved_status_survives_same_work_reload() => Sta(() =>
    {
        EnsureStrings();
        var style = new StyleProfileDto { Sentence_Length = "medium" };
        var project = new ProjectDto { Id = "a", Style = style };
        var misc = new LoreMisc();
        misc.Load(project);
        ((RadioButton)misc.FindName("SentenceLong")).IsChecked = true;
        style.Sentence_Length = "long";
        misc.MarkSaved();
        misc.Load(project);
        Assert.Equal("已保存", ((TextBlock)misc.FindName("StatusText")).Text);
        Assert.True(((RadioButton)misc.FindName("SentenceLong")).IsChecked);
        Assert.False(misc.HasUnsaved);

        misc.Load(new ProjectDto { Id = "b", Style = new StyleProfileDto { Sentence_Length = "medium" } });
        Assert.Equal("", ((TextBlock)misc.FindName("StatusText")).Text);
    });

    private static Button EditButton(LoreMisc misc)
    {
        var row = (Panel)((Panel)misc.FindName("SampleList")).Children[0];
        return row.Children.OfType<Panel>().SelectMany(panel => panel.Children.OfType<Button>()).First();
    }

    private static TextBox SampleEditor(LoreMisc misc)
    {
        var row = (Panel)((Panel)misc.FindName("SampleList")).Children[0];
        return row.Children.OfType<TextBox>().Single();
    }

    private static void EnsureStrings()
    {
        var app = Application.Current;
        if (app is null)
        {
            app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        }
        if (app.TryFindResource("Misc.Builtin") is string) return;
        app.Resources.MergedDictionaries.Add(LanguageManager.LoadStrings("zh-CN"));
    }

    private static void Sta(Action action) => StaRunner.Run(action, TimeSpan.FromSeconds(20));
}
