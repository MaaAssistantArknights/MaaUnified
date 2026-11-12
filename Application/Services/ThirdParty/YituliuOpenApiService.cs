using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace MAAUnified.Application.Services.ThirdParty;

/// <summary>
/// Token validation outcome of the yituliu OpenAPI. Mirrors WPF
/// YituliuApiService.TokenValidationResult (247db567f3): the business result is carried in the
/// response body `code`, not the HTTP status.
/// </summary>
public enum YituliuTokenValidationResult
{
    Valid = 0,
    WriteOnly = 1,
    Invalid = 2,
    NetworkError = 3,
}

public sealed record YituliuOperatorSkill(string Id, int Level);

public sealed record YituliuOperatorEquip(string Id, string Type, int Level);

/// <summary>
/// Operator progression returned by the yituliu OpenAPI (V2 format).
/// </summary>
public sealed record YituliuOperatorInfo(
    string Id,
    int Level,
    int EvolvePhase,
    int MainSkillLevel,
    int PotentialRank,
    IReadOnlyList<YituliuOperatorSkill> Skills,
    IReadOnlyList<YituliuOperatorEquip> Equips);

public sealed record YituliuOperatorInfoResult(
    YituliuTokenValidationResult Result,
    IReadOnlyList<YituliuOperatorInfo> Operators,
    string Message)
{
    public static YituliuOperatorInfoResult Failure(YituliuTokenValidationResult result, string message)
        => new(result, [], message);
}

/// <summary>
/// Third-party yituliu OpenAPI used to read operator progression data. Implementations must be
/// side-effect free: network or response failures never touch local operator-box data.
/// </summary>
public interface IYituliuOpenApiService
{
    Task<YituliuOperatorInfoResult> GetOperatorInfoAsync(
        string token,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// HTTP client for the yituliu OpenAPI. The HttpClient is injectable so tests (and future proxy
/// support) can replace the transport without touching parsing or caller behaviour.
/// </summary>
public sealed class YituliuOpenApiService : IYituliuOpenApiService
{
    public const string OperatorInfoUrl = "https://backend.yituliu.cn/open-api/operator/info";
    private const int CodeSuccess = 200;
    private const int CodeInsufficientPermissions = 20010;
    private const int CodeInvalidCredentials = 20027;
    private static readonly HttpClient DefaultHttpClient = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly HttpClient _httpClient;

    public YituliuOpenApiService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? DefaultHttpClient;
    }

    public async Task<YituliuOperatorInfoResult> GetOperatorInfoAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        var normalizedToken = (token ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedToken))
        {
            return YituliuOperatorInfoResult.Failure(
                YituliuTokenValidationResult.Invalid,
                "Yituliu OpenAPI token is empty.");
        }

        JsonNode? body;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, OperatorInfoUrl);
            // WPF requests send the raw token in Authorization (no scheme prefix).
            request.Headers.TryAddWithoutValidation("Authorization", normalizedToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return YituliuOperatorInfoResult.Failure(
                    YituliuTokenValidationResult.NetworkError,
                    $"Yituliu OpenAPI returned HTTP {(int)response.StatusCode}.");
            }

            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            body = string.IsNullOrWhiteSpace(payload) ? null : JsonNode.Parse(payload);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return YituliuOperatorInfoResult.Failure(
                YituliuTokenValidationResult.NetworkError,
                $"Yituliu OpenAPI request failed: {ex.Message}");
        }

        if (body is not JsonObject bodyObject)
        {
            return YituliuOperatorInfoResult.Failure(
                YituliuTokenValidationResult.Invalid,
                "Yituliu OpenAPI returned an unreadable payload.");
        }

        var code = ReadInt(bodyObject, "code");
        switch (code)
        {
            case CodeSuccess:
                return new YituliuOperatorInfoResult(
                    YituliuTokenValidationResult.Valid,
                    ParseOperators(bodyObject["data"]),
                    ReadString(bodyObject, "msg"));
            case CodeInsufficientPermissions:
                return YituliuOperatorInfoResult.Failure(
                    YituliuTokenValidationResult.WriteOnly,
                    ReadString(bodyObject, "msg"));
            case CodeInvalidCredentials:
                return YituliuOperatorInfoResult.Failure(
                    YituliuTokenValidationResult.Invalid,
                    ReadString(bodyObject, "msg"));
            default:
                return YituliuOperatorInfoResult.Failure(
                    YituliuTokenValidationResult.Invalid,
                    ReadString(bodyObject, "msg"));
        }
    }

    private static IReadOnlyList<YituliuOperatorInfo> ParseOperators(JsonNode? dataNode)
    {
        if (dataNode is not JsonArray data)
        {
            return [];
        }

        var operators = new List<YituliuOperatorInfo>(data.Count);
        foreach (var node in data)
        {
            if (node is not JsonObject oper)
            {
                continue;
            }

            var id = ReadString(oper, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            operators.Add(new YituliuOperatorInfo(
                id,
                ReadInt(oper, "level"),
                ReadInt(oper, "evolvePhase"),
                ReadInt(oper, "mainSkillLevel"),
                ReadInt(oper, "potentialRank"),
                ParseSkills(oper["skills"]),
                ParseEquips(oper["equips"])));
        }

        return operators;
    }

    private static IReadOnlyList<YituliuOperatorSkill> ParseSkills(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return [];
        }

        return array
            .OfType<JsonObject>()
            .Select(skill => new YituliuOperatorSkill(ReadString(skill, "id"), ReadInt(skill, "level")))
            .Where(skill => !string.IsNullOrWhiteSpace(skill.Id))
            .ToArray();
    }

    private static IReadOnlyList<YituliuOperatorEquip> ParseEquips(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return [];
        }

        return array
            .OfType<JsonObject>()
            .Select(equip => new YituliuOperatorEquip(
                ReadString(equip, "id"),
                ReadString(equip, "type"),
                ReadInt(equip, "level")))
            .Where(equip => !string.IsNullOrWhiteSpace(equip.Id))
            .ToArray();
    }

    private static string ReadString(JsonObject node, string property)
    {
        return node[property] is JsonValue value && value.TryGetValue(out string? text)
            ? text ?? string.Empty
            : string.Empty;
    }

    private static int ReadInt(JsonObject node, string property)
    {
        if (node[property] is not JsonValue value)
        {
            return 0;
        }

        if (value.TryGetValue(out int intValue))
        {
            return intValue;
        }

        return value.TryGetValue(out string? text)
               && int.TryParse(text, out intValue)
            ? intValue
            : 0;
    }
}
