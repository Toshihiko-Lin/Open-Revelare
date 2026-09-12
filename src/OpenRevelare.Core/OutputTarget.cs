namespace OpenRevelare.Core;

/// <summary>
/// What the render's terminal is allowed to put on the wire above diffuse white.
/// </summary>
public enum OutputDynamicRange
{
    /// <summary>
    /// Display-referred SDR, and byte-for-byte what the application has always produced.
    /// Diffuse white is <c>1.0</c>, nothing exists above it, out-of-gamut colour is gamut-mapped
    /// into <c>[0,1]</c>, and the output space's encoding curve is applied.
    /// </summary>
    DisplayReferredSdr,

    /// <summary>
    /// Scene-referred extended range. Diffuse white is still <c>1.0</c> — the two renderings agree
    /// there, which is the whole point — but highlights above it survive, rolled off toward
    /// <see cref="OutputTarget.HighlightHeadroom"/> instead of being clipped. The data stays
    /// LINEAR in the output primaries: no display encoding curve is applied, because the carrier
    /// (scRGB / CCCS) and the HDR file formats are themselves linear or PQ-encoded downstream.
    /// </summary>
    SceneReferredExtended,
}

/// <summary>
/// The parameterized terminal of the render (D-022).
///
/// <para>
/// WHY THIS EXISTS. Step 4 used to end at a fixed display-referred answer: print LUT or analytic
/// Cineon rendering, gamut-mapped into <c>[0,1]</c>, then the output space's TRC. That is one
/// point in a space of outputs, and it is the only point an SDR file can occupy. Preview on an
/// HDR display and an HDR deliverable are two different consumers of the SAME decision about what
/// lives above diffuse white, so the decision belongs in one parameterized object rather than
/// being re-made at each consumer.
/// </para>
///
/// <para>
/// TODAY'S BEHAVIOUR IS A SPECIAL CASE OF THIS TYPE, NOT A SIBLING OF IT. <see cref="Sdr"/>
/// delegates to the existing code path unchanged, and the golden renders prove it: if the SDR
/// target ever stopped being bit-exact, every existing project would silently re-render.
/// </para>
///
/// <para>
/// NOT DERIVED FROM THE DISPLAY. Invariant I5 says the display environment must not change the
/// render or the export, so the target is a property of the project — the user asks for an HDR
/// rendering — and never of the monitor that happens to be in front of the window. An SDR monitor
/// showing an HDR render clips it; that is the compositor's business, and the existing
/// <c>FinalTransformOwner</c> contract already says whose.
/// </para>
/// </summary>
public sealed record OutputTarget
{
    /// <summary>
    /// The luminance that <c>1.0</c> denotes in a scene-referred extended render, in nits.
    ///
    /// <para>
    /// ITU-R BT.2408's HDR reference white. It is the level broadcasters use for graphics and
    /// SDR-derived content inside a PQ container, and it is the anchor that keeps an HDR render
    /// looking like its SDR sibling rather than merely brighter. On screen the display contract's
    /// own reference-white scale (D-020) takes over instead, because there the OS already knows
    /// what the user chose; this constant is the anchor for FILES, which have no OS to ask.
    /// </para>
    /// </summary>
    public const float ReferenceWhiteNits = 203f;

    /// <summary>The output primaries and white point. In an extended render its TRC is unused.</summary>
    public ColorSpaceDef Space { get; }

    public OutputDynamicRange DynamicRange { get; }

    /// <summary>Peak luminance the render is allowed to reach, in nits.</summary>
    public float PeakNits { get; }

    /// <summary>The luminance of diffuse white — canonical <c>1.0</c> — in nits.</summary>
    public float DiffuseWhiteNits { get; }

    /// <summary>
    /// How far above diffuse white the render may go, as a multiple of it. Exactly <c>1.0</c> for
    /// an SDR target, which is another way of saying an SDR target has no headroom at all.
    /// </summary>
    public float HighlightHeadroom => PeakNits / DiffuseWhiteNits;

    public bool IsExtended => DynamicRange == OutputDynamicRange.SceneReferredExtended;

    private OutputTarget(
        ColorSpaceDef space,
        OutputDynamicRange dynamicRange,
        float peakNits,
        float diffuseWhiteNits)
    {
        Space = space;
        DynamicRange = dynamicRange;
        PeakNits = peakNits;
        DiffuseWhiteNits = diffuseWhiteNits;
    }

    /// <summary>
    /// The display-referred SDR target: today's rendering, unchanged, in <paramref name="space"/>.
    /// </summary>
    public static OutputTarget Sdr(ColorSpaceDef space) =>
        new(space, OutputDynamicRange.DisplayReferredSdr, ReferenceWhiteNits, ReferenceWhiteNits);

    /// <summary>
    /// A scene-referred extended target peaking at <paramref name="peakNits"/>.
    ///
    /// <para>
    /// ITS SPACE IS ALWAYS LINEAR EXTENDED sRGB, AND IS NOT A CHOICE. The output-space picker
    /// selects a display-referred encoding — sRGB, Adobe RGB, Rec709 — and every one of those is
    /// a statement about a bounded range and a transfer curve, neither of which an extended
    /// render has. The canonical carrier (D-005) is the right and only answer here: linear,
    /// Rec709 primaries, unbounded, and able to represent colour outside those primaries as
    /// components outside <c>[0,1]</c>. Choosing "Adobe RGB HDR" would not widen anything — the
    /// carrier already reaches past Adobe RGB — it would only add a curve to undo.
    /// </para>
    ///
    /// <para>
    /// <paramref name="peakNits"/> must exceed <paramref name="diffuseWhiteNits"/>: a target with
    /// no headroom is an SDR target, and accepting one here would produce a "HDR" render that is
    /// identical to the SDR one while claiming otherwise. Say <see cref="Sdr"/> instead.
    /// </para>
    /// </summary>
    public static OutputTarget Hdr(
        float peakNits,
        float diffuseWhiteNits = ReferenceWhiteNits)
    {
        ColorSpaceDef space = ColorSpaces.LinearExtendedSrgb;
        if (!float.IsFinite(diffuseWhiteNits) || diffuseWhiteNits <= 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(diffuseWhiteNits), diffuseWhiteNits, "Diffuse white must be finite and positive.");
        }
        if (!float.IsFinite(peakNits) || peakNits <= diffuseWhiteNits)
        {
            throw new ArgumentOutOfRangeException(
                nameof(peakNits),
                peakNits,
                "An extended target's peak must exceed its diffuse white; a target without headroom is an SDR target.");
        }

        return new OutputTarget(space, OutputDynamicRange.SceneReferredExtended, peakNits, diffuseWhiteNits);
    }
}
