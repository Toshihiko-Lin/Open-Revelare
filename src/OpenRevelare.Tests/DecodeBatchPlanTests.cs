using OpenRevelare.Gui.Services;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class DecodeBatchPlanTests
{
    [Fact]
    public void Split_sources_are_bounded_raw_jobs_stay_independent_and_identity_is_not_lost()
    {
        var jobs = Enumerable.Range(0, 9).Select(i => (Id: i, Path: "strip.fff"))
            .Concat(new[] { (Id: 9, Path: "photo.nef"), (Id: 10, Path: "photo.nef"), (Id: 11, Path: "other.tif") }).ToArray();
        var batches = DecodeBatchPlan.Create(jobs, j => j.Path, path => path.EndsWith(".nef"));
        Assert.All(batches, batch => Assert.InRange(batch.Length, 1, 4));
        Assert.All(batches.Where(b => b[0].Path.EndsWith(".nef")), batch => Assert.Single(batch));
        Assert.Equal(jobs.Select(j => j.Id), batches.SelectMany(b => b).Select(j => j.Id));
        Assert.All(batches, batch => Assert.Single(batch.Select(j => j.Path).Distinct()));
    }
}
