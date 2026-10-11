[CmdletBinding()]
param()
. (Join-Path $PSScriptRoot 'common.ps1')
$mediaBin = Join-Path $projectRoot '.tools\ffmpeg\ffmpeg-9.0.2-essentials_build\bin'
Invoke-Dotnet run --project (Join-Path $projectRoot 'tests\SSCapture.Tests\SSCapture.Tests.csproj') -c Release --no-restore -- --media-smoke (Join-Path $mediaBin 'ffmpeg.exe') (Join-Path $mediaBin 'ffprobe.exe') (Join-Path $projectRoot 'artifacts\media-validation')
