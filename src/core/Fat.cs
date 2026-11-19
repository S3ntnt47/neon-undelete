using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Undelete
{
    /// <summary>
    /// FAT12 / FAT16 / FAT32 reader. Walks the directory tree from the root and collects directory
    /// entries marked deleted (first byte 0xE5).
    ///
    /// On deletion FAT overwrites the first character of the short name and frees the file's cluster
    /// chain, so data is located by assuming the file was stored contiguously from its first cluster.
    /// That is usually true for files written in one go; fragmented files come back partly wrong.
    /// Long (VFAT) names survive intact and are reattached using their checksum.
    /// </summary>
    sealed class FatVolume : FsVolume
    {
        public int FatBits;
        int bps, spc, reserved, numFats;
        long fatSectors, rootDirSectors, firstDataSector, rootDirSector;
        uint rootCluster;
        long dataOffset;
        FatTable fat;
        uint nextId = 1;

        public override string FsName { get { return "FAT" + FatBits; } }
        public override string StructureInfo { get { return Fmt.N(TotalClusters) + " clusters · " + Fmt.N(RecordsScanned) + " folders"; } }
        public override string ProgressUnit { get { return "folders"; } }

        protected override long ClusterOffset(long c) { return dataOffset + (c - 2) * ClusterSize; }
        protected override bool LcnValid(long lcn, long len) { return lcn >= 2 && lcn + len <= TotalClusters + 2; }
        protected override bool HasAllocationMap { get { return true; } }
        protected override bool ClusterInUse(long c) { return c < 2 || c >= TotalClusters + 2 || fat.Get(c) != 0; }

        /// <summary>Does this boot sector describe a FAT file system?</summary>
        public static bool Looks(byte[] bs)
        {
            if (bs[510] != 0x55 || bs[511] != 0xAA) return false;
            if (bs[0] != 0xEB && bs[0] != 0xE9) return false;
            int b = U16(bs, 0x0B), s = bs[0x0D];
            if (b != 512 && b != 1024 && b != 2048 && b != 4096) return false;
            if (s == 0 || (s & (s - 1)) != 0) return false;
            if (U16(bs, 0x0E) == 0 || bs[0x10] == 0 || bs[0x10] > 4) return false;
            return U16(bs, 0x16) != 0 || U32(bs, 0x24) != 0;
        }

        public FatVolume(Disk disk, byte[] bs) : base(disk)
        {
            bps = U16(bs, 0x0B);
            spc = bs[0x0D];
            reserved = U16(bs, 0x0E);
            numFats = bs[0x10];
            int rootEnt = U16(bs, 0x11);
            long totSec = U16(bs, 0x13) != 0 ? U16(bs, 0x13) : U32(bs, 0x20);
            fatSectors = U16(bs, 0x16) != 0 ? U16(bs, 0x16) : U32(bs, 0x24);
            rootDirSectors = (rootEnt * 32 + bps - 1) / bps;
            firstDataSector = reserved + numFats * fatSectors + rootDirSectors;
            rootDirSector = reserved + numFats * fatSectors;
            long clusters = (totSec - firstDataSector) / spc;
            if (clusters <= 0) throw new InvalidDataException("FAT boot sector geometry looks invalid.");

            FatBits = clusters < 4085 ? 12 : clusters < 65525 ? 16 : 32;
            ClusterSize = bps * spc;
            TotalClusters = clusters;
            dataOffset = firstDataSector * bps;
            rootCluster = FatBits == 32 ? U32(bs, 0x2C) : 0;
            fat = new FatTable(disk, (long)reserved * bps, FatBits == 32 ? 28 : FatBits);

            int labelAt = FatBits == 32 ? 0x47 : 0x2B;
            string lbl = Encoding.ASCII.GetString(bs, labelAt, 11).Trim();
            if (lbl.Length > 0 && lbl != "NO NAME") Label = lbl;
        }

        sealed class DirJob
        {
            public string Path;
            public bool Root, Deleted;
            public uint Start;
            public DeletedItem Owner;      // the deleted folder this listing belongs to
        }

        public override void Scan()
        {
            Phase = "walking directories";
            var queue = new Queue<DirJob>();
            var visited = new HashSet<uint>();
            queue.Enqueue(new DirJob { Path = "\\", Root = true, Start = rootCluster });
            RecordsTotal = 1;

            while (queue.Count > 0 && !Cancel)
            {
                var job = queue.Dequeue();
                if (!job.Root && !visited.Add(job.Start)) { RecordsScanned++; continue; }
                byte[] data = ReadDir(job);
                if (data != null) ParseDir(data, job, queue, visited);
                RecordsScanned++;
                RecordsTotal = RecordsScanned + queue.Count;
            }
            if (Cancel) return;

            Phase = "checking which clusters were reused";
            foreach (var it in Items) Assess(it);
            Phase = "done";
        }

        byte[] ReadDir(DirJob job)
        {
            if (job.Root && FatBits != 32) return Disk.Read(rootDirSector * bps, (int)(rootDirSectors * bps));

            if (!job.Deleted)
            {
                bool complete;
                var runs = fat.Chain(job.Start, TotalClusters, (64L << 20) / ClusterSize, out complete);
                long len = runs.Sum(r => r.Len) * ClusterSize;
                if (len == 0) return null;
                byte[] b = new byte[len];
                ReadVirtual(runs, 0, b, 0, (int)len);
                return b;
            }

            // Deleted folder: its chain is gone. Read contiguous free clusters while they still look like
            // directory data. The first cluster must still start with the "." and ".." entries.
            if (job.Start < 2 || job.Start >= TotalClusters + 2) return null;
            var buf = new List<byte>();
            for (long c = job.Start; c < job.Start + 256 && c < TotalClusters + 2; c++)
            {
                if (c != job.Start && ClusterInUse(c)) break;
                byte[] cl = Disk.Read(ClusterOffset(c), ClusterSize);
                if (c == job.Start)
                {
                    bool dot = (cl[0] == '.' || cl[0] == 0xE5) && cl[1] == ' ' && (cl[11] & 0x10) != 0;
                    bool dotdot = (cl[32] == '.' || cl[32] == 0xE5) && cl[33] == '.' && (cl[43] & 0x10) != 0;
                    if (!dot || !dotdot) return null;
                }
                else if (!LooksLikeDir(cl)) break;
                buf.AddRange(cl);
                if (HasEndMarker(cl)) break;
            }
            return buf.ToArray();
        }

        static bool HasEndMarker(byte[] cl)
        {
            for (int o = 0; o + 32 <= cl.Length; o += 32) if (cl[o] == 0) return true;
            return false;
        }

        static bool LooksLikeDir(byte[] cl)
        {
            int good = 0, total = 0;
            for (int o = 0; o + 32 <= cl.Length; o += 32)
            {
                byte b0 = cl[o];
                if (b0 == 0) break;
                total++;
                byte attr = cl[o + 11];
                if (attr == 0x0F || (attr & 0xC0) == 0 && (b0 == 0xE5 || b0 >= 0x20)) good++;
            }
            return total > 0 && good * 10 >= total * 9;
        }

        void ParseDir(byte[] d, DirJob job, Queue<DirJob> queue, HashSet<uint> visited)
        {
            var lfn = new List<string>();
            int lfnChecksum = -1;

            for (int o = 0; o + 32 <= d.Length; o += 32)
            {
                byte b0 = d[o];
                if (b0 == 0x00) break;                       // end of directory
                byte attr = d[o + 11];

                if (attr == 0x0F)                            // VFAT long-name fragment (stored last-part-first)
                {
                    var sb = new StringBuilder(13);
                    foreach (int p in new[] { 1, 3, 5, 7, 9, 14, 16, 18, 20, 22, 24, 28, 30 })
                    {
                        char ch = (char)U16(d, o + p);
                        if (ch == '\0' || ch == '￿') break;
                        sb.Append(ch);
                    }
                    if (lfnChecksum >= 0 && lfnChecksum != d[o + 13]) lfn.Clear();
                    lfnChecksum = d[o + 13];
                    lfn.Add(sb.ToString());
                    continue;
                }

                bool deleted = b0 == 0xE5;
                string longName = null;
                if (lfn.Count > 0 && ChecksumMatches(d, o, lfnChecksum, deleted))
                {
                    var parts = new List<string>(lfn);
                    parts.Reverse();
                    longName = string.Concat(parts);
                }
                lfn.Clear(); lfnChecksum = -1;

                if ((attr & 0x08) != 0)                      // volume label
                {
                    if (job.Root && !deleted) { string l = Encoding.ASCII.GetString(d, o, 11).Trim(); if (l.Length > 0) Label = l; }
                    continue;
                }
                if (b0 == '.' && (d[o + 1] == ' ' || d[o + 1] == '.')) continue;   // "." and ".."

                bool isDir = (attr & 0x10) != 0;
                uint start = U16(d, o + 0x1A);
                if (FatBits == 32) start |= (uint)U16(d, o + 0x14) << 16;
                long size = U32(d, o + 0x1C);
                string name = longName ?? ShortName(d, o, deleted);

                if (deleted || job.Deleted)
                {
                    var it = new DeletedItem
                    {
                        Rec = nextId++,
                        Name = name,
                        Path = job.Path,
                        IsDir = isDir,
                        Size = isDir ? 0 : size,
                        HasData = !isDir && size > 0,
                        Created = FromDos(U16(d, o + 0x10), U16(d, o + 0x0E), d[o + 0x0D] * 10),
                        Modified = FromDos(U16(d, o + 0x18), U16(d, o + 0x16)),
                        InDeletedFolder = job.Deleted,
                        Guessed = !isDir && size > 0,
                        Ref = "directory entry · first cluster " + start,
                    };
                    if (!isDir && size > 0)
                    {
                        uint s = start;
                        if (FatBits == 32 && (s < 2 || s >= TotalClusters + 2)) s = U16(d, o + 0x1A);   // some systems clear the high word
                        if (s >= 2 && s < TotalClusters + 2)
                        {
                            long need = (size + ClusterSize - 1) / ClusterSize;
                            need = Math.Min(need, TotalClusters + 2 - s);
                            it.Frags.Add(new DataFrag { StartVcn = 0, Runs = new List<Run> { new Run(0, s, need) } });
                        }
                    }
                    Items.Add(it);
                    FoundCount++;
                    if (job.Owner != null)
                    {
                        if (job.Owner.Children == null) job.Owner.Children = new List<DeletedItem>();
                        job.Owner.Children.Add(it);
                    }
                    if (isDir && start >= 2)
                        queue.Enqueue(new DirJob { Path = job.Path + name + "\\", Deleted = true, Start = start, Owner = it });
                }
                else if (isDir && start >= 2)
                {
                    queue.Enqueue(new DirJob { Path = job.Path + name + "\\", Start = start });
                }
            }
        }

        /// <summary>The long-name checksum covers all 11 short-name bytes. A deleted entry has lost its first
        /// byte, so accept the long name if any first byte makes the checksum match.</summary>
        static bool ChecksumMatches(byte[] d, int o, int expected, bool deleted)
        {
            if (expected < 0) return false;
            if (!deleted) return ShortChecksum(d, o, d[o]) == expected;
            for (int c = 0x20; c < 0x100; c++) if (ShortChecksum(d, o, (byte)c) == expected) return true;
            return false;
        }

        static int ShortChecksum(byte[] d, int o, byte first)
        {
            int sum = 0;
            for (int i = 0; i < 11; i++)
            {
                byte c = i == 0 ? first : d[o + i];
                sum = (((sum & 1) << 7) + (sum >> 1) + c) & 0xFF;
            }
            return sum;
        }

        static string ShortName(byte[] d, int o, bool deleted)
        {
            var nb = new byte[8];
            Buffer.BlockCopy(d, o, nb, 0, 8);
            if (deleted) nb[0] = (byte)'_';                  // the first character was overwritten by 0xE5
            else if (nb[0] == 0x05) nb[0] = 0xE5;            // escaped 0xE5 lead byte (Kanji)
            string n = Encoding.GetEncoding(437).GetString(nb).TrimEnd();
            string e = Encoding.GetEncoding(437).GetString(d, o + 8, 3).TrimEnd();
            byte caseFlags = d[o + 0x0C];
            if ((caseFlags & 0x08) != 0) n = n.ToLowerInvariant();
            if ((caseFlags & 0x10) != 0) e = e.ToLowerInvariant();
            return e.Length > 0 ? n + "." + e : n;
        }
    }
}
