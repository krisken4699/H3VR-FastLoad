using System;
using System.Runtime.InteropServices;

namespace FastLoad.Early
{
    /// <summary>Process and system memory numbers straight from Windows. All values in bytes.</summary>
    public static class Memory
    {
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

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool GetProcessMemoryInfo(IntPtr process, out ProcessMemoryCountersEx counters, uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

        private static readonly uint CountersSize = (uint)Marshal.SizeOf(typeof(ProcessMemoryCountersEx));
        private static readonly uint StatusSize = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));

        /// <summary>Private bytes this process has committed (what counts against the page file); 0 if unreadable.</summary>
        public static long Commit()
        {
            try
            {
                ProcessMemoryCountersEx c;
                if (!GetProcessMemoryInfo(GetCurrentProcess(), out c, CountersSize)) return 0;
                return (long)c.PrivateUsage.ToUInt64();
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <param name="sysCommitFree">How much more the whole system can still commit before allocations fail.</param>
        /// <param name="sysCommitLimit">RAM plus page files.</param>
        public static bool Read(out long commit, out long workingSet, out long sysCommitFree, out long sysCommitLimit)
        {
            commit = 0;
            workingSet = 0;
            sysCommitFree = 0;
            sysCommitLimit = 0;
            try
            {
                ProcessMemoryCountersEx c;
                if (!GetProcessMemoryInfo(GetCurrentProcess(), out c, CountersSize)) return false;
                commit = (long)c.PrivateUsage.ToUInt64();
                workingSet = (long)c.WorkingSetSize.ToUInt64();

                MemoryStatusEx s = new MemoryStatusEx();
                s.dwLength = StatusSize;
                if (!GlobalMemoryStatusEx(ref s)) return false;
                sysCommitFree = (long)s.ullAvailPageFile;
                sysCommitLimit = (long)s.ullTotalPageFile;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
