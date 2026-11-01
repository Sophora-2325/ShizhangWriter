# 功能与 UI 现状基线
> 2026-10-22（0.3.15）更新：正文顶部操作条改为左对齐单行分组（专注 / 稿纸·铺满 / 字号·正文样式 / 插图·听写 / 批注·历史 / 更多）；新增专注临时隐藏侧栏、宽度快捷切换、批注/历史入口与「更多」格式浮层；只读章节不再整条禁用。详见 [RELEASE_0.3.15.md](RELEASE_0.3.15.md)。

> 2026-10-22（0.3.14）更新：右栏「功能」改为分组工具台（当前章 / 查找与检测 / 排版与批注 / 版本与发布），新增本章统计、复制正文、导出本章、作品备份，补齐状态、键盘焦点与只读/忙碌矩阵。

> 2026-10-22（0.3.12）更新：界面换成「校样台」——纸墨中性底 + 朱砂／靛青／松绿三种强调色，正文字体与强调色脱钩，正文列可切「稿纸／铺满」，目录选中改为浅底 + 2 DIP 标记、定稿收成行尾圆点。主题机制、字号与偏好迁移见 2.9 / 2.10；设计约束见 [UI_STYLE_GUIDE.md](UI_STYLE_GUIDE.md)。

> 2026-10-18 后续调整：笔记改为作品级分类管理，不再参与 AI 创作；查找区精简，AI 面板分组并移除本章写作参考。当前行为见 [个人笔记与侧栏界面调整](PERSONAL_NOTES_UI.md)。

> 2026-10-19 后续更新：人物页移除时态入口；脉络卡新增故事时间位置、基调和里程碑列表；笔记新增右键菜单及持久化手动排序。当前行为见 [脉络卡与笔记交互调整](THREAD_NOTES_REFRESH.md)。

> 2026-10-18（0.2.5）更新：左右选项列（目录/笔记/查找 与 设定/功能/AI/审阅）、AI 全流程面板、统一查找、敏感词检测、历史版本、每章多笔记与本章写作参考已上线；顶栏 AI 按钮/右侧隐藏按钮/阶段文字/底部步骤条已移除。本轮实现与验证见 [ROUND_RELEASE_NOTES.md](ROUND_RELEASE_NOTES.md)，接口契约见 [ROUND_NOTES_AI_PLAN.md](ROUND_NOTES_AI_PLAN.md)。下文为 0.2.4 及更早的基线记录。
> 2026-10-18 更新：右侧新增手机“审阅”面板，取代顶部阅读入口；创作栏精简、字符格式移至右键；设定标题简化。当前行为以 [PHONE_REVIEW.md](PHONE_REVIEW.md) 为准，下文旧阅读布局描述保留作历史记录。


> 2026-10-18 更新：杂项改为「文风偏好 / 内容与用词约束 / 其他」；人称与时态移至人物页独立的作品默认叙事设置；题材、基调、受众移至新增作品信息窗口，并增加封面与章节概况。详见 [小说设定与作品信息调整](LORE_REORGANIZATION.md)。下文保留此前基线。

> 归档日期：对照当前 `apps/desktop-wpf` + `services/core` 代码。  
> 用途：后续 UI / 功能优化的对照基线。**本文件只描述现状，不规定下一轮必做项。**

相关文档：[STACK.md](STACK.md) · [DESKTOP_WPF.md](DESKTOP_WPF.md) · [WINDOWS.md](WINDOWS.md)

---

> ## 📌 本轮变更（2026-10-15：发布到其他平台）
>
> | 位置 | 现状 |
> |---|---|
> | §2.1 / §2.4 文字操作栏 | 工具栏最右端贴边新增**分享按钮**（`Controls/FormatToolbar.xaml` 的 `ShareButton`，三节点分享图标，`ToolTip` = 发布到其他平台）。根 `Grid` 改成 `36 / * / 36` 三列：左右等宽，中间那一列才是整个工具栏的中线（工具行仍精确居中），工具行再长也只被 `ScrollViewer` 自己裁掉，挤不掉右侧贴边的按钮；`BusyHint` 跨三列，位置算法不变 |
> | §2.6 副窗口 | 新增 `PublishWindow`（发布到其他平台）：范围（本章 <章名> / 全书 N 章 M 字）、平台多选（按后端下发的 3 组 14 个平台渲染）、段首缩进与抬头两项按平台默认可覆盖、作者名与简介、生成结果列表（每项带「打开所在文件夹」）。`Owner` 是主窗口，`UiPrefs` 记住上次勾选的平台 / 范围 / 作者 / 缩进 |
> | §4 导出 | **新增「发布到其他平台」**（半自动）：生成平台适配的发布包到 `<作品文件夹>\导出\发布\<平台>\`（正文 `.txt` / `.md` / `.html` + `发布信息.txt`），提供「复制正文」（剪贴板同时放 UnicodeText / CF_HTML / RTF）与「打开作家后台」。**不做登录与自动提交**，不保存任何账号信息 |
> | 后端 | 新增 `services/core/publish/`（`platforms.py` 平台表 + `__init__.py` 生成器）与两个路由：`GET /publish/platforms`、`POST /project/{id}/publish/prepare`。平台地址与要求是纯数据，改地址只需改 `platforms.py` 一行；`unverified=True` 的条目界面会标「地址待核对」。**不改数据库 schema、不改 `作品.json`** |

---

> ## 📌 本轮变更（2026-10-14）
>
> 本基线归档后，界面又改了一轮。**下面这些描述已过时，读 §2.4 / §2.5 / §2.6 / §5 时请以本表为准**（正文里的「三区」「三个 Tab」等表述均指改动前的状态；已就地标注的除外）。
>
> | 位置 | 原描述 | 现状 |
> |---|---|---|
> | §2.4 | 创作区是**三区**（规划 / 正文 / 评注） | **四区**——新增 **审计区**（`Zone.Audit`）。区名胶囊变四个；`ViewStage()` 显式排除审计区，否则它会顶掉正文/评注成为「当前步骤」而破坏上一步/下一步 |
> | §2.4 | — | **审计区**（`Controls/AuditZone.xaml(.cs)`）：把 `chapter.consistency_report` 的 flags 按严重度排开（硬冲突在最上，左侧 3px 色条 + 徽标：红=硬冲突 / 蓝=警告 / 灰=提示）；每条显示 point / 依据 / 建议，右上「定位」跳到正文选中该段引文；右上「重新审计」走与「语义审计」相同的付费路径（带确认框）；空态区分「未审计」与「已审计且干净」，并写明「结果会被『提取摘要』覆盖」。**不改 `style` 快照规则**——它是只读报告面板 |
> | §2.4 | 评注区只有标题 + 一个评注框 | **新增「提交评注」按钮**（`ReviewZone.SubmitRequested` → `SubmitReviewAsync`，与「下一步」同路径）。顺带补上了纯净模式此前**无法进入下一章**的缺口 |
> | §2.5 | 右栏是**三个** Tab（人物 / 脉络 / 背景） | **四个**——新增 **杂项**（`Controls/LoreMisc.xaml(.cs)`），`EqualTabs` 四等分 |
> | §2.5 | — | **杂项**编辑的是**作品级**字段（不是章节快照）：第一组「会真的影响生成」= `forbidden`、`style.pov/tense/sentence_length/banned_words/preferred_replacements/reference_samples`（另只读展示 `metrics` 与 `compiled_system_constraints`）；第二组「当前版本不参与生成」= `genre`/`tone`/`audience`/`npc_auto_act`/`style.diction`/`style.punctuation_policy`。保存走 `POST .../bible`，**只提交真正改动过的字段**；「保存杂项」按钮在面板底部（不在 Tab 外层） |
> | §2.6 子窗口 | — | `CharacterEditorWindow` 新增「公开身份 / 知悉事实 / 状态」三栏；`ThreadEditorWindow` 新增「优先级」（窗口高 455 → 545）。关系框格式扩为 `名字：描述 \| 类型 \| 强度`，后两段可省——**省了就沿用原值**（此前保存时被硬编码为 `related` / `0.5`，每编辑一次就抹平一次） |
> | §2.2 | AI 弹出的按钮列表 | 删掉了「AI 规划」（规划区自带 `AiPlanButton`，是重复入口）、去掉了分组分割线，并改名重排为：**本章规划 / 生成正文 / 本章定稿 / 语义审计 / 本章摘要 / 本章评注**；弹窗改为水平中线对齐「AI」按钮 |
> | §4 | 成本面板 / Bible 编辑 / 禁用词·文风·类型基调 = 未接 | **Bible 编辑与文风一组已接**（杂项）；成本面板仍 ⬜ |

---

## 1. 产品形态

| 项 | 现状 |
|---|---|
| 主入口 | WPF 原生窗口 `apps/desktop-wpf`；桌面快捷方式或 `dist/wpf/novel-gm.exe` |
| 业务权威 | FastAPI `services/core`，`127.0.0.1:18765` |
| 自启后端 | `Api/BackendLauncher.cs`（根目录：`NOVEL_GM_ROOT` → `root.txt` → 向上查找 → 选文件夹）。启动时先探 `/health`：若 18765 上已有后端在跑，会比对它自报的 `paths.root` 与本地根目录，**不是同一个副本就直接报错退出**（`Backend.ForeignBackend`），避免新界面配上别的安装副本的旧后端 |
| 视觉 | 校样台 token：浅/深两套中性纸墨底 + 朱砂 / 靛青 / 松绿三种强调色；正文排印见 2.10（`Themes/`，见 2.9） |
| 正文字体 | 独立偏好：宋体 / 楷体 / 等线 / 仿宋（均 Windows 自带），与强调色脱钩；拉丁走 Cambria / Segoe UI |
| 图标 | 小书页形象：米白纸张、深蓝轮廓、蓝色折角和笑脸。原图为 `apps/desktop-wpf/Assets/novel-gm-logo.png`，`scripts/make-icon.ps1` 默认据此生成 16–256px 共 9 个尺寸的 `novel-gm.ico` 及透明预览 `novel-gm-256.png`。ICO 嵌入 exe 并设为主窗口图标，用于任务栏、Alt+Tab、资源管理器及桌面快捷方式；标题栏使用同一形象的 PNG。替换原图后重新生成并发布即可同步更新 |
| 文案 | 简体中文硬编码；无 `.resx` / 多语言框架 |
| 副窗口 | `NewProjectWindow`、`CharacterEditorWindow`、`ThreadEditorWindow`、`RenameWindow`、`SettingsWindow`；系统 MessageBox；`FolderPicker` |
| 作品存储 | 每个作品一个自包含文件夹（见第 2.8 节）；`%LOCALAPPDATA%\novel-gm\storage\novel.db` 只是索引 / 缓存 |

---

## 2. UI 布局基线

### 2.1 结构

```
36px 集成横条（自绘标题栏：N 图标 / 菜单 / 侧栏开关 / 作品名·阶段 / 窗口按钮）
    ↓
