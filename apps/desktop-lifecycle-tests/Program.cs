using System.Runtime.InteropServices;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NovelGm.Desktop;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Controls;
using NovelGm.Desktop.Models;

internal static class Program
{
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    private static App _app = null!;
    private static bool _completed;
    [STAThread]
    public static int Main()
    {
        _app = App.CreatePreview(); _app.InitializeComponent();
        var primary = new Window { Title = "Lifecycle regression (isolated)", Width = 520, Height = 340 };
        _app.BindPrimaryWindow(primary);
        primary.Show();
        _app.Dispatcher.InvokeAsync(async () =>
        {
            try { await Exercise(primary); _completed = true; primary.Close(); }
            catch (Exception error) { Console.Error.WriteLine(error); _app.Shutdown(1); }
        });
        var result = _app.Run();
        if (!_completed) return 1;
        Console.WriteLine("PASS: native child close, local Close button, Alt+F4 messages, three domains, zone window, shared saves, failure/retry, reopen, primary-only application exit");
        return result;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Until(Func<bool> done)
    {
        var limit = DateTime.UtcNow.AddSeconds(8);
        while (!done()) { if (DateTime.UtcNow > limit) throw new TimeoutException("Window lifecycle operation timed out"); await Task.Delay(15); }
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
    private static void NativeClose(Window window, bool altF4 = false)
    {
        if (altF4) window.Activate();
        var hwnd = new WindowInteropHelper(window).Handle;
        // SC_CLOSE is the native titlebar/system-menu action. WM_SYSKEYDOWN with
        // the Alt context bit exercises the same native route as Alt+F4.
        Require(PostMessage(hwnd, altF4 ? 0x0104u : 0x0112u,
            (IntPtr)(altF4 ? 0x73 : 0xF060), altF4 ? (IntPtr)0x203E0001 : IntPtr.Zero), "PostMessage failed");
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var item in Descendants<T>(child)) yield return item;
        }
    }
    private static async Task Exercise(Window primary)
    {
        var primaryHandle = new WindowInteropHelper(primary).Handle;
        var chapter = new ChapterDto { No = 1, Title = "窗口隔离" };
        foreach (var domain in new[] { "thread", "character", "scene" })
            chapter.Stacked_Constraints.Tracks.Add(new ConstraintTrackDto { Id = domain, Domain = domain,
                Element_Id = domain == "scene" ? "suspense" : domain, Name = domain,
                Segments = new() { new() { Start = 10, End = 30, Directive = "保留作者内容" } } });
        TaskCompletionSource<ChapterDto>? deferred = null;
        var fail = false;
        using var session = new StackedConstraintSession("isolated-probe", chapter, new LoreDto(), value =>
            deferred?.Task ?? (fail ? Task.FromException<ChapterDto>(new IOException("simulated offline"))
                : Task.FromResult(new ChapterDto { Stacked_Constraints = value })));
        StackedConstraintsWindow Open(string? domain = null)
        {
            var window = new StackedConstraintsWindow(session, domain) { Owner = primary };
            window.Show(); return window;
        }
        var overview = Open(); var threads = Open("thread"); var characters = Open("character"); var scenes = Open("scene");
        void CheckPrimary(params Window[] siblings)
        {
            Require(primary.IsVisible && IsWindow(primaryHandle), "Closing an auxiliary window closed the primary window");
            Require(ReferenceEquals(_app.MainWindow, primary), "An auxiliary window replaced MainWindow");
            Require(!_app.Dispatcher.HasShutdownStarted, "Auxiliary close started application shutdown");
            Require(siblings.All(w => w.IsVisible), "Closing one auxiliary window closed a sibling");
        }
        NativeClose(threads); await Until(() => !threads.IsVisible); CheckPrimary(overview, characters, scenes);
        NativeClose(characters, true); await Until(() => !characters.IsVisible); CheckPrimary(overview, scenes);
        var closeButton = Descendants<Button>(scenes).Single(b => Equals(b.Content, L.T("Common.Close")));
        closeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => !scenes.IsVisible); CheckPrimary(overview);
        var zone = new ZoneWindow("isolated", "本章规划", new PlanZone()) { Owner = primary };
        zone.Show(); NativeClose(zone, true); await Until(() => !zone.IsVisible); CheckPrimary(overview);

