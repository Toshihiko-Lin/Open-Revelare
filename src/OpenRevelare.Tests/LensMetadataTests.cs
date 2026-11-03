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
    public void TryParseDngOpcodeList3_Opcode1WarpRectilinear_ReadsFirstPlaneCoefficient()
    {
        // Opcode 1 payload per the DNG SDK: uint32 plane count, then 6 doubles per plane
        // (k0..k5 of the polynomial in r^2), then the optical centre (2 doubles).
        // planes = 1 => 4 + 8 * (6 + 2) = 68 bytes. 0.0455 / 3.25 = 0.014.
        byte[] data = OpcodeList3(
            (1, WarpRectilinearPayload(planes: 1, k1: 0.0455)));

        bool ok = LensMetadata.TryParseDngOpcodeList3(data, out var k1, out var vignette, out var falloff);

        Assert.True(ok);
        Assert.Equal(0.014, k1, 6);
        Assert.Equal(0d, vignette);
        Assert.Equal(2.5, falloff);
    }

    [Fact]
    public void TryParseDngOpcodeList3_Opcode1WithThreePlanes_ReadsOnlyFirstPlane()
    {
        // planes = 3 => 4 + 8 * (3 * 6 + 2) = 164 bytes; the per-plane coefficients of the first
        // plane are what map onto the single-k1 model.
        byte[] data = OpcodeList3(
            (1, WarpRectilinearPayload(planes: 3, k1: 0.0325)));

        bool ok = LensMetadata.TryParseDngOpcodeList3(data, out var k1, out _, out _);

        Assert.True(ok);
        Assert.Equal(0.01, k1, 6);
    }

    [Fact]
    public void TryParseDngOpcodeList3_Opcode3FixVignetteRadial_ReadsCornerGain()
    {
        // Opcode 3 payload: k0..k4 then the optical centre (7 doubles = 56 bytes). k0 is the gain
        // at the optical centre (~1.0), so the corner gain is the sum (1.17) and the amount 0.17.
        byte[] data = OpcodeList3(
            (3, FixVignetteRadialPayload(k0: 1.0, k1: 0.08, k2: 0.05, k3: 0.03, k4: 0.01)));

        bool ok = LensMetadata.TryParseDngOpcodeList3(data, out var k1, out var vignette, out var falloff);

        Assert.True(ok);
        Assert.Equal(0d, k1);
        Assert.Equal(0.17, vignette, 6);
        Assert.Equal(2.5, falloff);
    }

    [Fact]
    public void TryParseDngOpcodeList3_VignetteStoredWithoutUnitOffset_StillReadsAmount()
    {
        // A few writers store the gain minus one directly (k0 = 0). Accepting that shape keeps
        // those files from turning into a -1.0 amount.
        byte[] data = OpcodeList3(
            (3, FixVignetteRadialPayload(k0: 0.0, k1: 0.08, k2: 0.05, k3: 0.03, k4: 0.01)));

        bool ok = LensMetadata.TryParseDngOpcodeList3(data, out _, out var vignette, out _);

        Assert.True(ok);
        Assert.Equal(0.17, vignette, 6);
    }

    [Fact]
    public void TryParseDngOpcodeList3_BothOpcodes_ReadsWarpAndVignette()
    {
        byte[] data = OpcodeList3(
            (1, WarpRectilinearPayload(planes: 1, k1: 0.0455)),
            (3, FixVignetteRadialPayload(k0: 1.0, k1: 0.08, k2: 0.05, k3: 0.03, k4: 0.01)));

        bool ok = LensMetadata.TryParseDngOpcodeList3(data, out var k1, out var vignette, out _);

        Assert.True(ok);
        Assert.Equal(0.014, k1, 6);
        Assert.Equal(0.17, vignette, 6);
    }

    [Fact]
    public void TryParseDngOpcodeList3_Opcode4FixBadPixelsConstant_IsNotReadAsAWarp()
    {
        // Opcode 4 is FixBadPixelsConstant. The previous revision treated it as WarpRectilinear,
        // which is why no conforming DNG was ever parsed; the payload below looks like the old
        // test data and must now be ignored.
        byte[] payload = new byte[32];
        BinaryPrimitives.WriteDoubleBigEndian(payload.AsSpan(0, 8), 1.0);
        BinaryPrimitives.WriteDoubleBigEndian(payload.AsSpan(8, 8), 0.0455);
        byte[] data = OpcodeList3((4, payload));

        bool ok = LensMetadata.TryParseDngOpcodeList3(data, out var k1, out var vignette, out _);

        Assert.False(ok);
        Assert.Equal(0d, k1);
        Assert.Equal(0d, vignette);
    }

    [Theory]
    [InlineData(0u)]  // plane count of zero is invalid
    [InlineData(2u)]  // only 1 and 3 are valid plane counts
    [InlineData(4u)]
    public void TryParseDngOpcodeList3_WarpRectilinearWithInvalidPlaneCount_IsIgnored(uint planes)
    {
        byte[] payload = new byte[4 + 8 * (6 + 2)];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), planes);
        BinaryPrimitives.WriteDoubleBigEndian(payload.AsSpan(4 + 8, 8), 0.0455);

        bool ok = LensMetadata.TryParseDngOpcodeList3(OpcodeList3((1, payload)), out var k1, out _, out _);

        Assert.False(ok);
        Assert.Equal(0d, k1);
    }

    [Fact]
    public void TryParseDngOpcodeList3_TruncatedParameterBlock_IsRejected()
    {
        // Declares the full 164-byte three-plane payload but only carries 68 bytes.
        byte[] payload = WarpRectilinearPayload(planes: 3, k1: 0.0455);
        byte[] data = OpcodeList3((1, payload));
        Array.Resize(ref data, data.Length - 40);

        bool ok = LensMetadata.TryParseDngOpcodeList3(data, out var k1, out var vignette, out _);

        Assert.False(ok);
        Assert.Equal(0d, k1);
        Assert.Equal(0d, vignette);
    }

    [Fact]
    public void TryParseDngOpcodeList3_EmptyOrShortData_IsRejected()
    {
        Assert.False(LensMetadata.TryParseDngOpcodeList3([], out _, out _, out _));
        Assert.False(LensMetadata.TryParseDngOpcodeList3([0, 0, 0], out _, out _, out _));
        Assert.False(LensMetadata.TryParseDngOpcodeList3([0, 0, 0, 0], out _, out _, out _));
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
    public void EstimateCorrections_FromRealArwLibrary_UsesFirmwareParameters()
    {
        // Real captures are not committed. Point OPENREVELARE_LENS_SAMPLES at a directory of Sony
        // ARW files to run this; with the variable unset the test returns immediately.
        string? root = Environment.GetEnvironmentVariable("OPENREVELARE_LENS_SAMPLES");
        if (string.IsNullOrWhiteSpace(root) || !System.IO.Directory.Exists(root)) return;

        string[] samples = System.IO.Directory.GetFiles(root, "*.ARW", SearchOption.AllDirectories);
        if (samples.Length == 0) return;

        var estimates = samples
            .OrderBy(p => p)
            .Select(LensMetadata.EstimateCorrections)
            .OfType<LensCorrectionEstimate>()
            .ToList();

        Assert.Equal(samples.Length, estimates.Count);
        Assert.All(estimates, estimate =>
        {
            // The firmware spline has to win over the hard-coded lens preset.
            Assert.Equal("Sony RAW 固件参数", estimate.SourceDescription);
            Assert.False(string.IsNullOrWhiteSpace(estimate.LensModel));
            Assert.InRange(estimate.FocalLengthMm, 1, 2000);
            Assert.InRange(estimate.FNumber, 1, 64);
            Assert.InRange(estimate.DistortionK1, 0.001, 0.05);
            Assert.InRange(estimate.VignetteAmount, 0.01, 0.6);
        });
    }

    /// <summary>
    /// Builds an OpcodeList3 blob: a big-endian count, then per opcode a 16-byte header
    /// (id, version, flags, parameter byte count) followed by the parameters.
    /// </summary>
    private static byte[] OpcodeList3(params (uint Id, byte[] Payload)[] opcodes)
    {
        using var stream = new MemoryStream();
        Span<byte> u32 = stackalloc byte[4];

        BinaryPrimitives.WriteUInt32BigEndian(u32, (uint)opcodes.Length);
        stream.Write(u32);

        foreach (var (id, payload) in opcodes)
        {
            BinaryPrimitives.WriteUInt32BigEndian(u32, id);
            stream.Write(u32);
            BinaryPrimitives.WriteUInt32BigEndian(u32, 0x01040000); // DNG version
            stream.Write(u32);
            BinaryPrimitives.WriteUInt32BigEndian(u32, 0); // flags
            stream.Write(u32);
            BinaryPrimitives.WriteUInt32BigEndian(u32, (uint)payload.Length);
            stream.Write(u32);
            stream.Write(payload);
        }

        return stream.ToArray();
    }

    /// <summary>
    /// WarpRectilinear (opcode 1) parameters: uint32 plane count, six doubles per plane
    /// (k0 = 1, k1 as given, higher terms zero), then the optical centre.
    /// </summary>
    private static byte[] WarpRectilinearPayload(uint planes, double k1)
    {
        byte[] payload = new byte[4 + 8 * ((int)planes * 6 + 2)];
        var span = payload.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(0, 4), planes);

        for (int plane = 0; plane < (int)planes; plane++)
        {
            int start = 4 + plane * 6 * 8;
            BinaryPrimitives.WriteDoubleBigEndian(span.Slice(start, 8), 1.0);      // k0
            BinaryPrimitives.WriteDoubleBigEndian(span.Slice(start + 8, 8), k1);   // k1
            // k2..k5 stay zero.
        }

        int centre = 4 + (int)planes * 6 * 8;
        BinaryPrimitives.WriteDoubleBigEndian(span.Slice(centre, 8), 0.5);         // cx
        BinaryPrimitives.WriteDoubleBigEndian(span.Slice(centre + 8, 8), 0.5);     // cy
        return payload;
    }

    /// <summary>
    /// FixVignetteRadial (opcode 3) parameters: k0..k4 then the optical centre (7 doubles).
    /// </summary>
    private static byte[] FixVignetteRadialPayload(double k0, double k1, double k2, double k3, double k4)
    {
        byte[] payload = new byte[8 * 7];
        var span = payload.AsSpan();
        BinaryPrimitives.WriteDoubleBigEndian(span.Slice(0, 8), k0);
        BinaryPrimitives.WriteDoubleBigEndian(span.Slice(8, 8), k1);
        BinaryPrimitives.WriteDoubleBigEndian(span.Slice(16, 8), k2);
        BinaryPrimitives.WriteDoubleBigEndian(span.Slice(24, 8), k3);
        BinaryPrimitives.WriteDoubleBigEndian(span.Slice(32, 8), k4);
        BinaryPrimitives.WriteDoubleBigEndian(span.Slice(40, 8), 0.5);  // cx
        BinaryPrimitives.WriteDoubleBigEndian(span.Slice(48, 8), 0.5);  // cy
        return payload;
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