状态提示条 StatusText（保留占位、默认隐藏；成功不再写入；故障 / 错误改为弹窗）
    ↓
左 0.8*（可收起）｜ 中 3*［创作区 ｜ 对照，按需出现］｜ 右 1.1*
目录区作品树     ｜ 文字操作栏 + 区名开关 + 规划区/正文区/评注区 + 步骤条 ｜ 小说设定
```

三栏顶部直接贴住横条、底部贴住窗口边，无外边距、无圆角（`App.xaml` 的 `FlushPanel`）。栏间是 5px 分隔条（`PaneSplitter`：1px 灰线，悬停 / 拖动时变 3px 蓝线），拖动即可自由调整比例；列宽用星号权重，窗口缩放时保持比例。最小宽度：目录区 200、正文 360、右栏 280。关闭窗口时比例写入 `%LOCALAPPDATA%\novel-gm\layout.json`（`Api/LayoutStore.cs`：Left / Center / Right、对照栏相对正文列的倍数 Side、目录区与小说设定区是否显示、上次打开的作品与章节号），下次启动恢复，并自动打开上次的作品与章节、展示正文区（找不到作品时打开第一部；找不到章节时打开该作品最后一章）。

主文件：`MainWindow.xaml` / `MainWindow.xaml.cs`；树节点视图模型 `Models/WorkNode.cs`  
默认启动全屏（`WindowState=Maximized`）；取消全屏后的还原尺寸为 `1380×860`，最小 `1120×720`

### 2.2 集成顶部横条（自绘标题栏）

系统标题栏已由 `WindowChrome`（CaptionHeight 36、ResizeBorderThickness 6）替换，横条空白处可拖动 / 双击最大化；最大化时 `RootBorder` 按 `SystemParameters.WindowResizeBorderThickness` 补边距并去掉 1px 外框。

| 位置 | 控件 | 可见文案 | 行为 |
|---|---|---|---|
| 左 | 品牌图标 | 蓝底白字「N」 | 展示 |
| 左 | 菜单「文件」 | 新建作品… / 打开作品… / 最近打开 ▸ / 保存 / 关闭作品 / 打开作品文件夹 / 导入 TXT/MD… / 导出 Markdown / 导出 EPUB / 导出 TXT / 导出 Word / 退出 | 见下表 |
| 左 | 菜单「编辑」 | 撤销 / 重做 / 剪切 / 复制 / 粘贴 / 全选 / 加粗 / 倾斜 / 下划线 | 见下表；绑定 `ApplicationCommands` / `EditingCommands`，随当前焦点控件启用 |
| 左 | 菜单「视图」 | 纯净模式 / AI 辅助模式 | `UiPrefs.WorkMode`（默认 pure）；见下「工作模式」 |
| 左 | 菜单「设置」 | 运行设置…（Esc） | Esc 打开 `SettingsWindow`；设置窗内 Esc 关闭；确定后重载当前作品 |
| 右 | `AiAssistButton` | 文案「AI」（仅纯净模式显示） | 弹出 AI 辅助面板：AI 规划 / 生成 / 定稿 / 提取；打开规划区、评注区为 ZoneWindow 子窗口 |
| 左 | `SidebarToggle` | 窗户左页图标，位于「视图」右侧（提示：显示/隐藏目录区） | 收起/展开左栏，记忆收起前宽度 |
| 中 | `TitleProjectText` | `novel-gm · {作品名}`；未选作品时 `novel-gm` | 展示 |
| 中 | `PhaseText` | `阶段：{中文} · 第 N 章`（灰色） | 映射 Phase 枚举 |
| 右 | `LoreToggle` | 窗户右页图标，位于最小化按钮左侧（提示：显示/隐藏小说设定区） | 收起/展开右栏，记忆收起前宽度 |
| 右 | 窗口按钮 46×36 | 最小化 / 最大化↔还原 / 关闭（悬停红底） | `SystemCommands.*` |

样式：`App.xaml` 中 `TitleBarButton`、`TitleBarCloseButton`、`TitleMenu`（顶层菜单项圆角悬停，下拉为白底圆角阴影面板）。菜单项右侧灰字显示快捷键（`InputGestureText`）；带下级的项（`SubmenuHeaderTemplate`）右侧有小箭头，悬停向右弹出。

**「文件」菜单**（打开菜单时按当前作品刷新可用项，`FileMenu_SubmenuOpened`）：

| 菜单项 | 快捷键 | 行为 |
|---|---|---|
| 新建作品… | Ctrl+N（焦点在目录区或其它非创作区） | → `NewProjectWindow`；焦点在创作区时同键改为新建章节 |
| 打开作品… | Ctrl+O | 选含「作品.json」的文件夹 → `POST /projects/open` |
| 最近打开 ▸ | — | 最近打开过的作品，最多 8 个，最近的在前（`layout.json` 的 `Recent_Project_Ids`，第一项即启动时自动打开的 `Last_Project_Id`）；已不在目录区的作品自动跳过，没有时显示「（暂无）」 |
| 保存 | Ctrl+S | 立即发出待保存的编辑（`FlushEditsAsync`）；成功无提示，失败弹窗 |
| 关闭作品 | — | 先保存，再清空创作区和设定区（回到「尚未打开作品」提示）；未打开作品时禁用 |
| 打开作品文件夹 | — | 资源管理器打开当前作品文件夹；旧版作品或文件夹缺失时禁用 |
| 导入 TXT/MD… | — | 需先打开作品；多选文件 → 按 UTF-8 严格解码，失败改用 GB18030 → 按标题行 `第[数字]+[章回节卷]`（可带前导 `#`，40 字以内）拆章，标题去掉「第X章」前缀（为空时用默认「第N章」），第一个标题前的文字按文件名单独成章；拆不出标题的文件整篇作为一章 → 确认「将导入 N 章：…」→ `POST /project/{id}/import`，追加到末尾，状态为草稿，设定继承上一章 |
| 导出 Markdown / EPUB / TXT / Word | — | 当前作品 → `GET /project/{id}/export?fmt=…`，见 2.11 |
| 退出 | — | 关闭窗口 |

