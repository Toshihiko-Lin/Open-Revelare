using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using BitMiracle.LibTiff.Classic;
using OpenRevelare.Core;

namespace OpenRevelare.Gui.Services;

// Interchange format from PURENeg src/review.cjs, writeTrainingPack (MIT).
// OpenRevelare edits pixels rather than excluding region IDs: removals are erasures.
internal static class PureNegTrainingPack
{
    internal sealed record Labels(bool Clean = false, bool Test = false);

    public static string Export(string parent, string source, DustMask automatic, DustMask final,
        PureNegComponent.ReviewImage review, IReadOnlySet<int> visibleIds, Labels labels, CancellationToken ct,
        DustReviewProjection.GeometryKey? geometry = null, IReadOnlyList<DustMaskEditor.Stroke>? history = null)
    {
        if (automatic.Width != final.Width || automatic.Height != final.Height || automatic.Bounds != final.Bounds ||
            review.Width != final.Width || review.Height != final.Height)
            throw new ArgumentException("Training masks must use the detector source grid.");
        // Publish only a complete pack. Never overwrite an existing contribution.
        string id = "openrevelare-" + Guid.NewGuid().ToString("N");
        string stage = Path.Combine(parent, "." + id + ".partial");
        string destination = Path.Combine(parent, id + "-training");
        Directory.CreateDirectory(stage);
        try
        {
            ct.ThrowIfCancellationRequested();
            File.Copy(source, Path.Combine(stage, "source.tiff"));
            string hash;
            using (var snapshot = File.OpenRead(Path.Combine(stage, "source.tiff")))
                hash = Convert.ToHexString(SHA256.HashData(snapshot)).ToLowerInvariant();
            if (review.TrainingMetadata is { } metadata && metadata.TryGetProperty("sourceSha256", out var expected) &&
                !hash.Equals(expected.GetString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Detector source snapshot changed after detection.");
            var checks = DustTrainingSnapshot.WriteAndCheck(Path.Combine(stage, "source.tiff"),
                Path.Combine(stage, "repair-preview.tiff"), final, ct);
            if (!checks.Usable) throw new InvalidDataException("Repair snapshot changed pixels outside the final mask.");
            if (history is not null)
            {
                var replay = new DustMaskEditor(automatic);
                foreach (var stroke in history)
                {
                    ct.ThrowIfCancellationRequested();
                    replay.Paint(stroke.Brushes, stroke.Kind == "erase");
                }
                if (replay.Mask.ContentHash != final.ContentHash)
                    throw new InvalidDataException("Brush history does not reproduce the final mask.");
                File.WriteAllText(Path.Combine(stage, "strokes.json"), JsonSerializer.Serialize(new
                {
                    schema = "openrevelare-source-ellipse-strokes", version = 1,
                    strokes = history.Select(s => new { kind = s.Kind, brushes = s.Brushes.Select(b =>
                        new { x = b.X, y = b.Y, ax = b.Ax, ay = b.Ay, bx = b.Bx, by = b.By }) }),
                }));
            }
            var probability = new Dictionary<int, (long Sum, int Count, byte Max)>();
            bool hasMap = review.FibreMapPath is not null;
            if (hasMap)
            {
                using var map = File.OpenRead(review.FibreMapPath!);
                if (map.Length != (long)final.Width * final.Height || review.FibreMapSha256 is null ||
                    !Convert.ToHexString(SHA256.HashData(map)).Equals(review.FibreMapSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Fibre probability map does not match the detection snapshot.");
                map.Position = 0;
                using (var output = File.Create(Path.Combine(stage, "fibre-map.u8.gz")))
                using (var gzip = new GZipStream(output, CompressionLevel.Optimal)) map.CopyTo(gzip);
                byte[] samples = new byte[final.Width];
                foreach (var candidate in PureNegComponent.ReadRuns(review, ct))
                {
                    ct.ThrowIfCancellationRequested();
                    var run = candidate.Run;
                    map.Position = (long)run.Y * final.Width + run.Start;
                    map.ReadExactly(samples.AsSpan(0, run.End - run.Start));
                    var stat = probability.GetValueOrDefault(candidate.RegionId);
                    for (int i = 0; i < run.End - run.Start; i++)
                    {
                        stat.Sum += samples[i]; stat.Count++; stat.Max = Math.Max(stat.Max, samples[i]);
                    }
                    probability[candidate.RegionId] = stat;
                }
            }
            var autoRuns = automatic.EnumerateRuns().ToLookup(r => r.Y);
            var finalRuns = final.EnumerateRuns().ToLookup(r => r.Y);
            var counts = new Dictionary<string, long>();
            foreach (string layer in new[] { "auto", "undone", "erased", "added", "final" })
            {
                string path = Path.Combine(stage, "mask-" + layer + ".tiff");
                long count = 0;
                using (var tiff = Tiff.Open(path, "w") ?? throw new IOException("Cannot create training mask."))
                {
                    tiff.SetField(TiffTag.IMAGEWIDTH, final.Width);
                    tiff.SetField(TiffTag.IMAGELENGTH, final.Height);
                    tiff.SetField(TiffTag.SAMPLESPERPIXEL, 1);
                    tiff.SetField(TiffTag.BITSPERSAMPLE, 16);
                    tiff.SetField(TiffTag.PHOTOMETRIC, Photometric.MINISBLACK);
                    tiff.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
                    tiff.SetField(TiffTag.ORIENTATION, Orientation.TOPLEFT);
                    tiff.SetField(TiffTag.COMPRESSION, Compression.NONE);
                    tiff.SetField(TiffTag.ROWSPERSTRIP, 1);
                    var a = new bool[final.Width];
                    var f = new bool[final.Width];
                    var row = new byte[checked(final.Width * 2)];
                    for (int y = 0; y < final.Height; y++)
                    {
                        ct.ThrowIfCancellationRequested();
                        Array.Clear(a); Array.Clear(f); Array.Clear(row);
                        foreach (var run in autoRuns[y]) Array.Fill(a, true, run.Start, run.End - run.Start);
                        foreach (var run in finalRuns[y]) Array.Fill(f, true, run.Start, run.End - run.Start);
                        for (int x = 0; x < final.Width; x++)
                        {
                            bool selected = layer switch
                            {
                                "auto" => a[x], "final" => f[x], "added" => !a[x] && f[x],
                                "erased" => a[x] && !f[x], _ => false,
                            };
                            if (!selected) continue;
                            row[x * 2] = row[x * 2 + 1] = 255;
                            count++;
                        }
                        if (!tiff.WriteScanline(row, y)) throw new IOException("Cannot write training mask.");
                    }
                }
                counts[layer] = count;
                using (var input = File.OpenRead(path))
                using (var output = File.Create(path + ".gz"))
                using (var gzip = new GZipStream(output, CompressionLevel.Optimal)) input.CopyTo(gzip);
                File.Delete(path);
            }
            var pack = new
            {
                schema = "pureneg-training-pack", version = 1, app = "OpenRevelare " + AppInfo.Version,
                createdAt = DateTimeOffset.UtcNow, sourceName = "source.tiff", width = final.Width, height = final.Height,
                depth = 16, channels = 3, sourceSha256 = hash,
                detectionSettings = review.TrainingMetadata?.GetProperty("settings"),
                labels = new { clean = labels.Clean, test = labels.Test },
                timings = review.TrainingMetadata?.GetProperty("timings"),
                checks = new { sameSizeAsExport = checks.SameSizeAsExport,
                    changedOutsideMask = checks.ChangedOutsideMask, usable = checks.Usable },
                pixels = counts, strokes = Array.Empty<object>(),
                fibreMap = hasMap ? new { file = "fibre-map.u8.gz", format = "gzip of uint8, row-major, width x height, 0-255 = probability" } : null,
                regions = review.Regions.Select(r => new { id = r.Id, x = r.X, y = r.Y, width = r.Width,
                    height = r.Height, pixels = r.Pixels, kinds = r.Kinds, kept = visibleIds.Contains(r.Id),
                    probMean = probability.TryGetValue(r.Id, out var p) && p.Count > 0 ? (int?)Math.Round((double)p.Sum / p.Count, MidpointRounding.AwayFromZero) : null,
                    probMax = probability.TryGetValue(r.Id, out p) ? (int?)p.Max : null }),
                marks = review.TrainingMetadata?.GetProperty("marks"),
                layers = new Dictionary<string, string>
                {
                    ["source.tiff"] = "Unrepaired detector input (rendered SDR positive, original detector grid)",
                    ["repair-preview.tiff"] = "Core dust repair applied on the encoded detector grid; checked against source outside final mask",
                    ["mask-auto.tiff.gz"] = "Automatic repairs within the review scope",
                    ["mask-undone.tiff.gz"] = "Region exclusions (empty; this editor uses pixel erasures)",
                    ["mask-erased.tiff.gz"] = "Automatic pixels removed by the user",
                    ["mask-added.tiff.gz"] = "Pixels added by the user",
                    ["mask-final.tiff.gz"] = "Final repair selection; gzip of 16-bit grayscale TIFF",
                },
                openRevelare = new { detectorRevision = PureNegComponent.Revision, annotationOnly = false,
                    inference = review.TrainingMetadata is { } engine && engine.TryGetProperty("inference", out var info) ? (JsonElement?)info : null,
                    checkedImage = "repair-preview.tiff", repairSpace = "encoded detector SDR RGB; not the colour-managed final photo export",
                    strokeHistory = history is null ? null : new { file = "strokes.json", format = "openrevelare-source-ellipse-strokes", version = 1, replayVerified = true },
                    fibreMapStatus = hasMap ? "available" : OperatingSystem.IsMacOS() ? "upstream-codec-or-model-unavailable" : "upstream-coreml-requires-macos",
                    manualChanges = automatic.ContentHash != final.ContentHash,
                    sourceBounds = new { x = final.Bounds.X, y = final.Bounds.Y, w = final.Bounds.W, h = final.Bounds.H },
                    reviewGeometry = geometry is { } g ? new { turns = g.Turns, flipH = g.FlipH, flipV = g.FlipV,
                        rotation = g.Rotation, distortion = g.Distortion,
                        crop = g.Crop is { } c ? new { x = c.X, y = c.Y, w = c.W, h = c.H } : null,
                        opticalCorrection = g.EmbeddedLens is not null || g.Lensfun is not null } : null,
                    protectedRegionIds = review.Regions.Where(r => !visibleIds.Contains(r.Id)).Select(r => r.Id),
                    note = "Unmarked pixels are not certified clean. Source ellipse history is lossless; upstream circle strokes are not substituted. kept describes review visibility, not a human region verdict." },
            };
            File.WriteAllText(Path.Combine(stage, "pack.json"), JsonSerializer.Serialize(pack,
                new JsonSerializerOptions { WriteIndented = true }));
            ct.ThrowIfCancellationRequested();
            Directory.Move(stage, destination);
            return destination;
        }
        finally { PureNegComponent.DeleteTemporaryDirectory(stage); }
    }
}
