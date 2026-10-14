using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class LibRawRuntimeTests
{
    // Mandatory on every CI platform: a skipped decode must not conceal a missing/wrong DLL.
    [Fact]
    public void Loads_pinned_native_decoder() => Assert.Equal("0.22.2-Release", RawDecode.LibRawVersion);

    [Fact]
    public void Invalid_input_does_not_poison_subsequent_parallel_decodes()
    {
        string bad = Path.Combine(TestDataIsolation.Root, $"invalid-{Guid.NewGuid():N}.rw2");
        string good = Path.Combine(TestDataIsolation.Root, $"native-{Guid.NewGuid():N}.dng");
        try
        {
            File.WriteAllBytes(bad, new byte[16]);
            var source = new ImageBuffer(64, 48);
            Array.Fill(source.Data, 0.5f);
            LinearDng.Write(source, ColorSpaces.Srgb, good);
            Parallel.For(0, 12, _ =>
            {
                var error = Assert.ThrowsAny<Exception>(() => RawDecode.DecodeRaw(bad));
                Assert.Contains("LibRaw", error.Message);
                var decoded = RawDecode.DecodeRaw(good);
                Assert.Equal((64, 48), (decoded.Width, decoded.Height));
                // Process disposes its context before copying the independently owned image.
                Assert.All(decoded.Data, value => Assert.InRange(value, 0.213f, 0.215f));
            });
        }
        finally { File.Delete(bad); File.Delete(good); }
    }

    [Fact]
    public void Bayer_decode_preserves_unit_wb_crop_preview_and_unicode_metadata()
    {
        string path = Path.Combine(TestDataIsolation.Root, $"相机-é-{Guid.NewGuid():N}.dng");
        try
        {
            WriteBayerDng(path);
            // These probes use native open_file (UTF-16 on Windows, UTF-8 on Unix),
            // whereas the decode path uses a pinned input buffer.
            Assert.Equal(new[] { 2.0, 1.0, 3.0 }, RawDecode.CameraWhiteBalance(path));
            Assert.NotNull(RawDecode.CameraToSrgbMatrix(path));
            var full = RawDecode.DecodeRaw(path);
            Assert.Equal((144, 116), (full.Width, full.Height));
            int centre = (full.Height / 2 * full.Width + full.Width / 2) * 3;
            for (int c = 0; c < 3; c++)
                Assert.InRange(full.Data[centre + c], (c + 1) * 10000f / 65535f - 0.001f,
                    (c + 1) * 10000f / 65535f + 0.001f);
            var preview = RawDecode.DecodeRawDownsampled(path, RawDecode.FbddMode.Off,
                new[] { 1000 }, RawDecode.Demosaic.Preview);
            Assert.Equal((144, 116), (preview.SourceWidth, preview.SourceHeight));
            Assert.Equal((72, 58), (preview.Previews[0].Width, preview.Previews[0].Height));
            var region = RawDecode.DecodeRawRegion(path, RawDecode.RawBackend.LibRaw,
                RawDecode.FbddMode.Off, 60, 45, 8, 8, full.Width, full.Height);
            Assert.NotNull(region);
            var (slice, x0, y0) = region.Value;
            int local = ((45 - y0) * slice.Width + 60 - x0) * 3;
            int global = (45 * full.Width + 60) * 3;
            for (int c = 0; c < 3; c++)
                Assert.Equal(full.Data[global + c], slice.Data[local + c], 4);
        }
        finally { File.Delete(path); }
    }

    // Independent, minimal TIFF/DNG fixture: RGGB, constant per-channel sensor values,
    // deliberately non-unit as-shot WB, and a vendor crop smaller than the sensor.
    private static void WriteBayerDng(string path)
    {
        byte[] Shorts(params ushort[] values) => values.SelectMany(BitConverter.GetBytes).ToArray();
        byte[] Longs(params uint[] values) => values.SelectMany(BitConverter.GetBytes).ToArray();
        var entries = new List<(ushort Tag, ushort Type, uint Count, byte[] Data)>
        {
            (256, 4, 1, Longs(160)), (257, 4, 1, Longs(128)),
            (258, 3, 1, Shorts(16)), (259, 3, 1, Shorts(1)),
            (262, 3, 1, Shorts(32803)),
            (271, 2, 10, System.Text.Encoding.ASCII.GetBytes("Synthetic\0")),
            (272, 2, 6, System.Text.Encoding.ASCII.GetBytes("Bayer\0")),
            (273, 4, 1, Longs(0)), (277, 3, 1, Shorts(1)),
            (278, 4, 1, Longs(128)), (279, 4, 1, Longs(160 * 128 * 2)),
            (284, 3, 1, Shorts(1)), (33421, 3, 2, Shorts(2, 2)),
            (33422, 1, 4, new byte[] { 0, 1, 1, 2 }),
            (50706, 1, 4, new byte[] { 1, 4, 0, 0 }),
            (50717, 4, 1, Longs(65535)),
            (50719, 4, 2, Longs(8, 6)), (50720, 4, 2, Longs(144, 116)),
            (50721, 10, 9, Longs(1,1, 0,1, 0,1, 0,1, 1,1, 0,1, 0,1, 0,1, 1,1)),
            (50728, 5, 3, Longs(1,2, 1,1, 1,3)),
            (50778, 3, 1, Shorts(21)),
        };
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(new byte[] { (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0 });
        writer.Write((ushort)entries.Count);
        int extra = 8 + 2 + entries.Count * 12 + 4;
        int pixels = extra + entries.Where(e => e.Data.Length > 4).Sum(e => e.Data.Length);
        foreach (var (tag, type, count, data) in entries)
        {
            writer.Write(tag); writer.Write(type); writer.Write(count);
            if (tag == 273) writer.Write((uint)pixels);
            else if (data.Length <= 4) { writer.Write(data); writer.Write(new byte[4 - data.Length]); }
            else { writer.Write((uint)extra); extra += data.Length; }
        }
        writer.Write(0u);
        foreach (var e in entries.Where(e => e.Data.Length > 4)) writer.Write(e.Data);
        for (int y = 0; y < 128; y++)
            for (int x = 0; x < 160; x++)
                writer.Write((ushort)((y % 2 == 0 ? x % 2 == 0 ? 1 : 2 : x % 2 == 0 ? 2 : 3) * 10000));
        File.WriteAllBytes(path, stream.ToArray());
    }
}
