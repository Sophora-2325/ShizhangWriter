using NovelGm.Desktop.Models;

namespace NovelGm.Desktop.Api;

/// <summary>历史版本的后端调用；窗口只依赖这个接口，测试可注入假实现。</summary>
public interface IVersionsApi
{
    Task<List<VersionDto>> ListAsync(string projectId, int chapterNo);
    Task<VersionDetailDto> GetAsync(string projectId, int chapterNo, string versionId);
    Task<ChapterDto> RestoreAsync(string projectId, int chapterNo, string versionId);

    /// <summary>读章节状态用（判断 superseded 以禁用恢复）；失败返回 null，不影响查看。</summary>
    Task<ChapterDto?> TryGetChapterAsync(string projectId, int chapterNo);
}

/// <summary>IVersionsApi 的 HTTP 实现，路由见 docs/ROUND_NOTES_AI_PLAN.md §2.2。</summary>
public sealed class VersionsClient : IVersionsApi
{
    private readonly NovelApiClient _api;

    public VersionsClient(NovelApiClient api) => _api = api;

    public Task<List<VersionDto>> ListAsync(string projectId, int chapterNo) =>
        _api.ListVersionsAsync(projectId, chapterNo);

    public Task<VersionDetailDto> GetAsync(string projectId, int chapterNo, string versionId) =>
        _api.GetVersionAsync(projectId, chapterNo, versionId);

    public Task<ChapterDto> RestoreAsync(string projectId, int chapterNo, string versionId) =>
        _api.RestoreVersionAsync(projectId, chapterNo, versionId);

    public async Task<ChapterDto?> TryGetChapterAsync(string projectId, int chapterNo)
    {
        try
        {
            return await _api.GetChapterAsync(projectId, chapterNo);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
