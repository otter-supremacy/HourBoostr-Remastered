param(
    [ValidateSet('win-x64','linux-x64','linux-arm64')][string]$Runtime = 'win-x64',
    [string]$Version = '1.0.0-preview.1',
    [string]$Dotnet = 'dotnet',
    [string]$PublishRoot
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $PublishRoot) { $PublishRoot = Join-Path $projectRoot 'artifacts/publish' }
$output = Join-Path $PublishRoot $Runtime
& $Dotnet publish (Join-Path $projectRoot 'src/HourBoostr.Remastered/HourBoostr.Remastered.csproj') -c Release -r $Runtime --self-contained true -p:Version=$Version "-p:NuGetLockFilePath=obj/packages.$Runtime.lock.json" -o $output
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
if ($Runtime -eq 'win-x64') {
    & $Dotnet publish (Join-Path $projectRoot 'src/HourBoostr.Desktop/HourBoostr.Desktop.csproj') -c Release -r $Runtime --self-contained true -p:Version=$Version "-p:NuGetLockFilePath=obj/packages.$Runtime.lock.json" -o $output
    if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }
}
python (Join-Path $PSScriptRoot 'package.py') --input $output --runtime $Runtime --version $Version --output (Join-Path $projectRoot 'artifacts/releases')
if ($LASTEXITCODE -ne 0) { throw 'Packaging failed.' }
