using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenRevelare.Gui.Services;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class PureNegRuntimePackageTests
{
    [Theory]
    [InlineData("windows", Architecture.X64, "win-x64")]
    [InlineData("macos", Architecture.Arm64, "osx-arm64")]
    [InlineData("macos", Architecture.X64, "osx-x64")]
    [InlineData("linux", Architecture.X64, "linux-x64")]
    public void Package_follows_the_running_platform(string os, Architecture arch, string expected) =>
        Assert.Equal(expected, PureNegRuntimePackage.SelectPlatform(os, arch));

    [Theory]
    [InlineData("runtime/../../outside")]
    [InlineData("src/processor.cjs")]
    [InlineData("runtime/../model")]
    public async Task Runtime_archive_cannot_overwrite_engine_sources_or_escape_installation(string entry)
    {
        string root = Directory.CreateTempSubdirectory("pureneg-runtime-test-").FullName;
        try
        {
            string archive = Path.Combine(root, "test.zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry(entry).Open())) writer.Write("invalid");
            await Assert.ThrowsAsync<InvalidDataException>(() => PureNegRuntimePackage.ExtractAsync(archive, Path.Combine(root, "install"), CancellationToken.None));
            Assert.False(Directory.Exists(Path.Combine(root, "install")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task First_install_downloads_platform_model_and_runtime_but_does_not_mark_unprobed_runtime_ready()
    {
        string root = Directory.CreateTempSubdirectory("pureneg-runtime-test-").FullName;
        try
        {
            byte[] archive;
            using (var stream = new MemoryStream())
            {
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    void Write(string name) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write("fixture"); }
                    if (OperatingSystem.IsMacOS()) { Write("runtime/image-codec"); Write("runtime/FibreNet.mlmodelc/model.mil"); }
                    else { Write("runtime/FibreNet.onnx"); Write("runtime/pureneg-model-host" + (OperatingSystem.IsWindows() ? ".exe" : "")); }
                }
                archive = stream.ToArray();
            }
            string index = JsonSerializer.Serialize(new { revision = PureNegComponent.Revision, platforms = new Dictionary<string, object>
            {
                [PureNegRuntimePackage.CurrentPlatform] = new { file = "platform.zip", size = archive.Length,
                    sha256 = Convert.ToHexString(SHA256.HashData(archive)), modelFormat = OperatingSystem.IsMacOS() ? "coreml" : "onnx" },
            } });
            using var handler = new Handler(index, archive);
            using var client = new HttpClient(handler);
            await PureNegRuntimePackage.InstallAsync(client, root, new Progress<int>(), CancellationToken.None);
            Assert.Equal(2, handler.Requests.Count);
            Assert.EndsWith("/components.json", handler.Requests[0]);
            Assert.EndsWith("/platform.zip", handler.Requests[1]);
            Assert.False(PureNegRuntimePackage.IsReady(root)); // Actual inference must succeed first.
            Assert.False(File.Exists(Path.Combine(root, "runtime.zip")));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class Handler(string index, byte[] archive) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string url = request.RequestUri!.ToString();
            Requests.Add(url);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = url.EndsWith("components.json", StringComparison.Ordinal)
                    ? new StringContent(index, Encoding.UTF8, "application/json") : new ByteArrayContent(archive),
            });
        }
    }
}
