using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using MAAUnified.App.Features.Dialogs;
using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.App.ViewModels.Toolbox;
using MAAUnified.Application.Models;
using MAAUnified.CoreBridge;
using LegacyConfigurationKeys = MAAUnified.Compat.Constants.ConfigurationKeys;

namespace MAAUnified.Tests;

public sealed class ToolboxModuleO2FeatureTests
{
    [Fact]
    public async Task InitializeAsync_ShouldLoadBridgeSettingsAndPersistedToolData()
    {
        var globalSeeds = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LegacyConfigurationKeys.ChooseLevel3] = "false",
            [LegacyConfigurationKeys.ChooseLevel4] = "true",
            [LegacyConfigurationKeys.ChooseLevel5] = "true",
            [LegacyConfigurationKeys.ChooseLevel6] = "false",
            [LegacyConfigurationKeys.ToolBoxChooseLevel3Time] = "510",
            [LegacyConfigurationKeys.ToolBoxChooseLevel4Time] = "520",
            [LegacyConfigurationKeys.ToolBoxChooseLevel5Time] = "530",
            [LegacyConfigurationKeys.AutoSetTime] = "false",
            [LegacyConfigurationKeys.RecruitmentShowPotential] = "false",
            [LegacyConfigurationKeys.GachaShowDisclaimerNoMore] = "true",
            [LegacyConfigurationKeys.PeepTargetFps] = "37",
            [LegacyConfigurationKeys.MiniGameTaskName] = "MiniGame@SecretFront",
            [LegacyConfigurationKeys.MiniGameSecretFrontEnding] = "D",
            [LegacyConfigurationKeys.MiniGameSecretFrontEvent] = "游侠",
            [LegacyConfigurationKeys.OperBoxData] = "[{\"id\":\"char_003_kalts\",\"name\":\"凯尔希\",\"rarity\":6,\"elite\":2,\"level\":90,\"own\":true,\"potential\":6}]",
            [LegacyConfigurationKeys.DepotResult] = "{\"done\":true,\"data\":\"{\\\"2001\\\":123}\",\"syncTime\":\"2026-03-12T08:00:00.0000000+00:00\"}",
        };

        await using var fixture = await ToolboxTestFixture.CreateAsync(globalSeeds);
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);

        await vm.InitializeAsync();

        Assert.False(vm.ChooseLevel3);
        Assert.True(vm.ChooseLevel4);
        Assert.True(vm.ChooseLevel5);
        Assert.False(vm.ChooseLevel6);
        Assert.Equal(510, vm.RecruitLevel3Time);
        Assert.Equal(520, vm.RecruitLevel4Time);
        Assert.Equal(530, vm.RecruitLevel5Time);
        Assert.False(vm.RecruitAutoSetTime);
        Assert.False(vm.RecruitmentShowPotential);
        Assert.True(vm.GachaShowDisclaimerNoMore);
        Assert.False(vm.GachaShowDisclaimer);
        Assert.Equal(37, vm.PeepTargetFps);
        Assert.Equal("MiniGame@SecretFront", vm.MiniGameTaskName);
        Assert.Equal("D", vm.MiniGameSecretFrontEnding);
        Assert.Equal("游侠", vm.MiniGameSecretFrontEvent);
        Assert.Equal("MiniGame@SecretFront@Begin@EndingD@游侠", vm.GetMiniGameTask());
        Assert.Empty(vm.OperBoxHaveList);
        vm.SelectedTabIndex = 1;
        Dispatcher.UIThread.RunJobs(null);
        Assert.Single(vm.OperBoxHaveList);
        Assert.Single(vm.DepotResult);
        Assert.Contains("char_003_kalts", vm.OperBoxExportText, StringComparison.Ordinal);
        Assert.Contains("@penguin-statistics/depot", vm.ArkPlannerResult, StringComparison.Ordinal);
        Assert.Contains("2001", vm.LoliconResult, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeAsync_ShouldResolveToolboxImagesAndDefaultMiniGameTips()
    {
        var globalSeeds = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LegacyConfigurationKeys.OperBoxData] = "[{\"id\":\"char_003_kalts\",\"name\":\"凯尔希\",\"rarity\":6,\"elite\":2,\"level\":90,\"own\":true,\"potential\":6}]",
            [LegacyConfigurationKeys.DepotResult] = "{\"done\":true,\"data\":\"{\\\"2001\\\":123}\"}",
        };

        await using var fixture = await ToolboxTestFixture.CreateAsync(globalSeeds);
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);

        await vm.InitializeAsync();
        Assert.Empty(vm.OperBoxHaveList);
        vm.SelectedTabIndex = 1;
        Dispatcher.UIThread.RunJobs(null);

        Assert.NotNull(ToolboxAssetCatalog.ResolveOperatorEliteAssetPath(vm.OperBoxHaveList[0].Elite));
        Assert.NotNull(ToolboxAssetCatalog.ResolveOperatorPotentialAssetPath(vm.OperBoxHaveList[0].Potential));
        var itemPath = ToolboxAssetCatalog.ResolveItemImagePath(vm.DepotResult[0].Id);
        Assert.NotNull(itemPath);
        Assert.Contains(Path.Combine("Assets", "Toolbox", "Items"), itemPath!, StringComparison.Ordinal);

        vm.MiniGameTaskName = "SS@Store@Begin";
        Assert.Equal("请在活动商店页面开始。\n不买无限池。", vm.MiniGameTip);
        vm.MiniGameTaskName = "MiniGame@SecretFront";
        Assert.Equal("在选小队界面开始，如有存档须手动删除。\n第一次打自己看完把教程关了。\n推荐勾选游戏内「继承上一支队伍发回的数据」", vm.MiniGameTip);
    }

    [Fact]
    public async Task SetLanguage_ShouldRefreshToolboxLocalizedHeadersAndTips()
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync();
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);
        await vm.InitializeAsync();

        vm.SetLanguage("en-us");

        var notOwnedHeaderTemplate = vm.Texts["Toolbox.OperBox.Header.NotOwned"];
        var ownedHeaderTemplate = vm.Texts["Toolbox.OperBox.Header.Owned"];
        var currentMiniGame = Assert.Single(
            vm.MiniGameTaskList.Where(item => string.Equals(item.Value, vm.MiniGameTaskName, StringComparison.Ordinal)));
        var expectedMiniGameTip = string.IsNullOrWhiteSpace(currentMiniGame.Tip)
            ? string.Format(CultureInfo.InvariantCulture, vm.Texts["Toolbox.MiniGame.CurrentTask"], currentMiniGame.Display)
            : currentMiniGame.Tip;

        AssertHeaderMatchesLocalizedTemplate(notOwnedHeaderTemplate, vm.OperBoxNotHaveHeader);
        AssertHeaderMatchesLocalizedTemplate(ownedHeaderTemplate, vm.OperBoxHaveHeader);
        Assert.Equal("Not synced yet", vm.LastDepotSyncTimeText);
        Assert.Equal("Peek through MAA's eyes?", vm.PeepTip);
        Assert.Equal(expectedMiniGameTip, vm.MiniGameTip);
    }

    [Fact]
    public async Task SetLanguage_ShouldNotifyLocalizedTextMapsForViewBindings()
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync();
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);
        await vm.InitializeAsync();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.SetLanguage("en-us");

        Assert.Contains(nameof(ToolboxPageViewModel.Texts), changed);
        Assert.Contains(nameof(ToolboxPageViewModel.RootTexts), changed);
        Assert.Contains(nameof(ToolboxPageViewModel.RecruitTabTitle), changed);
        Assert.Contains(nameof(ToolboxPageViewModel.ExecutionReviewTitle), changed);
        Assert.Contains(string.Empty, changed);
    }

    [Fact]
    public async Task SetLanguage_ShouldRefreshToolboxTextsAcrossMultipleLanguageSwitches()
    {
        var globalSeeds = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LegacyConfigurationKeys.OperBoxData] = "[{\"id\":\"char_003_kalts\",\"name\":\"凯尔希\",\"rarity\":6,\"elite\":2,\"level\":90,\"own\":true,\"potential\":6}]",
        };

        await using var fixture = await ToolboxTestFixture.CreateAsync(globalSeeds);
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);
        await vm.InitializeAsync();
        vm.SelectedTabIndex = 1;
        Dispatcher.UIThread.RunJobs(null);

        var zhTexts = vm.Texts;

        vm.SetLanguage("en-us");

        var enTexts = vm.Texts;
        Assert.NotSame(zhTexts, enTexts);
        Assert.Equal("Recruit Recognition", vm.Texts["Toolbox.Tab.Recruit"]);
        Assert.Equal("Copy to clipboard", vm.Texts["Toolbox.OperBox.CopyToClipboard"]);
        Assert.Equal(vm.Texts["Toolbox.Tab.Recruit"], vm.RecruitTabTitle);
        Assert.Equal(vm.Texts["Toolbox.OperBox.CopyToClipboard"], vm.OperBoxCopyToClipboardText);
        Assert.Equal(vm.Texts["Toolbox.Action.StartRecognition"], vm.StartRecognitionText);
        Assert.Equal(vm.Texts["Toolbox.Section.ExecutionReview"], vm.ExecutionReviewTitle);
        AssertHeaderMatchesLocalizedTemplate(vm.Texts["Toolbox.OperBox.Header.Owned"], vm.OperBoxHaveHeader);

        vm.SetLanguage("ja-jp");

        var jaTexts = vm.Texts;
        Assert.NotSame(enTexts, jaTexts);
        Assert.Equal("公開求人認識", vm.Texts["Toolbox.Tab.Recruit"]);
        Assert.Equal("クリップボードにコピー", vm.Texts["Toolbox.OperBox.CopyToClipboard"]);
        Assert.Equal("目標FPS", vm.Texts["Toolbox.Peep.TargetFps"]);
        Assert.Equal(vm.Texts["Toolbox.Tab.Recruit"], vm.RecruitTabTitle);
        Assert.Equal(vm.Texts["Toolbox.OperBox.CopyToClipboard"], vm.OperBoxCopyToClipboardText);
        Assert.Equal(vm.Texts["Toolbox.Action.StartRecognition"], vm.StartRecognitionText);
        Assert.Equal(vm.Texts["Toolbox.Section.ExecutionReview"], vm.ExecutionReviewTitle);
        Assert.Equal(vm.Texts["Toolbox.Peep.TargetFps"], vm.PeepTargetFpsText);
        AssertHeaderMatchesLocalizedTemplate(vm.Texts["Toolbox.OperBox.Header.Owned"], vm.OperBoxHaveHeader);
    }

    [Fact]
    public async Task StartRecruitAsync_ShouldConnectAppendStartAndPersistSettings()
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync(
            profileSeeds: new Dictionary<string, JsonNode?>
            {
                ["ServerType"] = JsonValue.Create("KR"),
            });
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);
        await vm.InitializeAsync();
        vm.ChooseLevel3 = true;
        vm.ChooseLevel4 = false;
        vm.ChooseLevel5 = true;
        vm.ChooseLevel6 = true;
        vm.RecruitLevel3Time = 500;
        vm.RecruitLevel4Time = 510;
        vm.RecruitLevel5Time = 520;

        await vm.StartRecruitAsync();

        Assert.Equal(ToolboxExecutionState.Executing, vm.ExecutionState);
        Assert.Equal(1, fixture.Bridge.ConnectCallCount);
        Assert.Equal(1, fixture.Bridge.StartCallCount);
        var task = Assert.Single(fixture.Bridge.AppendedTasks);
        Assert.Equal("Recruit", task.Type);
        var payload = Assert.IsType<JsonObject>(JsonNode.Parse(task.ParamsJson));
        var selectLevels = Assert.IsType<JsonArray>(payload["select"]);
        var selected = selectLevels
            .Select(node => node is null ? int.MinValue : node.GetValue<int>())
            .ToArray();
        Assert.Contains(3, selected);
        Assert.Contains(5, selected);
        Assert.Contains(6, selected);
        Assert.DoesNotContain(4, selected);
        Assert.True(payload["set_time"]?.GetValue<bool>() ?? false);
        var recruitTime = Assert.IsType<JsonObject>(payload["recruitment_time"]);
        Assert.DoesNotContain("6", recruitTime.Select(pair => pair.Key), StringComparer.Ordinal);
        Assert.Equal("KR", payload["server"]?.GetValue<string>());
        Assert.Equal("500", fixture.Config.CurrentConfig.GlobalValues[LegacyConfigurationKeys.ToolBoxChooseLevel3Time]?.ToString());
        Assert.Equal("520", fixture.Config.CurrentConfig.GlobalValues[LegacyConfigurationKeys.ToolBoxChooseLevel5Time]?.ToString());
        Assert.Equal("Toolbox", fixture.Runtime.SessionService.CurrentRunOwner);
    }

    [Fact]
    public async Task StartDepotAsync_ShouldClearPreviousResultsBeforeRecognition()
    {
        var globalSeeds = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LegacyConfigurationKeys.DepotResult] = "{\"done\":true,\"data\":\"{\\\"2001\\\":123}\"}",
        };

        await using var fixture = await ToolboxTestFixture.CreateAsync(globalSeeds);
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);
        await vm.InitializeAsync();
        Assert.Single(vm.DepotResult);

        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await vm.StartDepotAsync();

        Assert.Empty(vm.DepotResult);
        Assert.False(vm.HasDepotResult);
        Assert.DoesNotContain("2001", vm.ArkPlannerResult, StringComparison.Ordinal);
        Assert.DoesNotContain("2001", vm.LoliconResult, StringComparison.Ordinal);
        Assert.Contains(nameof(ToolboxPageViewModel.ArkPlannerResult), changed);
        Assert.Contains(nameof(ToolboxPageViewModel.LoliconResult), changed);
        Assert.Equal(ToolboxExecutionState.Executing, vm.ExecutionState);
        Assert.Equal("Depot", Assert.Single(fixture.Bridge.AppendedTasks).Type);
    }

    [Fact]
    public async Task StartToolAsync_WhenToolboxBusy_ShouldShowDedicatedBusyDialogWithoutAppending()
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync();
        var dialogService = new RecordingDialogService(DialogReturnSemantic.Confirm);
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState, dialogService);
        await vm.InitializeAsync();

        DialogErrorRaisedEvent? raised = null;
        fixture.Runtime.DialogFeatureService.ErrorRaised += (_, e) => raised = e;

        await vm.StartRecruitAsync();
        await vm.StartDepotAsync();

        Assert.Equal(1, dialogService.WarningConfirmCallCount);
        Assert.Equal("Toolbox.Busy", dialogService.LastScope);
        Assert.NotNull(dialogService.LastRequest);
        Assert.Contains("正在执行", dialogService.LastRequest!.Title, StringComparison.Ordinal);
        Assert.Contains("招募识别", dialogService.LastRequest.Message, StringComparison.Ordinal);
        Assert.Equal("取消", dialogService.LastRequest.ConfirmText);
        Assert.Equal("停止当前任务", dialogService.LastRequest.CancelText);
        var appendedTask = Assert.Single(fixture.Bridge.AppendedTasks);
        Assert.Equal("Recruit", appendedTask.Type);
        Assert.Equal("Toolbox", fixture.Runtime.SessionService.CurrentRunOwner);
        Assert.Equal(ToolboxExecutionState.Executing, vm.ExecutionState);
        Assert.Equal(UiErrorCode.ToolboxExecutionFailed, vm.LastExecutionErrorCode);
        Assert.Null(raised);
        Assert.Equal(0, dialogService.ErrorCallCount);
    }

    [Fact]
    public async Task StartToolAsync_WhenBridgeNotInitialized_ShouldShowRetryableDialogWithoutGlobalErrorPopup()
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync();
        fixture.Bridge.ForceConnectFailure = true;
        fixture.Bridge.ConnectFailureCode = CoreErrorCode.NotInitialized;
        fixture.Bridge.ConnectFailureMessage = "Bridge is not initialized.";
        var dialogService = new RecordingDialogService(DialogReturnSemantic.Close);
        DialogErrorRaisedEvent? raised = null;
        fixture.Runtime.DialogFeatureService.ErrorRaised += (_, e) => raised = e;
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState, dialogService);
        await vm.InitializeAsync();

        await vm.StartRecruitAsync();

        Assert.Equal(1, dialogService.WarningConfirmCallCount);
        Assert.Equal("Toolbox.RetryableError", dialogService.LastScope);
        Assert.NotNull(dialogService.LastRequest);
        Assert.Contains("手速", dialogService.LastRequest!.Message, StringComparison.Ordinal);
        Assert.Equal("重试", dialogService.LastRequest.ConfirmText);
        Assert.Equal("稍后再试", dialogService.LastRequest.CancelText);
        Assert.Equal(ToolboxExecutionState.Failed, vm.ExecutionState);
        Assert.Equal(CoreErrorCode.NotInitialized.ToString(), vm.LastExecutionErrorCode);
        Assert.Null(raised);
        Assert.Equal(0, dialogService.ErrorCallCount);
    }

    [Fact]
    public async Task StartToolAsync_WhenBridgeNotInitializedAndDetailsClicked_ShouldOpenErrorDetails()
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync();
        fixture.Bridge.ForceConnectFailure = true;
        fixture.Bridge.ConnectFailureCode = CoreErrorCode.NotInitialized;
        fixture.Bridge.ConnectFailureMessage = "Bridge is not initialized.";
        var dialogService = new RecordingDialogService(DialogReturnSemantic.Details);
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState, dialogService);
        await vm.InitializeAsync();

        await vm.StartRecruitAsync();

        Assert.Equal(1, dialogService.WarningConfirmCallCount);
        Assert.Equal(1, dialogService.ErrorCallCount);
        Assert.Equal("Toolbox.Busy.ErrorDetails", dialogService.LastErrorScope);
        Assert.NotNull(dialogService.LastErrorRequest);
        Assert.Equal("Toolbox.Recruit", dialogService.LastErrorRequest!.Context);
        Assert.Equal(CoreErrorCode.NotInitialized.ToString(), dialogService.LastErrorRequest.Result.Error?.Code);
    }

    [Fact]
    public async Task StartOperBoxAsync_WhenConnectionFails_ShouldReportFriendlyConnectFailedError()
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync();
        fixture.Bridge.ForceConnectFailure = true;
        DialogErrorRaisedEvent? raised = null;
        fixture.Runtime.DialogFeatureService.ErrorRaised += (_, e) => raised = e;
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);
        await vm.InitializeAsync();

        await vm.StartOperBoxAsync();

        Assert.Equal(ToolboxExecutionState.Failed, vm.ExecutionState);
        Assert.Equal(UiErrorCode.ConnectFailed, vm.LastExecutionErrorCode);
        Assert.NotNull(raised);
        Assert.Equal("Toolbox.OperBox", raised!.Context);
        Assert.Equal(UiErrorCode.ConnectFailed, raised.Result.Error?.Code);
        Assert.DoesNotContain("Connection command failed to exec", raised.Result.Message, StringComparison.Ordinal);
        Assert.Contains("\"stage\":\"connect\"", raised.Result.Error?.Details ?? string.Empty, StringComparison.Ordinal);

        var localized = DialogTextCatalog.LocalizeErrorResult("zh-cn", raised.Result);
        Assert.Equal("连接模拟器失败。", localized.Message);
        Assert.Contains("ADB", DialogTextCatalog.BuildErrorSuggestion("zh-cn", raised.Result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartGachaAsync_ShouldStartCustomTaskAndAutoPeep()
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync();
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);
        await vm.InitializeAsync();
        vm.AgreeGachaDisclaimer();

        await vm.StartGachaAsync(once: false);

        Assert.Equal(ToolboxExecutionState.Executing, vm.ExecutionState);
        Assert.True(vm.IsGachaInProgress);
        Assert.True(vm.Peeping);
        Assert.Equal(1, fixture.Bridge.StartCallCount);
        Assert.Single(fixture.Bridge.AppendedTasks);
        Assert.Contains("GachaTenTimes", fixture.Bridge.AppendedTasks[0].ParamsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmGachaDisclaimerAsync_ShouldIssueLocalizedDialogRequest_AndAcceptOnConfirm()
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync();
        var dialogService = new RecordingDialogService(DialogReturnSemantic.Confirm);
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState, dialogService);
        await vm.InitializeAsync();

        vm.SetLanguage("en-us");

        var confirmed = await vm.ConfirmGachaDisclaimerAsync();

        Assert.True(confirmed);
        Assert.False(vm.GachaShowDisclaimer);
        Assert.Equal(1, dialogService.WarningConfirmCallCount);
        Assert.Equal("Toolbox.Gacha.Disclaimer", dialogService.LastScope);
        var request = dialogService.LastRequest;
        Assert.NotNull(request);
        Assert.Equal(DialogTextCatalog.WarningDialogTitle("en-us"), request.Title);
        Assert.Equal(vm.GachaWarningText, request.Message);
        Assert.Equal(DialogTextCatalog.WarningDialogConfirmButton("en-us"), request.ConfirmText);
        Assert.Equal(DialogTextCatalog.WarningDialogCancelButton("en-us"), request.CancelText);
        Assert.Equal("en-us", request.Language);

        var chrome = request.Chrome;
        Assert.NotNull(chrome);
        var chromeSnapshot = chrome!.GetSnapshot("en-us");
        Assert.Equal(vm.GachaWarningText, chromeSnapshot.GetNamedTextOrDefault(DialogTextCatalog.ChromeKeys.Prompt));
        Assert.Equal(vm.GachaDisclaimerLeadText, chromeSnapshot.GetNamedTextOrDefault(DialogTextCatalog.ChromeKeys.LeadText));
        Assert.Equal(vm.GachaDisclaimerEmphasisText, chromeSnapshot.GetNamedTextOrDefault(DialogTextCatalog.ChromeKeys.EmphasisText));
        Assert.Equal(vm.GachaDisclaimerBodyText, chromeSnapshot.GetNamedTextOrDefault(DialogTextCatalog.ChromeKeys.DetailText));
    }

    [Fact]
    public async Task ConfirmGachaDisclaimerAsync_ShouldKeepDisclaimerVisible_WhenDialogIsNotConfirmed()
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync();
        var dialogService = new RecordingDialogService(DialogReturnSemantic.Cancel);
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState, dialogService);
        await vm.InitializeAsync();

        var confirmed = await vm.ConfirmGachaDisclaimerAsync();

        Assert.False(confirmed);
        Assert.True(vm.GachaShowDisclaimer);
        Assert.Equal(1, dialogService.WarningConfirmCallCount);
    }

    [Fact]
    public async Task TogglePeepAsync_ShouldAcquireOwnerAndReleaseOnSecondToggle()
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync();
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);
        await vm.InitializeAsync();

        await vm.TogglePeepAsync();
        Assert.True(vm.Peeping);
        Assert.Equal("Toolbox", fixture.Runtime.SessionService.CurrentRunOwner);

        await vm.TogglePeepAsync();
        Assert.False(vm.Peeping);
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
    }

    [Fact]
    public async Task StopActiveToolAsync_WhenRecruitRunning_ShouldStopViaToolboxService()
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync();
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);
        await vm.InitializeAsync();

        await vm.StartRecruitAsync();
        Assert.True(vm.IsRecruitExecuting);
        Assert.Equal(vm.Texts["Toolbox.Action.Recognizing"], vm.RecruitStartRecognitionText);

        vm.SetToolActionHover(ToolboxToolKind.Recruit, hovering: true);
        Assert.Equal(vm.Texts["Toolbox.Action.Stop"], vm.RecruitStartRecognitionText);

        await vm.StopActiveToolAsync();

        Assert.Equal(1, fixture.Bridge.StopCallCount);
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
        Assert.Equal(ToolboxExecutionState.Failed, vm.ExecutionState);
        Assert.Equal(UiErrorCode.ToolboxExecutionCancelled, vm.LastExecutionErrorCode);
        Assert.Equal(vm.Texts["Toolbox.Status.ManuallyStopped"], vm.RecruitInfo);
        Assert.Equal(vm.Texts["Toolbox.Action.StartRecognition"], vm.RecruitStartRecognitionText);
    }

    [Fact]
    public async Task InitializeAsync_ShouldLoadMiniGameEntriesFromStageActivityOverride()
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "gui"));
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Root, "gui", "StageActivityV2.json"),
            """
            {
              "Official": {
                "miniGame": [
                  {
                    "Display": "测试小游戏",
                    "Value": "MiniGame@Test@Begin",
                    "Tip": "测试提示"
                  }
                ]
              }
            }
            """);

        using var _ = ToolboxAssetCatalog.PushTestBaseDirectoriesForTests(fixture.Root);
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);

        await vm.InitializeAsync();

        var entry = Assert.Single(vm.MiniGameTaskList, item => item.Value == "MiniGame@Test@Begin");
        Assert.Equal("测试小游戏", entry.Display);
        Assert.Equal("测试提示", entry.Tip);
    }

    [Fact]
    public async Task MiniGameEntries_ShouldFilterByCurrentClient_AndRefreshWhenClientChanges()
    {
        await using var fixture = await ToolboxTestFixture.CreateAsync();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "gui"));
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Root, "gui", "StageActivityV2.json"),
            """
            {
              "Official": {
                "miniGame": [
                  {
                    "Display": "国服小游戏",
                    "Value": "MiniGame@Official@Begin",
                    "Tip": "国服提示"
                  }
                ]
              },
              "YoStarEN": {
                "miniGame": [
                  {
                    "Display": "EN小游戏",
                    "Value": "MiniGame@EN@Begin",
                    "Tip": "EN提示"
                  }
                ]
              }
            }
            """);

        using var _ = ToolboxAssetCatalog.PushTestBaseDirectoriesForTests(fixture.Root);
        fixture.ConnectionState.ClientType = "Official";
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);

        await vm.InitializeAsync();

        Assert.Contains(vm.MiniGameTaskList, item => item.Value == "MiniGame@Official@Begin");
        Assert.DoesNotContain(vm.MiniGameTaskList, item => item.Value == "MiniGame@EN@Begin");

        fixture.ConnectionState.ClientType = "YoStarEN";

        Assert.Contains(vm.MiniGameTaskList, item => item.Value == "MiniGame@EN@Begin");
        Assert.DoesNotContain(vm.MiniGameTaskList, item => item.Value == "MiniGame@Official@Begin");
    }

    private static void AssertHeaderMatchesLocalizedTemplate(string template, string actual)
    {
        const string placeholder = "{0}";
        var index = template.IndexOf(placeholder, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Header template must contain {placeholder}: {template}");

        var prefix = template[..index];
        var suffix = template[(index + placeholder.Length)..];
        var pattern = $"^{Regex.Escape(prefix)}\\d+{Regex.Escape(suffix)}$";
        Assert.Matches(pattern, actual);
    }

    private sealed class RecordingDialogService : IAppDialogService
    {
        private readonly DialogReturnSemantic _warningConfirmReturn;

        public RecordingDialogService(DialogReturnSemantic warningConfirmReturn)
        {
            _warningConfirmReturn = warningConfirmReturn;
        }

        public int WarningConfirmCallCount { get; private set; }

        public string? LastScope { get; private set; }

        public WarningConfirmDialogRequest? LastRequest { get; private set; }

        public int ErrorCallCount { get; private set; }

        public string? LastErrorScope { get; private set; }

        public ErrorDialogRequest? LastErrorRequest { get; private set; }

        public Task<DialogCompletion<AnnouncementDialogPayload>> ShowAnnouncementAsync(
            AnnouncementDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DialogCompletion<AnnouncementDialogPayload>(DialogReturnSemantic.Close, null, "recording"));

        public Task<DialogCompletion<VersionUpdateDialogPayload>> ShowVersionUpdateAsync(
            VersionUpdateDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DialogCompletion<VersionUpdateDialogPayload>(DialogReturnSemantic.Close, null, "recording"));

        public Task<DialogCompletion<ProcessPickerDialogPayload>> ShowProcessPickerAsync(
            ProcessPickerDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DialogCompletion<ProcessPickerDialogPayload>(DialogReturnSemantic.Close, null, "recording"));

        public Task<DialogCompletion<EmulatorPathDialogPayload>> ShowEmulatorPathAsync(
            EmulatorPathDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DialogCompletion<EmulatorPathDialogPayload>(DialogReturnSemantic.Close, null, "recording"));

        public Task<DialogCompletion<ErrorDialogPayload>> ShowErrorAsync(
            ErrorDialogRequest request,
            string sourceScope,
            Func<CancellationToken, Task<UiOperationResult>>? openIssueReportAsync = null,
            Func<CancellationToken, Task<UiOperationResult>>? openSettingsAsync = null,
            CancellationToken cancellationToken = default)
        {
            ErrorCallCount++;
            LastErrorScope = sourceScope;
            LastErrorRequest = request;
            return Task.FromResult(new DialogCompletion<ErrorDialogPayload>(DialogReturnSemantic.Close, null, "recording"));
        }

        public Task<DialogCompletion<AchievementListDialogPayload>> ShowAchievementListAsync(
            AchievementListDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DialogCompletion<AchievementListDialogPayload>(DialogReturnSemantic.Close, null, "recording"));

        public Task<DialogCompletion<TextDialogPayload>> ShowTextAsync(
            TextDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DialogCompletion<TextDialogPayload>(DialogReturnSemantic.Close, null, "recording"));

        public Task<DialogCompletion<WarningConfirmDialogPayload>> ShowWarningConfirmAsync(
            WarningConfirmDialogRequest request,
            string sourceScope,
            CancellationToken cancellationToken = default)
        {
            WarningConfirmCallCount++;
            LastScope = sourceScope;
            LastRequest = request;
            return Task.FromResult(new DialogCompletion<WarningConfirmDialogPayload>(
                _warningConfirmReturn,
                _warningConfirmReturn == DialogReturnSemantic.Confirm ? new WarningConfirmDialogPayload(true) : null,
                "recording"));
        }
    }
}
