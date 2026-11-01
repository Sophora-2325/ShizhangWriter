using System.IO;
using System.Text;
using NovelGm.Desktop.Models;

namespace NovelGm.Desktop.Api;

/// <summary>
/// 本地敏感词库：%APPDATA%\novel-gm\sensitive-words.txt，UTF-8、每行一词。
/// 读入时去空行、trim、去重（忽略大小写）。增删只改内存，由窗口调 <see cref="TrySave"/> 落盘。
/// 检测只用本地词库，不联网，也不自动修改正文。
/// </summary>
public sealed class SensitiveWords
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UTF8Encoding LooseUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    private readonly List<string> _words = new();

    public SensitiveWords(string? filePath = null)
    {
        FilePath = string.IsNullOrWhiteSpace(filePath) ? DefaultPath : filePath;
        Load();
    }

    /// <summary>默认词库路径；测试可传临时路径。</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "novel-gm", "sensitive-words.txt");

    public string FilePath { get; }

    public IReadOnlyList<string> Words => _words;

    public bool HasWords => _words.Count > 0;

    /// <summary>读词库；文件不存在或读不了时视为空词库（不抛错，窗口会提示添加/导入）。</summary>
    public void Load()
    {
        _words.Clear();
        try
        {
            if (!File.Exists(FilePath)) return;
            AddWords(File.ReadAllText(FilePath, LooseUtf8));
        }
        catch (Exception)
        {
            _words.Clear();
        }
    }

    /// <summary>追加一个词条；空白或已存在（忽略大小写）返回 false。</summary>
    public bool Add(string? word)
    {
        var value = Normalize(word);
        if (value.Length == 0 || Contains(value)) return false;
        _words.Add(value);
        return true;
    }

    /// <summary>删除一个词条；返回是否真的删掉了。</summary>
    public bool Remove(string? word)
    {
        var value = Normalize(word);
        if (value.Length == 0) return false;
        var index = IndexOf(value);
        if (index < 0) return false;
        _words.RemoveAt(index);
        return true;
    }

    /// <summary>导入一个 UTF-8 TXT：严格解码，不合法时返回 <see cref="SensitiveImportStatus.InvalidUtf8"/>。</summary>
    public SensitiveImportResult Import(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path, StrictUtf8);
        }
        catch (DecoderFallbackException)
        {
            return new SensitiveImportResult(SensitiveImportStatus.InvalidUtf8, 0, "");
        }
        catch (Exception ex)
        {
            return new SensitiveImportResult(SensitiveImportStatus.Failed, 0, ex.Message);
        }

        if (string.IsNullOrWhiteSpace(text))
            return new SensitiveImportResult(SensitiveImportStatus.Empty, 0, "");
        var added = AddWords(text);
        return new SensitiveImportResult(SensitiveImportStatus.Ok, added, "");
    }

    /// <summary>导出词库：UTF-8（无 BOM）、CRLF 换行，每行一词。</summary>
    public void Export(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var text = string.Join("\r\n", _words);
        if (_words.Count > 0) text += "\r\n";
        File.WriteAllText(path, text, LooseUtf8);
    }

    /// <summary>写入词库；失败时把可读原因放进 error，交给状态行显示。</summary>
    public bool TrySave(out string? error)
    {
        try
        {
            Save();
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var text = string.Join("\r\n", _words);
        if (_words.Count > 0) text += "\r\n";
        File.WriteAllText(FilePath, text, LooseUtf8);
    }

    private int AddWords(string? text)
    {
        var added = 0;
        foreach (var line in (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var value = Normalize(line);
            if (value.Length == 0 || Contains(value)) continue;
            _words.Add(value);
            added++;
        }
        return added;
    }

    private bool Contains(string value) => IndexOf(value) >= 0;

    private int IndexOf(string value) =>
        _words.FindIndex(w => string.Equals(w, value, StringComparison.OrdinalIgnoreCase));

    private static string Normalize(string? word) => (word ?? "").Trim();
}

public enum SensitiveImportStatus
{
    Ok,
    /// <summary>文件不是合法的 UTF-8。</summary>
    InvalidUtf8,
    Failed,
    /// <summary>文件里没有可用词条。</summary>
    Empty,
}

public sealed record SensitiveImportResult(SensitiveImportStatus Status, int Added, string Error);

/// <summary>一条检测结果：章号 / 词 / 正文纯文本偏移 / 上下文。</summary>
public sealed record SensitiveHit(int ChapterNo, string ChapterTitle, string Word, int Offset, string Context);

/// <summary>本地敏感词扫描。纯文本子串匹配，忽略大小写；不跨章节取上下文。</summary>
public static class SensitiveScanner
{
    public const int ContextChars = 24;
    public const int MaxHits = 500;

    public static List<SensitiveHit> Scan(string? text, IReadOnlyList<string>? words, int chapterNo = 0,
        string chapterTitle = "")
    {
        var hits = new List<SensitiveHit>();
        if (string.IsNullOrEmpty(text) || words is null || words.Count == 0) return hits;

        foreach (var raw in words)
        {
            var word = (raw ?? "").Trim();
            if (word.Length == 0) continue;
            var index = 0;
            while (index <= text.Length - word.Length)
            {
                var at = text.IndexOf(word, index, StringComparison.OrdinalIgnoreCase);
                if (at < 0) break;
                var start = Math.Max(0, at - ContextChars);
                var end = Math.Min(text.Length, at + word.Length + ContextChars);
                var context = (start > 0 ? "…" : "") + text[start..end] + (end < text.Length ? "…" : "");
                hits.Add(new SensitiveHit(chapterNo, chapterTitle ?? "", word, at, context));
                index = at + Math.Max(1, word.Length);
                if (hits.Count >= MaxHits) break;
            }
            if (hits.Count >= MaxHits) break;
        }

        hits.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        return hits;
    }

    /// <summary>全书扫描；作废章节跳过。结果按章节顺序 + 偏移排列，单次最多 <see cref="MaxHits"/> 条。</summary>
    public static List<SensitiveHit> ScanChapters(IEnumerable<ChapterDto>? chapters, IReadOnlyList<string>? words)
    {
        var hits = new List<SensitiveHit>();
        if (words is null || words.Count == 0) return hits;
        foreach (var chapter in chapters ?? Array.Empty<ChapterDto>())
        {
            if (string.Equals(chapter.Status, "superseded", StringComparison.OrdinalIgnoreCase)) continue;
            hits.AddRange(Scan(chapter.Body_Text, words, chapter.No, chapter.Title));
            if (hits.Count >= MaxHits) break;
        }
        return hits;
    }
}
