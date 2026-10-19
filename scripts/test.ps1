[CmdletBinding()]
param([switch]$Interactive, [string]$OutputPath)
. (Join-Path $PSScriptRoot 'common.ps1')
Invoke-Dotnet restore (Join-Path $projectRoot 'tests\SSCapture.Tests\SSCapture.Tests.csproj') --configfile (Join-Path $projectRoot 'NuGet.Config') --nologo
if ($OutputPath) {
    if ($Interactive) { throw '出力先の分離は通常テストだけに対応します。' }
    $testOutput = [IO.Path]::GetFullPath($OutputPath, $projectRoot)
    Invoke-Dotnet build (Join-Path $projectRoot 'tests\SSCapture.Tests\SSCapture.Tests.csproj') -c Release --no-restore "-p:OutputPath=$testOutput" --nologo
    Invoke-Dotnet (Join-Path $testOutput 'SSCapture.Tests.dll')
    return
}
if ($Interactive) {
    Write-Host 'マウスとキーを自動操作します。完了までPCの操作を控えてください。画面取得とクリップボード変更はしません。'
    Invoke-Dotnet run --project (Join-Path $projectRoot 'tests\SSCapture.Tests\SSCapture.Tests.csproj') -c Release --no-restore -- --interactive-smoke
} else {
    Invoke-Dotnet run --project (Join-Path $projectRoot 'tests\SSCapture.Tests\SSCapture.Tests.csproj') -c Release --no-restore
}
