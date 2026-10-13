[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$dotnetExecutable = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnetExecutable) {
    $dotnetExecutable = 'C:\Program Files\dotnet\dotnet.exe'
}
$pythonExecutable = Join-Path $repositoryRoot '.venv\Scripts\python.exe'
$ruffExecutable = Join-Path $repositoryRoot '.venv\Scripts\ruff.exe'
if (-not (Test-Path -LiteralPath $dotnetExecutable)) {
    throw 'SDK .NET introuvable. Exécutez scripts\bootstrap.ps1.'
}
if (-not (Test-Path -LiteralPath $pythonExecutable)) {
    throw 'Environnement Python introuvable. Exécutez scripts\bootstrap.ps1.'
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
& $dotnetExecutable build (Join-Path $repositoryRoot 'MouseMotionLab.slnx') `
    --configuration $Configuration
if ($LASTEXITCODE -ne 0) { throw "dotnet build a échoué avec le code $LASTEXITCODE." }
& $dotnetExecutable test (Join-Path $repositoryRoot 'MouseMotionLab.slnx') `
    --configuration $Configuration --no-build
if ($LASTEXITCODE -ne 0) { throw "dotnet test a échoué avec le code $LASTEXITCODE." }
& $ruffExecutable check (Join-Path $repositoryRoot 'ml')
if ($LASTEXITCODE -ne 0) { throw "ruff a échoué avec le code $LASTEXITCODE." }
& $pythonExecutable -m pytest (Join-Path $repositoryRoot 'ml\tests') -q
if ($LASTEXITCODE -ne 0) { throw "pytest a échoué avec le code $LASTEXITCODE." }

Write-Host 'Build et tests terminés.' -ForegroundColor Green

