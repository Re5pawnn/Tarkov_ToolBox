[CmdletBinding()]
param(
    [switch]$WhatIf,
    [string]$LogPath
)

$ErrorActionPreference = 'Stop'

$source = $PSScriptRoot
$root = [IO.Path]::GetFullPath((Join-Path $source '..\..'))
$project = Join-Path $source 'TarkovMapLocatorDesktop.csproj'
$installerScript = Join-Path $source 'installer.iss'
$portablePublisher = Join-Path $source 'publish-desktop-package.ps1'
$signArtifact = Join-Path $source 'sign-artifact.ps1'
$releaseDirectory = Join-Path $root 'release'
$installerInput = Join-Path $releaseDirectory ('.installer-input-' + $PID)
$componentIsolationTest = Join-Path $source 'tests\Test-InstallerComponentIsolation.ps1'

function Write-Utf8File {
    param([string]$Path, [string]$Content)
    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

function Find-InnoCompiler {
    $paths = @(
        (Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    $compiler = $paths | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if ($compiler) { return $compiler }

    $winget = Get-Command winget.exe -ErrorAction SilentlyContinue
    if (-not $winget) {
        throw '未找到 Inno Setup 编译器，也无法使用 winget 自动安装。请先安装 Inno Setup 6。'
    }

    Write-Host '未找到 Inno Setup，正在自动安装…' -ForegroundColor Yellow
    & $winget.Source install --id JRSoftware.InnoSetup --exact --silent --accept-source-agreements --accept-package-agreements
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup 自动安装失败，退出码：$LASTEXITCODE" }

    $compiler = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if (-not $compiler) { throw 'Inno Setup 已安装，但未找到 ISCC.exe。' }
    return $compiler
}

function Get-NextPatchVersion {
    $projectText = [IO.File]::ReadAllText($project)
    $match = [regex]::Match($projectText, '<Version>(?<version>\d+\.\d+\.\d+)</Version>')
    if (-not $match.Success) { throw '无法从项目文件读取三段式版本号。' }
    $parts = $match.Groups['version'].Value.Split('.') | ForEach-Object { [int]$_ }
    return "$($parts[0]).$($parts[1]).$($parts[2] + 1)"
}

function Set-PackageVersion {
    param([string]$Version)

    $projectText = [IO.File]::ReadAllText($project)
    $projectText = ([regex]::new('<Version>\d+\.\d+\.\d+</Version>')).Replace($projectText, "<Version>$Version</Version>", 1)
    $projectText = ([regex]::new('<AssemblyVersion>\d+\.\d+\.\d+\.0</AssemblyVersion>')).Replace($projectText, "<AssemblyVersion>$Version.0</AssemblyVersion>", 1)
    $projectText = ([regex]::new('<FileVersion>\d+\.\d+\.\d+\.0</FileVersion>')).Replace($projectText, "<FileVersion>$Version.0</FileVersion>", 1)
    Write-Utf8File -Path $project -Content $projectText

    $issText = [IO.File]::ReadAllText($installerScript)
    $issText = ([regex]::new('#define MyAppVersion "\d+\.\d+\.\d+"')).Replace($issText, "#define MyAppVersion `"$Version`"", 1)
    Write-Utf8File -Path $installerScript -Content $issText
}

function Assert-ArtifactVersion {
    param([string]$Path, [string]$ExpectedVersion, [string]$Label)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label 未生成：$Path"
    }
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    if ($info.FileVersion -notlike "$ExpectedVersion*") {
        throw "$Label 版本校验失败：期望 $ExpectedVersion，实际 $($info.FileVersion)"
    }
    return $info
}

function Assert-InstallerComponentIsolation {
    $issText = [IO.File]::ReadAllText($installerScript)
    $expectedComponents = @('core', 'market', 'taskitems', 'memo', 'tasktracking', 'ingameprice', 'screenfilter', 'teamsync', 'mobilemap', 'utilities')
    $componentSection = [regex]::Match($issText, '(?ms)^\[Components\]\s*(?<body>.*?)(?=^\[)')
    if (-not $componentSection.Success) {
        throw '安装器缺少 Components 配置。'
    }

    $actualComponents = @(
        [regex]::Matches($componentSection.Groups['body'].Value, 'Name:\s*"(?<name>[^"]+)"') |
            ForEach-Object { $_.Groups['name'].Value }
    )
    if (($actualComponents -join '|') -ne ($expectedComponents -join '|')) {
        throw "安装器组件清单发生变化，必须同步扩展隔离测试。当前：$($actualComponents -join ', ')"
    }

    $requiredFragments = @(
        'Excludes: "\Modules\*,\bootstrap-data\*,\task-data\task-game-id-map.json,\task-data\task-tracking-catalog.json"',
        'Type: filesandordirs; Name: "{app}\Modules"',
        'Type: filesandordirs; Name: "{app}\assets\weapon-build"',
        'Type: filesandordirs; Name: "{app}\bootstrap-data"',
        'Type: files; Name: "{app}\task-data\task-game-id-map.json"',
        'Type: files; Name: "{app}\task-data\task-tracking-catalog.json"',
        'Source: "{#MyAppSource}\Modules\Market\*"; DestDir: "{app}\Modules\Market"; Components: market;',
        'Source: "{#MyAppSource}\Modules\TaskItems\*"; DestDir: "{app}\Modules\TaskItems"; Components: taskitems;',
        'Source: "{#MyAppSource}\Modules\Memo\*"; DestDir: "{app}\Modules\Memo"; Components: memo;',
        'Source: "{#MyAppSource}\Modules\TaskTracking\*"; DestDir: "{app}\Modules\TaskTracking"; Components: tasktracking;',
        'Source: "{#MyAppSource}\Modules\InGamePrice\*"; DestDir: "{app}\Modules\InGamePrice"; Components: ingameprice;',
        'Source: "{#MyAppSource}\Modules\ScreenFilter\*"; DestDir: "{app}\Modules\ScreenFilter"; Components: screenfilter;',
        'Source: "{#MyAppSource}\Modules\TeamSync\*"; DestDir: "{app}\Modules\TeamSync"; Components: teamsync;',
        'Source: "{#MyAppSource}\Modules\MobileMap\*"; DestDir: "{app}\Modules\MobileMap"; Components: mobilemap;',
        'Source: "{#MyAppSource}\Modules\Utilities\*"; DestDir: "{app}\Modules\Utilities"; Components: utilities;',
        'Source: "{#MyAppSource}\bootstrap-data\seed-id.txt"; DestDir: "{app}\bootstrap-data"; Components: market taskitems memo ingameprice utilities;',
        'Source: "{#MyAppSource}\bootstrap-data\market-cache.json"; DestDir: "{app}\bootstrap-data"; Components: market taskitems memo ingameprice utilities;',
        'Source: "{#MyAppSource}\bootstrap-data\item-tracker-cache.json"; DestDir: "{app}\bootstrap-data"; Components: taskitems;',
        'Source: "{#MyAppSource}\bootstrap-data\hideout-profit-cache.json"; DestDir: "{app}\bootstrap-data"; Components: utilities;'
    )

    foreach ($fragment in $requiredFragments) {
        if (-not $issText.Contains($fragment)) {
            throw "安装器组件隔离规则缺失：$fragment"
        }
    }

    if ($actualComponents | Where-Object { $_.Contains('\') }) {
        throw '可选功能不得使用嵌套组件，否则同级功能可能被联动安装。'
    }
}

$exitCode = 0
$transcriptStarted = $false
$versionApplied = $false
$originalProjectBytes = $null
$originalInstallerBytes = $null

try {
    if (-not [string]::IsNullOrWhiteSpace($LogPath)) {
        $resolvedLogPath = [IO.Path]::GetFullPath($LogPath)
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resolvedLogPath)) | Out-Null
        Start-Transcript -Path $resolvedLogPath -Force | Out-Null
        $transcriptStarted = $true
    }

    Assert-InstallerComponentIsolation
    $nextVersion = Get-NextPatchVersion
    $setupPath = Join-Path $releaseDirectory "TarkovToolbox-Setup-$nextVersion.exe"

    if ($WhatIf) {
        Write-Host "预演成功：将生成 v$nextVersion" -ForegroundColor Cyan
        Write-Host "便携版：$(Join-Path $root 'TarkovToolbox')"
        Write-Host "安装包：$setupPath"
    }
    else {
        $originalProjectBytes = [IO.File]::ReadAllBytes($project)
        $originalInstallerBytes = [IO.File]::ReadAllBytes($installerScript)

        Set-PackageVersion -Version $nextVersion
        $versionApplied = $true
        Write-Host "开始生成 塔科夫工具箱 v$nextVersion …" -ForegroundColor Cyan

        # Always validate source first. The published package repeats its own executable
        # self-test after seed data is copied, so both source and final output are checked.
        & dotnet run --project (Join-Path $source 'tests\TarkovMapLocatorDesktop.RegressionTests\TarkovMapLocatorDesktop.RegressionTests.csproj') -c Release
        if ($LASTEXITCODE -ne 0) { throw "回归检查失败，退出码：$LASTEXITCODE" }

        # Build and validate isolated installer input first. A compiler failure must
        # not replace the currently working portable release.
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $portablePublisher -OutputPath $installerInput -RequireUserData
        if ($LASTEXITCODE -ne 0) { throw "安装输入发布失败，退出码：$LASTEXITCODE" }

        $compiler = Find-InnoCompiler
        & $compiler "/DMyAppVersion=$nextVersion" "/DMyAppSource=$installerInput" $installerScript
        if ($LASTEXITCODE -ne 0) { throw "安装包编译失败，退出码：$LASTEXITCODE" }

        $setupInfo = Assert-ArtifactVersion -Path $setupPath -ExpectedVersion $nextVersion -Label '安装包'

        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $componentIsolationTest -SetupPath $setupPath
        if ($LASTEXITCODE -ne 0) { throw "安装器组件隔离测试失败，退出码：$LASTEXITCODE" }

        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $signArtifact -Path $setupPath
        if ($LASTEXITCODE -ne 0) { throw "安装包代码签名失败，退出码：$LASTEXITCODE" }

        # Only after the installer is complete do we replace the normal portable
        # folder. The publisher retains its own previous-version rollback.
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $portablePublisher -RequireUserData
        if ($LASTEXITCODE -ne 0) { throw "便携版发布失败，退出码：$LASTEXITCODE" }

        $portableExe = Join-Path $root 'TarkovToolbox\TarkovToolbox.exe'
        Assert-ArtifactVersion -Path $portableExe -ExpectedVersion $nextVersion -Label '便携版' | Out-Null
        $setupInfo = Assert-ArtifactVersion -Path $setupPath -ExpectedVersion $nextVersion -Label '安装包'

        $hash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash
        Write-Host ''
        Write-Host "打包完成：$setupPath" -ForegroundColor Green
        Write-Host "版本：$($setupInfo.FileVersion)"
        Write-Host "SHA-256：$hash"
    }
}
catch {
    $exitCode = 1

    if ($null -ne $setupPath -and (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
        Remove-Item -LiteralPath $setupPath -Force -ErrorAction SilentlyContinue
    }

    if ($versionApplied -and $null -ne $originalProjectBytes -and $null -ne $originalInstallerBytes) {
        try {
            [IO.File]::WriteAllBytes($project, $originalProjectBytes)
            [IO.File]::WriteAllBytes($installerScript, $originalInstallerBytes)
            Write-Host '构建失败，项目版本号已恢复。' -ForegroundColor Yellow
        }
        catch {
            Write-Host "恢复项目版本号失败：$($_.Exception.Message)" -ForegroundColor Red
        }
    }

    Write-Host ''
    Write-Host '打包失败：' -ForegroundColor Red
    Write-Host $_.Exception.ToString() -ForegroundColor Red
}
finally {
    if (Test-Path -LiteralPath $installerInput) {
        Remove-Item -LiteralPath $installerInput -Recurse -Force -ErrorAction SilentlyContinue
    }
    if ($transcriptStarted) {
        try { Stop-Transcript | Out-Null } catch { }
    }
}

exit $exitCode
