using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Server.Admin
{
    /// <summary>Facts about the PC the server runs on, for the "machine" block of GET status. Every figure is null when it cannot be read.</summary>
    public sealed class MachineInfo
    {
        public long? RamTotalBytes { get; set; }
        public long? RamFreeBytes { get; set; }
        public double? OsUptimeSeconds { get; set; }
        public long? DiskFreeBytes { get; set; }
        public long? DiskTotalBytes { get; set; }
        public string DiskRoot { get; set; }
    }

    public static class MachineProbe
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
            public ulong AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

        /// <summary>Reads RAM, OS uptime and the free space of the drive holding <paramref name="serverFolder"/>. Never throws.</summary>
        public static MachineInfo Read(string serverFolder)
        {
            var info = new MachineInfo();
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    var memory = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
                    if (GlobalMemoryStatusEx(ref memory))
                    {
                        info.RamTotalBytes = (long)memory.TotalPhys;
                        info.RamFreeBytes = (long)memory.AvailPhys;
                    }

                    // TickCount64 counts milliseconds since the OS started (it does include time spent asleep on current Windows versions).
                    info.OsUptimeSeconds = Math.Round(Environment.TickCount64 / 1000.0, 1);
                }
            }
            catch (Exception)
            {
                // P/Invoke problems leave the RAM figures null.
            }

            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(string.IsNullOrEmpty(serverFolder) ? AppDomain.CurrentDomain.BaseDirectory : serverFolder));
                if (!string.IsNullOrEmpty(root))
                {
                    var drive = new DriveInfo(root);
                    info.DiskRoot = root;
                    info.DiskFreeBytes = drive.AvailableFreeSpace;
                    info.DiskTotalBytes = drive.TotalSize;
                }
            }
            catch (Exception)
            {
                // A drive that cannot be queried (network share, unplugged) leaves the disk figures null.
            }

            return info;
        }
    }
}
