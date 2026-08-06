using System;
using System.IO;
using System.Runtime.InteropServices;

namespace OpenRevelare.Gui.Services;

/// <summary>
/// How much physical memory the machine actually has FREE right now.
///
/// Distinct from <c>GC.GetGCMemoryInfo().TotalAvailableMemoryBytes</c>, which reports the
/// ceiling this process is allowed to reach (total RAM, or the container limit) and never
/// moves. Sizing decode concurrency off that number assumes the whole machine is ours: it
/// picks the same three slots whether the user has 40 GB free or is running a browser, a
/// game and Lightroom and has 3 GB left. What matters for "can I afford another 1.2 GB
/// decode right now" is the free figure, and only the OS knows it.
///
/// Windows and Linux get a real answer. macOS has no cheap equivalent (the free-page count
/// lives behind mach host_statistics64, and its "free" is misleading anyway once the unified
/// buffer cache is counted), so it falls back to the total — callers must treat
/// <see cref="TryGetAvailableBytes"/> returning false as "assume the old total-based rule".
/// </summary>
internal static class SystemMemory
{
    /// <summary>Free physical memory in bytes, or false when this platform cannot say.</summary>
    public static bool TryGetAvailableBytes(out long bytes)
    {
        bytes = 0;
        try
        {
            if (OperatingSystem.IsWindows()) return TryWindows(out bytes);
            if (OperatingSystem.IsLinux()) return TryLinux(out bytes);
        }
        catch
        {
            // A memory probe must never be the thing that breaks an import.
        }
        return false;
    }

    /// <summary>Total physical memory (or the container's cap) — always available.</summary>
    public static long TotalBytes()
    {
        long total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return total > 0 ? total : 8L << 30;   // unknown → assume a small machine
    }

    // ── Windows ──────────────────────────────────────────────────────────────────
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    private static bool TryWindows(out long bytes)
    {
        var s = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref s)) { bytes = 0; return false; }
        bytes = (long)s.ullAvailPhys;
        return bytes > 0;
    }

    // ── Linux ────────────────────────────────────────────────────────────────────
    /// <summary>
    /// /proc/meminfo's MemAvailable — the kernel's own estimate of what a new allocation can
    /// get without swapping. Deliberately NOT MemFree, which excludes reclaimable page cache
    /// and would read as "almost nothing free" on any machine that has been up a while.
    /// </summary>
    private static bool TryLinux(out long bytes)
    {
        bytes = 0;
        foreach (string line in File.ReadLines("/proc/meminfo"))
        {
            if (!line.StartsWith("MemAvailable:", StringComparison.Ordinal)) continue;
            string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && long.TryParse(parts[1], out long kb))
            {
                bytes = kb * 1024;
                return bytes > 0;
            }
            break;
        }
        return false;
    }
}
