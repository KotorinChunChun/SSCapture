[CmdletBinding()]
param([switch]$ApprovedDedicatedMachine, [string]$FaultSetup, [string]$Version)
$ErrorActionPreference = 'Stop'
if (-not $ApprovedDedicatedMachine) { throw 'SSCaptureの登録と稼働状態を一時変更します。専用環境の承認が必要です。' }
$repository = Split-Path -Parent $PSScriptRoot
if (-not $Version) { $Version = [string](Get-Content -LiteralPath (Join-Path $repository 'tool.json') -Raw | ConvertFrom-Json).version }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw '検証対象版が不正です。' }
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\KotorinToys.SSCapture_is1'
$nativeKey = 'HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\KotorinToys.SSCapture_is1'
$existing = Get-ItemProperty -LiteralPath $key
$originalRoot = [IO.Path]::GetFullPath([string]$existing.InstallLocation).TrimEnd('\')
if ($originalRoot -ne 'C:\develop\KotorinToys\.installed\SSCapture' -or $existing.DisplayVersion -ne '0.1.0') {
    throw '事前確認した既存登録と異なります。新たに退避・復旧計画を確認してください。'
}
$setup = Join-Path $repository "release/v$Version/SSCapture-$Version-setup.exe"
$catalog = Get-Content -LiteralPath (Join-Path $repository "release/v$Version/SSCapture-packages.json") -Raw | ConvertFrom-Json
if ((Get-FileHash -LiteralPath $setup).Hash -ne $catalog.packages[0].sha256) { throw '検証用setupの台帳不一致です。' }
$oldSetup = Join-Path $repository 'release/v0.1.2/SSCapture-0.1.2-setup.exe'
if ((Get-FileHash -LiteralPath $oldSetup).Hash -ne 'D2A79E9ABAC8B202048BFAFD866D45C5A64D48B3859BE6D50920E49A54B5AF15') { throw '既知旧版のハッシュ不一致です。' }
$run = Join-Path $repository ("dev/history/v$Version/logs/installed-" + [Guid]::NewGuid().ToString('N'))
$backup = Join-Path $run 'backup'
New-Item -ItemType Directory -Path $backup -Force | Out-Null
$testRoot = Join-Path $run '日本語 空白の導入先'
function Write-Result($Value) { $Value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $run 'result.json') -Encoding utf8NoBOM }
function Run-Installer([string]$File, [string[]]$Arguments) {
    $process = Start-Process -FilePath $File -ArgumentList $Arguments -WindowStyle Hidden -PassThru
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "setupが終了コード$($process.ExitCode)で停止しました。ログを確認してください。" }
}
function Run-Fault([string]$LogName) {
    if (-not $FaultSetup) { return }
    $process = Start-Process -FilePath $FaultSetup -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',"/DIR=`"$testRoot`"", "/LOG=`"$(Join-Path $run $LogName)`"") -WindowStyle Hidden -PassThru
    $process.WaitForExit()
    if ($process.ExitCode -ne 10) { throw "故障注入setupが失敗コード10を返しません: $($process.ExitCode)" }
}
function Pipe-Request([string]$Command, [int]$ExpectedPid) {
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', "KotorinToys.$sid.ss-capture", [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::CurrentUserOnly)
    try {
        $pipe.Connect(3000)
        $reader = [IO.StreamReader]::new($pipe)
        $writer = [IO.StreamWriter]::new($pipe); $writer.AutoFlush = $true
        $writer.WriteLine((@{ command=$Command; expectedPid=$ExpectedPid } | ConvertTo-Json -Compress))
        $reply = $reader.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult() | ConvertFrom-Json
        if ($reply.pid -ne $ExpectedPid -or $reply.protocolVersion -ne 2) { throw '応答プロセスが一致しません。' }
        return $reply
    } finally { $pipe.Dispose() }
}
function Invoke-Managed([string]$Operation) {
    $helper = Join-Path $testRoot 'maintenance/KotorinToys.Maintenance.exe'
    $response = & $helper --product SSCapture --root $testRoot --operation $Operation 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "管理操作 $Operation が失敗: $response" }
    return $response | ConvertFrom-Json
}
function Assert-UserData {
    foreach ($relative in $script:dataHashes.Keys) {
        if ((Get-FileHash -LiteralPath (Join-Path $testRoot $relative)).Hash -ne $script:dataHashes[$relative]) { throw "利用者fixtureが変化: $relative" }
    }
}
$hostRoot = Join-Path $env:LOCALAPPDATA 'KotorinToys'
$hostNames = @('settings.json','settings.json.bak','removal-operation.json','last-removal-operation.json')
$hostBefore = @{}
foreach ($name in $hostNames) {
    $path = Join-Path $hostRoot $name
    if (Test-Path -LiteralPath $path) {
        Copy-Item -LiteralPath $path -Destination (Join-Path $backup $name)
        $hostBefore[$name] = (Get-FileHash -LiteralPath $path).Hash
    }
}
if (Test-Path -LiteralPath (Join-Path $hostRoot 'removal-operation.json')) { throw '本体に未完了の削除操作があります。開始しません。' }
$schedule = New-Object -ComObject Schedule.Service
$schedule.Connect()
$tasksFolder = $schedule.GetFolder('\')
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$tasksBefore = @{}
foreach ($id in @('host','ss-capture')) {
    $name = "KotorinToys.$sid.$id"
    try {
        $task = $tasksFolder.GetTask($name)
        $tasksBefore[$name] = @{ xml=$task.Xml; security=$task.GetSecurityDescriptor(7) }
    } catch {
        $failure = $_.Exception
        while ($failure.InnerException) { $failure = $failure.InnerException }
        if ($failure.HResult -ne -2147024894) { throw }
    }
}
$tasksBefore | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $backup 'tasks.json') -Encoding utf8NoBOM
& reg.exe export $nativeKey (Join-Path $backup 'registration.reg') /y | Out-Null
if ($LASTEXITCODE -ne 0) { throw '正式登録を退避できません。' }
$registrationHash = (Get-FileHash -LiteralPath (Join-Path $backup 'registration.reg')).Hash
Copy-Item -LiteralPath $originalRoot -Destination (Join-Path $backup 'original-install') -Recurse
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'SSCapture.lnk'
$shortcutExisted = Test-Path -LiteralPath $shortcut
if ($shortcutExisted) { Copy-Item -LiteralPath $shortcut -Destination (Join-Path $backup 'SSCapture.lnk') }
$running = @(Get-CimInstance Win32_Process -Filter "Name='SSCapture.exe'")
if ($running.Count -gt 1) { throw '複数プロセスがあります。開始しません。' }
$restart = $null
if ($running.Count -eq 1) {
    $candidate = $running[0]
    if ($candidate.ExecutablePath -ne (Join-Path $repository 'artifacts/app/SSCapture.exe').Replace('/','\')) { throw '事前確認と違う常駐プロセスです。' }
    $process = Get-Process -Id $candidate.ProcessId
    if ($process.SessionId -ne (Get-Process -Id $PID).SessionId) { throw 'セッションが異なります。' }
    if ((Invoke-CimMethod -InputObject $candidate -MethodName GetOwnerSid).Sid -ne $sid) { throw 'プロセス利用者が異なります。' }
    $reply = Pipe-Request 'stop' $candidate.ProcessId
    if ($reply.result -ne 'accepted') { throw '常駐が正常終了を拒否しました。実導入は開始しません。' }
    if (-not $process.WaitForExit(15000)) { throw '正常終了を確認できません。' }
    $restart = $candidate.ExecutablePath
}
$changed = $false
$result = [ordered]@{ run=$run; status='running'; originalVersion=$existing.DisplayVersion; restored=$false; checks=@() }
try {
    # このキーだけを退避済み。元の導入先・設定・画像は変更しない。
    Remove-Item -LiteralPath $key -Recurse
    $changed = $true
    if ($FaultSetup) {
        Run-Fault 'fresh-fault.log'
        if (Test-Path -LiteralPath $key) { throw '新規導入失敗後に登録が残りました。' }
        if (@(Get-ChildItem -LiteralPath $testRoot -File -Recurse).Count -ne 0) { throw '新規導入失敗後に配布ファイルが残りました。' }
        $result.checks += @{ name='fresh-postinstall-failure-rollback'; value=$true }
    }
    Run-Installer $setup @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',"/DIR=`"$testRoot`"", "/LOG=`"$(Join-Path $run 'fresh.log')`"")
    $detected = Invoke-Managed 'verify'
    $result.checks += @{ name='fresh-detect'; value=$detected }
    $started = Invoke-Managed 'start'
    $shown = Invoke-Managed 'show'
    $status = Invoke-Managed 'status'
    if ($started.pid -ne $shown.pid -or $shown.pid -ne $status.pid) { throw 'showでPIDが変わりました。' }
    $result.checks += @{ name='same-pid-start-show-status'; value=$status }
    $stopped = Invoke-Managed 'stop'
    $result.checks += @{ name='normal-stop'; value=$stopped }
    Run-Installer (Join-Path $testRoot 'unins000.exe') @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',"/LOG=`"$(Join-Path $run 'fresh-remove.log')`"")
    if (Test-Path -LiteralPath $key) { throw '削除後に製品登録が残っています。' }
    Run-Installer $oldSetup @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',"/DIR=`"$testRoot`"", "/LOG=`"$(Join-Path $run 'legacy.log')`"")
    if (Test-Path -LiteralPath (Join-Path $testRoot 'tool.json')) { throw 'tool.jsonなしの旧版ではありません。' }
    $script:dataHashes = @{}
    foreach ($relative in @('data/acceptance-settings.json','Captures/保持画像.png','tools/ffmpeg.exe','history/保持履歴.txt')) {
        $path = Join-Path $testRoot $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
        [IO.File]::WriteAllText($path, '実行しない保持確認fixture')
        $script:dataHashes[$relative] = (Get-FileHash -LiteralPath $path).Hash
    }
    if ($FaultSetup) {
        $oldHashes = @{}
        foreach ($file in Get-ChildItem -LiteralPath $testRoot -File -Recurse) { $oldHashes[$file.FullName] = (Get-FileHash -LiteralPath $file.FullName).Hash }
        Run-Fault 'legacy-fault.log'
        if ((Get-ItemProperty -LiteralPath $key).DisplayVersion -ne '0.1.2') { throw '更新失敗で旧版登録が戻りませんでした。' }
        foreach ($path in $oldHashes.Keys) { if ((Get-FileHash -LiteralPath $path).Hash -ne $oldHashes[$path]) { throw "更新失敗で元ファイルが変化: $path" } }
        if (Test-Path -LiteralPath (Join-Path $testRoot 'tool.json')) { throw '更新失敗後に新定義が残りました。' }
        Assert-UserData
        $result.checks += @{ name='legacy-postinstall-failure-rollback'; value=$true }
    }
    # /DIRなしで既存の日本語・空白を含む導入先を継承する。
    Run-Installer $setup @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',"/LOG=`"$(Join-Path $run 'legacy-upgrade.log')`"")
    $result.checks += @{ name='legacy-upgrade-detect'; value=(Invoke-Managed 'verify') }
    Assert-UserData
    $result.checks += @{ name='legacy-data-preserved'; value=$true }
    Run-Installer (Join-Path $testRoot 'unins000.exe') @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',"/LOG=`"$(Join-Path $run 'legacy-remove.log')`"")
    Assert-UserData
    $result.checks += @{ name='uninstall-data-preserved'; value=$true }
    $result.status = 'passed'
} catch {
    $result.status = 'failed'; $result.error = $_.Exception.Message
    if (Test-Path -LiteralPath $key) {
        $observed = Get-ItemProperty -LiteralPath $key
        $result.observedRegistration = @{
            name=$observed.DisplayName; version=$observed.DisplayVersion
            directory=$observed.InstallLocation; uninstall=$observed.UninstallString
        }
    }
    throw
} finally {
    if ($changed) {
        # テスト先の登録だけを元へ戻す。未知の登録先なら上書きしない。
        if (Test-Path -LiteralPath $key) {
            $current = Get-ItemProperty -LiteralPath $key
            if ([IO.Path]::GetFullPath([string]$current.InstallLocation).TrimEnd('\') -ne $testRoot) { throw '外部変更された登録は復元しません。退避先を確認してください。' }
            Remove-Item -LiteralPath $key -Recurse
        }
        if ((Get-FileHash -LiteralPath (Join-Path $backup 'registration.reg')).Hash -ne $registrationHash) { throw '退避した登録が変わっています。' }
        & reg.exe import (Join-Path $backup 'registration.reg') | Out-Null
        if ($LASTEXITCODE -ne 0) { throw '元の登録の復元に失敗しました。' }
        foreach ($name in $tasksBefore.Keys) {
            $before = $tasksBefore[$name]
            try { $current = $tasksFolder.GetTask($name) } catch { $current = $null }
            if ($null -eq $current) { $null = $tasksFolder.RegisterTask($name, $before.xml, 2, $null, $null, 3, $before.security) }
            elseif ($current.Xml -ne $before.xml -or $current.GetSecurityDescriptor(7) -ne $before.security) { throw '所有予約が外部変更されています。自動上書きしません。' }
        }
        $lease = [IO.FileStream]::new((Join-Path $hostRoot 'startup.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        try {
            $settingsPath = Join-Path $hostRoot 'settings.json'
            if ((Get-FileHash -LiteralPath $settingsPath).Hash -ne $hostBefore['settings.json']) {
                $journal = [Text.Json.Nodes.JsonNode]::Parse([IO.File]::ReadAllText((Join-Path $hostRoot 'last-removal-operation.json')))
                $currentSettings = [Text.Json.Nodes.JsonNode]::Parse([IO.File]::ReadAllText($settingsPath))
                if ($journal['targetId'].ToString() -ne 'ss-capture' -or
                    -not [Text.Json.Nodes.JsonNode]::DeepEquals($currentSettings, $journal['after'])) {
                    throw 'テスト操作以外の本体設定変更を検出しました。退避を自動上書きしません。'
                }
            }
            foreach ($name in $hostNames) {
                $path = Join-Path $hostRoot $name
                if ($hostBefore.ContainsKey($name)) { Copy-Item -LiteralPath (Join-Path $backup $name) -Destination $path -Force }
                elseif (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
            }
        } finally { $lease.Dispose() }
        if (Test-Path -LiteralPath $shortcut) {
            $shortcutTarget = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut).TargetPath
            if ($shortcutTarget -ne (Join-Path $testRoot 'SSCapture.exe') -and $shortcutTarget -ne (Join-Path $originalRoot 'SSCapture.exe')) { throw 'ショートカットが外部変更されています。' }
        }
        if ($shortcutExisted) { Copy-Item -LiteralPath (Join-Path $backup 'SSCapture.lnk') -Destination $shortcut -Force }
        elseif (Test-Path -LiteralPath $shortcut) { Remove-Item -LiteralPath $shortcut }
        $result.restored = $true
    }
    if ($restart) { $null = Start-Process -FilePath $restart -ArgumentList '--startup','--launch-origin','direct_task' -WindowStyle Hidden -PassThru }
    Write-Result $result
    Write-Output "実導入テスト結果: $run / $($result.status) / 元環境復元=$($result.restored)"
}
