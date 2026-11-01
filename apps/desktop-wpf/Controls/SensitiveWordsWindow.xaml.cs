using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using NovelGm.Desktop.Api;
using NovelGm.Desktop.Models;

namespace NovelGm.Desktop.Controls;

/// <summary>一次敏感词扫描的结果，供工具台显示状态。</summary>
public enum SensitiveScanOutcome { NoWords, NoChapter, Clean, Found }

public sealed record SensitiveScanResult(bool BookScope, int HitCount, SensitiveScanOutcome Outcome);

/// <summary>
/// 本地敏感词检测窗口：词库增删 / 导入导出（UTF-8），当前章或全书检测，结果按章列出并定位。
/// 契约见 docs/ROUND_NOTES_AI_PLAN.md §3.5。只提示命中位置，不自动替换正文。
/// </summary>
public sealed partial class SensitiveWordsWindow : Window
{
    private readonly Func<IReadOnlyList<ChapterDto>> _chaptersProvider;
    private readonly Func<int, (int No, string Text)?> _draftProvider;
    private readonly Action<SensitiveHit> _locate;
    private readonly int _currentChapterNo;
    private readonly SensitiveWords _words;

    private bool _bookScope;
    private List<SensitiveHit> _hits = new();

    /// <summary>扫描开始/结束：工具台据此显示「检测中 / 发现问题 / 未发现问题」。</summary>
    public event EventHandler? ScanStarted;
    public event EventHandler<SensitiveScanResult>? ScanFinished;

    private string? _statusKey;
    private object?[] _statusArgs = Array.Empty<object?>();
    private bool _statusError;

    public SensitiveWordsWindow(Func<IReadOnlyList<ChapterDto>> chaptersProvider,
        Func<int, (int No, string Text)?> draftProvider, Action<SensitiveHit> locate, int currentChapterNo)
        : this(chaptersProvider, draftProvider, locate, currentChapterNo, new SensitiveWords())
    {
    }

    internal SensitiveWordsWindow(Func<IReadOnlyList<ChapterDto>> chaptersProvider,
        Func<int, (int No, string Text)?> draftProvider, Action<SensitiveHit> locate, int currentChapterNo,
        SensitiveWords words)
    {
        _chaptersProvider = chaptersProvider;
        _draftProvider = draftProvider;
        _locate = locate;
        _currentChapterNo = currentChapterNo;
        _words = words;

        InitializeComponent();
        Loaded += (_, _) =>
        {
            RefreshWordList();
            UpdateScopeButtons();
            // 先让「检测中」有机会绘制，再同步扫描。
            Dispatcher.BeginInvoke(new Action(() => { if (IsLoaded) RunScan(); }), DispatcherPriority.Background);
        };
        EventHandler relabel = (_, _) => OnLanguageChanged();
        LanguageManager.LanguageChanged += relabel;
        Closed += (_, _) => LanguageManager.LanguageChanged -= relabel;
    }

    internal IReadOnlyList<SensitiveHit> HitsForTest => _hits;
    internal string StatusTextForTest => StatusText.Text;
    internal bool StatusIsErrorForTest => _statusError;
    internal bool IsBookScopeForTest => _bookScope;
    internal SensitiveWords WordsForTest => _words;

    /// <summary>测试在窗口未 Show 时执行与 Loaded 相同的初始化。</summary>
    internal void RefreshForTest()
    {
        RefreshWordList();
        UpdateScopeButtons();
        RunScan();
    }

    // ------------------------------------------------------------------ 词库

    private void RefreshWordList()
    {
        WordList.ItemsSource = null;
        WordList.ItemsSource = _words.Words;
        LibraryHint.Text = _words.HasWords
            ? L.T("Sens.WordCount", _words.Words.Count)
            : L.T("Sens.EmptyHint");
    }

    private void Add_Click(object sender, RoutedEventArgs e) => AddWord();

