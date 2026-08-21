namespace OpenRevelare.Core;

/// <summary>
/// Where a frame's average tone sits relative to Cineon's 90% diffuse white, in stops.
///
/// WHY THIS READS IN CODE VALUES AND NOT ON SCREEN. A display histogram answers "how bright is
/// the picture I am looking at", which changes with the output space and with whichever print
/// stock is selected — the same negative reads differently under 2383 than under the standard
/// transform, because a stock's toe and shoulder move tones around by design. That is the wrong
/// question for calibration. The question <see cref="FrameParams.DMaxPerChannel"/> and
/// <see cref="FrameParams.DMinPerChannel"/> are set to answer is where the picture sits IN THE
/// ENCODING, and the encoding is the one thing every downstream consumer agrees on.
///
/// So this measures the Cineon code the frame's average tone lands on, and reports its distance
/// from 336 — where an 18% mid grey sits under the standard placement. Reading zero means the
/// frame is exposed the way a reflected-light meter would call correct, and therefore placed
/// where a Cineon LUT expects to find it, whichever LUT that is.
///
/// THE REFERENCE IS THE GREY, NOT THE WHITE. A frame's average tone is by definition near a mid
/// grey — that is the assumption every reflected-light meter is built on — while 90% diffuse
/// white (code 685) is occupied by a small, bright minority of the picture. Metering against 685
/// would read −2.32 stops on a correctly exposed frame, and chasing that reading back to zero
/// would over-expose every roll by the same amount. The two references are a fixed distance
/// apart, log10(0.90/0.18) = 0.699 density = 349.5 codes = 2.32 stops, so the same measurement
/// states both; this reports the one that answers "is the exposure right".
///
/// REPORTED IN STOPS RATHER THAN CODES because a stop is the unit the measurement is actually
/// about — exposure — and because the code axis is logarithmic in density, so a fixed number of
/// codes IS a fixed number of stops. One stop is log10(2)/0.002 ≈ 150.5 codes.
/// </summary>
public static class ExposureMeter
{
    /// <summary>Codes per stop: a factor of two in scene luminance, in Cineon's 0.002/code.</summary>
    private const double CodesPerStop = 0.3010299956639812 / FrameParams.CineonDensityPerCode;

    /// <summary>
    /// Where an 18% mid grey sits under the standard placement — the reference this meter reads
    /// against, and the code a correctly exposed frame's average lands on.
    ///
    /// Derived from <see cref="DiffuseWhiteCode"/> rather than written as a literal, so the two
    /// cannot drift: a grey is log10(0.90/0.18) in density below a 90% white, whatever code the
    /// white is placed at.
    /// </summary>
    public const double ReferenceCode =
        DiffuseWhiteCode - 0.6989700043360187 / FrameParams.CineonDensityPerCode;

    /// <summary>Cineon's 90% diffuse white. Not the meter's reference — see
    /// <see cref="ReferenceCode"/> — but the anchor it is defined from.</summary>
    public const double DiffuseWhiteCode = 685.0;

