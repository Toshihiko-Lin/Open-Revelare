using System.Security.Cryptography;
using System.Text.Json;

namespace OpenRevelare.Core;

/// <summary>Publishes a self-contained directory only after every copy and project write succeeds.</summary>
public static class ProjectArchive
{
    public sealed record Progress(int CompletedFiles, int TotalFiles, long CopiedBytes, long TotalBytes);
    private sealed record Revision(long Length, long Modified, long Created)
    {
        public static Revision Capture(string path)
        {
            var file = new FileInfo(path);
            return new(file.Length, file.LastWriteTimeUtc.Ticks, file.CreationTimeUtc.Ticks);
        }
    }

    public static async Task<string> CreateAsync(Project.Data data, string destination,
        IProgress<Progress>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = Project.Snapshot(data);
        if (snapshot.Frames.Count == 0) throw new InvalidOperationException(CoreText.T("工程为空"));
        string target = Path.GetFullPath(destination);
        if (Directory.Exists(target) || File.Exists(target))
            throw new IOException(CoreText.T("归档目标已存在，请选择新的目录。"));
        string parent = Path.GetDirectoryName(target) ?? throw new IOException(CoreText.T("归档目标必须是新目录。"));
        Directory.CreateDirectory(parent);
        // Old projects can name only the calibration directory; freeze the exact files before copying.
        if (snapshot.Meta.SourcePath == "A" && (snapshot.Meta.CalRgbPaths is not { } rgb
            || new[] { "R", "G", "B" }.Any(k => !rgb.ContainsKey(k) || string.IsNullOrWhiteSpace(rgb[k]))))
        {
            var (r, g, b) = DecoupleCalibration.FindRgbCalFiles(snapshot.Meta.CalSourcePath ?? "");
            snapshot.Meta.CalRgbPaths = new() { ["R"] = r, ["G"] = g, ["B"] = b };
        }
        var entries = ProjectDependencies.Inspect(snapshot);
        var unavailable = entries.Where(e => !e.IsDirectory && e.State != ProjectDependencies.Availability.Present).ToArray();
        if (unavailable.Length > 0)
            throw new IOException(CoreText.T("归档资源不可用：") + "\n" + string.Join("\n", unavailable.Select(e => e.Path.Length == 0
                ? string.Join(" / ", e.Roles) : e.Path)));
        var files = entries.Where(e => !e.IsDirectory).ToArray();
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var revisions = files.ToDictionary(e => e.Path, e => Revision.Capture(e.Path), comparer);
        long total = revisions.Values.Sum(r => r.Length), copied = 0;
        string stage = Path.Combine(parent, ".openrevelare-archive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var remap = new Dictionary<string, string>(comparer);
        var checksums = new List<object>();
        try
        {
            progress?.Report(new(0, files.Length, 0, total));
            byte[] buffer = new byte[1024 * 1024];
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long lastReport = 0;
            for (int i = 0; i < files.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string source = files[i].Path;
                string relative = $"assets/{i + 1:D6}/" + Path.GetFileName(source);
                string output = Path.Combine(stage, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                    buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var writer = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    buffer.Length, FileOptions.Asynchronous))
                {
                    int count;
                    while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        await writer.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                        hash.AppendData(buffer, 0, count);
                        copied += count;
                        if (clock.ElapsedMilliseconds - lastReport >= 100)
                        {
                            lastReport = clock.ElapsedMilliseconds;
                            progress?.Report(new(i, files.Length, copied, total));
                        }
                    }
                    await writer.FlushAsync(cancellationToken);
                    writer.Flush(flushToDisk: true);
                }
                if (Revision.Capture(source) != revisions[source])
                    throw new IOException(CoreText.T("归档期间资源发生变化：") + source);
                remap[source] = output;
                checksums.Add(new { Path = relative, Bytes = revisions[source].Length,
                    Sha256 = Convert.ToHexString(hash.GetHashAndReset()) });
                progress?.Report(new(i + 1, files.Length, copied, total));
            }
            string Map(string path) => remap[Path.GetFullPath(path)];
            foreach (var frame in snapshot.Frames)
            {
                frame.SourcePath = Map(frame.SourcePath);
                if (!string.IsNullOrWhiteSpace(frame.Params.PrintLut) && !PrintLuts.IsBuiltin(frame.Params.PrintLut))
                    frame.Params.PrintLut = Map(frame.Params.PrintLut);
            }
            if (!string.IsNullOrWhiteSpace(snapshot.Meta.LccPath)) snapshot.Meta.LccPath = Map(snapshot.Meta.LccPath);
            if (snapshot.Meta.CalRgbPaths is { } paths)
                foreach (string key in paths.Keys.ToArray())
                    if (!string.IsNullOrWhiteSpace(paths[key])) paths[key] = Map(paths[key]);
            // Explicit RGB paths fully describe calibration; copying the entire source directory
            // would also sweep in unrelated photographs and still would not guarantee its layout.
            snapshot.Meta.CalSourcePath = null;
            snapshot.UseRelativePaths = true;
            Project.Save(Path.Combine(stage, "project.ncproj"), snapshot);
            AtomicFile.WriteAllText(Path.Combine(stage, "checksums.json"), JsonSerializer.Serialize(new
            {
                Version = 1, Algorithm = "SHA256", Files = checksums,
            }, new JsonSerializerOptions { WriteIndented = true }));
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var (source, revision) in revisions)
                if (Revision.Capture(source) != revision)
                    throw new IOException(CoreText.T("归档期间资源发生变化：") + source);
            // Same-parent rename prevents an incomplete directory from looking like a finished archive.
            Directory.Move(stage, target);
            return Path.Combine(target, "project.ncproj");
        }
        finally
        {
            if (Directory.Exists(stage))
                try { Directory.Delete(stage, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