**「编辑」菜单**（`Command` 指向标准命令，快捷键由控件默认手势提供；菜单项随可否执行灰显）：

| 菜单项 | 快捷键 | 行为 |
|---|---|---|
| 撤销 / 重做 | Ctrl+Z / Ctrl+Y | 当前焦点 `TextBox` / `RichTextBox` 的撤销栈 |
| 剪切 / 复制 / 粘贴 | Ctrl+X / Ctrl+C / Ctrl+V | 作用于焦点控件；只读 `SelectableText` 仅复制可用 |
| 全选 | Ctrl+A | 焦点控件内全选 |
| 加粗 / 倾斜 / 下划线 | Ctrl+B / Ctrl+I / Ctrl+U | `EditingCommands`；仅焦点在可编辑正文 `RichTextBox` 时可用 |

**快捷键**（`MainWindow.SetupShortcuts`）：Ctrl+N（创作区焦点 → 新建当前作品章节，否则 → 新建作品）/ Ctrl+O / Ctrl+S / Esc（打开设置；设置窗内 Esc 关闭，若 AI 弹层开着则先关弹层）/ Ctrl+PageUp（上一章）/ Ctrl+PageDown（下一章；已是最后一章则弹窗「此章为目前最后一章了」）绑在窗口上（Ctrl+PageUp/PageDown 用 `PreviewKeyDown` 处理；Ctrl+←/→ 保留原生按词移动）；编辑类手势由焦点控件与「编辑」菜单的 `ApplicationCommands` / `EditingCommands` 提供（规划区 TextBox、正文 RichTextBox、评注等）；F2（重命名有焦点的作品或章节）和 Delete（删除有焦点的章节，仍需确认）绑在 `WorkTree` 上，只在目录树有键盘焦点时生效，在编辑器里按 Delete 不会误删章节。

**工作模式**（`UiPrefs.WorkMode`，默认 `pure`）：

| 模式 | 界面 | 行为 |
|---|---|---|
| 纯净 | 目录区 + 正文；隐藏区名开关与底部步骤条；标题栏显示「AI」按钮；设定区默认收起，`LoreToggle` 仍可开 | `ApplyStage` 只保证正文在；新建/插入章节不自动切规划区；AI 规划不停靠规划区；关闭规划/评注子窗不回停靠到主栏；AI 面板可自愿 PopOut 规划/评注 |
| AI 辅助 | 区名开关 + 步骤条恢复；隐藏「AI」按钮 | 与原先三区流程一致；切回时恢复进入纯净前的设定区开合 |

人物 / 脉络 / 背景条目子窗口可拉伸，默认尺寸约为原尺寸 ×1.3。

### 2.3 左栏 — 目录区作品树

浅灰侧栏，目录区上方不放其他内容。每个作品是一个文件夹节点，章节是其子节点。

| 控件 | 文案 | 行为 |
|---|---|---|
| 标题行 | 目录区 | — |
| `NewWorkButton` | 新建文件夹图标（提示：新建作品） | → `NewProjectWindow` → `POST /project`（带 `parent_dir`），创建后自动展开 |
| `WorkTree` 作品节点 | 文件夹图标 + 作品名；当前作品加粗蓝色；悬停提示作品文件夹路径 | 单击展开/收起章节；首次点击懒加载章节并设为当前作品 |
| 作品节点（文件夹缺失） | 灰色斜体「作品名（文件夹缺失）」 | 打开时弹窗提示一次；右键只剩「从目录区移除」 |
| `WorkTree` 章节节点 | `N. 标题【已规划\|草稿\|已定稿\|已作废】` | 单击在中栏显示；点其他作品的章节会先切换作品 |
| `EmptyWorksText` | 暂无作品，点击右上角图标新建。 | 无作品时显示 |

**右键菜单**（`MainWindow.xaml` 资源 `WorkMenu` / `ChapterMenu`，白底圆角阴影，样式在 `App.xaml`）：

| 对象 | 菜单项 | 行为 |
|---|---|---|
| 作品 | 打开 | 设为当前作品并展开，选中最后一章 |
| 作品 | 在侧边打开 | 对照栏显示该作品最后一章 |
| 作品 | 在浏览器打开 | 全部未作废章节生成临时 HTML（`Api/HtmlPreview.cs`），用默认浏览器打开 |
| 作品 | 新建章节 | 打开该作品，`POST /project/{id}/chapters` 在末尾建一个空白章并选中；AI 辅助模式展示规划区，纯净模式保持正文区。创作区焦点下 Ctrl+N 对当前作品做同样操作 |
| 作品 | 在资源管理器中打开 | `explorer.exe "<作品文件夹>"` |
| 作品 | 复制文件夹路径 | 复制到剪贴板；失败弹窗 |
| 作品 | 作品信息… | 弹框：章节数（不含已作废）、已定稿数、总字数、文件夹路径 |
| 作品 | 导出 ▸ Markdown / EPUB / TXT / Word | 不必先打开作品，`ExportWorkAsync(id, fmt)`，与「文件」菜单的导出共用 |
| 作品 | 备份为 zip | `POST /project/{id}/backup` → 「导出/备份-作品名-yyyyMMdd-HHmm.zip」（不含「导出」目录本身），完成后在资源管理器中选中该文件 |
| 作品 | 重命名…（F2） | `RenameWindow` → `PATCH /project/{id}`，作品文件夹同步改名 |
| 作品 | 从目录区移除 | 所有作品都有；确认后 `DELETE /project/{id}`，只删索引，作品文件夹不动，可用「打开作品…」重新加回 |
| 作品 | 删除作品… | 两次确认 → 文件夹移到回收站（`Api/RecycleBin.cs`，SHFileOperation + FOF_ALLOWUNDO）→ `DELETE /project/{id}` |
| 章节 | 打开 | 在中栏显示 |
| 章节 | 在侧边打开 | 对照栏显示该章 |
| 章节 | 在浏览器打开 | 单章临时 HTML |
| 章节 | 在此章之后插入新章节 | 已作废章节不显示；`POST .../chapter/{n}/insert-after`：其后章节编号 +1（默认标题、文件名、设定来源、事实一并调整），新章为已规划、设定复制自本章；AI 辅助模式打开新章规划区，纯净模式保持正文区。插在中间的新章不挡后面章节的生成；AI「生成正文」仍只作用于最新一章，中间的章可手写或用「AI 规划」 |
| 章节 | 在资源管理器中定位 | `GET .../chapter/{n}/file` → `explorer.exe /select,"<文件>"` |
| 章节 | 复制正文 | 先保存，再把纯文本正文复制到剪贴板 |
| 章节 | 导出本章… | `SaveFileDialog`（默认位置为作品文件夹）：`.md` 为「# 第N章 标题」加正文，`.txt` 为纯文本（UTF-8 带 BOM、CRLF）；空行分段，客户端直接写文件 |
| 章节 | 重命名…（F2） | `PATCH .../chapter/{n}`，`.md` 文件同步改名；同名冲突报错 |
| 章节 | 删除内容 | 确认后 `POST .../chapter/{n}/clear`：清空正文（含格式）、摘要、报告与细修，章节与本章设定保留，状态回到已规划 |
| 章节 | 删除章节（Delete） | 确认后 `DELETE .../chapter/{n}`：删除 `.md` 与元数据，其后章节前移重新编号，默认标题「第N章」及文件名一并调整 |
| 章节 | 取消定稿 | 仅已定稿章节显示；确认后 `POST .../chapter/{n}/unfinalize`：回到草稿，清掉文风报告，文风基线不变；若是当前章且处于细修阶段，阶段退回生成 |
| 章节 | 回滚到此章… | 确认（默认「否」）后 `POST .../rollback/{n}`：其后章节标记为已作废（内容保留、只读），创作从该章继续 |

