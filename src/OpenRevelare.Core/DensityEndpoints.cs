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
/// This is now the ONLY inversion model. It replaced a grade/pivot chain that described the same
/// two ends with a scalar gamma plus a separately-solved wb_high — two descriptions of one fact,
/// which could be, and were, applied on top of each other. See <see cref="For"/>.
/// </summary>
public readonly struct DensityEndpoints
{
    /// <summary>Per-channel slope applied to density. Its between-channel differences ARE the
    /// highlight colour balance.</summary>
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
    /// The endpoints the post-LUT inversion slot should use. One model, always: the per-channel
    /// endpoints, which every roll now carries (see <see cref="FrameParams.DMaxPerChannel"/>).
    ///
    /// <see cref="FrameParams.WbHigh"/> is deliberately NOT passed. The endpoints already encode
    /// the highlight balance — that is what the between-channel differences in their slope ARE —
    /// so handing wb_high in as well applies the same correction twice, and it lands inside the
    /// inversion where no later control can undo it. wb_high survives only as the shadow-side
    /// partner's counterpart in the legacy serialisation and as a manual nudge; anything that
    /// solves the highlight automatically must write the endpoints and leave it at 1.
    /// </summary>
    public static DensityEndpoints For(FrameParams cal) =>
        FromMeasured(cal.DMaxPerChannel, cal.DMax, null, cal.WbOffset);

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
