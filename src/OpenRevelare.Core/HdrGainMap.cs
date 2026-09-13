using OpenRevelare.ColorManagement;

namespace OpenRevelare.Core;

/// <summary>
/// The numbers a gain-map reader needs to turn the SDR base back into the HDR rendition, in the
/// terms ISO 21496-1 and Adobe's <c>hdrgm</c> XMP namespace share. Every gain is a base-2
/// logarithm; the offsets are linear.
///
/// <para>
/// Carried per channel so the record can describe either kind of map (<see cref="GainMapChannels"/>);
/// a luminance map has the three ranges equal and <see cref="IsUniform"/> true, and is written in
/// the scalar single-channel form.
/// </para>
/// </summary>
public sealed record GainMapMetadata(
    float MinR, float MinG, float MinB,
    float MaxR, float MaxG, float MaxB,
    float Gamma,
    float OffsetSdr,
    float OffsetHdr,
    float HdrCapacityMin,
    float HdrCapacityMax)
{
    public float Min(int channel) => channel switch { 0 => MinR, 1 => MinG, _ => MinB };
    public float Max(int channel) => channel switch { 0 => MaxR, 1 => MaxG, _ => MaxB };

    /// <summary>Whether the three channels carry one and the same range, in which case the
    /// scalar metadata form describes the map exactly.</summary>
    public bool IsUniform =>
        MinR == MinG && MinG == MinB && MaxR == MaxG && MaxG == MaxB;
}

/// <summary>How many gains a pixel carries.</summary>
public enum GainMapChannels
{
    /// <summary>
    /// One gain per pixel, taken from luminance and applied to all three channels — a grayscale
    /// map. This is what Lightroom and every phone camera write, and therefore the form every
    /// reader has been exercised on. The HDR rendition it reconstructs has the base's hue at each
    /// pixel and the HDR rendition's luminance; where the per-channel shoulder would have rolled
    /// the three components differently, that difference is given up. The default.
    /// </summary>
    Luminance,

    /// <summary>
    /// One gain per channel — the HDR rendition reconstructed exactly, including the hue of a
    /// highlight whose components were rolled off separately. Rarer in the wild and rarer in
    /// readers; kept for callers that want the exact reconstruction and control the reader.
    /// </summary>
    PerChannel,
}

/// <summary>
/// The gain map between an SDR base rendition and its HDR sibling (ISO 21496-1 / Adobe gain map),
/// as a picture plus the metadata that gives its codes meaning.
///
/// <para>
/// WHY A GAIN MAP AND NOT A PQ FILE FIRST. It is the one HDR photo container every viewer already
/// opens: a reader that knows nothing about it shows the base JPEG, a reader that does recovers
/// the highlights on an HDR display and, on a display with LESS headroom than the master, scales
/// the gain down to fit — the same automatic soft proof the preview does (D-028), performed by the
/// consumer. It needs no encoder the application does not already have.
/// </para>
///
/// <para>
/// WHAT IS RELATED TO WHAT. The map is defined in the LINEAR form of the base's colour space: the
/// SDR base is decoded through its own TRC, the HDR carrier (linear extended sRGB, D-024) is
/// rotated into the base's primaries, and the gain is <c>log2((hdr + offset) / (sdr + offset))</c>
/// per channel. A reader undoes exactly that, so what it reconstructs is the HDR rendition —
/// which is also why the base is not free to be any SDR picture: it is the asymptote-1 member of
/// the same shoulder family, bit-identical to the HDR rendition below the knee (D-021), so the map
/// is zero over the shadows and mid-tones and only the highlights carry gain.
/// </para>
///
/// <para>
/// WHAT A GAIN-MAP JPEG CANNOT HOLD. The base's primaries bound both renditions: a colour outside
/// them is a negative component in the base's linear space, has no representable SDR value and no
/// finite logarithm. Such a pixel is pulled toward its own luminance-matched grey until every
/// component is non-negative — the same hue-and-luminance-preserving move the SDR path makes
/// (<see cref="GamutMapping.Desaturate"/>), minus the ceiling, which an HDR rendition does not
/// have. Clipping the negative component to zero instead would leave a fully saturated colour the
/// SDR base never shows and a gain of several stops below zero at that pixel. The carrier's own
/// gamut survives unchanged in the float32 TIFF master; the JPEG is the deliverable.
/// </para>
/// </summary>
public sealed class HdrGainMap
{
    /// <summary>
    /// The linear offset both renditions receive before the ratio is taken. It keeps a black base
    /// pixel from dividing by zero and bounds the gain at black to
    /// <c>log2(headroom / offset)</c>; 1/64 is what every published encoder uses.
    /// </summary>
    public const float DefaultOffset = 1f / 64f;

