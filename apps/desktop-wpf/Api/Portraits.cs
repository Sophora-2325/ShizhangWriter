using System.IO;
using System.Windows.Media.Imaging;

namespace NovelGm.Desktop.Api;

internal static class Portraits
{
    public static string? Resolve(string rootPath, string portrait) =>
        string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(portrait)
            ? null
            : Path.Combine(rootPath, portrait.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Loads fully into memory so the file is not kept locked.</summary>
    public static BitmapImage? Load(string? path, int decodeWidth = 128)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.DecodePixelWidth = decodeWidth;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }
}
