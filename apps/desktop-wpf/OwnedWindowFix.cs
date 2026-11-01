using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace NovelGm.Desktop;

/// <summary>
/// 关掉带所有者的子窗口时，Windows 会把所有者压到其他窗口后面，看起来像被收到了任务栏。
/// 无模式窗口在真正关掉之前先解除归属，关掉后再把还开着的窗口拉回前台。所有者自己正在关闭时不动它。
/// </summary>
internal static class OwnedWindowFix
{
    private static readonly FieldInfo? ShowingAsDialog = typeof(Window).GetField(
        "_showingAsDialog", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly DependencyProperty GuardProperty = DependencyProperty.RegisterAttached(
        "Guard", typeof(bool), typeof(OwnedWindowFix));
    private static readonly DependencyProperty WatchedProperty = DependencyProperty.RegisterAttached(
        "Watched", typeof(bool), typeof(OwnedWindowFix));
    private static readonly DependencyProperty ClosingStartedProperty = DependencyProperty.RegisterAttached(
        "ClosingStarted", typeof(bool), typeof(OwnedWindowFix));
    private static bool _installed;

    internal static void Install()
    {
        if (_installed) return;
        _installed = true;
        // 有些无模式窗口的 Loaded 不会走到类处理程序，显示和激活消息里再挂一次。
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded), true);
        ComponentDispatcher.ThreadFilterMessage += OnFilter;
    }

    internal static void Hide(Window window)
    {
        var owner = window.Owner;
        var state = owner?.WindowState ?? WindowState.Normal;
        window.Hide();
        Schedule(owner, state, window);
    }

    private static void OnFilter(ref MSG msg, ref bool handled)
    {
        if (WindowFrom(msg.hwnd) is Window window) TryAttach(window);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window) TryAttach(window);
    }

    private static void TryAttach(Window window)
    {
        if (window.Owner is not Window owner || (bool)window.GetValue(GuardProperty)) return;
        window.SetValue(GuardProperty, true);
        Watch(owner);
        var state = owner.WindowState;
        Window? remembered = owner;
        window.Closing += (_, args) =>
        {
            if (args.Cancel || window.Owner is not Window current || (bool)current.GetValue(ClosingStartedProperty)) return;
            state = current.WindowState;
            remembered = current;
            if (IsDialog(window)) return;
            // 句柄销毁前解除归属，Windows 才不会在关掉最后一个子窗口时把所有者压到后面。
            var next = Next(current, window);
            window.Owner = null;
            Bring(next ?? current);
        };
        window.Closed += (_, _) => Schedule(remembered, state, window);
    }

    private static void Watch(Window owner)
    {
        if ((bool)owner.GetValue(WatchedProperty)) return;
        owner.SetValue(WatchedProperty, true);
        owner.Closing += (_, args) =>
        {
            if (!args.Cancel) owner.SetValue(ClosingStartedProperty, true);
        };
    }

    private static void Schedule(Window? owner, WindowState state, Window closing)
    {
        if (owner is null || (bool)owner.GetValue(ClosingStartedProperty) || !owner.IsVisible) return;
        var remembered = owner;
        var closed = closing;
        remembered.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            if ((bool)remembered.GetValue(ClosingStartedProperty) || !remembered.IsVisible) return;
            if (state != WindowState.Minimized && remembered.WindowState == WindowState.Minimized)
                remembered.WindowState = state;
            Bring(Next(remembered, closed) ?? remembered);
        });
    }

    private static Window? Next(Window owner, Window closing) =>
        owner.OwnedWindows.OfType<Window>().LastOrDefault(w => !ReferenceEquals(w, closing) && w.IsVisible && w.IsLoaded);

    private static bool IsDialog(Window window) => ShowingAsDialog?.GetValue(window) is true;

    private static Window? WindowFrom(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        try
        {
            if (HwndSource.FromHwnd(hwnd)?.RootVisual is Window direct) return direct;
            var root = GetAncestor(hwnd, 2);
            if (root != IntPtr.Zero && root != hwnd && HwndSource.FromHwnd(root)?.RootVisual is Window ancestor) return ancestor;
        }
        catch (ArgumentException) { }
        return null;
    }

    private static void Bring(Window window)
    {
        if (!window.IsVisible) return;
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        if (hwnd == IntPtr.Zero) return;
        window.Activate();
        var foreground = GetForegroundWindow();
        if (foreground == hwnd) return;
        var foregroundThread = GetWindowThreadProcessId(foreground, out _);
        var current = GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != current &&
            AttachThreadInput(current, foregroundThread, true);
        try { SetForegroundWindow(hwnd); }
        finally { if (attached) AttachThreadInput(current, foregroundThread, false); }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
