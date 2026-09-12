using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using OpenRevelare.ColorManagement;
using OpenRevelare.Core;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// The gain-map JPEG deliverable: the SDR base is the SDR export, the map is the inverse of the
/// HDR rendition against it, and the container is what a reader that knows MPF and the
/// <c>hdrgm</c> namespace expects to find.
/// </summary>
public sealed class GainMapJpegTests
{
    private const double Peak = 1000d;   // +2.3 stops: 1000 / 203

    /// <summary>
    /// The per-channel map is the exact inverse: the HDR rendition — as fitted into the base's
    /// gamut — comes back component for component, on the colourful fixture whose every pixel
    /// needed fitting.
    /// </summary>
    [Fact]
    public void Per_channel_gain_map_reconstructs_the_hdr_rendition_from_the_sdr_base()
    {
        (RenderedFrame sdr, RenderedFrame hdr, OutputTarget target) = RenderPair(ColorSpaces.Srgb);

        HdrGainMap map = HdrGainMap.Compute(sdr, hdr, ColorSpaces.Srgb, target, GainMapChannels.PerChannel);

        Assert.Equal(0f, map.Metadata.HdrCapacityMin);
        // Capacity is the content's largest gain, never more than the master's headroom.
        float largestGain = Math.Max(Math.Max(map.Metadata.MaxR, map.Metadata.MaxG), map.Metadata.MaxB);
        Assert.Equal(largestGain, map.Metadata.HdrCapacityMax, 5);
        Assert.True(map.Metadata.HdrCapacityMax <= MathF.Log2(target.HighlightHeadroom) + 1e-5f);
        float[] sdrLinear = (float[])sdr.Pixels.Data.Clone();
        OutputRender.Decode(sdrLinear, ColorSpaces.Srgb);
        float[] expectedHdr = HdrGainMap.HdrInBaseGamut(hdr, ColorSpaces.Srgb);
        float weight = HdrGainMap.WeightFor(target.HighlightHeadroom, map.Metadata);
        Assert.Equal(1f, weight);
        // And a display with exactly the content's headroom already applies the map in full.
        Assert.Equal(1f, HdrGainMap.WeightFor(MathF.Pow(2f, map.Metadata.HdrCapacityMax), map.Metadata));
        for (int i = 0; i < sdrLinear.Length; i++)
        {
            float actual = HdrGainMap.Reconstruct(sdrLinear[i], map.Map.Data[i], i % 3, map.Metadata, weight);
            Assert.InRange(actual, expectedHdr[i] - 2e-3f, expectedHdr[i] + 2e-3f);
        }
        // And at zero weight — an SDR display — the reader gets the base back untouched.
        for (int i = 0; i < sdrLinear.Length; i++)
        {
            float actual = HdrGainMap.Reconstruct(sdrLinear[i], map.Map.Data[i], i % 3, map.Metadata, 0f);
            Assert.InRange(actual, sdrLinear[i] - 1e-6f, sdrLinear[i] + 1e-6f);
        }
    }