旧版集中存储的作品（没有作品文件夹）不显示「在资源管理器中打开」「复制文件夹路径」「备份为 zip」（`FolderOnlyItem`）；文件夹缺失的作品只显示「从目录区移除」（`HideWhenMissingItem`）。样式见 `App.xaml` 的 `ContextItem` 及其派生样式。

### 2.4 中栏 — 创作区（规划区 / 正文区 / 评注区 / 审计区）

> ⚠️ 本节写于只有三区时；**审计区**（只读报告面板）是后加的，完整说明见文首「本轮变更」。

从上到下：文字操作栏 → 区名开关 → 创作区（四区左右排列）→ 步骤条。原来的计划卡片、「＋ 规划下一章」「回滚」「创作下一章」按钮已移除（回滚移到章节右键菜单）。

**区名开关**（`PlanToggle / BodyToggle / ReviewToggle`，`App.xaml` 的 `ZoneToggle` 胶囊按钮，选中为蓝底蓝字）：规划区 / 正文区 / 评注区，保持胶囊原始大小，放在 `ZoneToggleRow`（`Canvas`）里由 `SyncZoneToggles` 定位：每个已打开的区，按钮居中于该区正上方，拖动分隔条或缩放窗口时跟着移动（三区全开且等分时即三等分位置）；未打开或已弹出的区，按钮按流程顺序贴在相邻边界处——前面没有打开的区就贴最左，后面没有就贴最右，夹在两区之间就放在分界处；三区都不在创作区时三等分。按钮之间至少留 6px，互相避让且不越出这一行。点一下打开或关闭该区；每次开关后留在创作区的各区按流程顺序左右排开并重新等分，区与区之间是 `PaneSplitter` 分隔条，可自由拖动调整比例（每区最小 140）。三区都关掉时显示灰色提示。区里没有内容也照常展示。

**按阶段自动展示**（`ApplyStage`）：换章节或章节进入新阶段时只展示对应的一个区 — 还没有正文 → 规划区；有正文、未定稿（或已定稿但已进入下一章）→ 正文区；最新一章定稿后 → 评注区。例外：刚打开作品（启动自动打开、目录区打开、点开另一部作品的章节、最近打开）时若最新一章已定稿，先展示正文区，点「下一步」再进评注区；在当前作品里点「定稿」后仍直接进评注区。同一章阶段不变时（例如自动保存、改设定后重载）保留用户自己打开的区。

**悬浮弹出按钮**（`App.xaml` 的 `ZonePopOutButton`）：各区没有标题条（区名看顶部的区名开关），「在子窗口打开」按钮 26×26 悬浮在区的右上角（距边 6px）。平时淡显（不透明度 0.45），鼠标进入该区时完全显示；悬停按钮时底色 `SidebarHoverBrush`、1px `BorderBrushKey` 描边、图标变为主题主色 `AccentBrush`，按下为 `SidebarPressedBrush`。样式只用主题画刷，四套主题的浅色 / 深色自动适配。停靠在主窗口时给按钮让位（`ApplyCornerInsets`）：规划区顶行右边距加 30（不挡「AI 规划」），正文区右边距 10 → 38（不挡滚动条）；评注区标题居中、输入框在按钮下方，不需要让位；弹出到子窗口后收回让位。弹出后该区内容移到子窗口（`ZoneWindow`，见 2.6），创作区里剩下的区重新等分，对应区名开关保持选中，点它会把子窗口提到前面；关闭子窗口即放回槽位——AI 辅助模式停靠回创作区，纯净模式关闭规划/评注子窗后主栏仍只显示正文。

2026-10-20：规划区改为分区菜单与右侧详情，详见 [PLAN_UI.md](PLAN_UI.md)；下述旧卡片描述仅为历史记录。

**规划区**（`Controls/PlanZone.xaml(.cs)`，写作卡片布局）：顶栏「本章大纲」+「已填 n / 9」进度 +「AI 规划」。主区域为大块自由书写卡片（`plan.free_text`）；下方可折叠「结构化大纲」容纳原全部表单字段。未打开作品时顶部提示条引导先新建或打开作品；章节已作废时提示条说明计划只读。

| 区域 | 内容 |
|---|---|
| 自由书写（默认主视觉） | 圆角浅底卡片内多行文本；结构化大纲有已填项时卡片内提示也会一起交给 AI |
| 结构化大纲（默认收起） | 四组轻量标题（无通栏分割线）：本章核心、戏份与人物（胶囊 +「+ 添加人物 / 脉络」）、约束（可再收起）、篇幅与留白。宽 ≥ 640 时成对字段并排。展开后与自由区之间可拖拽分栏（默认约 55%）；拖到最小自动收起。宿主窗最大化或弹出子窗口且大纲已开时，重新按 55% 排布。AI 规划完成后自动展开 |

自由书写与结构化字段一起保存（`PUT .../chapter/{n}/plan`），并一起写进生成提示词。「AI 规划」：结构化字段非空时先确认覆盖（自由文字保留）→ `POST .../chapter/{n}/replan`（还没有章节时 `POST .../plan`）→ 展开结构化大纲显示结果。生成正文时若结构化全空，后端先以自由书写为提示自动填表。

**正文区**：正文编辑器 + 可折叠「本章摘要」，行为同下文。

**评注区**（`Controls/ReviewZone.xaml(.cs)`）：标题「本章评注」，一个评注框（可选），框内灰色占位文字。下一章写什么由下一章的规划区负责，这里不再填写下一章意图。作为草稿自动保存到 `PUT .../chapter/{n}/review`（`chapter.review`），提交「下一步」后清空。

**文字操作栏**（`Controls/FormatToolbar.xaml(.cs)`，作用于正文选区；正文只读或正文区已弹出到子窗口时整栏禁用，子窗口里另有一条。左右栏出场默认为最窄宽度（目录 200 / 设定 300），创作区吃满剩余，顶栏按宽度自适应）：

