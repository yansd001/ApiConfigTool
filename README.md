# API 配置工具（Codex / Claude Code）

用于配置 Codex 和 Claude Code 第三方 API 的小工具，支持：

- Windows 图形界面
- macOS / Linux 交互式终端脚本

![API 配置工具最大化运行截图](image.png)

## 功能

- 分别配置 Codex 和 Claude Code 的 Base URL、API Key 与模型名称
- 从兼容 OpenAI 的 `GET /v1/models` 接口获取模型列表
- 自动读取本机已有配置
- 配置存在时修改 API 地址、密钥和模型，保留其他字段，并自动迁移旧版 Codex 认证字段
- Codex 支持在 API 配置和 GPT 账号登录之间切换，分别保存并恢复两套 `config.toml` / `auth.json`
- 配置不存在时根据默认模板创建
- 自动规范化 API 地址

## Windows 使用方法

从 GitHub Release 下载以下任一文件：

- `ApiConfigTool-win-x64-v版本号.zip`：推荐下载，解压后运行
- `ApiConfigTool.exe`：可直接运行的单文件程序

双击 `ApiConfigTool.exe`，选择 Codex 或 Claude Code 标签页，填写配置后保存即可。

打开工具时，Codex 未配置或当前为 GPT 账号登录状态会直接显示 API 配置表单，可先填写并保存 API 配置。

在 Codex 标签页中，可以使用“切换到 GPT 账号”和“切换到 API 配置”按钮切换登录方式。首次切换到 GPT 账号且不存在 GPT 备份时，程序会移除当前 API provider 和 API Key；请随后使用 Codex 自带流程登录账号。之后切回 API 配置会自动恢复 API 备份。

Windows 版本会修改以下文件：

- Codex
  - `%USERPROFILE%\.codex\config.toml`
  - `%USERPROFILE%\.codex\auth.json`
  - `%USERPROFILE%\.codex\apiconfig_state.json`（当前模式及两套配置备份）
- Claude Code
  - `%USERPROFILE%\.claude\settings.json`

## macOS / Linux 使用方法

从 GitHub Release 下载 `api-config-tool.sh`，然后执行：

```sh
chmod +x api-config-tool.sh
./api-config-tool.sh
```

也可以直接进入指定配置流程：

```sh
./api-config-tool.sh --codex
./api-config-tool.sh --claude
```

终端脚本需要以下任一运行时：

- Python 3.8 或更高版本：自动获取模型列表并提供编号选择
- Node.js 16 或更高版本：没有 Python 时使用，模型名称改为手动输入

脚本更新已有配置前会创建带时间戳的备份，并支持以下环境变量：

- `CODEX_HOME`：自定义 Codex 配置目录，默认 `~/.codex`
- `CLAUDE_CONFIG_DIR`：自定义 Claude Code 配置目录，默认 `~/.claude`

macOS / Linux 版本会修改以下文件：

- Codex
  - `${CODEX_HOME:-~/.codex}/config.toml`
  - `${CODEX_HOME:-~/.codex}/auth.json`
- Claude Code
  - `${CLAUDE_CONFIG_DIR:-~/.claude}/settings.json`

## 本地构建

需要安装 .NET 9 SDK：

```powershell
dotnet restore
dotnet build -c Release
```

## 发布 Windows 单文件程序

```powershell
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o publish
```

输出文件：`publish\ApiConfigTool.exe`

项目推送到 `main` 分支后，GitHub Actions 会自动递增补丁版本并创建 Release，同时发布 Windows 压缩包、Windows 单文件程序和 macOS / Linux 终端脚本。
