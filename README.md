# API Config Tool (Codex / Claude Code)

Windows desktop app. Double-click `ApiConfigTool.exe` to use.

For macOS and Linux, use the interactive shell version:

```sh
chmod +x api-config-tool.sh
./api-config-tool.sh
```

The shell version requires either Python 3.8+ or Node.js 16+. When Python is
available it fetches an interactive model list; otherwise the model name is
entered manually. The script preserves unrelated settings and creates timestamped
backups before changing existing files. It also honors `CODEX_HOME` and
`CLAUDE_CONFIG_DIR` when those environment variables are set.

## Features

- Input Base URL, API Key, model name
- Fetch model list via `GET {base_url}/models` (compatible with `/v1/models`)
- Write **Codex**
  - `%USERPROFILE%\.codex\config.toml`
  - `%USERPROFILE%\.codex\auth.json`
- Write **Claude Code**
  - `%USERPROFILE%\.claude\settings.json`
- If config exists: only replace base_url / api_key / model
- If config missing: create with default template

## Build

```powershell
cd ApiConfigTool
dotnet restore
dotnet build -c Release
```

## Publish single-file exe

```powershell
cd ApiConfigTool
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ..\publish
```

Output: `publish\ApiConfigTool.exe`
