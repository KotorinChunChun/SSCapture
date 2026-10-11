[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('Enable','Disable','Status')][string]$Action)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$executable = Join-Path $projectRoot 'artifacts\app\SSCapture.exe'
$startupDirectory = [Environment]::GetFolderPath('Startup')
$shortcutPath = Join-Path $startupDirectory 'SSCapture.lnk'
if ($Action -eq 'Status') {
    if (Test-Path -LiteralPath $shortcutPath) { Write-Output "登録済み: $shortcutPath" }
    else { Write-Output '自動起動は未登録です。' }
    return
}
if ($Action -eq 'Disable') {
    if (Test-Path -LiteralPath $shortcutPath) { Remove-Item -LiteralPath $shortcutPath }
    Write-Output 'SSCaptureの自動起動を解除しました。'
    return
}
if (-not (Test-Path -LiteralPath $executable)) { throw '先にビルドしてください。' }
$shell = New-Object -ComObject WScript.Shell
try {
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $executable
    $shortcut.WorkingDirectory = Split-Path -Parent $executable
    $shortcut.Description = 'SSCapture スクリーンショット'
    $shortcut.Save()
} finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
Write-Output "自動起動を登録しました: $shortcutPath"
