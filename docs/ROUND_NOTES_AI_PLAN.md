# 本轮设计契约：左右侧栏 / 笔记与 AI 参考 / 查找检测 / 历史版本

> 2026-10-18 后续调整：用户已要求笔记独立于 AI 创作。本文关于章节笔记、自动提炼与写作参考的约定已由 [个人笔记与侧栏界面调整](PERSONAL_NOTES_UI.md) 替代，其余功能约定保留。

> 本文件是分工与接口的唯一依据。实现时可以补充细节，但**不要改动这里定义的接口名、字段名、状态值**；确需改动先通知 Lead。

## 0. 环境与协作约定

- 仓库根：`<工作区>\novel-gm`（下文相对此目录）。
- 当前会话的沙箱**不允许子进程写仓库**（`dotnet build` / `pytest` 都会 Access denied）。**所有成员都不要运行构建/测试命令**，只做读写源码 + 自查；由 Lead 统一运行并把报错发回。
- 只改自己名下的文件（见 §5）。需要别人文件改动时发消息给 Lead。
- 不要提交 git，不要动 `dist/`、`AppVersions/`、`storage/`、`.git/`。
- 文案：每个功能自己新建 `apps/desktop-wpf/Strings/<Feature>.<lang>.xaml`（`<lang>` = `zh-CN` / `en-US` / `ja-JP`），键名统一带自己的前缀；**不要改** `Strings/zh-CN.xaml` 等聚合文件（Lead/外壳负责合并）。
- 完成时用 `send_message` 汇报：改了哪些文件、实现范围、未做的点、需要 Lead 验证的点。

## 1. 目标摘要

1. 左栏选项列：目录 / 笔记 / 查找，共用左面板，点当前项收起、选项列常驻；右侧选项列：设定 / 功能 / AI / 审阅，AI 面板标题「AI全流程」。移除顶栏 AI 按钮与弹层、右侧隐藏/显示按钮、标题栏阶段文字。`AI辅助模式` 改名 `AI全流程`（配置值仍 `assist`）。
2. 功能区按「发布与版本 / 查找与检测 / 编辑与批注」分组；删掉 4 个粘贴入口、格式刷、特殊字符、打开/接受/拒绝修订。普通粘贴、右键菜单、快捷键保留。
3. 全部流程操作迁入 AI 面板：上一步 / 本章规划 / AI规划 / 生成·续写 / 定稿 / 一致性审计 / 计划校验 / 提取 / 本章评注 / 下一步；含「本章写作参考」折叠区。
4. 统一查找（本章 / 全书），敏感词检测（本地词库），历史版本（列表 / 预览 / 恢复）。
5. 每章多笔记 + 停笔 600ms 自动保存 + 保存后 10s 后台提炼 + AI 操作前确保最新参考。

## 2. 后端契约（FastAPI，`services/core`）

所有路由挂在 `/project/{project_id}` 下，与现有代码风格一致（`ws.conflict(message, code)` / `ws.bad_request`）。

### 2.1 笔记与提炼（新模块 `services/core/notes.py`）

存储：`<作品文件夹>/.novel-gm/notes.json`（隐藏目录，随作品备份/导入）。文件结构：

```json
{
  "version": 1,
  "notes": [
    {
      "id": "12位hex",
      "chapter_no": 3,
      "title": "本章意图",
      "body": "纯文本",
      "content_version": 7,
      "created_at": "ISO8601",
      "updated_at": "ISO8601",
      "distill": {
        "status": "none|running|ready|stale|failed",
        "auto_reference": "",
        "reference": "",
        "source_version": 0,
        "updated_at": "ISO8601",
        "error": ""
      }
    }
  ]
}
```

- `status` 语义：`none` 从未提炼；`ready` `source_version == content_version` 且有结果；`stale` 来源已变（旧结果保留）；`failed` 上次失败（旧结果保留，`error` 说明）；`running` 进行中。
- 原笔记 `body`、自动结果 `auto_reference`、作者编辑的 `reference` 三者分开存。有效参考 = `reference` 非空 ? `reference` : `auto_reference`。
- 写入一律原子写（复用 `workspace.atomic_text/write_json`）。文件夹缺失时 `ROOT_MISSING`。
- 文件不存在 / 字段缺失 / 损坏 → 视为空笔记列表、空参考，不抛错。

路由：

