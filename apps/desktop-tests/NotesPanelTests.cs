using System.Runtime.ExceptionServices;
using System.Xml.Linq;
using System.Windows;
using System.Windows.Threading;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Controls;
using NovelGm.Desktop.Models;
using Xunit;

namespace NovelGm.Desktop.Tests;

public class NotesPanelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Delayed_reload_cannot_replace_a_newer_saved_draft_or_its_status(bool reloadFails) => Sta(() =>
    {
        var api = new FakeNotesApi();
        api.Notes.Add(Note("n1", 1, "线索", "旧内容", 1));
        var panel = Create(api); panel.SetProject("p1", Chapters(1), 1); Wait(panel.ReloadAsync());
        Open(panel, "n1");
        var stale = api.Notes.Select(n => n.Copy()).ToList();
        var response = new TaskCompletionSource<List<NoteDto>>();
        api.ListResponses.Enqueue(response.Task);
        var reload = panel.ReloadAsync();
        Assert.False(reload.IsCompleted);

        panel.BodyBox.Text = "刚刚保存的新内容";
        Wait(panel.FlushAsync());
        Assert.False(panel.HasUnsavedDraft);
        if (reloadFails) response.SetException(new IOException("过期刷新失败"));
        else response.SetResult(stale);
        Wait(reload);

        Assert.Equal("刚刚保存的新内容", panel.BodyBox.Text);
        Assert.Equal("刚刚保存的新内容", panel.SelectedNoteForTest!.Body);
        Assert.Equal(2, panel.SelectedNoteForTest.Content_Version);
        Assert.False(panel.StatusIsErrorForTest);
        Assert.Equal(Visibility.Collapsed, panel.RetryButton.Visibility);
    });

    [Fact]
    public void Named_creation_trims_title_and_body_saves_preserve_it() => Sta(() =>
    {
        var api = new FakeNotesApi();
        var panel = new NotesPanel(api, () => "  雨夜线索  ") { DebounceMilliseconds = 40 };
        panel.SetProject("p1", Array.Empty<ChapterDto>(), null); Wait(panel.ReloadAsync());
        Click(panel.NewButton);
        Assert.Equal("雨夜线索", Assert.Single(api.Notes).Title);
        Assert.Null(panel.FindName("TitleBox"));
        panel.BodyBox.Text = "门外的脚步声";
        Wait(panel.FlushAsync());
        Assert.Equal("雨夜线索", api.Notes[0].Title);
        Click(panel.SaveButton);
        Assert.Null(panel.SelectedNoteForTest);
        Open(panel, "n1");
        Assert.Equal("门外的脚步声", panel.BodyBox.Text);
        Assert.Equal("雨夜线索", panel.SelectedNoteForTest!.Title);
    });

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("too-long")]
    public void Cancelled_or_invalid_name_creates_no_entry(string? name) => Sta(() =>
    {
        var api = new FakeNotesApi();
        var panel = new NotesPanel(api, () => name == "too-long" ? new string('名', 121) : name);
        panel.SetProject("p1", Chapters(1), 1); Wait(panel.ReloadAsync());
        Click(panel.NewButton);
        Assert.Equal(0, api.CreateCalls);
        Assert.Empty(api.Notes);
        Assert.Null(panel.SelectedNoteForTest);
        Assert.True(panel.NewButton.IsEnabled);
    });

    [Fact]
    public void Name_at_limit_is_accepted() => Sta(() =>
    {
        var api = new FakeNotesApi();
        var title = new string('名', 120);
        var panel = new NotesPanel(api, () => title);
        panel.SetProject("p1", Chapters(1), 1); Wait(panel.ReloadAsync());
        Click(panel.NewButton);
        Assert.Equal(title, Assert.Single(api.Notes).Title);
    });

    [Fact]
    public void Compact_cards_show_only_names_even_for_pinned_and_unnamed_notes() => Sta(() =>
    {
        var api = new FakeNotesApi();
        var note = Note("n1", 1, "", "不应出现在列表中的正文", 1); note.Pinned = true;
        api.Notes.Add(note);
        var panel = Create(api); panel.SetProject("p1", Chapters(1), 1); Wait(panel.ReloadAsync());
        panel.Measure(new Size(260, 680)); panel.Arrange(new Rect(0, 0, 260, 680)); panel.UpdateLayout();
        var item = (System.Windows.Controls.ListBoxItem)panel.NoteList.ItemContainerGenerator.ContainerFromIndex(0);
        Assert.Equal(52, item.ActualHeight, 1);
        var label = Assert.Single(Descendants(item).OfType<System.Windows.Controls.TextBlock>());
        Assert.Equal(L.T("Notes.Untitled"), label.Text);
        Assert.DoesNotContain(note.Body, label.Text);
        Assert.Equal(System.Windows.TextTrimming.CharacterEllipsis, label.TextTrimming);
    });

    [Fact]
    public void Typing_debounces_to_one_save_and_keeps_the_latest_draft() => Sta(() =>
    {
        var api = new FakeNotesApi();
        api.Notes.Add(Note("n1", 1, "意图", "原始", 1));
        var panel = Create(api);
        panel.SetProject("p1", Chapters(1, 2), 1);
        Wait(panel.ReloadAsync());
        Open(panel, "n1");
        WpfPump.Until(() => panel.SelectedNoteForTest?.Id == "n1");

        panel.BodyBox.Text = "第一版";
        panel.BodyBox.Text = "第二版";
        panel.SetProject("p1", Chapters(1, 2), 2);
        Assert.Equal("第二版", panel.BodyBox.Text);
        Assert.True(panel.HasUnsavedDraft);
        WpfPump.Until(() => api.UpdateCalls >= 1);

        Assert.Equal(1, api.UpdateCalls);
        Assert.Equal("n1", api.LastNoteId);
        Assert.Equal("第二版", api.LastBody);
        Assert.False(panel.StatusIsErrorForTest);
    });

    [Fact]
    public void Failed_save_keeps_the_draft_and_can_retry() => Sta(() =>
    {
        var api = new FakeNotesApi { UpdateFailure = "network down" };
        api.Notes.Add(Note("n1", 1, "意图", "原始", 1));
        var panel = Create(api);
        panel.SetProject("p1", Chapters(1), 1);
        Wait(panel.ReloadAsync());
        Open(panel, "n1");
        WpfPump.Until(() => panel.SelectedNoteForTest?.Id == "n1");

        panel.BodyBox.Text = "不能丢的草稿";
        WpfPump.Until(() => api.UpdateCalls == 1 && panel.StatusIsErrorForTest);

        Assert.Equal(1, api.UpdateCalls);
        Assert.Equal("不能丢的草稿", panel.BodyBox.Text);
        Assert.True(panel.StatusIsErrorForTest);
        Assert.Contains("Notes.SaveFailed", panel.StatusTextForTest);

        api.UpdateFailure = null;
        Wait(panel.FlushAsync());

        Assert.Equal(2, api.UpdateCalls);
        Assert.Equal("不能丢的草稿", api.LastBody);
        Assert.False(panel.StatusIsErrorForTest);
        Assert.Equal("不能丢的草稿", panel.SelectedNoteForTest!.Body);
        Assert.Equal("意图", api.Notes[0].Title);
    });

    [Fact]
    public void Notes_are_project_wide_and_do_not_follow_chapters() => Sta(() =>
    {
        var api = new FakeNotesApi();
        api.Notes.Add(Note("n1", 1, "第一章", "甲", 1));
        api.Notes.Add(Note("n2", 2, "第二章", "乙", 1));
        var panel = Create(api);
        panel.SetProject("p1", Chapters(1, 2), 1);
        Wait(panel.ReloadAsync());
        Open(panel, "n1");
        Assert.Equal(2, panel.VisibleNotesForTest.Count);
        panel.SetCurrentChapter(2);
        Assert.Equal(2, panel.VisibleNotesForTest.Count);
        Assert.Equal("n1", panel.SelectedNoteForTest?.Id);
        Assert.Equal(0, api.DistillCalls);
        Assert.Equal(0, api.ReferenceCalls);
    });

    [Fact]
    public void Failed_explicit_save_keeps_editor_and_draft_until_retry_succeeds() => Sta(() =>
    {
        var api = new FakeNotesApi { UpdateFailure = "offline" };
        api.Notes.Add(Note("n1", 1, "草稿", "原文", 1));
        var panel = Create(api);
        panel.SetProject("p1", Chapters(1), 1);
        Wait(panel.ReloadAsync());
        Open(panel, "n1");
        panel.BodyBox.Text = "还没保存";
        Click(panel.SaveButton);
        Assert.True(panel.HasUnsavedDraft);
        Assert.Equal("还没保存", panel.BodyBox.Text);
        Assert.Equal("n1", panel.SelectedNoteForTest?.Id);
        Assert.Equal(Visibility.Visible, panel.EditorGrid.Visibility);
        api.UpdateFailure = null;
        Click(panel.SaveButton);
        Assert.False(panel.HasUnsavedDraft);
        Assert.Null(panel.SelectedNoteForTest);
        Assert.Equal(Visibility.Collapsed, panel.EditorGrid.Visibility);
        Assert.Equal(Visibility.Visible, panel.NoteList.Visibility);
        Assert.Single(panel.VisibleNotesForTest);
        Assert.Equal("还没保存", api.Notes[0].Body);
        Assert.Equal(0, api.DistillCalls);
    });

    [Fact]
    public void Saving_separate_entries_returns_to_list_and_can_reopen_without_duplicates() => Sta(() =>
    {
        var api = new FakeNotesApi();
        var panel = Create(api);
        panel.SetProject("p1", Array.Empty<ChapterDto>(), null);
        Wait(panel.ReloadAsync());
        panel.NewButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        WpfPump.Until(() => panel.SelectedNoteForTest is not null);
        panel.BodyBox.Text = "独立笔记";
        Click(panel.SaveButton);
        Assert.Equal("default", api.Notes[0].Folder);
        Assert.Null(panel.SelectedNoteForTest);
        Assert.Equal("独立笔记", Assert.Single(panel.VisibleNotesForTest).Body);
        Click(panel.NewButton);
        panel.BodyBox.Text = "另一段想法";
        Click(panel.SaveButton);
        Assert.Equal(2, panel.VisibleNotesForTest.Count);
        Open(panel, "n1");
        Assert.Equal("独立笔记", panel.BodyBox.Text);
        panel.BodyBox.Text = "修改第一条";
        Click(panel.SaveButton);
        Click(panel.SaveButton);
        Assert.Equal(2, api.Notes.Count);
        Assert.Equal("修改第一条", api.Notes[0].Body);
        Assert.Equal("另一段想法", api.Notes[1].Body);
        Wait(panel.ReloadAsync());
        Assert.Equal(2, panel.VisibleNotesForTest.Count);
        Assert.Null(panel.SelectedNoteForTest);
    });

    [Fact]
    public void Legacy_categories_are_combined_without_losing_entries_or_loading_folders() => Sta(() =>
    {
        var api = new FakeNotesApi();
        foreach (var folder in new[] { "default", "foreshadow", "memo", "custom" })
        {
            var note = Note(folder, 1, folder, "正文：" + folder, 1);
            note.Folder = folder;
            api.Notes.Add(note);
        }
        var panel = Create(api);
        panel.SetProject("p1", Chapters(1), 1);
        Wait(panel.ReloadAsync());
        Assert.Equal(4, panel.VisibleNotesForTest.Count);
        Assert.Equal(0, api.FolderCalls);
        Assert.Null(panel.SelectedNoteForTest);
        Assert.Equal(Visibility.Visible, panel.NoteList.Visibility);
        Assert.Null(panel.FindName("FolderTabs"));
        Assert.Null(panel.FindName("MoveFolderCombo"));
        Open(panel, "memo");
        panel.BodyBox.Text = "更新后的备忘";
        Click(panel.SaveButton);
        Assert.Equal("default", api.Notes.Single(n => n.Id == "memo").Folder);
        Assert.Equal(4, panel.VisibleNotesForTest.Count);
    });

    [Fact]
    public void Finish_after_autosave_returns_to_list_without_extra_writes() => Sta(() =>
    {
        var api = new FakeNotesApi();
        api.Notes.Add(Note("n1", 1, "想法", "原文", 1));
        var panel = Create(api);
        panel.SetProject("p1", Chapters(1), 1);
        Wait(panel.ReloadAsync());
        Open(panel, "n1");
        panel.BodyBox.Text = "自动保护的草稿";
        WpfPump.Until(() => api.UpdateCalls == 1 && !panel.HasUnsavedDraft);
        Assert.Equal(Visibility.Visible, panel.EditorGrid.Visibility);
        Click(panel.SaveButton);
        Assert.Null(panel.SelectedNoteForTest);
        Assert.Equal(1, api.UpdateCalls);
        Assert.Equal("自动保护的草稿", Assert.Single(panel.VisibleNotesForTest).Body);
    });

    private static void Click(System.Windows.Controls.Button button) =>
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    private static void Open(NotesPanel panel, string id)
    {
        panel.NoteList.SelectedItem = panel.VisibleNotesForTest.Single(n => n.Id == id);
        Wait(panel.OpenSelectedAsync());
    }

    [Fact]
    public void Single_selection_keeps_list_and_menu_opens_its_own_target() => Sta(() =>
    {
        var api = new FakeNotesApi();
        api.Notes.Add(Note("n1", 1, "第一篇", "甲", 1));
        api.Notes.Add(Note("n2", 2, "第二篇", "乙", 1));
        var panel = Create(api); panel.SetProject("p1", Chapters(1), 1); Wait(panel.ReloadAsync());
        panel.NoteList.SelectedItem = panel.VisibleNotesForTest[0];
        Assert.Null(panel.SelectedNoteForTest);
        Assert.Equal(Visibility.Visible, panel.NoteList.Visibility);
        var menu = panel.CreateNoteMenu(panel.VisibleNotesForTest.Single(n => n.Id == "n2"));
        Assert.Equal(6, menu.Items.Count);
        ((System.Windows.Controls.MenuItem)menu.Items[0]).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
        Assert.Equal("n2", panel.SelectedNoteForTest?.Id);
    });

    [Fact]
    public void Pin_move_copy_and_rename_preserve_contents_and_manual_order() => Sta(() =>
    {
        var api = new FakeNotesApi();
        api.Notes.Add(Note("n1", 1, "第一篇", "甲", 1)); api.Notes.Add(Note("n2", 2, "第二篇", "乙", 1));
        var panel = Create(api); panel.SetProject("p1", Chapters(1), 1); Wait(panel.ReloadAsync());
        Wait(panel.MoveNoteAsync(panel.VisibleNotesForTest[1], "up"));
        Assert.Equal(new[] { "n2", "n1" }, panel.VisibleNotesForTest.Select(n => n.Id));
        Wait(panel.PinNoteAsync(panel.VisibleNotesForTest[1]));
        Assert.Equal("n1", panel.VisibleNotesForTest[0].Id);
        Assert.False(panel.VisibleNotesForTest[0].CanMoveDown);
        Assert.False(panel.VisibleNotesForTest[1].CanMoveUp);
        Wait(panel.RenameNoteAsync(panel.VisibleNotesForTest[0], "改名"));
        Assert.True(panel.VisibleNotesForTest[0].Pinned);
        Assert.Equal("甲", panel.VisibleNotesForTest[0].Body);
        Wait(panel.CopyNoteAsync(panel.VisibleNotesForTest[0]));
        var copy = Assert.Single(panel.VisibleNotesForTest, n => n.Id != "n1" && n.Id != "n2");
        Assert.Equal("甲", copy.Body); Assert.False(copy.Pinned); Assert.Equal("none", copy.Distill.Status);
        Assert.Null(panel.SelectedNoteForTest);
        Assert.Equal(copy.Id, ((NoteDto)panel.NoteList.SelectedItem).Id);
        Open(panel, copy.Id); panel.BodyBox.Text = "副本修改"; Click(panel.SaveButton);
        Assert.Equal("甲", api.Notes.Single(n => n.Id == "n1").Body);
        Wait(panel.ReloadAsync()); Assert.Equal("n1", panel.VisibleNotesForTest[0].Id);
    });

    [Fact]
    public void Failed_menu_operation_keeps_selection_and_retries_without_reordering() => Sta(() =>
    {
        var api = new FakeNotesApi { MetadataFailure = "offline" };
        api.Notes.Add(Note("n1", 1, "甲", "内容", 1)); api.Notes.Add(Note("n2", 2, "乙", "内容", 1));
        var panel = Create(api); panel.SetProject("p1", Chapters(1), 1); Wait(panel.ReloadAsync());
        var before = panel.VisibleNotesForTest.Select(n => n.Id).ToArray();
        panel.NoteList.SelectedItem = panel.VisibleNotesForTest[1];
        Wait(panel.PinNoteAsync(panel.VisibleNotesForTest[1]));
        Assert.Equal(before, panel.VisibleNotesForTest.Select(n => n.Id));
        Assert.True(panel.StatusIsErrorForTest);
        Assert.Equal("n2", ((NoteDto)panel.NoteList.SelectedItem).Id);
        api.MetadataFailure = null; Click(panel.RetryButton);
        Assert.True(panel.VisibleNotesForTest[0].Pinned);
        Assert.Equal("n2", panel.VisibleNotesForTest[0].Id);
        Assert.False(panel.StatusIsErrorForTest);
    });

    [Fact]
    public void Sorting_buttons_enable_moves_only_inside_each_pin_group() => Sta(() =>
    {
        var api = new FakeNotesApi();
        var pinned = Note("n1", 1, "置顶", "甲", 1); pinned.Pinned = true;
        api.Notes.Add(pinned); api.Notes.Add(Note("n2", 2, "普通一", "乙", 1)); api.Notes.Add(Note("n3", 3, "普通二", "丙", 1));
        var panel = Create(api); panel.SetProject("p1", Chapters(1), 1); Wait(panel.ReloadAsync());
        panel.IsSorting = true;
        panel.Measure(new Size(260, 680)); panel.Arrange(new Rect(0, 0, 260, 680)); panel.UpdateLayout();
        var moves = Descendants(panel).OfType<System.Windows.Controls.Button>().Where(b => b.Tag is string direction && (direction == "up" || direction == "down")).ToList();
        var card = (System.Windows.Controls.ListBoxItem)panel.NoteList.ItemContainerGenerator.ContainerFromIndex(0);
        Assert.True(card.ActualHeight > 52, "排序操作展开时卡片应增高，避免裁切按钮");
        Assert.Equal(6, moves.Count);
        Assert.All(moves.Where(b => b.DataContext is NoteDto { Id: "n1" }), b => Assert.False(b.IsEnabled));
        var down = Assert.Single(moves, b => b.Tag is "down" && b.DataContext is NoteDto { Id: "n2" });
        Assert.True(down.IsEnabled);
        var up = Assert.Single(moves, b => b.Tag is "up" && b.DataContext is NoteDto { Id: "n3" });
        Assert.True(up.IsEnabled);
        Click(down);
        Assert.Equal(new[] { "n1", "n3", "n2" }, panel.VisibleNotesForTest.Select(n => n.Id));
    });

    [Theory]
    [InlineData("move")]
    [InlineData("copy")]
    public void Retry_after_committed_response_loss_reuses_operation_id(string action) => Sta(() =>
    {
        var api = new FakeNotesApi { LoseMoveResponse = action == "move", LoseCopyResponse = action == "copy" };
        for (var i = 1; i <= 3; i++) api.Notes.Add(Note("n" + i, i, "原稿" + i, "内容" + i, 1));
        var panel = Create(api); panel.SetProject("p1", Chapters(1), 1); Wait(panel.ReloadAsync());
        if (action == "move") Wait(panel.MoveNoteAsync(panel.VisibleNotesForTest[2], "up"));
        else Wait(panel.CopyNoteAsync(panel.VisibleNotesForTest[0]));
        Assert.True(panel.StatusIsErrorForTest);
        Click(panel.RetryButton);
        Assert.False(panel.StatusIsErrorForTest);
        Assert.Equal(2, api.OperationIds.Count);
        Assert.False(string.IsNullOrEmpty(api.OperationIds[0]));
        Assert.Equal(api.OperationIds[0], api.OperationIds[1]);
        if (action == "move") Assert.Equal(new[] { "n1", "n3", "n2" }, panel.VisibleNotesForTest.Select(n => n.Id));
        else
        {
            Assert.Equal(4, api.Notes.Count);
            Assert.Equal(4, panel.VisibleNotesForTest.Count);
            var copy = Assert.Single(api.Notes, n => n.Id == "n4");
            Assert.Equal("内容1", copy.Body);
        }
    });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    [Fact]
    public void Notes_three_language_dictionaries_define_the_same_keys()
    {
        var sets = PanelStringKeys.Sets("Notes");
        Assert.NotEmpty(sets["zh-CN"]);
        Assert.Equal(sets["zh-CN"], sets["en-US"]);
        Assert.Equal(sets["zh-CN"], sets["ja-JP"]);
        Assert.All(sets["zh-CN"], key => Assert.StartsWith("Notes.", key));
    }

    private static NotesPanel Create(FakeNotesApi api) =>
        new(api, () => "新笔记") { DebounceMilliseconds = 40 };

    private static NoteDto Note(string id, int chapter, string title, string body, int version) => new()
    {
        Id = id,
        Chapter_No = chapter,
        Title = title,
        Body = body,
        Content_Version = version,
        Created_At = $"{chapter:0000}-01-01T00:00:00+08:00",
        Distill = new NoteDistillDto(),
    };

    private static List<ChapterDto> Chapters(params int[] numbers) => numbers
        .Select(n => new ChapterDto { No = n, Title = "第" + n + "章" })
        .ToList();

    private sealed class FakeNotesApi : INotesApi
    {
        public List<NoteDto> Notes { get; } = new();
        public Queue<Task<List<NoteDto>>> ListResponses { get; } = new();
        public int ListCalls;
        public int CreateCalls;
        public int UpdateCalls;
        public int DeleteCalls;
        public int DistillCalls;
        public int ReferenceCalls;
        public int FolderCalls;
        public string? UpdateFailure;
        public string? MetadataFailure;
        public bool LoseMoveResponse, LoseCopyResponse;
        public List<string?> OperationIds { get; } = new();
        private readonly HashSet<string> _moves = new();
        private readonly Dictionary<string, string> _copies = new();
        public int? DistillContentVersionOverride = null;
        public string LastNoteId = "";
        public string LastBody = "";

        public Task<List<NoteFolderDto>> FoldersAsync(string id)
        {
            FolderCalls++;
            throw new InvalidOperationException("Category storage is no longer needed by the notes panel.");
        }
        public Task<NoteFolderDto> SaveFolderAsync(string id, string title, string? folderId = null) =>
            Task.FromResult(new NoteFolderDto { Id = folderId ?? "custom", Title = title });

        public Task<List<NoteDto>> ListAsync(string projectId)
        {
            ListCalls++;
            if (ListResponses.Count > 0) return ListResponses.Dequeue();
            return Task.FromResult(Notes.Select(n => n.Copy()).ToList());
        }

        public Task<NoteDto> CreateAsync(string projectId, string folder, string title, string body)
        {
            CreateCalls++;
            var note = new NoteDto
            {
                Id = "n" + (Notes.Count + 1),
                Folder = folder,
                Title = title,
                Body = body,
                Content_Version = 1,
                Created_At = DateTimeOffset.Now.ToString("O"),
                Distill = new NoteDistillDto(),
                Sort_Order = Notes.Where(n => !n.Pinned).Select(n => n.Sort_Order).DefaultIfEmpty(0).Min() - 1,
            };
            Notes.Add(note);
            return Task.FromResult(note.Copy());
        }

        public Task<NoteDto> UpdateAsync(string projectId, string noteId, string title, string body, int contentVersion, string folder)
        {
            UpdateCalls++;
            LastNoteId = noteId;
            LastBody = body;
            if (UpdateFailure is not null)
                return Task.FromException<NoteDto>(new InvalidOperationException(UpdateFailure));

            var note = Notes.First(n => n.Id == noteId);
            note.Folder = folder;
            note.Title = title;
            note.Body = body;
            note.Content_Version = contentVersion + 1;
            return Task.FromResult(note.Copy());
        }

        public Task DeleteAsync(string projectId, string noteId)
        {
            DeleteCalls++;
            Notes.RemoveAll(n => n.Id == noteId);
            return Task.CompletedTask;
        }

        public Task<List<NoteDto>> PinAsync(string projectId, string noteId, bool pinned)
        {
            if (MetadataFailure is not null) throw new IOException(MetadataFailure);
            var note = Notes.Single(n => n.Id == noteId);
            note.Sort_Order = Notes.Where(n => n.Pinned == pinned).Select(n => n.Sort_Order).DefaultIfEmpty(0).Min() - 1;
            note.Pinned = pinned;
            return ListAsync(projectId);
        }
        public Task<List<NoteDto>> MoveAsync(string projectId, string noteId, string direction, string? operationId = null)
        {
            OperationIds.Add(operationId);
            if (operationId is not null && _moves.Contains(operationId)) return ListAsync(projectId);
            if (MetadataFailure is not null) throw new IOException(MetadataFailure);
            var note = Notes.Single(n => n.Id == noteId);
            var group = Notes.Where(n => n.Pinned == note.Pinned).OrderBy(n => n.Sort_Order).ThenBy(n => n.Id).ToList();
            var index = group.IndexOf(note); var target = index + (direction == "up" ? -1 : 1);
            if (target >= 0 && target < group.Count) (group[index], group[target]) = (group[target], group[index]);
            for (var i = 0; i < group.Count; i++) group[i].Sort_Order = i;
            if (operationId is not null) _moves.Add(operationId);
            if (LoseMoveResponse) { LoseMoveResponse = false; throw new IOException("response lost after commit"); }
            return ListAsync(projectId);
        }
        public async Task<NoteDto> CopyAsync(string projectId, string noteId, string title, string? operationId = null)
        {
            OperationIds.Add(operationId);
            if (operationId is not null && _copies.TryGetValue(operationId, out var previous)) return Notes.Single(n => n.Id == previous).Copy();
            var copy = await CreateAsync(projectId, "default", title, Notes.Single(n => n.Id == noteId).Body);
            if (operationId is not null) _copies[operationId] = copy.Id;
            if (LoseCopyResponse) { LoseCopyResponse = false; throw new IOException("response lost after commit"); }
            return copy;
        }

        public Task<NoteDistillResultDto> DistillAsync(string projectId, string noteId, int contentVersion,
            int chapterNo)
        {
            DistillCalls++;
            return Task.FromResult(new NoteDistillResultDto
            {
                Note_Id = noteId,
                Status = "ready",
                Content_Version = DistillContentVersionOverride ?? contentVersion,
                Auto_Reference = "参考:" + noteId,
            });
        }

        public Task<NoteDto> SaveReferenceAsync(string projectId, string noteId, string reference) =>
            throw new NotSupportedException();

        public Task<ChapterReferenceDto> ChapterReferenceAsync(string projectId, int chapterNo)
        {
            ReferenceCalls++;
            var notes = Notes.Where(n => n.Chapter_No == chapterNo).ToList();
            return Task.FromResult(new ChapterReferenceDto
            {
                Chapter_No = chapterNo,
                Status = notes.Any(n => n.Distill.Status == "failed") ? "failed"
                    : notes.All(n => n.Distill.Status == "ready") && notes.Count > 0 ? "ready"
                    : notes.Count == 0 ? "none" : "stale",
                Reference = string.Join("\n", notes.Select(n => n.Distill.Effective)),
                Notes = notes.Select(n => new NoteRefItemDto
                {
                    Note_Id = n.Id,
                    Title = n.Title,
                    Status = n.Distill.Status,
                    Reference = n.Distill.Effective,
                }).ToList(),
            });
        }
    }

    internal static class PanelStringKeys
    {
        public static Dictionary<string, HashSet<string>> Sets(string feature)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "desktop-wpf", "Strings")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            var folder = Path.Combine(dir!.FullName, "desktop-wpf", "Strings");
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            return new[] { "zh-CN", "en-US", "ja-JP" }.ToDictionary(
                lang => lang,
                lang =>
                {
                    var file = Path.Combine(folder, feature + "." + lang + ".xaml");
                    return XDocument.Load(file).Root!.Elements()
                        .Select(element => (string?)element.Attribute(x + "Key"))
                        .OfType<string>()
                        .ToHashSet();
                });
        }
    }

    private static void Wait(Task task)
    {
        WpfPump.Until(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }

    private static void Sta(Action action) =>
        StaRunner.Run(action, TimeSpan.FromSeconds(35), "WPF notes operation timed out");
}
