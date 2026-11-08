<#
  Builds trutility and lays it out as an MSIX package.

  .\packaging\build-msix.ps1
      Builds bin\msix\layout and, when the Windows SDK is installed, packs
      bin\msix\trutility_<version>_x64.msix. The defaults give a test identity.

  For the Store, pass the values from Partner Center > Product identity:
      .\packaging\build-msix.ps1 -Name trutility `
          -Publisher "CN=xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx" -PublisherDisplay "SIA TeleBit"
  The Store signs the package itself, so upload the .msix unsigned.

  To try it locally without packing or signing, turn on Developer Mode and run:
      Add-AppxPackage -Register .\bin\msix\layout\AppxManifest.xml

  To install the .msix itself, sign it with a self-signed certificate:
      .\packaging\build-msix.ps1 -Sign
  and trust that certificate once, from an elevated PowerShell:
      Import-Certificate -FilePath .\bin\msix\trutility-test.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
#>
param(
    [string]$Name = "trutility",
    [string]$Publisher = "CN=trutility",
    [string]$PublisherDisplay = "TeleBit SIA",
    [switch]$Sign
)

$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$out = Join-Path $repo "bin\msix"
$layout = Join-Path $out "layout"

dotnet build (Join-Path $repo "trutility.csproj") -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "build failed" }

# The packaged Explorer verb (shellext\) is C++, so packing needs MSVC too.
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = if (Test-Path $vswhere) {
    & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
        -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
}
if (-not $msbuild) {
    throw "MSVC not found. Install it with: winget install Microsoft.VisualStudio.2022.BuildTools --override `"--add Microsoft.VisualStudio.Workload.VCTools --includeRecommended --passive`""
}
& $msbuild (Join-Path $repo "shellext\shellext.vcxproj") /p:Configuration=Release /p:Platform=x64 /nologo /v:q
if ($LASTEXITCODE -ne 0) { throw "shell extension build failed" }

# The Store wants the last version part to be 0, which the csproj already keeps.
[xml]$proj = Get-Content (Join-Path $repo "trutility.csproj")
$version = ($proj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version

if (Test-Path $layout) { Remove-Item -Recurse -Force $layout }
New-Item -ItemType Directory -Force $layout | Out-Null
$bin = Join-Path $repo "bin\Release\net462-windows"
Copy-Item (Join-Path $bin "trutility.exe"), (Join-Path $bin "trutility.exe.config"),
    (Join-Path $repo "bin\shellext\trutility.ShellExt.dll") $layout

# The manifest's logos are scaled down from the one large source image, since
# Windows' own scaling of a 1024px image to 44px looks blurry on the taskbar.
Add-Type -AssemblyName System.Drawing
$assets = New-Item -ItemType Directory -Force (Join-Path $layout "Assets")
$source = [System.Drawing.Image]::FromFile((Join-Path $PSScriptRoot "Assets\trutility_1024x1024.png"))
$logos = @{ "Square150x150Logo.png" = 150; "Square44x44Logo.png" = 44; "StoreLogo.png" = 50 }
# Without unplated sizes, Windows draws the icon on an accent-coloured plate in
# the taskbar, Start's list and Explorer. They are found through resources.pri.
foreach ($size in 16, 24, 32, 48, 256) { $logos["Square44x44Logo.targetsize-${size}_altform-unplated.png"] = $size }
try {
    foreach ($logo in $logos.GetEnumerator()) {
        $bitmap = New-Object System.Drawing.Bitmap $logo.Value, $logo.Value
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.DrawImage($source, 0, 0, $logo.Value, $logo.Value)
        $graphics.Dispose()
        $bitmap.Save((Join-Path $assets $logo.Key), [System.Drawing.Imaging.ImageFormat]::Png)
        $bitmap.Dispose()
    }
} finally { $source.Dispose() }

(Get-Content -Raw (Join-Path $PSScriptRoot "AppxManifest.xml")).
    Replace('$NAME$', $Name).
    Replace('$PUBLISHER_DISPLAY$', $PublisherDisplay).
    Replace('$PUBLISHER$', $Publisher).
    Replace('$VERSION$', $version) |
    Set-Content -Encoding utf8 (Join-Path $layout "AppxManifest.xml")

$makeappx = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\makeappx.exe" -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1
if (-not $makeappx) {
    Write-Host "Layout ready at $layout"
    Write-Host "No Windows SDK found, so no .msix was packed (winget install Microsoft.WindowsSDK.10.0.26100)."
    return
}

$makepri = Join-Path $makeappx.DirectoryName "makepri.exe"
$priconfig = Join-Path $out "priconfig.xml"
& $makepri createconfig /cf $priconfig /dq en-US /pv 10.0.0 /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makepri createconfig failed" }
& $makepri new /pr $layout /cf $priconfig /of (Join-Path $layout "resources.pri") /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makepri failed" }

$msix = Join-Path $out "trutility_${version}_x64.msix"
& $makeappx.FullName pack /d $layout /p $msix /o
if ($LASTEXITCODE -ne 0) { throw "makeappx failed" }
Write-Host "Packed $msix"

if (-not $Sign) { return }

# The certificate subject must equal the manifest's Publisher. It is made once
# and reused, so it only has to be trusted once.
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $Publisher } | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type Custom -Subject $Publisher -KeyUsage DigitalSignature `
        -FriendlyName "trutility MSIX test" -CertStoreLocation Cert:\CurrentUser\My `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
}
$cer = Join-Path $out "trutility-test.cer"
Export-Certificate -Cert $cert -FilePath $cer | Out-Null

$signtool = Join-Path $makeappx.DirectoryName "signtool.exe"
& $signtool sign /fd SHA256 /sha1 $cert.Thumbprint $msix
if ($LASTEXITCODE -ne 0) { throw "signtool failed" }

$trusted = Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object { $_.Thumbprint -eq $cert.Thumbprint }
if (-not $trusted) {
    Write-Host "Signed. Trust the certificate once, from an elevated PowerShell:"
    Write-Host "    Import-Certificate -FilePath `"$cer`" -CertStoreLocation Cert:\LocalMachine\TrustedPeople"
}
