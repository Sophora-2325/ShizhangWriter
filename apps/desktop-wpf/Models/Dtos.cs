using System.Text.Json;
using NovelGm.Desktop.Api;

namespace NovelGm.Desktop.Models;

public sealed class ProjectDto
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Phase { get; set; } = "";
    public int Current_Chapter { get; set; }
    public List<CharacterDto> Characters { get; set; } = new();
    public List<PlotThreadDto> Threads { get; set; } = new();
    public List<LoreEntryDto> Entries { get; set; } = new();
    public List<string> Forbidden { get; set; } = new();
    public List<string> Genre { get; set; } = new();
    public string Tone { get; set; } = "";
    public string Audience { get; set; } = "";
    public CoverAssetDto Cover { get; set; } = new();
    public bool Npc_Auto_Act { get; set; }
    public StyleProfileDto? Style { get; set; }
    public bool Offline_Mode { get; set; }
    public string Provider { get; set; } = "inherit";
    public double Estimated_Cost_Usd { get; set; }
    public string Root_Path { get; set; } = "";
    public bool Root_Missing { get; set; }
}

/// <summary>作品内的封面原图与预览引用。</summary>
public sealed class CoverAssetDto
{
    public string Preview { get; set; } = "";
    public string Original { get; set; } = "";
}

public sealed class WorkInfoUpdateDto
{
    public List<string>? Genre { get; set; }
    public string? Tone { get; set; }
    public string? Audience { get; set; }
    public CoverAssetDto? Cover { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasChanges => Genre is not null || Tone is not null || Audience is not null || Cover is not null;
}

/// <summary>作品文风画像；后端会从编辑字段重新编译生成约束。</summary>
public sealed class StyleProfileDto
{
    public string Pov { get; set; } = "";
    public string Tense { get; set; } = "";
    public string Sentence_Length { get; set; } = "";
    public List<string> Diction { get; set; } = new();
    public List<string> Banned_Words { get; set; } = new();
    public Dictionary<string, string> Preferred_Replacements { get; set; } = new();
    public List<string> Reference_Samples { get; set; } = new();
    public string Compiled_System_Constraints { get; set; } = "";
    public Dictionary<string, JsonElement> Punctuation_Policy { get; set; } = new();
    public Dictionary<string, double> Metrics { get; set; } = new();
}

/// <summary>一条 bible 路径编辑：path 形如 style.pov、forbidden、characters.0.status。</summary>
public sealed class BibleEditDto
{
    public string Path { get; set; } = "";
    public string Old { get; set; } = "";
    public string New { get; set; } = "";
    public string Reason { get; set; } = "";
}

public sealed class LoreDto
{
    public List<LoreEntryDto> Entries { get; set; } = new();
    public List<CharacterDto> Characters { get; set; } = new();
    public List<PlotThreadDto> Threads { get; set; } = new();

    public static LoreDto From(ProjectDto p) => new()
    {
        Entries = p.Entries.ToList(),
        Characters = p.Characters.ToList(),
        Threads = p.Threads.ToList(),
    };

    public LoreDto Copy() => new()
    {
        Entries = Entries.ToList(),
        Characters = Characters.ToList(),
        Threads = Threads.ToList(),
    };
}

/// <summary>一条背景设定；<see cref="Hard"/> 只对 Category == "rule" 有意义。</summary>
public sealed class LoreEntryDto
{
    public string Id { get; set; } = "";
    public string Category { get; set; } = "world";
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public bool Hard { get; set; }

    public LoreEntryDto Copy() => new()
    {
        Id = Id,
        Category = Category,
        Title = Title,
        Content = Content,
        Hard = Hard,
    };

