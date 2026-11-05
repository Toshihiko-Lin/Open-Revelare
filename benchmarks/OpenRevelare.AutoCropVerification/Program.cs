using System.Text.Json;
using OpenRevelare.Core;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: dotnet run --project benchmarks/OpenRevelare.AutoCropVerification -- <RAW directory> <output directory> [--project=<ncproj>] [--preview-edge=<px>] [--cache=<directory>] [--only=<stem>] [--independent]");
    return 2;
}
string input = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]);
if (input == output) throw new ArgumentException("Choose a separate output directory.");
Directory.CreateDirectory(output);
string[] paths = Directory.GetFiles(input).Where(RawDecode.IsRawExtension).Order(StringComparer.Ordinal).ToArray();
string? only = args.FirstOrDefault(a => a.StartsWith("--only="))?[7..];
if (only is not null) paths = paths.Where(p => Path.GetFileNameWithoutExtension(p).Equals(only, StringComparison.OrdinalIgnoreCase)).ToArray();
string? cache = args.FirstOrDefault(a => a.StartsWith("--cache="))?[8..];
if (cache is not null) Directory.CreateDirectory(cache);
string? projectPath = args.FirstOrDefault(a => a.StartsWith("--project="))?[10..];
string? edgeArg = args.FirstOrDefault(a => a.StartsWith("--preview-edge="))?[15..];
int previewEdge = edgeArg is null ? 1600 : int.Parse(edgeArg);
var project = projectPath is null ? null : Project.Load(projectPath);
var initial = new List<FrameParams>();
var detections = new FilmAutoCrop.Detection?[paths.Length];
// Yellow boxes are diagnostic suggestions only. The GUI never applies them by default.
var suggestions = new FilmAutoCrop.Rect?[paths.Length];
var suggestedRatios = new double?[paths.Length];
var suggestedAngles = new double?[paths.Length];
var previews = new List<ImageBuffer>();
var preparedFrames = new List<ImageBuffer>();
var sourceSizes = new List<(int Width, int Height)>();
for (int i = 0; i < paths.Length; i++)
{
    var (raw, width, height) = ReadPreview(paths[i], previewEdge, cache);
    sourceSizes.Add((width, height));
    var p = project?.Frames.FirstOrDefault(f => Path.GetFileName(f.SourcePath) == Path.GetFileName(paths[i]))?.Params.Clone() ?? new FrameParams();
    initial.Add(p);
    var prepared = FilmAutoCrop.Prepare(raw, p);
    preparedFrames.Add(prepared);
    var d = FilmAutoCrop.Detect(prepared);
    bool swap = ((p.QuarterTurns % 2) + 2) % 2 == 1;
    detections[i] = d is null ? null : d with { Width = swap ? height : width, Height = swap ? width : height, AppliedRotation = p.Rotation };
    FilmAutoCrop.Suggestion? suggestion = null;
    if (d is null)
    {
        suggestion = FilmAutoCrop.Suggest(prepared, FilmCropMode.ImageArea);
        suggestions[i] = suggestion?.Rect;
        suggestedAngles[i] = suggestion?.RotationCorrection;
    }
    // Draw in the same frame the rectangle belongs to: a suggestion reports the angle that levels
    // the strip, so the preview must be turned by it or the yellow box looks crooked on the film.
    var preview = Geometry.ApplyRotation(prepared, d?.RotationCorrection ?? suggestion?.RotationCorrection ?? 0, fill: 0);
    if (suggestion is { } placed)
        suggestedRatios[i] = Math.Round(placed.Rect.W * preview.Width / (placed.Rect.H * preview.Height), 6);
    previews.Add(preview);
    Console.WriteLine($"{i + 1}/{paths.Length} {Path.GetFileName(paths[i])}: " +
        (d is null ? suggestions[i] is null ? "review" : "review (suggested)" : $"{d.Polarity} ruler={d.HasPerforationRuler} angle={d.RotationCorrection:F3} box={d.Image}"));
}
var independent = detections.ToArray();
if (!args.Contains("--independent"))
{
    var references = FilmAutoCrop.BuildRollReferences(detections);
    Console.WriteLine("References: " + JsonSerializer.Serialize(references));
    for (int i = 0; i < detections.Length; i++)
    {
        if (references.Count == 0 || !FilmAutoCrop.NeedsRollReference(detections[i], references)) continue;
        var sizes = sourceSizes[i];
        bool swap = ((initial[i].QuarterTurns % 2) + 2) % 2 == 1;
        detections[i] = FilmAutoCrop.DetectWithRollReference(preparedFrames[i], references,
            swap ? sizes.Height : sizes.Width, swap ? sizes.Width : sizes.Height, initial[i].Rotation);
        if (detections[i] is not null)
        { suggestions[i] = null; suggestedRatios[i] = null; suggestedAngles[i] = null; }
        previews[i] = Geometry.ApplyRotation(preparedFrames[i], detections[i]?.RotationCorrection ?? suggestedAngles[i] ?? 0, fill: 0);
        Console.WriteLine($"Cross-check {Path.GetFileName(paths[i])}: {detections[i]}");
    }
}
var content = FilmAutoCrop.Plan(detections, FilmCropMode.ImageArea);
var sprockets = FilmAutoCrop.Plan(detections, FilmCropMode.WithSprockets);
var rows = new List<object>();
using var overview = new Image<Rgb24>(4 * 360, ((paths.Length + 3) / 4) * 240);
for (int i = 0; i < paths.Length; i++)
{
    var pixels = Resample.Box(previews[i], 720);
    using var view = new Image<Rgb24>(pixels.Width, pixels.Height);
    double white = pixels.Data.Where(float.IsFinite).Order().ElementAt((int)(pixels.Data.Length * .995));
    for (int y = 0; y < pixels.Height; y++) for (int x = 0; x < pixels.Width; x++)
    {
        int p = (y * pixels.Width + x) * 3;
        byte Channel(int c) => (byte)(255 * Math.Pow(Math.Clamp(pixels.Data[p + c] / Math.Max(white, 1e-6), 0, 1), 1 / 2.2));
        view[x, y] = new Rgb24(Channel(0), Channel(1), Channel(2));
    }
    Draw(content[i], new Rgb24(0, 255, 80));
    Draw(sprockets[i], new Rgb24(0, 200, 255));
    Draw(suggestions[i], new Rgb24(255, 220, 0));   // yellow: a guess, not a detection
    view.SaveAsJpeg(Path.Combine(output, Path.GetFileNameWithoutExtension(paths[i]) + ".jpg"));
    for (int y = 0; y < 240; y++) for (int x = 0; x < 360; x++)
        overview[(i % 4) * 360 + x, (i / 4) * 240 + y] = view[x * view.Width / 360, y * view.Height / 240];
    var d = detections[i];
    rows.Add(new { File = Path.GetFileName(paths[i]), InitialRotation=initial[i].Rotation, initial[i].DistortionK1, initial[i].VignetteAmount, initial[i].FlipH, initial[i].FlipV, initial[i].QuarterTurns, Independent = independent[i], Detection = d, Content = content[i], Sprockets = sprockets[i],
        ContentPixels = Size(content[i], d), SprocketPixels = Size(sprockets[i], d), Suggestion = suggestions[i], SuggestedRatio = suggestedRatios[i], SuggestedRotation = suggestedAngles[i] });

    void Draw(FilmAutoCrop.Rect? rect, Rgb24 color)
    {
        if (rect is not { } r) return;
        int x0 = Math.Clamp((int)(r.X * view.Width), 0, view.Width - 1);
        int y0 = Math.Clamp((int)(r.Y * view.Height), 0, view.Height - 1);
        int x1 = Math.Clamp((int)((r.X + r.W) * view.Width) - 1, 0, view.Width - 1);
        int y1 = Math.Clamp((int)((r.Y + r.H) * view.Height) - 1, 0, view.Height - 1);
        for (int x = x0; x <= x1; x++) { view[x, y0] = color; view[x, y1] = color; }
        for (int y = y0; y <= y1; y++) { view[x0, y] = color; view[x1, y] = color; }
    }
}
overview.SaveAsJpeg(Path.Combine(output, "overview.jpg"));
File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions
{ WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
Console.WriteLine($"Total={paths.Length}, content={content.Count(r => r is not null)}, sprockets={sprockets.Count(r => r is not null)}");
return 0;

static (ImageBuffer Image, int Width, int Height) ReadPreview(string path, int edge, string? cache)
{
    var info = new FileInfo(path);
    string? cached = cache is null ? null : Path.Combine(cache, Path.GetFileName(path) + $"-{edge}-{info.Length}-{info.LastWriteTimeUtc.Ticks}.bin");
    if (cached is not null && File.Exists(cached))
    {
        using var reader = new BinaryReader(File.OpenRead(cached));
        int width = reader.ReadInt32(), height = reader.ReadInt32();
        var image = new ImageBuffer(reader.ReadInt32(), reader.ReadInt32());
        reader.BaseStream.ReadExactly(System.Runtime.InteropServices.MemoryMarshal.AsBytes(image.Data.AsSpan()));
        return (image, width, height);
    }
    var (images, sourceWidth, sourceHeight) = RawDecode.DecodeRawDownsampled(path, RawDecode.FbddMode.Off, new[] { edge }, RawDecode.Demosaic.Preview);
    if (cached is not null)
    {
        using var writer = new BinaryWriter(File.Create(cached));
        writer.Write(sourceWidth); writer.Write(sourceHeight); writer.Write(images[0].Width); writer.Write(images[0].Height);
        writer.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(images[0].Data.AsSpan()));
    }
    return (images[0], sourceWidth, sourceHeight);
}

static object? Size(FilmAutoCrop.Rect? rect, FilmAutoCrop.Detection? detection)
{
    if (rect is not { } r || detection is not { } d) return null;
    int width = (int)(Math.Round((r.X + r.W) * d.Width) - Math.Round(r.X * d.Width));
    int height = (int)(Math.Round((r.Y + r.H) * d.Height) - Math.Round(r.Y * d.Height));
    double ratio = width >= height ? (double)width / height : (double)height / width;
    if (Math.Abs(ratio - 1.5) > 1e-10 && Math.Abs(ratio - 37.0 / 35) > 1e-10)
        throw new InvalidOperationException("Crop ratio drifted at export pixel rounding.");
    return new { Width = width, Height = height };
}
