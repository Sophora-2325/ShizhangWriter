using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace NovelGm.Desktop.Api;

/// <summary>
/// 两套互相独立的界面偏好：
/// 「外观」<see cref="Mode"/>（浅色 / 深色 / 跟随系统）与「强调色」<see cref="Accent"/>（朱砂 / 靛青 / 松绿）。
/// 两者正交组合出实际使用的字典：中性底 Themes/Light|Dark.xaml + Themes/Accents/{Name}{Light|Dark}.xaml。
/// 主题字典固定放在 Application.Resources.MergedDictionaries[0]。
/// </summary>
public static class ThemeManager
{
    public const string Light = "light";
    public const string Dark = "dark";
    public const string System = "system";

    /// <summary>强调色取值，顺序即设置页里的排列顺序。</summary>
    public const string Zhusha = "zhusha";
    public const string Dianqing = "dianqing";
    public const string Songlv = "songlv";
    public static readonly string[] Accents = { Zhusha, Dianqing, Songlv };

    /// <summary>正文字体栈。必须是 RichBody 清洗正则认得的四种形态，否则换字体会被旧章节里的默认值顶掉。</summary>
    public const string FontSimSun = "Cambria, SimSun, 宋体";
    public const string FontKaiTi = "Cambria, KaiTi, 楷体";
    public const string FontDengXian = "Segoe UI, DengXian, 等线";
    public const string FontFangSong = "Cambria, FangSong, 仿宋";
    public static readonly string[] BodyFontStacks = { FontSimSun, FontKaiTi, FontDengXian, FontFangSong };

    public static string Mode { get; private set; } = Light;
    public static string Accent { get; private set; } = Zhusha;
    public static bool IsDark { get; private set; }

    public static event EventHandler? ThemeChanged;

    /// <summary>正文字体或正文宽度模式变化（不换配色），供主窗重算稿纸列。</summary>
    public static event EventHandler? AppearanceChanged;

    private static bool _applied;
    private static string _appliedAccent = Zhusha;

    public static void Init()
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) => { if (s is Window w) Decorate(w); }));
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (Mode == System && e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
                Application.Current?.Dispatcher.BeginInvoke(Refresh);
        };
        Mode = Normalize(UiPrefs.Current.Theme);
        Accent = NormalizeAccent(UiPrefs.Current.Accent);
        Refresh();
        ApplyBodyFont();
    }

    /// <summary>切换外观（浅色 / 深色 / 跟随系统），强调色不变。</summary>
    public static void Apply(string mode)
    {
        Mode = Normalize(mode);
        UiPrefs.Current.Theme = Mode;
        UiPrefs.Save();
        Refresh();
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>切换强调色（朱砂 / 靛青 / 松绿），明暗仍由外观决定。</summary>
    public static void ApplyAccent(string accent)
    {
        Accent = NormalizeAccent(accent);
        UiPrefs.Current.Accent = Accent;
        UiPrefs.Save();
        Refresh();
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// 正文字体与强调色脱钩：把 BodyFontFamily 写进 Application.Resources 的本地键，
    /// 本地键优先于合并字典，正文编辑器、阅读视图、HTML 预览与导出会一起跟随。
    /// </summary>
    public static void ApplyBodyFont()
    {
        if (Application.Current is not { } app) return;
        app.Resources["BodyFontFamily"] = new FontFamily(NormalizeFont(UiPrefs.Current.BodyFont));
        AppearanceChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>切换正文列模式（稿纸列 / 铺满），只改本机偏好并通知主窗重算宽度。</summary>
    public static void ApplyBodyWidthMode(string mode)
    {
        UiPrefs.Current.BodyWidthMode = PaperLayout.IsPaper(mode) ? PaperLayout.ModePaper : PaperLayout.ModeFull;
        UiPrefs.Save();
        AppearanceChanged?.Invoke(null, EventArgs.Empty);
    }

    private static string Normalize(string? mode) => mode is Dark or System ? mode : Light;

    public static string NormalizeAccent(string? accent) =>
        accent is not null && Accents.Contains(accent) ? accent : Zhusha;

    public static string NormalizeFont(string? font) =>
        font is not null && BodyFontStacks.Contains(font) ? font : FontSimSun;

    private static void Refresh()
    {
        var dark = Mode == Dark || (Mode == System && SystemIsDark());
        if (_applied && dark == IsDark && Accent == _appliedAccent) return;
        IsDark = dark;
        _appliedAccent = Accent;
        _applied = true;

        var dicts = Application.Current.Resources.MergedDictionaries;
        var palette = CreatePalette(dark, Accent);
        if (dicts.Count > 0) dicts[0] = palette;
        else dicts.Add(palette);

        ApplyBodyFont();
        foreach (Window w in Application.Current.Windows)
            SetDarkTitleBar(w, dark);
    }

    /// <summary>
    /// 中性底 + 强调色两层合并。强调色字典只覆盖强调派生键，其余键仍来自中性底，
    /// 所以新增色键只要加在 Light/Dark.xaml，三套强调色自动继承。
    /// </summary>
    public static ResourceDictionary CreatePalette(bool dark, string accent)
    {
        var mode = dark ? "Dark" : "Light";
        var name = char.ToUpperInvariant(NormalizeAccent(accent)[0]) + NormalizeAccent(accent)[1..];
        var palette = new ResourceDictionary();
        palette.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/novel-gm;component/Themes/{mode}.xaml", UriKind.Absolute),
        });
        palette.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/novel-gm;component/Themes/Accents/{name}{mode}.xaml", UriKind.Absolute),
        });
        return palette;
    }

    private static bool SystemIsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 只有 Window 基类的隐式样式时，派生窗口拿不到主题背景和字色；
    /// 这里给没设过的窗口补上，并把系统标题栏切成对应的深浅色。
    /// </summary>
    private static void Decorate(Window w)
    {
        if (w.ReadLocalValue(Window.BackgroundProperty) == DependencyProperty.UnsetValue)
            w.SetResourceReference(Window.BackgroundProperty, "BgBrush");
        if (w.ReadLocalValue(Window.ForegroundProperty) == DependencyProperty.UnsetValue)
            w.SetResourceReference(Window.ForegroundProperty, "TextBrush");
        SetDarkTitleBar(w, IsDark);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeOld = 19;

    private static void SetDarkTitleBar(Window w, bool dark)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        var value = dark ? 1 : 0;
        if (DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref value, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkModeOld, ref value, sizeof(int));
    }
}
