using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Models;

namespace NovelGm.Desktop;

/// <summary>
/// 「发布到其他平台」：半自动发布。这里只做三件事——把作品整理成平台适配的发布包、
/// 把正文放进剪贴板、打开平台的作家后台。登录、审核、提交仍然由作者在平台后台上完成，
/// 本窗口不保存任何账号信息。
///
/// 平台清单与要求全部来自后端（<c>GET /publish/platforms</c>），客户端不硬编码平台。
/// </summary>
public partial class PublishWindow : Window
{
    /// <summary>下拉项：Display 是界面文字，Value 是发给后端的值。</summary>
    private sealed record Opt(string Display, string Value);

    /// <summary>抬头下拉项：null = 按平台默认。</summary>
    private sealed record HeadOpt(string Display, bool? Value);

    private readonly NovelApiClient _api;
    private readonly ProjectDto _project;
    private readonly ChapterDto? _chapter;
    private readonly Dictionary<string, CheckBox> _checks = new();

    private List<PublishPlatformDto> _platforms = new();
    private List<PublishGroupDto> _groups = new();
    private List<ChapterDto> _chapters = new();
    private bool _loading = true;

    public PublishWindow(NovelApiClient api, ProjectDto project, ChapterDto? chapter)
    {
        _api = api;
        _project = project;
        _chapter = chapter;
        InitializeComponent();
        Closing += (_, _) => SavePrefs();
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            _chapters = (await _api.ListChaptersAsync(_project.Id))
                .Where(c => !string.Equals(c.Status, "superseded", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var table = await _api.PublishPlatformsAsync();
            _groups = table.Groups;
            _platforms = table.Platforms;
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }

        BuildOptions();
        BuildPlatforms();
        BuildScope();
        LoadPrefs();
        RefreshRequirements();
        _loading = false;
    }

    // ---------------------------------------------------------------- 选项

    private void BuildOptions()
    {
        IndentCombo.ItemsSource = new[]
        {
            new Opt(L.T("Publish.Indent.Default"), ""),
            new Opt(L.T("Publish.Indent.None"), "none"),
            new Opt(L.T("Publish.Indent.Full"), "full"),
        };
        IndentCombo.SelectedIndex = 0;

        HeadCombo.ItemsSource = new[]
        {
            new HeadOpt(L.T("Publish.Indent.Default"), null),
            new HeadOpt(L.T("Publish.Head"), true),
            new HeadOpt(L.T("Publish.Indent.None"), false),
        };
        HeadCombo.SelectedIndex = 0;
    }

    private void BuildScope()
    {
        var name = _chapter is null
            ? ""
            : string.IsNullOrWhiteSpace(_chapter.Title) ? $"第{_chapter.No}章" : _chapter.Title;
        ScopeChapter.Content = string.IsNullOrEmpty(name) ? L.T("Publish.NeedChapter") : L.T("Publish.Scope.Chapter", name);
        ScopeChapter.IsEnabled = _chapter is not null;
        ScopeBook.Content = L.T("Publish.Scope.Book", _chapters.Count, _chapters.Sum(c => c.Word_Count));

        var book = _chapter is null || string.Equals(UiPrefs.Current.PublishScope, "book", StringComparison.OrdinalIgnoreCase);
        ScopeBook.IsChecked = book;
        ScopeChapter.IsChecked = !book;
    }

    private void LoadPrefs()
    {
        AuthorBox.Text = UiPrefs.Current.PublishAuthor;
        SynopsisBox.Text = string.Join("\n", _project.Entries
            .Where(e => string.Equals(e.Category, "world", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Content.Trim())
            .Where(c => c.Length > 0));
        SynopsisHint.Text = L.T("Publish.SynopsisHint");

        var indent = UiPrefs.Current.PublishIndent;
        IndentCombo.SelectedIndex = Math.Max(0, ((IEnumerable<Opt>)IndentCombo.ItemsSource)
            .ToList().FindIndex(o => o.Value == indent));

        // 没存过就默认每组勾第一个：既让第一次用的人有东西可点，也不硬编码任何平台名
        var saved = UiPrefs.Current.PublishPlatforms;
        if (saved.Count == 0)
        {
            saved = _groups
                .Select(g => _platforms.FirstOrDefault(p => p.Group == g.Key)?.Key)
                .Where(k => !string.IsNullOrEmpty(k))
                .Select(k => k!)
                .ToList();
        }
        foreach (var (key, check) in _checks)
            check.IsChecked = saved.Contains(key);
    }

    private List<string> SelectedKeys() =>
        _checks.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key)
            .OrderBy(k => _platforms.FindIndex(p => p.Key == k)).ToList();

    // ---------------------------------------------------------------- 平台列表

    private void BuildPlatforms()
    {
        PlatformPanel.Children.Clear();
        _checks.Clear();
        foreach (var group in _groups)
        {
            PlatformPanel.Children.Add(new TextBlock
            {
                Text = group.Name,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 10, 0, 2),
            });
            if (!string.IsNullOrWhiteSpace(group.Hint))
                PlatformPanel.Children.Add(Muted(group.Hint));
            foreach (var platform in _platforms.Where(p => p.Group == group.Key))
                PlatformPanel.Children.Add(BuildPlatformRow(platform));
        }
    }

