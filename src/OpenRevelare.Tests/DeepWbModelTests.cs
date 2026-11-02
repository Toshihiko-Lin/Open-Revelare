using System.Net;
using System.Net.Http;
using OpenRevelare.DeepWb.Onnx;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class DeepWbModelTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "OpenRevelare-deepwb-" + Guid.NewGuid().ToString("N"));
    private string Destination => Path.Combine(_directory, "net_awb.onnx");

    [Fact]
    public async Task FailedDownloadPreservesExistingModelAndRemovesTemporaryFile()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Destination, "existing model");
        using var client = new HttpClient(new Handler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 1, 2, 3 })
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => DeepWbModel.InstallAsync(client, Destination, null, default));
        Assert.Equal("existing model", await File.ReadAllTextAsync(Destination));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task HttpErrorDoesNotInstallModel()
    {
        using var client = new HttpClient(new Handler(() => new HttpResponseMessage(HttpStatusCode.NotFound)));
        await Assert.ThrowsAsync<HttpRequestException>(() => DeepWbModel.InstallAsync(client, Destination, null, default));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task CancellationDoesNotInstallModel()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var client = new HttpClient(new Handler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 1 })
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DeepWbModel.InstallAsync(client, Destination, null, cancellation.Token));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task SameSizeCorruptDownloadIsRejected()
    {
        using var client = new HttpClient(new Handler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[(int)DeepWbModel.Size])
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => DeepWbModel.InstallAsync(client, Destination, null, default));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task CancellationDuringDownloadRemovesPartialFile()
    {
        using var cancellation = new CancellationTokenSource();
        using var client = new HttpClient(new Handler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[200000])
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DeepWbModel.InstallAsync(
            client, Destination, new CancelProgress(cancellation), cancellation.Token));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task VerifiedDownloadInstallsExactModel()
    {
        // Use the repository's known model as the HTTP body, without requiring network access.
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "OpenRevelare.sln"))) root = root.Parent;
        Assert.NotNull(root);
        string source = Path.Combine(root.FullName, "models", "net_awb.onnx");
        using var client = new HttpClient(new Handler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(File.OpenRead(source))
        }));
        await DeepWbModel.InstallAsync(client, Destination, null, default);
        Assert.Equal(DeepWbModel.Size, new FileInfo(Destination).Length);
        Assert.Single(Directory.GetFiles(_directory));
        using var corrector = new OnnxDeepWbCorrector(Destination);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PreferredSourceFailureFallsBackToVerifiedModel(bool corruptPrimary, bool preferMirror)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "OpenRevelare.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var requested = new List<string>();
        using var client = new HttpClient(new SourceHandler(request =>
        {
            requested.Add(request.RequestUri!.AbsoluteUri);
            return request.RequestUri.AbsoluteUri == (preferMirror ? DeepWbModel.MirrorDownloadUrl : DeepWbModel.DownloadUrl)
                ? new HttpResponseMessage(corruptPrimary ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable)
                {
                    Content = new ByteArrayContent(new byte[] { 1, 2, 3 })
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(File.OpenRead(Path.Combine(root.FullName, "models", "net_awb.onnx")))
                };
        }));
        await DeepWbModel.InstallFromSourcesAsync(client, Destination, null, default, preferMirror);
        Assert.Equal(preferMirror ? new[] { DeepWbModel.MirrorDownloadUrl, DeepWbModel.DownloadUrl }
            : new[] { DeepWbModel.DownloadUrl, DeepWbModel.MirrorDownloadUrl }, requested);
        Assert.Equal(DeepWbModel.Size, new FileInfo(Destination).Length);
    }

    [Fact]
    public async Task UserCancellationDoesNotTryMirror()
    {
        using var cancellation = new CancellationTokenSource();
        int requests = 0;
        using var client = new HttpClient(new SourceHandler(_ =>
        {
            requests++;
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DeepWbModel.InstallFromSourcesAsync(client, Destination, null, cancellation.Token));
        Assert.Equal(1, requests);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    private sealed class SourceHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response(request));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class Handler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(response());
        }
    }

    private sealed class CancelProgress(CancellationTokenSource cancellation) : IProgress<int>
    {
        public void Report(int value) => cancellation.Cancel();
    }
}