    /// <summary>
    /// The map's codes are stored linearly in <c>[0,1]</c> over <c>[min, max]</c>. A gamma below
    /// one would spend more codes on small gains; here the map is zero almost everywhere and the
    /// interesting part is the top, so linear is the honest choice.
    /// </summary>
    public const float DefaultGamma = 1f;

    /// <summary>
    /// The narrowest range the map is ever declared over. A picture whose two renditions agree
    /// everywhere has a flat map, and a flat map with <c>min == max</c> is undefined; declaring
    /// this span instead keeps every reader's arithmetic finite while the reconstruction is still
    /// exact, because every code is then the minimum.
    /// </summary>
    private const float MinimumSpan = 1f / 1024f;

    /// <summary>
    /// The map, codes in <c>[0,1]</c>, same dimensions as the renditions. Always stored as three
    /// channels; a luminance map has the three equal and is encoded as one.
    /// </summary>
    public ImageBuffer Map { get; }

    public GainMapChannels Channels { get; }

    public GainMapMetadata Metadata { get; }

    /// <summary>The space the map is defined in — the base's.</summary>
    public ColorSpaceDef BaseSpace { get; }

    private HdrGainMap(ImageBuffer map, GainMapMetadata metadata, ColorSpaceDef baseSpace, GainMapChannels channels)
    {
        Map = map;
        Metadata = metadata;
        BaseSpace = baseSpace;
        Channels = channels;
    }

    /// <summary>
    /// Computes the map between <paramref name="sdrBase"/> and <paramref name="hdr"/>.
    ///
    /// <para>
    /// The two frames must be typed as what this arithmetic assumes: the base display-referred and
    /// profile-encoded in <paramref name="baseSpace"/> — checked against the exact profile bytes,
    /// not the name, in the spirit of D-003 — and the HDR rendition linear in the canonical
    /// extended carrier. <paramref name="target"/> is the master's own headroom, which becomes
    /// the map's declared HDR capacity: the content may reach it and never exceeds it
    /// (<see cref="HighlightRolloff.BoundAbove"/>).
    /// </para>
    /// </summary>
    public static HdrGainMap Compute(
        RenderedFrame sdrBase,
        RenderedFrame hdr,
        ColorSpaceDef baseSpace,
        OutputTarget target,
        GainMapChannels channels = GainMapChannels.Luminance)
    {
        ArgumentNullException.ThrowIfNull(sdrBase);
        ArgumentNullException.ThrowIfNull(hdr);
        ArgumentNullException.ThrowIfNull(target);
        RequireBase(sdrBase, baseSpace);
        RequireHdr(hdr);
        if (!target.IsExtended)
        {
            throw new ArgumentException(
                "A gain map relates an SDR base to an EXTENDED rendition; an SDR target has no gain to map.",
                nameof(target));
        }
        if (sdrBase.Pixels.Width != hdr.Pixels.Width || sdrBase.Pixels.Height != hdr.Pixels.Height)
        {
            throw new ArgumentException(
                $"The two renditions must share one geometry; base is {sdrBase.Pixels.Width}×{sdrBase.Pixels.Height}, " +
                $"HDR is {hdr.Pixels.Width}×{hdr.Pixels.Height}.");
        }

        int w = sdrBase.Pixels.Width, h = sdrBase.Pixels.Height;

        // Both into the base's linear space: the base undoes its own TRC, the HDR rendition is
        // rotated into the base's primaries and fitted into its gamut.
        float[] s = (float[])sdrBase.Pixels.Data.Clone();
        OutputRender.Decode(s, baseSpace);
        float[] d = HdrInBaseGamut(hdr, baseSpace);

        const float offset = DefaultOffset;
        float[] gain = new float[d.Length];
        if (channels == GainMapChannels.PerChannel)
        {
            ParallelSweep.Over(gain.Length, (from, to) =>
            {
                for (int i = from; i < to; i++)
                {
                    float hv = d[i], sv = s[i];
                    if (!(sv > 0f)) sv = 0f;      // NaN lands on black; negatives cannot occur
                    gain[i] = MathF.Log2((hv + offset) / (sv + offset));
                }
            });
        }
        else
        {
            // One gain from luminance, written into all three so the encoding below is shared.
            (float ly, float lg, float lb) = LuminanceWeights(baseSpace);
            ParallelSweep.OverPixels(w * h, (from, to) =>
            {
                for (int p = from; p < to; p += 3)
                {
                    float yh = ly * d[p] + lg * d[p + 1] + lb * d[p + 2];
                    float ys = ly * s[p] + lg * s[p + 1] + lb * s[p + 2];
                    if (!(ys > 0f)) ys = 0f;
                    // Never below zero: the HDR rendition is at least as luminous as its SDR
                    // sibling everywhere by construction, so a negative here is float noise or
                    // the gamut fit's rounding — and readers treat a negative range as a reason
                    // to disable the map rather than as a small darkening.
                    float g = Math.Max(MathF.Log2((yh + offset) / (ys + offset)), 0f);
                    gain[p] = g; gain[p + 1] = g; gain[p + 2] = g;
                }
            });
        }

        // The declared range per channel is what the content needs and no wider — a wider range
        // spends the map's eight bits on gains nobody has. It always includes zero, so that a
        // pixel where the renditions agree encodes as exactly "no gain" whatever its neighbours do.
        Span<float> min = stackalloc float[3];
        Span<float> max = stackalloc float[3];
        min.Fill(0f);
        max.Fill(0f);
        for (int i = 0; i < gain.Length; i += 3)
        {
            for (int c = 0; c < 3; c++)
            {
                float g = gain[i + c];
                if (g < min[c]) min[c] = g;
                if (g > max[c]) max[c] = g;
            }
        }
        for (int c = 0; c < 3; c++)
        {
            if (max[c] - min[c] < MinimumSpan) max[c] = min[c] + MinimumSpan;
        }

        float minR = min[0], minG = min[1], minB = min[2];
        float maxR = max[0], maxG = max[1], maxB = max[2];
        float[] codes = gain;   // encoded in place: the raw gains are not needed once bounded
        ParallelSweep.OverPixels(w * h, (from, to) =>
        {
            for (int p = from; p < to; p += 3)
            {
                codes[p] = Encode(codes[p], minR, maxR);
                codes[p + 1] = Encode(codes[p + 1], minG, maxG);
                codes[p + 2] = Encode(codes[p + 2], minB, maxB);
            }
        });

        // The declared capacity is the headroom at which a reader applies the map in full. It is
        // the CONTENT's largest gain, not the master's nominal peak: the shoulder approaches its
        // asymptote and never reaches it, so a +2.5-stop master holds perhaps +2.0 stops of gain,
        // and a display with 2.0 stops of headroom can show all of it. Declaring 2.5 would make
        // that display apply only 80 % and keep the top fifth of the file's highlights for a
        // display that does not exist. libultrahdr makes the same choice (capacity = max content
        // boost); the master's own peak is still on the roll and in the float32 TIFF.
        float capacity = Math.Max(Math.Max(maxR, maxG), maxB);
        var metadata = new GainMapMetadata(
            minR, minG, minB,
            maxR, maxG, maxB,
            DefaultGamma,
            offset,
            offset,
            HdrCapacityMin: 0f,
            HdrCapacityMax: Math.Min(capacity, MathF.Log2(target.HighlightHeadroom)));
        return new HdrGainMap(new ImageBuffer(w, h, codes), metadata, baseSpace, channels);
    }

