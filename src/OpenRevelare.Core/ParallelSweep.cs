using System.Collections.Concurrent;

namespace OpenRevelare.Core;

/// <summary>
/// Range-partitioned parallel sweeps over a flat buffer — the shape every whole-frame,
/// per-element operation in this pipeline wants.
///
/// WHAT THIS REPLACES, AND WHY IT IS NOT A STYLE PREFERENCE.
/// <c>Parallel.For(0, data.Length, i =&gt; ...)</c> reads as the obvious way to write a per-pixel
/// sweep, and it is the one thing you must not write. <c>Parallel.For</c>'s contract is that the
/// body runs once per index, so the delegate is INVOKED once per index — the loop it manages is
/// over work items, not over your array. On a 24 MP frame that is 72 million interface dispatches
/// plus the range bookkeeping around each one, none of which the JIT can hoist, inline into a
/// tight loop, or unroll.
///
/// Measured on this machine (8 cores, 24 MP RGB, float32, bit-identical output):
///
///   sRGB LUT sweep    per-element 220 ms → range-partitioned  98 ms   (2.3×)
///   powf sweep        per-element 507 ms → range-partitioned 327 ms   (1.5×)
///
/// The LUT case gains most because its body is a few ns of real work, so the dispatch WAS the
/// operation. The pow case gains least for the same reason inverted — a transcendental dominates
/// whatever wraps it. Both gains are free: the arithmetic is untouched, evaluated in the same
/// order per element, so results are identical bit for bit rather than merely close. That matters
/// in this codebase, whose tests assert exact parity against reference renders.
///
/// The fix is to hand the partitioner a RANGE per worker and let each worker run an ordinary
/// <c>for</c> loop over it. One delegate call per core instead of per element, and the inner loop
/// is a plain sequential walk the JIT optimises normally.
///
/// WHEN NOT TO USE THIS. Row-parallel code (<c>Parallel.For(0, height, y =&gt; ...)</c> with an
/// inner loop over the row) is ALREADY this pattern — the delegate is amortised over a whole row
/// — and <see cref="Inversion"/>, <see cref="LensCorrections"/> and <see cref="Geometry"/> are
/// written that way deliberately. Leave them alone. This helper is for the flat whole-buffer
/// sweeps that have no row structure to borrow.
/// </summary>
internal static class ParallelSweep
{
    /// <summary>
    /// Below this many elements the sweep runs on the calling thread.
    ///
    /// Spinning up workers costs tens of microseconds; a preview histogram or a thumbnail-sized
    /// buffer is finished in less than that, so the parallel version is a pessimisation. 64K
    /// floats is ~21K pixels — comfortably below any real frame and above every incidental
    /// buffer, so the frames that matter still parallelise and the small ones stop paying for
    /// the attempt.
    /// </summary>
    private const int SerialThreshold = 1 << 16;

    /// <summary>
    /// Runs <paramref name="body"/> over contiguous [from, to) sub-ranges covering
    /// [0, <paramref name="length"/>), in parallel across cores.
    ///
    /// The body must be independent per element — every caller here writes <c>data[i]</c> from
    /// <c>data[i]</c> alone, so the partitioning is unobservable.
    /// </summary>
    public static void Over(int length, Action<int, int> body)
    {
        if (length <= 0) return;

        if (length < SerialThreshold)
        {
            body(0, length);
            return;
        }

        Parallel.ForEach(Partitioner.Create(0, length),
                         range => body(range.Item1, range.Item2));
    }

    /// <summary>
    /// The pixel-indexed form: sweeps <paramref name="pixelCount"/> pixels and hands the body
    /// FLOAT offsets, already multiplied by three.
    ///
    /// Callers that walk interleaved RGB want <c>p * 3</c> as their base and would otherwise
    /// recompute it per pixel inside the range loop. Handing over the float range instead lets
    /// the body step <c>i += 3</c>, which is one add rather than a multiply per pixel and keeps
    /// the array bounds checks in a form the JIT can hoist.
    /// </summary>
    public static void OverPixels(int pixelCount, Action<int, int> body)
        => Over(pixelCount, (p0, p1) => body(p0 * 3, p1 * 3));
}
