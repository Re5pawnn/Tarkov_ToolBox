[CmdletBinding()]
param(
    [string]$OutputPath = '',
    [switch]$RequireUserData
)

$ErrorActionPreference = 'Stop'

$source = $PSScriptRoot
$project = Join-Path $source 'TarkovMapLocatorDesktop.csproj'
$prepareSeed = Join-Path $source 'prepare-bootstrap-data.ps1'
$signArtifact = Join-Path $source 'sign-artifact.ps1'
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    # Windows PowerShell evaluates parameter defaults before $PSScriptRoot is
    # initialized.  Resolve the default here so double-click and .bat publish
    # entry points work on both Windows PowerShell and PowerShell 7.
    $OutputPath = Join-Path ([IO.Path]::GetFullPath((Join-Path $source '..\..'))) 'TarkovToolbox'
}
$output = [IO.Path]::GetFullPath($OutputPath)
$parent = Split-Path -Parent $output
$leaf = Split-Path -Leaf $output
$stage = Join-Path $parent (".$leaf.stage-$PID-" + [Guid]::NewGuid().ToString('N'))
$previous = Join-Path $parent ".$leaf.previous"
$swapped = $false
$movedPrevious = $false

function Invoke-PublishedSelfTest {
    param([string]$Directory)
    $exe = Join-Path $Directory 'TarkovToolbox.exe'
    $selfTest = Start-Process -FilePath $exe -ArgumentList '--self-test' -PassThru -Wait
    if ($selfTest.ExitCode -ne 0) {
        throw "Published desktop self-test failed with exit code $($selfTest.ExitCode)."
    }
}

function Assert-PublishedVersion {
    param([string]$Directory)

    $projectText = [IO.File]::ReadAllText($project)
    $match = [regex]::Match($projectText, '<Version>(?<version>\d+\.\d+\.\d+)</Version>')
    if (-not $match.Success) { throw 'Cannot read the expected package version from the project.' }
    $exe = Join-Path $Directory 'TarkovToolbox.exe'
    $actual = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion
    if ($actual -notlike "$($match.Groups['version'].Value)*") {
        throw "Published desktop version mismatch. Expected $($match.Groups['version'].Value), actual $actual."
    }
}

function Copy-AppLocalVcRuntime {
    param([string]$Directory)

    $requiredFiles = @(
        'msvcp140.dll',
        'msvcp140_1.dll',
        'vcruntime140.dll',
        'vcruntime140_1.dll'
    )
    $sourceDirectories = @(
        (Join-Path $env:WINDIR 'System32')
    )

    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswhere -PathType Leaf) {
        $installations = & $vswhere -products * -property installationPath
        foreach ($installation in $installations) {
            $redistRoot = Join-Path $installation 'VC\Redist\MSVC'
            if (-not (Test-Path -LiteralPath $redistRoot -PathType Container)) { continue }
            $crt = Get-ChildItem -LiteralPath $redistRoot -Directory |
                Sort-Object Name -Descending |
                ForEach-Object { Join-Path $_.FullName 'x64\Microsoft.VC143.CRT' } |
                Where-Object { Test-Path -LiteralPath $_ -PathType Container } |
                Select-Object -First 1
            if ($crt) { $sourceDirectories = @($crt) + $sourceDirectories }
        }
    }

    foreach ($fileName in $requiredFiles) {
        $sourceFile = $sourceDirectories |
            ForEach-Object { Join-Path $_ $fileName } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
            Select-Object -First 1
        if (-not $sourceFile) {
            throw "Cannot find $fileName. A zero-prerequisite desktop package cannot be created."
        }
        Copy-Item -LiteralPath $sourceFile -Destination (Join-Path $Directory $fileName) -Force
    }
}

function Stop-CurrentDesktop {
    param([string]$Directory)
    $exe = Join-Path $Directory 'TarkovToolbox.exe'
    $recovery = Join-Path $env:LOCALAPPDATA 'TarkovMapLocatorDesktop\gamma-recovery'
    $hasRecovery = (Test-Path -LiteralPath $recovery) -and [bool](Get-ChildItem -LiteralPath $recovery -Filter 'gamma-recovery-*.json' -File -ErrorAction SilentlyContinue | Select-Object -First 1)
    if ($hasRecovery -and (Test-Path -LiteralPath $exe -PathType Leaf)) {
        $restore = Start-Process -FilePath $exe -ArgumentList '--restore-gamma' -WindowStyle Hidden -PassThru
        if (-not $restore.WaitForExit(8000)) { Stop-Process -Id $restore.Id -Force -ErrorAction SilentlyContinue }
    }
    Get-Process -Name 'TarkovToolbox','TarkovMapLocatorDesktop','TarkovMapLocatorAutoOcrTest' -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 350
}