    /// <summary>
    /// The frame's representative tone as a Cineon code, and how far that is from
    /// <see cref="ReferenceCode"/> in stops.
    ///
    /// THE STATISTIC IS THE MEDIAN, NOT THE MEAN, and the difference is the difference between a
    /// usable reading and a wrong one on any frame with a deliberately sacrificed highlight.
    ///
    /// A photograph often blows part of itself ON PURPOSE — a window behind the subject, a bright
    /// overcast sky, a backlit rim — to keep the subject correctly exposed. Those pixels are not
    /// the picture the exposure is about, but a mean counts them at full weight: measured on a
    /// synthetic frame with the subject at code 336, a blown region covering 30% of the frame
    /// pulled the mean to code 445 (+0.73 stops), and D_max solved from that darkened the SUBJECT
    /// by three quarters of a stop to bring the average back down. Half the frame blown cost 1.21
    /// stops. That is the meter obeying the part of the frame the photographer chose to give up.
    ///
    /// The median is immune to it as long as the sacrificed region is a minority: the same 30%
    /// case reads +0.00. It breaks down past 50%, which is unavoidable — a frame that is more sky
    /// than subject has no exposure that is right for both, and no statistic invents one.
    ///
    /// On an ordinary frame with no extremes the two agree closely, so this is not a change of
    /// intent. It changes the answer exactly where the mean was answering the wrong question.
    /// </summary>
    /// <param name="linearPositive">Stage 1's output: the linear positive 10^D_adj, interleaved
    /// RGB. NOT display-encoded — this must run before any rendering.</param>
    public static (double Code, double Stops) Measure(float[] linearPositive)
    {
        // Bare film base is the DARKEST thing in the positive — D_min maps to 10^-OutputRange by
        // construction — so sprocket rows, rebate and scan margin are a block of near-minimum
        // values sitting inside the frame. Including them drags the geometric mean down in
        // proportion to how much of the scan is border, and the error is self-reinforcing: a
        // reading that is too dark solves D_max too low, which over-exposes the picture, which is
        // the opposite of what the border implies. So anything at or near the base floor is not
        // picture and does not vote.
        //
        // THE CUT HAS TO CLEAR THE BASE'S OWN VARIATION, not just the nominal floor. A film base
        // is never one value across a scan: uneven development, lamp falloff, scanner vignetting
        // and dust spread it over a range, so its BRIGHTEST pixels sit measurably above the floor
        // D_min was calibrated to. Cutting at the floor would keep that upper tail — the exact
        // pixels a wide border contributes most of — and the reading would still be dragged down.
        //
        // 2/3 stop covers about 0.20 density of spread, which is well beyond what a base shows in
        // practice (0.02–0.10 typical). The asymmetry is deliberate: cutting too low leaves base
        // in the average and mis-solves the exposure, while cutting too high only discards the
        // deepest shadows — codes below ~190 out of a 95..1032 span, which carry little of what
        // the average is describing. So the margin is generous on purpose.
        float baseFloor = (float)Math.Pow(10.0, -FrameParams.OutputRange);
        float cut = baseFloor * 1.587f;   // +2/3 stop ≈ 0.20 density above the base
        // The mean is taken in the LOG domain, not on linear values. Averaging linear light and
        // taking the log afterwards is dominated by the brightest pixels — a window or a specular
        // highlight drags the reading up by a stop or more while the picture it is supposed to
        // describe has not moved. The log-domain mean is the geometric mean of the luminances,
        // which is the standard definition of a scene's average tone and what a reflected-light
        // meter approximates.
        // Collected rather than accumulated, because a median needs the samples. At 3 doubles per
        // pixel this is the one place the meter pays for memory — a 1.7 MP preview is ~14 MB,
        // which is small beside the frame buffers already in flight, and Measure runs on the
        // PREVIEW rather than the export buffer.
        var logs = new List<double>(linearPositive.Length / 3);

        for (int p = 0; p < linearPositive.Length; p += 3)
        {
            // Rec709 luminance: the meter reads TONE, so the three channels have to be combined
            // by how much each contributes to brightness rather than averaged flat.
            double y = 0.2126 * linearPositive[p]
                     + 0.7152 * linearPositive[p + 1]
                     + 0.0722 * linearPositive[p + 2];
            // Bare base and anything below it (sprocket cores, opaque rebate, RAW padding) is
            // not picture — see the cut above.
            if (y <= cut) continue;
            logs.Add(Math.Log10(y));
        }

        // Every pixel was base or below: an unexposed frame, or a scan that is all border. There
        // is no picture to meter, so say so rather than reporting the base as if it were one.
        if (logs.Count == 0) return (FrameParams.CineonBlackCode, double.NaN);

        double meanLog = Median(logs);
        // The same affine map LogEncoding applies, in code units: D_adj = meanLog, and
        // code = 1032 + D_adj / 0.002.
        double code = FrameParams.CineonWhiteCode + meanLog / FrameParams.CineonDensityPerCode;
        return (code, (code - ReferenceCode) / CodesPerStop);
    }

