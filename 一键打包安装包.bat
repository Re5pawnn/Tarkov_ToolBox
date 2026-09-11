@echo off
setlocal EnableExtensions
title 塔科夫工具箱 - Installer Builder

set "ROOT=%~dp0"
set "SCRIPT=%ROOT%src\TarkovMapLocatorDesktop\publish-installer.ps1"
set "RELEASE=%ROOT%release"
set "LOG=%RELEASE%\build-installer.log"
set "RESULT=1"
set "MODE="

if /I "%~1"=="--what-if" set "MODE=-WhatIf"

if not exist "%RELEASE%" mkdir "%RELEASE%"

echo.
echo ============================================================
echo   塔科夫工具箱 - Installer Builder
echo ============================================================
echo.

if not exist "%SCRIPT%" (
  echo [FAILED] Build script was not found:
  echo %SCRIPT%
  goto :finish
)

echo Running checks, portable publish and installer compilation.
echo Full log: %LOG%
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" %MODE% -LogPath "%LOG%"
set "RESULT=%ERRORLEVEL%"

echo.
if "%RESULT%"=="0" (
  if defined MODE (
    echo [SUCCESS] Dry run completed.
  ) else (
    echo [SUCCESS] Installer output:
    echo %RELEASE%
  )
) else (
  echo [FAILED] Exit code: %RESULT%
  echo Please check this log file:
  echo %LOG%
)

:finish
echo.
echo Press any key to close this window...
pause >nul
exit /b %RESULT%
