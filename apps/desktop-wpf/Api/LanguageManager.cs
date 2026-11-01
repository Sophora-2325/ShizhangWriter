using System.Globalization;
using System.Windows;

namespace NovelGm.Desktop.Api;

/// <summary>界面语言。字符串字典固定放在 Application.Resources.MergedDictionaries[1]。</summary>
public static class LanguageManager
{
    public const string Chinese = "zh-CN";
    public const string English = "en-US";
    public const string Japanese = "ja-JP";

    public static string Current { get; private set; } = Chinese;

    /// <summary>XAML 里的 DynamicResource 会自动刷新；由代码设置的文字需要订阅此事件重新生成。</summary>
    public static event EventHandler? LanguageChanged;

    public static void Init()
    {
        Current = string.IsNullOrEmpty(UiPrefs.Current.Language) ? FromSystem() : Normalize(UiPrefs.Current.Language);
        Swap();
    }

    public static void Apply(string language)
    {
        var lang = Normalize(language);
        UiPrefs.Current.Language = lang;
        UiPrefs.Save();
        if (lang == Current) return;
        Current = lang;
        Swap();
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    private static string Normalize(string? lang) => lang switch
    {
        Chinese or English or Japanese => lang!,
        _ when lang?.StartsWith("zh", StringComparison.OrdinalIgnoreCase) == true => Chinese,
        _ when lang?.StartsWith("ja", StringComparison.OrdinalIgnoreCase) == true => Japanese,
        _ => English,
    };

    private static string FromSystem() => Normalize(CultureInfo.CurrentUICulture.Name);

    private static void Swap()
    {
        var dicts = Application.Current.Resources.MergedDictionaries;
        var strings = LoadStrings(Current);
        if (dicts.Count > 1) dicts[1] = strings;
        else dicts.Add(strings);
    }

    /// <summary>显式指定资源所属程序集，独立预览或测试宿主也不会到自身查找桌面资源。</summary>
    internal static ResourceDictionary LoadStrings(string language) => new()
    {
        Source = new Uri($"pack://application:,,,/novel-gm;component/Strings/{Normalize(language)}.xaml", UriKind.Absolute),
    };
}
