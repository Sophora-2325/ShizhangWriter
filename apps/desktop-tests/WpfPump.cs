using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;

namespace NovelGm.Desktop.Tests;

internal static class WpfPump
{
    public static void Until(Func<bool> ready)
    {
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(5) };
        timer.Tick += (_, _) => { if (ready() || DateTime.UtcNow >= deadline) frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        if (!ready()) throw new TimeoutException("WPF operation did not complete.");
    }

    public static T Wait<T>(Task<T> task)
    {
        Until(() => task.IsCompleted);
        return task.GetAwaiter().GetResult();
    }

    public static void Images(FlowDocument document) => Until(() => document.Blocks.All(block => block switch
    {
        BlockUIContainer { Child: Image image } => image.Source is not null,
        Paragraph paragraph => paragraph.Inlines.OfType<InlineUIContainer>().All(inline => inline.Child is not Image img || img.Source is not null),
        _ => true,
    }));
}
