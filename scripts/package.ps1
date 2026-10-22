[CmdletBinding()]
param([switch]$PublicRelease)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
. (Join-Path $PSScriptRoot 'managed-payload.ps1')
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
$extractors = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot '.tools/package-downloads') -Filter innoextract.exe -File -Recurse |
    Where-Object { (Get-FileHash -LiteralPath $_.FullName).Hash -eq '0DAB51583E403970065DB49F87A6A2BF683AB6EBFDF27BEDE2BF633DD232A1A2' })
if ($extractors.Count -eq 0) { throw '固定SHA-256の非実行展開ツールがありません。setup-package.ps1の取得結果を確認してください。' }
$extractor = $extractors[0].FullName
$runtimeVersion = Get-CurrentRuntimeVersion
$work = Join-Path $projectRoot ("dev/history/v$version/logs/package-" + [Guid]::NewGuid().ToString('N'))
$stage = Join-Path $work 'app'
$output = Join-Path $projectRoot ("release/v$version")
New-Item -ItemType Directory -Path $stage, $output -Force | Out-Null
if (@(Get-ChildItem -LiteralPath $output -File).Count -ne 0) { throw '出力先に既存資産があります。内容を確認し退避してから再生成してください。' }
& (Join-Path $PSScriptRoot 'test.ps1') -OutputPath (Join-Path $work 'tests')
& (Join-Path $PSScriptRoot 'test-managed-payload.ps1')
Invoke-Dotnet run --project (Join-Path $projectRoot 'tests/SSCapture.Maintenance.Tests/SSCapture.Maintenance.Tests.csproj') -c Release -- (Join-Path $work 'maintenance-tests')
foreach ($component in @(
    @{ Project = 'src/SSCapture/SSCapture.csproj'; Destination = $stage },
    @{ Project = 'src/SSCapture.Maintenance/SSCapture.Maintenance.csproj'; Destination = (Join-Path $stage 'maintenance') }
)) {
    Invoke-Dotnet publish (Join-Path $projectRoot $component.Project) -c Release -r win-x64 --self-contained true "-p:RestoreConfigFile=$(Join-Path $projectRoot 'NuGet.Config')" '-p:DebugType=None' '-p:DebugSymbols=false' -o $component.Destination --nologo
}
Copy-ManagedDocuments $projectRoot $stage $version
$metadata.executable = 'SSCapture.exe'
$metadata.distributionStatus = if ($PublicRelease) { 'published' } else { 'unpublished' }
$metadata | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $stage 'tool.json') -Encoding utf8NoBOM
Copy-Item -LiteralPath (Join-Path $projectRoot 'licenses/NAudio-LICENSE.txt') -Destination $stage
Copy-Item -LiteralPath (Join-Path (Split-Path -Parent $compiler) 'License.txt') -Destination (Join-Path $stage 'Inno-Setup-LICENSE.txt')
foreach ($configuration in Get-ChildItem -LiteralPath $stage -Filter '*.runtimeconfig.json' -Recurse -File) {
    $options = (Get-Content -LiteralPath $configuration.FullName -Raw | ConvertFrom-Json).runtimeOptions
    foreach ($framework in $options.includedFrameworks) {
        if ($framework.name -notin @('Microsoft.NETCore.App','Microsoft.WindowsDesktop.App') -or $framework.version -ne $runtimeVersion) { throw '同梱ランタイムが公式採用版と一致しません。' }
        $pack = Join-Path $env:NUGET_PACKAGES ($framework.name.ToLowerInvariant() + '.runtime.win-x64/' + $framework.version)
        $license = if ($framework.name -eq 'Microsoft.NETCore.App') { 'LICENSE.TXT' } else { 'LICENSE' }
        Copy-Item -LiteralPath (Join-Path $pack $license) -Destination (Join-Path $configuration.DirectoryName ($framework.name + '-LICENSE.txt'))
        if ($framework.name -eq 'Microsoft.NETCore.App') {
            Copy-Item -LiteralPath (Join-Path $pack 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $configuration.DirectoryName 'Microsoft.NETCore.App-NOTICES.txt')
        }
    }
}
if ([Reflection.AssemblyName]::GetAssemblyName((Join-Path $stage 'SSCapture.dll')).Version.ToString() -ne "$version.0") { throw 'アセンブリ版が一致しません。' }
$payload = [ordered]@{ schemaVersion = 1; id = 'ss-capture'; version = $version; files = @(Get-PayloadManifest $stage) }
$payload | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stage 'payload-manifest.json') -Encoding utf8NoBOM
Assert-ManagedPayload $stage $version $runtimeVersion
Assert-PayloadManifest $stage $payload.files
& (Join-Path $PSScriptRoot 'test-public-payload-security.ps1') -Payload $stage -ReportDirectory (Join-Path $work 'security')
& $compiler "/DAppVersion=$version" "/DSourceDir=$stage" "/DOutputDir=$output" (Join-Path $projectRoot 'installer/SSCapture.iss')
if ($LASTEXITCODE -ne 0) { throw "インストーラー生成失敗: $LASTEXITCODE" }
$fileName = "SSCapture-$version-setup.exe"
$extracted = Join-Path $work 'extracted'
& $extractor --extract --output-dir $extracted (Join-Path $output $fileName)
if ($LASTEXITCODE -ne 0) { throw '生成したsetupを非実行展開できません。' }
foreach ($relative in @('app','tmp/payload')) {
    $root = Join-Path $extracted $relative
    Assert-ManagedPayload $root $version $runtimeVersion
    Assert-PayloadManifest $root $payload.files
    if ((Get-FileHash -LiteralPath (Join-Path $root 'payload-manifest.json')).Hash -ne
        (Get-FileHash -LiteralPath (Join-Path $stage 'payload-manifest.json')).Hash) { throw 'setup内の目録が発行元と一致しません。' }
}
if (@(Get-ChildItem -LiteralPath $extracted -File -Recurse).Count -ne 2 * ($payload.files.Count + 1)) { throw 'setup内に想定外の追加ファイルがあります。' }
$hash = (Get-FileHash -LiteralPath (Join-Path $output $fileName)).Hash.ToLowerInvariant()
[ordered]@{
    schemaVersion = 1; distributionStatus = $metadata.distributionStatus
    packages = @([ordered]@{ id = $metadata.id; name = $metadata.name; version = $version; appId = $metadata.appId; file = $fileName; sha256 = $hash; signature = $(if ($PublicRelease) { 'unsigned-public-build' } else { 'unsigned-local-build' }) })
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'SSCapture-packages.json') -Encoding utf8NoBOM
"$hash  $fileName" | Set-Content -LiteralPath (Join-Path $output 'SSCapture-SHA256SUMS.txt') -Encoding utf8NoBOM
[ordered]@{ version = $version; runtime = $runtimeVersion; setupSha256 = $hash; files = $payload.files.Count; work = $work; completedUtc = [DateTime]::UtcNow.ToString('O') } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $work 'receipt.json') -Encoding utf8NoBOM
Write-Host "ローカル検証用配布物: $output"
Write-Host "検査記録: $work"
Write-Host 'プッシュ・タグ・Release公開は行っていません。'
