using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Undelete
{
    sealed class DirNode
    {
        public string Name;
        public uint Parent;
        public ushort ParentSeq, Seq;
    }

    sealed class ParsedRecord
    {
        public ushort Flags, Seq;
        public ulong BaseRef;
        public string Name;
        public int NameNs = -1;
        public ulong ParentRef;
        public long FnSize;
        public long CTime, MTime, ChTime;
        public bool HasStd;
        public List<DataFrag> Data = new List<DataFrag>();
        public long DataSize = -1, InitSize = -1;
        public ushort DataFlags;
        public byte[] Resident;
        public bool HasData;
        public byte[] AttrList;
        public bool InUse { get { return (Flags & 1) != 0; } }
        public bool IsDir { get { return (Flags & 2) != 0; } }
    }

    /// <summary>
    /// Read-only NTFS reader: walks the MFT looking for FILE records whose "in use" flag is cleared,
    /// and rebuilds their paths from the parent references.
    /// </summary>
    sealed class NtfsVolume : FsVolume
    {
        public int BytesPerSector, RecordSize;
        public long MftLcn, MftBytes;

        List<Run> mftRuns;
        byte[] bitmap;

        public readonly Dictionary<uint, DirNode> Dirs = new Dictionary<uint, DirNode>();
        public readonly Dictionary<uint, DeletedItem> Deleted = new Dictionary<uint, DeletedItem>();

        public NtfsVolume(Disk disk) : base(disk) { Init(); }

        public override string FsName { get { return "NTFS"; } }
        public override string StructureInfo { get { return Fmt.N(RecordsTotal) + " mft records"; } }
        public override string ProgressUnit { get { return "mft records"; } }

        protected override long ClusterOffset(long lcn) { return lcn * ClusterSize; }
        protected override bool LcnValid(long lcn, long len) { return lcn >= 0 && lcn + len <= TotalClusters + 1; }
        protected override bool HasAllocationMap { get { return bitmap != null; } }
        protected override bool ClusterInUse(long lcn)
        {
            if (lcn < 0 || lcn >= TotalClusters) return true;
            return (bitmap[lcn >> 3] & (1 << (int)(lcn & 7))) != 0;
        }

        // ───────────────────────────── boot sector + $MFT bootstrap ─────────────────────────────

        void Init()
        {
            byte[] bs = Disk.Read(0, 512);
            BytesPerSector = U16(bs, 0x0B);
            int spc = bs[0x0D];
            if (spc > 0x80) spc = 1 << (256 - spc);
            ClusterSize = BytesPerSector * spc;
            TotalClusters = I64(bs, 0x28) / Math.Max(1, spc);
            MftLcn = I64(bs, 0x30);
            sbyte cpr = (sbyte)bs[0x40];
            RecordSize = cpr > 0 ? cpr * ClusterSize : 1 << (-cpr);

            if (BytesPerSector < 256 || ClusterSize <= 0 || RecordSize < 512 || RecordSize > 65536 ||
                MftLcn <= 0 || MftLcn >= TotalClusters)
                throw new InvalidDataException("NTFS boot sector geometry looks invalid.");

            // Bootstrap: the first records of $MFT always sit at MftLcn.
            mftRuns = new List<Run> { new Run(0, MftLcn, Math.Max(1, (64L * RecordSize + ClusterSize - 1) / ClusterSize)) };
            byte[] r0 = ReadRecord(0);
            if (r0 == null) throw new InvalidDataException("$MFT record 0 is unreadable.");
            ParsedRecord mft = Parse(r0, 0, RecordSize, true);
            var frags = new List<DataFrag>(mft.Data);

            // Heavily fragmented $MFT: further $DATA extents live in extension records named by $ATTRIBUTE_LIST.
            if (mft.AttrList != null)
            {
                var bootstrapRuns = AllFragRuns(frags);
                foreach (uint ext in AttrListRecords(mft.AttrList, 0x80))
                {
                    if (ext == 0) continue;
                    var saved = mftRuns; mftRuns = bootstrapRuns;
                    byte[] er = ReadRecord(ext);
                    mftRuns = saved;
                    if (er == null) continue;
                    foreach (var f in Parse(er, 0, RecordSize, true).Data)
                        if (!frags.Any(x => x.StartVcn == f.StartVcn)) frags.Add(f);
                }
            }
            if (frags.Count == 0 || mft.DataSize <= 0) throw new InvalidDataException("$MFT has no data runs.");
            mftRuns = AllFragRuns(frags);
            MftBytes = mft.DataSize;
            RecordsTotal = MftBytes / RecordSize;

            try
            {
                byte[] r3 = ReadRecord(3);      // $Volume: label is attribute 0x60
                if (r3 != null) Label = ReadResidentString(r3, 0x60) ?? "";
            }
            catch { }
        }

        static List<Run> AllFragRuns(List<DataFrag> frags)
        {
            var runs = new List<Run>();
            foreach (var f in frags) runs.AddRange(f.Runs);
            runs.Sort((a, b) => a.Vcn.CompareTo(b.Vcn));
            return runs;
        }

        static IEnumerable<uint> AttrListRecords(byte[] al, uint type)
        {
            int p = 0;
            while (p + 0x1A <= al.Length)
            {
                uint t = U32(al, p);
                int len = U16(al, p + 4);
                if (len < 0x1A) yield break;
                if (t == type) yield return (uint)(U64(al, p + 0x10) & 0xFFFFFFFFFFFFUL);
                p += len;
            }
        }

        string ReadResidentString(byte[] b, uint type)
        {
            int a = U16(b, 0x14);
            while (a + 16 <= RecordSize)
            {
                uint t = U32(b, a);
                int len = (int)U32(b, a + 4);
                if (t == 0xFFFFFFFF || len < 16 || a + len > RecordSize) break;
                if (t == type && b[a + 8] == 0)
                {
                    int clen = (int)U32(b, a + 0x10), coff = U16(b, a + 0x14);
                    if (a + coff + clen <= RecordSize) return Encoding.Unicode.GetString(b, a + coff, clen);
                }
                a += len;
            }
            return null;
        }

        // ───────────────────────────── record reading ─────────────────────────────

        public byte[] ReadRecord(long n)
        {
            byte[] b = new byte[RecordSize];
            ReadVirtual(mftRuns, n * RecordSize, b, 0, RecordSize);
            return Fixup(b, 0, RecordSize) ? b : null;
        }

        /// <summary>Applies the NTFS update-sequence fixups. Returns false for non-FILE or torn records.</summary>
        static bool Fixup(byte[] b, int o, int size)
        {
            if (b[o] != 'F' || b[o + 1] != 'I' || b[o + 2] != 'L' || b[o + 3] != 'E') return false;
            int usaOff = U16(b, o + 4), usaCnt = U16(b, o + 6);
            if (usaCnt < 2 || usaOff < 0x28 || usaOff + usaCnt * 2 > size) return false;
            ushort usn = U16(b, o + usaOff);
            for (int i = 1; i < usaCnt; i++)
            {
                int p = o + i * 512 - 2;
                if (p + 2 > o + size) break;
                if (U16(b, p) != usn) return false;
                b[p] = b[o + usaOff + i * 2];
                b[p + 1] = b[o + usaOff + i * 2 + 1];
            }
            return true;
        }

        static ParsedRecord Parse(byte[] b, int o, int size, bool wantData)
        {
            var r = new ParsedRecord();
            r.Seq = U16(b, o + 0x10);
            r.Flags = U16(b, o + 0x16);
            r.BaseRef = U64(b, o + 0x20);
            int a = o + U16(b, o + 0x14);
            int end = o + size;

            while (a + 16 <= end)
            {
                uint type = U32(b, a);
                if (type == 0xFFFFFFFF || type == 0) break;
                int len = (int)U32(b, a + 4);
                if (len < 16 || a + len > end) break;
                bool nonres = b[a + 8] != 0;
                int nameLen = b[a + 9];

                if (!nonres)
                {
                    int clen = (int)U32(b, a + 0x10);
                    int coff = U16(b, a + 0x14);
                    int c = a + coff;
                    if (coff >= 0x18 && clen >= 0 && c + clen <= a + len)
                    {
                        if (type == 0x10 && clen >= 0x20)
                        {
                            r.HasStd = true;
                            r.CTime = I64(b, c); r.MTime = I64(b, c + 8); r.ChTime = I64(b, c + 0x10);
                        }
                        else if (type == 0x30 && clen >= 0x42)
                        {
                            int fl = b[c + 0x40], ns = b[c + 0x41];
                            if (0x42 + fl * 2 <= clen && fl > 0 && BetterNs(ns, r.NameNs))
                            {
                                r.Name = Encoding.Unicode.GetString(b, c + 0x42, fl * 2);
                                r.NameNs = ns;
                                r.ParentRef = U64(b, c);
                                r.FnSize = I64(b, c + 0x30);
                            }
                        }
                        else if (type == 0x80 && nameLen == 0 && wantData)
                        {
                            r.HasData = true;
                            r.Resident = new byte[clen];
                            Buffer.BlockCopy(b, c, r.Resident, 0, clen);
                            r.DataSize = clen;
                            r.DataFlags = U16(b, a + 0x0C);
                        }
                        else if (type == 0x20)
                        {
                            r.AttrList = new byte[clen];
                            Buffer.BlockCopy(b, c, r.AttrList, 0, clen);
                        }
                    }
                }
                else if (type == 0x80 && nameLen == 0 && wantData && len >= 0x40)
                {
                    long svcn = I64(b, a + 0x10);
                    int roff = U16(b, a + 0x20);
                    r.HasData = true;
                    if (svcn == 0)
                    {
                        r.DataSize = I64(b, a + 0x30);
                        r.InitSize = I64(b, a + 0x38);
                        r.DataFlags = U16(b, a + 0x0C);
                    }
                    if (roff >= 0x40 && roff < len)
                        r.Data.Add(new DataFrag { StartVcn = svcn, Runs = ParseRuns(b, a + roff, a + len, svcn) });
                }
                a += len;
            }
            return r;
        }

        // Prefer Win32 / POSIX names over the 8.3 DOS name.
        static bool BetterNs(int ns, int cur) { return cur < 0 || (cur == 2 && ns != 2); }

        static List<Run> ParseRuns(byte[] b, int p, int end, long startVcn)
        {
            var list = new List<Run>();
            long lcn = 0, vcn = startVcn;
            while (p < end)
            {
                byte h = b[p];
                if (h == 0) break;
                int ls = h & 0xF, os = h >> 4;
                p++;
                if (ls == 0 || ls > 8 || os > 8 || p + ls + os > end) break;
                long len = 0;
                for (int i = 0; i < ls; i++) len |= (long)b[p + i] << (8 * i);
                p += ls;
                if (len <= 0) break;
                if (os == 0)
                {
                    list.Add(new Run(vcn, -1, len));
                }
                else
                {
                    long off = 0;
                    for (int i = 0; i < os; i++) off |= (long)b[p + i] << (8 * i);
                    if (os < 8 && (b[p + os - 1] & 0x80) != 0) off |= -1L << (8 * os);
                    p += os;
                    lcn += off;
                    list.Add(new Run(vcn, lcn, len));
                }
                vcn += len;
            }
            return list;
        }

        // ───────────────────────────── the scan ─────────────────────────────

        public override void Scan()
        {
            Phase = "reading master file table";
            int perChunk = Math.Max(1, (4 << 20) / RecordSize);
            byte[] buf = new byte[perChunk * RecordSize];
            var extFrags = new Dictionary<uint, List<DataFrag>>();

            for (long n = 0; n < RecordsTotal && !Cancel; n += perChunk)
            {
                int cnt = (int)Math.Min(perChunk, RecordsTotal - n);
                ReadVirtual(mftRuns, n * RecordSize, buf, 0, cnt * RecordSize);

                for (int i = 0; i < cnt; i++)
                {
                    int o = i * RecordSize;
                    uint num = (uint)(n + i);
                    if (buf[o] != (byte)'F') continue;
                    ushort flags = U16(buf, o + 0x16);
                    bool inUse = (flags & 1) != 0, isDir = (flags & 2) != 0;
                    ulong baseRef = U64(buf, o + 0x20);

                    if (inUse && (!isDir || baseRef != 0)) continue;   // live file: nothing to do
                    if (!Fixup(buf, o, RecordSize)) continue;

                    if (baseRef != 0)
                    {
                        // freed extension record: may hold extra data runs of a deleted fragmented file
                        var px = Parse(buf, o, RecordSize, true);
                        if (px.Data.Count > 0)
                        {
                            uint bnum = (uint)(baseRef & 0xFFFFFFFFFFFFUL);
                            List<DataFrag> l;
                            if (!extFrags.TryGetValue(bnum, out l)) extFrags[bnum] = l = new List<DataFrag>();
                            l.AddRange(px.Data);
                        }
                        continue;
                    }

                    if (inUse)
                    {
                        var pd = Parse(buf, o, RecordSize, false);
                        if (pd.Name != null)
                            Dirs[num] = new DirNode
                            {
                                Name = pd.Name,
                                Parent = (uint)(pd.ParentRef & 0xFFFFFFFFFFFFUL),
                                ParentSeq = (ushort)(pd.ParentRef >> 48),
                                Seq = pd.Seq
                            };
                        continue;
                    }

                    if (num < 24) continue;     // reserved system records
                    var p = Parse(buf, o, RecordSize, true);
                    if (p.Name == null) continue;

                    var it = new DeletedItem
                    {
                        Rec = num,
                        Seq = p.Seq,
                        IsDir = isDir,
                        Name = p.Name,
                        Parent = (uint)(p.ParentRef & 0xFFFFFFFFFFFFUL),
                        ParentSeq = (ushort)(p.ParentRef >> 48),
                        HasData = p.HasData,
                        Resident = p.Resident,
                        Frags = p.Data,
                        DataFlags = p.DataFlags,
                        InitSize = p.InitSize,
                        Size = p.DataSize >= 0 ? p.DataSize : (isDir ? 0 : Math.Max(0, p.FnSize)),
                        Ref = "mft record #" + num,
                    };
                    if (p.HasStd)
                    {
                        it.Created = FromFt(p.CTime);
                        it.Modified = FromFt(p.MTime);
                        it.Changed = FromFt(p.ChTime);
                    }
                    Deleted[num] = it;
                    FoundCount++;
                }
                RecordsScanned = n + cnt;
            }
            if (Cancel) return;

            foreach (var kv in extFrags)
            {
                DeletedItem it;
                if (!Deleted.TryGetValue(kv.Key, out it) || it.Resident != null) continue;
                foreach (var f in kv.Value)
                    if (!it.Frags.Any(x => x.StartVcn == f.StartVcn)) it.Frags.Add(f);
            }

            Phase = "loading cluster bitmap";
            LoadBitmap();

            Phase = "rebuilding folder paths";
            foreach (var it in Deleted.Values)
            {
                bool orphan, viaDeleted;
                it.Path = BuildPath(it.Parent, it.ParentSeq, out orphan, out viaDeleted);
                it.Orphan = orphan;
                it.InDeletedFolder = viaDeleted;

                DeletedItem pd;
                if (Deleted.TryGetValue(it.Parent, out pd) && pd.IsDir && SeqMatchesDeleted(pd.Seq, it.ParentSeq))
                {
                    if (pd.Children == null) pd.Children = new List<DeletedItem>();
                    pd.Children.Add(it);
                }
            }

            Phase = "checking which clusters were reused";
            foreach (var it in Deleted.Values) Assess(it);

            Items = Deleted.Values.ToList();
            Phase = "done";
        }

        // A freed record's sequence number is bumped on deletion, so a child may reference seq or seq-1.
        static bool SeqMatchesDeleted(ushort recSeq, ushort refSeq)
        {
            return recSeq == refSeq || recSeq == (ushort)(refSeq + 1);
        }

        string BuildPath(uint p, ushort ps, out bool orphan, out bool viaDeleted)
        {
            var parts = new List<string>();
            orphan = false; viaDeleted = false;
            for (int depth = 0; depth < 128; depth++)
            {
                if (p == 5) break;    // root directory
                DirNode dn; DeletedItem dd;
                if (Dirs.TryGetValue(p, out dn) && dn.Seq == ps)
                {
                    parts.Add(dn.Name); p = dn.Parent; ps = dn.ParentSeq;
                }
                else if (Deleted.TryGetValue(p, out dd) && dd.IsDir && SeqMatchesDeleted(dd.Seq, ps))
                {
                    parts.Add(dd.Name); p = dd.Parent; ps = dd.ParentSeq; viaDeleted = true;
                }
                else { orphan = true; break; }
                if (depth == 127) orphan = true;
            }
            parts.Reverse();
            var sb = new StringBuilder("\\");
            foreach (var s in parts) sb.Append(s).Append('\\');
            return sb.ToString();
        }

        void LoadBitmap()
        {
            try
            {
                byte[] r6 = ReadRecord(6);
                if (r6 == null) return;
                var p = Parse(r6, 0, RecordSize, true);
                long bytes = (TotalClusters + 7) / 8;
                if (bytes <= 0 || bytes > int.MaxValue) return;
                bitmap = new byte[bytes];
                if (p.Resident != null) { Buffer.BlockCopy(p.Resident, 0, bitmap, 0, (int)Math.Min(bytes, p.Resident.Length)); return; }
                var runs = AllFragRuns(p.Data);
                const int chunk = 8 << 20;
                for (long off = 0; off < bytes; off += chunk)
                {
                    int c = (int)Math.Min(chunk, bytes - off);
                    byte[] tmp = new byte[c];
                    ReadVirtual(runs, off, tmp, 0, c);
                    Buffer.BlockCopy(tmp, 0, bitmap, (int)off, c);
                }
            }
            catch (OutOfMemoryException) { bitmap = null; }
        }

        /// <summary>Diagnostics: loads any single MFT record (live or deleted) as an item.</summary>
        public DeletedItem LoadRecord(uint num)
        {
            byte[] b = ReadRecord(num);
            if (b == null) return null;
            var p = Parse(b, 0, RecordSize, true);
            var it = new DeletedItem
            {
                Rec = num, Seq = p.Seq, IsDir = p.IsDir, Name = p.Name ?? "",
                HasData = p.HasData, Resident = p.Resident, Frags = p.Data, DataFlags = p.DataFlags,
                InitSize = p.InitSize, Size = Math.Max(0, p.DataSize), Ref = "mft record #" + num
            };
            if (p.HasStd) { it.Created = FromFt(p.CTime); it.Modified = FromFt(p.MTime); }
            return it;
        }
    }
}
