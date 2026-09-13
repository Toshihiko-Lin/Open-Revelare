using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace OpenRevelare.Core;

/// <summary>
/// The byte-level shape of a gain-map JPEG, as Lightroom, Android (Ultra HDR) and Chrome write
/// and read it: the base JPEG, carrying an XMP <c>Container:Directory</c> that names the second
/// image and an MPF (CIPA DC-007) index that locates it, followed immediately by the gain-map
/// JPEG, which carries the <c>hdrgm</c> metadata that gives its codes meaning.
///
/// <para>
/// Two things are hand-written here because no encoder in the dependency set writes them: the
/// MPF APP2 segment, whose offsets are relative to its own position inside the base file, and the
/// two XMP packets. Everything else — the two JPEG streams, the base's ICC and EXIF — is the
/// ordinary encoder's work.
/// </para>
///
/// <para>
/// The same metadata is written twice, on purpose: as Adobe's <c>hdrgm</c> XMP, which is what
/// Lightroom writes and what Chrome and Android read first, and as the ISO 21496-1 binary
/// segment (<c>urn:iso:std:iso:ts:21496:-1</c>), which is what Apple's own HDR JPEGs carry and
/// what its readers look for. libultrahdr writes both; a reader takes whichever it knows. The
/// binary form goes into BOTH images, as the standard and libultrahdr have it — the base carries
/// only the version fields, so a reader can tell it is looking at an HDR file before it has parsed
/// the second image, and the map carries the full structure.
/// </para>
/// </summary>
internal static class GainMapJpegContainer
{
    private const string GContainerNs = "http://ns.google.com/photos/1.0/container/";
    private const string GContainerItemNs = "http://ns.google.com/photos/1.0/container/item/";
    private const string HdrgmNs = "http://ns.adobe.com/hdr-gain-map/1.0/";

    /// <summary>The base image's XMP: the directory naming both images, and the gain-map version.</summary>
    internal static byte[] PrimaryXmp(int gainMapLength)
    {
        if (gainMapLength <= 0) throw new ArgumentOutOfRangeException(nameof(gainMapLength));
        string xml =
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\" x:xmptk=\"OpenRevelare\">" +
            "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
            "<rdf:Description rdf:about=\"\"" +
            $" xmlns:Container=\"{GContainerNs}\"" +
            $" xmlns:Item=\"{GContainerItemNs}\"" +
            $" xmlns:hdrgm=\"{HdrgmNs}\"" +
            " hdrgm:Version=\"1.0\">" +
            "<Container:Directory><rdf:Seq>" +
            "<rdf:li rdf:parseType=\"Resource\">" +
            "<Container:Item Item:Semantic=\"Primary\" Item:Mime=\"image/jpeg\"/>" +
            "</rdf:li>" +
            "<rdf:li rdf:parseType=\"Resource\">" +
            "<Container:Item Item:Semantic=\"GainMap\" Item:Mime=\"image/jpeg\"" +
            $" Item:Length=\"{gainMapLength.ToString(CultureInfo.InvariantCulture)}\"/>" +
            "</rdf:li>" +
            "</rdf:Seq></Container:Directory>" +
            "</rdf:Description></rdf:RDF></x:xmpmeta>";
        return Encoding.UTF8.GetBytes(xml);
    }

