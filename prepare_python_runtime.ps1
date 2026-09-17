param(
    [string]$PythonVersion = "3.10.11",
    [string]$TargetDir = ".\python_runtime"
)

$ErrorActionPreference = "Stop"

function Write-Step([string]$Message) {
    Write-Host "[STEP] $Message" -ForegroundColor Cyan
}

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$runtimeDir = Join-Path $repoRoot $TargetDir
$tempDir = Join-Path $repoRoot ".tmp_python_runtime"
$zipPath = Join-Path $tempDir "python-embed.zip"
$getPipPath = Join-Path $tempDir "get-pip.py"
$requirementsPath = Join-Path $repoRoot "requirements.txt"

$pythonEmbedUrl = "https://www.python.org/ftp/python/$PythonVersion/python-$PythonVersion-embed-amd64.zip"
$getPipUrl = "https://bootstrap.pypa.io/get-pip.py"

Write-Step "Preparing temp and target directories"
New-Item -Path $tempDir -ItemType Directory -Force | Out-Null
if (Test-Path $runtimeDir) {
    Remove-Item -Path $runtimeDir -Recurse -Force
}
New-Item -Path $runtimeDir -ItemType Directory -Force | Out-Null

Write-Step "Downloading embedded Python $PythonVersion"
Invoke-WebRequest -Uri $pythonEmbedUrl -OutFile $zipPath

Write-Step "Extracting embedded Python"
Expand-Archive -Path $zipPath -DestinationPath $runtimeDir -Force

$pythonExe = Join-Path $runtimeDir "python.exe"
if (!(Test-Path $pythonExe)) {
    throw "python.exe not found in runtime folder after extraction."
}

$pthFile = Join-Path $runtimeDir "python310._pth"
if (!(Test-Path $pthFile)) {
    throw "python310._pth not found. Expected Python 3.10 embedded distribution."
}

Write-Step "Enabling site-packages in embedded Python"
$pthLines = Get-Content $pthFile
$updatedLines = New-Object System.Collections.Generic.List[string]
$siteEnabled = $false
$libAdded = $false

foreach ($line in $pthLines) {
    $trimmed = $line.Trim()
    if ($trimmed -eq "#import site" -or $trimmed -eq "import site") {
        $updatedLines.Add("import site")
        $siteEnabled = $true
        continue
    }
    if ($trimmed -eq "Lib\site-packages") {
        $libAdded = $true
    }
    $updatedLines.Add($line)
}

if (-not $libAdded) {
    $updatedLines.Add("Lib\site-packages")
}
if (-not $siteEnabled) {
    $updatedLines.Add("import site")
}

$updatedLines | Set-Content -Path $pthFile -Encoding ASCII

Write-Step "Downloading get-pip.py"
Invoke-WebRequest -Uri $getPipUrl -OutFile $getPipPath

Write-Step "Installing pip into embedded runtime"
& $pythonExe $getPipPath
if ($LASTEXITCODE -ne 0) {
    throw "get-pip.py failed with exit code $LASTEXITCODE"
}

if (!(Test-Path $requirementsPath)) {
    throw "requirements.txt not found: $requirementsPath"
}

$sitePackagesDir = Join-Path $runtimeDir "Lib\site-packages"
New-Item -Path $sitePackagesDir -ItemType Directory -Force | Out-Null

Write-Step "Installing requirements into embedded runtime"
& $pythonExe -m pip install --no-warn-script-location --upgrade pip --target "$sitePackagesDir"
if ($LASTEXITCODE -ne 0) {
    throw "pip upgrade failed with exit code $LASTEXITCODE"
}

Write-Step "Installing packaging tools required by PTB 13.x"
& $pythonExe -m pip install --no-warn-script-location --upgrade setuptools wheel --target "$sitePackagesDir"
if ($LASTEXITCODE -ne 0) {
    throw "setuptools/wheel installation failed with exit code $LASTEXITCODE"
}

& $pythonExe -m pip install --no-warn-script-location -r $requirementsPath --target "$sitePackagesDir"
if ($LASTEXITCODE -ne 0) {
    throw "requirements installation failed with exit code $LASTEXITCODE"
}

Write-Step "Validating runtime imports"
& $pythonExe -c "import pkg_resources, telegram, apscheduler, socks; print('Runtime OK')"
if ($LASTEXITCODE -ne 0) {
    throw "Runtime validation failed: required modules are not importable."
}

Write-Step "Cleaning temporary files"
Remove-Item -Path $tempDir -Recurse -Force

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "Embedded runtime prepared at: $runtimeDir"
