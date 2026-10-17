using System.Security.Cryptography;
using System.Text.Json;
using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class ProjectArchiveTests
{
    private sealed class InlineProgress(Action<ProjectArchive.Progress> report) : IProgress<ProjectArchive.Progress>
    {
        public void Report(ProjectArchive.Progress value) => report(value);
    }
    private static string Folder()
    {
        string dir = Path.Combine(TestDataIsolation.Root, "archive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
    private static string FileIn(string dir, string name, string content)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name); File.WriteAllText(path, content); return path;
    }

    [Fact]
    public async Task Archive_is_self_contained_after_move_with_duplicate_names_references_and_virtual_copies()
    {
        string dir = Folder(), originals = Path.Combine(dir, "originals");
        string first = FileIn(originals, "frame.tif", "first"), second = FileIn(Path.Combine(dir, "other"), "frame.tif", "second");
        string lut = FileIn(dir, "look.cube", "lut"), flat = FileIn(dir, "flat.tif", "flat");
        string r = FileIn(dir, "r.tif", "r"), g = FileIn(dir, "g.tif", "g"), b = FileIn(dir, "b.tif", "b");
        var data = new Project.Data
        {
            Meta = new() { SourcePath = "A", CalSourcePath = dir, CalRgbPaths = new() { ["R"] = r, ["G"] = g, ["B"] = b },
                LccPath = flat, LccEnabled = false, RollNote = "归档笔记" },
            Frames = new()
            {
                new() { SourcePath = first, Params = new() { PrintLut = lut, ExposureEv = 1.25 } },
                new() { SourcePath = first, IsVirtual = true, Params = new() { PrintLut = lut, ExposureEv = -0.5 } },
                new() { SourcePath = second, Params = new() { PrintLut = PrintLuts.Builtins[0].Id } },
            },
        };
        string target = Path.Combine(dir, "archive");
        await ProjectArchive.CreateAsync(data, target);
        Assert.Equal(first, data.Frames[0].SourcePath); Assert.Null(data.Frames[0].SourceFingerprint);
        Assert.Equal(dir, data.Meta.CalSourcePath); Assert.False(data.UseRelativePaths);
        string moved = Path.Combine(dir, "moved"); Directory.Move(target, moved);
        string project = Path.Combine(moved, "project.ncproj");
        var restored = Project.Load(project);
        Assert.True(restored.UseRelativePaths);
        Assert.False(restored.Meta.LccEnabled); Assert.Null(restored.Meta.CalSourcePath);
        Assert.Equal("归档笔记", restored.Meta.RollNote);
        Assert.Equal("first", File.ReadAllText(restored.Frames[0].SourcePath));
        Assert.Equal("second", File.ReadAllText(restored.Frames[2].SourcePath));
        Assert.Equal(restored.Frames[0].SourcePath, restored.Frames[1].SourcePath);
        Assert.NotEqual(restored.Frames[0].SourcePath, restored.Frames[2].SourcePath);
        Assert.Equal(1.25, restored.Frames[0].Params.ExposureEv);
        Assert.Equal(-0.5, restored.Frames[1].Params.ExposureEv);
        Assert.Equal(PrintLuts.Builtins[0].Id, restored.Frames[2].Params.PrintLut);
        Assert.All(ProjectDependencies.Inspect(restored), e => Assert.Equal(ProjectDependencies.Availability.Present, e.State));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(moved, "checksums.json")));
        var files = manifest.RootElement.GetProperty("Files"); Assert.Equal(7, files.GetArrayLength());
        foreach (var file in files.EnumerateArray())
        {
            string relative = file.GetProperty("Path").GetString()!;
            Assert.False(Path.IsPathRooted(relative));
            byte[] content = File.ReadAllBytes(Path.Combine(moved, relative));
            Assert.Equal(Convert.ToHexString(SHA256.HashData(content)), file.GetProperty("Sha256").GetString());
            Assert.Equal(content.Length, file.GetProperty("Bytes").GetInt64());
        }
        Project.Save(project, restored);
        using var json = JsonDocument.Parse(File.ReadAllText(project));
        Assert.False(Path.IsPathRooted(json.RootElement.GetProperty("frames")[0].GetProperty("source_path").GetString()!));
        string movedAgain = Path.Combine(dir, "moved-again"); Directory.Move(moved, movedAgain);
        Assert.Equal("lut", File.ReadAllText(Project.Load(Path.Combine(movedAgain, "project.ncproj")).Frames[0].Params.PrintLut));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Missing_original_or_disabled_declared_reference_prevents_partial_archive(bool originalMissing)
    {
        string dir = Folder(), source = FileIn(dir, "source.tif", "pixels"), target = Path.Combine(dir, "archive");
        var data = new Project.Data { Meta = new() { LccEnabled = false }, Frames = new() { new() { SourcePath = source } } };
        if (originalMissing) File.Delete(source); else data.Meta.LccPath = Path.Combine(dir, "missing-flat.tif");
        await Assert.ThrowsAsync<IOException>(() => ProjectArchive.CreateAsync(data, target));
        Assert.False(Directory.Exists(target)); Assert.Empty(Directory.GetDirectories(dir, ".openrevelare-archive-*"));
    }

    [Fact]
    public async Task Cancellation_after_a_copy_does_not_publish_partial_archive()
    {
        string dir = Folder(), source = FileIn(dir, "source.tif", new string('a', 1048577)), target = Path.Combine(dir, "archive");
        var data = new Project.Data { Frames = new() { new() { SourcePath = source } } };
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress(p => { if (p.CompletedFiles == 1) cancellation.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProjectArchive.CreateAsync(data, target, progress, cancellation.Token));
        Assert.False(Directory.Exists(target)); Assert.Empty(Directory.GetDirectories(dir, ".openrevelare-archive-*"));
        Assert.Equal(1048577, new FileInfo(source).Length);
    }

    [Fact]
    public async Task Changed_source_after_copy_is_rejected_before_publication()
    {
        string dir = Folder(), source = FileIn(dir, "source.tif", "original"), target = Path.Combine(dir, "archive");
        var data = new Project.Data { Frames = new() { new() { SourcePath = source } } };
        var progress = new InlineProgress(p => { if (p.CompletedFiles == 1) File.AppendAllText(source, "changed"); });
        await Assert.ThrowsAsync<IOException>(() => ProjectArchive.CreateAsync(data, target, progress));
        Assert.False(Directory.Exists(target)); Assert.Empty(Directory.GetDirectories(dir, ".openrevelare-archive-*"));
    }

    [Fact]
    public async Task Existing_destination_is_preserved()
    {
        string dir = Folder(), target = Path.Combine(dir, "archive"), source = FileIn(dir, "source.tif", "original");
        string existing = FileIn(target, "keep.txt", "keep");
        await Assert.ThrowsAsync<IOException>(() => ProjectArchive.CreateAsync(
            new Project.Data { Frames = new() { new() { SourcePath = source } } }, target));
        Assert.Equal("keep", File.ReadAllText(existing)); Assert.Single(Directory.GetFiles(target));
    }
}
