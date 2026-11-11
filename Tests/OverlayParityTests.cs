using System.Text.Json.Nodes;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using System.Threading.Channels;
using MAAUnified.App.Features.Dialogs;
using MAAUnified.App.ViewModels;
using MAAUnified.App.ViewModels.Advanced;
using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.App.ViewModels.Settings;
using MAAUnified.App.ViewModels.TaskQueue;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using MAAUnified.CoreBridge;
using MAAUnified.Platform;
using LegacyConfigurationKeys = MAAUnified.Compat.Constants.ConfigurationKeys;

namespace MAAUnified.Tests;

public sealed class OverlayParityTests
{
    [Fact]
    public void OverlayTargetPersistence_SerializeAndLoad_ShouldRoundTripSelection()
    {
        var target = new OverlayTarget(
            "hwnd:1A2B",
            "Game Window",
            false,
            NativeHandle: 0x1A2B,
            ProcessId: 2048,
            ProcessName: "emulator",
            WindowTitle: "Arknights");
        var globals = new Dictionary<string, JsonNode?>
        {
            [LegacyConfigurationKeys.OverlayTarget] = JsonValue.Create(OverlayTargetPersistence.Serialize(target)),
        };

        var persisted = OverlayTargetPersistence.Load(globals);

        Assert.NotNull(persisted);
        Assert.Equal(target.Id, persisted!.TargetId);
        Assert.Equal(target.NativeHandle, persisted.NativeHandle);
        Assert.Equal(target.ProcessId, persisted.ProcessId);
        Assert.Equal(target.ProcessName, persisted.ProcessName);
        Assert.Equal(target.WindowTitle, persisted.WindowTitle);
    }

    [Fact]
    public void OverlayTargetPersistence_ResolveSelection_ShouldRestoreByExactId()
    {
        var preview = new OverlayTarget("preview", "Preview + Logs", true);
        var target = new OverlayTarget("hwnd:100", "Game", false, NativeHandle: 0x100, ProcessId: 1, ProcessName: "emu", WindowTitle: "Title");
        var globals = new Dictionary<string, JsonNode?>
        {
            [LegacyConfigurationKeys.OverlayTarget] = JsonValue.Create("""{"TargetId":"hwnd:100","NativeHandle":256,"ProcessId":1,"ProcessName":"emu","WindowTitle":"Title"}"""),
        };

        var resolved = OverlayTargetPersistence.ResolveSelection([preview, target], globals);

        Assert.Equal(target, resolved);
    }

    [Fact]
    public void OverlayTargetPersistence_ResolveSelection_ShouldNotRestoreReusedIdOrHandleWhenIdentityChanged()
    {
        var preview = new OverlayTarget("preview", "Preview + Logs", true);
        var fallback = new OverlayTarget("hwnd:200", "Preferred Game", true, NativeHandle: 0x200, ProcessId: 2, ProcessName: "emu-a", WindowTitle: "Arknights");
        var reused = new OverlayTarget("hwnd:300", "Reused Window", false, NativeHandle: 0x300, ProcessId: 3, ProcessName: "other", WindowTitle: "Other");
        var globals = new Dictionary<string, JsonNode?>
        {
            [LegacyConfigurationKeys.OverlayTarget] = JsonValue.Create("""{"TargetId":"hwnd:300","Hwnd":768,"ProcessId":99,"ProcessName":"old-process","Title":"Old game"}"""),
        };

        var resolved = OverlayTargetPersistence.ResolveSelection([preview, fallback, reused], globals);

        Assert.Equal(fallback, resolved);
    }

    [Fact]
    public void OverlayTargetPersistence_ResolveSelection_ShouldNotTrustLegacyHandleWithoutProcessIdLikeWpf()
    {
        // WPF parity (OverlayViewModel.TryFindWindow, freeze 23ddd27): a stored hwnd without the
        // process id can never be validated (WPF compares the window's pid against stored pid 0,
        // which never matches), so resolution must fall through instead of restoring the handle.
        var preview = new OverlayTarget("preview", "Preview + Logs", true);
        var primary = new OverlayTarget("hwnd:200", "Primary", true, NativeHandle: 0x200, ProcessId: 2, ProcessName: "emu-a", WindowTitle: "A");
        var handleMatch = new OverlayTarget("hwnd:300", "Handle Match", false, NativeHandle: 0x300, ProcessId: 3, ProcessName: "emu-b", WindowTitle: "B");
        var globals = new Dictionary<string, JsonNode?>
        {
            [LegacyConfigurationKeys.OverlayTarget] = JsonValue.Create("""{"TargetId":"missing","Hwnd":768}"""),
        };

        var resolved = OverlayTargetPersistence.ResolveSelection([preview, primary, handleMatch], globals);

        Assert.Equal(primary, resolved);
    }

    [Fact]
    public void OverlayTargetPersistence_ResolveSelection_ShouldRestoreHandleWhenPidMatchesDespiteIdentityDrift()
    {
        // WPF parity: the stored hwnd path validates the process id only; process-name/title
        // drift (e.g. a dynamic window title) must not block the restore.
        var preview = new OverlayTarget("preview", "Preview + Logs", true);
        var target = new OverlayTarget("hwnd:300", "Renamed", false, NativeHandle: 0x300, ProcessId: 3, ProcessName: "emu-new", WindowTitle: "New Title");
        var globals = new Dictionary<string, JsonNode?>
        {
            [LegacyConfigurationKeys.OverlayTarget] = JsonValue.Create("""{"TargetId":"hwnd:300","NativeHandle":768,"ProcessId":3,"ProcessName":"emu-old","WindowTitle":"Old Title"}"""),
        };

        var resolved = OverlayTargetPersistence.ResolveSelection([preview, target], globals);

        Assert.Equal(target, resolved);
    }

    [Fact]
    public void OverlayTargetPersistence_ResolveSelection_ShouldMatchByProcessIdAloneLikeWpfEnumeration()
    {
        // WPF parity: during enumeration a process-id match alone is sufficient, regardless of
        // process name or window title.
        var preview = new OverlayTarget("preview", "Preview + Logs", true);
        var target = new OverlayTarget("hwnd:300", "Other Name", false, NativeHandle: 0x300, ProcessId: 5, ProcessName: "renamed", WindowTitle: "Other Title");
        var globals = new Dictionary<string, JsonNode?>
        {
            [LegacyConfigurationKeys.OverlayTarget] = JsonValue.Create("""{"TargetId":"missing","ProcessId":5,"ProcessName":"old-name","Title":"Old Title"}"""),
        };

        var resolved = OverlayTargetPersistence.ResolveSelection([preview, target], globals);

        Assert.Equal(target, resolved);
    }

    [Fact]
    public void OverlayTargetPersistence_ResolveSelection_ShouldMatchByProcessNameAndTitleAcrossPidChange()
    {
        // WPF parity: the name+title fallback does not require the persisted process id, so a
        // restarted game (new pid and hwnd) is still re-resolved.
        var preview = new OverlayTarget("preview", "Preview + Logs", true);
        var restarted = new OverlayTarget("hwnd:300", "Restarted", false, NativeHandle: 0x300, ProcessId: 200, ProcessName: "arknights", WindowTitle: "明日方舟 (MuMu)");
        var globals = new Dictionary<string, JsonNode?>
        {
            [LegacyConfigurationKeys.OverlayTarget] = JsonValue.Create("""{"TargetId":"missing","Hwnd":9999,"ProcessId":100,"ProcessName":"arknights","Title":"明日方舟"}"""),
        };

        var resolved = OverlayTargetPersistence.ResolveSelection([preview, restarted], globals);

        Assert.Equal(restarted, resolved);
    }

    [Fact]
    public void OverlayTargetPersistence_ResolveSelection_ShouldNotMatchMetadataWhenTitleIsNotContained()
    {
        // WPF parity: the name+title fallback is a one-directional containment
        // (current title contains the persisted title); a shorter current title must not match.
        var preview = new OverlayTarget("preview", "Preview + Logs", true);
        var other = new OverlayTarget("hwnd:300", "Other", false, NativeHandle: 0x300, ProcessId: 200, ProcessName: "arknights", WindowTitle: "明日方舟");
        var globals = new Dictionary<string, JsonNode?>
        {
            [LegacyConfigurationKeys.OverlayTarget] = JsonValue.Create("""{"TargetId":"missing","ProcessId":100,"ProcessName":"arknights","Title":"明日方舟 加长版标题"}"""),
            [LegacyConfigurationKeys.OverlayPreviewPinned] = JsonValue.Create(bool.TrueString),
        };

        var resolved = OverlayTargetPersistence.ResolveSelection([preview, other], globals);

        Assert.Equal(preview, resolved);
    }

