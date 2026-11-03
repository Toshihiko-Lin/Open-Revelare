using System.Buffers.Binary;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Exif.Makernotes;
using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class LensMetadataTests
{
    // Sony DistortionCorrParams / VignettingCorrParams as 16 knots after the knot count.
    private static readonly short[] DistortionKnots =
        [16, 0, 2, 10, 22, 40, 62, 91, 125, 164, 211, 264, 324, 394, 472, 560, 659];

    private static readonly short[] VignettingKnots =
        [16, 0, 16, 64, 144, 272, 432, 624, 848, 1088, 1376, 1696, 2032, 2400, 2800, 3216, 3664];

    private const string LensModel = "FE 70-200mm F4 Macro G OSS II";

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

    [Fact]
    public void EstimateCorrections_SonyMakerNote_ReadsFirmwareParametersInsteadOfFallingBackToPresets()
    {
        // MetadataExtractor puts Sony maker notes in SonyType1MakernoteDirectory, never in the
        // Exif SubIFD: looking only at SubIFDs is why the firmware parameters were never read.
        var makerNote = new SonyType1MakernoteDirectory();
        // TIFF type SHORT (3) surfaces as ushort[] (see the encoding note on the signed test).
        makerNote.Set(0x7037, DistortionKnots.Select(v => unchecked((ushort)v)).ToArray());
        makerNote.Set(0x7032, VignettingKnots.Select(v => unchecked((ushort)v)).ToArray());

        var estimate = LensMetadata.EstimateCorrections([SubIfd(), makerNote]);

        Assert.NotNull(estimate);
        Assert.Equal(LensModel, estimate.LensModel);
        Assert.Equal(126.0, estimate.FocalLengthMm);
        Assert.Equal(8.0, estimate.FNumber);
        // Least-squares fit of the firmware knots, not the 0.0089 hard-coded 70-200 preset.
        Assert.InRange(estimate.DistortionK1, 0.011, 0.015);
        Assert.InRange(estimate.VignetteAmount, 0.16, 0.18);
        Assert.Equal(2.5, estimate.VignetteFalloff);
        Assert.Contains("Sony RAW", estimate.SourceDescription);
    }

    [Fact]
    public void EstimateCorrections_SonyMakerNoteSignedShorts_AlsoReadsFirmwareParameters()
    {
        // The same Sony tags written as TIFF SSHORT (8) surface as short[]. Both encodings must
        // work, otherwise the fix only helps cameras that happen to write one of them.
        var makerNote = new SonyType1MakernoteDirectory();
        makerNote.Set(0x7037, DistortionKnots);
        makerNote.Set(0x7032, VignettingKnots);

        var estimate = LensMetadata.EstimateCorrections([SubIfd(), makerNote]);

        Assert.NotNull(estimate);
        Assert.InRange(estimate.DistortionK1, 0.011, 0.015);
        Assert.InRange(estimate.VignetteAmount, 0.16, 0.18);
        Assert.Contains("Sony RAW", estimate.SourceDescription);
    }

    [Fact]
    public void EstimateCorrections_SonyParametersInSubIfdOnly_StillReadsThem()
    {
        // Some DNG converters copy the Sony correction block into the Exif SubIFD; that stays
        // supported as a fallback.
        var subIfd = SubIfd();
        subIfd.Set(0x7037, DistortionKnots.Select(v => unchecked((ushort)v)).ToArray());
        subIfd.Set(0x7032, VignettingKnots.Select(v => unchecked((ushort)v)).ToArray());

        var estimate = LensMetadata.EstimateCorrections([subIfd]);

        Assert.NotNull(estimate);
        Assert.Equal(LensModel, estimate.LensModel);
        Assert.Equal(126.0, estimate.FocalLengthMm);
        Assert.Equal(8.0, estimate.FNumber);
        Assert.InRange(estimate.DistortionK1, 0.011, 0.015);
        Assert.InRange(estimate.VignetteAmount, 0.16, 0.18);
        Assert.Equal(2.5, estimate.VignetteFalloff);
        Assert.Contains("Sony RAW", estimate.SourceDescription);
    }

    [Fact]
    public void EstimateCorrections_BodySerialNumberOnly_DoesNotInventALens()
    {
        // 0xA431 is BodySerialNumber. Using it as a LensModel fallback looked presets up by
        // serial number and could replace a manual correction with k1 = 0.
        var subIfd = new ExifSubIfdDirectory();
        subIfd.Set(0xa431, "2024000123");
        subIfd.Set(0x920a, 126.0);
        subIfd.Set(0x829d, 8.0);

        var estimate = LensMetadata.EstimateCorrections([subIfd]);

        Assert.Null(estimate);
    }

    [Fact]
    public void EstimateCorrections_FromSyntheticArw_ReadsSonyMakerNoteThroughTheRealReader()
    {
        // End-to-end check against MetadataExtractor itself (2.9.3): a "SONY"-made TIFF whose
        // maker note IFD carries 0x7037/0x7032 as SHORTs must land in SonyType1MakernoteDirectory.
        // Value offsets inside Sony maker notes are relative to the TIFF header, as the reader
        // hands the whole file reader to the maker note IFD.
        string path = Path.Combine(Path.GetTempPath(), $"probe-{Guid.NewGuid():N}.arw");
        File.WriteAllBytes(path, SyntheticSonyTiff());
        try
        {
            var directories = ImageMetadataReader.ReadMetadata(path);
            Assert.NotNull(directories.OfType<SonyType1MakernoteDirectory>().FirstOrDefault());

            var estimate = LensMetadata.EstimateCorrections(directories);

            Assert.NotNull(estimate);
            Assert.Equal(LensModel, estimate.LensModel);
            Assert.Equal(126.0, estimate.FocalLengthMm);
            Assert.Equal(8.0, estimate.FNumber);
            Assert.InRange(estimate.DistortionK1, 0.011, 0.015);
            Assert.InRange(estimate.VignetteAmount, 0.16, 0.18);
            Assert.Contains("Sony RAW", estimate.SourceDescription);
        }
        finally
        {
            File.Delete(path);
        }
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
        // No ARW sample ships with this worktree (scratch/ is not present), so this only runs
        // where a real capture is available. The synthetic file above covers the same path.
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
    private static ExifSubIfdDirectory SubIfd(string lensModel = LensModel)
    {
        var subIfd = new ExifSubIfdDirectory();
        subIfd.Set(0xa434, lensModel);
        subIfd.Set(0x920a, 126.0);
        subIfd.Set(0x829d, 8.0);
        return subIfd;
    }

    /// <summary>
    /// Minimal little-endian TIFF with Make = "SONY", an Exif IFD carrying lens/exposure tags and
    /// a Sony type-1 maker note (IFD at the first maker note byte, as ARW files do) holding
    /// 0x7037 / 0x7032 as SHORT arrays.
    /// </summary>
    private static byte[] SyntheticSonyTiff()
    {
        byte[] make = "SONY\0"u8.ToArray();
        byte[] lens = System.Text.Encoding.ASCII.GetBytes(LensModel + "\0");

        const int ifd0 = 8;
        const int ifd0Size = 2 + 12 * 2 + 4;
        int exifIfd = ifd0 + ifd0Size;
        const int exifEntries = 4;
        int exifIfdSize = 2 + 12 * exifEntries + 4;

        int makeOffset = exifIfd + exifIfdSize;
        int lensOffset = makeOffset + make.Length;
        int focalOffset = lensOffset + lens.Length;
        int fNumberOffset = focalOffset + 8;
        int makerNote = fNumberOffset + 8;
        int makerNoteIfd = makerNote;
        int distortionOffset = makerNoteIfd + (2 + 12 * 2 + 4);
        int vignettingOffset = distortionOffset + DistortionKnots.Length * 2;
        int total = vignettingOffset + VignettingKnots.Length * 2;

        var buffer = new byte[total];
        var span = buffer.AsSpan();
        span[0] = (byte)'I';
        span[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(4), ifd0);

        int p = ifd0;
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(p), 2); p += 2;
        WriteIfdEntry(span, ref p, 0x010f, 2, (uint)make.Length, (uint)makeOffset);
        WriteIfdEntry(span, ref p, 0x8769, 4, 1, (uint)exifIfd);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(p), 0);

        p = exifIfd;
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(p), exifEntries); p += 2;
        WriteIfdEntry(span, ref p, 0x927c, 7, (uint)(total - makerNote), (uint)makerNote);
        WriteIfdEntry(span, ref p, 0xa434, 2, (uint)lens.Length, (uint)lensOffset);
        WriteIfdEntry(span, ref p, 0x920a, 5, 1, (uint)focalOffset);
        WriteIfdEntry(span, ref p, 0x829d, 5, 1, (uint)fNumberOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(p), 0);

        // Sony maker note IFD. Sony stores these value offsets relative to the TIFF header.
        p = makerNoteIfd;
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(p), 2); p += 2;
        WriteIfdEntry(span, ref p, 0x7037, 3, (uint)DistortionKnots.Length, (uint)distortionOffset);
        WriteIfdEntry(span, ref p, 0x7032, 3, (uint)VignettingKnots.Length, (uint)vignettingOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(p), 0);

        p = distortionOffset;
        foreach (short knot in DistortionKnots) { BinaryPrimitives.WriteInt16LittleEndian(span.Slice(p), knot); p += 2; }
        p = vignettingOffset;
        foreach (short knot in VignettingKnots) { BinaryPrimitives.WriteInt16LittleEndian(span.Slice(p), knot); p += 2; }

        make.CopyTo(span.Slice(makeOffset));
        lens.CopyTo(span.Slice(lensOffset));
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(focalOffset), 126);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(focalOffset + 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(fNumberOffset), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(fNumberOffset + 4), 1);
        return buffer;
    }

    private static void WriteIfdEntry(Span<byte> span, ref int p, ushort tag, ushort type, uint count, uint value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(p), tag); p += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(p), type); p += 2;
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(p), count); p += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(p), value); p += 4;
    }
}