    /// <summary>
    /// The default luminance map gives back the HDR rendition's luminance at every pixel with the
    /// base's hue; on a neutral picture that is the HDR rendition itself.
    /// </summary>
    [Fact]
    public void Luminance_gain_map_reconstructs_luminance_everywhere_and_neutrals_exactly()
    {
        (RenderedFrame sdr, RenderedFrame hdr, OutputTarget target) = RenderPair(ColorSpaces.Srgb);
        HdrGainMap map = HdrGainMap.Compute(sdr, hdr, ColorSpaces.Srgb, target);
        Assert.Equal(GainMapChannels.Luminance, map.Channels);
        Assert.True(map.Metadata.IsUniform);
        Assert.Equal(0f, map.Metadata.MinR);   // a luminance map never darkens

        float[] sdrLinear = (float[])sdr.Pixels.Data.Clone();
        OutputRender.Decode(sdrLinear, ColorSpaces.Srgb);
        float[] expectedHdr = HdrGainMap.HdrInBaseGamut(hdr, ColorSpaces.Srgb);
        double[,] m = ColorSpaces.Srgb.ToXyz();
        float ly = (float)m[1, 0], lg = (float)m[1, 1], lb = (float)m[1, 2];
        for (int p = 0; p < sdrLinear.Length; p += 3)
        {
            float[] r = new float[3];
            for (int c = 0; c < 3; c++)
                r[c] = HdrGainMap.Reconstruct(sdrLinear[p + c], map.Map.Data[p + c], c, map.Metadata, 1f);
            float yActual = ly * r[0] + lg * r[1] + lb * r[2];
            float yExpected = ly * expectedHdr[p] + lg * expectedHdr[p + 1] + lb * expectedHdr[p + 2];
            Assert.InRange(yActual, yExpected - 3e-3f, yExpected + 3e-3f);
        }

        (sdr, hdr, target) = RenderPair(ColorSpaces.Srgb, neutral: true);
        map = HdrGainMap.Compute(sdr, hdr, ColorSpaces.Srgb, target);
        sdrLinear = (float[])sdr.Pixels.Data.Clone();
        OutputRender.Decode(sdrLinear, ColorSpaces.Srgb);
        for (int i = 0; i < sdrLinear.Length; i++)
        {
            float actual = HdrGainMap.Reconstruct(sdrLinear[i], map.Map.Data[i], i % 3, map.Metadata, 1f);
            Assert.InRange(actual, hdr.Pixels.Data[i] - 2e-3f, hdr.Pixels.Data[i] + 2e-3f);
        }
    }

    /// <summary>
    /// A colour outside the base's primaries is desaturated toward its own grey, not clipped: its
    /// luminance survives, no component goes negative, and the map's range stays the content's
    /// rather than growing by the several stops a clipped-to-black component would add.
    /// </summary>
    [Fact]
    public void Out_of_gamut_hdr_colour_is_desaturated_into_the_base_not_clipped()
    {
        (RenderedFrame sdr, RenderedFrame hdr, OutputTarget target) = RenderPair(ColorSpaces.Srgb);
        Assert.Contains(hdr.Pixels.Data, v => v < 0f);

        float[] fitted = HdrGainMap.HdrInBaseGamut(hdr, ColorSpaces.Srgb);
        double[,] m = ColorSpaces.Srgb.ToXyz();
        float ly = (float)m[1, 0], lg = (float)m[1, 1], lb = (float)m[1, 2];
        Assert.All(fitted, v => Assert.True(v >= 0f));
        for (int p = 0; p < fitted.Length; p += 3)
        {
            float yBefore = ly * hdr.Pixels.Data[p] + lg * hdr.Pixels.Data[p + 1] + lb * hdr.Pixels.Data[p + 2];
            float yAfter = ly * fitted[p] + lg * fitted[p + 1] + lb * fitted[p + 2];
            if (yBefore > 0f) Assert.InRange(yAfter, yBefore - 1e-4f, yBefore + 1e-4f);
        }

        HdrGainMap map = HdrGainMap.Compute(sdr, hdr, ColorSpaces.Srgb, target);
        Assert.True(map.Metadata.MinR > -1.5f, $"min gain {map.Metadata.MinR} suggests a clipped component");
    }