| 分组 | 控件 | 行为 |
|---|---|---|
| 字体 | 下拉 | 微软雅黑 / 宋体 / 黑体 / 楷体 / 仿宋 / 等线（已安装的）在前，其后为全部系统字体 |
| 字号 | 可输入下拉 | 初号…小五 + 8～72 磅；可直接输入磅值回车 |
| 字形 | B / I / U | `EditingCommands.ToggleBold/Italic/Underline`（也可 Ctrl+B/I/U） |
| 字色 | A▾ | 弹出 18 色色板 +「自动（默认色）」；A 下方色条显示最近所选颜色 |
| 段落 | 左 / 居中 / 右 / 两端对齐、首行缩进 2 字符、行距 1.0～3.0 | 作用于选区所在的整段 |
| 样式 / 听写 | 与字体、字形等同组居中排开 | 样式整段套用预设；麦克风点按录音 → `POST /stt/transcribe`；语言 zh/en/ja（`UiPrefs.SttLanguage`）；整条工具栏内容水平居中、组间分隔均匀 |
| 正文区下方 | `BodyWordCountText` 右对齐 | 仅字数（如 `2438 字`），在 RichTextBox 外、本章摘要上方；两模式均不显示章序与章名 |

状态随选区刷新（加粗 / 对齐等按钮高亮、字体字号回显，混合格式时留空）。正文区弹出到子窗口时，子窗口工具栏同样带麦克风，与主栏共用同一套录音状态。

**正文编辑区**（`BodyEditor`，RichTextBox，默认微软雅黑 16px ≈ 小四）：手写与 AI 生成共用同一块正文，界面不做区分，也不显示任何占位文字。打开作品后即可像 Word 一样书写，只有未选作品、已作废章节、流水线请求进行中时只读（规划区、评注区同样）。载入时若 `body_rich` 的纯文本与 `.md` 一致则按格式显示，否则（例如 `.md` 被外部编辑器改过）按纯文本逐行重建（`Api/RichBody.cs`）。「本章摘要」为可折叠区，提取摘要后出现。

**自动保存**（三区共用）：正文、计划、评注任一处改动后 1.5 秒保存，改了哪部分就发哪部分（正文 `PUT .../body`，纯文本写回 `.md`、格式存为 `body_rich`；计划 `PUT .../plan`；评注 `PUT .../review`）。切换章节、执行任何按钮或重载前（`Safe` 开头）、关闭窗口前都会先把未保存的修改发出去，保存按顺序排队；失败时保留待保存标记并弹窗提示。作品还没有章节时，在任一区输入内容都会在首次保存时 `POST /project/{id}/chapters` 新建下一章（可带正文和计划），不重载、不打断输入，目录树静默加上并选中该章。

**步骤条**（底部居中）：

| 按钮 | 行为 |
|---|---|
| 上一步 | 只切换展示的区、不删任何内容：评注区 → 正文区 → 规划区；在规划区时打开上一章并显示其评注区。第 1 章规划区时禁用 |
| 生成正文 | 只能在最新一章（`current_chapter`）执行，否则弹窗提示。空白页先 `POST .../plan` 建章；本章计划为空时后端先自动规划。`POST .../chapter` 以本章现有正文为开头接着写：原有格式保留，续写内容的第一行接在最后一段后面，其余按普通段落追加，再 `PUT .../body` 保存格式。上一章尚未提交评注时后端返回 `REFINE_REQUIRED` |
| 定稿 | `POST .../finalize`：标记已定稿、风格检查、更新文风基线，阶段进入细节修订。正文为空时提示 |
| 提取摘要 | `POST .../extract`：提取摘要 / 状态变化 / 事实并做规则/状态一致性审计（不含语义 LLM）；evidence 落库前做逐字子串校验，不合格清空或一次重试；可在定稿前后执行，重复执行会替换本章旧事实 |
| 语义审计 | `POST .../audit/{n}?semantic=true`：显式强模型语义审计；flag 的 evidence 须为正文逐字子串，不合格降为 info；结果在正文区「一致性」可点开定位 |
| 下一步（主按钮） | 以最靠后的已展示区为当前步骤（弹出的区只在创作区里没有区时才算）：规划区 → 正文为空则生成正文，否则切到正文区；正文区 → 未定稿则定稿，已定稿则切到评注区；评注区 → `POST .../refine` 提交评注，再 `POST .../plan` 规划下一章并打开它的规划区。悬停提示说明这一步要做什么 |

不再有阅读闸门：「确认已读完」按钮与闸门提示已移除，细修不检查是否读完。

**忙碌态**（`RunStep`）：请求进行中，被点按钮的文字变为「规划中… / 生成中… / 定稿中… / 提取中… / 语义审计中… / 创作中…」，该按钮不可再点；同步在格式工具栏左侧空白（居中工具行与栏左缘之间）显示同一「XX中…」（`BusyHint`，14px）；其余步骤按钮与「AI 规划」变灰禁用，三区与文字操作栏只读。请求结束后无论成功或失败都恢复原样，错误用弹窗显示。

**对照栏**（`SidePane`，位于中栏列内、正文右侧，默认隐藏）：标题「对照：第N章 标题 · 作品名」+ × 关闭；正文只读。打开后与正文按比例分宽（默认对照 = 正文 × 0.7），中间分隔条可拖动；关闭时记住比例。所显示作品的章节被删除 / 清空或作品被移除时自动关闭。

**文字可选中**：本章摘要、状态条、对照栏标题与正文、正文区底部字数、右栏范围提示、人物 / 脉络卡片里的文字都能用鼠标选中并 Ctrl+C 复制（`App.xaml` 的 `SelectableText` 样式：只读、无边框、透明背景的 TextBox）。在卡片文字上点击或拖选不会触发「点击编辑」；点卡片空白处仍会打开编辑。

### 2.5 右栏 — 小说设定（按章设定快照）

标题区只有「小说设定」（16px 半粗，水平垂直居中）；打开作品后其下出现 `LoreScopeText`（蓝色小字）说明当前编辑的范围，未选作品时隐藏。标签行「人物 / 脉络 / 背景 / 杂项」用 `App.xaml` 的 `EqualTabs` 样式**四等分**整行，每个标签在自己那一格居中，选中时文字下方显示蓝色下划线（**「杂项」是后加的，见文首「本轮变更」**）：

| 情况 | 范围提示 | 保存目标 |
|---|---|---|
| 作品还没有章节 | 作品初始设定 · 第 1 章将继承 | 作品级 |
| 选中最新一章 | 第 N 章设定[ · 继承自第 M 章] · 新章节默认继承 | 该章快照，并同步作品级 |
| 选中较早章节 | 第 N 章设定[ · 继承自第 M 章] · 修改只影响本章 | 仅该章快照 |
| 选中已作废章节 | 第 N 章已作废 · 设定只读 | 不可编辑 |

所有修改走 `PUT /project/{id}/lore?chapter=N`，保存后重载。第 1 章的设定由用户自由填写；新建章节默认复制上一章（作品级）设定作为快照，并记下来源 `lore_from`；生成、定稿、审计、风格重写都用该章快照。

**继承自**（`LoreInheritBar`，标题区下方，仅第 2 章起、未作废章节显示）：「继承自：[第 M 章 ▾] [继承]」。下拉列出之前所有未作废章节，默认选中当前来源（没有来源时选上一章）；点「继承」确认「用第 M 章设定覆盖第 N 章设定？」后 `POST .../chapter/{n}/lore/inherit?from=M`，整份复制人物、脉络、背景条目并重载，之后仍可自由增删改。选同一章再点「继承」可以把改乱的设定恢复成来源章的样子。删除章节时来源章号随重新编号调整。

三个分区布局统一：内容区直接滚动（不再套外层 `LoreBox`），下方居中「添加」按钮；分区外边距 `4,8,4,8`，内边距收紧以加宽卡片约一成。空状态提示直接显示在滚动区内。

