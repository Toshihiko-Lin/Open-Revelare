using OpenRevelare.Core;
using OpenRevelare.ColorManagement;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class CameraCharacterizationTests
{
    [Fact]
    public void Viewer_characterization_is_not_persisted_as_a_film_calibration()
    {
        string path = Path.Combine(TestDataIsolation.Root, $"view-matrix-{Guid.NewGuid():N}.ncproj");
        try
        {
            var p = new FrameParams { NegativeCameraToWorking = new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } } };
            Project.Save(path, new Project.Data { Meta = new() { InputType = "raw" },
                Frames = new() { new Project.Frame { SourcePath = "camera.nef", Params = p } } });
            Assert.Null(Project.Load(path).Frames[0].Params.NegativeCameraToWorking);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Linear_native_channels_are_explicit_without_claiming_an_icc_characterization()
    {
        var frame = RawDecode.AdmitRawWorking(new ImageBuffer(1, 1), "camera.nef", "LibRaw", RawDecode.FbddMode.Off, "test");
        Assert.Equal(TransferState.LinearInCaptureChannels, frame.Source.OriginalEncoding.Transfer);
        Assert.IsType<UncharacterizedPixelEncoding>(frame.Source.OriginalEncoding);
        Assert.Throws<ArgumentException>(() => new CharacterizedPixelEncoding(BuiltInColorProfiles.Srgb(ProfileRole.Input),
            ColorReference.SceneReferred, TransferState.LinearInCaptureChannels, NumericRange.Normalized));
    }

    [Fact]
    public void Camera_identity_roundtrips_through_working_without_clipping_extended_values()
    {
        double[,] matrix = { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        double[,] working = Assert.IsType<double[,]>(CameraCharacterization.ToWorking(matrix));
        float[] original = { -0.1f, 0.2f, 1.5f, 0.4f, 0.4f, 0.4f };
        float[] actual = (float[])original.Clone();
        NegativeView.Prepare(actual, null, working);
        OutputRender.Convert(actual, ColorPipeline.Working, ColorSpaces.Srgb, GamutMapping.PreserveExtended);
        for (int i = 0; i < actual.Length; i++) Assert.InRange(Math.Abs(actual[i] - original[i]), 0, 2e-6);
    }

    [Fact]
    public void Invalid_camera_matrices_are_declined()
    {
        Assert.Null(CameraCharacterization.ToWorking(new double[3, 3]));
        Assert.Null(CameraCharacterization.ToWorking(new double[2, 3]));
        Assert.Null(CameraCharacterization.ToWorking(new double[,] { { double.NaN, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } }));
        Assert.Null(CameraCharacterization.ToWorking(new double[,] { { -1, 0, 0 }, { 0, -1, 0 }, { 0, 0, -1 } }));
    }

    [Fact]
    public void Whole_negative_and_sharp_patch_share_characterization_while_positive_pixels_stay_unchanged()
    {
        var source = new ImageBuffer(23, 17);
        for (int i = 0; i < source.Data.Length; i++) source.Data[i] = 0.05f + (i % 127) / 200f;
        float[] original = (float[])source.Data.Clone();
        double[,] camera = { { 1.1, -0.1, 0 }, { 0, 1, 0 }, { 0, -0.2, 1.2 } };
        var p = new FrameParams { NegativeCameraToWorking = CameraCharacterization.ToWorking(camera) };
        double[] wb = { 2, 1, 1.5 };
        var roi = new RegionRender.Roi(0, 0, 1, 1);
        float[] whole = (float[])original.Clone();
        NegativeView.Prepare(whole, wb, p.NegativeCameraToWorking);
        NegativeView.ToDisplay(whole, p.ResolvedOutputSpace);
        var patch = RegionRender.Render(source, p, roi, negative: true, negativeWb: wb);
        Assert.Equal(whole, patch.Image.Data);
        var positive = RegionRender.Render(source, p, roi);
        var without = p.Clone(); without.NegativeCameraToWorking = null;
        Assert.Equal(positive.Image.Data, RegionRender.Render(source, without, roi).Image.Data);
        Assert.Equal(original, source.Data);
        Assert.NotNull(p.Clone().NegativeCameraToWorking);
    }

    [Fact]
    public void Quantization_uses_black_offsets_and_channel_white_limits_without_double_subtraction()
    {
        var levels = new RawLevelMetadata(512, new double[] { 0, 16, 32, 0 }, 16383, 16000,
            new double[] { 16383, 16000, 15800, 0 }, 0, 8, 0.75, 0, 0);
        Assert.Equal(1d / (15800 - 512 - 32 - 8), levels.QuantisationStep);
        Assert.Equal(0, (levels with { White = 0, ChannelWhite = new double[4] }).QuantisationStep);
    }
}