    /// <summary>
    /// Below the knee the two renditions are bit-identical (D-021), so the map must carry no gain
    /// there and gain only where the highlights were spread out.
    /// </summary>
    [Fact]
    public void Gain_map_is_zero_where_the_renditions_agree_and_positive_in_the_highlights()
    {
        // A neutral ramp: the colourful fixture is a raw, unbalanced negative whose every pixel
        // lands outside Rec709 once inverted, which is a different test (see below).
        (RenderedFrame sdr, RenderedFrame hdr, OutputTarget target) = RenderPair(ColorSpaces.Srgb, neutral: true);
        HdrGainMap map = HdrGainMap.Compute(sdr, hdr, ColorSpaces.Srgb, target);

        Assert.Contains(hdr.Pixels.Data, v => v > 1f);
        bool sawGain = false, sawAgreement = false;
        float[] d = hdr.Pixels.Data;
        for (int i = 0; i < map.Map.Data.Length; i++)
        {
            int c = i % 3;
            int p = i - c;
            float gain = map.Metadata.Min(c) + map.Map.Data[i] * (map.Metadata.Max(c) - map.Metadata.Min(c));
            // A pixel outside the base's gamut is gamut-mapped in the SDR rendition and kept
            // extended in the HDR one, so the two are allowed to differ there whatever the level.
            bool inGamut = d[p] >= 0f && d[p + 1] >= 0f && d[p + 2] >= 0f;
            bool aboveKnee = d[p] > 0.5f || d[p + 1] > 0.5f || d[p + 2] > 0.5f;
            if (!inGamut) continue;
            if (aboveKnee) { if (gain > 0.01f) sawGain = true; }
            else { sawAgreement = true; Assert.InRange(gain, -1e-3f, 1e-3f); }
        }
        Assert.True(sawAgreement, "fixture must contain an in-gamut pixel below the knee");
        Assert.True(sawGain, "highlights above the knee must carry gain");
        Assert.True(map.Metadata.MaxR > 0f && map.Metadata.MinR <= 0f);
    }

    [Fact]
    public void Gain_map_refuses_a_base_that_is_not_the_exact_space_it_claims()
    {
        (RenderedFrame sdr, RenderedFrame hdr, OutputTarget target) = RenderPair(ColorSpaces.Srgb);

        Assert.Throws<ArgumentException>(() => HdrGainMap.Compute(sdr, hdr, ColorSpaces.DisplayP3, target));
        Assert.Throws<ArgumentException>(() => HdrGainMap.Compute(hdr, sdr, ColorSpaces.Srgb, target));
        Assert.Throws<ArgumentException>(() => HdrGainMap.Compute(sdr, hdr, ColorSpaces.Srgb, OutputTarget.Sdr(ColorSpaces.Srgb)));
    }

