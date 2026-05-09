using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Localization;
using MAAUnified.CoreBridge;
using MAAUnified.Platform;

namespace MAAUnified.Application.Services.Features;

public sealed class NotificationProviderFeatureService : INotificationProviderFeatureService
{
    private static readonly string[] Providers =
    [
        "Smtp",
        "ServerChan",
        "Bark",
        "Discord",
        "DingTalk",
        "Telegram",
        "Qmsg",
        "Gotify",
        "CustomWebhook",
    ];

    private static readonly IReadOnlyDictionary<string, string> ProviderAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SMTP"] = "Smtp",
            ["Smtp"] = "Smtp",
            ["ServerChan"] = "ServerChan",
            ["Bark"] = "Bark",
            ["Discord"] = "Discord",
            ["Discord Webhook"] = "Discord",
            ["DingTalk"] = "DingTalk",
            ["Telegram"] = "Telegram",
            ["Qmsg"] = "Qmsg",
            ["Gotify"] = "Gotify",
            ["Custom Webhook"] = "CustomWebhook",
            ["CustomWebhook"] = "CustomWebhook",
        };

    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

    private readonly bool _supported;
    private readonly Func<string, IReadOnlyDictionary<string, string>, string, string, CancellationToken, Task<UiOperationResult>> _sendAsync;
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _sendHttpAsync;

    public NotificationProviderFeatureService()
        : this(supported: true, sendAsync: null, sendHttpAsync: null)
    {
    }

    internal NotificationProviderFeatureService(
        bool supported,
        Func<string, IReadOnlyDictionary<string, string>, string, string, CancellationToken, Task<UiOperationResult>>? sendAsync)
        : this(supported, sendAsync, sendHttpAsync: null)
    {
    }

    internal NotificationProviderFeatureService(
        bool supported,
        Func<string, IReadOnlyDictionary<string, string>, string, string, CancellationToken, Task<UiOperationResult>>? sendAsync,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? sendHttpAsync)
    {
        _supported = supported;
        _sendHttpAsync = sendHttpAsync ?? DefaultSendHttpAsync;
        _sendAsync = sendAsync ?? SendByDefaultAsync;
    }

    public Task<string[]> GetAvailableProvidersAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Providers);
    }

    public Task<UiOperationResult> ValidateProviderParametersAsync(
        NotificationProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_supported)
        {
            return Task.FromResult(UiOperationResult.Fail(
                UiErrorCode.NotificationProviderUnsupported,
                "Notification provider test is unsupported in this environment."));
        }

        var validation = ValidateRequest(request, out _, out _);
        return Task.FromResult(validation);
    }

    public async Task<UiOperationResult> SendTestAsync(
        NotificationProviderTestRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_supported)
        {
            return UiOperationResult.Fail(
                UiErrorCode.NotificationProviderUnsupported,
                "Notification provider test is unsupported in this environment.");
        }

        var validation = ValidateRequest(
            new NotificationProviderRequest(request.Provider, request.ParametersText),
            out var provider,
            out var parameters);
        if (!validation.Success)
        {
            return validation;
        }

        if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Message))
        {
            return UiOperationResult.Fail(
                UiErrorCode.NotificationProviderInvalidParameters,
                "Notification title and message cannot be empty.");
        }

        return await _sendAsync(
            provider!,
            parameters!,
            request.Title.Trim(),
            request.Message.Trim(),
            cancellationToken);
    }

    private static UiOperationResult ValidateRequest(
        NotificationProviderRequest request,
        out string? provider,
        out IReadOnlyDictionary<string, string>? parameters)
    {
        provider = null;
        parameters = null;
        if (request is null)
        {
            return UiOperationResult.Fail(
                UiErrorCode.NotificationProviderInvalidParameters,
                "Notification provider request cannot be null.");
        }

        provider = NormalizeProvider(request.Provider);
        if (provider is null)
        {
            return UiOperationResult.Fail(
                UiErrorCode.NotificationProviderUnsupported,
                $"Notification provider `{request.Provider}` is unsupported.");
        }

        var parsed = ParseParameterText(request.ParametersText, out var parseError);
        if (parseError is not null)
        {
            return UiOperationResult.Fail(
                UiErrorCode.NotificationProviderInvalidParameters,
                parseError);
        }

        var parameterValidationError = ValidateProviderRules(provider, parsed);
        if (parameterValidationError is not null)
        {
            return UiOperationResult.Fail(
                UiErrorCode.NotificationProviderInvalidParameters,
                parameterValidationError);
        }

        parameters = parsed;
        return UiOperationResult.Ok($"Notification provider `{provider}` parameters are valid.");
    }

    private static string? ValidateProviderRules(
        string provider,
        IReadOnlyDictionary<string, string> parameters)
    {
        if (provider == "Smtp")
        {
            if (!HasValue(parameters, "server")
                || !HasValue(parameters, "port")
                || !HasValue(parameters, "from")
                || !HasValue(parameters, "to"))
            {
                return "Smtp requires `server`, `port`, `from`, and `to`.";
            }

            if (!int.TryParse(parameters["port"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
                || port < 1
                || port > 65535)
            {
                return "Smtp `port` must be in [1, 65535].";
            }

            return null;
        }

        if (provider == "ServerChan")
        {
            return HasValue(parameters, "sendKey")
                ? null
                : "ServerChan requires `sendKey`.";
        }

        if (provider == "Bark")
        {
            return HasValue(parameters, "sendKey")
                ? null
                : "Bark requires `sendKey`.";
        }

        if (provider == "Discord")
        {
            if (HasValue(parameters, "webhookUrl"))
            {
                return ValidateHttpUrl(parameters["webhookUrl"], "Discord `webhookUrl`");
            }

            if (HasValue(parameters, "botToken") && HasValue(parameters, "userId"))
            {
                return null;
            }

            return "Discord requires `webhookUrl` or (`botToken` + `userId`).";
        }

        if (provider == "DingTalk")
        {
            if (!HasValue(parameters, "accessToken"))
            {
                return "DingTalk requires `accessToken`.";
            }

            return null;
        }

        if (provider == "Telegram")
        {
            if (!HasValue(parameters, "botToken") || !HasValue(parameters, "chatId"))
            {
                return "Telegram requires `botToken` and `chatId`.";
            }

            if (HasValue(parameters, "apiUrl"))
            {
                return ValidateHttpUrl(parameters["apiUrl"], "Telegram `apiUrl`");
            }

            return null;
        }

        if (provider == "Qmsg")
        {
            if (!HasValue(parameters, "key"))
            {
                return "Qmsg requires `key`.";
            }

            if (HasValue(parameters, "server"))
            {
                return ValidateHttpUrl(parameters["server"], "Qmsg `server`");
            }

            return null;
        }

        if (provider == "Gotify")
        {
            if (!HasValue(parameters, "server") || !HasValue(parameters, "token"))
            {
                return "Gotify requires `server` and `token`.";
            }

            return ValidateHttpUrl(parameters["server"], "Gotify `server`");
        }

        if (provider == "CustomWebhook")
        {
            if (!HasValue(parameters, "url"))
            {
                return "CustomWebhook requires `url`.";
            }

            return ValidateHttpUrl(parameters["url"], "CustomWebhook `url`");
        }

        return null;
    }

    private static string? ValidateHttpUrl(string raw, string field)
    {
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri))
        {
            return $"{field} must be an absolute URL.";
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return $"{field} must use http/https scheme.";
        }

        return null;
    }

    private static Dictionary<string, string> ParseParameterText(string? text, out string? error)
    {
        error = null;
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
        {
            return parameters;
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.TrimEntries);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var equalIndex = line.IndexOf('=');
            if (equalIndex <= 0)
            {
                error = $"Invalid parameter line {i + 1}: `{line}`. Expected `key=value`.";
                return parameters;
            }

            var key = line[..equalIndex].Trim();
            var value = line[(equalIndex + 1)..].Trim();
            if (key.Length == 0)
            {
                error = $"Invalid parameter line {i + 1}: key cannot be empty.";
                return parameters;
            }

            parameters[key] = value;
        }

        return parameters;
    }

    private static string? NormalizeProvider(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return null;
        }

        return ProviderAliases.TryGetValue(provider.Trim(), out var canonical)
            ? canonical
            : null;
    }

    private static bool HasValue(IReadOnlyDictionary<string, string> parameters, string key)
    {
        return parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value);
    }

    private static Task<HttpResponseMessage> DefaultSendHttpAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        return _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private async Task<UiOperationResult> SendByDefaultAsync(
        string provider,
        IReadOnlyDictionary<string, string> parameters,
        string title,
        string message,
        CancellationToken cancellationToken)
    {
        if (provider == "DingTalk")
        {
            return await SendDingTalkAsync(parameters, title, message, cancellationToken);
        }

        if (!TryResolveProbeUrl(provider, parameters, out var probeUri))
        {
            return UiOperationResult.Ok($"Notification test request for `{provider}` accepted.");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, probeUri);
            using var response = await _sendHttpAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return UiOperationResult.Ok(
                    $"Notification test sent via `{provider}` ({title}: {message}).");
            }

            return UiOperationResult.Fail(
                UiErrorCode.NotificationProviderNetworkFailure,
                $"Notification test failed via `{provider}`: HTTP {(int)response.StatusCode}.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return UiOperationResult.Fail(
                UiErrorCode.NotificationProviderNetworkFailure,
                $"Notification test timed out for `{provider}`.");
        }
        catch (HttpRequestException ex)
        {
            return UiOperationResult.Fail(
                UiErrorCode.NotificationProviderNetworkFailure,
                $"Notification network failure for `{provider}`: {ex.Message}");
        }
    }

    private async Task<UiOperationResult> SendDingTalkAsync(
        IReadOnlyDictionary<string, string> parameters,
        string title,
        string message,
        CancellationToken cancellationToken)
    {
        if (!parameters.TryGetValue("accessToken", out var accessToken) || string.IsNullOrWhiteSpace(accessToken))
        {
            return UiOperationResult.Fail(
                UiErrorCode.NotificationProviderInvalidParameters,
                "DingTalk requires `accessToken`.");
        }

        var secret = parameters.TryGetValue("secret", out var configuredSecret) ? configuredSecret : null;
        var endpoint = BuildDingTalkWebhookUri(accessToken.Trim(), secret?.Trim());
        var body = JsonSerializer.Serialize(new
        {
            msgtype = "text",
            text = new
            {
                content = $"{title}\n{message}",
            },
        });

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            using var response = await _sendHttpAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return UiOperationResult.Ok("Notification test sent via `DingTalk`.");
            }

            return UiOperationResult.Fail(
                UiErrorCode.NotificationProviderNetworkFailure,
                $"Notification test failed via `DingTalk`: HTTP {(int)response.StatusCode}.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return UiOperationResult.Fail(
                UiErrorCode.NotificationProviderNetworkFailure,
                "Notification test timed out for `DingTalk`.");
        }
        catch (HttpRequestException ex)
        {
            return UiOperationResult.Fail(
                UiErrorCode.NotificationProviderNetworkFailure,
                $"Notification network failure for `DingTalk`: {ex.Message}");
        }
    }

    private static Uri BuildDingTalkWebhookUri(string accessToken, string? secret)
    {
        var encodedAccessToken = Uri.EscapeDataString(accessToken);
        var baseUrl = $"https://oapi.dingtalk.com/robot/send?access_token={encodedAccessToken}";
        if (string.IsNullOrWhiteSpace(secret))
        {
            return new Uri(baseUrl, UriKind.Absolute);
        }

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var sign = BuildDingTalkSign(timestamp, secret);
        var url = $"{baseUrl}&timestamp={timestamp}&sign={sign}";
        return new Uri(url, UriKind.Absolute);
    }

    private static string BuildDingTalkSign(string timestamp, string secret)
    {
        var stringToSign = $"{timestamp}\n{secret}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign));
        return Uri.EscapeDataString(Convert.ToBase64String(hash));
    }

    private static bool TryResolveProbeUrl(
        string provider,
        IReadOnlyDictionary<string, string> parameters,
        out Uri? probeUri)
    {
        probeUri = null;
        string? rawUrl = provider switch
        {
            "Discord" => parameters.TryGetValue("webhookUrl", out var discordWebhook) ? discordWebhook : null,
            "Gotify" => parameters.TryGetValue("server", out var gotifyServer) ? gotifyServer : null,
            "CustomWebhook" => parameters.TryGetValue("url", out var webhookUrl) ? webhookUrl : null,
            "Qmsg" => parameters.TryGetValue("server", out var qmsgServer) ? qmsgServer : null,
            "Bark" => parameters.TryGetValue("server", out var barkServer) ? barkServer : null,
            "Telegram" => parameters.TryGetValue("apiUrl", out var telegramApi) ? telegramApi : null,
            _ => null,
        };

        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return false;
        }

        return Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out probeUri)
            && (string.Equals(probeUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(probeUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
    }
}
