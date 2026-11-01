# 0.2.5 发布说明：左右侧栏重整、笔记与 AI 参考、查找检测、历史版本

> 实施日期：2026-10-18　版本：**0.2.5**　对应设计契约：[ROUND_NOTES_AI_PLAN.md](ROUND_NOTES_AI_PLAN.md)

## 1. 产物与入口核对

| 项 | 结果 |
|---|---|
| 正式程序 | `dist\wpf\novel-gm.exe`，FileVersion **0.2.5.0**，72,362,773 字节，SHA256 前 16 位 `B03AD851EECF9637` |
| 修复副本 1 | `dist\wpf-image-fix\novel-gm.exe`，同版本同哈希 |
| 修复副本 2 | `dist\wpf-image-selection\novel-gm.exe`，同版本同哈希 |
| 桌面快捷方式 | `%USERPROFILE%\Desktop\novel-gm.lnk` → `<工作区>\novel-gm\dist\wpf\novel-gm.exe`，目标存在 |
| 开始菜单快捷方式 | `%APPDATA%\Microsoft\Windows\Start Menu\Programs\novel-gm.lnk` → 同一路径，目标存在 |
| 工作目录 | 两个快捷方式均为 `<工作区>\novel-gm` |
| 后端 | 仓库 `services/core`（新增笔记/版本/查找模块），由 exe 经 `NOVEL_GM_ROOT` / `root.txt` 指向同一副本启动 |
| 安装版副本 | `AppVersions\novel-gm`（0.2.3，Inno 安装版）未重建安装包；现有快捷方式均不指向它。如需替换安装版，重新运行 `scripts\build-installer.bat` |

## 2. 功能落地

### 2.1 布局与交互

- 左栏新增常驻选项列：**目录 / 笔记 / 查找**；点未激活项切换并展开面板，点当前项收起面板，选项列保留。目录树与右键菜单原样保留。
- 右栏选项列改为 **设定 / 功能 / AI / 审阅**，AI 位于功能下方，面板标题「AI全流程」。
- 删除顶栏 AI 按钮及其弹层、右侧隐藏/显示按钮、标题栏「阶段：… · 第 X 章」；作品名保留。内部 `StageOf/ViewStage` 继续驱动流程按钮状态。
- 视图菜单原「AI 辅助模式」改名「AI全流程」（zh/en/ja），配置值仍写 `assist`；已存在的旧 `layout.json`/`ui.json` 继续可用。
- 纯净模式不再强制收起右栏，可主动打开 AI 侧栏，开关侧栏不改变工作模式。
- `layout.json` 新增 `Left_Tab`（`tree` 默认 / `notes` / `find`），`Right_Tab` 支持 `"ai"`；未知/缺失值回落默认，旧配置第一次升级左侧为目录。
- 窗口变窄：创作区 `MinWidth=420`，左右栏 `MinWidth=200/240`，各面板内部滚动。

### 2.2 功能区（右侧「功能」）

- 三组：**发布与版本**（发布到第三方、历史版本）、**查找与检测**（本章查找、全书查找、敏感词检测）、**编辑与批注**（段落设置、非打印字符、添加批注、批注列表）。
- 删除四个粘贴入口、格式刷、特殊字符、打开/接受/拒绝修订；普通粘贴、Ctrl+V / Ctrl+Shift+V、正文右键菜单、快捷键与已有修订数据的载入/保存均保留。
- 「发布到第三方」先 `FlushEditsAsync`（失败则不打开发布窗口），再复用原 `PublishWindow`；登录与最终提交仍由作者在平台完成。

### 2.3 AI 全流程面板

- 10 个操作全部迁入侧栏：上一步、本章规划、AI规划、生成/续写、定稿、一致性审计、计划校验、提取、本章评注、下一步；规划区内原「AI 规划」按钮已移除。
- 复用原 `RunStep` 执行逻辑、模型设置、用量统计与运行锁；运行中面板显示进度并禁用全部操作，防止重复提交；按钮可用状态由 `UpdateStepButtons → AiPanel.SetChapterState` 驱动。
- 新增「本章写作参考」折叠区：显示提炼状态（未提炼/提炼中/就绪/已过期/失败原因）、参考文本、编辑参考、重新提炼/重试。

### 2.4 统一查找

