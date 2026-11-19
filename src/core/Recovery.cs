using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Undelete
{
    sealed class RecoveryJob
    {
        public string Target;       // full output path
        public DeletedItem Item;
    }

    sealed class RecoveryResult
    {
        public int Files, Folders, Failed, Skipped;
        public long Bytes;
        public List<string> Errors = new List<string>();
        public string OutRoot;
    }

    /// <summary>Plans and runs a recovery of selected items into a folder on a different volume.</summary>
    sealed class Recovery
    {
        readonly FsVolume vol;
        public long TotalBytes, DoneBytes;
        public int TotalJobs, DoneJobs;
        public volatile string Current = "";

        public Recovery(FsVolume v) { vol = v; }

        /// <summary>Returns an error message if the destination is unsafe, otherwise null.</summary>
        public static string CheckDestination(FsVolume v, string dest)
        {
            if (string.IsNullOrWhiteSpace(dest)) return "Enter a destination folder.";
            string full;
            try { full = Path.GetFullPath(dest); }
            catch (Exception e) { return "Invalid path: " + e.Message; }
            string root = Path.GetPathRoot(full) ?? "";
            if (v.Disk.DriveLetter != '\0' && root.Length >= 2 && root[1] == ':' &&
                char.ToUpperInvariant(root[0]) == v.Disk.DriveLetter)
                return "Destination is on " + v.Disk.DriveLetter + ": - the drive being recovered. Writing there " +
                       "could overwrite the very data you are trying to save. Pick another drive.";
            if (!Directory.Exists(root)) return "Drive " + root + " does not exist.";
            return null;
        }

        public static string DefaultDestination(FsVolume v)
        {
            try
            {
                var best = DriveInfo.GetDrives()
                    .Where(d => d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable) &&
                                char.ToUpperInvariant(d.Name[0]) != v.Disk.DriveLetter)
                    .OrderByDescending(d => d.AvailableFreeSpace).FirstOrDefault();
                if (best != null) return Path.Combine(best.Name, "Recovered");
            }
            catch { }
            return "";
        }

        public List<RecoveryJob> Plan(IEnumerable<DeletedItem> selection, string dest)
        {
            string tag = vol.Disk.DriveLetter != '\0' ? vol.Disk.DriveLetter + "_drive" : "image";
            string outRoot = Path.Combine(Path.GetFullPath(dest), "Undeleted_" + tag + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));

            var all = new List<DeletedItem>();
            var seen = new HashSet<DeletedItem>();
            foreach (var s in selection) Collect(s, all, seen);

            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var jobs = new List<RecoveryJob>();
            foreach (var it in all.OrderBy(i => i.IsDir ? 0 : 1).ThenBy(i => i.FullPath.Length))
            {
                var parts = new List<string>();
                if (it.Orphan) parts.Add("_lost_parent_folder");
                parts.AddRange(it.Path.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries).Select(Sanitize));
                string dir = Path.Combine(new[] { outRoot }.Concat(parts).ToArray());
                string target = Path.Combine(dir, Sanitize(it.Name));
                if (!it.IsDir) target = Unique(target, used);
                else used.Add(target);
                jobs.Add(new RecoveryJob { Target = target, Item = it });
            }
            TotalJobs = jobs.Count;
            TotalBytes = jobs.Where(j => !j.Item.IsDir).Sum(j => j.Item.Size);
            return jobs;
        }

        static void Collect(DeletedItem it, List<DeletedItem> into, HashSet<DeletedItem> seen)
        {
            if (!seen.Add(it)) return;
            into.Add(it);
            if (it.Children != null) foreach (var c in it.Children) Collect(c, into, seen);
        }

        public RecoveryResult Run(List<RecoveryJob> jobs)
        {
            var res = new RecoveryResult();
            if (jobs.Count > 0)
            {
                string first = jobs[0].Target;
                res.OutRoot = first;
                // walk up to the Undeleted_* root
                string d = first;
                while (d != null && !Path.GetFileName(d).StartsWith("Undeleted_")) d = Path.GetDirectoryName(d);
                if (d != null) res.OutRoot = d;
            }
            foreach (var j in jobs)
            {
                if (vol.Cancel) break;
                Current = j.Item.FullPath;
                try
                {
                    if (j.Item.IsDir)
                    {
                        Directory.CreateDirectory(j.Target);
                        res.Folders++;
                    }
                    else if (j.Item.Chance == Chance.Unsupported)
                    {
                        res.Skipped++;
                        res.Errors.Add(j.Item.FullPath + ": " + (j.Item.Encrypted ? "EFS-encrypted" : "NTFS-compressed") + " files are not supported");
                    }
                    else
                    {
                        vol.RecoverTo(j.Item, j.Target, n => { DoneBytes += n; });
                        res.Files++;
                        res.Bytes += j.Item.Size;
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception e)
                {
                    res.Failed++;
                    res.Errors.Add(j.Item.FullPath + ": " + e.Message);
                }
                DoneJobs++;
            }
            // folder timestamps last (writing files into them changes mtime)
            foreach (var j in jobs.Where(x => x.Item.IsDir))
                if (Directory.Exists(j.Target)) FsVolume.SetTimes(j.Target, j.Item, true);
            return res;
        }

        static readonly char[] Bad = Path.GetInvalidFileNameChars();
        static readonly string[] Reserved = { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5",
                                              "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4",
                                              "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

        static string Sanitize(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (char c in name) sb.Append(Array.IndexOf(Bad, c) >= 0 || c < 32 ? '_' : c);
            string s = sb.ToString().TrimEnd(' ', '.');
            if (s.Length == 0) s = "_";
            string stem = s.Split('.')[0].ToUpperInvariant();
            if (Reserved.Contains(stem)) s = "_" + s;
            return s;
        }

        static string Unique(string path, HashSet<string> used)
        {
            string p = path;
            int n = 1;
            while (used.Contains(p) || File.Exists(p) || Directory.Exists(p))
            {
                p = Path.Combine(Path.GetDirectoryName(path),
                    Path.GetFileNameWithoutExtension(path) + " (" + (++n) + ")" + Path.GetExtension(path));
            }
            used.Add(p);
            return p;
        }
    }
}
