using System;

namespace Undelete
{
    /// <summary>MarcusUI / Aetheria palette, shared by the terminal and windowed front-ends.</summary>
    static class T
    {
        public const int Bg = 0x0c0a17, Panel = 0x13101f, PanelAlt = 0x1b1630, Text = 0xcfc8e0, Dim = 0x6e6688;
        public const int Crimson = 0xe8194f, Teal = 0x1fc2b0, Magenta = 0xd63fa0, Purple = 0x9a4fd0;
        public const int Blue = 0x4b7fe0, Lime = 0xc9dc4a, Orange = 0xf2653a, Dark = 0x0c0a17;

        // semantic colours (never themed)
        public const int Good = Teal, Warn = Orange, Bad = Crimson;

        public static int Lerp(int a, int b, double t)
        {
            if (t < 0) t = 0; if (t > 1) t = 1;
            int r = (int)(((a >> 16) & 255) + (((b >> 16) & 255) - ((a >> 16) & 255)) * t);
            int g = (int)(((a >> 8) & 255) + (((b >> 8) & 255) - ((a >> 8) & 255)) * t);
            int bl = (int)((a & 255) + ((b & 255) - (a & 255)) * t);
            return (r << 16) | (g << 8) | bl;
        }

        /// <summary>crimson → magenta → purple</summary>
        public static int Heat(double t) { return t < 0.5 ? Lerp(Crimson, Magenta, t * 2) : Lerp(Magenta, Purple, (t - 0.5) * 2); }
        /// <summary>purple → magenta → teal ("aurora")</summary>
        public static int Aurora(double t) { return t < 0.5 ? Lerp(Purple, Magenta, t * 2) : Lerp(Magenta, Teal, (t - 0.5) * 2); }
    }

    static class Fmt
    {
        public static string Bytes(long b)
        {
            string[] u = { "B", "KB", "MB", "GB", "TB", "PB" };
            double v = b; int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return i == 0 ? b + " B" : v.ToString(v >= 100 ? "0" : v >= 10 ? "0.0" : "0.00") + " " + u[i];
        }
        public static string N(long n) { return n.ToString("N0"); }
        public static string Date(DateTime d) { return d == DateTime.MinValue ? "-" : d.ToLocalTime().ToString("yyyy-MM-dd HH:mm"); }
    }

    /// <summary>Filtering and sorting shared by both front-ends.</summary>
    static class ItemQuery
    {
        public static readonly string[] TypeNames = { "all", "files", "folders" };
        public static readonly string[] ChanceNames = { "any", "recoverable", "excellent" };

        public static int ChanceRank(DeletedItem i)
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

        public static bool Matches(DeletedItem i, int typeMode, int chanceMode, bool hideEmpty, string filter)
        {
            if (typeMode == 1 && i.IsDir) return false;
            if (typeMode == 2 && !i.IsDir) return false;
            if (hideEmpty && !i.IsDir && i.Size == 0) return false;
            if (chanceMode == 1 && !(i.Chance == Chance.Excellent || i.Chance == Chance.Partial || i.Chance == Chance.Folder || i.Chance == Chance.Unknown)) return false;
            if (chanceMode == 2 && !(i.Chance == Chance.Excellent || i.Chance == Chance.Folder)) return false;
            if (!string.IsNullOrEmpty(filter) && i.FullPath.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) return false;
            return true;
        }

        public static string Describe(DeletedItem it, out int col)
        {
            switch (it.Chance)
            {
                case Chance.Excellent: col = T.Good; return "excellent - none of its clusters have been reused";
                case Chance.Partial:
                    col = T.Warn;
                    return "partial - " + Fmt.N(it.ClustersInUse) + " of " + Fmt.N(it.Clusters) + " clusters now belong to other files; expect corruption";
                case Chance.Overwritten: col = T.Bad; return "overwritten - its space has been reused by other files";
                case Chance.Zeroed: col = T.Bad; return "zeroed - space reads back as zeros (SSD TRIM or never flushed); content is most likely gone";
                case Chance.Unsupported: col = T.Dim; return "unsupported - NTFS-compressed or EFS-encrypted data";
                case Chance.Folder: col = T.Blue; return "folder - recovering it restores every deleted item inside";
                default: col = T.Dim; return "unknown - cluster bitmap unavailable";
            }
        }

        public static int CountDescendants(DeletedItem d)
        {
            if (d.Children == null) return 0;
            int n = 0;
            var stack = new System.Collections.Generic.Stack<DeletedItem>(d.Children);
            var seen = new System.Collections.Generic.HashSet<DeletedItem>();
            while (stack.Count > 0)
            {
                var c = stack.Pop();
                if (!seen.Add(c)) continue;
                n++;
                if (c.Children != null) foreach (var cc in c.Children) stack.Push(cc);
            }
            return n;
        }
    }
}
