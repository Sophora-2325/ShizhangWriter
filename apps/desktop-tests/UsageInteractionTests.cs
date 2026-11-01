using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using NovelGm.Desktop.Controls;
using Xunit;

namespace NovelGm.Desktop.Tests;

public class UsageInteractionTests
{
    [Fact]
    public void Releasing_a_click_opens_details_and_next_click_closes_them() => StaRunner.Run(() =>
    {
        var app = App.CreatePreview();
        app.InitializeComponent();
        var bubble = new UsageBubble();
        var host = new Window { Content = bubble, Width = 800, Height = 600,
            Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        host.Show(); host.UpdateLayout();
        bubble.SetDefaultPosition(new Point(200, 400));
        var button = (Button)bubble.FindName("Capsule");
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var arm = typeof(ButtonBase).GetMethod("SetIsPressed", flags)!;
        var release = typeof(ButtonBase).GetMethod("OnMouseLeftButtonUp", flags)!;

        void ReleaseClick()
        {
            Assert.True(button.CaptureMouse());
            arm.Invoke(button, new object[] { true });
            Assert.True(button.IsPressed);
            var preview = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent };
            button.RaiseEvent(preview);
            // Let WPF's real ButtonBase decide whether to raise Click after the preview route.
            release.Invoke(button, new object[] {
                new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                    { RoutedEvent = UIElement.MouseLeftButtonUpEvent } });
            host.UpdateLayout();
        }

        try
        {
            ReleaseClick();
            Assert.True(bubble.IsExpanded, "Mouse release must reach ButtonBase while it is still armed.");
            ReleaseClick();
            Assert.False(bubble.IsExpanded);
        }
        finally { host.Close(); }
    });
}
