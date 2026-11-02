using OpenRevelare.Core;

namespace OpenRevelare.Gui.Services;

/// <summary>File-picker and desktop drops share the same admission and duplicate rules.</summary>
internal static class ImportFiles
{
    private static readonly HashSet<string> Extensions = new(
        RawDecode.RawExtensions.Concat(new[] { ".tif", ".tiff" }), StringComparer.OrdinalIgnoreCase);

    internal sealed record Selection(string[] Images, string[] Projects, string[] Rejected);

    public static bool IsImage(string path) => Extensions.Contains(Path.GetExtension(path));

    // Folder imports intentionally match the existing picker: direct children, not a recursive
    // crawl that could unexpectedly collect several rolls or calibration subdirectories.
    public static Selection Collect(IEnumerable<string> paths)
    {
        var images = new List<string>();
        var projects = new List<string>();
        var rejected = new List<string>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (string input in paths)
        {
            try
            {
                string path = Path.GetFullPath(input);
                if (Directory.Exists(path))
                {
                    foreach (string child in Directory.EnumerateFiles(path).Where(IsImage).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                        if (seen.Add(child)) images.Add(child);
                }
                else if (!File.Exists(path)) rejected.Add(input);
                else if (seen.Add(path))
                {
                    if (IsImage(path)) images.Add(path);
                    else if (Path.GetExtension(path).Equals(".ncproj", StringComparison.OrdinalIgnoreCase)) projects.Add(path);
                    else rejected.Add(input);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                rejected.Add(input);
            }
        }
        return new(images.ToArray(), projects.ToArray(), rejected.ToArray());
    }
}
