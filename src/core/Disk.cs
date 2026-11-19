using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Undelete
{
    static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec,
                                                       uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadFile(SafeFileHandle h, byte[] buf, int toRead, out int read, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetFilePointerEx(SafeFileHandle h, long dist, out long newPos, uint method);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inLen,
                                                  byte[] outBuf, int outLen, out int returned, IntPtr overlapped);
        [DllImport("kernel32.dll")] public static extern IntPtr GetStdHandle(int n);
        [DllImport("kernel32.dll")] public static extern bool GetConsoleMode(IntPtr h, out uint mode);
        [DllImport("kernel32.dll")] public static extern bool SetConsoleMode(IntPtr h, uint mode);
    }

    /// <summary>
    /// Read-only, sector-aligned access to a raw volume (\\.\C:) or a raw image file.
    /// The handle is opened with GENERIC_READ only: this program never writes to the scanned source.
    /// </summary>
    sealed class Disk : IDisposable
    {
        const int Align = 4096;                 // multiple of every real sector size (512 / 4096)
        const uint GENERIC_READ = 0x80000000u;
        const uint FILE_SHARE_READ_WRITE = 3u;
        const uint OPEN_EXISTING = 3u;

        SafeFileHandle handle;
        public long BaseOffset;            // where the volume starts: physical-disk fallback or a partition inside a disk image
        public string Via;                 // describes the fallback path, if used
        readonly object gate = new object();
        byte[] scratch = new byte[0];

        public readonly string Source;     // what the user typed, e.g. "C:" or "D:\disk.img"
        public readonly bool IsDevice;
        public readonly char DriveLetter;  // '\0' for image files
        public string LastError;           // last low-level read failure (unreadable areas read as zeros)

        public Disk(string source)
        {
            Source = source;
            string p = source.Trim();
            if (p.Length >= 2 && p[1] == ':' && (p.Length == 2 || (p.Length == 3 && (p[2] == '\\' || p[2] == '/'))))
            {
                DriveLetter = char.ToUpperInvariant(p[0]);
                p = @"\\.\" + DriveLetter + ":";
            }

            IsDevice = p.StartsWith(@"\\.\");
            if (!IsDevice && !File.Exists(p)) throw new FileNotFoundException("No such image file: " + p);
            handle = Native.CreateFile(p, GENERIC_READ, FILE_SHARE_READ_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                throw new IOException(err == 5
                    ? "Access denied opening " + p + ". Raw volume access needs Administrator rights."
                    : "Cannot open " + p + " (Win32 error " + err + ").");
            }
            if (IsDevice) EnsureReadable(p);
        }

        /// <summary>
        /// Some volumes refuse direct reads (ERROR_NOT_SUPPORTED, usually a filter driver).
        /// In that case, find where the volume lives on its physical disk and read from there instead.
        /// </summary>
        void EnsureReadable(string volumePath)
        {
            byte[] probe = new byte[Align];
            int n;
            long pos;
            if (Native.SetFilePointerEx(handle, 0, out pos, 0) && Native.ReadFile(handle, probe, Align, out n, IntPtr.Zero) && n > 0)
                return;
            int readErr = Marshal.GetLastWin32Error();

            byte[] ext = new byte[32];   // VOLUME_DISK_EXTENTS with one DISK_EXTENT
            int ret;
            if (!Native.DeviceIoControl(handle, 0x00560000 /*IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS*/, IntPtr.Zero, 0, ext, ext.Length, out ret, IntPtr.Zero)
                || BitConverter.ToUInt32(ext, 0) != 1)
                throw new IOException("Volume " + volumePath + " refuses raw reads (Win32 error " + readErr +
                                      ") and spans multiple disks, so the physical-disk fallback is unavailable.");

            uint diskNo = BitConverter.ToUInt32(ext, 8);
            long start = BitConverter.ToInt64(ext, 16);
            string phys = @"\\.\PhysicalDrive" + diskNo;
            SafeFileHandle ph = Native.CreateFile(phys, GENERIC_READ, FILE_SHARE_READ_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (ph.IsInvalid)
                throw new IOException("Volume " + volumePath + " refuses raw reads (Win32 error " + readErr + ") and " + phys +
                                      " could not be opened (Win32 error " + Marshal.GetLastWin32Error() + ").");
            handle.Dispose();
            handle = ph;
            BaseOffset = start;
            Via = phys + " @ " + start;
        }

        public byte[] Read(long offset, int count)
        {
            byte[] b = new byte[count];
            ReadInto(offset, b, 0, count);
            return b;
        }

        /// <summary>Reads any byte range; unreadable areas come back as zeros.</summary>
        public void ReadInto(long offset, byte[] buf, int index, int count)
        {
            if (count <= 0) return;
            lock (gate)
            {
                long start = offset - (offset % Align);
                long end = offset + count;
                long alignedEnd = (end + Align - 1) / Align * Align;
                int len = (int)(alignedEnd - start);
                if (scratch.Length < len) scratch = new byte[len];

                int got = 0;
                long pos;
                if (!Native.SetFilePointerEx(handle, BaseOffset + start, out pos, 0))
                    LastError = "seek failed (Win32 error " + Marshal.GetLastWin32Error() + ")";
                else if (scratch.Length == len)
                {
                    int n;
                    if (Native.ReadFile(handle, scratch, len, out n, IntPtr.Zero)) got = n;
                    else LastError = "read failed (Win32 error " + Marshal.GetLastWin32Error() + ")";
                }
                else
                {
                    // ReadFile writes from index 0; read into an exact-size buffer
                    byte[] tmp = new byte[len];
                    int n;
                    if (Native.ReadFile(handle, tmp, len, out n, IntPtr.Zero)) { got = n; Buffer.BlockCopy(tmp, 0, scratch, 0, n); }
                    else LastError = "read failed (Win32 error " + Marshal.GetLastWin32Error() + ")";
                }
                if (got < len) Array.Clear(scratch, got, len - got);
                Buffer.BlockCopy(scratch, (int)(offset - start), buf, index, count);
            }
        }

        public void Dispose() { handle.Dispose(); }
    }
}
