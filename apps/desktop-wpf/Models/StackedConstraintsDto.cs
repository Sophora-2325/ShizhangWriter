using System.Text.Json;

namespace NovelGm.Desktop.Models;

public sealed class RhythmIssueDto
{
    public string Plan_Field { get; set; } = "";
    public string Plan_Quote { get; set; } = "";
    public string Track_Id { get; set; } = "";
    public string Segment_Id { get; set; } = "";
    public string Domain { get; set; } = "";
    public string Name { get; set; } = "";
    public string Element_Id { get; set; } = "";
    public double Start { get; set; }
    public double End { get; set; }
    public string Directive { get; set; } = "";
    public string Reason { get; set; } = "";
}

public sealed class ConstraintSegmentDto
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public double Start { get; set; }
    public double End { get; set; } = 20;
    public double Top { get; set; } = 10;
    public double Bottom { get; set; } = 90;
    public string Directive { get; set; } = "";
    public ConstraintSegmentDto Copy() => (ConstraintSegmentDto)MemberwiseClone();
}

public sealed class ConstraintTrackDto
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Domain { get; set; } = "thread";
    public string Element_Id { get; set; } = "";
    public string Name { get; set; } = "";
    public double Weight { get; set; } = 0.5;
    public List<ConstraintSegmentDto> Segments { get; set; } = new();
    public ConstraintTrackDto Copy() => new()
    {
        Id = Id, Domain = Domain, Element_Id = Element_Id, Name = Name, Weight = Weight,
        Segments = Segments.Select(s => s.Copy()).ToList(),
    };
}

public sealed class StackedConstraintsDto
{
    public List<ConstraintTrackDto> Tracks { get; set; } = new();
    public StackedConstraintsDto Copy() => new() { Tracks = Tracks.Select(t => t.Copy()).ToList() };
    internal string Fingerprint() => JsonSerializer.Serialize(this);
}
