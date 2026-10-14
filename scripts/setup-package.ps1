[CmdletBinding()]
param()
. (Join-Path $PSScriptRoot 'common.ps1')
$compiler = Join-Path $projectRoot '.tools\inno\app\ISCC.exe'
if (Test-Path -LiteralPath $compiler) {
    if ((Get-FileHash -LiteralPath $compiler).Hash -ne '29856cf82d9a993afd7da8f99fb89f24d65ce3271c5b042cf387257c8fd7660f') {
        throw '既存コンパイラーが指定版と異なります。.tools/innoを確認してください。自動置換は行いません。'
    }
    Write-Host 'Inno Setup 6.2.2は配置済みです。'
    return
}
$downloads = Join-Path $projectRoot ('.tools\package-downloads\' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $downloads -Force | Out-Null
function Get-VerifiedDownload([string]$Url, [string]$Name, [string]$Hash) {
    $path = Join-Path $downloads $Name
    Invoke-WebRequest -Uri $Url -OutFile $path
    if ((Get-FileHash -LiteralPath $path).Hash -ne $Hash) { throw "取得ファイルのSHA256が一致しません: $Name" }
    return $path
}
$extractorZip = Get-VerifiedDownload 'https://github.com/UserUnknownFactor/innoextract_win/releases/download/670/innoextract670.zip' 'innoextract670.zip' '79b69b9b1fcd98f42ccd4b245efdf6a03bcfb674ba6af482f5a46891c9ed4d14'
$setup = Get-VerifiedDownload 'https://github.com/jrsoftware/issrc/releases/download/is-6_2_2/innosetup-6.2.2.exe' 'innosetup-6.2.2.exe' '8117d10d00a2ad33a1390978ea3872861c330e087914410a6377b22c4c5b8563'
Expand-Archive -LiteralPath $extractorZip -DestinationPath (Join-Path $downloads 'extractor')
& (Join-Path $downloads 'extractor\innoextract.exe') --extract --output-dir (Join-Path $downloads 'inno') $setup
if ($LASTEXITCODE -ne 0) { throw 'コンパイラーの展開に失敗しました。' }
if ((Get-FileHash -LiteralPath (Join-Path $downloads 'inno\app\ISCC.exe')).Hash -ne '29856cf82d9a993afd7da8f99fb89f24d65ce3271c5b042cf387257c8fd7660f') {
    throw '展開したコンパイラーのSHA256が一致しません。'
}
$destination = Join-Path $projectRoot '.tools\inno'
if (Test-Path -LiteralPath $destination) { throw '既存の.tools/innoがあるため展開を中止しました。' }
Move-Item -LiteralPath (Join-Path $downloads 'inno') -Destination $destination
Write-Host 'Inno Setup 6.2.2を.tools/innoへ配置しました。システムへのインストールは行っていません。'
