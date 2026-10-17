using OpenRevelare.ColorManagement;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;

namespace OpenRevelare.Core;

/// <summary>
/// JPEG export via ImageSharp (8-bit sRGB). Port of negative/export.py::export_jpeg.
/// ImageSharp handles 8-bit JPEG cleanly (the 16-bit TIFF limitation that ruled it
/// out for TIFF does not apply here) — TIFF stays on LibTiff, JPEG on ImageSharp,
/// each where it is strong.
///
/// The pipeline hands us data already in the target encoding (sRGB for BASIC), so
/// this only quantises to 8-bit and encodes. 4:4:4 subsampling (no chroma loss).
/// </summary>
public static class JpegIO
{
    /// <summary>Encode to JPEG. Roll annotations are NOT written to EXIF — they are burned into
    /// the contact sheet's info bar instead (see the GUI's SheetInfoBar).
    ///
    /// Staged through <see cref="ExportFile.Write"/> for the same reason as TIFF: ImageSharp's
    /// Save truncates the destination before it has anything to put there.</summary>
    public static void ExportJpeg(ImageBuffer img, string path, int quality = 95,
                                  string? description = null, ColorSpace? icc = null)
        => ExportFile.Write(path, target => WriteJpeg(img, target, quality, description,
               ProfileBytes(icc is ColorSpace c ? TiffIO.Legacy(c) : null)));

    /// <summary>
    /// As above, embedding the profile of any registered space. The caller must have rendered the
    /// pixels into <paramref name="icc"/> already — see <see cref="OutputRender"/>.
    /// </summary>
    public static void ExportJpeg(ImageBuffer img, string path, int quality,
                                  string? description, ColorSpaceDef? icc)
        => ExportFile.Write(path, target => WriteJpeg(
               img, target, quality, description, ProfileBytes(icc)));

    /// <summary>Typed JPEG export with the exact profile bytes carried by the pixels.</summary>
    public static void ExportJpeg(
        RenderedFrame frame,
        string path,
        int quality = 95,
        string? description = null,
        ExportProfilePolicy profilePolicy = ExportProfilePolicy.EmbedExact,
        SourceExif? sourceExif = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Encoding.Reference != ColorReference.DisplayReferred
            || frame.Encoding.Transfer != TransferState.ProfileEncoded
            || frame.Encoding.Range != NumericRange.Normalized)
        {
            throw new NotSupportedException(
                "JPEG export requires normalized, display-referred, profile-encoded pixels; " +
                "scene-linear JPEG is not supported.");
        }