    /// <summary>卡片标题：没写标题时用正文前 24 字兜底。</summary>
    public string Display => string.IsNullOrWhiteSpace(Title)
        ? (Content.Length > 24 ? Content[..24] : Content)
        : Title;
}

/// <summary>背景设定的分类，与后端 models.LORE_CATEGORIES 一致；显示名是字符串资源 Lore.Category.&lt;key&gt;。</summary>
public static class LoreCategories
{
    public static readonly string[] All = { "world", "geo", "faction", "rule", "history", "item", "other" };
}

public class CharacterDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Age { get; set; } = "";
    /// <summary>性别：自由文本，不做枚举。</summary>
    public string Gender { get; set; } = "";
    public string Personality { get; set; } = "";
    public string Appearance { get; set; } = "";
    public string Outfit { get; set; } = "";
    /// <summary>爱好：与性格等一起属于人物公共资料。</summary>
    public string Hobbies { get; set; } = "";
    /// <summary>所属组织：杂项里的公共资料。</summary>
    public string Organization { get; set; } = "";
    /// <summary>思维习惯：杂项里的公共资料。</summary>
    public string Thinking_Habits { get; set; } = "";
    public string Recent_Thoughts { get; set; } = "";
    public string Role { get; set; } = "";
    public string Join_Reason { get; set; } = "";
    public string Voice { get; set; } = "";
    public string Secret { get; set; } = "";
    /// <summary>公开身份：L1 里会随提示词发出。</summary>
    public string Public_Face { get; set; } = "";
    /// <summary>知悉事实：当前只存盘，提示词与审计都不读。</summary>
    public List<string> Known_Facts { get; set; } = new();
    /// <summary>状态（伤势/位置/dead 等）：进 L1，审计会读 status["dead"] 判「已死角色仍有言行」。</summary>
    public Dictionary<string, string> Status { get; set; } = new();
    public string Notes { get; set; } = "";
    public string Portrait { get; set; } = "";
    public List<string> Goals { get; set; } = new();
    public List<RelationDto> Relations { get; set; } = new();
}

public sealed class RelationDto
{
    public string Target_Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string Label { get; set; } = "";
    public double Intensity { get; set; } = 0.5;
}

/// <summary>保存人物卡的请求：人物全字段 + 可选的本章行动绑定。
/// chapter_no 为 null 时两个章节字段都会被省略；chapter_action 显式空串表示清空该章行动。</summary>
public sealed class CharacterSaveRequestDto : CharacterDto
{
    public int? Chapter_No { get; set; }
    public string? Chapter_Action { get; set; }
}

/// <summary>人物卡保存结果：失败时窗口保持打开并显示错误，错误文本可原样重试。</summary>
public sealed record CharacterSaveResult(bool Ok, string Error)
{
    public static CharacterSaveResult Success() => new(true, "");
    public static CharacterSaveResult Fail(string error) => new(false, error ?? "");
}

public sealed class PlotThreadDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string State { get; set; } = "";
    public string User_Directive { get; set; } = "";
    public string Next_Milestone { get; set; } = "";
    public int Priority { get; set; } = 1;
    public string Time_Position { get; set; } = "present";
    public string Tone { get; set; } = "";
    public string Misc { get; set; } = "";
    public List<ThreadMilestoneDto> Milestones { get; set; } = new();
}

public sealed class ThreadMilestoneDto
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Completed { get; set; }
    public ThreadMilestoneDto Copy() => new() { Id = Id, Name = Name, Description = Description, Completed = Completed };
}

public sealed class ChapterPlanDto
{
    public string Scene_Goal { get; set; } = "";
    public string Conflict { get; set; } = "";
    public string Stakes { get; set; } = "";
    public List<string> Present_Character_Ids { get; set; } = new();
    public List<string> Required_Threads { get; set; } = new();
    public List<string> Must_Include { get; set; } = new();
    public List<string> Must_Not { get; set; } = new();
    public string? Open_Decision_For_User { get; set; }
    public int Target_Word_Count { get; set; } = 3000;
    public string Free_Text { get; set; } = "";
    public string Misc { get; set; } = "";
    /// <summary>本章戏份，取值见 <see cref="SceneTypes.All"/>。</summary>
    public List<string> Scene_Types { get; set; } = new();

