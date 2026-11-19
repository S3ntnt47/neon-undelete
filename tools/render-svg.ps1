# Renders an SVG to PNG at an exact pixel size with headless Microsoft Edge (keeps SVG glow filters,
# transparent background where the SVG has none).  Usage: .\render-svg.ps1 in.svg out.png 512 [512]
param([Parameter(Mandatory)][string]$Svg, [Parameter(Mandatory)][string]$Png, [Parameter(Mandatory)][int]$Width, [int]$Height = 0)
$ErrorActionPreference = 'Stop'
if ($Height -le 0) { $Height = $Width }
$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw 'Microsoft Edge not found' }
$svgUri = ([Uri](Resolve-Path $Svg).Path).AbsoluteUri
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("render-" + [Guid]::NewGuid())
New-Item -ItemType Directory $tmp | Out-Null
$html = Join-Path $tmp 'page.html'
"<!doctype html><html><head><style>html,body{margin:0;background:transparent;overflow:hidden}</style></head><body><img src=`"$svgUri`" style=`"display:block;width:${Width}px;height:${Height}px`"></body></html>" | Set-Content -Encoding utf8 $html
$out = [IO.Path]::GetFullPath($Png)
$args = @('--headless', '--disable-gpu', '--hide-scrollbars', '--force-device-scale-factor=1', "--user-data-dir=$tmp\ud",
          '--default-background-color=00000000', "--window-size=$Width,$Height", "--screenshot=$out", ([Uri]$html).AbsoluteUri)
$p = Start-Process -FilePath $edge -ArgumentList $args -Wait -PassThru -WindowStyle Hidden
Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
if (-not (Test-Path $out)) { throw "render failed: $Svg" }
