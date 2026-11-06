# WhatsApp Lite

A lightweight Windows desktop app for WhatsApp Web. The program itself is about 30 KB: instead of bundling its own copy of Chromium like Electron apps do, it uses the Microsoft Edge WebView2 engine already built into Windows.

## [⬇ Download for Windows](https://github.com/MadVibeCoder/whatsapp-lite/releases/latest/download/WhatsApp-Lite-Setup.exe)

1. Click **Download for Windows** above and open **WhatsApp-Lite-Setup.exe**.
2. If Windows shows *"Windows protected your PC"*, click **More info**, then **Run anyway**. (The installer isn't code-signed yet, so Windows doesn't recognise it.)
3. Click **OK** to install, then **Yes** to launch it.
4. On your phone, open WhatsApp → **Linked devices** (in Settings on iPhone, the ⋮ menu on Android) → **Link a device**, and scan the QR code.

No admin rights needed. It installs just for you, adds Start menu and desktop shortcuts, and can be removed any time from **Settings → Apps**. Installing a newer version updates it in place and keeps you logged in.

**Requires** Windows 10 or 11 and the Microsoft Edge WebView2 Runtime, which is part of Windows 11. If it's missing, the app offers to open the download page.

## Features

- Closing the window keeps it running in the system tray, so messages and notifications keep coming
- Red badge on the tray and taskbar icon when you have unread messages
- Optional **Start with Windows** (right-click the tray icon), starting quietly in the tray
- Links open in your normal browser; only WhatsApp Web runs inside the app
- Recovers by itself if the WebView2 engine crashes, and retries when you're offline
- Trims its memory use while hidden in the tray

## Building from source

Everything builds with the C# compiler that ships with Windows; no Visual Studio or SDK needed.

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

The build downloads the WebView2 SDK package once from NuGet and checks its SHA-256 before using it. The output goes to `dist\`: the portable app in `dist\app\` and the installer `dist\WhatsApp Lite Setup.exe`.

To release a new version:

1. Bump the numbers in `Version.cs`.
2. Run `build.ps1`.
3. Create a GitHub release whose installer asset is named exactly **`WhatsApp-Lite-Setup.exe`**, so the download link above keeps working.

---

WhatsApp Lite is an independent project and is not affiliated with, endorsed by, or connected to WhatsApp or Meta.
