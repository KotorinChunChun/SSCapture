[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$mediaRoot = Join-Path $projectRoot '.tools\ffmpeg'
$archive = Join-Path $projectRoot '.tools\ffmpeg-9.0.2.zip'
$expected = '60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba'
New-Item -ItemType Directory -Path $mediaRoot -Force | Out-Null
if (!(Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest 'https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-essentials_build.zip' -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expected) {
    throw 'FFmpeg archive SHA256 mismatch. The archive has been retained for inspection.'
}
Expand-Archive -LiteralPath $archive -DestinationPath $mediaRoot -Force
$mediaBin = Join-Path $mediaRoot 'ffmpeg-9.0.2-essentials_build\bin'
$versionOutput = & (Join-Path $mediaBin 'ffmpeg.exe') -version
if ($LASTEXITCODE -ne 0) { throw 'FFmpeg validation failed.' }
$versionOutput | Select-Object -First 1
