using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Navigation;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Models;

namespace NovelGm.Desktop;

/// <summary>从网上获取 Skill：搜索 GitHub 上的 SKILL.md、按链接导入、把文章交给 AI 提炼成草稿。
/// 导入或保存过任何 Skill 时 Changed = true，调用方据此刷新列表。</summary>
public partial class SkillDiscoverWindow : Window
{
    private string? _fileUrl;
    private bool _hasToken;

    public bool Changed { get; private set; }

    public SkillDiscoverWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadSources();
    }

    private static string Lang => LanguageManager.Current[..2];

    private async Task LoadSources()
    {
        await Busy("Disc.Loading", async () =>
        {
            var sources = await App.Api.GetSkillSourcesAsync();
            SetTokenState(sources.Has_Github_Token);
            ResultsHeader.Text = L.T("Disc.Recommended");
            ResultList.Items.Clear();
            foreach (var s in sources.Items.Where(s => s.Kind == "github"))
                ResultList.Items.Add(Item(s.Name, Describe(s), new GithubItemDto { Repo = s.Name, Url = s.Url }));
            ArticleList.Children.Clear();
            foreach (var s in sources.Items.Where(s => s.Kind == "article"))
                ArticleList.Children.Add(ArticleRow(s));
            Done("");
        });
        QueryBox.Focus();
    }

    private static string Describe(SkillSourceDto s) =>
        s.Desc.TryGetValue(Lang, out var d) ? d : s.Desc.Values.FirstOrDefault() ?? "";

    private void SetTokenState(bool has)
    {
        _hasToken = has;
        TokenState.Text = has ? L.T("Disc.TokenSet") : "";
        SearchHint.Text = L.T(has ? "Disc.SearchHintCode" : "Disc.SearchHintRepo");
    }

    // ---- GitHub ----

    private void QueryBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Search_Click(sender, e);
    }

    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        await Busy("Disc.Searching", async () =>
        {
            var r = await App.Api.SearchGithubSkillsAsync(QueryBox.Text.Trim());
            ResultList.Items.Clear();
            ClearFiles();
            foreach (var it in r.Items)
            {
                var title = it.Stars is int stars ? $"{it.Repo}  ★ {stars}" : it.Repo;
                var desc = string.IsNullOrEmpty(it.Path) ? it.Description : $"{it.Path}\n{it.Description}".Trim();
                ResultList.Items.Add(Item(title, desc, it));
            }
            ResultsHeader.Text = r.Items.Count == 0 ? L.T("Disc.NoResults") : L.T("Disc.Total", r.Total);
            Done("");
        });
    }

    private async void ResultList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if ((ResultList.SelectedItem as ListBoxItem)?.Tag is not GithubItemDto it) return;
        ClearFiles();
        if (!string.IsNullOrEmpty(it.Path))
        {
            FilesHeader.Text = it.Repo;
            FileList.Items.Add(Item(it.Path, "", new GithubFileDto { Path = it.Path, Url = it.Url }));
            FileList.SelectedIndex = 0;
            return;
        }
        await Busy("Disc.Loading", async () =>
        {
            var repo = await App.Api.ListGithubRepoSkillsAsync(it.Repo);
            FilesHeader.Text = repo.Only_Markdown ? L.T("Disc.OnlyMarkdown") : L.T("Disc.Files");
            foreach (var f in repo.Files)
                FileList.Items.Add(Item(f.Path, "", f));
            if (repo.Files.Count == 0) FilesHeader.Text = L.T("Disc.NoFiles");
            else if (repo.Files.Count == 1) FileList.SelectedIndex = 0;
            Done("");
        });
    }

    private async void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if ((FileList.SelectedItem as ListBoxItem)?.Tag is not GithubFileDto f) return;
        SetFile(null);
        await Busy("Disc.Loading", async () =>
        {
            var p = await App.Api.PreviewSkillUrlAsync(f.Url);
            PreviewHeader.Text = L.T("Disc.PreviewOf", p.Name, p.Chars);
            PreviewBox.Text = p.Text;
            SetFile(f.Url);
            ImportFileButton.IsEnabled = !p.Too_Long;
            Done(p.Too_Long ? L.T("Disc.TooLong", p.Chars) : "");
        });
    }

    private void ClearFiles()
    {
        FileList.Items.Clear();
        FilesHeader.Text = L.T("Disc.Files");
        SetFile(null);
    }

    private void SetFile(string? url)
    {
        _fileUrl = url;
        OpenFileButton.IsEnabled = ImportFileButton.IsEnabled = DistillFileButton.IsEnabled = url is not null;
        if (url is null)
        {
            PreviewHeader.Text = L.T("Disc.Preview");
            PreviewBox.Text = "";
        }
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (_fileUrl is not null) OpenUrl(_fileUrl);
    }

    private async void ImportFile_Click(object sender, RoutedEventArgs e)
    {
        if (_fileUrl is not null) await Import(_fileUrl);
    }

    private async void DistillFile_Click(object sender, RoutedEventArgs e)
    {
        if (_fileUrl is not null) await Distill(_fileUrl, "", "");
    }

    private async void SaveToken_Click(object sender, RoutedEventArgs e)
    {
        var token = TokenBox.Password.Trim();
        if (token.Length == 0) return;
        await Busy("Disc.Loading", async () =>
        {
            SetTokenState((await App.Api.SetGithubTokenAsync(token)).Has_Github_Token);
            TokenBox.Clear();
            Done("");
        });
    }

    private async void ClearToken_Click(object sender, RoutedEventArgs e)
    {
        if (!_hasToken) return;
        await Busy("Disc.Loading", async () =>
        {
            SetTokenState((await App.Api.SetGithubTokenAsync("")).Has_Github_Token);
            Done("");
        });
    }

    // ---- link / paste ----

    private async void ImportUrl_Click(object sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text.Trim();
        if (url.Length == 0) { Fail(L.T("Disc.NeedUrl")); UrlBox.Focus(); return; }
        await Import(url);
    }

    private async void DistillUrl_Click(object sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text.Trim();
        if (url.Length == 0) { Fail(L.T("Disc.NeedUrl")); UrlBox.Focus(); return; }
        await Distill(url, "", LinkHintBox.Text.Trim());
    }

    private async void DistillText_Click(object sender, RoutedEventArgs e)
    {
        var text = PasteBox.Text.Trim();
        if (text.Length == 0) { Fail(L.T("Disc.NeedText")); PasteBox.Focus(); return; }
        await Distill("", text, PasteHintBox.Text.Trim());
    }

    private FrameworkElement ArticleRow(SkillSourceDto s)
    {
        var link = new Hyperlink(new Run(s.Name)) { NavigateUri = new Uri(s.Url) };
        link.RequestNavigate += Link_RequestNavigate;
        var use = new Button { Content = L.T("Disc.UseLink"), Margin = new Thickness(12, 0, 0, 0), MinHeight = 26, Padding = new Thickness(10, 2, 10, 2) };
        use.Click += (_, _) => UrlBox.Text = s.Url;
        var title = new TextBlock(link) { VerticalAlignment = VerticalAlignment.Center };
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(title);
        head.Children.Add(use);
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(head);
        panel.Children.Add(Muted(Describe(s)));
        return panel;
    }

    // ---- actions ----

    private async Task Import(string url)
    {
        await Busy("Disc.Loading", async () =>
        {
            var s = await App.Api.ImportSkillUrlAsync(url);
            Changed = true;
            Done(L.T("Disc.Imported", s.Name), ok: true);
        });
    }

    private async Task Distill(string url, string text, string hint)
    {
        SkillDraftDto? draft = null;
        await Busy("Disc.Distilling", async () =>
        {
            draft = await App.Api.DistillSkillAsync(url, text, hint);
            Done(draft.Truncated ? L.T("Disc.Truncated") : "");
        });
        if (draft is null) return;
        if (new SkillEditorWindow(null, draft.ToSkill()) { Owner = this }.ShowDialog() == true)
        {
            Changed = true;
            Done(L.T("Disc.Saved"), ok: true);
        }
    }

    // ---- helpers ----

    private async Task Busy(string statusKey, Func<Task> action)
    {
        Tabs.IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;
        Status(L.T(statusKey), "MutedBrush");
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            Tabs.IsEnabled = true;
        }
    }

    private void Done(string message, bool ok = false) => Status(message, ok ? "OkBrush" : "MutedBrush");

    private void Fail(string message) => Status(message, "DangerBrush");

    private void Status(string message, string brush)
    {
        StatusText.Text = message;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, brush);
    }

    private ListBoxItem Item(string title, string description, object tag)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (!string.IsNullOrWhiteSpace(description))
        {
            var d = Muted(description);
            d.MaxHeight = 54;
            d.TextTrimming = TextTrimming.CharacterEllipsis;
            panel.Children.Add(d);
        }
        return new ListBoxItem { Content = panel, Tag = tag, ToolTip = string.IsNullOrWhiteSpace(description) ? null : description };
    }

    private static TextBlock Muted(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        return t;
    }

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        OpenUrl(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