    /// <summary>
    /// The HDR rendition as the map reconstructs it: linear in <paramref name="baseSpace"/>'s
    /// primaries, every component non-negative, nothing bounded above. Pixels inside the base's
    /// gamut are untouched; a pixel with a negative component is pulled toward its
    /// luminance-matched grey by the smallest amount that brings it to the gamut boundary, so
    /// its luminance and hue survive and only its saturation gives. The carrier's colour outside
    /// the base's primaries has no representation in a gain-map JPEG — see the class remarks.
    /// </summary>
    public static float[] HdrInBaseGamut(RenderedFrame hdr, ColorSpaceDef baseSpace)
    {
        ArgumentNullException.ThrowIfNull(hdr);
        RequireHdr(hdr);
        float[] d = (float[])hdr.Pixels.Data.Clone();
        if (!SharesPrimaries(ColorSpaces.LinearExtendedSrgb, baseSpace))
            OutputRender.Convert(d, ColorSpaces.LinearExtendedSrgb, baseSpace, GamutMapping.PreserveExtended);
        (float ly, float lg, float lb) = LuminanceWeights(baseSpace);
        ParallelSweep.OverPixels(d.Length / 3, (from, to) =>
        {
            for (int p = from; p < to; p += 3)
            {
                float r = d[p], g = d[p + 1], b = d[p + 2];
                if (float.IsNaN(r) || float.IsNaN(g) || float.IsNaN(b)) { d[p] = d[p + 1] = d[p + 2] = 0f; continue; }
                if (r >= 0f && g >= 0f && b >= 0f) continue;
                float y = ly * r + lg * g + lb * b;
                if (!(y > 0f)) { d[p] = d[p + 1] = d[p + 2] = 0f; continue; }
                // Largest t in [0,1] keeping grey + t·(c − grey) non-negative for all three.
                float t = 1f;
                if (r < 0f) t = Math.Min(t, y / (y - r));
                if (g < 0f) t = Math.Min(t, y / (y - g));
                if (b < 0f) t = Math.Min(t, y / (y - b));
                d[p] = Math.Max(y + t * (r - y), 0f);
                d[p + 1] = Math.Max(y + t * (g - y), 0f);
                d[p + 2] = Math.Max(y + t * (b - y), 0f);
            }
        });
        return d;
    }

