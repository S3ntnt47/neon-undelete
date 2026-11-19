using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;

namespace Undelete
{
    /// <summary>The interactive neon terminal UI.</summary>
    sealed class App
    {
        static readonly string Version = AppInfo.Version;

        static readonly string[] Logo =
        {
            "██╗   ██╗███╗   ██╗██████╗ ███████╗██╗     ███████╗████████╗███████╗",
            "██║   ██║████╗  ██║██╔══██╗██╔════╝██║     ██╔════╝╚══██╔══╝██╔════╝",
            "██║   ██║██╔██╗ ██║██║  ██║█████╗  ██║     █████╗     ██║   █████╗  ",
            "██║   ██║██║╚██╗██║██║  ██║██╔══╝  ██║     ██╔══╝     ██║   ██╔══╝  ",
            "╚██████╔╝██║ ╚████║██████╔╝███████╗███████╗███████╗   ██║   ███████╗",
            " ╚═════╝ ╚═╝  ╚═══╝╚═════╝ ╚══════╝╚══════╝╚══════╝   ╚═╝   ╚══════╝",
        };

        readonly Screen S = new Screen();
        readonly Stopwatch clock = Stopwatch.StartNew();
        FsVolume vol;

        // browser state
        List<DeletedItem> view = new List<DeletedItem>();
        readonly HashSet<DeletedItem> marked = new HashSet<DeletedItem>();
        int cursor, top;
        string filter = "";
        bool filterMode;
        int typeMode;          // 0 all, 1 files, 2 folders
        int chanceMode = 1;    // 0 all, 1 recoverable, 2 excellent only
        int sortMode;          // 0 modified, 1 name, 2 size, 3 chance, 4 folder
        bool sortAsc;
        bool hideEmpty = true;
        string lastDest;

        static readonly string[] TypeNames = { "all", "files", "folders" };
        static readonly string[] ChanceNames = { "any", "recoverable", "excellent" };
        static readonly string[] SortNames = { "modified", "name", "size", "chance", "folder" };

        public static bool IsAdmin()
        {
            try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }

        public void Run(string initialSource)
        {
            Screen.EnableVt();
            Console.TreatControlCAsInput = true;
            S.Raw("\x1b[?1049h\x1b[?25l");
            try
            {
                string src = initialSource;
                while (true)
                {
                    if (src == null) src = PickSource();
                    if (src == null) return;
                    if (ScanScreen(src))
                    {
                        if (!Browse()) return;
                    }
                    src = null;
                }
            }
            finally
            {
                S.Raw("\x1b[0m\x1b[2J\x1b[?25h\x1b[?1049l");
                if (vol != null) vol.Dispose();
            }
        }

        // ───────────────────────────── shared drawing ─────────────────────────────

        ConsoleKeyInfo? WaitKey(int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (Console.KeyAvailable) return Console.ReadKey(true);
                if (S.Resize()) return null;
                Thread.Sleep(15);
            }
            return null;
        }

        static bool IsQuit(ConsoleKeyInfo k)
        {
            return k.Key == ConsoleKey.Q || (k.Key == ConsoleKey.C && (k.Modifiers & ConsoleModifiers.Control) != 0);
        }

        int DrawLogo(int y)
        {
            double phase = (clock.ElapsedMilliseconds % 6000) / 6000.0;
            if (S.W >= Logo[0].Length + 4)
            {
                int x = (S.W - Logo[0].Length) / 2;
                for (int i = 0; i < Logo.Length; i++) S.PutGrad(x, y + i, Logo[i], T.Heat, phase + i * 0.02);
                return y + Logo.Length;
            }
            string s = "▓▒░ U N D E L E T E ░▒▓";
            S.PutGrad((S.W - s.Length) / 2, y, s, T.Heat, phase);
            return y + 1;
        }

        void StatusBar(string mode, int modeCol, string middle, string right = null)
        {
            int y = S.H - 1;
            S.Fill(0, y, S.W, 1, T.Panel);
            string badge = " " + mode + " ";
            S.Put(0, y, badge, T.Dark, modeCol);
            S.Put(badge.Length, y, "▌", modeCol, T.Panel);
            if (right == null) right = DateTime.Now.ToString("HH:mm:ss");
            string r = " " + right + " ";
            S.Put(badge.Length + 2, y, Screen.Clip(middle, S.W - badge.Length - r.Length - 4), T.Text, T.Panel);
            S.Put(S.W - r.Length - 2, y, "▐", T.Purple, T.Panel);
            S.Put(S.W - r.Length - 1, y, r, T.Dark, T.Purple);
        }

        void MessageBox(string title, string text, int col, Action background = null)
        {
            var lines = Wrap(text, Math.Min(S.W - 10, 72));
            while (true)
            {
                if (background != null) background(); else S.Clear();
                int w = Math.Min(S.W - 6, Math.Max(40, lines.Max(l => l.Length) + 6));
                int h = lines.Count + 5;
                int x = (S.W - w) / 2, y = Math.Max(1, (S.H - h) / 2);
                S.Box(x, y, w, h, col, title, T.Panel);
                for (int i = 0; i < lines.Count; i++) S.Put(x + 3, y + 2 + i, lines[i], T.Text, T.Panel);
                S.Put(x + w - 10, y + h - 2, "‹ OK ›", T.Dark, col);
                S.Flush();
                var k = WaitKey(500);
                if (k.HasValue) return;
            }
        }

        static List<string> Wrap(string text, int w)
        {
            var res = new List<string>();
            foreach (var para in text.Split('\n'))
            {
                string line = "";
                foreach (var word in para.Split(' '))
                {
                    if (line.Length + word.Length + 1 > w && line.Length > 0) { res.Add(line); line = ""; }
                    line = line.Length == 0 ? word : line + " " + word;
                    while (line.Length > w) { res.Add(line.Substring(0, w)); line = line.Substring(w); }
                }
                res.Add(line);
            }
            return res;
        }

