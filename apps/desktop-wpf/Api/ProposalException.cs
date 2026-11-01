using NovelGm.Desktop.Models;

namespace NovelGm.Desktop.Api;

public sealed class ProposalException : InvalidOperationException
{
    public string Code { get; }
    public IReadOnlyList<ProposalIssueDto> Issues { get; }
    internal ProposalException(string code, string message, List<ProposalIssueDto>? issues = null) : base(message)
    { Code = code; Issues = issues ?? new(); }
}
