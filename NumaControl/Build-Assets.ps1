#requires -Version 5.1
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$assets=Join-Path $PSScriptRoot 'assets'; [void](New-Item -ItemType Directory -Path $assets -Force)
function Draw-Mark([int]$size) {
    $bitmap=[Drawing.Bitmap]::new($size,$size,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g=[Drawing.Graphics]::FromImage($bitmap)
    try {
        $g.SmoothingMode=[Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.ScaleTransform($size/32.0,$size/32.0)
        $shape=[Drawing.Drawing2D.GraphicsPath]::new()
        $shape.AddArc(2,2,14,14,180,90); $shape.AddArc(16,2,14,14,270,90); $shape.AddArc(16,16,14,14,0,90); $shape.AddArc(2,16,14,14,90,90); $shape.CloseFigure()
        $gradient=[Drawing.Drawing2D.LinearGradientBrush]::new([Drawing.RectangleF]::new(2,2,28,28),[Drawing.ColorTranslator]::FromHtml('#C5BCFF'),[Drawing.ColorTranslator]::FromHtml('#9E8DED'),45.0)
        $g.FillPath($gradient,$shape)
        $letter=[Drawing.Drawing2D.GraphicsPath]::new()
        $letter.AddPolygon([Drawing.PointF[]]@([Drawing.PointF]::new(9,24),[Drawing.PointF]::new(9,8),[Drawing.PointF]::new(13,8),[Drawing.PointF]::new(20,18),[Drawing.PointF]::new(20,8),[Drawing.PointF]::new(24,8),[Drawing.PointF]::new(24,24),[Drawing.PointF]::new(20,24),[Drawing.PointF]::new(13,14),[Drawing.PointF]::new(13,24)))
        $ink=[Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#211C35')); $g.FillPath($ink,$letter)
        $ink.Dispose(); $letter.Dispose(); $gradient.Dispose(); $shape.Dispose()
    } finally { $g.Dispose() }
    return ,$bitmap
}
$frames=[Collections.Generic.List[byte[]]]::new()
$sizes=@(256,128,64,48,40,32,24,20,16)
foreach($size in $sizes) {
    $bitmap=Draw-Mark $size
    $memory=[IO.MemoryStream]::new()
    try { $bitmap.Save($memory,[Drawing.Imaging.ImageFormat]::Png); $frames.Add($memory.ToArray()) } finally { $memory.Dispose(); $bitmap.Dispose() }
}
$stream=[IO.File]::Create((Join-Path $PSScriptRoot 'src\app.ico')); $writer=[IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset=6+16*$sizes.Count
    for($i=0;$i -lt $sizes.Count;$i++) {
        $dimension=if($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
        $offset+=$frames[$i].Length
    }
    foreach($frame in $frames) { $writer.Write($frame) }
} finally { $writer.Dispose(); $stream.Dispose() }
$large=Draw-Mark 512
try { $large.Save((Join-Path $assets 'app-mark-512.png'),[Drawing.Imaging.ImageFormat]::Png) } finally { $large.Dispose() }
Write-Host 'App icon: nine sizes, 16-256 px; preview: 512 px.'
