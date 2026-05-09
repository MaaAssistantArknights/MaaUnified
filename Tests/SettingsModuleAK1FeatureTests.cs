using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using MAAUnified.App.ViewModels;
using MAAUnified.App.ViewModels.Settings;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using MAAUnified.Compat.Constants;
using MAAUnified.CoreBridge;
using MAAUnified.Platform;

namespace MAAUnified.Tests;

[Collection("MainShellSerial")]
public sealed class SettingsModuleAK1FeatureTests
{
    private static readonly string[] _connectionKeys =
    [
        "ConnectAddress",
        "ConnectConfig",
        "AdbPath",
        "ClientType",
        "StartGame",
        "TouchMode",
        "AutoDetect",
        "AlwaysAutoDetect",
        "RetryOnDisconnected",
    ];

    [Fact]
    public void ConnectionGameSharedState_DefaultAdbPath_ShouldBeSystemAdb()
    {
        var state = new ConnectionGameSharedStateViewModel();

        Assert.Equal("adb", state.AdbPath);
    }

    [Fact]
    public void ConnectionGameProfileSync_ReadFromEmptyProfile_ShouldUseSystemAdbDefault()
    {
        var profile = new UnifiedProfile();
        var state = new ConnectionGameSharedStateViewModel
        {
            AdbPath = string.Empty,
        };

        ConnectionGameProfileSync.ReadFromProfile(profile, state, tolerateMissing: false);

        Assert.Equal("adb", state.AdbPath);
    }

    [Fact]
    public void ConnectionGameSharedState_SelectedOptions_WithUnknownOrLegacyValues_ShouldReturnItemsSourceEntries()
    {
        var state = new ConnectionGameSharedStateViewModel();

        state.ConnectConfig = "Mumu";
        Assert.Same(
            state.ConnectConfigOptions.First(option => string.Equals(option.Value, "MuMuEmulator12", StringComparison.OrdinalIgnoreCase)),
            state.SelectedConnectConfigOption);

        state.ConnectConfig = "LegacyConnectConfig";
        Assert.Same(state.ConnectConfigOptions.First(), state.SelectedConnectConfigOption);

        state.ClientType = "Txwy";
        Assert.Same(
            state.ClientTypeOptions.First(option => string.Equals(option.Value, "txwy", StringComparison.OrdinalIgnoreCase)),
            state.SelectedClientTypeOption);

        state.ClientType = "LegacyClientType";
        Assert.Same(state.ClientTypeOptions.First(), state.SelectedClientTypeOption);

        state.TouchMode = "LegacyTouchMode";
        Assert.Same(state.TouchModeOptions.First(), state.SelectedTouchModeOption);

        state.AttachWindowScreencapMethod = "LegacyScreencap";
        Assert.Same(state.AttachWindowScreencapOptions.First(), state.SelectedAttachWindowScreencapOption);

        state.AttachWindowMouseMethod = "LegacyMouseInput";
        Assert.Same(state.AttachWindowInputOptions.First(), state.SelectedAttachWindowMouseOption);

        state.AttachWindowKeyboardMethod = "LegacyKeyboardInput";
        Assert.Same(state.AttachWindowInputOptions.First(), state.SelectedAttachWindowKeyboardOption);
    }

