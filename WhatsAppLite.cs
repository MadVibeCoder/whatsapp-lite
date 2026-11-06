// WhatsApp Lite (WebView2) - tiny native wrapper around WhatsApp Web.
// Compiles with the C# 5 compiler that ships with Windows (no SDK needed).
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

[assembly: AssemblyTitle("WhatsApp Lite")]
[assembly: AssemblyProduct("WhatsApp Lite")]
[assembly: AssemblyDescription("Lightweight WhatsApp Web desktop client")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace WhatsAppLite
{
    static class Program
    {
        internal static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhatsAppLite");

        [STAThread]
        static void Main(string[] args)
        {
            bool hidden = Array.IndexOf(args, "--hidden") >= 0;
            bool createdNew;
            Mutex mutex = new Mutex(true, "WhatsAppLite.SingleInstance", out createdNew);
            if (!createdNew)
            {
                // Already running: ask that instance to show itself (but not for the silent autostart launch).
                if (!hidden) { try { EventWaitHandle.OpenExisting("WhatsAppLite.Show").Set(); } catch { } }
                mutex.Dispose();
                return;
            }
            try
            {
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
            try
            {
                Directory.CreateDirectory(DataDir);
                string path = Path.Combine(DataDir, "error.log");
                FileInfo fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 256 * 1024) File.Delete(path);
                File.AppendAllText(path, DateTime.Now.ToString("s") + " " + ex + Environment.NewLine);
            }
            catch { }
        }
    }

    class MainForm : Form
    {
        const string AppName = "WhatsApp Lite";
        const string StartUrl = "https://web.whatsapp.com/";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        readonly WebView2 web = new WebView2();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly MenuItem autoStartItem = new MenuItem("Start with Windows");
        readonly System.Windows.Forms.Timer retryTimer = new System.Windows.Forms.Timer();
        readonly bool startHidden;
        readonly Icon baseIcon;
        readonly EventWaitHandle showEvent;
        readonly EventWaitHandle quitEvent;
        bool quitting;
        bool webInitStarted;
        int unread = -1;
        Rectangle normalBounds;

        public MainForm(bool hidden)
        {
            startHidden = hidden;
            Text = AppName;
            baseIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Icon = baseIcon;
            BackColor = Color.FromArgb(0x11, 0x1b, 0x21);
            MinimumSize = new Size(480, 500);
            StartPosition = FormStartPosition.Manual;
            LoadBounds();

            web.Dock = DockStyle.Fill;
            web.DefaultBackgroundColor = Color.FromArgb(0x11, 0x1b, 0x21); // no white flash while loading
            web.CreationProperties = new CoreWebView2CreationProperties();
            web.CreationProperties.UserDataFolder = Path.Combine(Program.DataDir, "WebView2Data");
            Controls.Add(web);

            BuildTray();

            retryTimer.Interval = 5000;
            retryTimer.Tick += delegate { retryTimer.Stop(); if (web.CoreWebView2 != null) web.CoreWebView2.Navigate(StartUrl); };

            // Cross-process signals: a second launch asks us to show; the installer asks us to quit cleanly.
            showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "WhatsAppLite.Show");
            quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "WhatsAppLite.Quit");
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
                    catch (InvalidOperationException) { /* handle not created yet; ignore this signal */ }
                }
            });
            t.IsBackground = true;
            t.Start();
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

        async void InitWebView()
        {
            if (webInitStarted) return;
            webInitStarted = true;
            try
            {
                await web.EnsureCoreWebView2Async(null);
            }
            catch (Exception ex)
            {
                Program.LogError(ex);
                if (MessageBox.Show("WhatsApp Lite needs the Microsoft Edge WebView2 Runtime, which could not be started.\n\n" + ex.Message +
                    "\n\nOpen the download page?", AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Error) == DialogResult.Yes)
                    Process.Start("https://developer.microsoft.com/microsoft-edge/webview2/");
                QuitApp();
                return;
            }
            CoreWebView2 core = web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsWebMessageEnabled = false;       // we do not use host<->page messaging
            core.Settings.AreHostObjectsAllowed = false;     // nor injected host objects

            core.NewWindowRequested += delegate (object s, CoreWebView2NewWindowRequestedEventArgs a) { a.Handled = true; OpenExternal(a.Uri); };
            core.NavigationStarting += delegate (object s, CoreWebView2NavigationStartingEventArgs a)
            {
                if (!IsWhatsApp(a.Uri)) { a.Cancel = true; OpenExternal(a.Uri); }
            };
            core.NavigationCompleted += delegate (object s, CoreWebView2NavigationCompletedEventArgs a)
            {
                if (!a.IsSuccess && a.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled) retryTimer.Start(); // offline? retry
            };
            core.DocumentTitleChanged += delegate { OnTitle(core.DocumentTitle); };
            core.ProcessFailed += delegate (object s, CoreWebView2ProcessFailedEventArgs a)
            {
                if (a.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited) return;
                BeginInvoke(new Action(delegate { try { core.Reload(); } catch { } })); // renderer crashed/hung: reload page
            };
            core.PermissionRequested += delegate (object s, CoreWebView2PermissionRequestedEventArgs a)
            {
                bool ok = IsWhatsApp(a.Uri) && (a.PermissionKind == CoreWebView2PermissionKind.Notifications ||
                    a.PermissionKind == CoreWebView2PermissionKind.Microphone ||
                    a.PermissionKind == CoreWebView2PermissionKind.Camera ||
                    a.PermissionKind == CoreWebView2PermissionKind.ClipboardRead);
                a.State = ok ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
            };

            SetMemoryTarget(!Visible);
            core.Navigate(StartUrl);
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
            base.OnFormClosing(e);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Normal) normalBounds = Bounds;
        }
        protected override void OnMove(EventArgs e)
        {
            base.OnMove(e);
            if (WindowState == FormWindowState.Normal) normalBounds = Bounds;
        }

        // ---------- tray ----------
        void BuildTray()
        {
            autoStartItem.Checked = IsAutoStart();
            autoStartItem.Click += delegate { SetAutoStart(!autoStartItem.Checked); autoStartItem.Checked = IsAutoStart(); };
            ContextMenu menu = new ContextMenu(new MenuItem[] {
                new MenuItem("Open WhatsApp", delegate { ShowWindow(); }),
                autoStartItem,
                new MenuItem("-"),
                new MenuItem("Quit", delegate { QuitApp(); })
            });
            tray.ContextMenu = menu;
            tray.Icon = baseIcon;
            tray.Text = AppName;
            tray.Visible = true;
            tray.MouseClick += delegate (object s, MouseEventArgs a)
            {
                if (a.Button != MouseButtons.Left) return;
                if (Visible && ContainsFocus) Hide(); else ShowWindow();
            };
        }

        void OnTitle(string title)
        {
            Match m = Regex.Match(title ?? "", @"^\((\d+)\)");
            int n = m.Success ? int.Parse(m.Groups[1].Value) : 0;
            if (n == unread) return;
            unread = n;
            Text = n > 0 ? "(" + n + ") " + AppName : AppName;
            tray.Text = n > 0 ? AppName + " - " + n + " unread" : AppName;
            ApplyIcon(n > 0 ? BadgedIcon() : baseIcon);
        }

        // Same icon for the tray and the taskbar button.
        void ApplyIcon(Icon ic)
        {
            Icon old = tray.Icon;
            tray.Icon = ic;
            Icon = ic;
            if (old != null && !object.ReferenceEquals(old, baseIcon) && !object.ReferenceEquals(old, ic)) old.Dispose();
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
        static bool IsWhatsApp(string url)
        {
            try
            {
                Uri u = new Uri(url);
                if (u.Scheme == "about" || u.Scheme == "blob") return true;
                return u.Scheme == Uri.UriSchemeHttps && (u.Host == "web.whatsapp.com" || u.Host.EndsWith(".whatsapp.com") || u.Host.EndsWith(".whatsapp.net"));
            }
            catch { return false; }
        }

        static void OpenExternal(string url)
        {
            try
            {
                Uri u = new Uri(url);
                if (u.Scheme == "http" || u.Scheme == "https" || u.Scheme == "mailto" || u.Scheme == "tel")
                    Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
            }
            catch { }
        }

        static bool IsAutoStart()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey)) { return k != null && k.GetValue(AppName) != null; }
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
                File.WriteAllText(StatePath, b.X + "," + b.Y + "," + b.Width + "," + b.Height + "," + (WindowState == FormWindowState.Maximized ? 1 : 0));
            }
            catch { }
        }
        void LoadBounds()
        {
            Rectangle def = new Rectangle(0, 0, 1100, 750);
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            def.Width = Math.Min(def.Width, wa.Width); def.Height = Math.Min(def.Height, wa.Height);
            def.X = wa.X + (wa.Width - def.Width) / 2; def.Y = wa.Y + (wa.Height - def.Height) / 2;
            bool max = false;
            try
            {
                string[] p = File.ReadAllText(StatePath).Split(',');
                Rectangle r = new Rectangle(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3]));
                if (r.Width >= 480 && r.Height >= 500)
                    foreach (Screen sc in Screen.AllScreens)
                        if (sc.WorkingArea.IntersectsWith(r)) { def = r; break; }
                max = p[4] == "1";
            }
            catch { }
            Bounds = def; normalBounds = def;
            if (max) WindowState = FormWindowState.Maximized;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { tray.Dispose(); retryTimer.Dispose(); }
            base.Dispose(disposing);
        }
    }

    static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr handle);
    }
}
