@echo off
if "%~1"=="" (
  echo Usage: build.cmd "A:\SteamLibrary\steamapps\common\GUNSAW"
  exit /b 1
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" -GameDirectory "%~1"
exit /b %ERRORLEVEL%
