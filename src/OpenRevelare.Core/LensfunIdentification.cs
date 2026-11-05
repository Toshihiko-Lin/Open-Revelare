using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

namespace OpenRevelare.Core;

/// <summary>Digitization equipment, not the camera/lens that originally exposed the film.</summary>
public sealed record LensCaptureInfo(string Make, string Camera, string Lens, double? FocalLength, double? Aperture,
    double? CropFactor = null, double? FocusDistance = null)
{
    /// <summary>Reads metadata only. Never unpacks or demosaics the RAW image.</summary>
    public static LensCaptureInfo Read(string path)
    {
        var directories = ImageMetadataReader.ReadMetadata(path);
        var camera = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
        var exposure = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault(d =>
            d.ContainsTag(0xa434) || d.ContainsTag(0x920a) || d.ContainsTag(0x829d));
        string Text(MetadataExtractor.Directory? d, int tag) => d?.GetString(tag)?.Trim('\0', ' ') ?? "";
        double? Number(int tag) => exposure is not null && exposure.TryGetDouble(tag, out double n)
            && double.IsFinite(n) && n > 0 ? n : null;
        string lens = Text(exposure, 0xa434);
        // Some manufacturers only identify the lens in MakerNotes. A descriptor can resolve
        // the numeric LensType ID; an unresolved number/specification is not a lens identity.
        if (lens.Length == 0)
            lens = directories.SelectMany(d => d.Tags
                    .Where(t => t.Name is "Lens Model" or "Lens Type")
                    .Select(t => d.GetDescription(t.Type)))
                .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s) && s.Any(char.IsLetter)
                    && !s.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase)) ?? "";
        double? focal = Number(0x920a), equivalent = Number(0xa405);
        return new(Text(camera, 0x010f), Text(camera, 0x0110), lens, focal, Number(0x829d),
            equivalent / focal, Number(0x9206));
    }
}

public sealed record LensfunProfile(string Maker, string Model, string[] Mounts,
    bool HasDistortion, bool HasVignetting)
{
    public override string ToString() => Model.StartsWith(Maker, StringComparison.OrdinalIgnoreCase)
        ? Model : $"{Maker} {Model}";
}

public enum LensfunMatchStatus { Matched, MissingLens, NotFound, Ambiguous }

public sealed record LensfunIdentification(LensCaptureInfo Capture, LensfunMatchStatus Status,
    IReadOnlyList<LensfunProfile> Candidates)
{
    public LensfunProfile? Profile => Status == LensfunMatchStatus.Matched ? Candidates[0] : null;
}

/// <summary>
/// Offline identification against Lensfun XML. This is deliberately separate from calibration
/// interpolation and pixel correction: finding a profile does not mean it has been applied.
/// Names must match after punctuation/case/maker normalization; no guessing from focal length.
/// </summary>
public sealed partial class LensfunIdentificationDatabase
{
    private sealed record Camera(string Maker, string Model, string Mount, double CropFactor);
    private readonly Camera[] _cameras;
    private readonly Dictionary<LensfunProfile, XElement> _profileXml = new();
    public IReadOnlyList<LensfunProfile> Profiles { get; }
    public static Lazy<LensfunIdentificationDatabase> Bundled { get; } = new(LoadBundled);

    private LensfunIdentificationDatabase(IEnumerable<XDocument> documents)
    {
        var roots = documents.Select(d => d.Root).OfType<XElement>().ToArray();
        string Text(XElement e, string name) => e.Elements(name).FirstOrDefault(x => x.Attribute("lang") is null)?.Value.Trim() ?? "";
        _cameras = roots.SelectMany(r => r.Elements("camera"))
            .Select(c => new Camera(Text(c, "maker"), Text(c, "model"), Text(c, "mount"), Number(c.Element("cropfactor")?.Value, 0))).ToArray();
        Profiles = roots.SelectMany(r => r.Elements("lens"))
            .Select(l =>
            {
                var profile = new LensfunProfile(Text(l, "maker"), Text(l, "model"),
                l.Elements("mount").Select(m => m.Value.Trim()).ToArray(),
                l.Elements("calibration").Elements("distortion").Any(),
                l.Elements("calibration").Elements("vignetting").Any());
                _profileXml.Add(profile, l);
                return profile;
            })
            .Where(p => p.Model.Length > 0).OrderBy(p => p.ToString(), StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static LensfunIdentificationDatabase FromXml(params string[] documents)
        => new(documents.Select(XDocument.Parse));

    private static LensfunIdentificationDatabase LoadBundled()
    {
        using var stream = typeof(LensfunIdentificationDatabase).Assembly
            .GetManifestResourceStream("OpenRevelare.Core.Lensfun.database.zip")
            ?? throw new FileNotFoundException("Bundled Lensfun database is missing.");
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var documents = new List<XDocument>();
        foreach (var entry in archive.Entries.Where(e => e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
        {
            using var xml = entry.Open();
            documents.Add(XDocument.Load(xml));
        }
        return new(documents);
    }

    public LensfunIdentification Identify(LensCaptureInfo capture)
    {
        if (string.IsNullOrWhiteSpace(capture.Lens))
            return new(capture, LensfunMatchStatus.MissingLens, []);
        var candidates = Profiles.Where(p => SameName(capture.Lens, p.Model, p.Maker)).ToArray();
        // Identical lens names can occur on several mounts. Only constrain by a known camera;
        // unknown/adapter setups remain ambiguous instead of silently choosing another lens.
        var cameras = _cameras.Where(c => SameName(capture.Camera, c.Model, c.Maker)
            && (capture.Make.Length == 0 || Normalize(capture.Make) == Normalize(c.Maker))).ToArray();
        if (candidates.Length > 1 && cameras.Length > 0)
        {
            var onMount = candidates.Where(p => p.Mounts.Any(m => cameras.Any(c => c.Mount == m))).ToArray();
            if (onMount.Length > 0) candidates = onMount;
        }
        return new(capture, candidates.Length switch
        {
            0 => LensfunMatchStatus.NotFound,
            1 => LensfunMatchStatus.Matched,
            _ => LensfunMatchStatus.Ambiguous,
        }, candidates);
    }

    private static bool SameName(string actual, string expected, string maker)
    {
        string TrimMaker(string name)
        {
            string n = Normalize(name), m = Normalize(maker);
            return m.Length > 0 && n.StartsWith(m, StringComparison.Ordinal) ? n[m.Length..] : n;
        }
        return TrimMaker(actual) is { Length: > 0 } value && value == TrimMaker(expected);
    }

    private static string Normalize(string text) => new(text.Normalize(NormalizationForm.FormKC)
        .Where(c => char.IsLetterOrDigit(c) || c is '.' or '-').Select(char.ToUpperInvariant).ToArray());
}
