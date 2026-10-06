[CmdletBinding()]
param([switch]$Rebuild, [ValidateSet('net9.0-windows', 'net10.0-windows')][string]$Tfm = 'net10.0-windows')

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'AnimeStudio.GUI'
$output = Join-Path $project "bin\Release\$Tfm"
$exe = Join-Path $output 'AnimeStudio.GUI.exe'
if ($Rebuild -or -not (Test-Path (Join-Path $output 'AnimeStudio.AssetExplorer.dll'))) {
    dotnet build $project -c Release -f $Tfm
    if ($LASTEXITCODE -ne 0) { throw 'Studio 编译失败。' }
}
# Explicit launch requested by the person running this script.
Start-Process -FilePath $exe -ArgumentList '--asset-explorer' -WorkingDirectory $output
