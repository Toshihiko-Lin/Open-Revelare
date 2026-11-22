namespace OpenRevelare.Core;

/// <summary>Scopes CPU and scratch-buffer limits to an export and its workers.</summary>
public static class ExportExecution
{
    private enum Mode { Default, Sequential, LowMemory }
    private static readonly AsyncLocal<Mode> Current = new();
    private static readonly ParallelOptions DefaultOptions = new();
    private static readonly ParallelOptions LowMemoryOptions = new()
    {
        MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
    };

    internal static bool IsSequential => Current.Value == Mode.Sequential;
    internal static bool UsesBoundedBuffers => Current.Value != Mode.Default;
    internal static ParallelOptions Options => Current.Value == Mode.LowMemory ? LowMemoryOptions : DefaultOptions;

    /// <summary>Run a synchronous export on one worker, without changing other jobs' settings.</summary>
    public static T RunSequential<T>(Func<T> export) => Run(Mode.Sequential, export);

    /// <summary>Allow bounded CPU parallelism while avoiding full-frame GPU scratch allocations.</summary>
    public static T RunLowMemory<T>(Func<T> export) => Run(Mode.LowMemory, export);

    private static T Run<T>(Mode mode, Func<T> export)
    {
        Mode previous = Current.Value;
        Current.Value = previous == Mode.Sequential ? previous : mode;
        try { return export(); }
        finally { Current.Value = previous; }
    }
}
