using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// The print-film emulation path, covered at the invariants that actually broke while it was
/// being written. Every test here corresponds to a bug that shipped into a build and was found
/// by looking at the screen — which is the argument for the file existing: the pipeline has four
/// render routes and a parameter has to reach all of them identically, and walking them by hand
/// missed one every time.
///
/// No cube file is committed (they are vendor-licensed), so the tests that need a 3D LUT build
/// one in memory. That is better than a fixture anyway: a synthetic cube with a KNOWN response
/// lets a test assert the arithmetic instead of a particular stock's look.
/// </summary>
public class PrintLutTests
{
    // ── The synthetic cube ───────────────────────────────────────────────────────

    /// <summary>
    /// Writes a cube whose response is exactly <paramref name="f"/> applied per channel, so a
    /// test can predict what any input must produce.
    /// </summary>
    private static string WriteCube(string path, int size, Func<double, double> f,
                                    string? title = null, double domainMin = 0.0,
                                    double domainMax = 1.0, bool inputRangeKeyword = false)
    {
        using var w = new StreamWriter(path);
        if (title != null) w.WriteLine($"TITLE \"{title}\"");
        w.WriteLine($"LUT_3D_SIZE {size}");
        if (inputRangeKeyword) w.WriteLine($"LUT_3D_INPUT_RANGE {domainMin} {domainMax}");
        else if (domainMin != 0.0 || domainMax != 1.0)
        {
            w.WriteLine($"DOMAIN_MIN {domainMin} {domainMin} {domainMin}");
            w.WriteLine($"DOMAIN_MAX {domainMax} {domainMax} {domainMax}");
        }
        // Red varies fastest, per the .cube spec.
        for (int b = 0; b < size; b++)
            for (int g = 0; g < size; g++)
                for (int r = 0; r < size; r++)
                {
                    double t = (double)(size - 1);
                    w.WriteLine($"{f(r / t):F6} {f(g / t):F6} {f(b / t):F6}");
                }
        return path;
    }

    private static string TempCube(string name) =>
        Path.Combine(Path.GetTempPath(), $"orv-test-{Guid.NewGuid():N}-{name}.cube");

    // ── Parsing ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Identity_cube_reproduces_its_input()
    {
        string p = WriteCube(TempCube("identity"), 33, x => x);
        try
        {
            var lut = CubeLut.Load(p);
            Assert.Equal(33, lut.Size);

            var data = new[] { 0f, 0.25f, 0.5f, 0.75f, 1f, 0.1f, 0.9f, 0.33f, 0.66f };
            var expected = (float[])data.Clone();
            lut.Apply(data);

            for (int i = 0; i < data.Length; i++)
                Assert.Equal(expected[i], data[i], 3);
        }
        finally { File.Delete(p); }
    }

    /// <summary>
    /// Tetrahedral interpolation keeps the neutral axis exact. This is the reason it was chosen
    /// over trilinear, which blends in six off-axis corners and desaturates greys slightly — on a
    /// print stock, whose response along the neutral is steep, that shows as banding through skin
    /// and sky. A coarse cube makes the difference measurable: with only 5 nodes per axis, the
    /// sample sits far from a grid point almost everywhere.
    /// </summary>
    [Fact]
    public void Neutrals_stay_neutral_through_a_coarse_cube()
    {
        // A non-linear response, so any interpolation error shows up rather than cancelling.
        string p = WriteCube(TempCube("gamma"), 5, x => Math.Pow(x, 2.2));
        try
        {
            var lut = CubeLut.Load(p);
            for (int i = 0; i <= 40; i++)
            {
                float v = i / 40f;
                var px = new[] { v, v, v };
                lut.Apply(px);
                Assert.Equal(px[0], px[1], 6);
                Assert.Equal(px[1], px[2], 6);
            }
        }
        finally { File.Delete(p); }
    }

