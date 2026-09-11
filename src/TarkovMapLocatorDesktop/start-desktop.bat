@echo off
setlocal
cd /d "%~dp0"
dotnet run --project .\TarkovMapLocatorDesktop.csproj
endlocal
