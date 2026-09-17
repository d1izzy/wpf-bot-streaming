param(
    [string]$Configuration = "Release",
    [string]$OutputRoot = ".\dist"
)

$ErrorActionPreference = "Stop"

function Write-Step([string]$Message) {
    Write-Host "[STEP] $Message" -ForegroundColor Cyan
}

function Find-MSBuild {
    $vsWhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vsWhere) {
        $msbuild = & $vsWhere -latest -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
        if ($msbuild -and (Test-Path $msbuild)) {
            return $msbuild
        }
    }

    $fallback = @(
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
        "${env:WINDIR}\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe"
    )

    foreach ($candidate in $fallback) {
        if (Test-Path $candidate) {
            return $candidate
        }
    }

    return $null
}

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$solutionPath = Join-Path $repoRoot "supp\supp.sln"
$projectOutput = Join-Path $repoRoot "supp\supp\bin\$Configuration"
$portableDir = Join-Path $OutputRoot "BotSuppPortable"
$zipPath = Join-Path $OutputRoot "BotSuppPortable.zip"

if (!(Test-Path $solutionPath)) {
    throw "Solution not found: $solutionPath"
}

Write-Step "Preparing output directories"
New-Item -Path $OutputRoot -ItemType Directory -Force | Out-Null
if (Test-Path $portableDir) { Remove-Item -Path $portableDir -Recurse -Force }
if (Test-Path $zipPath) { Remove-Item -Path $zipPath -Force }

$msbuild = Find-MSBuild
if ($msbuild) {
    Write-Step "Building solution ($Configuration) with MSBuild"
    & $msbuild $solutionPath /t:Restore,Build /p:Configuration=$Configuration /p:Platform="Any CPU" /m
    if ($LASTEXITCODE -ne 0) {
        throw "MSBuild failed with exit code $LASTEXITCODE"
    }
} else {
    Write-Warning "MSBuild not found. Build step skipped; packaging existing files only."
}

if (!(Test-Path $projectOutput)) {
    throw "Build output folder not found: $projectOutput"
}

Write-Step "Copying release files"
Copy-Item -Path $projectOutput -Destination $portableDir -Recurse -Force

$pythonRuntimeSource = Join-Path $repoRoot "python_runtime"
if (Test-Path $pythonRuntimeSource) {
    Write-Step "Copying embedded python runtime"
    $pythonRuntimeDest = Join-Path $portableDir "python_runtime"
    Copy-Item -Path $pythonRuntimeSource -Destination $pythonRuntimeDest -Recurse -Force
} else {
    Write-Warning "Folder 'python_runtime' not found in repository root. Run prepare_python_runtime.bat to bundle Python 3.10 automatically."
}

$proxyRuntimeCandidates = @(
    (Join-Path $repoRoot "proxy_runtime"),
    (Join-Path $projectOutput "proxy_runtime")
)
$proxyRuntimeSource = $proxyRuntimeCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($proxyRuntimeSource) {
    Write-Step "Copying proxy runtime"
    $proxyRuntimeDest = Join-Path $portableDir "proxy_runtime"
    Copy-Item -Path $proxyRuntimeSource -Destination $proxyRuntimeDest -Recurse -Force
} else {
    Write-Warning "Folder 'proxy_runtime' not found (checked repo root and build output). Bot will run without auto-proxy."
}

$readmePortablePath = Join-Path $portableDir "README_PORTABLE.txt"
@"
BotSupp Portable package
=======================

1) Extract this archive to any folder.
2) Run supp.exe.
3) In app:
   - set BOT_TOKEN and ADMIN_ID
   - save .env
   - edit FAQ if needed
   - start bot from 'Управление ботом'

Notes:
- First launch creates runtime workspace in:
  %LOCALAPPDATA%\BotSuppRuntime
- If python_runtime is included, no system Python is required.
"@ | Set-Content -Path $readmePortablePath -Encoding UTF8

Write-Step "Creating ZIP archive"
Compress-Archive -Path "$portableDir\*" -DestinationPath $zipPath -Force

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "Portable folder: $portableDir"
Write-Host "ZIP archive:     $zipPath"
