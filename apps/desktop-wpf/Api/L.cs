using System.Globalization;
using System.Windows;

namespace NovelGm.Desktop.Api;

/// <summary>按 key 取当前语言的界面文字；找不到时返回 key 本身，便于发现漏译。</summary>
public static class L
{
    public static string T(string key) => Application.Current?.TryFindResource(key) as string ?? key;

    public static string T(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(key), args);
}
