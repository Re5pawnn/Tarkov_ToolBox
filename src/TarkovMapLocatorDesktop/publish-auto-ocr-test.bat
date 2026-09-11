@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish-auto-ocr-test.ps1"
exit /b %ERRORLEVEL%