    public ChapterPlanDto Copy() => new()
    {
        Scene_Goal = Scene_Goal,
        Conflict = Conflict,
        Stakes = Stakes,
        Present_Character_Ids = Present_Character_Ids.ToList(),
        Required_Threads = Required_Threads.ToList(),
        Must_Include = Must_Include.ToList(),
        Must_Not = Must_Not.ToList(),
        Open_Decision_For_User = Open_Decision_For_User,
        Target_Word_Count = Target_Word_Count,
        Free_Text = Free_Text,
        Misc = Misc,
        Scene_Types = Scene_Types.ToList(),
    };
}

/// <summary>戏份类型，与后端 skills.SCENES 一致；显示名是字符串资源 Scene.&lt;key&gt;。</summary>
public static class SceneTypes
{
    public static readonly string[] All = { "action", "romance", "suspense", "daily", "sorrow", "confrontation" };
}

public sealed class ReviewDraftDto
{
    public string User_Annotations { get; set; } = "";
}

public sealed class ChapterSummaryDto
{
    public string One_Line { get; set; } = "";
    public List<BeatDto> Beats { get; set; } = new();
    public List<string> Continuity_Flags { get; set; } = new();
}

public sealed class BeatDto
{
    public int Order { get; set; }
    public string What_Happened { get; set; } = "";
    public string Evidence { get; set; } = "";
}

public sealed class ConsistencyFlagDto
{
    public string Kind { get; set; } = "";
    public string Severity { get; set; } = "warn";
    public string Point { get; set; } = "";
    public string Evidence { get; set; } = "";
    public string Suggestion { get; set; } = "";
}

public sealed class ConsistencyReportDto
{
    public List<ConsistencyFlagDto> Flags { get; set; } = new();
    public bool Passed { get; set; } = true;
    /// <summary>「语义那一趟」是否真的跑过（强模型）。旧数据为 false——用来区分「查过且干净」与「只跑过规则检查」。</summary>
    public bool Semantic_Checked { get; set; }
}

public sealed class ChapterDto
{
    public int No { get; set; }
    public string Chapter_Uid { get; set; } = "";
    public ProposalWorkspaceDto Proposal { get; set; } = new();
    public string Title { get; set; } = "";
    public string Status { get; set; } = "";
    public string Body_Text { get; set; } = "";
    public string Body_Rich { get; set; } = "";
    public int Word_Count { get; set; }
    public ChapterPlanDto? Plan { get; set; }
    public StackedConstraintsDto Stacked_Constraints { get; set; } = new();
    public ChapterSummaryDto? Summary { get; set; }
    public ConsistencyReportDto? Consistency_Report { get; set; }
    /// <summary>「本章校验」的结果：计划里的约束有没有被正文照做。与审计报告分开存，互不覆盖。</summary>
    public ConsistencyReportDto? Plan_Check { get; set; }
    public bool Stacked_Weight_Migration_Pending { get; set; }
    public LoreDto? Lore { get; set; }
    public int? Lore_From { get; set; }
    /// <summary>本章行动：人物 id → 待执行意图。只作用于本章，不参与继承。</summary>
    public Dictionary<string, string> Character_Actions { get; set; } = new();
    public ReviewDraftDto? Review { get; set; }
    public string Body_Hash { get; set; } = "";
    public string Summary_Status { get; set; } = "pending";
    public string Summary_Error { get; set; } = "";
    public Dictionary<string, System.Text.Json.JsonElement> Stale_Results { get; set; } = new();
    public List<int> Upstream_Changed { get; set; } = new();
    public List<int> Plan_Upstream_Changed { get; set; } = new();

    public string Display
    {
        get
        {
            var key = "ChapterStatus." + Status;
            var status = L.T(key);
            return L.T("Chapter.Display", No, Title, status == key ? Status : status);
        }
    }
}

