# Renders the Neon Undelete icon + all MSIX tile/logo assets + a multi-size .ico.
# Source art is neon-tube SVG in art\ (mark.svg = full detail, mark-small.svg = thick strokes for <=40 px,
# icon.svg = mark on the rounded plate). SVGs are rasterised with headless Edge (tools\render-svg.ps1),
# then composed and scaled here with System.Drawing.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root   = Split-Path -Parent $PSScriptRoot
$art    = Join-Path $root 'art'
$assets = Join-Path $root 'packaging\Assets'
$render = Join-Path $PSScriptRoot 'render-svg.ps1'
New-Item -ItemType Directory -Force $assets | Out-Null

& $render (Join-Path $art 'mark.svg')       (Join-Path $art 'mark.png')       1024
& $render (Join-Path $art 'mark-small.svg') (Join-Path $art 'mark-small.png') 256
& $render (Join-Path $art 'icon.svg')       (Join-Path $art 'icon.png')       512
$markBig   = [Drawing.Image]::FromFile((Join-Path $art 'mark.png'))
$markSmall = [Drawing.Image]::FromFile((Join-Path $art 'mark-small.png'))
$iconBig   = [Drawing.Image]::FromFile((Join-Path $art 'icon.png'))

$bg      = [Drawing.Color]::FromArgb(255, 0x0c, 0x0a, 0x17)
$crimson = [Drawing.Color]::FromArgb(255, 0xff, 0x2a, 0x5f)
$magenta = [Drawing.Color]::FromArgb(255, 0xe9, 0x4f, 0xb4)
$purple  = [Drawing.Color]::FromArgb(255, 0xa8, 0x5c, 0xff)
$teal    = [Drawing.Color]::FromArgb(255, 0x1f, 0xe0, 0xc8)

function Set-Hq([Drawing.Graphics]$g) {
  $g.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g.PixelOffsetMode   = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  $g.SmoothingMode     = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
}
function Draw-Mark([Drawing.Graphics]$g, [float]$x0, [float]$y0, [float]$size) {
  $img = if ($size -le 40) { $markSmall } else { $markBig }
  $g.DrawImage($img, (New-Object Drawing.RectangleF $x0, $y0, $size, $size))
}

function New-Asset([string]$name, [int]$w, [int]$h, [bool]$plated, [double]$markFrac, [bool]$border = $false, [bool]$wordmark = $false) {
  $bmp = New-Object Drawing.Bitmap $w, $h, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [Drawing.Graphics]::FromImage($bmp); Set-Hq $g
  $g.Clear($(if ($plated) { $bg } else { [Drawing.Color]::Transparent }))
  if ($border) {
    $bw = [Math]::Max(1, [int]($w / 110))
    $p = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(70, $purple)), ($bw * 3); $g.DrawRectangle($p, $bw, $bw, $w - 2 * $bw - 1, $h - 2 * $bw - 1); $p.Dispose()
    $p = New-Object Drawing.Pen $purple, $bw; $g.DrawRectangle($p, $bw, $bw, $w - 2 * $bw - 1, $h - 2 * $bw - 1); $p.Dispose()
  }
  $m = [Math]::Min($w, $h) * $markFrac
  if ($wordmark) {
    $mx = $h * 0.12; $my = ($h - $m) / 2
    Draw-Mark $g $mx $my $m
    $g.TextRenderingHint = [Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $fs = $h * 0.15
    $f1 = New-Object Drawing.Font 'Consolas', $fs, ([Drawing.FontStyle]::Bold), ([Drawing.GraphicsUnit]::Pixel)
    $f2 = New-Object Drawing.Font 'Consolas', ($fs * 0.62), ([Drawing.FontStyle]::Regular), ([Drawing.GraphicsUnit]::Pixel)
    $tx = $mx + $m + $h * 0.02
    $tr = New-Object Drawing.RectangleF $tx, 0, ($w - $tx), $h
    $tb = New-Object Drawing.Drawing2D.LinearGradientBrush $tr, $crimson, $purple, 0.0
    $g.DrawString('NEON', $f2, (New-Object Drawing.SolidBrush $teal), $tx, $h * 0.30)
    $g.DrawString('UNDELETE', $f1, $tb, $tx - $fs * 0.08, $h * 0.40)
    $f1.Dispose(); $f2.Dispose(); $tb.Dispose()
  } else {
    Draw-Mark $g (($w - $m) / 2) (($h - $m) / 2) $m
  }
  $g.Dispose()
  $path = Join-Path $assets $name
  $bmp.Save($path, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
  return $path
}

$made = @()
foreach ($s in @(@{q='scale-100';k=1}, @{q='scale-125';k=1.25}, @{q='scale-150';k=1.5}, @{q='scale-200';k=2}, @{q='scale-400';k=4})) {
  $k = $s.k; $q = $s.q
  $made += New-Asset "Square44x44Logo.$q.png"   ([int](44*$k))  ([int](44*$k))  $true 1.0
  $made += New-Asset "Square150x150Logo.$q.png" ([int](150*$k)) ([int](150*$k)) $true 0.74 $true
  $made += New-Asset "Wide310x150Logo.$q.png"   ([int](310*$k)) ([int](150*$k)) $true 0.74 $true $true
  $made += New-Asset "Square310x310Logo.$q.png" ([int](310*$k)) ([int](310*$k)) $true 0.74 $true
  $made += New-Asset "StoreLogo.$q.png"         ([int](50*$k))  ([int](50*$k))  $true 1.0
  $made += New-Asset "SplashScreen.$q.png"      ([int](620*$k)) ([int](300*$k)) $true 0.66 $false $true
}
# taskbar / start list icons: plated + unplated (transparent) per target size
foreach ($t in 16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256) {
  $made += New-Asset "Square44x44Logo.targetsize-$t.png" $t $t $true 1.0
  $made += New-Asset "Square44x44Logo.targetsize-${t}_altform-unplated.png" $t $t $false 1.0
  $made += New-Asset "Square44x44Logo.targetsize-${t}_altform-lightunplated.png" $t $t $false 1.0
}

# multi-size .ico (PNG-compressed entries) for the exes: plated icon art at 48+, thick mark on a dark tile below
$ico = Join-Path $root 'src\app.ico'
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($sz in $sizes) {
  $bmp = New-Object Drawing.Bitmap $sz, $sz, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [Drawing.Graphics]::FromImage($bmp); Set-Hq $g; $g.Clear([Drawing.Color]::Transparent)
  if ($sz -ge 48) { $g.DrawImage($iconBig, 0, 0, $sz, $sz) }
  else { $g.Clear($bg); Draw-Mark $g 0 0 $sz }
  $g.Dispose()
  $ms = New-Object IO.MemoryStream; $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
  ,$ms.ToArray()
}
$fs = [IO.File]::Create($ico); $bw = New-Object IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $sz = $sizes[$i]; $d = $pngs[$i]
  $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz }))); $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
  $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([UInt16]1); $bw.Write([UInt16]32)
  $bw.Write([UInt32]$d.Length); $bw.Write([UInt32]$offset); $offset += $d.Length
}
foreach ($d in $pngs) { $bw.Write($d) }
$bw.Dispose()
$markBig.Dispose(); $markSmall.Dispose(); $iconBig.Dispose()

"made $($made.Count) assets in $assets"
"made $ico"
