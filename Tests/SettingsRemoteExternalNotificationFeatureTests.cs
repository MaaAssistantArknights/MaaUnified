using System.Runtime.CompilerServices;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using MAAUnified.App.ViewModels.Settings;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using MAAUnified.Application.Services.Localization;
using MAAUnified.Compat.Constants;
using MAAUnified.CoreBridge;
using MAAUnified.Platform;

namespace MAAUnified.Tests;

public sealed class SettingsRemoteExternalNotificationFeatureTests
{
    [Fact]
    public async Task RemoteControlConnectivity_ValidEndpoints_SucceedsAndLogs()
    {
        var remote = new ScriptedRemoteControlFeatureService
        {
            TestHandler = static request =>
                UiOperationResult<RemoteControlConnectivityResult>.Ok(
                    new RemoteControlConnectivityResult(
                        request.PollIntervalMs,
                        new EndpointProbeResult("GetTask", request.GetTaskEndpoint, true, 200, "HTTP 200"),
                        new EndpointProbeResult("Report", request.ReportEndpoint, true, 204, "HTTP 204")),
                    "Remote connectivity passed."),
        };

        await using var fixture = await RuntimeFixture.CreateAsync(remoteControlFeatureService: remote);
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        vm.RemoteGetTaskEndpoint = "https://example.com/get";
        vm.RemoteReportEndpoint = "https://example.com/report";
        vm.RemotePollInterval = 5000;

        await vm.TestRemoteControlConnectivityAsync();

        Assert.False(vm.HasRemoteControlErrorMessage);
        Assert.Contains("连通测试成功", vm.RemoteControlStatusMessage, StringComparison.Ordinal);
        var eventLog = await File.ReadAllTextAsync(fixture.Diagnostics.EventLogPath);
        Assert.Contains("Settings.RemoteControl.Test", eventLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoteControlConnectivity_InvalidUri_ReturnsParameterError()
    {
        var remote = new ScriptedRemoteControlFeatureService
        {
            TestHandler = static _ => UiOperationResult<RemoteControlConnectivityResult>.Fail(
                UiErrorCode.RemoteControlInvalidParameters,
                "GetTask endpoint is invalid."),
        };

        await using var fixture = await RuntimeFixture.CreateAsync(remoteControlFeatureService: remote);
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await vm.TestRemoteControlConnectivityAsync();

        Assert.True(vm.HasRemoteControlErrorMessage);
        Assert.Contains(UiErrorCode.RemoteControlInvalidParameters, vm.RemoteControlErrorMessage, StringComparison.Ordinal);
        var errorLog = await File.ReadAllTextAsync(fixture.Diagnostics.ErrorLogPath);
        Assert.Contains("Settings.RemoteControl.Test", errorLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoteControlConnectivity_NetworkFailure_ReturnsNetworkError()
    {
        var details = new RemoteControlConnectivityResult(
            5000,
            new EndpointProbeResult("GetTask", "https://example.com/get", false, null, "timeout", UiErrorCode.RemoteControlNetworkFailure),
            new EndpointProbeResult("Report", "https://example.com/report", true, 200, "HTTP 200"));
        var remote = new ScriptedRemoteControlFeatureService
        {
            TestHandler = _ => UiOperationResult<RemoteControlConnectivityResult>.Fail(
                UiErrorCode.RemoteControlNetworkFailure,
                "Remote connectivity failed.",
                JsonSerializer.Serialize(details)),
        };

        await using var fixture = await RuntimeFixture.CreateAsync(remoteControlFeatureService: remote);
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        await vm.TestRemoteControlConnectivityAsync();

        Assert.True(vm.HasRemoteControlErrorMessage);
        Assert.Contains(UiErrorCode.RemoteControlNetworkFailure, vm.RemoteControlErrorMessage, StringComparison.Ordinal);
        Assert.Contains("GetTask=", vm.RemoteControlErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExternalNotification_LoadsAllProviders()
    {
        var notification = new ScriptedNotificationProviderFeatureService();

        await using var fixture = await RuntimeFixture.CreateAsync(notificationProviderFeatureService: notification);
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        Assert.Equal(9, vm.AvailableNotificationProviders.Count);
        Assert.Contains("Smtp", vm.AvailableNotificationProviders, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("DingTalk", vm.AvailableNotificationProviders, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("CustomWebhook", vm.AvailableNotificationProviders, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExternalNotification_ProviderSelection_BootstrapsEditableState_AndDisablesWithLastProvider()
    {
        await using var fixture = await RuntimeFixture.CreateAsync(
            notificationProviderFeatureService: new ScriptedNotificationProviderFeatureService());
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        var telegram = vm.NotificationProviderSelections.Single(
            static item => string.Equals(item.Provider, "Telegram", StringComparison.OrdinalIgnoreCase));

        Assert.False(vm.ExternalNotificationEnabled);
        Assert.False(vm.CanEditExternalNotification);
        Assert.True(vm.CanSelectExternalNotificationProvider);

        telegram.IsEnabled = true;

        Assert.True(vm.ExternalNotificationEnabled);
        Assert.True(vm.CanEditExternalNotification);
        Assert.Equal("Telegram", vm.SelectedNotificationProvider);

        telegram.IsEnabled = false;

        Assert.False(vm.ExternalNotificationEnabled);
        Assert.False(vm.CanEditExternalNotification);
    }

    [Fact]
    public async Task ExternalNotification_ValidateParameters_AllProviders_DataDriven()
    {
        var service = new NotificationProviderFeatureService();
        var cases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Smtp"] = "server=smtp.example.com\nport=465\nfrom=noreply@example.com\nto=ops@example.com",
            ["ServerChan"] = "sendKey=abc123",
            ["Bark"] = "sendKey=abc123\nserver=https://api.day.app",
            ["Discord"] = "botToken=bot-token\nuserId=10001",
            ["DiscordWebhook"] = "webhookUrl=https://discord.com/api/webhooks/test",
            ["DingTalk"] = "accessToken=ding-token\nsecret=ding-secret",
            ["Telegram"] = "botToken=token\nchatId=10001",
            ["Qmsg"] = "key=qmsg-key\nserver=https://qmsg.example.com",
            ["Gotify"] = "server=https://gotify.example.com\ntoken=test-token",
            ["CustomWebhook"] = "url=https://webhook.example.com/notify\nbody={\"ok\":true}",
        };

        foreach (var (provider, parameters) in cases)
        {
            var result = await service.ValidateProviderParametersAsync(
                new NotificationProviderRequest(provider, parameters));
            Assert.True(result.Success, $"Provider {provider} should pass validation but failed with: {result.Message}");
        }
    }

    [Fact]
    public async Task RemoteControlIdentity_SaveAndReload_RoundTripStable()
    {
        var root = Path.Combine(Path.GetTempPath(), "maa-unified-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using (var first = await RuntimeFixture.CreateAsync(root, cleanupRoot: false))
            {
                var vm = new SettingsPageViewModel(first.Runtime, new ConnectionGameSharedStateViewModel());
                await vm.InitializeAsync();

                vm.RemoteGetTaskEndpoint = "https://example.com/get";
                vm.RemoteReportEndpoint = "https://example.com/report";
                vm.RemotePollInterval = 5200;
                vm.RemoteUserIdentity = "  user-A  ";
                vm.RemoteDeviceIdentity = "  device-B  ";

                await vm.SaveRemoteControlAsync();

                Assert.Equal("user-A", ReadCurrentProfileString(first.Config, ConfigurationKeys.RemoteControlUserIdentity));
                Assert.Equal("device-B", ReadCurrentProfileString(first.Config, ConfigurationKeys.RemoteControlDeviceIdentity));
            }

            await using var second = await RuntimeFixture.CreateAsync(root, cleanupRoot: false);
            var reloaded = new SettingsPageViewModel(second.Runtime, new ConnectionGameSharedStateViewModel());
            await reloaded.InitializeAsync();

            Assert.Equal("user-A", reloaded.RemoteUserIdentity);
            Assert.Equal("device-B", reloaded.RemoteDeviceIdentity);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // ignore cleanup failures in temporary test directories
            }
        }
    }

    [Fact]
    public async Task RemoteControlIdentity_InvalidControlCharacters_BlocksSaveAndPreservesConfig()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        fixture.Config.CurrentConfig.Profiles[fixture.Config.CurrentConfig.CurrentProfile].Values[ConfigurationKeys.RemoteControlUserIdentity] = JsonValue.Create("baseline-user");
        fixture.Config.CurrentConfig.Profiles[fixture.Config.CurrentConfig.CurrentProfile].Values[ConfigurationKeys.RemoteControlDeviceIdentity] = JsonValue.Create("baseline-device");
        await fixture.Config.SaveAsync();

        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.RemoteUserIdentity = "bad\nuser";
        vm.RemoteDeviceIdentity = "good-device";

        await vm.SaveRemoteControlAsync();

        Assert.True(vm.HasRemoteControlErrorMessage);
        Assert.Contains(UiErrorCode.RemoteControlInvalidParameters, vm.RemoteControlErrorMessage, StringComparison.Ordinal);
        Assert.Equal("baseline-user", ReadCurrentProfileString(fixture.Config, ConfigurationKeys.RemoteControlUserIdentity));
        Assert.Equal("baseline-device", ReadCurrentProfileString(fixture.Config, ConfigurationKeys.RemoteControlDeviceIdentity));
    }

    [Fact]
    public async Task ExternalNotification_DingTalk_MissingAccessToken_FailsValidation()
    {
        var service = new NotificationProviderFeatureService();
        var result = await service.ValidateProviderParametersAsync(
            new NotificationProviderRequest("DingTalk", "secret=only-secret"));

        Assert.False(result.Success);
        Assert.Equal(UiErrorCode.NotificationProviderInvalidParameters, result.Error?.Code);
        Assert.Contains("accessToken", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExternalNotification_DingTalk_SendTest_SuccessAndNetworkFailureAreClassified()
    {
        var successService = CreateNotificationProviderServiceForHttpProbe(
            static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"errcode":0,"errmsg":"ok"}"""),
            }));
        var success = await successService.SendTestAsync(
            new NotificationProviderTestRequest(
                "DingTalk",
                "accessToken=test-token\nsecret=test-secret",
                "title",
                "message"));
        Assert.True(success.Success);

        var networkFailureService = CreateNotificationProviderServiceForHttpProbe(
            static (_, _) => throw new HttpRequestException("network down"));
        var failure = await networkFailureService.SendTestAsync(
            new NotificationProviderTestRequest(
                "DingTalk",
                "accessToken=test-token\nsecret=test-secret",
                "title",
                "message"));
        Assert.False(failure.Success);
        Assert.Equal(UiErrorCode.NotificationProviderNetworkFailure, failure.Error?.Code);
        Assert.DoesNotContain("network down", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("network down", failure.Error?.Details ?? string.Empty, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.NoContent)]
    [InlineData(System.Net.HttpStatusCode.OK)]
    public async Task ExternalNotification_DiscordWebhook_EmptyBody2xx_Succeeds(System.Net.HttpStatusCode statusCode)
    {
        // WPF frozen 23ddd271 (DiscordWebhookNotificationProvider) treats an empty response
        // body as success without requiring 204; Discord-compatible endpoints may return
        // 200 OK with an empty body and must not be reported as failures.
        var service = CreateNotificationProviderServiceForHttpProbe(
            (_, _) => Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(string.Empty),
            }));

        var result = await service.SendTestAsync(
            new NotificationProviderTestRequest(
                "DiscordWebhook",
                "webhookUrl=https://discord.example.com/api/webhooks/test",
                "title",
                "message"));

        Assert.True(result.Success);
    }

    [Fact]
    public async Task ExternalNotification_DiscordWebhook_EmptyBodyErrorStatus_StillFails()
    {
        // Deliberate divergence from WPF frozen 23ddd271: an empty body on a non-2xx status
        // is a real failure and must not be reported as success.
        var service = CreateNotificationProviderServiceForHttpProbe(
            static (_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
            {
                Content = new StringContent(string.Empty),
            }));

        var result = await service.SendTestAsync(
            new NotificationProviderTestRequest(
                "DiscordWebhook",
                "webhookUrl=https://discord.example.com/api/webhooks/test",
                "title",
                "message"));

        Assert.False(result.Success);
        Assert.Equal(UiErrorCode.NotificationProviderNetworkFailure, result.Error?.Code);
    }

    [Fact]
    public async Task ExternalNotification_DiscordWebhook_NonEmptyBody_Fails()
    {
        // WPF frozen 23ddd271 treats any non-empty webhook response (JSON error payload or
        // foreign HTML) as a failed send.
        var service = CreateNotificationProviderServiceForHttpProbe(
            static (_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"message":"Unknown Webhook","code":10015}"""),
            }));

        var result = await service.SendTestAsync(
            new NotificationProviderTestRequest(
                "DiscordWebhook",
                "webhookUrl=https://discord.example.com/api/webhooks/test",
                "title",
                "message"));

        Assert.False(result.Success);
        Assert.Equal(UiErrorCode.NotificationProviderNetworkFailure, result.Error?.Code);
    }

    [Fact]
    public async Task RemoteControlConnectivity_DoesNotPersistEndpointOrProbeMessage()
    {
        var ctor = typeof(RemoteControlFeatureService).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(UnifiedConfigurationService), typeof(UnifiedSessionService), typeof(IConnectFeatureService), typeof(ITaskQueueFeatureService), typeof(IToolboxFeatureService), typeof(IMaaCoreBridge), typeof(UiLogService), typeof(Func<string, Uri, CancellationToken, Task<EndpointProbeResult>>), typeof(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>)],
            modifiers: null);
        Assert.NotNull(ctor);
        var endpointSentinel = "https://user:REMOTE_SECRET@example.test/path?token=REMOTE_SECRET";
        var probeMessage = $"HTTP 500 body={endpointSentinel}";
        var remote = (RemoteControlFeatureService)ctor!.Invoke([
            null, null, null, null, null, null, null,
            (Func<string, Uri, CancellationToken, Task<EndpointProbeResult>>)((name, _, _) => Task.FromResult(
                new EndpointProbeResult(name, endpointSentinel, false, 500, probeMessage, UiErrorCode.RemoteControlNetworkFailure))),
            null
        ]);

        var result = await remote.TestConnectivityAsync(new RemoteControlConnectivityRequest(endpointSentinel, endpointSentinel, 1000));

        Assert.False(result.Success);
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(endpointSentinel, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("REMOTE_SECRET", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("body=", serialized, StringComparison.Ordinal);
        Assert.Contains("operation=GetTask", result.Error?.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExternalNotification_Bark_SendTest_PostsPushPayload()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        var service = CreateNotificationProviderServiceForHttpProbe(
            async (request, cancellationToken) =>
            {
                capturedRequest = request;
                capturedBody = request.Content is null
                    ? null
                    : await request.Content.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"code":200}"""),
                };
            });

        var result = await service.SendTestAsync(
            new NotificationProviderTestRequest(
                "Bark",
                "sendKey=bark-key\nserver=https://api.day.app",
                "title",
                "message"));

        Assert.True(result.Success);
        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.Equal("https://api.day.app/push", capturedRequest.RequestUri?.ToString());
        Assert.Contains("\"device_key\":\"bark-key\"", capturedBody, StringComparison.Ordinal);
        Assert.Contains("\"title\":\"[MAA] title\"", capturedBody, StringComparison.Ordinal);
        Assert.Contains("\"body\":\"message\"", capturedBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExternalNotification_Bark_ServerWithPath_UsesWpfAbsolutePushEndpoint()
    {
        // WPF frozen 23ddd271 (BarkNotificationProvider) posts to new Uri(base, "/push"),
        // which replaces any path in the configured server URL.
        HttpRequestMessage? capturedRequest = null;
        var service = CreateNotificationProviderServiceForHttpProbe((request, _) =>
        {
            capturedRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"code":200}"""),
            });
        });

        var result = await service.SendTestAsync(
            new NotificationProviderTestRequest(
                "Bark",
                "sendKey=bark-key\nserver=https://bark.example.com/sub",
                "title",
                "message"));

        Assert.True(result.Success);
        Assert.NotNull(capturedRequest);
        Assert.Equal("https://bark.example.com/push", capturedRequest!.RequestUri?.ToString());
    }

    [Fact]
    public async Task ExternalNotification_Gotify_ServerWithPath_UsesWpfAbsoluteMessageEndpoint()
    {
        // WPF frozen 23ddd271 (GotifyNotificationProvider) posts to new Uri(base, "/message"),
        // which replaces any path in the configured server URL.
        HttpRequestMessage? capturedRequest = null;
        var service = CreateNotificationProviderServiceForHttpProbe((request, _) =>
        {
            capturedRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":123}"""),
            });
        });

