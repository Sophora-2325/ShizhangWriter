# novel-gm 技术栈（锁定）

> 产品形态：**原生 WPF 客户端**（方案 B）。业务后端仍为 FastAPI。

## 选型原则

界面用 WPF 原生三栏 → 规则用 Python 后端 → 模型用 Provider 抽象。不做浏览器默认入口、不用 WebView。

## 锁定组合

| 层 | 选型 |
|---|---|
| 客户端 UI | **.NET 8 + WPF**（`apps/desktop-wpf`） |
| 后端 | Python 3.11+ / FastAPI + Pydantic v2 |
| 数据 | SQLite（WAL）+ 章节 Markdown |
| LLM | `services/core/providers/`：OpenAI（`openai`）、Claude（`anthropic`）、Gemini（`google-genai`）三个原生适配器；其余厂商（DeepSeek、Kimi、通义、智谱、豆包、硅基流动、xAI、Mistral、Groq、OpenRouter）、本地模型和自定义地址共用 OpenAI 兼容适配器；Mock 仅供体验和测试。预设目录在 `providers/registry.py`；用户添加的模型配置存 `app_settings.provider_instances`，同一预设可以有多条（id 形如 `openai`、`openai-2`），旧版按厂商存的 `provider_configs` 首次读取时自动迁移 |
| 密钥 | 每条模型配置单独存 Windows 凭据管理器（keyring，键名 `<配置 id>_api_key`）；环境变量只对 id 与预设相同的那条生效；没有密钥时报错，不回退到 Mock；旧版明文启动时自动迁移并擦除 |
| Skills | `SKILL.md`（frontmatter + 指令），内置 Skill 按内容哈希自动更新；`skill_sources.py` 负责 GitHub 搜索、按链接导入、网页正文提取（`httpx` + `lxml`）和 AI 提炼草稿 |
| 本地 STT | `services/core/stt/`：sherpa-onnx + SenseVoice Small INT8（`language=zh`，`use_itn=True`）；WPF 用 NAudio 采 16 kHz mono WAV，经 `GET /stt/status`、`POST /stt/transcribe` 转写后插入正文。模型不打进安装包；查找顺序见 TTSM README / `stt/paths.py` |

## 明确不用（产品默认）

Electron、Tauri WebView 作主界面、独立向量库、LangChain 全家桶。

## 布局与代码映射

| UI | 代码 |
|---|---|
| 左：章节 | `apps/desktop-wpf/MainWindow.xaml` |
| 中：正文 / 闸门 / 细修 | 同上 |
| 右：人物设定 | + `CharacterEditorWindow` |
| 状态机权威 | `services/core/` |

日常启动：桌面 **novel-gm** 快捷方式（首次运行 `scripts\install-desktop-shortcut.bat`）。开发：`scripts\run-wpf.bat`。详见 [DESKTOP_WPF.md](DESKTOP_WPF.md)。

功能与三栏控件 / API 覆盖面现状基线：[UI_BASELINE.md](UI_BASELINE.md)（后续优化对照用，不含实现任务）。
