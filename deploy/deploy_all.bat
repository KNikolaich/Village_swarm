@echo off
chcp 65001 >nul
rem Deploy to the city (amd64) and then home (arm64) in one go. Hornet firmware is built once (for the city) and reused for home.
rem Optional: set SKIP_FIRMWARE=1 first to reuse firmware\dist for both.
setlocal
set NOPAUSE=1
set "HIVE_PLATFORM=linux/amd64"
call "%~dp0deploy.bat" kirill@192.168.1.3
if errorlevel 1 goto failed
set "SKIP_FIRMWARE=1"
set "HIVE_PLATFORM=linux/arm64"
call "%~dp0deploy.bat" kirill@192.168.10.4
if errorlevel 1 goto failed
echo Город и дом обновлены.
pause
exit /b 0
:failed
echo Остановилось с ошибкой, см. выше.
pause
exit /b 1
