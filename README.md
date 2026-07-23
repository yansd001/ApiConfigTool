# API Config Tool (Codex / Claude Code)

Windows desktop app. Double-click `ApiConfigTool.exe` to use.

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
