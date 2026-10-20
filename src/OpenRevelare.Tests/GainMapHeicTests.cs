using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using OpenRevelare.ColorManagement;
using OpenRevelare.Core;
using OpenRevelare.Gui.Models;
using Xunit;

namespace OpenRevelare.Tests;

[Collection("GPU scheduler")]
public sealed class GainMapHeicTests
{
    [Theory]
    [InlineData("sRGB", 33, 17)]
    [InlineData("DisplayP3", 1031, 769)]
    public void Adaptive_file_keeps_sdr_primary_and_resolves_iso_alternate_and_scaled_gain(string name, int width, int height)
    {
        var space = ColorSpaces.ByName(name, ColorSpaces.Srgb);
        var sdr = Frame(space, false, width, height);
        var hdr = Frame(ColorSpaces.LinearExtendedSrgb, true, width, height);
        float[] beforeBase = (float[])sdr.Pixels.Data.Clone(), beforeHdr = (float[])hdr.Pixels.Data.Clone();
        string path = Temp();
        try
        {
            var exif = SourceExif.ForExport(null, false, new FilmCaptureMetadata { Artist = "Adaptive HEIC" });
            HeicIO.ExportGainMapHeic(sdr, hdr, space, OutputTarget.Hdr(812), path, sourceExif: exif);
            byte[] file = File.ReadAllBytes(path);
            var top = Boxes(file);
            Assert.Contains("tmap", Encoding.ASCII.GetString(top.Single(b => b.Type == "ftyp").Data));
            var meta = Boxes(top.Single(b => b.Type == "meta").Data, 4);
            ushort primary = U16(meta.Single(b => b.Type == "pitm").Data, 4);
            var infos = Boxes(meta.Single(b => b.Type == "iinf").Data, 6).ToDictionary(i => U16(i.Data, 4), i => i.Data);
            ushort tmap = Assert.Single(infos, i => Encoding.ASCII.GetString(i.Value, 8, 4) == "tmap").Key;
            Assert.NotEqual(primary, tmap);
            Assert.Equal(0, infos[primary][3] & 1);
            var references = Boxes(meta.Single(b => b.Type == "iref").Data, 4);
            byte[] derived = Assert.Single(references, r => r.Type == "dimg" && U16(r.Data, 0) == tmap).Data;
            Assert.Equal(2, U16(derived, 2));
            Assert.Equal(primary, U16(derived, 4));
            ushort gain = U16(derived, 6);
            Assert.Equal(1, infos[gain][3] & 1);
            byte[] alternatives = Assert.Single(Boxes(meta.Single(b => b.Type == "grpl").Data)).Data;
            Assert.Equal(2u, U32(alternatives, 8));
            Assert.Equal((uint)tmap, U32(alternatives, 12));
            Assert.Equal((uint)primary, U32(alternatives, 16));
            var payloads = Payloads(file, meta.Single(b => b.Type == "iloc").Data);
            Assert.Equal(infos.Count, payloads.Count);
            byte[] toneMap = payloads[tmap];
            Assert.Equal(0, toneMap[0]);
            Assert.Equal(GainMapJpegContainer.IsoMetadataPayload(HdrGainMap.Compute(sdr, hdr, space,
                OutputTarget.Hdr(812), GainMapChannels.PerChannel, useCommonChannelRange: true).Metadata), toneMap[1..]);
            Assert.Equal(0u, U32(toneMap, 1)); // ISO min/writer versions.
            Assert.Equal(0x40, toneMap[5]); // Shared channel parameters, use base colour space.
            Assert.Equal(0u, U32(toneMap, 6)); // SDR headroom is zero.
            Assert.True(U32(toneMap, 10) > 0); // Nonzero rational denominator.

            var iprp = Boxes(meta.Single(b => b.Type == "iprp").Data);
            var properties = Boxes(iprp.Single(b => b.Type == "ipco").Data);
            byte[] ipma = iprp.Single(b => b.Type == "ipma").Data;
            var associations = new Dictionary<ushort, List<(string Type, byte[] Data)>>();
            for (int o = 8; o < ipma.Length;)
            {
                ushort id = U16(ipma, o); int count = ipma[o + 2]; o += 3;
                var props = new List<(string, byte[])>();
                for (int j = 0; j < count; j++, o += 2)
                {
                    int index = U16(ipma, o) & 0x7fff;
                    Assert.InRange(index, 1, properties.Count);
                    props.Add(properties[index - 1]);
                }
                associations.Add(id, props);
            }
            byte[] dimensions = Assert.Single(associations[tmap], p => p.Type == "ispe").Data;
            Assert.Equal((uint)width, U32(dimensions, 4)); Assert.Equal((uint)height, U32(dimensions, 8));
            byte[] alternate = Assert.Single(associations[tmap], p => p.Type == "colr").Data;
            Assert.Equal(name == "DisplayP3" ? 12 : 1, U16(alternate, 4));
            Assert.Equal(8, U16(alternate, 6)); // Linear alternate, not PQ.
            Assert.Contains(associations[primary], p => p.Type == "colr" && p.Data.AsSpan(0, 4).SequenceEqual("prof"u8)
                && p.Data.AsSpan(4).SequenceEqual(sdr.OutputProfile.IccBytes.AsSpan()));
            Assert.DoesNotContain(associations[gain], p => p.Type == "colr" && p.Data.AsSpan(0, 4).SequenceEqual("prof"u8));
            if (width > 1024)
            {
                // Gain is 516 x 385 here; standalone HEVC may pad and crop its last row.
                Assert.Equal("hvc1", Encoding.ASCII.GetString(infos[gain], 8, 4));
                Assert.Contains(associations[gain], p => p.Type == "clap" && U32(p.Data, 0) == 516 && U32(p.Data, 8) == 385);
            }
            Assert.Contains("Adaptive HEIC", Encoding.UTF8.GetString(file));
        }
        finally { File.Delete(path); }
        Assert.Equal(beforeBase, sdr.Pixels.Data); Assert.Equal(beforeHdr, hdr.Pixels.Data);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Size_fitting_counts_both_images_and_failure_preserves_existing_file(bool forceResize)
    {
        string path = Temp();
        try
        {
            var sdr = Frame(ColorSpaces.Srgb, false, 128, 96);
            var hdr = Frame(ColorSpaces.LinearExtendedSrgb, true, 128, 96);
            var target = OutputTarget.Hdr(812);
            HeicIO.ExportGainMapHeic(sdr, hdr, ColorSpaces.Srgb, target, path, forceResize ? 40 : 100);
            long ceiling = new FileInfo(path).Length - (forceResize ? 1 : 300);
            var fit = HeicIO.ExportGainMapHeic(sdr, hdr, ColorSpaces.Srgb, target, path, 100, ceiling);
            Assert.NotNull(fit); Assert.True(new FileInfo(path).Length <= ceiling);
            if (forceResize) Assert.True(fit!.Shrunk);
            byte[] before = File.ReadAllBytes(path);
            Assert.Throws<ArgumentException>(() => HeicIO.ExportGainMapHeic(sdr,
                Frame(ColorSpaces.LinearExtendedSrgb, true, 127, 96), ColorSpaces.Srgb, target, path));
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Adaptive_option_is_explicit_and_persisted_while_pq_stays_default()
    {
        var options = new ExportOptions { Format = ExportFormat.Heic, HdrLimitStops = 2 };
        Assert.False(options.HeicAdaptiveHdr); Assert.False(options.WritesGainMap);
        Assert.Contains("PQ", options.Summary());
        options.HeicAdaptiveHdr = true;
        options.HdrBaseSpace = ColorSpaces.DisplayP3.Name;
        Assert.True(options.WritesGainMap); Assert.Equal(ColorSpaces.DisplayP3, options.FileDisplaySpace);
        Assert.Contains("Adaptive HDR", options.Summary());
        Assert.True(JsonSerializer.Deserialize<ExportOptions>(JsonSerializer.Serialize(options))!.HeicAdaptiveHdr);
    }

    private static RenderedFrame Frame(ColorSpaceDef space, bool hdr, int width, int height)
    {
        var pixels = new ImageBuffer(width, height);
        for (int i = 0; i < pixels.Data.Length; i++) pixels.Data[i] = (i % 97) / 97f * (hdr ? 4f : 1f);
        var profile = BuiltInColorProfiles.For(space, ProfileRole.Output);
        var encoding = new CharacterizedPixelEncoding(profile, hdr ? ColorReference.SceneReferred : ColorReference.DisplayReferred,
            hdr ? TransferState.LinearInProfilePrimaries : TransferState.ProfileEncoded, hdr ? NumericRange.Extended : NumericRange.Normalized);
        var recipe = new OutputRecipe(ColorPipelineVersion.ManagedV2, profile, RenderingIntent.RelativeColorimetric, false, "HEIC fixture", "", false);
        return new RenderedFrame(pixels, encoding, recipe, RenderFingerprint.ComputeManaged(pixels, encoding, recipe));
    }
    private static string Temp() => Path.Combine(Path.GetTempPath(), $"adaptive-heic-{Guid.NewGuid():N}.heic");
    private static ushort U16(byte[] d, int o) => BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(o));
    private static uint U32(byte[] d, int o) => BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(o));
    private static List<(string Type, byte[] Data)> Boxes(byte[] data, int start = 0)
    {
        var result = new List<(string, byte[])>();
        while (start < data.Length)
        {
            int size = checked((int)U32(data, start)); Assert.InRange(size, 8, data.Length - start);
            result.Add((Encoding.ASCII.GetString(data, start + 4, 4), data[(start + 8)..(start + size)])); start += size;
        }
        return result;
    }
    private static Dictionary<ushort, byte[]> Payloads(byte[] file, byte[] iloc)
    {
        Assert.Equal(0, iloc[0]); Assert.Equal(0x44, iloc[4]); Assert.Equal(0, iloc[5]);
        var result = new Dictionary<ushort, byte[]>(); int count = U16(iloc, 6);
        for (int i = 0, o = 8; i < count; i++, o += 14)
        {
            Assert.Equal(0, U16(iloc, o + 2)); Assert.Equal(1, U16(iloc, o + 4));
            int start = checked((int)U32(iloc, o + 6)), length = checked((int)U32(iloc, o + 10));
            Assert.InRange(start, 0, file.Length - length); result.Add(U16(iloc, o), file[start..(start + length)]);
        }
        return result;
    }
}
