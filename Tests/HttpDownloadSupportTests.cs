using System.Net;
using System.Net.Http;
using MAAUnified.Application.Services.VersionUpdate;

namespace MAAUnified.Tests;

public sealed class HttpDownloadSupportTests
{
    [Fact]
    public async Task GetWithRetryAfterAsync_RetriesOnceOnTooManyRequests()
    {
        var handler = new SequenceHandler(
            new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero) },
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("payload"),
            });
        using var client = new HttpClient(handler);

        using var response = await HttpDownloadSupport.GetWithRetryAfterAsync(
            client,
            "https://example.com/package.zip",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.CallCount);
        Assert.Equal("payload", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GetWithRetryAfterAsync_DoesNotRetryWithoutRetryAfter()
    {
        var handler = new SequenceHandler(
            new HttpResponseMessage(HttpStatusCode.TooManyRequests),
            new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(handler);

        using var response = await HttpDownloadSupport.GetWithRetryAfterAsync(
            client,
            "https://example.com/package.zip",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task CopyToAsync_ThrowsTimeoutWhenUpstreamStalls()
    {
        var previous = HttpDownloadSupport.IdleTimeout;
        HttpDownloadSupport.IdleTimeout = TimeSpan.FromMilliseconds(80);
        try
        {
            using var source = new StalledStream();
            using var destination = new MemoryStream();

            await Assert.ThrowsAsync<TimeoutException>(() =>
                HttpDownloadSupport.CopyToAsync(source, destination, progress: null, CancellationToken.None));
        }
        finally
        {
            HttpDownloadSupport.IdleTimeout = previous;
        }
    }

    private sealed class SequenceHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private int _index;

        public int CallCount => _index;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(responses[Math.Min(_index++, responses.Length - 1)]);
    }

    private sealed class StalledStream : MemoryStream
    {
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith(_ => 0, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => new(Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith(_ => 0, cancellationToken));
    }
}
