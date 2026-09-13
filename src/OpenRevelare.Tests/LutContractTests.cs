using OpenRevelare.ColorManagement;
using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// D-033: a LUT's output encoding is a declared contract, not a header lookup; its input is
/// always Cineon; and the HDR exit is pinned to ST 2084 at the 203-nit reference white.
/// </summary>
public sealed class LutContractTests
{
    // ── PQ ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Pq_is_the_ST2084_curve()
    {
        // Textbook anchors: 10 000 nits is code 1.0, 100 nits ≈ 0.508, 203 nits ≈ 0.580.
        Assert.Equal(1.0f, Pq.FromNits(10000f), 1e-6f);
        Assert.Equal(0.508f, Pq.FromNits(100f), 0.001f);
        Assert.Equal(0.580f, Pq.FromNits(203f), 0.001f);
        Assert.Equal(0f, Pq.ToNits(0f), 1e-6f);
        foreach (float nits in new[] { 0.5f, 10f, 203f, 1000f, 4000f })
            Assert.Equal(nits, Pq.ToNits(Pq.FromNits(nits)), nits * 1e-4f);
    }

    [Fact]
    public void Pq_decodes_to_the_carrier_with_reference_white_at_one()
    {
        float[] data = { Pq.FromNits(203f), Pq.FromNits(406f), Pq.FromNits(0f) };
        Pq.DecodeToCarrier(data, OutputTarget.ReferenceWhiteNits);
        Assert.Equal(1f, data[0], 1e-4f);
        Assert.Equal(2f, data[1], 1e-3f);
        Assert.Equal(0f, data[2], 1e-6f);
    }

    // ── Header prefill ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("# Display: ITU-Rec.709, Gamma 2.4", LutOutputEncoding.Rec709)]
    [InlineData("# Display: DCI-P3, Gamma 2.6", LutOutputEncoding.DciP3)]
    [InlineData("# Display: sRGB", LutOutputEncoding.Srgb)]
    [InlineData("# Display: Rec.2020, ST2084", LutOutputEncoding.Rec2020Pq)]
    [InlineData("# Display: Rec.2100 PQ", LutOutputEncoding.Rec2020Pq)]
    [InlineData("# Display: DCI-P3, Gamma 2.4", LutOutputEncoding.Unknown)]
    [InlineData("", LutOutputEncoding.Unknown)]
    public void A_header_display_line_prefills_the_output(string header, LutOutputEncoding expected)
    {
        CubeLut lut = CubeLut.Parse(new StringReader(header + "\n" + IdentityCube), "t");
        Assert.Equal(expected, lut.OutputEncoding);
    }

    [Theory]
    [InlineData("#   Input: DaVinci Intermediate")]
    [InlineData("#   Input: Rec.709 Gamma 2.4")]
    [InlineData("#   Input: ACEScct")]
    public void A_cube_authored_against_anything_but_Cineon_is_refused(string header)
    {
        // The input side is not a choice: this pipeline feeds Cineon and nothing else, so a file
        // that says it wants something else cannot be made right by any declaration.
        var ex = Assert.Throws<InvalidDataException>(() => CubeLut.Parse(new StringReader(header + "\n" + IdentityCube), "t"));
        Assert.Contains("Cineon", ex.Message);
    }

    [Fact]
    public void Resolves_DCI_P3_film_look_header_is_accepted_whole()
    {
        const string header = """
            # Resolve Film Look LUT
            #   Input: Cineon Log
            #        : floating point data (range 0.0 - 1.0)
            #  Output: Kodak 2383 film stock 'look' with D65 White Point
            #        : floating point data (range 0.0 - 1.0)
            # Display: DCI-P3, Gamma 2.6

            """;
        CubeLut lut = CubeLut.Parse(new StringReader(header + IdentityCube), "t");
        Assert.Equal(new LutContract(LutInputEncoding.Cineon, LutOutputEncoding.DciP3),
                     new LutContract(lut.InputEncoding, lut.OutputEncoding));
        Assert.Equal(LutInputEncodingSource.Declared, lut.InputEncodingSource);
    }

    // ── The roll's declaration ───────────────────────────────────────────────