    [Fact]
    public void ConnectionGameSharedState_ConnectAndTouchOptions_ShouldIncludeWpfParityEntries()
    {
        var state = new ConnectionGameSharedStateViewModel();

        Assert.Contains(
            state.ConnectConfigOptions,
            option => string.Equals(option.Value, "AVD", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            state.TouchModeOptions,
            option => string.Equals(option.Value, "MaaFwAdb", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ConnectionGameSharedState_AvdConfig_ShouldProvideEmulatorCandidatesWhenAutoDetectEnabled()
    {
        var state = new ConnectionGameSharedStateViewModel
        {
            ConnectConfig = "AVD",
            AutoDetect = true,
            AlwaysAutoDetect = false,
            ConnectAddress = string.Empty,
        };

        var candidates = state.BuildConnectAddressCandidates(includeConfiguredAddress: true);

        Assert.Contains("emulator-5554", candidates);
    }

    [Fact]
    public void ConnectionGameSharedState_RemoveAddressFromHistory_ShouldDeleteOnlyTargetEntry()
    {
        var state = new ConnectionGameSharedStateViewModel();
        state.ConnectAddress = "127.0.0.1:5555";
        state.ConnectAddress = "127.0.0.1:5556";
        state.ConnectAddress = "127.0.0.1:5557";

        state.RemoveAddressFromHistory("127.0.0.1:5556");

        Assert.DoesNotContain("127.0.0.1:5556", state.ConnectAddressHistory);
        Assert.Contains("127.0.0.1:5555", state.ConnectAddressHistory);
        Assert.Contains("127.0.0.1:5557", state.ConnectAddressHistory);
    }

    [Fact]
    public void ConnectionGameSharedState_MuMuExtras_ShouldAutoDetectAndValidateEmulatorPath()
    {
        var state = new ConnectionGameSharedStateViewModel
        {
            ConnectConfig = "MuMuEmulator12",
        };

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var autoDetectedPath = Path.Combine(userProfile, "Netease", "MuMuPlayerGlobal-12.0");
        var rendererDirectory = Path.Combine(autoDetectedPath, "shell", "sdk");
        var rendererPath = Path.Combine(rendererDirectory, "external_renderer_ipc.dll");
        var createdDirectory = !Directory.Exists(autoDetectedPath);
        Directory.CreateDirectory(rendererDirectory);
        File.WriteAllText(rendererPath, "test");

        try
        {
            state.MuMu12ExtrasEnabled = true;

            Assert.False(string.IsNullOrWhiteSpace(state.MuMu12EmulatorPath));
            Assert.True(state.ValidateMuMu12EmulatorPath(out var validationError), validationError);

            var invalidPath = Path.Combine(Path.GetTempPath(), "maa-unified-invalid-mumu", Guid.NewGuid().ToString("N"));
            var previousPath = state.MuMu12EmulatorPath;
            state.MuMu12EmulatorPath = invalidPath;
            Assert.Equal(previousPath, state.MuMu12EmulatorPath);
            Assert.False(string.IsNullOrWhiteSpace(state.TestLinkInfo));
        }
        finally
        {
            if (File.Exists(rendererPath))
            {
                File.Delete(rendererPath);
            }

            if (createdDirectory && Directory.Exists(autoDetectedPath))
            {
                Directory.Delete(autoDetectedPath, recursive: true);
            }
        }
    }

    [Fact]
    public void ConnectionGameSharedState_LdPlayerExtras_ShouldAutoDetectAndValidateEmulatorPath()
    {
        var state = new ConnectionGameSharedStateViewModel
        {
            ConnectConfig = "LDPlayer",
        };

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var autoDetectedPath = Path.Combine(userProfile, "leidian", "LDPlayer9");
        var openGlLibraryPath = Path.Combine(autoDetectedPath, "ldopengl64.dll");
        var createdDirectory = !Directory.Exists(autoDetectedPath);
        Directory.CreateDirectory(autoDetectedPath);
        File.WriteAllText(openGlLibraryPath, "test");

        try
        {
            state.LdPlayerExtrasEnabled = true;

            Assert.False(string.IsNullOrWhiteSpace(state.LdPlayerEmulatorPath));
            Assert.True(state.ValidateLdPlayerEmulatorPath(out var validationError), validationError);

            var invalidPath = Path.Combine(Path.GetTempPath(), "maa-unified-invalid-ld", Guid.NewGuid().ToString("N"));
            var previousPath = state.LdPlayerEmulatorPath;
            state.LdPlayerEmulatorPath = invalidPath;
            Assert.Equal(previousPath, state.LdPlayerEmulatorPath);
            Assert.False(string.IsNullOrWhiteSpace(state.TestLinkInfo));
        }
        finally
        {
            if (File.Exists(openGlLibraryPath))
            {
                File.Delete(openGlLibraryPath);
            }

            if (createdDirectory && Directory.Exists(autoDetectedPath))
            {
                Directory.Delete(autoDetectedPath, recursive: true);
            }
        }
    }

    [Fact]
    public void ConnectionGameSharedState_ClientTypeFlags_ShouldExposeYoStarAndOverseasHints()
    {
        var state = new ConnectionGameSharedStateViewModel
        {
            ClientType = "Official",
        };

        Assert.False(state.IsYoStarEnClientType);
        Assert.False(state.ShowOverseasClientHint);

        state.ClientType = "YoStarEN";
        Assert.True(state.IsYoStarEnClientType);
        Assert.True(state.ShowOverseasClientHint);

        state.ClientType = "Bilibili";
        Assert.False(state.IsYoStarEnClientType);
        Assert.False(state.ShowOverseasClientHint);
    }

    [Fact]
    public void ConnectionGameSharedState_SetLanguage_AfterOptionsRebuild_SelectedOptionsShouldUseNewListEntries()
    {
        var state = new ConnectionGameSharedStateViewModel
        {
            ConnectConfig = "Mumu",
            ClientType = "Txwy",
            TouchMode = "adb",
            AttachWindowScreencapMethod = "16",
            AttachWindowMouseMethod = "32",
            AttachWindowKeyboardMethod = "128",
        };

        var connectConfigOptionsBefore = state.ConnectConfigOptions;
        var clientTypeOptionsBefore = state.ClientTypeOptions;
        var touchModeOptionsBefore = state.TouchModeOptions;
        var screencapOptionsBefore = state.AttachWindowScreencapOptions;
        var inputOptionsBefore = state.AttachWindowInputOptions;

        var selectedConnectConfigBefore = state.SelectedConnectConfigOption;
        var selectedClientTypeBefore = state.SelectedClientTypeOption;
        var selectedTouchModeBefore = state.SelectedTouchModeOption;
        var selectedScreencapBefore = state.SelectedAttachWindowScreencapOption;
        var selectedMouseBefore = state.SelectedAttachWindowMouseOption;
        var selectedKeyboardBefore = state.SelectedAttachWindowKeyboardOption;

        var nextLanguage = string.Equals(state.RootTexts.Language, "zh-cn", StringComparison.OrdinalIgnoreCase)
            ? "en-us"
            : "zh-cn";
        state.SetLanguage(nextLanguage);

        Assert.NotSame(connectConfigOptionsBefore, state.ConnectConfigOptions);
        Assert.NotSame(clientTypeOptionsBefore, state.ClientTypeOptions);
        Assert.NotSame(touchModeOptionsBefore, state.TouchModeOptions);
        Assert.NotSame(screencapOptionsBefore, state.AttachWindowScreencapOptions);
        Assert.NotSame(inputOptionsBefore, state.AttachWindowInputOptions);

        Assert.NotSame(selectedConnectConfigBefore, state.SelectedConnectConfigOption);
        Assert.NotSame(selectedClientTypeBefore, state.SelectedClientTypeOption);
        Assert.NotSame(selectedTouchModeBefore, state.SelectedTouchModeOption);
        Assert.NotSame(selectedScreencapBefore, state.SelectedAttachWindowScreencapOption);
        Assert.NotSame(selectedMouseBefore, state.SelectedAttachWindowMouseOption);
        Assert.NotSame(selectedKeyboardBefore, state.SelectedAttachWindowKeyboardOption);
        Assert.Equal(selectedConnectConfigBefore, state.SelectedConnectConfigOption);
        Assert.Equal(selectedClientTypeBefore, state.SelectedClientTypeOption);
        Assert.Equal(selectedTouchModeBefore, state.SelectedTouchModeOption);
        Assert.Equal(selectedScreencapBefore, state.SelectedAttachWindowScreencapOption);
        Assert.Equal(selectedMouseBefore, state.SelectedAttachWindowMouseOption);
        Assert.Equal(selectedKeyboardBefore, state.SelectedAttachWindowKeyboardOption);

        Assert.Same(
            state.ConnectConfigOptions.First(option => string.Equals(option.Value, "MuMuEmulator12", StringComparison.OrdinalIgnoreCase)),
            state.SelectedConnectConfigOption);
        Assert.Same(
            state.ClientTypeOptions.First(option => string.Equals(option.Value, "txwy", StringComparison.OrdinalIgnoreCase)),
            state.SelectedClientTypeOption);
        Assert.Same(
            state.TouchModeOptions.First(option => string.Equals(option.Value, "adb", StringComparison.OrdinalIgnoreCase)),
            state.SelectedTouchModeOption);
        Assert.Same(
            state.AttachWindowScreencapOptions.First(option => string.Equals(option.Value, "16", StringComparison.OrdinalIgnoreCase)),
            state.SelectedAttachWindowScreencapOption);
        Assert.Same(
            state.AttachWindowInputOptions.First(option => string.Equals(option.Value, "32", StringComparison.OrdinalIgnoreCase)),
            state.SelectedAttachWindowMouseOption);
        Assert.Same(
            state.AttachWindowInputOptions.First(option => string.Equals(option.Value, "128", StringComparison.OrdinalIgnoreCase)),
            state.SelectedAttachWindowKeyboardOption);
    }

    [Fact]
    public async Task SaveConnectionGameSettings_AndMainShellSyncConnectionToProfile_WriteSameFieldSet()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var shell = fixture.Shell;
        var state = shell.ConnectionGameSharedState;
        state.ConnectAddress = " 10.10.10.10:16384 ";
        state.ConnectConfig = " Mumu ";
        state.AdbPath = " /opt/adb ";
        state.ClientType = " YoStarEN ";
        state.StartGameEnabled = true;
        state.TouchMode = " maatouch ";
        state.AutoDetect = false;
        state.AlwaysAutoDetect = true;
        state.RetryOnDisconnected = true;

        await shell.SettingsPage.SaveConnectionGameSettingsAsync();

        var profile = fixture.GetCurrentProfile();
        var expected = SnapshotConnectionValues(profile);

        ClearConnectionValues(profile);
        InvokePrivateMethod(shell, "SyncConnectionToProfile", (string?)null);
        var actual = SnapshotConnectionValues(profile);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task SaveConnectionGameSettings_ReflectsInBoundStartUpModuleImmediately_AndAfterRebind()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup")).Success);

        await fixture.Shell.TaskQueuePage.ReloadTasksAsync();
        await WaitUntilAsync(() => fixture.Shell.TaskQueuePage.StartUpModule.IsTaskBound);

        var state = fixture.Shell.SettingsPage.ConnectionGameSharedState;
        state.ConnectAddress = "192.168.0.9:5555";
        state.ConnectConfig = "LDPlayer";
        state.AdbPath = "/usr/bin/adb";
        state.ClientType = "YoStarJP";
        state.StartGameEnabled = true;
        state.TouchMode = "adb";
        state.AutoDetect = false;

        await fixture.Shell.SettingsPage.SaveConnectionGameSettingsAsync();
        AssertStartUpModuleConnectionValues(
            fixture.Shell,
            connectAddress: "192.168.0.9:5555",
            connectConfig: "LDPlayer",
            adbPath: "/usr/bin/adb",
            clientType: "YoStarJP",
            startGame: true,
            touchMode: "adb",
            autoDetect: false);

        await fixture.Shell.TaskQueuePage.ReloadTasksAsync();
        await WaitUntilAsync(() => fixture.Shell.TaskQueuePage.StartUpModule.IsTaskBound);
        AssertStartUpModuleConnectionValues(
            fixture.Shell,
            connectAddress: "192.168.0.9:5555",
            connectConfig: "LDPlayer",
            adbPath: "/usr/bin/adb",
            clientType: "YoStarJP",
            startGame: true,
            touchMode: "adb",
            autoDetect: false);
    }

    [Fact]
    public async Task StartUpBinding_DoesNotBackfillSharedStateFromStaleTaskParams()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup")).Success);

        var profile = fixture.GetCurrentProfile();
        profile.Values["ConnectAddress"] = JsonValue.Create("172.16.0.2:7000");
        profile.Values["ConnectConfig"] = JsonValue.Create("Mumu");
        profile.Values["AdbPath"] = JsonValue.Create("/profile/adb");
        profile.Values["ClientType"] = JsonValue.Create("YoStarEN");
        profile.Values["StartGame"] = JsonValue.Create(true);
        profile.Values["TouchMode"] = JsonValue.Create("maatouch");
        profile.Values["AutoDetect"] = JsonValue.Create(true);

        var staleParams = new JsonObject
        {
            ["client_type"] = "Txwy",
            ["start_game_enabled"] = false,
            ["account_name"] = "stale-account",
        };
        Assert.True((await fixture.TaskQueue.UpdateTaskParamsAsync(0, staleParams, persistImmediately: false)).Success);

        InvokePrivateMethod(fixture.Shell, "SyncConnectionFromProfile");
        await fixture.Shell.TaskQueuePage.ReloadTasksAsync();
        await WaitUntilAsync(() => fixture.Shell.TaskQueuePage.StartUpModule.IsTaskBound);

        AssertStartUpModuleConnectionValues(
            fixture.Shell,
            connectAddress: "172.16.0.2:7000",
            connectConfig: "Mumu",
            adbPath: "/profile/adb",
            clientType: "YoStarEN",
            startGame: true,
            touchMode: "maatouch",
            autoDetect: true);
        Assert.Equal("stale-account", fixture.Shell.TaskQueuePage.StartUpModule.AccountName);
    }

    [Fact]
    public async Task QueueEnabledTasks_UsesProfileClientTypeAndStartGame_WhenTaskParamsAreStale()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup")).Success);

        var profile = fixture.GetCurrentProfile();
        profile.Values["ClientType"] = JsonValue.Create("YoStarKR");
        profile.Values["StartGame"] = JsonValue.Create(true);
        profile.Values["ConnectConfig"] = JsonValue.Create("General");

        var staleParams = new JsonObject
        {
            ["client_type"] = "Txwy",
            ["start_game_enabled"] = false,
            ["account_name"] = "stale-account",
        };
        Assert.True((await fixture.TaskQueue.UpdateTaskParamsAsync(0, staleParams, persistImmediately: false)).Success);

        var queueResult = await fixture.TaskQueue.QueueEnabledTasksAsync();
        Assert.True(queueResult.Success);
        var appended = Assert.Single(fixture.Bridge.AppendedTasks);
        var json = Assert.IsType<JsonObject>(JsonNode.Parse(appended.ParamsJson));

        Assert.Equal("YoStarKR", json["client_type"]?.GetValue<string>());
        Assert.True(json["start_game_enabled"]?.GetValue<bool>());
    }

    [Fact]
    public async Task ConnectConfigPc_ForcesStartGameFalse_InSettingsAndStartUp()
    {
        await using var fixture = await TestFixture.CreateAsync();
        Assert.True((await fixture.TaskQueue.AddTaskAsync("StartUp", "startup")).Success);
        await fixture.Shell.TaskQueuePage.ReloadTasksAsync();
        await WaitUntilAsync(() => fixture.Shell.TaskQueuePage.StartUpModule.IsTaskBound);

        var state = fixture.Shell.SettingsPage.ConnectionGameSharedState;
        state.ConnectConfig = "PC";
        state.StartGameEnabled = true;
        await fixture.Shell.SettingsPage.SaveConnectionGameSettingsAsync();

        Assert.False(state.CanStartGameEnabled);
        Assert.False(state.StartGameEnabled);
        Assert.False(fixture.Shell.TaskQueuePage.StartUpModule.CanEditStartGameEnabled);
        Assert.False(fixture.Shell.TaskQueuePage.StartUpModule.StartGameEnabled);
        Assert.False(fixture.GetCurrentProfile().Values["StartGame"]?.GetValue<bool>());
    }

    [Fact]
    public async Task ConnectAsync_WithWindowsStyleAdbPathOnNonWindows_ShouldFallbackToSystemAdb()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var state = fixture.Shell.ConnectionGameSharedState;
        state.ConnectAddress = "127.0.0.1:5555";
        state.ConnectConfig = "MuMuEmulator12";
        state.AdbPath = @"D:\Program Files\Netease\MuMuPlayer-12.0\shell\.\adb.exe";

        await fixture.Shell.ConnectAsync();

        Assert.NotNull(fixture.Bridge.LastConnectionInfo);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(state.AdbPath.Trim(), fixture.Bridge.LastConnectionInfo!.AdbPath);
            return;
        }

