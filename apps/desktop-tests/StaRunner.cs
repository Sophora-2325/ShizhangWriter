using System;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Xunit;

namespace NovelGm.Desktop.Tests;

/// <summary>在独立 STA 线程上跑 WPF 测试。结束时关掉本线程残留的窗口并停掉 Dispatcher，
/// 否则 testhost 退出时还挂着活的 HwndSource / 计时器，偶发在进程关闭阶段崩溃。</summary>
internal static class StaRunner
{
    public static void Run(Action action, TimeSpan? timeout = null, string message = "WPF operation timed out")
    {
        Exception? error = null;
        Dispatcher? dispatcher = null;
        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            try { action(); }
            catch (Exception ex) { error = ex; }
            finally { Shutdown(dispatcher); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(timeout ?? TimeSpan.FromSeconds(120)))
        {
            if (dispatcher is { HasShutdownStarted: false } stuck) stuck.BeginInvokeShutdown(DispatcherPriority.Send);
            Assert.Fail(message);
        }
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static void Shutdown(Dispatcher dispatcher)
    {
        try
        {
            if (Application.Current is { } app && app.Dispatcher == dispatcher)
                foreach (var window in app.Windows.OfType<Window>().ToList())
                    window.Close();
        }
        catch (Exception) { }
        try { dispatcher.InvokeShutdown(); }
        catch (Exception) { }
    }
}
