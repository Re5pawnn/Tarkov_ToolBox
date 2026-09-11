@echo off
setlocal
cd /d "%~dp0"
for %%I in ("%~dp0..\..") do set "WORKSPACE_ROOT=%%~fI"
set "OUTPUT=%WORKSPACE_ROOT%\TarkovMapLocatorDesktop"

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish-desktop-package.ps1" -OutputPath "%OUTPUT%" -RequireUserData
if errorlevel 1 exit /b %errorlevel%
endlocal
