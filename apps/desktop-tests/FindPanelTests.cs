using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Controls;
using NovelGm.Desktop.Models;
using Xunit;

namespace NovelGm.Desktop.Tests;

public class FindPanelTests
{
    [Theory]
    [InlineData(1, "风停了")]
    [InlineData(2, "第二章的雨")]
    public void Book_search_refreshes_when_current_draft_or_chapter_changes(int chapter, string draft) => Sta(() =>
    {
        var api = new FakeSearchApi();
        api.Responses.Enqueue(Task.FromResult(Response("雨", 1, 1)));
        api.Responses.Enqueue(Task.FromResult(new SearchResponseDto()));
        var panel = new FindPanel(api) { DebounceMilliseconds = 30 };
        panel.SetProject("p1", "作品"); panel.SetDraft(1, "雨一直下"); panel.FocusScope(true);
        panel.QueryBox.Text = "雨";
        WpfPump.Until(() => panel.VisibleHitsForTest.Count == 1);

        panel.SetDraft(chapter, draft);
        Assert.Empty(panel.VisibleHitsForTest);
        Assert.False(panel.NextButton.IsEnabled);
        WpfPump.Until(() => api.Requests.Count == 2);
        Assert.Equal("book", api.Requests[1].Scope);
        Assert.Equal(chapter, api.Requests[1].Chapter_No);
        Assert.Equal(draft, api.Requests[1].Draft_Text);
    });

    [Fact]
    public void Empty_query_has_one_placeholder_and_no_header() => Sta(() =>
    {
        var panel = new FindPanel(new FakeSearchApi());
        panel.SetProject("p1", "很长的作品名称");
        Assert.Null(panel.FindName("TitleText"));
        panel.Measure(new Size(260, 680)); panel.Arrange(new Rect(0, 0, 260, 680)); panel.UpdateLayout();
        Assert.InRange(panel.QueryBox.TransformToAncestor(panel).Transform(new Point()).Y, 10, 15);
        Assert.Equal(Visibility.Collapsed, panel.EmptyHint.Visibility);
        panel.QueryBox.Text = "雨";
        panel.QueryBox.Text = "";
        Assert.Equal(Visibility.Collapsed, panel.EmptyHint.Visibility);
        Assert.Equal(Visibility.Collapsed, panel.StatusText.Visibility);
    });

    [Fact]
    public void Stale_search_response_is_dropped_by_generation() => Sta(() =>
    {
        var api = new FakeSearchApi();
        var first = new TaskCompletionSource<SearchResponseDto>();
        var second = new TaskCompletionSource<SearchResponseDto>();
        api.Responses.Enqueue(first.Task);
        api.Responses.Enqueue(second.Task);

        var panel = new FindPanel(api) { DebounceMilliseconds = 30 };
        panel.SetProject("p1", "作品");
        panel.SetChapters(Chapters(1, 2));
        panel.SetDraft(1, "林舟在雨中");

        panel.QueryBox.Text = "林舟";
        WpfPump.Until(() => api.Requests.Count == 1);
        panel.QueryBox.Text = "雨";
        WpfPump.Until(() => api.Requests.Count == 2);

        first.SetResult(Response("林舟", 5, chapter: 1));
        second.SetResult(Response("雨", 1, chapter: 1));
        WpfPump.Until(() => panel.VisibleHitsForTest.Count == 1);

        var hit = Assert.Single(panel.VisibleHitsForTest);
        Assert.Equal("雨", hit.Query);
        Assert.Equal(2, api.Requests.Count);
        Assert.Equal("chapter", api.Requests[0].Scope);
        Assert.Equal("林舟在雨中", api.Requests[0].Draft_Text);
    });

