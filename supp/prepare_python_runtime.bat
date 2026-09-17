@echo off
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File ".\prepare_python_runtime.ps1" %*
if errorlevel 1 (echo. & echo Python runtime preparation failed. & pause & exit /b 1)
echo.
echo Python runtime prepared successfully.
pause