        /// <summary>Single-line text input in a centred dialog. Returns null on Esc.</summary>
        string Prompt(string title, string label, string initial, int col, Action background)
        {
            string val = initial ?? "";
            while (true)
            {
                if (background != null) background(); else S.Clear();
                int w = Math.Min(S.W - 6, 84), h = 7;
                int x = (S.W - w) / 2, y = Math.Max(1, (S.H - h) / 2);
                S.Box(x, y, w, h, col, title, T.Panel);
                S.Put(x + 3, y + 2, Screen.Clip(label, w - 6), T.Dim, T.Panel);
                S.Fill(x + 3, y + 3, w - 6, 1, T.PanelAlt);
                string shown = Screen.ClipLeft(val, w - 10);
                S.Put(x + 3, y + 3, "> ", col, T.PanelAlt);
                S.Put(x + 5, y + 3, shown, T.Text, T.PanelAlt);
                if ((clock.ElapsedMilliseconds / 500) % 2 == 0) S.Put(x + 5 + shown.Length, y + 3, "█", T.Lime, T.PanelAlt);
                S.Put(x + 3, y + 5, "enter", T.Dark, col);
                S.Put(x + 9, y + 5, "confirm   ", T.Dim, T.Panel);
                S.Put(x + 19, y + 5, "esc", T.Dark, T.Dim);
                S.Put(x + 23, y + 5, "cancel", T.Dim, T.Panel);
                S.Flush();

                var kk = WaitKey(250);
                if (!kk.HasValue) continue;
                var k = kk.Value;
                if (k.Key == ConsoleKey.Escape) return null;
                if (k.Key == ConsoleKey.Enter) return val.Trim().Trim('"');
                if (k.Key == ConsoleKey.Backspace)
                {
                    if ((k.Modifiers & ConsoleModifiers.Control) != 0) val = "";
                    else if (val.Length > 0) val = val.Substring(0, val.Length - 1);
                }
                else if (k.KeyChar >= ' ') val += k.KeyChar;
            }
        }

        // ───────────────────────────── help ─────────────────────────────

        struct HelpLine { public string Text, Key; public int Col; public int Indent; }

        static List<HelpLine> LayoutHelp(HelpTopic t, int w)
        {
            var lines = new List<HelpLine>();
            int[] accents = { T.Crimson, T.Teal, T.Magenta, T.Purple, T.Blue };
            int h = 0;
            foreach (var b in t.Blocks)
            {
                switch (b.Kind)
                {
                    case 'h':
                        lines.Add(new HelpLine { Text = "" });
                        string head = "── " + b.A + " ";
                        lines.Add(new HelpLine { Text = head + new string('─', Math.Max(0, w - head.Length)), Col = accents[h++ % accents.Length] });
                        break;
                    case 'p':
                        foreach (var l in Wrap(b.A, w)) lines.Add(new HelpLine { Text = l, Col = T.Text });
                        lines.Add(new HelpLine { Text = "" });
                        break;
                    case 'l':
                        {
                            var wr = Wrap(b.A, w - 3);
                            for (int i = 0; i < wr.Count; i++) lines.Add(new HelpLine { Text = (i == 0 ? " ▸ " : "   ") + wr[i], Col = T.Text });
                            break;
                        }
                    case 'w':
                        {
                            var wr = Wrap(b.A, w - 3);
                            for (int i = 0; i < wr.Count; i++) lines.Add(new HelpLine { Text = (i == 0 ? " ▲ " : "   ") + wr[i], Col = T.Orange });
                            lines.Add(new HelpLine { Text = "" });
                            break;
                        }
                    case 'k':
                        {
                            int kw = Math.Min(26, Math.Max(12, b.A.Length + 3));
                            var wr = Wrap(b.B, Math.Max(10, w - kw - 1));
                            for (int i = 0; i < wr.Count; i++)
                                lines.Add(new HelpLine { Key = i == 0 ? b.A : null, Indent = kw + 1, Text = wr[i], Col = T.Text });
                            break;
                        }
                }
            }
            return lines;
        }

        void HelpScreen()
        {
            var topics = HelpContent.Topics(false);
            int sel = 0, scroll = 0;
            while (true)
            {
                S.Clear();
                S.PutGrad(1, 0, "▓▒░ UNDELETE", T.Heat);
                S.Put(14, 0, "  ▸ help", T.Dim);
                int navW = Math.Min(28, S.W / 4), top = 1, h = S.H - 3;
                S.Box(0, top, navW, h, T.Magenta, "topics", T.Panel);
                for (int i = 0; i < topics.Count && i < h - 2; i++)
                {
                    bool on = i == sel;
                    if (on) S.Fill(1, top + 1 + i, navW - 2, 1, T.Lime);
                    S.Put(2, top + 1 + i, Screen.Clip((on ? "▸ " : "  ") + topics[i].Title, navW - 4), on ? T.Dark : T.Text, on ? T.Lime : T.Panel);
                }

                int bx = navW + 1, bw = S.W - bx;
                S.Box(bx, top, bw, h, T.Teal, topics[sel].Title, T.Panel);
                int tw = bw - 6, rows = h - 2;
                var lines = LayoutHelp(topics[sel], tw);
                scroll = Math.Max(0, Math.Min(scroll, Math.Max(0, lines.Count - rows)));
                for (int r = 0; r < rows && scroll + r < lines.Count; r++)
                {
                    var l = lines[scroll + r];
                    int y = top + 1 + r, x = bx + 3;
                    if (l.Key != null) S.Put(x, y, Screen.PadR(" " + l.Key + " ", l.Indent - 1), T.Dark, T.Teal);
                    S.Put(x + l.Indent, y, Screen.Clip(l.Text, tw - l.Indent), l.Col, T.Panel);
                }
                if (lines.Count > rows)
                {
                    int th = Math.Max(1, rows * rows / lines.Count);
                    int ty = (int)((long)(rows - th) * scroll / Math.Max(1, lines.Count - rows));
                    for (int r = 0; r < rows; r++) S.Put(S.W - 1, top + 1 + r, r >= ty && r < ty + th ? "█" : "│", r >= ty && r < ty + th ? T.Magenta : T.Teal, T.Panel);
                }

                int kx = 1;
                kx = S.Key(kx, S.H - 2, "←→ tab", "topic", T.Magenta);
                kx = S.Key(kx, S.H - 2, "↑↓ pgup pgdn", "scroll", T.Teal);
                S.Key(kx, S.H - 2, "esc", "back", T.Orange);
                StatusBar("HELP", T.Magenta, "neon undelete " + AppInfo.Version + "  │  " + topics[sel].Title);
                S.Flush();

                var kk = WaitKey(1000);
                if (!kk.HasValue) continue;
                var k = kk.Value;
                switch (k.Key)
                {
                    case ConsoleKey.Escape: case ConsoleKey.F1: case ConsoleKey.Q: return;
                    case ConsoleKey.LeftArrow: sel = (sel + topics.Count - 1) % topics.Count; scroll = 0; break;
                    case ConsoleKey.RightArrow: case ConsoleKey.Tab: sel = (sel + 1) % topics.Count; scroll = 0; break;
                    case ConsoleKey.UpArrow: scroll--; break;
                    case ConsoleKey.DownArrow: scroll++; break;
                    case ConsoleKey.PageUp: scroll -= rows - 1; break;
                    case ConsoleKey.PageDown: scroll += rows - 1; break;
                    case ConsoleKey.Home: scroll = 0; break;
                    case ConsoleKey.End: scroll = int.MaxValue / 2; break;
                }
                if (k.KeyChar == '?') return;
            }
        }

