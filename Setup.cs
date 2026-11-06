// Tiny per-user installer/uninstaller for WhatsApp Lite. The app files are embedded as app.zip.
//   Setup.exe            interactive install
//   Setup.exe /S         silent install (no prompts, no launch)
//   Uninstall.exe        (copy of this exe placed in the install dir) uninstalls
//   Setup.exe /uninstall [/S]
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("WhatsApp Lite Setup")]
[assembly: AssemblyProduct("WhatsApp Lite")]
[assembly: AssemblyDescription("Installer for WhatsApp Lite")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

static class Setup
{
    const string AppName = "WhatsApp Lite";
    const string Exe = "WhatsApp Lite.exe";
    const string UninstKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WhatsAppLite";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    static string InstallDir
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName); }
    }
    static string DataDir
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhatsAppLite"); }
    }
    static string DesktopLnk { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"); } }
    static string StartLnk { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk"); } }

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
        Application.EnableVisualStyles();
        try { return uninstall ? DoUninstall(silent) : DoInstall(silent); }
        catch (Exception ex)
        {
            if (!silent) MessageBox.Show(ex.Message, AppName + " setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    // Ask a running copy to quit cleanly (so its WhatsApp session data is flushed), then force it as a last resort.
    static void StopApp()
    {
        try { EventWaitHandle.OpenExisting("WhatsAppLite.Quit").Set(); } catch { }
        DateTime end = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < end && Process.GetProcessesByName("WhatsApp Lite").Length > 0) Thread.Sleep(200);
        foreach (Process p in Process.GetProcessesByName("WhatsApp Lite"))
        {
            try { p.Kill(); p.WaitForExit(5000); } catch { }
        }
        Thread.Sleep(800); // let WebView2 helper processes exit and release their files
    }

    static int DoInstall(bool silent)
    {
        if (!silent && MessageBox.Show("Install " + AppName + " to:\n" + InstallDir + "?", AppName + " setup",
            MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return 2;

        StopApp();
        Directory.CreateDirectory(InstallDir);
        long bytes = 0;
        using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.zip"))
        using (ZipArchive zip = new ZipArchive(s, ZipArchiveMode.Read))
        {
            foreach (ZipArchiveEntry e in zip.Entries)
            {
                if (string.IsNullOrEmpty(e.Name)) continue;
                string dest = Path.GetFullPath(Path.Combine(InstallDir, e.FullName));
                if (!dest.StartsWith(InstallDir + Path.DirectorySeparatorChar)) continue; // no path traversal
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                e.ExtractToFile(dest, true);
                bytes += e.Length;
            }
        }
        string uninst = Path.Combine(InstallDir, "Uninstall.exe");
        File.Copy(Application.ExecutablePath, uninst, true);
        bytes += new FileInfo(uninst).Length;

        string exePath = Path.Combine(InstallDir, Exe);
        MakeShortcut(DesktopLnk, exePath);
        MakeShortcut(StartLnk, exePath);

        using (RegistryKey k = Registry.CurrentUser.CreateSubKey(UninstKey))
        {
            k.SetValue("DisplayName", AppName);
            k.SetValue("DisplayIcon", exePath);
            k.SetValue("DisplayVersion", "1.0.0");
            k.SetValue("Publisher", "Local");
            k.SetValue("InstallLocation", InstallDir);
            k.SetValue("UninstallString", "\"" + uninst + "\"");
            k.SetValue("QuietUninstallString", "\"" + uninst + "\" /S");
            k.SetValue("EstimatedSize", (int)(bytes / 1024), RegistryValueKind.DWord);
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }

        if (!silent && MessageBox.Show(AppName + " is installed.\n\nLaunch it now?", AppName + " setup",
            MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
            Process.Start(exePath);
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
        if (wipe)
        {
            for (int i = 0; i < 4 && Directory.Exists(DataDir); i++)
            {
                try { Directory.Delete(DataDir, true); } catch { Thread.Sleep(700); }
            }
        }

        // This exe lives in the install dir, so delete the folder from a detached helper after we exit.
        string cmd = "/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"" + InstallDir + "\"";
        Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
        if (!silent) MessageBox.Show(AppName + " was uninstalled.", AppName + " setup", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
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
