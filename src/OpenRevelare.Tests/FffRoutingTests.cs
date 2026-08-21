using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// Content-based routing for <c>.fff</c>, where one extension covers two unrelated formats.
///
/// Hasselblad spends <c>.fff</c> twice: on Imacon digital-back RAW (Bayer, needs LibRaw) and on
/// Flextight SCANNER exports, which are uncompressed 16-bit RGB TIFFs with no CFA at all. LibRaw
/// rejects the latter outright — verified against 0.21.5, the version shipped here, which answers
/// <c>-2 Unsupported file format or not RAW file</c> — so routing by extension alone turned a
/// file TiffIO reads perfectly into a failed import.
///
/// The discriminator is PhotometricInterpretation(262) == RGB(2) with SamplesPerPixel(277) == 3,
/// which a Bayer file cannot claim: CFA declares 32803 (or 34892 LinearRaw) with one sample per
/// pixel. These tests pin BOTH directions, because they are not equally safe. A scanner file sent
/// to LibRaw fails loudly and is merely annoying; a Bayer file sent to TiffIO would decode to a
/// WRONG IMAGE without erroring. Hence every ambiguous or unreadable case must fall back to RAW.
/// </summary>
public class FffRoutingTests
{
    /// <summary>Builds a minimal big-endian baseline TIFF header with the tags the sniffer reads.
    /// Only IFD0 matters, so no pixel data is needed.</summary>
    private static string WriteTiff(string ext, int photometric, int samplesPerPixel,
                                    bool bigEndian = true, ushort magic = 42)
    {
        var tags = new (int Tag, int Value)[]
        {
            (256, 100),                 // ImageWidth
            (257, 100),                 // ImageLength
            (258, 16),                  // BitsPerSample
            (259, 1),                   // Compression = none
            (262, photometric),
            (277, samplesPerPixel),
        };

        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        void U16(int v) { if (bigEndian) { w.Write((byte)(v >> 8)); w.Write((byte)v); } else { w.Write((byte)v); w.Write((byte)(v >> 8)); } }
        void U32(long v)
        {
            if (bigEndian) { w.Write((byte)(v >> 24)); w.Write((byte)(v >> 16)); w.Write((byte)(v >> 8)); w.Write((byte)v); }
            else { w.Write((byte)v); w.Write((byte)(v >> 8)); w.Write((byte)(v >> 16)); w.Write((byte)(v >> 24)); }
        }

        w.Write(bigEndian ? new byte[] { (byte)'M', (byte)'M' } : new byte[] { (byte)'I', (byte)'I' });
        U16(magic);
        U32(8);                                     // IFD0 immediately after the header
        U16(tags.Length);
        foreach (var (tag, value) in tags)
        {
            U16(tag);
            U16(3);                                 // SHORT
            U32(1);
            U16(value);                             // inline, left-aligned
            U16(0);
        }
        U32(0);                                     // no next IFD

        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ext);
        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    private static void With(string path, Action<string> body)
    {
        try { body(path); } finally { File.Delete(path); }
    }

    /// <summary>A Flextight scanner .fff — full RGB — must go to TiffIO, not LibRaw.</summary>
    [Fact]
    public void Scanner_fff_routes_to_tiff()
        => With(WriteTiff(".fff", photometric: 2, samplesPerPixel: 3), p =>
        {
            Assert.True(RawDecode.LooksLikeFullColorTiff(p));
            Assert.False(RawDecode.IsRawExtension(p));
        });

    /// <summary>A CFA .fff — an Imacon back — must still go to LibRaw. This is the direction that
    /// silently corrupts if it regresses, since TiffIO would not error on it.</summary>
    [Fact]
    public void Bayer_fff_routes_to_raw()
        => With(WriteTiff(".fff", photometric: 32803, samplesPerPixel: 1), p =>
        {
            Assert.False(RawDecode.LooksLikeFullColorTiff(p));
            Assert.True(RawDecode.IsRawExtension(p));
        });

    /// <summary>Little-endian is just as valid a TIFF; the sniffer must not assume Hasselblad's
    /// big-endian byte order.</summary>
    [Fact]
    public void Little_endian_scanner_fff_routes_to_tiff()
        => With(WriteTiff(".fff", 2, 3, bigEndian: false), p => Assert.False(RawDecode.IsRawExtension(p)));

