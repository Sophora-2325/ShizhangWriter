using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace NovelGm.Desktop.Api;

/// <param name="Title">发给后端的标题，已去掉「第X章」前缀；为空时后端用「第N章」。</param>
/// <param name="Heading">确认框里显示的原始标题行。</param>
public sealed record ImportedChapter(string Title, string Heading, string Body);

/// <summary>把外部 TXT / Markdown 按「第X章」标题行拆成章节。</summary>
public static class TextImport
{
    private static readonly Regex HeadingLine = new(
        @"^\s*#*\s*第[0-9０-９零〇一二三四五六七八九十百千两]+[章回节卷](?<rest>.*)$", RegexOptions.Compiled);

    /// <summary>超过这个长度的行当作正文，避免把「第三回合……」开头的段落误认成标题。</summary>
    private const int MaxHeadingLength = 40;

    static TextImport() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static List<ImportedChapter> Split(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var lines = Read(path).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var result = new List<ImportedChapter>();
        string? title = null, heading = null;
        var body = new List<string>();

        void Flush()
        {
            var text = string.Join("\n", body).Trim('\n', ' ', '\u3000');
            if (heading is not null) result.Add(new ImportedChapter(title ?? "", heading, text));
            else if (text.Trim().Length > 0) result.Add(new ImportedChapter(name, name, text));
            body.Clear();
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            var m = line.Length <= MaxHeadingLength ? HeadingLine.Match(line) : Match.Empty;
            if (!m.Success)
            {
                body.Add(line);
                continue;
            }
            Flush();
            heading = line.Trim().TrimStart('#').Trim();
            title = m.Groups["rest"].Value.Trim(' ', '\u3000', ':', '：', '、', '.', '-', '—');
        }
        Flush();
        return result;
    }

    /// <summary>先按 UTF-8 严格解码，失败再按 GB18030（老的中文 TXT 多是 GBK）。</summary>
    private static string Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes, start, bytes.Length - start);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding("GB18030").GetString(bytes);
        }
    }
}
