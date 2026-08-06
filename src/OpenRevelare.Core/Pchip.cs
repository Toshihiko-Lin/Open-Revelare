namespace OpenRevelare.Core;

/// <summary>
/// Monotone piecewise cubic Hermite interpolation — port of
/// scipy.interpolate.PchipInterpolator (Fritsch-Carlson derivatives). Used to
/// build tone-curve LUTs identically to the Python reference so curves match.
///
/// Assumes strictly increasing xs (the caller guarantees it). Evaluation clamps
/// to the data range; the LUT is only ever sampled on [0,1], which the anchored
/// control points always span.
/// </summary>
public sealed class Pchip
{
    private readonly double[] _x;
    private readonly double[] _y;
    private readonly double[] _d; // derivative at each knot

    public Pchip(double[] xs, double[] ys)
    {
        _x = xs;
        _y = ys;
        _d = FindDerivatives(xs, ys);
    }

    public double Eval(double x)
    {
        int n = _x.Length;
        if (x <= _x[0]) return EvalInterval(0, x);
        if (x >= _x[n - 1]) return EvalInterval(n - 2, x);
        // binary search for interval k: x[k] <= x < x[k+1]
        int lo = 0, hi = n - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (_x[mid] <= x) lo = mid; else hi = mid;
        }
        return EvalInterval(lo, x);
    }

    private double EvalInterval(int k, double x)
    {
        double h = _x[k + 1] - _x[k];
        double u = (x - _x[k]) / h;
        double u2 = u * u, u3 = u2 * u;
        double h00 = 2 * u3 - 3 * u2 + 1;
        double h10 = u3 - 2 * u2 + u;
        double h01 = -2 * u3 + 3 * u2;
        double h11 = u3 - u2;
        return h00 * _y[k] + h10 * h * _d[k] + h01 * _y[k + 1] + h11 * h * _d[k + 1];
    }

    private static int Sign(double v) => v > 0 ? 1 : (v < 0 ? -1 : 0);

    private static double[] FindDerivatives(double[] x, double[] y)
    {
        int n = x.Length;
        var hk = new double[n - 1];
        var mk = new double[n - 1]; // secant slopes (delta)
        for (int i = 0; i < n - 1; i++)
        {
            hk[i] = x[i + 1] - x[i];
            mk[i] = (y[i + 1] - y[i]) / hk[i];
        }

        var dk = new double[n];
        if (n == 2)
        {
            dk[0] = mk[0];
            dk[1] = mk[0];
            return dk;
        }

        // Interior knots: weighted harmonic mean, zeroed at sign changes/flats.
        for (int i = 1; i < n - 1; i++)
        {
            double m0 = mk[i - 1], m1 = mk[i];
            if (Sign(m0) != Sign(m1) || m0 == 0.0 || m1 == 0.0)
            {
                dk[i] = 0.0;
            }
            else
            {
                double w1 = 2 * hk[i] + hk[i - 1];
                double w2 = hk[i] + 2 * hk[i - 1];
                double whmean = (w1 / m0 + w2 / m1) / (w1 + w2);
                dk[i] = 1.0 / whmean;
            }
        }

        // Endpoints: scipy _edge_case (non-central three-point, shape-preserving).
        dk[0] = EdgeCase(hk[0], hk[1], mk[0], mk[1]);
        dk[n - 1] = EdgeCase(hk[n - 2], hk[n - 3], mk[n - 2], mk[n - 3]);
        return dk;
    }

    private static double EdgeCase(double h0, double h1, double m0, double m1)
    {
        double d = ((2 * h0 + h1) * m0 - h0 * m1) / (h0 + h1);
        if (Sign(d) != Sign(m0))
            return 0.0;
        if (Sign(m0) != Sign(m1) && Math.Abs(d) > 3.0 * Math.Abs(m0))
            return 3.0 * m0;
        return d;
    }
}
