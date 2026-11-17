using System.Text;
using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class ScannerStandardAssetsTests
{
    private const string Target = """
        IT8.7/1
        ORIGINATOR "Example target manufacturer"
        DESCRIPTOR "Transmission batch A"
        NUMBER_OF_FIELDS 4
        NUMBER_OF_SETS 2
        BEGIN_DATA_FORMAT
        SAMPLE_ID XYZ_X XYZ_Y XYZ_Z
        END_DATA_FORMAT
        BEGIN_DATA
        "A 1" 12.5 13.0 14.0 # comment
        A2 23 24 25
        END_DATA
        """;

    [Fact]
    public void It8_table_preserves_patch_ids_and_pcs_values_without_density_conversion()
    {
        var asset = ScannerStandardAssets.ParseCgats(Target);
        Assert.Equal("IT8/CGATS", asset.Format);
        Assert.True(asset.HasPcsReference); Assert.False(asset.HasDeviceRgb);
        Assert.Equal("A 1", asset.Rows[0][0]); Assert.Equal("12.5", asset.Rows[0][1]);
        Assert.Equal("Transmission batch A", asset.Metadata["DESCRIPTOR"]);
        Assert.Equal(CalibrationReferenceRole.TargetData, asset.Role);
    }

    [Fact]
    public void Argyll_input_pairs_are_recognized_but_printer_tables_are_rejected()
    {
        string ti3 = Target.Replace("IT8.7/1", "CTI3\nDEVICE_CLASS \"INPUT\"\nCOLOR_REP \"XYZ_RGB\"");
        Assert.Equal("Argyll CTI3", ScannerStandardAssets.ParseCgats(ti3).Format);
        Assert.Throws<InvalidDataException>(() => ScannerStandardAssets.ParseCgats(ti3.Replace("\"INPUT\"", "\"OUTPUT\"")));
    }

    [Fact]
    public void Malformed_counts_nonfinite_measurements_and_multiple_tables_are_rejected()
    {
        Assert.Throws<InvalidDataException>(() => ScannerStandardAssets.ParseCgats(Target.Replace("NUMBER_OF_SETS 2", "NUMBER_OF_SETS 3")));
        Assert.Throws<InvalidDataException>(() => ScannerStandardAssets.ParseCgats(Target.Replace("12.5", "NaN")));
        Assert.Throws<InvalidDataException>(() => ScannerStandardAssets.ParseCgats(Target + "\nBEGIN_DATA_FORMAT\nRGB_R\nEND_DATA_FORMAT"));
        Assert.Throws<InvalidDataException>(() => ScannerStandardAssets.ParseCgats(Target.Replace("END_DATA\n", "" ).Replace("END_DATA", "")));
    }

    [Fact]
    public void Icc_import_uses_native_validation_and_rejects_display_profiles()
    {
        string path = Path.Combine(TestDataIsolation.Root, "scanner-standard-" + Guid.NewGuid().ToString("N") + ".icc");
        try
        {
            byte[] bytes = BuiltInColorProfiles.Srgb().IccBytes.ToArray();
            File.WriteAllBytes(path, bytes);
            Assert.Throws<InvalidDataException>(() => ScannerStandardAssets.Read(path));
            Encoding.ASCII.GetBytes("scnr").CopyTo(bytes, 12);
            File.WriteAllBytes(path, bytes);
            var asset = ScannerStandardAssets.Read(path);
            Assert.Equal("ICC input RGB", asset.Format);
            Assert.Equal(CalibrationReferenceRole.InputIcc, asset.Role);
            Assert.Equal(64, asset.Sha256.Length);
            File.WriteAllBytes(path, new byte[40]);
            Assert.Throws<InvalidDataException>(() => ScannerStandardAssets.Read(path));
        }
        finally { File.Delete(path); }
    }
}