    /// <summary>
    /// The gain map's XMP. Ranges are written per channel as an <c>rdf:Seq</c> when they differ
    /// and as one scalar when they do not, which is the form every reader accepts.
    /// </summary>
    internal static byte[] GainMapXmp(GainMapMetadata m)
    {
        ArgumentNullException.ThrowIfNull(m);
        var sb = new StringBuilder();
        sb.Append("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\" x:xmptk=\"OpenRevelare\">")
          .Append("<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">")
          .Append("<rdf:Description rdf:about=\"\"")
          .Append($" xmlns:hdrgm=\"{HdrgmNs}\"")
          .Append(" hdrgm:Version=\"1.0\"")
          .Append($" hdrgm:Gamma=\"{F(m.Gamma)}\"")
          .Append($" hdrgm:OffsetSDR=\"{F(m.OffsetSdr)}\"")
          .Append($" hdrgm:OffsetHDR=\"{F(m.OffsetHdr)}\"")
          .Append($" hdrgm:HDRCapacityMin=\"{F(m.HdrCapacityMin)}\"")
          .Append($" hdrgm:HDRCapacityMax=\"{F(m.HdrCapacityMax)}\"")
          .Append(" hdrgm:BaseRenditionIsHDR=\"False\"");
        if (m.IsUniform)
        {
            sb.Append($" hdrgm:GainMapMin=\"{F(m.MinR)}\"")
              .Append($" hdrgm:GainMapMax=\"{F(m.MaxR)}\"")
              .Append("/>");
        }
        else
        {
            sb.Append('>')
              .Append("<hdrgm:GainMapMin><rdf:Seq>")
              .Append($"<rdf:li>{F(m.MinR)}</rdf:li><rdf:li>{F(m.MinG)}</rdf:li><rdf:li>{F(m.MinB)}</rdf:li>")
              .Append("</rdf:Seq></hdrgm:GainMapMin>")
              .Append("<hdrgm:GainMapMax><rdf:Seq>")
              .Append($"<rdf:li>{F(m.MaxR)}</rdf:li><rdf:li>{F(m.MaxG)}</rdf:li><rdf:li>{F(m.MaxB)}</rdf:li>")
              .Append("</rdf:Seq></hdrgm:GainMapMax>")
              .Append("</rdf:Description>");
        }
        sb.Append("</rdf:RDF></x:xmpmeta>");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string F(float v) => v.ToString("0.000000", CultureInfo.InvariantCulture);

    // ── ISO 21496-1 ──────────────────────────────────────────────────────────────────────────

    /// <summary>The segment's namespace, NUL-terminated, straight after the APP2 length.</summary>
    private static readonly byte[] IsoNamespace = Encoding.ASCII.GetBytes("urn:iso:std:iso:ts:21496:-1\0");

    /// <summary>Every log2 or linear quantity is a rational; this denominator gives it six decimals.</summary>
    private const uint IsoDenominator = 1_000_000;

    /// <summary>
    /// The base image's ISO 21496-1 segment: the namespace and the two version fields, nothing
    /// else — the base announces the standard, the map carries the numbers.
    /// </summary>
    internal static byte[] IsoVersionSegment()
    {
        byte[] seg = new byte[2 + 2 + IsoNamespace.Length + 4];
        seg[0] = 0xFF; seg[1] = 0xE2;
        BinaryPrimitives.WriteUInt16BigEndian(seg.AsSpan(2), (ushort)(seg.Length - 2));
        IsoNamespace.CopyTo(seg, 4);
        // minimum_version, writer_version: both zero, already so.
        return seg;
    }

    /// <summary>
    /// The ISO 21496-1 <c>GainMapMetadata</c> structure, as one complete APP2 segment. Same
    /// numbers as <see cref="GainMapXmp"/>, by construction from the same record: the base's
    /// headroom is the XMP's capacity minimum, the alternate's the capacity maximum.
    /// </summary>
    internal static byte[] IsoMetadataSegment(GainMapMetadata m)
    {
        ArgumentNullException.ThrowIfNull(m);
        int channels = m.IsUniform ? 1 : 3;
        int payload = 2 + 2 + 1 + 16 + channels * 40;
        byte[] seg = new byte[2 + 2 + IsoNamespace.Length + payload];
        int o = 0;
        seg[o++] = 0xFF; seg[o++] = 0xE2;
        BinaryPrimitives.WriteUInt16BigEndian(seg.AsSpan(o), (ushort)(seg.Length - 2)); o += 2;
        IsoNamespace.CopyTo(seg, o); o += IsoNamespace.Length;

        BinaryPrimitives.WriteUInt16BigEndian(seg.AsSpan(o), 0); o += 2;   // minimum_version
        BinaryPrimitives.WriteUInt16BigEndian(seg.AsSpan(o), 0); o += 2;   // writer_version
        // is_multichannel (bit 7), use_base_colour_space (bit 6): the map is defined in the base's
        // linear space, which is what HdrGainMap computes and what the XMP form implies.
        seg[o++] = (byte)((channels == 3 ? 0x80 : 0x00) | 0x40);
        o = WriteRational(seg, o, m.HdrCapacityMin);      // base_hdr_headroom
        o = WriteRational(seg, o, m.HdrCapacityMax);      // alternate_hdr_headroom
        for (int c = 0; c < channels; c++)
        {
            o = WriteRational(seg, o, m.Min(c));          // gain_map_min (signed)
            o = WriteRational(seg, o, m.Max(c));          // gain_map_max (signed)
            o = WriteRational(seg, o, m.Gamma);           // gamma
            o = WriteRational(seg, o, m.OffsetSdr);       // base_offset (signed)
            o = WriteRational(seg, o, m.OffsetHdr);       // alternate_offset (signed)
        }
        if (o != seg.Length) throw new InvalidOperationException("ISO 21496-1 segment size drifted from its layout.");
        return seg;
    }

    /// <summary>Numerator then denominator, both 32-bit big-endian; the numerator is written as a
    /// signed value, which is a no-op for the fields the standard declares unsigned.</summary>
    private static int WriteRational(byte[] seg, int o, float value)
    {
        int numerator = (int)Math.Round((double)value * IsoDenominator);
        BinaryPrimitives.WriteInt32BigEndian(seg.AsSpan(o), numerator);
        BinaryPrimitives.WriteUInt32BigEndian(seg.AsSpan(o + 4), IsoDenominator);
        return o + 8;
    }

    // ── MPF ──────────────────────────────────────────────────────────────────────────────────

    // Marker (2) + length (2) + "MPF\0" (4) + MP header (8) + index IFD (2 + 3×12 + 4) + 2 entries (2×16).
    private const int MpfSegmentLength = 2 + 2 + 4 + 8 + 42 + 32;

    /// <summary>
    /// The full ISO segment added to the encoded gain map after its leading application
    /// segments. Done BEFORE the base's XMP is written, because that XMP states the map's final
    /// byte length; the base's own (version-only) segment is added inside <see cref="Compose"/>.
    /// </summary>
    internal static byte[] WithIsoMetadata(byte[] jpeg, GainMapMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(jpeg);
        return InsertAfterLeadingApplicationSegments(jpeg, IsoMetadataSegment(metadata));
    }

    /// <summary>
    /// Joins the two encoded streams: the ISO version segment and then the MPF index go into the
    /// base after its leading application segments, and the finished gain map (already carrying
    /// its own ISO segment) follows the base byte for byte.
    ///
    /// <para>
    /// MPF offsets are measured from the MP header's byte-order field, not from the file start,
    /// so the segment has to know where it will land before it can be written — which is why it
    /// is inserted last rather than handed to the encoder as metadata.
    /// </para>
    /// </summary>
    internal static byte[] Compose(byte[] primaryJpeg, byte[] gainMapJpeg)
    {
        ArgumentNullException.ThrowIfNull(primaryJpeg);
        ArgumentNullException.ThrowIfNull(gainMapJpeg);
        primaryJpeg = InsertAfterLeadingApplicationSegments(primaryJpeg, IsoVersionSegment());
        int insertAt = EndOfLeadingApplicationSegments(primaryJpeg);

        int primaryLength = primaryJpeg.Length + MpfSegmentLength;
        // The MP header starts after marker, length and the "MPF\0" signature.
        int mpHeaderAt = insertAt + 8;
        int gainMapOffset = primaryLength - mpHeaderAt;

        byte[] mpf = new byte[MpfSegmentLength];
        int o = 0;
        mpf[o++] = 0xFF; mpf[o++] = 0xE2;                                  // APP2
        BinaryPrimitives.WriteUInt16BigEndian(mpf.AsSpan(o), (ushort)(MpfSegmentLength - 2)); o += 2;
        mpf[o++] = (byte)'M'; mpf[o++] = (byte)'P'; mpf[o++] = (byte)'F'; mpf[o++] = 0;
        // MP header: big-endian TIFF-style byte order, then the offset of the index IFD from here.
        mpf[o++] = (byte)'M'; mpf[o++] = (byte)'M'; mpf[o++] = 0x00; mpf[o++] = 0x2A;
        BinaryPrimitives.WriteUInt32BigEndian(mpf.AsSpan(o), 8); o += 4;
        // MP index IFD.
        BinaryPrimitives.WriteUInt16BigEndian(mpf.AsSpan(o), 3); o += 2;
        // MPFVersion, UNDEFINED ×4, "0100".
        BinaryPrimitives.WriteUInt16BigEndian(mpf.AsSpan(o), 0xB000); o += 2;
        BinaryPrimitives.WriteUInt16BigEndian(mpf.AsSpan(o), 7); o += 2;
        BinaryPrimitives.WriteUInt32BigEndian(mpf.AsSpan(o), 4); o += 4;
        mpf[o++] = (byte)'0'; mpf[o++] = (byte)'1'; mpf[o++] = (byte)'0'; mpf[o++] = (byte)'0';
        // NumberOfImages, LONG ×1.
        BinaryPrimitives.WriteUInt16BigEndian(mpf.AsSpan(o), 0xB001); o += 2;
        BinaryPrimitives.WriteUInt16BigEndian(mpf.AsSpan(o), 4); o += 2;
        BinaryPrimitives.WriteUInt32BigEndian(mpf.AsSpan(o), 1); o += 4;
        BinaryPrimitives.WriteUInt32BigEndian(mpf.AsSpan(o), 2); o += 4;
        // MPEntry, UNDEFINED ×32, at offset 50 from the MP header (8 + 2 + 36 + 4).
        BinaryPrimitives.WriteUInt16BigEndian(mpf.AsSpan(o), 0xB002); o += 2;
        BinaryPrimitives.WriteUInt16BigEndian(mpf.AsSpan(o), 7); o += 2;
        BinaryPrimitives.WriteUInt32BigEndian(mpf.AsSpan(o), 32); o += 4;
        BinaryPrimitives.WriteUInt32BigEndian(mpf.AsSpan(o), 50); o += 4;
        // Next IFD: none.
        BinaryPrimitives.WriteUInt32BigEndian(mpf.AsSpan(o), 0); o += 4;
        // Entry 1: the base — format JPEG, type "Baseline MP Primary Image", offset zero by definition.
        BinaryPrimitives.WriteUInt32BigEndian(mpf.AsSpan(o), 0x030000); o += 4;
        BinaryPrimitives.WriteUInt32BigEndian(mpf.AsSpan(o), (uint)primaryLength); o += 4;
        BinaryPrimitives.WriteUInt32BigEndian(mpf.AsSpan(o), 0); o += 4;
        BinaryPrimitives.WriteUInt16BigEndian(mpf.AsSpan(o), 0); o += 2;
        BinaryPrimitives.WriteUInt16BigEndian(mpf.AsSpan(o), 0); o += 2;
        // Entry 2: the gain map — format JPEG, type undefined.
        BinaryPrimitives.WriteUInt32BigEndian(mpf.AsSpan(o), 0x000000); o += 4;
        BinaryPrimitives.WriteUInt32BigEndian(mpf.AsSpan(o), (uint)gainMapJpeg.Length); o += 4;
        BinaryPrimitives.WriteUInt32BigEndian(mpf.AsSpan(o), (uint)gainMapOffset); o += 4;
        BinaryPrimitives.WriteUInt16BigEndian(mpf.AsSpan(o), 0); o += 2;
        BinaryPrimitives.WriteUInt16BigEndian(mpf.AsSpan(o), 0); o += 2;
        if (o != MpfSegmentLength) throw new InvalidOperationException("MPF segment size drifted from its layout.");

        byte[] file = new byte[primaryLength + gainMapJpeg.Length];
        primaryJpeg.AsSpan(0, insertAt).CopyTo(file);
        mpf.CopyTo(file, insertAt);
        primaryJpeg.AsSpan(insertAt).CopyTo(file.AsSpan(insertAt + MpfSegmentLength));
        gainMapJpeg.CopyTo(file, primaryLength);
        return file;
    }

    private static byte[] InsertAfterLeadingApplicationSegments(byte[] jpeg, byte[] segment)
    {
        int at = EndOfLeadingApplicationSegments(jpeg);
        byte[] result = new byte[jpeg.Length + segment.Length];
        jpeg.AsSpan(0, at).CopyTo(result);
        segment.CopyTo(result, at);
        jpeg.AsSpan(at).CopyTo(result.AsSpan(at + segment.Length));
        return result;
    }

    /// <summary>
    /// Where the base's run of APPn/COM segments after SOI ends — the first position at which a
    /// new application segment may be inserted without splitting anything.
    /// </summary>
    private static int EndOfLeadingApplicationSegments(byte[] jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
            throw new ArgumentException("Not a JPEG stream: missing SOI.", nameof(jpeg));
        int pos = 2;
        while (pos + 4 <= jpeg.Length && jpeg[pos] == 0xFF)
        {
            byte marker = jpeg[pos + 1];
            bool application = marker is >= 0xE0 and <= 0xEF or 0xFE;
            if (!application) break;
            int length = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(pos + 2));
            pos += 2 + length;
        }
        return pos;
    }
}
