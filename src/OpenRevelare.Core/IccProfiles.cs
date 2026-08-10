using System.Text;

namespace OpenRevelare.Core;

/// <summary>The RGB working space an export is encoded into.</summary>
public enum ColorSpace
{
    /// <summary>IEC 61966-2-1 sRGB — linear toe + 2.4 power segment.</summary>
    Srgb,

    /// <summary>Adobe RGB (1998) — pure power curve, gamma 563/256.</summary>
    AdobeRgb,
}

/// <summary>
/// Minimal ICC v2.1 matrix/TRC profile builder for the two working spaces we export.
///
/// DELIBERATE DEVIATION from export.py, which gets sRGB bytes out of littleCMS
/// (PIL's ImageCms) and AdobeRGB by hunting for a system-installed
/// AdobeRGB1998.icc — returning None, i.e. embedding NOTHING, when the file is
/// absent. .NET has no littleCMS equivalent, and depending on a system profile
/// would make the AdobeRGB export silently unprofiled on most machines and break
/// the "three platforms" goal. Generating both profiles instead is self-contained
/// and always available. The bytes therefore do NOT match Python's and never
/// could — what must match is the TRC math and the colorimetry, which is what
/// tools/parity/ref_export.py checks.
///
/// Profiles are matrix/TRC display profiles with D50 PCS and Bradford-adapted
/// primaries — the same construction every real sRGB/AdobeRGB profile uses.
/// </summary>
public static class IccProfiles
{
    // Bradford-adapted-to-D50 primaries, the standard values carried by the
    // reference profiles for each space.
    private static readonly double[,] SrgbPrimaries =
    {
        { 0.4360, 0.2225, 0.0139 },   // rXYZ
        { 0.3851, 0.7169, 0.0971 },   // gXYZ
        { 0.1431, 0.0606, 0.7141 },   // bXYZ
    };

    private static readonly double[,] AdobePrimaries =
    {
        { 0.6097, 0.3111, 0.0195 },
        { 0.2053, 0.6257, 0.0609 },
        { 0.1492, 0.0632, 0.7448 },
    };

    // ICC PCS illuminant, fixed at D50 by the spec.
    private static readonly double[] D50 = { 0.9642, 1.0, 0.8249 };

    public static byte[] Build(ColorSpace space) => space == ColorSpace.AdobeRgb
        ? Build("Adobe RGB (1998) compatible — OpenRevelare", AdobePrimaries, AdobeTrcTag())
        : Build("sRGB IEC61966-2.1 compatible — OpenRevelare", SrgbPrimaries, SrgbTrcTag());

    /// <summary>
    /// Builds a matrix/TRC profile for any registered space, deriving the D50-adapted primaries
    /// from its chromaticities rather than carrying a hard-coded table per space.
    ///
    /// sRGB and AdobeRGB keep going through <see cref="Build(ColorSpace)"/> above: their
    /// hard-coded primaries are the exact values the reference profiles carry, and a byte-for-byte
    /// match with what shipped before matters more for them than uniformity of construction.
    /// Everything else — Display P3, ACEScg, the paper spaces — is generated here.
    /// </summary>
    public static byte[] Build(ColorSpaceDef space)
    {
        if (space.Name.Equals("sRGB", StringComparison.OrdinalIgnoreCase))
            return Build(ColorSpace.Srgb);
        if (space.Name.Equals("AdobeRGB", StringComparison.OrdinalIgnoreCase))
            return Build(ColorSpace.AdobeRgb);

        // The profile's rXYZ/gXYZ/bXYZ are the RGB→XYZ columns adapted to the D50 PCS.
        double[,] toD50 = ColorSpaces.Mul(
            ColorSpaces.Adaptation(space.White, D50White), space.ToXyz());

        // Build() indexes [primary, component]; ToXyz() is [component, primary].
        double[,] rows =
        {
            { toD50[0, 0], toD50[1, 0], toD50[2, 0] },
            { toD50[0, 1], toD50[1, 1], toD50[2, 1] },
            { toD50[0, 2], toD50[1, 2], toD50[2, 2] },
        };

        byte[] trc = space.Name.Equals("DisplayP3", StringComparison.OrdinalIgnoreCase)
            ? SrgbTrcTag()                                   // P3 shares sRGB's piecewise curve
            : GammaTrcTag(OutputRender.EncodingGamma(space));

        return Build($"{space.Name} — OpenRevelare", rows, trc);
    }

    /// <summary>D50 as chromaticity, for adapting a space's primaries into the ICC PCS.</summary>
    private static readonly (double X, double Y) D50White = (0.34567, 0.35850);

