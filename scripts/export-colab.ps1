[CmdletBinding()]
param(
    [string]$Dataset = 'data\mousemotion.db',
    [string]$Output = 'artifacts\colab-export'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$pythonExecutable = Join-Path $repositoryRoot '.venv\Scripts\python.exe'
if (-not (Test-Path -LiteralPath $pythonExecutable)) {
    throw 'Environnement Python introuvable. Exécutez scripts\bootstrap.ps1.'
}
& $pythonExecutable (Join-Path $PSScriptRoot 'export_colab.py') `
    --database (Join-Path $repositoryRoot $Dataset) `
    --output (Join-Path $repositoryRoot $Output)
if ($LASTEXITCODE -ne 0) { throw "L'export Colab a échoué avec le code $LASTEXITCODE." }
