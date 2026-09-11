[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SetupPath
)

$ErrorActionPreference = 'Stop'

$setup = [IO.Path]::GetFullPath($SetupPath)
if (-not (Test-Path -LiteralPath $setup -PathType Leaf)) {
    throw "Setup package not found: $setup"
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('TarkovMapLocator-installer-components-' + [guid]::NewGuid().ToString('N'))
$installPath = Join-Path $testRoot 'app'
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)

if (-not $resolvedTestRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
    -not ([IO.Path]::GetFileName($resolvedTestRoot)).StartsWith('TarkovMapLocator-installer-components-', [StringComparison]::Ordinal)) {
    throw "Unsafe test directory rejected: $resolvedTestRoot"
}

$allModules = @('InGamePrice', 'Market', 'Memo', 'MobileMap', 'ScreenFilter', 'TaskItems', 'TaskTracking', 'TeamSync', 'Utilities')
$allBootstrapFiles = @('hideout-profit-cache.json', 'item-tracker-cache.json', 'market-cache.json', 'seed-id.txt')
$cases = @(
    @{ Name = 'core'; Component = $null; ExpectedModules = @(); ExpectWeaponAssets = $false; ExpectedBootstrap = @(); ExpectTaskTrackingData = $false },
    @{ Name = 'market'; Component = 'market'; ExpectedModules = @('Market'); ExpectWeaponAssets = $false; ExpectedBootstrap = @('market-cache.json', 'seed-id.txt'); ExpectTaskTrackingData = $false },
    @{ Name = 'taskitems'; Component = 'taskitems'; ExpectedModules = @('TaskItems'); ExpectWeaponAssets = $false; ExpectedBootstrap = @('item-tracker-cache.json', 'market-cache.json', 'seed-id.txt'); ExpectTaskTrackingData = $false },
    @{ Name = 'memo'; Component = 'memo'; ExpectedModules = @('Memo'); ExpectWeaponAssets = $false; ExpectedBootstrap = @('market-cache.json', 'seed-id.txt'); ExpectTaskTrackingData = $false },
    @{ Name = 'tasktracking'; Component = 'tasktracking'; ExpectedModules = @('TaskTracking'); ExpectWeaponAssets = $false; ExpectedBootstrap = @(); ExpectTaskTrackingData = $true },
    @{ Name = 'ingameprice'; Component = 'ingameprice'; ExpectedModules = @('InGamePrice'); ExpectWeaponAssets = $false; ExpectedBootstrap = @('market-cache.json', 'seed-id.txt'); ExpectTaskTrackingData = $false },
    @{ Name = 'screenfilter'; Component = 'screenfilter'; ExpectedModules = @('ScreenFilter'); ExpectWeaponAssets = $false; ExpectedBootstrap = @(); ExpectTaskTrackingData = $false },
    @{ Name = 'teamsync'; Component = 'teamsync'; ExpectedModules = @('TeamSync'); ExpectWeaponAssets = $false; ExpectedBootstrap = @(); ExpectTaskTrackingData = $false },
    @{ Name = 'mobilemap'; Component = 'mobilemap'; ExpectedModules = @('MobileMap'); ExpectWeaponAssets = $false; ExpectedBootstrap = @(); ExpectTaskTrackingData = $false },
    @{ Name = 'utilities'; Component = 'utilities'; ExpectedModules = @('Utilities'); ExpectWeaponAssets = $false; ExpectedBootstrap = @('hideout-profit-cache.json', 'market-cache.json', 'seed-id.txt'); ExpectTaskTrackingData = $false },
    @{ Name = 'full'; Type = 'full'; Component = $null; ExpectedModules = $allModules; ExpectWeaponAssets = $false; ExpectedBootstrap = $allBootstrapFiles; ExpectTaskTrackingData = $true },
    @{ Name = 'core-after-full'; Component = $null; ExpectedModules = @(); ExpectWeaponAssets = $false; ExpectedBootstrap = @(); ExpectTaskTrackingData = $false }
)