    [Fact]
    public void The_rolls_declaration_outranks_the_header_and_empty_defers_to_it()
    {
        CubeLut lut = CubeLut.Parse(
            new StringReader("# Input: Cineon Log\n# Display: ITU-Rec.709, Gamma 2.4\n" + IdentityCube), "t");

        var deferring = new FrameParams();
        Assert.Equal(new LutContract(LutInputEncoding.Cineon, LutOutputEncoding.Rec709),
                     deferring.LutContractFor(lut));

        var declaring = new FrameParams { PrintLutOutput = "Rec2020Pq" };
        Assert.Equal(new LutContract(LutInputEncoding.Cineon, LutOutputEncoding.Rec2020Pq),
                     declaring.LutContractFor(lut));

        var garbage = new FrameParams { PrintLutOutput = "whatever" };
        Assert.Equal(deferring.LutContractFor(lut), garbage.LutContractFor(lut));
    }

    [Fact]
    public void The_contract_survives_clone_and_the_project_round_trip()
    {
        var cal = new FrameParams { PrintLut = ":kodak-2383", PrintLutOutput = "DciP3" };
        Assert.Equal("DciP3", cal.Clone().PrintLutOutput);
        Assert.Equal("", new FrameParams().PrintLutOutput);

        string path = Path.Combine(Path.GetTempPath(), $"openrevelare-lut-contract-{Guid.NewGuid():N}.orv.json");
        try
        {
            var saved = new Project.Data();
            saved.Frames.Add(new Project.Frame { SourcePath = "frame.tif", Params = cal });
            Project.Save(path, saved);
            FrameParams loaded = Project.Load(path).Frames[0].Params;
            Assert.Equal("DciP3", loaded.PrintLutOutput);
        }
        finally { File.Delete(path); }
    }

    // ── Which target a LUT serves ────────────────────────────────────────────

    [Fact]
    public void A_LUT_is_applied_only_on_the_target_its_output_serves()
    {
        OutputTarget sdr = OutputTarget.Sdr(ColorSpaces.Srgb);
        OutputTarget hdr = OutputTarget.Hdr(peakNits: 1000f);

        var sdrLut = new LutContract(LutInputEncoding.Cineon, LutOutputEncoding.Rec709);
        var hdrLut = new LutContract(LutInputEncoding.Cineon, LutOutputEncoding.Rec2020Pq);
        var unknown = new LutContract(LutInputEncoding.Cineon, LutOutputEncoding.Unknown);

        Assert.True(sdrLut.AppliesTo(sdr));
        Assert.True(sdrLut.AppliesTo(hdr));     // D-034: as colour
        Assert.False(hdrLut.AppliesTo(sdr));
        Assert.True(hdrLut.AppliesTo(hdr));
        Assert.False(unknown.AppliesTo(sdr));
        Assert.False(unknown.AppliesTo(hdr));
    }

    [Fact]
    public void PrintLutFor_answers_the_same_for_the_render_and_the_recipe()
    {
        var sdrRoll = new FrameParams { OutputSpace = "sRGB", PrintLut = ":kodak-2383" };
        Assert.NotNull(ColorPipeline.PrintLutFor(sdrRoll, OutputTarget.Sdr(ColorSpaces.Srgb)));
        Assert.NotNull(ColorPipeline.PrintLutFor(sdrRoll, OutputTarget.Hdr(peakNits: 1000f)));
        var hdrRoll = new FrameParams { OutputSpace = "sRGB", PrintLut = ":kodak-2383", PrintLutOutput = "Rec2020Pq" };
        Assert.Null(ColorPipeline.PrintLutFor(hdrRoll, OutputTarget.Sdr(ColorSpaces.Srgb)));

        var none = new FrameParams { OutputSpace = "sRGB", PrintLut = "" };
        Assert.Null(ColorPipeline.PrintLutFor(none, OutputTarget.Sdr(ColorSpaces.Srgb)));

        var missing = new FrameParams { OutputSpace = "sRGB", PrintLut = Path.Combine(Path.GetTempPath(), "does-not-exist.cube") };
        Assert.Throws<NotSupportedException>(() => ColorPipeline.PrintLutFor(missing, OutputTarget.Sdr(ColorSpaces.Srgb)));
    }

