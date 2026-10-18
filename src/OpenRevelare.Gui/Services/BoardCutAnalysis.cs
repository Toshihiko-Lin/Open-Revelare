namespace OpenRevelare.Gui.Services;

/// <summary>Full-resolution measurements away from the UI thread, with bounded fan-out.
/// Decode callbacks retain ImageIo's byte-weighted memory gate. Null means no board.</summary>
internal static class BoardCutAnalysis
{
    internal static Task<double?> MeasureAsync(IReadOnlyList<Func<double?>> samples,
                                              CancellationToken cancellationToken = default,
                                              int maxWorkers = 1)
        => Task.Run(() =>
        {
            double? best = null;
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
                    if (cut is { } value && (best is null || value > best)) best = value;
            });
            return best;
        }, cancellationToken);
}
