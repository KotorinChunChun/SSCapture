[CmdletBinding()]
param([Parameter(Mandatory)][string]$Directory, [string]$Presentation = 'clipboard-colors.pptx')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($Directory)
Add-Type -AssemblyName System.Drawing
function Compare-Pixels([string]$First, [string]$Second) {
    $a = [Drawing.Bitmap]::new($First); $b = [Drawing.Bitmap]::new($Second)
    try {
        if ($a.Size -ne $b.Size) { return -1 }
        $different = 0L
        for ($y=0; $y -lt $a.Height; $y++) {
            for ($x=0; $x -lt $a.Width; $x++) {
                if ($a.GetPixel($x,$y).ToArgb() -ne $b.GetPixel($x,$y).ToArgb()) { $different++ }
            }
        }
        return $different
    } finally { $a.Dispose(); $b.Dispose() }
}
function Inspect-Png([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ([Convert]::ToHexString($bytes[0..7]) -ne '89504E470D0A1A0A') { throw "PNGではありません: $Path" }
    $paletteEntries = 0
    for ($offset = 8; $offset + 12 -le $bytes.Length;) {
        $length = [Buffers.Binary.BinaryPrimitives]::ReadUInt32BigEndian([byte[]]$bytes[$offset..($offset+3)])
        $type = [Text.Encoding]::ASCII.GetString($bytes, $offset+4, 4)
        if ($type -eq 'PLTE') { $paletteEntries = $length / 3 }
        $offset += 12 + $length
    }
    $bitmap = [Drawing.Bitmap]::new($Path)
    try {
        $colors = [Collections.Generic.HashSet[int]]::new()
        for ($y=0; $y -lt $bitmap.Height; $y++) {
            for ($x=0; $x -lt $bitmap.Width; $x++) { $null = $colors.Add($bitmap.GetPixel($x,$y).ToArgb()) }
        }
        [ordered]@{
            file=[IO.Path]::GetRelativePath($root,$Path); width=$bitmap.Width; height=$bitmap.Height
            pngBitDepth=[int]$bytes[24]; pngColorType=[int]$bytes[25]; paletteEntries=$paletteEntries
            uniqueRgbaColors=$colors.Count
            decodedPixelFormat=$bitmap.PixelFormat.ToString(); length=$bytes.Length
            sha256=(Get-FileHash -LiteralPath $Path).Hash
        }
    } finally { $bitmap.Dispose() }
}
$images = @(Get-ChildItem -LiteralPath $root -Filter '*.png' -File | ForEach-Object { Inspect-Png $_.FullName })
$presentationPath = Join-Path $root $Presentation
$comparisons = @()
foreach ($depth in @('Indexed8','Indexed4')) {
    $reference = Join-Path $root ($depth + '.png')
    $readback = Join-Path $root ($depth + '-clipboard-readback.png')
    if ((Test-Path -LiteralPath $reference) -and (Test-Path -LiteralPath $readback)) {
        $comparisons += [ordered]@{ media=($depth + '-clipboard-readback.png'); reference=$depth; differentPixels=(Compare-Pixels $reference $readback) }
    }
}
if (Test-Path -LiteralPath $presentationPath) {
    $zip = [IO.Compression.ZipFile]::OpenRead($presentationPath)
    try {
        $mediaDirectory = Join-Path $root 'extracted-media'
        New-Item -ItemType Directory -Path $mediaDirectory -Force | Out-Null
        foreach ($entry in $zip.Entries | Where-Object FullName -Match '^ppt/media/[^/]+\.png$') {
            if ($entry.Length -gt 20MB) { throw '想定より大きい検証画像です。' }
            $destination = Join-Path $mediaDirectory ([IO.Path]::GetFileName($entry.FullName))
            if (Test-Path -LiteralPath $destination) { throw '既存の抽出結果は上書きしません。' }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$destination)
            $images += Inspect-Png $destination
            foreach ($depth in @('Indexed8','Indexed4')) {
                $reference = Join-Path $root ($depth + '.png')
                if (Test-Path -LiteralPath $reference) {
                    $comparisons += [ordered]@{ media=$entry.FullName; reference=$depth; differentPixels=(Compare-Pixels $reference $destination) }
                }
            }
        }
        $mediaEntries = @($zip.Entries | Where-Object FullName -Like 'ppt/media/*' | Select-Object FullName,Length)
    } finally { $zip.Dispose() }
}
$result = [ordered]@{ images=$images; mediaEntries=$mediaEntries; comparisons=$comparisons }
$result | ConvertTo-Json -Depth 8 | Tee-Object -FilePath (Join-Path $root 'analysis.json')
