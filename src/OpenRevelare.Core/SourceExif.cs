using System.Buffers.Binary;
using System.Text;
using System.Globalization;
using System.Xml.Linq;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;

namespace OpenRevelare.Core;

/// <summary>
/// Capture metadata, detached from the source image's storage and colour recipe. Rebuilding
/// standard IFDs avoids transplanting RAW offsets, thumbnails or vendor-relative MakerNotes
/// into a different container. Pixel geometry and colour always belong to the new render.
/// </summary>
public sealed class SourceExif
{
    private readonly List<Entry> _root;
    private readonly List<Entry> _exif;
    private readonly List<Entry> _gps;
    private SourceExif? _digitization;
    private bool _filmCapture;

    private SourceExif(List<Entry> root, List<Entry> exif, List<Entry> gps)
        => (_root, _exif, _gps) = (root, exif, gps);

    public static SourceExif? Read(string path)
    {
        var directories = ImageMetadataReader.ReadMetadata(path);
        // A RAW may contain several previews. The first IFD0/Exif/GPS directories describe
        // the source; later preview directories must not replace the original capture data.
        var root = ReadDirectory(directories.OfType<ExifIfd0Directory>().FirstOrDefault(),
            tag => tag is 256 or 257 or 269 or 270 or 271 or 272 or 274 or 282 or 283 or 296 or 305 or 306 or 315 or 316 or 33432
                or 40091 or 40092 or 40093 or 40094 or 40095);
        var exif = ReadDirectory(directories.OfType<ExifSubIfdDirectory>().FirstOrDefault(d =>
            d.Tags.Any(t => t.Type is 0x829a or 0x829d or 0x8827 or 0x9000 or 0x9003 or 0x9004
                or 0xa001 or 0xa002 or 0xa003 or 0xa431 or 0xa433 or 0xa434 or 0xa435)),
            tag => tag is not (0x927c or 0xa005 or 0xa20c or 0x9214));
        var gps = ReadDirectory(directories.OfType<GpsDirectory>().FirstOrDefault(), _ => true);
        return root.Count + exif.Count + gps.Count == 0 ? null : new(root, exif, gps);
    }

    public string CaptureSummary => string.Join(" · ", new[]
    {
        Text(_root, 271), Text(_root, 272), Text(_exif, 0xa434), CaptureTime,
    }.Where(s => !string.IsNullOrWhiteSpace(s)));

    private string CaptureTime => Text(_exif, 0x9003) is { Length: > 0 } date ? date : Text(_root, 306);

    private string CaptureValue(ushort tag)
    {
        Entry? entry = _exif.FirstOrDefault(e => e.Tag == tag);
        if (entry is null) return "";
        return entry.Type switch
        {
            2 => Encoding.UTF8.GetString(entry.Data).TrimEnd('\0'),
            3 => BinaryPrimitives.ReadUInt16LittleEndian(entry.Data).ToString(CultureInfo.InvariantCulture),
            4 => BinaryPrimitives.ReadUInt32LittleEndian(entry.Data).ToString(CultureInfo.InvariantCulture),
            5 when entry.Data.Length >= 8 => BinaryPrimitives.ReadUInt32LittleEndian(entry.Data).ToString(CultureInfo.InvariantCulture)
                + "/" + BinaryPrimitives.ReadUInt32LittleEndian(entry.Data.AsSpan(4)).ToString(CultureInfo.InvariantCulture),
            _ => "",
        };
    }

    private static string Text(List<Entry> entries, ushort tag)
        => entries.FirstOrDefault(e => e.Tag == tag) is { } entry
            ? Encoding.UTF8.GetString(entry.Data).TrimEnd('\0') : "";

