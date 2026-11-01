using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;
using System.Windows.Threading;
using Xunit;

namespace NovelGm.Desktop.Tests;

public class OwnedWindowCloseTests
{
    [Fact]
    public void Closing_an_owned_window_keeps_its_owner_in_front() => StaRunner.Run(() =>
    {
        if (Application.Current is null) new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        OwnedWindowFix.Install();

        var failures = new List<string>();
        Check("modal", static (_, child) =>
        {
            child.Loaded += (_, _) => child.Dispatcher.BeginInvoke(child.Close);
            child.ShowDialog();
        }, failures);
        Check("modeless", static (_, child) =>
        {
            child.Show();
            Pump();
            ForceForeground(new WindowInteropHelper(child).Handle);
            Pump();
            child.Close();
        }, failures);
        Check("modeless-after-dialog", static (owner, child) =>
        {
            child.Show();
            var dialog = new Window { Owner = owner, Width = 240, Height = 140, ShowInTaskbar = false, Title = "dialog" };
            dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(dialog.Close);
            dialog.ShowDialog();
            child.Close();
        }, failures);
        Check("second-modeless", static (owner, child) =>
        {
            child.Show();
            var other = new Window { Owner = owner, Width = 240, Height = 140, ShowInTaskbar = false, Title = "other" };
            other.Show();
            Pump();
            other.Close();
            Pump();
            child.Close();
        }, failures);
        Check("hide", static (_, child) =>
        {
            child.Show();
            Pump();
            OwnedWindowFix.Hide(child);
        }, failures);
        Assert.True(failures.Count == 0, string.Join(" | ", failures));

        var root = ShowOwner(false);
        var block = true;
        var cancelled = new Window { Owner = root, Width = 200, Height = 120, Title = "cancel" };
        cancelled.Closing += (_, e) => { if (block) e.Cancel = true; };
        cancelled.Show();
        cancelled.Close();
        Pump();
        Assert.True(cancelled.IsVisible);
        Assert.Same(root, cancelled.Owner);
        block = false;
        root.Close();
        Pump();
        Assert.False(root.IsVisible);
        Assert.False(cancelled.IsVisible);
    });

    private static void Check(string name, Action<Window, Window> close, List<string> failures)
    {
        var owner = ShowOwner(name != "modeless");
        var hwnd = new WindowInteropHelper(owner).Handle;
        ForceForeground(hwnd);
        Pump();
        var child = new Window
        {
            Owner = owner, Width = 320, Height = 200, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Title = name,
        };
        try
        {
            close(owner, child);
            Pump();
            var foreground = GetForegroundWindow();
            var kept = owner.WindowState != WindowState.Minimized &&
                (foreground == hwnd || ForegroundBelongsTo(owner, foreground));
            if (!kept) failures.Add($"{name}: state={owner.WindowState} foreground={foreground} owner={hwnd}");
        }
        catch (Exception ex) { failures.Add(name + ": " + ex.Message); }
        finally
        {
            try { if (child.IsLoaded) child.Close(); } catch (InvalidOperationException) { }
            try { if (owner.IsLoaded) owner.Close(); } catch (InvalidOperationException) { }
            Pump();
        }
    }

    private static bool ForegroundBelongsTo(Window owner, IntPtr foreground)
    {
        if (new WindowInteropHelper(owner).Handle == foreground) return true;
        return owner.OwnedWindows.OfType<Window>().Any(w => w.IsVisible && new WindowInteropHelper(w).Handle == foreground);
    }

    private static Window ShowOwner(bool maximized)
    {
        var owner = new Window { Width = 900, Height = 600, Title = "owner", WindowStartupLocation = WindowStartupLocation.CenterScreen };
        WindowChrome.SetWindowChrome(owner, new WindowChrome
        {
            CaptionHeight = 36, ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false,
        });
        owner.Show();
        if (maximized) owner.WindowState = WindowState.Maximized;
        return owner;
    }

    private static void Pump() =>
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.SystemIdle, new Action(() => { }));

    private static void ForceForeground(IntPtr hwnd)
    {
        var foreground = GetForegroundWindow();
        var foregroundThread = GetWindowThreadProcessId(foreground, out _);
        AttachThreadInput(GetCurrentThreadId(), foregroundThread, true);
        SetForegroundWindow(hwnd);
        AttachThreadInput(GetCurrentThreadId(), foregroundThread, false);
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