| 方法/路径 | 请求 | 返回 |
|---|---|---|
| `GET /notes` | — | `{"notes":[Note]}`（全部章节，按 chapter_no, created_at） |
| `POST /notes` | `{chapter_no:int, title:str="", body:str=""}` | `Note`（新建，content_version=1, distill.status="none"） |
| `PUT /notes/{note_id}` | `{title:str, body:str, content_version:int}` | `Note`；版本不符 → 409 `NOTE_VERSION`（`detail.code`） |
| `DELETE /notes/{note_id}` | — | `{"ok":true}`；同时删掉该笔记参考 |
| `POST /notes/{note_id}/distill` | `{content_version:int, chapter_no:int}` | `{"note_id":..., "status":..., "auto_reference":..., "content_version":..., "error":""}`；版本不符 → 409 `NOTE_VERSION` 且不改任何已有结果 |
| `PUT /notes/{note_id}/reference` | `{reference:str}` | `Note`（作者编辑参考；不改变 content_version、不动 auto_reference） |
| `GET /notes/reference?chapter_no=N` | — | `{"chapter_no":N,"status":"none|ready|stale|failed|running","reference":"合并后的有效参考","error":"","notes":[{"note_id","title","status","reference"}]}` |

提炼行为（`POST .../distill`，同步执行）：

- 同一作品串行：模块级 `per-project RLock`（进程内即可）。
- 调用模型：`purpose="note_distill"` → `providers` 里走**快速模型**（`model_for("note_distill", hint)`，`STRONG_PURPOSES` 不含它即自动走 fast；不要新增强模型用途），`resolve_for_project(project)`，`add_usage(...)` 记 `purpose="note_distill"`。
- Prompt：system 固定中文说明；user 给笔记标题+正文。要求只输出 JSON `{"reference": "..."}`，内容压缩为本章写作参考，明确区分「构想（计划/意图/待定）」与「已发生事实」；不得扩写剧情、不得新增设定。
- 结果写回前**复核 content_version**：若已变 → 返回 409 `NOTE_VERSION`，把状态置回 `stale`，不覆盖 `auto_reference`。
- 成功：`auto_reference=结果`，`source_version=请求的content_version`，`status="ready"`，`error=""`。
- 失败：`status="failed"`，`error=可读原因`，保留旧 `auto_reference`；无可用模型（provider 解析失败/缺 key）同样按 failed 返回，不抛出 500。
- `notes.py` 提供 `chapter_reference(project_id, chapter_no) -> str`：把该章所有笔记的有效参考按笔记顺序用空行拼接；`status` 取最差（failed > stale > running > none > ready）。生成/规划用。

提示词注入（`services/core/context/__init__.py`）：

- `build_layered_context(..., note_reference: str = "")`，非空时追加到返回的 `extra` 末尾：

```
【本章写作参考（作者笔记提炼；「构想」未发生，「事实」已发生）】
<note_reference>
```

- `generation.run_plan` / `run_generate` 增加可选参数 `note_reference=""` 并透传。
- `main.api_plan`（目标章 = 即将规划的章）与 `main.api_generate`（当前章）调用前取 `notes.chapter_reference(...)` 传入；`api_replan`、`api_style_rewrite` 同样传入（按评注改写走同一注入）。取不到参考时传空串，不影响流程。

### 2.2 历史版本（新模块 `services/core/versions.py`）

存储：`<作品文件夹>/.novel-gm/versions/<no:04d>/<version_id>.json`，每章一个目录。`version_id` = 时间戳+序号（如 `20261018-193012-001`），文件内容：

```json
{
  "id": "...", "chapter_no": 3, "title_at_save": "第3章",
  "body_text": "...", "body_rich": "...",
  "saved_at": "ISO8601", "source": "original|autosave|ai|finalize|restore-backup|manual|legacy-text",
  "word_count": 1234, "hash": "sha256(body_text + '\u0000' + body_rich)",
  "format_available": true
}
```

- 去重键 = `hash`（正文+格式共同去重）。每章最多保留最近 100 版（按 `saved_at` 倒序裁剪）。
- 检查点（`checkpoint(project_id, no, previous_body, previous_rich, source)`）在正文真正被覆盖前调用：
  - 与最近一版 hash 相同 → 跳过。
  - `source == "autosave"` 且最近一版 `source == "autosave"` 且距其 `saved_at` < 5 分钟 → 跳过；否则新增。
  - 其它 source（`original`/`ai`/`finalize`/`restore-backup`/`manual`）总是按 hash 去重后新增。
  - 「首次改动保留原稿」：`api_save_body` 里发现正文/格式相对库中值有变化时，用**变化前**的值记一版，source=`original`（若最近一版已是同 hash 则不再重复）。