try {
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    & dotnet run --project (Join-Path $source 'tests\TarkovMapLocatorDesktop.RegressionTests\TarkovMapLocatorDesktop.RegressionTests.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { throw "Regression checks failed with exit code $LASTEXITCODE." }
    & dotnet publish $project -c Release -r win-x64 --self-contained true -p:SatelliteResourceLanguages=zh-Hans -o $stage
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    Copy-AppLocalVcRuntime -Directory $stage

    $seedArguments = @{ OutputPath = $stage }
    if ($RequireUserData) { $seedArguments.RequireUserData = $true }
    & $prepareSeed @seedArguments

    $required = @(
        'TarkovToolbox.exe',
        'msvcp140.dll',
        'msvcp140_1.dll',
        'vcruntime140.dll',
        'vcruntime140_1.dll',
        'bootstrap-data\market-cache.json',
        'bootstrap-data\hideout-profit-cache.json',
        'bootstrap-data\item-tracker-cache.json',
        'bootstrap-data\seed-id.txt',
        'TarkovMapLocator.ModuleContracts.dll',
        'Modules\Market\TarkovMapLocator.Modules.Market.dll',
        'Modules\Market\module.json',
        'Modules\TaskItems\TarkovMapLocator.Modules.TaskItems.dll',
        'Modules\TaskItems\module.json',
        'Modules\Memo\TarkovMapLocator.Modules.Memo.dll',
        'Modules\Memo\module.json',
        'Modules\TaskTracking\TarkovMapLocator.Modules.TaskTracking.dll',
        'Modules\TaskTracking\module.json',
        'Modules\TaskTracking\task-data\task-game-id-map.json',
        'Modules\TaskTracking\task-data\task-tracking-catalog.json',
        'Modules\InGamePrice\TarkovMapLocator.Modules.InGamePrice.dll',
        'Modules\InGamePrice\OpenCvSharp.dll',
        'Modules\InGamePrice\onnxruntime.dll',
        'Modules\InGamePrice\assets\in-game-price\ppocr\ch_PP-OCRv4_det_infer.onnx',
        'Modules\InGamePrice\module.json',
        'Modules\ScreenFilter\TarkovMapLocator.Modules.ScreenFilter.dll',
        'Modules\ScreenFilter\module.json',
        'Modules\TeamSync\TarkovMapLocator.Modules.TeamSync.dll',
        'Modules\TeamSync\module.json',
        'Modules\MobileMap\TarkovMapLocator.Modules.MobileMap.dll',
        'Modules\MobileMap\module.json',
        'Modules\MobileMap\web\index.html',
        'Modules\MobileMap\web\app.css',
        'Modules\MobileMap\web\app.js',
        'Modules\Utilities\TarkovMapLocator.Modules.Utilities.dll',
        'Modules\Utilities\module.json',
        'assets\maps\native-cache\customs.png',
        'README.md'
    )
    $missing = $required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $stage $_) -PathType Leaf) }
    if ($missing.Count -gt 0) { throw "Published desktop package is incomplete: $($missing -join ', ')" }
    $forbidden = @(
        'Tesseract.dll',
        'x64\leptonica-1.82.0.dll',
        'x64\tesseract50.dll',
        'assets\in-game-price\template.png',
        'assets\in-game-price\tessdata\chi_sim.traineddata',
        'bootstrap-data\in-game-price\template.png',
        'task-data\task-game-id-map.json',
        'task-data\task-tracking-catalog.json'
    )
    $unexpected = $forbidden | Where-Object { Test-Path -LiteralPath (Join-Path $stage $_) -PathType Leaf }
    if ($unexpected.Count -gt 0) { throw "Published desktop package still contains retired OCR files: $($unexpected -join ', ')" }
    Assert-PublishedVersion -Directory $stage
    Invoke-PublishedSelfTest -Directory $stage
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $signArtifact -Path (Join-Path $stage 'TarkovToolbox.exe')
    if ($LASTEXITCODE -ne 0) { throw "Desktop executable signing failed with exit code $LASTEXITCODE." }

    # The previous release is kept intact until the staged package has built,
    # received its bootstrap data, and passed its own executable self-test.
    Stop-CurrentDesktop -Directory $output
    if (Test-Path -LiteralPath $previous) { Remove-Item -LiteralPath $previous -Recurse -Force }
    if (Test-Path -LiteralPath $output) {
        Move-Item -LiteralPath $output -Destination $previous
        $movedPrevious = $true
    }
    try {
        Move-Item -LiteralPath $stage -Destination $output
        $swapped = $true
    }
    catch {
        if ($movedPrevious -and -not (Test-Path -LiteralPath $output) -and (Test-Path -LiteralPath $previous)) {
            Move-Item -LiteralPath $previous -Destination $output
            $movedPrevious = $false
        }
        throw
    }

    if (Test-Path -LiteralPath $previous) { Remove-Item -LiteralPath $previous -Recurse -Force }
    Write-Host "Desktop package published safely to: $output"
}
finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue }
    if (-not $swapped -and $movedPrevious -and -not (Test-Path -LiteralPath $output) -and (Test-Path -LiteralPath $previous)) {
        Move-Item -LiteralPath $previous -Destination $output -ErrorAction SilentlyContinue
    }
}