        byte[]? profileBytes = ExportColorPolicy.ResolveProfileBytes(frame, profilePolicy);
        ExportFile.Write(path, target => WriteJpeg(
            frame.Pixels, target, quality, description, profileBytes, sourceExif));
    }

    /// <summary>
    /// <see cref="ExportJpeg(RenderedFrame, string, int, string?, ExportProfilePolicy)"/> under a
    /// byte ceiling: the file is encoded to memory and, when it is over
    /// <paramref name="maxBytes"/>, re-encoded at a lower quality, then at a smaller size, until
    /// it fits — see <see cref="FitToSize"/> for the order. Returns what was actually written.
    /// </summary>
    public static JpegFit ExportJpeg(
        RenderedFrame frame,
        string path,
        int quality,
        long maxBytes,
        string? description = null,
        ExportProfilePolicy profilePolicy = ExportProfilePolicy.EmbedExact,
        SourceExif? sourceExif = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Encoding.Reference != ColorReference.DisplayReferred
            || frame.Encoding.Transfer != TransferState.ProfileEncoded
            || frame.Encoding.Range != NumericRange.Normalized)
        {
            throw new NotSupportedException(
                "JPEG export requires normalized, display-referred, profile-encoded pixels; " +
                "scene-linear JPEG is not supported.");
        }

        byte[]? profileBytes = ExportColorPolicy.ResolveProfileBytes(frame, profilePolicy);
        ImageBuffer full = frame.Pixels;
        // One downsampled copy per size step, reused across the quality search at that step.
        ImageBuffer scaled = full;
        Image? prepared = null;
        try
        {
            var (file, fit) = FitToSize(full.Width, full.Height, quality, maxBytes, (maxEdge, q) =>
            {
                if (Math.Max(scaled.Width, scaled.Height) > maxEdge)
                {
                    prepared?.Dispose();
                    prepared = null;
                    scaled = Resample.ToLongEdge(full, maxEdge, allowUpscale: false);
                }
                prepared ??= PrepareImage(scaled, description, profileBytes, xmp: null, sourceExif: sourceExif);
                return Encode(prepared, q);
            });
            ExportFile.Write(path, destination => File.WriteAllBytes(destination, file));
            return fit;
        }
        finally { prepared?.Dispose(); }
    }

    /// <summary>
    /// What a size-limited JPEG export actually wrote, against what was asked for: the quality
    /// used, the long edge the picture ended up at (equal to the source's when it was not
    /// shrunk for size), and the file's length.
    /// </summary>
    public sealed record JpegFit(int Quality, int LongEdge, long Bytes)
    {
        /// <summary>The quality the export was asked for, before any reduction.</summary>
        public int RequestedQuality { get; init; }

        /// <summary>The long edge the export would have had without the size limit.</summary>
        public int RequestedLongEdge { get; init; }

        public bool QualityReduced => Quality < RequestedQuality;
        public bool Shrunk => LongEdge < RequestedLongEdge;
    }

    /// <summary>The lowest quality the size fit will go to before it starts shrinking the
    /// picture instead. The same floor as the export dialog's slider: below it, JPEG artefacts
    /// cost more than the pixels a smaller picture gives up.</summary>
    public const int MinFitQuality = 40;

    /// <summary>
    /// Finds an encode of at most <paramref name="maxBytes"/>. <paramref name="encode"/> is
    /// called with a long-edge ceiling (the source's own edge means "unscaled") and a quality,
    /// and must return the complete file.
    ///
    /// <para>
    /// Order: quality first, size second. At the requested size the quality is walked down by
    /// bisection to <see cref="MinFitQuality"/>; only when the floor is still too big does the
    /// picture shrink one integer box factor, and the quality search starts over from the
    /// requested value at the new size. So the result is the LARGEST picture at which the floor
    /// quality fits, at the HIGHEST quality that fits at that size — the fewest pixels given up
    /// for the ceiling. Every encode is a full pass over the image, so the search is kept to a
    /// handful: at most 2 + log2(quality range) encodes per size step.
    /// </para>
    /// </summary>
    public static (byte[] File, JpegFit Fit) FitToSize(
        int width,
        int height,
        int quality,
        long maxBytes,
        Func<int, int, byte[]> encode,
        string formatName = "JPEG")
    {
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        int sourceEdge = Math.Max(width, height);
        int requested = Math.Clamp(quality, MinFitQuality, 100);

        for (int factor = 1; ; factor++)
        {
            // The search ladder: the source edge divided by 1, 2, 3 … Each step is hit EXACTLY by
            // Resample.ToLongEdge, so the size the fit reports is the size the file has — it used
            // to be an integer box factor, which landed at or under the step and made "长边 2048 +
            // 不超过 10 MB" produce a number neither option had asked for.
            int maxEdge = (sourceEdge + factor - 1) / factor;
            if (factor > 1 && (width / factor < 1 || height / factor < 1 || maxEdge < 64))
            {
                throw new InvalidOperationException(
                    $"Cannot fit the {formatName} under {maxBytes} bytes even at quality {MinFitQuality} " +
                    $"and a long edge of {(sourceEdge + factor - 2) / (factor - 1)} px.");
            }
            int edge = Math.Min(sourceEdge, maxEdge);

            byte[] best = encode(maxEdge, requested);
            if (best.Length <= maxBytes) return (best, Result(requested, best));
            if (requested == MinFitQuality) continue; // the failed probe already WAS the floor

            byte[] floor = encode(maxEdge, MinFitQuality);
            if (floor.Length > maxBytes) continue;   // even the floor is too big: shrink

            // Highest fitting quality in [MinFitQuality, requested): floor fits, requested does not.
            int lo = MinFitQuality, hi = requested;
            best = floor;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                byte[] candidate = encode(maxEdge, mid);
                if (candidate.Length <= maxBytes) { lo = mid; best = candidate; }
                else hi = mid;
            }
            return (best, Result(lo, best));

            JpegFit Result(int q, byte[] file) => new(q, edge, file.LongLength)
            {
                RequestedQuality = requested,
                RequestedLongEdge = sourceEdge,
            };
        }
    }

    /// <summary>
    /// A gain-map JPEG (ISO 21496-1 / Adobe gain map): <paramref name="sdrBase"/> as an ordinary
    /// JPEG any reader shows, plus the map that takes it back to <paramref name="hdr"/> on a
    /// display with headroom. See <see cref="HdrGainMap"/> for what relates the two and
    /// <see cref="GainMapJpegContainer"/> for how the file is put together.
    ///
    /// <para>
    /// The base is encoded exactly as <see cref="ExportJpeg(RenderedFrame, string, int, string?, ExportProfilePolicy)"/>
    /// would encode it on its own — same quantisation, same EXIF, same ICC policy — so a reader
    /// that ignores the map sees the SDR export, not an approximation of it. The map's metadata
    /// is written both as <c>hdrgm</c> XMP and as the ISO 21496-1 segment. The map is a
    /// per-channel map (<see cref="GainMapChannels.PerChannel"/>), encoded as a full-resolution
    /// RGB JPEG without chroma subsampling. Each channel must recover the HDR rendition's own
    /// value: a luminance-only map retains the SDR base's highlight colour instead, which can
    /// differ from the HDR preview after the two renditions' shoulders and gamut mapping.
    /// </para>
    /// </summary>
    public static void ExportGainMapJpeg(
        RenderedFrame sdrBase,
        RenderedFrame hdr,
        ColorSpaceDef baseSpace,
        OutputTarget target,
        string path,
        int quality = 95,
        string? description = null,
        ExportProfilePolicy profilePolicy = ExportProfilePolicy.EmbedExact,
        SourceExif? sourceExif = null)
    {
        byte[]? profileBytes = ExportColorPolicy.ResolveProfileBytes(sdrBase, profilePolicy);
        byte[] file = EncodeGainMapJpeg(
            sdrBase, hdr, baseSpace, target, quality, description, profileBytes, sourceExif);
        ExportFile.Write(path, destination => File.WriteAllBytes(destination, file));
    }

    /// <summary>
    /// <see cref="ExportGainMapJpeg(RenderedFrame, RenderedFrame, ColorSpaceDef, OutputTarget, string, int, string?, ExportProfilePolicy)"/>
    /// under a byte ceiling, by the same search as the plain export
    /// (<see cref="FitToSize"/>). The ceiling is on the WHOLE file — base plus map — since that is
    /// what a size limit means to whoever set it; both streams share one quality, and a size step
    /// shrinks both renditions together and recomputes the map from the shrunk pair, so the map
    /// keeps describing the base it is attached to.
    /// </summary>
    public static JpegFit ExportGainMapJpeg(
        RenderedFrame sdrBase,
        RenderedFrame hdr,
        ColorSpaceDef baseSpace,
        OutputTarget target,
        string path,
        int quality,
        long maxBytes,
        string? description = null,
        ExportProfilePolicy profilePolicy = ExportProfilePolicy.EmbedExact,
        SourceExif? sourceExif = null)
    {
        byte[]? profileBytes = ExportColorPolicy.ResolveProfileBytes(sdrBase, profilePolicy);
        RenderedFrame scaledBase = sdrBase, scaledHdr = hdr;
        PreparedGainMap? prepared = null;
        try
        {
            var (file, fit) = FitToSize(
                sdrBase.Pixels.Width, sdrBase.Pixels.Height, quality, maxBytes, (maxEdge, q) =>
            {
                if (Math.Max(scaledBase.Pixels.Width, scaledBase.Pixels.Height) > maxEdge)
                {
                    prepared?.Dispose();
                    prepared = null;
                    // Both renditions are always resampled from their originals.
                    scaledBase = sdrBase.WithPixels(
                        Resample.ToLongEdge(sdrBase.Pixels, maxEdge, allowUpscale: false));
                    scaledHdr = hdr.WithPixels(
                        Resample.ToLongEdge(hdr.Pixels, maxEdge, allowUpscale: false));
                }
                prepared ??= new PreparedGainMap(scaledBase, scaledHdr, baseSpace, target, description, profileBytes, sourceExif);
                return prepared.Encode(q);
            });
            ExportFile.Write(path, destination => File.WriteAllBytes(destination, file));
            return fit;
        }
        finally { prepared?.Dispose(); }
    }

    /// <summary>The complete gain-map file for one base/HDR pair at one quality.</summary>
    private static byte[] EncodeGainMapJpeg(
        RenderedFrame sdrBase,
        RenderedFrame hdr,
        ColorSpaceDef baseSpace,
        OutputTarget target,
        int quality,
        string? description,
        byte[]? profileBytes,
        SourceExif? sourceExif)
    {
        using var prepared = new PreparedGainMap(sdrBase, hdr, baseSpace, target, description, profileBytes, sourceExif);
        return prepared.Encode(quality);
    }

    /// <summary>One resolution's gain calculation, quantized images and fixed metadata.
    /// Only the compressed streams and the base's final-length XMP vary with quality.</summary>
    private sealed class PreparedGainMap : IDisposable
    {
        private readonly Image _primary, _gain;
        private readonly GainMapMetadata _metadata;
        private readonly SourceExif? _sourceExif;

        internal PreparedGainMap(RenderedFrame sdrBase, RenderedFrame hdr, ColorSpaceDef baseSpace,
            OutputTarget target, string? description, byte[]? profileBytes, SourceExif? sourceExif)
        {
            HdrGainMap map = HdrGainMap.Compute(sdrBase, hdr, baseSpace, target,
                GainMapChannels.PerChannel, useCommonChannelRange: true);
            _metadata = map.Metadata;
            _sourceExif = sourceExif;
            _primary = PrepareImage(sdrBase.Pixels, description, profileBytes, xmp: null, sourceExif: sourceExif);
            try
            {
                _gain = PrepareImage(map.Map, description: null, iccBytes: null,
                    xmp: GainMapJpegContainer.GainMapXmp(map.Metadata),
                    grayscale: map.Channels == GainMapChannels.Luminance);
            }
            catch { _primary.Dispose(); throw; }
        }

        internal byte[] Encode(int quality)
        {
            byte[] gainMapJpeg = GainMapJpegContainer.WithIsoMetadata(JpegIO.Encode(_gain, quality), _metadata);
            byte[] xmp = GainMapJpegContainer.PrimaryXmp(gainMapJpeg.Length);
            _primary.Metadata.XmpProfile = new SixLabors.ImageSharp.Metadata.Profiles.Xmp.XmpProfile(
                _sourceExif?.MergeXmp(xmp) ?? xmp);
            return GainMapJpegContainer.Compose(JpegIO.Encode(_primary, quality), gainMapJpeg);
        }

        public void Dispose() { _primary.Dispose(); _gain.Dispose(); }
    }

    private static void WriteJpeg(ImageBuffer img, string path, int quality, string? description,
                                  byte[]? iccBytes, SourceExif? sourceExif = null)
        => File.WriteAllBytes(path, Encode(img, quality, description, iccBytes, xmp: null, sourceExif: sourceExif));

    /// <summary>
    /// One JPEG stream. <paramref name="grayscale"/> encodes the first channel only, as a
    /// single-component JPEG — for a luminance gain map, whose three channels are equal.
    /// </summary>
    private static byte[] Encode(ImageBuffer img, int quality, string? description,
                                 byte[]? iccBytes, byte[]? xmp, bool grayscale = false, SourceExif? sourceExif = null)
    {
        using Image prepared = PrepareImage(img, description, iccBytes, xmp, grayscale, sourceExif);
        return Encode(prepared, quality);
    }

    private static Image PrepareImage(ImageBuffer img, string? description,
        byte[]? iccBytes, byte[]? xmp, bool grayscale = false, SourceExif? sourceExif = null)
    {
        int w = img.Width, h = img.Height;
        float[] src = img.Data;
        Image image = grayscale ? new Image<L8>(w, h) : new Image<Rgb24>(w, h);
        try
        {
            var exif = sourceExif?.CreateProfile(w, h, exactSrgb: iccBytes is null
                || iccBytes.AsSpan().SequenceEqual(IccProfiles.Build(ColorSpaces.Srgb))) ?? new ExifProfile();
            exif.SetValue(ExifTag.Software, TiffIO.SoftwareTag);
            if (!string.IsNullOrEmpty(description))
            {
                // ImageDescription (270) is ASCII per spec, so it gets the folded copy and the full
                // Unicode text goes to UserComment (37510) — same split as TiffIO, for the same reason.
                exif.SetValue(ExifTag.ImageDescription, TiffIO.ToAsciiSafe(description));
                exif.SetValue(ExifTag.UserComment,
                    new EncodedString(EncodedString.CharacterCode.Unicode, description));
            }
            image.Metadata.ExifProfile = exif;
            // Same profile the TIFF path embeds, from the same builder — an export dialog that offers
            // one "embed ICC" switch must mean the same thing in both containers.
            if (iccBytes is { Length: > 0 })
                image.Metadata.IccProfile = new SixLabors.ImageSharp.Metadata.Profiles.Icc.IccProfile(
                    iccBytes);
            xmp = sourceExif?.MergeXmp(xmp) ?? xmp;
            if (xmp is { Length: > 0 })
                image.Metadata.XmpProfile = new SixLabors.ImageSharp.Metadata.Profiles.Xmp.XmpProfile(xmp);

            if (image is Image<L8> gray)
            {
                void Fill(int y)
                {
                    Span<L8> row = gray.DangerousGetPixelRowMemory(y).Span;
                    int o = y * w * 3;
                    for (int x = 0; x < w; x++) row[x] = new L8(To8(src[o + x * 3]));
                }
                FillRows(w, h, Fill);
            }
            else
            {
                var rgb = (Image<Rgb24>)image;
                void Fill(int y)
                {
                    Span<Rgb24> row = rgb.DangerousGetPixelRowMemory(y).Span;
                    int o = y * w * 3;
                    for (int x = 0; x < w; x++)
                    {
                        int j = o + x * 3;
                        row[x] = new Rgb24(To8(src[j]), To8(src[j + 1]), To8(src[j + 2]));
                    }
                }
                FillRows(w, h, Fill);
            }

            return image;
        }
        catch { image.Dispose(); throw; }
    }

    private static void FillRows(int width, int height, Action<int> fill)
    {
        // The image's storage is fixed before workers start; each owns disjoint rows.
        if ((long)width * height >= 262144) Parallel.For(0, height, fill);
        else for (int y = 0; y < height; y++) fill(y);
    }

    private static byte[] Encode(Image image, int quality)
    {
        var encoder = new JpegEncoder
        {
            Quality = quality,
            // 4:4:4 — no chroma subsampling; a single-component stream for the grayscale map.
            ColorType = image is Image<L8> ? JpegEncodingColor.Luminance : JpegEncodingColor.YCbCrRatio444,
        };
        using var stream = new MemoryStream();
        image.Save(stream, encoder);
        return stream.ToArray();
    }

    private static byte To8(float v)
    {
        float c = v < 0.0f ? 0.0f : (v > 1.0f ? 1.0f : v);
        return (byte)(c * 255.0f + 0.5f);
    }

    private static byte[]? ProfileBytes(ColorSpaceDef? space) =>
        space is ColorSpaceDef value ? IccProfiles.Build(value) : null;
}
