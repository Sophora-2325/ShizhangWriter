using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NovelGm.Desktop.Api;
using Xunit;

namespace NovelGm.Desktop.Tests;

[CollectionDefinition("serial-body-editing", DisableParallelization = true)]
public sealed class SerialBodyEditingCollection;

[Collection("serial-body-editing")]
public class RealImageTests
{
    // 真实大图可选：设置 NOVEL_GM_REAL_IMAGE 指向本机图片；未设置时生成一张同样够大的样图。
    private static string? RealImagePath => Environment.GetEnvironmentVariable("NOVEL_GM_REAL_IMAGE");

    [Fact]
    public void Real_screenshot_can_be_clicked_five_times_and_stretched()
    {
        StaRunner.Run(Run, TimeSpan.FromSeconds(120), "实图测试没有在限时内结束");
    }

    private static void Run()
    {
        var sourcePath = RealImagePath;
        var generated = string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath);
        if (generated)
        {
            sourcePath = WriteSampleImage();
        }
        var imagePath = sourcePath!;
        Assert.True(File.Exists(imagePath), "找不到这张图：" + imagePath);
        var (pixelWidth, pixelHeight) = SizeOf(imagePath);
        var root = Path.Combine(Path.GetTempPath(), "novel-gm-real-image-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Exception? dispatcherError = null;
        Dispatcher.CurrentDispatcher.UnhandledException += (_, e) =>
        {
            dispatcherError = e.Exception;
            e.Handled = true;
        };
        var box = new RichTextBox { Width = 760, Height = 640, FontSize = 17, AcceptsTab = true };
        var host = new Window { Content = box, Width = 900, Height = 760, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000 };
        var changes = 0;
        BodyEditing.Attach(box, () => true, () => root, () => 1, () => null, () => changes++);
        host.Show();
        try
        {
            box.Document.Blocks.Clear();
            box.Document.Blocks.Add(new Paragraph(new Run("校门口的樱花开了。")));
            box.CaretPosition = box.Document.ContentEnd;
            var stored = BodyFigures.Store(root, imagePath);
            Assert.Equal(new FileInfo(imagePath).Length, new FileInfo(Path.Combine(root, stored.OriginalRelative)).Length);
            if (Math.Max(pixelWidth, pixelHeight) > BodyFigures.MaxEdge)
                Assert.NotEqual(stored.OriginalRelative, stored.Relative);
            BodyFigures.Insert(box, stored);
            var image = BlockImage(box);
            var caption = (Paragraph)((BlockUIContainer)image.Parent).NextBlock!;
            caption.Inlines.Add(new Run("校门"));
            Until(() => image.Source is BitmapSource && image.ActualWidth > 40);
            var widthBeforeClicks = image.ActualWidth;

            for (var n = 1; n <= 5; n++)
            {
                Click(image, n is 2 or 4 ? 2 : 1);
                Assert.NotNull(Corner(box));
            }
            // 连点仅选中；显式预览命令即使连续执行也只开一个窗口。
            box.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Assert.Empty(ExtraWindows(host));
            var capturePath = Environment.GetEnvironmentVariable("NOVEL_GM_REAL_SELECTION_CAPTURE");
            if (!string.IsNullOrEmpty(capturePath))
            {
                host.UpdateLayout();
                var capture = new RenderTargetBitmap((int)host.ActualWidth, (int)host.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                capture.Render(host);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(capture));
                using var output = File.Create(capturePath); encoder.Save(output);
            }
            var preview = BodyEditing.PreviewFigureAsync(image);
            var repeatedPreview = BodyEditing.PreviewFigureAsync(image);
            Until(() => preview.IsCompleted && repeatedPreview.IsCompleted);
            preview.GetAwaiter().GetResult(); repeatedPreview.GetAwaiter().GetResult();
            Assert.Single(ExtraWindows(host));
            foreach (var view in ExtraWindows(host).ToList())
            {
                var shown = (view.Content as ScrollViewer)?.Content as Image;
                var bitmap = shown?.Source as BitmapSource;
                Assert.NotNull(bitmap);
                Assert.True(bitmap!.PixelWidth >= Math.Min(pixelWidth, 1600), "大图宽=" + bitmap.PixelWidth);
                view.Close();
            }

            image = BlockImage(box);
            Assert.InRange(image.ActualWidth, widthBeforeClicks - 1, widthBeforeClicks + 1);
            Assert.Contains("校门口的樱花开了。", RichBody.PlainText(box.Document));
            Assert.Equal(8, Corner(box).Length);

            var before = image.ActualWidth;
            Drag(Corner(box)[3], 160, 40);
            image = BlockImage(box);
            Assert.InRange(image.Width, before + 159, before + 161);
            Assert.Contains("|w=", (string)image.Tag);

            var stretched = image.Width;
            Drag(Corner(box)[0], -80, -30);
            image = BlockImage(box);
            Assert.InRange(image.Width, stretched + 79, stretched + 81);

            Assert.True(box.CanUndo);
            box.Undo();
            image = BlockImage(box);
            Assert.InRange(image.Width, before + 159, before + 161);
            box.Undo();
            image = BlockImage(box);
            Assert.True(double.IsNaN(image.Width) || image.Width <= 0, "撤销后仍是拉伸宽度 " + image.Width);

            image = BodyFigures.SetAlign(box, image, TextAlignment.Left, root)!;
            Assert.Equal(HorizontalAlignment.Left, image.HorizontalAlignment);
            image = BodyFigures.SetAlign(box, image, TextAlignment.Right, root)!;
            Assert.Equal(HorizontalAlignment.Right, image.HorizontalAlignment);
            image = BodyFigures.SetAlign(box, image, TextAlignment.Center, root)!;
            Assert.Equal(HorizontalAlignment.Center, image.HorizontalAlignment);
            BodyFigures.SetAlt(image, "异环校门");
            Assert.Equal("异环校门", image.ToolTip);
            Assert.Contains("|alt=异环校门", (string)image.Tag);

            var prose = box.Document.Blocks.OfType<Paragraph>().First(p => p.FontStyle != FontStyles.Italic);
            box.CaretPosition = prose.ContentStart.GetPositionAtOffset(3);
            Assert.False(BodyFigures.CaretAtParagraphEnd(box));
            BodyFigures.InsertAtCaret(box, stored);
            Assert.NotEmpty(InlineImages(box));

            var (text, xaml) = RichBody.Capture(box.Document);
            Assert.DoesNotContain(BodyFigures.MarkerPrefix, text);
            Assert.Contains("校门口的樱花开了。", text);
            Assert.Contains("|alt=异环校门", xaml);
            var reopened = RichBody.Build(text, xaml, root);
            WpfPump.Images(reopened);
            Assert.Contains(reopened.Blocks.OfType<BlockUIContainer>(), b => b.Child is Image { Source: not null });

            image = BlockImage(box);
            Click(image, 1);
            PressDelete(box);
            Assert.Empty(box.Document.Blocks.OfType<BlockUIContainer>());
            Assert.DoesNotContain(box.Document.Blocks.OfType<Paragraph>(), p => new TextRange(p.ContentStart, p.ContentEnd).Text.Trim() == "校门");
            Assert.Contains("口的樱花开了。", RichBody.PlainText(box.Document));
            Assert.NotEmpty(InlineImages(box));
            Assert.True(changes > 0);
            if (dispatcherError is not null) throw dispatcherError;
        }
        finally
        {
            foreach (var view in ExtraWindows(host).ToList()) view.Close();
            host.Close();
            try { Directory.Delete(root, true); } catch (IOException) { }
            if (generated) { try { File.Delete(imagePath); } catch (IOException) { } }
        }
    }

