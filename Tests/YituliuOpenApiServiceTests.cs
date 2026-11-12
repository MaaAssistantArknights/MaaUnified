using System.Net;
using System.Net.Http;
using System.Text;
using MAAUnified.Application.Services.ThirdParty;

namespace MAAUnified.Tests;

/// <summary>
/// P1-10 (WPF 247db567f3, YituliuApiService): yituliu OpenAPI transport/parsing. The HttpClient is
/// injected so the request shape and business-code mapping are verifiable without network access.
/// </summary>
public sealed class YituliuOpenApiServiceTests
{
    [Fact]
    public async Task GetOperatorInfoAsync_ShouldSendRawTokenAndParseProgression()
    {
        const string payload = """
            {
              "code": 200,
              "msg": "ok",
              "data": [
                {
                  "id": "char_102_texas",
                  "level": 80,
                  "evolvePhase": 2,
                  "mainSkillLevel": 7,
                  "potentialRank": 3,
                  "skills": [ { "id": "sk_1", "level": 3 }, { "id": "sk_2", "level": 0 } ],
                  "equips": [ { "id": "uniequip_001", "type": "X", "level": 3 } ]
                }
              ]
            }
            """;
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, payload);
        var service = new YituliuOpenApiService(new HttpClient(handler));

        var result = await service.GetOperatorInfoAsync("third-party-token");

        Assert.Equal(YituliuTokenValidationResult.Valid, result.Result);
        var oper = Assert.Single(result.Operators);
        Assert.Equal("char_102_texas", oper.Id);
        Assert.Equal(80, oper.Level);
        Assert.Equal(2, oper.EvolvePhase);
        Assert.Equal(7, oper.MainSkillLevel);
        Assert.Equal(3, oper.PotentialRank);
        Assert.Equal(["sk_1:3", "sk_2:0"], oper.Skills.Select(skill => $"{skill.Id}:{skill.Level}"));
        var equip = Assert.Single(oper.Equips);
        Assert.Equal("uniequip_001", equip.Id);
        Assert.Equal("X", equip.Type);
        Assert.Equal(3, equip.Level);

        Assert.Equal(YituliuOpenApiService.OperatorInfoUrl, handler.LastRequestUri?.ToString());
        Assert.True(handler.LastRequestHeaders!.TryGetValues("Authorization", out var authorization));
        Assert.Equal("third-party-token", Assert.Single(authorization!));
    }

    [Fact]
    public async Task GetOperatorInfoAsync_ShouldMapBusinessCodes()
    {
        var writeOnly = await CreateService(HttpStatusCode.OK, """{ "code": 20010, "msg": "write only" }""")
            .GetOperatorInfoAsync("token");
        Assert.Equal(YituliuTokenValidationResult.WriteOnly, writeOnly.Result);
        Assert.Empty(writeOnly.Operators);

        var invalid = await CreateService(HttpStatusCode.OK, """{ "code": 20027, "msg": "invalid" }""")
            .GetOperatorInfoAsync("token");
        Assert.Equal(YituliuTokenValidationResult.Invalid, invalid.Result);
        Assert.Empty(invalid.Operators);

        var unknown = await CreateService(HttpStatusCode.OK, """{ "code": 500, "msg": "unknown" }""")
            .GetOperatorInfoAsync("token");
        Assert.Equal(YituliuTokenValidationResult.Invalid, unknown.Result);
    }

    [Fact]
    public async Task GetOperatorInfoAsync_ShouldReportNetworkAndPayloadFailures()
    {
        var httpFailure = await CreateService(HttpStatusCode.InternalServerError, "oops")
            .GetOperatorInfoAsync("token");
        Assert.Equal(YituliuTokenValidationResult.NetworkError, httpFailure.Result);

        // WPF RequestOperatorInfoAsync catches deserialization failures and reports NetworkError.
        var malformed = await CreateService(HttpStatusCode.OK, "not-json")
            .GetOperatorInfoAsync("token");
        Assert.Equal(YituliuTokenValidationResult.NetworkError, malformed.Result);

        var emptyData = await CreateService(HttpStatusCode.OK, """{ "code": 200, "data": [] }""")
            .GetOperatorInfoAsync("token");
        Assert.Equal(YituliuTokenValidationResult.Valid, emptyData.Result);
        Assert.Empty(emptyData.Operators);
    }

    [Fact]
    public async Task GetOperatorInfoAsync_WithEmptyToken_ShouldNotSendRequest()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, "{}");
        var service = new YituliuOpenApiService(new HttpClient(handler));

        var result = await service.GetOperatorInfoAsync("   ");

        Assert.Equal(YituliuTokenValidationResult.Invalid, result.Result);
        Assert.Equal(0, handler.CallCount);
    }

    private static YituliuOpenApiService CreateService(HttpStatusCode statusCode, string payload)
    {
        return new YituliuOpenApiService(new HttpClient(new StubHttpMessageHandler(statusCode, payload)));
    }

    internal sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _payload;

        public StubHttpMessageHandler(HttpStatusCode statusCode, string payload)
        {
            _statusCode = statusCode;
            _payload = payload;
        }

        public int CallCount { get; private set; }

        public Uri? LastRequestUri { get; private set; }

        public System.Net.Http.Headers.HttpRequestHeaders? LastRequestHeaders { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequestUri = request.RequestUri;
            LastRequestHeaders = request.Headers;
            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_payload, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }
}