    [Theory]
    [InlineData("sRGB")]
    [InlineData("DisplayP3")]
    public void Gain_map_jpeg_is_the_base_jpeg_followed_by_the_map_the_mpf_index_locates(string baseName)
    {
        ColorSpaceDef baseSpace = ColorSpaces.ByName(baseName, ColorSpaces.Srgb);
        (RenderedFrame sdr, RenderedFrame hdr, OutputTarget target) = RenderPair(baseSpace);
        string path = TempPath();
        try
        {
            JpegIO.ExportGainMapJpeg(sdr, hdr, baseSpace, target, path, quality: 95);
            byte[] file = File.ReadAllBytes(path);

            (int primaryLength, int mapOffset, int mapLength) = ReadMpf(file);
            Assert.Equal(file.Length, primaryLength + mapLength);
            Assert.Equal(primaryLength, mapOffset);
            Assert.Equal(0xFF, file[mapOffset]);
            Assert.Equal(0xD8, file[mapOffset + 1]);
            Assert.Equal(0xD9, file[^1]);

            byte[] primary = file[..primaryLength];
            byte[] map = file[mapOffset..];
            using Image<Rgb24> baseImage = Image.Load<Rgb24>(primary);
            using Image<Rgb24> mapImage = Image.Load<Rgb24>(map);
            Assert.Equal(sdr.Pixels.Width, baseImage.Width);
            Assert.Equal(sdr.Pixels.Width, mapImage.Width);
            Assert.NotNull(baseImage.Metadata.IccProfile);
            Assert.Null(mapImage.Metadata.IccProfile);
            // The luminance map is a single-component JPEG, the form every reader knows.
            Assert.Equal(8, Image.Identify(map).PixelType.BitsPerPixel);

            string primaryXmp = Encoding.UTF8.GetString(baseImage.Metadata.XmpProfile!.ToByteArray()!);
            Assert.Contains("Item:Semantic=\"GainMap\"", primaryXmp, StringComparison.Ordinal);
            Assert.Contains($"Item:Length=\"{mapLength}\"", primaryXmp, StringComparison.Ordinal);
            Assert.Contains("hdrgm:Version=\"1.0\"", primaryXmp, StringComparison.Ordinal);

            string mapXmp = Encoding.UTF8.GetString(mapImage.Metadata.XmpProfile!.ToByteArray()!);
            Assert.Contains("hdrgm:BaseRenditionIsHDR=\"False\"", mapXmp, StringComparison.Ordinal);
            Assert.Contains("hdrgm:HDRCapacityMax=\"", mapXmp, StringComparison.Ordinal);

            // The ISO 21496-1 segment says the same thing as the XMP, in the standard's terms; the
            // base carries only the version fields, the map the full structure.
            GainMapMetadata fromXmp = ParseMetadata(mapXmp);
            Assert.True(fromXmp.IsUniform);
            Assert.Equal(4, IsoPayloadLength(primary));
            GainMapMetadata iso = ReadIsoMetadata(map);
            Assert.Equal(fromXmp.HdrCapacityMin, iso.HdrCapacityMin, 5);
            Assert.Equal(fromXmp.HdrCapacityMax, iso.HdrCapacityMax, 5);
            for (int c = 0; c < 3; c++)
            {
                Assert.Equal(fromXmp.Min(c), iso.Min(c), 5);
                Assert.Equal(fromXmp.Max(c), iso.Max(c), 5);
            }
            Assert.Equal(1f, iso.Gamma, 5);
            Assert.Equal(1f / 64f, iso.OffsetSdr, 5);
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// What a reader does, end to end from the bytes: decode the 8-bit base, linearise it, decode
    /// the 8-bit map, apply the XMP's metadata — and get the HDR rendition back within the two
    /// quantisations' worth of error.
    /// </summary>
    [Fact]
    public void A_reader_recovers_the_hdr_rendition_from_the_file()
    {
        (RenderedFrame sdr, RenderedFrame hdr, OutputTarget target) = RenderPair(ColorSpaces.Srgb, neutral: true);
        string path = TempPath();
        try
        {
            JpegIO.ExportGainMapJpeg(sdr, hdr, ColorSpaces.Srgb, target, path, quality: 100);
            byte[] file = File.ReadAllBytes(path);
            (int primaryLength, int mapOffset, _) = ReadMpf(file);
            using Image<Rgb24> baseImage = Image.Load<Rgb24>(file[..primaryLength]);
            using Image<Rgb24> mapImage = Image.Load<Rgb24>(file[mapOffset..]);
            GainMapMetadata metadata = ParseMetadata(Encoding.UTF8.GetString(mapImage.Metadata.XmpProfile!.ToByteArray()!));
            float weight = HdrGainMap.WeightFor(target.HighlightHeadroom, metadata);
            Assert.Equal(1f, weight);

            int w = baseImage.Width, h = baseImage.Height;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    Rgb24 b = baseImage[x, y];
                    Rgb24 m = mapImage[x, y];
                    float[] baseLinear = { b.R / 255f, b.G / 255f, b.B / 255f };
                    OutputRender.Decode(baseLinear, ColorSpaces.Srgb);
                    float[] code = { m.R / 255f, m.G / 255f, m.B / 255f };
                    for (int c = 0; c < 3; c++)
                    {
                        float expected = Math.Max(hdr.Pixels.Data[(y * w + x) * 3 + c], 0f);
                        float actual = HdrGainMap.Reconstruct(baseLinear[c], code[c], c, metadata, weight);
                        // JPEG at quality 100 on a 4×2 fixture is not lossless; the base's 8-bit
                        // step alone is 0.4 % of white, and the map's step is ~0.01 stop.
                        float tolerance = 0.03f + 0.03f * expected;
                        Assert.InRange(actual, expected - tolerance, expected + tolerance);
                    }
                }
            }
        }
        finally { File.Delete(path); }
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The pair the GUI export renders: the same params with and without the HDR peak, the SDR
    /// member of the shoulder family in <paramref name="baseSpace"/>, no print LUT on either.
    /// </summary>
    private static (RenderedFrame Sdr, RenderedFrame Hdr, OutputTarget Target) RenderPair(
        ColorSpaceDef baseSpace, bool neutral = false)
    {
        var hdrParams = new FrameParams { OutputSpace = "sRGB", PrintLut = "", HdrPeakNits = Peak };
        var sdrParams = new FrameParams { OutputSpace = baseSpace.Name, PrintLut = "", HdrPeakNits = 0d };
        using var cmm = new LittleCmsEngine();
        RenderedFrame hdr = Pipeline.Render(MakeWorkingFrame(neutral), hdrParams, ColorPipelineVersion.ManagedV2, cmm);
        RenderedFrame sdr = Pipeline.Render(MakeWorkingFrame(neutral), sdrParams, ColorPipelineVersion.ManagedV2, cmm);
        return (sdr, hdr, hdrParams.ResolvedOutputTarget);
    }

    /// <summary>
    /// The synthetic negative the output-target tests use — an unbalanced orange mask, so the
    /// positive is far outside Rec709 — or, with <paramref name="neutral"/>, a grey ramp that
    /// renders in gamut from shadow to a highlight above the knee.
    /// </summary>
    private static WorkingFrame MakeWorkingFrame(bool neutral = false)
    {
        var pixels = new ImageBuffer(4, 2, neutral
            ? [
                0.81f,  0.81f,  0.81f,
                0.63f,  0.63f,  0.63f,
                0.42f,  0.42f,  0.42f,
                0.24f,  0.24f,  0.24f,
                0.13f,  0.13f,  0.13f,
                0.055f, 0.055f, 0.055f,
                0.02f,  0.02f,  0.02f,
                0.008f, 0.008f, 0.008f,
            ]
            : [
                0.81f,  0.52f,   0.29f,
                0.63f,  0.31f,   0.12f,
                0.42f,  0.17f,   0.052f,
                0.24f,  0.075f,  0.015f,
                0.13f,  0.035f,  0.0035f,
                0.055f, 0.010f,  0.0008f,
                1.15f,  1.05f,   0.95f,
                0.008f, 0.0015f, 0.00012f,
            ])
        {
            SourceQuantisationStep = 1.0 / 65535.0,
        };
        var originalEncoding = new UncharacterizedPixelEncoding(
            CaptureKind.Synthetic,
            "test:gain-map-negative-v1",
            CompatibilityPolicy.LegacyTreatNumbersAsWorking,
            TransferState.Unknown,
            NumericRange.Extended);
        var source = new SourceDescriptor(
            "test:gain-map-negative-v1",
            "gain-map synthetic negative",
            originalEncoding,
            "test-generated RGB float32");
        return new WorkingFrame(
            pixels,
            WorkingSpaceId.LinearAcesCgV1,
            WorkingAdmission.LegacyUncharacterizedPassthrough,
            source);
    }

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"openrevelare-gainmap-{Guid.NewGuid():N}.jpg");

