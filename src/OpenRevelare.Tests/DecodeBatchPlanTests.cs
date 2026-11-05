using OpenRevelare.Gui.Services;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class DecodeBatchPlanTests
{
    [Fact]
    public void Split_sources_are_bounded_raw_crops_share_a_batch_and_identity_is_not_lost()
    {
        var jobs = Enumerable.Range(0, 9).Select(i => (Id: i, Path: "strip.fff"))
            .Concat(new[] { (Id: 9, Path: "photo.nef"), (Id: 10, Path: "photo.nef"), (Id: 11, Path: "other.tif") }).ToArray();
        var batches = DecodeBatchPlan.Create(jobs, j => j.Path);
        Assert.All(batches, batch => Assert.InRange(batch.Length, 1, 4));
        Assert.Equal(2, Assert.Single(batches, b => b[0].Path.EndsWith(".nef")).Length);
        Assert.Equal(jobs.Select(j => j.Id), batches.SelectMany(b => b).Select(j => j.Id));
        Assert.All(batches, batch => Assert.Single(batch.Select(j => j.Path).Distinct()));
    }
}
