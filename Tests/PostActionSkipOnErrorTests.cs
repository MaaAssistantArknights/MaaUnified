using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using MAAUnified.Compat.Constants;
using MAAUnified.Platform;
using Xunit;

namespace MAAUnified.Tests;

public sealed class PostActionSkipOnErrorTests
{
    [Fact]
    public void PostActionConfig_SkipOnError_RoundTripsAndKeepsLegacyConfigsUsable()
    {
        var config = new PostActionConfig { Sleep = true, SkipOnError = true };

        var json = config.ToJson();
        Assert.True(json["skip_on_error"]!.GetValue<bool>());

        var parsed = PostActionConfig.FromJson(json);
        Assert.True(parsed.SkipOnError);
        Assert.True(parsed.Sleep);
        Assert.True(parsed.ShouldSkipOnError(true));
        Assert.False(parsed.ShouldSkipOnError(false));

        // A config written before the flag existed keeps loading with the skip disabled.
        var legacy = PostActionConfig.FromJson(JsonNode.Parse("""{"sleep":true}"""));
        Assert.False(legacy.SkipOnError);
        Assert.True(legacy.Sleep);
        Assert.False(legacy.ShouldSkipOnError(true));

        var clone = config.Clone();
        Assert.True(clone.SkipOnError);
    }

    [Fact]
    public void LegacyPostActionBitmask_SkipOnErrorBit_IsMigrated()
    {
        // WPF cda1c3b07f uses bit 8; a gui.json written by that build must keep the flag.
        var config = new UnifiedConfig();
        var profile = new UnifiedProfile();
        profile.Values[ConfigurationKeys.PostActions] = JsonValue.Create((1 << 7) | (1 << 8));
        config.Profiles["Default"] = profile;
        config.CurrentProfile = "Default";

        var resolution = PostActionLegacyMigration.ResolveForProfile(config, profile);

        Assert.True(resolution.Success);
        Assert.True(resolution.Config.Sleep);
        Assert.True(resolution.Config.SkipOnError);
    }

    [Fact]
    public async Task ExecuteAfterCompletion_WithSkipOnErrorAndFailedRun_DoesNotExecuteActions()
    {
        var lifecycle = new RecordingAppLifecycleService();
        await using var fixture = await Fixture.CreateAsync(lifecycle);

        // WPF cda1c3b07f: SkipOnError + a failed run early-returns before every action.
        var result = await fixture.PostAction.ExecuteAfterCompletionAsync(
            new PostActionExecutionContext(
                "AllTasksCompleted",
                WasSuccessfulTaskChain: false,
                RunId: "run-skip-on-error",
                TaskIndex: 0),
            new PostActionConfig { ExitSelf = true, SkipOnError = true });

        Assert.True(result.Success);
        Assert.Contains("skipped", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, lifecycle.ExitCount);

        var eventLog = await fixture.ReadEventLogAsync();
        Assert.Contains($"errorCode={UiErrorCode.PostActionSkippedOnError}", eventLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAfterCompletion_WithSkipOnErrorButSuccessfulRun_ExecutesActions()
    {
        var lifecycle = new RecordingAppLifecycleService();
        await using var fixture = await Fixture.CreateAsync(lifecycle);

        var result = await fixture.PostAction.ExecuteAfterCompletionAsync(
            new PostActionExecutionContext(
                "AllTasksCompleted",
                WasSuccessfulTaskChain: true,
                RunId: "run-successful",
                TaskIndex: 0),
            new PostActionConfig { ExitSelf = true, SkipOnError = true });

        Assert.True(result.Success);
        Assert.Equal(1, lifecycle.ExitCount);
    }

    [Fact]
    public async Task ExecuteAfterCompletion_WithoutSkipOnError_FailedRunStillExecutesActions()
    {
        var lifecycle = new RecordingAppLifecycleService();
        await using var fixture = await Fixture.CreateAsync(lifecycle);

        var result = await fixture.PostAction.ExecuteAfterCompletionAsync(
            new PostActionExecutionContext(
                "AllTasksCompleted",
                WasSuccessfulTaskChain: false,
                RunId: "run-failed-no-skip",
                TaskIndex: 0),
            new PostActionConfig { ExitSelf = true });

        Assert.True(result.Success);
        Assert.Equal(1, lifecycle.ExitCount);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(string root, PostActionFeatureService postAction)
        {
            Root = root;
            PostAction = postAction;
        }

        public string Root { get; }

        public PostActionFeatureService PostAction { get; }

        public static async Task<Fixture> CreateAsync(IAppLifecycleService lifecycle)
        {
            var root = Path.Combine(Path.GetTempPath(), "maa-unified-post-action-skip", Guid.NewGuid().ToString("N"));
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
            if (config.TryGetCurrentProfile(out var profile))
            {
                profile.Values[MacBundledAdbPolicy.ProfileUseBundledAdbKey] = JsonValue.Create(false);
            }

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

            var postAction = new PostActionFeatureService(
                config,
                diagnostics,
                platform.PostActionExecutorService,
                coreBridge: null,
                appLifecycleService: lifecycle,
                promptService: new NoOpPostActionPromptService());
            return new Fixture(root, postAction);
        }

        public async Task<string> ReadEventLogAsync()
        {
            var path = Path.Combine(Root, "debug", "avalonia-ui-events.log");
            for (var attempt = 0; attempt < 20; attempt++)
            {
                if (File.Exists(path))
                {
                    return await File.ReadAllTextAsync(path);
                }

                await Task.Delay(10);
            }

            return string.Empty;
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // keep the folder for inspection when a handle is still open
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingAppLifecycleService : IAppLifecycleService
    {
        public bool SupportsExit => true;

        public int ExitCount { get; private set; }

        public Task<UiOperationResult> RestartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(UiOperationResult.Ok("Restarted."));
        }

        public Task<UiOperationResult> ExitAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExitCount++;
            return Task.FromResult(UiOperationResult.Ok("Exited."));
        }
    }
}
