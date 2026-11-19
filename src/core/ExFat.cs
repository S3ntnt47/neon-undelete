using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Undelete
{
    /// <summary>
    /// exFAT reader. Deleting a file on exFAT only clears the "in use" bit of its directory entry set
    /// (0x85/0xC0/0xC1 become 0x05/0x40/0x41) and frees its clusters in the allocation bitmap.
    /// The full name, size and first cluster survive. Files flagged NoFatChain are contiguous, so their
    /// location is exact; others follow whatever is left of their FAT chain.
    /// </summary>
    sealed class ExFatVolume : FsVolume
    {
        readonly int bps;
        readonly long fatOffset, heapOffset;
        readonly uint rootCluster;
        readonly FatTable fat;
        byte[] bitmap;
        uint nextId = 1;

        public override string FsName { get { return "exFAT"; } }
        public override string StructureInfo { get { return Fmt.N(TotalClusters) + " clusters · " + Fmt.N(RecordsScanned) + " folders"; } }
        public override string ProgressUnit { get { return "folders"; } }

        protected override long ClusterOffset(long c) { return heapOffset + (c - 2) * ClusterSize; }
        protected override bool LcnValid(long lcn, long len) { return lcn >= 2 && lcn + len <= TotalClusters + 2; }
        protected override bool HasAllocationMap { get { return bitmap != null; } }
        protected override bool ClusterInUse(long c)
        {
            long i = c - 2;
            if (i < 0 || i >= TotalClusters || (i >> 3) >= bitmap.Length) return true;
            return (bitmap[i >> 3] & (1 << (int)(i & 7))) != 0;
        }

        public ExFatVolume(Disk disk, byte[] bs) : base(disk)
        {
            bps = 1 << bs[0x6C];
            ClusterSize = bps << bs[0x6D];
            fatOffset = (long)U32(bs, 0x50) * bps;
            heapOffset = (long)U32(bs, 0x58) * bps;
            TotalClusters = U32(bs, 0x5C);
            rootCluster = U32(bs, 0x60);
            if (bps < 512 || bps > 4096 || ClusterSize <= 0 || TotalClusters <= 0 || rootCluster < 2)
                throw new InvalidDataException("exFAT boot sector geometry looks invalid.");
            fat = new FatTable(disk, fatOffset, 32);
        }

        sealed class DirJob
        {
            public string Path;
            public List<Run> Runs;
            public long Length;
            public bool Deleted;
            public DeletedItem Owner;
        }

        /// <summary>Cluster runs of a stream: exact for contiguous (NoFatChain) streams, otherwise the FAT chain,
        /// padded with a contiguous guess where the chain has been cleared.</summary>
        List<Run> StreamRuns(uint first, long length, bool contiguous, out bool guessed)
        {
            guessed = false;
            long need = (length + ClusterSize - 1) / ClusterSize;
            if (need <= 0 || first < 2 || first >= TotalClusters + 2) return new List<Run>();
            need = Math.Min(need, TotalClusters + 2 - first);
            if (contiguous) return new List<Run> { new Run(0, first, need) };

            bool complete;
            var runs = fat.Chain(first, TotalClusters, need, out complete);
            long have = runs.Sum(r => r.Len);
            if (have < need)
            {
                guessed = true;
                long next = runs.Count > 0 ? runs[runs.Count - 1].Lcn + runs[runs.Count - 1].Len : first;
                long rest = Math.Min(need - have, TotalClusters + 2 - next);
                if (rest > 0) runs.Add(new Run(have, next, rest));
            }
            return runs;
        }

        public override void Scan()
        {
            Phase = "reading root directory";
            bool complete;
            var rootRuns = fat.Chain(rootCluster, TotalClusters, (64L << 20) / ClusterSize, out complete);
            long rootLen = rootRuns.Sum(r => r.Len) * ClusterSize;
            byte[] root = new byte[rootLen];
            ReadVirtual(rootRuns, 0, root, 0, (int)rootLen);

            // allocation bitmap (0x81) and volume label (0x83) live in the root directory
            for (int o = 0; o + 32 <= root.Length; o += 32)
            {
                byte t = root[o];
                if (t == 0) break;
                if (t == 0x81 && bitmap == null)
                {
                    uint first = U32(root, o + 0x14);
                    long len = I64(root, o + 0x18);
                    if (len > 0 && len < int.MaxValue)
                    {
                        bool g;
                        var runs = StreamRuns(first, len, (root[o + 1] & 2) != 0, out g);
                        bitmap = new byte[len];
                        ReadVirtual(runs, 0, bitmap, 0, (int)len);
                    }
                }
                else if (t == 0x83)
                {
                    int n = Math.Min(11, (int)root[o + 1]);
                    Label = Encoding.Unicode.GetString(root, o + 2, n * 2);
                }
            }

            Phase = "walking directories";
            var queue = new Queue<DirJob>();
            var visited = new HashSet<long>();
            ParseDir(root, new DirJob { Path = "\\" }, queue);
            RecordsScanned = 1;
            RecordsTotal = 1 + queue.Count;

            while (queue.Count > 0 && !Cancel)
            {
                var job = queue.Dequeue();
                if (job.Runs.Count > 0 && visited.Add(job.Runs[0].Lcn) && job.Length > 0 && job.Length <= (64L << 20))
                {
                    byte[] d = new byte[job.Length];
                    ReadVirtual(job.Runs, 0, d, 0, (int)job.Length);
                    ParseDir(d, job, queue);
                }
                RecordsScanned++;
                RecordsTotal = RecordsScanned + queue.Count;
            }
            if (Cancel) return;

            Phase = "checking which clusters were reused";
            foreach (var it in Items) Assess(it);
            Phase = "done";
        }

        void ParseDir(byte[] d, DirJob job, Queue<DirJob> queue)
        {
            for (int o = 0; o + 32 <= d.Length; o += 32)
            {
                byte t = d[o];
                if (t == 0x00) break;                                   // end of directory
                if ((t & 0x7F) != 0x05) continue;                       // not a File entry (live 0x85 / deleted 0x05)
                bool inUse = (t & 0x80) != 0;
                int sc = d[o + 1];
                if (sc < 2 || sc > 18 || o + (sc + 1) * 32 > d.Length) continue;

                int s = o + 32;                                         // Stream Extension entry
                if ((d[s] & 0x7F) != 0x40 || ((d[s] & 0x80) != 0) != inUse) continue;
                int nameLen = d[s + 3];
                var name = new StringBuilder(nameLen);
                bool ok = true;
                for (int k = 2; k <= sc && name.Length < nameLen; k++)
                {
                    int e = o + k * 32;
                    if ((d[e] & 0x7F) != 0x41 || ((d[e] & 0x80) != 0) != inUse) { ok = false; break; }
                    for (int c = 0; c < 15 && name.Length < nameLen; c++) name.Append((char)U16(d, e + 2 + c * 2));
                }
                if (!ok || name.Length == 0) continue;               // slots partly reused by a newer entry

                ushort attrs = U16(d, o + 4);
                bool isDir = (attrs & 0x10) != 0;
                bool contiguous = (d[s + 1] & 0x02) != 0;
                long validLen = I64(d, s + 8);
                uint first = U32(d, s + 0x14);
                long dataLen = I64(d, s + 0x18);
                string nm = name.ToString();
                bool guessed;
                var runs = StreamRuns(first, dataLen, contiguous, out guessed);

                if (!inUse || job.Deleted)
                {
                    var it = new DeletedItem
                    {
                        Rec = nextId++,
                        Name = nm,
                        Path = job.Path,
                        IsDir = isDir,
                        Size = isDir ? 0 : dataLen,
                        InitSize = isDir ? -1 : validLen,
                        HasData = !isDir && dataLen > 0,
                        Created = ExTime(U32(d, o + 8), d[o + 0x14], d[o + 0x16]),
                        Modified = ExTime(U32(d, o + 0x0C), d[o + 0x15], d[o + 0x17]),
                        InDeletedFolder = job.Deleted,
                        Guessed = guessed,
                        Ref = "directory entry set · first cluster " + first + (contiguous ? " · contiguous" : " · fat chain"),
                    };
                    if (!isDir && runs.Count > 0) it.Frags.Add(new DataFrag { StartVcn = 0, Runs = runs });
                    Items.Add(it);
                    FoundCount++;
                    if (job.Owner != null)
                    {
                        if (job.Owner.Children == null) job.Owner.Children = new List<DeletedItem>();
                        job.Owner.Children.Add(it);
                    }
                    if (isDir && runs.Count > 0)
                        queue.Enqueue(new DirJob { Path = job.Path + nm + "\\", Runs = runs, Length = dataLen, Deleted = true, Owner = it });
                }
                else if (isDir && runs.Count > 0)
                {
                    queue.Enqueue(new DirJob { Path = job.Path + nm + "\\", Runs = runs, Length = dataLen });
                }
                o += sc * 32;
            }
        }

        /// <summary>exFAT timestamp: DOS date/time + 10 ms increments + optional UTC offset (15-minute units).</summary>
        static DateTime ExTime(uint ts, byte tenMs, byte utcOffset)
        {
            if (ts == 0) return DateTime.MinValue;
            ushort date = (ushort)(ts >> 16), time = (ushort)(ts & 0xFFFF);
            if ((utcOffset & 0x80) == 0) return FromDos(date, time, tenMs * 10);
            try
            {
                int q = utcOffset & 0x7F;
                if (q >= 64) q -= 128;
                var t = new DateTime(1980 + (date >> 9), (date >> 5) & 15, date & 31, time >> 11, (time >> 5) & 63, (time & 31) * 2, DateTimeKind.Utc);
                return t.AddMilliseconds(tenMs * 10).AddMinutes(-15 * q);
            }
            catch { return DateTime.MinValue; }
        }
    }
}
