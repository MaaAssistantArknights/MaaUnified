using System.Net;
using System.Text.Json.Nodes;
using MAAUnified.Application.Services.Features;

namespace MAAUnified.Tests;

/// <summary>
/// 作业站神秘代码解析对齐 WPF CopilotViewModel.TryParseCopilotCode：
/// 支持五种格式（prts://s12345、prts://12345、maa://12345、s12345、12345），
/// prts:// 为作业站新分享格式，s 前缀为作业集。
/// </summary>
public sealed class CopilotCodeParsingTests
{
    [Theory]
    [InlineData("prts://99474", 99474)]
    [InlineData("PRTS://99474", 99474)]
    [InlineData("prts:///99474", 99474)]
    [InlineData("maa://99474", 99474)]
    [InlineData("maa:///99474", 99474)]
    [InlineData("99474", 99474)]
    public async Task LoadFromCodeAsync_SupportedSingleCopilotFormats_ResolveId(string source, int expectedId)
    {
        var handler = new CapturingHandler(_ => CopilotJson(200, "1-7"));
        var service = new CopilotFeatureService(new HttpClient(handler));

        var result = await service.LoadFromCodeAsync(source);

        Assert.True(result.Success, result.Message);
        Assert.Equal(expectedId, result.Value!.CopilotId);
        Assert.Contains($"/{expectedId}", handler.Requests.Single().AbsolutePath, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("prts://s12345", 12345)]
    [InlineData("PRTS://S12345", 12345)]
    [InlineData("s12345", 12345)]
    [InlineData("S12345", 12345)]
    [InlineData("12345", 12345)]
    [InlineData("maa://12345", 12345)]
    public async Task LoadSetFromCodeAsync_SupportedSetFormats_ResolveId(string source, int expectedId)
    {
        var handler = new CapturingHandler(uri => uri.AbsolutePath.Contains("/set/get", StringComparison.Ordinal)
            ? SetJson(200, expectedId)
            : CopilotJson(200, "1-7"));
        var service = new CopilotFeatureService(new HttpClient(handler));

        var result = await service.LoadSetFromCodeAsync(source);

        Assert.True(result.Success, result.Message);
        Assert.Contains($"id={expectedId}", handler.Requests[0].Query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("prts://s12345")]
    [InlineData("s12345")]
    [InlineData("S12345")]
    public async Task LoadFromCodeAsync_SetCodeOnSingleCopilotEntry_ReturnsActionableGuidance(string source)
    {
        var handler = new CapturingHandler(_ => CopilotJson(200, "1-7"));
        var service = new CopilotFeatureService(new HttpClient(handler));

        var result = await service.LoadFromCodeAsync(source);

        Assert.False(result.Success);
        Assert.Empty(handler.Requests);
        Assert.Contains("作业集", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("prts://abc")]
    [InlineData("prts://")]
    [InlineData("prts://sabc")]
    [InlineData("not-a-code")]
    public async Task LoadFromCodeAsync_InvalidCode_ReturnsActionableFailure(string source)
    {
        var handler = new CapturingHandler(_ => CopilotJson(200, "1-7"));
        var service = new CopilotFeatureService(new HttpClient(handler));

        var result = await service.LoadFromCodeAsync(source);

        Assert.False(result.Success);
        Assert.Empty(handler.Requests);
        Assert.Contains("prts://", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubmitFeedbackAsync_PrtsScheme_ExtractsNumericId()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var service = new CopilotFeatureService(new HttpClient(handler));

        var result = await service.SubmitFeedbackAsync("prts://99474", like: true);

        Assert.True(result.Success, result.Message);
        var body = JsonNode.Parse(handler.LastRequestBody ?? string.Empty);
        Assert.Equal(99474, (int)body!["id"]!);
    }

    private static HttpResponseMessage CopilotJson(int statusCode, string stageName)
    {
        var payload = new JsonObject
        {
            ["status_code"] = statusCode,
            ["data"] = new JsonObject
            {
                ["id"] = 99474,
                ["title"] = "测试作业",
                ["content"] = new JsonObject
                {
                    ["stage_name"] = stageName,
                    ["minimum_required"] = "v4.0",
                    ["actions"] = new JsonArray(new JsonObject { ["type"] = "Deploy" }),
                },
            },
        };
        return Json(HttpStatusCode.OK, payload);
    }

    private static HttpResponseMessage SetJson(int statusCode, int setId)
    {
        var payload = new JsonObject
        {
            ["status_code"] = statusCode,
            ["data"] = new JsonObject
            {
                ["id"] = setId,
                ["name"] = "测试作业集",
                ["copilot_ids"] = new JsonArray(99474),
            },
        };
        return Json(HttpStatusCode.OK, payload);
    }

    private static HttpResponseMessage Json(HttpStatusCode code, JsonObject payload)
    {
        return new HttpResponseMessage(code)
        {
            Content = new StringContent(payload.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
        };
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<Uri, HttpResponseMessage> _responder;

        public CapturingHandler(Func<Uri, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        public List<Uri> Requests { get; } = [];

        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("request without uri");
            Requests.Add(uri);
            if (request.Content is not null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return _responder(uri);
        }
    }
}
