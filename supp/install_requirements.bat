@echo off
echo Установка зависимостей для Python 3.10...
echo.
py -3.10 --version >nul 2>&1
if errorlevel 1 (
    echo ОШИБКА: Python 3.10 не найден!
    pause
    exit /b 1
)
py -3.10 -m pip install --upgrade pip
py -3.10 -m pip install -r requirements.txt
if errorlevel 1 (
    echo.
    echo ОШИБКА при установке зависимостей!
    pause
    exit /b 1
)
echo.
echo Зависимости успешно установлены!
pause
