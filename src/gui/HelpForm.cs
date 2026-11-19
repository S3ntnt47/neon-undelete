using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Undelete.Gui
{
    /// <summary>Topic picker on the left of the help window.</summary>
    sealed class TopicList : NeonControl
    {
        public List<HelpTopic> Topics;
        int selected, hover = -1;
        public event Action Changed;
        public int Selected { get { return selected; } set { selected = Math.Max(0, Math.Min(Topics.Count - 1, value)); Invalidate(); if (Changed != null) Changed(); } }

        public TopicList(List<HelpTopic> topics) { Topics = topics; Cursor = Cursors.Hand; TabStop = true; }

        int RowH { get { return N.S(32); } }
        protected override void OnMouseMove(MouseEventArgs e) { int h = e.Y / RowH; if (h != hover) { hover = h; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { int s = e.Y / RowH; if (s < Topics.Count) Selected = s; Focus(); }
        protected override bool IsInputKey(Keys k) { return (k & Keys.KeyCode) == Keys.Up || (k & Keys.KeyCode) == Keys.Down || base.IsInputKey(k); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Up) Selected = selected - 1;
            if (e.KeyCode == Keys.Down) Selected = selected + 1;
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(N.Panel);
            for (int i = 0; i < Topics.Count; i++)
            {
                var r = new Rectangle(0, i * RowH, Width, RowH - N.S(4));
                bool on = i == selected;
                if (on) using (var b = new SolidBrush(N.Lime)) g.FillRectangle(b, r);
                else if (i == hover) using (var b = new SolidBrush(N.PanelAlt)) g.FillRectangle(b, r);
                N.Draw(g, (on ? "▸ " : "  ") + Topics[i].Title, Font, new Rectangle(r.X + N.S(6), r.Y, r.Width - N.S(8), r.Height), on ? N.Dark : N.Text);
            }
        }
    }

    /// <summary>Scrollable, word-wrapped rendering of one help topic.</summary>
    sealed class HelpView : NeonControl
    {
        HelpTopic topic;
        int scroll, contentH;
        public HelpTopic Topic { get { return topic; } set { topic = value; scroll = 0; Invalidate(); } }

        public HelpView() { TabStop = true; Font = N.Mono(10f); }

        const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl;

        int Measure(string s, Font f, int w) { return TextRenderer.MeasureText(s, f, new Size(Math.Max(10, w), int.MaxValue), Wrap).Height; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(N.Panel);
            if (topic == null) return;
            int w = Width - N.S(16);
            int y = -scroll;
            var hf = N.Mono(11f, FontStyle.Bold);
            var kf = N.Mono(9f, FontStyle.Bold);
            int keyCol = N.S(200);
            Color[] accents = { N.Crimson, N.Teal, N.Magenta, N.Purple, N.Blue };
            int h = 0;

            foreach (var b in topic.Blocks)
            {
                switch (b.Kind)
                {
                    case 'h':
                        {
                            y += N.S(10);
                            Color c = accents[h++ % accents.Length];
                            string t = "── " + b.A + " ";
                            int th = Measure(t, hf, w);
                            TextRenderer.DrawText(g, t, hf, new Rectangle(0, y, w, th), c, Wrap);
                            int tw = N.Width(t, hf);
                            using (var p = new Pen(N.Alpha(c, 90))) g.DrawLine(p, tw + N.S(4), y + th / 2, w, y + th / 2);
                            y += th + N.S(8);
                            break;
                        }
                    case 'p':
                        {
                            int th = Measure(b.A, Font, w);
                            TextRenderer.DrawText(g, b.A, Font, new Rectangle(0, y, w, th), N.Text, Wrap);
                            y += th + N.S(10);
                            break;
                        }
                    case 'l':
                        {
                            int ind = N.S(22);
                            int th = Measure(b.A, Font, w - ind);
                            N.Draw(g, "▸", Font, new Rectangle(N.S(4), y, ind, N.S(20)), N.Magenta);
                            TextRenderer.DrawText(g, b.A, Font, new Rectangle(ind, y, w - ind, th), N.Text, Wrap);
                            y += th + N.S(7);
                            break;
                        }
                    case 'w':
                        {
                            int ind = N.S(26);
                            int th = Measure(b.A, Font, w - ind - N.S(10));
                            var r = new Rectangle(0, y - N.S(4), w, th + N.S(10));
                            using (var br = new SolidBrush(N.Mix(N.Panel, N.Orange, 0.10))) g.FillRectangle(br, r);
                            using (var br = new SolidBrush(N.Orange)) g.FillRectangle(br, 0, r.Y, N.S(3), r.Height);
                            N.Draw(g, "▲", Font, new Rectangle(N.S(9), y, ind, N.S(20)), N.Orange);
                            TextRenderer.DrawText(g, b.A, Font, new Rectangle(ind, y, w - ind - N.S(10), th), N.Orange, Wrap);
                            y += th + N.S(14);
                            break;
                        }
                    case 'k':
                        {
                            int kw = Math.Min(keyCol - N.S(14), N.Width(" " + b.A + " ", kf) + N.S(8));
                            int th = Math.Max(N.S(22), Measure(b.B, Font, w - keyCol));
                            var kr = new Rectangle(0, y, kw, N.S(22));
                            using (var br = new SolidBrush(N.Teal)) g.FillRectangle(br, kr);
                            N.Draw(g, b.A, kf, kr, N.Dark, N.Center);
                            TextRenderer.DrawText(g, b.B, Font, new Rectangle(keyCol, y + N.S(2), w - keyCol, th), N.Text, Wrap);
                            y += th + N.S(8);
                            break;
                        }
                }
            }
            contentH = y + scroll + N.S(10);

            if (contentH > Height)
            {
                int track = Height, th = Math.Max(N.S(30), track * Height / contentH);
                int ty = (int)((long)(track - th) * scroll / Math.Max(1, contentH - Height));
                using (var br = new SolidBrush(N.Bg)) g.FillRectangle(br, Width - N.S(6), 0, N.S(6), Height);
                using (var br = new SolidBrush(N.Magenta)) g.FillRectangle(br, Width - N.S(5), ty, N.S(4), th);
            }
        }

        void ScrollBy(int d)
        {
            int max = Math.Max(0, contentH - Height);
            int s = Math.Max(0, Math.Min(max, scroll + d));
            if (s != scroll) { scroll = s; Invalidate(); }
        }

        protected override void OnMouseWheel(MouseEventArgs e) { ScrollBy(-Math.Sign(e.Delta) * N.S(60)); }
        protected override void OnMouseDown(MouseEventArgs e) { Focus(); }
        protected override bool IsInputKey(Keys k)
        {
            var c = k & Keys.KeyCode;
            return c == Keys.Up || c == Keys.Down || c == Keys.PageUp || c == Keys.PageDown || c == Keys.Home || c == Keys.End || base.IsInputKey(k);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Up: ScrollBy(-N.S(40)); break;
                case Keys.Down: ScrollBy(N.S(40)); break;
                case Keys.PageUp: ScrollBy(-Height + N.S(40)); break;
                case Keys.PageDown: ScrollBy(Height - N.S(40)); break;
                case Keys.Home: ScrollBy(-contentH); break;
                case Keys.End: ScrollBy(contentH); break;
            }
            base.OnKeyDown(e);
        }
    }

    sealed class HelpForm : Form
    {
        readonly NeonPanel navPanel = new NeonPanel { Title = "help", Accent = N.Magenta };
        readonly NeonPanel bodyPanel = new NeonPanel { Accent = N.Teal };
        readonly TopicList nav;
        readonly HelpView view = new HelpView();
        readonly NeonButton closeBtn = new NeonButton("close", N.Crimson, true);

        public HelpForm(int startTopic = 0)
        {
            var topics = HelpContent.Topics(true);
            nav = new TopicList(topics);
            Text = "Neon Undelete help";
            BackColor = N.Bg; ForeColor = N.Text; Font = N.Mono(9.5f);
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(N.S(1000), N.S(680));
            MinimumSize = new Size(N.S(720), N.S(460));
            KeyPreview = true;
            ShowInTaskbar = false;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            navPanel.Controls.Add(nav);
            bodyPanel.Controls.Add(view);
            Controls.AddRange(new Control[] { navPanel, bodyPanel, closeBtn });
            nav.Changed += () => { view.Topic = topics[nav.Selected]; bodyPanel.Title = topics[nav.Selected].Title; };
            closeBtn.Click += (s, e) => Close();
            Resize += (s, e) => DoLayout();
            nav.Selected = Math.Max(0, Math.Min(topics.Count - 1, startTopic));
            DoLayout();
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); N.DarkTitleBar(this, N.Magenta); }
        protected override void OnShown(EventArgs e) { base.OnShown(e); view.Focus(); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.F1) { Close(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.PageDown) { nav.Selected = nav.Selected + 1; e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.PageUp) { nav.Selected = nav.Selected - 1; e.Handled = true; }
            base.OnKeyDown(e);
        }

        void DoLayout()
        {
            int W = ClientSize.Width, H = ClientSize.Height, m = N.S(12), gap = N.S(10), navW = N.S(260);
            int bottom = H - m - N.S(30) - gap;
            navPanel.SetBounds(m, m, navW, bottom - m);
            var ni = navPanel.Inner;
            nav.SetBounds(ni.X, ni.Y + N.S(4), ni.Width, ni.Height - N.S(4));
            bodyPanel.SetBounds(m + navW + gap, m, W - 2 * m - navW - gap, bottom - m);
            var bi = bodyPanel.Inner;
            view.SetBounds(bi.X, bi.Y + N.S(6), bi.Width, bi.Height - N.S(6));
            closeBtn.Width = N.S(120);
            closeBtn.Location = new Point(W - m - closeBtn.Width, H - m - N.S(30));
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            N.Draw(e.Graphics, "wheel / ↑↓ scroll  ·  ctrl+pgup/pgdn change topic  ·  esc close", N.Mono(8.5f),
                   new Rectangle(N.S(14), closeBtn.Top, closeBtn.Left - N.S(24), closeBtn.Height), N.Dim);
        }
    }
}
