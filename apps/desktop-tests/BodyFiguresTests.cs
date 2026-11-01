using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NovelGm.Desktop.Api;
using Xunit;

namespace NovelGm.Desktop.Tests;

public class BodyFiguresTests
{
    [Fact]
    public void Stored_image_survives_save_and_reload()
    {
        StaRunner.Run(Run);
    }

    private static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "novel-gm-figures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var src = Path.Combine(root, "source.png");
            File.WriteAllBytes(src, Png);
            var stored = BodyFigures.Store(root, src);
            Assert.StartsWith(Path.Combine(root, BodyFigures.FolderName), stored.Absolute);
            Assert.True(File.Exists(stored.Absolute));

            var box = new RichTextBox();
            box.Document.Blocks.Add(new Paragraph(new Run("上一段")));
            box.CaretPosition = box.Document.ContentEnd;
            BodyFigures.Insert(box, stored);
            WpfPump.Images(box.Document);

            var image = box.Document.Blocks.OfType<BlockUIContainer>().Select(b => b.Child).OfType<Image>().First();
            BodyFigures.SetWidth(box, image, 120, root);
            image = box.Document.Blocks.OfType<BlockUIContainer>().Select(b => b.Child).OfType<Image>().First();
            var prose = RichBody.PlainText(box.Document);
            var xaml = RichBody.SaveXaml(box.Document);
            var info = BodyFigures.AsInfo(image);
            Assert.NotNull(info);
            Assert.Contains(BodyFigures.Marker(info), xaml);
            Assert.Contains("|w=120", xaml);
            Assert.Contains(stored.OriginalRelative.Replace('\\', '/'), xaml);
            Assert.True(File.Exists(Path.Combine(root, stored.OriginalRelative.Replace('/', Path.DirectorySeparatorChar))));
            Assert.DoesNotContain(BodyFigures.MarkerPrefix, prose);
            var legacy = xaml.Replace(BodyFigures.Marker(info), BodyFigures.Marker(stored.Relative));
            var opened = RichBody.Build(prose, legacy, root);
            WpfPump.Images(opened);
            Assert.Contains(opened.Blocks.OfType<BlockUIContainer>(), b => b.Child is Image { Source: not null });

            var clip = new DataObject();
            clip.SetData(BodyFigures.ClipboardFormat, BodyFigures.Marker(info) + "\n" + stored.Absolute + "\n" + Path.Combine(root, stored.OriginalRelative));
            var pasted = new RichTextBox();
            pasted.CaretPosition = pasted.Document.ContentEnd;
            Assert.True(WpfPump.Wait(BodyFigures.TryPasteClipboardFigureAsync(pasted, root, clip)));
            WpfPump.Images(pasted.Document);
            var copy = pasted.Document.Blocks.OfType<BlockUIContainer>().Select(b => b.Child).OfType<Image>().First();
            var copyInfo = BodyFigures.AsInfo(copy);
            Assert.NotNull(copyInfo);
            Assert.Equal(stored.Relative, copyInfo!.Preview);
            Assert.NotSame(info, copyInfo);
            Assert.True(File.Exists(Path.Combine(root, copyInfo.Preview.Replace('/', Path.DirectorySeparatorChar))));
            Assert.DoesNotContain(stored.Absolute, copyInfo.Preview);
            Assert.Contains(box.Document.Blocks.OfType<BlockUIContainer>(), b => b.Child is Image { Source: not null });

            var again = RichBody.Build(prose, xaml, root);
            WpfPump.Images(again);
            Assert.Contains(again.Blocks.OfType<BlockUIContainer>(), b => b.Child is Image { Source: not null });

            var win = new Window { Content = box, Width = 320, Height = 200, ShowInTaskbar = false };
            win.Show();
            box.Dispatcher.Invoke(DispatcherPriority.Loaded, () => { });
            var end = box.Document.ContentEnd.GetNextInsertionPosition(LogicalDirection.Backward) ?? box.Document.ContentEnd;
            end.InsertTextInRun("甲");
            Assert.True(box.CanUndo);
            RichBody.SaveXaml(box.Document);
            Assert.Contains(box.Document.Blocks.OfType<BlockUIContainer>(), b => b.Child is Image { Source: not null });
            box.Undo();
            Assert.DoesNotContain("甲", RichBody.PlainText(box.Document));
            Assert.Contains(box.Document.Blocks.OfType<BlockUIContainer>(), b => b.Child is Image { Source: not null });
            win.Close();

            var wide = Path.Combine(root, "wide.png");
            WriteWide(wide, 2400, 1200);
            var scaled = BodyFigures.Store(root, wide);
            Assert.Equal(1920, PixelWidth(scaled.Absolute));
            Assert.Equal(2400, PixelWidth(Path.Combine(root, scaled.OriginalRelative.Replace('/', Path.DirectorySeparatorChar))));
            var shown = BodyFigures.Load(scaled.Absolute);
            Assert.Equal(BodyFigures.DisplayEdge, shown.PixelWidth);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* 临时目录 */ }
        }
    }

    private static void WriteWide(string path, int width, int height)
    {
        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
        bitmap.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static int PixelWidth(string path)
    {
        using var stream = File.OpenRead(path);
        return BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0].PixelWidth;
    }

    private static readonly byte[] Png =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x02, 0x00, 0x00, 0x00, 0x90, 0x77, 0x53,
        0xDE, 0x00, 0x00, 0x00, 0x0C, 0x49, 0x44, 0x41, 0x54, 0x08, 0xD7, 0x63, 0xF8, 0xCF, 0xC0, 0x00,
        0x00, 0x00, 0x03, 0x00, 0x01, 0x00, 0x05, 0xFE, 0x02, 0xFE, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45,
        0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82,
    ];
}
