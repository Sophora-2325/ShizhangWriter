namespace NovelGm.Desktop.Api;

/// <summary>工具台动作的可用性输入。</summary>
public readonly record struct ToolContext(
    bool HasWork,
    bool HasChapter,
    bool HasBody,
    bool Editable,
    bool CanComment,
    bool HasFolder,
    bool Busy);

/// <summary>工具台敏感词行的状态。只在本机、不持久化。</summary>
public enum SensitiveScanState { NotRun, Scanning, Clean, Found, Stale }

/// <summary>
/// 工具台每一行的启用规则。只做判断，不碰 UI，便于单测覆盖空态、只读与运行中。
/// </summary>
public static class ToolRules
{
    public const string Stats = "stats";
    public const string Copy = "copy";
    public const string ExportChapter = "export-chapter";
    public const string FindChapter = "find-chapter";
    public const string FindBook = "find-book";
    public const string Sensitive = "sensitive";
    public const string Spacing = "spacing";
    public const string Invisibles = "invisibles";
    public const string CommentAdd = "comment-add";
    public const string CommentList = "comment-list";
    public const string Versions = "versions";
    public const string Backup = "backup";
    public const string Publish = "publish";

    public static bool IsEnabled(string action, ToolContext c) => action switch
    {
        Stats or Copy => c.HasChapter && c.HasBody,
        ExportChapter => c.HasChapter && c.HasBody && !c.Busy,
        FindChapter => c.HasChapter && !c.Busy,
        FindBook => c.HasWork && !c.Busy,
        Sensitive => c.HasWork,
        Spacing => c.HasBody && c.Editable,
        Invisibles => c.HasBody,
        CommentAdd => c.HasBody && c.Editable && c.CanComment,
        CommentList => c.HasChapter,
        Versions => c.HasChapter && !c.Busy,
        Backup => c.HasWork && c.HasFolder && !c.Busy,
        Publish => c.HasWork && !c.Busy,
        _ => false,
    };
}
