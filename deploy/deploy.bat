@echo off
chcp 65001 >nul
rem One click: build the hive images and deliver them to the RPi through WSL (docs/deploy.md).
rem   deploy\deploy.bat [user@host] [extra install.sh options, e.g. --rtc]
rem Builds every hornet firmware first (PlatformIO), so the hive can update hornets over the air.
rem Needs: WSL with Ubuntu, Docker Desktop with WSL integration, ssh access to the RPi.
setlocal
set "TARGET=%~1"
if "%TARGET%"=="" set "TARGET=kirill@192.168.10.4"
set "HOST=%TARGET:*@=%"
set "EXTRA=%~2"
rem Processor of the target: linux/arm64 for the RPi (default), linux/amd64 for a PC or a Proxmox VM (set HIVE_PLATFORM first).
if "%HIVE_PLATFORM%"=="" set "HIVE_PLATFORM=linux/arm64"

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

rem Hornet firmware first: firmware/dist is baked into the api image, and the hive offers it in the "add hornet"
rem wizard and for over-the-air updates. set SKIP_FIRMWARE=1 to reuse what firmware/dist already has.
if "%SKIP_FIRMWARE%"=="1" goto images
set "PIO=pio"
where pio >nul 2>&1 || set "PIO=%USERPROFILE%\.platformio\penv\Scripts\platformio.exe"
"%PIO%" --version >nul 2>&1
if errorlevel 1 (
  echo PlatformIO не найден: на Улье останутся прежние прошивки из firmware\dist.
  goto images
)
echo.
echo === Прошивки шершней ===
set /a FWFAILED=0
for /f "tokens=2 delims=:]" %%e in ('findstr /b /c:"[env:" "%~dp0..\firmware\platformio.ini"') do (
  if not "%%e"=="native" (
    echo --- %%e
    "%PIO%" run -s -d "%~dp0..\firmware" -e %%e
    if errorlevel 1 (
      echo %%e не собралась, на Улье останется прежняя.
      set /a FWFAILED+=1
    )
  )
)
if not "%FWFAILED%"=="0" echo Не собралось прошивок: %FWFAILED%. Ошибки выше.

:images
echo.
echo === Образы backend и frontend, затем доставка на %TARGET% ===
wsl bash -lc "cd '%REPO%' && bash deploy/build-images.sh %HIVE_PLATFORM% && bash deploy/ship.sh %TARGET% --host %HOST% %EXTRA%"
set "CODE=%ERRORLEVEL%"
echo.
if "%CODE%"=="0" (echo Готово.) else (echo Остановилось с ошибкой, код %CODE%.)
if not "%NOPAUSE%"=="1" pause
exit /b %CODE%
