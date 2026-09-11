@echo off
setlocal
cd /d "%~dp0"
for %%I in ("%~dp0..\..") do set "WORKSPACE_ROOT=%%~fI"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish-desktop-package.ps1" -OutputPath "%WORKSPACE_ROOT%\TarkovMapLocatorDesktop" -RequireUserData
if errorlevel 1 exit /b %errorlevel%
endlocal
