using System.Text;
using OpenRevelare.Core;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class ExportFileTests
{
    private static string NewDirectory() =>
        Directory.CreateDirectory(Path.Combine(TestDataIsolation.ExportRoot, $"export-{Guid.NewGuid():N}")).FullName;

    [Fact]
    public void A_short_sibling_staging_name_preserves_a_long_unicode_destination()
    {
        string directory = NewDirectory();
        string path = Path.Combine(directory, new string('片', 82) + ".dng");
        ExportFile.Write(path, stage =>
        {
            Assert.Equal(directory, Path.GetDirectoryName(stage));
            Assert.Equal(".dng", Path.GetExtension(stage));
            Assert.InRange(Encoding.UTF8.GetByteCount(Path.GetFileName(stage)), 1, 80);
            File.WriteAllText(stage, "complete");
        });

        Assert.Equal("complete", File.ReadAllText(path));
        Assert.Single(Directory.EnumerateFiles(directory));
    }

    [Fact]
    public void A_successful_export_replaces_the_target_and_removes_staging()
    {
        string directory = NewDirectory();
        string target = Path.Combine(directory, "frame.dng");
        File.WriteAllText(target, "old");

        ExportFile.Write(target, stage =>
        {
            File.WriteAllText(stage, "new");
            Assert.Equal("old", File.ReadAllText(target));
        });

        Assert.Equal("new", File.ReadAllText(target));
        Assert.Equal(target, Assert.Single(Directory.EnumerateFileSystemEntries(directory)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_failed_write_leaves_the_destination_untouched_and_removes_staging(bool exists)
    {
        string directory = NewDirectory();
        string target = Path.Combine(directory, "frame.dng");
        if (exists) File.WriteAllText(target, "original");
        var failure = new IOException("Simulated interrupted write");

        Assert.Same(failure, Assert.Throws<IOException>(() => ExportFile.Write(target, stage =>
        {
            File.WriteAllText(stage, "partial");
            throw failure;
        })));

        if (exists)
        {
            Assert.Equal("original", File.ReadAllText(target));
            Assert.Equal(target, Assert.Single(Directory.EnumerateFileSystemEntries(directory)));
        }
        else
        {
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
        }
    }

    [Fact]
    public void A_directory_created_during_export_is_left_intact_if_publish_fails()
    {
        string directory = NewDirectory();
        string target = Path.Combine(directory, "frame.dng");
        string contents = Path.Combine(target, "keep.txt");

        Exception? failure = Record.Exception(() => ExportFile.Write(target, stage =>
        {
            File.WriteAllText(stage, "new");
            Directory.CreateDirectory(target);
            File.WriteAllText(contents, "keep");
        }));

        Assert.True(failure is IOException or UnauthorizedAccessException, $"Unexpected publish result: {failure}");
        Assert.Equal("keep", File.ReadAllText(contents));
        Assert.Equal(target, Assert.Single(Directory.EnumerateFileSystemEntries(directory)));
    }

    [Fact]
    public void Canonically_equivalent_names_reserve_different_outputs()
    {
        string directory = NewDirectory();
        var reserved = ExportFile.NewReservations();
        string? first = ExportFile.Reserve(directory, "Café", "dng", ExportFile.ConflictPolicy.Overwrite, reserved);
        string? second = ExportFile.Reserve(directory, "CAFE\u0301", "dng", ExportFile.ConflictPolicy.Overwrite, reserved);

        Assert.Equal("Café.dng", Path.GetFileName(first));
        Assert.Equal("CAFE\u0301 (2).dng", Path.GetFileName(second));
    }

    [Fact]
    public void A_directory_with_the_export_name_is_a_conflict()
    {
        string directory = NewDirectory();
        Directory.CreateDirectory(Path.Combine(directory, "frame.dng"));
        Assert.Null(ExportFile.Reserve(directory, "frame", "dng",
            ExportFile.ConflictPolicy.Skip, ExportFile.NewReservations()));
        Assert.Equal("frame (2).dng", Path.GetFileName(ExportFile.Reserve(directory, "frame", "dng",
            ExportFile.ConflictPolicy.Overwrite, ExportFile.NewReservations())));
    }

    [Fact]
    public void Stale_sweep_preserves_recent_and_live_staging_files()
    {
        string directory = NewDirectory();
        string recent = Create("2147483647-1-stage.dng", old: false);
        string live = Create($"{Environment.ProcessId}-2-stage.dng", old: true);
        string abandoned = Create("2147483647-3-stage.dng", old: true);
        string unrelated = Create("my-export.dng", old: true);

        Assert.Equal(1, ExportFile.CleanupStale(directory));
        Assert.False(File.Exists(abandoned));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(live));
        Assert.True(File.Exists(unrelated));

        string Create(string suffix, bool old)
        {
            string path = Path.Combine(directory, ExportFile.TempPrefix + suffix);
            File.WriteAllText(path, "test");
            if (old) File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-14));
            return path;
        }
    }
}
