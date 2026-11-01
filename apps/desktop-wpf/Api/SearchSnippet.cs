namespace NovelGm.Desktop.Api;

internal readonly record struct SearchSnippet(string Text, int MatchOffset, int MatchLength)
{
    internal static SearchSnippet Create(string context, int offset, int length, int budget)
    {
        budget = Math.Max(4, budget);
        var valid = offset >= 0 && length > 0 && offset <= context.Length - length;
        var start = valid ? Math.Max(0, offset - Math.Max(0, (budget - length) / 2)) : 0;
        start = Math.Min(start, Math.Max(0, context.Length - budget));
        // Avoid splitting a surrogate pair at either end of the preview.
        if (start > 0 && char.IsLowSurrogate(context[start])) start--;
        var end = Math.Min(context.Length, start + budget);
        if (end < context.Length && char.IsLowSurrogate(context[end])) end--;
        var prefix = start > 0 ? "…" : "";
        var text = prefix + context[start..end].Replace('\r', ' ').Replace('\n', ' ')
            + (end < context.Length ? "…" : "");
        return new(text, valid ? prefix.Length + offset - start : -1,
            valid ? Math.Max(0, Math.Min(length, end - offset)) : 0);
    }
}
