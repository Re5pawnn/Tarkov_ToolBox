param(
    [string]$Configuration = "Release",
    [string]$Platform = "x64",
    [string]$OutputDir = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $PSScriptRoot "update-package"
}

$appOutput = Join-Path $repoRoot "src\TarkovMapLocator.App\bin\$Platform\$Configuration\net10.0-windows10.0.19041.0\win-$Platform"
$updaterPublish = Join-Path $repoRoot "src\TarkovMapLocator.Updater\bin\$Configuration\net10.0-windows10.0.17763.0\win-$Platform\publish"
$payloadDir = Join-Path $OutputDir "payload"

dotnet build (Join-Path $repoRoot "TarkovMapLocator.sln") -c $Configuration -p:Platform=$Platform
dotnet publish (Join-Path $repoRoot "src\TarkovMapLocator.Updater\TarkovMapLocator.Updater.csproj") `
    -c $Configuration `
    -r "win-$Platform" `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true

if (Test-Path $OutputDir) {
    Remove-Item -LiteralPath $OutputDir -Recurse -Force
}

New-Item -ItemType Directory -Path $payloadDir | Out-Null
Copy-Item -Path (Join-Path $appOutput "*") -Destination $payloadDir -Recurse -Force
Copy-Item -Path (Join-Path $updaterPublish "TarkovMapLocatorUpdater.exe") -Destination (Join-Path $OutputDir "TarkovMapLocatorUpdater.exe") -Force

Write-Host "Update package created:"
Write-Host $OutputDir
