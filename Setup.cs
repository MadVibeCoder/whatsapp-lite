// Tiny per-user installer/uninstaller for VibeChat. The app files are embedded as app.zip.
//   Setup.exe            interactive install (or update over an existing install)
//   Setup.exe /S         silent install (no prompts, no launch)
//   Setup.exe /S /launch | /launch-hidden
//                        silent install, then start the app (hidden in the tray). Used by the app's own updater;
//                        if the install fails, the existing app is started again so the user is not left without it.
//   Uninstall.exe        (copy of this exe placed in the install dir) uninstalls
//   Setup.exe /uninstall [/S]
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("VibeChat Setup")]
[assembly: AssemblyProduct("VibeChat")]
[assembly: AssemblyDescription("Installer for VibeChat")]
// Version numbers live in Version.cs (shared with the app).

static class Setup
{
    const string AppName = "VibeChat";
    const string Exe = AppName + ".exe";
    const string UninstKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\VibeChat";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string QuitEvent = "VibeChat.Quit";

    static string InstallDir
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName); }
    }
    static string DataDir
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VibeChat"); }
    }
    static string DesktopLnk { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"); } }
    static string StartLnk { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk"); } }

    // e.g. "1.1.0", from Version.cs
    static string AppVersion
    {
        get { Version v = Assembly.GetExecutingAssembly().GetName().Version; return v.Major + "." + v.Minor + "." + v.Build; }
    }

    static bool HasArg(string[] args, string name)
    {
        foreach (string a in args) if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    [STAThread]
    static int Main(string[] args)
    {
        bool silent = HasArg(args, "/S");
        bool uninstall = HasArg(args, "/uninstall") ||
            string.Equals(Path.GetFileNameWithoutExtension(Application.ExecutablePath), "Uninstall", StringComparison.OrdinalIgnoreCase);
        bool launchHidden = HasArg(args, "/launch-hidden");
        bool launch = launchHidden || HasArg(args, "/launch");
        Application.EnableVisualStyles();
        try
        {
            int result = uninstall ? DoUninstall(silent) : DoInstall(silent);
            if (launch && !uninstall && result == 0) LaunchApp(launchHidden);
            return result;
        }
        catch (Exception ex)
        {
            if (!silent) MessageBox.Show(ex.Message, AppName + " setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            if (launch && !uninstall) LaunchApp(launchHidden); // an update failed: bring back the version that was running
            return 1;
        }
    }

    static void LaunchApp(bool hidden)
    {
        string exePath = Path.Combine(InstallDir, Exe);
        if (!File.Exists(exePath)) return;
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(exePath, hidden ? "--hidden" : "");
            psi.WorkingDirectory = InstallDir;
            using (Process.Start(psi)) { }
        }
        catch (Exception) { /* nothing more we can do silently */ }
    }

    // Running copies of the app in this Windows session (other users' copies are not ours to stop).
    static List<Process> AppProcesses()
    {
        int session = Process.GetCurrentProcess().SessionId;
        List<Process> mine = new List<Process>();
        foreach (Process p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Exe)))
        {
            bool ours = false;
            try { ours = p.SessionId == session; } catch { }
            if (ours) mine.Add(p); else p.Dispose();
        }
        return mine;
    }

    static void DisposeAll(List<Process> procs)
    {
        foreach (Process p in procs) p.Dispose();
    }

    // Ask a running copy to quit cleanly (so its WhatsApp session data is flushed), then force it as a last resort.
    static void StopApp()
    {
        List<Process> procs = AppProcesses();
        if (procs.Count == 0) return;
        try { using (EventWaitHandle ev = EventWaitHandle.OpenExisting(QuitEvent)) ev.Set(); } catch { }
        DateTime end = DateTime.UtcNow.AddSeconds(8);
        while (procs.Count > 0 && DateTime.UtcNow < end)
        {
            DisposeAll(procs);
            Thread.Sleep(200);
            procs = AppProcesses();
        }
        foreach (Process p in procs)
        {
            try { p.Kill(); p.WaitForExit(5000); } catch { }
            p.Dispose();
        }
        Thread.Sleep(800); // let WebView2 helper processes exit and release their files
    }

    static int DoInstall(bool silent)
    {
        if (!silent && MessageBox.Show("Install " + AppName + " " + AppVersion + " to:\n" + InstallDir + "?", AppName + " setup",
            MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return 2;

        bool upgrade;
        using (RegistryKey k = Registry.CurrentUser.OpenSubKey(UninstKey)) { upgrade = k != null; }

        StopApp();
        Directory.CreateDirectory(InstallDir);
        string root = InstallDir + Path.DirectorySeparatorChar;
        long bytes = 0;
        using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.zip"))
        {
            if (s == null) throw new InvalidOperationException("This installer is damaged (the app files are missing).");
            using (ZipArchive zip = new ZipArchive(s, ZipArchiveMode.Read))
            {
                foreach (ZipArchiveEntry e in zip.Entries)
                {
                    if (string.IsNullOrEmpty(e.Name)) continue;
                    string dest = Path.GetFullPath(Path.Combine(InstallDir, e.FullName));
                    if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // no path traversal
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    e.ExtractToFile(dest, true);
                    bytes += e.Length;
                }
            }
        }
        string uninst = Path.Combine(InstallDir, "Uninstall.exe");
        if (!string.Equals(Path.GetFullPath(Application.ExecutablePath), uninst, StringComparison.OrdinalIgnoreCase))
            File.Copy(Application.ExecutablePath, uninst, true);
        bytes += new FileInfo(uninst).Length;

        string exePath = Path.Combine(InstallDir, Exe);
        // On an update, respect a desktop shortcut the user has deleted.
        if (!upgrade || File.Exists(DesktopLnk)) MakeShortcut(DesktopLnk, exePath);
        MakeShortcut(StartLnk, exePath);

        using (RegistryKey k = Registry.CurrentUser.CreateSubKey(UninstKey))
        {
            k.SetValue("DisplayName", AppName);
            k.SetValue("DisplayIcon", exePath);
            k.SetValue("DisplayVersion", AppVersion);
            k.SetValue("Publisher", "Local");
            k.SetValue("InstallLocation", InstallDir);
            k.SetValue("UninstallString", "\"" + uninst + "\"");
            k.SetValue("QuietUninstallString", "\"" + uninst + "\" /S");
            k.SetValue("EstimatedSize", (int)(bytes / 1024), RegistryValueKind.DWord);
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }

        if (!silent && MessageBox.Show(AppName + " " + AppVersion + " is installed.\n\nLaunch it now?", AppName + " setup",
            MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
            using (Process.Start(new ProcessStartInfo(exePath) { WorkingDirectory = InstallDir })) { }
        return 0;
    }

    static int DoUninstall(bool silent)
    {
        if (!silent && MessageBox.Show("Uninstall " + AppName + "?", AppName + " setup", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return 2;
        bool wipe = !silent && MessageBox.Show("Also remove your saved WhatsApp login and settings?\n(You will need to scan the QR code again if you reinstall.)",
            AppName + " setup", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

        StopApp();
        try { File.Delete(DesktopLnk); } catch { }
        try { File.Delete(StartLnk); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstKey, false); } catch { }
        try { using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true)) { if (k != null) k.DeleteValue(AppName, false); } } catch { }

        List<string> leftovers = new List<string>();
        leftovers.Add(InstallDir);
        if (wipe)
        {
            for (int i = 0; i < 4 && Directory.Exists(DataDir); i++)
            {
                try { Directory.Delete(DataDir, true); } catch { Thread.Sleep(700); }
            }
            if (Directory.Exists(DataDir)) leftovers.Add(DataDir); // WebView2 still had files open; retry after we exit
        }

        if (!silent) MessageBox.Show(AppName + " was uninstalled.", AppName + " setup", MessageBoxButtons.OK, MessageBoxIcon.Information);
        // This exe lives in the install dir, so the folder is removed by a detached helper once we have exited.
        DeleteAfterExit(leftovers);
        return 0;
    }

    // Detached cmd.exe that retries (about once a second, for up to 30 s) until every folder is gone,
    // so it does not matter exactly when this process gets around to exiting.
    static void DeleteAfterExit(List<string> dirs)
    {
        string remove = "", allGone = "";
        foreach (string d in dirs)
        {
            remove += "rmdir /s /q \"" + d + "\" 2>nul & ";
            allGone += "if not exist \"" + d + "\" ";
        }
        string cmd = "/d /c for /l %i in (1,1,30) do @(ping -n 2 127.0.0.1 >nul & " + remove + allGone + "exit)";
        ProcessStartInfo psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), cmd);
        psi.CreateNoWindow = true;
        psi.UseShellExecute = false;
        psi.WorkingDirectory = Environment.SystemDirectory; // never the install dir, or it could not be removed
        using (Process.Start(psi)) { }
    }

    static void MakeShortcut(string lnkPath, string target)
    {
        try
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            object sh = Activator.CreateInstance(t);
            object lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh, new object[] { lnkPath });
            Type lt = lnk.GetType();
            lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { target });
            lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { Path.GetDirectoryName(target) });
            lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { target + ",0" });
            lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { AppName });
            lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
        }
        catch { /* shortcut is a convenience; do not fail the install */ }
    }
}