    // ── Renders ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The D-018 gap: a user's cube that declares Rec709 output was accepted by the picker and
    /// then refused by the recipe ("must carry a portable built-in identity"). Now it renders,
    /// and the recipe records what the file contained rather than where it was.
    /// </summary>
    [Fact]
    public void An_external_cube_renders_under_a_declared_contract_and_is_fingerprinted_by_content()
    {
        string path = TempCube(IdentityCube);
        try
        {
            var cal = new FrameParams
            {
                OutputSpace = "sRGB",
                PrintLut = path,
                PrintLutOutput = "Rec709",
                DisplayReferredStage2 = true,
            };
            using var cmm = new LittleCmsEngine();
            RenderedFrame frame = Pipeline.Render(MakeWorkingFrame(MakeSyntheticNegative()), cal, ColorPipelineVersion.ManagedV2, cmm);

            Assert.StartsWith("sha256:", frame.Recipe.PrintLutIdentity);
            Assert.Equal("sha256:" + PrintLuts.Validate(path).ContentIdentity, frame.Recipe.PrintLutIdentity);
            Assert.Contains("Cineon->Rec709", frame.Recipe.GamutPolicy);
            Assert.False(frame.Recipe.PixelProfileMismatch);

            // Same table under a different name → same identity; comments do not change pixels.
            string twin = TempCube("# some other comment\nTITLE \"twin\"\n" + IdentityCube);
            try { Assert.Equal(PrintLuts.Validate(path).ContentIdentity, PrintLuts.Validate(twin).ContentIdentity); }
            finally { PrintLuts.Forget(twin); File.Delete(twin); }
        }
        finally { PrintLuts.Forget(path); File.Delete(path); }
    }

    /// <summary>
    /// An identity cube declared Cineon → Rec709 is exactly the frozen v1 exit's contract, so a
    /// managed render through it must be bit-identical to the built-in path's maths: the contract
    /// added no arithmetic to the Cineon case.
    /// </summary>
    [Fact]
    public void The_Cineon_input_path_is_unchanged_by_the_contract()
    {
        string path = TempCube("# Display: ITU-Rec.709, Gamma 2.4\n" + IdentityCube);
        try
        {
            CubeLut lut = PrintLuts.Validate(path);
            float[] a = { 0.42f, 0.17f, 0.052f, 1.15f, 1.05f, 0.95f };
            float[] b = (float[])a.Clone();
            using var cmm = new LittleCmsEngine();

            ColorPipeline.ToOutputSpaceVia(a, lut, ColorSpaces.Rec709, ColorPipelineVersion.ManagedV2, cmm);
            ColorPipeline.ToOutputSpaceVia(
                b, lut, new LutContract(LutInputEncoding.Cineon, LutOutputEncoding.Rec709), ColorSpaces.Rec709, cmm);
            Assert.Equal(a, b);
        }
        finally { PrintLuts.Forget(path); File.Delete(path); }
    }

    [Fact]
    public void A_DCI_P3_output_is_converted_by_the_CMM_from_the_DCI_profile()
    {
        string path = TempCube("# Display: DCI-P3, Gamma 2.6\n" + IdentityCube);
        try
        {
            CubeLut lut = PrintLuts.Validate(path);
            float[] data = { 0.42f, 0.17f, 0.052f };
            var engine = new RecordingEngine();
            ColorPipeline.ToOutputSpaceVia(data, lut, ColorSpaces.Srgb, ColorPipelineVersion.ManagedV2, engine);

            ColorTransformRequest request = Assert.Single(engine.Requests);
            Assert.Equal(BuiltInColorProfiles.DciP3(ProfileRole.Input).Identity, request.Source.Identity);
            Assert.Equal(BuiltInColorProfiles.Srgb(ProfileRole.Output).Identity, request.Destination.Identity);
        }
        finally { PrintLuts.Forget(path); File.Delete(path); }
    }

