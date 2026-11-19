# Renders the Neon Undelete icon + all MSIX tile/logo assets + a multi-size .ico, from code.
# Pixel-block "U" (crimson -> magenta -> purple gradient, ANSI-shadow offset) with a teal block
# lifting out of it: a deleted file coming back.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root   = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root 'packaging\Assets'
New-Item -ItemType Directory -Force $assets | Out-Null

$bg      = [Drawing.Color]::FromArgb(255, 0x0c, 0x0a, 0x17)
$crimson = [Drawing.Color]::FromArgb(255, 0xe8, 0x19, 0x4f)
$magenta = [Drawing.Color]::FromArgb(255, 0xd6, 0x3f, 0xa0)
$purple  = [Drawing.Color]::FromArgb(255, 0x9a, 0x4f, 0xd0)
$teal    = [Drawing.Color]::FromArgb(255, 0x1f, 0xc2, 0xb0)
$dim     = [Drawing.Color]::FromArgb(255, 0x6e, 0x66, 0x88)

$grid = @(
  '..........',
  '....TT....',
  '.UU.TT.UU.',
  '.UU....UU.',
  '.UU....UU.',
  '.UU....UU.',
  '.UU....UU.',
  '.UUU..UUU.',
  '..UUUUUU..',
  '..........'
)

function Draw-Mark([Drawing.Graphics]$g, [float]$x0, [float]$y0, [float]$size) {
  $cell = $size / 10.0
  $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::None
  $rect = New-Object Drawing.RectangleF $x0, $y0, $size, $size
  $grad = New-Object Drawing.Drawing2D.LinearGradientBrush $rect, $crimson, $purple, 45.0
  $blend = New-Object Drawing.Drawing2D.ColorBlend 3
  $blend.Colors = @($crimson, $magenta, $purple); $blend.Positions = @(0.0, 0.5, 1.0)
  $grad.InterpolationColors = $blend
  $shadow = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(150, $purple))
  $glow   = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(40, $magenta))
  $tealB  = New-Object Drawing.SolidBrush $teal
  $tglow  = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(60, $teal))
  $off = [Math]::Max(1.0, $cell * 0.28)
  # glow + shadow pass
  for ($r = 0; $r -lt 10; $r++) { for ($c = 0; $c -lt 10; $c++) {
    $ch = $grid[$r][$c]; if ($ch -eq '.') { continue }
    $x = $x0 + $c * $cell; $y = $y0 + $r * $cell
    if ($size -ge 40) { $g.FillRectangle(($(if ($ch -eq 'T') { $tglow } else { $glow })), $x - $cell * 0.3, $y - $cell * 0.3, $cell * 1.6, $cell * 1.6) }
    if ($ch -eq 'U' -and $size -ge 24) { $g.FillRectangle($shadow, $x + $off, $y + $off, $cell, $cell) }
  } }
  # solid pass (slightly oversized to avoid seams)
  for ($r = 0; $r -lt 10; $r++) { for ($c = 0; $c -lt 10; $c++) {
    $ch = $grid[$r][$c]; if ($ch -eq '.') { continue }
    $x = $x0 + $c * $cell; $y = $y0 + $r * $cell
    $g.FillRectangle(($(if ($ch -eq 'T') { $tealB } else { $grad })), $x, $y, $cell + 0.5, $cell + 0.5)
  } }
  $grad.Dispose(); $shadow.Dispose(); $glow.Dispose(); $tealB.Dispose(); $tglow.Dispose()
}

function New-Asset([string]$name, [int]$w, [int]$h, [bool]$plated, [double]$markFrac, [bool]$border = $false, [bool]$wordmark = $false) {
  $bmp = New-Object Drawing.Bitmap $w, $h, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [Drawing.Graphics]::FromImage($bmp)
  $g.Clear($(if ($plated) { $bg } else { [Drawing.Color]::Transparent }))
  if ($border) {
    $bw = [Math]::Max(1, [int]($w / 110))
    $p = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(70, $magenta)), ($bw * 3); $g.DrawRectangle($p, $bw, $bw, $w - 2 * $bw - 1, $h - 2 * $bw - 1); $p.Dispose()
    $p = New-Object Drawing.Pen $magenta, $bw; $g.DrawRectangle($p, $bw, $bw, $w - 2 * $bw - 1, $h - 2 * $bw - 1); $p.Dispose()
  }
  $m = [Math]::Min($w, $h) * $markFrac
  if ($wordmark) {
    $mx = $h * 0.22; $my = ($h - $m) / 2
    Draw-Mark $g $mx $my $m
    $g.TextRenderingHint = [Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $fs = $h * 0.15
    $f1 = New-Object Drawing.Font 'Cascadia Mono', $fs, ([Drawing.FontStyle]::Bold), ([Drawing.GraphicsUnit]::Pixel)
    $f2 = New-Object Drawing.Font 'Cascadia Mono', ($fs * 0.62), ([Drawing.FontStyle]::Regular), ([Drawing.GraphicsUnit]::Pixel)
    $tx = $mx + $m + $h * 0.10
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
  $made += New-Asset "Square44x44Logo.$q.png"   ([int](44*$k))  ([int](44*$k))  $true 0.80
  $made += New-Asset "Square150x150Logo.$q.png" ([int](150*$k)) ([int](150*$k)) $true 0.56 $true
  $made += New-Asset "Wide310x150Logo.$q.png"   ([int](310*$k)) ([int](150*$k)) $true 0.56 $true $true
  $made += New-Asset "Square310x310Logo.$q.png" ([int](310*$k)) ([int](310*$k)) $true 0.56 $true
  $made += New-Asset "StoreLogo.$q.png"         ([int](50*$k))  ([int](50*$k))  $true 0.82
  $made += New-Asset "SplashScreen.$q.png"      ([int](620*$k)) ([int](300*$k)) $true 0.50 $false $true
}
# taskbar / start list icons: plated + unplated (transparent) per target size
foreach ($t in 16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256) {
  $made += New-Asset "Square44x44Logo.targetsize-$t.png" $t $t $true 0.90
  $made += New-Asset "Square44x44Logo.targetsize-${t}_altform-unplated.png" $t $t $false 0.92
  $made += New-Asset "Square44x44Logo.targetsize-${t}_altform-lightunplated.png" $t $t $false 0.92
}

# multi-size .ico (PNG-compressed entries) for the exes
$ico = Join-Path $root 'src\app.ico'
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($sz in $sizes) {
  $bmp = New-Object Drawing.Bitmap $sz, $sz, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [Drawing.Graphics]::FromImage($bmp); $g.Clear($bg)
  Draw-Mark $g ($sz * 0.04) ($sz * 0.04) ($sz * 0.92); $g.Dispose()
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

"made $($made.Count) assets in $assets"
"made $ico"
