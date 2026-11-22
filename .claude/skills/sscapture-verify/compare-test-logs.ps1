[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Before,
    [Parameter(Mandatory)][string]$After
)
$ErrorActionPreference = 'Stop'

# テストログの「PASS 名前」「FAIL 名前: 理由」の行を読む。
function Read-TestLines {
    param([Parameter(Mandatory)][string]$Path)
    $fullPath = [IO.Path]::GetFullPath($Path, (Get-Location).Path)
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { throw "ログがありません: $fullPath" }
    $lines = foreach ($line in [IO.File]::ReadAllLines($fullPath)) {
        if ($line -match '^(PASS|FAIL) (.+)$') { [pscustomobject]@{ Status = $Matches[1]; Text = $Matches[2] } }
    }
    if (-not $lines) { throw "テスト結果の行が見つかりません: $fullPath" }
    return @($lines)
}

# テスト名には「: 状態24」のような区切りを含むものがあるため、失敗行の理由は
# 両方のログに出てくる成功時のテスト名と照合して取り除く。
function ConvertTo-Results {
    param([object[]]$Lines, [string[]]$KnownNames)
    $results = [ordered]@{}
    foreach ($entry in $Lines) {
        $name = $entry.Text
        if ($entry.Status -eq 'FAIL') {
            $known = $KnownNames | Where-Object { $entry.Text -eq $_ -or $entry.Text.StartsWith($_ + ': ') } |
                Sort-Object Length -Descending | Select-Object -First 1
            if ($known) { $name = $known } elseif ($entry.Text -match '^(.+?): ') { $name = $Matches[1] }
        }
        $key = $name
        $index = 2
        while ($results.Contains($key)) { $key = "$name #$index"; $index++ }
        $results[$key] = $entry.Status
    }
    return $results
}

$beforeLines = Read-TestLines -Path $Before
$afterLines = Read-TestLines -Path $After
$knownNames = @($beforeLines + $afterLines | Where-Object Status -EQ 'PASS' | ForEach-Object Text | Sort-Object -Unique)
$old = ConvertTo-Results -Lines $beforeLines -KnownNames $knownNames
$new = ConvertTo-Results -Lines $afterLines -KnownNames $knownNames

function Format-Summary {
    param($Results)
    $failed = @($Results.Values | Where-Object { $_ -eq 'FAIL' }).Count
    '{0}件（成功 {1}・失敗 {2}）' -f $Results.Count, ($Results.Count - $failed), $failed
}
Write-Output ('変更前: ' + (Format-Summary $old))
Write-Output ('変更後: ' + (Format-Summary $new))

$differences = @()
foreach ($name in $old.Keys) {
    if (-not $new.Contains($name)) { $differences += "消えた  [$($old[$name])] $name" }
    elseif ($new[$name] -ne $old[$name]) { $differences += "変化    [$($old[$name]) → $($new[$name])] $name" }
}
foreach ($name in $new.Keys) {
    if (-not $old.Contains($name)) { $differences += "増えた  [$($new[$name])] $name" }
}
foreach ($name in $new.Keys) {
    if ($new[$name] -eq 'FAIL') { Write-Output "変更後の失敗: $name" }
}

if ($differences.Count -eq 0) {
    Write-Output 'テストごとの成否は一致しました。'
    exit 0
}
Write-Output 'テストごとの成否に差があります。'
$differences | ForEach-Object { Write-Output $_ }
exit 1
