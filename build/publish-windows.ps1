# Builds QueueJanne.exe and queuejanne-mcp.exe for Windows. They're self-contained, so .NET isn't needed to run them.
# Usage: powershell -ExecutionPolicy Bypass -File build\publish-windows.ps1 [-Install] [-Version 0.6.12]
#   -Install copies them to %LOCALAPPDATA%\Programs\QueueJanne and adds a Start menu shortcut.
#   -Version stamps the build; CI uses the version in Directory.Build.props with the run number as the patch.
param([switch]$Install, [string]$Version)

$ErrorActionPreference = "Stop"
if (Get-Process QueueJanne, queuejanne-mcp -ErrorAction SilentlyContinue) {
    throw "Close QueueJanne first (and Claude Code sessions that use its MCP server), so the files can be replaced."
}

$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root "artifacts\win-x64"
$versionArgs = if ($Version) { @("-p:Version=$Version") } else { @() }

dotnet publish (Join-Path $root "src\QueueJanne.App") -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishReadyToRun=true -o $out @versionArgs
if ($LASTEXITCODE -ne 0) { throw "Building the app failed." }
dotnet publish (Join-Path $root "src\QueueJanne.Mcp") -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -o $out @versionArgs
if ($LASTEXITCODE -ne 0) { throw "Building the MCP server failed." }

$app = Join-Path $out "QueueJanne.exe"
$mcp = Join-Path $out "queuejanne-mcp.exe"
if ($Install) {
    $target = Join-Path $env:LOCALAPPDATA "Programs\QueueJanne"
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item $app, $mcp $target -Force
    $app = Join-Path $target "QueueJanne.exe"
    $mcp = Join-Path $target "queuejanne-mcp.exe"
    $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path ([Environment]::GetFolderPath("Programs")) "QueueJanne.lnk"))
    $shortcut.TargetPath = $app
    $shortcut.Save()
    "Installed in $target, with a Start menu shortcut."
}

""
"App:        $app"
"MCP server: $mcp"
"Register the MCP server with Claude Code:  claude mcp add --scope user queuejanne -- `"$mcp`""