- 触发点：`api_save_body`（autosave/original）、`api_generate` 覆盖前（ai）、`api_finalize`（finalize）、`versions.restore` 覆盖前（restore-backup）、`api_clear_chapter` 清空前（restore-backup）。
- 图片：版本只存 `body_rich`，图片路径继续引用作品资源（`插图/` 等），不复制图片。

路由：

| 方法/路径 | 返回 |
|---|---|
| `GET /chapter/{n}/versions` | `{"versions":[{"id","chapter_no","title_at_save","saved_at","source","word_count","format_available"}]}` 按时间倒序 |
| `GET /chapter/{n}/versions/{vid}` | 上述字段 + `body_text`、`body_rich` |
| `POST /chapter/{n}/versions/{vid}/restore` | `Chapter`（复用正常保存链路：`upsert_chapter`，派生结果按正文变化自动失效） |

恢复规则：

- 章节 `superseded` → 409 `SUPERSEDED`（只读规则）；查看不受影响。
- 先 `checkpoint(... source="restore-backup")` 保留当前稿，再 `upsert_chapter` 写回版本的 `body_text`/`body_rich`/`word_count`（不改标题/状态/计划）。派生 `summary/state_delta/consistency` 等由 `upsert_chapter` 的 freshness 逻辑失效即可。
- `legacy-text` 版本：`format_available=false`，恢复时 `body_rich=""`，客户端明确提示无法恢复格式。
- 兼容旧纯文本备份：`workspace.write_chapter_body` 旧机制在 `.novel-gm/history/<sha256(文件名)[:16]>/<sha256(正文)>.md`。`GET versions` 时若有该目录且能确认归属（当前 `body_path` 的 sha256 前 16 位等于目录名，或曾用名记录的 hash 匹配），把其中的 `.md` 作为 `source="legacy-text"`、`format_available=false` 的版本返回（不写盘也可，只读列表+预览）；已存在同 hash 的新版本则不重复。
- 章节删除 / 插入导致编号变化时，`versions` 目录随章节号一起平移（在 `repository.delete_chapter` / `insert_chapter` 里调用 `versions.shift(project_id, from_no, delta)`；整目录改名即可）。

### 2.3 全书查找（新模块 `services/core/search.py`）

`POST /search`（挂在 `/project/{project_id}` 下），请求：

```json
{"query":"...", "case_sensitive":false, "scope":"book|chapter", "chapter_no":3,
 "draft_text":"当前编辑章未保存正文（可空）", "page":1, "page_size":50}
```

返回：

```json
{"total":12,"page":1,"page_size":50,"truncated":false,
 "chapters":[{"chapter_no":2,"title":"第2章","count":8,
   "matches":[{"offset":123,"context":"前文…命中…后文","context_offset":20,"in_draft":false}]}]}
```

- 纯文本子串查找，不做正则；`case_sensitive=false` 用 `casefold()`。
- 排除 `superseded` 章节；`scope=chapter` 只查 `chapter_no`，且若给了 `draft_text` 就用它替代库里的正文（`in_draft=true`）。
- 全书范围：若某章是该 `chapter_no` 且给了 draft，同样用 draft。
- 分页按「章节顺序 + 命中偏移」全局切页；`total` 是命中总数；`truncated` 表示单次最多扫描的字符预算（如 200 万字）被截断。
- 上下文：命中前后各 30 个字符（不跨章节），`context_offset` = 命中词在 `context` 里的起点。
- 空 query → 400 `EMPTY_QUERY`；`page_size` 上限 100。

## 3. 客户端契约（WPF）

命名空间 `NovelGm.Desktop.Controls`。所有面板都必须：自己内部 `ScrollViewer` 滚动、`Background` 用主题画刷、语言切换时重建代码生成的文字（订阅 `LanguageManager.LanguageChanged`，或全部用 `DynamicResource`）。

### 3.1 `NotesPanel`（T4）

```csharp
public partial class NotesPanel : UserControl
{
    public event EventHandler? NotesSaved;                 // 保存成功（触发参考状态刷新）
    public event EventHandler<string>? StatusChanged;      // 状态栏提示 / 错误文案
    public event EventHandler<int>? ReferenceChanged;      // 该章参考状态变化（章号）

    public void SetProject(string? projectId, IReadOnlyList<ChapterDto> chapters, int? currentChapter);
    public void SetCurrentChapter(int? chapterNo);         // 目录切换时跟随，不切正文
    public Task FlushAsync();                              // 提交待保存笔记（切章/切面板/关窗/执行 AI 前）
    public Task<bool> EnsureFreshReferenceAsync(int chapterNo); // 保存并等/补做提炼；失败返回 false 并已提示
    public string CurrentReference(int chapterNo);         // 当前合并参考（给 AI 面板展示）
    public Task ReloadAsync();
}
```