    private void WordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        AddWord();
    }

    private void AddWord()
    {
        var value = WordBox.Text.Trim();
        if (value.Length == 0)
        {
            SetStatus("Sens.NeedWord", true);
            return;
        }
        if (!_words.Add(value))
        {
            SetStatus("Sens.Dup", true, value);
            return;
        }

        WordBox.Text = "";
        if (!_words.TrySave(out var error)) SetStatus("Sens.SaveFailed", true, error);
        RefreshWordList();
        RunScan();
        if (error is null) SetStatus("Sens.Added", false, value);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (WordList.SelectedItem is not string word)
        {
            SetStatus("Sens.NeedWord", true);
            return;
        }
        if (!_words.Remove(word))
        {
            SetStatus("Sens.NeedWord", true);
            return;
        }
        if (!_words.TrySave(out var error)) SetStatus("Sens.SaveFailed", true, error);
        RefreshWordList();
        RunScan();
        if (error is null) SetStatus("Sens.Deleted", false);
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "TXT (*.txt)|*.txt|*.*|*.*",
            Title = L.T("Sens.ImportTitle"),
        };
        if (dialog.ShowDialog(this) != true) return;

        var result = _words.Import(dialog.FileName);
        switch (result.Status)
        {
            case SensitiveImportStatus.Ok:
            {
                var saved = _words.TrySave(out var saveError);
                RefreshWordList();
                RunScan();
                if (!saved) SetStatus("Sens.SaveFailed", true, saveError);
                else if (result.Added > 0) SetStatus("Sens.ImportOk", false, result.Added);
                else SetStatus("Sens.ImportNone", false);
                break;
            }
            case SensitiveImportStatus.InvalidUtf8:
                SetStatus("Sens.ImportInvalid", true);
                break;
            case SensitiveImportStatus.Empty:
                SetStatus("Sens.ImportNone", false);
                break;
            default:
                SetStatus("Sens.ImportFailed", true, result.Error);
                break;
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!_words.HasWords)
        {
            SetStatus("Sens.ExportNoWords", true);
            return;
        }
        var dialog = new SaveFileDialog
        {
            Filter = "TXT (*.txt)|*.txt",
            FileName = "sensitive-words.txt",
            Title = L.T("Sens.ExportTitle"),
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            _words.Export(dialog.FileName);
            SetStatus("Sens.ExportOk", false, dialog.FileName);
        }
        catch (Exception ex)
        {
            SetStatus("Sens.ExportFailed", true, ex.Message);
        }
    }

    // ------------------------------------------------------------------ 范围 / 检测

    private void ScopeChapter_Click(object sender, RoutedEventArgs e) => SetScope(false);

    private void ScopeBook_Click(object sender, RoutedEventArgs e) => SetScope(true);

    private void SetScope(bool book)
    {
        _bookScope = book;
        UpdateScopeButtons();
        RunScan();
    }

    private void UpdateScopeButtons()
    {
        ScopeChapterButton.IsChecked = !_bookScope;
        ScopeBookButton.IsChecked = _bookScope;
    }

    private void Rescan_Click(object sender, RoutedEventArgs e) => RunScan();

    internal void RunScan()
    {
        ScanStarted?.Invoke(this, EventArgs.Empty);
        _hits = new List<SensitiveHit>();
        if (!_words.HasWords)
        {
            RenderResults();
            SetStatus("Sens.NoWordsScan", false);
            ScanFinished?.Invoke(this, new SensitiveScanResult(_bookScope, 0, SensitiveScanOutcome.NoWords));
            return;
        }

        var chapters = CollectTexts();
        if (chapters.Count == 0)
        {
            RenderResults();
            SetStatus("Sens.NoChapter", true);
            ScanFinished?.Invoke(this, new SensitiveScanResult(_bookScope, 0, SensitiveScanOutcome.NoChapter));
            return;
        }

        var words = _words.Words.ToList();
        foreach (var (no, title, text) in chapters)
        {
            _hits.AddRange(SensitiveScanner.Scan(text, words, no, title));
            if (_hits.Count >= SensitiveScanner.MaxHits) break;
        }

        RenderResults();
        SetStatus("Sens.Hits", false, _hits.Count);
        ScanFinished?.Invoke(this, new SensitiveScanResult(_bookScope, _hits.Count,
            _hits.Count > 0 ? SensitiveScanOutcome.Found : SensitiveScanOutcome.Clean));
    }

    private List<(int No, string Title, string Text)> CollectTexts()
    {
        var result = new List<(int No, string Title, string Text)>();
        IReadOnlyList<ChapterDto> chapters;
        try
        {
            chapters = _chaptersProvider() ?? Array.Empty<ChapterDto>();
        }
        catch (Exception)
        {
            chapters = Array.Empty<ChapterDto>();
        }

        if (_bookScope)
        {
            foreach (var chapter in chapters)
            {
                if (string.Equals(chapter.Status, "superseded", StringComparison.OrdinalIgnoreCase)) continue;
                result.Add((chapter.No, chapter.Title ?? "", chapter.Body_Text ?? ""));
            }
            return result;
        }

        (int No, string Text)? draft = null;
        try
        {
            draft = _draftProvider(_currentChapterNo);
        }
        catch (Exception)
        {
            draft = null;
        }

        if (draft is { } text)
        {
            result.Add((text.No, TitleFor(text.No, chapters), text.Text ?? ""));
            return result;
        }

        var current = chapters.FirstOrDefault(c => c.No == _currentChapterNo);
        if (current is not null &&
            !string.Equals(current.Status, "superseded", StringComparison.OrdinalIgnoreCase))
        {
            result.Add((current.No, current.Title ?? "", current.Body_Text ?? ""));
        }
        return result;
    }

    private static string TitleFor(int chapterNo, IReadOnlyList<ChapterDto> chapters) =>
        chapters.FirstOrDefault(c => c.No == chapterNo)?.Title ?? "";

    private void RenderResults()
    {
        ResultsPanel.Children.Clear();
        if (_hits.Count == 0)
        {
            EmptyHint.Text = _words.HasWords ? L.T("Sens.NoHit") : L.T("Sens.EmptyHint");
            EmptyHint.Visibility = Visibility.Visible;
            return;
        }

        EmptyHint.Visibility = Visibility.Collapsed;
        foreach (var group in _hits.GroupBy(h => h.ChapterNo))
        {
            var title = group.First().ChapterTitle;
            var label = string.IsNullOrWhiteSpace(title)
                ? L.T("Sens.ChapterOnly", group.Key)
                : L.T("Sens.ChapterTitle", group.Key, title);
            ResultsPanel.Children.Add(new TextBlock
            {
                Text = label + " · " + L.T("Sens.Count", group.Count()),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(2, 8, 0, 4),
                TextWrapping = TextWrapping.Wrap,
                Foreground = BrushOr("TextBrush", Brushes.Black),
            });

            foreach (var hit in group)
            {
                var button = new Button
                {
                    Content = BuildHitText(hit),
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(8, 4, 8, 4),
                    Margin = new Thickness(0, 1, 0, 2),
                    MinHeight = 24,
                    FontSize = 12,
                    Background = BrushOr("Bg2Brush", Brushes.Transparent),
                    Tag = hit,
                    ToolTip = L.T("Sens.LocateTip"),
                };
                button.Click += Hit_Click;
                ResultsPanel.Children.Add(button);
            }
        }
    }

    private TextBlock BuildHitText(SensitiveHit hit)
    {
        var block = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = BrushOr("TextBrush", Brushes.Black),
        };
        block.Inlines.Add(new Run(hit.Word)
        {
            FontWeight = FontWeights.Bold,
            Foreground = BrushOr("DangerBrush", Brushes.IndianRed),
        });
        block.Inlines.Add(new Run("  " + hit.Context));
        return block;
    }

    private void Hit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SensitiveHit hit }) return;
        _locate(hit);
    }

    // ------------------------------------------------------------------ 状态行 / 语言

    private Brush BrushOr(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    private void SetStatus(string? key, bool error, params object?[] args)
    {
        _statusKey = string.IsNullOrEmpty(key) ? null : key;
        _statusArgs = args ?? Array.Empty<object?>();
        _statusError = error;
        RenderStatus();
    }

    private void RenderStatus()
    {
        StatusText.Text = _statusKey is null
            ? ""
            : (_statusArgs.Length == 0 ? L.T(_statusKey) : L.T(_statusKey, _statusArgs));
        StatusText.Foreground = _statusError
            ? BrushOr("DangerBrush", Brushes.IndianRed)
            : BrushOr("MutedBrush", Brushes.Gray);
    }

    private void OnLanguageChanged()
    {
        RefreshWordList();
        RenderResults();
        RenderStatus();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
