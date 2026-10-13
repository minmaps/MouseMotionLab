[CmdletBinding()]
param(
    [string]$Dataset = 'data\mousemotion.db',
    [string]$Prepared = 'prepared\default',
    [string]$Artifact = 'artifacts\latest',
    [int]$Epochs = 50,
    [int]$DurationEpochs = 100
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$pythonExecutable = Join-Path $repositoryRoot '.venv\Scripts\python.exe'
if (-not (Test-Path -LiteralPath $pythonExecutable)) {
    throw 'Environnement Python introuvable. Exécutez scripts\bootstrap.ps1.'
}

& $pythonExecutable -m mousemotion.cli prepare `
    --db (Join-Path $repositoryRoot $Dataset) `
    --output (Join-Path $repositoryRoot $Prepared)
if ($LASTEXITCODE -ne 0) { throw "La préparation a échoué avec le code $LASTEXITCODE." }
& $pythonExecutable -m mousemotion.cli train `
    --prepared (Join-Path $repositoryRoot $Prepared) `
    --artifact (Join-Path $repositoryRoot $Artifact) `
    --epochs $Epochs `
    --duration-epochs $DurationEpochs `
    --device auto
if ($LASTEXITCODE -ne 0) { throw "L’entraînement a échoué avec le code $LASTEXITCODE." }
& $pythonExecutable -m mousemotion.cli evaluate `
    --prepared (Join-Path $repositoryRoot $Prepared) `
    --artifact (Join-Path $repositoryRoot $Artifact) `
    --output (Join-Path $repositoryRoot 'artifacts\latest-evaluation') `
    --device auto
if ($LASTEXITCODE -ne 0) { throw "L’évaluation a échoué avec le code $LASTEXITCODE." }

