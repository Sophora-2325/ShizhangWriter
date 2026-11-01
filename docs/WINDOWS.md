# novel-gm Windows 开发与桌面产品化

## 技术栈

见 [STACK.md](STACK.md)。**不换栈。**

## 数据目录

| 环境变量 | 含义 | 默认 |
|---|---|---|
| `NOVEL_GM_ROOT` | 代码根（含 `services/core`） | 仓库 `novel-gm/` |
| `NOVEL_GM_DATA` | 库与章节存储目录 | `$ROOT/storage`；桌面壳为 `%LOCALAPPDATA%\com.novelgm.desktop\storage` |
| `NOVEL_GM_USE_APPDATA=1` | 强制把 storage 放到 `%LOCALAPPDATA%\novel-gm\storage` | 关闭 |
| `NOVEL_GM_HOST` / `NOVEL_GM_PORT` | API 监听 | `127.0.0.1` / `18765` |

健康检查会返回当前路径：`GET /health` → `{ status, paths }`。

桌面壳后端日志：`%LOCALAPPDATA%\com.novelgm.desktop\storage\backend.log`。

## 一键开发（单终端）

```bat
scripts\dev.bat
```

同一窗口并行跑 API + Vite；`Ctrl+C` 结束（或另开 `scripts\stop.bat`）。

- API：http://127.0.0.1:18765  
- WEB：http://127.0.0.1:5173  

也可用 npm（需已 `npm install` 过根目录）：

```bat
npm run dev
```

## 桌面壳（Tauri 2）

前置：WebView2、Rust、本仓库 `.venv`。

```powershell
cd novel-gm
$env:NOVEL_GM_ROOT = (Resolve-Path .).Path
cd apps\desktop
npm run tauri dev
```

壳启动时会：

1. 查找 `NOVEL_GM_ROOT`（或向上找 `services/core`）
2. 将 `NOVEL_GM_DATA` 指到应用数据目录
3. 若 18765 未占用则 `python -m services.core` 拉起后端，并等待端口就绪
4. 退出时结束子进程

打包：

```powershell
cd apps\desktop
npm run tauri build
```

产物：`release/bundle/nsis/*.exe`、`release/bundle/msi/*.msi`（或拷贝到 `novel-gm/dist/`）。

**注意**：安装包目前依赖本机 Python / 开发机 `.venv` 路径策略；完整离线 sidecar 需另打 PyInstaller 二进制（后续迭代）。开发与本机 Tauri 联调已可用。

## 验收测试

```powershell
.\.venv\Scripts\python.exe -m pytest services\core\tests -q
.\.venv\Scripts\python.exe evals\run_basic.py
```

## 布局

| 左 | 中 | 右 |
|---|---|---|
| 章节列表 / 规划 / 回滚 | 正文、计划、闸门、细修 | 人物 / 脉络 / 文风 |