| Tab | 框内内容 | 底部居中按钮 |
|---|---|---|
| 人物 | 卡片：左对齐字段表 姓名 / 年龄 / 性格 / 相貌 / 着装 / 近期所想；有画像时 36×36 圆角缩略图显示在姓名右侧；缺必填项显示红字「资料未齐」；卡片右下「编辑 / 删除」，点卡片也可编辑 | ＋ 添加人物 |
| 脉络 | 卡片：名称 + 类型·状态（中文）、用户指令、下一里程碑；「编辑 / 删除」→ `ThreadEditorWindow` | ＋ 添加脉络 |
| 背景 | 卡片：标题（没写时用正文前 24 字）+ 分类徽标（世界观 / 地理 / 势力 / 规则 / 历史 / 物件 / 其他，规则类带硬规则时追加「硬规则」）+ 正文预览两行；「编辑 / 删除」→ `LoreEntryEditorWindow`（分类、标题、正文、规则类才出现的「硬规则」勾选框） | ＋ 添加条目 |

背景设定（`ChapterLore.entries`）是世界书式的分类条目：`category` 取 `world` / `geo` / `faction` / `rule` / `history` / `item` / `other`，`title` 可空，`content` 是正文，`hard` 只对 `rule` 有意义。`rule` 且 `hard=true` 的条目进提示词 L0 的`【硬规则·世界】`，其余条目按分类分组进 L1 的`【背景设定】`。同样按章快照与继承，语义与人物、脉络一致。

三个底部按钮的中线与中栏底部步骤条的按钮行对齐：`StepBar` 尺寸变化、窗口尺寸变化、切换标签时，用 `TranslatePoint` 算出「下一步」按钮中线相对按钮容器底边的距离，设为按钮的下边距（`AlignLoreButtons`）。

### 2.6 子窗口

**新建作品**（`NewProjectWindow`）：作品名、保存位置（`ParentBox` + 浏览…，默认上次位置，否则「文档」）、提示「将创建：…\作品名」、开篇背景（可选，会作为第一条「世界观」条目，同时种入主线脉络的用户指令）、文风范文（可选）；取消 / 创建。

**打开作品**：系统文件夹选择框，选含 `作品.json` 的作品文件夹。

**迁移旧作品**：启动时对每个仍在集中存储中的旧作品弹框询问 — 是：选择保存位置并 `POST /project/{id}/migrate`；否：下次启动再问；取消：本次不再询问其余作品。

**创作区子窗口**（`ZoneWindow.cs`，Owner 为主窗口，标题「区名 · 第N章 标题」随章节切换更新）：点各区右上角的悬浮图标弹出，三区都可以。内容是同一份控件（移过去，不是复制），自动保存照常；正文区的子窗口顶部另有一条文字操作栏，同时主窗口的文字操作栏禁用。关闭子窗口即放回创作区并重新等分。每个区的窗口位置和大小在本次运行中记忆（默认 900×680、居中于主窗口）；主窗口关闭时先保存再一起关闭。

**重命名**（`RenameWindow`）：单行输入，打开时全选；作品 / 章节共用，提示文件夹或 `.md` 会同步改名。

**脉络**（`ThreadEditorWindow`）：名称 *、类型（主线 / 支线 / 感情线 / 悬疑线，外加末尾的「其他…」）、状态（伏笔 / 推进中 / 高潮 / 已收束 / 已搁置）、用户指令、下一里程碑。选「其他…」会弹出单行输入框（复用 `RenameWindow`）：填好的值加进本机类型列表并选中；与内置类型或已存值重名时直接选中已有项，不产生重复。自定义类型只存在 `%APPDATA%\novel-gm\ui.json` 的 `ThreadKinds` 里，**不写进作品文件**，因此换电脑不跟随作品；卡片上按用户原样显示（不转小写）。

**运行设置**（`SettingsWindow`，菜单 设置 → 运行设置…）：

| 控件 | 文案 | 行为 |
|---|---|---|
| `ProjectHint` | 当前作品名；未选作品时「请先在左侧选择作品」并禁用下方分组 | — |
| `ProviderCombo` | DeepSeek 云端 / 本地模型 / 模拟模式 | Tag=`deepseek\|local\|mock` |
| `OfflineCheck` | 离线模式（强制使用本地模型） | 勾选后锁定为本地模型 |
| `ApiKeyBox` + 按钮 | 保存密钥 / 清除密钥 | 立即 `POST /settings`，全局共用 |
| `KeyStatusText` | 密钥已保存 … / 尚未保存密钥 | `GET /settings` |
| 语音听写 | `SttStatusText` +「打开模型目录」 | `GET /stt/status` 显示已就绪路径或未找到模型说明；按钮打开 `%LOCALAPPDATA%\novel-gm\stt` |
| 确定 / 取消 | — | 确定时生成方式或离线有变化才 PATCH 作品设置 |

**人物资料**：必填 — 姓名、年龄、性格、相貌、着装、最近所想；可选 — 角色画像（64×64 预览，选择图片… / 清除；保存时复制到作品文件夹「人物画像/<人物id>.扩展名」；作品未迁移时禁用）、身份、关系、短期目标、出场原因、声口、秘密、备注。

### 2.7 阶段 / 状态中文映射（UI）

| API | 界面 |
|---|---|
| SETUP / PLAN / GENERATE / EXTRACT / GATE / REFINE / REPLAN | 初始化 / 章节规划 / 正文生成 / 信息提取 / 阅读确认 / 细节修订 / 后续规划 |
| planned / drafted / final / superseded | 已规划 / 草稿 / 已定稿 / 已作废 |
| seed / active / climax / resolved / abandoned | 伏笔 / 推进中 / 高潮 / 已收束 / 已搁置 |

| main / sub / romance / mystery | 主线 / 支线 / 感情线 / 悬疑线 |

仍保留英文：品牌 `novel-gm`、DeepSeek、Markdown/EPUB、未知枚举原样回退、后端错误 `detail` 原文。

### 2.8 作品文件夹结构

作品文件夹是权威数据；中央 `novel.db` 以 `projects.root_path` 记录位置，作为索引和检索缓存。

```
<保存位置>\<作品名>\
  作品.json               作品元数据、背景设定（entries）、人物、脉络、文风等（"format": 1）
  <章节名>.md             章节正文（纯文本；默认章节名「第N章」；格式另存于元数据 body_rich）
  人物画像\<人物id>.png    可选画像
  导出\                   导出的 Markdown / EPUB / TXT / Word，以及「备份-*.zip」
  .novel-gm\              隐藏
    chapters\0001.json    每章元数据：计划（含自由规划 free_text）、评注草稿 review、摘要、细修、设定快照 lore（含背景条目）与来源 lore_from、带格式正文 body_rich（FlowDocument XAML）、对应 "file"
    facts.json            已提取事实
```

- 文件夹被移走或删除：作品节点标记「（文件夹缺失）」，写操作返回 `ROOT_MISSING`。
- 名称中的非法字符替换为 `_`；同名章节文件会被拒绝（`CHAPTER_EXISTS`）。

### 2.9 外观与主题

两套互相独立的界面偏好，加上两个派生偏好，都写在 `%APPDATA%\novel-gm\ui.json`（`Api/UiPrefs.cs`），改完立即生效，不用重启，也不用先打开作品。同一个文件里还存 `ThreadKinds`（用户在脉络「类型 → 其他…」里自定义过的类型，见 2.6），旧配置文件没有该字段时回落到空列表。

| 偏好 | 取值 | 界面位置 |
|---|---|---|
| 外观 | 浅色 / 深色 / 跟随系统 | 设置 → 通用设置 → 外观 |
| 强调色 | 朱砂 / 靛青 / 松绿 | 设置 → 主题 |
| 正文字体 | 宋体 / 楷体 / 等线 / 仿宋 | 设置 → 主题 |
| 正文宽度 | 稿纸列 / 铺满 | 设置 → 主题；视图 → 稿纸列 |

配色由「中性底 + 强调色」两层字典合并，放在 `Application.Resources.MergedDictionaries[0]`（`Api/ThemeManager.cs`；`[1]` 是语言字典，由 `Api/LanguageManager.cs` 维护）：