    /// <summary>
    /// 'curv' holding a single u8Fixed8 gamma. Gamma 1.0 still goes through here rather than a
    /// count-0 (identity) curve: an explicit 1.0 says "linear" to every CMM, whereas count-0 is
    /// less widely handled.
    /// </summary>
    private static byte[] GammaTrcTag(double gamma)
    {
        var t = new List<byte>();
        t.AddRange(Encoding.ASCII.GetBytes("curv"));
        t.AddRange(UInt32Be(0));
        t.AddRange(UInt32Be(1));
        t.AddRange(UInt16Be((ushort)Math.Round(gamma * 256.0)));
        return t.ToArray();
    }

    /// <summary>
    /// 'curv' with a single u8Fixed8 gamma. AdobeRGB's 563/256 is exactly representable
    /// there (563/256 × 256 = 563) — which is precisely why the spec picked that odd
    /// number rather than a round 2.2.
    ///
    /// The exponent is stored as-is, NOT reciprocated: a count-1 'curv' means
    /// linear = encoded^g, the encoded→linear direction (see <see cref="SrgbTrcTag"/>).
    /// The reference AdobeRGB1998.icc carries the same 2.199 in the equivalent 'para'
    /// type-0 form, so this matches it.
    /// </summary>
    private static byte[] AdobeTrcTag()
    {
        var t = new List<byte>();
        t.AddRange(Encoding.ASCII.GetBytes("curv"));
        t.AddRange(UInt32Be(0));
        t.AddRange(UInt32Be(1));          // count 1 → gamma-only
        t.AddRange(UInt16Be(563));        // u8Fixed8: 563/256 = 2.19921875
        return t.ToArray();
    }

    /// <summary>
    /// 'curv' as a 1024-entry table. sRGB's TRC is a linear toe spliced to a power
    /// segment, which a single gamma cannot express — the toe is the whole point.
    ///
    /// DIRECTION: a 'curv' table is sampled uniformly over the DEVICE value and stores
    /// the PCS (linear) value — i.e. encoded → linear, the same direction the matrix
    /// that follows it expects. Writing the forward (linear → encoded) curve here
    /// inverts the profile: a reader linearising with it lands on x^(1/2.2)-ish instead
    /// of x^2.2, roughly 0.73 where 0.21 was meant, and every colour-managed consumer of
    /// the export sees a badly lifted image. Cross-checked against Rec709.icc, whose
    /// sampled curv maps encoded 0.5 to 0.260 — the encoded→linear direction.
    /// </summary>
    private static byte[] SrgbTrcTag()
    {
        const int N = 1024;
        var t = new List<byte>();
        t.AddRange(Encoding.ASCII.GetBytes("curv"));
        t.AddRange(UInt32Be(0));
        t.AddRange(UInt32Be(N));
        for (int i = 0; i < N; i++)
        {
            double x = i / (N - 1.0);   // device (sRGB-encoded)
            double y = Srgb.SrgbToLinear((float)x);
            t.AddRange(UInt16Be((ushort)Math.Round(Math.Clamp(y, 0.0, 1.0) * 65535.0)));
        }
        return t.ToArray();
    }

    private static byte[] Build(string description, double[,] primaries, byte[] trc)
    {
        var tags = new List<(string Sig, byte[] Data)>
        {
            ("desc", DescTag(description)),
            ("wtpt", XyzTag(D50[0], D50[1], D50[2])),
            ("rXYZ", XyzTag(primaries[0, 0], primaries[0, 1], primaries[0, 2])),
            ("gXYZ", XyzTag(primaries[1, 0], primaries[1, 1], primaries[1, 2])),
            ("bXYZ", XyzTag(primaries[2, 0], primaries[2, 1], primaries[2, 2])),
            ("rTRC", trc),
            ("gTRC", trc),
            ("bTRC", trc),
            ("cprt", TextTag("Generated by OpenRevelare. No rights reserved.")),
        };

        // Layout: 128-byte header, tag table, then tag data 4-byte aligned. The three
        // TRC tags share one data block — standard practice and what makes the grey
        // axis exactly neutral.
        int tableSize = 4 + tags.Count * 12;
        int offset = Align4(128 + tableSize);
        var placed = new List<(string Sig, int Offset, int Size)>();
        var blocks = new List<(int Offset, byte[] Data)>();
        var seen = new Dictionary<byte[], int>(ReferenceEqualityComparer.Instance);

        foreach (var (sig, data) in tags)
        {
            if (seen.TryGetValue(data, out int existing))
            {
                placed.Add((sig, existing, data.Length));
                continue;
            }
            seen[data] = offset;
            placed.Add((sig, offset, data.Length));
            blocks.Add((offset, data));
            offset = Align4(offset + data.Length);
        }
        int totalSize = offset;

        var buf = new byte[totalSize];
        WriteHeader(buf, totalSize);

        int p = 128;
        WriteUInt32Be(buf, p, (uint)tags.Count); p += 4;
        foreach (var (sig, off, size) in placed)
        {
            Encoding.ASCII.GetBytes(sig).CopyTo(buf, p); p += 4;
            WriteUInt32Be(buf, p, (uint)off); p += 4;
            WriteUInt32Be(buf, p, (uint)size); p += 4;
        }
        foreach (var (off, data) in blocks) data.CopyTo(buf, off);
        return buf;
    }

