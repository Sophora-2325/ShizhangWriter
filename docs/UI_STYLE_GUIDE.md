# 校样台视觉规范

> 0.3.12 起生效。目标不是「文艺」，而是让写作台读起来像专业稿纸：纸、墨、朱笔三种材质，克制、耐看、可长时间停留。
> 实现位置：`Themes/Light.xaml`、`Themes/Dark.xaml`、`Themes/Accents/*`、`Api/ThemeManager.cs`、`App.xaml`。


## 1. 五条原则

1. **三种材质**：纸、墨、朱笔，不引入第四种颜色。
2. **用明暗分层，不用线框分层**：外壳比正文页暗一档，靠色差与间距分区。
3. **朱砂只出现在「校」的语义位置**：评注、审计、当前选中、主操作。不做装饰。
4. **UI 全无衬线，衬线只属于正文**。
5. **不做装饰性动效**；交互反馈 120–160ms，遵循系统动画设置。

## 2. 色板

### 2.1 中性底

| 角色 | 键 | 浅色 | 深色 |
|---|---|---|---|
| 外壳 | `PageBrush` / `Bg2Brush` / `SidebarBrush` | `#F4F3EF` | `#141518` |
| 页/编辑面 | `BgBrush` / `Bg3Brush` / `ControlBgBrush` | `#FCFBF7` | `#1B1D20` |
| 墨 | `TextBrush` | `#16181B` | `#E9E6DE` |
| 弱 | `MutedBrush` | `#62666C` | `#9A9EA6` |
| 占位 | `PlaceholderBrush` | `#77736A` | `#6E7278` |
| 线 | `BorderBrushKey` / `WindowBorderBrush` | `#E2DFD8` | `#2A2D31` |
| 悬停/按下 | `SidebarHoverBrush` / `SidebarPressedBrush` / `ToolHoverBrush` | `#EAE8E1` / `#E0DDD4` / `#EDEBE4` | `#23262A` / `#2B2F34` / `#23262A` |
| 滚动条 | `ScrollThumbBrush` / `ScrollThumbHoverBrush` | `#CFCCC2` / `#B4B0A6` | `#3A3E42` / `#4A4E53` |

### 2.2 强调色

每套强调色只覆盖 8 个键：`AccentBrush`、`AccentHoverBrush`、`AccentPressedBrush`、`SelectedBarBrush`、`SelectedBgBrush`、`SidebarSelectedBrush`、`InfoBgBrush`、`InfoBorderBrush`。

| 强调色 | 浅：accent / hover / pressed / tint / border | 深：accent / hover / pressed / tint / border |
|---|---|---|
| 朱砂 | `#A6402E` / `#8E3527` / `#7C2E22` / `#F1DED8` / `#E0B9AF` | `#D0725C` / `#DF8570` / `#B85B47` / `#3A2320` / `#5A3A33` |
| 靛青 | `#2C4A73` / `#243D5F` / `#1D3350` / `#DDE6F0` / `#B9C9DB` | `#7FA6D9` / `#97B8E4` / `#6A8FBF` / `#1E2A38` / `#35465C` |
| 松绿 | `#3F6B4F` / `#355A42` / `#2C4B37` / `#DDE8E0` / `#B9CFC0` | `#8FBF9F` / `#A6CEB4` / `#79A889` / `#1F2D24` / `#38513F` |

### 2.3 语义色（不跟强调色走）

| 语义 | 浅 | 深 | 用途 |
|---|---|---|---|
| 评注/审计 | `#A6402E` / `#F1DED8` / `#E0B9AF` | `#D0725C` / `#3A2320` / `#5A3A33` | `ReviewBrush` / `ReviewTintBrush` / `ReviewBorderBrush` |
| 成功 | `#3F6B4F` | `#6E9E7C` | 已定稿、已保存 |
| 警告 | `#8A6420` 文字 / `#F4EAD6` 底 | `#C9A24A` / `#332A18` | 待处理、过期 |
| 危险 | `#A6402E` | `#D0725C` | 删除、硬冲突 |

## 3. 排版

| 层级 | 字体 | 规格 |
|---|---|---|
| 正文 | 偏好：宋体 / 楷体 / 等线 / 仿宋（带 Cambria / Segoe UI 拉丁兜底） | 17px，行高 1.9，首行缩进 2 字，段后 8（保留旧数据） |
| 章节标题 | 正文衬线 600 | 22px，中文数字，居中 |
| UI | 微软雅黑 / Segoe UI | 13px，行高 1.5；元数据 12px |
| 左选项列 | UI 字体 | 11px（56px 宽 + 日语 5 字的唯一例外） |

正文列宽：稿纸列 `MaxWidth = 34 × 字号 + 62`（17px → 640，正文约 34 字/行，居中）；「铺满」不限宽。只影响呈现，不改正文数据、插图、历史或导出换行。

## 4. 形状与层级

- 控件圆角 3；面板直角；徽标/胶囊保留 14 等特殊值。
- 默认不画线；列表行之间 1px `#E2DFD8` / `#2A2D31`。栏与栏之间靠色差 + 间距。
- 选中：浅底 + 左侧 2 DIP 强调色标记，不用整行高饱和色块。
- 章节状态：行尾 6px 圆点（定稿 `OkBrush`、被替代半透明弱色、草稿不显示），完整状态在 ToolTip。
- 阴影只给弹层：`0 8 24 rgba(20,22,26,.08)`。
- 图标 16px 单线，跟随前景色。

## 5. 实现约束

- 配色是「中性底 + 强调色」两层：`ThemeManager.CreatePalette(dark, accent)` 合并后放进 `Application.Resources.MergedDictionaries[0]`；语言字典保持在 `[1]`。
- 强调色切换只换这层；正文字体由 `ThemeManager.ApplyBodyFont()` 写 `BodyFontFamily` 本地键，与强调色脱钩。
- 旧 `Themes/Skins/*.xaml` 保留但不加载；`ui.json` 的 `Skin` 字段保留只读，供 0.3.11 回退。
- 首次启动 `AppearanceMigration` 迁移一次：经典→朱砂、樱紫/蓝白→靛青、青绿→松绿，并快照旧皮肤的正文字体。
- 新增色键只加在 `Light.xaml` / `Dark.xaml`，三套强调色自动继承；只有强调派生键才写进 `Themes/Accents/`。

## 6. 禁止

- 禁止再引入 Ant Design 默认蓝 `#1677FF`、成功绿 `#52C41A`、危险红 `#FF4D4F`。
- 禁止「整屏奶油色 + 高对比衬线标题 + 陶土色」的模板组合。
- 禁止用「清洗正文格式」实现换肤；禁止重写 `body_rich` 的段落属性。
- 禁止在 UI 里使用正文衬线；衬线只用于正文/引文。
- 禁止新增 11px 以下的正文级文字（左选项列例外）。

## 7. 验收清单

- [ ] 三种强调色 × 明暗都能加载，无缺失资源。
- [ ] 正文、次要文字、强调色文字对比度 ≥ 4.5:1。
- [ ] 稿纸列 17px 下每行 ≤ 34 字；切「铺满」后随窗口变化；偏好跨重启保留。
- [ ] 切换强调色/字体/宽度后，章节 `body_rich` 与历史不变。
- [ ] 目录选中为浅底 + 2 DIP 标记；定稿为行尾圆点。
- [ ] 三语、1120×720、100%/150%/200% 缩放下无裁切。