        threads = Open("thread");
        deferred = new TaskCompletionSource<ChapterDto>();
        session.Edit(data => data.Tracks[0].Weight = 0.7);
        NativeClose(overview); await Until(() => session.IsSaving);
        NativeClose(overview, true); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        CheckPrimary(overview, threads);  // Closing is cancelled while the save awaits IO.
        deferred.SetException(new IOException("simulated offline")); deferred = null;
        await Until(() => !session.IsSaving && session.Error is not null);
        CheckPrimary(overview, threads); Require(session.IsDirty, "Failure discarded the draft");
        NativeClose(overview); await Until(() => !overview.IsVisible);
        CheckPrimary(threads); Require(!session.HasPendingSave, "Close did not finish saving");
        overview = Open(); CheckPrimary(overview, threads);
        Require(ReferenceEquals(overview.Session, threads.Session) && overview.Session.Data.Tracks[0].Weight == 0.7,
            "Reopen destroyed the shared session");
        // Domain close must save the shared draft too, without closing the overview.
        session.Edit(data => data.Tracks[1].Weight = 0.1);
        NativeClose(threads, true); await Until(() => !threads.IsVisible);
        CheckPrimary(overview); Require(!session.HasPendingSave, "Domain close skipped shared save");
        Require(_app.ShutdownMode == ShutdownMode.OnMainWindowClose, "Wrong application exit policy");
        await ExerciseProposals(primary, overview);
        await ExerciseBodyRevisions(primary, overview);
        Require(App.Api is null && App.Backend is null, "Probe touched production backend");
    }

    private static async Task ExerciseBodyRevisions(Window primary, Window sibling)
    {
        var server = new BodyRevisionViewDto { Writable = true, Preview_Valid = true, Input_Token = "probe-input",
            Chapter = new() { No = 12, Chapter_Uid = "revision-probe", Title = "夜访", Body_Text = "天还没亮，夕阳照进屋里。" },
            Workspace = new() { Preview_Id = "preview", Instruction = "统一时间，保留人物动机。", Patches = new() {
                new() { Id = "patch", Before = "天还没亮，夕阳照进屋里。", After = "天还没亮，窗外只有微光。", Reason = "统一同一场景的时间表述。", Start = 0, End = 13 } } } };
        TaskCompletionSource<BodyRevisionViewDto>? deferred = null;
        using var session = new BodyRevisionSession("revision-probe", server, (_, cmd) => deferred?.Task ?? Task.FromResult(new BodyRevisionViewDto {
            Chapter = server.Chapter, Workspace = new() { Revision = cmd.Expected_Revision + 1, Instruction = cmd.Instruction, Preview_Id = "preview", Patches = server.Workspace.Patches },
            Writable = true, Preview_Valid = true, Input_Token = "probe-input" }), () => Task.FromResult(server));
        BodyRevisionWindow Open() { var window = new BodyRevisionWindow(session, (_, _) => Task.CompletedTask) { Owner = primary }; window.Show(); return window; }
        var first = Open(); var second = Open();
        NativeClose(first, true); await Until(() => !first.IsVisible);
        Require(primary.IsVisible && sibling.IsVisible && second.IsVisible, "Revision Alt+F4 affected a sibling");
        first = Open(); deferred = new(); session.Edit("保存失败也要保留的要求", Array.Empty<string>());
        NativeClose(first); await Task.Delay(70);
        Require(first.IsVisible && second.IsVisible && primary.IsVisible, "Revision close did not await saving");
        deferred.SetException(new IOException("revision offline")); deferred = null;
        await Until(() => session.Error is not null);
        Require(first.IsVisible && session.Instruction.Contains("保留"), "Revision failed close discarded draft");
        NativeClose(first); await Until(() => !first.IsVisible);
        Require(second.IsVisible && sibling.IsVisible && primary.IsVisible && !session.HasPendingSave, "Revision close affected shared session");
        second.CloseSaved();
        first = Open(); Require(first.Instruction.Text.Contains("保留"), "Revision reopen lost draft"); first.CloseSaved();
        // Capture actual WPF controls from visible native windows, without a backend.
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.impeccable/review"));
        Directory.CreateDirectory(directory);
        static void Capture(FrameworkElement element, string path)
        {
            element.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(element); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(path); encoder.Save(output);
        }
        foreach (var dark in new[] { false, true })
        {
            ThemeManager.Apply(dark ? "dark" : "light");
            foreach (var width in new[] { 240, 300 })
            {
                var panel = new AiPanel(); panel.SetChapterState(true, false, true, true, false, false); panel.SetWorkflow(server.Chapter);
                panel.SetProposalStatus("方案2", true);
                var host = new Window { Owner = primary, Content = panel, Width = width + 16, Height = 810, ShowInTaskbar = false };
                host.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Capture(panel, Path.Combine(directory, $"ai-{width}-{(dark ? "dark" : "light")}.png")); host.Close();
            }
            var window = Open(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Capture((FrameworkElement)window.Content, Path.Combine(directory, $"revision-{(dark ? "dark" : "light")}.png")); window.CloseSaved();
        }
        ThemeManager.Apply("light");
    }

    private static async Task ExerciseProposals(Window primary, Window stackSibling)
    {
        var server = new ProposalViewDto { Chapter = new() { No = 2, Chapter_Uid = "probe-two", Title = "方案窗口" },
            Writable = true, Workspace = new() { Draft = new() { Id = "draft", Chapter_Uid = "probe-two", Plot_Outline = "只读剧情草案",
                Questions = new() { new() { Id = "q", Question = "是否保留秘密？", Answer = "" } } } },
            Draft_Status = new() { State = "draft" } };
        TaskCompletionSource<ProposalViewDto>? deferred = null;
        using var session = new ProposalSession("proposal-probe", "隔离作品", server, cmd =>
            deferred?.Task ?? Task.FromResult(new ProposalViewDto { Chapter = server.Chapter, Workspace = server.Workspace, Writable = true }),
            (_, _) => Task.FromResult(server), () => Task.FromResult(server));
        PlanProposalWindow Open() { var w = new PlanProposalWindow(session, _ => Task.CompletedTask, () => { }, (_, _) => Task.CompletedTask) { Owner = primary }; w.Show(); return w; }
        ProposalHistoryWindow History() { var w = new ProposalHistoryWindow(session, () => Task.FromResult(new List<ProposalHistoryDto>()),
            _ => Task.FromResult(new ProposalRecordDto()), _ => Task.CompletedTask) { Owner = primary }; w.Show(); return w; }
        var proposal = Open(); var history = History();
        NativeClose(proposal); await Until(() => !proposal.IsVisible);
        Require(primary.IsVisible && history.IsVisible && stackSibling.IsVisible, "Proposal close affected another window");
        proposal = Open(); NativeClose(history, true); await Until(() => !history.IsVisible);
        Require(primary.IsVisible && proposal.IsVisible, "History Alt+F4 affected primary or proposal");
        history = History(); deferred = new TaskCompletionSource<ProposalViewDto>();
        session.EditAnswer("q", "保留这个未保存答案");
        NativeClose(proposal, true); await Until(() => session.HasPendingSave);
        await Task.Delay(60);
        Require(proposal.IsVisible && history.IsVisible && primary.IsVisible, "Proposal did not wait for its shared save");
        deferred.SetException(new IOException("proposal simulated offline")); deferred = null;
        await Until(() => session.Error is not null);
        Require(proposal.IsVisible && session.Displayed!.Questions[0].Answer == "保留这个未保存答案", "Failed close lost proposal answers");
        NativeClose(proposal); await Until(() => !proposal.IsVisible);
        Require(primary.IsVisible && history.IsVisible && stackSibling.IsVisible, "Retry close affected siblings");
        proposal = Open();
        Require(ReferenceEquals(proposal.Session, history.Session), "Proposal and history stopped sharing their session");
        var close = Descendants<Button>(proposal).Single(b => Equals(b.Content, L.T("Common.Close")));
        close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => !proposal.IsVisible);
        NativeClose(history); await Until(() => !history.IsVisible);
        Console.WriteLine("PASS: proposal/history native titlebar, Close button, Alt+F4, save-in-flight, failed close, retry and shared reopen");
    }
}