    private FrameworkElement BuildPlatformRow(PublishPlatformDto platform)
    {
        var check = new CheckBox { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 8, 0) };
        check.Checked += (_, _) => Platform_Toggled();
        check.Unchecked += (_, _) => Platform_Toggled();
        _checks[platform.Key] = check;

        var texts = new StackPanel();
        texts.Children.Add(new TextBlock { Text = platform.Name, FontWeight = FontWeights.SemiBold });
        texts.Children.Add(Muted(MetaText(platform)));

        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 6) };
        DockPanel.SetDock(check, Dock.Left);
        row.Children.Add(check);
        row.Children.Add(texts);
        row.ToolTip = string.Join("\n", platform.Notes);
        return row;
    }

    private static string MetaText(PublishPlatformDto platform)
    {
        var parts = new List<string> { Host(platform.Backend_Url), platform.Format.ToUpperInvariant() };
        parts.Add(platform.Batch_Import ? L.T("Publish.BatchImport") : L.T("Publish.PasteOnly"));
        if (platform.Min_Words > 0)
            parts.Add(L.T("Publish.MinWords", platform.Min_Words));
        if (platform.Unverified)
            parts.Add(L.T("Publish.Unverified"));
        return string.Join(" · ", parts);
    }

    private static string Host(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    private void Platform_Toggled()
    {
        if (_loading) return;
        RefreshRequirements();
    }

    private void RefreshRequirements()
    {
        RequirementsPanel.Children.Clear();
        var keys = SelectedKeys();
        var picked = _platforms.Where(p => keys.Contains(p.Key)).ToList();
        if (picked.Count == 0)
        {
            RequirementsPanel.Children.Add(Muted(L.T("Publish.NoPlatform")));
            return;
        }
        foreach (var platform in picked)
        {
            RequirementsPanel.Children.Add(new TextBlock
            {
                Text = platform.Name,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 6, 0, 1),
            });
            RequirementsPanel.Children.Add(Muted(L.T("Publish.BackendTip", platform.Backend_Url)));
            foreach (var note in platform.Notes)
                RequirementsPanel.Children.Add(new TextBlock
                {
                    Text = "· " + note,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 1, 0, 1),
                });
        }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => SetAll(true);

    private void ClearAll_Click(object sender, RoutedEventArgs e) => SetAll(false);

    private void SetAll(bool value)
    {
        foreach (var check in _checks.Values) check.IsChecked = value;
        RefreshRequirements();
    }

    private void Scope_Click(object sender, RoutedEventArgs e)
    {
        if (!_loading) SavePrefs();
    }

    // ---------------------------------------------------------------- 生成 / 复制 / 打开

    private async void Generate_Click(object sender, RoutedEventArgs e) => await RunAsync(copy: false);

    private async void Copy_Click(object sender, RoutedEventArgs e) => await RunAsync(copy: true);

    private async Task RunAsync(bool copy)
    {
        var keys = SelectedKeys();
        if (keys.Count == 0) { StatusText.Text = L.T("Publish.NoPlatform"); return; }
        if (ScopeChapter.IsChecked == true && _chapter is null) { StatusText.Text = L.T("Publish.NeedChapter"); return; }

        GenerateButton.IsEnabled = false;
        CopyButton.IsEnabled = false;
        StatusText.Text = L.T("Publish.Loading");
        try
        {
            var result = await _api.PreparePublishAsync(_project.Id, new PublishRequestDto
            {
                Platforms = keys,
                Scope = ScopeBook.IsChecked == true ? "book" : "chapter",
                Chapter_No = _chapter?.No ?? 0,
                Author = AuthorBox.Text?.Trim() ?? "",
                Synopsis = SynopsisBox.Text?.Trim() ?? "",
                Indent = (IndentCombo.SelectedItem as Opt)?.Value ?? "",
                With_Head = (HeadCombo.SelectedItem as HeadOpt)?.Value,
            });
            RenderResults(result);
            if (copy) CopyText(result);
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
        finally
        {
            GenerateButton.IsEnabled = true;
            CopyButton.IsEnabled = true;
        }
    }

    private void RenderResults(PublishResultDto result)
    {
        ResultsPanel.Children.Clear();
        var status = L.T("Publish.Done", result.Items.Count);
        if (result.Root_Missing) status += "\n" + L.T("Publish.NoRoot", result.Out_Root);
        if (result.Items.Any(i => i.Empty)) status += "\n" + L.T("Publish.EmptyBody");
        StatusText.Text = status;

        foreach (var item in result.Items)
        {
            var block = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };
            block.Children.Add(new TextBlock { Text = item.Name, FontWeight = FontWeights.SemiBold });
            if (item.Empty)
                block.Children.Add(Muted(L.T("Publish.EmptyBody")));
            block.Children.Add(Muted(item.Dir));

            var open = new Button
            {
                Content = L.T("Publish.OpenFolder"),
                Margin = new Thickness(0, 4, 0, 0),
                Padding = new Thickness(10, 2, 10, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            var dir = item.Dir;
            open.Click += (_, _) => ShowInExplorer(dir);
            block.Children.Add(open);
            ResultsPanel.Children.Add(block);
        }
    }

    private void CopyText(PublishResultDto result)
    {
        var preview = result.Preview;
        StatusText.Text = preview.Truncated
            ? L.T("Publish.CopiedTruncated", preview.Text.Length)
            : L.T("Publish.Copied", result.Chars);

        // 本章正文才带 RTF（来自写作面的实时文档）；整本没有对应的富文本来源
        var withRtf = string.Equals(result.Scope, "chapter", StringComparison.OrdinalIgnoreCase) && !preview.Truncated;
        if (Owner is MainWindow host)
        {
            host.PublishClipboard(preview.Text, preview.Html, withRtf);
            return;
        }
        try
        {
            Clipboard.SetText(preview.Text);
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
    }

    private void Backend_Click(object sender, RoutedEventArgs e)
    {
        var keys = SelectedKeys();
        if (keys.Count == 0) { StatusText.Text = L.T("Publish.NoPlatform"); return; }

        var urls = _platforms
            .Where(p => keys.Contains(p.Key))
            .Select(p => p.Backend_Url)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .ToList();

        foreach (var url in urls)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, L.T("Publish.OpenFailed", ex.Message), L.T("Publish.Title"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    /// <summary>文件存在时在资源管理器里选中它，否则打开它所在的文件夹。</summary>
    private void ShowInExplorer(string path)
    {
        try
        {
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else
                MessageBox.Show(this, L.T("Publish.OpenFailed", path), L.T("Publish.Title"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
    }

    // ---------------------------------------------------------------- 偏好与错误

    private void SavePrefs()
    {
        // 还没加载完或平台表没取到（后端不可用）时界面是空的，写回去会清掉之前的勾选
        if (_loading || _platforms.Count == 0) return;
        UiPrefs.Current.PublishPlatforms = SelectedKeys();
        UiPrefs.Current.PublishScope = ScopeBook.IsChecked == true ? "book" : "chapter";
        UiPrefs.Current.PublishAuthor = AuthorBox.Text?.Trim() ?? "";
        UiPrefs.Current.PublishIndent = (IndentCombo.SelectedItem as Opt)?.Value ?? "";
        UiPrefs.Save();
    }

    private void ReportError(Exception ex)
    {
        StatusText.Text = ex.Message;
        MessageBox.Show(this, ex.Message, L.T("Publish.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private TextBlock Muted(string text) => new()
    {
        Text = text,
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 1, 0, 1),
        Foreground = (Brush)FindResource("MutedBrush"),
    };
}
