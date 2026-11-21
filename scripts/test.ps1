[CmdletBinding()]
param(
    [switch]$Interactive,
    [string]$OutputPath,
    [ValidateSet('All', 'Notification', 'Command', 'Pinned', 'Preset', 'Recipe', 'Snap', 'Menu', 'Output', 'Recording', 'Startup')]
    [string]$Suite = 'All',
    [ValidateSet('Selection', 'Notification', 'Monitors', 'Placement')]
    [string]$NativeSmoke
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
if ($Interactive -and ($OutputPath -or $Suite -ne 'All')) {
    throw 'InteractiveはOutputPathやSuiteと併用できません。'
}
if ($NativeSmoke -and ($Interactive -or $Suite -ne 'All')) {
    throw 'NativeSmokeはInteractiveやSuiteと併用できません。'
}
$testArguments = @(if ($Suite -ne 'All') { "--$($Suite.ToLowerInvariant())-tests" })
if ($NativeSmoke) {
    $testArguments = @(switch ($NativeSmoke) {
        'Selection' { '--selection-input-smoke' }
        'Notification' { '--feedback-native-smoke' }
        'Monitors' { '--selection-monitor-smoke' }
        'Placement' { '--overlay-placement-smoke' }
    })
    Write-Host '専用画面で実デスクトップの操作を検証します。実行中はPCの操作を控えてください。'
}
Invoke-Dotnet restore (Join-Path $projectRoot 'tests\SSCapture.Tests\SSCapture.Tests.csproj') --configfile (Join-Path $projectRoot 'NuGet.Config') --nologo
if ($OutputPath) {
    $testOutput = [IO.Path]::GetFullPath($OutputPath, $projectRoot)
    Invoke-Dotnet build (Join-Path $projectRoot 'tests\SSCapture.Tests\SSCapture.Tests.csproj') -c Release --no-restore "-p:OutputPath=$testOutput" --nologo
    Invoke-Dotnet (Join-Path $testOutput 'SSCapture.Tests.dll') @testArguments
    return
}
if ($Interactive) {
    Write-Host 'マウスとキーを自動操作します。完了までPCの操作を控えてください。画面取得とクリップボード変更はしません。'
    Invoke-Dotnet run --project (Join-Path $projectRoot 'tests\SSCapture.Tests\SSCapture.Tests.csproj') -c Release --no-restore -- --interactive-smoke
} else {
    Invoke-Dotnet run --project (Join-Path $projectRoot 'tests\SSCapture.Tests\SSCapture.Tests.csproj') -c Release --no-restore -- @testArguments
}
