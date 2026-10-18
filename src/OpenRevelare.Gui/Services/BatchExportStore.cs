using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenRevelare.Core;
using OpenRevelare.Gui.Models;

namespace OpenRevelare.Gui.Services;

/// <summary>One recoverable job per project, with its render inputs frozen beside the status file.</summary>
internal static class BatchExportStore
{
    internal sealed record FileRevision(long Length, long Modified, long Created)
    {
        internal static FileRevision? TryCapture(string? path)
        {
            if (path is null) return null;
            try
            {
                var file = new FileInfo(path);
                return file.Exists ? new(file.Length, file.LastWriteTimeUtc.Ticks, file.CreationTimeUtc.Ticks) : null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
    }
    internal sealed class SavedItem
    {
        public string SourcePath { get; set; } = "";
        public string? OutputPath { get; set; }
        public BatchExportState State { get; set; }
        public string Error { get; set; } = "";
        public string Detail { get; set; } = "";
        public double[]? Box { get; set; }
        public FileRevision? CompletedOutput { get; set; }
    }
    internal sealed class SavedJob
    {
        public int Version { get; set; } = 1;
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string ProjectPath { get; set; } = "";
        public string ProjectHash { get; set; } = "";
        public string Folder { get; set; } = "";
        public string RollTitle { get; set; } = "";
        public string OptionsSummary { get; set; } = "";
        public DateTime StartedAt { get; set; }
        public ExportOptions Options { get; set; } = new();
        public FilmCaptureMetadata? FilmCapture { get; set; }
        public FilmCaptureMetadata[]? FrameFilmCaptures { get; set; }
        public bool ExportLinear { get; set; }
        public string ColorSpace { get; set; } = "sRGB";
        public double HdrLimitStops { get; set; }
        public ColorPipelineVersion PipelineVersion { get; set; }
        public TiffInputAssumption TiffAssumption { get; set; }
        public Dictionary<string, string?> ReferenceFingerprints { get; set; } = new();
        public SavedItem[] Items { get; set; } = [];
    }
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };
    private static string Folder(string projectPath)
    {
        string normal = Path.GetFullPath(projectPath);
        if (OperatingSystem.IsWindows()) normal = normal.ToUpperInvariant();
        return Path.Combine(Settings.DataDir, "batch-jobs",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normal))));
    }
    private static string HashProject(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static Dictionary<string, string?> References(Project.Data data)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return ProjectDependencies.Inspect(data).Where(e => !e.IsDirectory &&
            !e.Roles.Contains(ProjectDependencies.Kind.Original)).ToDictionary(e => e.Path,
            e => SourceFingerprint.TryCapture(e.Path), comparison);
    }

    internal static SavedJob Create(string projectPath, Project.Data snapshot, ExportOptions opt,
        BatchExportJob job, IReadOnlyList<double[]?> boxes,
        ColorPipelineVersion pipeline, TiffInputAssumption assumption)
    {
        string folder = Folder(projectPath);
        Directory.CreateDirectory(folder);
        string? previousId = TryReadId(Path.Combine(folder, "last.json"));
        var state = new SavedJob
        {
            ProjectPath = Path.GetFullPath(projectPath), ProjectHash = HashProject(projectPath),
            Folder = job.Folder, RollTitle = job.RollTitle, OptionsSummary = job.OptionsSummary,
            StartedAt = job.StartedAt, Options = opt.Clone(), FilmCapture = opt.FilmCapture,
            FrameFilmCaptures = opt.FrameFilmCaptures?.ToArray(),
            ExportLinear = opt.ExportLinear, ColorSpace = opt.ColorSpace, HdrLimitStops = opt.HdrLimitStops,
            PipelineVersion = pipeline, TiffAssumption = assumption,
            ReferenceFingerprints = References(snapshot),
            Items = job.Items.Select((item, i) => new SavedItem
            {
                SourcePath = item.SourcePath, OutputPath = item.OutputPath,
                State = item.State, Box = boxes[i],
            }).ToArray(),
        };
        // The state file is published last: a crash between these writes leaves the previous job
        // intact. A later job uses a new ID and never mutates its predecessor's snapshot.
        Project.Save(Path.Combine(folder, state.Id + ".ncproj"), snapshot);
        Save(state, job);
        if (previousId is not null && previousId != state.Id)
        {
            // Only the last job is recoverable. A replaced snapshot must not accumulate after
            // every export, but cleanup failure must not turn a successful export into an error.
            try { File.Delete(Path.Combine(folder, previousId + ".ncproj")); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return state;
    }

    private static string? TryReadId(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            string? id = document.RootElement.GetProperty("Id").GetString();
            return Guid.TryParseExact(id, "N", out _) ? id : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        { return null; }
    }

    internal static void Save(SavedJob state, BatchExportJob job)
    {
        foreach (var (saved, item) in state.Items.Zip(job.Items))
        {
            saved.State = item.State; saved.Error = item.Error; saved.Detail = item.Detail;
            saved.CompletedOutput = item.State == BatchExportState.Completed
                ? FileRevision.TryCapture(item.OutputPath) : null;
        }
        AtomicFile.WriteAllText(Path.Combine(Folder(state.ProjectPath), "last.json"),
            JsonSerializer.Serialize(state, Json));
    }

    internal static (SavedJob State, Project.Data Snapshot, string? Blocked)? Load(string projectPath)
    {
        string folder = Folder(projectPath), file = Path.Combine(folder, "last.json");
        if (!File.Exists(file)) return null;
        try
        {
            var state = JsonSerializer.Deserialize<SavedJob>(File.ReadAllText(file), Json)
                ?? throw new InvalidDataException();
            if (state.Version != 1 || !Guid.TryParseExact(state.Id, "N", out _) ||
                state.Items is null || state.Options is null || state.ReferenceFingerprints is null ||
                !Path.GetFullPath(projectPath).Equals(state.ProjectPath,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidDataException();
            var snapshot = Project.Load(Path.Combine(folder, state.Id + ".ncproj"));
            if (state.Items.Length != snapshot.Frames.Count ||
                (state.FrameFilmCaptures is { } films && (films.Length != state.Items.Length || films.Any(f => f is null))))
                throw new InvalidDataException();
            return (state, snapshot, Validate(state, snapshot));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        {
            return null;
        }
    }

    internal static string? Validate(SavedJob state, Project.Data snapshot)
    {
        try
        {
            if (!File.Exists(state.ProjectPath) || HashProject(state.ProjectPath) != state.ProjectHash)
                return Loc.T("工程在任务创建后已改变，请重新创建批量导出任务。");
            var references = References(snapshot);
            if (references.Count != state.ReferenceFingerprints.Count ||
                references.Any(pair => !state.ReferenceFingerprints.TryGetValue(pair.Key, out var previous)
                    || pair.Value != previous))
                return Loc.T("任务的校正参考或 LUT 已改变，请重新创建批量导出任务。");
            if (state.Items.Any(i => i.State == BatchExportState.Completed &&
                (i.CompletedOutput is null || i.CompletedOutput != FileRevision.TryCapture(i.OutputPath))))
                return Loc.T("已完成的导出文件已改变，请检查后重新创建批量导出任务。");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Loc.T("无法验证上次批量导出的文件，请检查后重新创建任务。");
        }
    }
}
