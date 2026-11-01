using System.Windows;
using System.Windows.Input;
using NovelGm.Desktop.Api;
using Xunit;

namespace NovelGm.Desktop.Tests;

public class UxFeedbackTests
{
    [Fact]
    public void Old_save_completion_does_not_mark_new_input_saved()
    {
        var feedback = new SaveFeedback { Dirty = true };
        Assert.Equal("Save.StatePending", feedback.Key);
        feedback.Dirty = false; feedback.Queue();
        Assert.Equal("Save.StateSaving", feedback.Key);
        feedback.Dirty = true; feedback.Complete(true);
        Assert.Equal("Save.StatePending", feedback.Key);
        feedback.Dirty = false; feedback.Queue(); feedback.Complete(true);
        Assert.Equal("Save.StateSaved", feedback.Key);
    }

    [Fact]
    public void Queued_writes_all_finish_before_saved_and_contexts_stay_independent()
    {
        var first = new SaveFeedback(); var otherChapter = new SaveFeedback();
        first.Queue(); first.Queue(); first.Complete(true);
        Assert.Equal("Save.StateSaving", first.Key);
        Assert.Equal("Save.StateSaved", otherChapter.Key);
        first.Complete(true);
        Assert.Equal("Save.StateSaved", first.Key);
    }

    [Fact]
    public void Failure_is_visible_until_retry_succeeds_and_newer_input_is_kept()
    {
        var feedback = new SaveFeedback(); feedback.Queue();
        feedback.Dirty = true; feedback.Complete(false);
        Assert.Equal("Save.StateFailed", feedback.Key);
        Assert.True(feedback.Dirty);
        feedback.Dirty = false; feedback.Queue();
        Assert.Equal("Save.StateSaving", feedback.Key);
        feedback.Complete(true);
        Assert.Equal("Save.StateSaved", feedback.Key);
    }

    [Theory]
    [InlineData(Key.Left, ModifierKeys.Control, 0)]
    [InlineData(Key.Right, ModifierKeys.Control, 0)]
    [InlineData(Key.PageUp, ModifierKeys.Control, -1)]
    [InlineData(Key.PageDown, ModifierKeys.Control, 1)]
    [InlineData(Key.PageUp, ModifierKeys.None, 0)]
    [InlineData(Key.PageDown, ModifierKeys.Control | ModifierKeys.Shift, 0)]
    public void Only_control_page_keys_navigate_chapters(Key key, ModifierKeys modifiers, int direction)
        => Assert.Equal(direction, ChapterNavigation.Direction(key, modifiers));

    [Theory]
    [InlineData(false, 0)] [InlineData(true, 1)]
    public async Task Audit_confirmation_controls_request_dispatch(bool accepted, int expected)
    {
        var calls = 0;
        await AuditConsent.ExecuteAsync(() => accepted, () => { calls++; return Task.CompletedTask; });
        Assert.Equal(expected, calls);
    }

    [Theory]
    [InlineData(0)] [InlineData(100)] [InlineData(198)]
    public void Compact_snippet_keeps_the_match_visible_at_every_position(int at)
    {
        var source = new string('前', at) + "命中" + new string('后', 200 - at);
        var snippet = SearchSnippet.Create(source, at, 2, 22);
        Assert.InRange(snippet.Text.Length, 2, 24);
        Assert.Equal("命中", snippet.Text.Substring(snippet.MatchOffset, snippet.MatchLength));
        Assert.Equal("命中", source.Substring(at, 2));
    }

    [Fact]
    public void Snippet_handles_long_query_invalid_context_and_unicode()
    {
        Assert.Empty(SearchSnippet.Create("", -1, 0, 20).Text);
        var longQuery = SearchSnippet.Create(new string('字', 80), 0, 80, 12);
        Assert.Equal(12, longQuery.MatchLength);
        var emoji = SearchSnippet.Create("🌿🌿命中🌿🌿🌿🌿", 4, 2, 7);
        Assert.Equal("命中", emoji.Text.Substring(emoji.MatchOffset, emoji.MatchLength));
        Assert.False(char.IsLowSurrogate(emoji.Text[0]));
    }

    [Theory]
    [InlineData("zh-CN")] [InlineData("en-US")] [InlineData("ja-JP")]
    public void Feedback_labels_load_for_all_languages(string language) => StaRunner.Run(() =>
    {
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(Application).TypeHandle);
        var strings = LanguageManager.LoadStrings(language);
        foreach (var key in new[] { "Save.StatePending", "Save.StateSaving", "Save.StateSaved", "Save.StateFailed",
            "Save.StateTip", "Main.WordCountTip", "Read.EffectiveWordCount", "Read.EffectiveWordCountTip",
            "Fmt.ExitFocus", "Fmt.ExitFocusTip", "Skill.ViewSource", "Audit.RunTitle", "Menu.PreviousChapter", "Menu.NextChapter" })
            Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<string>(strings[key])), key);
    });
}
