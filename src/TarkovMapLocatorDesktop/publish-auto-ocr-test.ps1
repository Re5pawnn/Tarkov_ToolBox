[CmdletBinding()]
param()

$source = Split-Path -Parent $PSCommandPath
$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $source '..\..'))
$output = Join-Path $workspaceRoot 'TarkovMapLocatorDesktop'
& (Join-Path $source 'publish-desktop-package.ps1') -OutputPath $output -RequireUserData
exit $LASTEXITCODE
