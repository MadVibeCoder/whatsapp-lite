// VibeChat (WebView2) - tiny native desktop client for WhatsApp Web. Not affiliated with WhatsApp or Meta.
// Compiles with the C# 5 compiler that ships with Windows (no SDK needed).
using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

[assembly: AssemblyTitle("VibeChat")]
[assembly: AssemblyProduct("VibeChat")]
[assembly: AssemblyDescription("VibeChat - lightweight desktop client for WhatsApp Web")]
// Version numbers live in Version.cs (shared with the installer).

namespace VibeChat
{
    static class Program
    {
        internal static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VibeChat");

        [STAThread]
        static void Main(string[] args)
        {
            bool hidden = Array.IndexOf(args, "--hidden") >= 0;
            bool createdNew;
            Mutex mutex = new Mutex(true, "VibeChat.SingleInstance", out createdNew);
            if (!createdNew)
            {
                // Already running: ask that instance to show itself (but not for the silent autostart launch).
                if (!hidden)
                {
                    // We were just started by the user, so we may take the foreground; pass that right on,
                    // otherwise Windows only flashes the other instance's taskbar button.
                    NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
                    try { using (EventWaitHandle ev = EventWaitHandle.OpenExisting("VibeChat.Show")) ev.Set(); } catch { }
                }
                mutex.Dispose();
                return;
            }
            try
            {
                AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e) { LogError(e.ExceptionObject as Exception); };
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e) { LogError(e.Exception); };
                Application.Run(new MainForm(hidden));
            }
            finally
            {
                try { mutex.ReleaseMutex(); } catch { }
                mutex.Dispose();
            }
        }

        // A tray app should not die with a crash dialog; keep a small log instead.
        internal static void LogError(Exception ex)
        {
            Log(ex != null ? ex.ToString() : "Unknown error");
        }

        internal static void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                string path = Path.Combine(DataDir, "error.log");
                FileInfo fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 256 * 1024) File.Delete(path);
                File.AppendAllText(path, DateTime.Now.ToString("s", CultureInfo.InvariantCulture) + " " + message + Environment.NewLine);
            }
            catch { }
        }
    }

    class MainForm : Form
    {
        const string AppName = "VibeChat";
        const string AppHost = "web.whatsapp.com";
        const string StartUrl = "https://" + AppHost + "/";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const int MaxBrowserRestarts = 5;
        static readonly Color Background = Color.FromArgb(0x11, 0x1b, 0x21);

        WebView2 web;                       // replaced if the WebView2 browser process dies
        readonly NotifyIcon tray = new NotifyIcon();
        readonly MenuItem autoStartItem = new MenuItem("Start with Windows");
        readonly System.Windows.Forms.Timer retryTimer = new System.Windows.Forms.Timer();
        readonly bool startHidden;
        readonly Icon baseIcon;
        Icon badgeIcon;                     // created on first unread message, then reused
        readonly EventWaitHandle showEvent;
        readonly EventWaitHandle quitEvent;
        bool quitting;
        bool webInitStarted;
        int browserRestarts;
        int unread = -1;
        Rectangle normalBounds;
        bool maximized;                     // last non-minimised state
        int lastDeactivateTick;
        bool trayClickWasActive;

        public MainForm(bool hidden)
        {
            startHidden = hidden;
            Text = AppName;
            baseIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Icon = baseIcon;
            BackColor = Background;
            MinimumSize = new Size(480, 500);
            StartPosition = FormStartPosition.Manual;
            LoadBounds();

            web = CreateWebView();
            Controls.Add(web);

            BuildTray();

            retryTimer.Interval = 5000;
            retryTimer.Tick += delegate { retryTimer.Stop(); if (web.CoreWebView2 != null) web.CoreWebView2.Navigate(StartUrl); };

            // Cross-process signals: a second launch asks us to show; the installer asks us to quit cleanly.
            showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "VibeChat.Show");
            quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "VibeChat.Quit");
            Thread t = new Thread(delegate ()
            {
                WaitHandle[] handles = new WaitHandle[] { showEvent, quitEvent };
                while (true)
                {
                    int which = WaitHandle.WaitAny(handles);
                    if (IsDisposed) return;
                    try
                    {
                        if (which == 0) BeginInvoke(new Action(ShowWindow));
                        else BeginInvoke(new Action(QuitApp));
                    }
                    catch (InvalidOperationException) { /* handle not created yet / already gone; ignore this signal */ }
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        static WebView2 CreateWebView()
        {
            WebView2 w = new WebView2();
            w.Dock = DockStyle.Fill;
            w.DefaultBackgroundColor = Background; // no white flash while loading
            w.CreationProperties = new CoreWebView2CreationProperties();
            w.CreationProperties.UserDataFolder = Path.Combine(Program.DataDir, "WebView2Data");
            return w;
        }

        // ---------- window handling ----------
        protected override void SetVisibleCore(bool value)
        {
            // Honour --hidden: create the window handle but do not show on first run.
            if (startHidden && !IsHandleCreated) { CreateHandle(); value = false; }
            base.SetVisibleCore(value);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            InitWebView();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (startHidden) InitWebView();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            SetMemoryTarget(!Visible); // trim RAM while hidden in the tray; messages still arrive
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            lastDeactivateTick = Environment.TickCount;
        }

        async void InitWebView()
        {
            if (webInitStarted) return;
            webInitStarted = true;
            WebView2 view = web;
            try
            {
                await view.EnsureCoreWebView2Async(null);
            }
            catch (Exception ex)
            {
                Program.LogError(ex);
                if (quitting) return;
                if (MessageBox.Show("VibeChat needs the Microsoft Edge WebView2 Runtime, which could not be started.\n\n" + ex.Message +
                    "\n\nOpen the download page?", AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Error) == DialogResult.Yes)
                    OpenExternal("https://developer.microsoft.com/microsoft-edge/webview2/");
                QuitApp();
                return;
            }
            if (quitting || IsDisposed || !object.ReferenceEquals(view, web)) return; // closed or replaced while starting up
            CoreWebView2 core = view.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsWebMessageEnabled = false;       // we do not use host<->page messaging
            core.Settings.AreHostObjectsAllowed = false;     // nor injected host objects

            core.NewWindowRequested += delegate (object s, CoreWebView2NewWindowRequestedEventArgs a) { a.Handled = true; OpenExternal(a.Uri); };
            core.NavigationStarting += delegate (object s, CoreWebView2NavigationStartingEventArgs a)
            {
                // The window has no address bar or back button, so only WhatsApp Web itself may load in it.
                if (!IsAppPage(a.Uri)) { a.Cancel = true; OpenExternal(a.Uri); }
            };
            core.NavigationCompleted += delegate (object s, CoreWebView2NavigationCompletedEventArgs a)
            {
                if (a.IsSuccess) { retryTimer.Stop(); return; }    // e.g. a manual reload got there first
                switch (a.WebErrorStatus)
                {
                    case CoreWebView2WebErrorStatus.OperationCanceled:                      // we (or a newer navigation) cancelled it
                    case CoreWebView2WebErrorStatus.ValidAuthenticationCredentialsRequired: // retrying would just re-prompt
                    case CoreWebView2WebErrorStatus.ValidProxyAuthenticationRequired:
                        return;
                }
                retryTimer.Start(); // offline or server error: retry shortly
            };
            core.DocumentTitleChanged += delegate { OnTitle(core.DocumentTitle); };
            core.ProcessFailed += delegate (object s, CoreWebView2ProcessFailedEventArgs a)
            {
                switch (a.ProcessFailedKind)
                {
                    case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                        // The whole engine is gone and this WebView2 can never be used again: build a new one.
                        BeginInvoke(new Action(RecreateWebView));
                        break;
                    case CoreWebView2ProcessFailedKind.RenderProcessExited:
                    case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                        BeginInvoke(new Action(delegate { try { core.Reload(); } catch { } })); // page crashed/hung: reload it
                        break;
                    // GPU, utility and sub-frame processes are restarted by WebView2 itself; a reload would only lose the user's place.
                }
            };
            core.PermissionRequested += delegate (object s, CoreWebView2PermissionRequestedEventArgs a)
            {
                bool ok = IsAppOrigin(a.Uri) && (a.PermissionKind == CoreWebView2PermissionKind.Notifications ||
                    a.PermissionKind == CoreWebView2PermissionKind.Microphone ||
                    a.PermissionKind == CoreWebView2PermissionKind.Camera ||
                    a.PermissionKind == CoreWebView2PermissionKind.ClipboardRead ||
                    a.PermissionKind == CoreWebView2PermissionKind.MultipleAutomaticDownloads); // saving several media files at once
                a.State = ok ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
            };

            SetMemoryTarget(!Visible);
            core.Navigate(StartUrl);
        }

        // The WebView2 browser process ended (crashed, was killed, runtime problem). The old control is dead
        // for good, so swap in a fresh one instead of leaving a blank window until the app is restarted.
        void RecreateWebView()
        {
            if (quitting || IsDisposed) return;
            if (++browserRestarts > MaxBrowserRestarts)
            {
                Program.Log("WebView2 browser process keeps exiting; giving up after " + MaxBrowserRestarts + " restarts");
                return;
            }
            Program.Log("WebView2 browser process exited; starting a new one (restart " + browserRestarts + ")");
            retryTimer.Stop();
            WebView2 old = web;
            web = CreateWebView();
            Controls.Add(web);
            Controls.Remove(old);
            try { old.Dispose(); } catch (Exception ex) { Program.LogError(ex); }
            webInitStarted = false;
            InitWebView();
        }

        void ShowWindow()
        {
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            TopMost = true; TopMost = false; // reliably raise above other windows
            Activate();
        }

        void QuitApp()
        {
            quitting = true;
            tray.Visible = false;
            Application.Exit();
        }

        void SetMemoryTarget(bool low)
        {
            try
            {
                if (web.CoreWebView2 != null)
                    web.CoreWebView2.MemoryUsageTargetLevel = low ? CoreWebView2MemoryUsageTargetLevel.Low : CoreWebView2MemoryUsageTargetLevel.Normal;
            }
            catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveBounds();
            if (!quitting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;      // close button = minimise to tray
                Hide();
                return;
            }
            quitting = true;          // shutdown, logoff, installer: do not try to revive WebView2 on the way out
            base.OnFormClosing(e);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            TrackBounds();
        }
        protected override void OnMove(EventArgs e)
        {
            base.OnMove(e);
            TrackBounds();
        }
        void TrackBounds()
        {
            if (WindowState == FormWindowState.Normal) normalBounds = Bounds;
            if (WindowState != FormWindowState.Minimized) maximized = WindowState == FormWindowState.Maximized;
        }

        // ---------- tray ----------
        void BuildTray()
        {
            autoStartItem.Checked = IsAutoStart();
            autoStartItem.Click += delegate
            {
                try { SetAutoStart(!autoStartItem.Checked); } catch (Exception ex) { Program.LogError(ex); }
                autoStartItem.Checked = IsAutoStart();
            };
            ContextMenu menu = new ContextMenu(new MenuItem[] {
                new MenuItem("Open " + AppName, delegate { ShowWindow(); }),
                autoStartItem,
                new MenuItem("-"),
                new MenuItem("Quit", delegate { QuitApp(); })
            });
            tray.ContextMenu = menu;
            tray.Icon = baseIcon;
            tray.Text = AppName;
            tray.Visible = true;
            // Pressing the tray icon activates the taskbar first, so by the time the click arrives our window has
            // already lost focus. Decide at mouse-down whether it was in front, allowing for that deactivation.
            tray.MouseDown += delegate (object s, MouseEventArgs a)
            {
                if (a.Button != MouseButtons.Left) return;
                trayClickWasActive = Visible && WindowState != FormWindowState.Minimized &&
                    (ContainsFocus || unchecked(Environment.TickCount - lastDeactivateTick) < 500);
            };
            tray.MouseClick += delegate (object s, MouseEventArgs a)
            {
                if (a.Button != MouseButtons.Left) return;
                if (trayClickWasActive) Hide(); else ShowWindow();
            };
        }

        void OnTitle(string title)
        {
            Match m = Regex.Match(title ?? "", @"^\((\d+)\)");
            int n = m.Success ? ParseCount(m.Groups[1].Value) : 0;
            if (n == unread) return;
            unread = n;
            Text = n > 0 ? "(" + n + ") " + AppName : AppName;
            tray.Text = n > 0 ? AppName + " - " + n + " unread" : AppName;
            if (n > 0 && badgeIcon == null) badgeIcon = BadgedIcon();
            Icon ic = n > 0 ? badgeIcon : baseIcon;
            tray.Icon = ic;  // same icon for the tray and the taskbar button
            Icon = ic;
        }

        // Regex \d also matches non-ASCII digits (e.g. Arabic-Indic), which int.Parse rejects; cap to avoid overflow.
        static int ParseCount(string digits)
        {
            int n = 0;
            foreach (char c in digits)
            {
                n = n * 10 + (int)char.GetNumericValue(c);
                if (n > 99999) return 99999;
            }
            return n;
        }

        Icon BadgedIcon()
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                using (SolidBrush red = new SolidBrush(Color.FromArgb(0xe0, 0x2e, 0x2e)))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.DrawIcon(baseIcon, new Rectangle(0, 0, 32, 32));
                    g.FillEllipse(Brushes.White, 17, 17, 15, 15);
                    g.FillEllipse(red, 19, 19, 11, 11);
                }
                IntPtr h = bmp.GetHicon();
                try
                {
                    using (Icon tmp = Icon.FromHandle(h)) { return (Icon)tmp.Clone(); }
                }
                finally { NativeMethods.DestroyIcon(h); }
            }
        }

        // ---------- helpers ----------
        // Top-level pages allowed inside the window: WhatsApp Web, its blob: URLs, and about:blank.
        // Everything else (help pages, other WhatsApp sites, links) opens in the default browser.
        static bool IsAppPage(string url)
        {
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u)) return false;
            if (u.Scheme == "about") return true;
            if (u.Scheme == "blob") return url.StartsWith("blob:" + StartUrl, StringComparison.OrdinalIgnoreCase);
            return IsAppOrigin(u);
        }

        // Only WhatsApp Web itself is granted notifications, microphone, camera, etc.
        static bool IsAppOrigin(string url)
        {
            Uri u;
            return Uri.TryCreate(url, UriKind.Absolute, out u) && IsAppOrigin(u);
        }
        static bool IsAppOrigin(Uri u)
        {
            return u.Scheme == Uri.UriSchemeHttps && u.IsDefaultPort && string.Equals(u.Host, AppHost, StringComparison.OrdinalIgnoreCase);
        }

        static void OpenExternal(string url)
        {
            try
            {
                Uri u;
                if (!Uri.TryCreate(url, UriKind.Absolute, out u)) return;
                if (u.Scheme == "http" || u.Scheme == "https" || u.Scheme == "mailto" || u.Scheme == "tel")
                    using (Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true })) { }
            }
            catch { }
        }

        static bool IsAutoStart()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey)) { return k != null && k.GetValue(AppName) != null; }
            }
            catch { return false; }
        }
        static void SetAutoStart(bool on)
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (k == null) return;
                if (on) k.SetValue(AppName, "\"" + Application.ExecutablePath + "\" --hidden");
                else k.DeleteValue(AppName, false);
            }
        }

        static string StatePath { get { return Path.Combine(Program.DataDir, "window.txt"); } }
        void SaveBounds()
        {
            try
            {
                Directory.CreateDirectory(Program.DataDir);
                Rectangle b = normalBounds.IsEmpty ? Bounds : normalBounds;
                File.WriteAllText(StatePath, string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3},{4}",
                    b.X, b.Y, b.Width, b.Height, maximized ? 1 : 0));
            }
            catch { }
        }
        void LoadBounds()
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Rectangle def = new Rectangle(0, 0, Math.Min(1100, wa.Width), Math.Min(750, wa.Height));
            def.X = wa.X + (wa.Width - def.Width) / 2; def.Y = wa.Y + (wa.Height - def.Height) / 2;
            bool max = false;
            try
            {
                string[] p = File.ReadAllText(StatePath).Split(',');
                int[] v = new int[4];
                for (int i = 0; i < 4; i++) v[i] = int.Parse(p[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);
                Rectangle r = new Rectangle(v[0], v[1], v[2], v[3]);
                if (r.Width >= MinimumSize.Width && r.Height >= MinimumSize.Height && TitleBarOnScreen(r)) def = r;
                max = p.Length > 4 && p[4].Trim() == "1";
            }
            catch { }
            Bounds = def; normalBounds = def; maximized = max;
            if (max) WindowState = FormWindowState.Maximized;
        }

        // Enough of the title bar must be on a connected screen to grab it; otherwise use the default position
        // (e.g. the window was last on a monitor that has since been unplugged).
        static bool TitleBarOnScreen(Rectangle r)
        {
            Rectangle bar = new Rectangle(r.X, r.Y, r.Width, 32);
            foreach (Screen sc in Screen.AllScreens)
            {
                Rectangle i = Rectangle.Intersect(sc.WorkingArea, bar);
                if (i.Width >= 120 && i.Height >= 8) return true;
            }
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { tray.Dispose(); retryTimer.Dispose(); }
            base.Dispose(disposing);
            if (disposing && badgeIcon != null) badgeIcon.Dispose(); // after the form, which may still reference it
        }
    }

    static class NativeMethods
    {
        public const int ASFW_ANY = -1;

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr handle);

        [DllImport("user32.dll")]
        public static extern bool AllowSetForegroundWindow(int processId);
    }
}