| 层 | 文件 | 内容 |
|---|---|---|
| 中性底 | `Themes/Light.xaml` / `Themes/Dark.xaml` | 纸、页、墨、弱、线、状态色；朱砂作默认强调色 |
| 强调色 | `Themes/Accents/{Zhusha,Dianqing,Songlv}{Light,Dark}.xaml` | 只覆盖 Accent / Hover / Pressed / SelectedBar / SelectedBg / SidebarSelected / InfoBg / InfoBorder 八个键 |

强调色切换只换这一层，正文字体、正文数据、导出换行都不动；评注/审计使用固定朱砂语义（`ReviewBrush` / `ReviewTintBrush` / `ReviewBorderBrush`），不跟强调色走。`Api/ThemeManager.CreatePalette` 负责合并，`Controls/ProofDesk.Apply` 也给独立测试宿主挂同一份。

0.3.12 起旧的 `Themes/Skins/*.xaml` 不再加载，只留在磁盘作回退；`ui.json` 里的 `Skin` 也保留只读。首次启动由 `Api/AppearanceMigration.cs` 迁移一次：经典 → 朱砂、樱紫/蓝白 → 靛青、青绿 → 松绿，并把旧皮肤的正文字体快照进 `BodyFont`。

主题页：3 张强调色卡片（每张一幅迷你界面预览），下面是正文字体与正文宽度两个下拉；明暗仍去「通用设置 → 外观」。左侧导航顺序：通用设置 / 主题 / 模型 / Skills / 插件。

### 2.10 正文排印

写作面（正文区）与 HTML 预览共用字体、字号与首行缩进；行距与行宽自 0.3.12 起分开设定——编辑器按稿纸列，预览与导出保留书页版式，避免改变导出换行。

| | 编辑器 | HTML 预览 |
|---|---|---|
| 字号 | 17px | 17px |
| 行距 | `LineHeight = 32.3`（≈ 17 × 1.9） | `line-height: 1.95`（保留书页版式） |
| 首行缩进 | `TextIndent = 34`（2 个字；旧章存 32，原样保留） | `text-indent: 2em` |
| 行宽 | 稿纸列 `MaxWidth = 34 × 字号 + 62`（17px → 640，正文约 34 字/行，居中）；「铺满」不限宽 | `max-width: 760px`（书页版式，不随稿纸列变化） |
| 字体 | 跟随「正文字体」偏好（`BodyFontFamily` 本地键） | 同一字体栈转成 CSS |
| 渲染 | `TextOptions.TextFormattingMode="Ideal"` | 浏览器默认 |

**正文字体随偏好走**（`Api/ThemeManager.ApplyBodyFont`）：可选宋体（出版物内文）、楷体（手写味）、等线（清爽）、仿宋（稿纸味），字体栈里都带英文衬线（Cambria / Segoe UI）负责拉丁字母和数字。实现方式是把 `BodyFontFamily` 写进 `Application.Resources` 的本地键，本地键优先于主题合并字典，所以 `BodyEditor`、`ReadingView`、`HtmlPreview`、`VersionsWindow` 与导出 `BodyFontSource()` 会一起跟随；切换强调色不会改变它。字体只用 Windows 自带的，不随包分发——仓库很轻，塞一个 13 MB 的中文字体会破坏这个特点。

**段落数据不迁移**：段后 8、首行缩进 32/34 这些值会被 `TextRange.Save` 写进章节的 `body_rich`。0.3.12 只改样式级的行高与列宽，不改写已有段落，所以老章节的段距和缩进保持原样；新建段落继续用当前样式。想统一到「无段后间距」需要另做一次带备份的正文迁移，不在本版本范围。

首行缩进本来就有开关（格式栏的缩进按钮，按 `2 × 字号` 计算），这里只是把默认值调成「开」，用户随时可以按段落关掉；标题、引用等预设会显式设成 0。

**章节数据的兼容处理**：正文以 FlowDocument XAML 存在 `body_rich` 里，而 `TextRange.Save` 会把「当时从编辑器继承到的」字体、字号、字色一起写进 XAML。`Api/RichBody.cs` 的 `StripInheritedDefaults` 负责在存盘和读取时剥掉这些**默认值**（`Microsoft YaHei`、应用用过的四套正文字体、字号 16/17、主题字色 `#FF262626` / `#FFE6E6E6`），于是：换主题会换正文字体、改默认字号对老章节也生效、格式栏能正确认出「正文」预设。用户手动选过的字体（`KaiTi` 等）和字号取值不同，会原样保留。

### 2.11 导出

「文件 → 导出 …」和作品右键「导出 ▸」走 `GET /project/{id}/export?fmt=markdown|epub|txt|docx`，产物落在作品文件夹的「导出」子目录（未迁移的旧作品落在 `%LOCALAPPDATA%\novel-gm\storage\projects\<id>\export`）。

- **TXT**（`export_txt`）：作品名、背景设定里「世界观」一组的正文，然后逐章「第N章 标题」加正文，段与段之间空一行；UTF-8 带 BOM、CRLF 换行，记事本打开不乱码。
- **Word**（`export/docx.py`）：和 EPUB 一样用 `zipfile` 手写 WordprocessingML，不依赖 python-docx。样式：正文（首行缩进 2 字、1.5 倍行距）、标题、标题 1（章标题，每章另起一页、居中）、标题 2 / 3（章节内一级 / 二级标题）、引用；保留加粗、斜体、下划线。字体用客户端传入的正文字体：西文取字体栈第一项，中文取第一个中文字体（没有时用宋体）。

导出内容与写作面对齐：

| | Markdown | EPUB |
|---|---|---|
| 段落结构 | 一段一行，空行分段 | 每段一个 `<p>` |
| 加粗 / 斜体 / 下划线 | `**粗**` / `*斜*` / `<u>斜</u>` | `<strong>` / `<em>` / `<u>` |
| 章节内标题 | `###`（一级）/ `####`（二级） | `<h2>` / `<h3>` |
| 引用 | `> ` | `<blockquote>` |
| 字体 | 纯文本格式，无法保留 | `style.css` 写入当前主题的正文字体 |
| 行距 / 缩进 / 行宽 | 无法保留 | `line-height:1.95`、`text-indent:2em`、`max-width:760px` |

- 解析与输出分别放在 `export/rich.py`（FlowDocument XAML → 块模型）和 `export/writers.py`（→ Markdown / XHTML / EPUB 样式表）。带格式正文 `body_rich` 优先，为空或解析不出来时回退纯文本 `body_text`。
- 块级语义：`Paragraph` 的 `Margin` 左缩进 ≥ 20 判为引用；段内显式加粗且字号 ≥ 28（一级）或 ≥ 20（二级）判为章节内标题，阈值对应格式栏的 Heading1 / Heading2 预设。整篇被选成全粗时根节点的 `FontWeight="Bold"` 不会被误判成标题，标题本身也不再重复套一层行内加粗。
- 标题是默认的「第N章」时不重复输出成「第N章 第N章」——与 HTML 预览用同一套判断（`export.chapter_heading`）。
- EPUB 的字体由客户端通过 `&font=` 传入（`MainWindow.BodyFontSource()` 取当前主题的 `BodyFontFamily`），后端只做字符白名单过滤再拼进 CSS，避免样式注入。EPUB 里字号用 `1em`，实际大小交给阅读器。
- EPUB 3 必需项齐备：mimetype 首个且不压缩、`container.xml`、`content.opf` 带 `dcterms:modified`、nav 文档带 `properties="nav"`；导出文件里的 XHTML / OPF 都过了 XML 良构校验。

---

## 3. 已接通功能闭环

