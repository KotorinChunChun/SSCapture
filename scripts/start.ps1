[CmdletBinding()]
param([switch]$Settings)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$executable = Join-Path $projectRoot 'artifacts\app\SSCapture.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw '先に pwsh -File scripts\build.ps1 を実行してください。' }
$launch = @{ FilePath = $executable; WorkingDirectory = (Split-Path -Parent $executable); WindowStyle = 'Hidden' }
if ($Settings) { $launch.ArgumentList = @('--settings'); $launch.WindowStyle = 'Normal' }
Start-Process @launch
