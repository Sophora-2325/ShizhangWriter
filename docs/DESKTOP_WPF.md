# WPF 原生客户端（方案 B）

独立 Windows 窗口，**不依赖浏览器 / WebView**。界面：.NET 8 WPF；业务：本机 FastAPI。

0.3.9 的 AI 规划入口位于规划区底部和右侧 AI 面板，两处打开同一个按作品／章节 UID 绑定的方案会话。草稿、采用稿和答案保存在独立方案容器，采用后只合并允许的规划字段；历史恢复只创建草稿。方案窗及历史窗都先保存再关闭，失败保留窗口。完整约定见 [PLAN_MODE_A.md](PLAN_MODE_A.md)。

## 日常使用（双击桌面图标）

首次在本机配置一次：

```bat
cd novel-gm
scripts\install-desktop-shortcut.bat
```

会：

1. 自包含发布到工作区固定目录 `AppVersions\novel-gm\novel-gm.exe`，包含后端与内嵌 Python
2. 在桌面创建 **novel-gm** 快捷方式
3. 快捷方式的目标和工作目录都指向上述正式版目录，后续更新保持路径不变

之后只需 **双击桌面图标**，无需打开终端、无需设环境变量。

正式版不依赖仓库 `.venv`。首次从源码发布时，先运行 `scripts\build-installer.bat` 准备完整 Python 运行环境，再发布到固定目录。开发调试仍使用仓库环境。

## 运行（开发）

```bat
cd novel-gm
scripts\run-wpf.bat
```

或：

```powershell
$env:NOVEL_GM_ROOT = (Resolve-Path .).Path
cd apps\desktop-wpf
dotnet run
```

客户端启动时会自动探测 `127.0.0.1:18765`；若未启动则拉起 `python -m services.core`。设置环境变量 `NOVEL_GM_PORT` 可改用其他端口（客户端与它拉起的后端都会用这个端口），便于在正式版运行时另开一套测试数据。

## 根目录如何找到

查找顺序：

1. 正式版优先使用 exe 同目录的后端与内嵌 Python
2. 开发环境才依次查找 `NOVEL_GM_ROOT`、`%LOCALAPPDATA%\novel-gm\root.txt` 和 exe 上级源码目录
3. 仍找不到 → 弹出文件夹选择对话框

## 仅重新发布

```bat
scripts\publish-wpf.bat
```

日常入口：工作区 `AppVersions/novel-gm/novel-gm.exe`（自包含 win-x64 客户端、后端与 Python）。脚本先验证构建中间目录 `dist/release-stage`，再原位更新正式版；旧组件备份到 `dist/release-backup`。详细命令见 `scripts/publish-release.ps1`。

已有 exe 时只装快捷方式：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install-desktop-shortcut.ps1 -SkipPublish
```

## 与旧入口的关系

| 入口 | 状态 |
|---|---|
| **桌面快捷方式 / `novel-gm.exe`** | **默认产品形态** |
| `scripts/run-wpf.bat` | 开发调试 |
| `scripts/dev.bat`（浏览器） | 仅调试 API / 对照 |
| Tauri `apps/desktop` | 不再作为推荐入口 |

## 界面

左目录区 · 中创作区（规划区 / 正文区 / 评注区 + 上一步 / 下一步）· 右小说设定（人物 / 脉络 / 背景，可按章继承）。

控件清单、中文文案与「后端有 / WPF 未接」能力缺口见 [UI_BASELINE.md](UI_BASELINE.md)。
