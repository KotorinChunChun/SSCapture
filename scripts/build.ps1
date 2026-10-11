[CmdletBinding()]
param([switch]$NoRestore)
. (Join-Path $PSScriptRoot 'common.ps1')
$appProject = Join-Path $projectRoot 'src\SSCapture\SSCapture.csproj'
$testProject = Join-Path $projectRoot 'tests\SSCapture.Tests\SSCapture.Tests.csproj'
$appDirectory = Join-Path $projectRoot 'artifacts\app'
Assert-BuildDependencies -Root $projectRoot
Assert-PublishIdle -AppDirectory $appDirectory
if (-not $NoRestore) {
    Invoke-Dotnet restore $testProject --configfile (Join-Path $projectRoot 'NuGet.Config') --nologo
}
Invoke-Dotnet build $testProject -c Release --no-restore --nologo
Invoke-Dotnet run --project $testProject -c Release --no-build --no-restore
# Validation takes time. Recheck before writing any published files.
Assert-BuildDependencies -Root $projectRoot
Assert-PublishIdle -AppDirectory $appDirectory
Invoke-Dotnet publish $appProject -c Release --no-build --no-restore -o $appDirectory --nologo
$mediaRoot = Join-Path $projectRoot '.tools\ffmpeg\ffmpeg-9.0.2-essentials_build'
$mediaOutput = Join-Path $appDirectory 'tools'
New-Item -ItemType Directory -Path $mediaOutput -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $mediaRoot 'bin\ffmpeg.exe'), (Join-Path $mediaRoot 'bin\ffprobe.exe'), (Join-Path $mediaRoot 'LICENSE'), (Join-Path $mediaRoot 'README.txt') -Destination $mediaOutput -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'licenses\NAudio-LICENSE.txt'), (Join-Path $projectRoot 'THIRD_PARTY.md') -Destination $appDirectory -Force
