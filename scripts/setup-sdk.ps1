[CmdletBinding()]
param([string]$Version = '10.0.401')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$toolDirectory = Join-Path $projectRoot '.tools'
$sdkDirectory = Join-Path $toolDirectory 'dotnet'
function Get-LocalSdkVersion {
    Push-Location -LiteralPath $projectRoot
    try {
        $installedVersion = (& (Join-Path $sdkDirectory 'dotnet.exe') --version | Out-String).Trim()
        if ($LASTEXITCODE -ne 0) { throw 'SDKの確認に失敗しました。' }
        return $installedVersion
    }
    finally { Pop-Location }
}
if (Test-Path -LiteralPath (Join-Path $sdkDirectory 'dotnet.exe')) {
    $installedVersion = Get-LocalSdkVersion
    if ($installedVersion -ne $Version) {
        throw "ローカルSDKの版が一致しません（要求: $Version、検出: $installedVersion）。自動更新は行いません。使用するSDKを確認してください。"
    }
    Write-Output $installedVersion
    return
}
New-Item -ItemType Directory -Path $toolDirectory -Force | Out-Null
$metadata = Invoke-RestMethod -Uri 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'
$sdk = $metadata.releases | ForEach-Object { $_.sdks } | Where-Object version -EQ $Version | Select-Object -First 1
$package = $sdk.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name -eq 'dotnet-sdk-win-x64.zip' } | Select-Object -First 1
if (-not $package) { throw "SDK $Version の公式配布情報が見つかりません。" }
$archive = Join-Path $toolDirectory "dotnet-sdk-$Version.zip"
Invoke-WebRequest -Uri $package.url -OutFile $archive
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash -ne $package.hash) { throw 'SDKのSHA512が一致しません。' }
Expand-Archive -LiteralPath $archive -DestinationPath $sdkDirectory -Force
Get-LocalSdkVersion