    [Fact]
    public void An_HDR_LUT_is_the_extended_rendering_and_an_SDR_LUT_is_its_colour_there()
    {
        string path = TempCube(IdentityCube);
        try
        {
            using var cmm = new LittleCmsEngine();
            WorkingFrame source = MakeWorkingFrame(MakeSyntheticNegative());

            var noLut = new FrameParams { OutputSpace = "sRGB", PrintLut = "", HdrPeakNits = 1000d, DisplayReferredStage2 = true };
            var sdrLut = new FrameParams { OutputSpace = "sRGB", PrintLut = path, PrintLutOutput = "Rec709", HdrPeakNits = 1000d, DisplayReferredStage2 = true };
            var hdrLut = new FrameParams { OutputSpace = "sRGB", PrintLut = path, PrintLutOutput = "Rec2020Pq", HdrPeakNits = 1000d, DisplayReferredStage2 = true };

            RenderedFrame plain = Pipeline.Render(source, noLut, ColorPipelineVersion.ManagedV2, cmm);
            RenderedFrame viaPrint = Pipeline.Render(source, sdrLut, ColorPipelineVersion.ManagedV2, cmm);
            RenderedFrame viaHdr = Pipeline.Render(source, hdrLut, ColorPipelineVersion.ManagedV2, cmm);

            // D-034: the SDR stock is applied on an extended target — as colour — and the recipe says so.
            Assert.NotEqual(plain.Pixels.Data, viaPrint.Pixels.Data);
            Assert.StartsWith("sha256:", viaPrint.Recipe.PrintLutIdentity);
            Assert.Contains("D-034", viaPrint.Recipe.GamutPolicy);
            Assert.Equal(BuiltInColorProfiles.LinearExtendedSrgb(ProfileRole.Output).Identity, viaPrint.OutputProfile.Identity);

            // D-033: the HDR LUT is the rendering. Carrier profile, contract in the recipe, bounded.
            Assert.Equal(BuiltInColorProfiles.LinearExtendedSrgb(ProfileRole.Output).Identity, viaHdr.OutputProfile.Identity);
            Assert.StartsWith("sha256:", viaHdr.Recipe.PrintLutIdentity);
            Assert.Contains("Cineon->Rec2020Pq", viaHdr.Recipe.GamutPolicy);
            Assert.Contains("PQ", viaHdr.Recipe.GamutPolicy);
            float headroom = hdrLut.ResolvedOutputTarget.HighlightHeadroom;
            Assert.All(viaHdr.Pixels.Data, v => Assert.True(v <= headroom + 1e-5f));
            Assert.NotEqual(plain.Pixels.Data, viaHdr.Pixels.Data);

            // And the mirror image: the HDR LUT stands aside on an SDR target.
            var sdrRoll = hdrLut.Clone();
            sdrRoll.HdrPeakNits = 0d;
            var sdrPlain = noLut.Clone();
            sdrPlain.HdrPeakNits = 0d;
            RenderedFrame hdrLutOnSdr = Pipeline.Render(source, sdrRoll, ColorPipelineVersion.ManagedV2, cmm);
            RenderedFrame plainSdr = Pipeline.Render(source, sdrPlain, ColorPipelineVersion.ManagedV2, cmm);
            Assert.Equal(plainSdr.Pixels.Data, hdrLutOnSdr.Pixels.Data);
            Assert.Equal("", hdrLutOnSdr.Recipe.PrintLutIdentity);
        }
        finally { PrintLuts.Forget(path); File.Delete(path); }
    }

    /// <summary>
    /// An identity cube declared PQ out: what leaves the LUT is exactly the Cineon code that
    /// entered it, so the extended path must reproduce "read the code as PQ at 203 nits, rotate
    /// 2020 → carrier" by hand. A neutral stays neutral through the rotation (both D65).
    /// </summary>
    [Fact]
    public void The_PQ_exit_decodes_at_reference_white_into_the_carrier()
    {
        string path = TempCube(IdentityCube);
        try
        {
            using var cmm = new LittleCmsEngine();
            var cal = new FrameParams
            {
                OutputSpace = "sRGB", PrintLut = path, HdrPeakNits = 4000d, DisplayReferredStage2 = true,
                PrintLutOutput = "Rec2020Pq",
            };
            float grey = MathF.Pow(10f, 0.002f * (445f - 1032f));
            float[] actual = { grey, grey, grey };
            ColorPipeline.ToOutputTargetFor(actual, cal, cal.ResolvedOutputTarget, ColorPipelineVersion.ManagedV2, cmm);

            float[] expected = { grey, grey, grey };
            OutputRender.Convert(expected, ColorPipeline.Working, ColorSpaces.Rec709, GamutMapping.Clip);
            LogEncoding.ToCineon(expected);                                    // what the cube was handed
            for (int i = 0; i < 3; i++) expected[i] = Pq.ToNits(expected[i]) / OutputTarget.ReferenceWhiteNits;

            for (int i = 0; i < 3; i++)
                Assert.Equal(expected[i], actual[i], Math.Max(1e-4f, expected[i] * 2e-3f));
        }
        finally { PrintLuts.Forget(path); File.Delete(path); }
    }

