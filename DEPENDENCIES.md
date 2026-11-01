# 依赖与运行准备

本目录保留用于恢复环境的依赖声明与现有锁文件，不附带本机已安装的第三方二进制环境。Python 依赖沿用项目现有版本范围，尚无完整的精确版本锁定文件；不承诺离线安装或逐字节重现本机环境。

## Windows 桌面端（当前默认入口）

需要 Windows 10 / 11 x64、Git、.NET 8 SDK 和 Python（推荐 3.13）。

| 部分 | 依赖来源 |
|---|---|
| WPF 客户端 | apps/desktop-wpf/NovelGm.Desktop.csproj；NAudio 2.2.1 |
| 后端 | services/core/requirements.txt |
| 桌面测试 | apps/desktop-tests/NovelGm.Desktop.Tests.csproj；Microsoft.NET.Test.Sdk、xUnit |
| 生命周期检查 | apps/desktop-lifecycle-tests/LifecycleProbe.csproj |

在本目录执行：

```powershell
py -3.13 -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r services\core\requirements.txt
dotnet restore apps\desktop-wpf\NovelGm.Desktop.csproj
scripts\run-wpf.bat
```

## 可选浏览器调试界面

需要 Node.js 18+。依赖清单和锁文件保留在根目录以及 apps/web/ 中，安装命令为：

```powershell
npm ci
npm ci --prefix apps/web
npm run dev
```

## 可选历史 Tauri 外壳

apps/desktop/ 保留原有源代码、package-lock.json、Cargo.toml 和 Cargo.lock。使用时还需要 Rust 工具链与相应的 Windows/WebView2 开发环境。这不是当前推荐的桌面入口。

```powershell
npm ci --prefix apps/desktop
cargo fetch --manifest-path apps/desktop/src-tauri/Cargo.toml
```

## 完整 Windows 安装包

需要 Python 3.13 和 Inno Setup 6，执行 scripts/build-installer.bat。构建脚本按需准备客户端、后端与内嵌 Python；本副本不附带已编译运行时或安装包。

## 可选本地语音模型

听写使用 sherpa-onnx 和 SenseVoice，本副本不附带模型文件。准备包含 model.int8.onnx 与 tokens.txt 的模型目录后，将 NOVEL_GM_STT_MODEL 指向该目录；也可放到 %LOCALAPPDATA%/novel-gm/stt/sense-voice-int8-2024-07-17/。模型来源及许可应按模型发布方说明核对。

## 第三方材料

第三方软件、依赖和模型各自适用上游许可证。本项目 CC BY-NC 4.0 不改变它们的许可。安装器的 installer/ChineseSimplified.isl 保留了原翻译者的署名与来源；其他依赖通过各自包管理器获取。
