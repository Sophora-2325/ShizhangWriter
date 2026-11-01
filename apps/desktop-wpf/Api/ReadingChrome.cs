namespace NovelGm.Desktop.Api;

/// <summary>阅读模式的页眉和纸页：书名、章节、进度，以及相对写作区抬起的一页。</summary>
internal static class ReadingChrome
{
    public readonly record struct Page(bool On, string Work, string Chapter, string Progress, bool CanPrev, bool CanNext);

    public static Page Build(bool on, string? work, string? chapter, int index, int count)
    {
        if (!on) return new(false, "", "", "", false, false);
        var placed = index >= 0 && count > 0;
        return new(
            true,
            work?.Trim() ?? "",
            chapter ?? "",
            placed ? $"{index + 1} / {count}" : "",
            placed && index > 0,
            placed && index < count - 1);
    }

    /// <summary>按实际中文全宽字度量列宽，避免拿拉丁数字宽度推算中文。</summary>
    public const int LongFormChars = 32;

    public const double ReadingFontSize = 20;

    public static double ColumnWidth(double characterWidth, int characters = LongFormChars) =>
        Math.Clamp(characters, 28, 38) * characterWidth;
}
