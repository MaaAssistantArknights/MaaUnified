using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using MAAUnified.App.ViewModels.Toolbox;
using MAAUnified.Application.Models;
using MAAUnified.Application.Services.Features;
using MAAUnified.CoreBridge;
using MAAUnified.Compat.Constants;

namespace MAAUnified.Tests;

public sealed class ToolboxExternalNotificationTests
{
    [Fact]
    public async Task TaskChainError_SendsExternalNotification_WhenSendWhenErrorEnabled()
    {
        var provider = new ScriptedNotificationProviderFeatureService();
        await using var fixture = await ToolboxTestFixture.CreateAsync(
            profileSeeds: new Dictionary<string, JsonNode?>
            {
                [ConfigurationKeys.ExternalNotificationEnabled] = JsonValue.Create("Bark"),
                [ConfigurationKeys.ExternalNotificationSendWhenError] = JsonValue.Create(true),
                [ConfigurationKeys.ExternalNotificationBarkSendKey] = JsonValue.Create("bark-key"),
                [ConfigurationKeys.ExternalNotificationBarkServer] = JsonValue.Create("https://api.day.app"),
            },
            notificationProviderFeatureService: provider);
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);
        await vm.InitializeAsync();

        await vm.StartRecruitAsync();
        vm.ApplyRuntimeCallback(CreateCallback(
            "TaskChainError",
            new JsonObject
            {
                ["taskchain"] = "Recruit",
            }));

