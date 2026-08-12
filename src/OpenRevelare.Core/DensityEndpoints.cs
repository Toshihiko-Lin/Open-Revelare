namespace OpenRevelare.Core;

/// <summary>
/// The canonical form of the density-domain inversion: one affine map per channel.
///
/// WHY THIS EXISTS. Steps 3–5 of the inversion apply, per channel, a scan-exposure shift, a
/// multiplicative <c>wb_high</c>, an additive <c>wb_offset</c>, and then a <c>grade</c> rotation
/// about <c>pivot</c> with <c>d_max</c> subtracted. Every one of those is a per-channel affine
/// operation on density, and the composition of per-channel affine maps is a single per-channel
/// affine map. Eight-plus parameters are therefore expressing six degrees of freedom.
///
/// That over-parameterisation is not cosmetic — it is why calibration ORDER matters today. The
/// shadow and highlight ends compete for the same freedom, so solving the highlight first and the
/// shadow second leaves a different answer than the reverse (measured residual 0.7 versus 0.04;
/// see THEORY.md step 5). In canonical form there is nothing to order: each channel states where
/// black sits and where white sits, and those two facts are independent.
///
/// WHAT THE SIX DEGREES OF FREEDOM MEAN. Splitting the per-channel scale and offset into their
/// shared part and their between-channel difference:
///
///   scale  — shared: the density range (what <c>grade</c> sets today)
///            between-channel: the highlight colour cast (what <c>wb_high</c> corrects today)
///   offset — shared: the black level
///            between-channel: the shadow colour cast (what <c>wb_offset</c> corrects today)
///
/// So white balance keeps all four of its colour degrees of freedom. What changes is that it is
/// READ OFF the endpoints rather than applied as a separate stage after them.
///
/// PHASE 1 SCOPE. This type only converts; it changes no behaviour. <see cref="FromLegacy"/>
/// reproduces the current chain exactly (verified to 4.4e-16, i.e. double-precision equality),
/// which is what makes the migration in later phases lossless — an existing project can be
/// restated in canonical form and must render bit-identically.
/// </summary>
public readonly struct DensityEndpoints
{
    /// <summary>Per-channel slope applied to density. Legacy equivalent: <c>grade × wb_high_c</c>.</summary>
    public double[] Scale { get; }

    /// <summary>Per-channel offset added after scaling.</summary>
    public double[] Offset { get; }

    public DensityEndpoints(double[] scale, double[] offset)
    {
        if (scale.Length != 3 || offset.Length != 3)
            throw new ArgumentException("DensityEndpoints requires 3 channels");
        Scale = scale;
        Offset = offset;
    }

    /// <summary>
    /// The canonical map for one channel: <c>D_adj = Scale_c · D + Offset_c</c>, where D is the
    /// film-base-normalised density <c>-log10(T / t_base)</c>.
    /// </summary>
    public double Apply(int channel, double density) => Scale[channel] * density + Offset[channel];

    /// <summary>
    /// Exact restatement of the WHOLE chain (steps 3–5) as one affine map per channel, acting on
    /// the film-base-normalised density.
    ///
    /// USE THIS ONLY where the input has NOT already been through steps 3–4. <see cref="Inversion"/>
    /// folds those into its LUT, so that slot wants <see cref="LegacyStep5"/> instead; passing this
    /// there applies wb_high and wb_offset twice. This full-chain form is what to reason with when
    /// mapping density to output end to end — e.g. <see cref="WhiteBalance"/>, which starts from a
    /// rendered sRGB positive rather than from LUT output.
    ///
    /// Derivation — expanding the chain in <see cref="Inversion"/> (steps 3–5), with
    /// <c>D</c> the film-base-normalised density and <c>w̄</c> = mean(wb_offset):
    ///
    /// <code>
    ///   D₁ = D + ev·log10(2)
    ///   D₂ = D₁·wh_c + (wo_c - w̄)
    ///   D_adj = pivot + (D₂ - pivot)·grade - d_max
    ///
    ///   ⇒ Scale_c  = grade · wh_c
    ///     Offset_c = grade·(ev·log10(2)·wh_c + (wo_c - w̄) - pivot) + pivot - d_max
    /// </code>
    ///
    /// Note <c>wb_offset</c> enters mean-subtracted, matching step 4 — it carries only the
    /// between-channel difference, never a shared shift. Dropping that term would silently
    /// change the black level.
    /// </summary>
    public static DensityEndpoints FromLegacy(FrameParams cal)
    {
        double grade = cal.Grade, pivot = cal.Pivot, dMax = cal.DMax;

        // The legacy chain GATES each term rather than always applying it (see
        // Inversion.BuildDensityLuts): a wb_high of 1.0000001 makes the multiply be SKIPPED,
        // which is not the same as multiplying by the stored value. Reproducing the algebra but
        // not the gating would differ in the last ulp for rolls sitting near these tolerances,
        // and bit-exactness is the whole acceptance test for this migration. So the endpoints are
        // derived from the EFFECTIVE values the LUT actually uses.
        double evShift = cal.ScanExposureEv != 0.0 ? cal.ScanExposureEv * Log10_2 : 0.0;
        bool wbHighActive = !ApproxAll(cal.WbHigh, 1.0);
        bool wbOffsetActive = cal.WbOffset.Any(x => Math.Abs(x) > Tol);
        double woMean = (cal.WbOffset[0] + cal.WbOffset[1] + cal.WbOffset[2]) / 3.0;

        var scale = new double[3];
        var offset = new double[3];
        for (int c = 0; c < 3; c++)
        {
            double wh = wbHighActive ? cal.WbHigh[c] : 1.0;
            double wo = wbOffsetActive ? cal.WbOffset[c] - woMean : 0.0;
            scale[c] = grade * wh;
            offset[c] = grade * (evShift * wh + wo - pivot) + pivot - dMax;
        }
        return new DensityEndpoints(scale, offset);
    }

    /// <summary>
    /// The endpoint form proper: each channel's measured highlight density mapped onto the
    /// output range, with the film base already at zero.
    ///
    /// <c>t_base</c> has put D_min at 0 for every channel (that IS the shadow endpoint, mask and
    /// all), so only the highlight end remains to be stated. Each channel is normalised by its
    /// OWN measured D_max:
    ///
    /// <code>
    ///   Scale_c  = outRange / dMaxPerChannel_c
    ///   Offset_c = -outRange
    /// </code>
    ///
    /// so density 0 → <c>-outRange</c> (black, since T_pos = 10^-outRange) and density
    /// <c>dMaxPerChannel_c</c> → 0 (white, T_pos = 1). No grade, no pivot, no wb_high: the slope
    /// IS the endpoint relationship, and the between-channel differences in that slope are the
    /// highlight colour balance. This is the DaVinci/Cineon shape — decode, invert, set the two
    /// endpoints — with contrast left to the output transform rather than folded in here.
    ///
    /// <paramref name="outRange"/> is roll-uniform, matching how <see cref="FrameParams.DMax"/>
    /// is established across a roll today, so a flat-lit frame is not stretched on its own.
    /// </summary>
    /// <param name="wbHigh">Per-channel highlight nudge, null = none. Divides that channel's
    /// endpoint, so wb_high &gt; 1 brings its white on earlier — the same per-channel multiplier
    /// on the slope that <c>wb_high</c> always was.</param>
    /// <param name="wbOffset">Per-channel shadow nudge in density, null = none. Moves where that
    /// channel's black sits.</param>
    public static DensityEndpoints FromMeasured(double[] dMaxPerChannel, double outRange,
                                                double[]? wbHigh = null, double[]? wbOffset = null)
    {
        var scale = new double[3];
        var offset = new double[3];
        for (int c = 0; c < 3; c++)
        {
            double wh = wbHigh is { Length: 3 } ? Math.Max(wbHigh[c], 1e-6) : 1.0;
            // The shadow nudge moves WHICH DENSITY reads black for this channel. t_base put
            // D_min at 0, so a nudge of wo shifts that endpoint to -wo.
            double dMin = wbOffset is { Length: 3 } ? -wbOffset[c] : 0.0;
            double dMax = dMaxPerChannel[c] / wh;
            // Deriving the slope from the SPAN is what keeps both ends pinned: black lands at
            // -outRange and white at 0 for every channel, whatever the nudges. Subtracting a
            // constant instead would drag the white end along with the black one, turning a
            // colour control into a per-channel gain.
            double span = Math.Max(dMax - dMin, 1e-6);
            scale[c] = outRange / span;
            offset[c] = -outRange - scale[c] * dMin;
        }
        return new DensityEndpoints(scale, offset);
    }

    /// <summary>
    /// Step 5 ALONE, for callers whose input density has already been through steps 3–4.
    ///
    /// <see cref="Inversion"/> folds the film-base divide, the scan-exposure shift, wb_high and
    /// wb_offset into its per-channel LUT, so the density reaching step 5 is already corrected.
    /// Using <see cref="FromLegacy"/> there would apply wb_high and wb_offset a SECOND time.
    /// Step 5 on its own is:
    ///
    /// <code>
    ///   a = pivot + (d - pivot)·grade - d_max
    ///     = grade·d + (pivot·(1 - grade) - d_max)
    /// </code>
    ///
    /// Same slope for all three channels, because in the legacy model the per-channel part
    /// lives entirely in the LUT.
    /// </summary>
    public static DensityEndpoints LegacyStep5(FrameParams cal)
        => LegacyStep5Of(cal.Grade, cal.Pivot, cal.DMax);

    /// <summary>
    /// <see cref="LegacyStep5"/> from loose parameters, for callers that carry grade/pivot/d_max
    /// around without a <see cref="FrameParams"/> — notably the white-balance solve.
    /// </summary>
    public static DensityEndpoints LegacyStep5Of(double grade, double pivot, double dMax)
    {
        double off = pivot * (1.0 - grade) - dMax;
        return new DensityEndpoints(
            new[] { grade, grade, grade },
            new[] { off, off, off });
    }

    /// <summary>
    /// The endpoints the post-LUT inversion slot should use: measured per-channel when the roll
    /// has them, otherwise <see cref="LegacyStep5"/>.
    ///
    /// The measured branch is self-contained — its slope already encodes the highlight balance,
    /// so a roll on that branch must NOT also carry wb_high (see <see cref="FrameParams.DMaxPerChannel"/>).
    /// </summary>
    public static DensityEndpoints For(FrameParams cal) =>
        cal.DMaxPerChannel is { Length: 3 } dm
            ? FromMeasured(dm, cal.DMax, cal.WbHigh, cal.WbOffset)
            : LegacyStep5(cal);

    /// <summary>
    /// The linear value the film base (density 0) maps to — the black floor the inversion
    /// normalises away so a sampled base lands on pure black.
    ///
    /// The legacy pipeline spelled it <c>10^(pivot·(1-grade) - d_max)</c>, which is the same
    /// number written in terms of parameters that no longer exist.
    ///
    /// Taken as the DARKEST of the three channels rather than channel 0's: a shadow nudge moves
    /// each channel's black endpoint independently, so the channels no longer agree on where
    /// black is. Subtracting the darkest floor is the only choice that cannot clip a channel that
    /// legitimately sits below it — the black-floor step clamps at zero, so an over-large floor
    /// would crush shadow detail in whichever channel the nudge pushed down.
    /// </summary>
    public double BlackFloor => Math.Pow(10.0, Math.Min(Math.Min(Offset[0], Offset[1]), Offset[2]));

    // Mirrors Inversion's gating predicates exactly — same constants, same comparison.
    private const double Tol = 1e-8;
    private const double Log10_2 = 0.3010299956639812;

    private static bool ApproxAll(double[] v, double target)
    {
        double atol = Tol + 1e-5 * Math.Abs(target);
        foreach (double x in v)
            if (Math.Abs(x - target) > atol) return false;
        return true;
    }

    /// <summary>
    /// Inverse of <see cref="Apply"/>: recovers the pre-step-5 density from an adjusted one.
    /// The Deep-WB solve needs this to reason backwards from a rendered positive
    /// (see <see cref="WhiteBalance.SrgbToPreStep4Density"/>), and it stays a closed form
    /// because the map is affine — which is the property that lets grade be retired without
    /// giving up the solver.
    /// </summary>
    public double Invert(int channel, double adjusted) => (adjusted - Offset[channel]) / Scale[channel];
}
