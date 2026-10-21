using OpenRevelare.Gui.Services;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class UpdaterVersionTests
{
    [Theory]
    [InlineData("1.8.5.0", "1.8.5")]
    [InlineData("1.8.5.1", "1.8.5.1")]
    public void App_version_preserves_hotfix_revision(string assemblyVersion, string expected)
    {
        Assert.Equal(expected, AppInfo.FormatVersion(new Version(assemblyVersion)));
    }

    [Theory]
    [InlineData("1.8.5.1", "1.8.5", 1)]
    [InlineData("1.8.5.1", "1.8.5.1", 0)]
    [InlineData("1.8.5.2", "1.8.5.1", 1)]
    [InlineData("1.8.5.10", "1.8.5.2", 1)]
    [InlineData("1.8.6", "1.8.5.1", 1)]
    [InlineData("1.8.5", "1.8.5.1", -1)]
    public void Update_comparison_orders_hotfixes_numerically(string remote, string current, int expected)
    {
        Assert.Equal(expected, Math.Sign(Updater.Compare(remote, current)));
    }

    [Fact]
    public void Running_release_does_not_offer_itself_as_an_update()
    {
        string release = typeof(AppInfo).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single()
            .InformationalVersion.Split('+')[0];
        Assert.Equal(release, AppInfo.Version);
        Assert.Equal(0, Updater.Compare(release, AppInfo.Version));
    }
}