    private static Image BlockImage(RichTextBox box) =>
        box.Document.Blocks.OfType<BlockUIContainer>().Select(b => b.Child).OfType<Image>().Single();

    private static IEnumerable<Image> InlineImages(RichTextBox box) =>
        box.Document.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<InlineUIContainer>()).Select(i => i.Child).OfType<Image>();

    private static void Click(Image image, int count)
    {
        var preview = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
        };
        image.RaiseEvent(preview);
        if (count < 2) return;
        var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonDownEvent,
        };
        typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!
            .GetSetMethod(true)!.Invoke(down, new object[] { count });
        image.RaiseEvent(down);
    }

    private static Thumb[] Corner(RichTextBox box)
    {
        var layer = AdornerLayer.GetAdornerLayer(box);
        var adorners = layer?.GetAdorners(box) ?? Array.Empty<Adorner>();
        var adorner = adorners.FirstOrDefault(a => VisualTreeHelper.GetChildrenCount(a) == 8);
        Assert.NotNull(adorner);
        return Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(adorner)).Select(i => (Thumb)VisualTreeHelper.GetChild(adorner, i)).ToArray();
    }

    private static void Drag(Thumb thumb, double x, double y)
    {
        thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        thumb.RaiseEvent(new DragDeltaEventArgs(x, y) { RoutedEvent = Thumb.DragDeltaEvent });
        thumb.RaiseEvent(new DragCompletedEventArgs(x, y, false) { RoutedEvent = Thumb.DragCompletedEvent });
        thumb.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
    }

    private static void PressDelete(RichTextBox box)
    {
        var source = PresentationSource.FromVisual(box);
        Assert.NotNull(source);
        box.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Delete)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        });
    }

    private static IEnumerable<Window> ExtraWindows(Window host)
    {
        foreach (PresentationSource source in PresentationSource.CurrentSources)
            if (source.RootVisual is Window window && window != host) yield return window;
    }

    private static string WriteSampleImage()
    {
        const int width = 2000, height = 1500, stride = width * 4;
        var pixels = new byte[stride * height];
        var random = new Random(17);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)random.Next(256);
            pixels[i + 1] = (byte)random.Next(256);
            pixels[i + 2] = (byte)random.Next(256);
            pixels[i + 3] = 255;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(Path.GetTempPath(), "novel-gm-real-image-source-" + Guid.NewGuid().ToString("N") + ".png");
        using var output = File.Create(path);
        encoder.Save(output);
        return path;
    }

    private static (int Width, int Height) SizeOf(string path)
    {
        using var input = File.OpenRead(path);
        var frame = BitmapDecoder.Create(input, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
        return (frame.PixelWidth, frame.PixelHeight);
    }

    private static void Until(Func<bool> ready)
    {
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow.AddSeconds(40);
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(15) };
        timer.Tick += (_, _) =>
        {
            if (ready() || DateTime.UtcNow >= deadline) frame.Continue = false;
        };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        if (!ready()) throw new TimeoutException("图片操作没有在限时内完成");
    }
}
