namespace OpenRevelare.Core;

/// <summary>
/// Writes the finished positive as a LINEAR DNG — a 16-bit LinearRaw image plus the colour tags
/// that say what its numbers mean.
///
/// WHAT IT IS FOR. Handing the result to Lightroom, Camera Raw or Capture One for the grade. A
/// 16-bit TIFF can do that too, but it arrives as a finished picture: the raw controls are off, the
/// tone curve has already been applied, and highlight recovery has nothing to recover from. A DNG
/// arrives as MATERIAL — the raw panel is live, the white balance slider works, and the host
/// applies its own rendering. What this file carries is the inversion and the grade; what it
/// deliberately does not carry is the display curve.
///
/// WHAT IS IN IT. The pixels are the ordinary SDR render in the roll's output space with that
/// space's encoding curve UNDONE, so they are linear light in those primaries, bounded to [0,1] and
/// quantised to 16 bits. <c>ColorMatrix1</c> states those primaries the way DNG wants them (XYZ D50
/// → this space), and <c>AsShotNeutral</c> is 1,1,1 because the white balance is already in the
/// pixels — the film base was neutralised in the density domain, which is the whole point of the
/// program, and a DNG that claimed a camera white balance on top would have the host undo it.
///
/// WHAT IT IS NOT. Not the scene-linear ACEScg master (that is the float32 TIFF, which keeps values
/// above 1.0 and outside the primaries), and not a repackaged camera RAW — the mosaic is long gone
/// by here, and the demosaic, the lens corrections and the inversion are all baked in.
///
/// STRUCTURE. DNG asks for a reduced-size preview in IFD0 and the real image in a SubIFD, which is
/// also what readers look for first, so that is what this writes: an 8-bit RGB thumbnail in IFD0
/// carrying the colour tags, and the 16-bit LinearRaw in one SubIFD. Both are uncompressed — the
/// file is an intermediate on its way into another program, and a deflate pass would trade its one
/// virtue (nothing between the pixels and the reader) for disk space the user did not ask to save.
/// </summary>
public static class LinearDng
{
    /// <summary>Longest edge of the embedded preview. Big enough for a file browser, small enough
    /// not to matter next to the main image.</summary>
    private const int ThumbnailLongEdge = 256;

    /// <summary>DNG's code for "linear, demosaiced, still raw" — the photometric this file exists
    /// to write. 34892 in the TIFF/EP registry.</summary>
    private const ushort PhotometricLinearRaw = 34892;

    /// <summary>
    /// Write <paramref name="displayReferred"/> — a finished render, encoded with
    /// <paramref name="space"/>'s curve — as a linear DNG at <paramref name="path"/>.
    ///
    /// The buffer is NOT modified: the decode runs on a copy, because callers hand this the same
    /// frame they may still be holding for a thumbnail.
    /// </summary>
    /// <param name="cameraModel">What the file reports as the camera. It is a made-up device by
    /// definition — the pixels went through this program, not through a sensor — so it names the
    /// program and the space, which is the honest answer and also what makes a host's per-camera
    /// defaults land on the right bucket.</param>
    public static void Write(ImageBuffer displayReferred, ColorSpaceDef space, string path,
                             string? cameraModel = null)
    {
        var linear = new ImageBuffer(displayReferred.Width, displayReferred.Height,
                                     (float[])displayReferred.Data.Clone());
        OutputRender.Decode(linear.Data, space);

        ushort[] raw = Quantise16(linear);
        ImageBuffer thumbSource = Resample.ToLongEdge(displayReferred, ThumbnailLongEdge, allowUpscale: false);
        byte[] thumb = Quantise8(thumbSource);

        var ifd0 = new List<Entry>
        {
            Entry.Short(254, 1),                                   // NewSubfileType: reduced-resolution
            Entry.Long(256, (uint)thumbSource.Width),
            Entry.Long(257, (uint)thumbSource.Height),
            Entry.Shorts(258, 8, 8, 8),                            // BitsPerSample
            Entry.Short(259, 1),                                   // Compression: none
            Entry.Short(262, 2),                                   // PhotometricInterpretation: RGB
            Entry.Ascii(271, "OpenRevelare"),                      // Make
            Entry.Ascii(272, cameraModel ?? $"OpenRevelare {space.Name}"),   // Model
            Entry.Placeholder(273, EntryType.Long, 1),             // StripOffsets — patched below
            Entry.Short(277, 3),                                   // SamplesPerPixel
            Entry.Long(278, (uint)thumbSource.Height),             // RowsPerStrip: one strip
            Entry.Long(279, (uint)thumb.Length),                   // StripByteCounts
            Entry.Short(284, 1),                                   // PlanarConfiguration: chunky
            Entry.Placeholder(330, EntryType.Long, 1),             // SubIFDs — patched below
            Entry.Bytes(50706, 1, 4, 0, 0),                        // DNGVersion 1.4.0.0
            Entry.Bytes(50707, 1, 1, 0, 0),                        // DNGBackwardVersion 1.1.0.0
            Entry.Ascii(50708, cameraModel ?? $"OpenRevelare {space.Name}"),  // UniqueCameraModel
            Entry.Rationals(50728, 1d, 1d, 1d),                    // AsShotNeutral: already balanced
            Entry.SignedRationals(50721, ColorMatrixD50(space)),   // ColorMatrix1
            Entry.Short(50778, 23),                                // CalibrationIlluminant1: D50
        };

        var subIfd = new List<Entry>
        {
            Entry.Short(254, 0),                                   // NewSubfileType: the main image
            Entry.Long(256, (uint)linear.Width),
            Entry.Long(257, (uint)linear.Height),
            Entry.Shorts(258, 16, 16, 16),
            Entry.Short(259, 1),
            Entry.Short(262, PhotometricLinearRaw),
            Entry.Placeholder(273, EntryType.Long, 1),             // StripOffsets — patched below
            Entry.Short(277, 3),
            Entry.Long(278, (uint)linear.Height),
            Entry.Long(279, (uint)raw.Length * 2),
            Entry.Short(284, 1),
            Entry.Shorts(339, 1, 1, 1),                            // SampleFormat: unsigned integer
            Entry.Shorts(50713, 1, 1),                             // BlackLevelRepeatDim
            Entry.Rationals(50714, 0d, 0d, 0d),                    // BlackLevel, one per sample
            Entry.Longs(50717, 65535, 65535, 65535),               // WhiteLevel, one per sample
        };

        ifd0.Sort(static (a, b) => a.Tag.CompareTo(b.Tag));        // TIFF requires ascending tags
        subIfd.Sort(static (a, b) => a.Tag.CompareTo(b.Tag));

        ExportFile.Write(path, staging => WriteFile(staging, ifd0, subIfd, thumb, raw));
    }

