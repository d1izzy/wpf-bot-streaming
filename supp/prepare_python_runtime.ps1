param(
    [string]$PythonVersion = "3.10.11",
    [string]$TargetDir = ".\python_runtime"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$runtimeDir = Join-Path $repoRoot $TargetDir
$tempDir = Join-Path $repoRoot ".tmp_python_runtime"
$zipPath = Join-Path $tempDir "python-embed.zip"
$getPipPath = Join-Path $tempDir "get-pip.py"
$requirementsPath = Join-Path $repoRoot "requirements.txt"
$pythonEmbedUrl = "https://www.python.org/ftp/python/$PythonVersion/python-$PythonVersion-embed-amd64.zip"
$getPipUrl = "https://bootstrap.pypa.io/get-pip.py"
New-Item -Path $tempDir -ItemType Directory -Force | Out-Null
if (Test-Path $runtimeDir) { Remove-Item -Path $runtimeDir -Recurse -Force }
New-Item -Path $runtimeDir -ItemType Directory -Force | Out-Null
Invoke-WebRequest -Uri $pythonEmbedUrl -OutFile $zipPath
Expand-Archive -Path $zipPath -DestinationPath $runtimeDir -Force
$pythonExe = Join-Path $runtimeDir "python.exe"
$pthFile = Join-Path $runtimeDir "python310._pth"
$pthLines = Get-Content $pthFile
$updatedLines = New-Object System.Collections.Generic.List[string]
foreach ($line in $pthLines) { if ($line.Trim() -eq "#import site") { $updatedLines.Add("import site") } else { $updatedLines.Add($line) } }
if (-not ($updatedLines -contains "Lib\site-packages")) { $updatedLines.Add("Lib\site-packages") }
$updatedLines | Set-Content -Path $pthFile -Encoding ASCII
Invoke-WebRequest -Uri $getPipUrl -OutFile $getPipPath
& $pythonExe $getPipPath
$sitePackagesDir = Join-Path $runtimeDir "Lib\site-packages"
New-Item -Path $sitePackagesDir -ItemType Directory -Force | Out-Null
& $pythonExe -m pip install --no-warn-script-location --upgrade pip setuptools wheel --target "$sitePackagesDir"
& $pythonExe -m pip install --no-warn-script-location -r $requirementsPath --target "$sitePackagesDir"
& $pythonExe -c "import pkg_resources, telegram, apscheduler, socks; print('Runtime OK')"
Remove-Item -Path $tempDir -Recurse -Force
Write-Host "Embedded runtime prepared at: $runtimeDir" -ForegroundColor Green
