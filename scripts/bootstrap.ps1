[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$dotnetExecutable = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnetExecutable) {
    $installedDotnet = 'C:\Program Files\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $installedDotnet) {
        $dotnetExecutable = $installedDotnet
    }
    else {
        Write-Host 'Installation du SDK .NET 10 (Windows peut demander une élévation UAC)…'
        winget install --id Microsoft.DotNet.SDK.10 --exact `
            --accept-package-agreements --accept-source-agreements
        if ($LASTEXITCODE -ne 0) { throw "winget a échoué avec le code $LASTEXITCODE." }
        if (-not (Test-Path -LiteralPath $installedDotnet)) {
            throw 'Le SDK .NET 10 ne semble pas avoir été installé.'
        }
        $dotnetExecutable = $installedDotnet
    }
}

$virtualEnvironment = Join-Path $repositoryRoot '.venv'
$pythonExecutable = Join-Path $virtualEnvironment 'Scripts\python.exe'
if (-not (Test-Path -LiteralPath $pythonExecutable)) {
    python -m venv $virtualEnvironment
}

& $pythonExecutable -m pip install --upgrade pip
if ($LASTEXITCODE -ne 0) { throw "La mise à jour de pip a échoué avec le code $LASTEXITCODE." }
if (Get-Command nvidia-smi -ErrorAction SilentlyContinue) {
    Write-Host 'Installation de PyTorch 2.14.1 CUDA 13.0…'
    & $pythonExecutable -m pip install 'torch==2.14.1+cu130' `
        --index-url 'https://download.pytorch.org/whl/cu130'
    if ($LASTEXITCODE -ne 0) { throw "L’installation de PyTorch CUDA a échoué avec le code $LASTEXITCODE." }
}
else {
    Write-Warning 'Aucun GPU NVIDIA détecté : installation de la roue PyTorch CPU.'
    & $pythonExecutable -m pip install 'torch==2.14.1'
    if ($LASTEXITCODE -ne 0) { throw "L’installation de PyTorch CPU a échoué avec le code $LASTEXITCODE." }
}
& $pythonExecutable -m pip install --editable "$(Join-Path $repositoryRoot 'ml')[dev]"
if ($LASTEXITCODE -ne 0) { throw "L’installation du package Python a échoué avec le code $LASTEXITCODE." }

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
& $dotnetExecutable restore (Join-Path $repositoryRoot 'MouseMotionLab.slnx')
if ($LASTEXITCODE -ne 0) { throw "dotnet restore a échoué avec le code $LASTEXITCODE." }

Write-Host ''
Write-Host 'Environnement prêt.' -ForegroundColor Green
Write-Host "  .NET : $dotnetExecutable"
Write-Host "  Python : $pythonExecutable"

