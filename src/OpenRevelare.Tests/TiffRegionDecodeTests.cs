using BitMiracle.LibTiff.Classic;
using OpenRevelare.ColorManagement;
using OpenRevelare.Core;
using System.Text;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// <see cref="TiffIO.LoadWorkingRegion"/> must be <see cref="TiffIO.LoadWorkingFrame(string, TiffInputAssumption, ColorPipelineVersion, IColorManagementEngine)"/>
/// followed by <see cref="Geometry.ApplyCrop"/> and <see cref="Resample.Box"/> — the same floats
/// bit for bit, the same descriptor — on every route the whole-frame decode can take.
///
/// The identity is load-bearing, not cosmetic. Every Stage-1 measurement (t_base, wb_high,
/// D-max, the endpoints) is taken on the preview, and a split scan's cells are previews of
/// windows; if the window decode landed one pixel over or summed in a different order, the
/// roll's numbers would depend on which decoder happened to produce the buffer. The window
/// decode exists so that a whole Flextight strip (127–190 MP, 1.5–2.3 GB as float) never has
/// to be decoded in full to preview one cell of it — the shape that put 8 GB machines into
/// swap and dropped cells from the roll-wide vote.
///
/// Odd dimensions and a box factor above one are used throughout, because the trailing rows
/// and columns the integer box discards are exactly where an off-by-one would hide.
/// </summary>
public sealed class TiffRegionDecodeTests
{
    private const int Width = 37, Height = 23;

    private static readonly (double X, double Y, double W, double H)[] Rects =
    {
        (0, 0, 1, 1),
        (0, 0, 1, 0.5035273368606702),
        (0, 0.5035273368606702, 1, 0.4964726631393298),
        (0.1234, 0.2345, 0.5, 0.3),
        (0.77, 0.05, 0.23, 0.9),
    };

    private static readonly int[] MaxEdges = { 0, 5, 9, 100 };

    public static IEnumerable<object[]> ManagedRoutes()
    {
        foreach (int bps in new[] { 8, 16 })
        {
            yield return new object[] { $"untagged {bps}-bit, detected", bps, false, false, TiffInputAssumption.Unspecified };
            yield return new object[] { $"untagged {bps}-bit, explicit linear", bps, false, false, TiffInputAssumption.Linear };
            yield return new object[] { $"untagged {bps}-bit, explicit sRGB", bps, false, false, TiffInputAssumption.Srgb };
            yield return new object[] { $"untagged {bps}-bit, legacy by bit depth", bps, false, false, TiffInputAssumption.LegacyByBitDepthCompatibility };
            yield return new object[] { $"embedded ICC {bps}-bit", bps, true, false, TiffInputAssumption.Unspecified };
        }
        yield return new object[] { "IEEE float, detected", 32, false, false, TiffInputAssumption.Unspecified };
        yield return new object[] { "IEEE float, embedded ICC", 32, true, false, TiffInputAssumption.Unspecified };
        yield return new object[] { "Flextight vendor gamma", 16, false, true, TiffInputAssumption.Unspecified };
        yield return new object[] { "Flextight vendor gamma, explicit linear", 16, false, true, TiffInputAssumption.Linear };
    }

