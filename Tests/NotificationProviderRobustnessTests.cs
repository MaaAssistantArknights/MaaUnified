using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MAAUnified.Application.Models;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using MAAUnified.Application.Services.Notifications;
using MAAUnified.Compat.Constants;
using MAAUnified.Platform;
using Xunit;

namespace MAAUnified.Tests;

public sealed class NotificationProviderRobustnessTests
{
    private const string TelegramTruncatedMark = "[...]\n";

    [Fact]
    public async Task Telegram_OversizedMessage_KeepsTailWithinTheServiceLimit()
    {
        var (service, captured) = CreateHttpProbe(
            """{"ok":true}""");

        var result = await service.SendTestAsync(
            new NotificationProviderTestRequest(
                "Telegram",
                "botToken=token\nchatId=10001",
                "title",
                new string('L', 5000) + "|TAIL-MARKER"));

        Assert.True(result.Success);
        var text = ReadTelegramText(captured.Body!);
        Assert.True(text.Length <= 4096, $"text length {text.Length}");
        Assert.StartsWith(TelegramTruncatedMark, text, StringComparison.Ordinal);
        Assert.EndsWith("|TAIL-MARKER", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Telegram_OversizedMessage_DoesNotSplitSurrogatePair()
    {
        var (service, captured) = CreateHttpProbe(
            """{"ok":true}""");

        // Fit the cut position (total length - 4090) onto the low surrogate of an emoji so a naive
        // substring would leave an orphaned surrogate at the start of the kept tail.
        var message = new StringBuilder(new string('x', 5000));
        const int emojiHighIndex = 909;
        message[emojiHighIndex] = '\uD83D';
        message[emojiHighIndex + 1] = '\uDE00';

        var result = await service.SendTestAsync(
            new NotificationProviderTestRequest(
                "Telegram",
                "botToken=token\nchatId=10001",
                "title",
                message.ToString()));

        Assert.True(result.Success);
        var text = ReadTelegramText(captured.Body!);
        Assert.True(text.Length <= 4096, $"text length {text.Length}");
        Assert.StartsWith(TelegramTruncatedMark, text, StringComparison.Ordinal);
        Assert.False(char.IsLowSurrogate(text[TelegramTruncatedMark.Length]));
        Assert.True(IsWellFormedUtf16(text), "truncated text contains an unpaired surrogate");
    }

    [Fact]
    public async Task Telegram_Failure_RecordsResponseBodyAndRedactsBotToken()
    {
        const string botToken = "123456:TOKEN-SENTINEL";
        var (service, _) = CreateHttpProbe(
            $$"""
              {"ok":false,"description":"Bad Request: message is too long; request https://api.telegram.org/bot{{botToken}}/sendMessage"}
              """,
            HttpStatusCode.BadRequest);

        var result = await service.SendTestAsync(
            new NotificationProviderTestRequest(
                "Telegram",
                $"botToken={botToken}\nchatId=10001",
                "title",
                "message"));

        Assert.False(result.Success);
        Assert.Equal(UiErrorCode.NotificationProviderNetworkFailure, result.Error?.Code);
        var details = result.Error?.Details ?? string.Empty;
        Assert.Contains("status=400", details, StringComparison.Ordinal);
        Assert.Contains("message is too long", details, StringComparison.Ordinal);
        Assert.Contains("endpoint=https://api.telegram.org", details, StringComparison.Ordinal);
        Assert.DoesNotContain("TOKEN-SENTINEL", details, StringComparison.Ordinal);
        Assert.DoesNotContain("TOKEN-SENTINEL", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CustomWebhook_Failure_LogsAuthorityOnlyAndRedactsUrl()
    {
        const string url = "https://hooks.example.com/services/SECRET-URL-TOKEN";
        var (service, _) = CreateHttpProbe(
            $$"""{"error":"delivery failed for {{url}}"}""",
            HttpStatusCode.InternalServerError);

        var result = await service.SendTestAsync(
            new NotificationProviderTestRequest(
                "CustomWebhook",
                $"url={url}\nbody={{\"content\":\"{{content}}\"}}",
                "title",
                "message"));

        Assert.False(result.Success);
        var details = result.Error?.Details ?? string.Empty;
        Assert.Contains("endpoint=https://hooks.example.com", details, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-URL-TOKEN", details, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-URL-TOKEN", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CustomWebhook_ContentTypeHeader_GoesToContentHeaders()
    {
        var (service, captured) = CreateHttpProbe("{}");

        var result = await service.SendTestAsync(
            new NotificationProviderTestRequest(
                "CustomWebhook",
                "url=https://example.com/hook\nheaders=Content-Type: application/x-www-form-urlencoded\nX-Token: abc\nbody={\"content\":\"{content}\"}",
                "title",
                "message"));

        Assert.True(result.Success);
        var request = Assert.IsType<HttpRequestMessage>(captured.Request);
        Assert.NotNull(request.Content);
        Assert.Equal("application/x-www-form-urlencoded", request.Content!.Headers.ContentType?.MediaType);
        // The default application/json content type must be replaced, not duplicated.
        Assert.Single(request.Content.Headers.GetValues("Content-Type"));
        Assert.True(request.Headers.TryGetValues("X-Token", out var tokenValues));
        Assert.Equal("abc", Assert.Single(tokenValues));
    }

    [Fact]
    public async Task CustomWebhook_InvalidHeaderName_IsSkipped()
    {
        var (service, captured) = CreateHttpProbe("{}");

        var result = await service.SendTestAsync(
            new NotificationProviderTestRequest(
                "CustomWebhook",
                "url=https://example.com/hook\nheaders={bad}: 1\nX-Ok: yes\nbody={\"content\":\"{content}\"}",
                "title",
                "message"));

        Assert.True(result.Success);
        var request = Assert.IsType<HttpRequestMessage>(captured.Request);
        Assert.True(request.Headers.TryGetValues("X-Ok", out var values));
        Assert.Equal("yes", Assert.Single(values));
        Assert.False(request.Headers.Contains("bad"));
    }

    [Fact]
    public async Task CustomWebhook_Placeholders_AreJsonEscaped()
    {
        var (service, captured) = CreateHttpProbe("{}");

        var result = await service.SendTestAsync(
            new NotificationProviderTestRequest(
                "CustomWebhook",
                "url=https://example.com/hook\nbody={\"title\":\"{title}\",\"content\":\"{content}\"}",
                "say \"hi\"\\path",
                "line1\nline2"));

        Assert.True(result.Success);
        using var document = JsonDocument.Parse(captured.Body!);
        Assert.Equal(
            "[MAA] say \"hi\"\\path",
            document.RootElement.GetProperty("title").GetString());
        // WPF 69b1ef2280 escapes newlines so the template stays valid JSON; the parsed value
        // therefore carries a real newline instead of breaking the document.
        Assert.Equal("line1\nline2", document.RootElement.GetProperty("content").GetString());
    }

    [Fact]
    public async Task AvailableProviders_NoLongerExposeDiscordWebhook()
    {
        var service = new NotificationProviderFeatureService();

        var providers = await service.GetAvailableProvidersAsync();

        Assert.Equal(9, providers.Length);
        Assert.DoesNotContain("DiscordWebhook", providers, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("CustomWebhook", providers, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeliveryPolicy_LegacyEnableDetails_MapsToIncludePreviousLogsWithDetailedCap()
    {
        var config = CreateConfig(profile =>
        {
            profile.Values[ConfigurationKeys.ExternalNotificationEnabled] = JsonValue.Create("Telegram");
            profile.Values[ConfigurationKeys.ExternalNotificationEnableDetails] = JsonValue.Create(true);
        });

        var policy = ExternalNotificationDeliveryPolicy.FromConfig(config);

        Assert.True(policy.Enable);
        Assert.True(policy.IncludePreviousLogs);
        Assert.Equal(ExternalNotificationDeliveryPolicy.DetailedMaxEntries, policy.MaxEntries);
        Assert.Equal(ExternalNotificationDeliveryPolicy.DefaultTimeMinutes, policy.TimeMinutes);
        Assert.False(policy.FilterPreviousLogs);
    }

    [Fact]
    public void DeliveryPolicy_NewKeysOverrideLegacyDetailsFlag()
    {
        var config = CreateConfig(profile =>
        {
            profile.Values[ConfigurationKeys.ExternalNotificationEnabled] = JsonValue.Create("Telegram");
            profile.Values[ConfigurationKeys.ExternalNotificationEnableDetails] = JsonValue.Create(true);
            profile.Values[ConfigurationKeys.ExternalNotificationIncludePreviousLogs] = JsonValue.Create(false);
            profile.Values[ConfigurationKeys.ExternalNotificationMaxEntries] = JsonValue.Create(7);
            profile.Values[ConfigurationKeys.ExternalNotificationTimeMinutes] = JsonValue.Create(0);
            profile.Values[ConfigurationKeys.ExternalNotificationBlacklist] = JsonValue.Create("noise");
            profile.Values[ConfigurationKeys.ExternalNotificationFilterPreviousLogs] = JsonValue.Create(true);
        });

        var policy = ExternalNotificationDeliveryPolicy.FromConfig(config);

        Assert.False(policy.IncludePreviousLogs);
        Assert.Equal(7, policy.MaxEntries);
        Assert.Equal(0, policy.TimeMinutes);
        Assert.True(policy.FilterPreviousLogs);
        Assert.Equal("noise", policy.Blacklist);
    }

    [Fact]
    public void DeliveryPolicy_ClampsOutOfRangeValues()
    {
        var config = CreateConfig(profile =>
        {
            profile.Values[ConfigurationKeys.ExternalNotificationMaxEntries] = JsonValue.Create(999999);
            profile.Values[ConfigurationKeys.ExternalNotificationTimeMinutes] = JsonValue.Create(-5);
            profile.Values[ConfigurationKeys.ExternalNotificationNewLogCount] = JsonValue.Create(0);
        });

        var policy = ExternalNotificationDeliveryPolicy.FromConfig(config);

        Assert.Equal(10000, policy.MaxEntries);
        Assert.Equal(0, policy.TimeMinutes);
        Assert.Equal(1, policy.NewLogCount);
    }

    [Fact]
    public void HistoryBundle_TakesNewestWithinWindowAndSkipsBlacklistedLines()
    {
        var origin = DateTimeOffset.Parse("2026-11-12T10:00:00+08:00");
        var history = new NotificationLogHistory();
        history.Add(new NotificationLogRecord(origin.AddMinutes(-30), "TraceLogBrush", "old-line"));
        history.Add(new NotificationLogRecord(origin.AddMinutes(-5), "TraceLogBrush", "noise-line"));
        history.Add(new NotificationLogRecord(origin.AddMinutes(-4), "TraceLogBrush", "first-kept"));
        history.Add(new NotificationLogRecord(origin.AddMinutes(-3), "ErrorLogBrush", "second-kept"));
        history.Add(new NotificationLogRecord(origin.AddMinutes(-2), "TraceLogBrush", "third-kept"));
        history.Add(new NotificationLogRecord(origin.AddMinutes(-1), "TraceLogBrush", "newest-kept"));

        var policy = CreatePolicy(includePreviousLogs: true, maxEntries: 3, timeMinutes: 10, filterPreviousLogs: true, blacklist: "noise");
        var bundled = history.Bundle("body", policy, origin);

        var lines = bundled.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);
        Assert.EndsWith("second-kept", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("third-kept", lines[1], StringComparison.Ordinal);
        Assert.EndsWith("newest-kept", lines[2], StringComparison.Ordinal);
        Assert.Equal("body", lines[3]);
        Assert.DoesNotContain("old-line", bundled, StringComparison.Ordinal);
        Assert.DoesNotContain("noise-line", bundled, StringComparison.Ordinal);
        Assert.Contains("[ErrorLogBrush]second-kept", bundled, StringComparison.Ordinal);
    }

    [Fact]
    public void HistoryBundle_InvalidBlacklist_ReturnsBodyUnchanged()
    {
        var origin = DateTimeOffset.Parse("2026-11-12T10:00:00+08:00");
        var history = new NotificationLogHistory();
        history.Add(new NotificationLogRecord(origin.AddMinutes(-1), "TraceLogBrush", "line"));

        var policy = CreatePolicy(includePreviousLogs: true, maxEntries: 5, timeMinutes: 60, filterPreviousLogs: true, blacklist: "([");

        Assert.Equal("body", history.Bundle("body", policy, origin));
    }

    [Theory]
    [InlineData(false, 5)]
    [InlineData(true, 0)]
    public void HistoryBundle_DisabledOrZeroEntries_ReturnsBodyUnchanged(bool includePreviousLogs, int maxEntries)
    {
        var origin = DateTimeOffset.Parse("2026-11-12T10:00:00+08:00");
        var history = new NotificationLogHistory();
        history.Add(new NotificationLogRecord(origin.AddMinutes(-1), "TraceLogBrush", "line"));

        var policy = CreatePolicy(includePreviousLogs: includePreviousLogs, maxEntries: maxEntries);

        Assert.Equal("body", history.Bundle("body", policy, origin));
    }

    [Fact]
    public void Trigger_CountThresholdAndContentMatch_ResetWhenDisabled()
    {
        var trigger = new ExternalNotificationTrigger();
        var countPolicy = CreatePolicy(
            enable: true,
            sendAfterLogCount: true,
            newLogCount: 3,
            sendWhenContentMatches: true,
            whitelist: "ERROR");

        Assert.False(trigger.ShouldSend(countPolicy, "first", null, hasChannels: true));
        Assert.False(trigger.ShouldSend(countPolicy, "second", null, hasChannels: true));
        Assert.True(trigger.ShouldSend(countPolicy, "third", null, hasChannels: true));

        Assert.False(trigger.ShouldSend(countPolicy, "first", null, hasChannels: false));
        Assert.False(trigger.ShouldSend(countPolicy, "first", null, hasChannels: true));
        Assert.False(trigger.ShouldSend(countPolicy, "second", null, hasChannels: true));
        Assert.True(trigger.ShouldSend(countPolicy, "third", null, hasChannels: true));

        Assert.False(trigger.ShouldSend(countPolicy, "ERROR text", null, hasChannels: false));
        Assert.True(trigger.ShouldSend(countPolicy, "ERROR text", null, hasChannels: true));
    }

    [Fact]
    public void Trigger_KindSelectionUsesDeliverySwitches()
    {
        var trigger = new ExternalNotificationTrigger();
        var policy = CreatePolicy(
            enable: true,
            sendWhenComplete: true,
            sendWhenError: false,
            sendWhenStalled: true);

        Assert.True(trigger.ShouldSend(policy, "done", ExternalNotificationKind.TaskComplete, hasChannels: true));
        Assert.False(trigger.ShouldSend(policy, "failed", ExternalNotificationKind.TaskError, hasChannels: true));
        Assert.True(trigger.ShouldSend(policy, "stalled", ExternalNotificationKind.Stalled, hasChannels: true));
    }

    [Fact]
    public async Task DialogEvents_RecordContentButtonsAndSelectionEvenWithoutVerboseDiagnostics()
    {
        var root = CreateTempRoot();
        try
        {
            var diagnostics = new UiDiagnosticsService(root, new UiLogService(), verboseDiagnostics: false);
            var service = new DialogFeatureService(diagnostics);

            var token = await service.BeginDialogAsync(
                DialogType.WarningConfirm,
                "Settings.Test",
                "Confirm?",
                CancellationToken.None,
                new DialogLogDescriptor("Body line 1\nBody line 2", ["OK", "Cancel"]));
            await service.CompleteDialogAsync(
                token,
                DialogReturnSemantic.Cancel,
                "warning-confirm-dialog-cancelled",
                CancellationToken.None,
                "Cancel");

            var eventLog = await File.ReadAllTextAsync(diagnostics.EventLogPath);
            Assert.Contains("Dialog.Open", eventLog, StringComparison.Ordinal);
            Assert.Contains($"trace={token.TraceId}", eventLog, StringComparison.Ordinal);
            Assert.Contains("title=Confirm?", eventLog, StringComparison.Ordinal);
            Assert.Contains("buttons=OK | Cancel", eventLog, StringComparison.Ordinal);
            Assert.Contains("content=Body line 1\\nBody line 2", eventLog, StringComparison.Ordinal);
            Assert.Contains("Dialog.Close", eventLog, StringComparison.Ordinal);
            Assert.Contains("selection=Cancel (Cancel)", eventLog, StringComparison.Ordinal);
            Assert.Contains("durationMs=", eventLog, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task VerboseOnlyEvents_StaySuppressedWhileUserVisibleEventsAreRecorded()
    {
        var root = CreateTempRoot();
        try
        {
            var diagnostics = new UiDiagnosticsService(root, new UiLogService(), verboseDiagnostics: false);

            await diagnostics.RecordEventAsync("Verbose.Only", "hidden-event");
            await diagnostics.RecordUserVisibleEventAsync("Dialog.Open", "visible-event");

            var eventLog = await File.ReadAllTextAsync(diagnostics.EventLogPath);
            Assert.Contains("visible-event", eventLog, StringComparison.Ordinal);
            Assert.DoesNotContain("hidden-event", eventLog, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task SystemNotification_ContentIsRecordedAsUserVisibleEvent()
    {
        var root = CreateTempRoot();
        try
        {
            var diagnostics = new UiDiagnosticsService(root, new UiLogService(), verboseDiagnostics: false);
            var bundle = new PlatformServiceBundle
            {
                TrayService = new NoOpTrayService(),
                NotificationService = new NoOpNotificationService(),
                HotkeyService = new NoOpGlobalHotkeyService(),
                AutostartService = new NoOpAutostartService(),
                FileDialogService = new NoOpFileDialogService(),
                OverlayService = new NoOpOverlayCapabilityService(),
                PostActionExecutorService = new NoOpPostActionExecutorService(),
            };
            var capability = new PlatformCapabilityFeatureService(bundle, diagnostics);

            await capability.SendSystemNotificationAsync(
                new SystemNotificationRequest("Task complete", "All tasks completed", []),
                CancellationToken.None);

            var eventLog = await File.ReadAllTextAsync(diagnostics.EventLogPath);
            Assert.Contains("Notification.Growl", eventLog, StringComparison.Ordinal);
            Assert.Contains("title=Task complete", eventLog, StringComparison.Ordinal);
            Assert.Contains("content=All tasks completed", eventLog, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task IssueReportBundle_IncludesEventLogOutsideVerboseDiagnostics()
    {
        var root = CreateTempRoot();
        try
        {
            var diagnostics = new UiDiagnosticsService(root, new UiLogService(), verboseDiagnostics: false);
            await diagnostics.RecordUserVisibleEventAsync("Dialog.Open", "bundle-visible-event");

            var bundlePath = await diagnostics.BuildIssueReportBundleAsync(root);
            using var archive = ZipFile.OpenRead(bundlePath);
            var entry = Assert.Single(archive.Entries.Where(static item =>
                string.Equals(item.FullName, "debug/avalonia-ui-events.log", StringComparison.Ordinal)));
            using var reader = new StreamReader(entry.Open());
            var content = await reader.ReadToEndAsync();
            Assert.Contains("bundle-visible-event", content, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public void ResolveEnabled_MigratesDiscordWebhookIntoAnAdditionalCustomWebhookChannel()
    {
        var config = CreateConfig(profile =>
        {
            profile.Values[ConfigurationKeys.ExternalNotificationEnabled] = JsonValue.Create("Discord Webhook,Custom Webhook");
            profile.Values[ConfigurationKeys.ExternalNotificationDiscordWebhookUrl] =
                JsonValue.Create("https://discord.example.com/api/webhooks/legacy");
            profile.Values[ConfigurationKeys.ExternalNotificationCustomWebhookUrl] =
                JsonValue.Create("https://example.com/custom");
            profile.Values[ConfigurationKeys.ExternalNotificationCustomWebhookBody] =
                JsonValue.Create("{\"custom\":\"{content}\"}");
        });

        var channels = ExternalNotificationChannels.ResolveEnabled(config);

        Assert.Equal(2, channels.Count);
        Assert.All(channels, channel => Assert.Equal("CustomWebhook", channel.Provider));
        Assert.Equal("https://discord.example.com/api/webhooks/legacy", channels[0].Parameters["url"]);
        Assert.Equal(ExternalNotificationChannels.DefaultCustomWebhookBody, channels[0].Parameters["body"]);
        Assert.Equal("https://example.com/custom", channels[1].Parameters["url"]);
        Assert.Equal("{\"custom\":\"{content}\"}", channels[1].Parameters["body"]);
    }

    [Fact]
    public void ResolveEnabled_AddsBarkDefaultServerAndSkipsUnknownSegments()
    {
        var config = CreateConfig(profile =>
        {
            profile.Values[ConfigurationKeys.ExternalNotificationEnabled] = JsonValue.Create("Bark,UnknownChannel");
            profile.Values[ConfigurationKeys.ExternalNotificationBarkSendKey] = JsonValue.Create("bark-key");
        });

        var channel = Assert.Single(ExternalNotificationChannels.ResolveEnabled(config));

        Assert.Equal("Bark", channel.Provider);
        Assert.Equal("bark-key", channel.Parameters["sendKey"]);
        Assert.Equal("https://api.day.app", channel.Parameters["server"]);
        var parameterText = ExternalNotificationChannels.BuildParameterText(channel);
        Assert.Contains("sendKey=bark-key", parameterText, StringComparison.Ordinal);
        Assert.Contains("server=https://api.day.app", parameterText, StringComparison.Ordinal);
    }

    private static (NotificationProviderFeatureService Service, CapturedRequest Captured) CreateHttpProbe(
        string responseBody,
        HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var captured = new CapturedRequest();
        var service = new NotificationProviderFeatureService(
            supported: true,
            sendAsync: null,
            sendHttpAsync: async (request, cancellationToken) =>
            {
                captured.Request = request;
                captured.Body = request.Content is null
                    ? null
                    : await request.Content.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(statusCode)
                {
                    Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
                };
            });
        return (service, captured);
    }

    private static string ReadTelegramText(string requestBody)
    {
        using var document = JsonDocument.Parse(requestBody);
        return document.RootElement.GetProperty("text").GetString() ?? string.Empty;
    }

    private static bool IsWellFormedUtf16(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (!char.IsSurrogate(text[index]))
            {
                continue;
            }

            if (!char.IsHighSurrogate(text[index])
                || index + 1 >= text.Length
                || !char.IsLowSurrogate(text[index + 1]))
            {
                return false;
            }

            index++;
        }

        return true;
    }

    private static ExternalNotificationDeliveryPolicy CreatePolicy(
        bool enable = true,
        bool sendWhenComplete = true,
        bool sendWhenError = true,
        bool sendWhenStalled = true,
        bool sendAfterLogCount = false,
        int newLogCount = 10,
        bool sendWhenContentMatches = false,
        string whitelist = "",
        bool includePreviousLogs = false,
        int maxEntries = 2,
        int timeMinutes = 60,
        bool filterPreviousLogs = false,
        string blacklist = "")
        => new(
            enable,
            sendWhenComplete,
            sendWhenError,
            sendWhenStalled,
            SendBeforeScheduledStart: false,
            sendAfterLogCount,
            newLogCount,
            sendWhenContentMatches,
            whitelist,
            includePreviousLogs,
            maxEntries,
            timeMinutes,
            filterPreviousLogs,
            blacklist);

    private static UnifiedConfig CreateConfig(Action<UnifiedProfile> configure)
    {
        var config = new UnifiedConfig
        {
            CurrentProfile = "Default",
            Profiles =
            {
                ["Default"] = new UnifiedProfile(),
            },
        };
        configure(config.Profiles["Default"]);
        return config;
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "maa-unified-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        return root;
    }

    private static void DeleteTempRoot(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch
        {
            // Ignore cleanup failures in temporary test directories.
        }
    }

    private sealed class CapturedRequest
    {
        public HttpRequestMessage? Request { get; set; }

        public string? Body { get; set; }
    }
}