public sealed class SettingsDto
{
    public bool Has_Deepseek_Key { get; set; }
    public string Key_Hint { get; set; } = "";
    public string Provider { get; set; } = "deepseek";
    public bool Offline_Mode { get; set; }
    public string Local_Base_Url { get; set; } = "";
    public string Local_Model { get; set; } = "";
    public string Gate_Mode { get; set; } = "behavior";
    public string Last_Parent_Dir { get; set; } = "";
}

public sealed class PriceDto
{
    public double Input_Miss { get; set; }
    public double Input_Cache_Hit { get; set; }
    public double Output { get; set; }
}

/// <summary>「添加模型」时可选的预设厂商，带默认地址和模型名。</summary>
public sealed class ProviderPresetDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    /// <summary>intl / cn / local</summary>
    public string Group { get; set; } = "";
    public bool Needs_Key { get; set; }
    public string Key_Url { get; set; } = "";
    public List<string> Env_Vars { get; set; } = new();
    public string Base_Url { get; set; } = "";
    public bool Base_Url_Required { get; set; }
    public string Model_Strong { get; set; } = "";
    public string Model_Fast { get; set; } = "";
    public bool Supports_Thinking { get; set; }

    public string DisplayName
    {
        get
        {
            var key = "Provider." + Id;
            var name = L.T(key);
            return name == key ? Name : name;
        }
    }
}

/// <summary>用户添加的一条模型配置；同一预设可以有多条，Id 形如 openai、openai-2。</summary>
public sealed class ProviderDto
{
    public string Id { get; set; } = "";
    public string Preset { get; set; } = "";
    public string Preset_Name { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>openai / anthropic / gemini / compat / local / custom</summary>
    public string Kind { get; set; } = "";
    /// <summary>intl / cn / local</summary>
    public string Group { get; set; } = "";
    public bool Cloud { get; set; }
    public bool Needs_Key { get; set; }
    public bool Has_Key { get; set; }
    public string Key_Hint { get; set; } = "";
    public string Key_Url { get; set; } = "";
    public List<string> Env_Vars { get; set; } = new();
    public string Base_Url { get; set; } = "";
    public string Default_Base_Url { get; set; } = "";
    public string Model_Strong { get; set; } = "";
    public string Model_Fast { get; set; } = "";
    public string Default_Model_Strong { get; set; } = "";
    public string Default_Model_Fast { get; set; } = "";
    public bool Supports_Thinking { get; set; }
    public bool Thinking { get; set; }
    public PriceDto? Custom_Price { get; set; }
    public PriceDto? Price { get; set; }
    public bool Price_Known { get; set; }
    public bool Configured { get; set; }

    /// <summary>名称没改过（仍是预设的中文默认名）时按界面语言显示预设名。</summary>
    public string DisplayName => Name == Preset_Name ? PresetDisplayName : Name;

    public string PresetDisplayName
    {
        get
        {
            var key = "Provider." + Preset;
            var name = L.T(key);
            return name == key ? Preset_Name : name;
        }
    }
}

public sealed class ProviderModelsDto
{
    public List<string> Models { get; set; } = new();
}

public sealed class ProviderTestDto
{
    public bool Ok { get; set; }
    public string Model { get; set; } = "";
    public string Reply { get; set; } = "";
    public double Latency_Ms { get; set; }
}

public sealed class SkillDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>plan / generate / both</summary>
    public string Applies_To { get; set; } = "both";
    public List<string> Scenes { get; set; } = new();
    public string Instructions { get; set; } = "";
    /// <summary>出处：网址或书名，多个用 ; 分隔</summary>
    public string Source { get; set; } = "";
    public bool Enabled { get; set; }
    public bool Builtin { get; set; }
    public bool Update_Available { get; set; }
}