    /// <summary>
    /// DNG's <c>ColorMatrix1</c>: XYZ (D50, the DNG reference) → this space's linear RGB. It is the
    /// inverse of the space's own primaries matrix, because for this file the "camera" IS the output
    /// space.
    /// </summary>
    private static double[] ColorMatrixD50(ColorSpaceDef space)
    {
        double[,] m = ColorSpaces.Invert3(ColorSpaces.ToXyzD50(space));
        return [m[0, 0], m[0, 1], m[0, 2], m[1, 0], m[1, 1], m[1, 2], m[2, 0], m[2, 1], m[2, 2]];
    }

    private static ushort[] Quantise16(ImageBuffer linear)
    {
        var raw = new ushort[linear.Data.Length];
        for (int i = 0; i < raw.Length; i++)
        {
            float v = linear.Data[i];
            raw[i] = v <= 0f ? (ushort)0
                   : v >= 1f ? (ushort)65535
                   : (ushort)MathF.Round(v * 65535f);
        }
        return raw;
    }

    /// <summary>The preview stays DISPLAY-REFERRED: it is looked at, not computed with.</summary>
    private static byte[] Quantise8(ImageBuffer displayReferred)
    {
        var bytes = new byte[displayReferred.Data.Length];
        for (int i = 0; i < bytes.Length; i++)
        {
            float v = displayReferred.Data[i];
            bytes[i] = v <= 0f ? (byte)0 : v >= 1f ? (byte)255 : (byte)MathF.Round(v * 255f);
        }
        return bytes;
    }

    // ── The TIFF container ────────────────────────────────────────────────────────────────────
    //
    // Written by hand rather than through LibTiff: DNG is almost entirely private tags, and adding
    // twenty of them to LibTiff's field registry costs more code than the eighty lines below, all
    // of it in a place where a mistake is harder to see. The file shape here is fixed — two IFDs,
    // one strip each, little-endian — so the layout can be computed in one pass and written in the
    // next.

    private enum EntryType : ushort { Byte = 1, Ascii = 2, Short = 3, Long = 4, Rational = 5, SignedRational = 10 }

    /// <summary>One IFD entry. <see cref="Payload"/> is the value as bytes; anything over four of
    /// them is written in the value area and referred to by offset.</summary>
    private sealed class Entry(ushort tag, EntryType type, uint count, byte[] payload)
    {
        public ushort Tag { get; } = tag;
        public EntryType Type { get; } = type;
        public uint Count { get; } = count;
        public byte[] Payload { get; set; } = payload;
        public bool Inline => Payload.Length <= 4;

        public static Entry Short(ushort tag, ushort value) => new(tag, EntryType.Short, 1, BitConverter.GetBytes(value));
        public static Entry Shorts(ushort tag, params ushort[] values)
            => new(tag, EntryType.Short, (uint)values.Length, values.SelectMany(BitConverter.GetBytes).ToArray());
        public static Entry Long(ushort tag, uint value) => new(tag, EntryType.Long, 1, BitConverter.GetBytes(value));
        public static Entry Longs(ushort tag, params uint[] values)
            => new(tag, EntryType.Long, (uint)values.Length, values.SelectMany(BitConverter.GetBytes).ToArray());
        public static Entry Bytes(ushort tag, params byte[] values) => new(tag, EntryType.Byte, (uint)values.Length, values);

