using System.Diagnostics;
using System.Text.Json;
using OpenRevelare.Core;

namespace OpenRevelare.Gui.Services;

/// <summary>
/// Opt-in timing trace for the interactive preview path. Off unless the process starts with
/// <c>OPENREVELARE_RENDER_TRACE=1</c>; then each stage appends one line to
/// <c>%TEMP%\openrevelare-render-trace.log</c>.
///
/// It exists because "the slider does not follow the pointer" cannot be diagnosed from a
/// benchmark: the cost that matters is the one paid on THIS roll, at THIS viewport, on THIS
/// display contract, and it is split between the UI thread (the drag render) and the
/// presentation worker (compose + pack + present). Reading both from one log is what tells which
/// half to work on.
/// </summary>
internal static class RenderTrace
{
    public static readonly bool Enabled =
        Environment.GetEnvironmentVariable("OPENREVELARE_RENDER_TRACE") == "1";

    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "openrevelare-render-trace.log");

    private static readonly object Gate = new();
    private static readonly PerformanceStatistics Statistics = new();

    public static T Measure<T>(string phase, Func<T> work)
    {
        if (!Enabled) return work();
        long start = Stopwatch.GetTimestamp();
        try { return work(); }
        finally { Statistics.Observe(phase, Stopwatch.GetElapsedTime(start).TotalMilliseconds); }
    }

    public static void Count(string counter)
    {
        if (Enabled) Statistics.Increment(counter);
    }

    public static void Observe(string phase, double milliseconds)
    {
        if (Enabled) Statistics.Observe(phase, milliseconds);
    }

    public static IDisposable? Scope(string phase) => Enabled ? new Span(phase) : null;

    private sealed class Span(string phase) : IDisposable
    {
        private readonly long _start = Stopwatch.GetTimestamp();
        public void Dispose() => Statistics.Observe(phase, Stopwatch.GetElapsedTime(_start).TotalMilliseconds);
    }

    public static void SaveSummary()
    {
        if (!Enabled) return;
        try
        {
            using var process = Process.GetCurrentProcess();
            string path = Path.Combine(Path.GetTempPath(), $"openrevelare-performance-{Environment.ProcessId}.json");
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(new
            {
                Schema = "openrevelare-performance-v1", ProcessId = Environment.ProcessId, AppVersion = AppInfo.Version,
                Platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                Runtime = Environment.Version.ToString(), ProcessorCount = Environment.ProcessorCount,
                PeakWorkingSetBytes = process.PeakWorkingSet64,
                Gpu = GpuAcceleration.Diagnostics,
                GpuMeasurements = GpuAcceleration.Measurements(),
                PercentileMethod = "nearest-rank, last 2048 samples per phase; durations include failed attempts",
                Phases = Statistics.Snapshot(), Counters = Statistics.Counters(),
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static Stopwatch? Start() => Enabled ? Stopwatch.StartNew() : null;

    public static void Write(string line)
    {
        if (!Enabled) return;
        lock (Gate)
        {
            try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}"); }
            catch (IOException) { }
        }
    }
}
