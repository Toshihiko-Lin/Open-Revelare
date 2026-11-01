using System.Text.Json;
using OpenRevelare.Core;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: dotnet run --project benchmarks/OpenRevelare.AutoCropVerification -- <RAW directory> <output directory>");
    return 2;
}
string input = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]);
if (input == output) throw new ArgumentException("Choose a separate output directory.");
Directory.CreateDirectory(output);
string[] paths = Directory.GetFiles(input).Where(RawDecode.IsRawExtension).Order(StringComparer.Ordinal).ToArray();
var detections = new FilmAutoCrop.Detection?[paths.Length];
var previews = new List<ImageBuffer>();
for (int i = 0; i < paths.Length; i++)
{
    var (images, width, height) = RawDecode.DecodeRawDownsampled(paths[i], RawDecode.FbddMode.Off,
        new[] { 900 }, RawDecode.Demosaic.Preview);
    var d = FilmAutoCrop.Detect(images[0]);
    detections[i] = d is null ? null : d with { Width = width, Height = height };
    previews.Add(Geometry.ApplyRotation(images[0], d?.RotationCorrection ?? 0));
    Console.WriteLine($"{i + 1}/{paths.Length} {Path.GetFileName(paths[i])}: {(d is null ? "review" : d.Polarity.ToString())}");
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
    view.SaveAsJpeg(Path.Combine(output, Path.GetFileNameWithoutExtension(paths[i]) + ".jpg"));
    for (int y = 0; y < 240; y++) for (int x = 0; x < 360; x++)
        overview[(i % 4) * 360 + x, (i / 4) * 240 + y] = view[x * view.Width / 360, y * view.Height / 240];
    var d = detections[i];
    rows.Add(new { File = Path.GetFileName(paths[i]), Detection = d, Content = content[i], Sprockets = sprockets[i],
        ContentPixels = Size(content[i], d), SprocketPixels = Size(sprockets[i], d) });

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
File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Total={paths.Length}, content={content.Count(r => r is not null)}, sprockets={sprockets.Count(r => r is not null)}");
return 0;

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
