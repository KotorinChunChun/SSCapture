$ErrorActionPreference = 'Stop'

function Resolve-PayloadPath([string]$Root, [string]$Relative) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative) -or $Relative.Contains(':')) { throw '配布参照が相対パスではありません。' }
    $base = [IO.Path]::GetFullPath($Root).TrimEnd('\','/')
    $path = [IO.Path]::GetFullPath((Join-Path $base $Relative))
    if (-not $path.StartsWith($base + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '配布参照が範囲外です。' }
    $current = $path
    while ($current.Length -ge $base.Length) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw '配布参照にリンクがあります。' }
        if ($current -eq $base) { break }
        $current = Split-Path -Parent $current
    }
    return $path
}

function Get-CurrentRuntimeVersion {
    $metadata = Invoke-RestMethod 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json' -TimeoutSec 30
    $version = [string]$metadata.'latest-runtime'
    $release = @($metadata.releases | Where-Object { $_.runtime.version -eq $version }) | Select-Object -First 1
    if ($version -notmatch '^10\.0\.\d+$' -or $release.windowsdesktop.version -ne $version) { throw '公式ランタイム情報を照合できません。' }
    return $version
}

function Assert-PayloadLinks([string]$Root) {
    foreach ($file in Get-ChildItem -LiteralPath $Root -Filter '*.md' -Recurse -File) {
        foreach ($match in [regex]::Matches([IO.File]::ReadAllText($file.FullName), '\[[^\]]*\]\(([^)]+)\)')) {
            $target = $match.Groups[1].Value.Trim('<','>')
            if ($target -match '^https?://|^#|^mailto:') { continue }
            $relative = [Uri]::UnescapeDataString(($target -split '#', 2)[0])
            $documentBase = [IO.Path]::GetRelativePath($Root, $file.DirectoryName)
            if ([IO.Path]::IsPathRooted($relative) -or $relative.Contains(':')) { throw "説明書の参照が不正です: $($file.Name)" }
            $resolved = Resolve-PayloadPath $Root (Join-Path $documentBase $relative)
            if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) { throw "説明書の参照切れ: $($file.Name) -> $relative" }
        }
    }
}

function Assert-ManagedPayload {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][string]$RuntimeVersion,
        [scriptblock]$ReadVersion = { param($Path) [Diagnostics.FileVersionInfo]::GetVersionInfo($Path) }
    )
    $required = @('SSCapture.exe','SSCapture.dll','SSCapture.deps.json','SSCapture.runtimeconfig.json','tool.json','README.md',
        'coreclr.dll','hostfxr.dll','hostpolicy.dll','System.Private.CoreLib.dll','System.Windows.Forms.dll',
        'maintenance/KotorinToys.Maintenance.exe','maintenance/KotorinToys.Maintenance.dll',
        'maintenance/KotorinToys.Maintenance.deps.json','maintenance/KotorinToys.Maintenance.runtimeconfig.json',
        'maintenance/coreclr.dll','maintenance/hostfxr.dll','maintenance/hostpolicy.dll','maintenance/System.Private.CoreLib.dll')
    foreach ($relative in $required) {
        if (-not (Test-Path -LiteralPath (Resolve-PayloadPath $Root $relative) -PathType Leaf)) { throw "管理用必須物がありません: $relative" }
    }
    foreach ($item in Get-ChildItem -LiteralPath $Root -Recurse -Force) {
        $relative = [IO.Path]::GetRelativePath($Root, $item.FullName).Replace('\','/')
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "配布物にリンクがあります: $relative" }
        if ($relative -match '(^|/)(data|Captures|tools|\.git|private|temp|node_modules)(/|$)' -or
            $item.Name -match '^\.env($|\.)|^(credentials|secrets|settings|last-region)\.json$|^(ffmpeg|ffprobe|oneocr|onnxruntime)(\.|$)' -or
            (-not $item.PSIsContainer -and $item.Extension -notin @('.exe','.dll','.json','.txt','.md'))) {
            throw "配布禁止ファイルがあります: $relative"
        }
    }
    $definition = Get-Content -LiteralPath (Join-Path $Root 'tool.json') -Raw | ConvertFrom-Json
    if ($definition.schemaVersion -ne 1 -or $definition.id -cne 'ss-capture' -or $definition.name -cne 'SSCapture' -or
        $definition.appId -cne 'KotorinToys.SSCapture' -or $definition.version -cne $Version -or
        $definition.repositoryUrl -cne 'https://github.com/KotorinChunChun/SSCapture' -or
        $definition.executable -cne 'SSCapture.exe' -or $definition.installedExecutable -cne 'SSCapture.exe' -or
        $definition.supportedEnvironment -cne 'windows11-x64') { throw '配布定義の製品識別・版・配置が一致しません。' }
    foreach ($ability in @('status','show','stop','silent-start')) {
        if ($ability -cnotin $definition.capabilities) { throw "管理能力が不足しています: $ability" }
    }
    foreach ($reference in @($definition.manual, $definition.readme)) {
        if (-not (Test-Path -LiteralPath (Resolve-PayloadPath $Root $reference) -PathType Leaf)) { throw '製品定義の説明書参照がありません。' }
    }
    if ($definition.icon -and -not (Test-Path -LiteralPath (Resolve-PayloadPath $Root $definition.icon) -PathType Leaf)) { throw '製品定義のアイコン参照がありません。' }
    $appInfo = & $ReadVersion (Join-Path $Root 'SSCapture.exe')
    if ($appInfo.ProductName -cne 'SSCapture' -or $appInfo.ProductVersion -ne $Version -or $appInfo.FileVersion -ne "$Version.0") { throw '配布EXEの製品名・版が一致しません。' }
    $desktopInfo = & $ReadVersion (Join-Path $Root 'System.Windows.Forms.dll')
    if ($desktopInfo.ProductVersion -notmatch ('^' + [regex]::Escape($RuntimeVersion) + '(\+|$)')) { throw 'Windows Desktopランタイム実体の版が一致しません。' }
    foreach ($config in @('SSCapture.runtimeconfig.json','maintenance/KotorinToys.Maintenance.runtimeconfig.json')) {
        $options = (Get-Content -LiteralPath (Join-Path $Root $config) -Raw | ConvertFrom-Json).runtimeOptions
        $expected = if ($config.StartsWith('maintenance/')) { @('Microsoft.NETCore.App') } else { @('Microsoft.NETCore.App','Microsoft.WindowsDesktop.App') }
        if ($options.framework -or $options.frameworks -or @($options.includedFrameworks).Count -ne $expected.Count) { throw "自己完結型ではありません: $config" }
        foreach ($framework in $options.includedFrameworks) {
            if ($framework.name -notin $expected -or $framework.version -ne $RuntimeVersion) { throw "同梱ランタイムが公式採用版と一致しません: $config" }
        }
        if (@($options.includedFrameworks.name | Sort-Object -Unique).Count -ne $expected.Count) { throw "同梱ランタイムの種類が不足しています: $config" }
        $runtimeRoot = Split-Path -Parent (Join-Path $Root $config)
        $runtimeInfo = & $ReadVersion (Join-Path $runtimeRoot 'coreclr.dll')
        $coreInfo = & $ReadVersion (Join-Path $runtimeRoot 'System.Private.CoreLib.dll')
        if ($coreInfo.ProductVersion -notmatch ('^' + [regex]::Escape($RuntimeVersion) + '(-servicing\.\d+\.\d+)?(\+|$)')) { throw "ランタイム実体の版が一致しません: $config" }
        # native coreclrのProductVersionはカンマ区切りのビルド番号。管理DLLと数値4部を照合する。
        foreach ($part in @('FileMajorPart','FileMinorPart','FileBuildPart','FilePrivatePart')) {
            if ($null -eq $runtimeInfo.$part -or $runtimeInfo.$part -ne $coreInfo.$part) { throw "CoreCLRとCoreLibの実体が一致しません: $config" }
        }
    }
    Assert-PayloadLinks $Root
}

