using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using MAAUnified.App.Features.Dialogs;
using MAAUnified.App.ViewModels.Copilot;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using MAAUnified.CoreBridge;
using MAAUnified.Platform;
using LegacyConfigurationKeys = MAAUnified.Compat.Constants.ConfigurationKeys;

namespace MAAUnified.Tests;

public sealed class CopilotListManagementTests
{
    [Fact]
    public async Task AddEmptyTaskAsync_ShouldPersistListAndUpdateFeedback()
    {
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;

        await vm.AddEmptyTaskAsync();

        Assert.Single(vm.Items);
        Assert.NotNull(vm.SelectedItem);
        Assert.Contains("新增", vm.StatusMessage, StringComparison.Ordinal);
        Assert.True(string.IsNullOrWhiteSpace(vm.LastErrorMessage));

        var payload = GetPersistedTaskListPayload(fixture.Config);
        Assert.NotNull(payload);
        var node = JsonNode.Parse(payload!);
        var array = Assert.IsType<JsonArray>(node);
        Assert.Single(array);
    }

    [Fact]
    public async Task RemoveSelectedAsync_WithoutSelection_ShouldSetFailureFeedbackAndLog()
    {
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;

        await vm.RemoveSelectedAsync();

        Assert.Contains("删除作业失败", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("请选择", vm.LastErrorMessage, StringComparison.Ordinal);
        Assert.True(await WaitForLogContainsAsync(fixture.Runtime.DiagnosticsService.ErrorLogPath, "[Copilot.Remove]"));
    }

    [Fact]
    public async Task MoveSelectedUpAndDown_ShouldReorderItemsAndPersist()
    {
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;

        await vm.AddEmptyTaskAsync();
        await vm.AddEmptyTaskAsync();
        await vm.AddEmptyTaskAsync();
        vm.Items[0].Name = "First";
        vm.Items[1].Name = "Second";
        vm.Items[2].Name = "Third";

        vm.SelectedItem = vm.Items[1];
        await vm.MoveSelectedUpAsync();

        Assert.Equal(["Second", "First", "Third"], vm.Items.Select(i => i.Name).ToArray());
        Assert.Equal("Second", vm.SelectedItem?.Name);

        await vm.MoveSelectedDownAsync();

        Assert.Equal(["First", "Second", "Third"], vm.Items.Select(i => i.Name).ToArray());
        Assert.Equal("Second", vm.SelectedItem?.Name);

        var payload = GetPersistedTaskListPayload(fixture.Config);
        Assert.NotNull(payload);
        var persistedArray = Assert.IsType<JsonArray>(JsonNode.Parse(payload!));
        Assert.Equal("First", persistedArray[0]?["Name"]?.GetValue<string>());
        Assert.Equal("Second", persistedArray[1]?["Name"]?.GetValue<string>());
        Assert.Equal("Third", persistedArray[2]?["Name"]?.GetValue<string>());
    }

    [Fact]
    public async Task MoveListItemToAsync_ShouldMoveRequestedItem_KeepSelectionAndPersist()
    {
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;

        await vm.AddEmptyTaskAsync();
        await vm.AddEmptyTaskAsync();
        await vm.AddEmptyTaskAsync();
        vm.Items[0].Name = "First";
        vm.Items[1].Name = "Second";
        vm.Items[2].Name = "Third";
        var movedItem = vm.Items[2];

        await vm.MoveListItemToAsync(movedItem, 0);

        Assert.Equal(["Third", "First", "Second"], vm.Items.Select(i => i.Name).ToArray());
        Assert.Same(movedItem, vm.SelectedItem);

        var persistedArray = Assert.IsType<JsonArray>(JsonNode.Parse(GetPersistedTaskListPayload(fixture.Config)!));
        Assert.Equal("Third", persistedArray[0]?["Name"]?.GetValue<string>());
        Assert.Equal("First", persistedArray[1]?["Name"]?.GetValue<string>());
        Assert.Equal("Second", persistedArray[2]?["Name"]?.GetValue<string>());
    }

    [Fact]
    public async Task MoveListItemToAsync_WhenPersistenceFails_ShouldRollbackOrder()
    {
        await using var fixture = await CopilotFixture.CreateAsync(failPersistence: true);
        var vm = fixture.ViewModel;

        vm.Items.Add(new CopilotItemViewModel("First", vm.Types[0]));
        vm.Items.Add(new CopilotItemViewModel("Second", vm.Types[0]));
        vm.Items.Add(new CopilotItemViewModel("Third", vm.Types[0]));
        var movedItem = vm.Items[2];

        await vm.MoveListItemToAsync(movedItem, 0);

        Assert.Equal(["First", "Second", "Third"], vm.Items.Select(i => i.Name).ToArray());
        Assert.Same(movedItem, vm.SelectedItem);
        Assert.Contains("失败", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("持久化", vm.LastErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetListItemCheckedAsync_ShouldUpdateItemAndPersist()
    {
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;

        await vm.AddEmptyTaskAsync();
        var item = Assert.Single(vm.Items);

        await vm.SetListItemCheckedAsync(item, false);

        Assert.False(item.IsChecked);
        var persistedArray = Assert.IsType<JsonArray>(JsonNode.Parse(GetPersistedTaskListPayload(fixture.Config)!));
        Assert.False(persistedArray[0]?["IsChecked"]?.GetValue<bool>());
    }

    [Fact]
    public async Task ToggleListItemRaidAsync_ShouldToggleRaidFlagAndPersist()
    {
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;

        await vm.AddEmptyTaskAsync();
        var item = Assert.Single(vm.Items);

        await vm.ToggleListItemRaidAsync(item);

        Assert.True(item.IsRaid);
        Assert.Contains(vm.RaidLabelText, item.DisplayName, StringComparison.Ordinal);
        var persistedArray = Assert.IsType<JsonArray>(JsonNode.Parse(GetPersistedTaskListPayload(fixture.Config)!));
        Assert.True(persistedArray[0]?["IsRaid"]?.GetValue<bool>());
    }

    [Fact]
    public async Task ConfirmAndClearAllAsync_WhenCancelled_ShouldKeepItemsAndSkipPersistence()
    {
        var dialog = new ScriptedDialogService(DialogReturnSemantic.Cancel);
        await using var fixture = await CopilotFixture.CreateAsync(dialogService: dialog);
        var vm = fixture.ViewModel;

        await vm.AddEmptyTaskAsync();
        await vm.AddEmptyTaskAsync();
        var persistedBefore = GetPersistedTaskListPayload(fixture.Config);

        await vm.ConfirmAndClearAllAsync();

        Assert.Equal(2, vm.Items.Count);
        Assert.Equal(1, dialog.WarningConfirmCallCount);
        Assert.Equal(persistedBefore, GetPersistedTaskListPayload(fixture.Config));
    }

    [Fact]
    public async Task ConfirmAndClearAllAsync_WhenConfirmed_ShouldClearItemsAndPersist()
    {
        var dialog = new ScriptedDialogService(DialogReturnSemantic.Confirm);
        await using var fixture = await CopilotFixture.CreateAsync(dialogService: dialog);
        var vm = fixture.ViewModel;

        await vm.AddEmptyTaskAsync();
        await vm.AddEmptyTaskAsync();

        await vm.ConfirmAndClearAllAsync();

        Assert.Empty(vm.Items);
        Assert.Null(vm.SelectedItem);
        Assert.Equal(1, dialog.WarningConfirmCallCount);
        Assert.Equal(vm.ClearAllConfirmTitleText, dialog.WarningConfirmRequests[0].Title);
        Assert.Equal(vm.ClearAllConfirmMessageText, dialog.WarningConfirmRequests[0].Message);
        Assert.Equal(vm.ClearAllConfirmButtonText, dialog.WarningConfirmRequests[0].ConfirmText);
        var persistedArray = Assert.IsType<JsonArray>(JsonNode.Parse(GetPersistedTaskListPayload(fixture.Config)!));
        Assert.Empty(persistedArray);
    }

    [Fact]
    public async Task CopilotTaskName_ShouldRenameActiveListItemAndPersistWithoutSchemaExpansion()
    {
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;

        await vm.AddEmptyTaskAsync();
        var item = Assert.Single(vm.Items);
        item.SourcePath = "resource/copilot/old-name.json";
        item.InlinePayload = string.Empty;
        vm.SelectedItem = item;

        vm.CopilotTaskName = "JT8-3";

        Assert.Equal("JT8-3", item.Name);
        Assert.True(await WaitForPersistedItemNameAsync(fixture.Config, "JT8-3"));
        var persistedArray = Assert.IsType<JsonArray>(JsonNode.Parse(GetPersistedTaskListPayload(fixture.Config)!));
        var persistedItem = Assert.IsType<JsonObject>(persistedArray[0]);
        Assert.Equal("JT8-3", persistedItem["Name"]?.GetValue<string>());
        Assert.DoesNotContain("stage_name", persistedItem.Select(property => property.Key), StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("StageName", persistedItem.Select(property => property.Key), StringComparer.Ordinal);
    }

    [Fact]
    public async Task CopilotTabIndex_ShouldUpdateActiveListItemTypeAndPersist()
    {
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;

        await vm.AddEmptyTaskAsync();
        var item = Assert.Single(vm.Items);
        vm.SelectedItem = item;

        vm.CopilotTabIndex = 3;

        Assert.Equal(3, item.TabIndex);
        Assert.Equal(vm.Types[3], item.Type);
        Assert.True(await WaitForPersistedItemTabAsync(fixture.Config, item.Name, 3, vm.Types[3]));
    }

    [Fact]
    public async Task SendLikeAsync_WithoutSelection_ShouldSetFailureFeedbackAndLog()
    {
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;

        await vm.SendLikeAsync(true);

        Assert.Contains("反馈失败", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("请选择", vm.LastErrorMessage, StringComparison.Ordinal);
        Assert.True(await WaitForLogContainsAsync(fixture.Runtime.DiagnosticsService.ErrorLogPath, "[Copilot.Feedback]"));
    }

    [Fact]
    public async Task AddEmptyTaskAsync_WhenPersistenceFails_ShouldRollbackListState()
    {
        await using var fixture = await CopilotFixture.CreateAsync(failPersistence: true);
        var vm = fixture.ViewModel;

        await vm.AddEmptyTaskAsync();

        Assert.Empty(vm.Items);
        Assert.Contains("失败", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("持久化", vm.LastErrorMessage, StringComparison.Ordinal);
        Assert.True(await WaitForLogContainsAsync(fixture.Runtime.DiagnosticsService.ErrorLogPath, "[Copilot.Add]"));
    }

    [Fact]
    public async Task Constructor_ShouldLoadPersistedTaskList_AndMapLegacyTabIndex()
    {
        var payload = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "Legacy-SSS",
                ["tab_index"] = 1,
            },
            new JsonObject
            {
                ["name"] = "Typed-Main",
                ["type"] = "主线",
            },
        }.ToJsonString();

        await using var fixture = await CopilotFixture.CreateAsync(persistedPayload: payload);
        var vm = fixture.ViewModel;

        Assert.Equal(2, vm.Items.Count);
        Assert.Equal("Legacy-SSS", vm.Items[0].Name);
        Assert.Equal("保全派驻", vm.Items[0].Type);
        Assert.Equal("Typed-Main", vm.Items[1].Name);
        Assert.Equal("主线/故事集/SideStory", vm.Items[1].Type);
        Assert.Equal("Legacy-SSS", vm.SelectedItem?.Name);
    }

    [Fact]
    public async Task SetLanguage_ShouldUpdateRootLocalizationLanguage()
    {
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;

        vm.SetLanguage("en-us");

        Assert.Equal("en-us", vm.RootTexts.Language);
        Assert.Equal("en-us", vm.Texts.Language);
        Assert.Equal("Fill gap", vm.SupportUnitUsageOptions[0].DisplayName);
        Assert.Contains("Tips:", vm.HelpText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetLanguage_ShouldNotifyLocalizedTextMapsForViewBindings()
    {
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.SetLanguage("en-us");

        Assert.Contains(nameof(CopilotPageViewModel.Texts), changed);
        Assert.Contains(nameof(CopilotPageViewModel.RootTexts), changed);
        Assert.Contains(nameof(CopilotPageViewModel.MainTabTitle), changed);
        Assert.Contains(nameof(CopilotPageViewModel.StartButtonText), changed);
        Assert.Contains(string.Empty, changed);
    }

    [Fact]
    public async Task SetLanguage_ShouldRelocalizeTrackedCopilotItemsAcrossRepeatedSwitches()
    {
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;
        var item = new CopilotItemViewModel("Task-A", "主线/故事集/SideStory", inlinePayload: "{}")
        {
            IsRaid = true,
        };

        vm.Items.Add(item);

        foreach (var language in new[] { "en-us", "ja-jp", "ko-kr", "zh-tw" })
        {
            vm.SetLanguage(language);

            Assert.Equal(language, vm.Texts.Language);
            Assert.Contains(vm.RaidLabelText, item.DisplayName, StringComparison.Ordinal);
            Assert.Equal(vm.InlineJsonHintText, item.ExecutionPathHint);
        }
    }

    [Fact]
    public async Task SetLanguage_ShouldKeepLocalizedBindingsFreshAcrossRepeatedSwitches()
    {
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;
        var texts = vm.Texts;
        var rootTexts = vm.RootTexts;

        foreach (var language in new[] { "en-us", "ja-jp", "ko-kr", "zh-tw" })
        {
            vm.SetLanguage(language);

            Assert.Same(texts, vm.Texts);
            Assert.Same(rootTexts, vm.RootTexts);
            Assert.Equal(language, vm.Texts.Language);
            Assert.Equal(language, vm.RootTexts.Language);
            Assert.Equal(vm.Texts["Copilot.Tab.Main"], vm.MainTabTitle);
            Assert.Equal(vm.Texts["Copilot.Button.File"], vm.FileButtonText);
            Assert.Equal(vm.Texts["Copilot.Option.UseSupportUnit"], vm.UseSupportUnitText);
            Assert.Equal(vm.Texts["Copilot.Rating.Prompt"], vm.RatingPromptText);
            Assert.Equal(vm.Texts["Copilot.HelpText"], vm.HelpText);
            Assert.Equal(vm.Texts["Copilot.Option.SupportUnitUsage.FillGap"], vm.SupportUnitUsageOptions[0].DisplayName);
        }
    }

    [Fact]
    public async Task SetLanguage_ShouldRefreshHelpLogAcrossRepeatedSwitches()
    {
        await using var fixture = await CopilotFixture.CreateAsync();
        var vm = fixture.ViewModel;

        Assert.Single(vm.Logs);

        foreach (var language in new[] { "en-us", "ja-jp", "ko-kr", "zh-tw" })
        {
            vm.SetLanguage(language);

            var helpLog = Assert.Single(vm.Logs);
            Assert.False(helpLog.HasTime);
            Assert.Equal(vm.HelpText.TrimEnd(), helpLog.Content);
        }
    }

    private static string? GetPersistedTaskListPayload(UnifiedConfigurationService config)
    {
        if (!config.CurrentConfig.GlobalValues.TryGetValue(LegacyConfigurationKeys.CopilotTaskList, out var node)
            || node is not JsonValue value
            || !value.TryGetValue(out string? payload))
        {
            return null;
        }

        return payload;
    }

    private static async Task<bool> WaitForLogContainsAsync(string path, string expected, int retry = 30, int delayMs = 25)
    {
        for (var i = 0; i < retry; i++)
        {
            if (File.Exists(path))
            {
                var content = await File.ReadAllTextAsync(path);
                if (content.Contains(expected, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            await Task.Delay(delayMs);
        }

        return false;
    }

    private static async Task<bool> WaitForPersistedItemNameAsync(
        UnifiedConfigurationService config,
        string expectedName,
        int retry = 30,
        int delayMs = 25)
    {
        for (var i = 0; i < retry; i++)
        {
            var payload = GetPersistedTaskListPayload(config);
            if (!string.IsNullOrWhiteSpace(payload)
                && JsonNode.Parse(payload) is JsonArray array
                && array.OfType<JsonObject>().Any(item =>
                    string.Equals(item["Name"]?.GetValue<string>(), expectedName, StringComparison.Ordinal)))
            {
                return true;
            }

            await Task.Delay(delayMs);
        }

        return false;
    }

    private static async Task<bool> WaitForPersistedItemTabAsync(
        UnifiedConfigurationService config,
        string expectedName,
        int expectedTabIndex,
        string expectedType,
        int retry = 30,
        int delayMs = 25)
    {
        for (var i = 0; i < retry; i++)
        {
            var payload = GetPersistedTaskListPayload(config);
            if (!string.IsNullOrWhiteSpace(payload)
                && JsonNode.Parse(payload) is JsonArray array
                && array.OfType<JsonObject>().Any(item =>
                    string.Equals(item["Name"]?.GetValue<string>(), expectedName, StringComparison.Ordinal)
                    && item["TabIndex"]?.GetValue<int>() == expectedTabIndex
                    && string.Equals(item["Type"]?.GetValue<string>(), expectedType, StringComparison.Ordinal)))
            {
                return true;
            }

            await Task.Delay(delayMs);
        }

        return false;
    }

    private sealed class CopilotFixture : IAsyncDisposable
    {
        private CopilotFixture(
            string root,
            UnifiedConfigurationService config,
            MAAUnifiedRuntime runtime,
            CopilotPageViewModel viewModel)
        {
            Root = root;
            Config = config;
            Runtime = runtime;
            ViewModel = viewModel;
        }

        public string Root { get; }

        public UnifiedConfigurationService Config { get; }

        public MAAUnifiedRuntime Runtime { get; }

        public CopilotPageViewModel ViewModel { get; }

        public static async Task<CopilotFixture> CreateAsync(
            bool failPersistence = false,
            string? persistedPayload = null,
            IAppDialogService? dialogService = null)
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

            if (!string.IsNullOrWhiteSpace(persistedPayload))
            {
                config.CurrentConfig.GlobalValues[LegacyConfigurationKeys.CopilotTaskList] = JsonValue.Create(persistedPayload);
            }

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
            var settings = new SettingsFeatureService(config, capability, diagnostics);
            ISettingsFeatureService settingsFeature = failPersistence
                ? new FailingSettingsFeatureService(settings)
                : settings;

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
                SettingsFeatureService = settingsFeature,
                DialogFeatureService = new DialogFeatureService(diagnostics),
                PostActionFeatureService = new PostActionFeatureService(
                    config,
                    diagnostics,
                    platform.PostActionExecutorService),
            };

            var vm = new CopilotPageViewModel(runtime, dialogService);
            return new CopilotFixture(root, config, runtime, vm);
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
                // ignore cleanup failures in temporary test directories
            }
        }
    }

    private sealed class ScriptedDialogService : IAppDialogService
    {
        private readonly DialogReturnSemantic _warningConfirmReturn;

        public ScriptedDialogService(DialogReturnSemantic warningConfirmReturn)
        {
            _warningConfirmReturn = warningConfirmReturn;
        }

        public int WarningConfirmCallCount { get; private set; }

        public List<WarningConfirmDialogRequest> WarningConfirmRequests { get; } = [];

        public Task<DialogCompletion<AnnouncementDialogPayload>> ShowAnnouncementAsync(
            AnnouncementDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DialogCompletion<AnnouncementDialogPayload>(DialogReturnSemantic.Close, null, "scripted"));

        public Task<DialogCompletion<VersionUpdateDialogPayload>> ShowVersionUpdateAsync(
            VersionUpdateDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DialogCompletion<VersionUpdateDialogPayload>(DialogReturnSemantic.Close, null, "scripted"));

        public Task<DialogCompletion<ProcessPickerDialogPayload>> ShowProcessPickerAsync(
            ProcessPickerDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DialogCompletion<ProcessPickerDialogPayload>(DialogReturnSemantic.Close, null, "scripted"));

        public Task<DialogCompletion<EmulatorPathDialogPayload>> ShowEmulatorPathAsync(
            EmulatorPathDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DialogCompletion<EmulatorPathDialogPayload>(DialogReturnSemantic.Close, null, "scripted"));

        public Task<DialogCompletion<ErrorDialogPayload>> ShowErrorAsync(
            ErrorDialogRequest request,
            string sourceScope,
            Func<CancellationToken, Task<UiOperationResult>>? openIssueReportAsync = null,
            Func<CancellationToken, Task<UiOperationResult>>? openSettingsAsync = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DialogCompletion<ErrorDialogPayload>(DialogReturnSemantic.Close, null, "scripted"));

        public Task<DialogCompletion<AchievementListDialogPayload>> ShowAchievementListAsync(
            AchievementListDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DialogCompletion<AchievementListDialogPayload>(DialogReturnSemantic.Close, null, "scripted"));

        public Task<DialogCompletion<TextDialogPayload>> ShowTextAsync(
            TextDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DialogCompletion<TextDialogPayload>(DialogReturnSemantic.Close, null, "scripted"));

        public Task<DialogCompletion<WarningConfirmDialogPayload>> ShowWarningConfirmAsync(
            WarningConfirmDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
        {
            WarningConfirmCallCount++;
            WarningConfirmRequests.Add(request);
            return Task.FromResult(new DialogCompletion<WarningConfirmDialogPayload>(
                _warningConfirmReturn,
                _warningConfirmReturn == DialogReturnSemantic.Confirm ? new WarningConfirmDialogPayload(true) : null,
                "scripted"));
        }
    }

    private sealed class FailingSettingsFeatureService : ISettingsFeatureService
    {
        private readonly ISettingsFeatureService _inner;

        public FailingSettingsFeatureService(ISettingsFeatureService inner)
        {
            _inner = inner;
        }

        public Task<UiOperationResult> SaveGlobalSettingAsync(string key, string value, CancellationToken cancellationToken = default)
            => Task.FromResult(UiOperationResult.Fail(
                UiErrorCode.CopilotListPersistenceFailed,
                "Copilot 列表持久化失败（模拟）。"));

        public Task<UiOperationResult> SaveGlobalSettingsAsync(
            IReadOnlyDictionary<string, string> updates,
            CancellationToken cancellationToken = default)
            => Task.FromResult(UiOperationResult.Fail(
                UiErrorCode.CopilotListPersistenceFailed,
                "Copilot 列表持久化失败（模拟）。"));

        public Task<UiOperationResult> TestNotificationAsync(string title, string message, CancellationToken cancellationToken = default)
            => _inner.TestNotificationAsync(title, message, cancellationToken);

        public Task<UiOperationResult> RegisterHotkeyAsync(string name, string gesture, CancellationToken cancellationToken = default)
            => _inner.RegisterHotkeyAsync(name, gesture, cancellationToken);

        public Task<UiOperationResult<bool>> GetAutostartStatusAsync(CancellationToken cancellationToken = default)
            => _inner.GetAutostartStatusAsync(cancellationToken);

        public Task<UiOperationResult> SetAutostartAsync(bool enabled, CancellationToken cancellationToken = default)
            => _inner.SetAutostartAsync(enabled, cancellationToken);

        public Task<UiOperationResult<string>> BuildIssueReportAsync(CancellationToken cancellationToken = default)
            => _inner.BuildIssueReportAsync(cancellationToken);
    }

    private sealed class FakeBridge : IMaaCoreBridge
    {
        private readonly Channel<CoreCallbackEvent> _channel = Channel.CreateUnbounded<CoreCallbackEvent>();

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
            => Task.FromResult(CoreResult<byte[]>.Fail(new CoreError(CoreErrorCode.GetImageFailed, "not supported")));

        public async IAsyncEnumerable<CoreCallbackEvent> CallbackStreamAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var callback in _channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return callback;
            }
        }

        public ValueTask DisposeAsync()
        {
            _channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
