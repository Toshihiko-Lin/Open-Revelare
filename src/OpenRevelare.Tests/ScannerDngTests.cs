using OpenRevelare.Core;
using OpenRevelare.ColorManagement;
using OpenRevelare.Gui.Views;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class ScannerDngTests
{
    // Independent TIFF writer: a one-pixel RGB preview and a 2x2 linear RGBI SubIFD.
    private static string Fixture(string software = "VueScan 9.8.16", int bits = 16, bool cfa = false, int samples = 4,
        string make = "Epson", int photometric = 34892)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".DNG");
        using var stream = File.Create(path);
        using var w = new BinaryWriter(stream);
        w.Write((ushort)0x4949); w.Write((ushort)42); w.Write(8);
        void Directory(int offset, (int Tag, int Type, int Count, int Value)[] tags)
        {
            stream.Position = offset; w.Write((ushort)tags.Length);
            foreach (var t in tags.OrderBy(t => t.Tag))
            { w.Write((ushort)t.Tag); w.Write((ushort)t.Type); w.Write(t.Count); w.Write(t.Value); }
            w.Write(0);
        }
        Directory(8, [ (254,4,1,1), (256,4,1,1), (257,4,1,1), (258,3,1,8),
            (259,3,1,1), (262,3,1,2), (271,2,make.Length+1,800), (277,3,1,3),
            (273,4,1,1000), (278,4,1,1), (279,4,1,3), (305,2,software.Length+1,820),
            (330,4,1,512), (50706,1,4,257) ]);
        Directory(512, [ (254,4,1,0), (256,4,1,2), (257,4,1,2), (258,3,samples,900),
            (259,3,1,1), (262,3,1,cfa ? 32803 : photometric), (277,3,1,samples),
            (273,4,1,1024), (278,4,1,2), (279,4,1,4*samples*2), (284,3,1,1) ]);
        stream.Position = 800; w.Write(System.Text.Encoding.ASCII.GetBytes(make + "\0"));
        stream.Position = 820; w.Write(System.Text.Encoding.ASCII.GetBytes(software + "\0"));
        stream.Position = 900; for (int i=0;i<samples;i++) w.Write((ushort)bits);
        stream.Position = 1000; w.Write(new byte[] { 255, 0, 255 });
        stream.Position = 1024;
        for (int i=0;i<4;i++)
        { w.Write((ushort)10000); w.Write((ushort)20000); w.Write((ushort)30000); if (samples == 4) w.Write((ushort)(i*20000)); }
        return path;
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void Full_scan_not_preview_and_infrared_does_not_change_rgb(int samples)
    {
        string path = Fixture(samples: samples);
        try
        {
            Assert.True(ScannerDng.IsScannerDng(path));
            Assert.False(RawDecode.IsRawExtension(path));
            Assert.True(ImportDialog.IsScan(path));
            Assert.Equal((2,2), TiffIO.ReadTiffSize(path));
            Assert.Equal(samples == 4, InfraredDust.HasSupportedInfrared(path));
            if (samples == 4)
                Assert.Equal((2, 2), (InfraredDust.Detect(path).Width, InfraredDust.Detect(path).Height));
            else
                Assert.Throws<NotSupportedException>(() => InfraredDust.Detect(path));
            var image = TiffIO.LoadTiff(path, false);
            for (int i=0;i<4;i++)
                for (int c=0;c<3;c++) Assert.Equal((c+1)*10000/65535f, image.Data[i*3+c], 5);
            using var engine = new LittleCmsEngine();
            var working = TiffIO.LoadWorkingFrame(path, TiffInputAssumption.Unspecified,
                ColorPipelineVersion.ManagedV2, engine);
            Assert.Equal(image.Data, working.Pixels.Data);
            Assert.IsType<UncharacterizedPixelEncoding>(working.Source.OriginalEncoding);
            Assert.Contains("ScannerLinearDng", working.Source.DecodeRecipe);
            Assert.Equal(TiffInputAssumption.Linear, TiffInputDetector.Detect(path).Assumption);
            Assert.Equal(image.Data, TiffIO.LoadTiffPreviews(path, false, [2])[0].Data);
            var roi = TiffIO.LoadTiffRegionExact(path, false, (1,1,1,1));
            Assert.Equal(image.Data.Take(3), roi.Data);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Camera_dng_with_rgb_preview_stays_on_raw_path()
    {
        string path = Fixture(software: "Adobe DNG Converter", cfa: true, samples: 3);
        try { Assert.True(RawDecode.IsRawExtension(path)); Assert.False(ImportDialog.IsScan(path));
            Assert.False(InfraredDust.HasSupportedInfrared(path)); }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("VueScan 9.8.16", "Nikon", 34892, true)]
    [InlineData("VueScan 9.8.16", "Plustek", 34892, true)]
    [InlineData("SilverFast 9", "Reflecta", 34892, false)]
    [InlineData("SilverFast 9", "Reflecta", 2, false)]
    [InlineData("Film digitizer", "Custom", 34892, false)]
    public void Compatible_scans_are_not_limited_to_epson_or_vuescan(
        string software, string make, int photometric, bool infrared)
    {
        string path = Fixture(software: software, make: make, photometric: photometric);
        try
        {
            Assert.True(ScannerDng.IsScannerDng(path));
            Assert.False(RawDecode.IsRawExtension(path));
            Assert.Equal((2, 2), TiffIO.ReadTiffSize(path));
            Assert.Equal(10000 / 65535f, TiffIO.LoadTiff(path, false).Data[0], 5);
            Assert.Equal(infrared, InfraredDust.HasSupportedInfrared(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Full_scan_in_main_ifd_chain_is_selected_instead_of_preview()
    {
        string path = Fixture(software: "Film digitizer", make: "Custom", samples: 3);
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            int count = BitConverter.ToUInt16(bytes, 8);
            int offset = Enumerable.Range(0, count).Select(i => 10 + i * 12)
                .Single(i => BitConverter.ToUInt16(bytes, i) == 330);
            BitConverter.GetBytes((ushort)65000).CopyTo(bytes, offset);
            BitConverter.GetBytes(512).CopyTo(bytes, 10 + count * 12);
            File.WriteAllBytes(path, bytes);
            Assert.True(ScannerDng.IsScannerDng(path));
            Assert.Equal((2, 2), TiffIO.ReadTiffSize(path));
            Assert.Equal(10000 / 65535f, TiffIO.LoadTiff(path, false).Data[0], 5);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Generic_camera_linear_dng_with_colour_matrix_stays_raw()
    {
        string path = Fixture(software: "Adobe DNG Converter", samples: 3);
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            int count = BitConverter.ToUInt16(bytes, 8);
            int offset = Enumerable.Range(0, count).Select(i => 10 + i * 12)
                .Single(i => BitConverter.ToUInt16(bytes, i) == 278);
            BitConverter.GetBytes((ushort)50721).CopyTo(bytes, offset);
            BitConverter.GetBytes((ushort)10).CopyTo(bytes, offset + 2);
            BitConverter.GetBytes(9).CopyTo(bytes, offset + 4);
            BitConverter.GetBytes(1100).CopyTo(bytes, offset + 8);
            Array.Resize(ref bytes, 1172);
            for (int i = 0; i < 9; i++)
            {
                BitConverter.GetBytes(i % 4 == 0 ? 1 : 0).CopyTo(bytes, 1100 + i * 8);
                BitConverter.GetBytes(1).CopyTo(bytes, 1104 + i * 8);
            }
            File.WriteAllBytes(path, bytes);
            Assert.False(ScannerDng.IsScannerDng(path));
            Assert.True(RawDecode.IsRawExtension(path));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(8, false)]
    [InlineData(16, true)]
    public void Unsupported_scan_never_decodes_preview(int bits, bool cfa)
    {
        string path = Fixture(bits: bits, cfa: cfa);
        try { Assert.Throws<NotSupportedException>(() => TiffIO.LoadTiff(path, false));
            Assert.False(InfraredDust.HasSupportedInfrared(path)); }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(8)]
    [InlineData(512)]
    public void Correction_tags_in_parent_or_image_are_not_silently_ignored(int ifd)
    {
        string path = Fixture();
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            int count = BitConverter.ToUInt16(bytes, ifd);
            // Replace a nonessential tag with BlackLevel, which this first implementation
            // deliberately refuses even when the declared level happens to be zero.
            int target = Enumerable.Range(0, count).Select(i => ifd + 2 + i * 12)
                .Single(offset => BitConverter.ToUInt16(bytes, offset) == (ifd == 8 ? 278 : 284));
            BitConverter.GetBytes((ushort)50714).CopyTo(bytes, target);
            File.WriteAllBytes(path, bytes);
            Assert.Throws<NotSupportedException>(() => TiffIO.LoadTiff(path, false));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Public_gt_x980_sample_when_supplied_matches_full_scan()
    {
        // Keep the user's public photo out of the repository; opt in for local integration.
        string? path = Environment.GetEnvironmentVariable("OPENREVELARE_SCANNER_DNG_SAMPLE");
        if (string.IsNullOrEmpty(path)) return;
        Assert.False(RawDecode.IsRawExtension(path));
        Assert.Equal((1032,1056), TiffIO.ReadTiffSize(path));
        var image = TiffIO.LoadTiff(path, false);
        Assert.Equal(1032*1056*3, image.Data.Length);
        Assert.All(image.Data, value => Assert.InRange(value, 0, 1));
        int[] expected = [31121, 11803, 3387];
        for (int c = 0; c < 3; c++)
            Assert.Equal(expected[c]/65535f, image.Data[(528*1032+516)*3+c], 5);
    }
}
