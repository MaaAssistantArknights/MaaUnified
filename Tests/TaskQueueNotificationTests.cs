using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using MAAUnified.App.ViewModels;
using MAAUnified.App.ViewModels.Settings;
using MAAUnified.App.ViewModels.TaskQueue;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using MAAUnified.Application.Services.Localization;
using MAAUnified.CoreBridge;
using MAAUnified.Compat.Constants;
using MAAUnified.Platform;
using Xunit;

namespace MAAUnified.Tests;

[Collection("MainShellSerial")]
public sealed class TaskQueueNotificationTests
{
    [Fact]
    public void SanityReminder_IsScheduledSixMinutesBeforeRecovery()
    {
        var now = DateTimeOffset.Parse("2026-07-26T10:00:00+08:00");
        var reportTime = now;

        var delay = TaskQueuePageViewModel.CalculateSanityReminderDelay(reportTime, 0, 10, now);

        Assert.Equal(TimeSpan.FromMinutes(54), delay);
    }

    [Fact]
    public void SanityReminder_IsNotScheduledWhenRecoveryIsWithinSixMinutes()
    {
        var now = DateTimeOffset.Parse("2026-07-26T10:00:00+08:00");

        var delay = TaskQueuePageViewModel.CalculateSanityReminderDelay(now, 9, 10, now);

        Assert.Null(delay);
    }

    [Fact]
    public async Task AllTasksCompleted_SendsNotification_WhenUseNotifyEnabled()
    {
        await using var fixture = await NotificationFixture.CreateAsync(useNotify: true);
        await fixture.ViewModel.InitializeAsync();

        var payload = new JsonObject
        {
            ["RunId"] = "completion-run",
            ["finished_tasks"] = new JsonArray(1),
        };
        fixture.MapCoreTaskId(1, 0);
        await NotificationFixture.InvokeCallbackAsync(
            fixture.ViewModel,
            new CoreCallbackEvent(0, "AllTasksCompleted", payload.ToJsonString(), DateTimeOffset.UtcNow));

        await WaitForNotificationCountAsync(fixture.NotificationCapability, 1);
        Assert.Contains("completed", fixture.NotificationCapability.LastTitle, StringComparison.OrdinalIgnoreCase);
        Assert.True(fixture.NotificationCapability.LastNotification?.UseSystemNotification);
    }

    [Fact]
    public async Task AllTasksCompleted_UsesInAppFallback_WhenUseNotifyDisabled()
    {
        await using var fixture = await NotificationFixture.CreateAsync(useNotify: false);
        await fixture.ViewModel.InitializeAsync();

        var payload = new JsonObject
        {
            ["RunId"] = "completion-run",
            ["finished_tasks"] = new JsonArray(1),
        };
        fixture.MapCoreTaskId(1, 0);
        await NotificationFixture.InvokeCallbackAsync(
            fixture.ViewModel,
            new CoreCallbackEvent(0, "AllTasksCompleted", payload.ToJsonString(), DateTimeOffset.UtcNow));

        await WaitForNotificationCountAsync(fixture.NotificationCapability, 1);
        Assert.False(fixture.NotificationCapability.LastNotification?.UseSystemNotification);
    }

    [Fact]
    public async Task TaskChainError_SendsNotification_WhenUseNotifyEnabled()
    {
        await using var fixture = await NotificationFixture.CreateAsync(useNotify: true);
        await fixture.ViewModel.InitializeAsync();

        var payload = new JsonObject
        {
            ["run_id"] = "error-run",
            ["task_chain"] = "Fight",
        };
        await NotificationFixture.InvokeCallbackAsync(
            fixture.ViewModel,
            new CoreCallbackEvent(0, "TaskChainError", payload.ToJsonString(), DateTimeOffset.UtcNow));

        await WaitForNotificationCountAsync(fixture.NotificationCapability, 1);
        Assert.Contains("Task error", fixture.NotificationCapability.LastTitle, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, fixture.NotificationCapability.LastMessage);
    }

