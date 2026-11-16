param([switch]$Optional)
$ErrorActionPreference = 'Stop'

if ($env:WEBVIEW2_BROWSER_EXECUTABLE_FOLDER) {
    $runtimePath = $env:WEBVIEW2_BROWSER_EXECUTABLE_FOLDER
    if (-not (Test-Path -LiteralPath (Join-Path $runtimePath 'msedgewebview2.exe'))) {
        throw 'WEBVIEW2_BROWSER_EXECUTABLE_FOLDER does not contain msedgewebview2.exe.'
    }
    (Resolve-Path -LiteralPath $runtimePath).Path
    return
}

$runtimeRoots = @(
    ${env:ProgramFiles(x86)}
    $env:ProgramFiles
    $env:LOCALAPPDATA
) | Where-Object { $_ } | Select-Object -Unique
$candidates = foreach ($root in $runtimeRoots) {
    $runtimeRoot = Join-Path $root 'Microsoft/EdgeWebView/Application'
    if (Test-Path -LiteralPath $runtimeRoot) {
        foreach ($directory in (Get-ChildItem -LiteralPath $runtimeRoot -Directory)) {
            $parsedVersion = $null
            if ([Version]::TryParse($directory.Name, [ref]$parsedVersion) -and
                (Test-Path -LiteralPath (Join-Path $directory.FullName 'msedgewebview2.exe'))) {
                [PSCustomObject]@{ Path = $directory.FullName; Version = $parsedVersion }
            }
        }
    }
}
$runtimePath = ($candidates | Sort-Object Version -Descending | Select-Object -First 1).Path
if ($runtimePath) {
    Write-Output $runtimePath
} elseif (-not $Optional) {
    throw 'WebView2 Runtime was not found. Install Microsoft Edge WebView2 Runtime before the native check.'
}