    /// <summary>BigTIFF (magic 43) is not parsed here, so it must fall back to the RAW path
    /// rather than be guessed at.</summary>
    [Fact]
    public void BigTiff_fff_falls_back_to_raw()
        => With(WriteTiff(".fff", 2, 3, magic: 43), p => Assert.True(RawDecode.IsRawExtension(p)));

    /// <summary>Garbage, truncated files and non-TIFF data must fall back to RAW, where LibRaw
    /// reports the real problem, instead of being handed to TiffIO.</summary>
    [Theory]
    [InlineData(new byte[] { 0x00 })]
    [InlineData(new byte[] { (byte)'M', (byte)'M', 0x00, 0x2a })]          // truncated header
    [InlineData(new byte[] { (byte)'J', (byte)'U', (byte)'N', (byte)'K', 1, 2, 3, 4 })]
    public void Unreadable_fff_falls_back_to_raw(byte[] content)
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".fff");
        File.WriteAllBytes(path, content);
        With(path, p =>
        {
            Assert.False(RawDecode.LooksLikeFullColorTiff(p));
            Assert.True(RawDecode.IsRawExtension(p));
        });
    }

    /// <summary>A missing file must not throw out of the routing decision.</summary>
    [Fact]
    public void Missing_fff_falls_back_to_raw()
        => Assert.True(RawDecode.IsRawExtension(Path.Combine(Path.GetTempPath(), "no-such-file.fff")));

    /// <summary>
    /// Sniffing is confined to .fff. Most camera RAWs ARE TIFF containers whose IFD0 describes an
    /// embedded RGB PREVIEW — a real .NEF answers true to <see cref="RawDecode.LooksLikeFullColorTiff"/>
    /// — so widening the sniff list would route genuine RAW files to TiffIO and decode the preview
    /// instead of the sensor data. Extension stays authoritative everywhere else.
    /// </summary>
    [Fact]
    public void Other_raw_extensions_ignore_content()
        => With(WriteTiff(".nef", photometric: 2, samplesPerPixel: 3), p =>
        {
            Assert.True(RawDecode.LooksLikeFullColorTiff(p));
            Assert.True(RawDecode.IsRawExtension(p));      // still RAW, despite looking like RGB
        });

    /// <summary>A non-RAW extension is never sniffed and never routed to LibRaw.</summary>
    [Fact]
    public void Tiff_extension_is_not_raw()
        => With(WriteTiff(".tif", 2, 3), p => Assert.False(RawDecode.IsRawExtension(p)));

    /// <summary>
    /// <see cref="RawDecode.HasRawExtension"/> answers the NAME question only, so it stays true
    /// for a scanner .fff that content-sniffing sent to TiffIO. That difference is what lets the
    /// import dialog recognise a scanner export ("was a RAW candidate, isn't RAW") without
    /// re-deciding the routing itself.
    /// </summary>
    [Fact]
    public void HasRawExtension_ignores_content()
        => With(WriteTiff(".fff", photometric: 2, samplesPerPixel: 3), p =>
        {
            Assert.True(RawDecode.HasRawExtension(p));    // name says maybe
            Assert.False(RawDecode.IsRawExtension(p));    // content says no
        });

    /// <summary>Extension matching is case-insensitive in both predicates — cameras and scanners
    /// write the upper-case form constantly, and a case-sensitive test here would drop them.</summary>
    [Fact]
    public void Extension_matching_is_case_insensitive()
    {
        With(WriteTiff(".FFF", photometric: 2, samplesPerPixel: 3), p =>
        {
            Assert.True(RawDecode.HasRawExtension(p));
            Assert.False(RawDecode.IsRawExtension(p));    // sniffed despite upper case
        });
        With(WriteTiff(".NEF", photometric: 2, samplesPerPixel: 3), p =>
        {
            Assert.True(RawDecode.HasRawExtension(p));
            Assert.True(RawDecode.IsRawExtension(p));
        });
    }

    /// <summary>A file that was never a RAW candidate must not be mistaken for a scanner export
    /// by the "was RAW by name, not RAW by content" rule.</summary>
    [Fact]
    public void Non_raw_extension_has_no_raw_name()
        => With(WriteTiff(".tif", 2, 3), p => Assert.False(RawDecode.HasRawExtension(p)));
}
