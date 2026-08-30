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