public sealed class SkillSourceDto
{
    public string Name { get; set; } = "";
    /// <summary>github / article</summary>
    public string Kind { get; set; } = "";
    public string Url { get; set; } = "";
    /// <summary>zh / en / ja → 简介</summary>
    public Dictionary<string, string> Desc { get; set; } = new();
}

public sealed class SkillSourcesDto
{
    public bool Has_Github_Token { get; set; }
    public List<SkillSourceDto> Items { get; set; } = new();
}

public sealed class GithubTokenDto
{
    public bool Has_Github_Token { get; set; }
}

public sealed class GithubItemDto
{
    public string Repo { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>按文件搜索时是 SKILL.md 的路径；按仓库搜索时为空</summary>
    public string Path { get; set; } = "";
    public string Url { get; set; } = "";
    public int? Stars { get; set; }
}

public sealed class GithubSearchDto
{
    /// <summary>code（按 SKILL.md 文件）/ repo（按仓库）</summary>
    public string Mode { get; set; } = "";
    public int Total { get; set; }
    public List<GithubItemDto> Items { get; set; } = new();
}

public sealed class GithubFileDto
{
    public string Path { get; set; } = "";
    public string Url { get; set; } = "";
}

public sealed class GithubRepoDto
{
    public string Repo { get; set; } = "";
    public string Branch { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Only_Markdown { get; set; }
    public List<GithubFileDto> Files { get; set; } = new();
}

public sealed class SkillPreviewDto
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int Chars { get; set; }
    public bool Too_Long { get; set; }
    public string Text { get; set; } = "";
    public string Url { get; set; } = "";
}

/// <summary>AI 提炼出的草稿，尚未保存</summary>
public sealed class SkillDraftDto
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Applies_To { get; set; } = "both";
    public List<string> Scenes { get; set; } = new();
    public string Instructions { get; set; } = "";
    public string Source { get; set; } = "";
    public string Title { get; set; } = "";
    public bool Truncated { get; set; }

    public SkillDto ToSkill() => new()
    {
        Name = Name,
        Description = Description,
        Applies_To = Applies_To,
        Scenes = Scenes,
        Instructions = Instructions,
        Source = Source,
    };
}

public sealed class PluginDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Description { get; set; } = "";
    public string Author { get; set; } = "";
    public List<string> Hooks { get; set; } = new();
    public bool Enabled { get; set; }
}

public sealed class PortraitDto
{
    public string Portrait { get; set; } = "";
}

public sealed class HealthDto
{
    public string Status { get; set; } = "";
    public HealthPathsDto Paths { get; set; } = new();
}

public sealed class HealthPathsDto
{
    public string Storage { get; set; } = "";
}

public sealed class SttStatusDto
{
    public bool Available { get; set; }
    public string? Model_Dir { get; set; }
    public string Message { get; set; } = "";
}

public sealed class SttResultDto
{
    public string Text { get; set; } = "";
    public string Lang { get; set; } = "zh";
}

public sealed class ExportDto
{
    public string Path { get; set; } = "";
}

// ---------------------------------------------------------------- 发布到其他平台
// 平台表由后端下发（GET /publish/platforms），客户端不硬编码任何平台。

public sealed class PublishGroupDto
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Hint { get; set; } = "";
}

public sealed class PublishPlatformDto
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Group { get; set; } = "";
    public string Backend_Url { get; set; } = "";
    public string Homepage { get; set; } = "";
    public string Format { get; set; } = "txt";
    public string Indent { get; set; } = "none";
    public int Min_Words { get; set; }
    public bool Batch_Import { get; set; }
    /// <summary>投稿入口地址没能确认过，界面上会加一句「地址待核对」。</summary>
    public bool Unverified { get; set; }
    public List<string> Notes { get; set; } = new();
}

public sealed class PublishPlatformsDto
{
    public List<PublishGroupDto> Groups { get; set; } = new();
    public List<PublishPlatformDto> Platforms { get; set; } = new();
}