    [Fact]
    public async Task TaskChainError_SendsExternalNotification_WhenConfigured()
    {
        var notificationProvider = new ScriptedNotificationProviderFeatureService();
        await using var fixture = await NotificationFixture.CreateAsync(
            useNotify: false,
            notificationProviderFeatureService: notificationProvider);
        if (fixture.Config.TryGetCurrentProfile(out var profile))
        {
            profile.Values[ConfigurationKeys.ExternalNotificationEnabled] = JsonValue.Create("Bark");
            profile.Values[ConfigurationKeys.ExternalNotificationSendWhenError] = JsonValue.Create(true);
            profile.Values[ConfigurationKeys.ExternalNotificationBarkSendKey] = JsonValue.Create("bark-key");
            profile.Values[ConfigurationKeys.ExternalNotificationBarkServer] = JsonValue.Create("https://api.day.app");
        }

        await fixture.ViewModel.InitializeAsync();

        var payload = new JsonObject
        {
            ["run_id"] = "external-error-run",
            ["task_chain"] = "Fight",
        };
        await NotificationFixture.InvokeCallbackAsync(
            fixture.ViewModel,
            new CoreCallbackEvent(0, "TaskChainError", payload.ToJsonString(), DateTimeOffset.UtcNow));

        await WaitForExternalNotificationCountAsync(notificationProvider, 1);
        Assert.Equal(1, fixture.NotificationCapability.NotificationCallCount);
        Assert.False(fixture.NotificationCapability.LastNotification?.UseSystemNotification);
        var call = Assert.Single(notificationProvider.SendCalls);
        Assert.Equal("Bark", call.Provider);
        Assert.Contains("sendKey=bark-key", call.ParametersText, StringComparison.Ordinal);
        Assert.Contains("server=https://api.day.app", call.ParametersText, StringComparison.Ordinal);
        Assert.Contains("Task error", call.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(call.Title, call.Message);
    }

    [Fact]
    public async Task AllTasksCompleted_ExternalNotification_UsesWpfCompletionBody()
    {
        var notificationProvider = new ScriptedNotificationProviderFeatureService();
        await using var fixture = await NotificationFixture.CreateAsync(
            useNotify: false,
            notificationProviderFeatureService: notificationProvider);
        if (fixture.Config.TryGetCurrentProfile(out var profile))
        {
            profile.Values[ConfigurationKeys.ExternalNotificationEnabled] = JsonValue.Create("Bark");
            profile.Values[ConfigurationKeys.ExternalNotificationSendWhenComplete] = JsonValue.Create(true);
            profile.Values[ConfigurationKeys.ExternalNotificationEnableDetails] = JsonValue.Create(true);
            profile.Values[ConfigurationKeys.ExternalNotificationBarkSendKey] = JsonValue.Create("bark-key");
            profile.Values[ConfigurationKeys.ExternalNotificationBarkServer] = JsonValue.Create("https://api.day.app");
        }

        await fixture.ViewModel.InitializeAsync();
        fixture.ViewModel.AppendSystemLog("detail-line");
        fixture.MapCoreTaskId(1, 0);
        await NotificationFixture.InvokeCallbackAsync(
            fixture.ViewModel,
            new CoreCallbackEvent(
                0,
                "AllTasksCompleted",
                """{"run_id":"external-completion-run","finished_tasks":[1]}""",
                DateTimeOffset.UtcNow));

        await WaitForExternalNotificationCountAsync(notificationProvider, 1);
        var call = Assert.Single(notificationProvider.SendCalls);
        Assert.Contains("completed", call.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("completed all tasks", call.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(fixture.Config.CurrentConfig.CurrentProfile, call.Message, StringComparison.Ordinal);
        Assert.Contains("[InfoLogBrush]detail-line", call.Message, StringComparison.Ordinal);
        Assert.Equal(1, fixture.NotificationCapability.NotificationCallCount);
        Assert.False(fixture.NotificationCapability.LastNotification?.UseSystemNotification);
    }

    [Fact]
    public void StalledExternalNotification_PrefersWpfSetting_AndDefaultsToDisabled()
    {
        var config = new UnifiedConfig
        {
            CurrentProfile = "Default",
            Profiles =
            {
                ["Default"] = new UnifiedProfile(),
            },
        };
        var profile = config.Profiles["Default"];
        profile.Values[ConfigurationKeys.ExternalNotificationEnabled] = JsonValue.Create("Bark");
        var notification = new TaskQueuePageViewModel.TaskQueueSystemNotification(
            "stalled",
            string.Empty,
            "test",
            "test",
            "recent logs");

        Assert.Empty(TaskQueuePageViewModel.BuildAutomaticExternalNotificationRequests(
            "TaskStalled",
            notification,
            config));

        profile.Values[ConfigurationKeys.ExternalNotificationSendWhenTimeout] = JsonValue.Create(true);
        Assert.Single(TaskQueuePageViewModel.BuildAutomaticExternalNotificationRequests(
            "TaskStalled",
            notification,
            config));

        profile.Values[ConfigurationKeys.ExternalNotificationSendWhenStalled] = JsonValue.Create(false);
        Assert.Empty(TaskQueuePageViewModel.BuildAutomaticExternalNotificationRequests(
            "TaskStalled",
            notification,
            config));
    }

    [Fact]
    public async Task AllTasksCompleted_WithoutFinishedMainTask_DoesNotSendNotification()
    {
        await using var fixture = await NotificationFixture.CreateAsync(useNotify: true);
        await fixture.ViewModel.InitializeAsync();

        await NotificationFixture.InvokeCallbackAsync(
            fixture.ViewModel,
            new CoreCallbackEvent(
                0,
                "AllTasksCompleted",
                """{"RunId":"non-main-completion"}""",
                DateTimeOffset.UtcNow));

        await Task.Delay(50);
        Assert.Equal(0, fixture.NotificationCapability.NotificationCallCount);
    }

    [Fact]
    public async Task TaskChainError_SendsEveryOccurrence_InSameRun()
    {
        await using var fixture = await NotificationFixture.CreateAsync(useNotify: true);
        await fixture.ViewModel.InitializeAsync();
        var callback = new CoreCallbackEvent(
            0,
            "TaskChainError",
            """{"run_id":"repeated-error-run","task_chain":"Fight"}""",
            DateTimeOffset.UtcNow);

        await NotificationFixture.InvokeCallbackAsync(fixture.ViewModel, callback);
        await NotificationFixture.InvokeCallbackAsync(fixture.ViewModel, callback);

        await WaitForNotificationCountAsync(fixture.NotificationCapability, 2);
    }

    [Fact]
    public async Task SubTaskError_DoesNotSendNotification()
    {
        await using var fixture = await NotificationFixture.CreateAsync(useNotify: true);
        await fixture.ViewModel.InitializeAsync();

        await NotificationFixture.InvokeCallbackAsync(
            fixture.ViewModel,
            new CoreCallbackEvent(
                0,
                "SubTaskError",
                """{"run_id":"subtask-error-run","task_chain":"Fight","subtask":"RecognizeDrops"}""",
                DateTimeOffset.UtcNow));

        await Task.Delay(50);
        Assert.Equal(0, fixture.NotificationCapability.NotificationCallCount);
    }

    [Fact]
    public async Task CloseDownTaskChainError_DoesNotSendNotification()
    {
        await using var fixture = await NotificationFixture.CreateAsync(useNotify: true);
        await fixture.ViewModel.InitializeAsync();

        await NotificationFixture.InvokeCallbackAsync(
            fixture.ViewModel,
            new CoreCallbackEvent(
                0,
                "TaskChainError",
                """{"run_id":"close-down-error-run","task_chain":"CloseDown"}""",
                DateTimeOffset.UtcNow));

        await Task.Delay(50);
        Assert.Equal(0, fixture.NotificationCapability.NotificationCallCount);
    }

    [Fact]
    public async Task RecruitTaskChainError_SendsRecognitionAndTaskErrorNotifications()
    {
        await using var fixture = await NotificationFixture.CreateAsync(useNotify: true);
        await fixture.ViewModel.InitializeAsync();

        await NotificationFixture.InvokeCallbackAsync(
            fixture.ViewModel,
            new CoreCallbackEvent(
                0,
                "TaskChainError",
                """{"run_id":"recruit-error-run","task_chain":"Recruit"}""",
                DateTimeOffset.UtcNow));

        await WaitForNotificationCountAsync(fixture.NotificationCapability, 2);
        Assert.Contains("error", fixture.NotificationCapability.LastTitle, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FightMissionFailedAndStop_SendsNotification()
    {
        await using var fixture = await NotificationFixture.CreateAsync(useNotify: true);
        await fixture.ViewModel.InitializeAsync();

        await NotificationFixture.InvokeCallbackAsync(
            fixture.ViewModel,
            new CoreCallbackEvent(
                10001,
                "SubTaskStart",
                """{"task_chain":"Fight","subtask":"ProcessTask","details":{"task":"FightMissionFailedAndStop"}}""",
                DateTimeOffset.UtcNow));

        await WaitForNotificationCountAsync(fixture.NotificationCapability, 1);
        Assert.Contains("stopped", fixture.NotificationCapability.LastTitle, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task OfflineConfirm_OnlySendsWhenAutoRestartIsDisabled(bool autoRestart, int expectedCount)
    {
        await using var fixture = await NotificationFixture.CreateAsync(useNotify: true);
        if (fixture.Config.TryGetCurrentProfile(out var profile))
        {
            profile.Values[ConfigurationKeys.AutoRestartOnDrop] = JsonValue.Create(autoRestart);
        }

        await fixture.ViewModel.InitializeAsync();
        await NotificationFixture.InvokeCallbackAsync(
            fixture.ViewModel,
            new CoreCallbackEvent(
                10001,
                "SubTaskStart",
                """{"task_chain":"Fight","subtask":"ProcessTask","details":{"task":"OfflineConfirm"}}""",
                DateTimeOffset.UtcNow));

        if (expectedCount > 0)
        {
            await WaitForNotificationCountAsync(fixture.NotificationCapability, expectedCount);
        }
        else
        {
            await Task.Delay(50);
        }

        Assert.Equal(expectedCount, fixture.NotificationCapability.NotificationCallCount);
    }

    [Theory]
    [InlineData("RecruitSpecialTag")]
    [InlineData("RecruitPreservedTag")]
    [InlineData("RecruitRobotTag")]
    public async Task RecruitRareTagEvents_SendNotification(string what)
    {
        await using var fixture = await NotificationFixture.CreateAsync(useNotify: true);
        await fixture.ViewModel.InitializeAsync();

        await NotificationFixture.InvokeCallbackAsync(
            fixture.ViewModel,
            new CoreCallbackEvent(
                10003,
                "SubTaskExtraInfo",
                new JsonObject
                {
                    ["task_chain"] = "Recruit",
                    ["subtask"] = "AutoRecruitTask",
                    ["what"] = what,
                    ["details"] = new JsonObject { ["tag"] = "Senior Operator" },
                }.ToJsonString(),
                DateTimeOffset.UtcNow));

        await WaitForNotificationCountAsync(fixture.NotificationCapability, 1);
        Assert.Equal("Senior Operator", fixture.NotificationCapability.LastMessage);
    }

    [Theory]
    [InlineData(4, 0)]
    [InlineData(5, 1)]
    [InlineData(6, 1)]
    public async Task RecruitResult_OnlySendsForFiveStarsOrHigher(int level, int expectedCount)
    {
        await using var fixture = await NotificationFixture.CreateAsync(useNotify: true);
        await fixture.ViewModel.InitializeAsync();

        await NotificationFixture.InvokeCallbackAsync(
            fixture.ViewModel,
            new CoreCallbackEvent(
                10003,
                "SubTaskExtraInfo",
                new JsonObject
                {
                    ["task_chain"] = "Recruit",
                    ["subtask"] = "AutoRecruitTask",
                    ["what"] = "RecruitResult",
                    ["details"] = new JsonObject { ["level"] = level },
                }.ToJsonString(),
                DateTimeOffset.UtcNow));

        if (expectedCount > 0)
        {
            await WaitForNotificationCountAsync(fixture.NotificationCapability, expectedCount);
        }
        else
        {
            await Task.Delay(50);
        }

        Assert.Equal(expectedCount, fixture.NotificationCapability.NotificationCallCount);
    }

    private static async Task WaitForNotificationCountAsync(
        NotificationTrackingPlatformCapabilityService service,
        int expectedCount,
        int timeoutMs = 1000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (service.NotificationCallCount != expectedCount && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        if (service.NotificationCallCount != expectedCount)
        {
            throw new TimeoutException(
                $"Expected notification count {expectedCount}, but saw {service.NotificationCallCount}.");
        }
    }

    private static async Task WaitForExternalNotificationCountAsync(
        ScriptedNotificationProviderFeatureService service,
        int expectedCount,
        int timeoutMs = 1000)
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
            return Task.FromResult(UiOperationResult.Ok("sent"));
        }
    }

    private sealed class NotificationFixture : IAsyncDisposable
    {
        private NotificationFixture(
            string root,
            UnifiedConfigurationService config,
            MAAUnifiedRuntime runtime,
            NotificationTrackingPlatformCapabilityService notificationCapability,
            TaskQueuePageViewModel viewModel)
        {
            Root = root;
            Config = config;
            Runtime = runtime;
            NotificationCapability = notificationCapability;
            ViewModel = viewModel;
        }

        public string Root { get; }

        public UnifiedConfigurationService Config { get; }

        public MAAUnifiedRuntime Runtime { get; }

        public NotificationTrackingPlatformCapabilityService NotificationCapability { get; }

        public TaskQueuePageViewModel ViewModel { get; }

        public void MapCoreTaskId(int taskId, int taskIndex)
        {
            var method = typeof(UnifiedSessionService).GetMethod(
                "SetTaskIdMapping",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Unable to locate task id mapping method.");
            method.Invoke(Runtime.SessionService, new object[] { taskId, taskIndex });
        }

        public static async Task<NotificationFixture> CreateAsync(
            bool useNotify,
            string language = "en-us",
            INotificationProviderFeatureService? notificationProviderFeatureService = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "maa-taskqueue-notification-tests", Guid.NewGuid().ToString("N"));
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

            config.CurrentConfig.GlobalValues[ConfigurationKeys.Localization] = JsonValue.Create(language);
            config.CurrentConfig.GlobalValues[ConfigurationKeys.UseNotify] = JsonValue.Create(useNotify);
            if (config.TryGetCurrentProfile(out var profile))
            {
                profile.Values["ClientType"] = JsonValue.Create("Official");
                profile.Values["ServerType"] = JsonValue.Create("CN");
                profile.Values["ConnectAddress"] = JsonValue.Create("127.0.0.1:5555");
                profile.Values["ConnectConfig"] = JsonValue.Create("General");
                profile.Values["AutoDetect"] = JsonValue.Create(true);
            }

            var bridge = new TestBridge();
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
            var tracking = new NotificationTrackingPlatformCapabilityService(capability);
            var connect = new ConnectFeatureService(session, config);
            var shell = new ShellFeatureService(connect);
            var taskQueue = new TaskQueueFeatureService(session, config);
            var dialog = new DialogFeatureService(diagnostics);
            var postAction = new PostActionFeatureService(config, diagnostics, platform.PostActionExecutorService);
            var versionUpdate = new VersionUpdateFeatureService(config, diagnostics, uiLogService: log, runtimeBaseDirectory: root);

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
                ShellFeatureService = shell,
                TaskQueueFeatureService = taskQueue,
                CopilotFeatureService = new CopilotFeatureService(),
                ToolboxFeatureService = new ToolboxFeatureService(),
                RemoteControlFeatureService = new RemoteControlFeatureService(),
                PlatformCapabilityService = tracking,
                OverlayFeatureService = new OverlayFeatureService(tracking),
                NotificationProviderFeatureService = notificationProviderFeatureService ?? new NotificationProviderFeatureService(),
                SettingsFeatureService = new SettingsFeatureService(config, tracking, diagnostics),
                DialogFeatureService = dialog,
                PostActionFeatureService = postAction,
                VersionUpdateFeatureService = versionUpdate,
                UiLanguageCoordinator = new UiLanguageCoordinator(config),
            };

            var connectionState = new ConnectionGameSharedStateViewModel
            {
                ConnectAddress = "127.0.0.1:5555",
                ConnectConfig = "General",
                ClientType = "Official",
                AutoDetect = true,
            };

            var viewModel = new TaskQueuePageViewModel(runtime, connectionState);
            return new NotificationFixture(root, config, runtime, tracking, viewModel);
        }

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // ignore cleanup failures
            }
        }

        public static async Task InvokeCallbackAsync(TaskQueuePageViewModel vm, CoreCallbackEvent callback)
        {
            var method = typeof(TaskQueuePageViewModel).GetMethod(
                "HandleCallbackCoreAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (method is null)
            {
                throw new InvalidOperationException("Unable to locate TaskQueue callback handler.");
            }

            var result = method.Invoke(vm, new object?[] { callback }) as Task
                ?? throw new InvalidOperationException("TaskQueue callback handler did not return a task.");
            await result;
        }

        private sealed class TestBridge : IMaaCoreBridge
        {
            public Task<CoreResult<CoreInitializeInfo>> InitializeAsync(CoreInitializeRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(CoreResult<CoreInitializeInfo>.Ok(new CoreInitializeInfo(request.BaseDirectory, "fake", "fake", request.ClientType)));

            public Task<CoreResult<bool>> ConnectAsync(CoreConnectionInfo connectionInfo, CancellationToken cancellationToken = default)
                => Task.FromResult(CoreResult<bool>.Ok(true));

            public Task<CoreResult<int>> AppendTaskAsync(CoreTaskRequest task, CancellationToken cancellationToken = default)
                => Task.FromResult(CoreResult<int>.Ok(1));

            public Task<CoreResult<bool>> StartAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(CoreResult<bool>.Ok(true));

            public Task<CoreResult<bool>> StopAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(CoreResult<bool>.Ok(true));

            public Task<CoreResult<CoreRuntimeStatus>> GetRuntimeStatusAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(CoreResult<CoreRuntimeStatus>.Ok(new CoreRuntimeStatus(true, true, false)));

            public Task<CoreResult<bool>> AttachWindowAsync(CoreAttachWindowRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.NotSupported, "not supported")));

            public Task<CoreResult<byte[]>> GetImageAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(CoreResult<byte[]>.Ok(Array.Empty<byte>()));

            public async IAsyncEnumerable<CoreCallbackEvent> CallbackStreamAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.CompletedTask;
                yield break;
            }
 
            public ValueTask DisposeAsync()
                => ValueTask.CompletedTask;
        }
    }
}