    /// <summary>A minimal MPF reader: the primary's size and the second image's offset and size.</summary>
    private static (int PrimaryLength, int MapOffset, int MapLength) ReadMpf(byte[] file)
    {
        int pos = 2;
        while (pos + 4 <= file.Length && file[pos] == 0xFF)
        {
            byte marker = file[pos + 1];
            int length = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(pos + 2));
            if (marker == 0xE2 && file[pos + 4] == 'M' && file[pos + 5] == 'P' && file[pos + 6] == 'F' && file[pos + 7] == 0)
            {
                int header = pos + 8;
                Assert.Equal((byte)'M', file[header]);
                uint ifdOffset = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(header + 4));
                int ifd = header + (int)ifdOffset;
                ushort count = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(ifd));
                int entriesAt = -1;
                uint images = 0;
                for (int i = 0; i < count; i++)
                {
                    int e = ifd + 2 + i * 12;
                    ushort tag = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(e));
                    uint value = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(e + 8));
                    if (tag == 0xB001) images = value;
                    if (tag == 0xB002) entriesAt = header + (int)value;
                }
                Assert.Equal(2u, images);
                Assert.True(entriesAt > 0);
                uint primarySize = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(entriesAt + 4));
                uint primaryOffset = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(entriesAt + 8));
                Assert.Equal(0u, primaryOffset);
                uint mapSize = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(entriesAt + 16 + 4));
                uint mapOffset = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(entriesAt + 16 + 8));
                return ((int)primarySize, header + (int)mapOffset, (int)mapSize);
            }
            if (marker is not (>= 0xE0 and <= 0xEF or 0xFE)) break;
            pos += 2 + length;
        }
        throw new Xunit.Sdk.XunitException("no MPF segment in the base image");
    }

    private static readonly byte[] IsoMagic = Encoding.ASCII.GetBytes("urn:iso:std:iso:ts:21496:-1\0");

    /// <summary>Position and payload length (after the namespace) of the image's ISO 21496-1 segment.</summary>
    private static (int Pos, int Length) FindIso(byte[] jpeg)
    {
        int pos = 2;
        while (pos + 4 <= jpeg.Length && jpeg[pos] == 0xFF)
        {
            byte marker = jpeg[pos + 1];
            int length = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(pos + 2));
            if (marker == 0xE2 && jpeg.AsSpan(pos + 4, IsoMagic.Length).SequenceEqual(IsoMagic))
                return (pos, length - 2 - IsoMagic.Length);
            if (marker is not (>= 0xE0 and <= 0xEF or 0xFE)) break;
            pos += 2 + length;
        }
        throw new Xunit.Sdk.XunitException("no ISO 21496-1 segment");
    }

    private static int IsoPayloadLength(byte[] jpeg) => FindIso(jpeg).Length;

    /// <summary>The ISO 21496-1 GainMapMetadata structure from the image's APP2 segment.</summary>
    private static GainMapMetadata ReadIsoMetadata(byte[] jpeg)
    {
        byte[] magic = IsoMagic;
        int pos = FindIso(jpeg).Pos;
        {
            {
                int length = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(pos + 2));
                int o = pos + 4 + magic.Length;
                Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(o)));       // minimum_version
                Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(o + 2)));   // writer_version
                byte flags = jpeg[o + 4];
                bool multichannel = (flags & 0x80) != 0;
                Assert.True((flags & 0x40) != 0, "use_base_colour_space must be set");
                o += 5;
                float Rational()
                {
                    int n = BinaryPrimitives.ReadInt32BigEndian(jpeg.AsSpan(o));
                    uint d = BinaryPrimitives.ReadUInt32BigEndian(jpeg.AsSpan(o + 4));
                    o += 8;
                    return (float)((double)n / d);
                }
                float baseHeadroom = Rational(), alternateHeadroom = Rational();
                int channels = multichannel ? 3 : 1;
                float[] min = new float[3], max = new float[3];
                float gamma = 1f, offB = 0f, offA = 0f;
                for (int c = 0; c < channels; c++)
                {
                    min[c] = Rational(); max[c] = Rational(); gamma = Rational(); offB = Rational(); offA = Rational();
                }
                if (!multichannel) { min[1] = min[2] = min[0]; max[1] = max[2] = max[0]; }
                Assert.Equal(o, pos + 2 + length);
                return new GainMapMetadata(min[0], min[1], min[2], max[0], max[1], max[2], gamma, offB, offA, baseHeadroom, alternateHeadroom);
            }
        }
    }

    private static GainMapMetadata ParseMetadata(string xmp)
    {
        float Scalar(string name) => float.Parse(
            Regex.Match(xmp, $"hdrgm:{name}=\"([^\"]+)\"").Groups[1].Value, CultureInfo.InvariantCulture);
        float[] Range(string name)
        {
            Match seq = Regex.Match(xmp, $"<hdrgm:{name}><rdf:Seq>(.*?)</rdf:Seq></hdrgm:{name}>");
            if (seq.Success)
            {
                return Regex.Matches(seq.Groups[1].Value, "<rdf:li>([^<]+)</rdf:li>")
                    .Select(m => float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToArray();
            }
            float one = Scalar(name);
            return [one, one, one];
        }
        float[] min = Range("GainMapMin");
        float[] max = Range("GainMapMax");
        return new GainMapMetadata(
            min[0], min[1], min[2], max[0], max[1], max[2],
            Scalar("Gamma"), Scalar("OffsetSDR"), Scalar("OffsetHDR"),
            Scalar("HDRCapacityMin"), Scalar("HDRCapacityMax"));
    }
}
