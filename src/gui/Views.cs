using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace Undelete.Gui
{
    /// <summary>Virtual, owner-drawn table of deleted items.</summary>
    sealed class ItemGrid : NeonControl
    {
        public List<DeletedItem> Items = new List<DeletedItem>();
        public HashSet<DeletedItem> Marked = new HashSet<DeletedItem>();
        public string EmptyText = "pick a volume and press scan";
        public int SortCol = 4;
        public bool SortAsc;

        public event Action CurrentChanged, MarksChanged, Activated, SortChanged;

        int current = -1, top, anchor = -1;
        bool dragThumb; int dragOffset;
        static readonly string[] Headers = { "", "chance", "name", "size", "modified", "folder" };

        public ItemGrid() { TabStop = true; Font = N.Mono(9.5f); }

        int RowH { get { return N.S(24); } }
        int HeadH { get { return N.S(26); } }
        int BarW { get { return N.S(10); } }
        int VisRows { get { return Math.Max(1, (Height - HeadH) / RowH); } }

        public int Current
        {
            get { return current; }
            set
            {
                int v = Items.Count == 0 ? -1 : Math.Max(0, Math.Min(Items.Count - 1, value));
                if (v != current) { current = v; EnsureVisible(); Invalidate(); if (CurrentChanged != null) CurrentChanged(); }
            }
        }

        public DeletedItem CurrentItem { get { return current >= 0 && current < Items.Count ? Items[current] : null; } }

        public void SetItems(List<DeletedItem> items, DeletedItem keep)
        {
            Items = items;
            int idx = keep != null ? items.IndexOf(keep) : -1;
            current = -1;
            top = 0;
            Current = idx >= 0 ? idx : 0;
            if (Items.Count == 0 && CurrentChanged != null) CurrentChanged();
            Invalidate();
        }

        void EnsureVisible()
        {
            if (current < top) top = current;
            if (current >= top + VisRows) top = current - VisRows + 1;
            ClampTop();
        }

        void ClampTop() { top = Math.Max(0, Math.Min(top, Math.Max(0, Items.Count - VisRows))); }

        int[] Cols()
        {
            int w = Width - BarW - N.S(4);
            int mark = N.S(30), chance = N.S(78), size = N.S(92), date = N.S(146);
            int rest = Math.Max(N.S(200), w - mark - chance - size - date);
            int name = Math.Max(N.S(150), Math.Min(N.S(460), (int)(rest * 0.42)));
            int folder = Math.Max(0, rest - name);
            return new[] { mark, chance, name, size, date, folder };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(N.Panel);
            var cols = Cols();
            var f = Font;
            var hf = N.Mono(8.5f);

            // header
            int x = N.S(4);
            for (int c = 0; c < cols.Length; c++)
            {
                string h = Headers[c];
                if (c == SortCol && c > 0) h += SortAsc ? " ▲" : " ▼";
                int pad = c == 4 ? N.S(10) : N.S(4);
                var r = new Rectangle(x + pad, 0, cols[c] - pad - N.S(6), HeadH);
                N.Draw(g, h, hf, r, c == SortCol ? N.Lime : N.Orange, c == 3 ? N.Right : N.Left);
                x += cols[c];
            }
            using (var p = new Pen(N.Alpha(N.Orange, 90))) g.DrawLine(p, N.S(4), HeadH - 1, Width - BarW - N.S(4), HeadH - 1);

            if (Items.Count == 0)
            {
                N.Draw(g, EmptyText, f, new Rectangle(0, HeadH, Width - BarW, Height - HeadH), N.Dim, N.Center);
                return;
            }

            ClampTop();
            Color zebra = N.Mix(N.Panel, N.PanelAlt, 0.45);
            for (int r = 0; r < VisRows + 1; r++)
            {
                int i = top + r;
                if (i >= Items.Count) break;
                var it = Items[i];
                int y = HeadH + r * RowH;
                bool on = i == current;
                bool mk = Marked.Contains(it);
                var row = new Rectangle(0, y, Width - BarW - N.S(2), RowH);
                if (on)
                {
                    using (var b = new SolidBrush(N.Lime)) g.FillRectangle(b, row);
                }
                else if (mk) using (var b = new SolidBrush(N.Mix(N.Panel, N.Lime, 0.10))) g.FillRectangle(b, row);
                else if (r % 2 == 1) using (var b = new SolidBrush(zebra)) g.FillRectangle(b, row);

                Color fg = on ? N.Dark : N.Text;
                x = N.S(4);
                N.Draw(g, mk ? "◆" : "◇", f, new Rectangle(x + N.S(6), y, cols[0], RowH), on ? N.Dark : (mk ? N.Lime : N.Alpha(N.Dim, 255)));
                x += cols[0];
                DrawChance(g, it, new Rectangle(x + N.S(4), y, cols[1] - N.S(8), RowH), on);
                x += cols[1];
                Color nc = on ? N.Dark : it.IsDir ? N.Blue : (it.Chance == Chance.Overwritten || it.Chance == Chance.Zeroed ? N.Dim : N.Text);
                N.Draw(g, it.IsDir ? "▸ " + it.Name : it.Name, f, new Rectangle(x + N.S(4), y, cols[2] - N.S(10), RowH), nc);
                x += cols[2];
                N.Draw(g, it.IsDir ? Fmt.N(ItemQuery.CountDescendants(it)) + " items" : Fmt.Bytes(it.Size), f,
                       new Rectangle(x, y, cols[3] - N.S(6), RowH), on ? N.Dark : (it.IsDir ? N.Dim : fg), N.Right);
                x += cols[3];
                N.Draw(g, Fmt.Date(it.Modified), f, new Rectangle(x + N.S(10), y, cols[4] - N.S(12), RowH), on ? N.Dark : N.Dim);
                x += cols[4];
                string path = (it.Orphan ? "‹lost›" : "") + it.Path;
                N.Draw(g, path, f, new Rectangle(x + N.S(4), y, cols[5] - N.S(8), RowH), on ? N.Dark : (it.Orphan ? N.Magenta : N.Dim), N.PathLeft);
            }

            // scrollbar
            var track = TrackRect();
            using (var b = new SolidBrush(N.Bg)) g.FillRectangle(b, track);
            if (Items.Count > VisRows)
            {
                var th = ThumbRect();
                using (var lg = new LinearGradientBrush(new Rectangle(th.X, th.Y - 1, th.Width, th.Height + 2), N.Crimson, N.Purple, 90f))
                    g.FillRectangle(lg, th);
            }
        }

        public static void DrawChance(Graphics g, DeletedItem it, Rectangle r, bool on)
        {
            string label = null; Color lc = N.Dim;
            int filled = 0; Color col = N.Teal;
            switch (it.Chance)
            {
                case Chance.Excellent: filled = 5; col = N.Teal; break;
                case Chance.Partial:
                    filled = it.Clusters > 0 ? Math.Max(1, (int)Math.Round(4.0 * (it.Clusters - it.ClustersInUse) / it.Clusters)) : 2;
                    col = N.Orange; break;
                case Chance.Overwritten: filled = 0; col = N.Crimson; break;
                case Chance.Zeroed: label = "zeros"; lc = N.Crimson; break;
                case Chance.Unsupported: label = "n/a"; break;
                case Chance.Folder: label = "folder"; lc = N.Blue; break;
                default: label = "?"; break;
            }
            if (label != null) { N.Draw(g, label, N.Mono(8.5f), r, on ? N.Dark : lc); return; }
            int sq = N.S(9), gap = N.S(3);
            int y = r.Y + (r.Height - sq) / 2;
            for (int i = 0; i < 5; i++)
            {
                var b = new Rectangle(r.X + i * (sq + gap), y, sq, sq);
                if (i < filled) using (var br = new SolidBrush(on ? N.Dark : col)) g.FillRectangle(br, b);
                else using (var p = new Pen(on ? N.Dark : (it.Chance == Chance.Overwritten ? N.Crimson : N.Dim))) g.DrawRectangle(p, b.X, b.Y, b.Width - 1, b.Height - 1);
            }
        }

        Rectangle TrackRect() { return new Rectangle(Width - BarW, HeadH, BarW, Height - HeadH); }

        Rectangle ThumbRect()
        {
            var tr = TrackRect();
            int th = Math.Max(N.S(28), (int)((long)tr.Height * VisRows / Math.Max(1, Items.Count)));
            int range = Math.Max(1, Items.Count - VisRows);
            int ty = tr.Y + (int)((long)(tr.Height - th) * top / range);
            return new Rectangle(tr.X + N.S(2), ty, tr.Width - N.S(4), th);
        }

        int RowAt(int y) { return y < HeadH ? -1 : top + (y - HeadH) / RowH; }

        int ColAt(int x)
        {
            var cols = Cols(); int cx = N.S(4);
            for (int c = 0; c < cols.Length; c++) { if (x < cx + cols[c]) return c; cx += cols[c]; }
            return cols.Length - 1;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (e.X >= Width - BarW && e.Y >= HeadH)
            {
                var th = ThumbRect();
                if (th.Contains(e.Location)) { dragThumb = true; dragOffset = e.Y - th.Y; }
                else { top += e.Y < th.Y ? -VisRows : VisRows; ClampTop(); Invalidate(); }
                return;
            }
            if (e.Y < HeadH)
            {
                int c = ColAt(e.X);
                if (c == 0) return;
                if (c == SortCol) SortAsc = !SortAsc; else { SortCol = c; SortAsc = c == 2 || c == 5; }
                if (SortChanged != null) SortChanged();
                return;
            }
            int row = RowAt(e.Y);
            if (row < 0 || row >= Items.Count) return;
            bool ctrl = (ModifierKeys & Keys.Control) != 0, shift = (ModifierKeys & Keys.Shift) != 0;
            if (shift && anchor >= 0)
            {
                for (int i = Math.Min(anchor, row); i <= Math.Max(anchor, row); i++) Marked.Add(Items[i]);
                FireMarks();
            }
            else if (ctrl || ColAt(e.X) == 0)
            {
                Toggle(Items[row]);
                anchor = row;
            }
            else anchor = row;
            Current = row;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragThumb)
            {
                var tr = TrackRect(); var th = ThumbRect();
                double f = (double)(e.Y - dragOffset - tr.Y) / Math.Max(1, tr.Height - th.Height);
                top = (int)(f * Math.Max(0, Items.Count - VisRows));
                ClampTop(); Invalidate();
            }
            Cursor = e.Y < HeadH && ColAt(e.X) > 0 ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseUp(MouseEventArgs e) { dragThumb = false; }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (e.Y >= HeadH && e.X < Width - BarW && ColAt(e.X) > 0 && Activated != null) Activated();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            top -= Math.Sign(e.Delta) * 3;
            ClampTop(); Invalidate();
        }

        protected override bool IsInputKey(Keys k)
        {
            switch (k & Keys.KeyCode)
            {
                case Keys.Up: case Keys.Down: case Keys.PageUp: case Keys.PageDown: case Keys.Home: case Keys.End: case Keys.Space: return true;
            }
            return base.IsInputKey(k);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Up: Current = current - 1; break;
                case Keys.Down: Current = current + 1; break;
                case Keys.PageUp: Current = current - VisRows + 1; break;
                case Keys.PageDown: Current = current + VisRows - 1; break;
                case Keys.Home: Current = 0; break;
                case Keys.End: Current = Items.Count - 1; break;
                case Keys.Space:
                    if (CurrentItem != null) { Toggle(CurrentItem); anchor = current; Current = current + 1; Invalidate(); }
                    break;
                case Keys.Enter: if (Activated != null) Activated(); break;
                case Keys.A:
                    if (e.Control) { MarkAll(); e.Handled = true; }
                    break;
            }
            base.OnKeyDown(e);
        }

        public void MarkAll()
        {
            if (Items.Count > 0 && Items.All(Marked.Contains)) foreach (var i in Items) Marked.Remove(i);
            else foreach (var i in Items) Marked.Add(i);
            FireMarks(); Invalidate();
        }

        public void ClearMarks() { Marked.Clear(); FireMarks(); Invalidate(); }

        void Toggle(DeletedItem it) { if (!Marked.Remove(it)) Marked.Add(it); FireMarks(); }
        void FireMarks() { if (MarksChanged != null) MarksChanged(); }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    }

    /// <summary>Volume picker: one row per drive with a usage meter.</summary>
    sealed class DriveList : NeonControl
    {
        public sealed class Entry
        {
            public string Path, Label, Fs;
            public long Size, Free;
            public bool Ok;
        }

        public List<Entry> Entries = new List<Entry>();
        int selected, hover = -1;
        public event Action Activated, SelectedChanged;

        public DriveList() { Cursor = Cursors.Hand; Font = N.Mono(9.5f); }

        int RowH { get { return N.S(40); } }
        public int PreferredHeight { get { return Math.Max(1, Entries.Count) * RowH; } }
        public Entry SelectedEntry { get { return selected >= 0 && selected < Entries.Count ? Entries[selected] : null; } }

        public void Reload()
        {
            string keep = SelectedEntry != null ? SelectedEntry.Path : null;
            var list = new List<Entry>();
            foreach (var d in DriveInfo.GetDrives())
            {
                var en = new Entry { Path = d.Name.Substring(0, 2) };
                try
                {
                    if (!d.IsReady) continue;
                    en.Label = d.VolumeLabel; en.Fs = d.DriveFormat; en.Size = d.TotalSize; en.Free = d.TotalFreeSpace;
                    en.Ok = FsVolume.IsSupported(en.Fs);
                }
                catch { continue; }
                list.Add(en);
            }
            Entries = list;
            selected = Math.Max(0, keep != null ? list.FindIndex(x => x.Path == keep) : list.FindIndex(x => x.Ok));
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e) { int h = e.Y / RowH; if (h != hover) { hover = h; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            int s = e.Y / RowH;
            if (s >= 0 && s < Entries.Count && s != selected) { selected = s; Invalidate(); if (SelectedChanged != null) SelectedChanged(); }
        }
        protected override void OnMouseDoubleClick(MouseEventArgs e) { if (Activated != null && SelectedEntry != null) Activated(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(N.Panel);
            for (int i = 0; i < Entries.Count; i++)
            {
                var en = Entries[i];
                var r = new Rectangle(0, i * RowH, Width, RowH - N.S(4));
                bool on = i == selected;
                if (on) using (var b = new SolidBrush(N.Lime)) g.FillRectangle(b, r);
                else if (i == hover) using (var b = new SolidBrush(N.PanelAlt)) g.FillRectangle(b, r);
                Color fg = on ? N.Dark : (en.Ok ? N.Text : N.Dim);
                N.Draw(g, en.Path, N.Mono(11f, FontStyle.Bold), new Rectangle(r.X + N.S(8), r.Y, N.S(34), r.Height), on ? N.Dark : N.Crimson);
                string lbl = string.IsNullOrEmpty(en.Label) ? "(no label)" : en.Label;
                N.Draw(g, lbl, Font, new Rectangle(r.X + N.S(46), r.Y + N.S(2), r.Width - N.S(150), N.S(18)), fg);
                N.Draw(g, (en.Fs ?? "") + (en.Ok ? "" : " · unsupported"), N.Mono(8f), new Rectangle(r.X + N.S(46), r.Y + N.S(18), r.Width - N.S(150), N.S(16)), on ? N.Dark : N.Dim);
                N.Draw(g, Fmt.Bytes(en.Size), N.Mono(8.5f), new Rectangle(r.Right - N.S(100), r.Y + N.S(2), N.S(92), N.S(18)), fg, N.Right);
                double used = en.Size > 0 ? 1 - (double)en.Free / en.Size : 0;
                var bar = new Rectangle(r.Right - N.S(100), r.Y + N.S(22), N.S(92), N.S(8));
                if (on) { using (var b = new SolidBrush(N.Mix(N.Lime, N.Dark, 0.25))) g.FillRectangle(b, bar); using (var b = new SolidBrush(N.Dark)) g.FillRectangle(b, bar.X, bar.Y, (int)(bar.Width * used), bar.Height); }
                else NeonMeter.PaintBar(g, bar, used, N.Aurora);
            }
            if (Entries.Count == 0) N.Draw(g, "no drives found", Font, ClientRectangle, N.Dim, N.Center);
        }
    }

    /// <summary>Key/value details of the current item.</summary>
    sealed class DetailView : NeonControl
    {
        DeletedItem item;
        public DeletedItem Item { get { return item; } set { item = value; Invalidate(); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(N.Panel);
            if (item == null) { N.Draw(g, "nothing selected", Font, ClientRectangle, N.Dim, N.Center); return; }
            int lh = N.S(22), y = 0, kx = N.S(92);
            var big = N.Mono(12f, FontStyle.Bold);
            N.Draw(g, item.Name, big, new Rectangle(0, y, Width, N.S(26)), item.IsDir ? N.Blue : N.Lime);
            y += N.S(30);

            Row(g, ref y, lh, kx, "size", item.IsDir ? "folder · " + Fmt.N(ItemQuery.CountDescendants(item)) + " deleted items inside"
                                                     : Fmt.Bytes(item.Size) + "  (" + Fmt.N(item.Size) + " bytes)", N.Text);
            Row(g, ref y, lh, kx, "location", (item.Orphan ? "‹lost parent› " : "") + item.Path, item.Orphan ? N.Magenta : N.Text, true);
            Row(g, ref y, lh, kx, "modified", Fmt.Date(item.Modified) + "     created " + Fmt.Date(item.Created), N.Text);

            N.Draw(g, "chance", Font, new Rectangle(0, y, kx, lh), N.Dim);
            ItemGrid.DrawChance(g, item, new Rectangle(kx, y, N.S(70), lh), false);
            int col; string d = ItemQuery.Describe(item, out col);
            N.Draw(g, d, Font, new Rectangle(kx + N.S(72), y, Width - kx - N.S(72), lh), N.C(col));
            y += lh;

            string st;
            if (item.IsDir) st = "directory - its contents come back from the deleted entries inside it";
            else if (item.Resident != null) st = "resident - stored inside the MFT record itself";
            else
            {
                int runs = item.AllRuns().Count;
                st = Fmt.N(item.Clusters) + " clusters in " + runs + " fragment" + (runs == 1 ? "" : "s") +
                     (item.Compressed ? " · compressed" : "") + (item.Encrypted ? " · EFS encrypted" : "") +
                     (item.InDeletedFolder ? " · inside a deleted folder" : "");
            }
            Row(g, ref y, lh, kx, "storage", st + (item.Guessed ? " · location inferred" : "") + "   · " + item.Ref, N.Dim);
        }

        void Row(Graphics g, ref int y, int lh, int kx, string k, string v, Color c, bool path = false)
        {
            N.Draw(g, k, Font, new Rectangle(0, y, kx, lh), N.Dim);
            N.Draw(g, v, Font, new Rectangle(kx, y, Width - kx, lh), c, path ? N.PathLeft : N.Left);
            y += lh;
        }
    }

    /// <summary>Shows a thumbnail, text excerpt or hex dump of the current deleted file.</summary>
    sealed class PreviewView : NeonControl
    {
        Image image;
        string text, note = "";
        bool hex;

        public void Clear(string message) { SetImage(null); text = null; note = message ?? ""; Invalidate(); }

        public void Show(DeletedItem it, byte[] data)
        {
            SetImage(null); text = null; hex = false; note = "";
            if (data == null || data.Length == 0) { note = it.IsDir ? "folder" : "empty"; Invalidate(); return; }
            if (data.All(b => b == 0)) { note = "reads as zeros"; Invalidate(); return; }

            if (LooksLikeImage(data))
            {
                try
                {
                    using (var ms = new MemoryStream(data))
                    using (var img = Image.FromStream(ms, false, false))
                        SetImage(new Bitmap(img));
                    note = image.Width + " × " + image.Height + (data.Length < it.Size ? " · partial" : "");
                    Invalidate();
                    return;
                }
                catch { note = "image data is damaged"; }
            }

            int n = Math.Min(data.Length, 4096);
            int printable = 0;
            for (int i = 0; i < n; i++) { byte b = data[i]; if (b == 9 || b == 10 || b == 13 || (b >= 32 && b < 127) || b >= 0xC0) printable++; }
            if (printable > n * 0.92)
            {
                text = Encoding.UTF8.GetString(data, 0, n).Replace("\r\n", "\n").Replace('\t', ' ');
                if (note.Length == 0) note = "text";
            }
            else
            {
                hex = true;
                var sb = new StringBuilder();
                for (int o = 0; o < Math.Min(data.Length, 512); o += 8)
                {
                    sb.Append(o.ToString("x4")).Append("  ");
                    for (int k = 0; k < 8; k++) sb.Append(o + k < data.Length ? data[o + k].ToString("x2") + " " : "   ");
                    sb.Append(' ');
                    for (int k = 0; k < 8 && o + k < data.Length; k++) { byte b = data[o + k]; sb.Append(b >= 32 && b < 127 ? (char)b : '·'); }
                    sb.Append('\n');
                }
                text = sb.ToString();
                if (note.Length == 0) note = "binary · first 512 bytes";
            }
            Invalidate();
        }

        static bool LooksLikeImage(byte[] d)
        {
            if (d.Length < 8) return false;
            if (d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return true;                         // jpeg
            if (d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) return true;          // png
            if (d[0] == 'G' && d[1] == 'I' && d[2] == 'F') return true;                             // gif
            if (d[0] == 'B' && d[1] == 'M') return true;                                            // bmp
            if ((d[0] == 'I' && d[1] == 'I' && d[2] == 42) || (d[0] == 'M' && d[1] == 'M' && d[3] == 42)) return true; // tiff
            return false;
        }

        void SetImage(Image img) { if (image != null) image.Dispose(); image = img; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(N.Bg);
            using (var p = new Pen(N.Alpha(N.Purple, 120))) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            var area = new Rectangle(N.S(6), N.S(6), Width - N.S(12), Height - N.S(28));
            if (image != null)
            {
                double s = Math.Min((double)area.Width / image.Width, (double)area.Height / image.Height);
                s = Math.Min(s, 4);
                int w = (int)(image.Width * s), h = (int)(image.Height * s);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(image, area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
            }
            else if (text != null)
            {
                var f = N.Mono(hex ? 7.5f : 8f);
                TextRenderer.DrawText(g, text, f, area, hex ? N.Dim : N.Text,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix | (hex ? 0 : TextFormatFlags.WordBreak) | TextFormatFlags.NoPadding);
            }
            N.Draw(g, note, N.Mono(8f), new Rectangle(N.S(8), Height - N.S(20), Width - N.S(16), N.S(16)),
                   image == null && text == null ? N.Dim : N.Purple, image == null && text == null ? N.Center : N.Left);
            if (image == null && text == null && note.Length > 0)
                N.Draw(g, "no preview", N.Mono(10f), area, N.Alpha(N.Dim, 255), N.Center);
        }

        protected override void Dispose(bool disposing) { if (disposing) SetImage(null); base.Dispose(disposing); }
    }

    /// <summary>Per-chance tallies with mini meters, or scan progress while scanning.</summary>
    sealed class SummaryView : NeonControl
    {
        public FsVolume Vol;
        public bool Scanning;
        public TimeSpan Elapsed;
        public int[] Counts;     // excellent, partial, overwritten, zeroed, unsupported, folder
        public long RecoverableBytes;
        readonly List<double> spark = new List<double>();
        long lastRec; DateTime lastT = DateTime.MinValue;

        public void Tick()
        {
            if (Vol != null && Scanning)
            {
                var now = DateTime.UtcNow;
                if (lastT != DateTime.MinValue)
                {
                    double dt = (now - lastT).TotalSeconds;
                    if (dt > 0) spark.Add((Vol.RecordsScanned - lastRec) / dt);
                    if (spark.Count > 120) spark.RemoveAt(0);
                }
                lastRec = Vol.RecordsScanned; lastT = now;
            }
            Invalidate();
        }

        public void ResetSpark() { spark.Clear(); lastT = DateTime.MinValue; lastRec = 0; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(N.Panel);
            int lh = N.S(22), y = 0;
            if (Vol == null) { N.Draw(g, "no volume scanned yet", Font, new Rectangle(0, 0, Width, lh), N.Dim); return; }

            if (Scanning)
            {
                double frac = Vol.RecordsTotal > 0 ? (double)Vol.RecordsScanned / Vol.RecordsTotal : 0;
                if (Vol.Phase.StartsWith("checking") || Vol.Phase.StartsWith("loading") || Vol.Phase.StartsWith("rebuilding") || Vol.Phase == "done") frac = 1;
                N.Draw(g, Vol.Phase + new string('.', (int)(Elapsed.TotalMilliseconds / 400) % 4), Font, new Rectangle(0, y, Width, lh), N.Text);
                y += lh + N.S(2);
                NeonMeter.PaintBar(g, new Rectangle(0, y, Width - N.S(50), N.S(14)), frac, N.Heat);
                N.Draw(g, (int)(frac * 100) + "%", Font, new Rectangle(Width - N.S(46), y - N.S(3), N.S(46), N.S(20)), N.Lime, N.Right);
                y += N.S(24);
                KV(g, ref y, lh, Vol.ProgressUnit.Split(' ').Last(), Fmt.N(Vol.RecordsScanned) + " / " + Fmt.N(Vol.RecordsTotal), N.Text);
                KV(g, ref y, lh, "deleted", Fmt.N(Vol.FoundCount) + " found", N.Crimson);
                KV(g, ref y, lh, "elapsed", Elapsed.ToString(@"mm\:ss"), N.Text);
                y += N.S(6);
                // throughput sparkline
                if (spark.Count > 1)
                {
                    double max = Math.Max(1, spark.Max());
                    int bw = Math.Max(2, N.S(3)), h = N.S(30);
                    int start = Math.Max(0, spark.Count - Width / bw);
                    for (int i = start; i < spark.Count; i++)
                    {
                        int bh = Math.Max(1, (int)(spark[i] / max * h));
                        using (var b = new SolidBrush(N.Mix(N.Purple, N.Magenta, (double)(i - start) / Math.Max(1, spark.Count - start))))
                            g.FillRectangle(b, (i - start) * bw, y + h - bh, bw - 1, bh);
                    }
                }
                return;
            }

            KV(g, ref y, lh, "volume", Vol.FsName + " · " + Fmt.Bytes(Vol.TotalClusters * Vol.ClusterSize) + " · " + Fmt.Bytes(Vol.ClusterSize) + "/cl", N.Text);
            KV(g, ref y, lh, "layout", Vol.StructureInfo, N.Text);
            KV(g, ref y, lh, "deleted", Fmt.N(Vol.Items.Count) + " entries", N.Crimson);
            y += N.S(8);
            if (Counts == null) return;
            int total = Math.Max(1, Counts.Sum());
            var rows = new[]
            {
                Tuple.Create("excellent", Counts[0], N.Teal), Tuple.Create("partial", Counts[1], N.Orange),
                Tuple.Create("overwritten", Counts[2], N.Crimson), Tuple.Create("zeroed", Counts[3], N.Crimson),
                Tuple.Create("unsupported", Counts[4], N.Dim), Tuple.Create("folders", Counts[5], N.Blue),
            };
            foreach (var r in rows)
            {
                N.Draw(g, r.Item1, Font, new Rectangle(0, y, N.S(100), lh), r.Item3);
                N.Draw(g, Fmt.N(r.Item2), Font, new Rectangle(N.S(96), y, N.S(70), lh), N.Text, N.Right);
                var bar = new Rectangle(N.S(176), y + N.S(7), Math.Max(N.S(10), Width - N.S(178)), N.S(8));
                using (var b = new SolidBrush(N.Track)) g.FillRectangle(b, bar);
                using (var b = new SolidBrush(r.Item3)) g.FillRectangle(b, bar.X, bar.Y, Math.Max(r.Item2 > 0 ? 2 : 0, (int)(bar.Width * (double)r.Item2 / total)), bar.Height);
                y += lh;
            }
            y += N.S(4);
            KV(g, ref y, lh, "intact", Fmt.Bytes(RecoverableBytes) + " recoverable", N.Teal);
        }

        void KV(Graphics g, ref int y, int lh, string k, string v, Color c)
        {
            N.Draw(g, k, Font, new Rectangle(0, y, N.S(80), lh), N.Dim);
            N.Draw(g, v, Font, new Rectangle(N.S(80), y, Width - N.S(80), lh), c);
            y += lh;
        }
    }
}
