using System.Net;
using MAAUnified.Application.Services;

namespace MAAUnified.Tests;

public sealed class GameDataReportServiceTests
{
    private const string PenguinUrl = "https://penguin-stats.io/api/v2/report";
    private const string YituliuUrl = "https://yituliu.example.com/api/report";

    [Fact]
    public async Task PostWithRetryAsync_SuccessWithPenguinIdHeader_ShouldInvokeCallback()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Headers = { { GameDataReportService.PenguinIdResponseHeader, "penguin-123" } },
        });
        var service = CreateService(handler, out _);
        string? assignedId = null;

        var success = await service.PostWithRetryAsync(
            PenguinUrl,
            "{}",
            null,
            GameDataReportService.PenguinStatsSubtask,
            penguinId => assignedId = penguinId);

        Assert.True(success);
        Assert.Equal("penguin-123", assignedId);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task PostWithRetryAsync_SuccessWithoutPenguinIdHeader_ShouldNotInvokeCallback()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var service = CreateService(handler, out _);
        var invoked = false;

        var success = await service.PostWithRetryAsync(
            PenguinUrl,
            "{}",
            null,
            GameDataReportService.PenguinStatsSubtask,
            _ => invoked = true);

        Assert.True(success);
        Assert.False(invoked);
    }

    [Fact]
    public async Task PostWithRetryAsync_ServerErrorThenSuccess_ShouldRetryWithWpfBackoff()
    {
        var attempt = 0;
        var handler = new FakeHandler(_ =>
        {
            attempt++;
            return new HttpResponseMessage(attempt < 3 ? HttpStatusCode.InternalServerError : HttpStatusCode.OK);
        });
        var service = CreateService(handler, out var delays);

        var success = await service.PostWithRetryAsync(
            YituliuUrl,
            "{}",
            null,
            "ReportToYituliu");

        Assert.True(success);
        Assert.Equal(3, handler.Requests.Count);
        // WPF TryPostAsync: 3000ms initial, x1.5 growth, including a final delay after the
        // last failed attempt would only occur when all attempts fail; here the 3rd succeeds.
        Assert.Equal([3000, 4500], delays);
    }

    [Fact]
    public async Task PostWithRetryAsync_ClientError_ShouldNotRetry()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        var service = CreateService(handler, out var delays);

        var success = await service.PostWithRetryAsync(
            YituliuUrl,
            "{}",
            null,
            "ReportToYituliu");

        // Non-penguin failure is silent and treated as success (一图流的上报失败不提示).
        Assert.True(success);
        Assert.Single(handler.Requests);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task PostWithRetryAsync_YituliuPersistentFailure_ShouldStaySilent()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var service = CreateService(handler, out var delays);

        var success = await service.PostWithRetryAsync(
            YituliuUrl,
            "{}",
            null,
            "ReportToYituliu");

        Assert.True(success);
        Assert.Equal(GameDataReportService.MaxRetryPerDomain, handler.Requests.Count);
        Assert.Equal([3000, 4500, 6750], delays);
    }

    [Fact]
    public async Task PostWithRetryAsync_PenguinPrimaryFails_ShouldTryBackupDomain()
    {
        var handler = new FakeHandler(request =>
            request.RequestUri!.Host == "penguin-stats.cn"
                ? new HttpResponseMessage(HttpStatusCode.OK)
                : new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var service = CreateService(handler, out _);

        var success = await service.PostWithRetryAsync(
            PenguinUrl,
            "{}",
            null,
            GameDataReportService.PenguinStatsSubtask);

        Assert.True(success);
        Assert.Equal(GameDataReportService.MaxRetryPerDomain + 1, handler.Requests.Count);
        Assert.All(handler.Requests.Take(GameDataReportService.MaxRetryPerDomain), request =>
            Assert.Equal("penguin-stats.io", request.RequestUri!.Host));
        Assert.Equal("penguin-stats.cn", handler.Requests[^1].RequestUri!.Host);
    }

    [Fact]
    public async Task PostWithRetryAsync_PenguinAllDomainsFail_ShouldReturnFalse()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var service = CreateService(handler, out _);

        var success = await service.PostWithRetryAsync(
            PenguinUrl,
            "{}",
            null,
            GameDataReportService.PenguinStatsSubtask);

        Assert.False(success);
        Assert.Equal(
            GameDataReportService.MaxRetryPerDomain * (1 + GameDataReportService.PenguinBackupDomains.Length),
            handler.Requests.Count);
    }

    [Fact]
    public async Task PostWithRetryAsync_PenguinSubtaskOnNonPenguinUrl_ShouldNotTryBackup()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var service = CreateService(handler, out _);

        var success = await service.PostWithRetryAsync(
            "https://mirror.penguin-stats.example.com/report",
            "{}",
            null,
            GameDataReportService.PenguinStatsSubtask);

        // WPF only falls back to backup domains when the URL contains the penguin.io domain.
        Assert.True(success);
        Assert.Equal(GameDataReportService.MaxRetryPerDomain, handler.Requests.Count);
    }

    [Fact]
    public async Task PostWithRetryAsync_ShouldSendBodyHeadersAndJsonContentType()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var service = CreateService(handler, out _);
        var headers = new Dictionary<string, string>
        {
            ["X-Penguin-Idempotency-Key"] = "MAAabcdef",
        };

        var success = await service.PostWithRetryAsync(
            PenguinUrl,
            "{\"drop\":\"30012\"}",
            headers,
            GameDataReportService.PenguinStatsSubtask);

        Assert.True(success);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("{\"drop\":\"30012\"}", handler.Bodies[0]);
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("utf-8", request.Content.Headers.ContentType.CharSet);
        Assert.Equal("MAAabcdef", request.Headers.GetValues("X-Penguin-Idempotency-Key").Single());
    }

    [Fact]
    public async Task PostWithRetryAsync_HandlerThrows_ShouldReturnFalseForPenguin()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("network down"));
        var service = CreateService(handler, out _);

        var success = await service.PostWithRetryAsync(
            PenguinUrl,
            "{}",
            null,
            GameDataReportService.PenguinStatsSubtask);

        Assert.False(success);
        // Exceptions abort the per-domain retry loop (WPF TryPostAsync catch), so each domain
        // is hit exactly once: primary + every backup domain.
        Assert.Equal(1 + GameDataReportService.PenguinBackupDomains.Length, handler.Requests.Count);
    }

    [Fact]
    public void BuildDefaultHttpClient_ShouldMatchWpfTransportDefaults()
    {
        // Frozen WPF reference: HttpService.BuildHttpClient sets Timeout = 15s and
        // User-Agent = MaaWpfGui/{informationalVersion}; MAAUnified uses its own product name.
        using var client = GameDataReportService.BuildDefaultHttpClient();

        Assert.Equal(TimeSpan.FromSeconds(15), client.Timeout);
        var userAgent = Assert.Single(client.DefaultRequestHeaders.UserAgent);
        Assert.Equal("MAAUnified", userAgent.Product!.Name);
        Assert.False(string.IsNullOrEmpty(userAgent.Product.Version));
        Assert.DoesNotContain("+", userAgent.Product.Version);
    }

    [Fact]
    public async Task PostWithRetryAsync_ShouldSendAcceptJsonAndHttp2RequestVersion()
    {
        // Frozen WPF reference: HttpService.PostAsync sets request Version = HTTP/2 and
        // appends Accept: application/json after the caller-provided headers.
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var service = CreateService(handler, out _);

        var success = await service.PostWithRetryAsync(
            PenguinUrl,
            "{}",
            null,
            GameDataReportService.PenguinStatsSubtask);

        Assert.True(success);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpVersion.Version20, request.Version);
        Assert.Contains(request.Headers.Accept, mediaType => mediaType.MediaType == "application/json");
    }

    private static GameDataReportService CreateService(FakeHandler handler, out List<int> delays)
    {
        var recordedDelays = new List<int>();
        delays = recordedDelays;
        return new GameDataReportService(
            new HttpClient(handler),
            delayAsync: (milliseconds, _) =>
            {
                recordedDelays.Add(milliseconds);
                return Task.CompletedTask;
            });
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return _responder(request);
        }
    }
}