        static bool IsHelpKey(ConsoleKeyInfo k) { return k.Key == ConsoleKey.F1 || k.KeyChar == '?'; }

        // ───────────────────────────── 1. pick a drive ─────────────────────────────

        sealed class Source
        {
            public string Path, Label, Fs, Kind;
            public long Size, Free;
            public bool Ok;
        }

        static List<Source> ListSources()
        {
            var list = new List<Source>();
            foreach (var d in DriveInfo.GetDrives())
            {
                var s = new Source { Path = d.Name.Substring(0, 2), Kind = d.DriveType.ToString().ToLowerInvariant() };
                try
                {
                    if (d.IsReady)
                    {
                        s.Label = d.VolumeLabel; s.Fs = d.DriveFormat; s.Size = d.TotalSize; s.Free = d.TotalFreeSpace;
                        s.Ok = FsVolume.IsSupported(s.Fs);
                    }
                    else s.Fs = "not ready";
                }
                catch { s.Fs = "?"; }
                if (d.DriveType == DriveType.CDRom && !d.IsReady) continue;
                list.Add(s);
            }
            list.Add(new Source { Path = null, Label = "open a disk image file (.img / .dd / .raw / .vhd)…", Ok = true });
            return list;
        }

        string PickSource()
        {
            var sources = ListSources();
            int sel = Math.Max(0, sources.FindIndex(s => s.Ok));
            bool admin = IsAdmin();
            while (true)
            {
                S.Clear();
                int y = DrawLogo(1);
                string sub = "ntfs · fat32 · exfat deleted-file recovery  ·  read-only scanner  ·  v" + Version;
                S.Put((S.W - sub.Length) / 2, y + 1, sub, T.Dim);

                int w = Math.Min(S.W - 4, 96), x = (S.W - w) / 2;
                int by = y + 3;
                int h = sources.Count + 4;
                S.Box(x, by, w, h, T.Crimson, "volumes", T.Panel);
                S.Put(x + 3, by + 1, Screen.PadR("drive", 7) + Screen.PadR("label", 20) + Screen.PadR("fs", 7) + Screen.PadL("size", 10) + "   used", T.Orange, T.Panel);
                for (int i = 0; i < sources.Count; i++)
                {
                    var s = sources[i];
                    int ry = by + 2 + i;
                    bool on = i == sel;
                    int bgc = on ? T.Lime : T.Panel;
                    int fgc = on ? T.Dark : (s.Ok ? T.Text : T.Dim);
                    S.Fill(x + 1, ry, w - 2, 1, bgc);
                    if (s.Path == null)
                    {
                        S.Put(x + 3, ry, "[ " + s.Label + " ]", on ? T.Dark : T.Teal, bgc);
                        continue;
                    }
                    string row = Screen.PadR(s.Path, 7) + Screen.PadR(string.IsNullOrEmpty(s.Label) ? "(" + s.Kind + ")" : s.Label, 20) +
                                 Screen.PadR(s.Fs ?? "", 7) + Screen.PadL(s.Size > 0 ? Fmt.Bytes(s.Size) : "", 10) + "   ";
                    S.Put(x + 3, ry, row, fgc, bgc);
                    int mx = x + 3 + row.Length, mw = Math.Max(4, Math.Min(20, w - row.Length - 20));
                    if (s.Size > 0)
                    {
                        double used = 1 - (double)s.Free / s.Size;
                        if (on) S.Put(mx, ry, Bar(used, mw), T.Dark, bgc);
                        else S.Meter(mx, ry, mw, used, T.Aurora);
                        S.Put(mx + mw + 1, ry, Screen.PadL(((int)(used * 100)) + "%", 4), fgc, bgc);
                    }
                    if (!s.Ok) S.Put(x + w - 15, ry, "unsupported", on ? T.Dark : T.Dim, bgc);
                }

                int ny = by + h + 1;
                if (!admin)
                {
                    S.Put(x + 1, ny, "▲ not running as administrator - raw drive access will be refused.", T.Orange);
                    S.Put(x + 1, ny + 1, "  press ", T.Dim);
                    S.Put(x + 9, ny + 1, " E ", T.Dark, T.Orange);
                    S.Put(x + 12, ny + 1, " to relaunch elevated (disk images work without it).", T.Dim);
                    ny += 3;
                }
                S.Put(x + 1, ny, "tip: stop using the drive you want to recover from. every write can overwrite deleted data.", T.Dim);

                int kx = 1;
                kx = S.Key(kx, S.H - 2, "↑↓", "choose", T.Teal);
                kx = S.Key(kx, S.H - 2, "enter", "scan", T.Crimson);
                if (!admin) kx = S.Key(kx, S.H - 2, "E", "elevate", T.Orange);
                kx = S.Key(kx, S.H - 2, "F5", "refresh", T.Blue);
                kx = S.Key(kx, S.H - 2, "F1", "help", T.Purple);
                S.Key(kx, S.H - 2, "Q", "quit", T.Magenta);
                StatusBar("SELECT", T.Crimson, "undelete v" + Version + "  │  " + (admin ? "administrator" : "standard user") + "  │  nothing is ever written to the scanned drive");
                S.Flush();

                var kk = WaitKey(80);   // short timeout keeps the logo shimmering
                if (!kk.HasValue) continue;
                var k = kk.Value;
                if (IsQuit(k) || k.Key == ConsoleKey.Escape) return null;
                if (IsHelpKey(k)) { HelpScreen(); continue; }
                if (k.Key == ConsoleKey.UpArrow) sel = (sel + sources.Count - 1) % sources.Count;
                else if (k.Key == ConsoleKey.DownArrow) sel = (sel + 1) % sources.Count;
                else if (k.Key == ConsoleKey.F5) { sources = ListSources(); sel = Math.Min(sel, sources.Count - 1); }
                else if (k.Key == ConsoleKey.E && !admin) { if (Elevate()) return null; }
                else if (k.Key == ConsoleKey.Enter)
                {
                    var s = sources[sel];
                    if (s.Path == null)
                    {
                        string p = Prompt("open disk image", "path to a disk image (.img .dd .raw .vhd):", "", T.Teal, null);
                        if (!string.IsNullOrEmpty(p)) return p;
                    }
                    else if (!s.Ok)
                        MessageBox("unsupported", s.Path + " is " + (s.Fs ?? "unknown") + ". Supported file systems: NTFS, FAT32/FAT16/FAT12 and exFAT.", T.Orange);
                    else return s.Path;
                }
            }
        }