public sealed class PublishItemDto
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Group { get; set; } = "";
    public string Format { get; set; } = "";
    public string Backend_Url { get; set; } = "";
    public string Homepage { get; set; } = "";
    public List<string> Notes { get; set; } = new();
    public bool Unverified { get; set; }
    public string Dir { get; set; } = "";
    public List<string> Files { get; set; } = new();
    public int Chars { get; set; }
    /// <summary>该章没有正文：文件照样生成，界面提示一句。</summary>
    public bool Empty { get; set; }
}

public sealed class PublishPreviewDto
{
    public string Text { get; set; } = "";
    public string Html { get; set; } = "";
    public int Chars { get; set; }
    /// <summary>正文长到剪贴板装不下：文件仍然完整，界面提示改用文件。</summary>
    public bool Truncated { get; set; }
}

public sealed class PublishResultDto
{
    public List<PublishItemDto> Items { get; set; } = new();
    public string Scope { get; set; } = "chapter";
    public int Chapter_No { get; set; }
    public int Chapters { get; set; }
    public int Chars { get; set; }
    public bool Root_Missing { get; set; }
    public string Out_Root { get; set; } = "";
    public PublishPreviewDto Preview { get; set; } = new();
}

public sealed class PublishRequestDto
{
    public List<string> Platforms { get; set; } = new();
    public string Scope { get; set; } = "chapter";
    public int Chapter_No { get; set; }
    public string Author { get; set; } = "";
    public string Synopsis { get; set; } = "";
    /// <summary>空串 = 用平台默认缩进。</summary>
    public string Indent { get; set; } = "";
    /// <summary>null = 用平台默认（书站不加抬头，社区平台加）。</summary>
    public bool? With_Head { get; set; }
}

// ---------------------------------------------------------------- 笔记与本章写作参考
// 存储契约见 docs/ROUND_NOTES_AI_PLAN.md §2.1。distill.status = none|running|ready|stale|failed。

public sealed class NoteListDto
{
    public List<NoteDto> Notes { get; set; } = new();
}

public sealed class NoteFolderListDto
{
    public List<NoteFolderDto> Folders { get; set; } = new();
}

public sealed class NoteFolderDto
{
    public string Id { get; set; } = "default";
    public string Title { get; set; } = "";
    public string DisplayTitle => !string.IsNullOrWhiteSpace(Title) ? Title : NovelGm.Desktop.Api.L.T("Notes.Folder." + Id);
}

