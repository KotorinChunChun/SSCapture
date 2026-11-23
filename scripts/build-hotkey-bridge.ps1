[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SigningModule
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

Import-Module -Name (Resolve-Path -LiteralPath $SigningModule).ProviderPath -Force
$certificate = Get-MsixSigningCertificate -Publisher 'CN=KotorinChunChun'
try {
    $sdkRoot = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Windows Kits/10/bin'
    $signTool = Get-ChildItem -LiteralPath $sdkRoot -Directory |
        Where-Object { $_.Name -as [version] } |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName 'x64/signtool.exe' } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1
    if (-not $signTool) { throw 'Windows SDKのSignToolがありません。' }

    $project = Join-Path $projectRoot 'src/SSCapture.HotkeyBridge/SSCapture.HotkeyBridge.csproj'
    $projectInfo = [xml](Get-Content -LiteralPath $project -Raw)
    $version = [version]$projectInfo.Project.PropertyGroup.Version
    $output = Join-Path $projectRoot ("dev/history/v$version/logs/hotkey-bridge-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $output | Out-Null
    Invoke-Dotnet publish $project -c Release -r win-x64 --self-contained true '-p:PublishAot=true' `
        '-p:RuntimeFrameworkVersion=10.0.15' '-p:DebugType=None' '-p:DebugSymbols=false' -o $output

    $exe = Join-Path $output 'SSCapture.HotkeyBridge.exe'
    $files = @(Get-ChildItem -LiteralPath $output -File)
    if ($files.Count -ne 1 -or $files[0].FullName -ne $exe) { throw '撮影キー補助の単体EXEを確認できません。' }
    & $signTool sign /fd SHA256 /s My /sha1 $certificate.Thumbprint /tr http://timestamp.digicert.com /td SHA256 $exe
    if ($LASTEXITCODE -ne 0) { throw '撮影キー補助の署名に失敗しました。' }
    $signature = Get-AuthenticodeSignature -LiteralPath $exe
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint -or
        $signature.SignerCertificate.Subject -ne $certificate.Subject -or -not $signature.TimeStamperCertificate) {
        throw "撮影キー補助の署名検証に失敗しました。状態: $($signature.Status)"
    }
    $sha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    [ordered]@{ schemaVersion = 1; id = 'ss-capture-hotkey-bridge'; version = $version.ToString()
        file = [IO.Path]::GetFileName($exe); sha256 = $sha256; publisher = $signature.SignerCertificate.Subject
        signerThumbprint = $signature.SignerCertificate.Thumbprint } | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $output 'SSCapture-HotkeyBridge.json') -Encoding utf8NoBOM
    Write-Output "署名済みの撮影キー補助: $exe"
}
finally { $certificate.Dispose() }
