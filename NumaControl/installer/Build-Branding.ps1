#requires -Version 5.1
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$logo=[Drawing.Image]::FromFile((Join-Path (Split-Path -Parent $PSScriptRoot) 'assets\app-mark-512.png'))
try {
    foreach($kind in @('banner','dialog')) {
        $height=if($kind -eq 'banner'){58}else{312}; $bitmap=[Drawing.Bitmap]::new(493,$height); $g=[Drawing.Graphics]::FromImage($bitmap)
        try {
            $g.Clear([Drawing.Color]::White); $g.SmoothingMode='AntiAlias'; $g.InterpolationMode='HighQualityBicubic'
            if($kind -eq 'banner') { $g.DrawImage($logo,441,6,44,44) }
            else {
                $brush=[Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#171A24')); $g.FillRectangle($brush,0,0,164,312); $brush.Dispose(); $g.DrawImage($logo,38,48,88,88)
                $font=[Drawing.Font]::new('Segoe UI',17,[Drawing.FontStyle]::Bold); $brush=[Drawing.SolidBrush]::new([Drawing.Color]::White)
                $g.DrawString("NUMA`nCONTROL",$font,$brush,[Drawing.RectangleF]::new(26,159,140,75)); $brush.Dispose(); $font.Dispose()
            }
            $bitmap.Save((Join-Path $PSScriptRoot ($kind+'.bmp')),[Drawing.Imaging.ImageFormat]::Bmp)
        } finally {$g.Dispose();$bitmap.Dispose()}
    }
} finally {$logo.Dispose()}
