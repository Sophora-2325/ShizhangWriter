using System.Windows;
using System.Windows.Controls;

namespace NovelGm.Desktop;

/// <summary>创作区某一区弹出后所在的子窗口；关闭时由主窗口把内容放回创作区。</summary>
public sealed class ZoneWindow : Window
{
    /// <summary>本次运行中每个区上次的窗口位置和大小。</summary>
    private static readonly Dictionary<string, Rect> Bounds = new();

    private readonly DockPanel _root = new();
    private readonly string _key;

    public ZoneWindow(string key, string title, FrameworkElement content, FrameworkElement? top = null)
    {
        _key = key;
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Title = title;
        MinWidth = 320;
        MinHeight = 240;
        if (top is not null)
        {
            DockPanel.SetDock(top, Dock.Top);
            _root.Children.Add(top);
        }
        _root.Children.Add(content);
        Content = _root;

        if (Bounds.TryGetValue(key, out var r))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = r.Left;
            Top = r.Top;
            Width = r.Width;
            Height = r.Height;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Width = 900;
            Height = 680;
        }
        Closing += (_, _) => Bounds[_key] = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;
    }

    /// <summary>取下窗口里的内容，好放回主窗口。</summary>
    public void Detach() => _root.Children.Clear();
}
