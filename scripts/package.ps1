[CmdletBinding()]
param()
. (Join-Path $PSScriptRoot 'common.ps1')
$metadata = Get-Content -LiteralPath (Join-Path $projectRoot 'tool.json') -Raw | ConvertFrom-Json
$version = [string]$metadata.version
if ($version -notmatch '^\d+\.\d+\.\d+$' -or $metadata.appId -ne 'KotorinToys.SSCapture') { throw '版またはAppIdが不正です。' }
$project = [xml](Get-Content -LiteralPath (Join-Path $projectRoot 'src\SSCapture\SSCapture.csproj') -Raw)
$properties = $project.Project.PropertyGroup
$manifest = [xml](Get-Content -LiteralPath (Join-Path $projectRoot 'src\SSCapture\app.manifest') -Raw)
if ($properties.Version -ne $version -or $properties.InformationalVersion -ne $version -or
    $properties.AssemblyVersion -ne "$version.0" -or $properties.FileVersion -ne "$version.0" -or
    $manifest.assembly.assemblyIdentity.version -ne "$version.0") { throw 'ソースの版表記が一致しません。' }
$compiler = Join-Path $projectRoot '.tools\inno\app\ISCC.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '先にpwsh -File scripts/setup-package.ps1を実行してください。' }
if ((Get-FileHash -LiteralPath $compiler).Hash -ne '29856cf82d9a993afd7da8f99fb89f24d65ce3271c5b042cf387257c8fd7660f' -or
    (Get-FileHash -LiteralPath (Join-Path (Split-Path -Parent $compiler) 'ISCmplr.dll')).Hash -ne '1a8a9332d55229b71749c7b01b8e4c1e34ae958be9d35f6dac76e233cdcf2deb') {
    throw '配布物生成用コンパイラーのSHA256が一致しません。'
}
# 利用中のartifacts/appからコピーせず、通常検証後に毎回新しい発行先を使う。
& (Join-Path $PSScriptRoot 'test.ps1')
$stage = Join-Path $projectRoot ('release\work\' + [Guid]::NewGuid().ToString('N') + '\app')
$output = Join-Path $projectRoot ('release\v' + $version)
New-Item -ItemType Directory -Path $stage, $output -Force | Out-Null
$appProject = Join-Path $projectRoot 'src\SSCapture\SSCapture.csproj'
Invoke-Dotnet publish $appProject -c Release -r win-x64 --self-contained true "-p:RestoreConfigFile=$(Join-Path $projectRoot 'NuGet.Config')" '-p:DebugType=None' '-p:DebugSymbols=false' -o $stage --nologo
Copy-Item -LiteralPath (Join-Path $projectRoot 'licenses\NAudio-LICENSE.txt'), (Join-Path $projectRoot 'THIRD_PARTY.md') -Destination $stage
Copy-Item -LiteralPath (Join-Path (Split-Path -Parent $compiler) 'License.txt') -Destination (Join-Path $stage 'Inno-Setup-LICENSE.txt')
$runtime = Get-Content -LiteralPath (Join-Path $stage 'SSCapture.runtimeconfig.json') -Raw | ConvertFrom-Json
if (-not $runtime.runtimeOptions.includedFrameworks -or -not (Test-Path -LiteralPath (Join-Path $stage 'coreclr.dll'))) { throw 'ランタイムが同梱されていません。' }
foreach ($framework in $runtime.runtimeOptions.includedFrameworks) {
    if ($framework.name -notin @('Microsoft.NETCore.App','Microsoft.WindowsDesktop.App') -or $framework.version -notmatch '^10\.0\.\d+$') { throw '未対応のランタイムです。' }
    $pack = Join-Path $env:NUGET_PACKAGES ($framework.name.ToLowerInvariant() + '.runtime.win-x64\' + $framework.version)
    $license = if ($framework.name -eq 'Microsoft.NETCore.App') { 'LICENSE.TXT' } else { 'LICENSE' }
    Copy-Item -LiteralPath (Join-Path $pack $license) -Destination (Join-Path $stage ($framework.name + '-LICENSE.txt'))
    if ($framework.name -eq 'Microsoft.NETCore.App') {
        Copy-Item -LiteralPath (Join-Path $pack 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $stage 'Microsoft.NETCore.App-NOTICES.txt')
    }
}
$entries = @(Get-ChildItem -LiteralPath $stage -Force -Recurse)
$locales = @('cs','de','es','fr','it','ja','ko','pl','pt-BR','ru','tr','zh-Hans','zh-Hant')
foreach ($entry in $entries) {
    $relative = [IO.Path]::GetRelativePath($stage, $entry.FullName)
    if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        ($entry.PSIsContainer -and $relative -notin $locales) -or
        (-not $entry.PSIsContainer -and ($entry.Extension -notin @('.exe','.dll','.json','.txt','.md') -or $entry.Name -match '^(ffmpeg|ffprobe|oneocr|onnxruntime|settings|last-region)')) -or
        (-not $entry.PSIsContainer -and $relative.Contains('\') -and ($relative.Split('\').Count -ne 2 -or $relative.Split('\')[0] -notin $locales -or $entry.Name -notmatch '\.resources\.dll$'))) {
        throw "配布対象外のフォルダ・ファイルがあります: $relative"
    }
}
$files = @($entries | Where-Object { -not $_.PSIsContainer })
$app = Join-Path $stage 'SSCapture.exe'
if ([Diagnostics.FileVersionInfo]::GetVersionInfo($app).ProductVersion -ne $version -or
    [Diagnostics.FileVersionInfo]::GetVersionInfo($app).FileVersion -ne "$version.0" -or
    [Reflection.AssemblyName]::GetAssemblyName((Join-Path $stage 'SSCapture.dll')).Version.ToString() -ne "$version.0") { throw '発行版が一致しません。' }
& $compiler "/DAppVersion=$version" "/DSourceDir=$stage" "/DOutputDir=$output" (Join-Path $projectRoot 'installer\SSCapture.iss')
if ($LASTEXITCODE -ne 0) { throw "インストーラー生成失敗: $LASTEXITCODE" }
$fileName = "SSCapture-$version-setup.exe"
$hash = (Get-FileHash -LiteralPath (Join-Path $output $fileName)).Hash.ToLowerInvariant()
$packages = [ordered]@{
    schemaVersion = 1
    distributionStatus = 'published'
    packages = @([ordered]@{ id = $metadata.id; name = $metadata.name; version = $version; appId = $metadata.appId; file = $fileName; sha256 = $hash; signature = 'unsigned-public-build' })
}
$packages | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'SSCapture-packages.json') -Encoding utf8NoBOM
"$hash  $fileName" | Set-Content -LiteralPath (Join-Path $output 'SSCapture-SHA256SUMS.txt') -Encoding utf8NoBOM
# 作業先の目録は公開用の3ファイルへ混ぜない。
$files | ForEach-Object { [ordered]@{ file = [IO.Path]::GetRelativePath($stage, $_.FullName); sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant() } } |
    ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path (Split-Path -Parent $stage) 'payload.json') -Encoding utf8NoBOM
Write-Host "配布物生成完了: $output"
Write-Host "SHA256: $hash"
Write-Host 'プッシュ・タグ・Release公開は行っていません。'
