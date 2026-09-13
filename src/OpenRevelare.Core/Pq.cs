namespace OpenRevelare.Core;

/// <summary>
/// SMPTE ST 2084, the perceptual quantizer — the transfer of HDR10 and of Resolve's
/// <c>Rec.2100 ST2084</c> output. ABSOLUTE: a code value names a luminance in cd/m², up to
/// 10 000, which is why it is not a <see cref="TransferFunction"/> member. Turning it into the
/// pipeline's carrier needs a reference white, and that white is
/// <see cref="OutputTarget.ReferenceWhiteNits"/> (203, BT.2408) — the same anchor every other
/// HDR path in this codebase uses, so a LUT that puts diffuse white at 203 nits lands exactly
/// on the carrier's 1.0.
/// </summary>
public static class Pq
{
    private const float M1 = 2610.0f / 16384.0f;
    private const float M2 = 2523.0f / 4096.0f * 128.0f;
    private const float C1 = 3424.0f / 4096.0f;
    private const float C2 = 2413.0f / 4096.0f * 32.0f;
    private const float C3 = 2392.0f / 4096.0f * 32.0f;

    /// <summary>The luminance PQ code 1.0 stands for.</summary>
    public const float PeakNits = 10000.0f;

    /// <summary>PQ code value → cd/m². Codes below 0 read as 0.</summary>
    public static float ToNits(float encoded)
    {
        float e = MathF.Pow(MathF.Max(encoded, 0.0f), 1.0f / M2);
        float num = MathF.Max(e - C1, 0.0f);
        float den = C2 - C3 * e;
        return PeakNits * MathF.Pow(num / den, 1.0f / M1);
    }

    /// <summary>cd/m² → PQ code value, the inverse of <see cref="ToNits"/>. Clamped to the 0…10 000 nit domain.</summary>
    public static float FromNits(float nits)
    {
        float y = MathF.Pow(Math.Clamp(nits, 0.0f, PeakNits) / PeakNits, M1);
        return MathF.Pow((C1 + C2 * y) / (1.0f + C3 * y), M2);
    }

    /// <summary>
    /// Decodes PQ code values in place into linear light where <c>1.0 = referenceWhiteNits</c>.
    /// </summary>
    public static void DecodeToCarrier(float[] data, float referenceWhiteNits)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!(referenceWhiteNits > 0f))
            throw new ArgumentOutOfRangeException(nameof(referenceWhiteNits));
        float inv = 1.0f / referenceWhiteNits;
        ParallelSweep.Over(data.Length, (from, to) =>
        {
            for (int i = from; i < to; i++) data[i] = ToNits(data[i]) * inv;
        });
    }
}
