<#
  Builds the Neon Undelete MSIX package.

  For the Store (upload the .msixupload / .msix to Partner Center - Microsoft signs it):
    .\packaging\package.ps1 -IdentityName "12345Name.NeonUndelete" -Publisher "CN=XXXXXXXX-..." -PublisherDisplayName "Your Name"

  For a local test install (self-signed):
    .\packaging\package.ps1 -Sign
    then, once, in an *admin* PowerShell:  Import-Certificate .\packaging\out\NeonUndelete-test.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
    then double-click the .msix

  Copy the three identity values from Partner Center > your app > Product management > Product identity.
#>
param(
  [string]$IdentityName = "NeonUndelete.Dev",
  [string]$Publisher = "CN=NeonUndeleteDev",
  [string]$PublisherDisplayName = "Neon Undelete (dev)",
  [string]$Version = "1.1.0.0",
  [switch]$Sign,
  [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$pkgDir = $PSScriptRoot
$root   = Split-Path -Parent $pkgDir
$tools  = Get-ChildItem (Join-Path $root 'tools\sdk-buildtools\pkg\bin') -Directory | Sort-Object Name -Descending | Select-Object -First 1
if (-not $tools) { throw "SDK build tools not found under tools\sdk-buildtools. See README." }
$bin = Join-Path $tools.FullName 'x64'
$makeappx = Join-Path $bin 'makeappx.exe'; $makepri = Join-Path $bin 'makepri.exe'; $signtool = Join-Path $bin 'signtool.exe'

if ($Version -notmatch '^\d+\.\d+\.\d+\.0$') { throw "Version must look like 1.2.3.0 (the Store requires the last part to be 0)." }

# 1. build the exes
if (-not $NoBuild) { & (Join-Path $root 'build.ps1') }

# 2. stage the package layout
$out = Join-Path $pkgDir 'out'
$layout = Join-Path $out 'layout'
if (Test-Path $layout) { Remove-Item -Recurse -Force $layout }
New-Item -ItemType Directory -Force (Join-Path $layout 'Assets') | Out-Null
Copy-Item (Join-Path $root 'UndeleteGui.exe'), (Join-Path $root 'Undelete.exe'), (Join-Path $root 'README.md') $layout
Copy-Item (Join-Path $pkgDir 'Assets\*') (Join-Path $layout 'Assets')

$xml = [IO.File]::ReadAllText((Join-Path $pkgDir 'AppxManifest.template.xml'))
$xml = $xml.Replace('{{IDENTITY_NAME}}', $IdentityName).Replace('{{PUBLISHER}}', [Security.SecurityElement]::Escape($Publisher)).
            Replace('{{PUBLISHER_DISPLAY_NAME}}', [Security.SecurityElement]::Escape($PublisherDisplayName)).Replace('{{VERSION}}', $Version)
[IO.File]::WriteAllText((Join-Path $layout 'AppxManifest.xml'), $xml, (New-Object Text.UTF8Encoding $false))

# 3. resource index (lets Windows pick scale-200 / targetsize assets)
$priConfig = Join-Path $out 'priconfig.xml'
& $makepri createconfig /cf $priConfig /dq en-US /pv 10.0.0 /o | Out-Null
# single .msix: keep every scale in one resources.pri (auto-splitting is only for bundles)
[xml]$pc = Get-Content $priConfig
$pc.SelectNodes('//packaging') | ForEach-Object { $_.ParentNode.RemoveChild($_) | Out-Null }
$pc.Save($priConfig)
& $makepri new /pr $layout /cf $priConfig /mn (Join-Path $layout 'AppxManifest.xml') /of (Join-Path $layout 'resources.pri') /o
if ($LASTEXITCODE -ne 0) { throw "makepri failed" }

# 4. pack
$msix = Join-Path $out ("NeonUndelete_" + $Version + "_x64.msix")
& $makeappx pack /d $layout /p $msix /o
if ($LASTEXITCODE -ne 0) { throw "makeappx failed" }

# 5. optional self-signing for local testing
if ($Sign) {
  $pfx = Join-Path $out 'NeonUndelete-test.pfx'; $cer = Join-Path $out 'NeonUndelete-test.cer'
  $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $Publisher -and $_.HasPrivateKey } | Select-Object -First 1
  if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type Custom -Subject $Publisher -KeyUsage DigitalSignature -FriendlyName "Neon Undelete test signing" `
              -CertStoreLocation Cert:\CurrentUser\My -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
  }
  $pw = ConvertTo-SecureString -String 'neon-undelete-test' -Force -AsPlainText
  Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $pw | Out-Null
  Export-Certificate -Cert $cert -FilePath $cer | Out-Null
  & $signtool sign /fd SHA256 /f $pfx /p 'neon-undelete-test' $msix
  if ($LASTEXITCODE -ne 0) { throw "signtool failed" }
  Write-Host "signed with test certificate $cer (trust it in LocalMachine\TrustedPeople to install)"
}

Write-Host "package: $msix"
