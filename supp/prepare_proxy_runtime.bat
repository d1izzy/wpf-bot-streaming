@echo off
setlocal
cd /d "%~dp0"
if "%~1"=="" (
  echo Usage:
  echo   prepare_proxy_runtime.bat "SUBSCRIPTION_URL" ["C:\path\to\sing-box.exe"]
  echo.
  pause
  exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File ".\prepare_proxy_runtime.ps1" -SubscriptionUrl "%~1" -ProxyCoreExePath "%~2"
if errorlevel 1 (echo. & echo Proxy runtime preparation failed. & pause & exit /b 1)
echo.
echo Proxy runtime prepared successfully.
pause
