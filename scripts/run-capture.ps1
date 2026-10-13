[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$dotnetExecutable = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnetExecutable) {
    $dotnetExecutable = 'C:\Program Files\dotnet\dotnet.exe'
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
& $dotnetExecutable run `
    --project (Join-Path $repositoryRoot 'src\MouseMotionLab.Capture\MouseMotionLab.Capture.csproj') `
    --configuration Release
if ($LASTEXITCODE -ne 0) { throw "MouseMotionLab.Capture s’est arrêté avec le code $LASTEXITCODE." }

