namespace OpenRevelare.Core;

/// <summary>
/// The order statistics the port needs, defined ONCE, matching numpy exactly.
///
/// These used to exist as four separate private copies (FilmBase, DecoupleCalibration, Sprocket,
/// Decouple) plus two copies of the median. They are the kind of thing that must not drift:
/// HANDOFF §6 records that getting numpy's dtype propagation right is precisely what took the
/// film_base / sprocket parity from "roughly agrees" to 14 of 15 keys bit-identical, and a rule
/// spread across four files is a rule that gets half-updated.
///
/// ⚠ THE float32 / float64 SPLIT IS DELIBERATE — DO NOT "UNIFY" IT.
/// numpy's <c>percentile</c> and <c>median</c> return float32 for a float32 input, and several
/// callers depend on that narrowing (Sprocket quantises the result back through float on purpose).
/// The <c>float[]</c> overloads therefore sort in single precision and interpolate in double,
/// which is what the reference does; the <c>double[]</c> overloads are for genuinely float64
/// inputs. Picking the wrong one shifts results in the last few digits — enough to matter after a
/// matrix inversion (§6: a 1e-6 drift became 1e-5 in chroma_amp).
///
/// All of these SORT A COPY: numpy leaves the caller's array alone and so do we.
/// </summary>
internal static class NumpyStats
{
    /// <summary>numpy.percentile(vals, q, method='linear') on a float64 array.</summary>
    internal static double Percentile(double[] vals, double q)
    {
        var s = (double[])vals.Clone();
        Array.Sort(s);
        return Interpolate(s, q);
    }

    /// <summary>numpy.percentile(vals, q, method='linear') on a float32 array — sorted in
    /// single precision, interpolated in double, exactly as numpy does it.</summary>
    internal static double Percentile(float[] vals, double q)
    {
        var s = (float[])vals.Clone();
        Array.Sort(s);
        int n = s.Length;
        double rank = (n - 1) * (q / 100.0);
        int lo = (int)Math.Floor(rank);
        if (lo >= n - 1) return s[n - 1];
        return s[lo] + (rank - lo) * ((double)s[lo + 1] - s[lo]);
    }

    /// <summary>
    /// numpy.percentile over ONE channel of an interleaved buffer, without materialising the
    /// channel first as a separate float array. The samples are widened to double before the
    /// sort — this is the float64 path applied to float32 data, which is what
    /// <c>np.percentile(img[..., c], q)</c> does when the caller has already promoted.
    /// </summary>
    internal static double PercentileChannel(float[] interleaved, int channel, int pixelCount,
                                             double q, int stride = 3)
    {
        var vals = new double[pixelCount];
        for (int p = 0; p < pixelCount; p++) vals[p] = interleaved[p * stride + channel];
        Array.Sort(vals);
        return Interpolate(vals, q);
    }

    /// <summary>numpy.median on a float64 array.</summary>
    internal static double Median(double[] vals)
    {
        var s = (double[])vals.Clone();
        Array.Sort(s);
        int n = s.Length;
        return (n % 2 == 1) ? s[n / 2] : 0.5 * (s[n / 2 - 1] + s[n / 2]);
    }

    /// <summary>numpy.median on a float32 array (sorted in single precision).</summary>
    internal static double Median(float[] vals)
    {
        var s = (float[])vals.Clone();
        Array.Sort(s);
        int n = s.Length;
        return (n % 2 == 1) ? s[n / 2] : 0.5 * ((double)s[n / 2 - 1] + s[n / 2]);
    }

    /// <summary>Linear interpolation between the two bracketing order statistics of an
    /// ALREADY-SORTED float64 array — the shared tail of the percentile overloads.</summary>
    private static double Interpolate(double[] sorted, double q)
    {
        int n = sorted.Length;
        double rank = (n - 1) * (q / 100.0);
        int lo = (int)Math.Floor(rank);
        if (lo >= n - 1) return sorted[n - 1];
        return sorted[lo] + (rank - lo) * (sorted[lo + 1] - sorted[lo]);
    }
}
