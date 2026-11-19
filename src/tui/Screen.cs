using System;
using System.IO;
using System.Text;

namespace Undelete
{
    /// <summary>Double-buffered cell grid rendered with 24-bit ANSI escapes.</summary>
    sealed class Screen
    {
        public int W, H;
        char[] ch; int[] fg, bg;
        readonly Stream stdout = Console.OpenStandardOutput();
        static readonly Encoding Utf8 = new UTF8Encoding(false);

        public static void EnableVt()
        {
            IntPtr h = Native.GetStdHandle(-11);
            uint mode;
            if (Native.GetConsoleMode(h, out mode)) Native.SetConsoleMode(h, mode | 0x0004 | 0x0001); // VT processing
        }

        public void Raw(string s) { byte[] b = Utf8.GetBytes(s); stdout.Write(b, 0, b.Length); stdout.Flush(); }

        public bool Resize()
        {
            int w, h;
            try { w = Math.Max(60, Console.WindowWidth); h = Math.Max(18, Console.WindowHeight); }
            catch { w = 100; h = 30; }
            if (ch != null && w == W && h == H) return false;
            W = w; H = h;
            ch = new char[W * H]; fg = new int[W * H]; bg = new int[W * H];
            return true;
        }

        public void Clear()
        {
            Resize();
            for (int i = 0; i < ch.Length; i++) { ch[i] = ' '; fg[i] = T.Text; bg[i] = T.Bg; }
        }

        public void Put(int x, int y, string s, int f, int b = -1)
        {
            if (y < 0 || y >= H || s == null) return;
            for (int i = 0; i < s.Length; i++)
            {
                int xx = x + i;
                if (xx < 0) continue;
                if (xx >= W) break;
                int k = y * W + xx;
                ch[k] = s[i] < ' ' ? ' ' : s[i];
                fg[k] = f;
                if (b >= 0) bg[k] = b;
            }
        }

        /// <summary>Text with a horizontal colour gradient.</summary>
        public void PutGrad(int x, int y, string s, Func<double, int> grad, double phase = 0)
        {
            int n = Math.Max(1, s.Length - 1);
            for (int i = 0; i < s.Length; i++)
            {
                double t = (double)i / n + phase;
                t = t - Math.Floor(t);
                t = 1 - Math.Abs(1 - 2 * t) ; // ping-pong so the shimmer wraps smoothly
                Put(x + i, y, s[i].ToString(), grad(phase == 0 ? (double)i / n : t));
            }
        }

        public void Fill(int x, int y, int w, int h, int b)
        {
            for (int yy = y; yy < y + h; yy++)
                for (int xx = x; xx < x + w; xx++)
                {
                    if (xx < 0 || yy < 0 || xx >= W || yy >= H) continue;
                    int k = yy * W + xx;
                    ch[k] = ' '; bg[k] = b;
                }
        }

        public void Box(int x, int y, int w, int h, int col, string title = null, int fill = -1)
        {
            if (w < 4 || h < 2) return;
            if (fill >= 0) Fill(x, y, w, h, fill);
            int b = fill;
            Put(x, y, "┌" + new string('─', w - 2) + "┐", col, b);
            for (int yy = y + 1; yy < y + h - 1; yy++) { Put(x, yy, "│", col, b); Put(x + w - 1, yy, "│", col, b); }
            Put(x, y + h - 1, "└" + new string('─', w - 2) + "┘", col, b);
            if (!string.IsNullOrEmpty(title))
            {
                string t = Clip(title, w - 8);
                Put(x + 1, y, "─ ", col, b);
                Put(x + 3, y, t, col, b);
                Put(x + 3 + t.Length, y, " ", col, b);
            }
        }

        /// <summary>Keybinding chip: key in an accent block, label dim.</summary>
        public int Key(int x, int y, string key, string label, int accent)
        {
            Put(x, y, " " + key + " ", T.Dark, accent);
            Put(x + key.Length + 2, y, " " + label + "  ", T.Dim);
            return x + key.Length + label.Length + 5;
        }

        public void Flush()
        {
            var sb = new StringBuilder(W * H * 6);
            int cf = -1, cb = -1;
            for (int y = 0; y < H; y++)
            {
                sb.Append("\x1b[").Append(y + 1).Append(";1H");
                int lim = y == H - 1 ? W - 1 : W;   // never touch the last cell: avoids scrolling
                for (int x = 0; x < lim; x++)
                {
                    int k = y * W + x;
                    if (fg[k] != cf) { cf = fg[k]; sb.Append("\x1b[38;2;").Append((cf >> 16) & 255).Append(';').Append((cf >> 8) & 255).Append(';').Append(cf & 255).Append('m'); }
                    if (bg[k] != cb) { cb = bg[k]; sb.Append("\x1b[48;2;").Append((cb >> 16) & 255).Append(';').Append((cb >> 8) & 255).Append(';').Append(cb & 255).Append('m'); }
                    sb.Append(ch[k]);
                }
            }
            Raw(sb.ToString());
        }

        public static string Clip(string s, int w)
        {
            if (w <= 0) return "";
            if (s.Length <= w) return s;
            if (w == 1) return "…";
            return s.Substring(0, w - 1) + "…";
        }

        /// <summary>Clips from the left, keeping the end of a path visible.</summary>
        public static string ClipLeft(string s, int w)
        {
            if (w <= 0) return "";
            if (s.Length <= w) return s;
            if (w == 1) return "…";
            return "…" + s.Substring(s.Length - (w - 1));
        }

        public static string PadR(string s, int w) { s = Clip(s, w); return s + new string(' ', Math.Max(0, w - s.Length)); }
        public static string PadL(string s, int w) { s = Clip(s, w); return new string(' ', Math.Max(0, w - s.Length)) + s; }

        /// <summary>Smooth block meter using ▏▎▍▌▋▊▉█.</summary>
        public void Meter(int x, int y, int w, double frac, Func<double, int> grad, int emptyCol = 0x2a2440)
        {
            const string parts = " ▏▎▍▌▋▊▉█";
            if (frac < 0) frac = 0; if (frac > 1) frac = 1;
            double cells = frac * w;
            for (int i = 0; i < w; i++)
            {
                double fillAmt = cells - i;
                int col = grad((double)i / Math.Max(1, w - 1));
                if (fillAmt >= 1) Put(x + i, y, "█", col);
                else if (fillAmt > 0) Put(x + i, y, parts[(int)(fillAmt * 8)].ToString(), col);
                else Put(x + i, y, "░", emptyCol);
            }
        }
    }
}
