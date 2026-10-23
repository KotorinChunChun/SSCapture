[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('Enable','Disable','Status')][string]$Action,
    [Parameter(Mandatory)][string]$InstallDirectory)
$ErrorActionPreference = 'Stop'
# 開発出力や無引数のlnkは登録しない。GUIと同じ所有照合・共通タスクを使う。
$startupRoot = [IO.Path]::GetFullPath($InstallDirectory)
$startupHelper = Join-Path $startupRoot 'KotorinToys.Maintenance.exe'
if (-not (Test-Path -LiteralPath $startupHelper -PathType Leaf)) { throw '正式インストール先の保守補助が見つかりません。' }
$operation = switch ($Action) { 'Enable' { 'startup-enable' } 'Disable' { 'startup-disable' } 'Status' { 'startup-status' } }
& $startupHelper --product SSCapture --root $startupRoot --operation $operation
if ($LASTEXITCODE -ne 0) { throw "自動起動の管理処理を完了できませんでした ($LASTEXITCODE)。" }