    /// <summary>
    /// Resolve's film-look cubes state their domain with LUT_3D_INPUT_RANGE rather than
    /// DOMAIN_MIN/MAX. Both spellings have to be honoured: the value happens to be 0..1 in those
    /// files, so ignoring the keyword would have gone unnoticed until a cube declared something
    /// else and silently sampled the wrong part of itself.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Declared_input_domain_is_honoured(bool useInputRangeKeyword)
    {
        // Identity over a domain of 0..2, so an input of 2.0 must come out as the cube's top.
        string p = WriteCube(TempCube("domain"), 9, x => x, domainMin: 0.0, domainMax: 2.0,
                             inputRangeKeyword: useInputRangeKeyword);
        try
        {
            var lut = CubeLut.Load(p);
            Assert.Equal(2.0f, lut.DomainMax[0], 4);

            var data = new[] { 2f, 2f, 2f, 1f, 1f, 1f, 0f, 0f, 0f };
            lut.Apply(data);
            Assert.Equal(1.0f, data[0], 3);    // top of domain -> top of table
            Assert.Equal(0.5f, data[3], 3);    // halfway
            Assert.Equal(0.0f, data[6], 3);
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void Values_outside_the_domain_clamp_rather_than_extrapolate()
    {
        // Clamping matters: a cube says nothing beyond its corners, and continuing the last
        // cell's gradient would invent highlight detail the stock does not have.
        string p = WriteCube(TempCube("clamp"), 9, x => x);
        try
        {
            var lut = CubeLut.Load(p);
            var data = new[] { 5f, 5f, 5f, -3f, -3f, -3f };
            lut.Apply(data);
            Assert.Equal(1.0f, data[0], 4);
            Assert.Equal(0.0f, data[3], 4);
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void Title_comes_from_the_file_then_falls_back_to_the_filename()
    {
        string titled = WriteCube(TempCube("t"), 5, x => x, title: "Kodak 2383 D65");
        string bare = WriteCube(TempCube("bare-name"), 5, x => x);
        try
        {
            Assert.Equal("Kodak 2383 D65", CubeLut.Load(titled).Title);
            Assert.Contains("bare-name", CubeLut.Load(bare).Title);
        }
        finally { File.Delete(titled); File.Delete(bare); }
    }

    [Fact]
    public void Malformed_files_are_rejected_with_a_reason()
    {
        string tooFew = TempCube("short");
        File.WriteAllText(tooFew, "LUT_3D_SIZE 3\n0 0 0\n1 1 1\n");
        string oneD = TempCube("1d");
        File.WriteAllText(oneD, "LUT_1D_SIZE 16\n0 0 0\n");
        string noSize = TempCube("nosize");
        File.WriteAllText(noSize, "# just a comment\n0.5 0.5 0.5\n");
        try
        {
            Assert.Throws<InvalidDataException>(() => CubeLut.Load(tooFew));
            Assert.Throws<InvalidDataException>(() => CubeLut.Load(oneD));
            Assert.Throws<InvalidDataException>(() => CubeLut.Load(noSize));
        }
        finally { File.Delete(tooFew); File.Delete(oneD); File.Delete(noSize); }
    }

    // ── Log encoding ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The anchors the whole path is calibrated on — Cineon's own, read from the one place that
    /// states them. 95 and 1032 are the ends of the encoding domain and 0.002 is its step; a
    /// previous revision anchored white at 685 instead, which forced a bespoke 0.00318 step while
    /// OutputRange went on being computed at 0.002, leaving two scales for one line.
    ///
    /// White is ABOVE 1.0 once normalised, because 1032 exceeds the 10-bit full scale of 1023.
    /// That headroom is real and is why a print-film cube declares a domain wider than [0,1].
    /// </summary>
    [Fact]
    public void Endpoints_land_on_the_Cineon_anchors()
    {
        Assert.Equal(95.0f, LogEncoding.Black * 1023f, 1);
        Assert.Equal(1032.0f, LogEncoding.White * 1023f, 1);
        Assert.True(LogEncoding.White > 1.0f, "1032 normalises above full scale");
    }

    /// <summary>
    /// THE CALIBRATION CONTRACT. Stage 1 emits 10^D_adj with the sampled base at
    /// 10^-OutputRange and the film's density ceiling at 1 — no black-point normalisation. Those
    /// two must land on the encoding's two ends.
    ///
    /// Stage 1 used to normalise the base to linear ZERO before handing over, so that the
    /// pass-through path rendered it as pure black. That was Stage 1 deciding, on the display
    /// rendering's behalf, that a calibrated film base is black; in the Cineon workflow it is a
    /// grey at code 95, and only a display transform takes it to black.
    /// </summary>
    [Fact]
    public void Sampled_black_encodes_to_code_95_not_below_it()
    {
        float floor = (float)Math.Pow(10.0, -FrameParams.OutputRange);

        var atBlack = new[] { floor, floor, floor };
        LogEncoding.ToCineon(atBlack);
        Assert.Equal(95.0f, atBlack[0] * 1023f, 1);

        // The density ceiling is 10^0 = 1 and must land on the white anchor.
        var atWhite = new[] { 1f, 1f, 1f };
        LogEncoding.ToCineon(atWhite);
        Assert.Equal(1032.0f, atWhite[0] * 1023f, 1);

        // Nothing may encode below the black anchor — T=0 would take the log to -infinity.
        var below = new[] { 0f, -0.5f, float.Epsilon };
        LogEncoding.ToCineon(below);
        foreach (float v in below)
            Assert.True(v * 1023f >= 94.9f, $"encoded to {v * 1023f}, below the 95 anchor");
    }

    /// <summary>
    /// Mid-tones must sit where the density model says, not merely between the endpoints. A
    /// half-density step below the ceiling is exactly half the span in code terms, because the
    /// mapping is affine in density by construction.
    /// </summary>
    [Fact]
    public void Encoding_is_affine_in_density()
    {
        var codes = new float[3];
        for (int i = 0; i < 3; i++)
        {
            double dAdj = -FrameParams.OutputRange * i / 2.0;
            var px = new[] { (float)Math.Pow(10.0, dAdj) };
            LogEncoding.ToCineon(px);
            codes[i] = px[0] * 1023f;
        }
        Assert.Equal(1032.0f, codes[0], 1);
        Assert.Equal(563.5f, codes[1], 1);   // midpoint of 95..1032
        Assert.Equal(95.0f, codes[2], 1);
    }

    [Fact]
    public void ToCineon_and_FromCineon_are_inverses()
    {
        float floor = (float)Math.Pow(10.0, -FrameParams.OutputRange);
        var original = new[] { floor, 0.02f, 0.05f, 0.18f, 0.5f, 0.9f, 1f };
        var roundTripped = (float[])original.Clone();
        LogEncoding.ToCineon(roundTripped);
        LogEncoding.FromCineon(roundTripped);

        for (int i = 0; i < original.Length; i++)
            Assert.Equal(original[i], roundTripped[i], 5);
    }

    /// <summary>
    /// THE FILM BASE RENDERS AS BLACK — the rendering normalises code 95 to zero.
    ///
    /// This reverses what this test previously asserted. The earlier version pinned the base to a
    /// lifted grey (0.10–0.25) on the reasoning that normalising shifted the curve away from cubes
    /// authored against the same encoding. Measured, the opposite holds: normalised, code 250
    /// renders at 0.172 against 2383's 0.10 and code 328 at 0.259 against 2383's 0.18, where the
    /// un-normalised curve gave 0.208 and 0.282 — closer to the stock at both points, not further.
    ///
    /// The base is pinned to 95 by the roll's calibration, so nothing a picture contains lies
    /// below it. Rendering it as black is slightly deeper than a real print (2383 gives 0.037),
    /// which is the accepted trade: the base and anything darker collapse to a common 0.
    /// </summary>
    [Fact]
    public void The_film_base_renders_as_black()
    {
        float floor = (float)Math.Pow(10.0, -FrameParams.OutputRange);

        var atBase = new[] { floor, floor, floor };
        ColorPipeline.ToOutputSpace(atBase, ColorSpaces.Srgb);
        Assert.All(atBase, v => Assert.Equal(0f, v, 4));

        // The film's density ceiling renders as very nearly display white — NOT exactly white.
        // It maps to code 1032, and the shoulder rolls the latitude above 685 asymptotically
        // toward 1 rather than clipping it, so the densest thing on the negative keeps a sliver of
        // separation from paper white instead of being burned into it.
        var atCeiling = new[] { 1f, 1f, 1f };
        ColorPipeline.ToOutputSpace(atCeiling, ColorSpaces.Srgb);
        Assert.All(atCeiling, v => Assert.InRange(v, 0.98f, 0.999f));
    }

    /// <summary>
    /// The transform's two anchors: Cineon's 90% diffuse white at code 685 → 1, and the encoding's
    /// black end at code 95 → 0. The second is the normalisation this rendering applies; it is a
    /// LOOK decision belonging to the rendering, not a change to the encoding, which still carries
    /// 95 untouched for the print-LUT path to consume.
    /// </summary>
    [Fact]
    public void CineonToDisplay_anchors_both_ends()
    {
        // The diffuse white is where the SHOULDER starts to bite: without it 685 sat at linear 1,
        // now it sits at 0.75 and encodes to 0.881 — the point the real 2383 cube puts it at.
        var white = new[] { 685f / 1023f };
        ColorPipeline.CineonToDisplay(white);
        Assert.Equal(0.75f, white[0], 3);

        var black = new[] { 95f / 1023f };
        ColorPipeline.CineonToDisplay(black);
        Assert.Equal(0f, black[0], 6);

        // Headroom above the diffuse white is rolled off toward 1 and never reaches it, so the
        // encoder has nothing left to clamp — that roll-off IS the fix, see Shoulder.
        var headroom = new[] { 1032f / 1023f };
        ColorPipeline.CineonToDisplay(headroom);
        Assert.True(headroom[0] > white[0], $"code 1032 must exceed the diffuse white, got {headroom[0]}");
        Assert.True(headroom[0] < 1f, $"code 1032 must not reach 1, got {headroom[0]}");
    }

    /// <summary>
    /// THE LATITUDE ABOVE THE DIFFUSE WHITE SURVIVES TO THE SCREEN — it used to clip.
    ///
    /// The encoding runs to code 1032 while a picture's white sits at 685, so 2.31 stops of
    /// latitude live above it. Rendering that span as a flat 1.0 discarded it, and made switching
    /// to a print-film cube look like the cube was darkening the highlights: the cube keeps the
    /// latitude (2383 puts 685 at 0.880), so detail reappeared where the standard path had shown
    /// paper white. The shoulder aligns the two.
    /// </summary>
    [Fact]
    public void The_highlight_latitude_rolls_off_instead_of_clipping()
    {
        const double dpc = FrameParams.CineonDensityPerCode;
        static float[] AtCode(double code)
        {
            float lin = (float)Math.Pow(10.0, (code - FrameParams.CineonWhiteCode) * dpc);
            return new[] { lin, lin, lin };
        }

        // The diffuse white lands where the real 2383 cube puts it, 0.880.
        var white = AtCode(685.0);
        ColorPipeline.ToOutputSpace(white, ColorSpaces.Srgb);
        Assert.All(white, v => Assert.InRange(v, 0.86f, 0.90f));

        // Codes above it stay strictly ordered and strictly below 1 — nothing clips.
        float prev = white[0];
        foreach (double code in new[] { 800.0, 900.0, 1032.0 })
        {
            var px = AtCode(code);
            ColorPipeline.ToOutputSpace(px, ColorSpaces.Srgb);
            Assert.True(px[0] > prev, $"code {code} must exceed the previous step, got {px[0]}");
            Assert.True(px[0] < 1.0f, $"code {code} must not clip, got {px[0]}");
            prev = px[0];
        }

        // The mid-tones are untouched by the shoulder: the knee sits at code 596.
        var mid = AtCode(486.0);
        ColorPipeline.ToOutputSpace(mid, ColorSpaces.Srgb);
        Assert.All(mid, v => Assert.InRange(v, 0.48f, 0.51f));
    }

    /// <summary>
    /// THE PURE CST HANDS THE CALIBRATION BACK UNCHANGED, which is the entire reason it exists.
    ///
    /// The roll's calibration and the exposure meter are stated in two scene-linear quantities: an
    /// 18% mid grey and Cineon's 90% diffuse white. A transform that only decodes the encoding must
    /// return exactly those, or every metered frame is quietly rescaled. The 0.90 factor in
    /// ToOutputSpacePureCst is what makes this hold — decoding code 685 to 1.0 instead would put
    /// the grey at 0.200.
    /// </summary>
    [Fact]
    public void The_pure_CST_preserves_the_calibration_anchors()
    {
        const double dpc = FrameParams.CineonDensityPerCode;
        double greyCode = 685.0 - Math.Log10(0.90 / 0.18) / dpc;

        static float[] AtCode(double code)
        {
            float lin = (float)Math.Pow(10.0, (code - FrameParams.CineonWhiteCode) * dpc);
            return new[] { lin, lin, lin };
        }

        // Into ACEScg, which is scene-linear, so what comes back is the decoded value itself.
        var grey = AtCode(greyCode);
        ColorPipeline.ToOutputSpacePureCst(grey, ColorSpaces.AcesCg);
        Assert.All(grey, v => Assert.Equal(0.18f, v, 3));

        var white = AtCode(685.0);
        ColorPipeline.ToOutputSpacePureCst(white, ColorSpaces.AcesCg);
        Assert.All(white, v => Assert.Equal(0.90f, v, 3));
    }

    /// <summary>
    /// THE PURE CST APPLIES NO DISPLAY RENDERING, and the film base is where that shows. The
    /// standard rendering normalises code 95 to black; the CST leaves it a light grey, because
    /// taking it to black is a look decision and this transform makes none.
    /// </summary>
    [Fact]
    public void The_pure_CST_does_not_normalise_the_film_base()
    {
        float floor = (float)Math.Pow(10.0, -FrameParams.OutputRange);

        var cst = new[] { floor, floor, floor };
        ColorPipeline.ToOutputSpacePureCst(cst, ColorSpaces.Srgb);
        Assert.All(cst, v => Assert.InRange(v, 0.20f, 0.35f));

        // The standard rendering, on the same input, takes it to black.
        var std = new[] { floor, floor, floor };
        ColorPipeline.ToOutputSpace(std, ColorSpaces.Srgb);
        Assert.All(std, v => Assert.Equal(0f, v, 4));
    }

    /// <summary>
    /// THE TRANSFORM MATCHES A CUBE IN THE MIDS AND IS ALLOWED TO DIVERGE IN THE SHADOWS.
    ///
    /// Both render the same encoding, so a mid grey has to land in the same place — measured
    /// against the real Kodak 2383 cube, its mid sits at code 486 and this transform's at 474.
    /// Below that they part company on purpose: a print stock's TOE is precisely what makes it a
    /// look, and 2383 renders code 250 at 0.10 where the standard transform gives 0.25. Asserting
    /// agreement down there would be asserting that a film look does nothing.
    ///
    /// So the test pins the mid-tone crossing, which is shared, and deliberately says nothing
    /// about the toe.
    /// </summary>
    [Fact]
    public void The_midtone_crossing_matches_a_Cineon_authored_stock()
    {
        int found = 0;
        for (int code = 95; code <= 1032; code++)
        {
            var px = new[] { code / 1023f };
            ColorPipeline.CineonToDisplay(px);
            OutputRender.Encode(px, ColorSpaces.Rec709);
            if (px[0] >= 0.5f) { found = code; break; }
        }

        // Kodak 2383 crosses 0.5 at code 486.
        Assert.InRange(found, 455, 515);
    }

    /// <summary>
    /// THE DEFECT A FIRST ATTEMPT HERE SHIPPED: decoding the log and handing the result to the
    /// output space's gamma is the identity in all but name, and renders a flat grey plate with
    /// none of the contrast a display rendering supplies.
    ///
    /// The standard transform folds in Kodak's 0.6 response gamma, and THAT is the contrast. The
    /// test pins it where the difference is largest and most visible: a mid-grey must not come
    /// out where a plain decode would leave it.
    /// </summary>
    [Fact]
    public void CineonToDisplay_applies_the_response_gamma_not_just_a_decode()
    {
        var mid = new[] { 500f / 1023f };
        ColorPipeline.CineonToDisplay(mid);

        // A plain decode (10^((v-white)*0.002*1023), renormalised) would leave code 500 far
        // darker; the response gamma lifts it to roughly a quarter of the range.
        Assert.InRange(mid[0], 0.20f, 0.28f);
    }

    // ── Pipeline routing ─────────────────────────────────────────────────────────

    /// <summary>
    /// THE INVARIANT THAT PROTECTS EVERY EXISTING PROJECT. A roll with no cube must render
    /// bit-for-bit as it did before this feature existed — not approximately, not visually: the
    /// same floats. Anything less means opening an old roll changes its picture.
    /// </summary>
    [Fact]
    public void Pass_through_is_bit_identical_to_plain_step_4()
    {
        float[] Scene() => new[]
        {
            0f, 0f, 0f,   0.02f, 0.05f, 0.11f,   0.18f, 0.18f, 0.18f,
            0.5f, 0.4f, 0.3f,   0.9f, 0.95f, 1f,   1f, 1f, 1f,
            0.77f, 0.12f, 0.34f,   0.01f, 0.99f, 0.5f,
        };

        foreach (var space in new[] { ColorSpaces.Srgb, ColorSpaces.DisplayP3, ColorSpaces.AdobeRgb })
        {
            var viaHelper = Scene();
            ColorPipeline.ToOutputSpaceFor(viaHelper, new FrameParams
            {
                PrintLut = "",
                OutputSpace = space.Name,
            });

            var direct = Scene();
            ColorPipeline.ToOutputSpace(direct, space);

            Assert.Equal(direct, viaHelper);
        }
    }

    /// <summary>
    /// A roll naming a cube that is missing or unreadable must still render. The render path has
    /// no way to present an error and a roll whose LUT moved should not fail to open — it falls
    /// back to pass-through, and the UI reports the problem when the file is PICKED.
    /// </summary>
    [Fact]
    public void A_missing_cube_degrades_to_pass_through()
    {
        var withMissing = new[] { 0.1f, 0.4f, 0.8f };
        ColorPipeline.ToOutputSpaceFor(withMissing,
            new FrameParams { PrintLut = "/definitely/not/here.cube" });

        var plain = new[] { 0.1f, 0.4f, 0.8f };
        ColorPipeline.ToOutputSpace(plain, ColorPipeline.DefaultOutput);

        Assert.Equal(plain, withMissing);
        Assert.Null(PrintLuts.Resolve("/definitely/not/here.cube"));
        Assert.Null(PrintLuts.Resolve(""));
        Assert.Null(PrintLuts.Resolve(null));
    }

    /// <summary>
    /// With a cube selected the render must actually differ, and must stay in range. An emulation
    /// that quietly did nothing would look exactly like the pass-through bug that shipped once
    /// already, where the preview rendered without the LUT because the parameter never reached
    /// the snapshot the renderer builds.
    /// </summary>
    [Fact]
    public void A_selected_cube_changes_the_render_and_stays_in_range()
    {
        // A cube that visibly darkens: output = input^2.
        string p = WriteCube(TempCube("darken"), 33, x => x * x);
        try
        {
            var cal = new FrameParams { PrintLut = p };
            var withLut = new[] { 0.1f, 0.3f, 0.5f, 0.7f, 0.9f, 1f };
            ColorPipeline.ToOutputSpaceFor(withLut, cal);

            var plain = new[] { 0.1f, 0.3f, 0.5f, 0.7f, 0.9f, 1f };
            ColorPipeline.ToOutputSpace(plain, ColorPipeline.DefaultOutput);

            Assert.NotEqual(plain, withLut);
            Assert.All(withLut, v => Assert.InRange(v, 0f, 1f));
            Assert.All(withLut, v => Assert.True(float.IsFinite(v)));
        }
        finally { PrintLuts.Forget(p); File.Delete(p); }
    }

    /// <summary>
    /// Stage 2 runs AFTER the cube, in the display domain, and is untouched by this feature —
    /// that is the design decision that kept the roll's D_max/D_min as the only pre-LUT controls
    /// rather than growing a second, competing set of log-domain sliders. The chain must survive
    /// both routes without producing anything unrenderable.
    /// </summary>
    [Fact]
    public void Stage2_runs_cleanly_over_both_routes()
    {
        string p = WriteCube(TempCube("stage2"), 17, x => Math.Pow(x, 1.0 / 1.8));
        try
        {
            foreach (string lutPath in new[] { "", p })
            {
                var cal = new FrameParams
                {
                    PrintLut = lutPath,
                    Contrast = 0.25,
                    Saturation = 0.15,
                    Highlights = -0.2,
                    Shadows = 0.3,
                    ExposureEv = 0.4,
                };
                var data = new[]
                {
                    0f, 0f, 0f,   0.2f, 0.35f, 0.5f,   0.8f, 0.6f, 0.4f,   1f, 1f, 1f,
                };
                Stage2.ApplyChain(data, cal, cal.ResolvedOutputSpace, encodeExit: true);

                Assert.All(data, v => Assert.True(float.IsFinite(v), $"non-finite with lut='{lutPath}'"));
                Assert.All(data, v => Assert.InRange(v, 0f, 1f));
            }
        }
        finally { PrintLuts.Forget(p); File.Delete(p); }
    }

    /// <summary>
    /// THE OTHER SHIPPED BUG: a print stock characterises how a finished POSITIVE prints, so the
    /// negative view — the film-base sampler and the split preview — must never route through it.
    /// Applying a cube to un-inverted film renders a look over the very pixels the user opened
    /// that view to measure, and feeds the cube values outside the domain it was authored for.
    ///
    /// Exercised through <see cref="RegionRender.Render"/> with negative:true — the real entry
    /// point the sampler calls. Asserting against a bare step-4 call instead would prove nothing:
    /// the bug was a change of WHICH transform that renderer invokes, so a test that never runs
    /// the renderer cannot see it.
    /// </summary>
    [Fact]
    public void The_negative_view_ignores_the_roll_print_lut()
    {
        // Strongly non-linear, so routing through it could not possibly go unnoticed.
        string p = WriteCube(TempCube("neg"), 17, x => x * x * x);
        try
        {
            var frame = new ImageBuffer(16, 16);
            for (int i = 0; i < frame.Data.Length; i++)
                frame.Data[i] = (i % 97) / 96.0f;

            var roi = new RegionRender.Roi(0, 0, 1, 1);

            var withLut = RegionRender.Render(frame, new FrameParams { PrintLut = p },
                                              roi, negative: true);
            var without = RegionRender.Render(frame, new FrameParams { PrintLut = "" },
                                              roi, negative: true);

            Assert.Equal(without.Image.Data, withLut.Image.Data);
        }
        finally { PrintLuts.Forget(p); File.Delete(p); }
    }

    /// <summary>
    /// The positive patch, by contrast, MUST honour the roll's stock — it is the same picture the
    /// main preview shows, at a higher resolution, and the two disagreeing is what makes a patch
    /// flash a different colour as the user zooms.
    /// </summary>
    [Fact]
    public void The_positive_patch_honours_the_roll_print_lut()
    {
        string p = WriteCube(TempCube("pos"), 17, x => x * x * x);
        try
        {
            var frame = new ImageBuffer(16, 16);
            for (int i = 0; i < frame.Data.Length; i++)
                frame.Data[i] = 0.05f + (i % 89) / 120.0f;

            var roi = new RegionRender.Roi(0, 0, 1, 1);

            var withLut = RegionRender.Render(frame, new FrameParams { PrintLut = p }, roi);
            var without = RegionRender.Render(frame, new FrameParams { PrintLut = "" }, roi);

            Assert.NotEqual(without.Image.Data, withLut.Image.Data);
        }
        finally { PrintLuts.Forget(p); File.Delete(p); }
    }

    /// <summary>
    /// The roll parameter has to survive a save/load round trip, and its absence in an older
    /// project must read back as pass-through rather than as anything else.
    /// </summary>
    [Fact]
    public void PrintLut_survives_clone_and_defaults_to_pass_through()
    {
        Assert.Equal("", new FrameParams().PrintLut);

        var cal = new FrameParams { PrintLut = "/some/stock.cube" };
        Assert.Equal("/some/stock.cube", cal.Clone().PrintLut);
    }

    /// <summary>
    /// Encode and Decode must be a true inverse pair. The print path relies on it: the cube emits
    /// Rec709 and any other output space is reached by decoding, converting and re-encoding, so a
    /// lossy pair would tint every non-Rec709 roll.
    /// </summary>
    [Fact]
    public void Encode_and_Decode_round_trip()
    {
        foreach (var space in new[]
                 { ColorSpaces.Srgb, ColorSpaces.Rec709, ColorSpaces.AdobeRgb, ColorSpaces.DisplayP3 })
        {
            var original = new[] { 0f, 0.05f, 0.2f, 0.5f, 0.8f, 1f };
            var data = (float[])original.Clone();
            OutputRender.Encode(data, space);
            OutputRender.Decode(data, space);

            for (int i = 0; i < original.Length; i++)
                Assert.Equal(original[i], data[i], 4);
        }
    }
}
