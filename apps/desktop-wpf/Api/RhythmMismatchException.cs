using NovelGm.Desktop.Models;

namespace NovelGm.Desktop.Api;

public sealed class RhythmMismatchException : InvalidOperationException
{
    public string ProjectId { get; }
    public int ChapterNo { get; }
    public IReadOnlyList<RhythmIssueDto> Issues { get; }
    internal RhythmMismatchException(string message, string projectId, int chapterNo, List<RhythmIssueDto> issues) : base(message)
    { ProjectId = projectId; ChapterNo = chapterNo; Issues = issues; }
}
