namespace NovelGm.Desktop.Api;

/// <summary>
/// 当前章的轻量统计：字数、非空段落、选中字数。
/// 字数沿用主窗与后端的口径（纯文本长度，含段落换行）；选中字数不计换行。
/// </summary>
public readonly record struct ChapterStats(int Words, int Paragraphs, int Selected)
{
    public static ChapterStats From(string? text, string? selection)
    {
        var body = text ?? "";
        var words = body.Length;
        var paragraphs = body.Replace("\r", "").Split('\n').Count(line => line.Trim().Length > 0);
        var selected = (selection ?? "").Replace("\r", "").Replace("\n", "").Length;
        return new ChapterStats(words, paragraphs, selected);
    }
}
