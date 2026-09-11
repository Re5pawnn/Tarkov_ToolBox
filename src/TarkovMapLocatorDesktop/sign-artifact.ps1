[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$Path)

$ErrorActionPreference = 'Stop'
$certificate = $env:TARKOV_SIGN_PFX
if ([string]::IsNullOrWhiteSpace($certificate)) { exit 0 }
if (-not (Test-Path -LiteralPath $certificate -PathType Leaf)) {
    throw "Code-signing certificate does not exist: $certificate"
}

$signTool = Get-Command signtool.exe -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source
if (-not $signTool) {
    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path -LiteralPath $kitsRoot -PathType Container) {
        $signTool = Get-ChildItem -LiteralPath $kitsRoot -Filter signtool.exe -File -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1 -ExpandProperty FullName
    }
}
if (-not $signTool) { throw 'A signing certificate is configured, but Windows SDK signtool.exe was not found.' }

$arguments = @('sign', '/fd', 'SHA256', '/td', 'SHA256', '/tr', 'http://timestamp.digicert.com', '/f', $certificate)
if (-not [string]::IsNullOrWhiteSpace($env:TARKOV_SIGN_PASSWORD)) {
    $arguments += @('/p', $env:TARKOV_SIGN_PASSWORD)
}
$arguments += [IO.Path]::GetFullPath($Path)
& $signTool @arguments
if ($LASTEXITCODE -ne 0) { throw "Code signing failed with exit code $LASTEXITCODE." }
