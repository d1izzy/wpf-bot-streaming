@echo off
setlocal

cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File ".\build_portable_zip.ps1" %*

if errorlevel 1 (
  echo.
  echo Build/package failed.
  pause
  exit /b 1
)

echo.
echo Package created successfully.
pause
