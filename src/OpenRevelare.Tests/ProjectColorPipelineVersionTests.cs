using System.Text.Json.Nodes;
using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class ProjectColorPipelineVersionTests
{
    [Fact]
    public void New_project_data_defaults_to_managed_v2()
    {
        var project = new Project.Data();

        Assert.Equal(ColorPipelineVersion.ManagedV2, project.ColorPipelineVersion);
    }

    [Theory]
    [InlineData("missing-display-referred-stage2.ncproj")]
    [InlineData("explicit-false-display-referred-stage2.ncproj")]
    public void Missing_pipeline_version_loads_as_legacy_v1_without_changing_render_fixture(
        string fixture)
    {
        Project.Data project = Project.Load(FixturePath(fixture));

        Assert.Equal(ColorPipelineVersion.LegacyV1, project.ColorPipelineVersion);
    }

    [Fact]
    public void Saving_loaded_legacy_project_preserves_v1_until_explicit_migration()
    {
        Project.Data project = Project.Load(FixturePath("missing-display-referred-stage2.ncproj"));

        WithTempProject(path =>
        {
            Project.Save(path, project);

            JsonNode root = JsonNode.Parse(File.ReadAllText(path))!;
            Assert.Equal(2, root["version"]!.GetValue<int>());
            Assert.Equal(1, root["color_pipeline_version"]!.GetValue<int>());
            Assert.Equal(ColorPipelineVersion.LegacyV1, Project.Load(path).ColorPipelineVersion);
        });
    }

    [Fact]
    public void Explicit_migration_is_the_only_operation_that_promotes_v1_to_v2()
    {
        Project.Data project = Project.Load(FixturePath("missing-display-referred-stage2.ncproj"));

        Project.MigrateColorPipelineToV2(project);

        Assert.Equal(ColorPipelineVersion.ManagedV2, project.ColorPipelineVersion);
        WithTempProject(path =>
        {
            Project.Save(path, project);
            Assert.Equal(2, JsonNode.Parse(File.ReadAllText(path))!["color_pipeline_version"]!
                .GetValue<int>());
        });
    }

    [Theory]
    [InlineData("0")]
    [InlineData("3")]
    [InlineData("\"2\"")]
    public void Load_rejects_unsupported_or_malformed_pipeline_versions_clearly(string value)
    {
        WithTempProject(path =>
        {
            File.WriteAllText(path,
                $$"""
                {
                  "version": 2,
                  "color_pipeline_version": {{value}},
                  "roll_meta": {},
                  "frames": []
                }
                """);

            InvalidDataException error = Assert.Throws<InvalidDataException>(() => Project.Load(path));
            Assert.Contains("color_pipeline_version", error.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Save_rejects_an_unsupported_in_memory_pipeline_version()
    {
        var project = new Project.Data { ColorPipelineVersion = (ColorPipelineVersion)3 };

        WithTempProject(path =>
        {
            InvalidDataException error = Assert.Throws<InvalidDataException>(
                () => Project.Save(path, project));
            Assert.Contains("color_pipeline_version", error.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(path));
        });
    }

    [Fact]
    public void A_managed_project_raises_the_schema_floor_so_an_old_build_refuses_it()
    {
        // The colour key is additive, so a pre-v3 build parses a managed project happily and then
        // renders it through the v1 pipeline without a word — silently wrong colour is worse than
        // a refusal, and the schema version is the only thing that can force the refusal.
        var project = new Project.Data { ColorPipelineVersion = ColorPipelineVersion.ManagedV2 };

        WithTempProject(path =>
        {
            Project.Save(path, project);

            JsonNode root = JsonNode.Parse(File.ReadAllText(path))!;
            Assert.Equal(3, root["version"]!.GetValue<int>());
            Assert.Equal(2, root["color_pipeline_version"]!.GetValue<int>());
            Assert.Equal(ColorPipelineVersion.ManagedV2, Project.Load(path).ColorPipelineVersion);
        });
    }

    [Fact]
    public void A_legacy_project_keeps_the_old_schema_floor_and_stays_openable_elsewhere()
    {
        // A pre-v3 build reads this correctly: it ignores the colour key and falls back to v1,
        // which is what the file asks for. Raising its floor would cost compatibility for nothing.
        var project = new Project.Data { ColorPipelineVersion = ColorPipelineVersion.LegacyV1 };

        WithTempProject(path =>
        {
            Project.Save(path, project);

            Assert.Equal(2, JsonNode.Parse(File.ReadAllText(path))!["version"]!.GetValue<int>());
        });
    }

    [Fact]
    public void A_v2_file_without_the_colour_key_still_loads_as_legacy()
    {
        WithTempProject(path =>
        {
            File.WriteAllText(path,
                """
                {
                  "version": 2,
                  "roll_meta": {},
                  "frames": []
                }
                """);

            Assert.Equal(ColorPipelineVersion.LegacyV1, Project.Load(path).ColorPipelineVersion);
        });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Load_rejects_schema_versions_outside_the_supported_range(int version)
    {
        WithTempProject(path =>
        {
            File.WriteAllText(path,
                $$"""
                {
                  "version": {{version}},
                  "roll_meta": {},
                  "frames": []
                }
                """);

            InvalidDataException error = Assert.Throws<InvalidDataException>(() => Project.Load(path));
            Assert.Contains("2", error.Message, StringComparison.Ordinal);
            Assert.Contains("3", error.Message, StringComparison.Ordinal);
        });
    }

    private static void WithTempProject(Action<string> test)
    {
        string path = Path.Combine(Path.GetTempPath(), $"open-revelare-{Guid.NewGuid():N}.ncproj");
        try
        {
            test(path);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".tmp");
        }
    }

    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ColorManagement", "legacy", name);
}
