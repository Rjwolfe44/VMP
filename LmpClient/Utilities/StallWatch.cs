using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;

namespace LmpClient.Utilities
{
    /// <summary>
    /// Cheap main-thread timing helpers. A slow operation (or a slow frame) is written to KSP.log with a label, so a
    /// player's log shows what froze the game instead of leaving a silent gap.
    /// </summary>
    public static class StallWatch
    {
        public const float FrameStallSeconds = 1.5f;
        private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

        /// <summary>This process' and the machine's memory at one moment. Fields stay 0 where the OS call failed.</summary>
        public struct MemorySnapshot
        {
            public long ManagedHeapMb;
            public long WorkingSetMb;
            public long PrivateMb;
            public uint PageFaults;
            public long RamFreeMb;
            public long RamTotalMb;
            public long CommitFreeMb;
            public long CommitTotalMb;
            public bool HasProcess;
            public bool HasMachine;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessMemoryCountersEx
        {
            public uint cb;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize;
            public UIntPtr WorkingSetSize;
            public UIntPtr QuotaPeakPagedPoolUsage;
            public UIntPtr QuotaPagedPoolUsage;
            public UIntPtr QuotaPeakNonPagedPoolUsage;
            public UIntPtr QuotaNonPagedPoolUsage;
            public UIntPtr PagefileUsage;
            public UIntPtr PeakPagefileUsage;
            public UIntPtr PrivateUsage;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessMemoryInfo(IntPtr process, out ProcessMemoryCountersEx counters, uint size);

        public static long Start()
        {
            return Stopwatch.GetTimestamp();
        }

        public static double ElapsedMs(long start)
        {
            return (Stopwatch.GetTimestamp() - start) * TicksToMs;
        }

        /// <summary>
        /// Logs the operation when it took at least <paramref name="thresholdMs"/>. Returns the elapsed milliseconds.
        /// </summary>
        public static double End(long start, string label, double thresholdMs)
        {
            var ms = ElapsedMs(start);
            if (ms >= thresholdMs)
            {
                LunaLog.Log($"[VMP]: Slow operation: {label} took {ms:F0} ms");
            }

            return ms;
        }

        /// <summary>
        /// Call once per frame on the main thread. Logs a frame that took longer than <see cref="FrameStallSeconds"/>
        /// together with the memory state, which is what separates a mod/VMP stall from the OS paging.
        /// </summary>
        public static void CheckFrame()
        {
            var seconds = Time.unscaledDeltaTime;
            if (seconds < FrameStallSeconds)
            {
                return;
            }

            LunaLog.Log($"[VMP]: Frame stall {seconds:F1} s in {HighLogic.LoadedScene}. {MemoryText(GetMemory())}");
            ClientHealth.NoteFrameStall(seconds);
        }

        /// <summary>
        /// Managed heap, this process' working set / private bytes / page faults and the machine's free RAM. Never throws
        /// (a missing Windows API leaves the matching fields at 0).
        /// </summary>
        public static MemorySnapshot GetMemory()
        {
            var snapshot = new MemorySnapshot { ManagedHeapMb = GC.GetTotalMemory(false) / (1024 * 1024) };

            try
            {
                var process = new ProcessMemoryCountersEx { cb = (uint)Marshal.SizeOf(typeof(ProcessMemoryCountersEx)) };
                if (GetProcessMemoryInfo(GetCurrentProcess(), out process, process.cb))
                {
                    snapshot.HasProcess = true;
                    snapshot.WorkingSetMb = (long)(process.WorkingSetSize.ToUInt64() / (1024 * 1024));
                    snapshot.PrivateMb = (long)(process.PrivateUsage.ToUInt64() / (1024 * 1024));
                    snapshot.PageFaults = process.PageFaultCount;
                }

                var machine = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf(typeof(MemoryStatusEx)) };
                if (GlobalMemoryStatusEx(ref machine))
                {
                    snapshot.HasMachine = true;
                    snapshot.RamFreeMb = (long)(machine.ullAvailPhys / (1024 * 1024));
                    snapshot.RamTotalMb = (long)(machine.ullTotalPhys / (1024 * 1024));
                    snapshot.CommitFreeMb = (long)(machine.ullAvailPageFile / (1024 * 1024));
                    snapshot.CommitTotalMb = (long)(machine.ullTotalPageFile / (1024 * 1024));
                }
            }
            catch (Exception)
            {
                // Not Windows, or the API is blocked: report what we have.
            }

            return snapshot;
        }

        /// <summary>The memory text used in "Frame stall" log lines.</summary>
        public static string MemoryText(MemorySnapshot memory)
        {
            var text = $"managed heap {memory.ManagedHeapMb} MB";
            if (memory.HasProcess)
            {
                text += $", KSP working set {memory.WorkingSetMb} MB, private {memory.PrivateMb} MB, page faults {memory.PageFaults}";
            }

            if (memory.HasMachine)
            {
                text += $", RAM free {memory.RamFreeMb} of {memory.RamTotalMb} MB, commit free {memory.CommitFreeMb} of {memory.CommitTotalMb} MB";
            }

            if (!memory.HasProcess && !memory.HasMachine)
            {
                text += ", process memory unavailable";
            }

            return text;
        }
    }
}
