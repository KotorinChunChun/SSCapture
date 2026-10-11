[CmdletBinding()]
param()
. (Join-Path $PSScriptRoot 'common.ps1')
# Generated text image only; no screenshot, clipboard access, or microphone input.
Invoke-Dotnet run --project (Join-Path $projectRoot 'tests\SSCapture.Tests\SSCapture.Tests.csproj') -c Release --no-restore -- --ocr-smoke