        static string Bar(double f, int w)
        {
            int n = (int)Math.Round(f * w);
            return new string('█', Math.Max(0, Math.Min(w, n))) + new string('░', Math.Max(0, w - n));
        }

        bool Elevate()
        {
            try
            {
                var exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
                return true;
            }
            catch (Win32Exception) { return false; }   // user declined UAC
        }

        // ───────────────────────────── 2. scan ─────────────────────────────

        bool ScanScreen(string src)
        {
            FsVolume v;
            try { v = FsVolume.Open(src); }
            catch (Exception e) { MessageBox("cannot open " + src, e.Message, T.Crimson); return false; }

            Exception err = null;
            var th = new Thread(() => { try { v.Scan(); } catch (Exception e) { err = e; } });
            th.IsBackground = true;
            th.Start();
            var sw = Stopwatch.StartNew();
            var spark = new List<double>();
            long lastRec = 0; long lastT = 0;

            while (th.IsAlive)
            {
                long t = sw.ElapsedMilliseconds;
                if (t - lastT >= 250)
                {
                    long r = v.RecordsScanned;
                    spark.Add((r - lastRec) * 1000.0 / Math.Max(1, t - lastT));
                    lastRec = r; lastT = t;
                    if (spark.Count > 200) spark.RemoveAt(0);
                }
                DrawScan(v, sw.Elapsed, spark);
                var k = WaitKey(60);
                if (k.HasValue && (k.Value.Key == ConsoleKey.Escape || IsQuit(k.Value))) { v.Cancel = true; th.Join(); }
            }
            if (v.Cancel) { v.Dispose(); return false; }
            if (err != null) { v.Dispose(); MessageBox("scan failed", err.Message, T.Crimson); return false; }

            if (vol != null) vol.Dispose();
            vol = v;
            marked.Clear(); cursor = 0; top = 0; filter = "";
            Refresh();
            return true;
        }

        void DrawScan(FsVolume v, TimeSpan elapsed, List<double> spark)
        {
            S.Clear();
            int y = DrawLogo(1) + 2;
            int w = Math.Min(S.W - 4, 90), x = (S.W - w) / 2;
            string name = (v.Disk.DriveLetter != '\0' ? v.Disk.DriveLetter + ":" : Path.GetFileName(v.Disk.Source)) +
                          (v.Label.Length > 0 ? "  " + v.Label : "");
            S.Box(x, y, w, 12, T.Magenta, "scanning " + name, T.Panel);
            double frac = v.RecordsTotal > 0 ? (double)v.RecordsScanned / v.RecordsTotal : 0;
            if (v.Phase.StartsWith("checking") || v.Phase.StartsWith("loading") || v.Phase.StartsWith("rebuilding") || v.Phase == "done") frac = 1;

            S.Put(x + 3, y + 2, "phase", T.Dim, T.Panel);
            S.Put(x + 13, y + 2, v.Phase + new string('.', (int)(elapsed.TotalMilliseconds / 400) % 4), T.Text, T.Panel);
            S.Meter(x + 3, y + 4, w - 13, frac, T.Heat);
            S.Put(x + w - 9, y + 4, Screen.PadL(((int)(frac * 100)) + "%", 5), T.Lime, T.Panel);

            S.Put(x + 3, y + 6, v.ProgressUnit.Split(' ').Last(), T.Dim, T.Panel);
            S.Put(x + 13, y + 6, Fmt.N(v.RecordsScanned) + " / " + Fmt.N(v.RecordsTotal), T.Text, T.Panel);
            S.Put(x + 3, y + 7, "deleted", T.Dim, T.Panel);
            S.Put(x + 13, y + 7, Fmt.N(v.FoundCount) + " entries found", T.Crimson, T.Panel);
            S.Put(x + 3, y + 8, "volume", T.Dim, T.Panel);
            S.Put(x + 13, y + 8, Fmt.Bytes(v.TotalClusters * v.ClusterSize) + "  ·  " + v.FsName + "  ·  " +
                                  Fmt.Bytes(v.ClusterSize) + " clusters", T.Text, T.Panel);
            S.Put(x + 3, y + 9, "elapsed", T.Dim, T.Panel);
            S.Put(x + 13, y + 9, elapsed.ToString(@"mm\:ss"), T.Text, T.Panel);

            // throughput sparkline
            const string bars = "▁▂▃▄▅▆▇█";
            int sw = w - 6;
            var tail = spark.Skip(Math.Max(0, spark.Count - sw)).ToList();
            double max = tail.Count > 0 ? Math.Max(1, tail.Max()) : 1;
            for (int i = 0; i < tail.Count; i++)
            {
                int lvl = (int)Math.Min(7, tail[i] / max * 7.99);
                S.Put(x + 3 + i, y + 10, bars[lvl].ToString(), T.Lerp(T.Purple, T.Magenta, (double)i / Math.Max(1, sw)), T.Panel);
            }

            S.Key(1, S.H - 2, "esc", "cancel scan", T.Crimson);
            StatusBar("SCAN", T.Magenta, "read-only raw access to " + v.Disk.Source + "  │  " +
                      (v.RecordsScanned > 0 && elapsed.TotalSeconds > 0 ? Fmt.N((long)(v.RecordsScanned / elapsed.TotalSeconds)) + " " + v.ProgressUnit + "/s" : ""));
            S.Flush();
        }