    [Theory]
    [MemberData(nameof(ManagedRoutes))]
    public void Managed_v2_window_decode_equals_whole_decode_cropped_and_boxed(
        string route,
        int bitsPerSample,
        bool embedIcc,
        bool flextight,
        TiffInputAssumption assumption)
    {
        string path = flextight ? WriteFlextightTiff() : WriteTiff(bitsPerSample, embedIcc);
        try
        {
            using var engine = new LittleCmsEngine();
            WorkingFrame full = TiffIO.LoadWorkingFrame(
                path, assumption, ColorPipelineVersion.ManagedV2, engine);
            AssertWindowsMatch(route, full,
                (rect, maxEdge) => TiffIO.LoadWorkingRegion(
                    path, rect, maxEdge, assumption, ColorPipelineVersion.ManagedV2, engine));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(8, false)]
    [InlineData(16, false)]
    [InlineData(16, true)]
    [InlineData(32, false)]
    public void Legacy_v1_window_decode_equals_whole_decode_cropped_and_boxed(int bitsPerSample, bool srgb)
    {
        string path = WriteTiff(bitsPerSample, embedIcc: false);
        try
        {
            TiffInputAssumption assumption = srgb ? TiffInputAssumption.Srgb : TiffInputAssumption.Unspecified;
            using var engine = new LittleCmsEngine();
            WorkingFrame full = TiffIO.LoadWorkingFrame(
                path, assumption, ColorPipelineVersion.LegacyV1, engine);
            AssertWindowsMatch($"legacy v1 {bitsPerSample}-bit srgb={srgb}", full,
                (rect, maxEdge) => TiffIO.LoadWorkingRegion(
                    path, rect, maxEdge, assumption, ColorPipelineVersion.LegacyV1, engine));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A window decode of the whole image at full resolution IS the whole decode.</summary>
    [Fact]
    public void Whole_window_at_full_resolution_is_the_whole_frame()
    {
        string path = WriteTiff(16, embedIcc: true);
        try
        {
            using var engine = new LittleCmsEngine();
            WorkingFrame full = TiffIO.LoadWorkingFrame(
                path, TiffInputAssumption.Unspecified, ColorPipelineVersion.ManagedV2, engine);
            WorkingFrame window = TiffIO.LoadWorkingRegion(
                path, (0, 0, 1, 1), 0, TiffInputAssumption.Unspecified, ColorPipelineVersion.ManagedV2, engine);
            Assert.Equal(full.Pixels.Width, window.Pixels.Width);
            Assert.Equal(full.Pixels.Height, window.Pixels.Height);
            AssertFloatBitsEqual(full.Pixels.Data, window.Pixels.Data);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Several sizes off one pass are the same frames as one size per pass.</summary>
    [Theory]
    [InlineData(16, true)]
    [InlineData(16, false)]
    [InlineData(8, false)]
    public void Several_edges_in_one_pass_match_one_edge_per_pass(int bitsPerSample, bool embedIcc)
    {
        string path = WriteTiff(bitsPerSample, embedIcc);
        try
        {
            using var engine = new LittleCmsEngine();
            int[] edges = { 9, 0, 5, 100 };
            foreach (var rect in Rects)
            {
                WorkingFrame[] together = TiffIO.LoadWorkingRegions(
                    path, rect, edges, TiffInputAssumption.Unspecified, ColorPipelineVersion.ManagedV2, engine);
                Assert.Equal(edges.Length, together.Length);
                for (int k = 0; k < edges.Length; k++)
                {
                    WorkingFrame alone = TiffIO.LoadWorkingRegion(
                        path, rect, edges[k], TiffInputAssumption.Unspecified, ColorPipelineVersion.ManagedV2, engine);
                    Assert.Equal(alone.Pixels.Width, together[k].Pixels.Width);
                    Assert.Equal(alone.Pixels.Height, together[k].Pixels.Height);
                    AssertFloatBitsEqual(alone.Pixels.Data, together[k].Pixels.Data, $"rect {rect}, edge {edges[k]}");
                    Assert.Equal(alone.Pixels.SourceQuantisationStep, together[k].Pixels.SourceQuantisationStep);
                    Assert.Equal(alone.Source.DecodeRecipe, together[k].Source.DecodeRecipe);
                }
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The sharp patch asks in PIXELS. Every pixel rectangle of the image must come back as
    /// exactly those pixels — the normalised round trip through <see cref="TiffIO.RegionRequest"/>
    /// may not land a column over anywhere, including at the far edge.
    /// </summary>
    [Fact]
    public void Pixel_rectangles_come_back_exact()
    {
        string path = WriteTiff(16, embedIcc: false);
        try
        {
            using var engine = new LittleCmsEngine();
            WorkingFrame full = TiffIO.LoadWorkingFrame(
                path, TiffInputAssumption.Unspecified, ColorPipelineVersion.ManagedV2, engine);
            for (int x = 0; x < Width; x += 5)
            for (int y = 0; y < Height; y += 4)
            for (int w = 1; x + w <= Width; w += 7)
            for (int h = 1; y + h <= Height; h += 5)
            {
                var slice = OpenRevelare.Gui.Services.ImageIo.LoadWorkingRegion(
                    path, x, y, w, h, Width, Height,
                    ColorPipelineVersion.ManagedV2, engine, TiffInputAssumption.Unspecified);
                Assert.NotNull(slice);
                var (working, x0, y0) = slice.Value;
                Assert.Equal((x, y), (x0, y0));
                Assert.Equal((w, h), (working.Pixels.Width, working.Pixels.Height));
                var expected = new float[w * h * 3];
                for (int row = 0; row < h; row++)
                    Array.Copy(full.Pixels.Data, ((y + row) * Width + x) * 3, expected, row * w * 3, w * 3);
                AssertFloatBitsEqual(expected, working.Pixels.Data, $"rect {x},{y} {w}×{h}");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The Path A calibration mean off the ROI alone is the mean off the whole frame, bit for
    /// bit — same pixels, same float32 order — on both the frozen and the typed route.
    /// </summary>
    [Theory]
    [InlineData(8, false)]
    [InlineData(16, true)]
    public void Roi_mean_off_the_region_equals_the_whole_frame(int bitsPerSample, bool embedIcc)
    {
        string path = WriteTiff(bitsPerSample, embedIcc);
        try
        {
            using var engine = new LittleCmsEngine();
            double[] wholeLegacy = DecoupleCalibration.RoiMean(TiffIO.LoadTiff(path, inputIsSrgb: false));
            double[] regionLegacy = OpenRevelare.Gui.Services.ImageIo.RoiMeanFull(path);
            Assert.Equal(wholeLegacy, regionLegacy);

            double[] wholeTyped = DecoupleCalibration.RoiMean(TiffIO.LoadWorkingFrame(
                path, TiffInputAssumption.Unspecified, ColorPipelineVersion.ManagedV2, engine).Pixels);
            double[] regionTyped = OpenRevelare.Gui.Services.ImageIo.RoiMeanFull(
                path, ColorPipelineVersion.ManagedV2, engine, TiffInputAssumption.Unspecified);
            Assert.Equal(wholeTyped, regionTyped);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A tall image is read in parallel bands (one LibTiff handle and, on the ICC route, one
    /// LittleCMS lease per band); the bands must reproduce the sequential <see cref="Resample.Box"/>
    /// sum bit for bit. Small factors keep the band unit small enough for several bands on any
    /// core count; uncompressed, LZW in small strips (parallel), and LZW in one strip (sequential
    /// by policy) all have to agree.
    /// </summary>
    [Theory]
    [InlineData(16, true, false, 8)]     // ICC, uncompressed
    [InlineData(16, false, false, 8)]    // untagged, uncompressed
    [InlineData(8, true, true, 8)]       // ICC, LZW in 8-row strips → parallel
    [InlineData(16, false, true, 1500)]  // untagged, LZW in one strip → sequential
    public void Parallel_bands_match_the_sequential_box(int bitsPerSample, bool embedIcc, bool lzw, int rowsPerStrip)
    {
        const int tallWidth = 61, tallHeight = 1500;
        string path = WriteTiff(
            bitsPerSample, embedIcc, tallWidth, tallHeight, lzw ? Compression.LZW : Compression.NONE, rowsPerStrip);
        try
        {
            using var engine = new LittleCmsEngine();
            WorkingFrame full = TiffIO.LoadWorkingFrame(
                path, TiffInputAssumption.Unspecified, ColorPipelineVersion.ManagedV2, engine);
            var rects = new (double X, double Y, double W, double H)[]
            {
                (0, 0, 1, 1), (0.1, 0.23, 0.7, 0.6), (0.3, 0.71, 0.5, 0.29),
            };
            foreach (var rect in rects)
            foreach (int maxEdge in new[] { 0, 400, 130, 50 })
            {
                ImageBuffer expected = Geometry.ApplyCrop(full.Pixels, rect);
                if (maxEdge > 0) expected = Resample.Box(expected, maxEdge);
                WorkingFrame window = TiffIO.LoadWorkingRegion(
                    path, rect, maxEdge, TiffInputAssumption.Unspecified, ColorPipelineVersion.ManagedV2, engine);
                Assert.Equal((expected.Width, expected.Height), (window.Pixels.Width, window.Pixels.Height));
                AssertFloatBitsEqual(expected.Data, window.Pixels.Data, $"rect {rect}, maxEdge {maxEdge}");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The Flextight route (v1 gamma LUT, one stateless stage shared by all bands).</summary>
    [Fact]
    public void Parallel_bands_on_the_vendor_gamma_route_match()
    {
        string path = WriteFlextightTiff(61, 1500);
        try
        {
            using var engine = new LittleCmsEngine();
            WorkingFrame full = TiffIO.LoadWorkingFrame(
                path, TiffInputAssumption.Unspecified, ColorPipelineVersion.ManagedV2, engine);
            Assert.Contains("vendor gamma", full.Source.DecodeRecipe, StringComparison.Ordinal);
            foreach (int maxEdge in new[] { 0, 130 })
            {
                ImageBuffer expected = maxEdge > 0 ? Resample.Box(full.Pixels, maxEdge) : full.Pixels;
                WorkingFrame window = TiffIO.LoadWorkingRegion(
                    path, (0, 0, 1, 1), maxEdge, TiffInputAssumption.Unspecified, ColorPipelineVersion.ManagedV2, engine);
                AssertFloatBitsEqual(expected.Data, window.Pixels.Data, $"maxEdge {maxEdge}");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Preview precision on a route that runs a CMM transform: the recipe says so, the pixels
    /// agree with the exact decode to the documented tolerance, and the value is a real one —
    /// not the exact decode relabelled. On the FlexColor exports that motivated it the
    /// difference measured 3e-4 at most in linear light; 1e-3 is the contract.
    /// </summary>
    [Theory]
    [InlineData(16, true, TiffInputAssumption.Unspecified)]   // embedded ICC
    [InlineData(8, true, TiffInputAssumption.Unspecified)]
    [InlineData(16, false, TiffInputAssumption.Srgb)]         // explicit built-in sRGB
    [InlineData(16, false, TiffInputAssumption.Unspecified)]  // detected → sRGB convention
    public void Preview_precision_is_close_to_exact_and_says_so(int bitsPerSample, bool embedIcc, TiffInputAssumption assumption)
    {
        string path = WriteTiff(bitsPerSample, embedIcc, 61, 300, Compression.NONE, 8);
        try
        {
            using var engine = new LittleCmsEngine();
            foreach (int maxEdge in new[] { 0, 40 })
            {
                WorkingFrame exact = TiffIO.LoadWorkingRegion(
                    path, (0, 0, 1, 1), maxEdge, assumption, ColorPipelineVersion.ManagedV2, engine, TransformPrecision.Exact);
                WorkingFrame preview = TiffIO.LoadWorkingRegion(
                    path, (0, 0, 1, 1), maxEdge, assumption, ColorPipelineVersion.ManagedV2, engine, TransformPrecision.Preview);

                Assert.Equal(exact.Source.DecodeRecipe + " [preview precision: 16-bit optimised transform]", preview.Source.DecodeRecipe);
                Assert.DoesNotContain("preview precision", exact.Source.DecodeRecipe, StringComparison.Ordinal);
                Assert.Equal(exact.Admission, preview.Admission);
                Assert.Equal((exact.Pixels.Width, exact.Pixels.Height), (preview.Pixels.Width, preview.Pixels.Height));

                float[] a = exact.Pixels.Data, b = preview.Pixels.Data;
                float maxAbs = 0f;
                for (int i = 0; i < a.Length; i++) maxAbs = Math.Max(maxAbs, Math.Abs(a[i] - b[i]));
                Assert.True(maxAbs <= 1e-3f, $"preview precision drifted {maxAbs:E2} from exact at maxEdge {maxEdge}");
                Assert.True(maxAbs > 0f, "preview precision produced the exact decode — the fast transform was not used");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A route with no CMM transform has nothing to trade: precision must not change a bit.</summary>
    [Theory]
    [InlineData(true, TiffInputAssumption.Unspecified)]   // Flextight vendor gamma (v1 LUT)
    [InlineData(false, TiffInputAssumption.Linear)]       // explicit linear passthrough
    public void Preview_precision_leaves_transformless_routes_untouched(bool flextight, TiffInputAssumption assumption)
    {
        string path = flextight ? WriteFlextightTiff() : WriteTiff(16, embedIcc: false);
        try
        {
            using var engine = new LittleCmsEngine();
            WorkingFrame exact = TiffIO.LoadWorkingRegion(
                path, (0, 0, 1, 1), 9, assumption, ColorPipelineVersion.ManagedV2, engine, TransformPrecision.Exact);
            WorkingFrame preview = TiffIO.LoadWorkingRegion(
                path, (0, 0, 1, 1), 9, assumption, ColorPipelineVersion.ManagedV2, engine, TransformPrecision.Preview);
            Assert.Equal(exact.Source.DecodeRecipe, preview.Source.DecodeRecipe);
            AssertFloatBitsEqual(exact.Pixels.Data, preview.Pixels.Data);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>An empty window is refused the way <see cref="Geometry.ApplyCrop"/> refuses it.</summary>
    [Fact]
    public void Empty_window_is_rejected()
    {
        string path = WriteTiff(8, embedIcc: false);
        try
        {
            using var engine = new LittleCmsEngine();
            Assert.Throws<ArgumentException>(() => TiffIO.LoadWorkingRegion(
                path, (0.5, 0.5, 0.0, 0.0), 0,
                TiffInputAssumption.Unspecified, ColorPipelineVersion.ManagedV2, engine));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void AssertWindowsMatch(
        string route,
        WorkingFrame full,
        Func<(double X, double Y, double W, double H), int, WorkingFrame> decodeWindow)
    {
        foreach (var rect in Rects)
        foreach (int maxEdge in MaxEdges)
        {
            ImageBuffer expected = Geometry.ApplyCrop(full.Pixels, rect);
            if (maxEdge > 0) expected = Resample.Box(expected, maxEdge);

            WorkingFrame window = decodeWindow(rect, maxEdge);
            string where = $"{route}, rect {rect}, maxEdge {maxEdge}";

            Assert.True(expected.Width == window.Pixels.Width && expected.Height == window.Pixels.Height,
                $"{where}: expected {expected.Width}×{expected.Height}, got {window.Pixels.Width}×{window.Pixels.Height}");
            AssertFloatBitsEqual(expected.Data, window.Pixels.Data, where);

            Assert.Equal(expected.SourceQuantisationStep, window.Pixels.SourceQuantisationStep);
            Assert.Equal(full.Space, window.Space);
            Assert.Equal(full.Admission, window.Admission);
            Assert.Equal(full.Source.StableSourceId, window.Source.StableSourceId);
            Assert.Equal(full.Source.DecodeRecipe, window.Source.DecodeRecipe);
            // An embedded profile is a fresh ColorProfileRef per decode — reference identity —
            // so the encodings are compared by what they say, identity hash included.
            Assert.Equal(full.Source.OriginalEncoding.ToString(), window.Source.OriginalEncoding.ToString());
        }
    }

    private static void AssertFloatBitsEqual(float[] expected, float[] actual, string where = "")
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(
                BitConverter.SingleToInt32Bits(expected[i]) == BitConverter.SingleToInt32Bits(actual[i]),
                $"{where}: float {i} differs: expected={expected[i]:R}, actual={actual[i]:R}");
        }
    }

    /// <summary>
    /// A <see cref="Width"/>×<see cref="Height"/> RGB TIFF with every pixel distinct (a
    /// deterministic hash of its position), so a window landing one pixel off cannot match.
    /// </summary>
    private static string WriteTiff(int bitsPerSample, bool embedIcc)
        => WriteTiff(bitsPerSample, embedIcc, Width, Height, Compression.NONE, rowsPerStrip: 7);

    private static string WriteTiff(
        int bitsPerSample, bool embedIcc, int width, int height, Compression compression, int rowsPerStrip)
    {
        string path = Path.Combine(Path.GetTempPath(), $"openrevelare-region-{Guid.NewGuid():N}.tif");
        using Tiff tif = Tiff.Open(path, "w")
            ?? throw new IOException($"could not create test TIFF: {path}");
        tif.SetField(TiffTag.IMAGEWIDTH, width);
        tif.SetField(TiffTag.IMAGELENGTH, height);
        tif.SetField(TiffTag.SAMPLESPERPIXEL, 3);
        tif.SetField(TiffTag.BITSPERSAMPLE, bitsPerSample);
        tif.SetField(TiffTag.ORIENTATION, Orientation.TOPLEFT);
        tif.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
        tif.SetField(TiffTag.PHOTOMETRIC, Photometric.RGB);
        tif.SetField(TiffTag.COMPRESSION, compression);
        tif.SetField(TiffTag.ROWSPERSTRIP, rowsPerStrip);
        if (bitsPerSample == 32) tif.SetField(TiffTag.SAMPLEFORMAT, SampleFormat.IEEEFP);
        if (embedIcc)
        {
            byte[] icc = IccProfiles.Build(ColorSpace.AdobeRgb);
            tif.SetField(TiffTag.ICCPROFILE, icc.Length, icc);
        }

        byte[] row = new byte[width * 3 * (bitsPerSample / 8)];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            for (int c = 0; c < 3; c++)
            {
                // Spread over the full range, never uniform along a row or a column.
                uint hash = (uint)(x * 2654435761u + y * 40503u + c * 97u);
                hash ^= hash >> 13; hash *= 0x5bd1e995u; hash ^= hash >> 15;
                int s = x * 3 + c;
                switch (bitsPerSample)
                {
                    case 8:
                        row[s] = (byte)hash;
                        break;
                    case 16:
                        row[s * 2] = (byte)hash;
                        row[s * 2 + 1] = (byte)(hash >> 8);
                        break;
                    default:
                        float value = (hash & 0xFFFF) / 65535.0f * 1.5f - 0.25f;   // extended range
                        BitConverter.GetBytes(value).CopyTo(row, s * 4);
                        break;
                }
            }
            Assert.True(tif.WriteScanline(row, y));
        }
        return path;
    }

    /// <summary>
    /// A Flextight-shaped 16-bit TIFF: no ICC, the settings plist in private tag 50457 declaring
    /// gamma 2.0. Written by hand because LibTiff will not emit an unregistered private tag.
    /// </summary>
    private static string WriteFlextightTiff() => WriteFlextightTiff(Width, Height);

    private static string WriteFlextightTiff(int width, int height)
    {
        byte[] plist = Encoding.UTF8.GetBytes("""
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0"><dict>
              <key>CurrentIx</key><integer>0</integer>
              <key>ImageSettings</key><array><dict>
                <key>ImageCorrection</key><dict><key>Gamma</key><real>2.0</real></dict>
                <key>Name</key><string>Negative RGB standard</string>
              </dict></array>
            </dict></plist>
            """);

        const ushort entryCount = 11;
        const uint ifdOffset = 8;
        uint bitsOffset = ifdOffset + 2 + entryCount * 12 + 4;
        uint plistOffset = bitsOffset + 6;
        uint stripOffset = plistOffset + checked((uint)plist.Length);
        uint stripBytes = checked((uint)(width * height * 3 * 2));

        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((byte)'I');
            writer.Write((byte)'I');
            writer.Write((ushort)42);
            writer.Write(ifdOffset);
            writer.Write(entryCount);

            void Entry(ushort tag, ushort type, uint count, uint value)
            {
                writer.Write(tag);
                writer.Write(type);
                writer.Write(count);
                writer.Write(value);
            }

            Entry(256, 4, 1, (uint)width);              // ImageWidth
            Entry(257, 4, 1, (uint)height);             // ImageLength
            Entry(258, 3, 3, bitsOffset);               // BitsPerSample (16,16,16) out of line
            Entry(259, 3, 1, 1);                        // Compression none
            Entry(262, 3, 1, 2);                        // Photometric RGB
            Entry(273, 4, 1, stripOffset);              // StripOffsets
            Entry(277, 3, 1, 3);                        // SamplesPerPixel
            Entry(278, 4, 1, (uint)height);             // RowsPerStrip
            Entry(279, 4, 1, stripBytes);               // StripByteCounts
            Entry(284, 3, 1, 1);                        // PlanarConfig contig
            Entry(50457, 1, checked((uint)plist.Length), plistOffset);   // Flextight settings
            writer.Write(0u);                           // next IFD

            writer.Write((ushort)16); writer.Write((ushort)16); writer.Write((ushort)16);
            writer.Write(plist);
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            for (int c = 0; c < 3; c++)
            {
                uint hash = (uint)(x * 2654435761u + y * 40503u + c * 97u);
                hash ^= hash >> 13; hash *= 0x5bd1e995u; hash ^= hash >> 15;
                writer.Write((ushort)hash);
            }
        }

        string path = Path.Combine(Path.GetTempPath(), $"openrevelare-region-flextight-{Guid.NewGuid():N}.fff");
        File.WriteAllBytes(path, bytes.ToArray());
        return path;
    }
}
