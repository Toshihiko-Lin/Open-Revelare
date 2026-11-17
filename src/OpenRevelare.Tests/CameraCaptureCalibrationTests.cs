using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class CameraCaptureCalibrationTests
{
    private static CameraRawMosaicMetadata Metadata => new(4, 2, 8, 6, 1, 1, 0, "0,1,3,2",
        "Test", "Bayer", "unit-1", 100, .25, 4095, "rig-A", "unpack-v1");
    private static CameraRawMosaic Frame(ushort[] data, CameraRawMosaicMetadata? metadata = null)
        => new(data, metadata ?? Metadata, "fixture.raw");

    [Fact]
    public void Matched_dark_open_recovers_known_transmission_without_double_black_or_clamping()
    {
        // Includes a physically measurable low signal only one code above dark, and an over-open value.
        ushort[] original = [600, 350, 101, 1100, 100, 100, 100, 100];
        var samples = Frame((ushort[])original.Clone());
        var dark = Frame(Enumerable.Repeat((ushort)100, 8).ToArray());
        var open = Frame(Enumerable.Repeat((ushort)1100, 8).ToArray());
        samples.Samples[4] = 1200;
        var result = CameraCaptureCalibration.Measure(samples, dark, open);
        Assert.Equal(.5f, result.Transmission[0]);
        Assert.Equal(.25f, result.Transmission[1]);
        Assert.Equal(.001f, result.Transmission[2]);
        Assert.Equal(1f, result.Transmission[3]);
        Assert.Equal(1.1f, result.Transmission[4]);
        Assert.Equal(CaptureSampleIssue.None, result.Issues[2]);
        Assert.Equal(CaptureSampleIssue.AboveOpenReference, result.Issues[4]);
        Assert.Equal(5, result.UsableSamples);
        Assert.Equal((ushort)600, samples.Samples[0]);
        Assert.All(dark.Samples, v => Assert.Equal((ushort)100, v));
        Assert.All(open.Samples, v => Assert.Equal((ushort)1100, v));
    }

    [Fact]
    public void Saturation_invalid_references_and_nonpositive_measurements_keep_explicit_flags()
    {
        var s = Frame([4095, 500, 500, 99, 100, 500, 500, 500]);
        var d = Frame([100, 100, 100, 100, 100, 4095, 100, 100]);
        var o = Frame([1100, 4095, 100, 1100, 1100, 1100, 1100, 1100]);
        var result = CameraCaptureCalibration.Measure(s, d, o);
        Assert.True(result.Issues[0].HasFlag(CaptureSampleIssue.SampleSaturated));
        Assert.True(result.Issues[1].HasFlag(CaptureSampleIssue.ReferenceSaturated));
        Assert.True(result.Issues[2].HasFlag(CaptureSampleIssue.InvalidDenominator));
        Assert.True(result.Issues[3].HasFlag(CaptureSampleIssue.NegativeTransmission));
        Assert.True(result.Issues[4].HasFlag(CaptureSampleIssue.ZeroTransmission));
        Assert.True(result.Issues[5].HasFlag(CaptureSampleIssue.ReferenceSaturated));
        Assert.All(result.Transmission.Take(6), value => Assert.True(float.IsNaN(value)));
        Assert.Equal(2, result.UsableSamples);
    }

    [Fact]
    public void Reference_exposure_device_phase_geometry_and_declared_session_must_match()
    {
        var valid = Frame(Enumerable.Repeat((ushort)500, 8).ToArray());
        var differences = new[]
        {
            Metadata with { Iso = 200 }, Metadata with { ExposureSeconds = .5 }, Metadata with { Serial = "unit-2" },
            Metadata with { SessionId = "rig-B" }, Metadata with { BayerChannels = "2,1,3,0" },
            Metadata with { Left = 2 }, Metadata with { Orientation = 6 }, Metadata with { Decoder = "other" },
            Metadata with { WhiteLevel = 16383 },
            Metadata with { Aperture = 8 }, Metadata with { FocalLength = 100 },
        };
        foreach (var metadata in differences)
            Assert.Throws<InvalidOperationException>(() => CameraCaptureCalibration.Measure(valid, Frame(valid.Samples, metadata), valid));
        Assert.Throws<ArgumentException>(() => CameraCaptureCalibration.Measure(valid, Frame(valid.Samples, Metadata with { ExposureSeconds = 0 }), valid));
        Assert.Throws<NotSupportedException>(() => CameraCaptureCalibration.Measure(valid, Frame(valid.Samples, Metadata with { BayerChannels = "0,2,1,3" }), valid));
    }

    [Fact]
    public void Native_unpack_returns_sensor_counts_including_black_without_processing_or_WB()
    {
        string path = Path.Combine(TestDataIsolation.Root, $"capture-{Guid.NewGuid():N}.dng");
        try
        {
            LibRawRuntimeTests.WriteBayerDng(path, width: 160, height: 128, blackLevel: 512);
            byte[] original = File.ReadAllBytes(path);
            var raw = CameraCaptureCalibration.ReadRaw(path, "test-rig");
            var m = raw.Metadata;
            Assert.Equal(160, m.SensorWidth); Assert.Equal(128, m.SensorHeight);
            Assert.Equal((long)m.Width * m.Height, raw.Samples.LongLength);
            for (int y = 0; y < m.Height; y++)
            for (int x = 0; x < m.Width; x++)
            {
                int sy = y + m.Top, sx = x + m.Left;
                int channel = sy % 2 == 0 ? sx % 2 == 0 ? 1 : 2 : sx % 2 == 0 ? 2 : 3;
                Assert.Equal((ushort)(channel * 10000 + 512), raw.Samples[y * m.Width + x]);
            }
            Assert.Equal(original, File.ReadAllBytes(path));
            // Fixture lacks exposure/ISO evidence: reading is allowed, calibration must refuse it.
            Assert.Throws<ArgumentException>(() => CameraCaptureCalibration.Measure(raw, raw, raw));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Non_cfa_linear_dng_is_never_silently_treated_as_raw_sensor_samples()
    {
        string path = Path.Combine(TestDataIsolation.Root, $"linear-capture-{Guid.NewGuid():N}.dng");
        try
        {
            LinearDng.Write(new ImageBuffer(96, 64), ColorSpaces.Srgb, path);
            Assert.Throws<NotSupportedException>(() => CameraCaptureCalibration.ReadRaw(path, "test-rig"));
        }
        finally { File.Delete(path); }
    }
}
