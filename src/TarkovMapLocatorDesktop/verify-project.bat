@echo off
setlocal
cd /d "%~dp0"

dotnet build .\TarkovMapLocatorDesktop.csproj -c Release
if errorlevel 1 exit /b %errorlevel%

dotnet run --project .\tests\TarkovMapLocatorDesktop.RegressionTests\TarkovMapLocatorDesktop.RegressionTests.csproj -c Release --no-restore
if errorlevel 1 exit /b %errorlevel%

powershell -NoProfile -ExecutionPolicy Bypass -Command "$p=Start-Process -FilePath '.\bin\Release\net10.0-windows\TarkovToolbox.exe' -ArgumentList '--self-test' -PassThru -Wait; exit $p.ExitCode"
if errorlevel 1 exit /b %errorlevel%

echo Verification completed successfully.
endlocal
