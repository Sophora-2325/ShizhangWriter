using System.IO;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Input;
using NovelGm.Desktop.Api;

namespace NovelGm.Desktop;

public partial class App : Application
{
    public static DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    private readonly bool _previewMode;

    public App() : this(false) { }

    private App(bool previewMode)
    {
        _previewMode = previewMode;
        UiPrefs.PersistenceEnabled = !previewMode;
        OwnedWindowFix.Install();
        EventManager.RegisterClassHandler(typeof(Window), Keyboard.PreviewKeyDownEvent,
            new KeyEventHandler((sender, e) =>
            {
                if (sender is Window window && !ReferenceEquals(window, Current?.MainWindow) &&
                    e.Key == Key.System && e.SystemKey == Key.F4)
                {
                    // Consume auxiliary Alt+F4 locally, including cancelled/async saves.
                    e.Handled = true;
                    window.Close();
                }
            }));
    }

    // 保持真实 App 类型以加载编译后的 XAML；预览不能启动后端或读取用户配置。
    internal static App CreatePreview() => new(true);

    public static BackendLauncher Backend { get; private set; } = null!;
    public static NovelApiClient Api { get; private set; } = null!;

    private static string CrashLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "novel-gm", "ui-errors.log");

    protected override async void OnStartup(StartupEventArgs e)
    {
        if (_previewMode) return;
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogError(args.Exception);
            args.SetObserved();
        };
        UiPrefs.Load();
        LanguageManager.Init();
        ThemeManager.Init();
        Backend = new BackendLauncher();
        Api = new NovelApiClient();
        try
        {
            await Backend.EnsureRunningAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                L.T("App.BackendFailed", ex.Message),
                L.T("App.Title"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var main = new MainWindow();
        BindPrimaryWindow(main);
        main.Show();
    }

    // Startup dialogs and auxiliary editors must never determine application lifetime.
    // Auxiliary editors belong directly to the primary window, not to each other.
    internal void BindPrimaryWindow(Window main)
    {
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
    }

    /// <summary>界面线程漏网的异常：记日志并提示，不让整个程序退出（未保存的正文还在编辑器里）。</summary>
    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogError(e.Exception);
        e.Handled = true;
        try
        {
            MessageBox.Show(L.T("App.UnhandledError", e.Exception.Message, CrashLogPath), L.T("App.Title"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch { /* 提示失败不再抛 */ }
    }

    private static void LogError(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            File.AppendAllText(CrashLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* 日志写不进去不影响使用 */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Api?.Dispose(); } catch { /* ignore */ }
        try { Backend?.Dispose(); } catch { /* ignore */ }
        base.OnExit(e);
    }
}