        await WaitForExternalNotificationCountAsync(provider, 1);
        var call = Assert.Single(provider.SendCalls);
        Assert.Equal("Bark", call.Provider);
        Assert.Contains("sendKey=bark-key", call.ParametersText, StringComparison.Ordinal);
        Assert.Contains("server=https://api.day.app", call.ParametersText, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(call.Title));
        Assert.Contains("Recruit", call.Title, StringComparison.Ordinal);
        Assert.Equal(call.Title, call.Message);
        Assert.Equal(ToolboxExecutionState.Failed, vm.ExecutionState);
    }

    [Fact]
    public async Task TaskChainError_SkipsExternalNotification_WhenSendWhenErrorDisabled()
    {
        var provider = new ScriptedNotificationProviderFeatureService();
        await using var fixture = await ToolboxTestFixture.CreateAsync(
            profileSeeds: new Dictionary<string, JsonNode?>
            {
                [ConfigurationKeys.ExternalNotificationEnabled] = JsonValue.Create("Bark"),
                [ConfigurationKeys.ExternalNotificationSendWhenError] = JsonValue.Create(false),
                [ConfigurationKeys.ExternalNotificationBarkSendKey] = JsonValue.Create("bark-key"),
                [ConfigurationKeys.ExternalNotificationBarkServer] = JsonValue.Create("https://api.day.app"),
            },
            notificationProviderFeatureService: provider);
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);
        await vm.InitializeAsync();

        await vm.StartRecruitAsync();
        vm.ApplyRuntimeCallback(CreateCallback(
            "TaskChainError",
            new JsonObject
            {
                ["taskchain"] = "Recruit",
            }));

        Assert.Equal(ToolboxExecutionState.Failed, vm.ExecutionState);
        await Task.Delay(500);
        Assert.Empty(provider.SendCalls);
    }

    [Fact]
    public async Task TaskChainError_SkipsExternalNotification_WhenNoProviderEnabled()
    {
        var provider = new ScriptedNotificationProviderFeatureService();
        await using var fixture = await ToolboxTestFixture.CreateAsync(
            profileSeeds: new Dictionary<string, JsonNode?>
            {
                [ConfigurationKeys.ExternalNotificationSendWhenError] = JsonValue.Create(true),
            },
            notificationProviderFeatureService: provider);
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);
        await vm.InitializeAsync();

        await vm.StartRecruitAsync();
        vm.ApplyRuntimeCallback(CreateCallback(
            "TaskChainError",
            new JsonObject
            {
                ["taskchain"] = "Recruit",
            }));

        Assert.Equal(ToolboxExecutionState.Failed, vm.ExecutionState);
        await Task.Delay(500);
        Assert.Empty(provider.SendCalls);
    }

    [Fact]
    public async Task TaskChainCompleted_SkipsExternalNotification_EvenWhenSendWhenErrorEnabled()
    {
        var provider = new ScriptedNotificationProviderFeatureService();
        await using var fixture = await ToolboxTestFixture.CreateAsync(
            profileSeeds: new Dictionary<string, JsonNode?>
            {
                [ConfigurationKeys.ExternalNotificationEnabled] = JsonValue.Create("Bark"),
                [ConfigurationKeys.ExternalNotificationSendWhenError] = JsonValue.Create(true),
                [ConfigurationKeys.ExternalNotificationBarkSendKey] = JsonValue.Create("bark-key"),
                [ConfigurationKeys.ExternalNotificationBarkServer] = JsonValue.Create("https://api.day.app"),
            },
            notificationProviderFeatureService: provider);
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);
        await vm.InitializeAsync();

        await vm.StartRecruitAsync();
        vm.ApplyRuntimeCallback(CreateCallback(
            "TaskChainCompleted",
            new JsonObject
            {
                ["taskchain"] = "Recruit",
            }));

        Assert.Equal(ToolboxExecutionState.Succeeded, vm.ExecutionState);
        await Task.Delay(500);
        Assert.Empty(provider.SendCalls);
    }

    [Fact]
    public async Task TaskChainError_ExternalNotificationFailure_RaisesErrorDialogFeedback()
    {
        // WPF frozen 23ddd271 (ExternalNotificationService.SendAsync) toasts failed
        // automatic sends; MAAUnified mirrors the main queue's error-dialog feedback
        // for toolbox sends instead of leaving only a warn log line.
        var provider = new ScriptedNotificationProviderFeatureService
        {
            SendHandler = static _ => UiOperationResult.Fail(
                UiErrorCode.NotificationProviderNetworkFailure,
                "network down"),
        };
        await using var fixture = await ToolboxTestFixture.CreateAsync(
            profileSeeds: new Dictionary<string, JsonNode?>
            {
                [ConfigurationKeys.ExternalNotificationEnabled] = JsonValue.Create("Bark"),
                [ConfigurationKeys.ExternalNotificationSendWhenError] = JsonValue.Create(true),
                [ConfigurationKeys.ExternalNotificationBarkSendKey] = JsonValue.Create("bark-key"),
                [ConfigurationKeys.ExternalNotificationBarkServer] = JsonValue.Create("https://api.day.app"),
            },
            notificationProviderFeatureService: provider);
        var raised = new ConcurrentQueue<DialogErrorRaisedEvent>();
        fixture.Runtime.DialogFeatureService.ErrorRaised += (_, e) => raised.Enqueue(e);
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);
        await vm.InitializeAsync();

        await vm.StartRecruitAsync();
        vm.ApplyRuntimeCallback(CreateCallback(
            "TaskChainError",
            new JsonObject
            {
                ["taskchain"] = "Recruit",
            }));

        Assert.True(await WaitForConditionAsync(() => provider.SendCalls.Count > 0));
        Assert.True(await WaitForConditionAsync(
            () => raised.Any(e => string.Equals(e.Context, "Toolbox.Notification.Error", StringComparison.Ordinal))));
        Assert.True(raised.TryPeek(out var failure));
        Assert.Equal(UiErrorCode.NotificationProviderNetworkFailure, failure!.Result.Error?.Code);
    }

    private static CoreCallbackEvent CreateCallback(string msgName, JsonObject? payload = null)
    {
        return new CoreCallbackEvent(0, msgName, (payload ?? new JsonObject()).ToJsonString(), DateTimeOffset.Now);
    }

    private static async Task<bool> WaitForConditionAsync(
        Func<bool> condition,
        int timeoutMs = 2000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return condition();
    }

    private static async Task WaitForExternalNotificationCountAsync(
        ScriptedNotificationProviderFeatureService service,
        int expectedCount,
        int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (service.SendCalls.Count != expectedCount && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        if (service.SendCalls.Count != expectedCount)
        {
            throw new TimeoutException(
                $"Expected external notification count {expectedCount}, but saw {service.SendCalls.Count}.");
        }
    }

    private sealed class ScriptedNotificationProviderFeatureService : INotificationProviderFeatureService
    {
        public List<NotificationProviderTestRequest> SendCalls { get; } = [];

        public Func<NotificationProviderTestRequest, UiOperationResult>? SendHandler { get; set; }

        public Task<string[]> GetAvailableProvidersAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new[]
            {
                "Smtp",
                "ServerChan",
                "Bark",
                "Discord",
                "DingTalk",
                "Telegram",
                "Qmsg",
                "Gotify",
                "CustomWebhook",
            });
        }

        public Task<UiOperationResult> ValidateProviderParametersAsync(
            NotificationProviderRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(UiOperationResult.Ok("valid"));
        }

        public Task<UiOperationResult> SendTestAsync(
            NotificationProviderTestRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SendCalls.Add(request);
            return Task.FromResult(SendHandler?.Invoke(request) ?? UiOperationResult.Ok("sent"));
        }
    }
}
