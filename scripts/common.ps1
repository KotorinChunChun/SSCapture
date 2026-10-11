$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$dotnetExe = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { 'dotnet' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools\cli'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.tools\nuget'
function Invoke-Dotnet {
    & $dotnetExe @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $LASTEXITCODE" }
}

function Get-BuildDependencyPaths {
    param([Parameter(Mandatory)][string]$Root)
    $mediaRoot = Join-Path $Root '.tools\ffmpeg\ffmpeg-9.0.2-essentials_build'
    @(
        (Join-Path $Root 'src\SSCapture\SSCapture.csproj')
        (Join-Path $Root 'tests\SSCapture.Tests\SSCapture.Tests.csproj')
        (Join-Path $Root 'NuGet.Config')
        (Join-Path $mediaRoot 'bin\ffmpeg.exe')
        (Join-Path $mediaRoot 'bin\ffprobe.exe')
        (Join-Path $mediaRoot 'LICENSE')
        (Join-Path $mediaRoot 'README.txt')
        (Join-Path $Root 'licenses\NAudio-LICENSE.txt')
        (Join-Path $Root 'THIRD_PARTY.md')
    )
}

function Assert-BuildDependencies {
    param([Parameter(Mandatory)][string]$Root)
    $missing = @(Get-BuildDependencyPaths -Root $Root | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) })
    if ($missing.Count -gt 0) {
        throw "ビルドに必要なファイルがありません。依存物のセットアップとリポジトリ内容を確認してください。`n$($missing -join "`n")"
    }
}

function Get-PublishProcesses {
    # Limit the result to possible owners of the files this build publishes.
    # Query errors and inaccessible metadata are never treated as an idle app.
    Get-CimInstance -ClassName Win32_Process -Filter "Name = 'SSCapture.exe' OR Name = 'ffmpeg.exe' OR Name = 'ffprobe.exe' OR Name = 'dotnet.exe' OR Name = 'corerun.exe'" `
        -Property Name, ProcessId, ExecutablePath, CommandLine -ErrorAction Stop
}

function ConvertTo-PublishPath {
    param([Parameter(Mandatory)][string]$Path)
    if (-not [IO.Path]::IsPathFullyQualified($Path)) { throw '絶対パスではありません。' }
    $normalized = [IO.Path]::GetFullPath($Path).Replace('/', '\')
    if ($normalized.StartsWith('\\?\UNC\', [StringComparison]::OrdinalIgnoreCase)) { $normalized = '\\' + $normalized.Substring(8) }
    elseif ($normalized.StartsWith('\\?\', [StringComparison]::OrdinalIgnoreCase)) { $normalized = $normalized.Substring(4) }
    return $normalized.TrimEnd('\')
}

function Assert-PublishIdle {
    param(
        [Parameter(Mandatory)][string]$AppDirectory,
        [scriptblock]$ReadProcesses = { Get-PublishProcesses }
    )
    $targets = @{
        'SSCapture.exe' = ConvertTo-PublishPath (Join-Path $AppDirectory 'SSCapture.exe')
        'ffmpeg.exe' = ConvertTo-PublishPath (Join-Path $AppDirectory 'tools\ffmpeg.exe')
        'ffprobe.exe' = ConvertTo-PublishPath (Join-Path $AppDirectory 'tools\ffprobe.exe')
    }
    $targetAssembly = ConvertTo-PublishPath (Join-Path $AppDirectory 'SSCapture.dll')
    try { $processes = @(& $ReadProcesses) }
    catch { throw "プロセスの稼働状況を確認できないため、発行を中止しました: $($_.Exception.Message)" }
    foreach ($process in $processes) {
        if ($process.ProcessId -eq $PID) { continue }
        $name = [string]$process.Name
        $commandLine = [string]$process.CommandLine
        $isHost = $name -in @('dotnet.exe', 'corerun.exe')
        $mentionsAssembly = $commandLine -match '(?i)SSCapture\.dll'
        if ([string]::IsNullOrWhiteSpace($name)) { throw '名前を確認できないプロセスがあるため、発行を中止しました。' }
        if (-not $targets.ContainsKey($name) -and -not $isHost) { continue }
        $identity = "$name (PID $($process.ProcessId))"
        try { $executablePath = ConvertTo-PublishPath ([string]$process.ExecutablePath) }
        catch { throw "$identity の実行パスを確認できないため、発行を中止しました。" }
        if ($targets.ContainsKey($name) -and $executablePath.Equals($targets[$name], [StringComparison]::OrdinalIgnoreCase)) {
            throw "$identity が更新対象を使用中です。録画・出力を完了してトレイから終了後、再実行してください。自動停止は行いません。"
        }
        if ($isHost -and [string]::IsNullOrWhiteSpace($commandLine)) {
            throw "$identity の起動引数を確認できないため、発行を中止しました。"
        }
        if (-not $isHost -or -not $mentionsAssembly) { continue }
        $references = [regex]::Matches($commandLine, '(?i)"(?<path>[^"]*SSCapture\.dll)"|(?<path>[^\s"]*SSCapture\.dll)(?=\s|$)')
        if ($references.Count -eq 0) { throw "$identity のSSCapture.dll指定を判別できないため、発行を中止しました。" }
        foreach ($reference in $references) {
            try { $assemblyPath = ConvertTo-PublishPath $reference.Groups['path'].Value }
            catch { throw "$identity のSSCapture.dll指定が相対パスまたは不明なため、発行を中止しました。" }
            if ($assemblyPath.Equals($targetAssembly, [StringComparison]::OrdinalIgnoreCase)) {
                throw "$identity が更新対象のSSCapture.dllを使用中です。終了後に再実行してください。自動停止は行いません。"
            }
        }
    }
}
