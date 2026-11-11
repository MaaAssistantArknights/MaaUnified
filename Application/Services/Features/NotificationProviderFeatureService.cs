using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Security;
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
        "DiscordWebhook",
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
            ["Discord Webhook"] = "DiscordWebhook",
            ["DiscordWebhook"] = "DiscordWebhook",
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
    private readonly UnifiedConfigurationService? _configService;
    private readonly Func<string, IReadOnlyDictionary<string, string>, string, string, CancellationToken, Task<UiOperationResult>> _sendAsync;
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _sendHttpAsync;

    public NotificationProviderFeatureService()
        : this(configService: null, supported: true, sendAsync: null, sendHttpAsync: null)
    {
    }

    public NotificationProviderFeatureService(UnifiedConfigurationService configService)
        : this(configService, supported: true, sendAsync: null, sendHttpAsync: null)
    {
    }

    internal NotificationProviderFeatureService(
        bool supported,
        Func<string, IReadOnlyDictionary<string, string>, string, string, CancellationToken, Task<UiOperationResult>>? sendAsync)
        : this(configService: null, supported, sendAsync, sendHttpAsync: null)
    {
    }

    internal NotificationProviderFeatureService(
        bool supported,
        Func<string, IReadOnlyDictionary<string, string>, string, string, CancellationToken, Task<UiOperationResult>>? sendAsync,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? sendHttpAsync)
        : this(configService: null, supported, sendAsync, sendHttpAsync)
    {
    }

    internal NotificationProviderFeatureService(
        UnifiedConfigurationService? configService,
        bool supported,
        Func<string, IReadOnlyDictionary<string, string>, string, string, CancellationToken, Task<UiOperationResult>>? sendAsync,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? sendHttpAsync)
    {
        _configService = configService;
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
        if (!CanUseSensitiveSettings)
        {
            return Task.FromResult(SensitiveStorageUnavailable());
        }

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
        if (!CanUseSensitiveSettings)
        {
            return SensitiveStorageUnavailable();
        }

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

        try
        {
            return await _sendAsync(
                provider!,
                parameters!,
                PrefixTitle(request.Title),
                request.Message.Trim(),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // WPF frozen providers catch construction/argument failures (e.g. a malformed
            // ServerChan `sctp` key rejected by ConstructUrl) and report a failed send
            // instead of throwing; keep the send path total so a single bad provider
            // cannot escape as an unhandled exception or abort a multi-provider test loop.
            return UiOperationResult.Fail(
                UiErrorCode.NotificationProviderFailed,
                $"Notification test failed via `{provider}`.",
                $"errorType={ex.GetType().Name}");
        }
    }

    private bool CanUseSensitiveSettings => _configService?.SensitiveStorageState
        is not (SensitiveStorageState.Unavailable or SensitiveStorageState.Corrupt);

    private static UiOperationResult SensitiveStorageUnavailable()
        => UiOperationResult.Fail(
            UiErrorCode.NotificationProviderUnsupported,
            "External notifications are disabled while sensitive storage is unavailable.");

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
            if (!HasValue(parameters, "sendKey"))
            {
                return "Bark requires `sendKey`.";
            }

            return !HasValue(parameters, "server")
                ? null
                : ValidateHttpUrl(parameters["server"], "Bark `server`");
        }

        if (provider == "Discord")
        {
            if (HasValue(parameters, "botToken") && HasValue(parameters, "userId"))
            {
                return null;
            }

            return "Discord requires `botToken` and `userId`.";
        }

        if (provider == "DiscordWebhook")
        {
            return HasValue(parameters, "webhookUrl")
                ? ValidateHttpUrl(parameters["webhookUrl"], "DiscordWebhook `webhookUrl`")
                : "DiscordWebhook requires `webhookUrl`.";
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
            if (!HasValue(parameters, "key") || !HasValue(parameters, "server"))
            {
                return "Qmsg requires `key` and `server`.";
            }

            return ValidateHttpUrl(parameters["server"], "Qmsg `server`");
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
            if (!HasValue(parameters, "url") || !HasValue(parameters, "body"))
            {
                return "CustomWebhook requires `url` and `body`.";
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

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
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
                error = $"Invalid parameter line {i + 1}; expected key=value";
                return parameters;
            }

            var key = line[..equalIndex].Trim();
            var value = line[(equalIndex + 1)..].Trim();
            if (key.Length == 0)
            {
                error = $"Invalid parameter line {i + 1}; expected key=value";
                return parameters;
            }

            if (string.Equals(key, "body", StringComparison.OrdinalIgnoreCase))
            {
                // WPF stores Custom Webhook bodies verbatim, including multiline JSON.
                parameters[key] = string.Join("\n", lines.Skip(i))[(lines[i].IndexOf('=') + 1)..];
                break;
            }

            if (string.Equals(key, "headers", StringComparison.OrdinalIgnoreCase))
            {
                // Custom Webhook headers are `Name: value` lines and may span several
                // lines; continuation lines never look like `key=value` assignments.
                var headers = new StringBuilder(value);
                while (TryReadHeaderContinuationLine(lines, ref i, out var continuation))
                {
                    headers.Append('\n').Append(continuation);
                }

                parameters[key] = headers.ToString();
                continue;
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
        return provider switch
        {
            "Smtp" => await SendSmtpAsync(parameters, title, message, cancellationToken),
            "ServerChan" => await SendServerChanAsync(parameters, title, message, cancellationToken),
            "Bark" => await SendBarkAsync(parameters, title, message, cancellationToken),
            "Discord" => await SendDiscordAsync(parameters, title, message, cancellationToken),
            "DiscordWebhook" => await SendDiscordWebhookProviderAsync(parameters, message, cancellationToken),
            "DingTalk" => await SendDingTalkAsync(parameters, title, message, cancellationToken),
            "Telegram" => await SendTelegramAsync(parameters, title, message, cancellationToken),
            "Qmsg" => await SendQmsgAsync(parameters, title, message, cancellationToken),
            "Gotify" => await SendGotifyAsync(parameters, title, message, cancellationToken),
            "CustomWebhook" => await SendCustomWebhookAsync(parameters, title, message, cancellationToken),
            _ => UiOperationResult.Fail(
                UiErrorCode.NotificationProviderUnsupported,
                $"Notification provider `{provider}` is unsupported."),
        };
    }

    private async Task<UiOperationResult> SendSmtpAsync(
        IReadOnlyDictionary<string, string> parameters,
        string title,
        string message,
        CancellationToken cancellationToken)
    {
        if (!parameters.TryGetValue("server", out var server) || string.IsNullOrWhiteSpace(server)
            || !parameters.TryGetValue("port", out var portText) || !int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
            || !parameters.TryGetValue("from", out var from) || string.IsNullOrWhiteSpace(from)
            || !parameters.TryGetValue("to", out var to) || string.IsNullOrWhiteSpace(to))
        {
            return UiOperationResult.Fail(
                UiErrorCode.NotificationProviderInvalidParameters,
                "Smtp requires `server`, `port`, `from`, and `to`.");
        }

        try
        {
            using var client = new SmtpClient(server.Trim(), port)
            {
                EnableSsl = TryGetBool(parameters, "useSsl", false),
                UseDefaultCredentials = false,
            };
            if (TryGetBool(parameters, "requiresAuthentication", false))
            {
                client.Credentials = new NetworkCredential(
                    parameters.TryGetValue("user", out var user) ? user : string.Empty,
                    parameters.TryGetValue("password", out var password) ? password : string.Empty);
            }

            // WPF parity: strip CR/LF from the title before it becomes the mail subject
            // (System.Net.Mail rejects subjects containing newlines).
            var subject = title.Replace("\r", string.Empty).Replace("\n", string.Empty).Trim();
            using var mail = new MailMessage(
                from.Trim(),
                to.Trim(),
                subject,
                message.Trim());
            await client.SendMailAsync(mail, cancellationToken);
            return UiOperationResult.Ok("Notification test sent via `Smtp`.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return UiOperationResult.Fail(
                UiErrorCode.NotificationProviderNetworkFailure,
                "Notification test timed out for `Smtp`.");
        }
        catch (Exception ex)
        {
            return NetworkFailure("Smtp", ex);
        }
    }

    private async Task<UiOperationResult> SendServerChanAsync(
        IReadOnlyDictionary<string, string> parameters,
        string title,
        string message,
        CancellationToken cancellationToken)
    {
        if (!parameters.TryGetValue("sendKey", out var sendKey) || string.IsNullOrWhiteSpace(sendKey))
        {
            return UiOperationResult.Fail(
                UiErrorCode.NotificationProviderInvalidParameters,
                "ServerChan requires `sendKey`.");
        }

        var postData = $"text={Uri.EscapeDataString(NormalizeServerChanTitle(title))}&desp={Uri.EscapeDataString(message.Trim())}";
        return await SendFormPostAsync(BuildServerChanUrl(sendKey.Trim()), postData, "ServerChan", cancellationToken, static (_, response) =>
        {
            using var document = JsonDocument.Parse(response);
            return document.RootElement.TryGetProperty("code", out var code)
                   && code.TryGetInt32(out var value)
                   && value == 0;
        });
    }

    private async Task<UiOperationResult> SendBarkAsync(
        IReadOnlyDictionary<string, string> parameters,
        string title,
        string message,
        CancellationToken cancellationToken)
    {
        if (!parameters.TryGetValue("sendKey", out var sendKey) || string.IsNullOrWhiteSpace(sendKey))
        {
            return UiOperationResult.Fail(
                UiErrorCode.NotificationProviderInvalidParameters,
                "Bark requires `sendKey`.");
        }

        var server = parameters.TryGetValue("server", out var configuredServer) && !string.IsNullOrWhiteSpace(configuredServer)
            ? configuredServer
            : "https://api.day.app";

        var pushUri = new Uri(new Uri(NormalizeHttpBaseUrl(server), UriKind.Absolute), "/push");
        var body = JsonSerializer.Serialize(new
        {
            device_key = sendKey.Trim(),
            title = title.Trim(),
            body = message.Trim(),
            group = "MaaAssistantArknights",
            icon = "https://cdn.jsdelivr.net/gh/MaaAssistantArknights/design@main/logo/maa-logo_256x256.png",
        });
        return await SendJsonPostAsync(pushUri, body, "Bark", cancellationToken, static (_, response) =>
        {
            using var document = JsonDocument.Parse(response);
            return document.RootElement.TryGetProperty("code", out var code)
                   && code.TryGetInt32(out var value)
                   && value == 200;
        });
    }

    private async Task<UiOperationResult> SendDiscordAsync(
        IReadOnlyDictionary<string, string> parameters,
        string title,
        string message,
        CancellationToken cancellationToken)
    {
        if (parameters.TryGetValue("botToken", out var botToken) && !string.IsNullOrWhiteSpace(botToken)
            && parameters.TryGetValue("userId", out var userId) && !string.IsNullOrWhiteSpace(userId))
        {
            return await SendDiscordDmAsync(botToken.Trim(), userId.Trim(), message, cancellationToken);
        }

        return UiOperationResult.Fail(
            UiErrorCode.NotificationProviderInvalidParameters,
            "Discord requires `botToken` and `userId`.");
    }

    private async Task<UiOperationResult> SendTelegramAsync(
        IReadOnlyDictionary<string, string> parameters,
        string title,
        string message,
        CancellationToken cancellationToken)
    {
        if (!parameters.TryGetValue("botToken", out var botToken) || string.IsNullOrWhiteSpace(botToken)
            || !parameters.TryGetValue("chatId", out var chatId) || string.IsNullOrWhiteSpace(chatId))
        {
            return UiOperationResult.Fail(
                UiErrorCode.NotificationProviderInvalidParameters,
                "Telegram requires `botToken` and `chatId`.");
        }

        var apiBase = parameters.TryGetValue("apiUrl", out var apiUrl) && !string.IsNullOrWhiteSpace(apiUrl)
            ? NormalizeHttpBaseUrl(apiUrl)
            : "https://api.telegram.org/";
        var messageUri = new Uri(new Uri(apiBase, UriKind.Absolute), $"bot{botToken.Trim()}/sendMessage");
        var payload = new Dictionary<string, object?>
        {
            ["chat_id"] = chatId.Trim(),
            ["text"] = $"{title.Trim()}: {message.Trim()}",
        };
        if (parameters.TryGetValue("topicId", out var topicId) && !string.IsNullOrWhiteSpace(topicId))
        {
            payload["message_thread_id"] = topicId.Trim();
        }

        return await SendJsonPostAsync(messageUri, JsonSerializer.Serialize(payload), "Telegram", cancellationToken, static (_, response) =>
        {
            using var document = JsonDocument.Parse(response);
            return document.RootElement.TryGetProperty("ok", out var ok)
                   && ok.ValueKind == JsonValueKind.True;
        });
    }

    private async Task<UiOperationResult> SendQmsgAsync(
        IReadOnlyDictionary<string, string> parameters,
        string title,
        string message,
        CancellationToken cancellationToken)
    {
        if (!parameters.TryGetValue("key", out var key) || string.IsNullOrWhiteSpace(key))
        {
            return UiOperationResult.Fail(
                UiErrorCode.NotificationProviderInvalidParameters,
                "Qmsg requires `key`.");
        }

        if (!parameters.TryGetValue("server", out var server) || string.IsNullOrWhiteSpace(server))
        {
            return UiOperationResult.Fail(
                UiErrorCode.NotificationProviderInvalidParameters,
                "Qmsg requires `server`.");
        }

        var uri = new Uri(new Uri(NormalizeHttpBaseUrl(server), UriKind.Absolute), $"jsend/{key.Trim()}");
        var payload = new Dictionary<string, string>
        {
            ["msg"] = message.Trim(),
        };
        if (parameters.TryGetValue("user", out var user) && !string.IsNullOrWhiteSpace(user))
        {
            payload["qq"] = user.Trim();
        }

        if (parameters.TryGetValue("bot", out var bot) && !string.IsNullOrWhiteSpace(bot))
        {
            payload["bot"] = bot.Trim();
        }

        return await SendJsonPostAsync(uri, JsonSerializer.Serialize(payload), "Qmsg", cancellationToken, static (_, response) =>
        {
            using var document = JsonDocument.Parse(response);
            return document.RootElement.TryGetProperty("success", out var success)
                   && success.ValueKind == JsonValueKind.True;
        });
    }

    private async Task<UiOperationResult> SendGotifyAsync(
        IReadOnlyDictionary<string, string> parameters,
        string title,
        string message,
        CancellationToken cancellationToken)
    {
        if (!parameters.TryGetValue("server", out var server) || string.IsNullOrWhiteSpace(server)
            || !parameters.TryGetValue("token", out var token) || string.IsNullOrWhiteSpace(token))
        {
            return UiOperationResult.Fail(
                UiErrorCode.NotificationProviderInvalidParameters,
                "Gotify requires `server` and `token`.");
        }

        var messageUri = new Uri(new Uri(NormalizeHttpBaseUrl(server), UriKind.Absolute), "/message");
        var request = new HttpRequestMessage(HttpMethod.Post, messageUri);
        request.Headers.Add("X-Gotify-Key", token.Trim());
        request.Content = JsonContent.Create(new { title = title.Trim(), message = StripExternalLogColorMarkers(message) });
        return await SendRequestAsync(request, "Gotify", cancellationToken, static (_, responseText) =>
        {
            using var document = JsonDocument.Parse(responseText);
            return document.RootElement.TryGetProperty("id", out var id)
                   && id.TryGetInt32(out var _);
        });
    }

    private async Task<UiOperationResult> SendCustomWebhookAsync(
        IReadOnlyDictionary<string, string> parameters,
        string title,
        string message,
        CancellationToken cancellationToken)
    {
        if (!parameters.TryGetValue("url", out var url) || string.IsNullOrWhiteSpace(url))
        {
            return UiOperationResult.Fail(
                UiErrorCode.NotificationProviderInvalidParameters,
                "CustomWebhook requires `url`.");
        }

        var body = parameters.TryGetValue("body", out var bodyTemplate) ? bodyTemplate : string.Empty;
        if (string.IsNullOrWhiteSpace(body))
        {
            return UiOperationResult.Fail(
                UiErrorCode.NotificationProviderInvalidParameters,
                "CustomWebhook requires `body`.");
        }

        var rendered = body
            .Replace("{title}", title.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal)
            .Replace("{content}", message.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal), StringComparison.Ordinal)
            .Replace("{time}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), StringComparison.Ordinal);

        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(url.Trim(), UriKind.Absolute))
        {
            Content = new StringContent(rendered, Encoding.UTF8, "application/json"),
        };
        if (parameters.TryGetValue("headers", out var headers) && !string.IsNullOrWhiteSpace(headers))
        {
            foreach (var header in ParseHeaderLines(headers))
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return await SendRequestAsync(request, "CustomWebhook", cancellationToken);
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
                content = $"{title}: {message}",
            },
        });

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            using var response = await _sendHttpAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return UiOperationResult.Fail(
                    UiErrorCode.NotificationProviderNetworkFailure,
                    $"Notification test failed via `DingTalk`: HTTP {(int)response.StatusCode}.");
            }

            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(responseText);
            if (document.RootElement.TryGetProperty("errcode", out var errorCode)
                && errorCode.TryGetInt32(out var value)
                && value == 0)
            {
                return UiOperationResult.Ok("Notification test sent via `DingTalk`.");
            }

            return UiOperationResult.Fail(
                UiErrorCode.NotificationProviderNetworkFailure,
                "Notification test failed via `DingTalk`: provider response was not successful.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return UiOperationResult.Fail(
                UiErrorCode.NotificationProviderNetworkFailure,
                "Notification test timed out for `DingTalk`.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            // WPF treats unparseable DingTalk responses as send failures.
            return NetworkFailure("DingTalk", ex);
        }
    }

    private async Task<UiOperationResult> SendDiscordWebhookAsync(
        string webhookUrl,
        string message,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(webhookUrl, UriKind.Absolute))
        {
            Content = JsonContent.Create(new { content = message.Trim() }),
        };
        // WPF frozen 23ddd271 (DiscordWebhookNotificationProvider) treats an empty response
        // body as success without checking the exact 2xx code: official webhooks return
        // 204 No Content, but Discord-compatible endpoints/relays may answer 200 with an
        // empty body. SendRequestAsync already rejects non-2xx statuses before this
        // predicate runs, so WPF's acceptance of empty-bodied 4xx/5xx responses is
        // intentionally not replicated.
        return await SendRequestAsync(request, "DiscordWebhook", cancellationToken, static (_, body) => string.IsNullOrEmpty(body));
    }

    private async Task<UiOperationResult> SendDiscordWebhookProviderAsync(
        IReadOnlyDictionary<string, string> parameters,
        string message,
        CancellationToken cancellationToken)
    {
        if (!parameters.TryGetValue("webhookUrl", out var webhookUrl) || string.IsNullOrWhiteSpace(webhookUrl))
        {
            return UiOperationResult.Fail(
                UiErrorCode.NotificationProviderInvalidParameters,
                "DiscordWebhook requires `webhookUrl`.");
        }

        return await SendDiscordWebhookAsync(webhookUrl.Trim(), message, cancellationToken);
    }

    private async Task<UiOperationResult> SendDiscordDmAsync(
        string botToken,
        string userId,
        string message,
        CancellationToken cancellationToken)
    {
        try
        {
            var channelRequest = new HttpRequestMessage(HttpMethod.Post, new Uri("https://discord.com/api/v9/users/@me/channels"))
            {
                Content = JsonContent.Create(new { recipient_id = userId }),
            };
            AddDiscordHeaders(channelRequest, botToken);
            using var channelResponse = await _sendHttpAsync(channelRequest, cancellationToken);
            if (!channelResponse.IsSuccessStatusCode)
            {
                return UiOperationResult.Fail(
                    UiErrorCode.NotificationProviderNetworkFailure,
                    $"Discord channel creation failed: HTTP {(int)channelResponse.StatusCode}.");
            }

            var channelJson = await channelResponse.Content.ReadAsStringAsync(cancellationToken);
            using var channelDoc = JsonDocument.Parse(channelJson);
            if (!channelDoc.RootElement.TryGetProperty("id", out var channelIdElement)
                || string.IsNullOrWhiteSpace(channelIdElement.GetString()))
            {
                return UiOperationResult.Fail(
                    UiErrorCode.NotificationProviderNetworkFailure,
                    "Discord channel creation returned no channel id.");
            }

            var channelId = channelIdElement.GetString()!;
            var messageRequest = new HttpRequestMessage(HttpMethod.Post, new Uri($"https://discord.com/api/v9/channels/{channelId}/messages", UriKind.Absolute))
            {
                Content = JsonContent.Create(new { content = message.Trim() }),
            };
            AddDiscordHeaders(messageRequest, botToken);
            return await SendRequestAsync(messageRequest, "Discord", cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return UiOperationResult.Fail(
                UiErrorCode.NotificationProviderNetworkFailure,
                "Notification test timed out for `Discord`.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return NetworkFailure("Discord", ex);
        }
    }

    private async Task<UiOperationResult> SendJsonPostAsync(
        Uri uri,
        string json,
        string provider,
        CancellationToken cancellationToken,
        Func<HttpResponseMessage, string, bool> isProviderSuccess)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        return await SendRequestAsync(request, provider, cancellationToken, isProviderSuccess);
    }

    private async Task<UiOperationResult> SendFormPostAsync(
        string url,
        string postData,
        string provider,
        CancellationToken cancellationToken,
        Func<HttpResponseMessage, string, bool> isProviderSuccess)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(url, UriKind.Absolute))
        {
            Content = new StringContent(postData, Encoding.UTF8, "application/x-www-form-urlencoded"),
        };
        return await SendRequestAsync(request, provider, cancellationToken, isProviderSuccess);
    }

    private async Task<UiOperationResult> SendRequestAsync(
        HttpRequestMessage request,
        string provider,
        CancellationToken cancellationToken,
        Func<HttpResponseMessage, string, bool>? isProviderSuccess = null)
    {
        try
        {
            using var response = await _sendHttpAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return UiOperationResult.Fail(
                    UiErrorCode.NotificationProviderNetworkFailure,
                    $"Notification test failed via `{provider}`: HTTP {(int)response.StatusCode}.");
            }

            if (isProviderSuccess is not null)
            {
                var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!isProviderSuccess(response, responseText))
                {
                    return UiOperationResult.Fail(
                        UiErrorCode.NotificationProviderNetworkFailure,
                        $"Notification test failed via `{provider}`: provider response was not successful.");
                }
            }

            return UiOperationResult.Ok($"Notification test sent via `{provider}`.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return UiOperationResult.Fail(
                UiErrorCode.NotificationProviderNetworkFailure,
                $"Notification test timed out for `{provider}`.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return NetworkFailure(provider, ex);
        }
    }

    private static UiOperationResult NetworkFailure(string provider, Exception exception)
        => UiOperationResult.Fail(
            UiErrorCode.NotificationProviderNetworkFailure,
            $"Notification network failure for `{provider}`.",
            $"errorType={exception.GetType().Name}");

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

    private static void AddDiscordHeaders(HttpRequestMessage request, string botToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", botToken);
        request.Headers.UserAgent.ParseAdd("DiscordBot");
    }

    private static string PrefixTitle(string title)
    {
        var trimmed = title.Trim();
        return trimmed.StartsWith("[MAA] ", StringComparison.Ordinal)
            ? trimmed
            : $"[MAA] {trimmed}";
    }

    private static string StripExternalLogColorMarkers(string content)
    {
        var matches = Regex.Matches(content, @"\[(.*?)\]\[(.*?)\]([\s\S]*?)(?=\n\[|$)");
        if (matches.Count == 0)
        {
            return content.Trim();
        }

        var builder = new StringBuilder();
        foreach (Match match in matches)
        {
            builder.Append('[')
                .Append(match.Groups[1].Value)
                .Append(']')
                .Append(match.Groups[3].Value.TrimEnd('\n', '\r'))
                .Append('\n');
        }

        return builder.ToString().TrimEnd('\n', '\r');
    }

    private static string BuildServerChanUrl(string sendKey)
    {
        if (!sendKey.StartsWith("sctp", StringComparison.OrdinalIgnoreCase))
        {
            return $"https://sctapi.ftqq.com/{sendKey}.send";
        }

        var match = Regex.Match(sendKey, @"^sctp(\d+)t", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            throw new ArgumentException("Invalid key format for sctp.", nameof(sendKey));
        }

        var num = match.Groups[1].Value;
        return $"https://{num}.push.ft07.com/send/{sendKey}.send";
    }

    private static string NormalizeServerChanTitle(string title)
    {
        var normalized = title.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);
        return normalized.Length > 32 ? normalized[..32] : normalized;
    }

    private static string NormalizeHttpBaseUrl(string rawUrl)
        => rawUrl.Trim().TrimEnd('/') + "/";

    private static bool TryReadHeaderContinuationLine(string[] lines, ref int index, out string continuation)
    {
        continuation = string.Empty;
        var lookahead = index + 1;
        while (lookahead < lines.Length && lines[lookahead].Trim().Length == 0)
        {
            lookahead++;
        }

        if (lookahead >= lines.Length)
        {
            return false;
        }

        var candidate = lines[lookahead].Trim();
        var equalIndex = candidate.IndexOf('=');
        var colonIndex = candidate.IndexOf(':');
        var looksLikeAssignment = equalIndex > 0 && (colonIndex < 0 || equalIndex < colonIndex);
        if (looksLikeAssignment)
        {
            return false;
        }

        continuation = candidate;
        index = lookahead;
        return true;
    }

    private static bool TryGetBool(IReadOnlyDictionary<string, string> parameters, string key, bool fallback)
    {
        if (!parameters.TryGetValue(key, out var text) || string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        return bool.TryParse(text, out var parsed) ? parsed : fallback;
    }

    private static IEnumerable<KeyValuePair<string, string>> ParseHeaderLines(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var index = line.IndexOf(':');
            if (index <= 0)
            {
                continue;
            }

            yield return new KeyValuePair<string, string>(line[..index].Trim(), line[(index + 1)..].Trim());
        }
    }
}