```
建项目 → 填人物 → 选 Provider / 离线
  → 规划区：表单 / 自由规划 / AI 规划（空作品上输入即自动建章）
  → 下一步：生成正文（接着现有正文续写）⇄ 正文区手写 / 排版（自动保存）
  → 下一步：定稿（提取摘要可随时单独执行）
  → 评注区：本章评注（草稿自动保存）
  → 下一步：细修 + 自动规划下一章（新章默认继承上一章设定）
旁路：上一步回看、任意区弹出子窗口、按章继承设定、右键回滚 / 插入章节 / 取消定稿、导入 TXT/MD、导出 MD/EPUB/TXT/Word、备份 zip、保存/清除密钥
```

生成限制：上一章（已生成或已定稿）尚未在评注区点「下一步」提交时，下一章 generate 返回 409 `REFINE_REQUIRED`。generate 不再限制阶段：作用于 `current_chapter`，没有当前章时返回 400 `NO_CHAPTER`，无计划时先自动规划，已有正文时以其为前缀续写。`POST /project/{id}/chapters` 新建下一章（可带手写正文与 `body_rich`），并设为当前章、阶段回到章节规划。阅读闸门已取消（`/gate` 路由保留兼容，WPF 不再调用；refine 不检查闸门；plan 允许在 GATE / REFINE 阶段调用）。生成、风格重写、删除内容会在后端清空 `body_rich`；WPF 续写成功后会把原格式加续写段落重新写回。

WPF 已调用的 API（`NovelApiClient`）：

- `GET /health`（启动器）、`GET|POST /settings`（仅 DeepSeek 密钥）
- `GET /projects`、`POST /project`、`POST /projects/open`、`GET|PATCH|DELETE /project/{id}`、`POST .../migrate`、`PATCH .../settings`
- `GET|POST .../chapters`、`POST .../plan|chapter|finalize|extract|refine|rollback`
- `PATCH|DELETE .../chapter/{n}`、`POST .../chapter/{n}/clear`、`PUT .../chapter/{n}/body|plan|review`、`POST .../chapter/{n}/replan`、`POST .../chapter/{n}/lore/inherit?from=`
- `PUT .../lore?chapter=`、`POST .../portrait`、`GET .../export?fmt=markdown|epub`

---

## 4. 后端已有、WPF 未暴露（优化候选）——**⚠️ 部分已接，见表内 ✅/◐/⬜ 标注**

对照 `services/core/main.py`：

| 能力 | 后端路由 / 字段 | WPF |
|---|---|---|
| 成本面板 | `GET /cost`、`estimated_cost_usd` | ⬜ 未接 / 未展示 |
| 本地模型 URL、模型名、`gate_mode` | `POST /settings` | ⬜ 仅密钥 |
| Bible 编辑 | `POST .../bible` | ✅ **已接**——杂项保存走它（`NovelApiClient.BibleEditAsync`）；后端 `_apply_bible_path` 扩展了 `style.*` / `genre` / `tone` / `audience` / `npc_auto_act` / `characters.{i}.known_facts` / `characters.{i}.status` / `threads.{i}.priority`，并在改风格后**重编译 `compiled_system_constraints`** |
| 背景设定 / 分类条目 | `PUT .../lore` 的 `entries`（world / geo / faction / rule / history / item / other） | 已接（右栏「背景」Tab） |
| 世界观规则编辑 | 已折进 `entries` 的 `rule` 分类 | 已接（同上） |
| 禁用词 / 文风 / 类型基调编辑 | Project + bible | ✅ **已接**——右栏「杂项」Tab（「会真的影响生成」一组 + 「当前版本不参与生成」一组） |
| 细修时改脉络 | refine `thread_updates` | ◐ 脉络在右栏直接编辑；**refine 仍传空**（细修四个字段仍无入口） |
| 一致性 / 风格报告 | extract / finalize / audit 写入章节 | ◐ **一致性**已有专用「审计区」面板 + 正文区「一致性」Expander；**风格报告仍无落点** |
| 独立审计 | `POST .../audit/{n}?semantic=true` | ✅ 步骤条 / AI 助手「语义审计」+ 独立**审计区**；semantic flag 有 evidence 门禁 |
| ~~审计结果会被覆盖（缺陷）~~ | `api_extract` 曾也写 `consistency_report` | ✅ **已修**——`api_extract` 不再写这一列，只有 `api_audit` 写，两者各写各的。另加 `ConsistencyReport.semantic_checked`，让审计区区分「查过且干净」与「只跑过规则检查」；旧数据 → `false` → 显示「还没做过语义审计」 |
| 风格重写 | `POST .../style-rewrite` | 无 |
| 导出 bible | `export?fmt=bible` | 无 |
| 事件时间线 | `GET .../events` | 无 |
| 规划备注 | `PlanRequest.user_notes` | `/plan` 仍传空；规划区的自由规划会在 AI 规划 / 自动填表时作为 user_notes |
| 流式生成 | Provider `stream` | 无 HTTP 流式 |

Web（`apps/web`）曾接过 cost / bible / audit / style-rewrite 等，可作为 WPF 扩展参考，不再作为产品默认入口。

---

## 5. 关键代码入口

| 目标 | 文件 |
|---|---|
| 改布局 / 文案 | `MainWindow.xaml(.cs)`、`App.xaml`、子窗口 XAML |
| 换配色 / 加主题 / 界面语言 | `Themes/*.xaml`、`Themes/Skins/*.xaml`、`Api/ThemeManager.cs`、`Api/UiPrefs.cs`、`Strings/*.xaml`、`Api/LanguageManager.cs` |
| 导出排版与格式 | `services/core/export/rich.py`、`writers.py`、`__init__.py`；字体由 `MainWindow.BodyFontSource()` 传给 `NovelApiClient.ExportAsync` |
| 对接 API | `Api/NovelApiClient.cs`、`Models/Dtos.cs` |
| 启动 / 根目录 | `Api/BackendLauncher.cs`、`Api/FolderPicker.cs` |
| 浏览器预览 / 回收站 / 画像 | `Api/HtmlPreview.cs`、`Api/RecycleBin.cs`、`Api/Portraits.cs` |
| 文字操作栏 / 带格式正文 / 栏宽比例 | `Controls/FormatToolbar.xaml(.cs)`、`Api/RichBody.cs`、`Api/LayoutStore.cs` |
| 创作区三区 / 子窗口 | `Controls/PlanZone.xaml(.cs)`、`Controls/ReviewZone.xaml(.cs)`、`ZoneWindow.cs`；布局与步骤在 `MainWindow.xaml.cs` 的 creation zones / chapter workflow 段 |
| 作品文件夹读写 | `services/core/workspace.py`、`repository.py` |
| 状态机 / 路由 | `services/core/main.py`、`statemachine/`、`models.py` |

---

## 6. 优化准备结论

1. **布局骨架稳定**：三栏 IDE 已定型；后续优先补能力面板 / 报告 / 设定可编辑，不宜推倒重排。
2. **文案已基本中文化**：剩余为品牌与技术词。
3. **能力缺口清晰**：见第 4 节；Web 先行能力是最自然的扩展面。
4. **本阶段无代码改动**：仅归档基线。

---

## 7. 下一轮优化（待指定优先级）

基线已就绪。请任选（或组合）方向后再开**实施计划**：

1. **设定可编辑** — 背景设定已可编辑（分类条目）；脉络在右栏可编辑；剩余 Bible / 禁用词 / 文风 / 类型基调
2. **成本与报告面板** — `GET /cost`、一致性/风格报告展示  
3. **高级流水线** — 规划备注、风格重写、导出 bible（独立语义审计已接）  
4. **本地 Provider 设置** — `local_base_url` / `local_model` / `gate_mode`  
5. **布局再美化** — 不增能力，只调视觉层级与信息密度  

指定优先级前，不新增功能实现。
