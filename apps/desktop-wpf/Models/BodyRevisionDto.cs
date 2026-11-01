namespace NovelGm.Desktop.Models;

public sealed class BodyRevisionPatchDto
{
    public string Id { get; set; } = "";
    public string Before { get; set; } = "";
    public string After { get; set; } = "";
    public string Reason { get; set; } = "";
    public int Start { get; set; }
    public int End { get; set; }
    public string Unavailable { get; set; } = "";
}
public sealed class BodyRevisionWorkspaceDto
{
    public long Revision { get; set; }
    public string Instruction { get; set; } = "";
    public List<string> Selected_Issues { get; set; } = new();
    public string Preview_Id { get; set; } = "";
    public string Input_Token { get; set; } = "";
    public List<BodyRevisionPatchDto> Patches { get; set; } = new();
}
public sealed class BodyRevisionIssueDto
{
    public string Id { get; set; } = "";
    public string Source { get; set; } = "";
    public string Point { get; set; } = "";
    public string Evidence { get; set; } = "";
    public string Suggestion { get; set; } = "";
}
public sealed class BodyRevisionViewDto
{
    public ChapterDto Chapter { get; set; } = new();
    public BodyRevisionWorkspaceDto Workspace { get; set; } = new();
    public string Input_Token { get; set; } = "";
    public bool Preview_Valid { get; set; }
    public bool Writable { get; set; }
    public string Operation { get; set; } = "read";
    public List<BodyRevisionIssueDto> Issues { get; set; } = new();
}
public sealed class BodyRevisionCommandDto
{
    public string Chapter_Uid { get; set; } = "";
    public long Expected_Revision { get; set; }
    public string Request_Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Input_Token { get; set; } = "";
    public string Instruction { get; set; } = "";
    public List<string> Selected_Issues { get; set; } = new();
    public string Preview_Id { get; set; } = "";
    public List<string> Patch_Ids { get; set; } = new();
    public string Body_Text { get; set; } = "";
    public string Body_Rich { get; set; } = "";
}
