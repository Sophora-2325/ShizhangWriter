using System.IO;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;

namespace NovelGm.Desktop.Api;

/// <summary>作品内不可变的图片文件；正文各处只保存自己的布局和相对路径。</summary>
internal static class ImageAssets
{
    private const long CacheBudget = 64L * 1024 * 1024;
    private const int CacheEntries = 32;
    private static readonly object CacheLock = new();
    private sealed record Cached(string Path, long Length, long Modified, int Cap, BitmapImage Bitmap, long Bytes);
    private static readonly LinkedList<Cached> Cache = new();
    private static long _cacheBytes;
    private static readonly SemaphoreSlim Workers = new(2);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Path, int Cap), Lazy<Task<BitmapImage>>> Pending = new();

    internal static async Task<T> Background<T>(Func<T> work)
    {
        await Workers.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(work).ConfigureAwait(false); }
        finally { Workers.Release(); }
    }

    public static async Task<BitmapImage> LoadAsync(string path, int cap)
    {
        var key = (Path.GetFullPath(path).ToUpperInvariant(), Math.Clamp(cap, 1, 4096));
        var request = Pending.GetOrAdd(key, _ => new Lazy<Task<BitmapImage>>(() => Background(() => Load(path, cap))));
        try { return await request.Value.ConfigureAwait(false); }
        finally
        {
            ((ICollection<KeyValuePair<(string Path, int Cap), Lazy<Task<BitmapImage>>>>)Pending)
                .Remove(new(key, request));
        }
    }

    public static BodyFigures.Stored Store(string root, string source)
    {
        var originals = Path.Combine(root, BodyFigures.FolderName, "原图");
        Directory.CreateDirectory(originals);
        var temp = Path.Combine(originals, ".import-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            // Snapshot first: hashing, format detection and preview all refer to the same bytes.
            using (var input = File.OpenRead(source))
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(true);
            }
            var (width, height, extension, alpha) = Describe(temp);
            var hash = Hash(temp);
            var originalRel = $"{BodyFigures.FolderName}/原图/{hash}{extension}";
            var original = Path.Combine(root, originalRel.Replace('/', Path.DirectorySeparatorChar));
            Publish(temp, original);
            // Small images need no second copy. Their original is also the preview.
            if (Math.Max(width, height) <= BodyFigures.MaxEdge)
                return new(original, originalRel, originalRel);

            var previewRel = $"{BodyFigures.FolderName}/{hash}-preview-v1-{BodyFigures.MaxEdge}{(alpha ? ".png" : ".jpg")}";
            var preview = Path.Combine(root, previewRel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(preview))
            {
                var previewTemp = preview + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    var bitmap = Decode(original, BodyFigures.MaxEdge, width, height);
                    BitmapEncoder encoder = alpha ? new PngBitmapEncoder() : new JpegBitmapEncoder { QualityLevel = 90 };
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var output = new FileStream(previewTemp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        encoder.Save(output);
                        output.Flush(true);
                    }
                    Publish(previewTemp, preview);
                }
                finally { RemoveTemp(previewTemp); }
            }
            return new(preview, previewRel, originalRel);
        }
        finally { RemoveTemp(temp); }
    }

    private static string Hash(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }

    private static void Publish(string temp, string destination)
    {
        try { File.Move(temp, destination); }
        catch (IOException) when (File.Exists(destination))
        {
            // Concurrent imports of identical content converge on one complete asset.
            // Repair a damaged asset without exposing a half-written destination.
            if (Hash(temp) != Hash(destination)) File.Move(temp, destination, overwrite: true);
        }
    }

    private static void RemoveTemp(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static (int Width, int Height, string Extension, bool Alpha) Describe(string path)
    {
        using var input = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0) throw new InvalidDataException("Invalid image dimensions.");
        // Use the actual file format rather than the source filename.
        var extension = decoder switch
        {
            PngBitmapDecoder => ".png", JpegBitmapDecoder => ".jpg", GifBitmapDecoder => ".gif",
            TiffBitmapDecoder => ".tiff", BmpBitmapDecoder => ".bmp", IconBitmapDecoder => ".ico",
            _ => ".img",
        };
        return (frame.PixelWidth, frame.PixelHeight, extension, decoder is not JpegBitmapDecoder);
    }

    private static BitmapImage Decode(string path, int cap, int width, int height)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.IgnoreImageCache;
        // Undo serializes Image.Source to XAML. A closed StreamSource cannot round-trip;
        // an on-load file URI preserves a reloadable reference without locking the file.
        bitmap.UriSource = new Uri(Path.GetFullPath(path), UriKind.Absolute);
        if (Math.Max(width, height) > cap)
        {
            if (width >= height) bitmap.DecodePixelWidth = cap;
            else bitmap.DecodePixelHeight = cap;
        }
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    public static BitmapImage Load(string path, int cap)
    {
        cap = Math.Clamp(cap, 1, 4096);
        var file = new FileInfo(Path.GetFullPath(path));
        var length = file.Length;
        var modified = file.LastWriteTimeUtc.Ticks;
        lock (CacheLock)
        {
            for (var node = Cache.First; node is not null;)
            {
                var next = node.Next;
                var entry = node.Value;
                if (string.Equals(entry.Path, file.FullName, StringComparison.OrdinalIgnoreCase))
                {
                    if (entry.Length != length || entry.Modified != modified)
                    {
                        _cacheBytes -= entry.Bytes;
                        Cache.Remove(node);
                    }
                    else if (entry.Cap == cap)
                    {
                        Cache.Remove(node);
                        Cache.AddFirst(node);
                        return entry.Bitmap;
                    }
                }
                node = next;
            }
        }
        var (width, height, _, _) = Describe(file.FullName);
        var bitmap = Decode(file.FullName, cap, width, height);
        var bytes = (long)bitmap.PixelWidth * bitmap.PixelHeight * Math.Max(4, (bitmap.Format.BitsPerPixel + 7) / 8);
        if (bytes > CacheBudget) return bitmap;
        lock (CacheLock)
        {
            Cache.AddFirst(new Cached(file.FullName, length, modified, cap, bitmap, bytes));
            _cacheBytes += bytes;
            while (Cache.Count > CacheEntries || _cacheBytes > CacheBudget)
            {
                _cacheBytes -= Cache.Last!.Value.Bytes;
                Cache.RemoveLast();
            }
        }
        return bitmap;
    }
}
