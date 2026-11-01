namespace NovelGm.Desktop.Api;

/// <summary>一次「从审阅定位到正文」的请求：作品、章节、起止锚点和审阅里选中的原文。</summary>
public sealed record ReviewLocateRequest(string ProjectId, int ChapterNo, ReadingAnchor Start, ReadingAnchor End, string Quote);

/// <summary>
/// 把审阅文本映射回正文纯文本。重复文本绝不随便跳：只接受「正文版本一致时的原偏移」，
/// 或前后文唯一命中的候选；仍多义就返回 false，由外壳提示。
/// </summary>
public static class ReviewLocator
{
    public static bool TryResolve(string bodyText, ReviewLocateRequest request, out int start, out int length, out bool exact)
    {
        start = 0;
        length = 0;
        exact = false;
        if (request is null) return false;
        var quote = Normalize(request.Quote);
        if (quote.Length == 0) return false;
        var text = Normalize(bodyText);

        var hits = new List<int>();
        for (var i = text.IndexOf(quote, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(quote, i + 1, StringComparison.Ordinal))
            hits.Add(i);
        if (hits.Count == 0) return false;
        if (hits.Count == 1)
        {
            start = hits[0];
            length = quote.Length;
            exact = true;
            return true;
        }

        // 正文版本一致时，原偏移就是最可靠的答案。
        var anchor = request.Start;
        var revision = ReadingAnchor.Hash(text);
        if (anchor is not null && revision == anchor.Revision)
        {
            var offset = Math.Clamp(anchor.Offset, 0, text.Length);
            if (offset + quote.Length <= text.Length
                && string.CompareOrdinal(text, offset, quote, 0, quote.Length) == 0)
            {
                start = offset;
                length = quote.Length;
                exact = true;
                return true;
            }
        }

        if (anchor is null) return false;

        // 正文改过：只有前后文唯一命中的那一处才算可靠。
        var contextual = hits.Where(i => PositionMatches(text, i, anchor)).ToList();
        if (contextual.Count == 1)
        {
            start = contextual[0];
            length = quote.Length;
            return true;
        }
        // 起止锚点都指向同一处时，用结束位置再校验一次（不用于放宽匹配）。
        if (contextual.Count > 1 && request.End is not null)
        {
            var ended = contextual.Where(i => EndMatches(text, i, quote.Length, request.End)).ToList();
            if (ended.Count == 1)
            {
                start = ended[0];
                length = quote.Length;
                return true;
            }
        }
        return false;
    }

    /// <summary>锚点位置（起点或终点）的前文/后文必须同时吻合。</summary>
    private static bool PositionMatches(string text, int position, ReadingAnchor anchor)
    {
        var before = Normalize(anchor.Before);
        var after = Normalize(anchor.After);
        if (position < before.Length || position > text.Length || after.Length > text.Length - position) return false;
        return (before.Length == 0 || string.CompareOrdinal(text, position - before.Length, before, 0, before.Length) == 0)
               && (after.Length == 0 || string.CompareOrdinal(text, position, after, 0, after.Length) == 0);
    }

    private static bool EndMatches(string text, int start, int length, ReadingAnchor anchor)
    {
        var end = start + length;
        if (ReadingAnchor.Hash(text) == anchor.Revision)
            return Math.Clamp(anchor.Offset, 0, text.Length) == end;
        return PositionMatches(text, end, anchor);
    }

    private static string Normalize(string? text) =>
        (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
}
