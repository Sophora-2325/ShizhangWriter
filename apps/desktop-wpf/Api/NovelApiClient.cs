using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using NovelGm.Desktop.Models;

namespace NovelGm.Desktop.Api;

public sealed class NovelApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    public NovelApiClient(string? baseUrl = null)
    {
        _http = new HttpClient(new LanguageHeaderHandler { InnerHandler = new HttpClientHandler() })
        {
            BaseAddress = new Uri(baseUrl ?? BackendLauncher.BaseUrl),
            Timeout = TimeSpan.FromMinutes(10),
        };
    }

    public void Dispose() => _http.Dispose();

    public Task<HealthDto> HealthAsync() => GetAsync<HealthDto>("/health");
    public async Task<UsageDto> GetSessionUsageAsync(DateTimeOffset since, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        var path = "/usage/session?since=" + Uri.EscapeDataString(since.UtcDateTime.ToString("O"));
        using var response = await _http.GetAsync(path, timeout.Token);
        return await ReadAsync<UsageDto>(response);
    }
    public Task<BodyRevisionViewDto> GetBodyRevisionAsync(string id, int no) => GetAsync<BodyRevisionViewDto>(ProposalPath(id, no) + "/body-revision");
    public Task<BodyRevisionViewDto> SaveBodyRevisionAsync(string id, int no, BodyRevisionCommandDto command) => PutAsync<BodyRevisionViewDto>(ProposalPath(id, no) + "/body-revision", command);
    public Task<BodyRevisionViewDto> PreviewBodyRevisionAsync(string id, int no, BodyRevisionCommandDto command) => PostAsync<BodyRevisionViewDto>(ProposalPath(id, no) + "/body-revision/preview", command);
    public Task<BodyRevisionViewDto> ApplyBodyRevisionAsync(string id, int no, BodyRevisionCommandDto command) => PostAsync<BodyRevisionViewDto>(ProposalPath(id, no) + "/body-revision/apply", command);

    private static string ProposalPath(string id, int no) => $"/project/{Uri.EscapeDataString(id)}/chapter/{no}";
    public Task<ProposalViewDto> GetProposalAsync(string id, int no) => GetAsync<ProposalViewDto>(ProposalPath(id, no) + "/proposal");
    public Task<ProposalViewDto> GenerateProposalAsync(string id, int no, ProposalCommandDto command) =>
        PostAsync<ProposalViewDto>(ProposalPath(id, no) + "/proposal", command);
    public Task<ProposalViewDto> SaveProposalAnswersAsync(string id, int no, ProposalCommandDto command) =>
        PutAsync<ProposalViewDto>(ProposalPath(id, no) + "/proposal/answers", command);
    public Task<ProposalViewDto> ConfirmProposalAsync(string id, int no, ProposalCommandDto command) =>
        PostAsync<ProposalViewDto>(ProposalPath(id, no) + "/proposal/confirm", command);
    public Task<List<ProposalHistoryDto>> ListPlansAsync(string id, int no) => GetAsync<List<ProposalHistoryDto>>(ProposalPath(id, no) + "/plans");
    public Task<ProposalRecordDto> GetPlanRecordAsync(string id, int no, string record) =>
        GetAsync<ProposalRecordDto>(ProposalPath(id, no) + "/plans/" + Uri.EscapeDataString(record));
    public Task<ProposalViewDto> RestorePlanRecordAsync(string id, int no, string record, ProposalCommandDto command) =>
        PostAsync<ProposalViewDto>(ProposalPath(id, no) + "/plans/" + Uri.EscapeDataString(record) + "/restore", command);
    public Task<ProposalViewDto> DeletePlanRecordAsync(string id, int no, string record, ProposalCommandDto command) =>
        SendAsync<ProposalViewDto>(HttpMethod.Delete, ProposalPath(id, no) + "/plans/" + Uri.EscapeDataString(record), command);

    public Task<ChapterDto> SaveStackedConstraintsAsync(string projectId, int chapterNo, StackedConstraintsDto value) =>
        PutAsync<ChapterDto>($"/project/{Uri.EscapeDataString(projectId)}/chapter/{chapterNo}/stacked-constraints", value);

    public Task<ChapterDto> AcknowledgeStackedWeightMigrationAsync(string projectId, int chapterNo) =>
        PostAsync<ChapterDto>($"/project/{Uri.EscapeDataString(projectId)}/chapter/{chapterNo}/stacked-constraints/migration/ack", new { });

    public Task<SettingsDto> GetSettingsAsync() => GetAsync<SettingsDto>("/settings");

    public async Task<List<ProviderDto>> ListProvidersAsync() =>
        await GetAsync<List<ProviderDto>>("/providers") ?? new();

    public async Task<List<ProviderPresetDto>> ListPresetsAsync() =>
        await GetAsync<List<ProviderPresetDto>>("/providers/presets") ?? new();

    /// <summary>body 含 preset 和要填的字段：name、api_key、base_url、model_strong、model_fast、thinking、price。</summary>
    public Task<ProviderDto> CreateProviderAsync(object body) => PostAsync<ProviderDto>("/providers", body);

    /// <summary>patch 只含要改的字段：name、api_key、base_url、model_strong、model_fast、thinking、price、clear_price。</summary>
    public Task<ProviderDto> UpdateProviderAsync(string id, object patch) =>
        PutAsync<ProviderDto>($"/providers/{Uri.EscapeDataString(id)}", patch);

    public Task DeleteProviderAsync(string id) => DeleteAsync($"/providers/{Uri.EscapeDataString(id)}");

    public Task DeleteProviderKeyAsync(string id) => DeleteAsync($"/providers/{Uri.EscapeDataString(id)}/key");

    /// <summary>用还没保存的表单内容测试连接；draft 含 id（编辑时）或 preset，以及 api_key、base_url、模型名。</summary>
    public Task<ProviderTestDto> TestProviderDraftAsync(object draft) =>
        PostAsync<ProviderTestDto>("/providers/test", draft);

    public async Task<List<string>> ListModelsDraftAsync(object draft) =>
        (await PostAsync<ProviderModelsDto>("/providers/models", draft)).Models;

    /// <summary>更新全局设置；传入的匿名对象只包含要改的字段。</summary>
    public Task<SettingsDto> UpdateSettingsAsync(object patch) =>
        PostAsync<SettingsDto>("/settings", patch);

    public async Task<List<ProjectDto>> ListProjectsAsync() =>
        await GetAsync<List<ProjectDto>>("/projects") ?? new();

    public Task<ProjectDto> CreateProjectAsync(string title, string background, string? sample, string parentDir)
    {
        var body = new
        {
            title,
            parent_dir = parentDir,
            entries = string.IsNullOrWhiteSpace(background)
                ? Array.Empty<object>()
                : new object[]
                {
                    new
                    {
                        id = "",
                        category = "world",
                        title = "",
                        content = background,
                        hard = false,
                    },
                },
            characters = Array.Empty<object>(),
            style = new
            {
                pov = "third_limited",
                tense = "past",
                reference_samples = string.IsNullOrWhiteSpace(sample)
                    ? Array.Empty<string>()
                    : new[] { sample },
            },
            threads = new[]
            {
                new
                {
                    id = "main",
                    name = "主线",
                    kind = "main",
                    state = "active",
                    user_directive = string.IsNullOrWhiteSpace(background) ? "推进主线" : background,
                    next_milestone = "",
                    priority = 1,
                },
            },
        };
        return PostAsync<ProjectDto>("/project", body);
    }

    public Task<ProjectDto> GetProjectAsync(string id) =>
        GetAsync<ProjectDto>($"/project/{id}");

    public Task<ProjectDto> OpenProjectAsync(string path) =>
        PostAsync<ProjectDto>("/projects/open", new { path });

    public Task<ProjectDto> MigrateProjectAsync(string id, string parentDir) =>
        PostAsync<ProjectDto>($"/project/{id}/migrate", new { parent_dir = parentDir });

    public Task<ProjectDto> RenameProjectAsync(string id, string title) =>
        PatchAsync<ProjectDto>($"/project/{id}", new { title });

    public Task<ProjectDto> UpdateWorkInfoAsync(string id, WorkInfoUpdateDto changes) =>
        PatchAsync<ProjectDto>($"/project/{id}/info", changes);

    public Task DeleteProjectAsync(string id) => DeleteAsync($"/project/{id}");

    public Task<ChapterDto> RenameChapterAsync(string id, int n, string title) =>
        PatchAsync<ChapterDto>($"/project/{id}/chapter/{n}", new { title });

    public Task<ChapterDto> ClearChapterAsync(string id, int n) =>
        PostAsync<ChapterDto>($"/project/{id}/chapter/{n}/clear", new { });

    public Task DeleteChapterAsync(string id, int n) => DeleteAsync($"/project/{id}/chapter/{n}");

    public Task<ProjectDto> SaveLoreAsync(string id, LoreDto lore, int? chapterNo) =>
        PutAsync<ProjectDto>(chapterNo is int n ? $"/project/{id}/lore?chapter={n}" : $"/project/{id}/lore", lore);

    public async Task<string> CopyPortraitAsync(string id, string characterId, string sourcePath) =>
        (await PostAsync<PortraitDto>($"/project/{id}/portrait",
            new { character_id = characterId, source_path = sourcePath })).Portrait;

    public Task<ProjectDto> PatchProviderAsync(string id, string provider, bool? offline = null)
    {
        object body = offline is null
            ? new { provider }
            : new { provider, offline_mode = offline };
        return PatchAsync<ProjectDto>($"/project/{id}/settings", body);
    }

    public async Task<List<ChapterDto>> ListChaptersAsync(string id) =>
        await GetAsync<List<ChapterDto>>($"/project/{id}/chapters") ?? new();

    public Task<ChapterDto> GetChapterAsync(string id, int no) => GetAsync<ChapterDto>($"/project/{id}/chapter/{no}");

    public Task<ChapterDto> PlanAsync(string id, string notes = "") =>
        PostAsync<ChapterDto>($"/project/{id}/plan", new { user_notes = notes });

    /// <summary>生成当前章正文；已有正文时后端以其为开头接着写。</summary>
    public Task<ChapterDto> GenerateAsync(string id, string memoryPolicy = "extract") =>
        PostAsync<ChapterDto>($"/project/{id}/chapter", new { prefix = "", confirm_plan = true, memory_policy = memoryPolicy });

    public Task<ChapterDto> CreateChapterAsync(string id, string text, string rich, ChapterPlanDto? plan = null) =>
        PostAsync<ChapterDto>($"/project/{id}/chapters", new { body_text = text, body_rich = rich, plan });

    public Task<ChapterDto> SavePlanAsync(string id, int n, ChapterPlanDto plan) =>
        PutAsync<ChapterDto>($"/project/{id}/chapter/{n}/plan", plan);

    /// <summary>规划区「AI 规划」：重新生成第 n 章计划表单，保留自由规划。</summary>
    public Task<ChapterDto> ReplanAsync(string id, int n) =>
        PostAsync<ChapterDto>($"/project/{id}/chapter/{n}/replan", new { });

    public Task<ChapterDto> SaveReviewAsync(string id, int n, string notes) =>
        PutAsync<ChapterDto>($"/project/{id}/chapter/{n}/review", new { user_annotations = notes });

    public Task<ChapterDto> InheritLoreAsync(string id, int n, int from) =>
        PostAsync<ChapterDto>($"/project/{id}/chapter/{n}/lore/inherit?from={from}", new { });

    public Task<ChapterDto> FinalizeAsync(string id, int n) =>
        PostAsync<ChapterDto>($"/project/{id}/chapter/{n}/finalize", new { });

    public Task<ChapterDto> ExtractAsync(string id, int n) =>
        PostAsync<ChapterDto>($"/project/{id}/chapter/{n}/extract", new { });

    /// <summary>按「字段路径」改作品级设定。referenceSamples 为 null 表示不改范文，空列表表示清空。</summary>
    public Task<ProjectDto> BibleEditAsync(string id, IEnumerable<BibleEditDto> edits, IReadOnlyList<string>? referenceSamples = null) =>
        PostAsync<ProjectDto>($"/project/{id}/bible", new
        {
            edits = edits.ToList(),
            preview_only = false,
            reference_samples = referenceSamples,
        });

    /// <summary>独立语义审计（强模型，显式付费）；semantic=true。</summary>
    public Task<ChapterDto> AuditAsync(string id, int n, bool semantic = true) =>
        PostAsync<ChapterDto>($"/project/{id}/audit/{n}?semantic={(semantic ? "true" : "false")}", new { });

    /// <summary>「本章校验」：核对计划里的 must_include / must_not / required_threads 有没有被正文照做。
    /// 结果落在 chapter.plan_check，不碰审计报告。</summary>
    public Task<ChapterDto> PlanCheckAsync(string id, int n, bool semantic = true) =>
        PostAsync<ChapterDto>($"/project/{id}/chapter/{n}/plan-check?semantic={(semantic ? "true" : "false")}", new { });

    public Task<ChapterDto> SaveBodyAsync(string id, int n, string text, string rich) =>
        PutAsync<ChapterDto>($"/project/{id}/chapter/{n}/body", new { body_text = text, body_rich = rich });

    public Task<ProjectDto> RefineAsync(string id, int n, string notes) =>
        PostAsync<ProjectDto>($"/project/{id}/chapter/{n}/refine", new
        {
            chapter_no = n,
            user_annotations = notes,
            thread_updates = Array.Empty<object>(),
            bible_edits = Array.Empty<object>(),
            constraints_for_next = Array.Empty<object>(),
        });

    public Task RollbackAsync(string id, int n) =>
        PostAsync($"/project/{id}/rollback/{n}", new { });

    public Task<ChapterDto> InsertChapterAfterAsync(string id, int n) =>
        PostAsync<ChapterDto>($"/project/{id}/chapter/{n}/insert-after", new { });

    public Task<ChapterDto> UnfinalizeAsync(string id, int n) =>
        PostAsync<ChapterDto>($"/project/{id}/chapter/{n}/unfinalize", new { });

    public async Task<string> ChapterFileAsync(string id, int n) =>
        (await GetAsync<ExportDto>($"/project/{id}/chapter/{n}/file"))?.Path ?? "";

    public Task<List<ChapterDto>> ImportChaptersAsync(string id, IEnumerable<(string Title, string Body)> chapters) =>
        PostAsync<List<ChapterDto>>($"/project/{id}/import",
            new { chapters = chapters.Select(c => new { title = c.Title, body_text = c.Body }).ToArray() });

    public async Task<string> BackupAsync(string id) =>
        (await PostAsync<ExportDto>($"/project/{id}/backup", new { })).Path;

    /// <summary>保存共享人物资料；可选携带 chapter_no / chapter_action 把本章行动写进该章。
    /// 省略 action 表示不改动，显式空串表示清空。</summary>
    public Task<CharacterDto> SaveCharacterAsync(string projectId, CharacterSaveRequestDto c) =>
        PostAsync<CharacterDto>($"/project/{projectId}/characters", c);

    /// <summary>旧入口，只写作品主档；web 端与人物卡都已改用 <see cref="SaveCharacterAsync"/>。</summary>
    public Task<CharacterDto> UpsertCharacterAsync(string projectId, CharacterDto c) =>
        PostAsync<CharacterDto>($"/project/{projectId}/characters", c);

    /// <summary>删除人物；chapter_no 有值时同时清掉该章名单里的这条记录。</summary>
    public Task DeleteCharacterAsync(string projectId, string charId, int? chapterNo = null) =>
        DeleteAsync(chapterNo is int n
            ? $"/project/{projectId}/characters/{charId}?chapter_no={n}"
            : $"/project/{projectId}/characters/{charId}");

    /// <summary>导出。font 传写作面当前主题的正文字体，后端会写进 EPUB 的样式表。</summary>
    public Task<ExportDto> ExportAsync(string id, string fmt, string? font = null) =>
        GetAsync<ExportDto>($"/project/{id}/export?fmt={fmt}"
            + (string.IsNullOrWhiteSpace(font) ? "" : $"&font={Uri.EscapeDataString(font)}"));

    /// <summary>平台表（投稿入口 + 要求速查）；平台清单只在后端维护，客户端不硬编码。</summary>
    public async Task<PublishPlatformsDto> PublishPlatformsAsync() =>
        await GetAsync<PublishPlatformsDto>("/publish/platforms") ?? new PublishPlatformsDto();

    /// <summary>生成发布包到「导出/发布/&lt;平台&gt;/」，同时返回剪贴板负载。写文件是后端的活。</summary>
    public Task<PublishResultDto> PreparePublishAsync(string id, PublishRequestDto req) =>
        PostAsync<PublishResultDto>($"/project/{id}/publish/prepare", req);

    public Task<List<SkillDto>> ListSkillsAsync() => GetAsync<List<SkillDto>>("/skills");

    public Task<SkillDto> CreateSkillAsync(SkillDto s) =>
        PostAsync<SkillDto>("/skills", new { s.Name, s.Description, s.Applies_To, s.Scenes, s.Instructions, s.Source });

    public Task<SkillDto> UpdateSkillAsync(SkillDto s) =>
        PutAsync<SkillDto>($"/skills/{Uri.EscapeDataString(s.Id)}", new { s.Name, s.Description, s.Applies_To, s.Scenes, s.Instructions, s.Source });

    public Task<SkillDto> ResetSkillAsync(string id) =>
        PostAsync<SkillDto>($"/skills/{Uri.EscapeDataString(id)}/reset", new { });

    public Task<SkillDto> SetSkillEnabledAsync(string id, bool enabled) =>
        PatchAsync<SkillDto>($"/skills/{Uri.EscapeDataString(id)}", new { enabled });

    public Task DeleteSkillAsync(string id) => DeleteAsync($"/skills/{Uri.EscapeDataString(id)}");

    /// <summary>path 为 .md 文件，或含 SKILL.md 的文件夹。</summary>
    public Task<SkillDto> ImportSkillAsync(string path) => PostAsync<SkillDto>("/skills/import", new { path });

    public Task<SkillSourcesDto> GetSkillSourcesAsync() => GetAsync<SkillSourcesDto>("/skills/sources");

    public Task<GithubTokenDto> SetGithubTokenAsync(string token) =>
        PutAsync<GithubTokenDto>("/skills/github-token", new { token });

    public Task<GithubSearchDto> SearchGithubSkillsAsync(string query, int page = 1) =>
        GetAsync<GithubSearchDto>($"/skills/github/search?q={Uri.EscapeDataString(query)}&page={page}");

    public Task<GithubRepoDto> ListGithubRepoSkillsAsync(string repo) =>
        GetAsync<GithubRepoDto>($"/skills/github/repo?repo={Uri.EscapeDataString(repo)}");

    public Task<SkillPreviewDto> PreviewSkillUrlAsync(string url) =>
        PostAsync<SkillPreviewDto>("/skills/preview-url", new { url });

    public Task<SkillDto> ImportSkillUrlAsync(string url) => PostAsync<SkillDto>("/skills/import-url", new { url });

    public Task<SkillDraftDto> DistillSkillAsync(string url, string text, string hint) =>
        PostAsync<SkillDraftDto>("/skills/distill", new { url, text, hint });

    public Task<List<PluginDto>> ListPluginsAsync() => GetAsync<List<PluginDto>>("/plugins");

    public Task<PluginDto> ImportPluginAsync(string path) => PostAsync<PluginDto>("/plugins/import", new { path });

    public Task<PluginDto> SetPluginEnabledAsync(string id, bool enabled) =>
        PatchAsync<PluginDto>($"/plugins/{Uri.EscapeDataString(id)}", new { enabled });

    public Task DeletePluginAsync(string id) => DeleteAsync($"/plugins/{Uri.EscapeDataString(id)}");

    // ---------------------------------------------------------------- 笔记与提炼
    // 路由见 docs/ROUND_NOTES_AI_PLAN.md §2.1。

    public async Task<List<NoteDto>> ListNotesAsync(string id) =>
        (await GetAsync<NoteListDto>($"/project/{id}/notes"))?.Notes ?? new();

    public Task<NoteDto> CreateNoteAsync(string id, int chapterNo, string title, string body, string folder = "default") =>
        PostAsync<NoteDto>($"/project/{id}/notes", new { chapter_no = chapterNo, title, body, folder });

    public async Task<List<NoteDto>> PinNoteAsync(string id, string noteId, bool pinned) =>
        (await PatchAsync<NoteListDto>($"/project/{id}/notes/{Uri.EscapeDataString(noteId)}/pin", new { pinned })).Notes;
    public async Task<List<NoteDto>> MoveNoteAsync(string id, string noteId, string direction, string? operationId = null) =>
        (await PostAsync<NoteListDto>($"/project/{id}/notes/{Uri.EscapeDataString(noteId)}/move", new { direction, operation_id = operationId })).Notes;
    public Task<NoteDto> CopyNoteAsync(string id, string noteId, string title, string? operationId = null) =>
        PostAsync<NoteDto>($"/project/{id}/notes/{Uri.EscapeDataString(noteId)}/copy", new { title, operation_id = operationId });

    /// <summary>version 与后端 current 不一致时后端返回 409 NOTE_VERSION。</summary>
    public Task<NoteDto> UpdateNoteAsync(string id, string noteId, string title, string body, int contentVersion, string? folder = null) =>
        PutAsync<NoteDto>($"/project/{id}/notes/{Uri.EscapeDataString(noteId)}",
            new { title, body, content_version = contentVersion, folder });

    public async Task<List<NoteFolderDto>> ListNoteFoldersAsync(string id) =>
        (await GetAsync<NoteFolderListDto>($"/project/{id}/notes/folders"))?.Folders ?? new();

    public Task<NoteFolderDto> SaveNoteFolderAsync(string id, string title, string? folderId = null) =>
        folderId is null
            ? PostAsync<NoteFolderDto>($"/project/{id}/notes/folders", new { title })
            : PutAsync<NoteFolderDto>($"/project/{id}/notes/folders/{Uri.EscapeDataString(folderId)}", new { title });

    public Task DeleteNoteAsync(string id, string noteId) =>
        DeleteAsync($"/project/{id}/notes/{Uri.EscapeDataString(noteId)}");

    public Task<NoteDistillResultDto> DistillNoteAsync(string id, string noteId, int contentVersion, int chapterNo) =>
        PostAsync<NoteDistillResultDto>($"/project/{id}/notes/{Uri.EscapeDataString(noteId)}/distill",
            new { content_version = contentVersion, chapter_no = chapterNo });

    public Task<NoteDto> SaveNoteReferenceAsync(string id, string noteId, string reference) =>
        PutAsync<NoteDto>($"/project/{id}/notes/{Uri.EscapeDataString(noteId)}/reference", new { reference });

    public Task<ChapterReferenceDto> GetChapterReferenceAsync(string id, int chapterNo) =>
        GetAsync<ChapterReferenceDto>($"/project/{id}/notes/reference?chapter_no={chapterNo}");

    // ---------------------------------------------------------------- 历史版本
    // 路由见 §2.2。

    public async Task<List<VersionDto>> ListVersionsAsync(string id, int no) =>
        (await GetAsync<VersionListDto>($"/project/{id}/chapter/{no}/versions"))?.Versions ?? new();

    public Task<VersionDetailDto> GetVersionAsync(string id, int no, string versionId) =>
        GetAsync<VersionDetailDto>($"/project/{id}/chapter/{no}/versions/{Uri.EscapeDataString(versionId)}");

    public Task<ChapterDto> RestoreVersionAsync(string id, int no, string versionId) =>
        PostAsync<ChapterDto>($"/project/{id}/chapter/{no}/versions/{Uri.EscapeDataString(versionId)}/restore",
            new { });

    // ---------------------------------------------------------------- 全书查找
    // 路由见 §2.3。

    public Task<SearchResponseDto> SearchAsync(string id, SearchRequestDto request) =>
        PostAsync<SearchResponseDto>($"/project/{id}/search", request);

    private async Task<T> GetAsync<T>(string path)
    {
        var resp = await _http.GetAsync(path);
        return await ReadAsync<T>(resp);
    }

    private async Task PostAsync(string path, object body)
    {
        var resp = await _http.PostAsJsonAsync(path, body, JsonOpts);
        await EnsureOkAsync(resp);
    }

    private async Task<T> PostAsync<T>(string path, object body)
    {
        var resp = await _http.PostAsJsonAsync(path, body, JsonOpts);
        return await ReadAsync<T>(resp);
    }

    private Task<T> PatchAsync<T>(string path, object body) => SendAsync<T>(HttpMethod.Patch, path, body);

    private Task<T> PutAsync<T>(string path, object body) => SendAsync<T>(HttpMethod.Put, path, body);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object body)
    {
        var json = JsonSerializer.Serialize(body, body.GetType(), JsonOpts);
        var req = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        var resp = await _http.SendAsync(req);
        return await ReadAsync<T>(resp);
    }

    private async Task DeleteAsync(string path)
    {
        var resp = await _http.DeleteAsync(path);
        await EnsureOkAsync(resp);
    }

    private static async Task EnsureOkAsync(HttpResponseMessage resp)
    {
        if (resp.IsSuccessStatusCode) return;
        var text = await resp.Content.ReadAsStringAsync();
        throw ParseError(text, resp.StatusCode.ToString());
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage resp)
    {
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw ParseError(text, resp.StatusCode.ToString());
        return JsonSerializer.Deserialize<T>(text, JsonOpts)
               ?? throw new InvalidOperationException(L.T("Api.EmptyResponse"));
    }

    private static string ExtractMessage(string text, string fallback)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("detail", out var detail))
            {
                if (detail.ValueKind == JsonValueKind.String)
                    return detail.GetString() ?? fallback;
                if (detail.TryGetProperty("message", out var msg))
                    return msg.GetString() ?? fallback;
            }
        }
        catch { /* ignore */ }
        return string.IsNullOrWhiteSpace(text) ? fallback : text;
    }

    internal static InvalidOperationException ParseError(string text, string fallback)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var detail = doc.RootElement.GetProperty("detail");
            if (detail.ValueKind == JsonValueKind.Object && detail.TryGetProperty("code", out var proposalCode))
            {
                if (detail.TryGetProperty("issues", out var issues) && issues.GetArrayLength() > 0 &&
                    issues[0].TryGetProperty("left", out _))
                    return new ProposalException(proposalCode.GetString()!, ExtractMessage(text, fallback),
                        JsonSerializer.Deserialize<List<ProposalIssueDto>>(issues.GetRawText(), JsonOpts));
                if (proposalCode.GetString() is { } c && (c.StartsWith("PROPOSAL_") || c.StartsWith("REQUEST_") ||
                    c.StartsWith("PLAN_HISTORY_") || c.StartsWith("REVISION_") || c is "CHAPTER_CHANGED" or "UNKNOWN_QUESTION"))
                    return new ProposalException(c, ExtractMessage(text, fallback));
            }
            if (detail.ValueKind == JsonValueKind.Object && detail.TryGetProperty("code", out var code) &&
                code.GetString() == "PLAN_RHYTHM_MISMATCH")
                return new RhythmMismatchException(ExtractMessage(text, fallback),
                    detail.GetProperty("project_id").GetString()!, detail.GetProperty("chapter_no").GetInt32(),
                    JsonSerializer.Deserialize<List<RhythmIssueDto>>(detail.GetProperty("issues").GetRawText(), JsonOpts) ?? new());
        }
        catch (JsonException) { }
        catch (InvalidOperationException) { }
        catch (KeyNotFoundException) { }
        return new InvalidOperationException(ExtractMessage(text, fallback));
    }

    /// <summary>每个请求都带上当前界面语言，后端据此翻译错误提示。</summary>
    private sealed class LanguageHeaderHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            request.Headers.AcceptLanguage.Clear();
            request.Headers.AcceptLanguage.ParseAdd(LanguageManager.Current);
            return base.SendAsync(request, ct);
        }
    }
}
