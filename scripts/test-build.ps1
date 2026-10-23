[CmdletBinding()]
param([string]$OutputRoot)
. (Join-Path $PSScriptRoot 'common.ps1')

# Generated files and injected process/dotnet results only. No real build,
# process stop, clipboard access, or write to artifacts/app is performed.
$fixtureVersion = [string](Get-Content -LiteralPath (Join-Path $projectRoot 'tool.json') -Raw | ConvertFrom-Json).version
$fixtureParent = if ($OutputRoot) { [IO.Path]::GetFullPath($OutputRoot, $projectRoot) } else { Join-Path $projectRoot "dev/history/v$fixtureVersion/logs/build-fixtures" }
$fixtureBase = Join-Path $fixtureParent ([Guid]::NewGuid().ToString('N'))
$sourceBuild = Join-Path $PSScriptRoot 'build.ps1'
$sourceCommon = Join-Path $PSScriptRoot 'common.ps1'
$passed = 0
$failed = 0
$preservedSettings = '{"SchemaVersion":3,"ExternalApplicationsVersion":1,"ImageEditors":[{"Id":"paint","Name":"ペイント","Executable":"C:\\Windows\\System32\\mspaint.exe","Arguments":"\"{file}\""},{"Id":"custom","Name":"追加アプリ","Executable":"C:\\custom.exe","Arguments":"\"{file}\""}],"SelectedImageEditorId":"custom"}'

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Assert-Rejected([scriptblock]$Action, [string]$MessagePart) {
    $rejected = $false
    try { & $Action }
    catch {
        $rejected = $true
        Assert-Check ($_.Exception.Message.Contains($MessagePart)) "想定外の拒否理由: $($_.Exception.Message)"
    }
    Assert-Check $rejected "拒否されませんでした: $MessagePart"
}
function Test-Case([string]$Name, [scriptblock]$Action) {
    try { & $Action; $script:passed++; Write-Host "PASS $Name" }
    catch { $script:failed++; Write-Host "FAIL $Name`: $($_.Exception.Message)" }
}
function Set-FixtureState([string]$Root, [object[]]$First = @(), [object[]]$Second = @(), [bool]$FailTests = $false) {
    @{ First = $First; Second = $Second; FailTests = $FailTests } | ConvertTo-Json -Depth 6 |
        Set-Content -LiteralPath (Join-Path $Root 'state.json') -Encoding utf8
}
function New-BuildFixture([string]$Name) {
    $root = Join-Path $fixtureBase $Name
    foreach ($path in Get-BuildDependencyPaths -Root $root) {
        New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
        [IO.File]::WriteAllText($path, 'fixture dependency')
    }
    $scriptDirectory = Join-Path $root 'scripts'
    New-Item -ItemType Directory -Path $scriptDirectory -Force | Out-Null
    Copy-Item -LiteralPath $sourceBuild -Destination (Join-Path $scriptDirectory 'build.ps1')
    $fakeCommon = @'
. '__SOURCE_COMMON__'
$projectRoot = Split-Path -Parent $PSScriptRoot
$fixtureState = Get-Content -LiteralPath (Join-Path $projectRoot 'state.json') -Raw | ConvertFrom-Json
$fixtureReads = 0
function Get-PublishProcesses {
    $script:fixtureReads++
    if ($script:fixtureReads -eq 1) { return $fixtureState.First }
    return $fixtureState.Second
}
function Invoke-Dotnet {
    @{ Args = @($args) } | ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $projectRoot 'calls.jsonl')
    if ($args[0] -eq 'run' -and $fixtureState.FailTests) { throw 'fixture test failure' }
    if ($args[0] -eq 'publish') {
        [IO.File]::WriteAllText((Join-Path $projectRoot 'artifacts\app\published.fixture'), 'published')
    }
}
'@
    $fakeCommon.Replace('__SOURCE_COMMON__', $sourceCommon.Replace("'", "''")) |
        Set-Content -LiteralPath (Join-Path $scriptDirectory 'common.ps1') -Encoding utf8
    New-Item -ItemType Directory -Path (Join-Path $root 'artifacts\app\data') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $root 'artifacts\app\data\settings.json'), $preservedSettings)
    Set-FixtureState $root
    return $root
}
function New-FixtureProcess([string]$Name, [string]$Path, [string]$CommandLine = '') {
    [pscustomobject]@{ Name = $Name; ExecutablePath = $Path; CommandLine = $CommandLine; ProcessId = 999999 }
}
function Get-FixtureCalls([string]$Root) {
    $path = Join-Path $Root 'calls.jsonl'
    if (Test-Path -LiteralPath $path) { Get-Content -LiteralPath $path | ForEach-Object { $_ | ConvertFrom-Json } }
}
function Assert-Unpublished([string]$Root) {
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $Root 'artifacts\app\published.fixture'))) '拒否後に発行された'
    Assert-Check ([IO.File]::ReadAllText((Join-Path $Root 'artifacts\app\data\settings.json')) -eq $preservedSettings) '既存の外部アプリ登録・選択が変更された'
}

