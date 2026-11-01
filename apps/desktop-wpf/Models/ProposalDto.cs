using System.Text.Json;
using NovelGm.Desktop.Api;

namespace NovelGm.Desktop.Models;

public sealed class ProposalWorkspaceDto
{
    public long Revision { get; set; }
    public ProposalDto? Draft { get; set; }
    public ProposalDto? Adopted { get; set; }
}

public sealed class ProposalDto
{
    public string Id { get; set; } = "";
    public string Chapter_Uid { get; set; } = "";
    public string Plot_Outline { get; set; } = "";
    public Dictionary<string, JsonElement> Proposed_Plan { get; set; } = new();
    public List<ProposalQuestionDto> Questions { get; set; } = new();
    public List<AuthorDecisionDto> Author_Decisions { get; set; } = new();
    public string Used_Answers_Hash { get; set; } = "";
    public string Active_History_Id { get; set; } = "";
    public string? Adopted_At { get; set; }
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public ProposalUsageDto Usage { get; set; } = new();
    public JsonElement Source_Manifest { get; set; } = JsonSerializer.SerializeToElement(new Dictionary<string, string>());
}

public sealed class ProposalUsageDto { public double? Cost_Usd { get; set; } }

public sealed class ProposalQuestionDto
{
    public string Id { get; set; } = "";
    public string Question { get; set; } = "";
    public string Why { get; set; } = "";
    public List<string> Options { get; set; } = new();
    public string Answer { get; set; } = "";
    public string Related_Field { get; set; } = "";
}

public sealed class AuthorDecisionDto
{
    public string Id { get; set; } = "";
    public string Question { get; set; } = "";
    public string Answer { get; set; } = "";
}

public sealed class ProposalStatusDto
{
    public string State { get; set; } = "none";
    public List<string> Reasons { get; set; } = new();
}

public sealed class ProposalDiffDto
{
    public string Field { get; set; } = "";
    public JsonElement Before { get; set; }
    public JsonElement After { get; set; }
}

public sealed class ProposalViewDto
{
    public ChapterDto Chapter { get; set; } = new();
    public ProposalWorkspaceDto Workspace { get; set; } = new();
    public ProposalStatusDto Draft_Status { get; set; } = new();
    public ProposalStatusDto Adopted_Status { get; set; } = new();
    public string Input_Token { get; set; } = "";
    public List<ProposalDiffDto> Diff { get; set; } = new();
    public string Operation { get; set; } = "read";
    public bool Writable { get; set; }
    public bool Answers_Changed { get; set; }
}

public sealed class ProposalCommandDto
{
    public string Chapter_Uid { get; set; } = "";
    public long Expected_Revision { get; set; }
    public string Request_Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Input_Token { get; set; } = "";
    public string Mode { get; set; } = "generate";
    public Dictionary<string, string> Answers { get; set; } = new();
    public Dictionary<string, string> Decisions { get; set; } = new();
    public string? Plot_Outline { get; set; }
}

public sealed class ProposalHistoryDto
{
    public string Record_Id { get; set; } = "";
    public string Saved_At { get; set; } = "";
    public string Action { get; set; } = "";
    public string Model { get; set; } = "";
    public string Provider { get; set; } = "";
    public ProposalUsageDto Usage { get; set; } = new();
    public string? Adopted_At { get; set; }
    public bool Protected { get; set; }
    public string Display => $"{(DateTimeOffset.TryParse(Saved_At, out var time) ? time.ToLocalTime().ToString("MM-dd HH:mm") : Saved_At)} · {L.T("Proposal.History." + Action)}";
    public string Metadata => $"{Provider} · {Model} · " + (Usage.Cost_Usd is { } cost ? $"${cost:0.####}" : L.T("Proposal.UnknownCost"));
}

public sealed class ProposalRecordDto
{
    public string Record_Id { get; set; } = "";
    public string Saved_At { get; set; } = "";
    public string Action { get; set; } = "";
    public ProposalDto Proposal { get; set; } = new();
}

public sealed class ProposalEvidenceDto
{
    public string Kind { get; set; } = "";
    public string Field { get; set; } = "";
    public string Id { get; set; } = "";
    public string Quote { get; set; } = "";
    public string Track_Id { get; set; } = "";
    public string Segment_Id { get; set; } = "";
    public string Name { get; set; } = "";
    public double Start { get; set; }
    public double End { get; set; }
}

public sealed class ProposalIssueDto
{
    public ProposalEvidenceDto Left { get; set; } = new();
    public ProposalEvidenceDto Right { get; set; } = new();
    public string Reason { get; set; } = "";
}