        var result = await service.SendTestAsync(
            new NotificationProviderTestRequest(
                "Gotify",
                "server=https://gotify.example.com/sub\ntoken=gotify-token",
                "title",
                "message"));

        Assert.True(result.Success);
        Assert.NotNull(capturedRequest);
        Assert.Equal("https://gotify.example.com/message", capturedRequest!.RequestUri?.ToString());
    }

    [Fact]
    public async Task ExternalNotification_CustomWebhook_MultiLineHeaders_SendTest_DeliversAllHeaders()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        var service = CreateNotificationProviderServiceForHttpProbe(async (request, cancellationToken) =>
        {
            capturedRequest = request;
            capturedBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var result = await service.SendTestAsync(
            new NotificationProviderTestRequest(
                "CustomWebhook",
                "url=https://example.com/hook\nheaders=X-Token: 123\nX-Second: abc\nbody={title}|{content}",
                "title",
                "message"));

        Assert.True(result.Success);
        Assert.NotNull(capturedRequest);
        Assert.True(capturedRequest!.Headers.TryGetValues("X-Token", out var tokenValues));
        Assert.Equal("123", Assert.Single(tokenValues));
        Assert.True(capturedRequest.Headers.TryGetValues("X-Second", out var secondValues));
        Assert.Equal("abc", Assert.Single(secondValues));
        Assert.Equal("[MAA] title|message", capturedBody);
    }

    [Fact]
    public async Task ExternalNotification_ParameterText_WithInvalidLine_RemainsRejected()
    {
        var service = CreateNotificationProviderServiceForHttpProbe(static (_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        var result = await service.ValidateProviderParametersAsync(
            new NotificationProviderRequest(
                "CustomWebhook",
                "url=https://example.com/hook\nthis line is not an assignment\nbody={title}"));

        Assert.False(result.Success);
        Assert.Equal(UiErrorCode.NotificationProviderInvalidParameters, result.Error?.Code);
    }

    [Fact]
    public async Task ExternalNotification_ValidateParameters_UsesChannelStatusText()
    {
        var notification = new ScriptedNotificationProviderFeatureService
        {
            ValidateHandler = static _ => UiOperationResult.Ok("valid"),
        };

        await using var fixture = await RuntimeFixture.CreateAsync(notificationProviderFeatureService: notification);
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        vm.ExternalNotificationEnabled = true;
        vm.SelectedNotificationProvider = "Bark";
        vm.NotificationProviderParametersText = "sendKey=abc123\nserver=https://api.day.app";
        await vm.ValidateExternalNotificationParametersAsync();

        Assert.Equal("Bark 通道参数校验通过。", vm.ExternalNotificationStatusMessage);
        Assert.DoesNotContain("Provider", vm.ExternalNotificationStatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExternalNotification_InvalidUnsavedLine_DoesNotReachDiagnosticsOrIssueReport()
    {
        const string sentinel = "UNSAVED/NOTIFY?token=raw+sentinel";
        const string encodedSentinel = "UNSAVED%2FNOTIFY%3Ftoken%3Draw%2Bsentinel";
        await using var fixture = await RuntimeFixture.CreateAsync();
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        vm.ExternalNotificationEnabled = true;
        vm.SelectedNotificationProvider = "CustomWebhook";
        vm.NotificationProviderParametersText = sentinel;

        await vm.ValidateExternalNotificationParametersAsync();

        Assert.True(vm.HasExternalNotificationErrorMessage);
        Assert.DoesNotContain(sentinel, vm.ExternalNotificationErrorMessage, StringComparison.Ordinal);
        var errorLog = await File.ReadAllTextAsync(fixture.Diagnostics.ErrorLogPath);
        Assert.DoesNotContain(sentinel, errorLog, StringComparison.Ordinal);
        Assert.DoesNotContain(encodedSentinel, errorLog, StringComparison.Ordinal);

        var bundlePath = await fixture.Diagnostics.BuildIssueReportBundleAsync(fixture.Root);
        using var archive = ZipFile.OpenRead(bundlePath);
        foreach (var entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            var content = await reader.ReadToEndAsync();
            Assert.DoesNotContain(sentinel, content, StringComparison.Ordinal);
            Assert.DoesNotContain(encodedSentinel, content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ExternalNotification_TestSend_SelectedProviderOnly()
    {
        var notification = new ScriptedNotificationProviderFeatureService
        {
            SendHandler = static _ => UiOperationResult.Ok("sent"),
        };

        await using var fixture = await RuntimeFixture.CreateAsync(notificationProviderFeatureService: notification);
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        vm.ExternalNotificationEnabled = true;
        vm.SelectedNotificationProvider = "Telegram";
        vm.NotificationProviderParametersText = "botToken=token\nchatId=12345";
        await vm.TestExternalNotificationAsync();

        var call = Assert.Single(notification.SendCalls);
        Assert.Equal("Telegram", call.Provider);
        Assert.Equal("Telegram 通道测试通知发送成功。", vm.ExternalNotificationStatusMessage);
    }

    [Fact]
    public async Task ExternalNotification_ValidateAndTestWithoutEnabledProvider_ShowVisibleError()
    {
        var notification = new ScriptedNotificationProviderFeatureService();
        await using var fixture = await RuntimeFixture.CreateAsync(notificationProviderFeatureService: notification);
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        Assert.False(vm.ExternalNotificationEnabled);

        await vm.ValidateExternalNotificationParametersAsync();

        Assert.True(vm.HasExternalNotificationErrorMessage);
        Assert.Contains(UiErrorCode.NotificationProviderInvalidParameters, vm.ExternalNotificationErrorMessage, StringComparison.Ordinal);
        Assert.Empty(notification.ValidateCalls);

        await vm.TestExternalNotificationAsync();

        Assert.True(vm.HasExternalNotificationErrorMessage);
        Assert.Contains(UiErrorCode.NotificationProviderInvalidParameters, vm.ExternalNotificationErrorMessage, StringComparison.Ordinal);
        Assert.Empty(notification.SendCalls);
    }

    [Fact]
    public async Task ExternalNotification_UnsupportedProvider_ReturnsUnsupportedError()
    {
        var notification = new ScriptedNotificationProviderFeatureService
        {
            SendHandler = static _ => UiOperationResult.Fail(
                UiErrorCode.NotificationProviderUnsupported,
                "provider unsupported"),
        };

        await using var fixture = await RuntimeFixture.CreateAsync(notificationProviderFeatureService: notification);
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        vm.ExternalNotificationEnabled = true;
        vm.SelectedNotificationProvider = "Smtp";
        vm.NotificationProviderParametersText = "server=smtp.example.com\nport=465\nfrom=a@b.c\nto=d@e.f";
        await vm.TestExternalNotificationAsync();

        Assert.True(vm.HasExternalNotificationWarningMessage);
        Assert.Contains(UiErrorCode.NotificationProviderUnsupported, vm.ExternalNotificationWarningMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExternalNotification_FailureMessages_AreDifferentiatedByCategory()
    {
        var invalidMessage = await RunExternalFailureCaseAsync(
            static request => UiOperationResult.Fail(UiErrorCode.NotificationProviderInvalidParameters, "bad params"),
            method: "validate");
        var networkMessage = await RunExternalFailureCaseAsync(
            static request => UiOperationResult.Fail(UiErrorCode.NotificationProviderNetworkFailure, "network down"),
            method: "send");
        var unsupportedMessage = await RunExternalFailureCaseAsync(
            static request => UiOperationResult.Fail(UiErrorCode.NotificationProviderUnsupported, "unsupported"),
            method: "send");

        Assert.Contains(UiErrorCode.NotificationProviderInvalidParameters, invalidMessage, StringComparison.Ordinal);
        Assert.Contains(UiErrorCode.NotificationProviderNetworkFailure, networkMessage, StringComparison.Ordinal);
        Assert.Contains(UiErrorCode.NotificationProviderUnsupported, unsupportedMessage, StringComparison.Ordinal);
        Assert.NotEqual(invalidMessage, networkMessage);
        Assert.NotEqual(networkMessage, unsupportedMessage);
    }

    [Fact]
    public async Task ExternalNotification_StalledToggle_DefaultsToEnabled_ForWpfParity()
    {
        // WPF frozen 23ddd271 defaults ExternalNotificationSendWhenTimeout to true,
        // so a fresh-install settings page must show the stalled toggle as enabled.
        await using var fixture = await RuntimeFixture.CreateAsync(
            notificationProviderFeatureService: new ScriptedNotificationProviderFeatureService
            {
                ValidateHandler = static _ => UiOperationResult.Ok("valid"),
            });
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        Assert.True(vm.ExternalNotificationSendWhenStalled);

        // Toggling off persists through save and reload.
        vm.ExternalNotificationSendWhenStalled = false;
        await vm.SaveExternalNotificationAsync();
        Assert.Equal(
            "False",
            ReadCurrentProfileString(fixture.Config, ConfigurationKeys.ExternalNotificationSendWhenStalled));

        // The WPF legacy key is dual-written so the choice travels back to WPF,
        // which only reads/writes ExternalNotification.SendWhenTimeout (frozen 23ddd271).
        Assert.Equal(
            "False",
            ReadCurrentProfileString(fixture.Config, ConfigurationKeys.ExternalNotificationSendWhenTimeout));

        // Toggling back on mirrors the legacy key as well.
        vm.ExternalNotificationSendWhenStalled = true;
        await vm.SaveExternalNotificationAsync();
        Assert.Equal(
            "True",
            ReadCurrentProfileString(fixture.Config, ConfigurationKeys.ExternalNotificationSendWhenTimeout));
    }

    [Fact]
    public async Task ExternalNotification_SaveAndReload_RoundTripStable()
    {
        var root = Path.Combine(Path.GetTempPath(), "maa-unified-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var notification = new ScriptedNotificationProviderFeatureService
            {
                ValidateHandler = static _ => UiOperationResult.Ok("valid"),
            };

            await using (var first = await RuntimeFixture.CreateAsync(
                root,
                cleanupRoot: false,
                notificationProviderFeatureService: notification))
            {
                var vm = new SettingsPageViewModel(first.Runtime, new ConnectionGameSharedStateViewModel());
                await vm.InitializeAsync();

                vm.ExternalNotificationEnabled = true;
                vm.ExternalNotificationSendWhenComplete = true;
                vm.ExternalNotificationSendWhenError = true;
                vm.ExternalNotificationSendWhenStalled = false;
                vm.ExternalNotificationEnableDetails = true;

                vm.NotificationProviderSelections.Single(
                    static item => string.Equals(item.Provider, "Smtp", StringComparison.OrdinalIgnoreCase)).IsEnabled = true;
                vm.NotificationProviderSelections.Single(
                    static item => string.Equals(item.Provider, "Telegram", StringComparison.OrdinalIgnoreCase)).IsEnabled = true;
                vm.NotificationProviderSelections.Single(
                    static item => string.Equals(item.Provider, "CustomWebhook", StringComparison.OrdinalIgnoreCase)).IsEnabled = true;

                vm.SelectedNotificationProvider = "Smtp";
                vm.NotificationProviderParametersText = "server=smtp.example.com\nport=587\nfrom=ops@example.com\nto=dev@example.com";
                vm.SelectedNotificationProvider = "Telegram";
                vm.NotificationProviderParametersText = "botToken=token-1\nchatId=10001";
                vm.SelectedNotificationProvider = "CustomWebhook";
                vm.NotificationProviderParametersText = "url=https://example.com/hook\nheaders=X-Token: 123\nbody={title}";

                await vm.SaveExternalNotificationAsync();

                Assert.Contains("SMTP, Telegram, Custom Webhook", vm.StatusMessage, StringComparison.Ordinal);
                Assert.Contains("已保存", vm.StatusMessage, StringComparison.Ordinal);
                Assert.DoesNotContain("server=smtp.example.com", vm.StatusMessage, StringComparison.Ordinal);
                Assert.Equal(
                    "SMTP,Telegram,Custom Webhook",
                    ReadCurrentProfileString(first.Config, ConfigurationKeys.ExternalNotificationEnabled));
                Assert.Equal(
                    "False",
                    ReadCurrentProfileString(first.Config, ConfigurationKeys.ExternalNotificationSendWhenStalled));
                // Save dual-writes the WPF legacy key so WPF keeps the same toggle state.
                Assert.Equal(
                    "False",
                    ReadCurrentProfileString(first.Config, ConfigurationKeys.ExternalNotificationSendWhenTimeout));
            }

            await using var second = await RuntimeFixture.CreateAsync(
                root,
                cleanupRoot: false,
                notificationProviderFeatureService: new ScriptedNotificationProviderFeatureService
                {
                    ValidateHandler = static _ => UiOperationResult.Ok("valid"),
                });
            var reloaded = new SettingsPageViewModel(second.Runtime, new ConnectionGameSharedStateViewModel());
            await reloaded.InitializeAsync();

            Assert.Contains("server=smtp.example.com", reloaded.StatusMessage, StringComparison.Ordinal);
            Assert.True(reloaded.ExternalNotificationEnabled);
            Assert.True(reloaded.ExternalNotificationSendWhenComplete);
            Assert.True(reloaded.ExternalNotificationSendWhenError);
            Assert.False(reloaded.ExternalNotificationSendWhenStalled);
            Assert.True(reloaded.ExternalNotificationEnableDetails);

            reloaded.SelectedNotificationProvider = "Smtp";
            Assert.Contains("server=smtp.example.com", reloaded.NotificationProviderParametersText, StringComparison.Ordinal);
            reloaded.SelectedNotificationProvider = "Telegram";
            Assert.Contains("chatId=10001", reloaded.NotificationProviderParametersText, StringComparison.Ordinal);
            reloaded.SelectedNotificationProvider = "CustomWebhook";
            Assert.Contains("headers=X-Token: 123", reloaded.NotificationProviderParametersText, StringComparison.Ordinal);
            Assert.Contains("body={title}", reloaded.NotificationProviderParametersText, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // ignore cleanup failures in temporary test directories
            }
        }
    }

    [Fact]
    public async Task ExternalNotification_CustomWebhook_MultiLineHeaders_SaveAndReload_RoundTrips()
    {
        var root = Path.Combine(Path.GetTempPath(), "maa-unified-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var notification = new ScriptedNotificationProviderFeatureService
            {
                ValidateHandler = static _ => UiOperationResult.Ok("valid"),
            };

            await using (var first = await RuntimeFixture.CreateAsync(
                root,
                cleanupRoot: false,
                notificationProviderFeatureService: notification))
            {
                var vm = new SettingsPageViewModel(first.Runtime, new ConnectionGameSharedStateViewModel());
                await vm.InitializeAsync();

                vm.ExternalNotificationEnabled = true;
                vm.NotificationProviderSelections.Single(
                    static item => string.Equals(item.Provider, "CustomWebhook", StringComparison.OrdinalIgnoreCase)).IsEnabled = true;
                vm.SelectedNotificationProvider = "CustomWebhook";
                vm.NotificationProviderParametersText =
                    "url=https://example.com/hook\nheaders=X-Token: 123\nX-Second: abc\nbody={title}";

                await vm.SaveExternalNotificationAsync();

                Assert.True(vm.HasExternalNotificationStatusMessage);
                Assert.False(vm.HasExternalNotificationErrorMessage);
                Assert.Equal(
                    "https://example.com/hook",
                    ReadCurrentProfileString(first.Config, ConfigurationKeys.ExternalNotificationCustomWebhookUrl));
                Assert.Equal(
                    "X-Token: 123\nX-Second: abc",
                    ReadCurrentProfileString(first.Config, "ExternalNotification.CustomWebhook.Headers")
                        .Replace("\r\n", "\n", StringComparison.Ordinal));
                Assert.Equal(
                    "{title}",
                    ReadCurrentProfileString(first.Config, ConfigurationKeys.ExternalNotificationCustomWebhookBody));
            }

            await using var second = await RuntimeFixture.CreateAsync(
                root,
                cleanupRoot: false,
                notificationProviderFeatureService: new ScriptedNotificationProviderFeatureService
                {
                    ValidateHandler = static _ => UiOperationResult.Ok("valid"),
                });
            var reloaded = new SettingsPageViewModel(second.Runtime, new ConnectionGameSharedStateViewModel());
            await reloaded.InitializeAsync();

            reloaded.SelectedNotificationProvider = "CustomWebhook";
            Assert.Contains("headers=X-Token: 123", reloaded.NotificationProviderParametersText, StringComparison.Ordinal);
            Assert.Contains("X-Second: abc", reloaded.NotificationProviderParametersText, StringComparison.Ordinal);
            Assert.Contains("body={title}", reloaded.NotificationProviderParametersText, StringComparison.Ordinal);
            Assert.Equal(
                "X-Token: 123\nX-Second: abc",
                reloaded.CustomWebhookHeaders.Replace("\r\n", "\n", StringComparison.Ordinal));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // ignore cleanup failures in temporary test directories
            }
        }
    }

    [Fact]
    public async Task ExternalNotification_Disabled_SkipsProviderCallsAndSavesEmptyProviderList()
    {
        var notification = new ScriptedNotificationProviderFeatureService
        {
            ValidateHandler = static _ => UiOperationResult.Fail(
                UiErrorCode.NotificationProviderInvalidParameters,
                "bad params"),
            SendHandler = static _ => UiOperationResult.Fail(
                UiErrorCode.NotificationProviderNetworkFailure,
                "network down"),
        };

        await using var fixture = await RuntimeFixture.CreateAsync(notificationProviderFeatureService: notification);
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        vm.ExternalNotificationEnabled = false;
        vm.SelectedNotificationProvider = "Smtp";
        vm.NotificationProviderParametersText = "server";

        await vm.ValidateExternalNotificationParametersAsync();

        Assert.True(vm.HasExternalNotificationErrorMessage);
        Assert.Contains(UiErrorCode.NotificationProviderInvalidParameters, vm.ExternalNotificationErrorMessage, StringComparison.Ordinal);

        await vm.TestExternalNotificationAsync();

        Assert.True(vm.HasExternalNotificationErrorMessage);
        Assert.Contains(UiErrorCode.NotificationProviderInvalidParameters, vm.ExternalNotificationErrorMessage, StringComparison.Ordinal);

        await vm.SaveExternalNotificationAsync();

        Assert.Empty(notification.ValidateCalls);
        Assert.Empty(notification.SendCalls);
        Assert.True(vm.HasExternalNotificationStatusMessage);
        Assert.False(vm.HasExternalNotificationWarningMessage);
        Assert.False(vm.HasExternalNotificationErrorMessage);
        Assert.Equal(string.Empty, ReadCurrentProfileString(fixture.Config, ConfigurationKeys.ExternalNotificationEnabled));
    }

    [Fact]
    public async Task ExternalNotification_LoadLegacyProviderList_PreservesMultiProviderSelection()
    {
        await using var fixture = await RuntimeFixture.CreateAsync(
            notificationProviderFeatureService: new ScriptedNotificationProviderFeatureService
            {
                ValidateHandler = static _ => UiOperationResult.Ok("valid"),
            });
        fixture.Config.CurrentConfig.Profiles[fixture.Config.CurrentConfig.CurrentProfile].Values[ConfigurationKeys.ExternalNotificationEnabled] =
            JsonValue.Create("SMTP,Telegram,Custom Webhook");
        await fixture.Config.SaveAsync();

        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        Assert.True(vm.ExternalNotificationEnabled);
        await vm.SaveExternalNotificationAsync();
        Assert.Equal(
            "SMTP,Telegram,Custom Webhook",
            ReadCurrentProfileString(fixture.Config, ConfigurationKeys.ExternalNotificationEnabled));
    }

    [Fact]
    public async Task ExternalNotification_UncheckedProviderWithStoredParameters_ShouldNotShowOrValidate()
    {
        var notification = new ScriptedNotificationProviderFeatureService
        {
            ValidateHandler = static request =>
                string.Equals(request.Provider, "Smtp", StringComparison.OrdinalIgnoreCase)
                    ? UiOperationResult.Fail(UiErrorCode.NotificationProviderInvalidParameters, "smtp should stay disabled")
                    : UiOperationResult.Ok("valid"),
        };

        await using var fixture = await RuntimeFixture.CreateAsync(notificationProviderFeatureService: notification);
        var profile = fixture.Config.CurrentConfig.Profiles[fixture.Config.CurrentConfig.CurrentProfile];
        profile.Values[ConfigurationKeys.ExternalNotificationEnabled] = JsonValue.Create("Bark");
        profile.Values[ConfigurationKeys.ExternalNotificationBarkServer] = JsonValue.Create("https://api.day.app");
        profile.Values[ConfigurationKeys.ExternalNotificationBarkSendKey] = JsonValue.Create("bark-key");
        profile.Values[ConfigurationKeys.ExternalNotificationSmtpUseSsl] = JsonValue.Create("false");
        profile.Values[ConfigurationKeys.ExternalNotificationSmtpRequiresAuthentication] = JsonValue.Create("false");
        await fixture.Config.SaveAsync();

        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        Assert.True(vm.ExternalNotificationEnabled);
        Assert.True(vm.BarkSectionVisible);
        Assert.False(vm.SmtpSectionVisible);

        await vm.SaveExternalNotificationAsync();

        Assert.True(vm.BarkSectionVisible);
        Assert.False(vm.SmtpSectionVisible);
        Assert.DoesNotContain(notification.ValidateCalls, call => string.Equals(call.Provider, "Smtp", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Bark", ReadCurrentProfileString(fixture.Config, ConfigurationKeys.ExternalNotificationEnabled));
    }

    [Fact]
    public async Task ConfigurationManager_SaveAsNew_TracksInlineSuccessState()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        var newProfileName = $"Alpha-{Guid.NewGuid():N}";
        Assert.DoesNotContain(
            vm.ConfigurationProfiles,
            profile => string.Equals(profile, newProfileName, StringComparison.OrdinalIgnoreCase));
        vm.ConfigurationManagerNewProfileName = newProfileName;

        await vm.AddConfigurationProfileAsync();

        Assert.Contains(vm.ConfigurationProfiles, profile => string.Equals(profile, newProfileName, StringComparison.OrdinalIgnoreCase));
        Assert.True(fixture.Config.CurrentConfig.Profiles.ContainsKey(newProfileName));
        Assert.True(vm.HasConfigurationManagerSaveAsNewSucceeded);
        Assert.Equal("保存成功", vm.ConfigurationManagerSaveAsNewSucceededText);
        Assert.Empty(vm.ConfigurationManagerNewProfileName);

        vm.ConfigurationManagerNewProfileName = "Beta";

        Assert.False(vm.HasConfigurationManagerSaveAsNewSucceeded);
        Assert.Equal(string.Empty, vm.ConfigurationManagerSaveAsNewSucceededText);
    }

    [Fact]
    public async Task ConfigurationManager_SaveAsNew_DuplicateProfile_ShowsInlineFailureAndKeepsInput()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        const string existingProfileName = "Default";
        vm.ConfigurationManagerNewProfileName = existingProfileName;

        await vm.AddConfigurationProfileAsync();

        Assert.False(vm.HasConfigurationManagerSaveAsNewSucceeded);
        Assert.Equal(string.Empty, vm.ConfigurationManagerSaveAsNewSucceededText);
        Assert.True(vm.HasConfigurationManagerSaveAsNewFailed);
        Assert.Equal("保存失败：请换一个未使用的配置名称。", vm.ConfigurationManagerSaveAsNewFailedText);
        Assert.Equal(existingProfileName, vm.ConfigurationManagerNewProfileName);

        vm.ConfigurationManagerNewProfileName = $"{existingProfileName}-copy";

        Assert.False(vm.HasConfigurationManagerSaveAsNewFailed);
        Assert.Equal(string.Empty, vm.ConfigurationManagerSaveAsNewFailedText);
    }

    private static async Task<string> RunExternalFailureCaseAsync(
        Func<NotificationProviderTestRequest, UiOperationResult> handler,
        string method)
    {
        var notification = new ScriptedNotificationProviderFeatureService
        {
            ValidateHandler = static _ => UiOperationResult.Ok("valid"),
            SendHandler = handler,
        };

        await using var fixture = await RuntimeFixture.CreateAsync(notificationProviderFeatureService: notification);
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        vm.ExternalNotificationEnabled = true;
        vm.SelectedNotificationProvider = "CustomWebhook";
        vm.NotificationProviderParametersText = "url=https://webhook.example.com";
        if (string.Equals(method, "validate", StringComparison.OrdinalIgnoreCase))
        {
            notification.ValidateHandler = static _ =>
                UiOperationResult.Fail(UiErrorCode.NotificationProviderInvalidParameters, "bad params");
            await vm.ValidateExternalNotificationParametersAsync();
        }
        else
        {
            await vm.TestExternalNotificationAsync();
        }

        return vm.HasExternalNotificationWarningMessage
            ? vm.ExternalNotificationWarningMessage
            : vm.ExternalNotificationErrorMessage;
    }

    private static NotificationProviderFeatureService CreateNotificationProviderServiceForHttpProbe(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendHttpAsync)
    {
        var ctor = typeof(NotificationProviderFeatureService).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(bool), typeof(Func<string, IReadOnlyDictionary<string, string>, string, string, CancellationToken, Task<UiOperationResult>>), typeof(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>)],
            modifiers: null);
        Assert.NotNull(ctor);
        return (NotificationProviderFeatureService)ctor!.Invoke([true, null, sendHttpAsync]);
    }

    private static string ReadCurrentProfileString(UnifiedConfigurationService config, string key)
    {
        if (!string.IsNullOrWhiteSpace(config.CurrentConfig.CurrentProfile)
            && config.CurrentConfig.Profiles.TryGetValue(config.CurrentConfig.CurrentProfile, out var profile)
            && profile.Values.TryGetValue(key, out var node))
        {
            return node?.ToString() ?? string.Empty;
        }

        return config.CurrentConfig.GlobalValues.TryGetValue(key, out var fallbackNode)
            ? fallbackNode?.ToString() ?? string.Empty
            : string.Empty;
    }

    [Fact]
    public async Task ExternalNotification_Smtp_TitleWithNewline_StrippedBeforeMailMessage()
    {
        // WPF frozen SmtpNotificationProvider strips CR/LF from the title before assigning the
        // mail subject; System.Net.Mail.MailMessage rejects newline subjects with
        // ArgumentException before any network I/O, so an unreachable server must surface a
        // socket-level failure instead of an ArgumentException.
        var service = new NotificationProviderFeatureService();
        var result = await service.SendTestAsync(new NotificationProviderTestRequest(
            "Smtp",
            "server=127.0.0.1\nport=9\nfrom=noreply@example.com\nto=ops@example.com",
            "\u4efb\u52a1\u5df2\u5168\u90e8\u5b8c\u6210\uff01\n(\u7528\u65f6 0h 0m 0s)",
            "content"));

        Assert.False(result.Success);
        Assert.Equal(UiErrorCode.NotificationProviderNetworkFailure, result.Error?.Code);
        Assert.DoesNotContain("errorType=ArgumentException", result.Error?.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExternalNotification_ServerChan_MalformedSctpKey_ReturnsClassifiedFailure()
    {
        // WPF frozen ServerChanNotificationProvider.ConstructUrl throws ArgumentException for an
        // `sctp`-prefixed key that does not match `^sctp(\d+)t`, but the provider catches it and
        // reports a failed send; MAAUnified must classify the failure instead of throwing.
        var httpCalled = false;
        var service = CreateNotificationProviderServiceForHttpProbe((_, _) =>
        {
            httpCalled = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var result = await service.SendTestAsync(new NotificationProviderTestRequest(
            "ServerChan",
            "sendKey=sctpABC",
            "title",
            "message"));

        Assert.False(result.Success);
        Assert.Equal(UiErrorCode.NotificationProviderFailed, result.Error?.Code);
        Assert.Contains("errorType=ArgumentException", result.Error?.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("sctpABC", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sctpABC", result.Error?.Details ?? string.Empty, StringComparison.Ordinal);
        Assert.False(httpCalled);
    }

    [Fact]
    public async Task ExternalNotification_SendAsync_UnexpectedException_ReturnsClassifiedFailure()
    {
        // The injected send delegate stands in for any provider construction-phase failure
        // (malformed ServerChan sctp key, header/URI construction, DingTalk signing): the
        // send path must classify unexpected exceptions instead of throwing.
        var ctor = typeof(NotificationProviderFeatureService).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(bool), typeof(Func<string, IReadOnlyDictionary<string, string>, string, string, CancellationToken, Task<UiOperationResult>>), typeof(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>)],
            modifiers: null);
        Assert.NotNull(ctor);
        var service = (NotificationProviderFeatureService)ctor!.Invoke([
            true,
            (Func<string, IReadOnlyDictionary<string, string>, string, string, CancellationToken, Task<UiOperationResult>>)(
                (_, _, _, _, _) => throw new InvalidOperationException("boom")),
            null,
        ]);

        var result = await service.SendTestAsync(new NotificationProviderTestRequest(
            "ServerChan",
            "sendKey=abc123",
            "title",
            "message"));

        Assert.False(result.Success);
        Assert.Equal(UiErrorCode.NotificationProviderFailed, result.Error?.Code);
        Assert.Contains("errorType=InvalidOperationException", result.Error?.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExternalNotification_TestSend_ThrowingProvider_DoesNotAbortRemainingProviders()
    {
        // WPF frozen ExternalNotificationService wraps each provider send in a per-provider
        // try/catch and continues the loop; a single throwing provider must not abort the rest.
        var notification = new ScriptedNotificationProviderFeatureService
        {
            SendHandler = static request => string.Equals(request.Provider, "ServerChan", StringComparison.OrdinalIgnoreCase)
                ? throw new ArgumentException("Invalid key format for sctp.")
                : UiOperationResult.Ok("sent"),
        };

        await using var fixture = await RuntimeFixture.CreateAsync(notificationProviderFeatureService: notification);
        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        vm.NotificationProviderSelections.Single(
            static item => string.Equals(item.Provider, "ServerChan", StringComparison.OrdinalIgnoreCase)).IsEnabled = true;
        vm.NotificationProviderSelections.Single(
            static item => string.Equals(item.Provider, "Telegram", StringComparison.OrdinalIgnoreCase)).IsEnabled = true;

        await vm.TestExternalNotificationAsync();

        Assert.Equal(2, notification.SendCalls.Count);
        Assert.Contains(notification.SendCalls, static call => call.Provider == "ServerChan");
        Assert.Contains(notification.SendCalls, static call => call.Provider == "Telegram");
        Assert.True(vm.HasExternalNotificationErrorMessage);
        Assert.Contains("ServerChan", vm.ExternalNotificationErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExternalNotification_EnableDetails_IndependentOfSendWhenComplete_RoundTrips()
    {
        var root = Path.Combine(Path.GetTempPath(), "maa-unified-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using (var first = await RuntimeFixture.CreateAsync(
                root,
                cleanupRoot: false,
                notificationProviderFeatureService: new ScriptedNotificationProviderFeatureService
                {
                    ValidateHandler = static _ => UiOperationResult.Ok("valid"),
                }))
            {
                // Seed a legacy WPF-style config: SendWhenComplete=false + EnableDetails=true.
                var profile = first.Config.CurrentConfig.Profiles[first.Config.CurrentConfig.CurrentProfile];
                profile.Values[ConfigurationKeys.ExternalNotificationEnabled] = JsonValue.Create("SMTP");
                profile.Values[ConfigurationKeys.ExternalNotificationSendWhenComplete] = JsonValue.Create("False");
                profile.Values[ConfigurationKeys.ExternalNotificationEnableDetails] = JsonValue.Create("True");
                await first.Config.SaveAsync();

                var vm = new SettingsPageViewModel(first.Runtime, new ConnectionGameSharedStateViewModel());
                await vm.InitializeAsync();

                Assert.False(vm.ExternalNotificationSendWhenComplete);
                Assert.True(vm.ExternalNotificationEnableDetails);

                // Toggling SendWhenComplete must not clear the stored EnableDetails value.
                vm.ExternalNotificationSendWhenComplete = true;
                Assert.True(vm.ExternalNotificationEnableDetails);
                vm.ExternalNotificationSendWhenComplete = false;
                Assert.True(vm.ExternalNotificationEnableDetails);

                await vm.SaveExternalNotificationAsync();

                Assert.Equal(
                    "False",
                    ReadCurrentProfileString(first.Config, ConfigurationKeys.ExternalNotificationSendWhenComplete));
                Assert.Equal(
                    "True",
                    ReadCurrentProfileString(first.Config, ConfigurationKeys.ExternalNotificationEnableDetails));
            }

            await using var second = await RuntimeFixture.CreateAsync(
                root,
                cleanupRoot: false,
                notificationProviderFeatureService: new ScriptedNotificationProviderFeatureService
                {
                    ValidateHandler = static _ => UiOperationResult.Ok("valid"),
                });
            var reloaded = new SettingsPageViewModel(second.Runtime, new ConnectionGameSharedStateViewModel());
            await reloaded.InitializeAsync();

            Assert.False(reloaded.ExternalNotificationSendWhenComplete);
            Assert.True(reloaded.ExternalNotificationEnableDetails);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // ignore cleanup failures in temporary test directories
            }
        }
    }

    [Fact]
    public async Task ExternalNotification_LegacyDiscordWebhook_LoadsAsCustomWebhookChannel()
    {
        await using var fixture = await RuntimeFixture.CreateAsync(
            notificationProviderFeatureService: new ScriptedNotificationProviderFeatureService());
        if (fixture.Config.TryGetCurrentProfile(out var profile))
        {
            profile.Values[ConfigurationKeys.ExternalNotificationEnabled] = JsonValue.Create("Discord Webhook");
            profile.Values[ConfigurationKeys.ExternalNotificationDiscordWebhookUrl] =
                JsonValue.Create("https://discord.example.com/api/webhooks/legacy");
        }

        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();

        Assert.True(vm.ExternalNotificationEnabled);
        Assert.Equal("CustomWebhook", vm.SelectedNotificationProvider);
        Assert.Equal("https://discord.example.com/api/webhooks/legacy", vm.CustomWebhookUrl);
        Assert.Equal("{\"content\": \"{content}\"}", vm.CustomWebhookBody);
        Assert.Contains(
            vm.NotificationProviderSelections,
            static item => item.Provider == "CustomWebhook" && item.IsEnabled);
        Assert.DoesNotContain(
            vm.NotificationProviderSelections,
            static item => string.Equals(item.Provider, "DiscordWebhook", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExternalNotification_LegacyDiscordWebhook_SaveWritesCustomWebhookKeysOnly()
    {
        await using var fixture = await RuntimeFixture.CreateAsync(
            notificationProviderFeatureService: new ScriptedNotificationProviderFeatureService
            {
                ValidateHandler = static _ => UiOperationResult.Ok("valid"),
            });
        if (fixture.Config.TryGetCurrentProfile(out var profile))
        {
            profile.Values[ConfigurationKeys.ExternalNotificationEnabled] = JsonValue.Create("Discord Webhook");
            profile.Values[ConfigurationKeys.ExternalNotificationDiscordWebhookUrl] =
                JsonValue.Create("https://discord.example.com/api/webhooks/legacy");
            profile.Values[ConfigurationKeys.ExternalNotificationCustomWebhookBody] =
                JsonValue.Create("{\"content\": \"{content}\"}");
        }

        var vm = new SettingsPageViewModel(fixture.Runtime, new ConnectionGameSharedStateViewModel());
        await vm.InitializeAsync();
        Assert.Equal("CustomWebhook", vm.SelectedNotificationProvider);
        Assert.Contains("url=https://discord.example.com/api/webhooks/legacy", vm.NotificationProviderParametersText, StringComparison.Ordinal);
        vm.ExternalNotificationEnabled = true;
        await vm.SaveExternalNotificationAsync();

        Assert.False(vm.HasExternalNotificationErrorMessage);
        Assert.Equal("https://discord.example.com/api/webhooks/legacy", vm.CustomWebhookUrl);
        Assert.Equal(
            "CustomWebhook",
            vm.SelectedNotificationProvider);
        Assert.Equal(
            "Custom Webhook",
            ReadCurrentProfileString(fixture.Config, ConfigurationKeys.ExternalNotificationEnabled));
        Assert.Equal(
            "https://discord.example.com/api/webhooks/legacy",
            ReadCurrentProfileString(fixture.Config, ConfigurationKeys.ExternalNotificationCustomWebhookUrl));
        Assert.Equal(
            "{\"content\": \"{content}\"}",
            ReadCurrentProfileString(fixture.Config, ConfigurationKeys.ExternalNotificationCustomWebhookBody));
    }

    [Theory]
    [InlineData("zh-cn", "MAA \u5916\u90e8\u901a\u77e5\u6d4b\u8bd5", "\u8fd9\u662f MAA \u5916\u90e8\u901a\u77e5\u6d4b\u8bd5\u4fe1\u606f\u3002\u5982\u679c\u4f60\u770b\u5230\u4e86\u8fd9\u6bb5\u5185\u5bb9\uff0c\u5c31\u8bf4\u660e\u901a\u77e5\u53d1\u9001\u6210\u529f\u4e86\uff01")]
    [InlineData("en-us", "MAA external notification test", "This is MAA external notification test information. If you see this content, the notification was sent successfully!")]
    [InlineData("zh-tw", "MAA \u5916\u90e8\u901a\u77e5\u6e2c\u8a66", "\u9019\u662f MAA \u5916\u90e8\u901a\u77e5\u6e2c\u8a66\u8cc7\u8a0a\u3002\u5982\u679c\u60a8\u770b\u5230\u4e86\u9019\u6bb5\u5167\u5bb9\uff0c\u5c31\u8aaa\u660e\u901a\u77e5\u767c\u9001\u6210\u529f\u4e86\uff01")]
    [InlineData("ja-jp", "MAA \u5916\u90e8\u901a\u77e5\u30c6\u30b9\u30c8", "\u3053\u308c\u306f MAA \u5916\u90e8\u901a\u77e5\u30c6\u30b9\u30c8\u60c5\u5831\u3067\u3059\u3002 \u3053\u306e\u30b3\u30f3\u30c6\u30f3\u30c4\u304c\u8868\u793a\u3055\u308c\u305f\u5834\u5408\u3001\u901a\u77e5\u306f\u6b63\u5e38\u306b\u9001\u4fe1\u3055\u308c\u307e\u3057\u305f\uff01")]
    [InlineData("ko-kr", "MAA \uc678\ubd80 \uc54c\ub9bc \ud14c\uc2a4\ud2b8", "MAA \uc678\ubd80 \uc54c\ub9bc \ud14c\uc2a4\ud2b8\uc785\ub2c8\ub2e4. \uc774 \uba54\uc2dc\uc9c0\uac00 \ud45c\uc2dc\ub418\uc5c8\ub2e4\uba74 \uc54c\ub9bc \uc804\uc1a1\uc774 \uc131\uacf5\ud55c \uac83\uc785\ub2c8\ub2e4!")]
    public void ExternalNotification_DefaultTestTitleAndMessage_MatchWpfFrozen(
        string language,
        string expectedTitle,
        string expectedMessage)
    {
        var manager = UiLocalizationResourceManager.Shared;
        Assert.Equal(expectedTitle, manager.Lookup(language, "Settings.ExternalNotification.DefaultTitle").Value);
        Assert.Equal(expectedMessage, manager.Lookup(language, "Settings.ExternalNotification.DefaultMessage").Value);
    }

    private sealed class RuntimeFixture : IAsyncDisposable
    {
        private readonly bool _cleanupRoot;

        private RuntimeFixture(
            string root,
            MAAUnifiedRuntime runtime,
            UnifiedConfigurationService config,
            UiDiagnosticsService diagnostics,
            bool cleanupRoot)
        {
            Root = root;
            Runtime = runtime;
            Config = config;
            Diagnostics = diagnostics;
            _cleanupRoot = cleanupRoot;
        }

        public string Root { get; }

        public MAAUnifiedRuntime Runtime { get; }

        public UnifiedConfigurationService Config { get; }

        public UiDiagnosticsService Diagnostics { get; }

        public static async Task<RuntimeFixture> CreateAsync(
            string? root = null,
            bool cleanupRoot = true,
            IRemoteControlFeatureService? remoteControlFeatureService = null,
            INotificationProviderFeatureService? notificationProviderFeatureService = null)
        {
            root ??= Path.Combine(Path.GetTempPath(), "maa-unified-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "config"));

            var log = new UiLogService();
            var diagnostics = new UiDiagnosticsService(root, log);
            var config = new UnifiedConfigurationService(
                new AvaloniaJsonConfigStore(root),
                new GuiNewJsonConfigImporter(),
                new GuiJsonConfigImporter(),
                log,
                root);
            await config.LoadOrBootstrapAsync();

            var bridge = new FakeBridge();
            var session = new UnifiedSessionService(bridge, config, log, new SessionStateMachine());
            var platform = new PlatformServiceBundle
            {
                TrayService = new NoOpTrayService(),
                NotificationService = new NoOpNotificationService(),
                HotkeyService = new NoOpGlobalHotkeyService(),
                AutostartService = new NoOpAutostartService(),
                FileDialogService = new NoOpFileDialogService(),
                OverlayService = new NoOpOverlayCapabilityService(),
                PostActionExecutorService = new NoOpPostActionExecutorService(),
            };

            var capability = new PlatformCapabilityFeatureService(platform, diagnostics);
            var connect = new ConnectFeatureService(session, config);
            var runtime = new MAAUnifiedRuntime
            {
                CoreBridge = bridge,
                ConfigurationService = config,
                ResourceWorkflowService = new ResourceWorkflowService(root, bridge, log),
                SessionService = session,
                Platform = platform,
                LogService = log,
                DiagnosticsService = diagnostics,
                ConnectFeatureService = connect,
                ShellFeatureService = new ShellFeatureService(connect),
                TaskQueueFeatureService = new TaskQueueFeatureService(session, config),
                CopilotFeatureService = new CopilotFeatureService(),
                ToolboxFeatureService = new ToolboxFeatureService(),
                RemoteControlFeatureService = remoteControlFeatureService ?? new RemoteControlFeatureService(),
                PlatformCapabilityService = capability,
                OverlayFeatureService = new OverlayFeatureService(capability),
                NotificationProviderFeatureService = notificationProviderFeatureService ?? new NotificationProviderFeatureService(),
                SettingsFeatureService = new SettingsFeatureService(config, capability, diagnostics),
                ConfigurationProfileFeatureService = new ConfigurationProfileFeatureService(config),
                DialogFeatureService = new DialogFeatureService(diagnostics),
                PostActionFeatureService = new PostActionFeatureService(
                    config,
                    diagnostics,
                    platform.PostActionExecutorService),
            };

            return new RuntimeFixture(root, runtime, config, diagnostics, cleanupRoot);
        }

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            if (!_cleanupRoot)
            {
                return;
            }

            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // ignore cleanup failures in temporary test directories
            }
        }
    }

    private sealed class ScriptedRemoteControlFeatureService : IRemoteControlFeatureService
    {
        public Func<RemoteControlConnectivityRequest, UiOperationResult<RemoteControlConnectivityResult>>? TestHandler { get; set; }

        public List<RemoteControlConnectivityRequest> TestCalls { get; } = [];

        public Task<CoreResult<bool>> StartRemotePollingAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<bool>.Ok(true));
        }

        public Task<UiOperationResult<RemoteControlConnectivityResult>> TestConnectivityAsync(
            RemoteControlConnectivityRequest request,
            CancellationToken cancellationToken = default)
        {
            TestCalls.Add(request);
            var result = TestHandler?.Invoke(request)
                ?? UiOperationResult<RemoteControlConnectivityResult>.Ok(
                    new RemoteControlConnectivityResult(
                        request.PollIntervalMs,
                        new EndpointProbeResult("GetTask", request.GetTaskEndpoint, true, 200, "HTTP 200"),
                        new EndpointProbeResult("Report", request.ReportEndpoint, true, 200, "HTTP 200")),
                    "ok");
            return Task.FromResult(result);
        }
    }

    private sealed class ScriptedNotificationProviderFeatureService : INotificationProviderFeatureService
    {
        public string[] Providers { get; set; } =
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

        public Func<NotificationProviderRequest, UiOperationResult>? ValidateHandler { get; set; }

        public Func<NotificationProviderTestRequest, UiOperationResult>? SendHandler { get; set; }

        public List<NotificationProviderRequest> ValidateCalls { get; } = [];

        public List<NotificationProviderTestRequest> SendCalls { get; } = [];

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
            ValidateCalls.Add(request);
            var result = ValidateHandler?.Invoke(request)
                ?? UiOperationResult.Ok("valid");
            return Task.FromResult(result);
        }

        public Task<UiOperationResult> SendTestAsync(
            NotificationProviderTestRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SendCalls.Add(request);
            var result = SendHandler?.Invoke(request)
                ?? UiOperationResult.Ok("sent");
            return Task.FromResult(result);
        }
    }

    private sealed class FakeBridge : IMaaCoreBridge
    {
        public Task<CoreResult<CoreInitializeInfo>> InitializeAsync(
            CoreInitializeRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<CoreInitializeInfo>.Ok(new CoreInitializeInfo(request.BaseDirectory, "fake", "fake", request.ClientType)));
        }

        public Task<CoreResult<bool>> ConnectAsync(CoreConnectionInfo connectionInfo, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<bool>.Ok(true));
        }

        public Task<CoreResult<int>> AppendTaskAsync(CoreTaskRequest task, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<int>.Ok(1));
        }

        public Task<CoreResult<bool>> StartAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<bool>.Ok(true));
        }

        public Task<CoreResult<bool>> StopAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<bool>.Ok(true));
        }

        public Task<CoreResult<CoreRuntimeStatus>> GetRuntimeStatusAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<CoreRuntimeStatus>.Ok(new CoreRuntimeStatus(true, true, false)));
        }

        public Task<CoreResult<bool>> AttachWindowAsync(CoreAttachWindowRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.NotSupported, "not supported")));
        }

        public Task<CoreResult<byte[]>> GetImageAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<byte[]>.Fail(new CoreError(CoreErrorCode.GetImageFailed, "not supported")));
        }

        public async IAsyncEnumerable<CoreCallbackEvent> CallbackStreamAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