function Copy-ManagedDocuments([string]$Source, [string]$Destination, [string]$Version) {
    $documents = @('README.md','docs/usage.md','docs/recovery.md','THIRD_PARTY.md')
    foreach ($relative in $documents) {
        $sourceFile = Resolve-PayloadPath $Source $relative
        $targetFile = Resolve-PayloadPath $Destination $relative
        $sourceDirectory = Split-Path -Parent $sourceFile
        $content = [IO.File]::ReadAllText($sourceFile)
        $content = [regex]::Replace($content, '(\[[^\]]*\])\(([^)]+)\)', {
            param($match)
            $link = $match.Groups[2].Value
            if ($link -match '^https?://|^#|^mailto:') { return $match.Value }
            $parts = $link -split '#', 2
            $resolved = [IO.Path]::GetFullPath((Join-Path $sourceDirectory ([Uri]::UnescapeDataString($parts[0]))))
            $repositoryPath = [IO.Path]::GetRelativePath($Source, $resolved).Replace('\','/')
            $null = Resolve-PayloadPath $Source $repositoryPath
            if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) { throw "説明書の元参照がありません: $repositoryPath" }
            if ($repositoryPath -in $documents) { return $match.Value }
            $encoded = (($repositoryPath -split '/') | ForEach-Object { [Uri]::EscapeDataString($_) }) -join '/'
            $fragment = if ($parts.Count -eq 2) { '#' + $parts[1] } else { '' }
            return $match.Groups[1].Value + "(https://github.com/KotorinChunChun/SSCapture/blob/v$Version/$encoded$fragment)"
        })
        New-Item -ItemType Directory -Path (Split-Path -Parent $targetFile) -Force | Out-Null
        [IO.File]::WriteAllText($targetFile, $content, [Text.UTF8Encoding]::new($false))
    }
}

function Get-PayloadManifest([string]$Root) {
    @(
        Get-ChildItem -LiteralPath $Root -File -Recurse -Force | Sort-Object FullName | ForEach-Object {
            $relative = [IO.Path]::GetRelativePath($Root, $_.FullName).Replace('\','/')
            if ($relative -ne 'payload-manifest.json') {
                [ordered]@{ path = $relative; length = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
            }
        }
    )
}

function Assert-PayloadManifest([string]$Root, [object[]]$Files) {
    $actual = @(Get-PayloadManifest $Root)
    if ($actual.Count -ne $Files.Count) { throw '配布目録とファイル数が一致しません。' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $Files) {
        if (-not $seen.Add([string]$entry.path)) { throw '配布目録に重複があります。' }
        $path = Resolve-PayloadPath $Root $entry.path
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-Item -LiteralPath $path).Length -ne $entry.length -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.sha256) { throw "配布目録と実体が一致しません: $($entry.path)" }
    }
}
