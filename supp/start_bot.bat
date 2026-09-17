@echo off
echo Запуск бота поддержки на Python 3.10...
echo.
py -3.10 --version >nul 2>&1
if errorlevel 1 (
    echo ОШИБКА: Python 3.10 не найден!
    pause
    exit /b 1
)
py -3.10 -c "import telegram" >nul 2>&1
if errorlevel 1 (
    echo ВНИМАНИЕ: Зависимости не установлены!
    pause
    exit /b 1
)
py -3.10 bot.py
pause