- 每章多笔记：顶部章节下拉（全部未作废章节 + 「跟随编辑章」），列表（标题/摘要/状态点），编辑区（标题 + 正文），新建、删除。
- 默认跟随编辑章（`SetCurrentChapter`），也可选别的章查看而**不切换正文**。
- 停笔 600ms 自动保存；切笔记、切章、切作品、`FlushAsync`、窗口关闭都提交；保存失败保留草稿、状态栏红字提示、可重试。
- 保存成功后启动 10s 计时器；到点若笔记有变化 → 调 distill；同作品任务串行由后端保证；客户端只串行自己的请求并丢弃过期响应（按 note id + content_version 校验）。
- 参考区状态文字：未提炼 / 提炼中 / 已就绪 / 已过期（来源已变）/ 失败（附原因 + 重试按钮）。

### 3.2 `FindPanel`（T4）

```csharp
public partial class FindPanel : UserControl
{
    public event EventHandler<FindHit>? LocateRequested;   // 点击结果
    public event EventHandler<string>? StatusChanged;

    public void SetProject(string? projectId, string? workTitle);
    public void SetDraft(int chapterNo, string bodyText);   // 当前编辑章未保存正文（可空）
    public void SetChapters(IReadOnlyList<ChapterDto> chapters);
    public void FocusScope(bool bookScope);                 // 右侧入口打开时选范围
    public void Reset();                                    // 切作品
}
public sealed record FindHit(int ChapterNo, int Offset, string Query, bool InDraft);
```

- 范围切换（本章 / 全书）、查找框、大小写选项、上/下一处、替换、全部替换（本章）。
- 全书结果：按章节分组显示 章名 · 命中数 + 上下文列表，点击结果定位。
- 异步搜索 + 350ms 防抖；新查询/切作品/切范围 → 递增 generation，旧结果直接丢弃。
- 只读正文禁用替换（`SetReadOnly(bool)` 或由 `SetChapters` 里的状态判断，面板暴露 `SetReplaceEnabled(bool)`）。
- 定位交给 MainWindow（保存当前章 → 切章 → 在正文里选中并滚动）；若定位前复核失败（该处已不含查询词），状态栏提示「结果已失效」。

### 3.3 `AiPanel`（T3）

```csharp
public partial class AiPanel : UserControl
{
    public event EventHandler? PrevRequested;
    public event EventHandler? ShowPlanRequested;      // 本章规划：切到规划区
    public event EventHandler? AiPlanRequested;
    public event EventHandler? GenerateRequested;
    public event EventHandler? FinalizeRequested;
    public event EventHandler? AuditRequested;
    public event EventHandler? PlanCheckRequested;
    public event EventHandler? ExtractRequested;
    public event EventHandler? ShowReviewRequested;    // 本章评注：切到评注区
    public event EventHandler? NextRequested;
    public event EventHandler? EditReferenceRequested; // 编辑「本章写作参考」里的作者参考
    public event EventHandler? RedistillRequested;     // 重新提炼

    public void SetChapterState(bool hasChapter, bool atFirstChapter, bool isLatestChapter, bool hasBody, bool isFinal, bool isReadOnly);
    public void SetBusy(string? busyKey);              // 非空 = 运行中：显示进度、其余按钮禁用
    public void SetReference(ReferenceView reference); // 状态 + 文本 + 是否可重试
}
public sealed record ReferenceView(string StatusKey, string Text, bool CanRetry, bool CanEdit);
```

- 面板标题「AI全流程」；10 个操作按钮顺序固定：上一步 / 本章规划 / AI规划 / 生成·续写 / 定稿 / 一致性审计 / 计划校验 / 提取 / 本章评注 / 下一步。
- 「本章写作参考」`Expander`（默认展开）：状态行 + 只读文本框 + 编辑 / 重新提炼按钮；空态提示。
- 运行中禁止重复点击；进度文字用现有 `Busy.*` 键（`RunStep` 的统一忙碌显示改到这里）。
- 不自己调后端；只发事件。

### 3.4 `VersionsWindow`（T4）

