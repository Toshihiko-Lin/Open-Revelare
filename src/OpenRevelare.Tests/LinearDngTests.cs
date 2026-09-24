using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// The linear DNG export. A writer nobody can read is worth nothing, so the central test does not
/// inspect our own tags — it hands the file to LibRaw, a decoder that had no part in writing it, and
/// checks that what comes back is the linear light that went in.
/// </summary>
public sealed class LinearDngTests
{
    /// <summary>
    /// Whether LibRaw's native library can be loaded here. Probed by handing it sixteen bytes of
    /// nothing: the library is initialised before it looks at them, so a missing library fails with
    /// DllNotFoundException while a present one fails complaining about the data.
    /// </summary>
    private static readonly Lazy<bool> LibRawPresent = new(() =>
    {
        try
        {
            using var ctx = Sdcb.LibRaw.RawContext.FromBuffer(new byte[16]);
            return true;
        }
        catch (DllNotFoundException) { return false; }
        catch { return true; }
    });

    /// <summary>
    /// For the tests that read the file back. CI's macOS job deliberately does not build LibRaw —
    /// that is release.yml's job, and building it costs minutes (see .github/workflows/ci.yml) — so
    /// there the decoder is absent and these three would report a missing dylib as a DNG bug. They
    /// still run on Windows, on Linux, and on a Mac that has the bundled dylib, which is what a
    /// "nobody can read it" regression would surface on.
    /// </summary>
    private sealed class LibRawFactAttribute : FactAttribute
    {
        public LibRawFactAttribute()
        {
            if (!LibRawPresent.Value) Skip = "LibRaw native library not present on this machine";
        }
    }

    /// <summary>A display-referred frame: sRGB-encoded, the way a finished render arrives here.</summary>
    private static ImageBuffer Encoded(int w, int h, Func<int, int, (float R, float G, float B)> f)
    {
        var img = new ImageBuffer(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (r, g, b) = f(x, y);
                int i = (y * w + x) * 3;
                img.Data[i] = r; img.Data[i + 1] = g; img.Data[i + 2] = b;
            }
        return img;
    }

    private static string NewPath() =>
        Path.Combine(TestDataIsolation.Root, $"linear-{Guid.NewGuid():N}.dng");

    [LibRawFact]
    public void Writes_a_file_libraw_can_decode()
    {
        string path = NewPath();
        ImageBuffer src = Encoded(64, 48, (x, y) => (x / 64f, y / 48f, 0.5f));

        LinearDng.Write(src, ColorSpaces.Srgb, path);
        ImageBuffer decoded = RawDecode.DecodeRaw(path);

        Assert.Equal((64, 48), (decoded.Width, decoded.Height));
    }

    /// <summary>
    /// The point of the format: the file holds LINEAR light, not the encoded values on screen. A
    /// mid-grey sRGB 0.5 is 0.2140 of the light, and that is what a reader must find.
    /// </summary>
    ///
    /// <remarks>
    /// The test frames are at least 32 px on a side because LibRaw refuses anything under 22 —
    /// its own sanity floor for a raw file, reached before it looks at a single tag. The written
    /// file is well-formed below that; nothing can read it back here to prove it.
    /// </remarks>
    [LibRawFact]
    public void Carries_linear_light_rather_than_the_display_encoding()
    {
        string path = NewPath();
        LinearDng.Write(Encoded(32, 32, (_, _) => (0.5f, 0.5f, 0.5f)), ColorSpaces.Srgb, path);

        ImageBuffer decoded = RawDecode.DecodeRaw(path);

        float expected = Srgb.SrgbToLinear(0.5f);
        int centre = ((16 * decoded.Width) + 16) * 3;
        Assert.Equal(expected, decoded.Data[centre], 3);
        Assert.Equal(expected, decoded.Data[centre + 1], 3);
        Assert.Equal(expected, decoded.Data[centre + 2], 3);
    }

    /// <summary>Black and white have to land exactly on the ends, or every subsequent grade in the
    /// host starts from a file that cannot reach them.</summary>
    [LibRawFact]
    public void Puts_black_at_zero_and_white_at_full_scale()
    {
        string path = NewPath();
        // Left half black, right half white.
        LinearDng.Write(Encoded(64, 32, (x, _) => x < 32 ? (0f, 0f, 0f) : (1f, 1f, 1f)),
                        ColorSpaces.Srgb, path);

        ImageBuffer decoded = RawDecode.DecodeRaw(path);

        int black = ((16 * decoded.Width) + 8) * 3;
        int white = ((16 * decoded.Width) + 56) * 3;
        Assert.Equal(0f, decoded.Data[black], 4);
        Assert.Equal(1f, decoded.Data[white], 4);
    }

    /// <summary>
    /// The colour tags are what make the numbers mean something to a host. ColorMatrix1 is XYZ(D50)
    /// → the output space, so applying it to the space's own white point must give a neutral: if it
    /// is transposed or inverted the wrong way, this is where it shows.
    /// </summary>
    [Theory]
    [InlineData("sRGB")]
    [InlineData("AdobeRGB")]
    [InlineData("DisplayP3")]
    public void States_a_colour_matrix_that_turns_d50_white_into_a_neutral(string spaceName)
    {
        ColorSpaceDef space = ColorSpaces.ByName(spaceName, ColorSpaces.Srgb);
        double[,] xyzToSpace = ColorSpaces.Invert3(ColorSpaces.ToXyzD50(space));

        // D50 as XYZ (Y = 1), the illuminant the matrix is declared against.
        double[] d50 = ColorSpaces.Apply(xyzToSpace, [0.9642, 1.0000, 0.8249]);

        Assert.Equal(d50[0], d50[1], 3);
        Assert.Equal(d50[1], d50[2], 3);
    }

    /// <summary>
    /// The embedded preview is display-referred in the ROLL's output space, so it carries that
    /// space's profile — without it a file browser reads an Adobe RGB or P3 roll's thumbnail as
    /// sRGB and shows it undersaturated. Checked by the profile's own signature rather than by
    /// re-parsing the IFD: what matters is that the bytes are in the file and the file still
    /// decodes (which the tests above establish), not where exactly the tag sits.
    /// </summary>
    [Theory]
    [InlineData("sRGB")]
    [InlineData("AdobeRGB")]
    [InlineData("DisplayP3")]
    public void Tags_the_preview_with_the_output_spaces_profile(string spaceName)
    {
        string path = NewPath();
        ColorSpaceDef space = ColorSpaces.ByName(spaceName, ColorSpaces.Srgb);

        LinearDng.Write(Encoded(64, 48, (_, _) => (0.5f, 0.5f, 0.5f)), space, path);

        byte[] file = File.ReadAllBytes(path);
        byte[] profile = IccProfiles.Build(space);
        Assert.Contains("acsp"u8.ToArray(), Window(file));          // an ICC profile is in there
        Assert.True(IndexOf(file, profile) >= 0, "the space's own profile bytes are not in the file");
    }

    /// <summary>Every 4-byte window of the file, for a signature search.</summary>
    private static IEnumerable<byte[]> Window(byte[] file)
    {
        for (int i = 0; i + 4 <= file.Length; i++) yield return file[i..(i + 4)];
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }

    [Fact]
    public void Leaves_the_buffer_it_was_given_untouched()
    {
        string path = NewPath();
        ImageBuffer src = Encoded(32, 32, (_, _) => (0.5f, 0.25f, 0.75f));
        float[] before = (float[])src.Data.Clone();

        LinearDng.Write(src, ColorSpaces.Srgb, path);

        Assert.Equal(before, src.Data);
    }
}