        // ───────────────────────────── 3. browse ─────────────────────────────

        void Refresh()
        {
            IEnumerable<DeletedItem> q = vol.Items;
            if (typeMode == 1) q = q.Where(i => !i.IsDir);
            else if (typeMode == 2) q = q.Where(i => i.IsDir);
            if (hideEmpty) q = q.Where(i => i.IsDir || i.Size > 0);
            if (chanceMode == 1) q = q.Where(i => i.Chance == Chance.Excellent || i.Chance == Chance.Partial || i.Chance == Chance.Folder || i.Chance == Chance.Unknown);
            else if (chanceMode == 2) q = q.Where(i => i.Chance == Chance.Excellent || i.Chance == Chance.Folder);
            if (filter.Length > 0)
            {
                string f = filter;
                q = q.Where(i => i.FullPath.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            Func<DeletedItem, object> key;
            switch (sortMode)
            {
                case 1: key = i => i.Name.ToLowerInvariant(); break;
                case 2: key = i => i.Size; break;
                case 3: key = i => ChanceRank(i); break;
                case 4: key = i => i.FullPath.ToLowerInvariant(); break;
                default: key = i => i.Modified; break;
            }
            bool asc = sortMode == 1 || sortMode == 4 ? !sortAsc : sortAsc;   // text sorts default A→Z
            view = (asc ? q.OrderBy(key) : q.OrderByDescending(key)).ToList();
            cursor = Math.Max(0, Math.Min(cursor, view.Count - 1));
        }

        static int ChanceRank(DeletedItem i)
        {
            switch (i.Chance)
            {
                case Chance.Excellent: return 6;
                case Chance.Folder: return 5;
                case Chance.Partial: return 4;
                case Chance.Unknown: return 3;
                case Chance.Zeroed: return 2;
                case Chance.Unsupported: return 1;
                default: return 0;
            }
        }

        bool Browse()
        {
            while (true)
            {
                DrawBrowse();
                var kk = WaitKey(1000);
                if (!kk.HasValue) continue;
                var k = kk.Value;
                int page = Math.Max(1, ListRows() - 1);

                if (filterMode)
                {
                    if (k.Key == ConsoleKey.Enter || k.Key == ConsoleKey.Escape || k.Key == ConsoleKey.DownArrow || k.Key == ConsoleKey.UpArrow)
                    {
                        filterMode = false;
                        if (k.Key == ConsoleKey.Escape) { filter = ""; Refresh(); }
                    }
                    else if (k.Key == ConsoleKey.Backspace) { if (filter.Length > 0) { filter = filter.Substring(0, filter.Length - 1); Refresh(); } }
                    else if (k.KeyChar >= ' ') { filter += k.KeyChar; cursor = 0; Refresh(); }
                    continue;
                }

                if (IsQuit(k)) return false;
                if (IsHelpKey(k)) { HelpScreen(); continue; }
                switch (k.Key)
                {
                    case ConsoleKey.Escape: return true;
                    case ConsoleKey.UpArrow: cursor--; break;
                    case ConsoleKey.DownArrow: cursor++; break;
                    case ConsoleKey.PageUp: cursor -= page; break;
                    case ConsoleKey.PageDown: cursor += page; break;
                    case ConsoleKey.Home: cursor = 0; break;
                    case ConsoleKey.End: cursor = view.Count - 1; break;
                    case ConsoleKey.Spacebar:
                        if (view.Count > 0) { var it = view[cursor]; if (!marked.Remove(it)) marked.Add(it); cursor++; }
                        break;
                    case ConsoleKey.A:
                        if (view.Count > 0 && view.All(marked.Contains)) foreach (var i in view) marked.Remove(i);
                        else foreach (var i in view) marked.Add(i);
                        break;
                    case ConsoleKey.X: marked.Clear(); break;
                    case ConsoleKey.Oem2: case ConsoleKey.Divide: case ConsoleKey.F: filterMode = true; break;
                    case ConsoleKey.T: typeMode = (typeMode + 1) % 3; Refresh(); break;
                    case ConsoleKey.C: chanceMode = (chanceMode + 1) % 3; Refresh(); break;
                    case ConsoleKey.S: sortMode = (sortMode + 1) % SortNames.Length; sortAsc = false; Refresh(); break;
                    case ConsoleKey.I: sortAsc = !sortAsc; Refresh(); break;
                    case ConsoleKey.Z: hideEmpty = !hideEmpty; Refresh(); break;
                    case ConsoleKey.R: case ConsoleKey.Enter: DoRecover(); break;
                }
                if (k.KeyChar == '/') filterMode = true;
                cursor = Math.Max(0, Math.Min(cursor, view.Count - 1));
            }
        }

        int ListRows() { return Math.Max(3, S.H - 13); }

        void DrawBrowse()
        {
            S.Clear();
            int W = S.W, H = S.H;

            // header
            S.PutGrad(1, 0, "▓▒░ UNDELETE", T.Heat);
            string src = vol.Disk.DriveLetter != '\0' ? vol.Disk.DriveLetter + ":" : Path.GetFileName(vol.Disk.Source);
            S.Put(14, 0, "  ▸ " + src + (vol.Label.Length > 0 ? " " + vol.Label : "") + "  ·  " + vol.FsName.ToLowerInvariant() + "  ·  " +
                         Fmt.Bytes(vol.ClusterSize) + " clusters  ·  " + vol.StructureInfo + "  ·  " +
                         Fmt.N(vol.Items.Count) + " deleted entries", T.Dim);

            // list panel
            int lh = ListRows() + 3;
            int lx = 0, ly = 1, lw = W;
            string title = "deleted items · " + Fmt.N(view.Count) + " shown";
            S.Box(lx, ly, lw, lh, T.Crimson, title, T.Panel);
            string sortTag = " sort: " + SortNames[sortMode] + (IsAscending() ? " ▲" : " ▼") + "  type: " + TypeNames[typeMode] +
                             "  chance: " + ChanceNames[chanceMode] + (hideEmpty ? "  (empty hidden)" : "") + " ";
            if (sortTag.Length + title.Length + 10 < lw) S.Put(lw - sortTag.Length - 2, ly, sortTag, T.Orange, T.Panel);

            int iw = lw - 4, ix = 2;
            const int markW = 2, chanceW = 7, sizeW = 10, dateW = 18;
            int nameW = Math.Max(12, Math.Min(56, (int)((iw - markW - chanceW - sizeW - dateW) * 0.42)));
            int pathW = Math.Max(0, iw - markW - chanceW - nameW - sizeW - dateW - 3);

            int hy = ly + 1;
            S.Put(ix, hy, "  " + Screen.PadR("chance", chanceW) + Screen.PadR("name", nameW) + " " + Screen.PadL("size", sizeW) +
                          "  " + Screen.PadR("modified", dateW - 2) + " " + "folder", T.Orange, T.Panel);

            int rows = ListRows();
            if (cursor < top) top = cursor;
            if (cursor >= top + rows) top = cursor - rows + 1;
            top = Math.Max(0, Math.Min(top, Math.Max(0, view.Count - rows)));

            for (int r = 0; r < rows; r++)
            {
                int i = top + r;
                if (i >= view.Count) break;
                var it = view[i];
                int y = hy + 1 + r;
                bool on = i == cursor;
                int bgc = on ? T.Lime : T.Panel;
                if (on) S.Fill(1, y, lw - 2, 1, bgc);
                int x = ix;
                bool mk = marked.Contains(it);
                S.Put(x, y, mk ? "◆ " : "  ", on ? T.Dark : T.Lime, bgc); x += markW;
                DrawChance(x, y, it, on, bgc); x += chanceW;
                string nm = it.IsDir ? it.Name + "\\" : it.Name;
                int nc = on ? T.Dark : it.IsDir ? T.Blue : (it.Chance == Chance.Overwritten || it.Chance == Chance.Zeroed ? T.Dim : T.Text);
                S.Put(x, y, Screen.PadR(nm, nameW), nc, bgc); x += nameW + 1;
                S.Put(x, y, Screen.PadL(it.IsDir ? Fmt.N(CountDesc(it)) + " ⋯" : Fmt.Bytes(it.Size), sizeW), on ? T.Dark : T.Text, bgc); x += sizeW + 2;
                S.Put(x, y, Screen.PadR(Fmt.Date(it.Modified), dateW - 2), on ? T.Dark : T.Dim, bgc); x += dateW - 1;
                string path = (it.Orphan ? "‹lost›" : "") + it.Path;
                S.Put(x, y, Screen.ClipLeft(path, pathW), on ? T.Dark : (it.Orphan ? T.Magenta : T.Dim), bgc);
            }
            if (view.Count == 0)
                S.Put(ix + 2, hy + 2, vol.Items.Count == 0 ? "no deleted entries were found in the MFT." : "nothing matches the current filters.  (C = chance filter, Z = show empty, / = search)", T.Dim, T.Panel);

            // scrollbar
            if (view.Count > rows)
            {
                int th = Math.Max(1, rows * rows / view.Count);
                int ty = (int)((long)(rows - th) * top / Math.Max(1, view.Count - rows));
                for (int r = 0; r < rows; r++) S.Put(lw - 1, hy + 1 + r, r >= ty && r < ty + th ? "█" : "│", r >= ty && r < ty + th ? T.Magenta : T.Crimson, T.Panel);
            }

            // detail panel
            int dy = ly + lh, dh = H - dy - 2;
            S.Box(0, dy, W, dh, T.Teal, "details", T.Panel);
            if (view.Count > 0) DrawDetail(view[cursor], 2, dy + 1, W - 4);

            // footer keys
            int kx = 1, ky = H - 2;
            if (filterMode)
            {
                kx = S.Key(kx, ky, "type", "to search name/path", T.Magenta);
                kx = S.Key(kx, ky, "enter", "keep", T.Teal);
                S.Key(kx, ky, "esc", "clear", T.Crimson);
            }
            else
            {
                kx = S.Key(kx, ky, "↑↓", "move", T.Teal);
                kx = S.Key(kx, ky, "spc", "mark", T.Lime);
                kx = S.Key(kx, ky, "A", "all", T.Lime);
                kx = S.Key(kx, ky, "/", "search", T.Magenta);
                kx = S.Key(kx, ky, "T", "type", T.Blue);
                kx = S.Key(kx, ky, "C", "chance", T.Blue);
                kx = S.Key(kx, ky, "S", "sort", T.Purple);
                kx = S.Key(kx, ky, "I", "invert", T.Purple);
                kx = S.Key(kx, ky, "Z", "empty", T.Purple);
                kx = S.Key(kx, ky, "R", "recover", T.Crimson);
                kx = S.Key(kx, ky, "F1", "help", T.Purple);
                kx = S.Key(kx, ky, "esc", "drives", T.Orange);
                S.Key(kx, ky, "Q", "quit", T.Orange);
            }

            long mb = marked.Where(m => !m.IsDir).Sum(m => m.Size);
            string mid = src + "  │  " + Fmt.N(view.Count) + " / " + Fmt.N(vol.Items.Count) + " items  │  " +
                         Fmt.N(marked.Count) + " marked (" + Fmt.Bytes(mb) + ")" + (filter.Length > 0 || filterMode ? "  │  search: " + filter + (filterMode ? "█" : "") : "");
            StatusBar(filterMode ? "SEARCH" : "BROWSE", filterMode ? T.Magenta : T.Lime, mid);
            S.Flush();
        }

        bool IsAscending() { return sortMode == 1 || sortMode == 4 ? !sortAsc : sortAsc; }

        void DrawChance(int x, int y, DeletedItem it, bool on, int bgc)
        {
            int filled; int col;
            switch (it.Chance)
            {
                case Chance.Excellent: filled = 5; col = T.Good; break;
                case Chance.Partial:
                    filled = it.Clusters > 0 ? Math.Max(1, (int)Math.Round(4.0 * (it.Clusters - it.ClustersInUse) / it.Clusters)) : 2;
                    col = T.Warn; break;
                case Chance.Overwritten: filled = 0; col = T.Bad; break;
                case Chance.Zeroed: S.Put(x, y, "zeros ", on ? T.Dark : T.Bad, bgc); return;
                case Chance.Unsupported: S.Put(x, y, "n/a   ", on ? T.Dark : T.Dim, bgc); return;
                case Chance.Folder: S.Put(x, y, "dir   ", on ? T.Dark : T.Blue, bgc); return;
                default: S.Put(x, y, "?     ", on ? T.Dark : T.Dim, bgc); return;
            }
            S.Put(x, y, new string('■', filled), on ? T.Dark : col, bgc);
            S.Put(x + filled, y, new string('□', 5 - filled), on ? T.Dark : (it.Chance == Chance.Overwritten ? T.Bad : T.Dim), bgc);
        }

        static int CountDesc(DeletedItem d)
        {
            if (d.Children == null) return 0;
            int n = 0;
            var stack = new Stack<DeletedItem>(d.Children);
            var seen = new HashSet<DeletedItem>();
            while (stack.Count > 0)
            {
                var c = stack.Pop();
                if (!seen.Add(c)) continue;
                n++;
                if (c.Children != null) foreach (var cc in c.Children) stack.Push(cc);
            }
            return n;
        }

        void DrawDetail(DeletedItem it, int x, int y, int w)
        {
            vol.CheckZeros(it);   // one small read; detects TRIM'd SSD space
            S.Put(x, y, Screen.Clip(it.Name, w - 30), it.IsDir ? T.Blue : T.Lime, T.Panel);
            string sz = it.IsDir ? "folder · " + Fmt.N(CountDesc(it)) + " deleted items inside" : Fmt.N(it.Size) + " bytes";
            S.Put(x + w - sz.Length, y, sz, T.Text, T.Panel);

            S.Put(x, y + 1, "path     ", T.Dim, T.Panel);
            S.Put(x + 9, y + 1, Screen.ClipLeft((it.Orphan ? "‹lost parent›" : "") + it.FullPath, w - 9),
                  it.Orphan ? T.Magenta : T.Text, T.Panel);

            S.Put(x, y + 2, "times    ", T.Dim, T.Panel);
            S.Put(x + 9, y + 2, "modified " + Fmt.Date(it.Modified) + "   created " + Fmt.Date(it.Created) +
                                "   ·  " + it.Ref + (it.Guessed ? "  ·  location inferred" : ""), T.Text, T.Panel);

            S.Put(x, y + 3, "chance   ", T.Dim, T.Panel);
            DrawChance(x + 9, y + 3, it, false, T.Panel);
            int col; string txt = Describe(it, out col);
            S.Put(x + 16, y + 3, Screen.Clip(txt, w - 16), col, T.Panel);

            if (y + 4 < S.H - 3)
            {
                S.Put(x, y + 4, "storage  ", T.Dim, T.Panel);
                string st;
                if (it.IsDir) st = "directory index (contents are recovered from the child entries)";
                else if (it.Resident != null) st = "resident: data stored inside the MFT record itself";
                else
                {
                    int nfr = it.AllRuns().Count;
                    st = Fmt.N(it.Clusters) + " clusters in " + nfr + " run" + (nfr == 1 ? "" : "s") +
                         (it.Compressed ? " · compressed" : "") + (it.Encrypted ? " · EFS encrypted" : "") +
                         (it.InDeletedFolder ? " · inside a deleted folder" : "");
                }
                S.Put(x + 9, y + 4, Screen.Clip(st, w - 9), T.Text, T.Panel);
            }
        }

        static string Describe(DeletedItem it, out int col)
        {
            switch (it.Chance)
            {
                case Chance.Excellent: col = T.Good; return "excellent - none of its clusters have been reused";
                case Chance.Partial:
                    col = T.Warn;
                    return "partial - " + Fmt.N(it.ClustersInUse) + " of " + Fmt.N(it.Clusters) + " clusters now belong to other files; expect corruption";
                case Chance.Overwritten: col = T.Bad; return "overwritten - its space has been reused by other files";
                case Chance.Zeroed: col = T.Bad; return "zeroed - space reads back as zeros (SSD TRIM); content is most likely gone";
                case Chance.Unsupported: col = T.Dim; return "unsupported - NTFS-compressed or EFS-encrypted data";
                case Chance.Folder: col = T.Blue; return "folder - recovering it restores every deleted item inside";
                default: col = T.Dim; return "unknown - cluster bitmap unavailable";
            }
        }

        // ───────────────────────────── 4. recover ─────────────────────────────

        void DoRecover()
        {
            List<DeletedItem> sel = marked.Count > 0 ? marked.ToList() : (view.Count > 0 ? new List<DeletedItem> { view[cursor] } : null);
            if (sel == null) return;

            string dest = lastDest ?? Recovery.DefaultDestination(vol);
            while (true)
            {
                string label = "recover " + Fmt.N(sel.Count) + " item" + (sel.Count == 1 ? "" : "s") + " into (must be a different drive):";
                dest = Prompt("recover", label, dest, T.Crimson, DrawBrowse);
                if (dest == null) return;
                string bad = Recovery.CheckDestination(vol, dest);
                if (bad == null) break;
                MessageBox("unsafe destination", bad, T.Orange, DrawBrowse);
            }
            lastDest = dest;

            var rec = new Recovery(vol);
            List<RecoveryJob> jobs;
            try { jobs = rec.Plan(sel, dest); }
            catch (Exception e) { MessageBox("cannot plan recovery", e.Message, T.Crimson, DrawBrowse); return; }

            RecoveryResult result = null;
            Exception err = null;
            vol.Cancel = false;
            var th = new Thread(() => { try { result = rec.Run(jobs); } catch (Exception e) { err = e; } });
            th.IsBackground = true;
            th.Start();
            while (th.IsAlive)
            {
                DrawBrowse();
                int w = Math.Min(S.W - 6, 80), h = 9, x = (S.W - w) / 2, y = (S.H - h) / 2;
                S.Box(x, y, w, h, T.Crimson, "recovering", T.Panel);
                double f = rec.TotalBytes > 0 ? (double)rec.DoneBytes / rec.TotalBytes : (double)rec.DoneJobs / Math.Max(1, rec.TotalJobs);
                S.Meter(x + 3, y + 2, w - 12, f, T.Heat);
                S.Put(x + w - 8, y + 2, Screen.PadL((int)(f * 100) + "%", 5), T.Lime, T.Panel);
                S.Put(x + 3, y + 4, Fmt.N(rec.DoneJobs) + " / " + Fmt.N(rec.TotalJobs) + " items  ·  " +
                                    Fmt.Bytes(rec.DoneBytes) + " / " + Fmt.Bytes(rec.TotalBytes), T.Text, T.Panel);
                S.Put(x + 3, y + 5, Screen.ClipLeft(rec.Current, w - 6), T.Dim, T.Panel);
                S.Put(x + 3, y + 7, "esc", T.Dark, T.Dim);
                S.Put(x + 7, y + 7, "stop", T.Dim, T.Panel);
                S.Flush();
                var k = WaitKey(100);
                if (k.HasValue && k.Value.Key == ConsoleKey.Escape) vol.Cancel = true;
            }
            bool stopped = vol.Cancel;
            vol.Cancel = false;
            if (err != null) { MessageBox("recovery failed", err.Message, T.Crimson, DrawBrowse); return; }

            ShowResult(result, stopped);
            if (result.Failed == 0 && !stopped) marked.Clear();
        }

        void ShowResult(RecoveryResult r, bool stopped)
        {
            var lines = new List<KeyValuePair<string, int>>();
            lines.Add(new KeyValuePair<string, int>((stopped ? "stopped early. " : "") + "recovered " + Fmt.N(r.Files) + " file" + (r.Files == 1 ? "" : "s") +
                                                    " (" + Fmt.Bytes(r.Bytes) + ") and " + Fmt.N(r.Folders) + " folder" + (r.Folders == 1 ? "" : "s"), T.Good));
            if (r.Skipped > 0) lines.Add(new KeyValuePair<string, int>("skipped " + r.Skipped + " unsupported file(s)", T.Warn));
            if (r.Failed > 0) lines.Add(new KeyValuePair<string, int>("failed " + r.Failed + " item(s)", T.Bad));
            lines.Add(new KeyValuePair<string, int>("", T.Text));
            lines.Add(new KeyValuePair<string, int>("saved to " + (r.OutRoot ?? "?"), T.Text));
            foreach (var e in r.Errors.Take(6)) lines.Add(new KeyValuePair<string, int>("  ! " + e, T.Dim));
            if (r.Errors.Count > 6) lines.Add(new KeyValuePair<string, int>("  … and " + (r.Errors.Count - 6) + " more", T.Dim));
            lines.Add(new KeyValuePair<string, int>("", T.Text));
            lines.Add(new KeyValuePair<string, int>("recovered files may still be damaged if their clusters were reused - open and check them.", T.Dim));

            while (true)
            {
                DrawBrowse();
                int w = Math.Min(S.W - 6, 96), h = lines.Count + 5, x = (S.W - w) / 2, y = Math.Max(1, (S.H - h) / 2);
                S.Box(x, y, w, h, r.Failed > 0 ? T.Orange : T.Teal, "recovery complete", T.Panel);
                for (int i = 0; i < lines.Count; i++) S.Put(x + 3, y + 2 + i, Screen.Clip(lines[i].Key, w - 6), lines[i].Value, T.Panel);
                int kx = S.Key(x + 3, y + h - 2, "O", "open folder", T.Teal);
                S.Key(kx, y + h - 2, "any key", "close", T.Dim);
                S.Flush();
                var k = WaitKey(500);
                if (!k.HasValue) continue;
                if (k.Value.Key == ConsoleKey.O && r.OutRoot != null && Directory.Exists(r.OutRoot))
                {
                    try { Process.Start("explorer.exe", "\"" + r.OutRoot + "\""); } catch { }
                }
                return;
            }
        }
    }
}
