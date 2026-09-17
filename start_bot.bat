@echo off
echo Запуск бота поддержки на Python 3.10...
echo.

REM Проверка версии Python
py -3.10 --version >nul 2>&1
if errorlevel 1 (
    echo ОШИБКА: Python 3.10 не найден!
    echo Убедитесь, что Python 3.10 установлен и доступен через команду "py -3.10"
    echo.
    pause
    exit /b 1
)

REM Проверка установленных зависимостей
py -3.10 -c "import telegram" >nul 2>&1
if errorlevel 1 (
    echo ВНИМАНИЕ: Зависимости не установлены!
    echo Установите зависимости командой: py -3.10 -m pip install -r requirements.txt
    echo.
    pause
    exit /b 1
)

echo Python 3.10 найден, зависимости установлены.
echo Запуск бота...
echo.
py -3.10 bot.py
pause


