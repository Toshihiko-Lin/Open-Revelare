using OpenRevelare.Core;
using OpenRevelare.Gui.Models;
using OpenRevelare.Gui.Services;
using Xunit;

namespace OpenRevelare.Tests;

// A missing last target or a failed geometry calculation used to allow earlier targets
// to change. Preparation must leave the entire live roll untouched until publication.
public sealed class BatchParameterEditTests : IDisposable
{
    private readonly string _folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
        "revelare-batch-" + Guid.NewGuid().ToString("N"));

    public BatchParameterEditTests() => Directory.CreateDirectory(_folder);

    private RollFrame Frame(string name, bool exists = true)
    {
        string path = System.IO.Path.Combine(_folder, name);
        if (exists) File.WriteAllText(path, "source");
        return new RollFrame(path);
    }

    [Fact]
    public void Missing_last_target_rejects_before_editing_any_parameters()
    {
        var first = Frame("first.tif");
        var missing = Frame("missing.tif", exists: false);
        int calls = 0;
        Assert.Throws<IOException>(() => BatchParameterEdit.Prepare(new[] { first, missing },
            p => { calls++; p.ExposureEv = 2; }));
        Assert.Equal(0, calls);
        Assert.Equal(0, first.Params.ExposureEv);
    }

    [Fact]
    public void Failed_second_calculation_does_not_publish_the_first()
    {
        var first = Frame("first.tif");
        var second = Frame("second.tif");
        int calls = 0;
        Assert.Throws<InvalidOperationException>(() => BatchParameterEdit.Prepare(
            new[] { first, second }, p =>
            {
                p.TBase[0] = 0.25;
                if (++calls == 2) throw new InvalidOperationException("crop failed");
            }));
        Assert.Equal(2, calls);
        Assert.NotEqual(0.25, first.Params.TBase[0]);
        Assert.NotEqual(0.25, second.Params.TBase[0]);
    }

    [Fact]
    public void Success_prepares_independent_values_and_preserves_unedited_fields()
    {
        var first = Frame("first.tif");
        first.Params.Rotation = 3;
        first.Params.CropRect = (0.1, 0.2, 0.5, 0.6);
        var copy = RollFrame.MakeVirtualCopy(first);
        var prepared = BatchParameterEdit.Prepare(new[] { first, copy }, p => p.ExposureEv = 2);
        Assert.Equal(0, first.Params.ExposureEv);
        Assert.Equal(0, copy.Params.ExposureEv);
        Assert.All(prepared, item =>
        {
            Assert.Equal(2, item.Params.ExposureEv);
            Assert.Equal(3, item.Params.Rotation);
            Assert.Equal(first.Params.CropRect, item.Params.CropRect);
            Assert.NotSame(item.Frame.Params, item.Params);
        });
        prepared[0].Params.TBase[0] = 0.25;
        Assert.NotEqual(0.25, prepared[1].Params.TBase[0]);
        Assert.NotEqual(0.25, first.Params.TBase[0]);
    }

    [Fact]
    public void Broadcast_requires_source_but_copied_snapshot_survives_source_deletion()
    {
        var source = Frame("source.tif");
        var target = Frame("target.tif");
        var clipboard = source.Params.Clone();
        clipboard.ExposureEv = 1.5;
        File.Delete(source.Path);
        Assert.Throws<IOException>(() => BatchParameterEdit.Prepare(new[] { target },
            p => p.ExposureEv = clipboard.ExposureEv, source));
        var prepared = BatchParameterEdit.Prepare(new[] { target },
            p => p.ExposureEv = clipboard.ExposureEv);
        Assert.Equal(1.5, prepared[0].Params.ExposureEv);
        Assert.Equal(0, target.Params.ExposureEv);
    }

    public void Dispose()
    {
        foreach (string path in Directory.EnumerateFiles(_folder)) File.Delete(path);
        Directory.Delete(_folder);
    }
}
