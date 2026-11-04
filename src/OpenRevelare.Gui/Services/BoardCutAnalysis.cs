namespace OpenRevelare.Gui.Services;

/// <summary>Full-resolution measurements away from the UI thread, with bounded fan-out.
/// Decode callbacks retain ImageIo's byte-weighted memory gate. Null means no board.</summary>
internal static class BoardCutAnalysis
{
    // Full RGB, luma, connectivity labels and decoder buffers overlap during a measurement.
    // Only admit all three representative frames together when that working set fits.
    internal static int RecommendedWorkers(int decodeWorkers, int samples, long? availableBytes)
    {
        int memoryLimit = availableBytes is { } free
            ? (int)Math.Clamp((Math.Max(0, free) - (2L << 30)) / (4L << 30), 1, 3)
            : 2;
        return Math.Max(1, Math.Min(Math.Min(decodeWorkers, samples), memoryLimit));
    }

    internal static bool CanOverlapPreviews(int decodeWorkers, long? availableBytes)
        => decodeWorkers >= 6 && availableBytes is >= (18L << 30);

    internal static Task<double?> MeasureAsync(IReadOnlyList<Func<double?>> samples,
                                              CancellationToken cancellationToken = default,
                                              int maxWorkers = 1,
                                              Action<int, int>? progress = null)
        => Task.Run(() =>
        {
            double? best = null;
            int done = 0;
            progress?.Invoke(0, samples.Count);
            var gate = new object();
            Parallel.ForEach(samples, new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = maxWorkers,
            }, sample =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                double? cut = sample();
                cancellationToken.ThrowIfCancellationRequested();
                lock (gate)
                {
                    if (cut is { } value && (best is null || value > best)) best = value;
                    progress?.Invoke(++done, samples.Count);
                }
            });
            return best;
        }, cancellationToken);
}