    private static void WriteHeader(byte[] buf, int size)
    {
        WriteUInt32Be(buf, 0, (uint)size);
        Encoding.ASCII.GetBytes("ADBE").CopyTo(buf, 4);        // preferred CMM
        WriteUInt32Be(buf, 8, 0x02100000);                     // ICC v2.1
        Encoding.ASCII.GetBytes("mntr").CopyTo(buf, 12);       // display device class
        Encoding.ASCII.GetBytes("RGB ").CopyTo(buf, 16);
        Encoding.ASCII.GetBytes("XYZ ").CopyTo(buf, 20);
        // Creation date/time: fixed, so a given profile is byte-stable across runs
        // (an embedded timestamp would make otherwise-identical exports differ).
        WriteUInt16Be(buf, 24, 2026); WriteUInt16Be(buf, 26, 1); WriteUInt16Be(buf, 28, 1);
        Encoding.ASCII.GetBytes("acsp").CopyTo(buf, 36);
        Encoding.ASCII.GetBytes("APPL").CopyTo(buf, 40);       // primary platform
        WriteUInt32Be(buf, 64, 0);                             // perceptual intent
        WriteInt32Be(buf, 68, S15Fixed16(D50[0]));
        WriteInt32Be(buf, 72, S15Fixed16(D50[1]));
        WriteInt32Be(buf, 76, S15Fixed16(D50[2]));
    }

    // v2 'desc' (textDescription): ASCII part + empty Unicode + empty ScriptCode.
    private static byte[] DescTag(string text)
    {
        byte[] ascii = Encoding.ASCII.GetBytes(text.Replace('—', '-'));
        var t = new List<byte>();
        t.AddRange(Encoding.ASCII.GetBytes("desc"));
        t.AddRange(UInt32Be(0));
        t.AddRange(UInt32Be((uint)ascii.Length + 1));
        t.AddRange(ascii);
        t.Add(0);
        t.AddRange(UInt32Be(0));            // Unicode language code
        t.AddRange(UInt32Be(0));            // Unicode count
        t.AddRange(UInt16Be(0));            // ScriptCode code
        t.Add(0);                           // ScriptCode count
        t.AddRange(new byte[67]);           // ScriptCode body (fixed 67 bytes)
        return t.ToArray();
    }

    private static byte[] TextTag(string text)
    {
        var t = new List<byte>();
        t.AddRange(Encoding.ASCII.GetBytes("text"));
        t.AddRange(UInt32Be(0));
        t.AddRange(Encoding.ASCII.GetBytes(text));
        t.Add(0);
        return t.ToArray();
    }

    private static byte[] XyzTag(double x, double y, double z)
    {
        var t = new List<byte>();
        t.AddRange(Encoding.ASCII.GetBytes("XYZ "));
        t.AddRange(UInt32Be(0));
        t.AddRange(Int32Be(S15Fixed16(x)));
        t.AddRange(Int32Be(S15Fixed16(y)));
        t.AddRange(Int32Be(S15Fixed16(z)));
        return t.ToArray();
    }

    private static int S15Fixed16(double v) => (int)Math.Round(v * 65536.0);
    private static int Align4(int v) => (v + 3) & ~3;

    private static byte[] UInt32Be(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
    private static byte[] Int32Be(int v) => UInt32Be(unchecked((uint)v));
    private static byte[] UInt16Be(ushort v) => new[] { (byte)(v >> 8), (byte)v };

    private static void WriteUInt32Be(byte[] b, int o, uint v) => UInt32Be(v).CopyTo(b, o);
    private static void WriteInt32Be(byte[] b, int o, int v) => Int32Be(v).CopyTo(b, o);
    private static void WriteUInt16Be(byte[] b, int o, ushort v) => UInt16Be(v).CopyTo(b, o);
}
