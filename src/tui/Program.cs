using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Undelete
{
    static class Program
    {
        const string Usage =
@"undelete - recover deleted files and folders from NTFS, FAT and exFAT volumes (read-only scanner)

  Undelete.exe                         interactive neon terminal UI
  Undelete.exe C:                      interactive, scan C: straight away
  Undelete.exe --list <src> [out.csv]  scan and list deleted entries as CSV
  Undelete.exe --recover <src> <dest> <text>
                                       recover every deleted entry whose path contains <text>
                                       (use * for everything)

  Undelete.exe --record <src> <n...>   diagnostics: show raw MFT record headers
  Undelete.exe --probe C: D: ...       diagnostics: test raw read access per volume

  <src> is a drive letter (C:) - needs Administrator - or a disk image file (.img/.dd/.raw/.vhd).
  <dest> must be on a different drive than <src>.";

        [STAThread]
        static int Main(string[] args)
        {
            try
            {
                if (args.Length > 0)
                {
                    string a = args[0].ToLowerInvariant();
                    if (a == "-h" || a == "--help" || a == "/?") { Console.WriteLine(Usage); return 0; }
                    if (a == "--list" || a == "-l") return List(args);
                    if (a == "--recover" || a == "-r") return RecoverCli(args);
                    if (a == "--probe") return Probe(args);
                    if (a == "--record") return DumpRecords(args);
                    if (a == "--extract") return Extract(args);
                }
                if (Console.IsOutputRedirected || Console.IsInputRedirected)
                {
                    Console.Error.WriteLine("The interactive UI needs a real console window. Use --list / --recover for scripting.");
                    return 2;
                }
                Console.OutputEncoding = new UTF8Encoding(false);
                new App().Run(args.Length > 0 ? args[0] : null);
                return 0;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("error: " + e.Message);
                return 1;
            }
        }

        static FsVolume ScanCli(string src)
        {
            var v = FsVolume.Open(src);
            Console.Error.WriteLine("volume " + src + ": " + Fmt.Bytes(v.TotalClusters * v.ClusterSize) + ", cluster " + v.ClusterSize +
                                    ", " + v.FsName + ", " + v.StructureInfo);
            var th = new Thread(v.Scan) { IsBackground = true };
            th.Start();
            while (!th.Join(1000))
                Console.Error.WriteLine("  " + v.Phase + "  " + Fmt.N(v.RecordsScanned) + "/" + Fmt.N(v.RecordsTotal) + "  deleted found: " + Fmt.N(v.FoundCount));
            Console.Error.WriteLine("done: " + Fmt.N(v.Items.Count) + " deleted entries");
            return v;
        }

        static int List(string[] args)
        {
            if (args.Length < 2) { Console.Error.WriteLine(Usage); return 2; }
            using (var v = ScanCli(args[1]))
            {
                TextWriter w = args.Length > 2 ? new StreamWriter(args[2], false, new UTF8Encoding(true)) : Console.Out;
                w.WriteLine("record,type,name,folder,size,modified_utc,created_utc,chance,clusters,clusters_reused,orphan");
                foreach (var it in v.Items.OrderBy(i => i.FullPath, StringComparer.OrdinalIgnoreCase))
                {
                    w.WriteLine(string.Join(",", new[] {
                        it.Rec.ToString(), it.IsDir ? "dir" : "file", Csv(it.Name), Csv(it.Path), it.Size.ToString(),
                        Iso(it.Modified), Iso(it.Created), it.Chance.ToString().ToLowerInvariant(),
                        it.Clusters.ToString(), it.ClustersInUse.ToString(), it.Orphan ? "1" : "0" }));
                }
                if (w != Console.Out) { w.Dispose(); Console.Error.WriteLine("wrote " + args[2]); }
            }
            return 0;
        }

        static int RecoverCli(string[] args)
        {
            if (args.Length < 4) { Console.Error.WriteLine(Usage); return 2; }
            using (var v = ScanCli(args[1]))
            {
                string bad = Recovery.CheckDestination(v, args[2]);
                if (bad != null) { Console.Error.WriteLine("error: " + bad); return 3; }
                string text = args[3];
                var sel = v.Items.Where(i => text == "*" || i.FullPath.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (sel.Count == 0) { Console.Error.WriteLine("nothing matches '" + text + "'"); return 4; }
                var rec = new Recovery(v);
                var jobs = rec.Plan(sel, args[2]);
                Console.Error.WriteLine("recovering " + jobs.Count + " entries (" + Fmt.Bytes(rec.TotalBytes) + ")...");
                var r = rec.Run(jobs);
                Console.Error.WriteLine("recovered " + r.Files + " files, " + r.Folders + " folders (" + Fmt.Bytes(r.Bytes) + "), " +
                                        r.Failed + " failed, " + r.Skipped + " skipped -> " + r.OutRoot);
                foreach (var e in r.Errors) Console.Error.WriteLine("  ! " + e);
                return r.Failed > 0 ? 5 : 0;
            }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle h, uint code, IntPtr inBuf, int inLen,
                                           IntPtr outBuf, int outLen, out int ret, IntPtr ov);

        /// <summary>Diagnostics: tries several raw-read variants on a volume.</summary>
        static int Probe(string[] args)
        {
            foreach (var target in args.Skip(1))
            {
                string p = target.Length == 2 ? @"\\.\" + target : target;
                foreach (uint flags in new uint[] { 0, 0x20000000 /*NO_BUFFERING*/ })
                    foreach (int size in new[] { 512, 4096, 65536 })
                    {
                        var h = Native.CreateFile(p, 0x80000000u, 3u, IntPtr.Zero, 3u, flags, IntPtr.Zero);
                        if (h.IsInvalid) { Console.WriteLine(p + " open failed " + System.Runtime.InteropServices.Marshal.GetLastWin32Error()); continue; }
                        int ret;
                        bool dasd = DeviceIoControl(h, 0x00090083 /*FSCTL_ALLOW_EXTENDED_DASD_IO*/, IntPtr.Zero, 0, IntPtr.Zero, 0, out ret, IntPtr.Zero);
                        int dasdErr = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                        var buf = new byte[size]; int n;
                        bool ok = Native.ReadFile(h, buf, size, out n, IntPtr.Zero);
                        int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                        Console.WriteLine(p + " flags=" + flags.ToString("X") + " size=" + size + " dasd=" + dasd + (dasd ? "" : "(" + dasdErr + ")") +
                                          " read=" + ok + " n=" + n + " err=" + (ok ? 0 : err) +
                                          (ok && n >= 11 ? " oem='" + Encoding.ASCII.GetString(buf, 3, 8) + "'" : ""));
                        h.Dispose();
                    }
            }
            return 0;
        }

        /// <summary>Diagnostics: prints the header of specific MFT records.</summary>
        static int DumpRecords(string[] args)
        {
            using (var v = OpenNtfs(args[1]))
            {
                Console.WriteLine("via " + (v.Disk.Via ?? "volume handle"));
                foreach (var s in args.Skip(2))
                {
                    long n = long.Parse(s);
                    byte[] b = v.ReadRecord(n);
                    if (b == null) { Console.WriteLine(n + ": not a valid FILE record"); continue; }
                    ushort flags = FsVolume.U16(b, 0x16), seq = FsVolume.U16(b, 0x10);
                    int nameAt = -1;
                    for (int i = 0x38; i + 0x42 < b.Length; i++)
                        if (FsVolume.U32(b, i) == 0x30) { nameAt = i; break; }
                    string name = "";
                    if (nameAt > 0)
                    {
                        int c = nameAt + FsVolume.U16(b, nameAt + 0x14);
                        if (c + 0x42 < b.Length) name = Encoding.Unicode.GetString(b, c + 0x42, Math.Min(b[c + 0x40] * 2, b.Length - c - 0x42));
                    }
                    Console.WriteLine(n + ": flags=" + flags + " (" + ((flags & 1) != 0 ? "in use" : "DELETED") + ((flags & 2) != 0 ? ", dir" : "") +
                                      ") seq=" + seq + " name=" + name);
                }
            }
            return 0;
        }

        /// <summary>Diagnostics: copies one record's data out through the same path recovery uses.</summary>
        static int Extract(string[] args)
        {
            using (var v = OpenNtfs(args[1]))
            {
                var it = v.LoadRecord(uint.Parse(args[2]));
                if (it == null) { Console.Error.WriteLine("bad record"); return 1; }
                Console.Error.WriteLine("record " + it.Rec + " '" + it.Name + "' size=" + it.Size + " init=" + it.InitSize +
                                        " runs=" + it.AllRuns().Count + (it.Resident != null ? " resident" : ""));
                if (File.Exists(args[3])) File.Delete(args[3]);
                v.RecoverTo(it, args[3], null);
            }
            return 0;
        }

        static NtfsVolume OpenNtfs(string src)
        {
            var v = FsVolume.Open(src);
            var n = v as NtfsVolume;
            if (n == null) { v.Dispose(); throw new InvalidOperationException("this diagnostic only works on NTFS volumes"); }
            return n;
        }

        static string Csv(string s) { return "\"" + s.Replace("\"", "\"\"") + "\""; }
        static string Iso(DateTime d) { return d == DateTime.MinValue ? "" : d.ToString("yyyy-MM-ddTHH:mm:ssZ"); }
    }
}
