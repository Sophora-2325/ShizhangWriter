using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Input;
using System.Windows.Media;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Controls;
using NovelGm.Desktop.Models;
using Xunit;

namespace NovelGm.Desktop.Tests;

public class StackedConstraintTests
{
    static StackedConstraintTests() => System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(Application).TypeHandle);
    private static ChapterDto Chapter() => new()
    {
        No = 7, Title = "来信", Stacked_Constraints = new()
        {
            Tracks = new() { new() { Id = "t", Domain = "character", Element_Id = "c", Name = "林舟",
                Segments = new() { new() { Id = "a", Start = 10, End = 30 }, new() { Id = "b", Start = 80, End = 100 } } } },
        },
    };
    private static StackedConstraintSession Session(Func<StackedConstraintsDto, Task<ChapterDto>>? persist = null) =>
        new("project-one", Chapter(), new LoreDto(), persist ?? (value => Task.FromResult(new ChapterDto { Stacked_Constraints = value })));
    private static void Pump(Task task)
    {
        var dispatcher = Dispatcher.CurrentDispatcher; var frame = new DispatcherFrame();
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
    }

    [Fact]
    public void Geometry_clamps_to_canvas_without_changing_duration_or_source()
    {
        var source = new ConstraintSegmentDto { Start = 20, End = 40, Top = 10, Bottom = 50 };
        var moved = ConstraintLane.Translate(source, 200, -200);
        Assert.Equal(80, moved.Start); Assert.Equal(100, moved.End);
        Assert.Equal(0, moved.Top); Assert.Equal(40, moved.Bottom);
        Assert.Equal(20, source.Start); Assert.Equal(10, source.Top);
    }

    [Fact]
    public void Lane_arrow_keys_only_edit_the_selected_track_in_that_lane() => StaRunner.Run(() =>
    {
        using var session = Session();
        session.Select("t", "a");
        var otherLane = new ConstraintLane(session, "other");
        var source = new TestInputSource();
        otherLane.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Right) { RoutedEvent = Keyboard.KeyDownEvent });
        Assert.Equal(10, session.SelectedSegment!.Start);
        var ownLane = new ConstraintLane(session, "t");
        ownLane.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Right) { RoutedEvent = Keyboard.KeyDownEvent });
        Assert.Equal(11, session.SelectedSegment.Start);
    });
    private sealed class TestInputSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }

    [Fact]
    public void Shared_selection_is_not_a_write_and_history_restores_deleted_clips() => StaRunner.Run(() =>
    {
        using var session = Session();
        var notifications = 0; session.Changed += _ => notifications++;
        session.Select("t", "b", true);
        Assert.False(session.IsDirty); Assert.True(session.RevealSelection);
        session.DeleteSelection(); Assert.Single(session.Data.Tracks[0].Segments);
        session.Undo(); Assert.Equal(2, session.Data.Tracks[0].Segments.Count);
        session.Redo(); Assert.Single(session.Data.Tracks[0].Segments);
        Assert.True(notifications >= 4);
        session.ReadOnly = true; session.Undo(); session.Edit(d => d.Tracks.Clear(), true);
        Assert.Single(session.Data.Tracks); Assert.False(session.CanUndo);
    });

    [Fact]
    public void Save_acknowledges_only_its_snapshot_and_later_saves_are_ordered() => StaRunner.Run(() =>
    {
        var first = new TaskCompletionSource<ChapterDto>(); var received = new List<StackedConstraintsDto>();
        using var session = Session(value => { received.Add(value); return received.Count == 1 ? first.Task : Task.FromResult(new ChapterDto { Stacked_Constraints = value }); });
        session.Edit(d => d.Tracks[0].Weight = 0.2);
        var save = session.FlushAsync();
        Pump(Dispatcher.CurrentDispatcher.InvokeAsync(() => { }).Task);
        Assert.Single(received);
        session.Edit(d => d.Tracks[0].Weight = 0.9);
        first.SetResult(new ChapterDto { Stacked_Constraints = received[0] }); Pump(save);
        Assert.True(session.IsDirty); Assert.Equal(0.9, session.Data.Tracks[0].Weight);
        Pump(session.FlushAsync());
        Assert.Equal(new[] { 0.2, 0.9 }, received.Select(v => v.Tracks[0].Weight));
        Assert.False(session.HasPendingSave);
    });

    [Fact]
    public void Failed_save_keeps_draft_and_retries_same_chapter() => StaRunner.Run(() =>
    {
        var attempts = 0;
        using var session = Session(value => ++attempts == 1 ? Task.FromException<ChapterDto>(new Exception("offline")) : Task.FromResult(new ChapterDto { Stacked_Constraints = value }));
        session.Edit(d => d.Tracks[0].Weight = 0.1);
        var failed = session.FlushAsync(); Pump(failed); Assert.False(failed.Result);
        Assert.True(session.IsDirty); Assert.Equal("offline", session.Error);
        var retried = session.FlushAsync(); Pump(retried); Assert.True(retried.Result);
        Assert.Equal("project-one", session.ProjectId); Assert.Equal(7, session.ChapterNo);
        Assert.Equal(0.1, session.Data.Tracks[0].Weight); Assert.False(session.IsDirty);
    });

    [Fact]
    public void Categories_and_main_window_share_one_draft_and_selection() => StaRunner.Run(() =>
    {
        using var session = Session();
        var main = new StackedConstraintsWindow(session); var domain = new StackedConstraintsWindow(session, "character");
        Resources(main); Resources(domain);
        main.RefreshView(); domain.RefreshView();
        main.Measure(new Size(1280, 800)); main.Arrange(new Rect(0, 0, 1280, 800));
        domain.Measure(new Size(700, 600)); domain.Arrange(new Rect(0, 0, 700, 600));
        session.Select("t", "b", true);
        Assert.Same(main.Session, domain.Session); Assert.Equal("b", domain.Session.SelectedSegmentId);
        session.Edit(d => d.Tracks[0].Segments[1].Directive = "保守秘密");
        Assert.Equal("保守秘密", main.Session.SelectedSegment?.Directive);
        session.ReadOnly = true; Assert.False(main.SaveButton.IsEnabled); Assert.False(domain.SaveButton.IsEnabled);
        Pump(session.FlushAsync()); main.Close(); domain.Close();
    });

    [Fact]
    public void Plan_members_with_constraints_cannot_be_removed() => StaRunner.Run(() =>
    {
        var plan = new PlanZone { MemberLocked = (_, id) => id == "c" };
        plan.Load(new ChapterPlanDto { Present_Character_Ids = new() { "c" } }, new LoreDto());
        plan.RemoveSelection("characters", "c"); Assert.Contains("c", plan.Read().Present_Character_Ids);
        plan.MemberLocked = null; plan.RemoveSelection("characters", "c"); Assert.Empty(plan.Read().Present_Character_Ids);
    });

    [Fact]
    public void Overview_keeps_label_identity_and_places_all_leaders_below_labels() => StaRunner.Run(() =>
    {
        using var session = Session();
        var overview = new ConstraintOverview(session);
        overview.Resources.MergedDictionaries.Add(ThemeManager.CreatePalette(false, ThemeManager.Zhusha));
        overview.Resources.MergedDictionaries.Add(LanguageManager.LoadStrings("zh-CN"));
        overview.Measure(new Size(380, 500)); overview.Arrange(new Rect(0, 0, 380, 500)); overview.Render();
        var button = Assert.Single(overview.Children.OfType<Button>());
        session.Select("t", "a"); overview.Render();
        Assert.Same(button, Assert.Single(overview.Children.OfType<Button>()));
        session.Edit(d => d.Tracks[0].Weight = 1); overview.Render();
        Assert.Same(button, Assert.Single(overview.Children.OfType<Button>()));
        Assert.All(overview.Children.OfType<System.Windows.Shapes.Line>(), line => Assert.True(Panel.GetZIndex(line) < Panel.GetZIndex(button)));
    });

    [Theory]
    [InlineData("zh-CN")][InlineData("en-US")][InlineData("ja-JP")]
    public void New_dictionary_has_complete_localized_keys(string language) => StaRunner.Run(() =>
    {
        var reference = new ResourceDictionary { Source = new Uri("pack://application:,,,/novel-gm;component/Strings/Stack.zh-CN.xaml") };
        var translated = new ResourceDictionary { Source = new Uri($"pack://application:,,,/novel-gm;component/Strings/Stack.{language}.xaml") };
        Assert.Equal(reference.Keys.Cast<string>().OrderBy(k => k), translated.Keys.Cast<string>().OrderBy(k => k));
        Assert.All(translated.Values.Cast<string>(), value => Assert.False(string.IsNullOrWhiteSpace(value)));
    });

    [Fact]
    public void Structured_mismatch_preserves_plan_quote_and_clip_location()
    {
        const string payload = """
            {"detail":{"code":"PLAN_RHYTHM_MISMATCH","message":"调整后重试","project_id":"p","chapter_no":7,
            "issues":[{"plan_field":"free_text","plan_quote":"先争吵再来信","track_id":"t","segment_id":"s",
            "domain":"character","name":"林舟","start":10,"end":30,"reason":"先后相反"}]}}
            """;
        var error = Assert.IsType<RhythmMismatchException>(NovelApiClient.ParseError(payload, "409"));
        Assert.Equal("p", error.ProjectId); Assert.Equal(7, error.ChapterNo);
        var issue = Assert.Single(error.Issues);
        Assert.Equal("先争吵再来信", issue.Plan_Quote); Assert.Equal("s", issue.Segment_Id); Assert.Equal(30, issue.End);
        Assert.IsNotType<RhythmMismatchException>(NovelApiClient.ParseError("bad response", "503"));
    }

    [Theory]
    [InlineData("free_text")][InlineData("must_not")][InlineData("scene_goal")]
    public void Mismatch_navigation_selects_the_original_field_without_editing(string field) => StaRunner.Run(() =>
    {
        var plan = new ChapterPlanDto { Free_Text = "原句：先争吵再来信", Must_Not = new() { "先争吵再来信" }, Scene_Goal = "先争吵再来信" };
        var editor = new PlanZone(); editor.Load(plan, new LoreDto());
        var before = System.Text.Json.JsonSerializer.Serialize(editor.Read());
        editor.LocateField(field, "先争吵再来信");
        var box = field == "free_text" ? editor.FreeBox : field == "must_not" ? editor.MustNotBox : editor.SceneGoalBox;
        Assert.Equal("先争吵再来信", box.SelectedText);
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(editor.Read()));
    });

    [Theory]
    [InlineData("zh-CN", false)][InlineData("en-US", false)][InlineData("ja-JP", true)]
    public void Mismatch_window_handles_long_evidence_and_exposes_both_editing_entries(string language, bool dark) => StaRunner.Run(() =>
    {
        var window = new RhythmMismatchWindow(new[] { new RhythmIssueDto { Domain = "character", Name = new string('林', 100),
            Plan_Quote = new string('句', 500), Directive = "先来信再争吵", Reason = "明确的先后差异", Start = 10, End = 30 } }, (_, _) => Task.CompletedTask);
        window.Resources.MergedDictionaries.Add(ThemeManager.CreatePalette(dark, ThemeManager.Zhusha));
        window.Resources.MergedDictionaries.Add(LanguageManager.LoadStrings(language));
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(600, 500)); content.Arrange(new Rect(0, 0, 600, 500)); content.UpdateLayout();
        var buttons = Descendants<Button>(content).ToList();
        Assert.Contains(buttons, b => Equals(b.Content, L.T("Stack.EditPlan")));
        Assert.Contains(buttons, b => Equals(b.Content, L.T("Stack.EditRhythm")));
        Assert.Equal(500, Assert.Single(Descendants<TextBox>(content)).Text.Length);
        window.Close();
    });
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T item) yield return item;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static void Resources(Window window)
    {
        window.Resources.MergedDictionaries.Add(ThemeManager.CreatePalette(false, ThemeManager.Zhusha));
        window.Resources.MergedDictionaries.Add(LanguageManager.LoadStrings("zh-CN"));
    }
}
