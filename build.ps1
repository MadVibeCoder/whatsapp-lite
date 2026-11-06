# Builds WhatsApp Lite (WebView2) with the C# compiler that ships with Windows - no SDK required.
# Output: dist\app\ (portable files) and dist\WhatsApp Lite Setup.exe (installer)
# To release a new version, change the numbers in Version.cs.
$ErrorActionPreference = 'Stop'
$here   = $PSScriptRoot
$dist   = Join-Path $here 'dist'
$app    = Join-Path $dist 'app'
$cache  = Join-Path $here '.nuget-cache'
$csc    = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$icon   = Join-Path $here 'icon.ico'
$verCs  = Join-Path $here 'Version.cs'
$wv2Ver = '1.0.2792.45'
$wv2Sha = '02FFD77440C09B5FF60F5F06DEFDC3FB07FB5630077C7CF6115BF6BE793F91E3'   # SHA-256 of the official nupkg

if (-not (Test-Path $csc)) { throw "C# compiler not found at $csc" }
$version = [regex]::Match((Get-Content $verCs -Raw), 'AssemblyInformationalVersion\("([^"]+)"\)').Groups[1].Value
if (-not $version) { throw "Could not read the version from $verCs" }

# 1. WebView2 SDK package from NuGet: downloaded once, hash-verified on every build
$zip = Join-Path $cache "wv2-$wv2Ver.zip"
if (-not (Test-Path $zip) -or (Get-FileHash $zip -Algorithm SHA256).Hash -ne $wv2Sha) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    New-Item -ItemType Directory -Force $cache | Out-Null
    Invoke-WebRequest "https://www.nuget.org/api/v2/package/Microsoft.Web.WebView2/$wv2Ver" -OutFile $zip -UseBasicParsing
    $got = (Get-FileHash $zip -Algorithm SHA256).Hash
    if ($got -ne $wv2Sha) { Remove-Item $zip -Force; throw "WebView2 package hash mismatch (got $got) - refusing to build" }
}

# 2. Clean output and take the three DLLs we ship straight from the verified package
Remove-Item $dist -Recurse -Force -ErrorAction SilentlyContinue
if (Test-Path $dist) { throw "Could not clean $dist - close any WhatsApp Lite running from it and retry" }
New-Item -ItemType Directory -Force $app | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$pkg = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    foreach ($name in 'lib/net462/Microsoft.Web.WebView2.Core.dll', 'lib/net462/Microsoft.Web.WebView2.WinForms.dll', 'runtimes/win-x64/native/WebView2Loader.dll') {
        $entry = $pkg.GetEntry($name)
        if (-not $entry) { throw "$name not found in the WebView2 package" }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $app $entry.Name), $true)
    }
} finally { $pkg.Dispose() }

# 3. Compile the app
& $csc /nologo /warn:4 /warnaserror+ /target:winexe /platform:x64 /optimize+ "/out:$app\WhatsApp Lite.exe" "/win32icon:$icon" "/win32manifest:$here\app.manifest" `
    "/reference:$app\Microsoft.Web.WebView2.Core.dll" "/reference:$app\Microsoft.Web.WebView2.WinForms.dll" `
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "$here\WhatsAppLite.cs" $verCs
if ($LASTEXITCODE) { throw "App compile failed" }

# 4. Zip the app and embed it in the installer
$appZip = Join-Path $dist 'app.zip'
Compress-Archive "$app\*" $appZip -CompressionLevel Optimal
& $csc /nologo /warn:4 /warnaserror+ /target:winexe /platform:x64 /optimize+ "/out:$dist\WhatsApp Lite Setup.exe" "/win32icon:$icon" "/win32manifest:$here\app.manifest" `
    "/resource:$appZip,app.zip" /reference:System.Windows.Forms.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll "$here\Setup.cs" $verCs
if ($LASTEXITCODE) { throw "Installer compile failed" }
Remove-Item $appZip

"WhatsApp Lite $version"
Get-ChildItem $dist -File | Select-Object Name, @{n='MB'; e={[math]::Round($_.Length / 1MB, 2)}}
