using OpenRevelare.Gui.Services;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class ImportFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(TestDataIsolation.Root, $"drop-{Guid.NewGuid():N}");

    public ImportFilesTests() => Directory.CreateDirectory(_root);

    private string FileAt(string name)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "admission test; not a decoded image");
        return path;
    }

    [Fact]
    public void Folder_and_explicit_file_drop_deduplicate_and_keep_folder_import_shallow()
    {
        string tiff = FileAt("01.TIFF");
        string raw = FileAt("02.dng");
        FileAt("preview.jpg");
        FileAt("another-roll/03.tif");
        var files = ImportFiles.Collect(new[] { _root, tiff, raw });
        Assert.Equal(new[] { tiff, raw }, files.Images);
        Assert.Empty(files.Projects);
        Assert.Empty(files.Rejected);
    }

    [Fact]
    public void Mixed_drop_classifies_project_images_and_unsupported_files_separately()
    {
        string project = FileAt("roll.ncproj");
        string image = FileAt("frame.tif");
        string preview = FileAt("preview.png");
        var files = ImportFiles.Collect(new[] { project, image, preview, project });
        Assert.Equal(new[] { image }, files.Images);
        Assert.Equal(new[] { project }, files.Projects);
        Assert.Equal(new[] { preview }, files.Rejected);
    }

    [Fact]
    public void Missing_and_invalid_entries_do_not_discard_the_usable_images()
    {
        string missing = Path.Combine(_root, "missing.tif");
        string image = FileAt("frame.tif");
        var files = ImportFiles.Collect(new[] { missing, "\0", image });
        Assert.Equal(new[] { image }, files.Images);
        Assert.Equal(new[] { missing, "\0" }, files.Rejected);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
