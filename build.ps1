# Builds Undelete.exe (terminal UI + CLI) and UndeleteGui.exe (windowed UI) with the C# compiler
# that ships with Windows (.NET Framework 4.x) - no SDK needed.
$ErrorActionPreference = 'Stop'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$root = Join-Path $PSScriptRoot 'src'
function Files($dir) { Get-ChildItem (Join-Path $root $dir) -Filter *.cs | ForEach-Object { $_.FullName } }
$core = Files 'core'
$icon = Join-Path $root 'app.ico'
$iconArg = if (Test-Path $icon) { "/win32icon:$icon" } else { $null }

$tui = Join-Path $PSScriptRoot 'Undelete.exe'
& $csc /nologo /optimize+ /platform:x64 /target:exe /codepage:65001 $iconArg "/out:$tui" $core (Files 'tui')
if ($LASTEXITCODE -ne 0) { throw "terminal build failed" }
Write-Host "built $tui"

$gui = Join-Path $PSScriptRoot 'UndeleteGui.exe'
& $csc /nologo /optimize+ /platform:x64 /target:winexe /codepage:65001 "/out:$gui" `
    "/win32manifest:$(Join-Path $root 'gui\app.manifest')" $iconArg `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll $core (Files 'gui')
if ($LASTEXITCODE -ne 0) { throw "gui build failed" }
Write-Host "built $gui"

# installer: both exes, the icon and the README embedded as resources
$dist = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
$setup = Join-Path $dist 'NeonUndelete-Setup.exe'
$payload = @(
    "/resource:$gui,payload/UndeleteGui.exe",
    "/resource:$tui,payload/Undelete.exe",
    "/resource:$icon,payload/app.ico",
    "/resource:$(Join-Path $PSScriptRoot 'README.md'),payload/README.md"
)
& $csc /nologo /optimize+ /platform:x64 /target:winexe /codepage:65001 "/out:$setup" `
    "/win32manifest:$(Join-Path $root 'gui\app.manifest')" $iconArg `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll $payload `
    $core (Join-Path $root 'gui\Controls.cs') (Files 'setup')
if ($LASTEXITCODE -ne 0) { throw "setup build failed" }
Write-Host "built $setup"
