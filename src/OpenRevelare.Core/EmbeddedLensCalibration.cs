using System.Buffers.Binary;

namespace OpenRevelare.Core;

/// <summary>Native normalized curves. Arrays are owned by this profile and never modified by rendering.</summary>
public sealed record EmbeddedLensCalibration(string Source, double[][]? Warp = null,
    double[]? DistortionKnots = null, double[]? VignetteKnots = null, double[]? VignettePolynomial = null,
    double CenterX = 0.5, double CenterY = 0.5, double VignetteCenterX = 0.5, double VignetteCenterY = 0.5)
{
    public bool HasDistortion => Warp is not null || DistortionKnots is not null;
    public bool HasVignette => VignetteKnots is not null || VignettePolynomial is not null;
    public EmbeddedLensCalibration Copy() => this with {
        Warp = Warp?.Select(x => (double[])x.Clone()).ToArray(),
        DistortionKnots = DistortionKnots is null ? null : (double[])DistortionKnots.Clone(),
        VignetteKnots = VignetteKnots is null ? null : (double[])VignetteKnots.Clone(),
        VignettePolynomial = VignettePolynomial is null ? null : (double[])VignettePolynomial.Clone() };

    public bool IsUsable
    {
        get
        {
            bool Finite(double[] a) => a.All(x => double.IsFinite(x) && Math.Abs(x) <= 10000);
            bool Center(double x) => double.IsFinite(x) && x >= 0 && x <= 1;
            if (!(HasDistortion || HasVignette) || !Center(CenterX) || !Center(CenterY)
                || !Center(VignetteCenterX) || !Center(VignetteCenterY)) return false;
            if (Warp is not null && (Warp.Length is not (1 or 3) || Warp.Any(x => x is null || x.Length != 6 || !Finite(x) || Math.Abs(x[4]) > 0.25 || Math.Abs(x[5]) > 0.25))) return false;
            if (DistortionKnots is not null && (DistortionKnots.Length is < 2 or > 256 || !Finite(DistortionKnots)
                || DistortionKnots.Any(x => x <= 0 || x > 4))) return false;
            if (VignetteKnots is not null && (VignetteKnots.Length is < 2 or > 256 || !Finite(VignetteKnots)
                || VignetteKnots.Any(x => x <= 0 || x > 64))) return false;
            if (VignettePolynomial is not null && (VignettePolynomial.Length != 5 || !Finite(VignettePolynomial))) return false;
            for (int i = 0; i <= 1024; i++)
            {
                double r = i / 1024.0, gain = Gain(r);
                if (!double.IsFinite(gain) || gain <= 0 || gain > 64) return false;
                if (Warp is not null)
                    foreach (var k in Warp)
                    {
                        double r2 = r * r;
                        double derivative = k[0] + r2 * (3*k[1] + r2 * (5*k[2] + r2*7*k[3]));
                        if (derivative <= 0 || derivative > 8) return false;
                    }
            }
            return true;
        }
    }

    public static double Interpolate(double[] knots, double radius)
    {
        double t = Math.Clamp(radius, 0, 1) * (knots.Length - 1);
        int i = Math.Min((int)t, knots.Length - 2);
        return knots[i] + (knots[i + 1] - knots[i]) * (t - i);
    }

    public double Gain(double r)
    {
        if (VignetteKnots is { } knots) return Interpolate(knots, r);
        if (VignettePolynomial is not { } k) return 1;
        double r2 = r*r;
        return 1 + r2 * (k[0] + r2 * (k[1] + r2 * (k[2] + r2 * (k[3] + r2*k[4]))));
    }

    public (double X, double Y) Map(double x, double y, int channel, double width, double height)
    {
        var (cx, cy, radius) = Coordinates(width, height, CenterX, CenterY);
        double dx = (x-cx)/radius, dy = (y-cy)/radius, r2 = dx*dx+dy*dy;
        if (DistortionKnots is { } knots)
        {
            double scale = Interpolate(knots, Math.Sqrt(r2));
            return (cx+(x-cx)*scale, cy+(y-cy)*scale);
        }
        if (Warp is not { } warp) return (x,y);
        var k = warp[warp.Length == 1 ? 0 : channel];
        double radial = k[0]+r2*(k[1]+r2*(k[2]+r2*k[3]));
        double tx = 2*k[4]*dx*dy + k[5]*(r2+2*dx*dx);
        double ty = 2*k[5]*dx*dy + k[4]*(r2+2*dy*dy);
        return (cx+radius*(dx*radial+tx), cy+radius*(dy*radial+ty));
    }

    internal static (double X, double Y, double Radius) Coordinates(double w, double h, double x, double y)
    {
        double cx = (w-1)*x, cy = (h-1)*y;
        return (cx, cy, Math.Max(1, Math.Sqrt(Math.Pow(Math.Max(cx,w-1-cx),2)+Math.Pow(Math.Max(cy,h-1-cy),2))));
    }

    public static bool TryParseDng(byte[] data, out EmbeddedLensCalibration? profile)
    {
        profile = null;
        try
        {
            var span = data.AsSpan();
            uint count = BinaryPrimitives.ReadUInt32BigEndian(span); int offset = 4;
            if (count > 1024) return false;
            double[][]? warp = null; double[]? vignette = null;
            double cx = .5, cy = .5, vx = .5, vy = .5;
            for (uint i = 0; i < count; i++)
            {
                if (data.Length-offset < 16) return false;
                uint id = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(offset));
                uint version = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(offset+4));
                uint flags = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(offset+8));
                uint size = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(offset+12)); offset += 16;
                if (size > data.Length-offset) return false;
                var payload = span.Slice(offset,(int)size); offset += (int)size;
                if (version > 0x01070100 || id is not (1 or 3)) { if ((flags&1)==0) return false; else continue; }
                double Read(int pos) => BinaryPrimitives.ReadDoubleBigEndian(data.AsSpan(offset-(int)size+pos,8));
                if (id == 1)
                {
                    if (warp is not null || size < 4) return false;
                    uint planes = BinaryPrimitives.ReadUInt32BigEndian(payload);
                    if (planes is not (1 or 3) || size != 4+48*planes+16) return false;
                    warp = new double[planes][];
                    for (int p = 0; p < planes; p++) warp[p] = Enumerable.Range(0,6).Select(j => Read(4+p*48+j*8)).ToArray();
                    cx = Read((int)size-16); cy = Read((int)size-8);
                }
                else
                {
                    if (vignette is not null || size != 56) return false;
                    vignette = Enumerable.Range(0,5).Select(j => Read(j*8)).ToArray();
                    vx = Read(40); vy = Read(48);
                }
            }
            if (offset != data.Length) return false;
            var result = new EmbeddedLensCalibration("DNG OpcodeList3", warp, VignettePolynomial:vignette,
                CenterX:cx, CenterY:cy, VignetteCenterX:vx, VignetteCenterY:vy);
            if (!result.IsUsable) return false;
            profile = result; return true;
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException) { return false; }
    }
}