    /// <summary>
    /// Switching to film capture replaces the WHOLE capture group, including GPS and time.
    /// Merely patching a camera model would create a false film-camera/digital-exposure hybrid.
    /// The portable source EXIF survives separately in XMP as digitization provenance.
    /// </summary>
    public static SourceExif? ForExport(SourceExif? source, bool preserveSource, FilmCaptureMetadata? film)
    {
        if (film is null) return preserveSource ? source : null;
        film.Validate();
        var root = (preserveSource ? source : null)?._root.Where(e => e.Tag is 269 or 270 or 315 or 33432 or 40091 or 40092 or 40093 or 40094 or 40095)
            .ToList() ?? new List<Entry>();
        var exif = new List<Entry>();
        void Add(List<Entry> entries, ushort tag, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            entries.RemoveAll(e => e.Tag == tag);
            entries.Add(Ascii(tag, text.Trim()));
        }
        Add(root, 271, film.Camera.Make); Add(root, 272, film.Camera.Model);
        Add(root, 315, film.Artist); Add(root, 33432, film.Copyright);
        Add(exif, 0xa431, film.Camera.SerialNumber);
        Add(exif, 0xa433, film.Lens.Make); Add(exif, 0xa434, film.Lens.Model);
        Add(exif, 0xa435, film.Lens.SerialNumber);
        if (!string.IsNullOrWhiteSpace(film.Iso)) exif.Add(Short(0x8827, ushort.Parse(film.Iso)));
        foreach (var (tag, value) in new (ushort, double?)[]
        {
            (0x829d, FilmCaptureMetadata.Number(film.FNumber, "Aperture")),
            (0x920a, FilmCaptureMetadata.Number(film.FocalLength, "Focal length")),
            (0x829a, FilmCaptureMetadata.Number(film.ExposureTime, "Shutter speed", allowFraction: true)),
        })
        {
            if (value is null) continue;
            uint denominator = value > 4000 ? 1000u : 1_000_000u;
            exif.Add(Bytes(tag, 5, [.. BitConverter.GetBytes(checked((uint)Math.Round(value.Value * denominator))),
                .. BitConverter.GetBytes(denominator)]));
        }
        if (!string.IsNullOrWhiteSpace(film.DateTimeOriginal))
            Add(exif, 0x9003, FilmCaptureMetadata.ParseDate(film.DateTimeOriginal).ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture));
        return new SourceExif(root, exif, []) { _filmCapture = true, _digitization = preserveSource ? source : null };
    }

    /// <summary>Merge provenance into the same XMP packet as HDR gain-map metadata.</summary>
    public byte[]? MergeXmp(byte[]? existing)
    {
        if (!_filmCapture) return existing;
        XNamespace x = "adobe:ns:meta/", rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
        XNamespace provenance = "https://openrevelare.org/ns/capture/1.0/";
        XDocument document = existing is { Length: > 0 }
            ? XDocument.Parse(Encoding.UTF8.GetString(existing))
            : new XDocument(new XElement(x + "xmpmeta", new XAttribute(XNamespace.Xmlns + "x", x),
                new XElement(rdf + "RDF", new XAttribute(XNamespace.Xmlns + "rdf", rdf))));
        XElement description = new(rdf + "Description", new XAttribute(rdf + "about", ""),
            new XAttribute(XNamespace.Xmlns + "or", provenance),
            new XAttribute(provenance + "CaptureInformationSource", "Film"));
        if (_digitization is { } source)
        {
            byte[] original = Serialize(source._root, source._exif, source._gps, 0, 0);
            description.Add(new XElement(provenance + "DigitizationSourceExif", Convert.ToBase64String(original)),
                new XElement(provenance + "DigitizationCameraMake", Text(source._root, 271)),
                new XElement(provenance + "DigitizationCameraModel", Text(source._root, 272)),
                new XElement(provenance + "DigitizationLensModel", Text(source._exif, 0xa434)),
                new XElement(provenance + "DigitizationDateTime", source.CaptureTime),
                new XElement(provenance + "DigitizationISO", source.CaptureValue(0x8827)),
                new XElement(provenance + "DigitizationFNumber", source.CaptureValue(0x829d)),
                new XElement(provenance + "DigitizationExposureTime", source.CaptureValue(0x829a)),
                new XElement(provenance + "DigitizationFocalLength", source.CaptureValue(0x920a)),
                new XElement(provenance + "DigitizationBodySerialNumber", Text(source._exif, 0xa431)),
                new XElement(provenance + "DigitizationLensSerialNumber", Text(source._exif, 0xa435)));
        }
        document.Descendants(rdf + "RDF").First().Add(description);
        byte[] result = Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting));
        if (result.Length > 65000) throw new InvalidDataException("Digitization provenance exceeds the portable XMP size limit.");
        return result;
    }

    private static List<Entry> ReadDirectory(MetadataExtractor.Directory? directory, Func<int, bool> include)
    {
        var entries = new List<Entry>();
        if (directory is null) return entries;
        foreach (var tag in directory.Tags)
        {
            // Unknown private tags can themselves be pointers into RAW data. Recognized
            // standard capture tags are portable; opaque private structures are not.
            if (!include(tag.Type) || tag.Type is < 0 or > ushort.MaxValue
                || tag.Name.StartsWith("Unknown", StringComparison.Ordinal)) continue;
            Entry? entry = FromValue((ushort)tag.Type, directory.GetObject(tag.Type));
            if (entry is not null) entries.Add(entry);
        }
        return entries;
    }

    public ExifProfile CreateProfile(int width, int height, bool exactSrgb)
        => new(ToByteArray(width, height, exactSrgb)[6..]);

    /// <summary>An Exif APP1 payload, also accepted by libheif's EXIF metadata writer.</summary>
    public byte[] ToByteArray(int width, int height, bool exactSrgb)
    {
        var (root, exif, gps) = ForOutput(width, height, exactSrgb);
        return [.. Encoding.ASCII.GetBytes("Exif\0\0"), .. Serialize(root, exif, gps, 0, 0)];
    }

    private (List<Entry> Root, List<Entry> Exif, List<Entry> Gps) ForOutput(int width, int height, bool exactSrgb)
    {
        var root = _root.Where(e => e.Tag is not (256 or 257 or 274 or 305)).ToList();
        root.AddRange([
            Long(256, checked((uint)width)), Long(257, checked((uint)height)),
            Short(274, 1), Ascii(305, TiffIO.SoftwareTag),
        ]);
        var exif = _exif.Where(e => e.Tag is not (0xa001 or 0xa002 or 0xa003 or 0xa500 or 0x9101 or 0x9102)).ToList();
        exif.AddRange([
            Short(0xa001, exactSrgb ? (ushort)1 : ushort.MaxValue),
            Long(0xa002, checked((uint)width)), Long(0xa003, checked((uint)height)),
        ]);
        return (root, exif, new List<Entry>(_gps));
    }

    /// <summary>
    /// Add metadata to a staged classic TIFF/DNG without decoding or re-encoding pixels.
    /// Existing strip/SubIFD offsets and DNG calibration stay absolute and untouched. Only
    /// the root directory moves; the caller still owns atomic replacement of the final file.
    /// </summary>
    public void ApplyToTiff(string path, int width, int height, bool exactSrgb)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (reader.ReadUInt16() != 0x4949 || reader.ReadUInt16() != 42)
            throw new NotSupportedException("EXIF attachment requires the application's little-endian classic TIFF output.");
        stream.Position = reader.ReadUInt32();
        int count = reader.ReadUInt16();
        var original = new Dictionary<ushort, Entry>();
        for (int i = 0; i < count; i++)
        {
            ushort tag = reader.ReadUInt16(), type = reader.ReadUInt16();
            uint components = reader.ReadUInt32();
            byte[] inline = reader.ReadBytes(4);
            int bytes = checked((int)components * TypeSize(type));
            byte[] payload;
            if (bytes <= 4) payload = inline[..bytes];
            else
            {
                long resume = stream.Position;
                stream.Position = BinaryPrimitives.ReadUInt32LittleEndian(inline);
                payload = reader.ReadBytes(bytes);
                if (payload.Length != bytes) throw new EndOfStreamException("Truncated staged TIFF directory.");
                stream.Position = resume;
            }
            original[tag] = new Entry(tag, type, components, payload);
        }
        uint nextIfd = reader.ReadUInt32();
        var (root, exif, gps) = ForOutput(width, height, exactSrgb);
        foreach (Entry entry in root)
        {
            // IFD0 in DNG is the newly generated preview, so keep its own dimensions.
            // An explicit export description also takes precedence over a source caption.
            if (entry.Tag is 256 or 257 or 270 && original.ContainsKey(entry.Tag)) continue;
            original[entry.Tag] = entry;
        }
        if (MergeXmp(null) is { } xmp) original[700] = Bytes(700, 1, xmp);
        original.Remove(34665);
        original.Remove(34853);
        long aligned = (stream.Length + 1) & ~1L;
        uint offset = checked((uint)aligned);
        byte[] metadata = Serialize(original.Values.ToList(), exif, gps, offset, nextIfd);
        stream.Position = aligned;
        stream.Write(metadata);
        stream.Position = 4;
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(checked(offset + 8));
    }

    private sealed record Entry(ushort Tag, ushort Type, uint Count, byte[] Data);
    private static Entry Short(ushort tag, ushort value) => new(tag, 3, 1, BitConverter.GetBytes(value));
    private static Entry Long(ushort tag, uint value) => new(tag, 4, 1, BitConverter.GetBytes(value));
    private static Entry Ascii(ushort tag, string value) => Bytes(tag, 2, Encoding.UTF8.GetBytes(value + '\0'));
    private static Entry Bytes(ushort tag, ushort type, byte[] data) => new(tag, type, (uint)(data.Length / TypeSize(type)), data);

    private static Entry? FromValue(ushort tag, object? value)
    {
        return value switch
        {
            StringValue s => Bytes(tag, 2, [.. s.Bytes, (byte)0]),
            string s => Ascii(tag, s),
            byte b => Bytes(tag, 1, [b]),
            byte[] b => Bytes(tag, tag == 0 ? (ushort)1 : (ushort)7, (byte[])b.Clone()),
            sbyte b => Bytes(tag, 6, [unchecked((byte)b)]),
            sbyte[] b => Bytes(tag, 6, b.Select(v => unchecked((byte)v)).ToArray()),
            ushort v => Short(tag, v),
            ushort[] v => Bytes(tag, 3, v.SelectMany(BitConverter.GetBytes).ToArray()),
            short v => Bytes(tag, 8, BitConverter.GetBytes(v)),
            short[] v => Bytes(tag, 8, v.SelectMany(BitConverter.GetBytes).ToArray()),
            uint v => Long(tag, v),
            uint[] v => Bytes(tag, 4, v.SelectMany(BitConverter.GetBytes).ToArray()),
            int v => Bytes(tag, 9, BitConverter.GetBytes(v)),
            int[] v => Bytes(tag, 9, v.SelectMany(BitConverter.GetBytes).ToArray()),
            float v => Bytes(tag, 11, BitConverter.GetBytes(v)),
            float[] v => Bytes(tag, 11, v.SelectMany(BitConverter.GetBytes).ToArray()),
            double v => Bytes(tag, 12, BitConverter.GetBytes(v)),
            double[] v => Bytes(tag, 12, v.SelectMany(BitConverter.GetBytes).ToArray()),
            Rational v => Rationals(tag, [v]),
            Rational[] v => Rationals(tag, v),
            _ => null,
        };
    }

    private static Entry Rationals(ushort tag, Rational[] values)
    {
        bool signed = tag is 0x9201 or 0x9203 or 0x9204 or 0x9400 or 0x9403 or 0x9405
            || values.Any(v => v.Numerator < 0 || v.Denominator < 0);
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        foreach (var value in values)
        {
            if (signed)
            {
                writer.Write(checked((int)value.Numerator));
                writer.Write(checked((int)value.Denominator));
            }
            else
            {
                writer.Write(checked((uint)value.Numerator));
                writer.Write(checked((uint)value.Denominator));
            }
        }
        return Bytes(tag, signed ? (ushort)10 : (ushort)5, bytes.ToArray());
    }

    private static int TypeSize(ushort type) => type switch
    {
        1 or 2 or 6 or 7 => 1,
        3 or 8 => 2,
        4 or 9 or 11 or 13 => 4,
        5 or 10 or 12 => 8,
        _ => throw new InvalidDataException($"Unsupported EXIF/TIFF data type {type}."),
    };

    private static byte[] Serialize(List<Entry> root, List<Entry> exif, List<Entry> gps, uint origin, uint nextIfd)
    {
        root = new List<Entry>(root);
        if (exif.Count > 0) root.Add(Long(34665, 0));
        if (gps.Count > 0) root.Add(Long(34853, 0));
        var directories = new[] { root, new List<Entry>(exif), new List<Entry>(gps) };
        foreach (var directory in directories) directory.Sort((a, b) => a.Tag.CompareTo(b.Tag));
        uint cursor = 8;
        var offsets = new uint[3];
        for (int i = 0; i < 3; i++)
        {
            if (directories[i].Count == 0) continue;
            offsets[i] = checked(origin + cursor);
            cursor = checked(cursor + (uint)(2 + directories[i].Count * 12 + 4));
        }
        for (int i = 1; i < 3; i++)
        {
            ushort pointer = i == 1 ? (ushort)34665 : (ushort)34853;
            int entry = root.FindIndex(e => e.Tag == pointer);
            if (entry >= 0) root[entry] = Long(pointer, offsets[i]);
        }
        using var data = new MemoryStream();
        using var writer = new BinaryWriter(data);
        writer.Write((ushort)0x4949);
        writer.Write((ushort)42);
        writer.Write(offsets[0]);
        uint valueCursor = checked(origin + cursor);
        for (int i = 0; i < 3; i++)
        {
            if (directories[i].Count == 0) continue;
            writer.Write(checked((ushort)directories[i].Count));
            foreach (Entry entry in directories[i])
            {
                writer.Write(entry.Tag); writer.Write(entry.Type); writer.Write(entry.Count);
                if (entry.Data.Length <= 4)
                {
                    writer.Write(entry.Data);
                    writer.Write(new byte[4 - entry.Data.Length]);
                }
                else
                {
                    writer.Write(valueCursor);
                    valueCursor = checked(valueCursor + (uint)((entry.Data.Length + 1) & ~1));
                }
            }
            writer.Write(i == 0 ? nextIfd : 0u);
        }
        foreach (Entry entry in directories.SelectMany(d => d))
        {
            if (entry.Data.Length <= 4) continue;
            writer.Write(entry.Data);
            if ((entry.Data.Length & 1) != 0) writer.Write((byte)0);
        }
        // A JPEG APP1 has a 16-bit length. Keeping one portable EXIF payload also makes
        // fitting a file-size limit count the exact metadata bytes attached to that file.
        if (origin == 0 && data.Length > 65527)
            throw new InvalidDataException("Source EXIF exceeds the portable JPEG EXIF size limit.");
        return data.ToArray();
    }
}