        Assert.Null(fixture.Bridge.LastConnectionInfo!.AdbPath);
    }

    [Fact]
    public async Task ConnectAsync_ShouldApplyLiveTouchModeAndAdbFlagsBeforeConnect()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var state = fixture.Shell.ConnectionGameSharedState;
        state.ConnectAddress = "127.0.0.1:5555";
        state.ConnectConfig = "General";
        state.TouchMode = "maatouch";
        state.AdbLiteEnabled = true;
        state.KillAdbOnExit = true;

        await fixture.Shell.ConnectAsync();

        var applied = Assert.IsType<CoreInstanceOptions>(fixture.Bridge.LastAppliedInstanceOptions);
        Assert.Equal("maatouch", applied.TouchMode);
        Assert.True(applied.AdbLiteEnabled);
        Assert.True(applied.KillAdbOnExit);
    }

    [Fact]
    public async Task SaveConnectionGameSettings_ShouldSyncCoreInstanceOptions()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var settings = fixture.Shell.SettingsPage;
        var state = settings.ConnectionGameSharedState;
        state.TouchMode = "adb";
        state.AdbLiteEnabled = true;
        state.KillAdbOnExit = true;
        settings.DeploymentWithPause = true;

        await settings.SaveConnectionGameSettingsAsync();

        var applied = Assert.IsType<CoreInstanceOptions>(fixture.Bridge.LastAppliedInstanceOptions);
        Assert.Equal("adb", applied.TouchMode);
        Assert.True(applied.AdbLiteEnabled);
        Assert.True(applied.KillAdbOnExit);
        Assert.True(applied.DeploymentWithPause);
    }

    [Fact]
    public async Task SaveStartPerformanceSettings_ShouldSyncDeploymentWithPauseToCore()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.Shell.SettingsPage.DeploymentWithPause = true;

        await fixture.Shell.SettingsPage.SaveStartPerformanceSettingsAsync();

        Assert.True(fixture.Bridge.LastAppliedInstanceOptions?.DeploymentWithPause);
    }

    [Fact]
    public async Task SaveStartPerformanceSettings_WithNoEffectiveChange_ShouldSkipCoreSync()
    {
        await using var fixture = await TestFixture.CreateAsync();

        await fixture.Shell.SettingsPage.SaveStartPerformanceSettingsAsync();

        Assert.Null(fixture.Bridge.LastAppliedInstanceOptions);
    }

    [Fact]
    public async Task StartAsync_ShouldApplySavedDeploymentWithPauseBeforeRun()
    {
        await using var fixture = await TestFixture.CreateAsync();
        fixture.GetCurrentProfile().Values[ConfigurationKeys.RoguelikeDeploymentWithPause] = JsonValue.Create(true);

        var result = await fixture.Runtime.ConnectFeatureService.StartAsync();

        Assert.True(result.Success);
        Assert.True(fixture.Bridge.LastAppliedInstanceOptions?.DeploymentWithPause);
    }

    private static void AssertStartUpModuleConnectionValues(
        MainShellViewModel shell,
        string connectAddress,
        string connectConfig,
        string adbPath,
        string clientType,
        bool startGame,
        string touchMode,
        bool autoDetect)
    {
        var module = shell.TaskQueuePage.StartUpModule;
        Assert.Equal(connectAddress, module.ConnectAddress);
        Assert.Equal(connectConfig, module.ConnectConfig);
        Assert.Equal(adbPath, module.AdbPath);
        Assert.Equal(clientType, module.ClientType);
        Assert.Equal(startGame, module.StartGameEnabled);
        Assert.Equal(touchMode, module.TouchMode);
        Assert.Equal(autoDetect, module.AutoDetectConnection);
    }

    private static Dictionary<string, string> SnapshotConnectionValues(UnifiedProfile profile)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in _connectionKeys)
        {
            result[key] = profile.Values.TryGetValue(key, out var node) && node is not null
                ? node.ToJsonString()
                : "<missing>";
        }

        return result;
    }

    private static void ClearConnectionValues(UnifiedProfile profile)
    {
        foreach (var key in _connectionKeys)
        {
            profile.Values.Remove(key);
        }
    }

    private static void InvokePrivateMethod(object target, string methodName, params object?[] suppliedArguments)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var parameters = method!.GetParameters();
        Assert.True(
            suppliedArguments.Length <= parameters.Length,
            $"Method '{methodName}' expects {parameters.Length} parameters but got {suppliedArguments.Length}.");

        var invokeArguments = new object?[parameters.Length];
        for (var index = 0; index < parameters.Length; index++)
        {
            if (index < suppliedArguments.Length)
            {
                invokeArguments[index] = suppliedArguments[index];
                continue;
            }

            Assert.True(
                parameters[index].HasDefaultValue,
                $"Method '{methodName}' parameter '{parameters[index].Name}' is required.");
            invokeArguments[index] = parameters[index].DefaultValue is DBNull
                ? Type.Missing
                : parameters[index].DefaultValue;
        }

        method.Invoke(target, invokeArguments);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var startedAt = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - startedAt > timeoutMs)
            {
                throw new TimeoutException("Condition not reached in expected time.");
            }

            await Task.Delay(20);
        }
    }

    private sealed class TestFixture : IAsyncDisposable
    {
        private TestFixture(
            string root,
            UnifiedConfigurationService config,
            TaskQueueFeatureService taskQueue,
            MAAUnifiedRuntime runtime,
            MainShellViewModel shell,
            FakeBridge bridge)
        {
            Root = root;
            Config = config;
            TaskQueue = taskQueue;
            Runtime = runtime;
            Shell = shell;
            Bridge = bridge;
        }

        public string Root { get; }

        public UnifiedConfigurationService Config { get; }

        public TaskQueueFeatureService TaskQueue { get; }

        public MAAUnifiedRuntime Runtime { get; }

        public MainShellViewModel Shell { get; }

        public FakeBridge Bridge { get; }

        public static async Task<TestFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "maa-unified-tests", Guid.NewGuid().ToString("N"));
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
            var taskQueue = new TaskQueueFeatureService(session, config);

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
            var connectFeatureService = new ConnectFeatureService(session, config);
            var shellFeatureService = new ShellFeatureService(connectFeatureService);
            var runtime = new MAAUnifiedRuntime
            {
                CoreBridge = bridge,
                ConfigurationService = config,
                ResourceWorkflowService = new ResourceWorkflowService(root, bridge, log),
                SessionService = session,
                Platform = platform,
                LogService = log,
                DiagnosticsService = diagnostics,
                ConnectFeatureService = connectFeatureService,
                ShellFeatureService = shellFeatureService,
                TaskQueueFeatureService = taskQueue,
                CopilotFeatureService = new CopilotFeatureService(),
                ToolboxFeatureService = new ToolboxFeatureService(),
                RemoteControlFeatureService = new RemoteControlFeatureService(),
                PlatformCapabilityService = capability,
                OverlayFeatureService = new OverlayFeatureService(capability),
                NotificationProviderFeatureService = new NotificationProviderFeatureService(),
                SettingsFeatureService = new SettingsFeatureService(config, capability, diagnostics),
                DialogFeatureService = new DialogFeatureService(diagnostics),
                PostActionFeatureService = new PostActionFeatureService(config, diagnostics, platform.PostActionExecutorService),
            };

            var shell = new MainShellViewModel(runtime);
            InvokePrivateMethod(shell, "SyncConnectionFromProfile");

            return new TestFixture(root, config, taskQueue, runtime, shell, bridge);
        }

        public UnifiedProfile GetCurrentProfile()
        {
            Assert.True(Config.TryGetCurrentProfile(out var profile));
            return profile;
        }

        public async ValueTask DisposeAsync()
        {
            TestShellCleanup.StopTimerScheduler(Shell);
            await Runtime.DisposeAsync();
            await Bridge.DisposeAsync();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // ignore temporary folder cleanup failures
            }
        }
    }

    private sealed class FakeBridge : IMaaCoreBridge
    {
        private readonly Channel<CoreCallbackEvent> _callbackChannel = Channel.CreateUnbounded<CoreCallbackEvent>();
        private readonly List<CoreTaskRequest> _tasks = [];
        private int _taskId;

        public IReadOnlyList<CoreTaskRequest> AppendedTasks => _tasks;

        public CoreConnectionInfo? LastConnectionInfo { get; private set; }

        public CoreInstanceOptions? LastAppliedInstanceOptions { get; private set; }

        public Task<CoreResult<CoreInitializeInfo>> InitializeAsync(CoreInitializeRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<CoreInitializeInfo>.Ok(new CoreInitializeInfo(request.BaseDirectory, "fake", "fake", request.ClientType)));

        public Task<CoreResult<bool>> ConnectAsync(CoreConnectionInfo connectionInfo, CancellationToken cancellationToken = default)
        {
            LastConnectionInfo = connectionInfo;
            return Task.FromResult(CoreResult<bool>.Ok(true));
        }

        public Task<CoreResult<bool>> ApplyInstanceOptionsAsync(CoreInstanceOptions options, CancellationToken cancellationToken = default)
        {
            LastAppliedInstanceOptions = options;
            return Task.FromResult(CoreResult<bool>.Ok(true));
        }

        public Task<CoreResult<int>> AppendTaskAsync(CoreTaskRequest task, CancellationToken cancellationToken = default)
        {
            _tasks.Add(task);
            return Task.FromResult(CoreResult<int>.Ok(Interlocked.Increment(ref _taskId)));
        }

        public Task<CoreResult<bool>> StartAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Ok(true));

        public Task<CoreResult<bool>> StopAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Ok(true));

        public Task<CoreResult<CoreRuntimeStatus>> GetRuntimeStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<CoreRuntimeStatus>.Ok(new CoreRuntimeStatus(true, true, false)));

        public Task<CoreResult<bool>> AttachWindowAsync(CoreAttachWindowRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(CoreResult<bool>.Fail(new CoreError(CoreErrorCode.NotSupported, "not supported")));

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
