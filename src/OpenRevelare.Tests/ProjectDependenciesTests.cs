using System.Text.Json;
using System.Text.Json.Nodes;
using OpenRevelare.Core;
using OpenRevelare.Gui.Models;
using OpenRevelare.Gui.ViewModels;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class ProjectDependenciesTests
{
    [Fact]
    public async Task Inventory_remains_available_without_a_displayable_image_and_captures_a_snapshot()
    {
        string dir = Folder(), source = Path.Combine(dir, "offline.tif"), lut = Path.Combine(dir, "look.cube");
        using var vm = new MainViewModel();
        vm.Frames.Add(new RollFrame(source) { Params = new() { PrintLut = lut } });
        Assert.False(vm.HasImage);
        Assert.True(vm.CanInspectProjectDependencies);
        var task = vm.InspectProjectDependenciesAsync();
        vm.Frames[0].Params.PrintLut = "";
        var entries = await task;
        Assert.Contains(entries, e => e.Path == lut && e.Roles.Contains(ProjectDependencies.Kind.PrintLut));
        Assert.All(entries, e => Assert.Equal(ProjectDependencies.Availability.Missing, e.State));
        vm.HasLongTask = true;
        Assert.False(vm.CanInspectProjectDependencies);
        vm.HasLongTask = false;
    }

    private static string Folder()
    {
        string dir = Path.Combine(TestDataIsolation.Root, "dependencies-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Shared_original_and_LUT_are_listed_once_with_every_using_frame()
    {
        string dir = Folder(), source = Path.Combine(dir, "source.tif"), lut = Path.Combine(dir, "look.cube");
        File.WriteAllText(source, "pixels"); File.WriteAllText(lut, "cube");
        var data = new Project.Data { Frames = new()
        {
            new() { SourcePath = source, Params = new() { PrintLut = lut } },
            new() { SourcePath = source, IsVirtual = true, Params = new() { PrintLut = lut } },
            new() { SourcePath = source, Params = new() { PrintLut = PrintLuts.Builtins[0].Id } },
        } };
        var entries = ProjectDependencies.Inspect(data);
        Assert.Equal(2, entries.Count);
        var original = Assert.Single(entries, e => e.Roles.Contains(ProjectDependencies.Kind.Original));
        Assert.Equal(new[] { 1, 2, 3 }, original.Frames);
        Assert.Equal(6L, original.Bytes);
        Assert.All(entries, e => Assert.Equal(ProjectDependencies.Availability.Present, e.State));
        Assert.Equal(new[] { 1, 2 }, Assert.Single(entries, e => e.Roles.Contains(ProjectDependencies.Kind.PrintLut)).Frames);
    }

    [Fact]
    public void Missing_and_disabled_references_remain_visible_and_incomplete_RGB_is_explicit()
    {
        string dir = Folder(), missing = Path.Combine(dir, "flat.tif");
        var data = new Project.Data { Meta = new()
        {
            SourcePath = "A", CalSourcePath = dir,
            CalRgbPaths = new() { ["R"] = missing }, LccPath = missing, LccEnabled = false,
        } };
        var entries = ProjectDependencies.Inspect(data);
        var shared = Assert.Single(entries, e => e.Path == missing);
        Assert.Equal(2, shared.Roles.Length);
        Assert.True(shared.Required); // R is needed even though the LCC use is disabled.
        Assert.Equal(ProjectDependencies.Availability.Missing, shared.State);
        Assert.Equal(2, entries.Count(e => e.Required && e.State == ProjectDependencies.Availability.Unspecified));
        Assert.True(Assert.Single(entries, e => e.IsDirectory).State == ProjectDependencies.Availability.Present);
        data.Meta.SourcePath = "B"; data.Meta.CalRgbPaths = null;
        Assert.False(Assert.Single(ProjectDependencies.Inspect(data), e => e.Path == missing).Required);
    }

    [Fact]
    public void Legacy_LCC_default_and_orphan_virtual_original_are_required()
    {
        string dir = Folder();
        var data = new Project.Data { Meta = new() { LccPath = Path.Combine(dir, "flat.tif") },
            Frames = new() { new() { IsVirtual = true, SourcePath = Path.Combine(dir, "negative.tif") } } };
        Assert.All(ProjectDependencies.Inspect(data), e => Assert.True(e.Required));
        data.Meta.LccEnabled = true; data.Meta.LccPath = null;
        Assert.Contains(ProjectDependencies.Inspect(data), e => e.Roles.Contains(ProjectDependencies.Kind.Lcc)
            && e.State == ProjectDependencies.Availability.Unspecified && e.Required);
    }

    [Fact]
    public void Directory_cannot_satisfy_a_file_dependency_and_inventory_does_not_decode_pixels()
    {
        string dir = Folder();
        var data = new Project.Data { Frames = new() { new() { SourcePath = dir } } };
        Assert.Equal(ProjectDependencies.Availability.Missing, Assert.Single(ProjectDependencies.Inspect(data)).State);
    }

    [Fact]
    public void Manifest_roundtrips_context_roles_and_missing_paths_without_changing_project()
    {
        string dir = Folder(), source = Path.Combine(dir, "missing.tif"), path = Path.Combine(dir, "manifest.json");
        var data = new Project.Data { Frames = new() { new() { SourcePath = source } } };
        var entries = ProjectDependencies.Inspect(data);
        ProjectDependencies.SaveManifest(path, "胶卷 A", entries);
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(1, json.RootElement.GetProperty("Version").GetInt32());
        Assert.Equal("胶卷 A", json.RootElement.GetProperty("RollTitle").GetString());
        var entry = json.RootElement.GetProperty("Entries")[0];
        Assert.Equal(source, entry.GetProperty("Path").GetString());
        Assert.Equal("Missing", entry.GetProperty("State").GetString());
        Assert.Null(data.Frames[0].SourceFingerprint);
    }

    [Fact]
    public void Relative_resources_follow_moved_project_directory_and_copy_save_stays_resolvable()
    {
        string dir = Folder(), path = Path.Combine(dir, "roll.ncproj");
        Project.Save(path, new Project.Data { Meta = new() { SourcePath = "A" }, Frames = new()
        {
            new() { SourcePath = Path.Combine(dir, "negative.tif") },
            new() { SourcePath = Path.Combine(dir, "negative.tif"), IsVirtual = true,
                Params = new() { PrintLut = PrintLuts.Builtins[0].Id } },
        } });
        var root = JsonNode.Parse(File.ReadAllText(path))!;
        var meta = root["roll_meta"]!;
        meta["lcc_path"] = "refs/flat.tif"; meta["cal_source_path"] = "refs";
        meta["cal_rgb_paths"] = new JsonObject { ["R"] = "refs/r.tif", ["G"] = "refs/g.tif", ["B"] = "refs/b.tif" };
        foreach (var frame in root["frames"]!.AsArray()) frame!["source_path"] = "negative.tif";
        root["frames"]![0]!["print_lut"] = "refs/look.cube";
        File.WriteAllText(path, root.ToJsonString());
        string moved = Path.Combine(dir, "moved"); Directory.CreateDirectory(moved);
        string movedProject = Path.Combine(moved, "roll.ncproj"); File.Copy(path, movedProject);
        var data = Project.Load(movedProject);
        Assert.All(data.Frames, f => Assert.Equal(Path.Combine(moved, "negative.tif"), f.SourcePath));
        Assert.Equal(Path.Combine(moved, "refs", "flat.tif"), data.Meta.LccPath);
        Assert.Equal(Path.Combine(moved, "refs"), data.Meta.CalSourcePath);
        Assert.Equal(Path.Combine(moved, "refs", "g.tif"), data.Meta.CalRgbPaths!["G"]);
        Assert.Equal(Path.Combine(moved, "refs", "look.cube"), data.Frames[0].Params.PrintLut);
        Assert.Equal(PrintLuts.Builtins[0].Id, data.Frames[1].Params.PrintLut);
        string copy = Path.Combine(dir, "copy.ncproj"); Project.Save(copy, data);
        Assert.Equal(data.Frames[0].SourcePath, Project.Load(copy).Frames[0].SourcePath);
    }
}
