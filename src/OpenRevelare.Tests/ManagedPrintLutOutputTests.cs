using OpenRevelare.ColorManagement;
using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class ManagedPrintLutOutputTests
{
    private const float PcsTolerance = 0.0025f;

    [Theory]
    [InlineData("sRGB")]
    [InlineData("DisplayP3")]
    [InlineData("AdobeRGB")]
    public void Managed_v2_cube_output_matches_Rec709_in_XYZ_through_the_exact_target_profile(
        string outputName)
    {
        CubeLut lut = IdentityCube();
        ColorSpaceDef output = ColorSpaces.ByName(outputName, ColorSpaces.Srgb);
        float[] sceneLinear =
        {
            0.18f, 0.09f, 0.04f,
            0.42f, 0.24f, 0.12f,
        };

        float[] native = (float[])sceneLinear.Clone();
        ColorPipeline.ToOutputSpaceVia(native, lut, ColorSpaces.Rec709);

        float[] legacyTarget = (float[])sceneLinear.Clone();
        ColorPipeline.ToOutputSpaceVia(legacyTarget, lut, output);

        using var engine = new RecordingEngine();
        float[] managedTarget = (float[])sceneLinear.Clone();
        ColorPipeline.ToOutputSpaceVia(
            managedTarget,
            lut,
            output,
            ColorPipelineVersion.ManagedV2,
            engine);

        ColorTransformRequest request = Assert.Single(engine.Requests);
        ColorProfileRef expectedSource = BuiltInColorProfiles.Rec709(ProfileRole.Input);
        ColorProfileRef expectedDestination = BuiltInColorProfiles.For(output, ProfileRole.Output);
        Assert.Equal(TransformPurpose.PrintLutOutput, request.Purpose);
        Assert.Equal(RenderingIntent.RelativeColorimetric, request.Intent);
        Assert.False(request.BlackPointCompensation);
        Assert.Equal(1.0, request.AdaptationState);
        Assert.Equal(PixelFormatDescriptor.RgbFloat32, request.SourceFormat);
        Assert.Equal(PixelFormatDescriptor.RgbFloat32, request.DestinationFormat);
        Assert.Equal(ProfileRole.Input, request.Source.Role);
        Assert.Equal(ProfileRole.Output, request.Destination.Role);
        Assert.Equal(expectedSource.Identity, request.Source.Identity);
        Assert.Equal(expectedSource.IccBytes.ToArray(), request.Source.IccBytes.ToArray());
        Assert.Equal(expectedDestination.Identity, request.Destination.Identity);
        Assert.Equal(expectedDestination.IccBytes.ToArray(), request.Destination.IccBytes.ToArray());

        for (int pixel = 0; pixel < native.Length / 3; pixel++)
        {
            double[] nativeXyz = DecodePixelToXyz(native, pixel, ColorSpaces.Rec709);
            double[] managedXyz = DecodePixelToXyz(managedTarget, pixel, output);
            for (int channel = 0; channel < 3; channel++)
            {
                double difference = Math.Abs(nativeXyz[channel] - managedXyz[channel]);
                Assert.True(
                    difference <= PcsTolerance,
                    $"{outputName} pixel {pixel} XYZ[{channel}] moved by {difference:R}; " +
                    $"native={nativeXyz[channel]:R}, managed={managedXyz[channel]:R}");
            }
        }

        Assert.True(
            legacyTarget.Where((value, index) =>
                    BitConverter.SingleToInt32Bits(value) !=
                    BitConverter.SingleToInt32Bits(managedTarget[index]))
                .Any(),
            $"{outputName} ManagedV2 unexpectedly retained the frozen hybrid encoding");
    }

    [Fact]
    public void Managed_v2_Rec709_control_is_bit_exact_and_needs_no_transform()
    {
        CubeLut lut = IdentityCube();
        float[] sceneLinear = { 0.18f, 0.09f, 0.04f, 0.42f, 0.24f, 0.12f };
        float[] expected = (float[])sceneLinear.Clone();
        ColorPipeline.ToOutputSpaceVia(expected, lut, ColorSpaces.Rec709);
        using var neverCalled = StubEngine.NeverCalled();

        float[] actual = (float[])sceneLinear.Clone();
        ColorPipeline.ToOutputSpaceVia(
            actual,
            lut,
            ColorSpaces.Rec709,
            ColorPipelineVersion.ManagedV2,
            neverCalled);

        AssertFloatBitsEqual(expected, actual, "Rec709 native control");
        Assert.Equal(0, neverCalled.LeaseCalls);
    }

    [Theory]
    [InlineData("sRGB")]
    [InlineData("DisplayP3")]
    [InlineData("AdobeRGB")]
    public void Managed_v2_RenderedFrame_carries_the_exact_requested_profile_without_mismatch(
        string outputName)
    {
        ColorSpaceDef output = ColorSpaces.ByName(outputName, ColorSpaces.Srgb);
        ColorProfileRef exactOutput = BuiltInColorProfiles.For(output, ProfileRole.Output);
        var parameters = new FrameParams
        {
            OutputSpace = outputName,
            PrintLut = ":kodak-2383",
            DisplayReferredStage2 = true,
        };
        using var engine = new RecordingEngine();

        RenderedFrame rendered = Pipeline.Render(
            MakeWorkingFrame(MakeSyntheticNegative()),
            parameters,
            ColorPipelineVersion.ManagedV2,
            engine);

        Assert.Equal(ColorPipelineVersion.ManagedV2, rendered.Recipe.PipelineVersion);
        Assert.False(rendered.Recipe.PixelProfileMismatch);
        Assert.Equal(exactOutput.Identity, rendered.OutputProfile.Identity);
        Assert.Equal(exactOutput.IccBytes.ToArray(), rendered.OutputProfile.IccBytes.ToArray());
        Assert.Equal(
            exactOutput.IccBytes.ToArray(),
            rendered.Recipe.RequestedOutputProfile.IccBytes.ToArray());
        Assert.Same(rendered.OutputProfile, rendered.Recipe.RequestedOutputProfile);
        Assert.IsType<ProfileSource.BuiltIn>(rendered.OutputProfile.Source);
        Assert.Equal(TransferState.ProfileEncoded, rendered.Encoding.Transfer);
        Assert.Equal(ColorReference.DisplayReferred, rendered.Encoding.Reference);
        Assert.Equal(NumericRange.Normalized, rendered.Encoding.Range);
        Assert.Contains("exact output ICC", rendered.Recipe.GamutPolicy, StringComparison.Ordinal);
        Assert.Equal(parameters.PrintLut, rendered.Recipe.PrintLutIdentity);

        ColorTransformRequest request = Assert.Single(engine.Requests);
        Assert.Equal(
            BuiltInColorProfiles.Rec709(ProfileRole.Input).IccBytes.ToArray(),
            request.Source.IccBytes.ToArray());
        Assert.Equal(exactOutput.IccBytes.ToArray(), request.Destination.IccBytes.ToArray());
        Assert.Equal(TransformPurpose.PrintLutOutput, request.Purpose);
    }

    [Fact]
    public void Managed_v2_non_LUT_orchestration_preserves_the_existing_display_referred_math()
    {
        var parameters = new FrameParams
        {
            OutputSpace = "DisplayP3",
            PrintLut = "",
            DisplayReferredStage2 = true,
            WbGains = new[] { 1.04, 0.98, 1.02 },
            ExposureEv = 0.2,
            BlackPoint = 0.01,
            WhitePoint = 0.96,
            Contrast = 0.08,
            Highlights = 0.1,
            Shadows = -0.08,
            Saturation = 0.12,
        };
        ImageBuffer expected = Pipeline.ProcessFrame(MakeSyntheticNegative(), parameters);
        using var neverCalled = StubEngine.NeverCalled();

        RenderedFrame managed = Pipeline.Render(
            MakeWorkingFrame(MakeSyntheticNegative()),
            parameters,
            ColorPipelineVersion.ManagedV2,
            neverCalled);

        AssertFloatBitsEqual(expected.Data, managed.Pixels.Data, "ManagedV2 non-LUT Stage2");
        Assert.Equal(
            BuiltInColorProfiles.DisplayP3(ProfileRole.Output).Identity,
            managed.OutputProfile.Identity);
        Assert.False(managed.Recipe.PixelProfileMismatch);
        Assert.Equal(0, neverCalled.LeaseCalls);
    }

    [Fact]
    public void Explicit_LegacyV1_render_delegates_to_the_frozen_overload_without_using_the_CMM()
    {
        var parameters = new FrameParams
        {
            OutputSpace = "AdobeRGB",
            PrintLut = ":kodak-2383",
            DisplayReferredStage2 = false,
        };
        RenderedFrame frozen = Pipeline.Render(
            MakeWorkingFrame(MakeSyntheticNegative()),
            parameters);
        using var neverCalled = StubEngine.NeverCalled();

        RenderedFrame versioned = Pipeline.Render(
            MakeWorkingFrame(MakeSyntheticNegative()),
            parameters,
            ColorPipelineVersion.LegacyV1,
            neverCalled);

        AssertFloatBitsEqual(frozen.Pixels.Data, versioned.Pixels.Data, "LegacyV1 render");
        Assert.Equal(frozen.Recipe, versioned.Recipe);
        Assert.Equal(frozen.OutputProfile.Identity, versioned.OutputProfile.Identity);
        Assert.Equal(
            frozen.OutputProfile.IccBytes.ToArray(),
            versioned.OutputProfile.IccBytes.ToArray());
        Assert.Equal(ColorPipelineVersion.LegacyV1, versioned.Recipe.PipelineVersion);
        Assert.True(versioned.Recipe.PixelProfileMismatch);
        Assert.Equal(0, neverCalled.LeaseCalls);
    }

    [Fact]
    public void Managed_v2_applies_CMM_then_non_neutral_Stage2_exactly_once()
    {
        var parameters = new FrameParams
        {
            OutputSpace = "sRGB",
            PrintLut = ":kodak-2383",
            DisplayReferredStage2 = true,
            Contrast = 1.0,
        };

        FrameParams scene = parameters.Clone();
        scene.OutputIntent = OutputIntent.None;
        ImageBuffer expectedNative = Pipeline.ProcessFrame(MakeSyntheticNegative(), scene);
        CubeLut builtIn = Assert.IsType<CubeLut>(PrintLuts.Resolve(parameters.PrintLut));
        ColorPipeline.ToOutputSpaceVia(expectedNative.Data, builtIn, ColorSpaces.Rec709);

        using var engine = new ConstantOutputEngine(0.4f, 0.45f, 0.55f);
        RenderedFrame rendered = Pipeline.Render(
            MakeWorkingFrame(MakeSyntheticNegative()),
            parameters,
            ColorPipelineVersion.ManagedV2,
            engine);

        Assert.Equal(1, engine.LeaseCalls);
        Assert.Equal(1, engine.ApplyCalls);
        Assert.NotNull(engine.LastSource);
        AssertFloatBitsEqual(expectedNative.Data, engine.LastSource!, "CMM native Rec709 input");

        float[] cmmOutput = { 0.4f, 0.45f, 0.55f };
        for (int index = 0; index < rendered.Pixels.Data.Length; index++)
        {
            float expectedOnce = (cmmOutput[index % 3] - 0.5f) * 2.0f + 0.5f;
            float expectedTwice = (expectedOnce - 0.5f) * 2.0f + 0.5f;
            float value = rendered.Pixels.Data[index];
            Assert.Equal(
                BitConverter.SingleToInt32Bits(expectedOnce),
                BitConverter.SingleToInt32Bits(value));
            Assert.NotEqual(
                BitConverter.SingleToInt32Bits(expectedTwice),
                BitConverter.SingleToInt32Bits(value));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Managed_v2_curve_samples_target_encoded_coordinates_without_private_gamma(
        bool displayReferredFlag)
    {
        FrameParams parameters = ConstantQuarterCurve();
        parameters.OutputSpace = "sRGB";
        parameters.PrintLut = ":kodak-2383";
        parameters.DisplayReferredStage2 = displayReferredFlag;
        using var engine = new ConstantOutputEngine(0.5f);

        RenderedFrame rendered = Pipeline.Render(
            MakeWorkingFrame(MakeSyntheticNegative()),
            parameters,
            ColorPipelineVersion.ManagedV2,
            engine);

        Assert.Equal(1, engine.ApplyCalls);
        Assert.All(rendered.Pixels.Data, value => Assert.InRange(Math.Abs(value - 0.25f), 0.0f, 2e-6f));
    }

    [Fact]
    public void Legacy_v1_curve_keeps_the_private_gamma_round_trip()
    {
        FrameParams parameters = ConstantQuarterCurve();
        parameters.DisplayReferredStage2 = false;
        float[] pixels = { 0.5f, 0.5f, 0.5f };

        Stage2.ApplyChain(pixels, parameters, ColorSpaces.Srgb, encodeExit: false);

        float expected = (float)Math.Pow(0.25f, 2.2f);
        Assert.All(pixels, value => Assert.InRange(Math.Abs(value - expected), 0.0f, 2e-6f));
        Assert.All(pixels, value => Assert.True(Math.Abs(value - 0.25f) > 0.1f));
    }

    [Theory]
    [InlineData("sRGB")]
    [InlineData("DisplayP3")]
    [InlineData("AdobeRGB")]
    [InlineData("Rec709")]
    public void Legacy_false_uses_ACEScg_primaries_with_selected_TRC_and_ignores_print_LUT(
        string outputName)
    {
        ColorSpaceDef selected = ColorSpaces.ByName(outputName, ColorSpaces.Srgb);
        var withoutLutParameters = new FrameParams
        {
            OutputSpace = outputName,
            PrintLut = "",
            DisplayReferredStage2 = false,
            Contrast = 0.1,
            Saturation = 0.08,
        };
        FrameParams withLutParameters = withoutLutParameters.Clone();
        withLutParameters.PrintLut = ":kodak-2383";

        RenderedFrame withoutLut = Pipeline.Render(
            MakeWorkingFrame(MakeSyntheticNegative()),
            withoutLutParameters);
        RenderedFrame withLut = Pipeline.Render(
            MakeWorkingFrame(MakeSyntheticNegative()),
            withLutParameters);

        AssertFloatBitsEqual(withoutLut.Pixels.Data, withLut.Pixels.Data, $"legacy false {outputName}");
        Assert.Equal(string.Empty, withLut.Recipe.PrintLutIdentity);
        Assert.Contains("print LUT ignored", withLut.Recipe.GamutPolicy, StringComparison.Ordinal);
        Assert.True(withLut.Recipe.PixelProfileMismatch);
        Assert.Equal(ProfileRole.Output, withLut.OutputProfile.Role);
        Assert.Equal(
            BuiltInColorProfiles.For(selected, ProfileRole.Output).Identity,
            withLut.Recipe.RequestedOutputProfile.Identity);

        ColorProfileRef compatibility = BuiltInColorProfiles.LegacyLinearStage2Output(selected);
        Assert.Equal(compatibility.Identity, withLut.OutputProfile.Identity);
        Assert.Equal(compatibility.IccBytes.ToArray(), withLut.OutputProfile.IccBytes.ToArray());
        Assert.Equal(withoutLut.OutputProfile.Identity, withLut.OutputProfile.Identity);

        double[,]? toWorking = IccRead.ReadMatrix(withLut.OutputProfile.IccBytes.ToArray());
        Assert.NotNull(toWorking);
        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                double expected = row == column ? 1.0 : 0.0;
                Assert.InRange(Math.Abs(toWorking![row, column] - expected), 0.0, 0.003);
            }
        }

        float[][]? profileTrcs = IccRead.BuildTrcLuts(
            withLut.OutputProfile.IccBytes.ToArray(),
            out bool allLinear);
        Assert.NotNull(profileTrcs);
        Assert.False(allLinear);
        const int codeIndex = 32768;
        float encoded = codeIndex / 65535.0f;
        float[] expectedLinear = { encoded, encoded, encoded };
        OutputRender.Decode(expectedLinear, selected);
        for (int channel = 0; channel < 3; channel++)
        {
            Assert.InRange(
                Math.Abs(profileTrcs![channel][codeIndex] - expectedLinear[channel]),
                0.0f,
                0.001f);
        }
    }

    [Fact]
    public void Managed_v2_None_is_scene_linear_ignores_LUT_and_never_uses_the_CMM()
    {
        var parameters = new FrameParams
        {
            OutputIntent = OutputIntent.None,
            OutputSpace = "DisplayP3",
            PrintLut = ":kodak-2383",
            WbGains = new[] { 1.1, 0.9, 1.05 },
            Contrast = 0.2,
        };
        ImageBuffer sourcePixels = MakeSyntheticNegative();
        float[] sourceSnapshot = (float[])sourcePixels.Data.Clone();
        ImageBuffer expected = Pipeline.ProcessFrame(MakeSyntheticNegative(), parameters);
        using var neverCalled = StubEngine.NeverCalled();

        RenderedFrame rendered = Pipeline.Render(
            MakeWorkingFrame(sourcePixels),
            parameters,
            ColorPipelineVersion.ManagedV2,
            neverCalled);

        AssertFloatBitsEqual(expected.Data, rendered.Pixels.Data, "ManagedV2 None");
        AssertFloatBitsEqual(sourceSnapshot, sourcePixels.Data, "ManagedV2 None source");
        Assert.Equal(0, neverCalled.LeaseCalls);
        Assert.Equal(string.Empty, rendered.Recipe.PrintLutIdentity);
        Assert.False(rendered.Recipe.PixelProfileMismatch);
        Assert.Equal(ProfileRole.Output, rendered.OutputProfile.Role);
        Assert.Equal(
            BuiltInColorProfiles.LinearAcesCg(ProfileRole.Output).Identity,
            rendered.OutputProfile.Identity);
        Assert.Equal(ColorReference.SceneReferred, rendered.Encoding.Reference);
        Assert.Equal(TransferState.LinearInProfilePrimaries, rendered.Encoding.Transfer);
        Assert.Equal(NumericRange.Extended, rendered.Encoding.Range);
    }

    [Fact]
    public void Managed_v2_top_level_Rec709_LUT_is_bit_exact_to_frozen_non_curve_math()
    {
        var parameters = new FrameParams
        {
            OutputSpace = "Rec709",
            PrintLut = ":kodak-2383",
            DisplayReferredStage2 = true,
            ExposureEv = 0.15,
            Contrast = 0.12,
            Saturation = 0.07,
        };
        ImageBuffer expected = Pipeline.ProcessFrame(MakeSyntheticNegative(), parameters);
        using var neverCalled = StubEngine.NeverCalled();

        RenderedFrame managed = Pipeline.Render(
            MakeWorkingFrame(MakeSyntheticNegative()),
            parameters,
            ColorPipelineVersion.ManagedV2,
            neverCalled);

        AssertFloatBitsEqual(expected.Data, managed.Pixels.Data, "ManagedV2 Rec709 top-level");
        Assert.Equal(0, neverCalled.LeaseCalls);
        Assert.Equal(ProfileRole.Output, managed.OutputProfile.Role);
        Assert.Equal(
            BuiltInColorProfiles.Rec709(ProfileRole.Output).Identity,
            managed.OutputProfile.Identity);
    }

    [Fact]
    public void Managed_v2_external_cube_without_output_characterization_fails_closed()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"openrevelare-uncharacterized-{Guid.NewGuid():N}.cube");
        File.WriteAllText(path, IdentityCubeText);
        try
        {
            CubeLut external = PrintLuts.Validate(path);
            Assert.Equal(LutOutputEncoding.Unknown, external.OutputEncoding);

            var parameters = new FrameParams
            {
                OutputSpace = "sRGB",
                PrintLut = path,
                DisplayReferredStage2 = true,
            };
            using var neverCalled = StubEngine.NeverCalled();
            RenderedFrame legacy = Pipeline.Render(
                MakeWorkingFrame(MakeSyntheticNegative()),
                parameters,
                ColorPipelineVersion.LegacyV1,
                neverCalled);
            Assert.Equal(ColorPipelineVersion.LegacyV1, legacy.Recipe.PipelineVersion);

            ImageBuffer sourcePixels = MakeSyntheticNegative();
            float[] sourceSnapshot = (float[])sourcePixels.Data.Clone();
            NotSupportedException error = Assert.Throws<NotSupportedException>(() => Pipeline.Render(
                MakeWorkingFrame(sourcePixels),
                parameters,
                ColorPipelineVersion.ManagedV2,
                neverCalled));

            // The message is user-facing and translated, so it is matched on the fact it states
            // rather than on internal vocabulary the user would have had no way to act on.
            Assert.Contains("没有声明输出色彩空间", error.Message, StringComparison.Ordinal);
            AssertFloatBitsEqual(sourceSnapshot, sourcePixels.Data, "uncharacterized LUT source");
            Assert.Equal(0, neverCalled.LeaseCalls);
        }
        finally
        {
            PrintLuts.Forget(path);
            File.Delete(path);
        }
    }

    [Fact]
    public void Managed_v2_configured_cube_that_cannot_be_loaded_fails_closed()
    {
        string missingPath = Path.Combine(
            Path.GetTempPath(),
            $"openrevelare-missing-{Guid.NewGuid():N}.cube");
        var parameters = new FrameParams
        {
            OutputSpace = "sRGB",
            PrintLut = missingPath,
            DisplayReferredStage2 = true,
        };
        ImageBuffer sourcePixels = MakeSyntheticNegative();
        float[] sourceSnapshot = (float[])sourcePixels.Data.Clone();
        using var neverCalled = StubEngine.NeverCalled();

        try
        {
            RenderedFrame legacy = Pipeline.Render(
                MakeWorkingFrame(MakeSyntheticNegative()),
                parameters,
                ColorPipelineVersion.LegacyV1,
                neverCalled);
            Assert.Equal(ColorPipelineVersion.LegacyV1, legacy.Recipe.PipelineVersion);

            NotSupportedException error = Assert.Throws<NotSupportedException>(() => Pipeline.Render(
                MakeWorkingFrame(sourcePixels),
                parameters,
                ColorPipelineVersion.ManagedV2,
                neverCalled));

            Assert.Contains("无法载入", error.Message, StringComparison.Ordinal);
            AssertFloatBitsEqual(sourceSnapshot, sourcePixels.Data, "missing LUT source");
            Assert.Equal(0, neverCalled.LeaseCalls);
        }
        finally
        {
            PrintLuts.Forget(missingPath);
        }
    }

    [Fact]
    public void Managed_v2_CMM_failure_does_not_mutate_the_working_source()
    {
        var parameters = new FrameParams
        {
            OutputSpace = "sRGB",
            PrintLut = ":kodak-2383",
            DisplayReferredStage2 = true,
        };
        ImageBuffer sourcePixels = MakeSyntheticNegative();
        float[] sourceSnapshot = (float[])sourcePixels.Data.Clone();
        long quantisationBits = BitConverter.DoubleToInt64Bits(sourcePixels.SourceQuantisationStep);
        using var engine = new PartialWriteThenThrowEngine();

        ColorManagementException error = Assert.Throws<ColorManagementException>(() => Pipeline.Render(
            MakeWorkingFrame(sourcePixels),
            parameters,
            ColorPipelineVersion.ManagedV2,
            engine));

        Assert.Contains("native pixels were left unchanged", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, engine.ApplyCalls);
        AssertFloatBitsEqual(sourceSnapshot, sourcePixels.Data, "CMM failure working source");
        Assert.Equal(
            quantisationBits,
            BitConverter.DoubleToInt64Bits(sourcePixels.SourceQuantisationStep));
    }

    private const string IdentityCubeText = """
        TITLE "ManagedV2 identity"
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

    private static FrameParams ConstantQuarterCurve() => new()
    {
        CurveHasEndpoints = true,
        CurvePreserveHue = false,
        CurvePointsM = new List<(double, double)>
        {
            (0.0, 0.25),
            (1.0, 0.25),
        },
    };

    private static CubeLut IdentityCube()
    {
        return CubeLut.Parse(
            new StringReader(IdentityCubeText),
            "ManagedV2 identity",
            LutInputEncoding.Cineon,
            LutOutputEncoding.Rec709);
    }

    private static double[] DecodePixelToXyz(
        float[] encoded,
        int pixel,
        ColorSpaceDef declaredSpace)
    {
        int offset = pixel * 3;
        float[] linear =
        {
            encoded[offset],
            encoded[offset + 1],
            encoded[offset + 2],
        };
        OutputRender.Decode(linear, declaredSpace);
        double[,] matrix = declaredSpace.ToXyz();
        return
        [
            matrix[0, 0] * linear[0] + matrix[0, 1] * linear[1] + matrix[0, 2] * linear[2],
            matrix[1, 0] * linear[0] + matrix[1, 1] * linear[1] + matrix[1, 2] * linear[2],
            matrix[2, 0] * linear[0] + matrix[2, 1] * linear[1] + matrix[2, 2] * linear[2],
        ];
    }

    private static WorkingFrame MakeWorkingFrame(ImageBuffer pixels)
    {
        var original = new UncharacterizedPixelEncoding(
            CaptureKind.Synthetic,
            "test:managed-print-lut",
            CompatibilityPolicy.LegacyTreatNumbersAsWorking,
            TransferState.Unknown,
            NumericRange.Extended);
        var source = new SourceDescriptor(
            "test:managed-print-lut",
            "Managed print-LUT synthetic negative",
            original,
            "test-generated RGB float32");
        return new WorkingFrame(
            pixels,
            WorkingSpaceId.LinearAcesCgV1,
            WorkingAdmission.LegacyUncharacterizedPassthrough,
            source);
    }

    private static ImageBuffer MakeSyntheticNegative() => new(
        4,
        2,
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

    private static void AssertFloatBitsEqual(float[] expected, float[] actual, string label)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            int expectedBits = BitConverter.SingleToInt32Bits(expected[i]);
            int actualBits = BitConverter.SingleToInt32Bits(actual[i]);
            Assert.True(
                expectedBits == actualBits,
                $"{label} sample {i}: expected={expected[i]:R} (0x{expectedBits:X8}), " +
                $"actual={actual[i]:R} (0x{actualBits:X8})");
        }
    }

    private sealed class RecordingEngine : IColorManagementEngine
    {
        private readonly LittleCmsEngine _inner = new();

        public List<ColorTransformRequest> Requests { get; } = new();
        public CmmBuildIdentity Build => _inner.Build;

        public ProfileValidationResult Validate(ColorProfileRef profile) => _inner.Validate(profile);

        public IColorTransformLease Lease(ColorTransformRequest request)
        {
            Requests.Add(request);
            return _inner.Lease(request);
        }

        public CmmDiagnosticsSnapshot GetDiagnostics() => _inner.GetDiagnostics();
        public void Dispose() => _inner.Dispose();
    }

    private sealed class ConstantOutputEngine : IColorManagementEngine
    {
        private readonly float _red;
        private readonly float _green;
        private readonly float _blue;

        public int LeaseCalls { get; private set; }
        public int ApplyCalls { get; private set; }
        public float[]? LastSource { get; private set; }
        public List<ColorTransformRequest> Requests { get; } = new();

        public ConstantOutputEngine(float value) : this(value, value, value) { }

        public ConstantOutputEngine(float red, float green, float blue)
        {
            _red = red;
            _green = green;
            _blue = blue;
        }

        public CmmBuildIdentity Build { get; } = TestBuild("constant-output");

        public ProfileValidationResult Validate(ColorProfileRef profile) =>
            throw new InvalidOperationException("validation is not part of this orchestration test");

        public IColorTransformLease Lease(ColorTransformRequest request)
        {
            LeaseCalls++;
            Requests.Add(request);
            return new LeaseImpl(this, request);
        }

        public CmmDiagnosticsSnapshot GetDiagnostics() =>
            throw new InvalidOperationException("diagnostics are not part of this orchestration test");

        public void Dispose() { }

        private sealed class LeaseImpl : IColorTransformLease
        {
            private readonly ConstantOutputEngine _owner;

            public LeaseImpl(ConstantOutputEngine owner, ColorTransformRequest request)
            {
                _owner = owner;
                Key = ColorTransformKey.From(request, owner.Build, CmmTransformFlags.None);
            }

            public ColorTransformKey Key { get; }

            public void Apply(ReadOnlySpan<float> source, Span<float> destination, int pixelCount)
            {
                int componentCount = checked(pixelCount * 3);
                _owner.ApplyCalls++;
                _owner.LastSource = source[..componentCount].ToArray();
                for (int index = 0; index < componentCount; index += 3)
                {
                    destination[index] = _owner._red;
                    destination[index + 1] = _owner._green;
                    destination[index + 2] = _owner._blue;
                }
            }

            public void Dispose() { }
        }
    }

    private sealed class PartialWriteThenThrowEngine : IColorManagementEngine
    {
        public int ApplyCalls { get; private set; }
        public CmmBuildIdentity Build { get; } = TestBuild("partial-write-throw");

        public ProfileValidationResult Validate(ColorProfileRef profile) =>
            throw new InvalidOperationException("validation is not part of this failure test");

        public IColorTransformLease Lease(ColorTransformRequest request) => new LeaseImpl(this, request);

        public CmmDiagnosticsSnapshot GetDiagnostics() =>
            throw new InvalidOperationException("diagnostics are not part of this failure test");

        public void Dispose() { }

        private sealed class LeaseImpl : IColorTransformLease
        {
            private readonly PartialWriteThenThrowEngine _owner;

            public LeaseImpl(PartialWriteThenThrowEngine owner, ColorTransformRequest request)
            {
                _owner = owner;
                Key = ColorTransformKey.From(request, owner.Build, CmmTransformFlags.None);
            }

            public ColorTransformKey Key { get; }

            public void Apply(ReadOnlySpan<float> source, Span<float> destination, int pixelCount)
            {
                _owner.ApplyCalls++;
                if (!destination.IsEmpty) destination[0] = 0.99f;
                throw new InvalidOperationException("synthetic partial CMM write");
            }

            public void Dispose() { }
        }
    }

    private static CmmBuildIdentity TestBuild(string product) => new(
        product,
        "test",
        0,
        "test",
        "test",
        CmmTransformFlags.None,
        new string('0', 64),
        new string('0', 64),
        "{}",
        "test");

    private sealed class StubEngine : IColorManagementEngine
    {
        public int LeaseCalls { get; private set; }

        public CmmBuildIdentity Build { get; } = new(
            "test CMM",
            "test",
            0,
            "test",
            "test",
            CmmTransformFlags.None,
            new string('0', 64),
            new string('0', 64),
            "{}",
            "test");

        public static StubEngine NeverCalled() => new();

        public ProfileValidationResult Validate(ColorProfileRef profile) =>
            throw new InvalidOperationException("the CMM must not be used");

        public IColorTransformLease Lease(ColorTransformRequest request)
        {
            LeaseCalls++;
            throw new InvalidOperationException("the CMM must not be used");
        }

        public CmmDiagnosticsSnapshot GetDiagnostics() =>
            throw new InvalidOperationException("the CMM must not be used");

        public void Dispose() { }
    }
}
