using BitMiracle.LibTiff.Classic;
using OpenRevelare.Core;
using System.Text;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// One test per evidence tier, in the order the detector trusts them. The point of each is not
/// that some enum comes back, but that a file which declared its colour gets read rather than
/// guessed at — that is the whole reason the upfront Linear/sRGB question could be removed.
/// </summary>
public sealed class TiffInputDetectorTests
{
    [Fact]
    public void Float_samples_are_scene_linear_without_asking()
    {
        string path = WriteTiff(configure: tif =>
        {
            tif.SetField(TiffTag.BITSPERSAMPLE, 32);
            tif.SetField(TiffTag.SAMPLEFORMAT, SampleFormat.IEEEFP);
        },
        row: FloatRow(0.18f, 0.42f, 0.73f));
        try
        {
            TiffInputDetection detection = TiffInputDetector.Detect(path);

            Assert.Equal(TiffInputEvidence.FloatSampleFormat, detection.Evidence);
            Assert.Equal(TiffInputAssumption.Linear, detection.Assumption);
            Assert.Null(detection.CharacterizedSpace);
            Assert.True(detection.IsConclusive);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Baseline_chromaticity_tags_produce_an_exact_space_not_a_two_way_guess()
    {
        // Adobe RGB primaries with D65, which is precisely the case a Linear/sRGB question cannot
        // express: the right answer is neither of the two options it offers.
        string path = WriteTiff(configure: tif =>
        {
            tif.SetField(TiffTag.WHITEPOINT, new float[] { 0.3127f, 0.3290f });
            tif.SetField(TiffTag.PRIMARYCHROMATICITIES, new float[]
            {
                0.6400f, 0.3300f,
                0.2100f, 0.7100f,
                0.1500f, 0.0600f,
            });
        });
        try
        {
            TiffInputDetection detection = TiffInputDetector.Detect(path);

            Assert.Equal(TiffInputEvidence.BaselineChromaticity, detection.Evidence);
            ColorSpaceDef space = Assert.NotNull(detection.CharacterizedSpace);
            Assert.Equal(0.6400, space.Red.X, 4);
            Assert.Equal(0.7100, space.Green.Y, 4);
            Assert.Equal(0.3127, space.White.X, 4);
            // No TransferFunction present, so TIFF 6.0's stated default applies.
            Assert.Equal(TransferFunction.Power, space.Transfer);
            Assert.Equal(2.2, space.Gamma, 4);
            Assert.Contains("TIFF 6.0", detection.Diagnostic, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_transfer_function_table_is_matched_rather_than_assumed()
    {
        string path = WriteTiff(configure: tif =>
        {
            tif.SetField(TiffTag.WHITEPOINT, new float[] { 0.3127f, 0.3290f });
            tif.SetField(TiffTag.PRIMARYCHROMATICITIES, new float[]
            {
                0.6400f, 0.3300f,
                0.3000f, 0.6000f,
                0.1500f, 0.0600f,
            });
            tif.SetField(TiffTag.TRANSFERFUNCTION, SrgbTransferTable());
        });
        try
        {
            TiffInputDetection detection = TiffInputDetector.Detect(path);

            ColorSpaceDef space = Assert.NotNull(detection.CharacterizedSpace);
            Assert.Equal(TransferFunction.SrgbPiecewise, space.Transfer);
            Assert.Contains("sRGB piecewise", detection.Diagnostic, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void An_unrecognized_transfer_table_keeps_the_exact_primaries_and_says_so()
    {
        // A table that is none of the candidate curves must not be forced into the nearest one.
        short[] table = new short[256 * 3];
        for (int channel = 0; channel < 3; channel++)
            for (int code = 0; code < 256; code++)
                table[channel * 256 + code] = unchecked((short)(ushort)(code % 2 == 0 ? 0 : 65535));

        string path = WriteTiff(configure: tif =>
        {
            tif.SetField(TiffTag.WHITEPOINT, new float[] { 0.3127f, 0.3290f });
            tif.SetField(TiffTag.PRIMARYCHROMATICITIES, new float[]
            {
                0.6400f, 0.3300f,
                0.3000f, 0.6000f,
                0.1500f, 0.0600f,
            });
            tif.SetField(TiffTag.TRANSFERFUNCTION, table, table, table);
        });
        try
        {
            TiffInputDetection detection = TiffInputDetector.Detect(path);

            ColorSpaceDef space = Assert.NotNull(detection.CharacterizedSpace);
            Assert.Equal(TransferFunction.Power, space.Transfer);
            Assert.Equal(2.2, space.Gamma, 4);
            Assert.Contains("无法匹配", detection.Diagnostic, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Degenerate_primaries_fall_through_instead_of_failing_the_load()
    {
        // Collinear primaries cannot produce a matrix. The detector must decline them quietly and
        // let a lower tier answer, rather than throwing on the decode path.
        string path = WriteTiff(configure: tif =>
        {
            tif.SetField(TiffTag.WHITEPOINT, new float[] { 0.3127f, 0.3290f });
            tif.SetField(TiffTag.PRIMARYCHROMATICITIES, new float[]
            {
                0.3000f, 0.3000f,
                0.3000f, 0.3000f,
                0.3000f, 0.3000f,
            });
            tif.SetField(TiffTag.SOFTWARE, "VueScan 9.8.11");
        });
        try
        {
            TiffInputDetection detection = TiffInputDetector.Detect(path);

            Assert.Equal(TiffInputEvidence.ScannerSoftware, detection.Evidence);
            Assert.Equal(TiffInputAssumption.Srgb, detection.Assumption);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("VueScan 9.8.11")]
    [InlineData("SilverFast 8")]
    [InlineData("EPSON Scan 2")]
    [InlineData("OpenRevelare")]
    public void A_known_writer_answers_for_its_own_untagged_output(string software)
    {
        string path = WriteTiff(configure: tif => tif.SetField(TiffTag.SOFTWARE, software));
        try
        {
            TiffInputDetection detection = TiffInputDetector.Detect(path);

            Assert.Equal(TiffInputEvidence.ScannerSoftware, detection.Evidence);
            Assert.Equal(TiffInputAssumption.Srgb, detection.Assumption);
            Assert.True(detection.IsConclusive);
            Assert.Contains(software, detection.Diagnostic, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void An_unknown_writer_is_not_treated_as_evidence()
    {
        string path = WriteTiff(configure: tif => tif.SetField(TiffTag.SOFTWARE, "SomeUnknownScanner 1.0"));
        try
        {
            TiffInputDetection detection = TiffInputDetector.Detect(path);

            Assert.Equal(TiffInputEvidence.ConventionalDefault, detection.Evidence);
            Assert.False(detection.IsConclusive);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_file_that_declares_nothing_gets_a_labelled_convention_not_a_refusal()
    {
        string path = WriteTiff();
        try
        {
            TiffInputDetection detection = TiffInputDetector.Detect(path);

            Assert.Equal(TiffInputEvidence.ConventionalDefault, detection.Evidence);
            Assert.Equal(TiffInputAssumption.Srgb, detection.Assumption);
            Assert.Null(detection.CharacterizedSpace);
            // The whole point of the flag: the UI has to be able to say this was not read out of
            // the file, and offer to change it.
            Assert.False(detection.IsConclusive);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_missing_file_degrades_to_the_convention_rather_than_throwing()
    {
        string path = Path.Combine(Path.GetTempPath(), $"openrevelare-absent-{Guid.NewGuid():N}.tif");

        TiffInputDetection detection = TiffInputDetector.Detect(path);

        Assert.Equal(TiffInputEvidence.ConventionalDefault, detection.Evidence);
    }

    [Theory]
    [InlineData(1, "sRGB")]
    [InlineData(2, "AdobeRGB")]
    public void Exif_colour_space_names_the_encoding(int exifColorSpace, string expectedSpaceName)
    {
        string path = WriteExifTiff((ushort)exifColorSpace);
        try
        {
            TiffInputDetection detection = TiffInputDetector.Detect(path);

            Assert.Equal(TiffInputEvidence.ExifColorSpace, detection.Evidence);
            ColorSpaceDef space = Assert.NotNull(detection.CharacterizedSpace);
            Assert.Equal(expectedSpaceName, space.Name);
            Assert.True(detection.IsConclusive);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Exif_uncalibrated_is_a_refusal_to_say_and_is_not_turned_into_a_space()
    {
        // 0xFFFF explicitly means "uncalibrated". Reading it as sRGB would invent a claim the file
        // deliberately declined to make.
        string path = WriteExifTiff(0xFFFF);
        try
        {
            TiffInputDetection detection = TiffInputDetector.Detect(path);

            Assert.Equal(TiffInputEvidence.ConventionalDefault, detection.Evidence);
            Assert.Null(detection.CharacterizedSpace);
            Assert.False(detection.IsConclusive);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Hand-built because LibTiff.NET 2.4.660 exposes no public way to write an Exif sub-IFD.
    /// Same approach as the Flextight fixture in ManagedUntaggedTiffAssumptionTests.
    /// </summary>
    private static string WriteExifTiff(ushort exifColorSpace)
    {
        const ushort entryCount = 11;
        const uint ifdOffset = 8;
        const uint bitsOffset = ifdOffset + 2 + entryCount * 12 + 4;
        const uint exifIfdOffset = bitsOffset + 6;
        const uint stripOffset = exifIfdOffset + 2 + 1 * 12 + 4;

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

            // Ascending tag order, as the format requires.
            Entry(256, 3, 1, 1);                 // width
            Entry(257, 3, 1, 1);                 // height
            Entry(258, 3, 3, bitsOffset);        // bits/sample array
            Entry(259, 3, 1, 1);                 // no compression
            Entry(262, 3, 1, 2);                 // RGB
            Entry(273, 4, 1, stripOffset);       // strip offset
            Entry(277, 3, 1, 3);                 // samples/pixel
            Entry(278, 4, 1, 1);                 // rows/strip
            Entry(279, 4, 1, 3);                 // strip byte count
            Entry(284, 3, 1, 1);                 // contiguous
            Entry(34665, 4, 1, exifIfdOffset);   // Exif IFD pointer
            writer.Write(0u);                    // no next IFD

            writer.Write((ushort)8);
            writer.Write((ushort)8);
            writer.Write((ushort)8);

            writer.Write((ushort)1);             // one Exif entry
            Entry(0xA001, 3, 1, exifColorSpace); // ColorSpace, SHORT in the low half of the field
            writer.Write(0u);                    // no next IFD

            writer.Write(new byte[] { 206, 138, 81 });
        }

        string path = Path.Combine(
            Path.GetTempPath(), $"openrevelare-detect-exif-{Guid.NewGuid():N}.tif");
        File.WriteAllBytes(path, bytes.ToArray());
        return path;
    }

    /// <summary>The sRGB curve sampled into a TIFF TransferFunction table: code -> linear light.</summary>
    private static short[] SrgbTransferTable()
    {
        var table = new short[256];
        for (int code = 0; code < 256; code++)
        {
            double encoded = code / 255.0;
            double linear = encoded <= 0.04045
                ? encoded / 12.92
                : Math.Pow((encoded + 0.055) / 1.055, 2.4);
            table[code] = unchecked((short)(ushort)Math.Round(linear * 65535.0));
        }
        return table;
    }

    private static byte[] FloatRow(params float[] samples)
    {
        var row = new byte[samples.Length * sizeof(float)];
        Buffer.BlockCopy(samples, 0, row, 0, row.Length);
        return row;
    }

    private static string WriteTiff(Action<Tiff>? configure = null, byte[]? row = null)
    {
        string path = Path.Combine(
            Path.GetTempPath(), $"openrevelare-detect-{Guid.NewGuid():N}.tif");
        using Tiff tif = Tiff.Open(path, "w")
            ?? throw new IOException($"could not create test TIFF: {path}");
        tif.SetField(TiffTag.IMAGEWIDTH, 1);
        tif.SetField(TiffTag.IMAGELENGTH, 1);
        tif.SetField(TiffTag.SAMPLESPERPIXEL, 3);
        tif.SetField(TiffTag.BITSPERSAMPLE, 8);
        tif.SetField(TiffTag.ORIENTATION, Orientation.TOPLEFT);
        tif.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
        tif.SetField(TiffTag.PHOTOMETRIC, Photometric.RGB);
        tif.SetField(TiffTag.COMPRESSION, Compression.NONE);
        tif.SetField(TiffTag.ROWSPERSTRIP, 1);
        configure?.Invoke(tif);
        Assert.True(tif.WriteScanline(row ?? new byte[] { 206, 138, 81 }, 0));
        return path;
    }
}
