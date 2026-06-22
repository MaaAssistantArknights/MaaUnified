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
            if (!HasValue(parameters, "sendKey") || !HasValue(parameters, "server"))
            {
                return "Bark requires `sendKey` and `server`.";
            }

            return ValidateHttpUrl(parameters["server"], "Bark `server`");
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
        return provider switch
        {
            "Smtp" => await SendSmtpAsync(parameters, title, message, cancellationToken),
            "ServerChan" => await SendServerChanAsync(parameters, title, message, cancellationToken),
            "Bark" => await SendBarkAsync(parameters, title, message, cancellationToken),
            "Discord" => await SendDiscordAsync(parameters, title, message, cancellationToken),
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

            using var mail = new MailMessage(
                from.Trim(),
                to.Trim(),
                title.Trim(),
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
            return UiOperationResult.Fail(
                UiErrorCode.NotificationProviderNetworkFailure,
                $"Notification network failure for `Smtp`: {ex.Message}");
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
        return await SendFormPostAsync(BuildServerChanUrl(sendKey.Trim()), postData, "ServerChan", cancellationToken);
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

        if (!parameters.TryGetValue("server", out var server) || string.IsNullOrWhiteSpace(server))
        {
            return UiOperationResult.Fail(
                UiErrorCode.NotificationProviderInvalidParameters,
                "Bark requires `server`.");
        }

        var pushUri = new Uri(new Uri(NormalizeHttpBaseUrl(server), UriKind.Absolute), "push");
        var body = JsonSerializer.Serialize(new
        {
            device_key = sendKey.Trim(),
            title = title.Trim(),
            body = message.Trim(),
            group = "MAAUnified",
            icon = "https://cdn.jsdelivr.net/gh/MaaAssistantArknights/design@main/v2/icons/maa-logo_256x256.png",
        });
        return await SendJsonPostAsync(pushUri, body, "Bark", cancellationToken);
    }

    private async Task<UiOperationResult> SendDiscordAsync(
        IReadOnlyDictionary<string, string> parameters,
        string title,
        string message,
        CancellationToken cancellationToken)
    {
        if (parameters.TryGetValue("webhookUrl", out var webhookUrl) && !string.IsNullOrWhiteSpace(webhookUrl))
        {
            return await SendDiscordWebhookAsync(webhookUrl.Trim(), message, cancellationToken);
        }

        if (parameters.TryGetValue("botToken", out var botToken) && !string.IsNullOrWhiteSpace(botToken)
            && parameters.TryGetValue("userId", out var userId) && !string.IsNullOrWhiteSpace(userId))
        {
            return await SendDiscordDmAsync(botToken.Trim(), userId.Trim(), message, cancellationToken);
        }

        return UiOperationResult.Fail(
            UiErrorCode.NotificationProviderInvalidParameters,
            "Discord requires `webhookUrl` or (`botToken` + `userId`).");
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

        return await SendJsonPostAsync(messageUri, JsonSerializer.Serialize(payload), "Telegram", cancellationToken);
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
            ["msg"] = $"{title.Trim()}: {message.Trim()}",
        };
        if (parameters.TryGetValue("user", out var user) && !string.IsNullOrWhiteSpace(user))
        {
            payload["qq"] = user.Trim();
        }

        if (parameters.TryGetValue("bot", out var bot) && !string.IsNullOrWhiteSpace(bot))
        {
            payload["bot"] = bot.Trim();
        }

        return await SendJsonPostAsync(uri, JsonSerializer.Serialize(payload), "Qmsg", cancellationToken);
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

        var messageUri = new Uri(new Uri(NormalizeHttpBaseUrl(server), UriKind.Absolute), "message");
        var request = new HttpRequestMessage(HttpMethod.Post, messageUri);
        request.Headers.Add("X-Gotify-Key", token.Trim());
        request.Content = JsonContent.Create(new { title = title.Trim(), message = message.Trim() });
        return await SendRequestAsync(request, "Gotify", cancellationToken, inspectJsonResponse: true);
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
            .Replace("{title}", title.Trim(), StringComparison.Ordinal)
            .Replace("{content}", message.Trim(), StringComparison.Ordinal)
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

        return await SendRequestAsync(request, "CustomWebhook", cancellationToken, inspectJsonResponse: false);
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

    private async Task<UiOperationResult> SendDiscordWebhookAsync(
        string webhookUrl,
        string message,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(webhookUrl, UriKind.Absolute))
        {
            Content = JsonContent.Create(new { content = message.Trim() }),
        };
        return await SendRequestAsync(request, "Discord", cancellationToken, inspectJsonResponse: false);
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
            return await SendRequestAsync(messageRequest, "Discord", cancellationToken, inspectJsonResponse: false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return UiOperationResult.Fail(
                UiErrorCode.NotificationProviderNetworkFailure,
                "Notification test timed out for `Discord`.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return UiOperationResult.Fail(
                UiErrorCode.NotificationProviderNetworkFailure,
                $"Notification network failure for `Discord`: {ex.Message}");
        }
    }

    private async Task<UiOperationResult> SendJsonPostAsync(
        Uri uri,
        string json,
        string provider,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        return await SendRequestAsync(request, provider, cancellationToken, inspectJsonResponse: true);
    }

    private async Task<UiOperationResult> SendFormPostAsync(
        string url,
        string postData,
        string provider,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(url, UriKind.Absolute))
        {
            Content = new StringContent(postData, Encoding.UTF8, "application/x-www-form-urlencoded"),
        };
        return await SendRequestAsync(request, provider, cancellationToken, inspectJsonResponse: true);
    }

    private async Task<UiOperationResult> SendRequestAsync(
        HttpRequestMessage request,
        string provider,
        CancellationToken cancellationToken,
        bool inspectJsonResponse)
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

            if (inspectJsonResponse)
            {
                var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
                var jsonFailure = TryReadProviderJsonFailure(responseText);
                if (jsonFailure is not null)
                {
                    return UiOperationResult.Fail(
                        UiErrorCode.NotificationProviderNetworkFailure,
                        $"Notification test failed via `{provider}`: {jsonFailure}");
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
            return UiOperationResult.Fail(
                UiErrorCode.NotificationProviderNetworkFailure,
                $"Notification network failure for `{provider}`: {ex.Message}");
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

    private static void AddDiscordHeaders(HttpRequestMessage request, string botToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", botToken);
        request.Headers.UserAgent.ParseAdd("DiscordBot");
    }

    private static string? TryReadProviderJsonFailure(string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(responseText);
        var root = doc.RootElement;
        if (root.TryGetProperty("code", out var codeElement)
            && codeElement.TryGetInt32(out var code)
            && code is not (0 or 200))
        {
            var message = root.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString()
                : null;
            return string.IsNullOrWhiteSpace(message) ? $"code={code}" : $"{message} (code={code})";
        }

        if (root.TryGetProperty("success", out var successElement)
            && successElement.ValueKind == JsonValueKind.False)
        {
            var message = root.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString()
                : null;
            return string.IsNullOrWhiteSpace(message) ? "success=false" : message;
        }

        return null;
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
