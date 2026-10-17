using OpenRevelare.Gui.Views;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class ImportReadinessTests
{
    [Fact]
    public void Optional_calibration_requires_its_reference_only_when_enabled()
    {
        string flat = Path.Combine(TestDataIsolation.Root, $"flat-{Guid.NewGuid():N}.tif");
        File.WriteAllText(flat, "test reference");
        try
        {
            Assert.Empty(ImportDialog.MissingReference(false, false, false, false, null));
            Assert.Contains("R/G/B", ImportDialog.MissingReference(true, false, false, false, null));
            Assert.NotEmpty(ImportDialog.MissingReference(true, false, true, false, null));
            Assert.Contains("LCC", ImportDialog.MissingReference(false, false, false, true, null));
            Assert.Empty(ImportDialog.MissingReference(true, true, false, true, flat));
        }
        finally { File.Delete(flat); }
    }
}
