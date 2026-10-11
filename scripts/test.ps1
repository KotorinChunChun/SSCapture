[CmdletBinding()]
param([switch]$Interactive)
. (Join-Path $PSScriptRoot 'common.ps1')
Invoke-Dotnet restore (Join-Path $projectRoot 'tests\SSCapture.Tests\SSCapture.Tests.csproj') --configfile (Join-Path $projectRoot 'NuGet.Config') --nologo
if ($Interactive) {
    Write-Host 'マウスとキーを自動操作します。完了までPCの操作を控えてください。画面取得とクリップボード変更はしません。'
    Invoke-Dotnet run --project (Join-Path $projectRoot 'tests\SSCapture.Tests\SSCapture.Tests.csproj') -c Release --no-restore -- --interactive-smoke
} else {
    Invoke-Dotnet run --project (Join-Path $projectRoot 'tests\SSCapture.Tests\SSCapture.Tests.csproj') -c Release --no-restore
}
