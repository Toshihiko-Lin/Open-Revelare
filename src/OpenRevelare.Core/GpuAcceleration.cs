using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OpenRevelare.Core;

public enum GpuAccelerationMode { Auto, Off, On }
public sealed record GpuWorkloadMeasurement(string Workload, double CpuMs, double GpuMs, bool Profitable);

/// <summary>
/// OS-native compute for shared image operations. Auto measures complete upload/compute/readback
/// against the CPU once per workload bucket, checks parity, and keeps only profitable routes.
/// An occupied device never makes a foreground caller wait behind an export.
/// </summary>
public static class GpuAcceleration
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, GpuWorkloadMeasurement> Decisions = new();
    private static GpuComputeSession? _session;
    private static volatile bool _unavailable;
    private static string? _failure;
    private static long _gpuRuns;
    private static volatile GpuAccelerationMode _mode = Environment.GetEnvironmentVariable("OPENREVELARE_GPU")?.ToLowerInvariant() switch
    {
        "off" => GpuAccelerationMode.Off,
        "on" => GpuAccelerationMode.On,
        _ => GpuAccelerationMode.Auto,
    };

    public static GpuAccelerationMode Mode
    {
        get => _mode;
        set
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            lock (Gate)
            {
                _mode = value;
                Decisions.Clear();
                if (value == GpuAccelerationMode.Off) { _session?.Dispose(); _session = null; }
            }
        }
    }

    /// <summary>Reading diagnostics does not initialize the GPU or compile shaders.</summary>
    public static string Diagnostics => $"{Mode}; backend={Backend}; runs={Interlocked.Read(ref _gpuRuns)}; " +
        (_failure ?? (_session is null ? "not initialized" : "ready"));
    private static string Backend => OperatingSystem.IsWindows() ? "DirectCompute" : OperatingSystem.IsMacOS() ? "Metal" : "CPU";

    public static void ReleaseResources()
    {
        lock (Gate) { _session?.Dispose(); _session = null; }
    }

    public static GpuWorkloadMeasurement[] Measurements()
    {
        lock (Gate) return Decisions.Values.ToArray();
    }

    internal static bool TryResize(ImageBuffer source, int width, int height, Func<float[]> cpu, out float[] result)
    {
        result = null!;
        if ((long)width * height > 100_000_000 || !SupportedDimensions(source)
            || width > 1_048_560 || height > 1_048_560) return false;
        return TryRun($"resize:{Bucket(source.PixelCount)}:{Bucket((long)width * height)}", source.PixelCount,
            () => _session!.Resize(source, width, height), cpu, out result);
    }

    internal static bool TrySharpen(ImageBuffer source, float[] kernel, float amount, float threshold, float limit,
        Func<float[]> cpu, out float[] result)
    {
        result = null!;
        if (!SupportedDimensions(source)) return false;
        return TryRun($"sharpen:{Bucket(source.PixelCount)}:{kernel.Length}", source.PixelCount,
            () => _session!.Sharpen(source, kernel, amount, threshold, limit), cpu, out result);
    }

    private static bool SupportedDimensions(ImageBuffer source) => source.Width <= 1_048_560 && source.Height <= 1_048_560;

    private static int Bucket(long size) => System.Numerics.BitOperations.Log2((ulong)Math.Max(1, size));

    private static bool TryRun(string key, long pixels, Func<float[]> gpu, Func<float[]> cpu, out float[] result)
    {
        result = null!;
        var mode = Mode;
        if (mode == GpuAccelerationMode.Off || _unavailable
            || (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
            || (mode == GpuAccelerationMode.Auto && pixels < 4_000_000)) return false;
        // Bound peak device allocations; very large scans retain the existing CPU route.
        if (pixels > 100_000_000 || !Monitor.TryEnter(Gate)) return false;
        try
        {
            if (_unavailable || Mode == GpuAccelerationMode.Off) return false;
            if (mode == GpuAccelerationMode.Auto && Decisions.TryGetValue(key, out var decision) && !decision.Profitable) return false;
            _session ??= new GpuComputeSession();
            long start = Stopwatch.GetTimestamp();
            float[] accelerated = gpu();
            double gpuMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (!Decisions.ContainsKey(key))
            {
                start = Stopwatch.GetTimestamp();
                float[] reference = cpu();
                double cpuMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if (!Equivalent(reference, accelerated))
                {
                    Disable("GPU output failed CPU parity check");
                    result = reference;
                    return true;
                }
                bool profitable = gpuMs < cpuMs * 0.9;
                Decisions[key] = new(key, cpuMs, gpuMs, profitable);
                if (mode == GpuAccelerationMode.Auto && !profitable)
                {
                    result = reference;
                    return true;
                }
            }
            result = accelerated;
            Interlocked.Increment(ref _gpuRuns);
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
            or InvalidOperationException or OutOfMemoryException)
        {
            Disable(ex.Message);
            return false;
        }
        finally { Monitor.Exit(Gate); }
    }

    private static void Disable(string reason)
    {
        _failure = reason;
        _unavailable = true;
        _session?.Dispose();
        _session = null;
    }

    internal static bool Equivalent(float[] reference, float[] actual)
    {
        if (reference.Length != actual.Length) return false;
        for (int i = 0; i < reference.Length; i++)
        {
            float a = reference[i], b = actual[i];
            if (a.Equals(b)) continue;
            if (!float.IsFinite(a) || !float.IsFinite(b) || MathF.Abs(a - b) > 1e-6f + MathF.Abs(a) * 2e-6f)
                return false;
        }
        return true;
    }
}

