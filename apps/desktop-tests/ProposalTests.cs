using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Xml.Linq;
using NovelGm.Desktop;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Controls;
using NovelGm.Desktop.Models;
using Xunit;

namespace NovelGm.Desktop.Tests;

public class ProposalTests
{
    static ProposalTests() => System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(Application).TypeHandle);
    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    private static ProposalViewDto View(bool adopted = false) => new()
    {
        Chapter = new() { No = 4, Chapter_Uid = "stable-four", Title = "雨夜来信" }, Writable = true, Input_Token = "token",
        Workspace = new() { Draft = adopted ? null : Proposal(), Adopted = adopted ? Proposal() : null },
        Draft_Status = new() { State = adopted ? "none" : "draft" }, Adopted_Status = new() { State = adopted ? "adopted" : "none" }
    };
    private static ProposalDto Proposal() => new()
    {
        Id = "proposal", Chapter_Uid = "stable-four", Plot_Outline = string.Concat(Enumerable.Repeat("保留来信秘密，在章末推进对峙。\n", 100)),
        Questions = new() { new() { Id = "question-one", Question = "交易的筹码是什么？", Why = "决定对峙的内容",
            Options = new() { "一封旧信", "失而复得的钥匙" }, Answer = "旧答案" } },
        Author_Decisions = new() { new() { Id = "decision-one", Question = "是否揭晓身份？", Answer = "暂不揭晓" } }
    };
    private static ProposalSession Session(ProposalViewDto? initial = null, Func<ProposalCommandDto, Task<ProposalViewDto>>? save = null,
        Func<string, ProposalCommandDto, Task<ProposalViewDto>>? operation = null)
    {
        var server = Copy(initial ?? View());
        return new("project-one", "窗口测试", Copy(server), save ?? (command =>
        {
            server.Workspace.Draft ??= Copy(server.Workspace.Adopted!);
            foreach (var q in server.Workspace.Draft.Questions)
                if (command.Answers.TryGetValue(q.Id, out var value)) q.Answer = value;
            foreach (var d in server.Workspace.Draft.Author_Decisions)
                if (command.Decisions.TryGetValue(d.Id, out var value)) d.Answer = value;
            if (command.Plot_Outline is { Length: > 0 } outline) server.Workspace.Draft.Plot_Outline = outline;
            server.Workspace.Revision++; return Task.FromResult(Copy(server));
        }), operation ?? ((_, _) => Task.FromResult(Copy(server))), () => Task.FromResult(Copy(server)));
    }
    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame(); var dispatcher = Dispatcher.CurrentDispatcher;
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
    }

    [Fact]
    public void Editing_adopted_answers_creates_local_draft_and_preserves_adopted_snapshot() => StaRunner.Run(() =>
    {
        using var session = Session(View(true));
        session.EditAnswer("question-one", "旧答案"); Assert.Null(session.Data.Workspace.Draft);
        session.EditAnswer("question-one", "新草稿答案");
        Assert.Equal("旧答案", session.Data.Workspace.Adopted!.Questions[0].Answer);
        Assert.Equal("新草稿答案", session.Data.Workspace.Draft!.Questions[0].Answer);
        Pump(session.FlushAsync()); Assert.False(session.HasPendingSave);
        Assert.Equal("旧答案", session.Data.Workspace.Adopted.Questions[0].Answer);
    });

    [Fact]
    public void Save_queue_preserves_later_edits_and_uses_latest_server_revision() => StaRunner.Run(() =>
    {
        var first = new TaskCompletionSource<ProposalViewDto>(); var calls = new List<ProposalCommandDto>();
        using var session = Session(save: c =>
        {
            calls.Add(c);
            var saved = View(); saved.Workspace.Revision = calls.Count;
            saved.Workspace.Draft!.Questions[0].Answer = c.Answers["question-one"];
            return calls.Count == 1 ? first.Task : Task.FromResult(saved);
        });
        session.EditAnswer("question-one", "第一版"); var saving = session.FlushAsync();
        session.EditAnswer("question-one", "正在输入的第二版");
        var ack = View(); ack.Workspace.Revision = 1; ack.Workspace.Draft!.Questions[0].Answer = "第一版";
        first.SetResult(ack); Pump(saving);
        Assert.Equal(2, calls.Count); Assert.Equal(1, calls[1].Expected_Revision);
        Assert.Equal("正在输入的第二版", session.Displayed!.Questions[0].Answer);
        Assert.Equal("stable-four", calls[1].Chapter_Uid); Assert.False(session.HasPendingSave);
    });

    [Fact]
    public void Failed_save_keeps_input_and_network_retry_reuses_exact_request_id() => StaRunner.Run(() =>
    {
        var calls = new List<ProposalCommandDto>();
        using var session = Session(save: c =>
        {
            calls.Add(c);
            return calls.Count == 1 ? Task.FromException<ProposalViewDto>(new HttpRequestException("offline")) : Task.FromResult(View());
        });
        session.EditAnswer("question-one", "");
        var failed = session.FlushAsync(); Pump(failed);
        Assert.False(failed.Result); Assert.True(session.HasPendingSave); Assert.Equal("", session.Displayed!.Questions[0].Answer);
        var retry = session.FlushAsync(); Pump(retry); Assert.True(retry.Result);
        Assert.Equal(calls[0].Request_Id, calls[1].Request_Id); Assert.Equal("", calls[1].Answers["question-one"]);
    });

    [Fact]
    public void Read_only_view_cannot_change_answers() => StaRunner.Run(() =>
    {
        using var session = Session(); session.ReadOnly = true;
        session.EditAnswer("question-one", "禁止修改"); Assert.Equal("旧答案", session.Displayed!.Questions[0].Answer);
        session.EditPlotOutline("禁止修改"); Assert.StartsWith("保留来信秘密", session.Displayed.Plot_Outline);
        session.EditPlotOutline(""); Assert.StartsWith("保留来信秘密", session.Displayed.Plot_Outline);
        Assert.False(session.HasPendingSave);
    });

    [Fact]
    public void Structured_error_parser_keeps_both_source_quotes()
    {
        var error = NovelApiClient.ParseError("""{"detail":{"code":"PLAN_RHYTHM_MISMATCH","message":"明确差异","issues":[{"left":{"kind":"plan","field":"free_text","quote":"先揭晓"},"right":{"kind":"stack","track_id":"t","segment_id":"s","quote":"最后揭晓"},"reason":"顺序相反"}]}}""", "409");
        var result = Assert.IsType<ProposalException>(error);
        Assert.Equal("先揭晓", result.Issues[0].Left.Quote); Assert.Equal("s", result.Issues[0].Right.Segment_Id);
        Assert.IsType<ProposalException>(NovelApiClient.ParseError("""{"detail":{"code":"PROPOSAL_CHANGED","message":"刷新后重试"}}""", "409"));
    }

    [Theory]
    [InlineData("zh-CN", false)] [InlineData("en-US", true)] [InlineData("ja-JP", true)]
    public void Proposal_window_handles_narrow_long_content_and_read_only_state(string language, bool dark) => StaRunner.Run(() =>
    {
        using var session = Session(); session.ReadOnly = true;
        var window = new PlanProposalWindow(session, _ => Task.CompletedTask, () => { }, (_, _) => Task.CompletedTask);
        window.Resources.MergedDictionaries.Add(ThemeManager.CreatePalette(dark, ThemeManager.Zhusha));
        window.Resources.MergedDictionaries.Add(LanguageManager.LoadStrings(language));
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(560, 660)); content.Arrange(new Rect(0, 0, 560, 660)); content.UpdateLayout();
        Assert.True(window.Outline.IsReadOnly); Assert.False(window.ConfirmButton.IsEnabled);
        Assert.False(window.GenerateButton.IsEnabled); Assert.True(window.HistoryButton.IsEnabled);
        Assert.Equal(L.T("Proposal.Generate"), window.GenerateButton.Content);
        Assert.Equal(session.Displayed!.Plot_Outline, window.Outline.Text);
        Assert.True(window.Actions.ActualWidth <= 560);
        Assert.True(window.Actions.ActualHeight > 0);
        window.Close();
    });

    [Fact]
    public void Generate_button_stays_generate_and_draft_outline_is_editable() => StaRunner.Run(() =>
    {
        using var session = Session();
        var window = new PlanProposalWindow(session, _ => Task.CompletedTask, () => { }, (_, _) => Task.CompletedTask);
        Assert.False(window.Outline.IsReadOnly);
        Assert.Equal(L.T("Proposal.Generate"), window.GenerateButton.Content);
        window.Outline.Text = "作者手改的剧情";
        Assert.Equal("作者手改的剧情", session.Displayed!.Plot_Outline);
        Assert.True(session.HasPendingSave);
        Pump(session.FlushAsync());
        Assert.False(session.HasPendingSave);
        Assert.Equal("作者手改的剧情", session.Displayed.Plot_Outline);
        window.Close();
    });

    [Fact]
    public void Empty_proposal_window_shows_generate_and_does_not_adopt() => StaRunner.Run(() =>
    {
        var empty = View(); empty.Workspace.Draft = null; empty.Workspace.Adopted = null;
        empty.Draft_Status = new() { State = "none" }; empty.Adopted_Status = new() { State = "none" };
        using var session = Session(empty);
        var window = new PlanProposalWindow(session, _ => Task.CompletedTask, () => { }, (_, _) => Task.CompletedTask);
        Assert.True(window.Outline.IsReadOnly);
        Assert.Equal(Visibility.Visible, window.EmptyHint.Visibility);
        Assert.Equal(L.T("Proposal.Generate"), window.GenerateButton.Content);
        Assert.True(window.GenerateButton.IsEnabled);
        Assert.False(window.ConfirmButton.IsEnabled);
        window.Close();
    });

    [Fact]
    public void Clearing_outline_does_not_save_or_adopt_empty_text() => StaRunner.Run(() =>
    {
        using var session = Session();
        var window = new PlanProposalWindow(session, _ => Task.CompletedTask, () => { }, (_, _) => Task.CompletedTask);
        var previous = session.Displayed!.Plot_Outline;
        window.Outline.Text = "";
        Assert.False(session.HasPendingSave);
        Assert.Equal(previous, session.Displayed.Plot_Outline);
        Assert.False(window.ConfirmButton.IsEnabled);
        window.Close();
    });

    [Fact]
    public void Editing_adopted_outline_forks_local_draft() => StaRunner.Run(() =>
    {
        using var session = Session(View(true));
        session.EditPlotOutline(session.Displayed!.Plot_Outline);
        Assert.Null(session.Data.Workspace.Draft);
        session.EditPlotOutline("从已采用稿改出的草稿");
        Assert.StartsWith("保留来信秘密", session.Data.Workspace.Adopted!.Plot_Outline);
        Assert.Equal("从已采用稿改出的草稿", session.Data.Workspace.Draft!.Plot_Outline);
        Pump(session.FlushAsync());
        Assert.False(session.HasPendingSave);
        Assert.StartsWith("保留来信秘密", session.Data.Workspace.Adopted.Plot_Outline);
    });

    [Fact]
    public void Both_planning_entries_raise_requests_without_overwriting_form() => StaRunner.Run(() =>
    {
        var panel = new AiPanel(); var plan = new PlanZone(); var count = 0;
        panel.AiPlanRequested += (_, _) => count++; plan.ProposalRequested += (_, _) => count++;
        plan.Load(new() { Free_Text = "作者内容" }, new());
        panel.SetChapterState(true, true, true, false, false, false);
        panel.AiPlanButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        plan.ProposalButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(2, count); Assert.Equal("作者内容", plan.Read().Free_Text);
    });

    [Fact]
    public void Three_languages_have_matching_proposal_keys() => StaRunner.Run(() =>
    {
        var languages = new[] { "zh-CN", "en-US", "ja-JP" }.Select(LanguageManager.LoadStrings).ToList();
        foreach (var strings in languages)
            foreach (var key in new[] { "Proposal.Title", "Proposal.History", "Proposal.Restore", "Proposal.Reason.validation_changed", "Proposal.ReadOnly" })
                Assert.IsType<string>(strings[key]);
        var roots = new DirectoryInfo(AppContext.BaseDirectory);
        while (roots is not null && !Directory.Exists(Path.Combine(roots.FullName, "desktop-wpf", "Strings"))) roots = roots.Parent;
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var keys = new[] { "zh-CN", "en-US", "ja-JP" }.Select(l => XDocument.Load(Path.Combine(roots!.FullName,
            "desktop-wpf", "Strings", "PlanMode." + l + ".xaml")).Descendants().Select(e => (string?)e.Attribute(x + "Key")).OfType<string>().Order().ToArray()).ToArray();
        Assert.Equal(keys[0], keys[1]); Assert.Equal(keys[0], keys[2]);
    });

    [Fact]
    public void Option_and_free_answer_use_one_value_and_clear_is_saved() => StaRunner.Run(() =>
    {
        using var session = Session();
        var window = new PlanProposalWindow(session, _ => Task.CompletedTask, () => { }, (_, _) => Task.CompletedTask);
        var group = Assert.IsType<StackPanel>(window.QuestionsPanel.Children[0]);
        var options = group.Children.OfType<WrapPanel>().Single();
        var button = Assert.IsType<Button>(options.Children[0]);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var box = group.Children.OfType<TextBox>().Single();
        Assert.Equal("一封旧信", box.Text); Assert.Equal("一封旧信", session.Displayed!.Questions[0].Answer);
        box.Text = ""; Assert.True(session.HasPendingSave); Pump(session.FlushAsync());
        Assert.Equal("", session.Displayed.Questions[0].Answer); window.Close();
    });

    [Fact]
    public void History_restore_is_explicit_and_current_references_cannot_be_deleted() => StaRunner.Run(() =>
    {
        var initial = View(); initial.Workspace.Draft!.Active_History_Id = "current";
        using var session = Session(initial);
        var calls = new List<string>();
        var history = new ProposalHistoryWindow(session, () => Task.FromResult(new List<ProposalHistoryDto> {
            new() { Record_Id = "current", Action = "generated", Protected = true } }),
            _ => Task.FromResult(new ProposalRecordDto { Record_Id = "current", Proposal = Proposal() }),
            action => { calls.Add(action); return Task.CompletedTask; });
        Pump(history.ReloadAsync()); Assert.False(history.DeleteButton.IsEnabled); Assert.True(history.RestoreButton.IsEnabled);
        Assert.Empty(calls);
        history.RestoreButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(Dispatcher.CurrentDispatcher.InvokeAsync(() => { }).Task);
        Assert.Equal(new[] { "restore:current" }, calls); history.Close();
    });

    [Fact]
    public void Forking_adopted_answers_keeps_the_same_text_editor_during_autosave() => StaRunner.Run(() =>
    {
        using var session = Session(View(true), c =>
        {
            var response = View(); response.Workspace.Draft!.Id = "server-forked-draft";
            response.Workspace.Draft.Questions[0].Answer = c.Answers["question-one"];
            response.Workspace.Adopted = Proposal(); return Task.FromResult(response);
        });
        var window = new PlanProposalWindow(session, _ => Task.CompletedTask, () => { }, (_, _) => Task.CompletedTask);
        TextBox Editor() => ((StackPanel)window.QuestionsPanel.Children[0]).Children.OfType<TextBox>().Single();
        var editor = Editor(); editor.Text = "修改采用稿答案时继续输入";
        Assert.Same(editor, Editor()); Pump(session.FlushAsync()); Assert.Same(editor, Editor());
        Assert.Equal("修改采用稿答案时继续输入", editor.Text);
        Assert.Equal("旧答案", session.Data.Workspace.Adopted!.Questions[0].Answer); window.Close();
    });

    [Fact]
    public void Lost_response_retry_acknowledges_only_old_input_then_saves_new_typing() => StaRunner.Run(() =>
    {
        var calls = new List<ProposalCommandDto>();
        using var session = Session(save: c =>
        {
            calls.Add(c);
            if (calls.Count == 1) return Task.FromException<ProposalViewDto>(new HttpRequestException("response lost"));
            var saved = View(); saved.Workspace.Revision = calls.Count - 1;
            saved.Workspace.Draft!.Questions[0].Answer = c.Answers["question-one"];
            return Task.FromResult(saved);
        });
        session.EditAnswer("question-one", "已到服务器的第一版"); Pump(session.FlushAsync());
        session.EditAnswer("question-one", "失败后继续输入的第二版"); Pump(session.FlushAsync());
        Assert.Equal(3, calls.Count); Assert.Equal(calls[0].Request_Id, calls[1].Request_Id);
        Assert.NotEqual(calls[1].Request_Id, calls[2].Request_Id);
        Assert.Equal("失败后继续输入的第二版", session.Displayed!.Questions[0].Answer); Assert.False(session.HasPendingSave);
    });

    [Fact]
    public void Revision_conflict_refreshes_once_and_rebases_only_known_question_ids() => StaRunner.Run(() =>
    {
        var server = View(); server.Workspace.Revision = 2; var attempts = 0;
        using var session = new ProposalSession("project", "作品", View(), c =>
        {
            if (++attempts == 1) return Task.FromException<ProposalViewDto>(new ProposalException("PROPOSAL_CHANGED", "changed"));
            Assert.Equal(2, c.Expected_Revision); server.Workspace.Revision++;
            server.Workspace.Draft!.Questions[0].Answer = c.Answers["question-one"];
            return Task.FromResult(Copy(server));
        }, (_, _) => Task.FromResult(Copy(server)), () => Task.FromResult(Copy(server)));
        session.EditAnswer("question-one", "保留作者刚写的答案"); Pump(session.FlushAsync());
        Assert.Equal(2, attempts); Assert.False(session.HasPendingSave); Assert.Equal("保留作者刚写的答案", session.Displayed!.Questions[0].Answer);
    });

    [Fact]
    public void Lost_model_response_reuses_request_id_and_committed_retry_replays_receipt() => StaRunner.Run(() =>
    {
        var calls = new List<ProposalCommandDto>(); var server = View();
        using var session = new ProposalSession("project", "作品", Copy(server), c => Task.FromResult(Copy(server)),
            (_, c) => { calls.Add(c); return calls.Count == 1 ? Task.FromException<ProposalViewDto>(new HttpRequestException("lost")) : Task.FromResult(Copy(server)); },
            () => Task.FromResult(Copy(server)));
        Pump(session.ExecuteAsync("generate")); Pump(session.RefreshAsync()); Pump(session.ExecuteAsync("generate"));
        Assert.Equal(2, calls.Count); Assert.Equal(calls[0].Request_Id, calls[1].Request_Id);
        // A second lost response was actually committed on the server. The HTTP
        // retry must resolve its exact receipt without starting another model call.
        var receipts = new HashSet<string>(); var modelCalls = 0;
        using var committed = new ProposalSession("project", "作品", View(), c => Task.FromResult(Copy(server)), (_, c) =>
        {
            calls.Add(c);
            if (!receipts.Add(c.Request_Id)) return Task.FromResult(Copy(server));
            modelCalls++; server.Workspace.Revision = 1;
            return Task.FromException<ProposalViewDto>(new HttpRequestException("committed response lost"));
        }, () => Task.FromResult(Copy(server)));
        Pump(committed.ExecuteAsync("generate")); var before = calls.Count;
        Pump(committed.RefreshAsync()); Pump(committed.ExecuteAsync("generate"));
        Assert.Equal(before + 1, calls.Count); Assert.Equal(1, modelCalls);
        Assert.Equal(calls[^2].Request_Id, calls[^1].Request_Id); Assert.Equal(1, committed.Data.Workspace.Revision);
    });

    [Fact]
    public void Unrelated_revision_change_cannot_acknowledge_a_lost_operation() => StaRunner.Run(() =>
    {
        var server = View(); var calls = new List<ProposalCommandDto>();
        using var session = new ProposalSession("project", "作品", Copy(server), c => Task.FromResult(Copy(server)), (_, c) =>
        {
            calls.Add(c);
            return calls.Count == 1
                ? Task.FromException<ProposalViewDto>(new HttpRequestException("request did not reach server"))
                : Task.FromException<ProposalViewDto>(new ProposalException("PROPOSAL_CHANGED", "another operation changed the revision"));
        }, () => Task.FromResult(Copy(server)));
        Pump(session.ExecuteAsync("confirm"));
        // An answer save in another window is not evidence that confirm succeeded.
        server.Workspace.Revision = 1; Pump(session.RefreshAsync());
        var retry = session.ExecuteAsync("confirm"); Pump(retry);
        Assert.False(retry.Result); Assert.Equal(2, calls.Count);
        Assert.Equal(calls[0].Request_Id, calls[1].Request_Id);
        Assert.NotEqual("confirm", session.Data.Operation);
    });

    [Fact]
    public void Rebasing_unsaved_answers_onto_adopted_snapshot_does_not_mutate_it() => StaRunner.Run(() =>
    {
        var server = View(true); server.Workspace.Revision = 1; var attempts = 0;
        using var session = new ProposalSession("project", "作品", View(), c =>
            Task.FromException<ProposalViewDto>(++attempts == 1
                ? new ProposalException("PROPOSAL_CHANGED", "confirmed in another window")
                : new HttpRequestException("save failed")), (_, c) => Task.FromResult(Copy(server)), () => Task.FromResult(Copy(server)));
        session.EditAnswer("question-one", "尚未保存的新草稿答案"); Pump(session.FlushAsync());
        Assert.True(session.HasPendingSave);
        Assert.Equal("旧答案", session.Data.Workspace.Adopted!.Questions[0].Answer);
        Assert.True(session.HasDraft);
        Assert.Equal("尚未保存的新草稿答案", session.Data.Workspace.Draft!.Questions[0].Answer);
    });
}