- 左栏查找面板：本章/全书范围、大小写、上/下一处、替换/全部替换（只读正文与全书范围的替换禁用）。
- 全书查找走新增 `POST /project/{id}/search`（分页、排除作废章节、上下文前后 30 字、匹配数）；当前编辑章用未保存正文（`draft_text`，`null` 表示未提供，空串表示草稿被清空）。
- 异步 + 350ms 防抖；新查询/切作品/切范围递增 generation 丢弃旧结果；点击结果先保存当前章，再切章并复核定位（失败提示「结果已失效」）。
- 主窗口与弹出正文窗口共用该入口（Ctrl+H 在正文编辑器内触发左栏查找）。

### 2.5 敏感词检测

- 独立本机词库 `%APPDATA%\novel-gm\sensitive-words.txt`：UTF-8 每行一词，去空行/去重/trim；支持增删、严格 UTF-8 导入（失败提示）、导出（UTF-8 无 BOM、CRLF）。
- 范围当前章/全书；当前章用未保存正文；结果按章列出词、上下文、命中数，点击定位，不自动替换；无词库提示添加或导入；不混入写作禁用词，也不给「平台审核必过」类结论。

### 2.6 历史版本

- 存储 `<作品>/.novel-gm/versions/<no:04d>/<id>.json`：正文、富文本格式、时间、来源（original/autosave/ai/finalize/restore-backup/manual/legacy-text）、字数、正文+格式哈希；图片继续引用作品资源。
- 检查点：正文保存时保留改动前原稿（首次 `original`，之后 autosave 每 5 分钟最多一版）；AI 覆盖、定稿、清空、恢复前立即留版；正文+格式共同去重；每章保留最近 100 版。
- 兼容旧 `.novel-gm/history` 纯文本备份：只展示可确认归属且早于新版本的备份，标记「仅文本」、预览不还原格式、恢复时明确提示无法恢复原格式，并过滤空备份。
- 恢复前确认并备份当前稿，经正常 `upsert_chapter` 链路写回，派生结果按 freshness 失效；作废章节禁止恢复但可查看。
- 章节删除/插入时版本目录与笔记归属随章节号平移，章节改名不影响归属。

### 2.7 每章多笔记与后台提炼

- 左栏笔记面板：章节下拉（跟随编辑章或查看其他章而不切正文）、笔记列表、标题+纯文本编辑、新建/删除；600ms 防抖自动保存，切笔记/切章/切作品/关窗前提交。
- 保存失败保留草稿、红字提示与重试；切换笔记/章节/新建/删除前先 `TryFlushAsync`，失败则留在原笔记，不静默丢失；切换作品前由外壳检查 `HasUnsavedDraft` 并让作者确认。
- 保存后停顿 10s 提炼（多篇笔记排队，同一作品串行）；相同内容不重复调用；请求携带 `content_version`，过期响应按版本丢弃；后端写回前再次复核版本，不符返回 409 且不覆盖旧结果。
- 提炼走 `model_for("note_distill")` 快速模型路由、`resolve_for_project` 与 `add_usage`；提示词要求区分「构想」与「已发生事实」、不扩写、不新增设定。
- 原笔记 `body`、自动结果 `auto_reference`、作者参考 `reference` 分开保存；删除笔记即删参考；来源变化标记过期；失败保留旧结果并给重试；无模型时笔记照常保存。
- 本章规划、生成/续写、AI 规划、按评注改写（style-rewrite）注入该章参考；不跨章继承、不写回人物/世界观/伏笔等正式设定。
- 执行 AI 操作前先保存笔记并补做最新提炼；失败弹窗显示原因，可选重试 / 使用原始笔记继续 / 取消。

### 2.8 文案

- 新增功能字典 `Strings/Ai|Notes|Find|Versions|Sensitive.{zh-CN,en-US,ja-JP}.xaml`，由三个聚合字典 `MergedDictionaries` 合并；三种语言键集合一致，新增静态测试校验 XAML `DynamicResource` 与代码 `L.T` 字面量在三种语言中都有定义。

## 3. 后端接口

