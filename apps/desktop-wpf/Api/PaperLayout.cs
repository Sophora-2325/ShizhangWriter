namespace NovelGm.Desktop.Api;

/// <summary>
/// 稿纸列的纯计算：正文按「实际字号 × 34 字」限宽，「铺满」时不限宽。
/// 只影响编辑器外层呈现，不改变正文数据、插图、历史或导出的换行。
/// </summary>
public static class PaperLayout
{
    /// <summary>一行排多少字。</summary>
    public const int CharactersPerLine = 34;

    /// <summary>正文区左右留白之和：左 24 + 右 38（与 BodyZoneView.Margin 一致）。</summary>
    public const double HorizontalPadding = 62;

    /// <summary>窗口再窄也保留的最小可读宽度。</summary>
    public const double MinWidth = 480;

    public const string ModePaper = "paper";
    public const string ModeFull = "full";

    public static bool IsPaper(string? mode) =>
        !string.Equals(mode, ModeFull, StringComparison.OrdinalIgnoreCase);

    /// <summary>paper 模式返回限宽（DIP），full 模式返回 PositiveInfinity（WPF 视为不限）。</summary>
    public static double Width(double fontSize, bool paper)
    {
        if (!paper) return double.PositiveInfinity;
        var size = double.IsFinite(fontSize) && fontSize > 0 ? fontSize : 17;
        return Math.Max(MinWidth, Math.Round(CharactersPerLine * size + HorizontalPadding, 1));
    }
}