Test-Case 'すべての依存・ライセンス不足を出力前に拒否' {
    $root = New-BuildFixture 'missing-dependency'
    foreach ($path in Get-BuildDependencyPaths -Root $root) {
        # Rename this generated fixture file only; restore it after each rejection.
        $held = $path + '.fixture-held'
        Move-Item -LiteralPath $path -Destination $held
        try {
            Assert-Rejected { & (Join-Path $root 'scripts\build.ps1') } 'ビルドに必要なファイルがありません'
            Assert-Check (@(Get-FixtureCalls $root).Count -eq 0) '依存不足でdotnetを呼んだ'
            Assert-Unpublished $root
        }
        finally { Move-Item -LiteralPath $held -Destination $path }
    }
}
Test-Case '対象exe・media・DLLホスト・不明情報を拒否し別フォルダを許可' {
    $root = New-BuildFixture 'process-boundaries'
    $app = Join-Path $root 'artifacts\app'
    $other = Join-Path $root 'unrelated'
    $blocked = @(
        (New-FixtureProcess 'SSCapture.exe' (Join-Path $app 'SSCapture.exe'))
        (New-FixtureProcess 'ffmpeg.exe' (Join-Path $app 'tools\ffmpeg.exe'))
        (New-FixtureProcess 'ffprobe.exe' (Join-Path $app 'tools\ffprobe.exe'))
        (New-FixtureProcess 'dotnet.exe' (Join-Path $other 'dotnet.exe') ('dotnet exec "' + (Join-Path $app 'SSCapture.dll') + '"'))
        (New-FixtureProcess 'corerun.exe' (Join-Path $other 'corerun.exe') ('corerun "' + (Join-Path $app 'SSCapture.dll') + '"'))
    )
    foreach ($process in $blocked) { Assert-Rejected { Assert-PublishIdle $app -ReadProcesses { $process } } '使用中' }
    foreach ($process in @(
        (New-FixtureProcess 'SSCapture.exe' '')
        (New-FixtureProcess 'dotnet.exe' (Join-Path $other 'dotnet.exe'))
        (New-FixtureProcess 'dotnet.exe' (Join-Path $other 'dotnet.exe') 'dotnet SSCapture.dll')
    )) { Assert-Rejected { Assert-PublishIdle $app -ReadProcesses { $process } } '発行を中止' }
    Assert-Rejected { Assert-PublishIdle $app -ReadProcesses { throw 'fixture query failure' } } '稼働状況を確認できない'
    $allowed = @(
        (New-FixtureProcess 'SSCapture.exe' (Join-Path $other 'SSCapture.exe'))
        (New-FixtureProcess 'ffmpeg.exe' (Join-Path $other 'ffmpeg.exe'))
        (New-FixtureProcess 'dotnet.exe' (Join-Path $other 'dotnet.exe') ('dotnet "' + (Join-Path $other 'SSCapture.dll') + '"'))
        (New-FixtureProcess 'dotnet.exe' (Join-Path $other 'dotnet.exe') 'dotnet build Other.csproj')
        (New-FixtureProcess 'pwsh.exe' (Join-Path $other 'pwsh.exe') ('pwsh -Command Get-FileHash "' + (Join-Path $app 'SSCapture.dll') + '"'))
    )
    Assert-PublishIdle $app -ReadProcesses { $allowed }
    Assert-PublishIdle $app -ReadProcesses { @() }
    Assert-Unpublished $root
}
Test-Case '開始時に稼働中ならビルドも発行もしない' {
    $root = New-BuildFixture 'running-at-start'
    $process = New-FixtureProcess 'SSCapture.exe' (Join-Path $root 'artifacts\app\SSCapture.exe')
    Set-FixtureState $root -First @($process)
    Assert-Rejected { & (Join-Path $root 'scripts\build.ps1') } '使用中'
    Assert-Check (@(Get-FixtureCalls $root).Count -eq 0) '稼働中にdotnetを呼んだ'
    Assert-Unpublished $root
}
Test-Case 'テスト中に起動された対象を発行直前に再検知' {
    $root = New-BuildFixture 'running-after-tests'
    $process = New-FixtureProcess 'SSCapture.exe' (Join-Path $root 'artifacts\app\SSCapture.exe')
    Set-FixtureState $root -Second @($process)
    Assert-Rejected { & (Join-Path $root 'scripts\build.ps1') } '使用中'
    Assert-Check ((@(Get-FixtureCalls $root | ForEach-Object { $_.Args[0] }) -join ',') -eq 'restore,build,run') '検証前後の呼出順が不正'
    Assert-Unpublished $root
}
Test-Case '通常テスト失敗なら発行せずdataを保持' {
    $root = New-BuildFixture 'test-failure'
    Set-FixtureState $root -FailTests $true
    Assert-Rejected { & (Join-Path $root 'scripts\build.ps1') } 'fixture test failure'
    Assert-Check ((@(Get-FixtureCalls $root | ForEach-Object { $_.Args[0] }) -join ',') -eq 'restore,build,run') '失敗後に別工程を実行した'
    Assert-Unpublished $root
}
foreach ($noRestore in @($false, $true)) {
    Test-Case "検証成功後だけ発行・依存同梱しdata維持 NoRestore=$noRestore" {
        $root = New-BuildFixture "success-$noRestore"
        & (Join-Path $root 'scripts\build.ps1') -NoRestore:$noRestore
        $calls = @(Get-FixtureCalls $root)
        $expected = if ($noRestore) { 'build,run,publish' } else { 'restore,build,run,publish' }
        Assert-Check (($calls.ForEach({ $_.Args[0] }) -join ',') -eq $expected) 'restore/build/test/publishの順序が不正'
        $run = @($calls | Where-Object { $_.Args[0] -eq 'run' })[0]
        Assert-Check ($run.Args -contains '--no-build' -and $run.Args -contains '--no-restore') 'テスト時に再ビルド/復元する'
        foreach ($relative in @('published.fixture', 'tools\ffmpeg.exe', 'tools\ffprobe.exe', 'tools\LICENSE', 'tools\README.txt', 'NAudio-LICENSE.txt', 'THIRD_PARTY.md')) {
            Assert-Check (Test-Path -LiteralPath (Join-Path $root "artifacts\app\$relative") -PathType Leaf) "必要な出力がない: $relative"
        }
        Assert-Check ([IO.File]::ReadAllText((Join-Path $root 'artifacts\app\data\settings.json')) -eq $preservedSettings) '既存の外部アプリ登録・選択が変更された'
    }
}
Write-Host "Build script fixtures: $passed passed, $failed failed"
Write-Host "Fixture files retained: $fixtureBase"
if ($failed -ne 0) { exit 1 }