| 方法 | 路径 | 说明 |
|---|---|---|
| GET/POST | `/project/{id}/notes` | 列出/新建章节笔记 |
| PUT/DELETE | `/project/{id}/notes/{note_id}` | 改标题+正文（带版本）/删除（同时删参考） |
| POST | `/project/{id}/notes/{note_id}/distill` | 同步提炼一篇笔记（同作品串行、版本复核） |
| PUT | `/project/{id}/notes/{note_id}/reference` | 保存作者编辑后的参考 |
| GET | `/project/{id}/notes/reference?chapter_no=` | 本章合并参考与状态 |
| GET | `/project/{id}/chapter/{n}/versions` | 版本列表 |
| GET | `/project/{id}/chapter/{n}/versions/{vid}` | 只读预览（含 body_rich） |
| POST | `/project/{id}/chapter/{n}/versions/{vid}/restore` | 恢复（先备份当前稿） |
| POST | `/project/{id}/search` | 分页全书/本章查找 |

## 4. 验证结果

### 4.1 自动化

| 套件 | 结果 |
|---|---|
| 后端 pytest（`services/core/tests`） | **267 passed, 1 skipped** |
| WPF xUnit（`apps/desktop-tests`） | **112 passed, 0 failed**（一次全量回归中既有 `PhoneReviewTests.Enter_indents_new_body_paragraph_using_current_character_size` 偶发失败，单独复跑与随后全量复跑均通过，属既有 WPF 焦点/时序用例抖动，与本轮改动无关） |
| `dotnet build`（WPF + tests，Debug/Release 发布） | 0 error |

新增测试覆盖：笔记版本冲突/过期响应/删除清参考/无模型 failed/损坏文件/章节平移；版本 original+autosave+恢复备份/100 版裁剪/旧文本兼容与去重/删除插入平移/superseded 409；查找分页/大小写/casefold 偏移/草稿空串/null/作废排除/空查询；笔记防抖/失败保留/generation 失效；词库去重/导入导出/扫描偏移；AI 面板忙碌禁用矩阵；布局迁移与外壳结构；三语文案完整性。

### 4.2 真实主窗口走查（0.2.5 发布 exe）

隔离环境：临时替换本机 `%LOCALAPPDATA%\novel-gm\storage`、`layout.json`、`%APPDATA%\novel-gm\ui.json`，结束后原样还原；后端 `NOVEL_GM_PORT=18799`、provider=mock，避免真实模型费用。

| 步骤 | 结果 |
|---|---|
| 纯净模式启动 | AI 侧栏可见、RailAi 存在，未改变工作模式 |
| 左栏选项列 | 目录→TreeView、笔记→笔记列表、查找→查找面板依次切换正常 |
| 全书查找 | 输入 `letter` 得到两章结果；点击第 2 章结果后跳到正文并选中命中处 |
| 右栏选项列 | 设定（人物/脉络/背景/杂项 tab）、功能（9 条、3 组）、AI（10 按钮+写作参考）、审阅依次切换正常 |
| 移除项 | 顶栏 AI 按钮/弹层、右侧隐藏按钮、阶段文字、底部步骤条均不存在 |
| AI 全流程 | 本章规划、本章评注、上一步切换正常；AI规划、生成、定稿、提取、审计、计划校验、下一步在 mock 模型下全部执行完成，运行中进度与禁用状态正确 |
| 历史版本 | 从功能区打开，列表/预览/「仅文本」标记正常 |
| 敏感词 | 从功能区打开，词库空态与导入导出入口正常 |
| 键盘 | 正文内 Ctrl+H 打开统一查找面板 |

截图：`../_walkthrough/shots/00-pure-mode.png`、`01-start.png`、`02-notes.png`、`03-find.png`、`04-tools.png`、`05-ai.png`、`06-ai-after-flow.png`、`07-versions-window.png`、`07-sensitive-words-window.png`、`08-ctrl-h.png`；步骤日志 `../_walkthrough/walkthrough.log`。

## 5. 已知边界

- 全书查找为纯子串匹配（非正则）；单次扫描预算 200 万字，超出置 `truncated`。
- 旧纯文本备份只能按当前 `.md` 文件名 hash 确认归属；章节改名前的旧名 history 目录不展示（改名不影响新版本归属）。
- 版本按章节号存储；删除/插入章节时目录随编号平移，历史版本不随章节标题变化。
- 版本与笔记存于作品目录 `.novel-gm`，随「备份为 zip」与重新导入保留；首次打开旧作品不会自动生成版本，直到正文下一次保存。
- `AppVersions\novel-gm` 是 0.2.3 的安装版副本，本轮未重建安装包；所有桌面/开始菜单入口均指向 `dist\wpf` 的 0.2.5。