```csharp
public sealed partial class VersionsWindow : Window
{
    public VersionsWindow(string projectId, int chapterNo, string projectTitle,
                          Func<int, Task> onRestored);   // 恢复成功后回调（MainWindow 重载该章）
    public static void Show(Window owner, string projectId, int chapterNo, string projectTitle,
                            Func<int, Task> onRestored);
}
```

- 左侧版本列表（时间 / 来源 / 字数 / 「仅文本」标记），右侧只读预览（有 `body_rich` 时用 RichTextBox 只读还原格式，否则纯文本 + 提示）。
- 恢复：确认框 → 调 `POST restore` → 提示成功 → 回调 → 刷新列表；失败弹窗。
- 恢复按钮在章节只读（作废）时禁用，但窗口仍可查看。

### 3.5 `SensitiveWordsWindow`（T4）

```csharp
public sealed partial class SensitiveWordsWindow : Window
{
    public SensitiveWordsWindow(Func<IReadOnlyList<ChapterDto>> chaptersProvider,
                                Func<int, (int No, string Text)?> draftProvider,
                                Action<int, int> locate, int currentChapterNo);
}
```

- 词库：`Api/SensitiveWords.cs`，文件 `%APPDATA%\novel-gm\sensitive-words.txt`（UTF-8，每行一词；读入去空行/去重/trim）。增删词条、导入（OpenFileDialog，UTF-8 严格解码，失败提示）、导出（UTF-8、CRLF）。
- 检测范围：当前章 / 全书；当前章用 `draftProvider` 的未保存正文；全书用 `chaptersProvider()`（排除 superseded）。
- 结果按章节列出：词、上下文、命中数；点击 → `locate(chapterNo, offset)`；不自动替换。
- 无词库：显示「添加词条或导入 TXT」提示；不提示「平台审核必过」之类结论。

### 3.6 外壳（T2）

- 左栏：`LeftRail` 列（56px，图标+文字，样式复用 `RailButton`）三项：目录 / 笔记 / 查找；`LeftPane` 内容切换 `WorkTreeHost` / `NotesPanelView` / `FindPanelView`。点当前项收起左面板，选项列保留。`SidebarToggle` 仍可用。
- 右栏：`OptionRail` 四项：设定 / 功能 / AI / 审阅（顺序固定，AI 在功能下）。`RightTab` 增加 `Ai`，`LayoutStore.Right_Tab` 接受 `"ai"`；新增 `LayoutStore.Left_Tab`（`"tree"` 默认 / `"notes"` / `"find"`）；旧配置缺字段时按默认值。
- 删除：`AiAssistButton` + `AiAssistPopup` + 相关事件/定位回调、`LoreToggle`（设置窗/布局里的 `Lore_Visible` 语义保留为「右栏是否展开」）、`PhaseText` 的赋值与 XAML（保留 `UpdatePhaseText` 内部阶段判断供按钮状态用，或改为只更新内部状态）。
- 删除 `StepBar` 及底部按钮；`AlignLoreButtons` 需要改成不依赖 `StepBar` 的底部对齐（右栏底部按钮中线对齐创作区底部即可，或直接取消该对齐）。
- 模式菜单：`Mode.Assist` 文案改「AI全流程」（三语），`UiPrefs.WorkMode` 仍存 `assist`；纯净模式不再隐藏 AI 侧栏，打开 AI 面板不改变工作模式（`ApplyWorkMode` 里去掉对 RightPane 的强制折叠；`_loreBeforePure` 逻辑删除）。纯净模式仍隐藏区名开关和底部步骤条。
- 创作区：保留区名开关与四区；`UpdateStepButtons` 的可用状态改为驱动 `AiPanel.SetChapterState`。
- 窗口变窄：`MinWidth` 保持 1120，左右栏 `MinWidth` 降到 200/240，创作区 `MinColumn MinWidth=420`；左右面板内容各自滚动（新增面板自带，目录树已有滚动）。
- MainWindow 挂钩（Lead 最终复核）：`_chapters` 变化 → `NotesPanelView.SetProject/SetChapters`、`FindPanelView.SetChapters`；`ShowChapter` → `SetCurrentChapter` + `FindPanelView.SetDraft`；AI 事件 → 现有 `Prev_Click` / `RevealZone(Zone.Plan)` / `AiPlan_Requested` / `Generate_Click` / `Finalize_Click` / `Audit_Click` / `PlanCheck_Click` / `Extract_Click` / `RevealZone(Zone.Review)` / `Next_Click`；执行 AI 前 `await NotesPanelView.FlushAsync()` + `EnsureFreshReferenceAsync`；`RunStep` 改为 `AiPanelView.SetBusy(...)`。
- 功能区入口（T3 发事件、T2 接）：`ToolsPanel` 暴露 `event EventHandler<(string Action, bool BookScope)>`: `publish` / `versions` / `find-chapter` / `find-book` / `sensitive` / 编辑批注类直接内部处理。