    [Fact]
    public void Results_are_grouped_and_next_raises_locate() => Sta(() =>
    {
        var api = new FakeSearchApi();
        api.Responses.Enqueue(Task.FromResult(Response("雨", 3, chapter: 1)));
        var panel = new FindPanel(api) { DebounceMilliseconds = 30 };
        panel.SetProject("p1", "作品");
        panel.SetChapters(Chapters(1, 2));
        panel.SetDraft(1, "雨一直下");

        var located = new List<FindHit>();
        panel.LocateRequested += (_, hit) => located.Add(hit);

        panel.QueryBox.Text = "雨";
        WpfPump.Until(() => panel.VisibleHitsForTest.Count == 3);
        // 1 个章节标题 + 3 条结果
        Assert.Equal(4, panel.ResultsPanel.Children.Count);

        panel.NextButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        panel.NextButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        Assert.Equal(2, located.Count);
        Assert.Equal(0, located[0].Offset);
        Assert.Equal(3, located[1].Offset);
        Assert.Equal("雨", located[1].Query);
        Assert.True(located[1].InDraft);
    });

    [Fact]
    public void Replace_requests_carry_query_replacement_and_scope_rules() => Sta(() =>
    {
        var api = new FakeSearchApi();
        var panel = new FindPanel(api) { DebounceMilliseconds = 30 };
        panel.SetProject("p1", "作品");
        panel.SetChapters(Chapters(1));
        panel.SetDraft(1, "甲乙丙");
        panel.QueryBox.Text = "甲";
        WpfPump.Until(() => api.Requests.Count >= 1);
        panel.ReplaceBox.Text = "丁";

        var requests = new List<FindReplaceRequest>();
        panel.ReplaceRequested += (_, request) => requests.Add(request);

        panel.ReplaceAllButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var request = Assert.Single(requests);
        Assert.Equal("甲", request.Query);
        Assert.Equal("丁", request.Replacement);
        Assert.True(request.ReplaceAll);

        panel.FocusScope(true); // 全书范围不允许替换
        Assert.False(panel.ReplaceButton.IsEnabled);
        Assert.False(panel.ReplaceAllButton.IsEnabled);
        panel.ReplaceAllButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Single(requests);
        Assert.True(panel.IsBookScopeForTest);

        panel.SetReplaceEnabled(false);
        panel.FocusScope(false);
        Assert.False(panel.ReplaceButton.IsEnabled);
        panel.ReplaceButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Single(requests);
        Assert.True(panel.StatusIsErrorForTest);
    });

    [Fact]
    public void Chapter_scope_without_a_chapter_reports_and_does_not_call_backend() => Sta(() =>
    {
        var api = new FakeSearchApi();
        var panel = new FindPanel(api) { DebounceMilliseconds = 30 };
        panel.SetProject("p1", "作品");
        panel.SetChapters(Chapters(1));

        panel.QueryBox.Text = "甲";
        WpfPump.Until(() => panel.StatusIsErrorForTest);

        Assert.Empty(api.Requests);
        Assert.Equal(0, panel.VisibleHitsForTest.Count);
    });

    [Fact]
    public void Find_three_language_dictionaries_define_the_same_keys()
    {
        var sets = NotesPanelTests.PanelStringKeys.Sets("Find");
        Assert.NotEmpty(sets["zh-CN"]);
        Assert.Equal(sets["zh-CN"], sets["en-US"]);
        Assert.Equal(sets["zh-CN"], sets["ja-JP"]);
        Assert.All(sets["zh-CN"], key => Assert.StartsWith("Find.", key));
    }

