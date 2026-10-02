param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$assets=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $assets -Force | Out-Null
$source=[Drawing.Image]::FromFile((Join-Path $PSScriptRoot 'assets\logo.png'))
function LogoPng([int]$size) {
    $bitmap=New-Object Drawing.Bitmap($size,$size)
    $graphics=[Drawing.Graphics]::FromImage($bitmap)
    $stream=New-Object IO.MemoryStream
    try {
        $graphics.Clear([Drawing.Color]::Transparent)
        $graphics.InterpolationMode=[Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode=[Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.CompositingQuality=[Drawing.Drawing2D.CompositingQuality]::HighQuality
        $edge=[Math]::Min($size/[double]$source.Width,$size/[double]$source.Height)
        $width=[int][Math]::Round($source.Width*$edge); $height=[int][Math]::Round($source.Height*$edge)
        $graphics.DrawImage($source,[int](($size-$width)/2),[int](($size-$height)/2),$width,$height)
        $bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png)
        return ,$stream.ToArray()
    } finally {$stream.Dispose(); $graphics.Dispose(); $bitmap.Dispose()}
}
try {
    [IO.File]::WriteAllBytes((Join-Path $assets 'logo-ui.png'),(LogoPng 256))
    $sizes=@(16,24,32,48,64,128,256)
    $images=@(); foreach($size in $sizes){$images+=,(LogoPng $size)}
    $file=[IO.File]::Create((Join-Path $assets 'webcoding.ico'))
    $writer=New-Object IO.BinaryWriter($file)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
        $offset=6+16*$sizes.Count
        for($i=0;$i -lt $sizes.Count;$i++) {
            $dimension=if($sizes[$i] -eq 256){0}else{$sizes[$i]}
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
            $offset+=$images[$i].Length
        }
        foreach($image in $images){$writer.Write([byte[]]$image)}
    } finally {$writer.Dispose(); $file.Dispose()}
} finally {$source.Dispose()}
