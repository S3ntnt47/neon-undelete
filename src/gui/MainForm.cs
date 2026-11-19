using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace Undelete.Gui
{
    sealed class MainForm : Form
    {
        // layout pieces
        readonly Logo logo = new Logo();
        readonly NeonButton elevateBtn = new NeonButton("restart as administrator", N.Orange, true);
        readonly Label adminChip = new Label();
        readonly NeonButton helpBtn = new NeonButton("? help", N.Magenta);

        readonly NeonPanel volPanel = new NeonPanel { Title = "volumes", Accent = N.Crimson };
        readonly DriveList drives = new DriveList();
        readonly NeonButton scanBtn = new NeonButton("scan", N.Crimson, true);
        readonly NeonButton imageBtn = new NeonButton("image…", N.Teal);
        readonly NeonButton refreshBtn = new NeonButton("⟳", N.Blue);

        readonly NeonPanel filterPanel = new NeonPanel { Title = "filters", Accent = N.Magenta };
        readonly NeonTextBox search = new NeonTextBox(N.Magenta) { Placeholder = "search name or folder…  (ctrl+f)" };
        readonly Segmented typeSeg = new Segmented(N.Blue, ItemQuery.TypeNames);
        readonly Segmented chanceSeg = new Segmented(N.Teal, ItemQuery.ChanceNames);
        readonly NeonButton emptyBtn = new NeonButton("hide empty files", N.Purple) { Checked = true };

        readonly NeonPanel sumPanel = new NeonPanel { Title = "scan", Accent = N.Purple };
        readonly SummaryView summary = new SummaryView();

        readonly NeonPanel listPanel = new NeonPanel { Title = "deleted items", Accent = N.Crimson };
        readonly ItemGrid grid = new ItemGrid();
        readonly NeonButton markAllBtn = new NeonButton("mark all", N.Lime);
        readonly NeonButton clearBtn = new NeonButton("clear", N.Dim);

        readonly NeonPanel detailPanel = new NeonPanel { Title = "details", Accent = N.Teal };
        readonly DetailView details = new DetailView();
        readonly PreviewView preview = new PreviewView();

        readonly NeonPanel actionPanel = new NeonPanel { Title = "recover", Accent = N.Orange };
        readonly NeonTextBox dest = new NeonTextBox(N.Orange) { Placeholder = "destination folder on a different drive" };
        readonly NeonButton browseBtn = new NeonButton("browse", N.Orange);
        readonly NeonButton recoverBtn = new NeonButton("recover", N.Crimson, true);
        readonly NeonMeter progress = new NeonMeter();
        readonly Label progressText = new Label();

        readonly StatusLine status = new StatusLine();
        readonly System.Windows.Forms.Timer ui = new System.Windows.Forms.Timer { Interval = 100 };
        readonly System.Windows.Forms.Timer previewDelay = new System.Windows.Forms.Timer { Interval = 180 };

        FsVolume vol;
        Thread scanThread, recoverThread;
        FsVolume scanning;
        Exception scanError;
        Stopwatch scanClock;
        Recovery recovery;
        RecoveryResult recoveryResult;
        Exception recoveryError;
        int previewGen;
        readonly bool admin = IsAdmin();

        public MainForm(string initialSource)
        {
            Text = "Undelete";
            BackColor = N.Bg;
            ForeColor = N.Text;
            Font = N.Mono(9.5f);
            DoubleBuffered = true;
            KeyPreview = true;
            MinimumSize = new Size(N.S(1100), N.S(700));
            Size = new Size(Math.Min(N.S(1500), Screen.PrimaryScreen.WorkingArea.Width - 40), Math.Min(N.S(920), Screen.PrimaryScreen.WorkingArea.Height - 40));
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            logo.Subtitle = "ntfs · fat32 · exfat recovery  ·  read-only  ·  never writes to the scanned drive  ·  F1 help";
            adminChip.AutoSize = false;
            adminChip.TextAlign = ContentAlignment.MiddleRight;
            adminChip.Font = N.Mono(9f);
            adminChip.BackColor = N.Bg;
            adminChip.ForeColor = admin ? N.Teal : N.Orange;
            adminChip.Text = admin ? "◉ administrator" : "▲ standard user · raw drive access needs admin";
            elevateBtn.Visible = !admin;

            volPanel.Controls.AddRange(new Control[] { drives, scanBtn, imageBtn, refreshBtn });
            filterPanel.Controls.AddRange(new Control[] { search, typeSeg, chanceSeg, emptyBtn });
            chanceSeg.Selected = 1;
            sumPanel.Controls.Add(summary);
            listPanel.Controls.AddRange(new Control[] { grid, markAllBtn, clearBtn });
            detailPanel.Controls.AddRange(new Control[] { details, preview });
            progressText.Font = N.Mono(8.5f); progressText.ForeColor = N.Dim; progressText.BackColor = N.Panel; progressText.AutoEllipsis = true;
            actionPanel.Controls.AddRange(new Control[] { dest, browseBtn, recoverBtn, progress, progressText });
            Controls.AddRange(new Control[] { logo, adminChip, elevateBtn, helpBtn, volPanel, filterPanel, sumPanel, listPanel, detailPanel, actionPanel, status });

            // events
            drives.Reload();
            drives.Activated += StartScan;
            scanBtn.Click += (s, e) => { if (scanThread != null) CancelScan(); else StartScan(); };
            imageBtn.Click += (s, e) => OpenImage();
            refreshBtn.Click += (s, e) => { drives.Reload(); LayoutAll(); };
            elevateBtn.Click += (s, e) => Elevate();
            helpBtn.Click += (s, e) => ShowHelp(0);
            search.Box.TextChanged += (s, e) => Refilter();
            typeSeg.Changed += Refilter;
            chanceSeg.Changed += Refilter;
            emptyBtn.Click += (s, e) => { emptyBtn.Checked = !emptyBtn.Checked; emptyBtn.Invalidate(); Refilter(); };
            grid.SortChanged += Refilter;
            grid.CurrentChanged += OnCurrent;
            grid.MarksChanged += UpdateStatus;
            grid.Activated += () => Recover(false);
            markAllBtn.Click += (s, e) => grid.MarkAll();
            clearBtn.Click += (s, e) => grid.ClearMarks();
            browseBtn.Click += (s, e) => Browse();
            recoverBtn.Click += (s, e) => { if (recoverThread != null) { if (vol != null) vol.Cancel = true; } else Recover(true); };
            ui.Tick += (s, e) => Pump();
            previewDelay.Tick += (s, e) => { previewDelay.Stop(); LoadPreview(); };
            ui.Start();

            Resize += (s, e) => LayoutAll();
            UpdateStatus();
            preview.Clear("");
            if (!string.IsNullOrEmpty(initialSource)) Shown += (s, e) => StartScan(initialSource);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            N.DarkTitleBar(this, N.Crimson);
        }

        protected override void OnLoad(EventArgs e) { base.OnLoad(e); LayoutAll(); }

        // ───────────────────────────── layout ─────────────────────────────

        void LayoutAll()
        {
            if (ClientSize.Width < 100) return;
            int W = ClientSize.Width, H = ClientSize.Height, gap = N.S(10), m = N.S(12);
            int headH = N.S(104), statusH = N.S(26), actionH = N.S(78), sideW = N.S(350);

            logo.SetBounds(m, N.S(6), Math.Min(N.S(720), W - N.S(400)), headH - N.S(6));
            adminChip.SetBounds(W - m - N.S(460), N.S(18), N.S(460), N.S(20));
            elevateBtn.Width = elevateBtn.PreferredWidth;
            elevateBtn.Location = new Point(W - m - elevateBtn.Width, N.S(44));
            helpBtn.Width = helpBtn.PreferredWidth;
            helpBtn.Location = new Point((elevateBtn.Visible ? elevateBtn.Left - N.S(10) : W - m) - helpBtn.Width, N.S(44));

            status.SetBounds(0, H - statusH, W, statusH);
            int bottom = H - statusH - gap;
            actionPanel.SetBounds(m, bottom - actionH, W - 2 * m, actionH);
            int bodyTop = headH, bodyBottom = actionPanel.Top - gap;

            // sidebar
            int volH = drives.PreferredHeight + N.S(78);
            volPanel.SetBounds(m, bodyTop, sideW, volH);
            var vi = volPanel.Inner;
            drives.SetBounds(vi.X, vi.Y + N.S(2), vi.Width, drives.PreferredHeight);
            int by = drives.Bottom + N.S(6);
            refreshBtn.Width = N.S(46);
            imageBtn.Width = imageBtn.PreferredWidth;
            scanBtn.SetBounds(vi.X, by, vi.Width - imageBtn.Width - refreshBtn.Width - N.S(16), N.S(30));
            imageBtn.Location = new Point(scanBtn.Right + N.S(8), by);
            refreshBtn.Location = new Point(imageBtn.Right + N.S(8), by);

            int filtH = N.S(186);
            filterPanel.SetBounds(m, volPanel.Bottom + gap, sideW, filtH);
            var fi = filterPanel.Inner;
            search.SetBounds(fi.X, fi.Y + N.S(4), fi.Width, N.S(30));
            typeSeg.SetBounds(fi.X, search.Bottom + N.S(10), fi.Width, N.S(28));
            chanceSeg.SetBounds(fi.X, typeSeg.Bottom + N.S(8), fi.Width, N.S(28));
            emptyBtn.SetBounds(fi.X, chanceSeg.Bottom + N.S(8), fi.Width, N.S(28));

            sumPanel.SetBounds(m, filterPanel.Bottom + gap, sideW, Math.Max(N.S(60), bodyBottom - filterPanel.Bottom - gap));
            var si = sumPanel.Inner;
            summary.SetBounds(si.X, si.Y + N.S(2), si.Width, si.Height - N.S(2));

            // main column
            int mx = m + sideW + gap, mw = W - mx - m;
            int detailH = N.S(196);
            listPanel.SetBounds(mx, bodyTop, mw, bodyBottom - bodyTop - detailH - gap);
            var li = listPanel.Inner;
            markAllBtn.Width = markAllBtn.PreferredWidth; clearBtn.Width = clearBtn.PreferredWidth;
            grid.SetBounds(li.X, li.Y, li.Width, li.Height - N.S(38));
            markAllBtn.Location = new Point(li.X, grid.Bottom + N.S(8));
            clearBtn.Location = new Point(markAllBtn.Right + N.S(8), grid.Bottom + N.S(8));

            detailPanel.SetBounds(mx, listPanel.Bottom + gap, mw, detailH);
            var di = detailPanel.Inner;
            int pw = N.S(250);
            preview.SetBounds(di.Right - pw, di.Y + N.S(2), pw, di.Height - N.S(2));
            details.SetBounds(di.X, di.Y + N.S(2), di.Width - pw - N.S(14), di.Height - N.S(2));

            var ai = actionPanel.Inner;
            browseBtn.Width = browseBtn.PreferredWidth;
            recoverBtn.Width = Math.Max(N.S(260), recoverBtn.PreferredWidth);
            int destW = Math.Max(N.S(240), (int)(ai.Width * 0.38));
            dest.SetBounds(ai.X, ai.Y + N.S(8), destW, N.S(32));
            browseBtn.SetBounds(dest.Right + N.S(8), ai.Y + N.S(9), browseBtn.Width, N.S(30));
            recoverBtn.SetBounds(ai.Right - recoverBtn.Width, ai.Y + N.S(9), recoverBtn.Width, N.S(30));
            int px = browseBtn.Right + N.S(20), pW = recoverBtn.Left - px - N.S(20);
            progress.SetBounds(px, ai.Y + N.S(6), Math.Max(N.S(40), pW), N.S(14));
            progressText.SetBounds(px, progress.Bottom + N.S(4), Math.Max(N.S(40), pW), N.S(18));
            Invalidate(true);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F1) { ShowHelp(0); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.F) { search.Box.Focus(); search.Box.SelectAll(); e.Handled = true; }
            else if (e.KeyCode == Keys.F5) { StartScan(); e.Handled = true; }
            else if (e.KeyCode == Keys.Escape && search.Box.Focused) { search.Text = ""; grid.Focus(); e.Handled = true; }
            base.OnKeyDown(e);
        }

        void ShowHelp(int topic)
        {
            using (var h = new HelpForm(topic)) h.ShowDialog(this);
        }

        // ───────────────────────────── scanning ─────────────────────────────

        static bool IsAdmin()
        {
            try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }

        void Elevate()
        {
            try
            {
                Process.Start(new ProcessStartInfo(Application.ExecutablePath) { UseShellExecute = true, Verb = "runas" });
                Close();
            }
            catch (Win32Exception) { }   // UAC declined
        }

        void OpenImage()
        {
            using (var d = new OpenFileDialog { Title = "Open a disk image", Filter = "Disk images (*.img;*.dd;*.raw;*.bin;*.vhd)|*.img;*.dd;*.raw;*.bin;*.vhd|All files|*.*" })
                if (d.ShowDialog(this) == DialogResult.OK) StartScan(d.FileName);
        }

        void StartScan() { var en = drives.SelectedEntry; if (en != null) StartScan(en.Path); }

        void StartScan(string src)
        {
            if (scanThread != null || recoverThread != null) return;
            if (src.Length == 2 && src[1] == ':')
            {
                var en = drives.Entries.FirstOrDefault(x => x.Path.Equals(src, StringComparison.OrdinalIgnoreCase));
                if (en != null && !en.Ok)
                {
                    DarkDialog.Show(this, "unsupported", src + " is " + en.Fs + ". Supported file systems: NTFS, FAT32/FAT16/FAT12 and exFAT.", N.Orange);
                    return;
                }
                if (!admin)
                {
                    int r = DarkDialog.Show(this, "administrator needed",
                        "Reading " + src + " directly needs administrator rights.\n\nRestart Undelete as administrator now?", N.Orange,
                        "restart as admin", "cancel");
                    if (r == 0) Elevate();
                    return;
                }
            }

            FsVolume v;
            try { v = FsVolume.Open(src); }
            catch (Exception ex) { DarkDialog.Show(this, "cannot open " + src, ex.Message, N.Crimson); return; }

            scanning = v;
            scanError = null;
            scanClock = Stopwatch.StartNew();
            summary.Vol = v; summary.Scanning = true; summary.ResetSpark();
            sumPanel.Title = "scanning " + Describe(v);
            scanBtn.Text = "cancel"; scanBtn.Accent = N.Orange;
            grid.EmptyText = "scanning…";
            grid.SetItems(new List<DeletedItem>(), null);
            scanThread = new Thread(() => { try { v.Scan(); } catch (Exception ex) { scanError = ex; } }) { IsBackground = true };
            scanThread.Start();
        }

        void CancelScan() { if (scanning != null) scanning.Cancel = true; }

        static string Describe(FsVolume v)
        {
            string s = v.Disk.DriveLetter != '\0' ? v.Disk.DriveLetter + ":" : Path.GetFileName(v.Disk.Source);
            return v.Label.Length > 0 ? s + " " + v.Label : s;
        }

        void ScanFinished()
        {
            var v = scanning;
            scanThread = null; scanning = null;
            scanBtn.Text = "scan"; scanBtn.Accent = N.Crimson;
            summary.Scanning = false;
            if (v.Cancel || scanError != null)
            {
                if (scanError != null) DarkDialog.Show(this, "scan failed", scanError.Message, N.Crimson);
                v.Dispose();
                summary.Vol = vol;
                sumPanel.Title = vol != null ? "scan · " + Describe(vol) : "scan";
                grid.EmptyText = "scan cancelled";
                Refilter();
                return;
            }
            if (vol != null) vol.Dispose();
            vol = v;
            grid.Marked.Clear();
            sumPanel.Title = "scan · " + Describe(v) + " · " + scanClock.Elapsed.TotalSeconds.ToString("0.0") + "s";
            summary.Counts = new[]
            {
                v.Items.Count(i => i.Chance == Chance.Excellent && !i.IsDir), v.Items.Count(i => i.Chance == Chance.Partial),
                v.Items.Count(i => i.Chance == Chance.Overwritten), v.Items.Count(i => i.Chance == Chance.Zeroed),
                v.Items.Count(i => i.Chance == Chance.Unsupported), v.Items.Count(i => i.IsDir)
            };
            summary.RecoverableBytes = v.Items.Where(i => i.Chance == Chance.Excellent && !i.IsDir).Sum(i => i.Size);
            grid.EmptyText = v.Items.Count == 0 ? "no deleted entries found in the MFT" : "nothing matches the filters";
            if (dest.Text.Length == 0) dest.Text = Recovery.DefaultDestination(v);
            Refilter();
            grid.Focus();
        }

        // ───────────────────────────── list ─────────────────────────────

        void Refilter()
        {
            if (vol == null) { UpdateStatus(); return; }
            string f = search.Text.Trim();
            int tm = typeSeg.Selected, cm = chanceSeg.Selected;
            bool he = emptyBtn.Checked;
            IEnumerable<DeletedItem> q = vol.Items.Where(i => ItemQuery.Matches(i, tm, cm, he, f));
            Func<DeletedItem, object> key;
            switch (grid.SortCol)
            {
                case 1: key = i => ItemQuery.ChanceRank(i); break;
                case 2: key = i => i.Name.ToLowerInvariant(); break;
                case 3: key = i => i.Size; break;
                case 5: key = i => i.FullPath.ToLowerInvariant(); break;
                default: key = i => i.Modified; break;
            }
            var list = (grid.SortAsc ? q.OrderBy(key) : q.OrderByDescending(key)).ToList();
            grid.SetItems(list, grid.CurrentItem);
            UpdateStatus();
        }

        void OnCurrent()
        {
            details.Item = grid.CurrentItem;
            preview.Clear(grid.CurrentItem == null ? "" : "loading…");
            previewDelay.Stop();
            previewDelay.Start();
        }

        void LoadPreview()
        {
            var it = grid.CurrentItem;
            var v = vol;
            if (it == null || v == null) { preview.Clear(""); return; }
            if (it.IsDir) { preview.Clear("folder"); return; }
            if (it.Chance == Chance.Unsupported) { preview.Clear("compressed / encrypted"); return; }
            if (it.Chance == Chance.Overwritten) { preview.Clear("overwritten"); return; }
            int gen = ++previewGen;
            string ext = Path.GetExtension(it.Name).ToLowerInvariant();
            bool img = new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff" }.Contains(ext);
            int max = img ? (int)Math.Min(it.Size, 24L << 20) : 64 * 1024;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                byte[] data = null;
                try { v.CheckZeros(it); data = v.ReadData(it, max); } catch { }
                BeginInvoke((Action)(() =>
                {
                    if (gen != previewGen || grid.CurrentItem != it) return;
                    preview.Show(it, data);
                    details.Invalidate();
                    grid.Invalidate();
                }));
            });
        }

        void UpdateStatus()
        {
            long mb = grid.Marked.Where(m => !m.IsDir).Sum(m => m.Size);
            int n = grid.Marked.Count;
            recoverBtn.Text = recoverThread != null ? "stop" : n > 0 ? "recover " + Fmt.N(n) + " marked · " + Fmt.Bytes(mb) : "recover selected";
            listPanel.Title = "deleted items" + (vol != null ? " · " + Fmt.N(grid.Items.Count) + " shown" : "");
            listPanel.RightTitle = vol != null ? Fmt.N(n) + " marked  ·  click ◇ / space / ctrl-click to mark  ·  double-click recovers" : "";
            if (scanThread != null)
                status.Set("SCANNING", N.Magenta, "read-only raw access to " + scanning.Disk.Source + (scanning.Disk.Via != null ? " via " + scanning.Disk.Via : ""));
            else if (recoverThread != null)
                status.Set("RECOVER", N.Crimson, "writing to " + dest.Text);
            else if (vol == null)
                status.Set("READY", N.Lime, "pick a volume and press scan  │  " + (admin ? "administrator" : "standard user") + "  │  read-only");
            else
                status.Set("BROWSE", N.Lime, Describe(vol) + "  │  " + Fmt.N(grid.Items.Count) + " / " + Fmt.N(vol.Items.Count) + " items  │  " +
                                             Fmt.N(n) + " marked (" + Fmt.Bytes(mb) + ")  │  read-only");
        }

        void Pump()
        {
            if (scanThread != null)
            {
                summary.Elapsed = scanClock.Elapsed;
                summary.Tick();
                if (!scanThread.IsAlive) ScanFinished();
                UpdateStatus();
            }
            if (recoverThread != null)
            {
                var r = recovery;
                progress.Value = r.TotalBytes > 0 ? (double)r.DoneBytes / r.TotalBytes : (double)r.DoneJobs / Math.Max(1, r.TotalJobs);
                progress.Label = (int)(progress.Value * 100) + "%";
                progressText.Text = Fmt.N(r.DoneJobs) + " / " + Fmt.N(r.TotalJobs) + " · " + Fmt.Bytes(r.DoneBytes) + " / " + Fmt.Bytes(r.TotalBytes) + " · " + r.Current;
                if (!recoverThread.IsAlive) RecoverFinished();
            }
        }

        // ───────────────────────────── recovery ─────────────────────────────

        void Browse()
        {
            using (var d = new FolderBrowserDialog { Description = "Choose where recovered files go (must be a different drive)", ShowNewFolderButton = true })
            {
                if (Directory.Exists(dest.Text)) d.SelectedPath = dest.Text;
                if (d.ShowDialog(this) == DialogResult.OK) dest.Text = d.SelectedPath;
            }
        }

        void Recover(bool useMarks)
        {
            if (vol == null || recoverThread != null || scanThread != null) return;
            List<DeletedItem> sel = useMarks && grid.Marked.Count > 0 ? grid.Marked.ToList()
                                  : grid.CurrentItem != null ? new List<DeletedItem> { grid.CurrentItem } : null;
            if (sel == null) return;

            if (dest.Text.Trim().Length == 0) Browse();
            string d = dest.Text.Trim().Trim('"');
            string bad = Recovery.CheckDestination(vol, d);
            if (bad != null) { DarkDialog.Show(this, "unsafe destination", bad, N.Orange); return; }

            int risky = sel.Count(i => i.Chance == Chance.Overwritten || i.Chance == Chance.Zeroed);
            if (risky == sel.Count && sel.Count > 0 &&
                DarkDialog.Show(this, "probably unrecoverable",
                    (sel.Count == 1 ? "This file's" : "These files'") + " space has been reused or reads as zeros, so the output will most likely be garbage.\n\nRecover anyway?",
                    N.Orange, "recover anyway", "cancel") != 0)
                return;

            recovery = new Recovery(vol);
            List<RecoveryJob> jobs;
            try { jobs = recovery.Plan(sel, d); }
            catch (Exception ex) { DarkDialog.Show(this, "cannot plan recovery", ex.Message, N.Crimson); return; }

            vol.Cancel = false;
            recoveryResult = null; recoveryError = null;
            recoverBtn.Accent = N.Orange;
            var rec = recovery;
            recoverThread = new Thread(() => { try { recoveryResult = rec.Run(jobs); } catch (Exception ex) { recoveryError = ex; } }) { IsBackground = true };
            recoverThread.Start();
            UpdateStatus();
        }

        void RecoverFinished()
        {
            recoverThread = null;
            bool stopped = vol.Cancel;
            vol.Cancel = false;
            recoverBtn.Accent = N.Crimson;
            progress.Value = 1;
            UpdateStatus();
            if (recoveryError != null) { DarkDialog.Show(this, "recovery failed", recoveryError.Message, N.Crimson); return; }
            var r = recoveryResult;
            progressText.Text = "done · " + Fmt.N(r.Files) + " files · " + Fmt.Bytes(r.Bytes) + " → " + r.OutRoot;

            string msg = (stopped ? "Stopped early.\n" : "") +
                         "Recovered " + Fmt.N(r.Files) + " file" + (r.Files == 1 ? "" : "s") + " (" + Fmt.Bytes(r.Bytes) + ") and " +
                         Fmt.N(r.Folders) + " folder" + (r.Folders == 1 ? "" : "s") + "." +
                         (r.Skipped > 0 ? "\nSkipped " + r.Skipped + " unsupported file(s)." : "") +
                         (r.Failed > 0 ? "\nFailed: " + r.Failed + "." : "") +
                         "\n\nSaved to:\n" + r.OutRoot +
                         (r.Errors.Count > 0 ? "\n\n" + string.Join("\n", r.Errors.Take(5).Select(x => "! " + x)) + (r.Errors.Count > 5 ? "\n… and " + (r.Errors.Count - 5) + " more" : "") : "") +
                         "\n\nRecovered files can still be damaged if their space was reused - open them to check.";
            int choice = DarkDialog.Show(this, "recovery complete", msg, r.Failed > 0 ? N.Orange : N.Teal, "open folder", "close");
            if (choice == 0 && r.OutRoot != null && Directory.Exists(r.OutRoot))
                try { Process.Start("explorer.exe", "\"" + r.OutRoot + "\""); } catch { }
            if (r.Failed == 0 && !stopped) grid.ClearMarks();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (scanning != null) scanning.Cancel = true;
            if (vol != null) vol.Cancel = true;
            base.OnFormClosing(e);
        }
    }
}
