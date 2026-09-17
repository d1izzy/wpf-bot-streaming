@echo off
echo Остановка бота...
taskkill /F /IM python.exe /FI "WINDOWTITLE eq *bot.py*" 2>nul
echo Бот остановлен.
pause