public sealed class NoteDto
{
    public bool Pinned { get; set; }
    public int Sort_Order { get; set; }
    public string PinLabel => Pinned ? NovelGm.Desktop.Api.L.T("Notes.Pinned") : "";
    [System.Text.Json.Serialization.JsonIgnore] public bool CanMoveUp { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool CanMoveDown { get; set; }
    public string Id { get; set; } = "";
    public int Chapter_No { get; set; }
    public string Folder { get; set; } = "default";
    public string Title { get; set; } = "";
    /// <summary>作者原始笔记（纯文本），与提炼结果分开存。</summary>
    public string Body { get; set; } = "";
    public int Content_Version { get; set; }
    public string Created_At { get; set; } = "";
    public string Updated_At { get; set; } = "";
    public NoteDistillDto Distill { get; set; } = new();

    public NoteDto Copy() => new()
    {
        Pinned = Pinned,
        Sort_Order = Sort_Order,
        Id = Id,
        Chapter_No = Chapter_No,
        Folder = Folder,
        Title = Title,
        Body = Body,
        Content_Version = Content_Version,
        Created_At = Created_At,
        Updated_At = Updated_At,
        Distill = Distill?.Copy() ?? new NoteDistillDto(),
    };

    /// <summary>列表仅显示名称；历史未命名条目不以正文作为名称。</summary>
    public string DisplayTitle => !string.IsNullOrWhiteSpace(Title) ? Title : NovelGm.Desktop.Api.L.T("Notes.Untitled");

    public string Summary
    {
        get
        {
            var text = (Body ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return text.Length > 64 ? text[..64] + "…" : text;
        }
    }
}

public sealed class NoteDistillDto
{
    /// <summary>none / running / ready / stale / failed。</summary>
    public string Status { get; set; } = "none";
    public string Auto_Reference { get; set; } = "";
    /// <summary>作者手写的参考；非空时优先于自动结果。</summary>
    public string Reference { get; set; } = "";
    public int Source_Version { get; set; }
    public string Updated_At { get; set; } = "";
    public string Error { get; set; } = "";

    /// <summary>有效参考 = 作者参考非空 ? 作者参考 : 自动结果。</summary>
    public string Effective => !string.IsNullOrWhiteSpace(Reference) ? Reference : Auto_Reference ?? "";

    public NoteDistillDto Copy() => new()
    {
        Status = Status,
        Auto_Reference = Auto_Reference,
        Reference = Reference,
        Source_Version = Source_Version,
        Updated_At = Updated_At,
        Error = Error,
    };
}

/// <summary>POST /notes/{id}/distill 的返回。</summary>
public sealed class NoteDistillResultDto
{
    public string Note_Id { get; set; } = "";
    public string Status { get; set; } = "none";
    public string Auto_Reference { get; set; } = "";
    public int Content_Version { get; set; }
    public string Error { get; set; } = "";
}

public sealed class NoteRefItemDto
{
    public string Note_Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Status { get; set; } = "none";
    public string Reference { get; set; } = "";
}

public sealed class ChapterReferenceDto
{
    public int Chapter_No { get; set; }
    /// <summary>none / ready / stale / failed / running（取该章最差状态）。</summary>
    public string Status { get; set; } = "none";
    public string Reference { get; set; } = "";
    public string Error { get; set; } = "";
    public List<NoteRefItemDto> Notes { get; set; } = new();
}

// ---------------------------------------------------------------- 历史版本
// 契约见 §2.2。source = original|autosave|ai|finalize|restore-backup|manual|legacy-text。

public sealed class VersionListDto
{
    public List<VersionDto> Versions { get; set; } = new();
}

public class VersionDto
{
    public string Id { get; set; } = "";
    public int Chapter_No { get; set; }
    public string Title_At_Save { get; set; } = "";
    public string Saved_At { get; set; } = "";
    public string Source { get; set; } = "";
    public int Word_Count { get; set; }
    /// <summary>false = 只有纯文本（如 legacy-text），恢复后没有格式。</summary>
    public bool Format_Available { get; set; }
}

public sealed class VersionDetailDto : VersionDto
{
    public string Body_Text { get; set; } = "";
    public string Body_Rich { get; set; } = "";
}

// ---------------------------------------------------------------- 全书/本章查找
// 契约见 §2.3。纯文本子串查找，不做正则。

public sealed class SearchRequestDto
{
    public string Query { get; set; } = "";
    public bool Case_Sensitive { get; set; }
    /// <summary>book / chapter。</summary>
    public string Scope { get; set; } = "book";
    public int Chapter_No { get; set; }
    /// <summary>当前编辑章未保存正文；空串表示用库里的正文。</summary>
    public string Draft_Text { get; set; } = "";
    public int Page { get; set; } = 1;
    public int Page_Size { get; set; } = 50;
}

public sealed class SearchHitDto
{
    public int Offset { get; set; }
    public string Context { get; set; } = "";
    public int Context_Offset { get; set; }
    public bool In_Draft { get; set; }
}

public sealed class SearchChapterDto
{
    public int Chapter_No { get; set; }
    public string Title { get; set; } = "";
    public int Count { get; set; }
    public List<SearchHitDto> Matches { get; set; } = new();
}

public sealed class SearchResponseDto
{
    public int Total { get; set; }
    public int Page { get; set; }
    public int Page_Size { get; set; }
    public bool Truncated { get; set; }
    public List<SearchChapterDto> Chapters { get; set; } = new();
}