    /// <summary>The Y row of the space's RGB→XYZ matrix — its own luminance weights, not sRGB's.</summary>
    private static (float R, float G, float B) LuminanceWeights(ColorSpaceDef space)
    {
        double[,] m = space.ToXyz();
        return ((float)m[1, 0], (float)m[1, 1], (float)m[1, 2]);
    }

    /// <summary>
    /// What a reader does with one sample: the base's linear value, the map's code and the display's
    /// headroom weight in <c>[0,1]</c> (one at or above the master's capacity, zero at SDR) give
    /// back the linear value of the rendition for that display. Used by the tests to prove the map
    /// is the inverse of <see cref="Compute"/>; not on any render path.
    /// </summary>
    public static float Reconstruct(
        float sdrLinear,
        float code,
        int channel,
        GainMapMetadata metadata,
        float weight)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        float gain = metadata.Min(channel)
            + MathF.Pow(code, 1f / metadata.Gamma) * (metadata.Max(channel) - metadata.Min(channel));
        return (sdrLinear + metadata.OffsetSdr) * MathF.Pow(2f, gain * weight) - metadata.OffsetHdr;
    }

    /// <summary>The headroom weight a reader derives from its display, per the shared definition.</summary>
    public static float WeightFor(float displayHeadroom, GainMapMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        float log = MathF.Log2(Math.Max(displayHeadroom, 1f));
        float span = metadata.HdrCapacityMax - metadata.HdrCapacityMin;
        if (!(span > 0f)) return log >= metadata.HdrCapacityMax ? 1f : 0f;
        return Math.Clamp((log - metadata.HdrCapacityMin) / span, 0f, 1f);
    }

    private static float Encode(float gain, float min, float max)
    {
        float t = (gain - min) / (max - min);
        // DefaultGamma is one; written out so the metadata and the encoder cannot disagree.
        return Math.Clamp(MathF.Pow(t, DefaultGamma), 0f, 1f);
    }

    private static bool SharesPrimaries(ColorSpaceDef a, ColorSpaceDef b) =>
        a.Red == b.Red && a.Green == b.Green && a.Blue == b.Blue && a.White == b.White;

    private static void RequireBase(RenderedFrame frame, ColorSpaceDef baseSpace)
    {
        if (frame.Encoding.Reference != ColorReference.DisplayReferred
            || frame.Encoding.Transfer != TransferState.ProfileEncoded
            || frame.Encoding.Range != NumericRange.Normalized)
        {
            throw new ArgumentException(
                "The gain map's base must be a normalized, display-referred, profile-encoded rendition.",
                nameof(frame));
        }
        ProfileIdentity expected = BuiltInColorProfiles.For(baseSpace, ProfileRole.Output).Identity;
        if (frame.OutputProfile.Identity != expected)
        {
            throw new ArgumentException(
                $"The base is tagged {frame.OutputProfile.Description} [{frame.OutputProfile.Identity}], " +
                $"not the exact {baseSpace.Name} profile the map would be defined in.",
                nameof(frame));
        }
    }

    private static void RequireHdr(RenderedFrame frame)
    {
        if (frame.Encoding.Reference != ColorReference.SceneReferred
            || frame.Encoding.Transfer != TransferState.LinearInProfilePrimaries
            || frame.Encoding.Range != NumericRange.Extended)
        {
            throw new ArgumentException(
                "The gain map's HDR rendition must be an extended, linear, scene-referred frame.",
                nameof(frame));
        }
        ProfileIdentity carrier = BuiltInColorProfiles.LinearExtendedSrgb(ProfileRole.Output).Identity;
        if (frame.OutputProfile.Identity != carrier)
        {
            throw new ArgumentException(
                $"The HDR rendition is tagged {frame.OutputProfile.Description}, not the canonical " +
                "linear extended sRGB carrier (D-024).",
                nameof(frame));
        }
    }
}
