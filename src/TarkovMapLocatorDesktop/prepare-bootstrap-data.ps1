[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,
    [switch]$RequireUserData
)

$ErrorActionPreference = 'Stop'

$output = [IO.Path]::GetFullPath($OutputPath)
if (-not (Test-Path -LiteralPath (Join-Path $output 'TarkovToolbox.exe') -PathType Leaf)) {
    throw "Invalid publish output: $output"
}

$profile = Join-Path $env:LOCALAPPDATA 'TarkovMapLocatorDesktop'
$trackerProfile = Join-Path $env:LOCALAPPDATA 'TarkovMapLocator'
$seed = Join-Path $output 'bootstrap-data'
New-Item -ItemType Directory -Force -Path $seed | Out-Null

function Copy-SeedFile {
    param(
        [AllowNull()]
        [string]$Source,
        [Parameter(Mandatory = $true)]
        [string]$Destination,
        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    if ([string]::IsNullOrWhiteSpace($Source) -or -not (Test-Path -LiteralPath $Source -PathType Leaf)) {
        return $false
    }

    Copy-Item -LiteralPath $Source -Destination $Destination -Force
    Write-Host "Included: $Label"
    return $true
}

function Get-Sha256Hex {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $stream = [IO.File]::OpenRead($Path)
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = $sha256.ComputeHash($stream)
        return ([BitConverter]::ToString($bytes)).Replace('-', '')
    }
    finally {
        $sha256.Dispose()
        $stream.Dispose()
    }
}

$marketCache = Join-Path $profile 'market-cache.json'
$hideoutProfitCache = Join-Path $profile 'hideout-profit-cache.json'
$trackerCache = Join-Path $trackerProfile 'item-tracker-cache.json'
$seedMarketCache = Join-Path $seed 'market-cache.json'
$seedHideoutProfitCache = Join-Path $seed 'hideout-profit-cache.json'
$seedTrackerCache = Join-Path $seed 'item-tracker-cache.json'

$hasMarketCache = Copy-SeedFile -Source $marketCache -Destination $seedMarketCache -Label 'market cache'
$hasHideoutProfitCache = Copy-SeedFile -Source $hideoutProfitCache -Destination $seedHideoutProfitCache -Label 'hideout profit cache'
$hasTrackerCache = Copy-SeedFile -Source $trackerCache -Destination $seedTrackerCache -Label 'task item tracker cache'
if ($RequireUserData) {
    if (-not $hasMarketCache) { throw "Market cache was not found: $marketCache" }
    if (-not $hasHideoutProfitCache) { throw "Hideout profit cache was not found: $hideoutProfitCache" }
    if (-not $hasTrackerCache) { throw "Task item tracker cache was not found: $trackerCache" }
}

$seedFiles = @($seedMarketCache, $seedHideoutProfitCache, $seedTrackerCache)
$seedId = (($seedFiles | ForEach-Object { Get-Sha256Hex -Path $_ }) -join ':')
[IO.File]::WriteAllText((Join-Path $seed 'seed-id.txt'), $seedId, [Text.Encoding]::UTF8)
Write-Host "Bootstrap data prepared: $seed"
