using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using BitMiracle.LibTiff.Classic;
using OpenRevelare.Core;
using OpenRevelare.Gui.Services;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class PureNegTrainingPackTests
{
    [Fact]
    public void Pack_preserves_source_and_upstream_layers_including_erasures_and_additions()
    {
        string root = Directory.CreateTempSubdirectory("training-test-").FullName;
        try
        {
            string source = Path.Combine(root, "private-name.tiff");
            TiffIO.ExportTiff16(new ImageBuffer(4, 2, Enumerable.Range(0, 24).Select(i => i / 30f).ToArray()), source);
            byte[] bytes = File.ReadAllBytes(source);
            var auto = new DustMask(4, 2, (0, 0, 1, 1), [new(0, 0, 2)]);
            var final = new DustMask(4, 2, (0, 0, 1, 1), [new(0, 1, 3)]);
            var review = new PureNegComponent.ReviewImage(source, "unused", 4, 2, [], false);
            string pack = PureNegTrainingPack.Export(root, source, auto, final, review, new HashSet<int>(),
                new(true, true), CancellationToken.None);
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(pack, "source.tiff")));
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(pack, "pack.json")));
            Assert.Equal("pureneg-training-pack", json.RootElement.GetProperty("schema").GetString());
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                json.RootElement.GetProperty("sourceSha256").GetString());
            Assert.True(json.RootElement.GetProperty("labels").GetProperty("test").GetBoolean());
            Assert.True(json.RootElement.GetProperty("checks").GetProperty("sameSizeAsExport").GetBoolean());
            Assert.Equal(0, json.RootElement.GetProperty("checks").GetProperty("changedOutsideMask").GetInt32());
            Assert.True(File.Exists(Path.Combine(pack, "repair-preview.tiff")));
            Assert.DoesNotContain("private-name", json.RootElement.GetRawText());
            foreach (var (layer, expected) in new[] {
                ("auto", new ushort[] { 65535, 65535, 0, 0 }),
                ("final", new ushort[] { 0, 65535, 65535, 0 }),
                ("added", new ushort[] { 0, 0, 65535, 0 }),
                ("erased", new ushort[] { 65535, 0, 0, 0 }),
                ("undone", new ushort[] { 0, 0, 0, 0 }) })
            {
                string tiffPath = Path.Combine(root, layer + ".tiff");
                using (var file = File.OpenRead(Path.Combine(pack, "mask-" + layer + ".tiff.gz")))
                using (var gzip = new GZipStream(file, CompressionMode.Decompress))
                using (var output = File.Create(tiffPath)) gzip.CopyTo(output);
                using var tiff = Tiff.Open(tiffPath, "r");
                Assert.Equal(16, tiff.GetField(TiffTag.BITSPERSAMPLE)[0].ToInt());
                byte[] row = new byte[8];
                Assert.True(tiff.ReadScanline(row, 0));
                Assert.Equal(expected, Enumerable.Range(0, 4).Select(x => BitConverter.ToUInt16(row, x * 2)));
                Assert.True(tiff.ReadScanline(row, 1));
                Assert.All(row, b => Assert.Equal((byte)0, b));
                Assert.Equal(expected.Count(v => v != 0), json.RootElement.GetProperty("pixels").GetProperty(layer).GetInt32());
            }
            string second = PureNegTrainingPack.Export(root, source, auto, final, review, new HashSet<int>(),
                new(), CancellationToken.None);
            Assert.NotEqual(pack, second);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Real_probability_map_is_verified_compressed_and_summarized_per_region()
    {
        string root = Directory.CreateTempSubdirectory("training-map-test-").FullName;
        try
        {
            string source = Path.Combine(root, "source.tiff"), map = Path.Combine(root, "map.u8"), runs = Path.Combine(root, "runs.bin");
            TiffIO.ExportTiff16(new ImageBuffer(4, 2), source);
            byte[] values = [10, 30, 0, 0, 0, 0, 0, 0];
            File.WriteAllBytes(map, values);
            using (var writer = new BinaryWriter(File.Create(runs)))
            {
                writer.Write("FDRUNS01"u8); writer.Write(1); writer.Write(4); writer.Write(2); writer.Write(1);
                writer.Write(1); writer.Write(0); writer.Write(0); writer.Write(2);
            }
            var mask = new DustMask(4, 2, (0, 0, 1, 1), [new(0, 0, 2)]);
            var review = new PureNegComponent.ReviewImage(source, runs, 4, 2,
                [new(1, 0, 0, 2, 1, 2, ["fibers"])], false, FibreMapPath: map,
                FibreMapSha256: Convert.ToHexString(SHA256.HashData(values)));
            string pack = PureNegTrainingPack.Export(root, source, mask, mask, review, new HashSet<int> { 1 }, new(), CancellationToken.None);
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(pack, "pack.json")));
            Assert.Equal(20, json.RootElement.GetProperty("regions")[0].GetProperty("probMean").GetInt32());
            Assert.Equal(30, json.RootElement.GetProperty("regions")[0].GetProperty("probMax").GetInt32());
            using var gzip = new GZipStream(File.OpenRead(Path.Combine(pack, "fibre-map.u8.gz")), CompressionMode.Decompress);
            using var unpacked = new MemoryStream();
            gzip.CopyTo(unpacked);
            Assert.Equal(values, unpacked.ToArray());
            File.WriteAllBytes(map, new byte[8]);
            Assert.Throws<InvalidDataException>(() => PureNegTrainingPack.Export(root, source, mask, mask, review,
                new HashSet<int> { 1 }, new(), CancellationToken.None));
            Assert.Empty(Directory.GetDirectories(root, "*.partial"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Ellipse_history_survives_undo_redo_and_replays_to_the_exported_mask()
    {
        string root = Directory.CreateTempSubdirectory("training-history-test-").FullName;
        try
        {
            string source = Path.Combine(root, "source.tiff");
            TiffIO.ExportTiff16(new ImageBuffer(20, 16), source);
            var initial = new DustMask(20, 16, (.1, .1, .5, .5), [new(3, 2, 8)]);
            var editor = new DustMaskEditor(initial);
            Assert.True(editor.Paint([new(5, 3, 2.5, .7, -.3, 1.5)], true));
            Assert.True(editor.Paint([new(12, 9, 3, 1, -1, 2)], false));
            Assert.Equal(2, editor.History.Count);
            Assert.True(editor.Undo());
            Assert.Single(editor.History);
            Assert.True(editor.Redo());
            var review = new PureNegComponent.ReviewImage(source, "unused", 20, 16, [], false);
            string pack = PureNegTrainingPack.Export(root, source, initial, editor.Mask, review, new HashSet<int>(), new(),
                CancellationToken.None, history: editor.History);
            using var strokes = JsonDocument.Parse(File.ReadAllText(Path.Combine(pack, "strokes.json")));
            Assert.Equal(2, strokes.RootElement.GetProperty("strokes").GetArrayLength());
            Assert.Equal("erase", strokes.RootElement.GetProperty("strokes")[0].GetProperty("kind").GetString());
            Assert.Throws<InvalidDataException>(() => PureNegTrainingPack.Export(root, source, initial, editor.Mask,
                review, new HashSet<int>(), new(), CancellationToken.None, history: []));
            Assert.True(editor.Undo());
            Assert.True(editor.Paint([new(14, 4, 1, 0, 0, 1)], false));
            Assert.False(editor.CanRedo);
            Assert.Equal(14, editor.History[^1].Brushes[0].X);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Cancellation_and_source_failure_leave_no_partial_pack()
    {
        string root = Directory.CreateTempSubdirectory("training-test-").FullName;
        try
        {
            var mask = new DustMask(4, 2, (0, 0, 1, 1), []);
            var review = new PureNegComponent.ReviewImage("missing", "unused", 4, 2, [], false);
            Assert.Throws<OperationCanceledException>(() => PureNegTrainingPack.Export(root, "missing", mask, mask,
                review, new HashSet<int>(), new(), new CancellationToken(true)));
            Assert.Throws<FileNotFoundException>(() => PureNegTrainingPack.Export(root, "missing", mask, mask,
                review, new HashSet<int>(), new(), CancellationToken.None));
            Assert.Empty(Directory.GetFileSystemEntries(root));
        }
        finally { Directory.Delete(root, true); }
    }
}
