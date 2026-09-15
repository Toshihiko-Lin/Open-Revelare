namespace OpenRevelare.Presentation;

/// <summary>
/// Row-parallel driver for the presentation assembly's pointwise per-pixel loops.
///
/// <para>
/// Small surfaces stay on the calling thread — below the threshold the thread-pool hand-off costs
/// more than the rows — and any exception a row throws surfaces as ITSELF, not wrapped in an
/// <see cref="AggregateException"/>: the builders' contracts name the exact exception type a bad
/// pixel produces, and the tests hold them to it. When several rows fail at once the first is
/// rethrown; they are all the same finding on different pixels.
/// </para>
/// </summary>
internal static class PresentationRows
{
    private const long ParallelPixelThreshold = 256L * 1024L;

    public static void Run(int rows, int rowWidth, Action<int> body)
    {
        long work = checked((long)rows * rowWidth);
        if (Environment.ProcessorCount > 1 && work >= ParallelPixelThreshold)
        {
            try
            {
                Parallel.For(0, rows, body);
            }
            catch (AggregateException exception)
            {
                Exception first = exception.Flatten().InnerExceptions[0];
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
            }
            return;
        }

        for (int y = 0; y < rows; y++)
            body(y);
    }
}
