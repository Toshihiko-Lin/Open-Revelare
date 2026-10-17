using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using OpenRevelare.ColorManagement;
using OpenRevelare.Core;

// Persistent, deterministic files for independent decoders and real-device transfer.
// Optional dimensions allow testing actual gallery/memory limits (e.g. 10003 8001).
if (args.Length is not (1 or 3))
    throw new ArgumentException("Usage: ExportCompatibility OUTPUT_DIRECTORY [WIDTH HEIGHT]");
string root = Path.GetFullPath(args[0]);
int width = args.Length == 3 ? int.Parse(args[1]) : 1031;
int height = args.Length == 3 ? int.Parse(args[2]) : 769;
if (width < 32 || height < 32) throw new ArgumentOutOfRangeException(nameof(args));
Directory.CreateDirectory(root);
GpuAcceleration.Mode = GpuAccelerationMode.Off;
var manifest = new List<object>();
var sdrPixels = new ImageBuffer(width, height);
var hdrPixels = new ImageBuffer(width, height);
for (int y = 0; y < height; y++)
    for (int x = 0; x < width; x++)
        for (int c = 0; c < 3; c++)
        {
            int p = (y * width + x) * 3 + c;
            float v = Math.Clamp(x / (float)(width - 1) + 0.15f * MathF.Sin(y * 0.03f + c), 0f, 1f);
            // Neutral highlight patch must reconstruct above SDR white in an HDR decoder.
            if (x > width * 3 / 4 && y > height * 3 / 4) v = 0.9f;
            sdrPixels.Data[p] = v;
            hdrPixels.Data[p] = Srgb.SrgbToLinear(v) * (1f + 3f * y / (height - 1));
        }
var hdr = Frame(hdrPixels, ColorSpaces.LinearExtendedSrgb, true);
foreach (ColorSpaceDef space in new[] { ColorSpaces.Srgb, ColorSpaces.DisplayP3, ColorSpaces.AdobeRgb, ColorSpaces.Rec709 })
{
    var sdr = Frame(sdrPixels, space, false);
    string name = space.Name.ToLowerInvariant();
    Write(name + ".jpg", p => JpegIO.ExportJpeg(sdr, p), "JPEG SDR");
    if (space == ColorSpaces.Srgb || space == ColorSpaces.DisplayP3)
        Write(name + "-hdr.jpg", p => JpegIO.ExportGainMapJpeg(sdr, hdr, space, OutputTarget.Hdr(1000), p), "JPEG HDR gain map");
    Write(name + ".dng", p => LinearDng.Write(sdrPixels, space, p), "Linear DNG");
    if (space != ColorSpaces.Srgb) continue;
    var quantized = sdrPixels.Data.Select(v => (ushort)(Math.Clamp(v, 0f, 1f) * 65535f + 0.5f)).ToArray();
    string u16Hash = Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(quantized.AsSpan())));
    string f32Hash = Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(hdrPixels.Data.AsSpan())));
    foreach (var compression in Enum.GetValues<TiffIO.CompressionMode>())
    {
        Write("tiff16-" + compression + ".tif", p => TiffIO.ExportTiff(sdr, p, compression), "TIFF uint16", u16Hash);
        Write("tiff32-" + compression + ".tif", p => TiffIO.ExportTiff(hdr, p, compression), "TIFF float32", f32Hash);
    }
}
File.WriteAllText(Path.Combine(root, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(root);

void Write(string name, Action<string> export, string kind, string? pixelSha256 = null)
{
    string path = Path.Combine(root, name);
    if (File.Exists(path)) throw new IOException("Refusing to overwrite fixture: " + path);
    export(path);
    manifest.Add(new { Name = name, Kind = kind, Width = width, Height = height,
        Bytes = new FileInfo(path).Length, Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
        PixelSha256 = pixelSha256 });
}

static RenderedFrame Frame(ImageBuffer pixels, ColorSpaceDef space, bool extended)
{
    var profile = BuiltInColorProfiles.For(space, ProfileRole.Output);
    var encoding = new CharacterizedPixelEncoding(profile,
        extended ? ColorReference.SceneReferred : ColorReference.DisplayReferred,
        extended ? TransferState.LinearInProfilePrimaries : TransferState.ProfileEncoded,
        extended ? NumericRange.Extended : NumericRange.Normalized);
    var recipe = new OutputRecipe(ColorPipelineVersion.ManagedV2, profile,
        RenderingIntent.RelativeColorimetric, false, "export compatibility fixture", "", false);
    return new RenderedFrame(pixels, encoding, recipe, RenderFingerprint.ComputeManaged(pixels, encoding, recipe));
}
