using NovelGm.Desktop.Models;

namespace NovelGm.Desktop.Api;

/// <summary>统一查找的后端调用；面板只依赖这个接口，测试可注入假实现。</summary>
public interface ISearchApi
{
    Task<SearchResponseDto> SearchAsync(string projectId, SearchRequestDto request);
}

/// <summary>ISearchApi 的 HTTP 实现，路由见 docs/ROUND_NOTES_AI_PLAN.md §2.3。</summary>
public sealed class SearchClient : ISearchApi
{
    private readonly NovelApiClient _api;

    public SearchClient(NovelApiClient api) => _api = api;

    public Task<SearchResponseDto> SearchAsync(string projectId, SearchRequestDto request) =>
        _api.SearchAsync(projectId, request);
}