    /// <summary>
    /// The per-channel D_max that puts a frame's average tone on <see cref="ReferenceCode"/>,
    /// holding D_min and the channels' relative balance fixed.
    ///
    /// WHY D_MAX FOLLOWS THE METER RATHER THAN BEING MEASURED. The highlight detector reads the
    /// densest 0.1% tail — the brightest thousandth of the frame, which is a SPECULAR highlight,
    /// not a diffuse white. How far that overshoots the diffuse white is a property of the scene
    /// (a window, a chrome bumper, an overcast sky) and varies frame to frame, so pinning it to a
    /// fixed code makes the picture's placement depend on whether the photograph happened to
    /// contain a light source.
    ///
    /// THIS WAS REMOVED ONCE AND PUT BACK. The removal was prompted by 自动白点 — which sets the
    /// detector's endpoint and stops — looking better than the full chain. It does, but only
    /// because it moves one endpoint inside a calibration the meter had already placed. With the
    /// meter gone from the chain entirely, nothing constrains the placement: pinning the brightest
    /// 0.1% to code 1032 leaves a normal frame's average around code 350–700 against a target of
    /// 336, and the picture blows out. Metering is what makes the placement defensible.
    ///
    /// Metering the TONE instead measures the picture rather than its brightest accident. So the
    /// exposure is what gets pinned, and D_max is solved from it: whatever ceiling places this
    /// frame's tone on the grey reference is the ceiling this frame has. The specular highlight
    /// then lands wherever the scene actually put it — above the diffuse white, in the headroom
    /// between 685 and 1032, which is what that headroom is for.
    ///
    /// THE SOLVE IS CLOSED FORM. The endpoint map is affine in density, so shifting the average
    /// by <c>Δ</c> codes needs the span scaled by exactly the ratio that moves it there:
    ///
    ///   <c>D_max_c' = D_min_c + (D_max_c − D_min_c) · (avg − blackCode) / (target − blackCode)</c>
    ///
    /// with <c>avg</c> and <c>target</c> both in codes. Each channel keeps its own D_min and its
    /// own span ratio, so the roll's colour balance — which lives in the DIFFERENCES between the
    /// three spans — is carried through untouched. It is a placement, not a grade.
    /// </summary>
    /// <param name="measuredCode">The frame's average tone, from <see cref="Measure"/>.</param>
    /// <param name="dMax">The current per-channel highlight endpoint.</param>
    /// <param name="dMin">The current per-channel shadow endpoint.</param>
    /// <param name="targetCode">Where the average should land; <see cref="ReferenceCode"/> by
    /// default.</param>
    public static double[] SolveDMaxForAverage(double measuredCode, double[] dMax, double[] dMin,
                                               double targetCode = ReferenceCode)
    {
        double black = FrameParams.CineonBlackCode;
        double from = measuredCode - black;
        double to = targetCode - black;

        var solved = new double[3];
        // A frame whose average sits at or below the black end carries no exposure to place —
        // an all-black frame, or one where the base was sampled above the picture. Leave the
        // endpoints alone rather than solving against a degenerate ratio.
        if (!(from > 1e-6) || !(to > 1e-6))
        {
            Array.Copy(dMax, solved, 3);
            return solved;
        }

        double ratio = from / to;
        for (int c = 0; c < 3; c++)
        {
            double span = Math.Max(dMax[c] - dMin[c], 1e-6);
            solved[c] = dMin[c] + span * ratio;
        }
        return solved;
    }

    /// <summary>
    /// The median of <paramref name="values"/>, computed in place.
    ///
    /// Sorts rather than using a selection algorithm: the caller hands over a per-pixel list that
    /// is already built, the sort is O(n log n) on a preview-sized array and runs once per frame,
    /// and a quickselect would trade readable code for a saving that does not show up beside the
    /// decode and render either side of it.
    ///
    /// An even count averages the two middle samples, which matters less here than it would on a
    /// small sample but keeps the statistic well defined.
    /// </summary>
    private static double Median(List<double> values)
    {
        values.Sort();
        int n = values.Count;
        return (n & 1) == 1 ? values[n / 2] : 0.5 * (values[n / 2 - 1] + values[n / 2]);
    }
}
