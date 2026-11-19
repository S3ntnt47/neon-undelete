using System;
using System.Drawing;
using System.Windows.Forms;

namespace Undelete.Gui
{
    static class GuiProgram
    {
        [STAThread]
        static void Main(string[] args)
        {
            N.SetProcessDPIAware();
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) N.Scale = g.DpiX / 96f;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) =>
                DarkDialog.Show(null, "unexpected error", e.Exception.Message + "\n\n" + e.Exception.StackTrace, N.Crimson);
            if (args.Length > 0 && (args[0] == "--help" || args[0] == "/?" || args[0] == "-h"))
            {
                using (var h = new HelpForm()) { h.StartPosition = FormStartPosition.CenterScreen; h.ShowInTaskbar = true; Application.Run(h); }
                return;
            }
            Application.Run(new MainForm(args.Length > 0 ? args[0] : null));
        }
    }
}
