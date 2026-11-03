#requires -Version 5.1
param([Parameter(Mandatory=$true)][string]$Directory)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$bitmap=[Drawing.Bitmap]::new(64,64,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g=[Drawing.Graphics]::FromImage($bitmap)
try {
    $g.SmoothingMode=[Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([Drawing.ColorTranslator]::FromHtml('#162839'))
    $brush=[Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#20DDB3'))
    $g.FillEllipse($brush,7,7,50,50); $brush.Dispose()
    $brush=[Drawing.SolidBrush]::new([Drawing.Color]::White)
    $g.FillPolygon($brush,[Drawing.Point[]]@([Drawing.Point]::new(26,19),[Drawing.Point]::new(26,45),[Drawing.Point]::new(46,32))); $brush.Dispose()
    $png=Join-Path $Directory 'fixture-game.png'; $bitmap.Save($png,[Drawing.Imaging.ImageFormat]::Png)
} finally { $g.Dispose(); $bitmap.Dispose() }
$bytes=[IO.File]::ReadAllBytes($png); $icon=Join-Path $Directory 'fixture-game.ico'; $writer=[IO.BinaryWriter]::new([IO.File]::Create($icon))
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]1)
    $writer.Write([byte]64); $writer.Write([byte]64); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$bytes.Length); $writer.Write([uint32]22); $writer.Write($bytes)
} finally { $writer.Dispose() }
$fixture=Join-Path $Directory ('NumaIconFixture'+[Guid]::NewGuid().ToString('N').Substring(0,8)+'.exe')
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:exe /platform:x64 /optimize+ "/win32icon:$icon" "/out:$fixture" (Join-Path $PSScriptRoot 'Fixture.cs')
if($LASTEXITCODE -ne 0) { throw 'Icon fixture compilation failed.' }
