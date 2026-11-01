using NovelGm.Desktop.Models;

namespace NovelGm.Desktop.Api;

/// <summary>Window-local draft. The patch excludes every field the author did not change.</summary>
internal sealed class WorkInfoDraft
{
    private readonly ProjectDto _baseline;
    public string Genre { get; set; }
    public string Tone { get; set; }
    public string Audience { get; set; }
    public CoverAssetDto Cover { get; set; }
    public WorkInfoDraft(ProjectDto project)
    {
        _baseline = project;
        Genre = string.Join(", ", project.Genre); Tone = project.Tone; Audience = project.Audience;
        Cover = new() { Preview = project.Cover.Preview, Original = project.Cover.Original };
    }
    public WorkInfoUpdateDto Changes()
    {
        var genre = Genre.Split(new[] { ',', '，', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(v => v.Trim()).Where(v => v.Length > 0).Distinct().ToList();
        return new()
        {
            Genre = genre.SequenceEqual(_baseline.Genre) ? null : genre,
            Tone = Tone.Trim() == _baseline.Tone ? null : Tone.Trim(),
            Audience = Audience.Trim() == _baseline.Audience ? null : Audience.Trim(),
            Cover = Cover.Preview == _baseline.Cover.Preview && Cover.Original == _baseline.Cover.Original
                ? null : new() { Preview = Cover.Preview, Original = Cover.Original },
        };
    }
}
