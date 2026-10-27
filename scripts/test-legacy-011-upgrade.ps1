param([switch]$ApprovedDedicatedMachine)
$ErrorActionPreference = 'Stop'
if (-not $ApprovedDedicatedMachine) { throw '専用PCでの登録退避・復元が必要です。' }
$repository = Split-Path -Parent $PSScriptRoot
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\KotorinToys.SSCapture_is1'
$nativeKey = 'HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\KotorinToys.SSCapture_is1'
$current = Get-ItemProperty -LiteralPath $key
$originalRoot = [IO.Path]::GetFullPath([string]$current.InstallLocation).TrimEnd('\')
if ($originalRoot -ne 'C:\develop\KotorinToys\.installed\SSCapture' -or $current.DisplayVersion -ne '0.1.0') {
    throw '事前確認した既存登録ではありません。登録を変更しません。'
}
if (@(Get-CimInstance Win32_Process -Filter "Name='SSCapture.exe'").Count) { throw 'SSCaptureが動作中です。登録を変更しません。' }
$run = Join-Path $repository ('dev/history/v0.1.6/logs/legacy-installer-' + [Guid]::NewGuid().ToString('N'))
$testRoot = Join-Path $run '日本語 空白の導入先'
$backup = Join-Path $run 'original-registration.reg'
$oldSetup = Join-Path $repository 'dev/history/v0.1.6/logs/public-011/SSCapture-0.1.1-setup.exe'
$prior = Join-Path $repository 'dev/history/v0.1.6/logs/public-010/{app}'
$newSetup = Join-Path $repository 'release/v0.1.6/SSCapture-0.1.6-setup.exe'
$manifest = Get-Content -LiteralPath (Join-Path $repository 'release/v0.1.6/SSCapture-packages.json') -Raw | ConvertFrom-Json
if ($manifest.packages.Count -ne 1 -or $manifest.packages[0].version -ne '0.1.6' -or
    (Get-FileHash -LiteralPath $newSetup).Hash.ToLowerInvariant() -ne $manifest.packages[0].sha256 -or
    (Get-FileHash -LiteralPath $oldSetup).Hash -ne '6562BFF5E8EE22420CED644D27E7C5A4427694883EE21CC411416ADD31601D47') {
    throw '新旧setupの版またはSHA-256が一致しません。'
}
$known = @{
    'tool.json' = '5798B1CF3E82628755221E1877D845B911A64513A1F7F8B42A2E6B34978C7F62'
    'README.md' = 'E4C3A54F8F20DEEA17CB11A23E4343397E6242B37E6BF7E05091598B723AAA08'
    'docs/usage.md' = 'DBD4FCB8EB496860C111561D252FF52BE820F57DD9D177E6C70F48C97FE4F585'
}
foreach ($relative in $known.Keys) {
    if ((Get-FileHash -LiteralPath (Join-Path $prior $relative)).Hash -ne $known[$relative]) { throw '0.1.0配布物の照合に失敗しました。' }
}
function Snapshot([string]$Root) {
    if (-not (Test-Path -LiteralPath $Root)) { return @() }
    return @(Get-ChildItem -LiteralPath $Root -File -Recurse -Force | Sort-Object FullName | ForEach-Object {
        [IO.Path]::GetRelativePath($Root, $_.FullName) + '|' + (Get-FileHash -LiteralPath $_.FullName).Hash
    })
}
function Run-Setup([string]$File, [string[]]$Arguments) {
    $process = Start-Process -FilePath $File -ArgumentList $Arguments -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(180000)) {
        $script:pendingSetup = $process
        throw 'setupの終了を確認できません。強制終了しません。'
    }
    if ($process.ExitCode -ne 0) { throw "setupが終了コード$($process.ExitCode)で終了しました。版別ログを確認してください。" }
}
$originalData = @(Snapshot (Join-Path $originalRoot 'data'))
$originalTools = @(Snapshot (Join-Path $originalRoot 'tools'))
$originalCaptures = @(Snapshot (Join-Path $originalRoot 'Captures'))
$hostSettings = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'KotorinToys/settings.json'
$hostSettingsHash = if (Test-Path -LiteralPath $hostSettings) { (Get-FileHash -LiteralPath $hostSettings).Hash } else { $null }
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'SSCapture.lnk'
$shortcutHash = if (Test-Path -LiteralPath $shortcut) { (Get-FileHash -LiteralPath $shortcut).Hash } else { $null }
New-Item -ItemType Directory -Path $run -Force | Out-Null
$shortcutBackup = Join-Path $run 'original-shortcut.lnk'
if ($null -ne $shortcutHash) {
    Copy-Item -LiteralPath $shortcut -Destination $shortcutBackup
    if ((Get-FileHash -LiteralPath $shortcutBackup).Hash -ne $shortcutHash) { throw 'ショートカットの退避に失敗しました。' }
}
& reg.exe export $nativeKey $backup /y | Out-Null
if ($LASTEXITCODE -ne 0) { throw '元登録の退避に失敗しました。' }
$backupHash = (Get-FileHash -LiteralPath $backup).Hash
$result = [ordered]@{ status='running'; restored=$false; fixtureDataPreserved=$false; originalDataPreserved=$false; checks=@() }
$changed = $false
$script:pendingSetup = $null
try {
    # 固定AppIdの登録だけを退避後に一時的に切り替える。実導入先は変更しない。
    Remove-Item -LiteralPath $key -Recurse
    $changed = $true
    Run-Setup $oldSetup @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOICONS',("/DIR=`"$testRoot`""), ("/LOG=`"$(Join-Path $run 'old-setup.log')`""))
    $old = Get-ItemProperty -LiteralPath $key
    if ($old.DisplayVersion -ne '0.1.1' -or [IO.Path]::GetFullPath([string]$old.InstallLocation).TrimEnd('\') -ne $testRoot -or
        (Get-FileHash -LiteralPath (Join-Path $testRoot 'SSCapture.exe')).Hash -ne '80C8D6106A35CF71965EE3808E61196773BB3863E30818BE6812D976B95C87BA') {
        throw '0.1.1の実導入結果が一致しません。'
    }
    foreach ($relative in $known.Keys) {
        $target = Join-Path $testRoot $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $prior $relative) -Destination $target
    }
    $fixtureHashes = @{}
    foreach ($relative in @('data/settings.json','data/history.json','tools/ffmpeg.exe','Captures/保持画像.png')) {
        $path = Join-Path $testRoot $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
        [IO.File]::WriteAllText($path, '保持確認用fixture')
        $fixtureHashes[$relative] = (Get-FileHash -LiteralPath $path).Hash
    }
    Run-Setup $newSetup @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOICONS',("/LOG=`"$(Join-Path $run 'new-setup.log')`""))
    $updated = Get-ItemProperty -LiteralPath $key
    if ($updated.DisplayVersion -ne '0.1.6' -or [IO.Path]::GetFullPath([string]$updated.InstallLocation).TrimEnd('\') -ne $testRoot) { throw '更新後の登録が一致しません。' }
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $testRoot 'SSCapture.exe'))
    if ($info.ProductName -ne 'SSCapture' -or $info.ProductVersion -ne '0.1.6') { throw '更新後EXEの製品・版が一致しません。' }
    $helper = Join-Path $testRoot 'maintenance/KotorinToys.Maintenance.exe'
    $verify = & $helper --product SSCapture --root $testRoot --operation verify
    if ($LASTEXITCODE -ne 0) { throw '更新後の管理補助による導入検出に失敗しました。' }
    $identity = ($verify -join "`n") | ConvertFrom-Json
    if ($identity.state -ne '導入済み' -or $identity.version -ne '0.1.6') { throw '更新後の製品・版の照合結果が一致しません。' }
    foreach ($relative in $fixtureHashes.Keys) {
        if ((Get-FileHash -LiteralPath (Join-Path $testRoot $relative)).Hash -ne $fixtureHashes[$relative]) { throw '隔離fixtureの設定・履歴が変わりました。' }
    }
    $result.fixtureDataPreserved = $true
    $result.checks += '公開0.1.1と0.1.0残存定義から0.1.6 setupで更新・同じ導入先を継承'
    $result.checks += '実EXEと登録版・隔離fixtureの設定・履歴・撮影結果・FFmpegを照合'
    $result.status = 'passed'
} catch {
    $result.status = 'failed'; $result.error = $_.Exception.Message
    throw
} finally {
    if ($null -ne $script:pendingSetup -and -not $script:pendingSetup.HasExited) {
        $result.status = 'setup-running'; $result.error = 'setupが実行中のため登録を復元していません。'
        $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $run 'result.json') -Encoding utf8NoBOM
        throw 'setupが実行中のため元登録を復元できません。強制終了せず版別記録を確認してください。'
    }
    if ($changed) {
        if (Test-Path -LiteralPath $key) {
            $observed = Get-ItemProperty -LiteralPath $key
            if ([IO.Path]::GetFullPath([string]$observed.InstallLocation).TrimEnd('\') -ne $testRoot) { throw '登録が外部変更されたため自動復元しません。退避済み登録を確認してください。' }
            Remove-Item -LiteralPath $key -Recurse
        }
        if ((Get-FileHash -LiteralPath $backup).Hash -ne $backupHash) { throw '退避済み登録が変更されています。' }
        & reg.exe import $backup | Out-Null
        if ($LASTEXITCODE -ne 0) { throw '元登録の復元に失敗しました。' }
        $restored = Get-ItemProperty -LiteralPath $key
        if ($restored.DisplayName -ne $current.DisplayName -or $restored.DisplayVersion -ne $current.DisplayVersion -or
            [IO.Path]::GetFullPath([string]$restored.InstallLocation).TrimEnd('\') -ne $originalRoot) { throw '元登録の復元結果が一致しません。' }
        $result.restored = $true
        # /NOICONSでも固定名のショートカットが作られるため、変更時は退避した元へ戻す。
        if (Test-Path -LiteralPath $shortcut) {
            $observedHash = (Get-FileHash -LiteralPath $shortcut).Hash
            if ($observedHash -ne $shortcutHash) {
                $observedTarget = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut).TargetPath
                if ($observedTarget -ne (Join-Path $testRoot 'SSCapture.exe')) { throw 'テスト先と異なるショートカットへの外部変更を検出しました。' }
                if ($null -eq $shortcutHash) { Remove-Item -LiteralPath $shortcut }
                else { Copy-Item -LiteralPath $shortcutBackup -Destination $shortcut -Force }
            }
        } elseif ($null -ne $shortcutHash) {
            Copy-Item -LiteralPath $shortcutBackup -Destination $shortcut
        }
        $changedData = @(Compare-Object -ReferenceObject $originalData -DifferenceObject @(Snapshot (Join-Path $originalRoot 'data')))
        $changedTools = @(Compare-Object -ReferenceObject $originalTools -DifferenceObject @(Snapshot (Join-Path $originalRoot 'tools')))
        $changedCaptures = @(Compare-Object -ReferenceObject $originalCaptures -DifferenceObject @(Snapshot (Join-Path $originalRoot 'Captures')))
        $currentHostHash = if (Test-Path -LiteralPath $hostSettings) { (Get-FileHash -LiteralPath $hostSettings).Hash } else { $null }
        $currentShortcutHash = if (Test-Path -LiteralPath $shortcut) { (Get-FileHash -LiteralPath $shortcut).Hash } else { $null }
        if ($changedData.Count -or $changedTools.Count -or $changedCaptures.Count -or $hostSettingsHash -ne $currentHostHash -or $shortcutHash -ne $currentShortcutHash) {
            throw '元の設定・履歴・本体設定またはショートカットが変化しました。確認が必要です。'
        }
        $result.originalDataPreserved = $true
    }
    $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $run 'result.json') -Encoding utf8NoBOM
    Write-Output "旧版更新テスト：$($result.status) / 元登録復元=$($result.restored) / 記録=$run"
}
