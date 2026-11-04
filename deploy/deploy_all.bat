@echo off
chcp 65001 >nul
rem Deploy to the city (amd64) and then home (arm64) in one go. Only the hornet firmwares that changed are built (for the city); home finds them up to date and reuses them.
rem Optional: set WITH_FIRMWARE=1 first to rebuild every firmware.
setlocal
set NOPAUSE=1
set "HIVE_PLATFORM=linux/amd64"
call "%~dp0deploy.bat" kirill@192.168.1.3
if errorlevel 1 goto failed
rem The second hive reuses what the first built, even when WITH_FIRMWARE=1 forced it.
set "WITH_FIRMWARE="
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
