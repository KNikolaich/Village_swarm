@echo off
chcp 65001 >nul
rem Deploy without rebuilding hornet firmware (web and backend changes only).
rem Uses the firmware already in firmware\dist. Extra arguments go to deploy.bat, e.g. kirill@192.168.10.4.
set SKIP_FIRMWARE=1
call "%~dp0deploy.bat" %*