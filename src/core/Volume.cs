using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Undelete
{
    /// <summary>One data run: VCN range mapped to an LCN / cluster number (Lcn &lt; 0 means sparse / hole).</summary>
    struct Run
    {
        public long Vcn, Lcn, Len;
        public Run(long vcn, long lcn, long len) { Vcn = vcn; Lcn = lcn; Len = len; }
    }

    sealed class DataFrag
    {
        public long StartVcn;
        public List<Run> Runs;
    }

    enum Chance { Unknown, Excellent, Partial, Overwritten, Zeroed, Unsupported, Folder }

    sealed class DeletedItem
    {
        public uint Rec;
        public ushort Seq;
        public bool IsDir;
        public string Name = "";
        public uint Parent;
        public ushort ParentSeq;
        public long Size, InitSize = -1;
        public DateTime Created, Modified, Changed;
        public byte[] Resident;
        public List<DataFrag> Frags = new List<DataFrag>();
        public ushort DataFlags;
        public bool HasData;
        public string Path = "\\";        // reconstructed parent path, always ends with '\'
        public bool Orphan;               // parent chain could not be resolved
        public bool InDeletedFolder;      // some ancestor folder is itself deleted
        public bool Guessed;              // cluster layout was inferred (FAT: assumed contiguous)
        public string Ref = "";           // where the entry was found, e.g. "mft record #123"
        public long Clusters, ClustersInUse;
        public Chance Chance = Chance.Unknown;
        public int ZeroCheck;             // 0 = not checked, 1 = has data, 2 = reads as zeros
        public List<DeletedItem> Children;

        public bool Compressed { get { return (DataFlags & 0x00FF) != 0; } }
        public bool Encrypted { get { return (DataFlags & 0x4000) != 0; } }
        public string FullPath { get { return Path + Name; } }

        public List<Run> AllRuns()
        {
            var runs = new List<Run>();
            foreach (var f in Frags.OrderBy(f => f.StartVcn)) runs.AddRange(f.Runs);
            runs.Sort((a, b) => a.Vcn.CompareTo(b.Vcn));
            return runs;
        }
    }

    /// <summary>
    /// A scanned file system. Subclasses find deleted entries (NTFS: MFT records, FAT/exFAT: directory entries)
    /// and describe their clusters; reading, assessing and recovering is shared.
    /// </summary>
    abstract class FsVolume : IDisposable
    {
        public readonly Disk Disk;
        public string Label = "";
        public int ClusterSize;
        public long TotalClusters;
        public List<DeletedItem> Items = new List<DeletedItem>();

        // progress (read from the UI thread)
        public volatile string Phase = "starting";
        public long RecordsScanned, RecordsTotal;
        public int FoundCount;
        public volatile bool Cancel;

        protected FsVolume(Disk disk) { Disk = disk; }

        public abstract string FsName { get; }                 // "NTFS", "FAT32", "exFAT"…
        public abstract string StructureInfo { get; }          // one line of file-system facts for the UI
        public virtual string ProgressUnit { get { return "records"; } }
        public abstract void Scan();

        protected abstract long ClusterOffset(long lcn);       // byte offset of a cluster on the volume
        protected abstract bool LcnValid(long lcn, long len);
        protected abstract bool HasAllocationMap { get; }
        protected abstract bool ClusterInUse(long lcn);

        public void Dispose() { Disk.Dispose(); }

        public static readonly string[] SupportedFs = { "NTFS", "FAT32", "FAT", "FAT16", "FAT12", "exFAT" };
        public static bool IsSupported(string fs) { return fs != null && SupportedFs.Any(s => s.Equals(fs, StringComparison.OrdinalIgnoreCase)); }

        // ───────────────────────────── detection ─────────────────────────────

        /// <summary>Opens a drive ("C:") or image file and picks the right file-system reader.</summary>
        public static FsVolume Open(string source)
        {
            var d = new Disk(source);
            try
            {
                byte[] bs = d.Read(0, 512);
                if (!d.IsDevice && !IsBootSector(bs))
                {
                    long po = PartitionOffset(d, bs);      // whole-disk image (MBR / GPT / fixed VHD)
                    if (po > 0) { d.BaseOffset = po; bs = d.Read(0, 512); }
                }
                string oem = Encoding.ASCII.GetString(bs, 3, 8);
                if (oem == "NTFS    ") return new NtfsVolume(d);
                if (oem == "EXFAT   ") return new ExFatVolume(d, bs);
                if (FatVolume.Looks(bs)) return new FatVolume(d, bs);
                throw new InvalidDataException(d.LastError != null
                    ? "Cannot read the boot sector: " + d.LastError
                    : "Unrecognised file system. Supported: NTFS, FAT12/16/32 and exFAT.");
            }
            catch { d.Dispose(); throw; }
        }

        static bool IsBootSector(byte[] bs)
        {
            string oem = Encoding.ASCII.GetString(bs, 3, 8);
            return oem == "NTFS    " || oem == "EXFAT   " || FatVolume.Looks(bs);
        }

        /// <summary>Byte offset of the first data partition in a whole-disk image, or 0.</summary>
        static long PartitionOffset(Disk d, byte[] mbr)
        {
            if (mbr[510] != 0x55 || mbr[511] != 0xAA) return 0;
            if (mbr[0x1BE + 4] == 0xEE)
            {
                byte[] gpt = d.Read(512, 512);
                if (Encoding.ASCII.GetString(gpt, 0, 8) != "EFI PART") return 0;
                long entLba = I64(gpt, 0x48);
                int count = (int)Math.Min(128, U32(gpt, 0x50)), size = (int)U32(gpt, 0x54);
                if (size < 128 || size > 4096) return 0;
                byte[] ents = d.Read(entLba * 512, count * size);
                for (int i = 0; i < count; i++)
                {
                    int o = i * size;
                    bool used = false;
                    for (int k = 0; k < 16; k++) if (ents[o + k] != 0) { used = true; break; }
                    if (!used) continue;
                    long start = I64(ents, o + 0x20) * 512;
                    byte[] bs = d.Read(start, 512);
                    if (IsBootSector(bs)) return start;
                }
                return 0;
            }
            for (int i = 0; i < 4; i++)
            {
                int e = 0x1BE + i * 16;
                byte type = mbr[e + 4];
                if (type == 0 || type == 0x05 || type == 0x0F) continue;
                long start = (long)U32(mbr, e + 8) * 512;
                if (start == 0) continue;
                byte[] bs = d.Read(start, 512);
                if (IsBootSector(bs)) return start;
            }
            return 0;
        }

        // ───────────────────────────── shared reading ─────────────────────────────

        /// <summary>Reads 'count' bytes at a virtual offset of a stream described by runs.
        /// Sparse runs, holes and invalid clusters come back as zeros.</summary>
        public void ReadVirtual(List<Run> runs, long off, byte[] buf, int idx, int count)
        {
            int ri = 0;
            while (count > 0)
            {
                long vcn = off / ClusterSize;
                int inCluster = (int)(off % ClusterSize);
                while (ri < runs.Count && runs[ri].Vcn + runs[ri].Len <= vcn) ri++;
                if (ri >= runs.Count || runs[ri].Vcn > vcn)
                {
                    long holeEnd = ri < runs.Count ? runs[ri].Vcn * (long)ClusterSize : long.MaxValue;
                    int z = (int)Math.Min(count, holeEnd - off);
                    Array.Clear(buf, idx, z);
                    off += z; idx += z; count -= z;
                    continue;
                }
                Run r = runs[ri];
                long left = (r.Vcn + r.Len - vcn) * ClusterSize - inCluster;
                int n = (int)Math.Min(count, left);
                if (r.Lcn < 0 || !LcnValid(r.Lcn, r.Len)) Array.Clear(buf, idx, n);
                else Disk.ReadInto(ClusterOffset(r.Lcn + (vcn - r.Vcn)) + inCluster, buf, idx, n);
                off += n; idx += n; count -= n;
            }
        }

        /// <summary>Rates how intact a deleted file's data is, from the volume's allocation map.</summary>
        protected void Assess(DeletedItem it)
        {
            if (it.IsDir) { it.Chance = Chance.Folder; return; }
            if (it.Compressed || it.Encrypted) { it.Chance = Chance.Unsupported; return; }
            if (it.Resident != null || it.Size == 0) { it.Chance = Chance.Excellent; return; }
            if (!HasAllocationMap) { it.Chance = Chance.Unknown; return; }

            long need = (it.Size + ClusterSize - 1) / ClusterSize;
            long seen = 0, used = 0;
            foreach (var r in it.AllRuns())
            {
                if (r.Vcn >= need) break;
                long len = Math.Min(r.Len, need - r.Vcn);
                if (r.Lcn < 0) continue;
                for (long k = 0; k < len; k++) if (ClusterInUse(r.Lcn + k)) used++;
                seen += len;
            }
            it.Clusters = seen;
            it.ClustersInUse = used;
            if (seen == 0) it.Chance = Chance.Overwritten;
            else if (used == 0) it.Chance = seen < need ? Chance.Partial : Chance.Excellent;
            else if (used < seen) it.Chance = Chance.Partial;
            else it.Chance = Chance.Overwritten;
        }

        /// <summary>Peeks at the first clusters: SSDs that TRIM freed space return all zeros.</summary>
        public void CheckZeros(DeletedItem it)
        {
            if (it.ZeroCheck != 0) return;
            it.ZeroCheck = 1;
            if (it.Resident != null || it.IsDir || it.Size == 0) return;
            int n = (int)Math.Min(it.Size, Math.Min(65536, ClusterSize * 4));
            byte[] b = new byte[n];
            ReadVirtual(it.AllRuns(), 0, b, 0, n);
            for (int i = 0; i < n; i++) if (b[i] != 0) return;
            it.ZeroCheck = 2;
            if (it.Chance == Chance.Excellent || it.Chance == Chance.Partial) it.Chance = Chance.Zeroed;
        }

        /// <summary>Reads up to 'max' bytes of a deleted file's contents into memory (for previews).</summary>
        public byte[] ReadData(DeletedItem it, int max)
        {
            if (it.IsDir) return new byte[0];
            if (it.Resident != null)
            {
                byte[] r = new byte[Math.Min(max, it.Resident.Length)];
                Buffer.BlockCopy(it.Resident, 0, r, 0, r.Length);
                return r;
            }
            int n = (int)Math.Min(it.Size, max);
            byte[] b = new byte[n];
            ReadVirtual(it.AllRuns(), 0, b, 0, n);
            long init = it.InitSize >= 0 ? it.InitSize : it.Size;
            if (init < n) Array.Clear(b, (int)Math.Max(0, init), n - (int)Math.Max(0, init));
            return b;
        }

        /// <summary>Copies a deleted file's contents to outFile (which must be on another volume).</summary>
        public void RecoverTo(DeletedItem it, string outFile, Action<long> progress)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outFile));
            using (var o = new FileStream(outFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                if (it.Resident != null)
                {
                    o.Write(it.Resident, 0, it.Resident.Length);
                }
                else
                {
                    var runs = it.AllRuns();
                    long init = it.InitSize >= 0 ? Math.Min(it.InitSize, it.Size) : it.Size;
                    byte[] buf = new byte[1 << 20];
                    long done = 0;
                    while (done < it.Size)
                    {
                        if (Cancel) throw new OperationCanceledException();
                        int want = (int)Math.Min(buf.Length, it.Size - done);
                        ReadVirtual(runs, done, buf, 0, want);
                        if (done + want > init)
                        {
                            int from = (int)Math.Max(0, init - done);
                            Array.Clear(buf, from, want - from);   // past the "valid data" length reads as zero
                        }
                        o.Write(buf, 0, want);
                        done += want;
                        if (progress != null) progress(want);
                    }
                }
            }
            SetTimes(outFile, it, false);
        }

        public static void SetTimes(string path, DeletedItem it, bool dir)
        {
            try
            {
                if (dir)
                {
                    if (it.Created != DateTime.MinValue) Directory.SetCreationTimeUtc(path, it.Created);
                    if (it.Modified != DateTime.MinValue) Directory.SetLastWriteTimeUtc(path, it.Modified);
                }
                else
                {
                    if (it.Created != DateTime.MinValue) File.SetCreationTimeUtc(path, it.Created);
                    if (it.Modified != DateTime.MinValue) File.SetLastWriteTimeUtc(path, it.Modified);
                }
            }
            catch { }
        }

        // ───────────────────────────── helpers ─────────────────────────────

        protected static DateTime FromFt(long ft)
        {
            if (ft <= 0) return DateTime.MinValue;
            try { return DateTime.FromFileTimeUtc(ft); } catch { return DateTime.MinValue; }
        }

        /// <summary>DOS date/time (local time) → UTC.</summary>
        protected static DateTime FromDos(ushort date, ushort time, int extraMs = 0)
        {
            if (date == 0) return DateTime.MinValue;
            try
            {
                var t = new DateTime(1980 + (date >> 9), (date >> 5) & 15, date & 31, time >> 11, (time >> 5) & 63, (time & 31) * 2, DateTimeKind.Local);
                return t.AddMilliseconds(extraMs).ToUniversalTime();
            }
            catch { return DateTime.MinValue; }
        }

        internal static ushort U16(byte[] b, int o) { return (ushort)(b[o] | (b[o + 1] << 8)); }
        internal static uint U32(byte[] b, int o) { return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24)); }
        internal static ulong U64(byte[] b, int o) { return U32(b, o) | ((ulong)U32(b, o + 4) << 32); }
        internal static long I64(byte[] b, int o) { return (long)U64(b, o); }
    }

    /// <summary>Reads FAT / exFAT allocation table entries on demand through a small page cache.</summary>
    sealed class FatTable
    {
        readonly Disk disk;
        readonly long offset;
        readonly int bits;
        readonly Dictionary<long, byte[]> pages = new Dictionary<long, byte[]>();
        const int Page = 64 * 1024;

        public FatTable(Disk disk, long offset, int bits) { this.disk = disk; this.offset = offset; this.bits = bits; }

        byte At(long pos)
        {
            long p = pos / Page;
            byte[] pg;
            if (!pages.TryGetValue(p, out pg))
            {
                if (pages.Count > 512) pages.Clear();   // cap ~32 MB
                pages[p] = pg = disk.Read(offset + p * Page, Page);
            }
            return pg[pos % Page];
        }

        public uint Get(long cluster)
        {
            switch (bits)
            {
                case 12:
                    {
                        long pos = cluster + cluster / 2;
                        int v = At(pos) | (At(pos + 1) << 8);
                        return (uint)((cluster & 1) != 0 ? v >> 4 : v & 0xFFF);
                    }
                case 16: return (uint)(At(cluster * 2) | (At(cluster * 2 + 1) << 8));
                case 28:   // FAT32: top 4 bits reserved
                    return (uint)(At(cluster * 4) | (At(cluster * 4 + 1) << 8) | (At(cluster * 4 + 2) << 16) | (At(cluster * 4 + 3) << 24)) & 0x0FFFFFFF;
                default:   // exFAT: full 32 bits
                    return (uint)(At(cluster * 4) | (At(cluster * 4 + 1) << 8) | (At(cluster * 4 + 2) << 16) | (At(cluster * 4 + 3) << 24));
            }
        }

        public bool IsEnd(uint v)
        {
            switch (bits)
            {
                case 12: return v >= 0xFF7;
                case 16: return v >= 0xFFF7;
                case 28: return v >= 0x0FFFFFF7;
                default: return v >= 0xFFFFFFF7;
            }
        }

        /// <summary>Follows a cluster chain into runs (consecutive clusters merged). Stops at the end marker,
        /// a free/invalid entry, a loop, or after maxClusters.</summary>
        public List<Run> Chain(uint start, long totalClusters, long maxClusters, out bool complete)
        {
            var runs = new List<Run>();
            complete = false;
            uint c = start;
            long vcn = 0;
            var seen = new HashSet<uint>();
            while (c >= 2 && c < totalClusters + 2 && vcn < maxClusters)
            {
                if (!seen.Add(c)) return runs;
                if (runs.Count > 0 && runs[runs.Count - 1].Lcn + runs[runs.Count - 1].Len == c)
                {
                    var last = runs[runs.Count - 1];
                    runs[runs.Count - 1] = new Run(last.Vcn, last.Lcn, last.Len + 1);
                }
                else runs.Add(new Run(vcn, c, 1));
                vcn++;
                uint next = Get(c);
                if (IsEnd(next)) { complete = true; return runs; }
                if (next < 2) return runs;
                c = next;
            }
            complete = vcn >= maxClusters;
            return runs;
        }
    }
}