    [Fact]
    public void OverlayTargetPersistence_ResolveSelection_ShouldRestoreByProcessMetadataWhenWindowReturns()
    {
        var preview = new OverlayTarget("preview", "Preview + Logs", true);
        var reappeared = new OverlayTarget("hwnd:300", "Metadata Match", false, NativeHandle: 0x300, ProcessId: 3, ProcessName: "emu-b", WindowTitle: "Arknights - MuMu");
        var globals = new Dictionary<string, JsonNode?>
        {
            [LegacyConfigurationKeys.OverlayTarget] = JsonValue.Create("""{"TargetId":"missing","Hwnd":9999,"ProcessId":3,"ProcessName":"emu-b","Title":"Arknights"}"""),
        };

        var resolved = OverlayTargetPersistence.ResolveSelection([preview, reappeared], globals);

        Assert.Equal(reappeared, resolved);
    }

    [Fact]
    public void OverlayTargetPersistence_ResolveSelection_ShouldPreferNativeTarget_WhenLegacyPreviewSelectionWasImplicit()
    {
        var preview = new OverlayTarget("preview", "Preview + Logs", true);
        var target = new OverlayTarget("hwnd:400", "Game", false, NativeHandle: 0x400, ProcessId: 4, ProcessName: "emu", WindowTitle: "Title");
        var globals = new Dictionary<string, JsonNode?>
        {
            [LegacyConfigurationKeys.OverlayTarget] = JsonValue.Create("""{"TargetId":"missing","NativeHandle":9999,"ProcessId":77,"ProcessName":"other","WindowTitle":"other"}"""),
        };

        var resolved = OverlayTargetPersistence.ResolveSelection([preview, target], globals);

        Assert.Equal(target, resolved);
    }

    [Fact]
    public void OverlayTargetPersistence_ResolveSelection_ShouldRespectPreview_WhenPreviewPreferenceWasExplicitlyPinned()
    {
        var preview = new OverlayTarget("preview", "Preview + Logs", true);
        var target = new OverlayTarget("hwnd:401", "Game", false, NativeHandle: 0x401, ProcessId: 5, ProcessName: "emu", WindowTitle: "Title");
        var globals = new Dictionary<string, JsonNode?>
        {
            [LegacyConfigurationKeys.OverlayTarget] = JsonValue.Create(OverlayTargetPersistence.Serialize(preview)),
            [LegacyConfigurationKeys.OverlayPreviewPinned] = JsonValue.Create(bool.TrueString),
        };

        var resolved = OverlayTargetPersistence.ResolveSelection([preview, target], globals);

        Assert.Equal(preview, resolved);
    }

