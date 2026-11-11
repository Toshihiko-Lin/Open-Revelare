using System.Security.Cryptography;
using System.Text.Json;
using OpenRevelare.Gui.Services;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class PureNegNativeBundleTests
{
    [Theory]
    [InlineData("win-x64")]
    [InlineData("darwin-arm64")]
    [InlineData("darwin-x64")]
    [InlineData("linux-x64")]
    [InlineData("linux-arm64")]
    public void Bundles_require_matching_platform_revision_and_content_hash(string platform)
    {
        string root = Directory.CreateTempSubdirectory("pureneg-bundle-test-").FullName;
        try
        {
            string addon = Path.Combine(root, "filmdust.node");
            File.WriteAllBytes(addon, [1, 2, 3]);
            void Manifest(string target, string revision) => File.WriteAllText(Path.Combine(root, "manifest.json"),
                JsonSerializer.Serialize(new { platform = target, revision,
                    sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(addon))) }));
            Manifest(platform, PureNegComponent.Revision);
            Assert.Equal(addon, PureNegComponent.ValidateNativeBundle(root, platform));
            Assert.Null(PureNegComponent.ValidateNativeBundle(root, "different-platform"));
            File.WriteAllBytes(addon, [4, 5, 6]);
            Assert.Null(PureNegComponent.ValidateNativeBundle(root, platform));
            Manifest(platform, "different-revision");
            Assert.Null(PureNegComponent.ValidateNativeBundle(root, platform));
        }
        finally { Directory.Delete(root, true); }
    }
}
