[CmdletBinding()]
param([string]$OutputRoot = 'dev/history/v0.1.3/logs/payload-fixtures')
. (Join-Path $PSScriptRoot 'managed-payload.ps1')
$repository = Split-Path -Parent $PSScriptRoot
$base = [IO.Path]::GetFullPath($OutputRoot, $repository)
if (-not $base.StartsWith((Join-Path $repository 'dev\history\'), [StringComparison]::OrdinalIgnoreCase)) { throw 'fixtureは版別ログ配下へ作成してください。' }
$root = Join-Path $base ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -Force | Out-Null
$passed = 0
$failed = 0
function Case([string]$CaseLabel, [scriptblock]$Action) {
    try { & $Action; $script:passed++; Write-Host "PASS $CaseLabel" }
    catch { $script:failed++; Write-Host "FAIL $CaseLabel : $($_.Exception.Message)" }
}
function Rejected([scriptblock]$Action) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    if (-not $rejected) { throw '不正な配布物を拒否しませんでした。' }
}
function Write-Json([string]$Relative, $Value) {
    $Value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $root $Relative) -Encoding utf8NoBOM
}
$files = @('SSCapture.exe','SSCapture.dll','SSCapture.deps.json','coreclr.dll','hostfxr.dll','hostpolicy.dll',
    'System.Private.CoreLib.dll','System.Windows.Forms.dll','README.md',
    'maintenance/KotorinToys.Maintenance.exe','maintenance/KotorinToys.Maintenance.dll',
    'maintenance/KotorinToys.Maintenance.deps.json','maintenance/coreclr.dll',
    'maintenance/hostfxr.dll','maintenance/hostpolicy.dll','maintenance/System.Private.CoreLib.dll')
foreach ($file in $files) {
    $path = Join-Path $root $file
    New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
    [IO.File]::WriteAllText($path, 'fixture')
}
$definition = Get-Content -LiteralPath (Join-Path $repository 'tool.json') -Raw | ConvertFrom-Json
$definition.executable = 'SSCapture.exe'
$definition.version = '0.1.3'
Write-Json 'tool.json' $definition
$frameworks = @(
    @{ name='Microsoft.NETCore.App'; version='10.0.13' },
    @{ name='Microsoft.WindowsDesktop.App'; version='10.0.13' })
Write-Json 'SSCapture.runtimeconfig.json' @{ runtimeOptions=@{ includedFrameworks=$frameworks } }
Write-Json 'maintenance/KotorinToys.Maintenance.runtimeconfig.json' @{ runtimeOptions=@{ includedFrameworks=@($frameworks[0]) } }
$readVersion = { param($Path)
    if ([IO.Path]::GetFileName($Path) -in @('coreclr.dll','System.Private.CoreLib.dll','System.Windows.Forms.dll')) {
        return [pscustomobject]@{ ProductVersion='10.0.13+fixture'; FileMajorPart=10; FileMinorPart=0; FileBuildPart=1326; FilePrivatePart=50504 }
    }
    [pscustomobject]@{ ProductName='SSCapture'; ProductVersion='0.1.3'; FileVersion='0.1.3.0' }
}
function Validate { Assert-ManagedPayload -Root $root -Version '0.1.3' -RuntimeVersion '10.0.13' -ReadVersion $readVersion }
Case '正常な管理配布物' { Validate }
foreach ($file in @($files) + @('tool.json','SSCapture.runtimeconfig.json','maintenance/KotorinToys.Maintenance.runtimeconfig.json')) {
    Case "必須物欠落 $file" {
        $path = Join-Path $root $file
        $held = Join-Path $base ([Guid]::NewGuid().ToString('N') + '.held')
        Move-Item -LiteralPath $path -Destination $held
        try { Rejected { Validate } } finally { Move-Item -LiteralPath $held -Destination $path }
    }
}
foreach ($name in @('id','name','appId','version','repositoryUrl','installedExecutable','executable','supportedEnvironment','manual','readme')) {
    Case "定義不一致 $name" {
        $before = $definition.$name
        $definition.$name = 'wrong'
        Write-Json 'tool.json' $definition
        try { Rejected { Validate } } finally { $definition.$name = $before; Write-Json 'tool.json' $definition }
    }
}
Case '管理能力不足' {
    $before = $definition.capabilities; $definition.capabilities = @('status'); Write-Json 'tool.json' $definition
    try { Rejected { Validate } } finally { $definition.capabilities = $before; Write-Json 'tool.json' $definition }
}
Case '実EXEの製品名不一致' {
    Rejected { Assert-ManagedPayload $root '0.1.3' '10.0.13' -ReadVersion { [pscustomobject]@{ ProductName='Other'; ProductVersion='0.1.3'; FileVersion='0.1.3.0' } } }
}
Case '旧runtimeを拒否' { Rejected { Assert-ManagedPayload $root '0.1.3' '10.0.14' -ReadVersion $readVersion } }
Case '設定だけ新版でDesktop実体が旧版なら拒否' {
    $baselineReader = $readVersion
    Rejected { Assert-ManagedPayload $root '0.1.3' '10.0.13' -ReadVersion {
        param($Path)
        if ([IO.Path]::GetFileName($Path) -eq 'System.Windows.Forms.dll') { return [pscustomobject]@{ ProductVersion='10.0.11+fixture' } }
        & $baselineReader $Path
    } }
}
Case 'CoreCLRだけ別ビルドなら拒否' {
    $baselineReader = $readVersion
    Rejected { Assert-ManagedPayload $root '0.1.3' '10.0.13' -ReadVersion {
        param($Path)
        $info = & $baselineReader $Path
        if ([IO.Path]::GetFileName($Path) -eq 'coreclr.dll') { $info.FileBuildPart = 1126 }
        $info
    } }
}
Case '補助だけ旧runtimeを拒否' {
    Write-Json 'maintenance/KotorinToys.Maintenance.runtimeconfig.json' @{ runtimeOptions=@{ includedFrameworks=@(@{name='Microsoft.NETCore.App';version='10.0.11'}) } }
    try { Rejected { Validate } } finally { Write-Json 'maintenance/KotorinToys.Maintenance.runtimeconfig.json' @{ runtimeOptions=@{ includedFrameworks=@($frameworks[0]) } } }
}
foreach ($link in @('missing.md','../outside.md','C:\Windows\win.ini','%2e%2e/outside.md')) {
    Case "説明書参照を拒否 $link" {
        [IO.File]::WriteAllText((Join-Path $root 'README.md'), "[参照]($link)")
        try { Rejected { Validate } } finally { [IO.File]::WriteAllText((Join-Path $root 'README.md'), 'fixture') }
    }
}
foreach ($forbidden in @('secrets.json','sample.pdb','private/test.md','data/settings.json','tools/ffmpeg.exe')) {
    Case "禁止物 $forbidden" {
        $path = Join-Path $root $forbidden
        New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
        [IO.File]::WriteAllText($path, 'fixture')
        try { Rejected { Validate } } finally {
            Remove-Item -LiteralPath $path
            if ($forbidden.Contains('/')) { Remove-Item -LiteralPath (Split-Path -Parent $path) }
        }
    }
}
Case '目録の全件照合・欠落・追加・改変・重複' {
    $manifest = @(Get-PayloadManifest $root)
    Assert-PayloadManifest $root $manifest
    Rejected { Assert-PayloadManifest $root $manifest[1..($manifest.Count-1)] }
    Rejected { Assert-PayloadManifest $root (@($manifest) + @($manifest[0])) }
    $path = Join-Path $root 'SSCapture.exe'; [IO.File]::WriteAllText($path, 'modified')
    try { Rejected { Assert-PayloadManifest $root $manifest } } finally { [IO.File]::WriteAllText($path, 'fixture') }
}
Write-Host "RESULT: $passed passed, $failed failed"
if ($failed) { exit 1 }
