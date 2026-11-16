param(
    [Parameter(Mandatory=$true)][string]$Executable,
    [Parameter(Mandatory=$true)][string]$FixturePath,
    [Parameter(Mandatory=$true)][int]$DebugPort
)
$ErrorActionPreference = 'Stop'
$fixtureRoot = (Resolve-Path -LiteralPath $FixturePath).Path
# These overrides apply only to the isolated test child, never the calling session.
$appProcess = Start-Process -FilePath $Executable -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $fixtureRoot 'native.stdout.log') -RedirectStandardError (Join-Path $fixtureRoot 'native.stderr.log') -Environment @{
    API_CONFIG_TOOL_HOME = Join-Path $fixtureRoot 'store'
    CODEX_HOME = Join-Path $fixtureRoot 'codex'
    CLAUDE_CONFIG_DIR = Join-Path $fixtureRoot 'claude'
    LOCALAPPDATA = Join-Path $fixtureRoot 'local'
    WEBVIEW2_USER_DATA_FOLDER = Join-Path $fixtureRoot 'webview'
    WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=$DebugPort"
}
Set-Content -LiteralPath (Join-Path $fixtureRoot 'app.pid') -Value $appProcess.Id
