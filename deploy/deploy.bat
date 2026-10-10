@echo off
chcp 65001 >nul
rem One click: build the hive images and deliver them to the RPi through WSL (docs/deploy.md).
rem   deploy\deploy.bat [user@host] [extra install.sh options, e.g. --rtc]
rem Needs: WSL with Ubuntu, Docker Desktop with WSL integration, ssh access to the RPi.
setlocal
set "TARGET=%~1"
if "%TARGET%"=="" set "TARGET=kirill@192.168.10.4"
set "HOST=%TARGET:*@=%"
set "EXTRA=%~2"

wsl --status >nul 2>&1
if errorlevel 1 (
  echo WSL не найден. Включите WSL и поставьте Ubuntu, либо запустите команды из docs/deploy.md в Git Bash.
  pause
  exit /b 1
)
wsl docker info >nul 2>&1
if errorlevel 1 (
  echo Docker недоступен в WSL. Запустите Docker Desktop и включите: Settings - Resources - WSL integration - Ubuntu, затем Apply.
  pause
  exit /b 1
)

set "WINDIR_REPO=%~dp0.."
set "WINDIR_REPO=%WINDIR_REPO:\=/%"
for /f "usebackq delims=" %%i in (`wsl wslpath -a "%WINDIR_REPO%"`) do set "REPO=%%i"

echo.
echo === Образы backend и frontend, затем доставка на %TARGET% ===
wsl bash -lc "cd '%REPO%' && bash deploy/build-images.sh linux/arm64 && bash deploy/ship.sh %TARGET% --host %HOST% %EXTRA%"
set "CODE=%ERRORLEVEL%"
echo.
if "%CODE%"=="0" (echo Готово.) else (echo Остановилось с ошибкой, код %CODE%.)
pause
exit /b %CODE%
