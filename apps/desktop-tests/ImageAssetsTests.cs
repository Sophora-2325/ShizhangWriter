using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NovelGm.Desktop.Api;
using Xunit;

namespace NovelGm.Desktop.Tests;

public class ImageAssetsTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Displayed_image_resize_delete_and_undo_preserve_serializable_metadata(bool inline) => Sta(root =>
    {
        var path = Path.Combine(root, "source.png");
        Write(path, 100, 60);
        var stored = BodyFigures.Store(root, path);
        var editor = new RichTextBox();
        editor.Document = RichBody.FromPlain("前文后文");
        editor.CaretPosition = ((Paragraph)editor.Document.Blocks.FirstBlock).ContentStart.GetPositionAtOffset(2)!;
        var info = new BodyFigures.FigureInfo { Preview = stored.Relative, Original = stored.OriginalRelative, Width = 120, Inline = inline };
        BodyFigures.Insert(editor, info, stored.Absolute, forceBlock: !inline);
        WpfPump.Images(editor.Document);
        var window = new System.Windows.Window { Content = editor, Width = 600, Height = 400, ShowInTaskbar = false };
        Image Current()
        {
            var child = inline
                ? editor.Document.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<InlineUIContainer>()).Single().Child
                : editor.Document.Blocks.OfType<BlockUIContainer>().Single().Child;
            if (child is not Image) throw new InvalidOperationException(System.Windows.Markup.XamlWriter.Save(child));
            return (Image)child;
        }
        try
        {
            window.Show();
            window.UpdateLayout();
            editor.IsUndoEnabled = false;
            editor.IsUndoEnabled = true;
            var image = Current();
            Assert.IsType<string>(image.Tag);
            BodyFigures.SetWidth(editor, image, 240, root);
            Assert.Equal(240, Current().Width);
            Assert.True(editor.CanUndo);
            editor.Undo();
            Assert.Equal(120, Current().Width);
            Assert.Equal(120, BodyFigures.AsInfo(Current())!.Width);
            Assert.NotNull(Current().Source);
            editor.Redo();
            Assert.Equal(240, Current().Width);
            BodyFigures.SetAlt(Current(), "原图说明");
            Assert.Equal("原图说明", BodyFigures.AsInfo(Current())!.Alt);
            BodyFigures.Delete(editor, Current());
            editor.Undo();
            Assert.Equal("原图说明", BodyFigures.AsInfo(Current())!.Alt);
            Assert.NotNull(Current().Source);
            var captured = RichBody.Capture(editor.Document);
            Assert.Contains("|w=240", captured.Rich);
            Assert.Contains("原图说明", captured.Rich);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Single_6_6_MiB_image_import_display_resize_and_save() => Sta(root =>
    {
        var path = Path.Combine(root, "6.6MiB.bmp");
        var bitmap = new WriteableBitmap(1800, 1280, 96, 96, PixelFormats.Bgr24, null);
        var encoder = new BmpBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path)) encoder.Save(stream);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var pending = BodyFigures.StoreAsync(root, path);
        var dispatch = WpfPump.Wait(System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeAsync(() => true).Task);
        Assert.True(dispatch);
        var stored = WpfPump.Wait(pending);
        var importMs = watch.ElapsedMilliseconds;
        var editor = new RichTextBox();
        BodyFigures.Insert(editor, stored);
        WpfPump.Images(editor.Document);
        var displayMs = watch.ElapsedMilliseconds;
        var image = (Image)editor.Document.Blocks.OfType<BlockUIContainer>().Single().Child;
        var source = image.Source;
        for (var i = 0; i < 20; i++)
        {
            image = BodyFigures.SetWidth(editor, image, 200 + i, root)!;
            Assert.Same(source, image.Source);
        }
        var captured = RichBody.Capture(editor.Document);
        Assert.True(captured.Rich.Length < 5000, "Saving must keep resource references rather than embedding image pixels.");
        output.WriteLine($"Image {new FileInfo(path).Length / 1048576.0:0.00} MiB; import {importMs} ms; displayed {displayMs} ms; including 20 resizes and save {watch.ElapsedMilliseconds} ms.");
    });

    [Fact]
    public void Cold_images_do_not_block_dispatcher_even_when_decoders_are_busy() => Sta(root =>
    {
        var path = Path.Combine(root, "cold.png");
        Write(path, 2000, 800);
        using var entered = new CountdownEvent(2);
        using var release = new ManualResetEventSlim();
        var blockers = Enumerable.Range(0, 2).Select(_ => ImageAssets.Background(() =>
        {
            entered.Signal();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            return true;
        })).ToArray();
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var info = new BodyFigures.FigureInfo { Preview = "cold.png" };
            var document = new FlowDocument(new Paragraph(new Run(BodyFigures.Marker(info))));
            BodyFigures.Hydrate(document, root);
            var image = Assert.IsType<Image>(Assert.Single(document.Blocks.OfType<BlockUIContainer>()).Child);
            Assert.Null(image.Source);
            Assert.True(WpfPump.Wait(System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeAsync(() => true).Task));
            // Even saving while a picture is pending must only serialize its resource marker.
            Assert.Contains("cold.png", RichBody.SaveXaml(document));
            release.Set();
            WpfPump.Images(document);
            Assert.NotNull(image.Source);
        }
        finally
        {
            release.Set();
            Task.WaitAll(blockers);
        }
    });

    [Fact]
    public void Pending_clipboard_import_does_not_modify_a_replaced_document() => Sta(root =>
    {
        var path = Path.Combine(root, "source.png");
        Write(path, 20, 10);
        var clipboard = new System.Windows.DataObject();
        clipboard.SetData(BodyFigures.ClipboardFormat, BodyFigures.Marker("source.png") + "\n" + path);
        var editor = new RichTextBox();
        var pending = BodyFigures.TryPasteClipboardFigureAsync(editor, root, clipboard);
        editor.Document = new FlowDocument(new Paragraph(new Run("新章节")));
        Assert.False(WpfPump.Wait(pending));
        Assert.Equal("新章节", RichBody.PlainText(editor.Document));
    });

    [Fact]
    public void Identical_small_images_share_one_file_even_with_different_names() => Sta(root =>
    {
        var source = Path.Combine(root, "first.png");
        Write(source, 20, 10);
        var renamed = Path.Combine(root, "renamed.jpg");
        File.Copy(source, renamed);
        var first = BodyFigures.Store(root, source);
        var second = BodyFigures.Store(root, renamed);
        Assert.Equal(first, second);
        Assert.EndsWith(".png", first.Relative);
        Assert.Equal(first.Relative, first.OriginalRelative);
        Assert.Single(Directory.GetFiles(Path.Combine(root, "插图"), "*", SearchOption.AllDirectories));
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(first.Absolute));
    });

    [Fact]
    public void Large_images_keep_original_and_reuse_preview_across_imports() => Sta(root =>
    {
        var source = Path.Combine(root, "large.png");
        Write(source, 2300, 300);
        var first = BodyFigures.Store(root, source);
        Assert.Equal(first, BodyFigures.Duplicate(root, first.Absolute, Path.Combine(root, first.OriginalRelative)));
        Assert.Equal(1920, BodyFigures.Load(first.Absolute, 4096).PixelWidth);
        Assert.Equal(2300, BodyFigures.Load(Path.Combine(root, first.OriginalRelative), 4096).PixelWidth);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(root, "插图"), "*", SearchOption.AllDirectories).Length);
    });

    [Fact]
    public void Concurrent_imports_publish_one_complete_asset() => Sta(root =>
    {
        var source = Path.Combine(root, "source.png");
        Write(source, 2000, 20);
        var results = Task.WhenAll(Enumerable.Range(0, 4).Select(_ => BodyFigures.StoreAsync(root, source))).GetAwaiter().GetResult();
        Assert.All(results, result => Assert.Equal(results[0], result));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(root, "插图"), "*", SearchOption.AllDirectories).Length);
        Assert.NotNull(BodyFigures.Load(results[0].Absolute));
    });

    [Fact]
    public void Invalid_import_leaves_no_asset_or_partial_file() => Sta(root =>
    {
        var source = Path.Combine(root, "broken.png");
        File.WriteAllText(source, "not an image");
        Assert.ThrowsAny<Exception>(() => BodyFigures.Store(root, source));
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "插图"), "*", SearchOption.AllDirectories));
    });

    [Fact]
    public void Frozen_cache_reuses_decode_without_locking_file_and_refreshes_changed_files() => Sta(root =>
    {
        var source = Path.Combine(root, "source.png");
        Write(source, 200, 100);
        var first = BodyFigures.Load(source, 100);
        Assert.True(first.IsFrozen);
        Assert.Equal(100, first.PixelWidth);
        Assert.Same(first, BodyFigures.Load(source, 100));
        Assert.NotSame(first, BodyFigures.Load(source, 50));
        // This also proves the decoded bitmap does not hold an open file handle.
        Write(source, 60, 40);
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddSeconds(3));
        var changed = BodyFigures.Load(source, 100);
        Assert.NotSame(first, changed);
        Assert.Equal(60, changed.PixelWidth);
    });

    [Fact]
    public void Shared_assets_keep_independent_layout_and_legacy_paths_still_open() => Sta(root =>
    {
        var source = Path.Combine(root, "source.png");
        Write(source, 20, 10);
        var stored = BodyFigures.Store(root, source);
        var first = new BodyFigures.FigureInfo { Preview = stored.Relative, Original = stored.OriginalRelative, Width = 120 };
        var second = first.Clone();
        second.Width = 240;
        var document = new FlowDocument();
        document.Blocks.Add(new Paragraph(new Run(BodyFigures.Marker(first))));
        document.Blocks.Add(new Paragraph(new Run(BodyFigures.Marker(second))));
        var legacy = Path.Combine(root, "插图", "20261017-old.png");
        File.Copy(source, legacy);
        document.Blocks.Add(new Paragraph(new Run(BodyFigures.Marker("插图/20261017-old.png"))));
        BodyFigures.Hydrate(document, root);
        WpfPump.Images(document);
        var images = document.Blocks.OfType<BlockUIContainer>().Select(block => (Image)block.Child).ToList();
        Assert.Equal(3, images.Count);
        Assert.Equal(120, images[0].Width);
        Assert.Equal(240, images[1].Width);
        Assert.Same(images[0].Source, images[1].Source);
        Assert.NotSame(images[0].Tag, images[1].Tag);
        Assert.NotNull(images[2].Source);
    });

    [Fact]
    public void Missing_preview_uses_original_and_copy_is_local_to_destination_work() => Sta(root =>
    {
        var source = Path.Combine(root, "source.png");
        Write(source, 20, 10);
        var first = BodyFigures.Store(root, source);
        var info = new BodyFigures.FigureInfo { Preview = "插图/missing.png", Original = first.OriginalRelative, Width = 123 };
        var document = new FlowDocument(new Paragraph(new Run(BodyFigures.Marker(info))));
        BodyFigures.Hydrate(document, root);
        WpfPump.Images(document);
        var image = Assert.IsType<Image>(Assert.Single(document.Blocks.OfType<BlockUIContainer>()).Child);
        Assert.Equal(123, image.Width);
        var target = Path.Combine(root, "another-work");
        var copied = BodyFigures.Duplicate(target, first.Absolute, Path.Combine(root, first.OriginalRelative));
        Assert.StartsWith(target, copied.Absolute);
        Assert.True(File.Exists(copied.Absolute));
        Assert.Equal(File.ReadAllBytes(first.Absolute), File.ReadAllBytes(copied.Absolute));
        info.Preview = "../source.png";
        info.Original = "";
        Assert.Null(BodyFigures.OriginalOrPreview(info, target));
    });

    private static void Write(string path, int width, int height)
    {
        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }

    private static void Sta(Action<string> run) => StaRunner.Run(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "novel-gm-assets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { run(root); }
        finally { Directory.Delete(root, recursive: true); }
    });
}
