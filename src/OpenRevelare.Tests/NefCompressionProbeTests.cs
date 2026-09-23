using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// The Nikon High Efficiency guard. The damage it prevents is specific: the public LibRaw accepts
/// an HE/HE* NEF, prints "Data corrupted at ..." and returns horizontal colour bars, so without a
/// metadata probe the frame arrives as pixels and reads to the user as a corrupt original.
///
/// Built by hand rather than checked in as a sample: what matters is the MakerNote NESTING (a TIFF
/// inside the Exif IFD, with its own byte order and its own offset base), and a synthetic file
/// states that structure exactly, while a real 50 MB NEF would only state one camera's version of
/// it.
/// </summary>
public sealed class NefCompressionProbeTests
{
    /// <summary>A minimal NEF skeleton: IFD0 → Exif IFD → Nikon MakerNote → tag 0x0093.</summary>
    private static string WriteNef(int compression, string extension = ".nef")
    {
        var b = new List<byte>();
        void U16(int v) { b.Add((byte)(v & 0xFF)); b.Add((byte)(v >> 8)); }
        void U32(long v) { for (int i = 0; i < 4; i++) b.Add((byte)((v >> (8 * i)) & 0xFF)); }

        const int exifIfd = 26, maker = 44, makerHeader = 54, makerIfd = 62;

        U16('I' | ('I' << 8)); U16(42); U32(8);                       // file header

        U16(1); U16(0x8769); U16(4); U32(1); U32(exifIfd); U32(0);     // IFD0 → Exif IFD
        U16(1); U16(0x927C); U16(7); U32(36); U32(maker); U32(0);      // Exif IFD → MakerNote

        b.AddRange("Nikon\0"u8);                                      // MakerNote signature
        b.AddRange(new byte[] { 0x02, 0x11, 0x00, 0x00 });             // version + pad
        Assert.Equal(makerHeader, b.Count);
        U16('I' | ('I' << 8)); U16(42); U32(8);                       // MakerNote's own TIFF header
        Assert.Equal(makerIfd, b.Count);
        U16(1); U16(0x0093); U16(3); U32(1); U16(compression); U16(0); U32(0);   // NEFCompression

        string path = Path.Combine(TestDataIsolation.Root, $"nef-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, b.ToArray());
        return path;
    }

    [Theory]
    [InlineData(13)]   // High Efficiency
    [InlineData(14)]   // High Efficiency*
    public void Reads_the_high_efficiency_codes_and_refuses_them(int compression)
    {
        string path = WriteNef(compression);

        Assert.Equal(compression, RawDecode.ReadNefCompression(path));
        Assert.NotNull(RawDecode.UnsupportedRawReason(path));
    }

    [Theory]
    [InlineData(4)]    // lossy (type 2)
    [InlineData(10)]   // packed 14-bit
    public void Leaves_the_codecs_libraw_handles_alone(int compression)
    {
        string path = WriteNef(compression);

        Assert.Equal(compression, RawDecode.ReadNefCompression(path));
        Assert.Null(RawDecode.UnsupportedRawReason(path));
    }

    /// <summary>
    /// The gate is the extension, because the tag number is Nikon's alone: 0x0093 in another
    /// vendor's MakerNote means something else entirely, and a file that is not a NEF must not be
    /// refused over it.
    /// </summary>
    [Fact]
    public void Says_nothing_about_a_file_that_is_not_a_nef()
    {
        Assert.Null(RawDecode.UnsupportedRawReason(WriteNef(13, ".tif")));
    }

    [Fact]
    public void Treats_an_unreadable_file_as_nothing_known()
    {
        string path = Path.Combine(TestDataIsolation.Root, $"nef-{Guid.NewGuid():N}.nef");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });

        Assert.Null(RawDecode.ReadNefCompression(path));
        Assert.Null(RawDecode.UnsupportedRawReason(path));
    }

    /// <summary>
    /// The decode path itself must refuse, not just the probe — and with the probe's message, which
    /// is what proves the guard runs BEFORE LibRaw rather than after it has already been wrong.
    /// </summary>
    [Fact]
    public void Never_lets_a_high_efficiency_frame_reach_the_decoder()
    {
        string path = WriteNef(14);

        var ex = Assert.Throws<NotSupportedException>(() => RawDecode.DecodeRaw(path));
        Assert.Equal(RawDecode.UnsupportedRawReason(path), ex.Message);
    }
}