    [Fact]
    public void Replace_row_can_toggle_and_query_can_clear_without_leaving_stale_results() => Sta(() =>
    {
        var api = new FakeSearchApi();
        api.Responses.Enqueue(Task.FromResult(Response("雨", 3, 1)));
        var panel = new FindPanel(api) { DebounceMilliseconds = 30 };
        panel.SetProject("p1", "作品"); panel.SetDraft(1, "雨雨雨");
        Assert.Equal(Visibility.Collapsed, panel.ReplaceRow.Visibility);
        Click(panel.ToggleReplaceButton);
        Assert.Equal(Visibility.Visible, panel.ReplaceRow.Visibility);
        panel.QueryBox.Text = "雨";
        WpfPump.Until(() => panel.VisibleHitsForTest.Count == 3);
        Click(panel.ClearQueryButton);
        Assert.Empty(panel.QueryBox.Text);
        Assert.Empty(panel.VisibleHitsForTest);
        Assert.Empty(panel.HitChecksForTest);
        Assert.False(panel.NextButton.IsEnabled);
        Assert.False(panel.ReplaceAllButton.IsEnabled);
        Click(panel.ToggleReplaceButton);
        Assert.Equal(Visibility.Collapsed, panel.ReplaceRow.Visibility);
    });

    [Fact]
    public void Result_and_chapter_checkboxes_select_only_the_requested_replacements() => Sta(() =>
    {
        var api = new FakeSearchApi();
        api.Responses.Enqueue(Task.FromResult(Response("雨", 3, 1)));
        var panel = new FindPanel(api) { DebounceMilliseconds = 30 };
        panel.SetProject("p1", "作品"); panel.SetDraft(1, "雨一直雨一直雨");
        panel.QueryBox.Text = "雨";
        WpfPump.Until(() => panel.HitChecksForTest.Count == 3);
        var requests = new List<FindReplaceRequest>();
        panel.ReplaceRequested += (_, request) => requests.Add(request);
        var second = panel.HitChecksForTest[1];
        second.IsChecked = true; second.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Null(Assert.Single(panel.ChapterChecksForTest).IsChecked);
        Click(panel.ReplaceAllButton);
        var request = Assert.Single(requests);
        Assert.Equal(new[] { 3 }, request.SelectedOffsets);
        Assert.Equal("雨一直雨一直雨", request.SourceText);
        Assert.Equal(1, request.ChapterNo);
        var chapter = panel.ChapterChecksForTest[0];
        chapter.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.All(panel.HitChecksForTest, check => Assert.True(check.IsChecked));
        chapter.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.All(panel.HitChecksForTest, check => Assert.False(check.IsChecked));
        Click(panel.ReplaceAllButton);
        Assert.Null(requests[1].SelectedOffsets);
        second.IsChecked = true; second.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        panel.SetDraft(1, "改过的正文");
        Assert.Empty(panel.HitChecksForTest);
    });

    private static void Click(System.Windows.Controls.Button button) =>
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static SearchResponseDto Response(string query, int hits, int chapter)
    {
        var matches = Enumerable.Range(0, hits)
            .Select(i => new SearchHitDto
            {
                Offset = i * 3,
                Context = "前文…" + query + "…后文",
                Context_Offset = 3,
                In_Draft = true,
            })
            .ToList();
        var response = new SearchResponseDto
        {
            Total = hits,
            Page = 1,
            Page_Size = 50,
        };
        if (hits > 0)
        {
            response.Chapters.Add(new SearchChapterDto
            {
                Chapter_No = chapter,
                Title = "第" + chapter + "章",
                Count = hits,
                Matches = matches,
            });
        }
        return response;
    }

    private static List<ChapterDto> Chapters(params int[] numbers) => numbers
        .Select(n => new ChapterDto { No = n, Title = "第" + n + "章" })
        .ToList();

    private sealed class FakeSearchApi : ISearchApi
    {
        public List<SearchRequestDto> Requests { get; } = new();
        public Queue<Task<SearchResponseDto>> Responses { get; } = new();

        public Task<SearchResponseDto> SearchAsync(string projectId, SearchRequestDto request)
        {
            Requests.Add(request);
            return Responses.Count > 0
                ? Responses.Dequeue()
                : Task.FromResult(new SearchResponseDto { Page = 1, Page_Size = 50 });
        }
    }

    private static void Sta(Action action) =>
        StaRunner.Run(action, TimeSpan.FromSeconds(35), "WPF find operation timed out");
}
