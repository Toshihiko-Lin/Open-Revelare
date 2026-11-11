using System.Net;
using System.Security.Cryptography;
using System.Text;
using OpenRevelare.Gui.Services;
using Xunit;

namespace OpenRevelare.Tests;

public sealed class PureNegDownloadTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Unavailable_or_corrupt_primary_retries_other_source_without_retaining_partial_data(bool preferMirror, bool corrupt)
    {
        string root = Directory.CreateTempSubdirectory("pureneg-download-test-").FullName;
        try
        {
            byte[] expected = Encoding.UTF8.GetBytes("verified package");
            var requests = new List<string>();
            using var handler = new Handler((request, _) =>
            {
                requests.Add(request.RequestUri!.Host);
                return Task.FromResult(requests.Count == 1
                    ? new HttpResponseMessage(corrupt ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable)
                        { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("bad")) }
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(expected) });
            });
            using var client = new HttpClient(handler);
            string path = Path.Combine(root, "package");
            await PureNegDownload.DownloadAsync(client, "package.zip", path,
                Convert.ToHexString(SHA256.HashData(expected)), 100, preferMirror, CancellationToken.None,
                expectedSize: expected.Length);
            Assert.Equal(preferMirror ? new[] { "gitee.com", "github.com" } : new[] { "github.com", "gitee.com" }, requests);
            Assert.Equal(expected, await File.ReadAllBytesAsync(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task User_cancellation_does_not_start_mirror_download()
    {
        using var cancel = new CancellationTokenSource();
        int requests = 0;
        using var handler = new Handler((_, ct) =>
        {
            requests++;
            cancel.Cancel();
            ct.ThrowIfCancellationRequested();
            throw new Exception("unreachable");
        });
        using var client = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PureNegDownload.FromSourcesAsync("components.json", false,
            cancel.Token, async (url, ct) => await PureNegDownload.GetAsync(client, url, ct)));
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task Both_failed_sources_remove_invalid_download()
    {
        string root = Directory.CreateTempSubdirectory("pureneg-download-test-").FullName;
        try
        {
            using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new ByteArrayContent(new byte[101]) }));
            using var client = new HttpClient(handler);
            string path = Path.Combine(root, "package");
            var error = await Assert.ThrowsAsync<IOException>(() => PureNegDownload.DownloadAsync(client,
                "package.zip", path, null, 100, false, CancellationToken.None));
            Assert.Equal(2, Assert.IsType<AggregateException>(error.InnerException).InnerExceptions.Count);
            Assert.False(File.Exists(path));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
}
