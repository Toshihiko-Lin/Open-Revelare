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
/// All three desktop platforms now answer. Callers must still treat
/// <see cref="TryGetAvailableBytes"/> returning false as "assume the old total-based rule",
/// since every probe is allowed to fail.
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
            if (OperatingSystem.IsMacOS()) return TryMacOS(out bytes);
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

    // ── macOS ────────────────────────────────────────────────────────────────────
    /// <summary>How much of the reclaimable pool to believe. Half is a judgement call, not a
    /// measured constant: it keeps a warm 32 GB Mac well above the old fixed 3 slots while
    /// stopping an idle 16 GB one from committing nearly all of RAM.</summary>
    private const double ReclaimableShare = 0.5;

    [DllImport("libc", SetLastError = true)]
    private static extern int sysctlbyname(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        out ulong oldp, ref nuint oldlenp, IntPtr newp, nuint newlen);

    /// <summary>One unsigned sysctl integer, false if the key is absent or is neither 4 nor 8
    /// bytes wide.</summary>
    private static bool Sysctl(string name, out ulong value)
    {
        nuint len = sizeof(ulong);
        value = 0;
        // Width matters: these vm.* keys are uint32 on some releases and uint64 on others.
        // `out ulong` zeroes first, so a 4-byte write lands in the low half on little-endian
        // (every Mac, Intel and Apple Silicon alike) and reads back correctly either way.
        // Anything else — a missing key, a wider struct — is rejected rather than misread.
        return sysctlbyname(name, out value, ref len, IntPtr.Zero, 0) == 0
            && len is sizeof(uint) or sizeof(ulong);
    }

    /// <summary>
    /// macOS has no single "available" counter, so this reconstructs one from the VM page
    /// statistics that <c>sysctlbyname</c> exposes — the same numbers mach's
    /// <c>host_statistics64</c> returns, without paying for the mach port.
    ///
    /// Deliberately NOT <c>vm.page_free_count</c> alone. That is the exact analogue of Linux's
    /// MemFree, which the branch above rejects for good reason, and macOS is the worst case for
    /// it: the unified buffer cache grows to fill whatever is idle, so free pages sit near zero
    /// on any Mac that has been awake for an hour. Reading that as "no memory available" would
    /// pin every Mac to one decode slot forever — a regression from the total-based fallback
    /// this replaces, not a fix.
    ///
    /// What the kernel can hand a new allocation without swapping is free + speculative
    /// (read-ahead pages nobody has claimed) + purgeable (volatile caches, dropped on demand)
    /// + external (clean file-backed pages, reclaimable by dropping them). Inactive pages that
    /// are NOT external are dirty anonymous memory: reclaiming those means a swap write, which
    /// is the stall we are sizing concurrency to avoid, so they are left out.
    ///
    /// The reclaimable part is then discounted (<see cref="ReclaimableShare"/>) rather than
    /// counted in full. "Reclaimable" is not "free": evicting clean file pages costs the
    /// re-read, and on a freshly booted 16 GB Mac the undiscounted sum reads ~11.8 GB, which
    /// sizes 8 concurrent decodes — ~9.6 GB committed on a 16 GB machine, the swap storm this
    /// whole gate exists to avoid. Free and speculative pages are counted whole; they are
    /// genuinely unclaimed.
    /// </summary>
    private static bool TryMacOS(out long bytes)
    {
        bytes = 0;
        if (!Sysctl("hw.pagesize", out ulong pageSize) || pageSize == 0) return false;
        if (!Sysctl("vm.page_free_count", out ulong free)) return false;

        // Only the free count is required. The rest are refinements: each has been present for
        // many releases, but a probe that hard-fails on one missing key would silently drop the
        // whole platform back to the total-based estimate, which is what P5 was about.
        ulong outright = free;
        if (Sysctl("vm.page_speculative_count", out ulong spec)) outright += spec;

        ulong reclaimable = 0;
        if (Sysctl("vm.page_purgeable_count", out ulong purge)) reclaimable += purge;
        if (Sysctl("vm.page_external_count", out ulong ext)) reclaimable += ext;

        ulong pages = outright + (ulong)(reclaimable * ReclaimableShare);

        // Page counts are wildly below this bound on real hardware, so tripping it means a
        // garbage read. Rejected rather than clamped: a clamp would wrap into a small positive
        // that reads as a plausible answer.
        if (pages > (ulong)long.MaxValue / pageSize) return false;

        bytes = (long)(pages * pageSize);
        return bytes > 0;
    }
}