/// <summary>Calls are serialized by the scheduler. SafeHandle owns the device and cached pipelines.</summary>
internal sealed class GpuComputeSession : SafeHandleZeroOrMinusOneIsInvalid
{
    internal GpuComputeSession() : base(ownsHandle: true)
    {
        var error = new StringBuilder(4096);
        SetHandle(Native.or_gpu_create(error, error.Capacity));
        if (IsInvalid) throw new InvalidOperationException(error.ToString());
    }

    protected override bool ReleaseHandle() { Native.or_gpu_destroy(handle); return true; }

    internal float[] Resize(ImageBuffer source, int width, int height)
    {
        var (xm, xw) = PackTaps(Resample.AxisTaps(source.Width, width));
        var (ym, yw) = PackTaps(Resample.AxisTaps(source.Height, height));
        var result = new float[checked(width * height * 3)];
        var error = new StringBuilder(4096);
        int status = Native.or_gpu_resize(this, source.Data, source.Width, source.Height, result, width, height,
            xm, xw, xw.Length, ym, yw, yw.Length, error, error.Capacity);
        if (status != 0) throw new InvalidOperationException(error.ToString());
        return result;
    }

    internal float[] Sharpen(ImageBuffer source, float[] kernel, float amount, float threshold, float limit)
    {
        var result = new float[source.Data.Length];
        var error = new StringBuilder(4096);
        int status = Native.or_gpu_sharpen(this, source.Data, result, source.Width, source.Height,
            kernel, kernel.Length, amount, threshold, limit, error, error.Capacity);
        if (status != 0) throw new InvalidOperationException(error.ToString());
        return result;
    }

    private static (int[] Metadata, float[] Weights) PackTaps((int Start, float[] Weights)[] taps)
    {
        var metadata = new int[checked(taps.Length * 3)];
        var weights = new float[taps.Sum(t => t.Weights.Length)];
        int offset = 0;
        for (int i = 0; i < taps.Length; i++)
        {
            metadata[i * 3] = taps[i].Start;
            metadata[i * 3 + 1] = offset;
            metadata[i * 3 + 2] = taps[i].Weights.Length;
            taps[i].Weights.CopyTo(weights, offset);
            offset += taps[i].Weights.Length;
        }
        return (metadata, weights);
    }

    private static class Native
    {
        private const string Library = "OpenRevelare.Compute.Native";
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr or_gpu_create([Out] StringBuilder error, int size);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void or_gpu_destroy(IntPtr session);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int or_gpu_resize(GpuComputeSession session, float[] input, int sw, int sh,
            [Out] float[] output, int dw, int dh, int[] xm, float[] xw, int xcount,
            int[] ym, float[] yw, int ycount, [Out] StringBuilder error, int size);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int or_gpu_sharpen(GpuComputeSession session, float[] input, [Out] float[] output,
            int width, int height, float[] kernel, int count, float amount, float threshold, float limit,
            [Out] StringBuilder error, int size);
    }
}
