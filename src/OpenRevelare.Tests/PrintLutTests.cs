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
    /// The anchors the whole path is calibrated on. White is Cineon's 90% diffuse white (685),
    /// NOT the 1032 headroom ceiling — sending the roll's white to 1032 put the entire picture
    /// inside a print stock's shoulder, and no amount of moving D_max could recover it because
    /// that shifts the picture without changing its span.
    /// </summary>
    [Fact]
    public void Endpoints_land_on_the_Cineon_anchors()
    {
        Assert.Equal(95.0f, LogEncoding.Black * 1023f, 1);
        Assert.Equal(685.0f, LogEncoding.White * 1023f, 1);
    }

    /// <summary>
    /// THE BUG THIS FILE EXISTS FOR, ONE OF FOUR. Stage 1 does not emit 10^D_adj: it applies the
    /// black floor afterwards, so what arrives is (10^D_adj - floor)/(1 - floor), with the sampled
    /// black at exactly 0. Taking a logarithm of that put the sampled black near code -574, deep
    /// under a print stock's toe where the whole shadow range collapsed — and the only way to see
    /// a picture again was to pull D_min down until the blacks cleared the toe, wrecking the
    /// calibration to compensate for an encoding mistake.
    /// </summary>
    [Fact]
    public void Sampled_black_encodes_to_code_95_not_below_it()
    {
        // Stage 1's output for the sampled black IS zero, by the black-floor normalisation.
        var atBlack = new[] { 0f, 0f, 0f };
        LogEncoding.ToCineon(atBlack);
        Assert.Equal(95.0f, atBlack[0] * 1023f, 1);

        // The highlight endpoint is 1 and must land on white.
        var atWhite = new[] { 1f, 1f, 1f };
        LogEncoding.ToCineon(atWhite);
        Assert.Equal(685.0f, atWhite[0] * 1023f, 1);

        // Nothing may encode below the black anchor — that is the dead zone under the toe.
        var below = new[] { 0f, -0.5f, float.Epsilon };
        LogEncoding.ToCineon(below);
        foreach (float v in below)
            Assert.True(v * 1023f >= 94.9f, $"encoded to {v * 1023f}, below the 95 anchor");
    }

    /// <summary>
    /// Mid-tones must sit where the density model says, not merely between the endpoints. A
    /// half-density step below white is exactly half the span in code terms, because the mapping
    /// is affine in density by construction.
    /// </summary>
    [Fact]
    public void Encoding_is_affine_in_density()
    {
        double floor = Math.Pow(10.0, -FrameParams.OutputRange);
        double span = 1.0 - floor;

        // Three points evenly spaced in D_adj must be evenly spaced in code.
        var codes = new float[3];
        for (int i = 0; i < 3; i++)
        {
            double dAdj = -FrameParams.OutputRange * i / 2.0;
            double postFloor = (Math.Pow(10.0, dAdj) - floor) / span;
            var px = new[] { (float)postFloor };
            LogEncoding.ToCineon(px);
            codes[i] = px[0] * 1023f;
        }
        Assert.Equal(685.0f, codes[0], 1);
        Assert.Equal(390.0f, codes[1], 1);   // midpoint of 95..685
        Assert.Equal(95.0f, codes[2], 1);
    }

    [Fact]
    public void ToCineon_and_FromCineon_are_inverses()
    {
        var original = new[] { 0f, 0.01f, 0.05f, 0.18f, 0.5f, 0.9f, 1f };
        var roundTripped = (float[])original.Clone();
        LogEncoding.ToCineon(roundTripped);
        LogEncoding.FromCineon(roundTripped);

        for (int i = 0; i < original.Length; i++)
            Assert.Equal(original[i], roundTripped[i], 5);
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
