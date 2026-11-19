using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Undelete.Gui;

namespace Undelete.Setup
{
    /// <summary>
    /// Per-user installer for Neon Undelete. No admin rights needed: installs to
    /// %LOCALAPPDATA%\Programs\Neon Undelete, adds shortcuts and an "Installed apps" entry.
    ///   Setup.exe                      interactive install / update
    ///   Setup.exe /silent [/desktop] [/admin] [/path]
    ///   uninstall.exe /uninstall [/silent]
    /// </summary>
    static class Installer
    {
        public const string AppName = "Neon Undelete";
        public const string Version = AppInfo.Version;
        const string RegKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\NeonUndelete";
        static readonly string[] Payload = { "UndeleteGui.exe", "Undelete.exe", "app.ico", "README.md" };

        public static string DefaultDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName); } }
        static string StartMenuLnk { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk"); } }
        static string DesktopLnk { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"); } }

        public static string InstalledDir()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RegKey))
                return k == null ? null : k.GetValue("InstallLocation") as string;
        }

        public static void Install(string dir, bool desktop, bool runAsAdmin, bool addPath, Action<double, string> progress)
        {
            if (Process.GetProcessesByName("UndeleteGui").Any(p => SafePath(p).StartsWith(dir, StringComparison.OrdinalIgnoreCase)) ||
                Process.GetProcessesByName("Undelete").Any(p => SafePath(p).StartsWith(dir, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Neon Undelete is running from the install folder. Close it and try again.");

            Directory.CreateDirectory(dir);
            var asm = Assembly.GetExecutingAssembly();
            int step = 0, steps = Payload.Length + 4;
            foreach (var name in Payload)
            {
                progress((double)step++ / steps, "copying " + name);
                using (var s = asm.GetManifestResourceStream("payload/" + name))
                {
                    if (s == null) throw new InvalidDataException("installer is missing " + name);
                    using (var f = File.Create(Path.Combine(dir, name))) s.CopyTo(f);
                }
                Thread.Sleep(60);
            }

            progress((double)step++ / steps, "adding uninstaller");
            string self = asm.Location, uninst = Path.Combine(dir, "uninstall.exe");
            if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(uninst), StringComparison.OrdinalIgnoreCase))
                File.Copy(self, uninst, true);

            progress((double)step++ / steps, "creating shortcuts");
            string exe = Path.Combine(dir, "UndeleteGui.exe"), ico = Path.Combine(dir, "app.ico");
            Shortcut(StartMenuLnk, exe, ico, dir, runAsAdmin);
            if (desktop) Shortcut(DesktopLnk, exe, ico, dir, runAsAdmin);
            else if (File.Exists(DesktopLnk)) File.Delete(DesktopLnk);

            progress((double)step++ / steps, addPath ? "adding 'undelete' to PATH" : "registering");
            SetUserPath(dir, addPath);

            using (var k = Registry.CurrentUser.CreateSubKey(RegKey))
            {
                k.SetValue("DisplayName", AppName);
                k.SetValue("DisplayVersion", Version);
                k.SetValue("DisplayIcon", ico);
                k.SetValue("Publisher", AppName);
                k.SetValue("InstallLocation", dir);
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                k.SetValue("UninstallString", "\"" + uninst + "\" /uninstall");
                k.SetValue("QuietUninstallString", "\"" + uninst + "\" /uninstall /silent");
                k.SetValue("EstimatedSize", (int)(Directory.GetFiles(dir).Sum(f => new FileInfo(f).Length) / 1024), RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("Desktop", desktop ? 1 : 0, RegistryValueKind.DWord);
                k.SetValue("RunAsAdmin", runAsAdmin ? 1 : 0, RegistryValueKind.DWord);
                k.SetValue("AddedToPath", addPath ? 1 : 0, RegistryValueKind.DWord);
            }
            progress(1, "installed");
        }

        public static void Uninstall()
        {
            string dir = InstalledDir() ?? Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            foreach (var lnk in new[] { StartMenuLnk, DesktopLnk }) if (File.Exists(lnk)) File.Delete(lnk);
            SetUserPath(dir, false);
            Registry.CurrentUser.DeleteSubKeyTree(RegKey, false);

            string self = Assembly.GetExecutingAssembly().Location;
            foreach (var name in Payload)
            {
                string f = Path.Combine(dir, name);
                try { if (File.Exists(f)) File.Delete(f); } catch { }
            }
            bool selfInDir = string.Equals(Path.GetDirectoryName(self), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            if (selfInDir)
            {
                // the running uninstaller can't delete itself: remove it and the folder once this process has exited
                Process.Start(new ProcessStartInfo("cmd.exe",
                    "/c ping -n 3 127.0.0.1 >nul & del /f /q \"" + self + "\" & rmdir \"" + dir + "\"")
                { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            }
            else
            {
                try { File.Delete(Path.Combine(dir, "uninstall.exe")); Directory.Delete(dir, false); } catch { }
            }
        }

        static string SafePath(Process p) { try { return p.MainModule.FileName; } catch { return ""; } }

        static void Shortcut(string lnk, string target, string icon, string workDir, bool runAsAdmin)
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(t);
            object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
            Type st = sc.GetType();
            st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
            st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { workDir });
            st.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { icon + ",0" });
            st.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { "Recover deleted files and folders from NTFS, FAT32 and exFAT drives" });
            st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
            Marshal.FinalReleaseComObject(sc);
            Marshal.FinalReleaseComObject(shell);
            if (runAsAdmin)
            {
                // LinkFlags bit 0x2000 (RunAsUser) = the "Run as administrator" checkbox
                byte[] b = File.ReadAllBytes(lnk);
                b[0x15] |= 0x20;
                File.WriteAllBytes(lnk, b);
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessageTimeout(IntPtr h, int msg, IntPtr w, string l, int flags, int timeout, out IntPtr result);

        /// <summary>Adds or removes the install folder from the per-user PATH.</summary>
        static void SetUserPath(string dir, bool add)
        {
            using (var env = Registry.CurrentUser.OpenSubKey("Environment", true))
            {
                if (env == null) return;
                string cur = env.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
                var parts = cur.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                bool has = parts.Any(p => string.Equals(p.TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
                if (add == has) return;
                if (add) parts.Add(dir);
                else parts.RemoveAll(p => string.Equals(p.TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
                env.SetValue("Path", string.Join(";", parts), RegistryValueKind.ExpandString);
            }
            IntPtr r;
            SendMessageTimeout((IntPtr)0xFFFF, 0x1A, IntPtr.Zero, "Environment", 2, 2000, out r);   // WM_SETTINGCHANGE
        }
    }

    /// <summary>[■] label checkbox in the neon style.</summary>
    sealed class NeonCheck : NeonControl
    {
        public bool Checked;
        public Color Accent;
        public string Hint = "";
        bool hover;

        public NeonCheck(string text, bool on, Color accent, string hint = "")
        {
            Text = text; Checked = on; Accent = accent; Hint = hint;
            Height = N.S(hint.Length > 0 ? 40 : 24);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); }
        protected override void OnClick(EventArgs e) { Checked = !Checked; Invalidate(); base.OnClick(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(N.Panel);
            int bx = N.S(2), by = N.S(5), bs = N.S(14);
            using (var p = new Pen(Checked || hover ? Accent : N.Dim)) g.DrawRectangle(p, bx, by, bs, bs);
            if (Checked) using (var b = new SolidBrush(Accent)) g.FillRectangle(b, bx + N.S(3), by + N.S(3), bs - N.S(5), bs - N.S(5));
            N.Draw(g, Text, Font, new Rectangle(N.S(26), 0, Width - N.S(26), N.S(24)), Checked ? N.Text : N.Dim);
            if (Hint.Length > 0) N.Draw(g, Hint, N.Mono(8f), new Rectangle(N.S(26), N.S(20), Width - N.S(26), N.S(18)), N.Dim);
        }
    }

    sealed class SetupForm : Form
    {
        readonly Logo logo = new Logo();
        readonly NeonPanel panel = new NeonPanel { Title = "install", Accent = N.Magenta };
        readonly Label pathLabel = new Label();
        readonly NeonButton changeBtn = new NeonButton("change", N.Teal);
        readonly NeonCheck desktop = new NeonCheck("desktop shortcut", true, N.Teal);
        readonly NeonCheck admin = new NeonCheck("always start as administrator", false, N.Orange, "skips the in-app restart; scanning drives needs admin");
        readonly NeonCheck path = new NeonCheck("add 'undelete' command to PATH", false, N.Purple, "use the terminal version from any console");
        readonly NeonMeter meter = new NeonMeter();
        readonly Label statusLabel = new Label();
        readonly NeonButton installBtn = new NeonButton("install", N.Crimson, true);
        readonly NeonButton cancelBtn = new NeonButton("cancel", N.Dim);
        readonly NeonButton uninstallBtn = new NeonButton("uninstall", N.Orange);
        string dir;
        bool done;

        public SetupForm()
        {
            string existing = Installer.InstalledDir();
            dir = existing ?? Installer.DefaultDir;
            Text = Installer.AppName + " setup";
            BackColor = N.Bg; ForeColor = N.Text; Font = N.Mono(9.5f);
            FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(N.S(660), N.S(470));
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            logo.Subtitle = "setup  ·  v" + Installer.Version + "  ·  installs for " + Environment.UserName + " only, no admin needed";
            logo.SetBounds(N.S(14), N.S(8), N.S(632), N.S(100));

            panel.SetBounds(N.S(14), N.S(116), N.S(632), N.S(260));
            pathLabel.Font = N.Mono(9f); pathLabel.ForeColor = N.Text; pathLabel.BackColor = N.Panel; pathLabel.AutoEllipsis = true;
            pathLabel.SetBounds(N.S(14), N.S(46), N.S(470), N.S(20));
            var pathKey = new Label { Text = "location", Font = N.Mono(8.5f), ForeColor = N.Dim, BackColor = N.Panel };
            pathKey.SetBounds(N.S(14), N.S(26), N.S(200), N.S(18));
            changeBtn.Width = changeBtn.PreferredWidth; changeBtn.Location = new Point(N.S(618) - changeBtn.Width, N.S(38));
            desktop.SetBounds(N.S(14), N.S(84), N.S(590), desktop.Height);
            admin.SetBounds(N.S(14), N.S(116), N.S(590), admin.Height);
            path.SetBounds(N.S(14), N.S(164), N.S(590), path.Height);
            meter.SetBounds(N.S(14), N.S(214), N.S(604), N.S(12));
            statusLabel.Font = N.Mono(8.5f); statusLabel.ForeColor = N.Dim; statusLabel.BackColor = N.Panel;
            statusLabel.SetBounds(N.S(14), N.S(232), N.S(604), N.S(18));
            panel.Controls.AddRange(new Control[] { pathKey, pathLabel, changeBtn, desktop, admin, path, meter, statusLabel });

            int by = N.S(400);
            installBtn.Text = existing != null ? "update" : "install";
            installBtn.Width = N.S(150); cancelBtn.Width = cancelBtn.PreferredWidth; uninstallBtn.Width = uninstallBtn.PreferredWidth;
            installBtn.Location = new Point(N.S(646) - installBtn.Width, by);
            cancelBtn.Location = new Point(installBtn.Left - N.S(10) - cancelBtn.Width, by);
            uninstallBtn.Location = new Point(N.S(14), by);
            uninstallBtn.Visible = existing != null;

            Controls.AddRange(new Control[] { logo, panel, installBtn, cancelBtn, uninstallBtn });
            UpdatePath();
            statusLabel.Text = existing != null ? "already installed - update replaces the program files" : "ready";

            changeBtn.Click += (s, e) =>
            {
                using (var d = new FolderBrowserDialog { Description = "Install Neon Undelete into…", SelectedPath = Path.GetDirectoryName(dir) })
                    if (d.ShowDialog(this) == DialogResult.OK) { dir = Path.Combine(d.SelectedPath, Installer.AppName); UpdatePath(); }
            };
            cancelBtn.Click += (s, e) => Close();
            installBtn.Click += (s, e) => { if (done) Launch(); else DoInstall(); };
            uninstallBtn.Click += (s, e) =>
            {
                if (DarkDialog.Show(this, "uninstall", "Remove Neon Undelete, its shortcuts and its Installed-apps entry?", N.Orange, "uninstall", "cancel") != 0) return;
                try { Installer.Uninstall(); DarkDialog.Show(this, "uninstalled", "Neon Undelete has been removed.", N.Teal); Close(); }
                catch (Exception ex) { DarkDialog.Show(this, "uninstall failed", ex.Message, N.Crimson); }
            };
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); N.DarkTitleBar(this, N.Magenta); }

        void UpdatePath() { pathLabel.Text = dir; }

        void DoInstall()
        {
            installBtn.Enabled = cancelBtn.Enabled = changeBtn.Enabled = uninstallBtn.Enabled = false;
            bool dsk = desktop.Checked, adm = admin.Checked, pth = path.Checked;
            string target = dir;
            Exception err = null;
            var th = new Thread(() =>
            {
                try { Installer.Install(target, dsk, adm, pth, (f, msg) => BeginInvoke((Action)(() => { meter.Value = f; statusLabel.Text = msg; }))); }
                catch (Exception ex) { err = ex; }
            }) { IsBackground = true };
            th.Start();
            while (th.IsAlive) { Application.DoEvents(); Thread.Sleep(15); }
            Application.DoEvents();

            if (err != null)
            {
                statusLabel.Text = "failed"; statusLabel.ForeColor = N.Crimson;
                DarkDialog.Show(this, "install failed", err.Message, N.Crimson);
                installBtn.Enabled = cancelBtn.Enabled = changeBtn.Enabled = true;
                return;
            }
            done = true;
            meter.Value = 1;
            statusLabel.ForeColor = N.Teal;
            statusLabel.Text = "installed ✓  start menu" + (dsk ? " + desktop shortcut" : "") + (pth ? "  ·  'undelete' on PATH (new terminals)" : "");
            panel.Title = "done"; panel.Accent = N.Teal; panel.Invalidate();
            installBtn.Text = "launch"; installBtn.Enabled = true;
            cancelBtn.Text = "close"; cancelBtn.Width = cancelBtn.PreferredWidth; cancelBtn.Enabled = true;
            cancelBtn.Location = new Point(installBtn.Left - N.S(10) - cancelBtn.Width, cancelBtn.Top);
        }

        void Launch()
        {
            try
            {
                var psi = new ProcessStartInfo(Path.Combine(dir, "UndeleteGui.exe")) { UseShellExecute = true, WorkingDirectory = dir };
                if (admin.Checked) psi.Verb = "runas";
                Process.Start(psi);
            }
            catch (System.ComponentModel.Win32Exception) { }
            Close();
        }
    }

    static class SetupProgram
    {
        [STAThread]
        static int Main(string[] args)
        {
            var a = new HashSet<string>(args.Select(x => x.ToLowerInvariant().TrimStart('-', '/')));
            N.SetProcessDPIAware();
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) N.Scale = g.DpiX / 96f;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool uninstall = a.Contains("uninstall") ||
                             string.Equals(Path.GetFileName(Application.ExecutablePath), "uninstall.exe", StringComparison.OrdinalIgnoreCase);
            try
            {
                if (uninstall)
                {
                    if (a.Contains("silent")) { Installer.Uninstall(); return 0; }
                    if (DarkDialog.Show(null, "uninstall Neon Undelete", "Remove Neon Undelete, its shortcuts and its Installed-apps entry?", N.Orange, "uninstall", "cancel") != 0) return 1;
                    Installer.Uninstall();
                    DarkDialog.Show(null, "uninstalled", "Neon Undelete has been removed.", N.Teal);
                    return 0;
                }
                if (a.Contains("silent"))
                {
                    Installer.Install(Installer.InstalledDir() ?? Installer.DefaultDir, a.Contains("desktop"), a.Contains("admin"), a.Contains("path"), (f, m) => { });
                    return 0;
                }
                Application.Run(new SetupForm());
                return 0;
            }
            catch (Exception ex)
            {
                if (!a.Contains("silent")) DarkDialog.Show(null, "setup error", ex.Message, N.Crimson);
                return 2;
            }
        }
    }
}
