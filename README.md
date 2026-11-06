<img src="assets/vibechat.png" alt="" width="96" align="right">

# VibeChat

A lightweight desktop app for WhatsApp Web, for Windows and Ubuntu. Instead of bundling its own copy of Chromium like Electron apps do, it uses the web engine already on your computer (WebView2 on Windows, WebKitGTK on Linux), so the program itself is tiny: about 30 KB on Windows and a 10 KB package on Ubuntu.

## [⬇ Download for Windows](https://github.com/MadVibeCoder/vibechat/releases/latest/download/VibeChat-Setup.exe)

1. Click **Download for Windows** above and open **VibeChat-Setup.exe**.
2. If Windows shows *"Windows protected your PC"*, click **More info**, then **Run anyway**. (The installer isn't code-signed yet, so Windows doesn't recognise it.)
3. Click **OK** to install, then **Yes** to launch it.
4. On your phone, open WhatsApp → **Linked devices** (in Settings on iPhone, the ⋮ menu on Android) → **Link a device**, and scan the QR code.

No admin rights needed. It installs just for you, adds Start menu and desktop shortcuts, and can be removed any time from **Settings → Apps**. Installing a newer version updates it in place and keeps you logged in.

**Requires** Windows 10 or 11 and the Microsoft Edge WebView2 Runtime, which is part of Windows 11. If it's missing, the app offers to open the download page.

## [⬇ Download for Ubuntu (.deb)](https://github.com/MadVibeCoder/vibechat/releases/latest/download/VibeChat.deb)

For Ubuntu 22.04 or newer (and other Debian-based distributions with GTK 3 and WebKitGTK 4.1).

1. Download **VibeChat.deb** and double-click it to install, or run `sudo apt install ./VibeChat.deb` in the folder you saved it to. Its few dependencies (GTK, WebKitGTK, the tray icon library) are installed automatically.
2. Open **VibeChat** from your apps and scan the QR code as above.

Right-click the tray icon for **Start on login** and **Quit**; middle-click it to open the window. Shortcuts: Ctrl+W hides the window, Ctrl+Q quits, Ctrl + / − / 0 zoom, F5 reloads. The tray icon needs AppIndicator support, which Ubuntu has built in; on other GNOME desktops add the *AppIndicator* extension. Without a tray, closing the window quits the app.

## Features

- Closing the window keeps it running in the system tray, so messages and notifications keep coming
- Red badge on the tray and taskbar icon when you have unread messages (on Ubuntu also a count on the dock icon)
- Optional **Start with Windows** / **Start on login** (right-click the tray icon), starting quietly in the tray
- Links open in your normal browser; only WhatsApp Web runs inside the app
- Recovers by itself if the web engine crashes, and retries when you're offline
- Windows: trims its memory use while hidden in the tray. Ubuntu: clicking a message notification opens that chat

## Building from source

Everything builds with the C# compiler that ships with Windows; no Visual Studio or SDK needed.

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

The build downloads the WebView2 SDK package once from NuGet and checks its SHA-256 before using it. The output goes to `dist\`: the portable app in `dist\app\` and the installer `dist\VibeChat Setup.exe`.

The icon is drawn in code by `tools\IconGen.cs`, which writes `icon.ico` and a preview sheet; `linux/icons/` holds the same design as SVG.

**Linux:** `python3 linux/build-deb.py` builds `linux/dist/vibechat_<version>_all.deb`. It only needs Python 3, so it also runs on Windows or macOS. To run from the source tree without installing: `python3 linux/vibechat.py` (needs `python3-gi gir1.2-gtk-3.0 gir1.2-webkit2-4.1 gir1.2-ayatanaappindicator3-0.1`).

To release a new version:

1. Bump the numbers in `Version.cs`.
2. Run `build.ps1`.
3. Run `python3 linux/build-deb.py`.
4. Create a GitHub release with the installers named exactly **`VibeChat-Setup.exe`** and **`VibeChat.deb`**, so the download links above keep working.

---

VibeChat is an independent project. It is not affiliated with, endorsed by, or connected to WhatsApp or Meta; "WhatsApp" is a trademark of WhatsApp LLC and is used here only to describe what the app connects to.