        public static Entry Ascii(ushort tag, string text)
        {
            byte[] bytes = System.Text.Encoding.ASCII.GetBytes(text + '\0');
            return new Entry(tag, EntryType.Ascii, (uint)bytes.Length, bytes);
        }

        /// <summary>A value only the finished layout knows — a strip or an IFD offset.</summary>
        public static Entry Placeholder(ushort tag, EntryType type, uint count)
            => new(tag, type, count, new byte[4]);

        public static Entry Rationals(ushort tag, params double[] values)
            => new(tag, EntryType.Rational, (uint)values.Length,
                   values.SelectMany(v => Rational(v, signed: false)).ToArray());

        public static Entry SignedRationals(ushort tag, double[] values)
            => new(tag, EntryType.SignedRational, (uint)values.Length,
                   values.SelectMany(v => Rational(v, signed: true)).ToArray());

        /// <summary>
        /// A rational with a fixed denominator of 1 000 000. DNG's colour matrices are small numbers
        /// with no exact fractional form, and six decimal places is finer than the matrices
        /// themselves are known — Adobe's own profiles state them to four.
        /// </summary>
        private static byte[] Rational(double value, bool signed)
        {
            const int denominator = 1_000_000;
            var bytes = new byte[8];
            if (signed) BitConverter.GetBytes((int)Math.Round(value * denominator)).CopyTo(bytes, 0);
            else BitConverter.GetBytes((uint)Math.Round(Math.Max(0d, value) * denominator)).CopyTo(bytes, 0);
            BitConverter.GetBytes((uint)denominator).CopyTo(bytes, 4);
            return bytes;
        }
    }

    private static void WriteFile(string path, List<Entry> ifd0, List<Entry> subIfd,
                                  byte[] thumb, ushort[] raw)
    {
        // ── Layout. Everything is a function of the entry counts, so no seeking back is needed
        // except to fill the placeholders, which happens before a byte is written.
        uint ifd0Offset = 8;
        uint ifd0Size = (uint)(2 + 12 * ifd0.Count + 4);
        uint subIfdOffset = ifd0Offset + ifd0Size;
        uint subIfdSize = (uint)(2 + 12 * subIfd.Count + 4);

        uint cursor = subIfdOffset + subIfdSize;
        foreach (Entry e in ifd0.Concat(subIfd))
        {
            if (e.Inline) continue;
            e.Payload = WordAligned(e.Payload);   // every value offset must be even
            cursor += (uint)e.Payload.Length;
        }

        uint thumbOffset = cursor;
        cursor += (uint)thumb.Length;
        if (cursor % 2 != 0) cursor++;            // the raw strip starts on an even boundary
        uint rawOffset = cursor;

        Patch(ifd0, 330, subIfdOffset);
        Patch(ifd0, 273, thumbOffset);
        Patch(subIfd, 273, rawOffset);

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var w = new BinaryWriter(fs);
        w.Write((byte)'I'); w.Write((byte)'I');
        w.Write((ushort)42);
        w.Write(ifd0Offset);

        uint values = subIfdOffset + subIfdSize;
        values = WriteIfd(w, ifd0, values, nextIfd: 0);
        values = WriteIfd(w, subIfd, values, nextIfd: 0);
        foreach (Entry e in ifd0.Concat(subIfd))
        {
            if (e.Inline) continue;
            w.Write(e.Payload);
        }

        w.Write(thumb);
        if (fs.Position % 2 != 0) w.Write((byte)0);
        foreach (ushort v in raw) w.Write(v);
    }

    /// <summary>Pads a value to an even length: TIFF requires every value offset to be
    /// word-aligned, and the values are laid end to end.</summary>
    private static byte[] WordAligned(byte[] payload)
        => payload.Length % 2 == 0 ? payload : [.. payload, (byte)0];

    private static void Patch(List<Entry> ifd, ushort tag, uint value)
        => ifd.First(e => e.Tag == tag).Payload = BitConverter.GetBytes(value);

    /// <summary>
    /// Writes one IFD, taking <paramref name="valueCursor"/> as the next free byte of the value
    /// area and returning where it ends up — so the second IFD's values follow the first's without
    /// either having to know the other's size.
    /// </summary>
    private static uint WriteIfd(BinaryWriter w, List<Entry> entries, uint valueCursor, uint nextIfd)
    {
        w.Write((ushort)entries.Count);
        foreach (Entry e in entries)
        {
            w.Write(e.Tag);
            w.Write((ushort)e.Type);
            w.Write(e.Count);
            if (e.Inline)
            {
                // Inline values are left-aligned in the four bytes, the rest zero.
                var four = new byte[4];
                e.Payload.CopyTo(four, 0);
                w.Write(four);
            }
            else
            {
                w.Write(valueCursor);
                valueCursor += (uint)e.Payload.Length;
            }
        }
        w.Write(nextIfd);
        return valueCursor;
    }
}
