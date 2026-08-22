using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// What an UNTAGGED TIFF's samples mean — the file carries no ICC profile and no scanner
/// declaration, so the decoder has to infer the encoding, and the only thing always present to
/// infer it from is the bit depth.
///
/// WHAT THIS GUARDS. An 8-bit untagged TIFF used to be read as already-linear, which is what every
/// GUI path hits (they all pass <c>inputIsSrgb: false</c>). That is not a mild exposure error: it
/// COMPRESSES CHANNEL RATIOS toward neutral, and Stage 1's film-base and D_max estimators read
/// exactly those ratios. On the scan that prompted this, an orange base measured R/B = 7.5 read as
/// 2.5 — a mask three times weaker than the real one — so the automatic invert divided by a wrong
/// base and threw the residual out as a colour cast that only hand-sampling could absorb.
///
/// The 16-bit half is guarded just as deliberately: it must NOT change, because existing rolls are
/// calibrated against it and the linear assumption is defensible at that depth.
/// </summary>
public class UntaggedTiffEncodingTests
{
    /// <summary>
    /// A minimal baseline TIFF written BY HAND rather than through <c>TiffIO.ExportTiff16</c>:
    /// the point is to pin what happens to specific bytes on disk, and our own writer embeds a
    /// profile (which would resolve the very ambiguity under test). Single strip, chunky RGB,
    /// no ICC, no private tags.
    /// </summary>
    private static string WriteUntaggedTiff(int bps, int[] samples)
    {
        int spp = 3, w = samples.Length / spp, h = 1;
        int bytesPerSample = bps / 8;
        var pixels = new byte[w * spp * bytesPerSample];
        for (int i = 0; i < samples.Length; i++)
        {
            if (bps == 8) pixels[i] = (byte)samples[i];
            else { pixels[i * 2] = (byte)(samples[i] & 0xFF); pixels[i * 2 + 1] = (byte)(samples[i] >> 8); }
        }

        (ushort Tag, ushort Type, uint Count, uint Value)[] entries =
        {
            (256, 4, 1, (uint)w),          // ImageWidth
            (257, 4, 1, (uint)h),          // ImageLength
            (258, 3, 3, 0),                // BitsPerSample -> offset, filled below
            (259, 3, 1, 1),                // Compression: none
            (262, 3, 1, 2),                // Photometric: RGB
            (273, 4, 1, 0),                // StripOffsets -> filled below
            (277, 3, 1, (uint)spp),        // SamplesPerPixel
            (278, 4, 1, (uint)h),          // RowsPerStrip
            (279, 4, 1, (uint)pixels.Length), // StripByteCounts
            (284, 3, 1, 1),                // PlanarConfig: chunky
        };

        uint ifdOffset = 8;
        uint bpsOffset = ifdOffset + 2 + (uint)entries.Length * 12 + 4;
        uint dataOffset = bpsOffset + 6;

        string path = Path.Combine(Path.GetTempPath(), $"untagged-{bps}-{Guid.NewGuid():N}.tif");
        using var fs = new FileStream(path, FileMode.Create);
        using var bw = new BinaryWriter(fs);
        bw.Write((byte)'I'); bw.Write((byte)'I'); bw.Write((ushort)42); bw.Write(ifdOffset);
        bw.Write((ushort)entries.Length);
        foreach (var (tag, type, count, value) in entries)
        {
            bw.Write(tag); bw.Write(type); bw.Write(count);
            uint v = tag switch { 258 => bpsOffset, 273 => dataOffset, _ => value };
            // A SHORT that fits inline sits in the LOW half of the 4-byte value field on a
            // little-endian file; writing it as a uint places it there already.
            bw.Write(v);
        }
        bw.Write(0u);                                   // next IFD: none
        for (int i = 0; i < 3; i++) bw.Write((ushort)bps);
        bw.Write(pixels);
        return path;
    }

    /// <summary>
    /// The regression itself: an 8-bit untagged file is sRGB-encoded by convention, so its samples
    /// must arrive DECODED. Mid-grey 128 is linear ~0.2158, not 0.502 — reading it as linear is
    /// off by more than a stop right where the film base lives.
    /// </summary>
    [Fact]
    public void Untagged_8bit_is_decoded_as_sRGB()
    {
        string path = WriteUntaggedTiff(8, new[] { 128, 128, 128 });
        try
        {
            ImageBuffer img = TiffIO.LoadTiff(path, inputIsSrgb: false);
            Assert.Equal(Srgb.SrgbToLinear(128 / 255.0f), img.Data[0], 5);
            Assert.True(img.Data[0] < 0.25f, $"still reading 8-bit as linear: got {img.Data[0]:F4}");
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// The reason the bug mattered, stated in the terms Stage 1 actually consumes: the decode has
    /// to RESTORE the channel ratio an orange film base really has. These are the measured base
    /// values from the scan that prompted the fix.
    /// </summary>
    [Fact]
    public void Untagged_8bit_restores_film_base_channel_ratio()
    {
        string path = WriteUntaggedTiff(8, new[] { 206, 138, 81 });
        try
        {
            ImageBuffer img = TiffIO.LoadTiff(path, inputIsSrgb: false);
            double rOverB = img.Data[0] / img.Data[2];
            // True linear is ~7.5; the linear misreading gave ~2.5. Anything near 2.5 means the
            // decode is still missing.
            Assert.True(rOverB > 6.5, $"film-base R/B came out {rOverB:F2}, expected ~7.5");
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// 16-bit untagged stays LINEAR. Deliberately unchanged: rolls already calibrated at this
    /// depth would shift if the decoder started guessing a curve here.
    /// </summary>
    [Fact]
    public void Untagged_16bit_is_still_treated_as_linear()
    {
        string path = WriteUntaggedTiff(16, new[] { 32768, 32768, 32768 });
        try
        {
            ImageBuffer img = TiffIO.LoadTiff(path, inputIsSrgb: false);
            Assert.Equal(32768 / 65535.0f, img.Data[0], 5);
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// An explicit <c>inputIsSrgb: true</c> must still mean exactly what it always did, and must
    /// agree with what the untagged 8-bit path now infers — the two are the same claim about the
    /// same file, reached by different routes.
    /// </summary>
    [Fact]
    public void Explicit_sRGB_flag_agrees_with_the_inferred_8bit_decode()
    {
        string path = WriteUntaggedTiff(8, new[] { 206, 138, 81 });
        try
        {
            ImageBuffer forced = TiffIO.LoadTiff(path, inputIsSrgb: true);
            ImageBuffer inferred = TiffIO.LoadTiff(path, inputIsSrgb: false);
            for (int i = 0; i < 3; i++)
                Assert.Equal(forced.Data[i], inferred.Data[i], 5);
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// The region decode feeds the preview and the film-base patch, so it must infer the same
    /// encoding as the whole-file load. A disagreement here would show as the sampling patch
    /// flashing a different colour over the preview underneath it.
    /// </summary>
    [Fact]
    public void Region_decode_infers_the_same_encoding()
    {
        string path = WriteUntaggedTiff(8, new[] { 206, 138, 81 });
        try
        {
            ImageBuffer whole = TiffIO.LoadTiff(path, inputIsSrgb: false);
            ImageBuffer region = TiffIO.LoadTiffRegion(path, (0, 0, 1, 1), inputIsSrgb: false, maxEdge: 0);
            for (int i = 0; i < 3; i++)
                Assert.Equal(whole.Data[i], region.Data[i], 5);
        }
        finally { File.Delete(path); }
    }
}
