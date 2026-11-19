using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Undelete.Gui
{
    /// <summary>Palette as GDI colours, fonts and DPI scaling.</summary>
    static class N
    {
        public static Color C(int rgb) { return Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255); }
        public static readonly Color Bg = C(T.Bg), Panel = C(T.Panel), PanelAlt = C(T.PanelAlt), Text = C(T.Text), Dim = C(T.Dim);
        public static readonly Color Crimson = C(T.Crimson), Teal = C(T.Teal), Magenta = C(T.Magenta), Purple = C(T.Purple);
        public static readonly Color Blue = C(T.Blue), Lime = C(T.Lime), Orange = C(T.Orange), Dark = C(T.Dark);
        public static readonly Color Track = C(0x2a2440);

        public static float Scale = 1f;
        public static int S(int px) { return (int)Math.Round(px * Scale); }
        public static float Sf(float px) { return px * Scale; }

        static string family;
        static readonly Dictionary<string, Font> cache = new Dictionary<string, Font>();

        public static Font Mono(float pt, FontStyle st = FontStyle.Regular)
        {
            if (family == null)
            {
                var installed = new InstalledFontCollection().Families.Select(ff => ff.Name).ToList();
                family = new[] { "Cascadia Mono", "JetBrains Mono", "DM Mono", "Consolas", "Courier New" }.First(n => n == "Courier New" || installed.Contains(n));
            }
            string key = pt + "|" + st;
            Font f;
            if (!cache.TryGetValue(key, out f)) cache[key] = f = new Font(family, pt, st, GraphicsUnit.Point);
            return f;
        }

        public static Color Mix(Color a, Color b, double t)
        {
            if (t < 0) t = 0; if (t > 1) t = 1;
            return Color.FromArgb(255, (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        public static Color Alpha(Color c, int a) { return Color.FromArgb(a, c.R, c.G, c.B); }

        public static ColorBlend Blend(params Color[] cols)
        {
            var b = new ColorBlend(cols.Length);
            b.Colors = cols;
            b.Positions = Enumerable.Range(0, cols.Length).Select(i => (float)i / (cols.Length - 1)).ToArray();
            return b;
        }

        public static readonly ColorBlend Heat = Blend(Crimson, Magenta, Purple);
        public static readonly ColorBlend Aurora = Blend(Purple, Magenta, Teal);

        public const TextFormatFlags Left = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
                                            TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
        public const TextFormatFlags Right = (Left & ~TextFormatFlags.Left) | TextFormatFlags.Right;
        public const TextFormatFlags Center = (Left & ~TextFormatFlags.Left) | TextFormatFlags.HorizontalCenter;
        public const TextFormatFlags PathLeft = (Left & ~TextFormatFlags.EndEllipsis) | TextFormatFlags.PathEllipsis;

        public static void Draw(Graphics g, string s, Font f, Rectangle r, Color c, TextFormatFlags fl = Left)
        {
            TextRenderer.DrawText(g, s, f, r, c, fl);
        }

        public static int Width(string s, Font f)
        {
            return TextRenderer.MeasureText(s, f, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();

        /// <summary>Dark title bar and (Windows 11) a border tinted with the accent.</summary>
        public static void DarkTitleBar(Form f, Color border)
        {
            try
            {
                int on = 1;
                DwmSetWindowAttribute(f.Handle, 20, ref on, 4);                    // DWMWA_USE_IMMERSIVE_DARK_MODE
                int cap = (Bg.B << 16) | (Bg.G << 8) | Bg.R;
                DwmSetWindowAttribute(f.Handle, 35, ref cap, 4);                   // DWMWA_CAPTION_COLOR
                int bd = (border.B << 16) | (border.G << 8) | border.R;
                DwmSetWindowAttribute(f.Handle, 34, ref bd, 4);                    // DWMWA_BORDER_COLOR
                int txt = (Text.B << 16) | (Text.G << 8) | Text.R;
                DwmSetWindowAttribute(f.Handle, 36, ref txt, 4);                   // DWMWA_TEXT_COLOR
            }
            catch { }
        }
    }

    /// <summary>Base for custom-painted controls.</summary>
    class NeonControl : Control
    {
        public NeonControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = N.Panel;
            ForeColor = N.Text;
            Font = N.Mono(9.5f);
        }
    }

    /// <summary>Thin outlined box with its title cut into the top border:  ┌─ title ───┐</summary>
    class NeonPanel : Panel
    {
        public Color Accent = N.Crimson;
        string title = "", right = "";
        public Color RightColor = N.Orange;

        public string Title { get { return title; } set { title = value ?? ""; Invalidate(); } }
        public string RightTitle { get { return right; } set { if (value != right) { right = value ?? ""; Invalidate(new Rectangle(0, 0, Width, N.S(20))); } } }

        public NeonPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = N.Panel;
            Padding = new Padding(N.S(12), N.S(20), N.S(12), N.S(10));
        }

        public Rectangle Inner { get { return DisplayRectangle; } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            int ty = N.S(8);
            g.Clear(N.Bg);
            var box = new Rectangle(0, ty, Width - 1, Height - ty - 1);
            using (var b = new SolidBrush(N.Panel)) g.FillRectangle(b, box);
            // faint outer glow + crisp 1px border
            using (var p = new Pen(N.Alpha(Accent, 50), N.Sf(3))) g.DrawRectangle(p, box);
            using (var p = new Pen(Accent, 1)) g.DrawRectangle(p, box);

            var f = N.Mono(9f);
            if (title.Length > 0)
            {
                string t = "─ " + title + " ";
                int w = N.Width(t, f);
                var tr = new Rectangle(N.S(10), 0, w + N.S(2), ty * 2 + 1);
                using (var b = new SolidBrush(N.Panel)) g.FillRectangle(b, new Rectangle(tr.X, ty - N.S(2), tr.Width, N.S(5)));
                N.Draw(g, t, f, tr, Accent);
            }
            if (right.Length > 0)
            {
                string t = " " + right + " ";
                int w = N.Width(t, f);
                var tr = new Rectangle(Width - w - N.S(12), 0, w + N.S(2), ty * 2 + 1);
                using (var b = new SolidBrush(N.Panel)) g.FillRectangle(b, new Rectangle(tr.X, ty - N.S(2), tr.Width, N.S(5)));
                N.Draw(g, t, f, tr, RightColor);
            }
        }
    }

    /// <summary>[ text ] button; hover or primary inverts (accent fill, dark text).</summary>
    class NeonButton : NeonControl
    {
        public Color Accent = N.Teal;
        public bool Primary;
        public bool Checked;
        bool hover, down;

        public NeonButton(string text, Color accent, bool primary = false)
        {
            Text = text; Accent = accent; Primary = primary;
            Cursor = Cursors.Hand;
            TabStop = false;
            Height = N.S(30);
            Width = PreferredWidth;
        }

        public int PreferredWidth { get { return N.Width("[ " + Text + " ]", Font) + N.S(18); } }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : N.Panel);
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            Color acc = Enabled ? Accent : N.Dim;
            bool inv = Enabled && (hover || Primary || Checked);
            Color fill = inv ? (down ? N.Mix(acc, Color.White, 0.25) : hover && (Primary || Checked) ? N.Mix(acc, Color.White, 0.15) : acc) : N.Panel;
            using (var b = new SolidBrush(fill)) g.FillRectangle(b, r);
            if (inv) using (var p = new Pen(N.Alpha(acc, 70), N.Sf(4))) g.DrawRectangle(p, r);
            using (var p = new Pen(acc)) g.DrawRectangle(p, r);
            N.Draw(g, "[ " + Text + " ]", Font, ClientRectangle, inv ? N.Dark : acc, N.Center);
        }
    }

    /// <summary>[ all │ files │ folders ] segmented toggle.</summary>
    class Segmented : NeonControl
    {
        public string[] Options;
        int selected, hover = -1;
        public Color Accent = N.Magenta;
        public event Action Changed;

        public int Selected { get { return selected; } set { selected = value; Invalidate(); } }

        public Segmented(Color accent, params string[] options)
        {
            Options = options; Accent = accent;
            Height = N.S(28);
            Cursor = Cursors.Hand;
        }

        int SegAt(int x) { return Math.Max(0, Math.Min(Options.Length - 1, x * Options.Length / Math.Max(1, Width))); }
        protected override void OnMouseMove(MouseEventArgs e) { int h = SegAt(e.X); if (h != hover) { hover = h; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            int s = SegAt(e.X);
            if (s != selected) { selected = s; Invalidate(); if (Changed != null) Changed(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(N.Panel);
            int n = Options.Length;
            for (int i = 0; i < n; i++)
            {
                int x0 = i * Width / n, x1 = (i + 1) * Width / n;
                var r = new Rectangle(x0, 0, x1 - x0, Height - 1);
                bool on = i == selected;
                if (on) using (var b = new SolidBrush(Accent)) g.FillRectangle(b, r);
                else if (i == hover) using (var b = new SolidBrush(N.PanelAlt)) g.FillRectangle(b, r);
                N.Draw(g, Options[i], N.Mono(9f), r, on ? N.Dark : (i == hover ? N.Text : N.Dim), N.Center);
                if (i > 0 && !on && i - 1 != selected) using (var p = new Pen(N.Alpha(Accent, 90))) g.DrawLine(p, x0, N.S(6), x0, Height - N.S(7));
            }
            using (var p = new Pen(Accent)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        }
    }

    /// <summary>Segmented block meter ▮▮▮▮▯▯ filled with a gradient.</summary>
    class NeonMeter : NeonControl
    {
        double value;
        public ColorBlend Gradient = N.Heat;
        public string Label = "";
        public double Value { get { return value; } set { double v = Math.Max(0, Math.Min(1, value)); if (Math.Abs(v - this.value) > 0.0005) { this.value = v; Invalidate(); } } }

        public NeonMeter() { Height = N.S(16); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : N.Panel);
            int labelW = Label.Length > 0 ? N.Width(Label, Font) + N.S(10) : 0;
            var bar = new Rectangle(0, 0, Width - labelW, Height);
            PaintBar(g, bar, value, Gradient);
            if (labelW > 0) N.Draw(g, Label, Font, new Rectangle(Width - labelW + N.S(8), 0, labelW, Height), N.Lime);
        }

        public static void PaintBar(Graphics g, Rectangle bar, double value, ColorBlend grad)
        {
            int cell = N.S(7), gap = Math.Max(1, N.S(2));
            using (var empty = new SolidBrush(N.Track))
            using (var lg = new LinearGradientBrush(new Rectangle(bar.X - 1, bar.Y, bar.Width + 2, bar.Height), N.Crimson, N.Purple, 0f))
            {
                lg.InterpolationColors = grad;
                int cells = Math.Max(1, (bar.Width + gap) / (cell + gap));
                int full = (int)Math.Round(value * cells);
                for (int i = 0; i < cells; i++)
                {
                    var r = new Rectangle(bar.X + i * (cell + gap), bar.Y, cell, bar.Height);
                    g.FillRectangle(i < full ? (Brush)lg : empty, r);
                }
            }
        }
    }

    /// <summary>The ANSI-shadow block logo painted with a slowly shimmering crimson→magenta→purple gradient.</summary>
    class Logo : NeonControl
    {
        static readonly string[] Lines =
        {
            "██╗   ██╗███╗   ██╗██████╗ ███████╗██╗     ███████╗████████╗███████╗",
            "██║   ██║████╗  ██║██╔══██╗██╔════╝██║     ██╔════╝╚══██╔══╝██╔════╝",
            "██║   ██║██╔██╗ ██║██║  ██║█████╗  ██║     █████╗     ██║   █████╗  ",
            "██║   ██║██║╚██╗██║██║  ██║██╔══╝  ██║     ██╔══╝     ██║   ██╔══╝  ",
            "╚██████╔╝██║ ╚████║██████╔╝███████╗███████╗███████╗   ██║   ███████╗",
            " ╚═════╝ ╚═╝  ╚═══╝╚═════╝ ╚══════╝╚══════╝╚══════╝   ╚═╝   ╚══════╝",
        };
        float phase;
        readonly Timer t = new Timer { Interval = 60 };
        public string Subtitle = "";

        public Logo()
        {
            BackColor = N.Bg;
            t.Tick += (s, e) => { phase = (phase + 0.004f) % 2f; Invalidate(); };
            if (!SystemInformation.UIEffectsEnabled) return;   // reduced-motion setting
            t.Start();
        }

        protected override void Dispose(bool disposing) { if (disposing) t.Dispose(); base.Dispose(disposing); }

        /// <summary>
        /// Paints the ANSI-shadow letters cell by cell: '█' becomes a solid gradient block,
        /// the box-drawing "shadow" characters become thin lines, so the logo stays crisp at any size.
        /// </summary>
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(N.Bg);
            int rows = Lines.Length, cols = Lines[0].Length;
            int ch = Math.Max(4, (Height - N.S(26)) / rows);
            int cw = Math.Max(3, (int)Math.Round(ch * 0.62));
            if (cw * cols > Width - N.S(4)) { cw = Math.Max(2, (Width - N.S(4)) / cols); ch = (int)(cw / 0.62); }
            int ox = N.S(2), oy = N.S(4);
            float w = cw * cols;

            var rect = new RectangleF(ox - w * phase, 0, w * 2, Height);
            using (var br = new LinearGradientBrush(rect, N.Crimson, N.Purple, 0f))
            using (var shadow = new Pen(N.Alpha(N.Purple, 150), Math.Max(1f, cw / 4f)))
            using (var glow = new SolidBrush(N.Alpha(N.Magenta, 28)))
            {
                br.InterpolationColors = N.Blend(N.Crimson, N.Magenta, N.Purple, N.Magenta, N.Crimson);
                br.WrapMode = WrapMode.TileFlipX;
                for (int r = 0; r < rows; r++)
                    for (int c = 0; c < cols; c++)
                    {
                        char k = Lines[r][c];
                        if (k == ' ') continue;
                        int x = ox + c * cw, y = oy + r * ch;
                        if (k == '█')
                        {
                            g.FillRectangle(glow, x - 1, y - 1, cw + 2, ch + 2);
                            g.FillRectangle(br, x, y, cw, ch);
                            continue;
                        }
                        // shadow strokes, drawn through the cell centre
                        float mx = x + cw / 2f, my = y + ch / 2f;
                        bool left = "═╗╝".IndexOf(k) >= 0, right = "═╔╚".IndexOf(k) >= 0;
                        bool up = "║╝╚".IndexOf(k) >= 0, down = "║╗╔".IndexOf(k) >= 0;
                        if (left) g.DrawLine(shadow, x, my, mx, my);
                        if (right) g.DrawLine(shadow, mx, my, x + cw, my);
                        if (up) g.DrawLine(shadow, mx, y, mx, my);
                        if (down) g.DrawLine(shadow, mx, my, mx, y + ch);
                    }
            }
            N.Draw(g, Subtitle, N.Mono(8.5f), new Rectangle(N.S(4), oy + rows * ch + N.S(6), Width - N.S(4), N.S(16)), N.Dim);
        }
    }

    /// <summary>Vim-style status line: mode badge │ text │ clock.</summary>
    class StatusLine : NeonControl
    {
        public string Mode = "READY", Middle = "";
        public Color ModeColor = N.Lime;
        readonly Timer t = new Timer { Interval = 1000 };

        public StatusLine()
        {
            BackColor = N.Panel;
            Font = N.Mono(9f);
            t.Tick += (s, e) => Invalidate();
            t.Start();
        }

        public void Set(string mode, Color col, string middle)
        {
            if (mode == Mode && col == ModeColor && middle == Middle) return;
            Mode = mode; ModeColor = col; Middle = middle; Invalidate();
        }

        protected override void Dispose(bool disposing) { if (disposing) t.Dispose(); base.Dispose(disposing); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(N.Panel);
            string badge = " " + Mode + " ";
            int bw = N.Width(badge, Font) + N.S(8);
            using (var b = new SolidBrush(ModeColor)) g.FillRectangle(b, 0, 0, bw, Height);
            N.Draw(g, badge, N.Mono(9f, FontStyle.Bold), new Rectangle(0, 0, bw, Height), N.Dark, N.Center);
            // chevron edge
            using (var b = new SolidBrush(ModeColor))
                g.FillPolygon(b, new[] { new Point(bw, 0), new Point(bw + Height / 2, Height / 2), new Point(bw, Height) });

            string clock = " " + DateTime.Now.ToString("HH:mm:ss") + " ";
            int cw = N.Width(clock, Font) + N.S(10);
            using (var b = new SolidBrush(N.Purple)) g.FillRectangle(b, Width - cw, 0, cw, Height);
            using (var b = new SolidBrush(N.Purple))
                g.FillPolygon(b, new[] { new Point(Width - cw, 0), new Point(Width - cw - Height / 2, Height / 2), new Point(Width - cw, Height) });
            N.Draw(g, clock, Font, new Rectangle(Width - cw, 0, cw, Height), N.Dark, N.Center);

            N.Draw(g, Middle, Font, new Rectangle(bw + Height / 2 + N.S(10), 0, Width - bw - cw - Height - N.S(20), Height), N.Text);
        }
    }

    /// <summary>Dark text box with an accent border and a "›" prompt.</summary>
    class NeonTextBox : NeonControl
    {
        public readonly TextBox Box = new TextBox();
        public Color Accent = N.Magenta;
        string placeholder = "";

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);

        /// <summary>Native cue banner (hint text shown while empty).</summary>
        public string Placeholder
        {
            get { return placeholder; }
            set { placeholder = value ?? ""; if (Box.IsHandleCreated) SendMessage(Box.Handle, 0x1501, (IntPtr)1, placeholder); }
        }

        public NeonTextBox(Color accent)
        {
            Accent = accent;
            BackColor = N.PanelAlt;
            Height = N.S(30);
            Box.BorderStyle = BorderStyle.None;
            Box.BackColor = N.PanelAlt;
            Box.ForeColor = N.Text;
            Box.Font = N.Mono(10f);
            Controls.Add(Box);
            Box.GotFocus += (s, e) => Invalidate();
            Box.LostFocus += (s, e) => Invalidate();
            Box.TextChanged += (s, e) => Invalidate();
            Box.HandleCreated += (s, e) => SendMessage(Box.Handle, 0x1501, (IntPtr)1, placeholder);
        }

        public override string Text { get { return Box.Text; } set { Box.Text = value; } }

        protected override void OnLayout(LayoutEventArgs e)
        {
            int px = N.S(22);
            Box.SetBounds(px, (Height - Box.PreferredHeight) / 2 + 1, Width - px - N.S(8), Box.PreferredHeight);
        }

        protected override void OnClick(EventArgs e) { Box.Focus(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(N.PanelAlt);
            bool focus = Box.Focused;
            using (var p = new Pen(focus ? Accent : N.Alpha(Accent, 120))) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            N.Draw(g, "›", N.Mono(11f, FontStyle.Bold), new Rectangle(N.S(7), 0, N.S(14), Height), Accent);
        }
    }

    /// <summary>Borderless dark dialog drawn like a neon panel.</summary>
    class DarkDialog : Form
    {
        int result = -1;
        readonly Color accent;
        readonly string titleText;

        DarkDialog(string title, string text, Color accent, string[] buttons)
        {
            this.accent = accent;
            titleText = title;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = N.Panel;
            ShowInTaskbar = false;
            KeyPreview = true;
            DoubleBuffered = true;
            Font = N.Mono(9.5f);

            int w = N.S(560);
            var body = new Label
            {
                Text = text, ForeColor = N.Text, BackColor = N.Panel, Font = N.Mono(9.5f),
                Location = new Point(N.S(22), N.S(40)), MaximumSize = new Size(w - N.S(44), 0), AutoSize = true, UseMnemonic = false
            };
            Controls.Add(body);
            int by = body.Bottom + N.S(22);
            int x = w - N.S(22);
            for (int i = buttons.Length - 1; i >= 0; i--)
            {
                int idx = i;
                var b = new NeonButton(buttons[i], i == 0 ? accent : N.Dim, i == 0);
                x -= b.Width;
                b.Location = new Point(x, by);
                b.Click += (s, e) => { result = idx; Close(); };
                Controls.Add(b);
                x -= N.S(10);
            }
            ClientSize = new Size(w, by + N.S(30) + N.S(20));
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { result = -1; Close(); }
            if (e.KeyCode == Keys.Enter) { result = 0; Close(); }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(N.Panel);
            using (var p = new Pen(accent)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            using (var p = new Pen(N.Alpha(accent, 60), N.Sf(4))) g.DrawRectangle(p, 1, 1, Width - 3, Height - 3);
            N.Draw(g, "─ " + titleText + " ", N.Mono(10f, FontStyle.Bold), new Rectangle(N.S(14), N.S(8), Width - N.S(28), N.S(22)), accent);
        }

        // drag by the body
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); }
        }
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        public static int Show(IWin32Window owner, string title, string text, Color accent, params string[] buttons)
        {
            if (buttons.Length == 0) buttons = new[] { "ok" };
            using (var d = new DarkDialog(title, text, accent, buttons))
            {
                d.ShowDialog(owner);
                return d.result;
            }
        }
    }
}