    /// <summary>
    /// D-034 pinned: through a real print stock on an extended target, every pixel is the SDR
    /// print rendering times ONE scalar ≥ 1 (its RGB ratios are the print's), the scalar is
    /// exactly 1 wherever the analytic family agrees with itself (below the knee — the print
    /// passes through bit-for-bit there), and it climbs above 1 in the highlights.
    /// </summary>
    [Fact]
    public void A_print_stock_under_HDR_keeps_its_colour_and_takes_the_HDR_tone()
    {
        using var cmm = new LittleCmsEngine();
        CubeLut lut = PrintLuts.Validate(":luts/Rec709 Kodak 2383 D65.cube");
        var contract = new LutContract(LutInputEncoding.Cineon, LutOutputEncoding.Rec709);
        var roll = new FrameParams { OutputSpace = "sRGB", PrintLut = ":luts/Rec709 Kodak 2383 D65.cube", HdrPeakNits = 1000d };
        OutputTarget target = roll.ResolvedOutputTarget;

        // Neutrals and a warm colour, from deep shadow to the top of the Cineon domain.
        float[] codes = { 120f, 250f, 445f, 600f, 685f, 800f, 950f, 1023f };
        var input = new List<float>();
        foreach (float c in codes)
        {
            float lin = MathF.Pow(10f, 0.002f * (c - 1032f));
            input.AddRange(new[] { lin, lin, lin });
            input.AddRange(new[] { lin, lin * 0.7f, lin * 0.45f });
        }

        float[] print = input.ToArray();          // the SDR print, in the carrier's linear light
        ColorPipeline.ToOutputSpaceVia(print, lut, contract, target.Space, cmm);
        float[] hdr = input.ToArray();
        ColorPipeline.ToOutputTargetFor(hdr, roll, target, ColorPipelineVersion.ManagedV2, cmm);

        // The analytic family, to know where the knee is for each sample.
        float[] famSdr = input.ToArray(); float[] famHdr = input.ToArray();
        ColorPipeline.ToOutputTargetFor(famSdr, new FrameParams { OutputSpace = "sRGB" }, OutputTarget.Sdr(ColorSpaces.Srgb), ColorPipelineVersion.ManagedV2, cmm);
        OutputRender.Decode(famSdr, ColorSpaces.Srgb);
        ColorPipeline.ToOutputTargetFor(famHdr, new FrameParams { OutputSpace = "sRGB", HdrPeakNits = 1000d }, target, ColorPipelineVersion.ManagedV2, cmm);

        bool sawLift = false;
        for (int p = 0; p < hdr.Length; p += 3)
        {
            float ySdr = 0.2126f * famSdr[p] + 0.7152f * famSdr[p + 1] + 0.0722f * famSdr[p + 2];
            float yHdr = 0.2126f * famHdr[p] + 0.7152f * famHdr[p + 1] + 0.0722f * famHdr[p + 2];
            float gain = ySdr > 1e-6f ? MathF.Max(yHdr / ySdr, 1f) : 1f;
            for (int c = 0; c < 3; c++)
            {
                if (gain == 1f)
                    Assert.Equal(print[p + c], hdr[p + c]);                       // bit-for-bit
                else
                    Assert.Equal(print[p + c] * gain, hdr[p + c], MathF.Max(1e-5f, hdr[p + c] * 2e-3f));
            }
            if (gain > 1.05f) sawLift = true;
        }
        Assert.True(sawLift, "the top of the ramp must be lifted above the print");
        Assert.All(hdr, v => Assert.True(v <= target.HighlightHeadroom + 1e-4f));
    }

    [Fact]
    public void The_SDR_rendition_keeps_a_print_stock_and_drops_an_HDR_LUT()
    {
        var print = new FrameParams { OutputSpace = "AdobeRGB", PrintLut = ":luts/Rec709 Kodak 2383 D65.cube", HdrPeakNits = 1000d };
        FrameParams p = print.SdrRendition();
        Assert.Equal(":luts/Rec709 Kodak 2383 D65.cube", p.PrintLut);
        Assert.Equal(0d, p.HdrPeakNits);

        var hdrLut = new FrameParams { OutputSpace = "sRGB", PrintLut = ":luts/Rec709 Kodak 2383 D65.cube", PrintLutOutput = "Rec2020Pq", HdrPeakNits = 1000d };
        Assert.Equal("", hdrLut.SdrRendition().PrintLut);
        Assert.Equal("", hdrLut.SdrRendition().PrintLutOutput);
    }