function Invoke-InstallerCase {
    param([hashtable]$Case)

    [IO.Directory]::CreateDirectory($installPath) | Out-Null
    $installType = if ($Case.Type) { $Case.Type } else { 'custom' }
    $components = if ($null -eq $Case.Component) { 'core' } else { "core,$($Case.Component)" }
    $logPath = Join-Path $testRoot ("$($Case.Name).log")
    $arguments = @(
        '/CURRENTUSER',
        '/VERYSILENT',
        '/SUPPRESSMSGBOXES',
        '/NORESTART',
        "/DIR=$installPath",
        "/TYPE=$installType",
        "/LOG=$logPath"
    )
    if ($installType -eq 'custom') {
        $arguments += "/COMPONENTS=$components"
    }

    $install = Start-Process -FilePath $setup -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    if ($install.ExitCode -ne 0) {
        throw "$($Case.Name) install failed with exit code $($install.ExitCode)"
    }

    $moduleRoot = Join-Path $installPath 'Modules'
    $actualModules = @(
        Get-ChildItem -LiteralPath $moduleRoot -Directory -ErrorAction SilentlyContinue |
            Sort-Object Name |
            Select-Object -ExpandProperty Name
    )
    $expectedModules = @($Case.ExpectedModules | Sort-Object)

    if (($actualModules -join '|') -ne ($expectedModules -join '|')) {
        throw "$($Case.Name) component leak: expected [$($expectedModules -join ', ')], actual [$($actualModules -join ', ')]"
    }

    $hasWeaponAssets = Test-Path -LiteralPath (Join-Path $installPath 'assets\weapon-build') -PathType Container
    if ($hasWeaponAssets -ne $Case.ExpectWeaponAssets) {
        throw "$($Case.Name) weapon asset mismatch: expected $($Case.ExpectWeaponAssets), actual $hasWeaponAssets"
    }

    $bootstrapRoot = Join-Path $installPath 'bootstrap-data'
    $actualBootstrap = @(
        Get-ChildItem -LiteralPath $bootstrapRoot -File -ErrorAction SilentlyContinue |
            Sort-Object Name |
            Select-Object -ExpandProperty Name
    )
    $expectedBootstrap = @($Case.ExpectedBootstrap | Sort-Object)
    if (($actualBootstrap -join '|') -ne ($expectedBootstrap -join '|')) {
        throw "$($Case.Name) bootstrap-data leak: expected [$($expectedBootstrap -join ', ')], actual [$($actualBootstrap -join ', ')]"
    }

    $trackingFiles = @('task-game-id-map.json', 'task-tracking-catalog.json')
    $actualTrackingFiles = @(
        $trackingFiles | Where-Object { Test-Path -LiteralPath (Join-Path $installPath "Modules\TaskTracking\task-data\$_") -PathType Leaf }
    )
    $expectedTrackingFiles = if ($Case.ExpectTaskTrackingData) { $trackingFiles } else { @() }
    if (($actualTrackingFiles -join '|') -ne ($expectedTrackingFiles -join '|')) {
        throw "$($Case.Name) task-tracking data mismatch: expected [$($expectedTrackingFiles -join ', ')], actual [$($actualTrackingFiles -join ', ')]"
    }
    $legacyTrackingFiles = @(
        $trackingFiles | Where-Object { Test-Path -LiteralPath (Join-Path $installPath "task-data\$_") -PathType Leaf }
    )
    if ($legacyTrackingFiles.Count -gt 0) {
        throw "$($Case.Name) retained legacy task-tracking data in the core task-data directory: $($legacyTrackingFiles -join ', ')"
    }

    $selfTest = Start-Process -FilePath (Join-Path $installPath 'TarkovToolbox.exe') -ArgumentList '--self-test' -WindowStyle Hidden -Wait -PassThru
    if ($selfTest.ExitCode -ne 0) {
        throw "$($Case.Name) self-test failed with exit code $($selfTest.ExitCode)"
    }

    Write-Host "Component isolation passed: $($Case.Name)" -ForegroundColor Green
}

try {
    [IO.Directory]::CreateDirectory($resolvedTestRoot) | Out-Null
    foreach ($case in $cases) {
        Invoke-InstallerCase -Case $case
    }
}
finally {
    $uninstaller = Join-Path $installPath 'unins000.exe'
    if (Test-Path -LiteralPath $uninstaller -PathType Leaf) {
        try {
            Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -WindowStyle Hidden -Wait | Out-Null
        }
        catch {
            Write-Warning "Test uninstall failed: $($_.Exception.Message)"
        }
    }

    if (Test-Path -LiteralPath $resolvedTestRoot) {
        [IO.Directory]::Delete($resolvedTestRoot, $true)
    }
}

Write-Host 'All installer component isolation tests passed.' -ForegroundColor Green
