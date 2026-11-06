# Builds WhatsApp Lite (WebView2) with the C# compiler that ships with Windows - no SDK required.
# Output: dist\app\ (portable files) and dist\WhatsApp Lite Setup.exe (installer)
$ErrorActionPreference = 'Stop'
$here   = $PSScriptRoot
$dist   = Join-Path $here 'dist'
$app    = Join-Path $dist 'app'
$cache  = Join-Path $here '.nuget-cache'
$csc    = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$icon   = Join-Path $here 'icon.ico'
$wv2Ver = '1.0.2792.45'
$wv2Sha = '02FFD77440C09B5FF60F5F06DEFDC3FB07FB5630077C7CF6115BF6BE793F91E3'   # SHA-256 of the official nupkg

if (-not (Test-Path $csc)) { throw "C# compiler not found at $csc" }

# 1. WebView2 SDK DLLs (managed wrappers + native loader), fetched once from NuGet and hash-verified
$pkg = Join-Path $cache "Microsoft.Web.WebView2.$wv2Ver"
if (-not (Test-Path "$pkg\lib\net462\Microsoft.Web.WebView2.Core.dll")) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    New-Item -ItemType Directory -Force $cache | Out-Null
    $zip = Join-Path $cache "wv2-$wv2Ver.zip"
    Invoke-WebRequest "https://www.nuget.org/api/v2/package/Microsoft.Web.WebView2/$wv2Ver" -OutFile $zip -UseBasicParsing
    $got = (Get-FileHash $zip -Algorithm SHA256).Hash
    if ($got -ne $wv2Sha) { Remove-Item $zip -Force; throw "WebView2 package hash mismatch (got $got) - refusing to build" }
    Expand-Archive $zip $pkg -Force
}

# 2. Compile the app
Remove-Item $dist -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $app | Out-Null
Copy-Item "$pkg\lib\net462\Microsoft.Web.WebView2.Core.dll", "$pkg\lib\net462\Microsoft.Web.WebView2.WinForms.dll", "$pkg\runtimes\win-x64\native\WebView2Loader.dll" $app
& $csc /nologo /warn:4 /warnaserror+ /target:winexe /platform:x64 /optimize+ "/out:$app\WhatsApp Lite.exe" "/win32icon:$icon" "/win32manifest:$here\app.manifest" `
    "/reference:$app\Microsoft.Web.WebView2.Core.dll" "/reference:$app\Microsoft.Web.WebView2.WinForms.dll" `
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "$here\WhatsAppLite.cs"
if ($LASTEXITCODE) { throw "App compile failed" }

# 3. Zip the app and embed it in the installer
$appZip = Join-Path $dist 'app.zip'
Compress-Archive "$app\*" $appZip -CompressionLevel Optimal
& $csc /nologo /warn:4 /warnaserror+ /target:winexe /platform:x64 /optimize+ "/out:$dist\WhatsApp Lite Setup.exe" "/win32icon:$icon" "/win32manifest:$here\app.manifest" `
    "/resource:$appZip,app.zip" /reference:System.Windows.Forms.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll "$here\Setup.cs"
if ($LASTEXITCODE) { throw "Installer compile failed" }
Remove-Item $appZip

Get-ChildItem $dist -File | Select-Object Name, @{n='MB'; e={[math]::Round($_.Length / 1MB, 2)}}
