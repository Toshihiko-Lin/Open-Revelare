using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenRevelare.Core;

/// <summary>An inventory of external resources, independent of decoder and GUI caches.</summary>
public static class ProjectDependencies
{
    public enum Kind { Original, Lcc, CalibrationR, CalibrationG, CalibrationB, CalibrationDirectory, PrintLut }
    public enum Availability { Present, Missing, Unspecified, Inaccessible }
    public sealed record Entry(string Path, bool IsDirectory, Kind[] Roles, bool Required,
        int[] Frames, Availability State, long? Bytes);

    public static IReadOnlyList<Entry> Inspect(Project.Data data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var paths = new Dictionary<string, List<(Kind Role, bool Required, int Frame, bool Directory)>>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        void Add(string? path, Kind role, bool required, int frame = 0, bool directory = false)
        {
            // Keep unspecified channels separate so incomplete Path-A metadata is visible.
            string key = string.IsNullOrWhiteSpace(path) ? "\0" + role : Path.GetFullPath(path);
            if (!paths.TryGetValue(key, out var uses)) paths[key] = uses = new();
            uses.Add((role, required, frame, directory));
        }
        for (int i = 0; i < data.Frames.Count; i++)
        {
            var frame = data.Frames[i];
            Add(frame.SourcePath, Kind.Original, true, i + 1);
            if (!string.IsNullOrWhiteSpace(frame.Params.PrintLut) && !PrintLuts.IsBuiltin(frame.Params.PrintLut))
                Add(frame.Params.PrintLut, Kind.PrintLut, true, i + 1);
        }
        bool lccRequired = data.Meta.LccEnabled ?? data.Frames.FirstOrDefault()?.LccEnabled
            ?? !string.IsNullOrWhiteSpace(data.Meta.LccPath);
        if (!string.IsNullOrWhiteSpace(data.Meta.LccPath) || lccRequired)
            Add(data.Meta.LccPath, Kind.Lcc, lccRequired);
        bool pathA = data.Meta.SourcePath == "A";
        foreach (var (channel, kind) in new[] { ("R", Kind.CalibrationR), ("G", Kind.CalibrationG), ("B", Kind.CalibrationB) })
        {
            string? path = data.Meta.CalRgbPaths?.GetValueOrDefault(channel);
            if (!string.IsNullOrWhiteSpace(path) || pathA) Add(path, kind, pathA);
        }
        if (!string.IsNullOrWhiteSpace(data.Meta.CalSourcePath))
            Add(data.Meta.CalSourcePath, Kind.CalibrationDirectory, false, directory: true);

        return paths.Select(pair =>
        {
            string path = pair.Key[0] == '\0' ? "" : pair.Key;
            bool directory = pair.Value.Any(u => u.Directory);
            Availability state;
            long? bytes = null;
            try
            {
                if (path.Length == 0) state = Availability.Unspecified;
                else
                {
                    // Exists() suppresses access errors. Attributes lets the inventory distinguish them.
                    var attributes = File.GetAttributes(path);
                    bool actualDirectory = (attributes & FileAttributes.Directory) != 0;
                    state = actualDirectory == directory ? Availability.Present : Availability.Missing;
                    if (state == Availability.Present && !directory) bytes = new FileInfo(path).Length;
                }
            }
            catch (FileNotFoundException) { state = Availability.Missing; }
            catch (DirectoryNotFoundException) { state = Availability.Missing; }
            catch (IOException) { state = Availability.Inaccessible; }
            catch (UnauthorizedAccessException) { state = Availability.Inaccessible; }
            return new Entry(path, directory, pair.Value.Select(u => u.Role).Distinct().ToArray(),
                pair.Value.Any(u => u.Required), pair.Value.Where(u => u.Frame > 0).Select(u => u.Frame).Distinct().ToArray(), state, bytes);
        }).ToArray();
    }

    public static void SaveManifest(string path, string rollTitle, IReadOnlyList<Entry> entries)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(new
        {
            Version = 1, RollTitle = rollTitle, CapturedAt = DateTimeOffset.Now,
            Scope = "External resources only; file presence does not validate contents. Copy the project separately. No files are bundled.",
            Entries = entries,
        }, options));
    }
}