## 4. 三语文案

- T2 负责聚合文件 `Strings/zh-CN.xaml`、`en-US.xaml`、`ja-JP.xaml`：新增既有键的改名（`Mode.Assist`）、`Rail.Notes`、`Rail.Find`、`Rail.Ai`、`Main.ToggleAi` 等外壳键，并在三个聚合文件里用 `ResourceDictionary.MergedDictionaries` 合并各功能字典：

```xml
<ResourceDictionary.MergedDictionaries>
  <ResourceDictionary Source="Ai.zh-CN.xaml"/>
  <ResourceDictionary Source="Notes.zh-CN.xaml"/>
  <ResourceDictionary Source="Find.zh-CN.xaml"/>
  <ResourceDictionary Source="Versions.zh-CN.xaml"/>
  <ResourceDictionary Source="Sensitive.zh-CN.xaml"/>
</ResourceDictionary.MergedDictionaries>
```

- 每个功能字典里所有键必须带前缀：`Ai.*` / `Notes.*` / `Find.*` / `Versions.*` / `Sens.*`。三语键集合必须完全一致（Lead 会跑校验测试）。

## 5. 文件归属（写入范围）

| 成员 | 独占写入 |
|---|---|
| backend-core | `services/core/notes.py`、`versions.py`、`search.py`、`main.py`、`repository.py`、`workspace.py`、`context/__init__.py`、`generation/__init__.py`、`providers/mock.py`、`freshness.py`（如需要）、`services/core/tests/test_notes.py`、`test_versions.py`、`test_search.py`、`test_workspace.py` |
| wpf-shell | `apps/desktop-wpf/MainWindow.xaml`、`MainWindow.xaml.cs`、`Api/LayoutStore.cs`、`Api/UiPrefs.cs`、`App.xaml`（仅 Rail/选中样式与必要新增样式）、`Strings/zh-CN.xaml`、`en-US.xaml`、`ja-JP.xaml`、`apps/desktop-tests/ShellLayoutTests.cs`、`LayoutStoreTests.cs` |
| wpf-ai | `Controls/AiPanel.xaml(.cs)`、`Controls/ToolsPanel.xaml(.cs)`、`Api/BodyEditing.cs`（命令列表与查找弹窗移植）、`Strings/Ai.<lang>.xaml`、`apps/desktop-tests/AiPanelTests.cs` |
| wpf-panels | `Controls/NotesPanel.xaml(.cs)`、`FindPanel.xaml(.cs)`、`VersionsWindow.xaml(.cs)`、`SensitiveWordsWindow.xaml(.cs)`、`Api/NotesClient.cs`、`Api/SearchClient.cs`、`Api/VersionsClient.cs`、`Api/SensitiveWords.cs`、`Models/Dtos.cs`（只追加 DTO）、`Api/NovelApiClient.cs`（只追加方法）、`Strings/Notes/Find/Versions/Sensitive.<lang>.xaml`、`apps/desktop-tests/NotesPanelTests.cs`、`FindPanelTests.cs`、`VersionsWindowTests.cs`、`SensitiveWordsTests.cs` |

冲突处理：`Models/Dtos.cs`、`Api/NovelApiClient.cs` 只有 wpf-panels 能写；别人需要新 DTO 时发消息。`App.xaml` 只有 wpf-shell 能写；T3/T4 需要样式时复用已有键或发消息。

## 6. 完成标准

- 后端：`pytest services/core/tests` 全绿；新增用例覆盖笔记版本冲突/过期响应/删除清参考/无模型失败、版本去重/100版裁剪/旧文本兼容/恢复失效派生、查找分页/大小写/草稿/作废排除。
- WPF：`dotnet build` 0 error；新增 xUnit 覆盖面板状态机（笔记 600ms/失败保留、查找 generation 失效、AI 面板忙碌禁用、布局迁移）。
- 发布：版本号 `0.2.5`，`dist/wpf/novel-gm.exe` 更新，桌面/开始菜单快捷方式指向它，修复副本同步，真实主窗口手工走查通过。
