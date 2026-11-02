using System.Buffers.Binary;
using MetadataExtractor.Formats.Exif;
using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class LensMetadataTests
{
    [Fact]
    public void EstimateCorrections_WithSonySubIfd_CalculatesLeastSquaresK1AndCornerVignette()
    {
        var subIfd = new ExifSubIfdDirectory();
        subIfd.Set(0xa434, "FE 70-200mm F4 Macro G OSS II");
        subIfd.Set(0x920a, 126.0);
        subIfd.Set(0x829d, 8.0);

        // Sony MakerNotes tag 0x7037 (17 values: n=16, then 16 knots)
        short[] distKnots = [16, 0, 2, 10, 22, 40, 62, 91, 125, 164, 211, 264, 324, 394, 472, 560, 659];
        subIfd.Set(0x7037, distKnots);

        // Sony MakerNotes tag 0x7032 (17 values: n=16, then 16 knots)
        short[] vignKnots = [16, 0, 16, 64, 144, 272, 432, 624, 848, 1088, 1376, 1696, 2032, 2400, 2800, 3216, 3664];
        subIfd.Set(0x7032, vignKnots);

        var estimate = LensMetadata.EstimateCorrections([subIfd]);
        Assert.NotNull(estimate);
        Assert.Equal("FE 70-200mm F4 Macro G OSS II", estimate.LensModel);
        Assert.Equal(126.0, estimate.FocalLengthMm);
        Assert.Equal(8.0, estimate.FNumber);
        Assert.InRange(estimate.DistortionK1, 0.011, 0.015);
        Assert.InRange(estimate.VignetteAmount, 0.16, 0.18);
        Assert.Equal(2.5, estimate.VignetteFalloff);
        Assert.Contains("Sony RAW", estimate.SourceDescription);
    }

    [Fact]
    public void EstimateCorrections_DngOpcodeList3_ParsesWarpRectilinearAndFixVignette()
    {
        // Construct binary DNG OpcodeList3
        using var ms = new MemoryStream();
        // Count = 2 opcodes
        byte[] b4 = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b4, 2);
        ms.Write(b4);

        // Opcode 1: WarpRectilinear (ID = 4)
        BinaryPrimitives.WriteUInt32BigEndian(b4, 4); ms.Write(b4); // OpcodeID
        BinaryPrimitives.WriteUInt32BigEndian(b4, 0x01030000); ms.Write(b4); // DNG version
        BinaryPrimitives.WriteUInt32BigEndian(b4, 0); ms.Write(b4); // Flags
        BinaryPrimitives.WriteUInt32BigEndian(b4, 32); ms.Write(b4); // Param bytes: 4 doubles

        byte[] b8 = new byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(b8, 1.0); ms.Write(b8); // k0
        BinaryPrimitives.WriteDoubleBigEndian(b8, 0.0455); ms.Write(b8); // k1 (DNG space: 0.0455 / 3.25 = 0.014)
        BinaryPrimitives.WriteDoubleBigEndian(b8, 0.0); ms.Write(b8); // k2
        BinaryPrimitives.WriteDoubleBigEndian(b8, 0.0); ms.Write(b8); // k3

        // Opcode 2: FixVignetteRadial (ID = 3)
        BinaryPrimitives.WriteUInt32BigEndian(b4, 3); ms.Write(b4); // OpcodeID
        BinaryPrimitives.WriteUInt32BigEndian(b4, 0x01030000); ms.Write(b4); // DNG version
        BinaryPrimitives.WriteUInt32BigEndian(b4, 0); ms.Write(b4); // Flags
        BinaryPrimitives.WriteUInt32BigEndian(b4, 40); ms.Write(b4); // Param bytes: 5 doubles

        BinaryPrimitives.WriteDoubleBigEndian(b8, 0.0); ms.Write(b8); // k0
        BinaryPrimitives.WriteDoubleBigEndian(b8, 0.05); ms.Write(b8); // k1
        BinaryPrimitives.WriteDoubleBigEndian(b8, 0.05); ms.Write(b8); // k2
        BinaryPrimitives.WriteDoubleBigEndian(b8, 0.05); ms.Write(b8); // k3
        BinaryPrimitives.WriteDoubleBigEndian(b8, 0.02); ms.Write(b8); // k4 (total gain = 0.17)

        byte[] opcodeData = ms.ToArray();
        bool ok = LensMetadata.TryParseDngOpcodeList3(opcodeData, out var k1, out var vignAmount, out var vignFalloff);
        Assert.True(ok);
        Assert.InRange(k1, 0.0135, 0.0145);
        Assert.InRange(vignAmount, 0.169, 0.171);
        Assert.Equal(2.5, vignFalloff);
    }

    [Theory]
    [InlineData("FE 90mm F2.8 Macro G OSS", 90.0, 8.0, 0.0, 0.12)]
    [InlineData("Sigma 105mm F2.8 DG DN Macro | Art", 105.0, 8.0, 0.002, 0.15)]
    [InlineData("Canon RF 100mm F2.8 L MACRO IS USM", 100.0, 8.0, -0.003, 0.14)]
    [InlineData("NIKKOR Z MC 105mm f/2.8 VR S", 105.0, 8.0, 0.001, 0.12)]
    public void EstimateCorrections_KnownLenses_FallbacksAccurately(string lens, double focal, double fNumber, double expectedK1, double expectedVignette)
    {
        var subIfd = new ExifSubIfdDirectory();
        subIfd.Set(0xa434, lens);
        subIfd.Set(0x920a, focal);
        subIfd.Set(0x829d, fNumber);

        var estimate = LensMetadata.EstimateCorrections([subIfd]);
        Assert.NotNull(estimate);
        Assert.Equal(expectedK1, estimate.DistortionK1);
        Assert.Equal(expectedVignette, estimate.VignetteAmount);
        Assert.Contains("镜头预设", estimate.SourceDescription);
    }

    [Fact]
    public void EstimateCorrections_FromActualArwIfPresent_MatchesGroundTruth()
    {
        const string path = "scratch/test-roll/RAW/DSC01910.ARW";
        if (!File.Exists(path)) return;

        var estimate = LensMetadata.EstimateCorrections(path);
        Assert.NotNull(estimate);
        Assert.Equal("FE 70-200mm F4 Macro G OSS II", estimate.LensModel);
        Assert.Equal(126.0, estimate.FocalLengthMm);
        Assert.Equal(8.0, estimate.FNumber);
        Assert.InRange(estimate.DistortionK1, 0.011, 0.015);
        Assert.InRange(estimate.VignetteAmount, 0.16, 0.18);
        Assert.Contains("Sony RAW", estimate.SourceDescription);
    }
}