    [Fact]
    public async Task TaskQueuePage_AppendSystemLog_ShouldMirrorAllOverlayLogs()
    {
        await using var fixture = await OverlayFixture.CreateAsync();
        var vm = fixture.Shell.TaskQueuePage;

        for (var index = 0; index < 205; index++)
        {
            vm.AppendSystemLog($"entry-{index}");
        }

        Assert.Equal(205, vm.OverlayLogs.Count);
        Assert.Contains("entry-0", vm.OverlayLogs[0].Content, StringComparison.Ordinal);
        Assert.Contains("entry-204", vm.OverlayLogs[^1].Content, StringComparison.Ordinal);
        var card = Assert.Single(vm.LogCards);
        Assert.Equal(205, card.Items.Count);
        Assert.Contains("entry-204", card.Items[^1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TaskQueuePage_AppendSystemLog_ShouldMergeOrdinaryEntriesIntoCurrentCard()
    {
        await using var fixture = await OverlayFixture.CreateAsync();
        var vm = fixture.Shell.TaskQueuePage;

        vm.AppendSystemLog("first adb retry");
        vm.AppendSystemLog("second adb retry");

        var card = Assert.Single(vm.LogCards);
        Assert.Equal(2, card.Items.Count);
        Assert.Equal("first adb retry", card.Items[0].Content);
        Assert.Equal("second adb retry", card.PrimaryContent);
    }

    [Fact]
    public async Task TaskQueuePage_AppendSystemLog_ShouldKeepTimestampedMultilineMessageInOneCard()
    {
        await using var fixture = await OverlayFixture.CreateAsync();
        var vm = fixture.Shell.TaskQueuePage;

        vm.AppendSystemLog("""
            [12:00:01] adb connect failed
            [12:00:02] retry adb connect
            [12:00:03] adb connect failed again
            """);

        var card = Assert.Single(vm.LogCards);
        Assert.Single(card.Items);
        Assert.Contains("[12:00:01] adb connect failed", card.PrimaryContent, StringComparison.Ordinal);
        Assert.Contains("[12:00:02] retry adb connect", card.PrimaryContent, StringComparison.Ordinal);
        Assert.Contains("[12:00:03] adb connect failed again", card.PrimaryContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LanguageSwitch_ShouldRefreshOverlayTooltipWithoutBacktrackingExistingOverlayLogs()
    {
        await using var fixture = await OverlayFixture.CreateAsync();
        var vm = fixture.Shell.TaskQueuePage;
        vm.SetLanguage("zh-cn");
        vm.AppendSystemLog("已有悬浮窗日志");

        var tooltipBefore = vm.OverlayButtonToolTip;
        var logBefore = Assert.Single(vm.OverlayLogs).Content;

        vm.SetLanguage("en-us");

        Assert.Contains("选择目标", tooltipBefore, StringComparison.Ordinal);
        Assert.Contains("Pick target", vm.OverlayButtonToolTip, StringComparison.Ordinal);
        Assert.NotEqual(tooltipBefore, vm.OverlayButtonToolTip);
        Assert.Equal(logBefore, Assert.Single(vm.OverlayLogs).Content);
    }

    [Fact]
    public async Task OverlaySharedState_ShouldSyncVisibilityBetweenTaskQueueConsumers()
    {
        await using var fixture = await OverlayFixture.CreateAsync();
        var taskQueue = fixture.Shell.TaskQueuePage;
        var secondTaskQueue = new TaskQueuePageViewModel(
            fixture.Runtime,
            fixture.Shell.ConnectionGameSharedState,
            dialogService: NoOpAppDialogService.Instance);

        Assert.False(taskQueue.OverlayVisible);
        Assert.False(secondTaskQueue.OverlayVisible);

        await taskQueue.ToggleOverlayAsync();

        Assert.True(taskQueue.OverlayVisible);
        Assert.True(secondTaskQueue.OverlayVisible);
    }

    [Fact]
    public async Task OverlayPresentation_ShouldPreferRunOwnerOverIdlePreference()
    {
        await using var fixture = await OverlayFixture.CreateAsync();
        var presentation = fixture.Shell.OverlayPresentation;

        Assert.Equal(OverlayLogSource.TaskQueue, presentation.ResolvedSource);
        Assert.Same(fixture.Shell.TaskQueuePage.OverlayLogs, presentation.CurrentLogs);

        presentation.PreferCopilot();
        Assert.Equal(OverlayLogSource.Copilot, presentation.ResolvedSource);
        Assert.Same(fixture.Shell.CopilotPage.Logs, presentation.CurrentLogs);

        Assert.True(fixture.Runtime.SessionService.TryBeginRun("TaskQueue", out _));
        presentation.RefreshResolvedSource();

        Assert.Equal(OverlayLogSource.TaskQueue, presentation.ResolvedSource);
        Assert.Same(fixture.Shell.TaskQueuePage.OverlayLogs, presentation.CurrentLogs);

        fixture.Runtime.SessionService.EndRun("TaskQueue");
        presentation.RefreshResolvedSource();

        Assert.Equal(OverlayLogSource.Copilot, presentation.ResolvedSource);
        Assert.Same(fixture.Shell.CopilotPage.Logs, presentation.CurrentLogs);
    }

    [Fact]
    public async Task MainShellOverlayContextMethods_ShouldUpdateIdlePreference()
    {
        await using var fixture = await OverlayFixture.CreateAsync();

        await fixture.Shell.ToggleOverlayFromCopilotAsync();
        Assert.Equal(OverlayLogSource.Copilot, fixture.Shell.OverlayPresentation.PreferredSource);

        await fixture.Shell.ToggleOverlayFromTaskQueueAsync();
        Assert.Equal(OverlayLogSource.TaskQueue, fixture.Shell.OverlayPresentation.PreferredSource);
    }

    [Fact]
    public async Task OverlayStateChangedEvent_ShouldSyncRuntimeModeStatusAndSharedVisibility()
    {
        await using var fixture = await OverlayFixture.CreateAsync();
        var taskQueue = fixture.Shell.TaskQueuePage;
        var secondTaskQueue = new TaskQueuePageViewModel(
            fixture.Runtime,
            fixture.Shell.ConnectionGameSharedState,
            dialogService: NoOpAppDialogService.Instance);
        var applyMethod = typeof(MainShellViewModel).GetMethod(
            "ApplyOverlayStateChanged",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(applyMethod);
        var state = new OverlayStateChangedEvent(
            OverlayRuntimeMode.Preview,
            Visible: true,
            TargetId: "preview",
            Action: "fallback-enter",
            Message: "Overlay switched to Preview + Logs mode.",
            Timestamp: DateTimeOffset.UtcNow,
            Provider: "test-overlay",
            UsedFallback: true,
            ErrorCode: PlatformErrorCodes.OverlayPreviewMode);
        applyMethod!.Invoke(fixture.Shell, [state]);

        Assert.Equal(OverlayRuntimeMode.Preview, taskQueue.OverlayMode);
        Assert.True(taskQueue.OverlayVisible);
        Assert.True(secondTaskQueue.OverlayVisible);
        Assert.Equal(OverlayRuntimeMode.Preview, secondTaskQueue.OverlayMode);
        Assert.Equal("Overlay switched to Preview + Logs mode.", taskQueue.OverlayStatusText);
        Assert.Equal("Preview + Logs", taskQueue.OverlayTargetSummaryText);
        Assert.Contains("Overlay switched to Preview + Logs mode.", taskQueue.OverlayButtonToolTip, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToggleOverlayFromTrayAsync_ShouldUsePreviewModeGrowlWhenOverlayFallsBack()
    {
        var overlayService = new ScriptedOverlayService();
        overlayService.VisibleMode = OverlayRuntimeMode.Preview;
        overlayService.VisibleMessage = "Overlay switched to Preview + Logs mode.";
        overlayService.EmitStateOnVisibilityToggle = false;

        await using var fixture = await OverlayFixture.CreateAsync(overlayService: overlayService);
        var applyMethod = typeof(MainShellViewModel).GetMethod(
            "ApplyOverlayStateChanged",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(applyMethod);
        applyMethod!.Invoke(fixture.Shell, [new OverlayStateChangedEvent(
            OverlayRuntimeMode.Preview,
            Visible: false,
            TargetId: "preview",
            Action: "fallback-enter",
            Message: overlayService.VisibleMessage,
            Timestamp: DateTimeOffset.UtcNow,
            Provider: overlayService.Capability.Provider,
            UsedFallback: true,
            ErrorCode: PlatformErrorCodes.OverlayPreviewMode)]);

        await fixture.Shell.ToggleOverlayFromTrayAsync();

        Assert.Contains(fixture.Shell.GrowlMessages, message => message.Contains("预览模式", StringComparison.Ordinal));
        Assert.Equal(OverlayRuntimeMode.Preview, fixture.Shell.TaskQueuePage.OverlayMode);
        Assert.True(fixture.Shell.TaskQueuePage.OverlayVisible);
    }

    [Fact]
    public void OverlayTargetPersistence_ShouldPersistResolvedSelection_ShouldAllowFirstWriteAndLegacyPreviewMigration()
    {
        var native = new OverlayTarget("hwnd:100", "Game", false, NativeHandle: 0x100, ProcessId: 1, ProcessName: "emu", WindowTitle: "Title");
        var emptyGlobals = new Dictionary<string, JsonNode?>();

        Assert.True(OverlayTargetPersistence.ShouldPersistResolvedSelection(native, emptyGlobals));

        // Legacy migration: the persisted selection is the preview pseudo-target, so persisting the
        // auto-resolved native target must stay allowed (pins
        // ToggleOverlayAsync_ShouldAutoMigrateLegacyPreviewSelectionToNativeTarget semantics).
        var legacyPreviewGlobals = new Dictionary<string, JsonNode?>
        {
            [LegacyConfigurationKeys.OverlayTarget] = JsonValue.Create(
                OverlayTargetPersistence.Serialize(new OverlayTarget("preview", "Preview + Logs", true))),
        };

        Assert.True(OverlayTargetPersistence.ShouldPersistResolvedSelection(native, legacyPreviewGlobals));
    }

    [Fact]
    public void OverlayTargetPersistence_ShouldPersistResolvedSelection_ShouldAllowWhenPersistedIdentityStillMatches()
    {
        // Restarted game: new hwnd/pid, but the process metadata still matches the binding, so
        // persisting the refreshed identity is consistent with the existing config.
        var restarted = new OverlayTarget("hwnd:200", "Game (restarted)", false, NativeHandle: 0x200, ProcessId: 2, ProcessName: "emu", WindowTitle: "Title");
        var globals = new Dictionary<string, JsonNode?>
        {
            [LegacyConfigurationKeys.OverlayTarget] = JsonValue.Create("""{"TargetId":"hwnd:100","NativeHandle":256,"ProcessId":1,"ProcessName":"emu","WindowTitle":"Title"}"""),
        };

        Assert.True(OverlayTargetPersistence.ShouldPersistResolvedSelection(restarted, globals));
    }

    [Fact]
    public void OverlayTargetPersistence_ShouldPersistResolvedSelection_ShouldRejectUnrelatedFallbackWhileBindingIsAbsent()
    {
        // The persisted target is temporarily absent: an unrelated fallback window (or the preview
        // pseudo-target) must not overwrite the binding.
        var globals = new Dictionary<string, JsonNode?>
        {
            [LegacyConfigurationKeys.OverlayTarget] = JsonValue.Create("""{"TargetId":"hwnd:100","NativeHandle":256,"ProcessId":1,"ProcessName":"emu","WindowTitle":"Title"}"""),
        };
        var fallback = new OverlayTarget("hwnd:300", "Notepad", false, NativeHandle: 0x300, ProcessId: 3, ProcessName: "notepad", WindowTitle: "Untitled - Notepad");
        var preview = new OverlayTarget("preview", "Preview + Logs", true);

        Assert.False(OverlayTargetPersistence.ShouldPersistResolvedSelection(fallback, globals));
        Assert.False(OverlayTargetPersistence.ShouldPersistResolvedSelection(preview, globals));
    }

    [Fact]
    public async Task ToggleOverlayAsync_WhenSelectedTargetHwndIsStale_ShouldReresolveByPersistedIdentityLikeWpf()
    {
        var overlayService = new DynamicOverlayService
        {
            Targets =
            [
                new OverlayTarget("preview", "Preview + Logs", true),
                new OverlayTarget("hwnd:100", "Arknights (old)", false, NativeHandle: 0x100, ProcessId: 100, ProcessName: "arknights", WindowTitle: "明日方舟"),
            ],
        };
        await using var fixture = await OverlayFixture.CreateAsync(overlayService: overlayService);
        var taskQueue = fixture.Shell.TaskQueuePage;

        fixture.Runtime.ConfigurationService.CurrentConfig.GlobalValues[LegacyConfigurationKeys.OverlayTarget] =
            JsonValue.Create(OverlayTargetPersistence.Serialize(new OverlayTarget(
                "hwnd:100", "Arknights (old)", false, NativeHandle: 0x100, ProcessId: 100, ProcessName: "arknights", WindowTitle: "明日方舟")));
        fixture.Runtime.ConfigurationService.CurrentConfig.GlobalValues[LegacyConfigurationKeys.OverlayPreviewPinned] =
            JsonValue.Create(bool.FalseString);

        await taskQueue.ReloadOverlayTargetsAsync();
        Assert.Equal("hwnd:100", taskQueue.SelectedOverlayTarget?.Id);

        // The game restarts: the old hwnd/pid are gone and a new window carries the same
        // process identity metadata. Without re-resolution the toggle would fail with
        // OverlayTargetGone; WPF re-runs TryFindWindow on every EnableOverlay instead.
        overlayService.Targets =
        [
            new OverlayTarget("preview", "Preview + Logs", true),
            new OverlayTarget("hwnd:200", "Arknights (restarted)", false, NativeHandle: 0x200, ProcessId: 200, ProcessName: "arknights", WindowTitle: "明日方舟"),
        ];

        await taskQueue.ToggleOverlayAsync();

        Assert.True(taskQueue.OverlayVisible);
        Assert.Equal("hwnd:200", overlayService.SelectedTargetId);
    }

    [Fact]
    public async Task ToggleOverlayAsync_WhenPersistedTargetIsTemporarilyMissing_ShouldNotOverwriteBindingWithFallbackLikeWpf()
    {
        // WPF parity (OverlayViewModel.LoadConfig → SetTargetHwnd(found, persist:false), freeze
        // 23ddd27): enabling the overlay while the persisted target is absent must not rewrite the
        // binding with an automatic fallback window; WPF leaves the config untouched so the game is
        // re-found automatically once it returns.
        var overlayService = new DynamicOverlayService
        {
            Targets =
            [
                new OverlayTarget("preview", "Preview + Logs", true),
                new OverlayTarget("hwnd:300", "Notepad", false, NativeHandle: 0x300, ProcessId: 300, ProcessName: "notepad", WindowTitle: "Untitled - Notepad"),
            ],
        };
        await using var fixture = await OverlayFixture.CreateAsync(overlayService: overlayService);
        var taskQueue = fixture.Shell.TaskQueuePage;

        fixture.Runtime.ConfigurationService.CurrentConfig.GlobalValues[LegacyConfigurationKeys.OverlayTarget] =
            JsonValue.Create(OverlayTargetPersistence.Serialize(new OverlayTarget(
                "hwnd:100", "Arknights", false, NativeHandle: 0x100, ProcessId: 100, ProcessName: "arknights", WindowTitle: "明日方舟")));
        fixture.Runtime.ConfigurationService.CurrentConfig.GlobalValues[LegacyConfigurationKeys.OverlayPreviewPinned] =
            JsonValue.Create(bool.FalseString);

        await taskQueue.ToggleOverlayAsync();

        // The fallback window is used for this session, but the persisted binding stays intact.
        Assert.True(taskQueue.OverlayVisible);
        Assert.Equal("hwnd:300", overlayService.SelectedTargetId);
        var persisted = OverlayTargetPersistence.Load(fixture.Runtime.ConfigurationService.CurrentConfig.GlobalValues);
        Assert.NotNull(persisted);
        Assert.Equal("hwnd:100", persisted!.TargetId);
        Assert.Equal(100, persisted.ProcessId);

        // When the game returns (restarted: new hwnd/pid), the current session keeps its preferred
        // selection (preferredTargetId > persisted identity is a protected invariant), so the
        // fallback stays selected in-session.
        overlayService.Targets =
        [
            new OverlayTarget("preview", "Preview + Logs", true),
            new OverlayTarget("hwnd:300", "Notepad", false, NativeHandle: 0x300, ProcessId: 300, ProcessName: "notepad", WindowTitle: "Untitled - Notepad"),
            new OverlayTarget("hwnd:200", "Arknights (restarted)", false, NativeHandle: 0x200, ProcessId: 200, ProcessName: "arknights", WindowTitle: "明日方舟"),
        ];

        await taskQueue.ReloadOverlayTargetsAsync();

        Assert.Equal("hwnd:300", taskQueue.SelectedOverlayTarget?.Id);

        // A fresh session (no in-session preferred selection) re-resolves from the preserved
        // binding and finds the restarted game by process metadata — the WPF behavior that the
        // config-overwrite would have destroyed.
        OverlaySharedStateRegistry.Get(fixture.Runtime).SelectedTargetId = "preview";
        await taskQueue.ReloadOverlayTargetsAsync();

        Assert.Equal("hwnd:200", taskQueue.SelectedOverlayTarget?.Id);
    }

    [Fact]
    public async Task OverlayAdvancedToggle_WhenPersistedTargetIsTemporarilyMissing_ShouldNotOverwriteBindingWithFallbackLikeWpf()
    {
        var overlayService = new DynamicOverlayService
        {
            Targets =
            [
                new OverlayTarget("preview", "Preview + Logs", true),
                new OverlayTarget("hwnd:300", "Notepad", false, NativeHandle: 0x300, ProcessId: 300, ProcessName: "notepad", WindowTitle: "Untitled - Notepad"),
            ],
        };
        await using var fixture = await OverlayFixture.CreateAsync(overlayService: overlayService);
        var advanced = new OverlayAdvancedPageViewModel(fixture.Runtime);

        fixture.Runtime.ConfigurationService.CurrentConfig.GlobalValues[LegacyConfigurationKeys.OverlayTarget] =
            JsonValue.Create(OverlayTargetPersistence.Serialize(new OverlayTarget(
                "hwnd:100", "Arknights", false, NativeHandle: 0x100, ProcessId: 100, ProcessName: "arknights", WindowTitle: "明日方舟")));

        await advanced.ToggleOverlayAsync();

        Assert.True(advanced.Visible);
        Assert.Equal("hwnd:300", overlayService.SelectedTargetId);
        var persisted = OverlayTargetPersistence.Load(fixture.Runtime.ConfigurationService.CurrentConfig.GlobalValues);
        Assert.NotNull(persisted);
        Assert.Equal("hwnd:100", persisted!.TargetId);
        Assert.Equal(100, persisted.ProcessId);
    }

    [Fact]
    public async Task ToggleOverlayAsync_ShouldAutoMigrateLegacyPreviewSelectionToNativeTarget()
    {
        var overlayService = new ScriptedOverlayService();
        await using var fixture = await OverlayFixture.CreateAsync(overlayService: overlayService);

        fixture.Runtime.ConfigurationService.CurrentConfig.GlobalValues[LegacyConfigurationKeys.OverlayTarget] =
            JsonValue.Create(OverlayTargetPersistence.Serialize(new OverlayTarget("preview", "Preview + Logs", true)));
        fixture.Runtime.ConfigurationService.CurrentConfig.GlobalValues.Remove(LegacyConfigurationKeys.OverlayPreviewPinned);

        await fixture.Shell.TaskQueuePage.ReloadOverlayTargetsAsync();
        Assert.Equal("hwnd:123", fixture.Shell.TaskQueuePage.SelectedOverlayTarget?.Id);

        await fixture.Shell.TaskQueuePage.ToggleOverlayAsync();

        Assert.Equal("hwnd:123", overlayService.SelectedTargetId);
        Assert.True(fixture.Runtime.ConfigurationService.CurrentConfig.GlobalValues.TryGetValue(
            LegacyConfigurationKeys.OverlayPreviewPinned,
            out var previewPinnedNode));
        Assert.Equal(bool.FalseString, previewPinnedNode?.GetValue<string>());
    }

    [Fact]
    public async Task ReloadOverlayTargetsAsync_WhenOverlayIsUnconfigured_ShouldDefaultToPreview()
    {
        var overlayService = new ScriptedOverlayService();
        await using var fixture = await OverlayFixture.CreateAsync(overlayService: overlayService);

        fixture.Runtime.ConfigurationService.CurrentConfig.GlobalValues.Remove(LegacyConfigurationKeys.OverlayTarget);
        fixture.Runtime.ConfigurationService.CurrentConfig.GlobalValues.Remove(LegacyConfigurationKeys.OverlayPreviewPinned);

        await fixture.Shell.TaskQueuePage.ReloadOverlayTargetsAsync();

        Assert.Equal("preview", fixture.Shell.TaskQueuePage.SelectedOverlayTarget?.Id);
    }

    [Fact]
    public async Task AttachWindowConnect_PcConfig_ShouldAttachToArknightsWindowWithConfiguredMethods()
    {
        var bridge = new AttachWindowRecordingBridge();
        var overlay = new StaticOverlayTargetsService(
        [
            new OverlayTarget("preview", "Preview + Logs", true),
            new OverlayTarget("hwnd:1A2B", "明日方舟", false, NativeHandle: 0x1A2B, ProcessId: 42, ProcessName: "arknights", WindowTitle: "明日方舟"),
            new OverlayTarget("hwnd:200", "Other", false, NativeHandle: 0x200, ProcessId: 7, ProcessName: "other", WindowTitle: "Other window"),
        ]);
        await using var fixture = await AttachWindowConnectFixture.CreateAsync(bridge, overlay);
        var connectionInfo = new CoreConnectionInfo(
            string.Empty,
            "PC",
            null,
            new CoreConnectionExtras(
                AttachWindowScreencapMethod: "16",
                AttachWindowMouseMethod: "32",
                AttachWindowKeyboardMethod: "128"),
            TimeSpan.FromSeconds(30));

        var result = await fixture.Connect.ValidateAndConnectAsync(connectionInfo, CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        var request = Assert.Single(bridge.AttachWindowRequests);
        Assert.Equal(new nint(0x1A2B), request.WindowHandle);
        Assert.Equal(16UL, request.ScreencapMethod);
        Assert.Equal(32UL, request.MouseMethod);
        Assert.Equal(128UL, request.KeyboardMethod);
        Assert.Equal(SessionState.Connected, fixture.Session.CurrentState);
        Assert.Empty(bridge.ConnectRequests);
    }

    [Fact]
    public async Task AttachWindowConnect_NoMatchingWindow_ShouldFailWithoutBridgeAttach()
    {
        var bridge = new AttachWindowRecordingBridge();
        var overlay = new StaticOverlayTargetsService(
        [
            new OverlayTarget("preview", "Preview + Logs", true),
            new OverlayTarget("hwnd:200", "Other", false, NativeHandle: 0x200, ProcessId: 7, ProcessName: "other", WindowTitle: "Other window"),
        ]);
        await using var fixture = await AttachWindowConnectFixture.CreateAsync(bridge, overlay);
        var connectionInfo = new CoreConnectionInfo(
            string.Empty,
            "PC",
            null,
            CoreConnectionExtras.Empty,
            TimeSpan.FromSeconds(30));

        var result = await fixture.Connect.ValidateAndConnectAsync(connectionInfo, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(bridge.AttachWindowRequests);
        Assert.NotEqual(SessionState.Connected, fixture.Session.CurrentState);
    }

    [Fact]
    public async Task AttachWindowConnect_MultipleMatchingWindows_ShouldUseFirstLikeWpf()
    {
        var bridge = new AttachWindowRecordingBridge();
        var overlay = new StaticOverlayTargetsService(
        [
            new OverlayTarget("preview", "Preview + Logs", true),
            new OverlayTarget("hwnd:100", "first", false, NativeHandle: 0x100, ProcessId: 1, ProcessName: "arknights", WindowTitle: "明日方舟"),
            new OverlayTarget("hwnd:200", "second", false, NativeHandle: 0x200, ProcessId: 2, ProcessName: "arknights", WindowTitle: "明日方舟"),
        ]);
        await using var fixture = await AttachWindowConnectFixture.CreateAsync(bridge, overlay);
        var connectionInfo = new CoreConnectionInfo(
            string.Empty,
            "PC",
            null,
            CoreConnectionExtras.Empty,
            TimeSpan.FromSeconds(30));

        var result = await fixture.Connect.ValidateAndConnectAsync(connectionInfo, CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        var request = Assert.Single(bridge.AttachWindowRequests);
        Assert.Equal(new nint(0x100), request.WindowHandle);
        // WPF defaults: FramePool screencap (2), PostMessageWithCursorPos mouse/keyboard (64).
        Assert.Equal(2UL, request.ScreencapMethod);
        Assert.Equal(64UL, request.MouseMethod);
        Assert.Equal(64UL, request.KeyboardMethod);
    }

    [Fact]
    public async Task AttachWindowConnect_InvalidMethodValues_ShouldFallBackToWpfDefaults()
    {
        var bridge = new AttachWindowRecordingBridge();
        var overlay = new StaticOverlayTargetsService(
        [
            new OverlayTarget("hwnd:100", "first", false, NativeHandle: 0x100, ProcessId: 1, ProcessName: "arknights", WindowTitle: "明日方舟"),
        ]);
        await using var fixture = await AttachWindowConnectFixture.CreateAsync(bridge, overlay);
        var connectionInfo = new CoreConnectionInfo(
            string.Empty,
            "PC",
            null,
            new CoreConnectionExtras(
                AttachWindowScreencapMethod: "not-a-number",
                AttachWindowMouseMethod: " ",
                AttachWindowKeyboardMethod: "-1x"),
            TimeSpan.FromSeconds(30));

        var result = await fixture.Connect.ValidateAndConnectAsync(connectionInfo, CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        var request = Assert.Single(bridge.AttachWindowRequests);
        Assert.Equal(2UL, request.ScreencapMethod);
        Assert.Equal(64UL, request.MouseMethod);
        Assert.Equal(64UL, request.KeyboardMethod);
    }

    [Fact]
    public async Task AttachWindowConnect_BridgeFailure_ShouldSurfaceAttachWindowFailure()
    {
        var bridge = new AttachWindowRecordingBridge
        {
            AttachWindowResult = CoreResult<bool>.Fail(new CoreError(CoreErrorCode.ConnectFailed, "native attach failed")),
        };
        var overlay = new StaticOverlayTargetsService(
        [
            new OverlayTarget("hwnd:100", "first", false, NativeHandle: 0x100, ProcessId: 1, ProcessName: "arknights", WindowTitle: "明日方舟"),
        ]);
        await using var fixture = await AttachWindowConnectFixture.CreateAsync(bridge, overlay);
        var connectionInfo = new CoreConnectionInfo(
            string.Empty,
            "PC",
            null,
            CoreConnectionExtras.Empty,
            TimeSpan.FromSeconds(30));

        var result = await fixture.Connect.ValidateAndConnectAsync(connectionInfo, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Single(bridge.AttachWindowRequests);
        Assert.Equal(SessionState.Idle, fixture.Session.CurrentState);
    }

    [Fact]
    public async Task AttachWindowConnect_NonPcConfig_ShouldNotEnterAttachPath()
    {
        var bridge = new AttachWindowRecordingBridge();
        var overlay = new StaticOverlayTargetsService(
        [
            new OverlayTarget("hwnd:100", "first", false, NativeHandle: 0x100, ProcessId: 1, ProcessName: "arknights", WindowTitle: "明日方舟"),
        ]);
        await using var fixture = await AttachWindowConnectFixture.CreateAsync(bridge, overlay);
        var connectionInfo = new CoreConnectionInfo(
            string.Empty,
            "General",
            null,
            CoreConnectionExtras.Empty,
            TimeSpan.FromSeconds(30));

        var result = await fixture.Connect.ValidateAndConnectAsync(connectionInfo, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(bridge.AttachWindowRequests);
    }

    [Fact]
    public async Task AttachWindowConnect_SuccessPath_ShouldRaiseLocalizedUserNotices()
    {
        var bridge = new AttachWindowRecordingBridge();
        var overlay = new StaticOverlayTargetsService(
        [
            new OverlayTarget("hwnd:100", "arknights", false, NativeHandle: 0x100, ProcessId: 1, ProcessName: "arknights", WindowTitle: "明日方舟"),
        ]);
        await using var fixture = await AttachWindowConnectFixture.CreateAsync(bridge, overlay);
        fixture.Runtime.ConfigurationService.CurrentConfig.GlobalValues[LegacyConfigurationKeys.Localization] =
            JsonValue.Create("en-us");
        var notices = new List<(string Message, string Level)>();
        fixture.Connect.AttachWindowUserNotice += (message, level) => notices.Add((message, level));
        var connectionInfo = new CoreConnectionInfo(
            string.Empty,
            "PC",
            null,
            CoreConnectionExtras.Empty,
            TimeSpan.FromSeconds(30));

        var result = await fixture.Connect.ValidateAndConnectAsync(connectionInfo, CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains(notices, notice =>
            notice.Level == "WARN"
            && notice.Message.Contains("PC connection (experimental feature", StringComparison.Ordinal));
        Assert.Contains(notices, notice =>
            notice.Level == "INFO"
            && notice.Message.Contains("Found window \"明日方舟\"", StringComparison.Ordinal));
        Assert.DoesNotContain(notices, notice => notice.Level == "ERROR");
    }

    [Fact]
    public async Task AttachWindowConnect_NoMatchingWindow_ShouldRaiseLocalizedNotFoundNotice()
    {
        var bridge = new AttachWindowRecordingBridge();
        var overlay = new StaticOverlayTargetsService(
        [
            new OverlayTarget("hwnd:200", "Other", false, NativeHandle: 0x200, ProcessId: 7, ProcessName: "other", WindowTitle: "Other window"),
        ]);
        await using var fixture = await AttachWindowConnectFixture.CreateAsync(bridge, overlay);
        fixture.Runtime.ConfigurationService.CurrentConfig.GlobalValues[LegacyConfigurationKeys.Localization] =
            JsonValue.Create("zh-cn");
        var notices = new List<(string Message, string Level)>();
        fixture.Connect.AttachWindowUserNotice += (message, level) => notices.Add((message, level));
        var connectionInfo = new CoreConnectionInfo(
            string.Empty,
            "PC",
            null,
            CoreConnectionExtras.Empty,
            TimeSpan.FromSeconds(30));

        var result = await fixture.Connect.ValidateAndConnectAsync(connectionInfo, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(bridge.AttachWindowRequests);
        Assert.Contains(notices, notice =>
            notice.Level == "WARN"
            && notice.Message.Contains("连接 PC 端", StringComparison.Ordinal));
        Assert.Contains(notices, notice =>
            notice.Level == "ERROR"
            && notice.Message.Contains("未找到窗口", StringComparison.Ordinal)
            && notice.Message.Contains("明日方舟", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AttachWindowConnect_MultipleMatchingWindows_ShouldRaiseLocalizedMultipleFoundNotice()
    {
        var bridge = new AttachWindowRecordingBridge();
        var overlay = new StaticOverlayTargetsService(
        [
            new OverlayTarget("hwnd:100", "first", false, NativeHandle: 0x100, ProcessId: 1, ProcessName: "arknights", WindowTitle: "明日方舟"),
            new OverlayTarget("hwnd:200", "second", false, NativeHandle: 0x200, ProcessId: 2, ProcessName: "arknights", WindowTitle: "明日方舟"),
        ]);
        await using var fixture = await AttachWindowConnectFixture.CreateAsync(bridge, overlay);
        fixture.Runtime.ConfigurationService.CurrentConfig.GlobalValues[LegacyConfigurationKeys.Localization] =
            JsonValue.Create("en-us");
        var notices = new List<(string Message, string Level)>();
        fixture.Connect.AttachWindowUserNotice += (message, level) => notices.Add((message, level));
        var connectionInfo = new CoreConnectionInfo(
            string.Empty,
            "PC",
            null,
            CoreConnectionExtras.Empty,
            TimeSpan.FromSeconds(30));

        var result = await fixture.Connect.ValidateAndConnectAsync(connectionInfo, CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(new nint(0x100), Assert.Single(bridge.AttachWindowRequests).WindowHandle);
        Assert.Contains(notices, notice =>
            notice.Level == "INFO"
            && notice.Message.Contains("Found 2 \"明日方舟\" windows, using the first one", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AttachWindowConnect_BridgeFailure_ShouldRaiseLocalizedAttachWindowFailedNotice()
    {
        var bridge = new AttachWindowRecordingBridge
        {
            AttachWindowResult = CoreResult<bool>.Fail(new CoreError(CoreErrorCode.ConnectFailed, "native attach failed")),
        };
        var overlay = new StaticOverlayTargetsService(
        [
            new OverlayTarget("hwnd:100", "first", false, NativeHandle: 0x100, ProcessId: 1, ProcessName: "arknights", WindowTitle: "明日方舟"),
        ]);
        await using var fixture = await AttachWindowConnectFixture.CreateAsync(bridge, overlay);
        fixture.Runtime.ConfigurationService.CurrentConfig.GlobalValues[LegacyConfigurationKeys.Localization] =
            JsonValue.Create("zh-cn");
        var notices = new List<(string Message, string Level)>();
        fixture.Connect.AttachWindowUserNotice += (message, level) => notices.Add((message, level));
        var connectionInfo = new CoreConnectionInfo(
            string.Empty,
            "PC",
            null,
            CoreConnectionExtras.Empty,
            TimeSpan.FromSeconds(30));

        var result = await fixture.Connect.ValidateAndConnectAsync(connectionInfo, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Single(bridge.AttachWindowRequests);
        Assert.Contains(notices, notice =>
            notice.Level == "ERROR"
            && notice.Message.Contains("AttachWindow 绑定窗口失败", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AttachWindowConnect_UserNotices_ShouldReachTaskQueueLogCardsAndOverlayLogs()
    {
        // WPF parity (freeze 23ddd27 AsstProxy.AsstAttachWindowConnect -> TaskQueueViewModel.AddLog;
        // OverlayViewModel binds the same LogItemViewModels): PC window binding prompts are visible
        // in the main task queue log panel and in the overlay log.
        var bridge = new AttachWindowRecordingBridge
        {
            AttachWindowResult = CoreResult<bool>.Fail(new CoreError(CoreErrorCode.ConnectFailed, "native attach failed")),
        };
        var overlay = new StaticOverlayTargetsService(
        [
            new OverlayTarget("hwnd:100", "arknights", false, NativeHandle: 0x100, ProcessId: 1, ProcessName: "arknights", WindowTitle: "明日方舟"),
        ]);
        await using var fixture = await AttachWindowConnectFixture.CreateAsync(bridge, overlay);
        fixture.Runtime.ConfigurationService.CurrentConfig.GlobalValues[LegacyConfigurationKeys.Localization] =
            JsonValue.Create("zh-cn");
        var shell = new MainShellViewModel(fixture.Runtime, NoOpAppDialogService.Instance);
        try
        {
            var connectionInfo = new CoreConnectionInfo(
                string.Empty,
                "PC",
                null,
                CoreConnectionExtras.Empty,
                TimeSpan.FromSeconds(30));

            var result = await fixture.Connect.ValidateAndConnectAsync(connectionInfo, CancellationToken.None);

            Assert.False(result.Success);
            var delivered = await WaitUntilAsync(() =>
            {
                Dispatcher.UIThread.RunJobs();
                return shell.TaskQueuePage.OverlayLogs.Any(entry => entry.Level == "ERROR");
            });
            Assert.True(delivered, "AttachWindow notices did not reach the overlay log.");
            Assert.Contains(
                shell.TaskQueuePage.LogCards.SelectMany(card => card.Items),
                entry => entry.Level == "WARN"
                    && entry.Content.Contains("连接 PC 端", StringComparison.Ordinal));
            Assert.Contains(
                shell.TaskQueuePage.LogCards.SelectMany(card => card.Items),
                entry => entry.Level == "INFO"
                    && entry.Content.Contains("找到窗口 \"明日方舟\"", StringComparison.Ordinal));
            Assert.Contains(
                shell.TaskQueuePage.LogCards.SelectMany(card => card.Items),
                entry => entry.Level == "ERROR"
                    && entry.Content.Contains("AttachWindow 绑定窗口失败", StringComparison.Ordinal));
            Assert.Contains(
                shell.TaskQueuePage.OverlayLogs,
                entry => entry.Level == "ERROR"
                    && entry.Content.Contains("AttachWindow 绑定窗口失败", StringComparison.Ordinal));
        }
        finally
        {
            TestShellCleanup.StopTimerScheduler(shell);
        }
    }

    [Fact]
    public async Task SessionAttachWindow_ZeroHandle_ShouldFailFastWithoutBridgeCall()
    {
        var bridge = new AttachWindowRecordingBridge();
        var overlay = new StaticOverlayTargetsService([]);
        await using var fixture = await AttachWindowConnectFixture.CreateAsync(bridge, overlay);
        var connectionInfo = new CoreConnectionInfo(
            string.Empty,
            "PC",
            null,
            CoreConnectionExtras.Empty,
            TimeSpan.FromSeconds(30));

        var result = await fixture.Session.AttachWindowAsync(
            connectionInfo,
            new CoreAttachWindowRequest(nint.Zero, 2, 64, 64),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(CoreErrorCode.InvalidRequest, result.Error?.Code);
        Assert.Empty(bridge.AttachWindowRequests);
        Assert.Equal(SessionState.Idle, fixture.Session.CurrentState);
    }

    [Fact]
    public async Task AttachWindowConnect_WhenExactTitleFinderAvailable_ShouldBypassCappedPickerEnumeration()
    {
        var bridge = new AttachWindowRecordingBridge();
        // The picker enumeration (QueryTargetsAsync) intentionally does not see the game window
        // here (80-target cap / trimmed titles); the exact-title finder still finds it, mirroring
        // WPF AsstProxy.FindWindowsByName (no cap, raw exact title match).
        var overlay = new ExactTitleFinderOverlayService(
            title => string.Equals(title, "明日方舟", StringComparison.Ordinal) ? [(nint)0x1A2B] : []);
        await using var fixture = await AttachWindowConnectFixture.CreateAsync(bridge, overlay);
        var connectionInfo = new CoreConnectionInfo(
            string.Empty,
            "PC",
            null,
            CoreConnectionExtras.Empty,
            TimeSpan.FromSeconds(30));

        var result = await fixture.Connect.ValidateAndConnectAsync(connectionInfo, CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        var request = Assert.Single(bridge.AttachWindowRequests);
        Assert.Equal(new nint(0x1A2B), request.WindowHandle);
        Assert.Equal(0, overlay.QueryTargetsCallCount);
    }

    [Fact]
    public async Task BuildConnectionCandidates_PcConfig_ShouldReturnSingleCandidateThatKeepsConfiguredAddress()
    {
        var bridge = new AttachWindowRecordingBridge();
        var overlay = new StaticOverlayTargetsService([]);
        await using var fixture = await AttachWindowConnectFixture.CreateAsync(bridge, overlay);

        var result = fixture.Connect.BuildConnectionCandidates(
            "127.0.0.1:5555",
            "PC",
            null,
            new CoreConnectionExtras(AttachWindowScreencapMethod: "2"),
            autoDetect: true,
            alwaysAutoDetect: false,
            includeConfiguredAddress: true,
            timeout: TimeSpan.FromSeconds(30));

        Assert.True(result.Success, result.Message);
        var candidate = Assert.Single(result.Value!);
        Assert.Equal("PC", candidate.ConnectConfig);
        Assert.Equal("127.0.0.1:5555", candidate.Address);
        Assert.Null(candidate.AdbPath);
    }

    [Fact]
    public async Task TaskQueueConnect_AttachWindowMode_ShouldSkipAdbRecoveryLikeWpf()
    {
        var bridge = new AttachWindowRecordingBridge();
        var overlay = new StaticOverlayTargetsService([]);
        await using var fixture = await AttachWindowConnectFixture.CreateAsync(bridge, overlay);
        var sharedState = new ConnectionGameSharedStateViewModel
        {
            ConnectConfig = "PC",
            RetryOnDisconnected = true,
            AllowAdbRestart = true,
            AllowAdbHardRestart = true,
        };
        var vm = new TaskQueuePageViewModel(fixture.Runtime, sharedState);
        await vm.InitializeAsync();

        var method = typeof(TaskQueuePageViewModel).GetMethod(
            "TryConnectWithRetryAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = (Task<UiOperationResult>)method!.Invoke(vm, [CancellationToken.None, null])!;
        var result = await task;

        Assert.False(result.Success);
        // WPF parity: PC window-binding failure must not run emulator-start / ADB recovery logs.
        Assert.DoesNotContain(vm.LogCards, card =>
            card.PrimaryContent.Contains("正在尝试", StringComparison.Ordinal)
            || card.PrimaryContent.Contains("Trying to", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("2020-01-01", "2026-10-04", true)]
    [InlineData("2024-10-03", "2026-10-04", true)]
    [InlineData("2024-10-04", "2026-10-04", false)]
    [InlineData("2026-10-04", "2026-10-04", false)]
    public void GpuDriverOutdatedCheck_ShouldMatchWpfTwoYearRule(string driverDate, string now, bool expected)
    {
        // WPF AsstProxy.Init warns when the selected GPU driver date is over two years old.
        Assert.Equal(
            expected,
            ResourceWorkflowService.IsGpuDriverOutdated(
                DateTime.Parse(driverDate, System.Globalization.CultureInfo.InvariantCulture),
                DateTime.Parse(now, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void BuildOverlayPickerItems_ShouldSortByProcessNameThenTitleLikeWpfPicker()
    {
        var targets = new[]
        {
            new OverlayTarget("preview", "Preview + Logs", true),
            new OverlayTarget("hwnd:300", "Game B - zeta - 3", false, NativeHandle: 0x300, ProcessId: 3, ProcessName: "zeta", WindowTitle: "Game B"),
            new OverlayTarget("hwnd:100", "Game A - emu - 1", false, NativeHandle: 0x100, ProcessId: 1, ProcessName: "emu", WindowTitle: "Game A"),
            new OverlayTarget("hwnd:400", "Other - zeta - 4", false, NativeHandle: 0x400, ProcessId: 4, ProcessName: "zeta", WindowTitle: "AAA Other"),
            new OverlayTarget("hwnd:200", "Mystery (pid:2)", false, NativeHandle: 0x200, ProcessId: 2, ProcessName: null, WindowTitle: "Mystery"),
        };

        // WPF parity (ProcessPickerDialogViewModel, freeze 23ddd27): OrderBy(ProcessName).ThenBy(Title),
        // with "(unknown)" when the process name cannot be resolved; the preview pseudo-target is not listed.
        var items = TaskQueuePageViewModel.BuildOverlayPickerItems(targets);

        Assert.Equal(["hwnd:200", "hwnd:100", "hwnd:400", "hwnd:300"], items.Select(item => item.Id).ToArray());
    }

    private sealed class OverlayFixture : IAsyncDisposable
    {
        private OverlayFixture(string root, MAAUnifiedRuntime runtime, MainShellViewModel shell)
        {
            Root = root;
            Runtime = runtime;
            Shell = shell;
        }

        public string Root { get; }

        public MAAUnifiedRuntime Runtime { get; }

        public MainShellViewModel Shell { get; }

        public static async Task<OverlayFixture> CreateAsync(
            IOverlayCapabilityService? overlayService = null,
            ITrayService? trayService = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "maa-unified-overlay-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

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
                TrayService = trayService ?? new NoOpTrayService(),
                NotificationService = new NoOpNotificationService(),
                HotkeyService = new NoOpGlobalHotkeyService(),
                AutostartService = new NoOpAutostartService(),
                FileDialogService = new NoOpFileDialogService(),
                OverlayService = overlayService ?? new NoOpOverlayCapabilityService(),
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
                RemoteControlFeatureService = new RemoteControlFeatureService(),
                PlatformCapabilityService = capability,
                OverlayFeatureService = new OverlayFeatureService(capability),
                NotificationProviderFeatureService = new NotificationProviderFeatureService(),
                SettingsFeatureService = new SettingsFeatureService(config, capability, diagnostics),
                ConfigurationProfileFeatureService = new ConfigurationProfileFeatureService(config),
                DialogFeatureService = new DialogFeatureService(diagnostics),
                PostActionFeatureService = new PostActionFeatureService(
                    config,
                    diagnostics,
                    platform.PostActionExecutorService),
                AppLifecycleService = new NoOpAppLifecycleService(),
            };

            return new OverlayFixture(root, runtime, new MainShellViewModel(runtime, NoOpAppDialogService.Instance));
        }

        public async ValueTask DisposeAsync()
        {
            TestShellCleanup.StopTimerScheduler(Shell);
            await Runtime.DisposeAsync();

            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // Ignore cleanup failures for temp directories.
            }
        }
    }

    private sealed class DynamicOverlayService : IOverlayCapabilityService
    {
        public IReadOnlyList<OverlayTarget> Targets { get; set; } = [new OverlayTarget("preview", "Preview + Logs", true)];

        public string SelectedTargetId { get; private set; } = "preview";

        public PlatformCapabilityStatus Capability { get; } = new(
            Supported: true,
            Message: "dynamic overlay",
            Provider: "dynamic-overlay",
            HasFallback: true,
            FallbackMode: "preview");

        public event EventHandler<OverlayStateChangedEvent>? OverlayStateChanged
        {
            add { }
            remove { }
        }

        public Task<PlatformOperationResult> BindHostWindowAsync(
            nint hostWindowHandle,
            bool clickThrough,
            double opacity,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(PlatformOperation.NativeSuccess(Capability.Provider, "bound", "overlay.bind-host"));
        }

        public Task<PlatformOperationResult<IReadOnlyList<OverlayTarget>>> QueryTargetsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(PlatformOperation.NativeSuccess(Capability.Provider, Targets, "targets", "overlay.query-targets"));
        }

        public Task<PlatformOperationResult> SelectTargetAsync(string targetId, CancellationToken cancellationToken = default)
        {
            if (!string.Equals(targetId, "preview", StringComparison.OrdinalIgnoreCase)
                && Targets.All(target => !string.Equals(target.Id, targetId, StringComparison.Ordinal)))
            {
                return Task.FromResult(PlatformOperation.Failed(
                    Capability.Provider,
                    $"Overlay target is unavailable: {targetId}",
                    PlatformErrorCodes.OverlayTargetGone,
                    "overlay.select-target"));
            }

            SelectedTargetId = targetId;
            return Task.FromResult(PlatformOperation.NativeSuccess(Capability.Provider, "selected", "overlay.select-target"));
        }

        public Task<PlatformOperationResult> SetVisibleAsync(bool visible, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(PlatformOperation.NativeSuccess(Capability.Provider, "visibility", "overlay.set-visible"));
        }
    }

    private sealed class ExactTitleFinderOverlayService(Func<string, IReadOnlyList<nint>> finder)
        : IOverlayCapabilityService, IAttachWindowTargetFinder
    {
        public int QueryTargetsCallCount { get; private set; }

        public PlatformCapabilityStatus Capability { get; } = new(
            Supported: true,
            Message: "finder overlay",
            Provider: "finder-overlay",
            HasFallback: true,
            FallbackMode: "preview");

        public event EventHandler<OverlayStateChangedEvent>? OverlayStateChanged
        {
            add { }
            remove { }
        }

        public IReadOnlyList<nint> FindVisibleWindowsByExactTitle(string exactTitle)
            => finder(exactTitle);

        public Task<PlatformOperationResult> BindHostWindowAsync(
            nint hostWindowHandle,
            bool clickThrough,
            double opacity,
            CancellationToken cancellationToken = default)
            => Task.FromResult(PlatformOperation.NativeSuccess(Capability.Provider, "bound", "overlay.bind-host"));

        public Task<PlatformOperationResult<IReadOnlyList<OverlayTarget>>> QueryTargetsAsync(CancellationToken cancellationToken = default)
        {
            QueryTargetsCallCount++;
            IReadOnlyList<OverlayTarget> targets = [new OverlayTarget("preview", "Preview + Logs", true)];
            return Task.FromResult(PlatformOperation.NativeSuccess(Capability.Provider, targets, "targets", "overlay.query-targets"));
        }

        public Task<PlatformOperationResult> SelectTargetAsync(string targetId, CancellationToken cancellationToken = default)
            => Task.FromResult(PlatformOperation.NativeSuccess(Capability.Provider, "selected", "overlay.select-target"));

        public Task<PlatformOperationResult> SetVisibleAsync(bool visible, CancellationToken cancellationToken = default)
            => Task.FromResult(PlatformOperation.NativeSuccess(Capability.Provider, "visibility", "overlay.set-visible"));
    }

    private sealed class ScriptedOverlayService : IOverlayCapabilityService
    {
        private string _selectedTargetId = "preview";

        public PlatformCapabilityStatus Capability { get; } = new(
            Supported: true,
            Message: "scripted overlay",
            Provider: "scripted-overlay",
            HasFallback: true,
            FallbackMode: "preview");

        public OverlayRuntimeMode VisibleMode { get; set; } = OverlayRuntimeMode.Native;

        public string VisibleMessage { get; set; } = "Overlay attached to native target.";

        public bool EmitStateOnVisibilityToggle { get; set; } = true;

        public string SelectedTargetId => _selectedTargetId;

        public event EventHandler<OverlayStateChangedEvent>? OverlayStateChanged;

        public Task<PlatformOperationResult> BindHostWindowAsync(
            nint hostWindowHandle,
            bool clickThrough,
            double opacity,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(PlatformOperation.NativeSuccess(Capability.Provider, "bound", "overlay.bind-host"));
        }

        public Task<PlatformOperationResult<IReadOnlyList<OverlayTarget>>> QueryTargetsAsync(CancellationToken cancellationToken = default)
        {
            IReadOnlyList<OverlayTarget> targets =
            [
                new OverlayTarget("preview", "Preview + Logs", true),
                new OverlayTarget("hwnd:123", "Arknights - 123", false, NativeHandle: 0x123, ProcessId: 123, ProcessName: "emu", WindowTitle: "Arknights"),
            ];
            return Task.FromResult(PlatformOperation.NativeSuccess(Capability.Provider, targets, "targets", "overlay.query-targets"));
        }

        public Task<PlatformOperationResult> SelectTargetAsync(string targetId, CancellationToken cancellationToken = default)
        {
            _selectedTargetId = string.IsNullOrWhiteSpace(targetId) ? "preview" : targetId;
            return Task.FromResult(PlatformOperation.NativeSuccess(Capability.Provider, "selected", "overlay.select-target"));
        }

        public Task<PlatformOperationResult> SetVisibleAsync(bool visible, CancellationToken cancellationToken = default)
        {
            if (!visible)
            {
                if (EmitStateOnVisibilityToggle)
                {
                    Emit(new OverlayStateChangedEvent(
                        OverlayRuntimeMode.Hidden,
                        Visible: false,
                        TargetId: _selectedTargetId,
                        Action: "hide",
                        Message: "Overlay hidden.",
                        Timestamp: DateTimeOffset.UtcNow,
                        Provider: Capability.Provider,
                        UsedFallback: false,
                        ErrorCode: null));
                }

                return Task.FromResult(PlatformOperation.NativeSuccess(Capability.Provider, "hidden", "overlay.set-visible"));
            }

            var usedFallback = VisibleMode == OverlayRuntimeMode.Preview;
            if (EmitStateOnVisibilityToggle)
            {
                Emit(new OverlayStateChangedEvent(
                    VisibleMode,
                    Visible: true,
                    TargetId: VisibleMode == OverlayRuntimeMode.Preview ? "preview" : _selectedTargetId,
                    Action: usedFallback ? "fallback-enter" : "show-native",
                    Message: VisibleMessage,
                    Timestamp: DateTimeOffset.UtcNow,
                    Provider: Capability.Provider,
                    UsedFallback: usedFallback,
                    ErrorCode: usedFallback ? PlatformErrorCodes.OverlayPreviewMode : null));
            }

            return Task.FromResult(
                usedFallback
                    ? PlatformOperation.FallbackSuccess(Capability.Provider, VisibleMessage, "overlay.set-visible", PlatformErrorCodes.OverlayPreviewMode)
                    : PlatformOperation.NativeSuccess(Capability.Provider, VisibleMessage, "overlay.set-visible"));
        }

        public void Emit(OverlayStateChangedEvent e)
        {
            if (!string.IsNullOrWhiteSpace(e.TargetId))
            {
                _selectedTargetId = e.TargetId;
            }

            OverlayStateChanged?.Invoke(this, e);
        }
    }

    private sealed class FakeBridge : IMaaCoreBridge
    {
        private readonly Channel<CoreCallbackEvent> _callbackChannel = Channel.CreateUnbounded<CoreCallbackEvent>();

        public Task<CoreResult<CoreInitializeInfo>> InitializeAsync(
            CoreInitializeRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<CoreInitializeInfo>.Ok(
                new CoreInitializeInfo(request.BaseDirectory, "fake", "fake", request.ClientType)));
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
            await foreach (var callback in _callbackChannel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return callback;
            }
        }

        public ValueTask DisposeAsync()
        {
            _callbackChannel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> predicate, int retry = 40, int delayMs = 25)
    {
        for (var i = 0; i < retry; i++)
        {
            if (predicate())
            {
                return true;
            }

            await Task.Delay(delayMs);
        }

        return false;
    }

    private sealed class AttachWindowConnectFixture : IAsyncDisposable
    {
        private AttachWindowConnectFixture(
            string root,
            MAAUnifiedRuntime runtime,
            UnifiedSessionService session,
            ConnectFeatureService connect)
        {
            Root = root;
            Runtime = runtime;
            Session = session;
            Connect = connect;
        }

        public string Root { get; }

        public MAAUnifiedRuntime Runtime { get; }

        public UnifiedSessionService Session { get; }

        public ConnectFeatureService Connect { get; }

        public static async Task<AttachWindowConnectFixture> CreateAsync(
            AttachWindowRecordingBridge bridge,
            IOverlayCapabilityService overlayService)
        {
            var root = Path.Combine(Path.GetTempPath(), "maa-unified-attachwindow-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            var log = new UiLogService();
            var diagnostics = new UiDiagnosticsService(root, log);
            var config = new UnifiedConfigurationService(
                new AvaloniaJsonConfigStore(root),
                new GuiNewJsonConfigImporter(),
                new GuiJsonConfigImporter(),
                log,
                root);
            await config.LoadOrBootstrapAsync();

            var session = new UnifiedSessionService(bridge, config, log, new SessionStateMachine());
            var platform = new PlatformServiceBundle
            {
                TrayService = new NoOpTrayService(),
                NotificationService = new NoOpNotificationService(),
                HotkeyService = new NoOpGlobalHotkeyService(),
                AutostartService = new NoOpAutostartService(),
                FileDialogService = new NoOpFileDialogService(),
                OverlayService = overlayService,
                PostActionExecutorService = new NoOpPostActionExecutorService(),
            };

            var capability = new PlatformCapabilityFeatureService(platform, diagnostics);
            var connect = new ConnectFeatureService(
                session,
                config,
                log,
                bridge,
                root,
                overlayTargetProvider: overlayService);
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
                RemoteControlFeatureService = new RemoteControlFeatureService(),
                PlatformCapabilityService = capability,
                OverlayFeatureService = new OverlayFeatureService(capability),
                NotificationProviderFeatureService = new NotificationProviderFeatureService(),
                SettingsFeatureService = new SettingsFeatureService(config, capability, diagnostics),
                ConfigurationProfileFeatureService = new ConfigurationProfileFeatureService(config),
                DialogFeatureService = new DialogFeatureService(diagnostics),
                PostActionFeatureService = new PostActionFeatureService(
                    config,
                    diagnostics,
                    platform.PostActionExecutorService),
                AppLifecycleService = new NoOpAppLifecycleService(),
            };

            return new AttachWindowConnectFixture(root, runtime, session, connect);
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
                // Ignore cleanup failures for temp directories.
            }
        }
    }

    private sealed class StaticOverlayTargetsService(IReadOnlyList<OverlayTarget> targets) : IOverlayCapabilityService
    {
        public PlatformCapabilityStatus Capability { get; } = new(
            Supported: true,
            Message: "static overlay targets",
            Provider: "static-overlay",
            HasFallback: true,
            FallbackMode: "preview");

        public event EventHandler<OverlayStateChangedEvent>? OverlayStateChanged
        {
            add { }
            remove { }
        }

        public Task<PlatformOperationResult> BindHostWindowAsync(
            nint hostWindowHandle,
            bool clickThrough,
            double opacity,
            CancellationToken cancellationToken = default)
            => Task.FromResult(PlatformOperation.NativeSuccess(Capability.Provider, "bound", "overlay.bind-host"));

        public Task<PlatformOperationResult<IReadOnlyList<OverlayTarget>>> QueryTargetsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(PlatformOperation.NativeSuccess(Capability.Provider, targets, "targets", "overlay.query-targets"));

        public Task<PlatformOperationResult> SelectTargetAsync(string targetId, CancellationToken cancellationToken = default)
            => Task.FromResult(PlatformOperation.NativeSuccess(Capability.Provider, "selected", "overlay.select-target"));

        public Task<PlatformOperationResult> SetVisibleAsync(bool visible, CancellationToken cancellationToken = default)
            => Task.FromResult(PlatformOperation.NativeSuccess(Capability.Provider, "visibility", "overlay.set-visible"));
    }

    private sealed class AttachWindowRecordingBridge : IMaaCoreBridge
    {
        private readonly Channel<CoreCallbackEvent> _callbackChannel = Channel.CreateUnbounded<CoreCallbackEvent>();

        public List<CoreAttachWindowRequest> AttachWindowRequests { get; } = [];

        public List<CoreConnectionInfo> ConnectRequests { get; } = [];

        public CoreResult<bool> AttachWindowResult { get; set; } = CoreResult<bool>.Ok(true);

        public Task<CoreResult<CoreInitializeInfo>> InitializeAsync(
            CoreInitializeRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CoreResult<CoreInitializeInfo>.Ok(
                new CoreInitializeInfo(request.BaseDirectory, "fake", "fake", request.ClientType)));
        }

        public Task<CoreResult<bool>> ConnectAsync(CoreConnectionInfo connectionInfo, CancellationToken cancellationToken = default)
        {
            ConnectRequests.Add(connectionInfo);
            return Task.FromResult(CoreResult<bool>.Fail(new CoreError(
                CoreErrorCode.InvalidRequest,
                "ADB connect must not run for PC attach-window mode.")));
        }

        public Task<CoreResult<int>> AppendTaskAsync(CoreTaskRequest task, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<int>.Ok(1));

        public Task<CoreResult<bool>> StartAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Ok(true));

        public Task<CoreResult<bool>> StopAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Ok(true));

        public Task<CoreResult<CoreRuntimeStatus>> GetRuntimeStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<CoreRuntimeStatus>.Ok(new CoreRuntimeStatus(true, false, false)));

        public Task<CoreResult<bool>> AttachWindowAsync(CoreAttachWindowRequest request, CancellationToken cancellationToken = default)
        {
            AttachWindowRequests.Add(request);
            return Task.FromResult(AttachWindowResult);
        }

        public Task<CoreResult<byte[]>> GetImageAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<byte[]>.Fail(new CoreError(CoreErrorCode.GetImageFailed, "not supported")));

        public async IAsyncEnumerable<CoreCallbackEvent> CallbackStreamAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var callback in _callbackChannel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return callback;
            }
        }

        public ValueTask DisposeAsync()
        {
            _callbackChannel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