    // ── The shipped LUT folder ───────────────────────────────────────────────

    [Fact]
    public void The_shipped_folder_offers_six_print_stocks_with_their_contracts_prefilled()
    {
        IReadOnlyList<(string Id, string Name)> bundled = PrintLuts.Bundled();
        Assert.Equal(6, bundled.Count);
        foreach ((string id, string name) in bundled)
        {
            Assert.StartsWith(PrintLuts.BundledPrefix, id);
            Assert.True(PrintLuts.IsBuiltin(id), id);
            CubeLut lut = PrintLuts.Validate(id);
            Assert.Equal(LutInputEncoding.Cineon, lut.InputEncoding);
            Assert.Equal(LutInputEncodingSource.Declared, lut.InputEncodingSource);
            Assert.Equal(LutOutputEncoding.Rec709, lut.OutputEncoding);
            Assert.Equal(33, lut.Size);
            Assert.StartsWith("Rec709 ", name);
        }
        Assert.Equal(3, bundled.Count(b => b.Name.Contains("2383", StringComparison.Ordinal)));
        Assert.Equal(3, bundled.Count(b => b.Name.Contains("3513DI", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_pre_D033_sentinel_still_resolves_to_the_same_table_as_its_shipped_file()
    {
        CubeLut embedded = PrintLuts.Validate(":kodak-2383");
        CubeLut shipped = PrintLuts.Validate(":luts/Rec709 Kodak 2383 D65.cube");
        Assert.Equal(embedded.ContentIdentity, shipped.ContentIdentity);
        Assert.False(PrintLuts.IsBuiltin(":luts/../escape.cube"));
        Assert.False(PrintLuts.IsBuiltin(":luts/"));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private const string IdentityCube = """
        LUT_3D_SIZE 2
        0 0 0
        1 0 0
        0 1 0
        1 1 0
        0 0 1
        1 0 1
        0 1 1
        1 1 1
        """;

    private static string TempCube(string text)
    {
        string path = Path.Combine(Path.GetTempPath(), $"openrevelare-lut-contract-{Guid.NewGuid():N}.cube");
        File.WriteAllText(path, text);
        return path;
    }

    private static WorkingFrame MakeWorkingFrame(ImageBuffer pixels)
    {
        var original = new UncharacterizedPixelEncoding(
            CaptureKind.Synthetic, "test:lut-contract",
            CompatibilityPolicy.LegacyTreatNumbersAsWorking, TransferState.Unknown, NumericRange.Extended);
        var source = new SourceDescriptor("test:lut-contract", "LUT contract synthetic negative", original, "test-generated RGB float32");
        return new WorkingFrame(pixels, WorkingSpaceId.LinearAcesCgV1, WorkingAdmission.LegacyUncharacterizedPassthrough, source);
    }

    private static ImageBuffer MakeSyntheticNegative() => new(
        4, 2,
        new[]
        {
            0.81f,  0.52f,  0.29f,
            0.63f,  0.31f,  0.12f,
            0.42f,  0.17f,  0.052f,
            0.24f,  0.075f, 0.015f,
            0.13f,  0.035f, 0.0035f,
            0.055f, 0.010f, 0.0008f,
            1.15f,  1.05f,  0.95f,
            0.008f, 0.0015f, 0.00012f,
        })
    {
        SourceQuantisationStep = 1.0 / 65535.0,
    };

    private sealed class RecordingEngine : IColorManagementEngine
    {
        private readonly LittleCmsEngine _inner = new();
        public List<ColorTransformRequest> Requests { get; } = new();
        public CmmBuildIdentity Build => _inner.Build;
        public ProfileValidationResult Validate(ColorProfileRef profile) => _inner.Validate(profile);
        public CmmDiagnosticsSnapshot GetDiagnostics() => _inner.GetDiagnostics();
        public IColorTransformLease Lease(ColorTransformRequest request)
        {
            Requests.Add(request);
            return _inner.Lease(request);
        }
        public void Dispose() => _inner.Dispose();
    }
}
