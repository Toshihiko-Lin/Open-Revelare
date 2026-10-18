using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OpenRevelare.Core;

public enum GpuAccelerationMode { Auto, Off, On }
public sealed record GpuWorkloadMeasurement(string Workload, double CpuMs, double GpuMs, bool Profitable)
{
    public double ColdCpuMs { get; init; }
    public double ColdGpuMs { get; init; }
    public double? LastGpuMs { get; init; }
    public int ConsecutiveSlowRuns { get; init; }

    internal GpuWorkloadMeasurement Observe(double elapsed)
    {
        int slow = elapsed >= CpuMs * 0.9 ? ConsecutiveSlowRuns + 1 : 0;
        return this with { LastGpuMs = elapsed, ConsecutiveSlowRuns = slow, Profitable = Profitable && slow < 2 };
    }
}

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
    private static long _resourceFallbacks;
    private static string? _lastResourceFallback;
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
        $"resourceFallbacks={Interlocked.Read(ref _resourceFallbacks)}; lastResourceFallback={_lastResourceFallback ?? "none"}; " +
        (_failure ?? (_session is null ? "not initialized" : "ready")) + $"; HEIC={HeicIO.EncoderDiagnostics}";
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

    internal static bool TryResizeSharpen(ImageBuffer source, int width, int height, float[] kernel, float amount,
        float threshold, float limit, Func<float[]> cpu, out float[] result)
    {
        result = null!;
        if (!SupportedDimensions(source) || width > 1_048_560 || height > 1_048_560
            || (long)width * height > 100_000_000) return false;
        return TryRun($"delivery:{Bucket(source.PixelCount)}:{Bucket((long)width*height)}:{kernel.Length}", source.PixelCount,
            () => _session!.ResizeSharpen(source, width, height, kernel, amount, threshold, limit), cpu, out result, minimumPixels: 1_000_000);
    }

    internal static bool TryCubeLut(float[] source, CubeLut lut, float[] parameters, Func<float[]> cpu, out float[] result)
    {
        result = null!;
        if (Mode == GpuAccelerationMode.Off || _unavailable
            || (Mode == GpuAccelerationMode.Auto && source.Length / 3 < 500_000)) return false;
        // CPU cube addressing rejects NaN coordinates. Keep that path's validation
        // rather than let an undefined float-to-int conversion address GPU memory.
        if (source.AsSpan().Contains(float.NaN)) return false;
        return TryRun($"cube-lut:{Bucket(source.Length / 3)}:{lut.ContentIdentity}", source.Length / 3,
            () => _session!.CubeLut(source, lut.Table, lut.Size, parameters), cpu, out result, minimumPixels: 500_000, exact: true);
    }

    private static int Bucket(long size) => System.Numerics.BitOperations.Log2((ulong)Math.Max(1, size));

    private static bool TryRun(string key, long pixels, Func<float[]> gpu, Func<float[]> cpu, out float[] result, long minimumPixels = 4_000_000, bool exact = false)
    {
        result = null!;
        var mode = Mode;
        if (mode == GpuAccelerationMode.Off || _unavailable
            || (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
            || (mode == GpuAccelerationMode.Auto && pixels < minimumPixels)) return false;
        // Bound peak device allocations; very large scans retain the existing CPU route.
        if (pixels > 100_000_000 || !Monitor.TryEnter(Gate)) return false;
        try
        {
            mode = Mode;
            if (_unavailable || mode == GpuAccelerationMode.Off) return false;
            bool calibrated = Decisions.TryGetValue(key, out var decision);
            if (mode == GpuAccelerationMode.Auto && calibrated && !decision!.Profitable) return false;
            _session ??= new GpuComputeSession();
            long start = Stopwatch.GetTimestamp();
            float[] accelerated = gpu();
            double gpuMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (!calibrated)
            {
                start = Stopwatch.GetTimestamp();
                float[] reference = cpu();
                double cpuMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if (exact ? !reference.AsSpan().SequenceEqual(accelerated) : !Equivalent(reference, accelerated))
                {
                    Disable("GPU output failed CPU parity check");
                    result = reference;
                    return true;
                }
                // First dispatch/JIT costs are not representative of later exports.
                // Warm both paths once, then time their complete steady-state work.
                double coldGpuMs = gpuMs, coldCpuMs = cpuMs;
                start = Stopwatch.GetTimestamp();
                reference = cpu();
                cpuMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                start = Stopwatch.GetTimestamp();
                accelerated = gpu();
                gpuMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if (exact ? !reference.AsSpan().SequenceEqual(accelerated) : !Equivalent(reference, accelerated))
                {
                    Disable("GPU output failed warmed CPU parity check");
                    result = reference;
                    return true;
                }
                bool profitable = gpuMs < cpuMs * 0.9;
                Decisions[key] = new(key, cpuMs, gpuMs, profitable) { ColdCpuMs = coldCpuMs, ColdGpuMs = coldGpuMs };
                if (mode == GpuAccelerationMode.Auto && !profitable)
                {
                    result = reference;
                    return true;
                }
            }
            else if (mode == GpuAccelerationMode.Auto)
            {
                // Allocation/driver pressure can change after calibration. Stop
                // using a route after two consecutive unprofitable invocations.
                Decisions[key] = decision!.Observe(gpuMs);
            }
            result = accelerated;
            Interlocked.Increment(ref _gpuRuns);
            return true;
        }
        catch (Exception ex) when (ex is GpuResourceUnavailableException or OutOfMemoryException)
        {
            // Oversized workloads and transient allocation pressure must not poison
            // future small operations or cache an unprofitable calibration decision.
            _lastResourceFallback = ex.Message;
            Interlocked.Increment(ref _resourceFallbacks);
            // Native resource rejection has already released its buffers; keep
            // compiled pipelines for later work. Managed allocation failure may
            // happen before entering native code, so release retained resources.
            if (ex is OutOfMemoryException)
            {
                _session?.Dispose();
                _session = null;
            }
            return false;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
            or InvalidOperationException)
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

internal sealed class GpuResourceUnavailableException(string message) : Exception(message);

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

    internal float[] CubeLut(float[] source, float[] lut, int edge, float[] parameters)
    {
        var result = new float[source.Length];
        var error = new StringBuilder(4096);
        int status = Native.or_gpu_cube_lut(this, source, result, source.Length / 3, lut, edge, parameters, error, error.Capacity);
        if (status != 2) { CheckStatus(status, error); return result; }
        // Pointwise operation: chunks need no overlap and preserve output ordering.
        int pixels = Math.Min(262144, source.Length / 3);
        for (int offset = 0; offset < source.Length;)
        {
            int count = Math.Min(pixels, (source.Length - offset) / 3);
            var input = new float[count * 3];
            var output = new float[input.Length];
            Array.Copy(source, offset, input, 0, input.Length);
            status = Native.or_gpu_cube_lut(this, input, output, count, lut, edge, parameters, error, error.Capacity);
            if (status == 2 && count > 1) { pixels = Math.Max(1, count / 2); continue; }
            CheckStatus(status, error);
            output.CopyTo(result, offset);
            offset += output.Length;
        }
        return result;
    }

    internal float[] Resize(ImageBuffer source, int width, int height)
    {
        var (xm, xw) = PackTaps(Resample.AxisTaps(source.Width, width));
        var (ym, yw) = PackTaps(Resample.AxisTaps(source.Height, height));
        var result = new float[checked(width * height * 3)];
        var error = new StringBuilder(4096);
        int status = Native.or_gpu_resize(this, source.Data, source.Width, source.Height, result, width, height,
            xm, xw, xw.Length, ym, yw, yw.Length, error, error.Capacity);
        CheckStatus(status, error);
        return result;
    }

    internal float[] ResizeSharpen(ImageBuffer source, int width, int height, float[] kernel, float amount, float threshold, float limit)
    {
        var (xm, xw) = PackTaps(Resample.AxisTaps(source.Width, width));
        var (ym, yw) = PackTaps(Resample.AxisTaps(source.Height, height));
        var result = new float[checked(width * height * 3)];
        var error = new StringBuilder(4096);
        CheckStatus(Native.or_gpu_resize_sharpen(this, source.Data, source.Width, source.Height, result, width, height,
            xm, xw, xw.Length, ym, yw, yw.Length, kernel, kernel.Length, amount, threshold, limit, error, error.Capacity), error);
        return result;
    }

    internal float[] Sharpen(ImageBuffer source, float[] kernel, float amount, float threshold, float limit)
    {
        var result = new float[source.Data.Length];
        var error = new StringBuilder(4096);
        int status = Native.or_gpu_sharpen(this, source.Data, result, source.Width, source.Height,
            kernel, kernel.Length, amount, threshold, limit, error, error.Capacity);
        if (status == 2 && source.Height > 1)
        {
            SharpenTiles(source, kernel, amount, threshold, limit, result, Math.Min(256, source.Height / 2));
            return result;
        }
        CheckStatus(status, error);
        return result;
    }

    // Each strip includes the vertical blur's halo. Only its interior is published,
    // so clamping happens at the image boundary, never at a strip seam.
    internal void SharpenTiles(ImageBuffer source, float[] kernel, float amount, float threshold, float limit,
        float[] result, int rows)
    {
        if (rows < 1) throw new ArgumentOutOfRangeException(nameof(rows));
        if (result.Length != source.Data.Length) throw new ArgumentException("Destination size mismatch", nameof(result));
        int radius = kernel.Length / 2;
        int stride = checked(source.Width * 3);
        var error = new StringBuilder(4096);
        for (int y = 0; y < source.Height;)
        {
            int count = Math.Min(rows, source.Height - y);
            int top = Math.Max(0, y - radius);
            int bottom = (int)Math.Min(source.Height, (long)y + count + radius);
            var input = new float[checked((bottom - top) * stride)];
            Array.Copy(source.Data, top * stride, input, 0, input.Length);
            var output = new float[input.Length];
            int status = Native.or_gpu_sharpen(this, input, output, source.Width, bottom - top,
                kernel, kernel.Length, amount, threshold, limit, error, error.Capacity);
            if (status == 2 && count > 1)
            {
                rows = Math.Max(1, count / 2);
                continue;
            }
            CheckStatus(status, error);
            Array.Copy(output, (y - top) * stride, result, y * stride, count * stride);
            y += count;
        }
    }

    private static void CheckStatus(int status, StringBuilder error)
    {
        if (status == 2) throw new GpuResourceUnavailableException(error.ToString());
        if (status != 0) throw new InvalidOperationException(error.ToString());
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
        internal static extern int or_gpu_resize_sharpen(GpuComputeSession session, float[] input, int sw, int sh,
            [Out] float[] output, int dw, int dh, int[] xm, float[] xw, int xcount, int[] ym, float[] yw, int ycount,
            float[] kernel, int count, float amount, float threshold, float limit, [Out] StringBuilder error, int size);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int or_gpu_cube_lut(GpuComputeSession session, float[] input, [Out] float[] output,
            int pixels, float[] lut, int edge, float[] parameters, [Out] StringBuilder error, int size);
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
