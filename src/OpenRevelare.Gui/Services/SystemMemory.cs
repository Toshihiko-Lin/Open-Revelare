namespace OpenRevelare.Gui.Services;

internal static class SystemMemory
{
    public static bool TryGetAvailableBytes(out long bytes) => Core.PhysicalMemory.TryGetAvailableBytes(out bytes);
    public static long TotalBytes() => Core.PhysicalMemory.TotalBytes();
}
