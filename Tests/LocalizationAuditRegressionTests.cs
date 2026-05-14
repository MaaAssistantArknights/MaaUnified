using System.Text.RegularExpressions;
using MAAUnified.App.ViewModels;
using MAAUnified.App.ViewModels.Copilot;
using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.App.ViewModels.TaskQueue;
using MAAUnified.App.ViewModels.Toolbox;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using MAAUnified.CoreBridge;
using MAAUnified.Platform;

namespace MAAUnified.Tests;

[Collection("MainShellSerial")]
public sealed class LocalizationAuditRegressionTests
{
    private static readonly HashSet<string> RootTechnicalLiteralKeys = new(StringComparer.Ordinal)
    {
        "Settings.RemoteControl.GetTaskEndpointWatermark",
        "Settings.RemoteControl.ReportTaskEndpointWatermark",
        "Settings.VersionUpdate.ProxyAddressWatermark",
    };

    private static readonly string[] MainTabKeys =
    [
        "Main.Tab.TaskQueue",
        "Main.Tab.Copilot",
        "Main.Tab.Toolbox",
        "Main.Tab.Settings",
    ];

    private static readonly string[] TargetViewFiles =
    [
        "App/Features/Advanced/CopilotView.axaml",
        "App/Features/Advanced/ToolboxView.axaml",
        "App/Features/Root/TaskQueueView.axaml",
        "App/Features/Root/SettingsView.axaml",
    ];

    private static readonly string[] RootCriticalKeys =
    [
        "TaskQueue.Root.TaskListTitle",
        "TaskQueue.Root.TaskConfigTitle",
        "TaskQueue.Root.GeneralSettings",
        "TaskQueue.Root.AdvancedSettings",
        "TaskQueue.Root.RenameDialogTitle",
        "TaskQueue.Root.RenameDialogPrompt",
        "TaskQueue.Root.RenameDialogConfirm",
        "TaskQueue.Root.RenameDialogCancel",
        "TaskQueue.Root.RenameDialogCancelStatus",
        "TaskQueue.Root.RenameDialogClosedStatus",
        "TaskQueue.Root.OverlayTargetPickerTitle",
        "TaskQueue.Root.OverlayTargetPickerConfirm",
        "TaskQueue.Root.OverlayTargetPickerCancel",
        "TaskQueue.Root.OverlayTargetPickerEmptyTitle",
        "TaskQueue.Root.OverlayTargetPickerEmptyBody",
        "TaskQueue.Root.OverlayTargetPickerCancelStatus",
        "TaskQueue.Root.OverlayTargetPickerClosedStatus",
        "Settings.Section.Performance",
        "Settings.Section.Game",
        "Settings.Section.GUI",
        "Settings.Section.VersionUpdate",
        "Settings.Section.About",
        "Settings.ConfigurationManager.ProfileName",
        "Settings.ConfigurationManager.NewProfileWatermark",
        "Settings.ConfigurationManager.SaveAsNew",
        "Settings.ConfigurationManager.SaveAsNewSucceededInline",
        "Settings.ConfigurationManager.SaveAsNewFailedInline",
        "Settings.ConfigurationManager.SaveAsNewFailedReason.Generic",
        "Settings.ConfigurationManager.SaveAsNewFailedReason.ProfileAlreadyExists",
        "Settings.ConfigurationManager.SaveAsNewFailedReason.ProfileNameEmpty",
        "Settings.ConfigurationManager.SaveAsNewFailedReason.ProfileNameInvalid",
        "Settings.ConfigurationManager.SaveAsNewFailedReason.SourceProfileMissing",
        "Settings.ConfigurationManager.DeleteCurrent",
        "Settings.ConfigurationManager.AutoSavedCurrent",
        "Settings.ConfigurationManager.ExportAll",
        "Settings.ConfigurationManager.ExportCurrent",
        "Settings.ConfigurationManager.Import",
        "Settings.ConfigurationManager.Dialog.DeleteTitle",
        "Settings.ConfigurationManager.Dialog.DeleteMessage",
        "Settings.ConfigurationManager.Dialog.ExportAllTitle",
        "Settings.ConfigurationManager.Dialog.ExportCurrentTitle",
        "Settings.ConfigurationManager.Dialog.ImportTitle",
        "Settings.ConfigurationManager.Dialog.ImportLegacyTitle",
        "Settings.ConfigurationManager.Dialog.ImportConfigTitle",
        "Settings.ConfigurationManager.Dialog.DamagedFiles",
        "Settings.ConfigurationManager.Dialog.DamagedFilesNoImport",
        "Settings.ConfigurationManager.Import.NoFilesSelected",
        "Settings.ConfigurationManager.Import.TooManyFiles",
        "Settings.ConfigurationManager.Import.UnifiedSingleFileOnly",
        "Settings.ConfigurationManager.Import.UnrecognizedFiles",
        "Settings.ConfigurationManager.Import.LegacyFilenameInvalid",
        "Settings.ConfigurationManager.Import.LegacyDirectoryMissingBoth",
        "Settings.ConfigurationManager.Import.LegacyDirectoryMissingGui",
        "Settings.ConfigurationManager.Import.LegacyDirectoryMissingGuiNew",
        "Settings.ConfigurationManager.Import.MissingLegacyParts",
        "Settings.ConfigurationManager.Import.LegacyInvalidFilesOnly",
        "Settings.ConfigurationManager.Import.LegacyInvalidFilesWithMissing",
        "Settings.ConfigurationManager.Import.ContextRefresh",
        "Settings.ConfigurationManager.ImportLegacy",
        "Settings.ConfigurationManager.ImportLegacy.ContextRefresh",
        "Settings.ConfigurationManager.ImportLegacy.PendingConfirmation",
        "Settings.Timer.ForceScheduledStart",
        "Settings.Timer.CustomConfig",
        "Settings.Timer.ShowWindowBeforeForceScheduledStart",
        "Settings.Timer.SlotLabel",
        "Settings.State.Saving",
        "Settings.State.Saved",
        "Settings.Performance.Gpu.Select",
        "Settings.Performance.Gpu.CustomDescription",
        "Settings.Performance.Gpu.CustomInstancePath",
        "Settings.Performance.Gpu.RestartRequired",
        "Settings.Performance.Gpu.Option.Disabled",
        "Settings.Performance.Gpu.Status.Unsupported",
        "Settings.Game.ClientType",
        "Settings.Game.DeploymentWithPause",
        "Settings.Game.StartsWithScript",
        "Settings.Game.StartsWithScriptTip",
        "Settings.Game.EndsWithScript",
        "Settings.Game.EndsWithScriptTip",
        "Settings.Game.UseScriptOnCopilot",
        "Settings.Game.UseScriptOnManualStop",
        "Settings.Game.BlockSleep",
        "Settings.Game.BlockSleepWithScreenOn",
        "Settings.Game.EnablePenguin",
        "Settings.Game.EnableYituliu",
        "Settings.Game.PenguinId",
        "Settings.Game.TaskTimeoutMinutes",
        "Settings.Game.ReminderIntervalMinutes",
        "Settings.Action.SaveGui",
        "Settings.Action.SaveConnectionGame",
        "Settings.Action.SaveStartPerformance",
        "Settings.Action.SaveTimer",
        "Settings.Action.SaveRemote",
        "Settings.Action.TestRemote",
        "Settings.Action.RegisterHotkeys",
        "Settings.Action.ValidateNotification",
        "Settings.Action.TestNotification",
        "Settings.Action.SaveNotification",
        "Settings.Action.SaveVersionUpdate",
        "Settings.Action.CheckVersionUpdate",
        "Settings.Action.SaveAchievement",
        "Settings.Action.RefreshAchievement",
        "Settings.Action.ShowAchievement",
        "Settings.Action.OpenAchievementGuide",
        "Settings.Action.BuildIssueReport",
        "Settings.Action.OpenDebugDirectory",
        "Settings.Action.ClearImageCache",
        "Settings.Action.CheckAnnouncement",
        "Settings.Action.OpenOfficial",
        "Settings.Action.OpenCommunity",
        "Settings.Action.OpenDownload",
        "Settings.Action.RefreshProfiles",
        "Settings.Action.Delete",
        "Settings.Action.Cancel",
        "Settings.Action.Close",
        "Settings.Action.ChooseAgain",
        "Settings.Action.ImportAnyway",
        "Settings.Action.ImportValidContent",
        "Settings.Start.AutoStartMaa",
        "Settings.Start.AutoStartMaaTip",
        "Settings.Start.MinimizeDirectly",
        "Settings.Start.RunDirectly",
        "Settings.Start.AutoOpenEmulator",
        "Settings.Start.RetryOnDisconnected",
        "Settings.Start.RetryOnDisconnectedTip",
        "Settings.Start.ApplyOnNextLaunch",
        "Settings.Start.ApplyOnNextExecution",
        "Settings.Start.EmulatorPath",
        "Settings.Start.EmulatorPathTip",
        "Settings.Start.SelectButton",
        "Settings.Start.EmulatorExtraArgs",
        "Settings.Start.EmulatorExtraArgsTip",
        "Settings.Start.EmulatorWaitSeconds",
        "Settings.Start.EmulatorWaitSecondsNote",
        "Settings.StartPerformance.Normalize",
        "Settings.Start.Dialog.SelectEmulatorPathTitle",
        "Settings.Start.Dialog.SelectEmulatorPathConfirm",
        "Settings.Start.Dialog.SelectEmulatorPathCancel",
        "Settings.Start.Status.EmulatorPathUpdated",
        "Settings.Start.Status.EmulatorPathSelectionCancelled",
        "Settings.Start.Status.EmulatorPathSelectionClosed",
        "Settings.StartPerformance.Validation.EmulatorWaitSecondsOutOfRange",
        "Settings.StartPerformance.Validation.Passed",
        "Settings.StartPerformance.Validation.EmulatorPathRequired",
        "Settings.StartPerformance.Validation.EmulatorPathNotFound",
        "Settings.StartPerformance.Validation.GpuFallbackApplied",
        "Settings.StartPerformance.Status.SyncCoreInstanceOptionsFailed",
        "Settings.VersionUpdate.Dialog.Title",
        "Settings.VersionUpdate.Dialog.Confirm",
        "Settings.VersionUpdate.Dialog.Cancel",
        "Settings.VersionUpdate.Status.SaveChannelFailed",
        "Settings.VersionUpdate.Status.SaveChannelSucceeded",
        "Settings.VersionUpdate.Status.SaveProxyFailed",
        "Settings.VersionUpdate.Status.SaveProxySucceeded",
        "Settings.VersionUpdate.Status.CheckFailed",
        "Settings.VersionUpdate.Status.DialogConfirmed",
        "Settings.VersionUpdate.Status.DialogCancelled",
        "Settings.VersionUpdate.Status.DialogClosed",
        "Settings.VersionUpdate.Status.ResourceUpdateFailed",
        "Settings.VersionUpdate.Status.OpenChangelogFailed",
        "Settings.VersionUpdate.Status.OpenChangelogSucceeded",
        "Settings.VersionUpdate.Status.OpenResourceRepositoryFailed",
        "Settings.VersionUpdate.Status.OpenResourceRepositorySucceeded",
        "Settings.VersionUpdate.Status.OpenMirrorChyanFailed",
        "Settings.VersionUpdate.Status.OpenMirrorChyanSucceeded",
        "Settings.VersionUpdate.Status.PersistFailed",
        "Settings.VersionUpdate.StartupCheck",
        "Settings.VersionUpdate.ScheduledCheck",
        "Settings.VersionUpdate.AutoDownload",
        "Settings.VersionUpdate.AutoInstall",
        "Settings.VersionUpdate.Channel",
        "Settings.VersionUpdate.Source",
        "Settings.VersionUpdate.ForceGithub",
        "Settings.VersionUpdate.MirrorCdk",
        "Settings.VersionUpdate.Copy",
        "Settings.VersionUpdate.MirrorSite",
        "Settings.VersionUpdate.ProxyType",
        "Settings.VersionUpdate.ProxyAddress",
        "Settings.VersionUpdate.ProxyAddressWatermark",
        "Settings.VersionUpdate.ResourceApi",
        "Settings.VersionUpdate.PanelUiVersion",
        "Settings.VersionUpdate.PanelCoreVersion",
        "Settings.VersionUpdate.PanelBuildTime",
        "Settings.VersionUpdate.PanelResourceVersion",
        "Settings.VersionUpdate.PanelResourceTime",
        "Settings.VersionUpdate.SoftwareUpdate",
        "Settings.VersionUpdate.Changelog",
        "Settings.VersionUpdate.ResourceUpdate",
        "Settings.VersionUpdate.ResourceRepository",
        "Settings.VersionUpdate.Status.PersistFailedSuffix",
        "Settings.Connect.AutoDetect",
        "Settings.Connect.AutoDetectTip",
        "Settings.Connect.AlwaysAutoDetect",
        "Settings.Connect.Configuration",
        "Settings.Connect.AttachWindowWarning",
        "Settings.Connect.Address",
        "Settings.Connect.AddressTip",
        "Settings.Connect.AdbPath",
        "Settings.Connect.MuMu.EnableExtras",
        "Settings.Connect.MuMu.EmulatorPath",
        "Settings.Connect.MuMu.BridgeConnection",
        "Settings.Connect.InstanceId",
        "Settings.Connect.LdPlayer.EnableExtras",
        "Settings.Connect.LdPlayer.EmulatorPath",
        "Settings.Connect.LdPlayer.ManualSetIndex",
        "Settings.Connect.ScreencapMethod",
        "Settings.Connect.MouseInput",
        "Settings.Connect.KeyboardInput",
        "Settings.Connect.ScreenshotTest",
        "Settings.Connect.AllowAdbRestart",
        "Settings.Connect.AllowAdbHardRestart",
        "Settings.Connect.TouchMode",
        "Settings.Connect.ReplaceAdb",
        "Settings.Connect.KillAdbOnExit",
        "Settings.Connect.AdbLiteEnabled",
        "Settings.Connect.Dialog.SelectAdbPath",
        "Settings.Connect.Dialog.ScreenshotPreview",
        "Settings.Connect.Status.ConnectingEmulator",
        "Settings.Connect.Status.DownloadingAdb",
        "Settings.Connect.Status.ReplacedAdb",
        "Settings.Connect.Error.GetImageFailed",
        "Settings.Connect.Error.ScreenshotTestFailed",
        "Settings.Connect.Error.ScreenshotTestException",
        "Settings.Connect.Error.AutoReplaceUnsupported",
        "Settings.Connect.Error.ReplaceAdbExtractedNotFound",
        "Settings.Connect.Error.ReplaceAdbFailed",
        "Settings.Connect.Error.ConnectFailed",
        "Settings.Connect.Error.Details",
        "Settings.Connect.Error.ConnectionFailedShort",
        "Settings.RemoteControl.Warning",
        "Settings.RemoteControl.GetTaskEndpoint",
        "Settings.RemoteControl.GetTaskEndpointWatermark",
        "Settings.RemoteControl.ReportTaskEndpoint",
        "Settings.RemoteControl.ReportTaskEndpointWatermark",
        "Settings.RemoteControl.PollIntervalMs",
        "Settings.RemoteControl.UserIdentity",
        "Settings.RemoteControl.UserIdentityWatermark",
        "Settings.RemoteControl.DeviceIdentity",
        "Settings.RemoteControl.DeviceIdentityWatermark",
        "Settings.RemoteControl.Status.SaveFailed",
        "Settings.RemoteControl.Status.SaveSucceeded",
        "Settings.RemoteControl.Status.TestSucceeded",
        "Settings.RemoteControl.Status.TestFailed",
        "Settings.RemoteControl.Error.InvalidParameters",
        "Settings.RemoteControl.Error.NetworkFailure",
        "Settings.RemoteControl.Error.Unsupported",
        "Settings.ExternalNotification.OnlyCompleteNote",
        "Settings.ExternalNotification.Enabled",
        "Settings.ExternalNotification.TestSend",
        "Settings.ExternalNotification.SendWhenComplete",
        "Settings.ExternalNotification.EnableDetails",
        "Settings.ExternalNotification.SendWhenError",
        "Settings.ExternalNotification.SendWhenTimeout",
        "Settings.ExternalNotification.ProviderParameters",
        "Settings.ExternalNotification.ProviderParametersNote",
        "Settings.ExternalNotification.TestTitle",
        "Settings.ExternalNotification.TestMessage",
        "Settings.ExternalNotification.Status.ValidateSucceeded",
        "Settings.ExternalNotification.Status.TestSucceeded",
        "Settings.ExternalNotification.Status.SaveSucceeded",
        "Settings.ExternalNotification.Status.OperationFailed",
        "Settings.ExternalNotification.Status.PreparedUpdates",
        "Settings.ExternalNotification.Error.InvalidParameters",
        "Settings.ExternalNotification.Error.NetworkFailure",
        "Settings.ExternalNotification.Error.Unsupported",
        "Settings.ExternalNotification.Error.ParameterFormatLine",
        "Settings.ExternalNotification.Error.ParameterKeyEmpty",
        "Settings.ExternalNotification.Error.ParseFailed",
        "Settings.Background.Dialog.SelectImage",
        "Settings.Background.ImagePath",
        "Settings.Background.Opacity",
        "Settings.Background.BlurRadius",
        "Settings.Background.StretchMode",
        "Settings.Background.Note",
        "Settings.IssueReport.PreflightNote",
        "Settings.IssueReport.Faq",
        "Settings.IssueReport.IssueEntry",
        "Settings.IssueReport.OpenRuntimeLogWindow",
        "Settings.IssueReport.DeveloperMode",
        "Settings.IssueReport.DeveloperModeNote",
        "Settings.IssueReport.Build",
        "Settings.IssueReport.ClearImageCache",
        "Settings.IssueReport.OpenDebugDirectory",
        "Settings.IssueReport.OpenEntry",
        "Settings.IssueReport.OpenHelp",
        "Settings.Achievement.Backup",
        "Settings.Achievement.Restore",
        "Settings.Achievement.UnlockAll",
        "Settings.Achievement.LockAll",
        "Settings.Achievement.PopupDisabled",
        "Settings.Achievement.PopupAutoClose",
        "Settings.Achievement.Dialog",
        "Settings.Achievement.Dialog.FilterAll",
        "Settings.Achievement.Dialog.FilterUnlocked",
        "Settings.Achievement.Dialog.FilterInProgress",
        "Settings.Achievement.Dialog.FilterNew",
        "Settings.Achievement.Dialog.ResultsFormat",
        "Settings.Achievement.Dialog.ClearFilters",
        "Settings.Achievement.Dialog.EmptyTitle",
        "Settings.Achievement.Dialog.EmptyDescription",
        "Settings.Achievement.Dialog.OverviewFormat",
        "Settings.Achievement.Dialog.Snapshot",
        "Settings.Achievement.Initialize",
        "Settings.Achievement.OpenGuide",
        "Settings.Achievement.Refresh",
        "Settings.Achievement.Refresh.Snapshot",
        "Settings.Achievement.Save",
        "Settings.Achievement.Save.Refresh",
        "Settings.Achievement.StateChanged",
        "Settings.Achievement.Restore.Refresh",
        "Settings.Achievement.LockAll.Refresh",
        "Settings.Achievement.UnlockAll.Refresh",
        "Settings.About.Announcement.Dialog",
        "Settings.About.Announcement.Save",
        "Settings.About.CheckAnnouncement",
        "Settings.About.Dialog.DoNotRemindThisAnnouncementAgain",
        "Settings.About.Dialog.ReadProgress.PendingBadge",
        "Settings.About.Dialog.ReadProgress.PendingTitle",
        "Settings.About.Dialog.ReadProgress.PendingCaption",
        "Settings.About.Dialog.ReadProgress.ReadyBadge",
        "Settings.About.Dialog.ReadProgress.ReadyTitle",
        "Settings.About.Dialog.ReadProgress.ReadyCaption",
        "Settings.About.OpenCommunity",
        "Settings.About.OpenDownload",
        "Settings.About.OpenOfficialWebsite",
        "Settings.SaveScoped.Error.BatchEmpty",
        "Settings.SaveScoped.Error.ProfileMissing",
        "Settings.SaveScoped.Error.SettingKeyMissing",
        "Settings.SaveScoped.Error.SaveFailed",
        "Settings.SaveScoped.Status.BatchSavedSummary",
        "Settings.SaveScoped.Status.SavedCount",
        "SanityReport",
        "MissionStart.FightTask",
        "CurrentSanity",
    ];

    private static readonly string[] CopilotCriticalKeys =
    [
        "Copilot.Tab.Main",
        "Copilot.Input.PathOrCodeWatermark",
        "Copilot.Button.File",
        "Copilot.Button.Paste",
        "Copilot.Option.BattleList",
        "Copilot.Option.UseSanityPotion",
        "Copilot.Option.LoopTimes",
        "Copilot.Button.Clear",
    ];

    private static readonly string[] ToolboxCriticalKeys =
    [
        "Toolbox.Tab.Recruit",
        "Toolbox.Action.StartRecognition",
        "Toolbox.MiniGame.Name",
        "Toolbox.Status.WaitingForExecution",
        "Toolbox.Tip.RecruitRecognition",
    ];

    private static readonly string[] TaskQueueCriticalKeys =
    [
        "StartUp.Title",
        "StartUp.Option.ClientType.Official",
        "StartUp.Option.ConnectConfig.General",
        "StartUp.Option.ConnectConfig.PC",
        "StartUp.Option.ConnectConfig.GeneralWithoutScreencapErr",
        "StartUp.Option.TouchMode.MiniTouch",
        "StartUp.Option.AttachScreencap.FramePool",
        "StartUp.Option.AttachInput.Seize",
        "StartUp.Option.AttachInput.PostWithCursor",
        "StartUp.Option.AttachInput.PostWithWindowPos",
        "Fight.Title",
        "Fight.NotSwitch",
        "Fight.UseStoneDisplay",
        "Fight.StageSelect",
        "Fight.PerformBattles",
        "Fight.SeriesTip",
        "Fight.DrGrandetTip",
        "Fight.AssignedMaterial",
        "Fight.SpecifiedDropsTip",
        "Fight.Drop.NotSelected",
        "Fight.StageReset.Current",
        "Fight.StageReset.Ignore",
        "Fight.DefaultStage",
        "Fight.Annihilation.Current",
        "Fight.Annihilation.Chernobog",
        "Fight.Annihilation.LungmenOutskirts",
        "Fight.Annihilation.LungmenDowntown",
        "Fight.HideSeries",
        "Fight.AllowUseStoneSave",
        "Fight.AllowUseStoneSaveWarning",
        "Recruit.Title",
        "Recruit.Option.ExtraTags.Default",
        "Recruit.Option.ExtraTags.SelectAll",
        "Recruit.Option.ExtraTags.RareOnly",
        "Infrast.Title",
        "Infrast.Mode.Normal",
        "Infrast.Mode.Custom",
        "Infrast.Mode.Rotation",
        "Infrast.Drone.NotUse",
        "Infrast.Drone.Money",
        "Infrast.Plan.AutoCurrent",
        "Infrast.Plan.DefaultName",
        "Infrast.Default.UserDefined",
        "Infrast.Default.153Time3",
        "Infrast.Error.CustomFileNotFound",
        "Infrast.Error.PlanOutOfRange",
        "Infrast.Error.ParseFailed",
        "Infrast.Status.LoadedPlans",
        "Mall.Title",
        "Mall.VisitOnce",
        "Mall.OnlyOnceADay",
        "Mall.CreditFightOnce",
        "Mall.CreditFightTip",
        "Mall.Formation",
        "Mall.Formation.Current",
        "Mall.UseFormation",
        "Mall.Shopping",
        "Mall.BuyFirst",
        "Mall.Blacklist",
        "Mall.Drink",
        "Mall.ForceShopping",
        "Mall.OnlyDiscount",
        "Mall.OnlyDiscountTip",
        "Mall.Reserve",
        "Mall.ReserveTip",
        "Award.Title",
        "Award.RecruitTip",
        "Award.FreeGachaTip",
        "Award.GachaWarning",
        "Award.Confirm",
        "Award.Cancel",
        "Award.Orundum",
        "Award.Mining",
        "Award.Special",
        "PostAction.Title",
        "PostAction.BackToHome",
        "PostAction.ExitArknights",
        "PostAction.ExitEmulator",
        "PostAction.ExitSelf",
        "PostAction.IfNoOther",
        "PostAction.Once",
        "PostAction.OnceTip",
        "PostAction.Sleep",
        "PostAction.Hibernate",
        "PostAction.HibernateTip",
        "PostAction.Shutdown",
        "PostAction.Unsupported",
        "PostAction.Cmd.ExitArknights",
        "PostAction.Cmd.BackToHome",
        "PostAction.Cmd.ExitEmulator",
        "PostAction.Cmd.ExitSelf",
        "PostAction.Cmd.Watermark",
        "PostAction.Warn.IfNoOtherNeedsSystemAction",
        "PostAction.Warn.UnsupportedDowngrade",
        "Roguelike.Title",
        "Reclamation.Title",
        "Reclamation.Option.Mode.Archive",
        "Reclamation.Option.Mode.NoArchive",
        "Reclamation.Option.IncrementMode.Click",
        "Reclamation.Option.IncrementMode.Hold",
        "Reclamation.Option.Theme.FireClosed",
        "Reclamation.Option.Theme.Tales",
        "Reclamation.ToolToCraftTip",
        "Reclamation.ToolToCraftPlaceholder",
        "Reclamation.EarlyTip",
    ];

    private static readonly string[] RoguelikeNoFallbackKeys =
    [
        "Roguelike.CoreCharTip",
        "Roguelike.UseSupportTip",
        "Roguelike.ExpectedCollapsalParadigmsTip",
        "Roguelike.JieGardenDifficultyTip",
        "Roguelike.MultiTaskSharedTip",
        "Roguelike.StartFoldartalTip",
        "Roguelike.StartWithSelectList",
        "Roguelike.DelayAbortUntilCombatComplete",
        "RoguelikeStrategyExp",
        "RoguelikeStrategyGold",
        "RoguelikeStrategyLastReward",
        "RoguelikeStrategyMonthlySquad",
        "RoguelikeStrategyDeepExploration",
        "RoguelikeStrategyFindPlaytime",
        "RoguelikeStrategyCollapse",
        "RoguelikePlaytimeLing",
        "RoguelikePlaytimeShu",
        "RoguelikePlaytimeNian",
        "NotSwitch",
        "FirstMoveAdvantage",
        "SlowAndSteadyWinsTheRace",
        "OvercomingYourWeaknesses",
        "FlexibleDeployment",
        "Unbreakable",
        "AsYourHeartDesires",
    ];

    private static readonly Regex ChineseUiLiteralPattern = new(
        "(?<attr>Header|HeaderText|Content|Text|Watermark|ToolTip\\.Tip)\\s*=\\s*\"(?<value>[^\"]*[\\u4e00-\\u9fff][^\"]*)\"",
        RegexOptions.Compiled);

    [Fact]
    public void MainTabs_ShouldResolveLocalizedText_ForFiveLanguages()
    {
        var map = new RootLocalizationTextMap("Root.Localization.Tests");
        var languages = new[] { "zh-cn", "zh-tw", "en-us", "ja-jp", "ko-kr" };

        foreach (var language in languages)
        {
            map.Language = language;
            foreach (var key in MainTabKeys)
            {
                var value = map[key];
                Assert.False(string.IsNullOrWhiteSpace(value), $"Expected non-empty localized text for {language}:{key}.");
                Assert.NotEqual(key, value);
            }
        }
    }

    [Fact]
    public void MainTabs_ShouldNotFallbackToEnglish_ForJaKoZhTw()
    {
        var map = new RootLocalizationTextMap("Root.Localization.Tests");
        map.Language = "en-us";
        var enUsBaseline = MainTabKeys.ToDictionary(key => key, key => map[key], StringComparer.Ordinal);

        foreach (var language in new[] { "ja-jp", "ko-kr", "zh-tw" })
        {
            map.Language = language;
            foreach (var key in MainTabKeys)
            {
                var value = map[key];
                Assert.False(string.IsNullOrWhiteSpace(value));
                Assert.NotEqual(key, value);
                Assert.NotEqual(enUsBaseline[key], value);
            }
        }
    }

    [Fact]
    public void TargetViews_ShouldNotContainHardcodedChineseStaticLabels()
    {
        var root = GetMaaUnifiedRoot();
        var findings = new List<string>();
        foreach (var relative in TargetViewFiles)
        {
            var fullPath = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            findings.AddRange(FindHardcodedChineseLiterals(fullPath));
        }

        Assert.True(
            findings.Count == 0,
            "Detected hardcoded Chinese UI literals in target views:\n" + string.Join('\n', findings));
    }

    [Fact]
    public async Task SwitchLanguageToAsync_ShouldUpdateMainChainAndPageLocalizationStates()
    {
        await using var fixture = await RuntimeFixture.CreateAsync();
        var vm = new MainShellViewModel(fixture.Runtime);
        try
        {
            await vm.InitializeAsync();

            var beforeRootTab = vm.RootTexts["Main.Tab.Settings"];
            var beforeTaskQueueTitle = vm.TaskQueuePage.RootTexts["TaskQueue.Root.TaskListTitle"];

            await vm.SwitchLanguageToAsync("en-us");

            Assert.True(
                await WaitUntilAsync(
                    () =>
                        string.Equals(vm.CurrentShellLanguage, "en-us", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(vm.SettingsPage.Language, "en-us", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(vm.TaskQueuePage.Texts.Language, "en-us", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(vm.CopilotPage.RootTexts.Language, "en-us", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(vm.ToolboxPage.RootTexts.Language, "en-us", StringComparison.OrdinalIgnoreCase),
                    retry: 160,
                    delayMs: 25));

            var afterRootTab = vm.RootTexts["Main.Tab.Settings"];
            var afterTaskQueueTitle = vm.TaskQueuePage.RootTexts["TaskQueue.Root.TaskListTitle"];
            Assert.NotEqual(beforeRootTab, afterRootTab);
            Assert.NotEqual(beforeTaskQueueTitle, afterTaskQueueTitle);
        }
        finally
        {
            TestShellCleanup.StopTimerScheduler(vm);
        }
    }

    [Fact]
    public void RootCriticalKeys_ShouldNotFallbackToEnglish_ForJaKoZhTw()
    {
        var map = new RootLocalizationTextMap("Root.Localization.Tests");
        map.Language = "en-us";
        var enUsBaseline = RootCriticalKeys.ToDictionary(key => key, key => map[key], StringComparer.Ordinal);

        foreach (var language in new[] { "ja-jp", "ko-kr", "zh-tw" })
        {
            map.Language = language;
            foreach (var key in RootCriticalKeys)
            {
                var localized = map[key];
                Assert.False(string.IsNullOrWhiteSpace(localized));
                Assert.NotEqual(key, localized);
                if (RootTechnicalLiteralKeys.Contains(key))
                {
                    continue;
                }

                Assert.NotEqual(
                    enUsBaseline[key],
                    localized);
            }
        }
    }

    [Fact]
    public void VersionUpdateProxyAddressWatermark_ShouldMatchWpfAcrossSupportedLanguages()
    {
        var map = new RootLocalizationTextMap("Root.Localization.Tests");

        foreach (var language in new[] { "zh-cn", "zh-tw", "en-us", "ja-jp", "ko-kr" })
        {
            map.Language = language;
            Assert.Equal("<IP>:<Port>", map["Settings.VersionUpdate.ProxyAddressWatermark"]);
        }
    }

    [Fact]
    public void CopilotCriticalKeys_ShouldNotFallbackToEnglish_ForJaKo()
    {
        var map = new CopilotLocalizationTextMap();
        map.Language = "en-us";
        var enUsBaseline = CopilotCriticalKeys.ToDictionary(key => key, key => map[key], StringComparer.Ordinal);

        foreach (var language in new[] { "ja-jp", "ko-kr" })
        {
            map.Language = language;
            foreach (var key in CopilotCriticalKeys)
            {
                Assert.NotEqual(
                    enUsBaseline[key],
                    map[key]);
            }
        }
    }

    [Fact]
    public void ToolboxCriticalKeys_ShouldNotFallbackToEnglish_ForJaKo()
    {
        var map = new ToolboxLocalizationTextMap();
        map.Language = "en-us";
        var enUsBaseline = ToolboxCriticalKeys.ToDictionary(key => key, key => map[key], StringComparer.Ordinal);

        foreach (var language in new[] { "ja-jp", "ko-kr" })
        {
            map.Language = language;
            foreach (var key in ToolboxCriticalKeys)
            {
                Assert.NotEqual(
                    enUsBaseline[key],
                    map[key]);
            }
        }
    }

    [Fact]
    public void TaskQueueCriticalKeys_ShouldNotFallbackToEnglish_ForJaKoZhTw()
    {
        var map = new LocalizedTextMap();
        map.Language = "en-us";
        var enUsBaseline = TaskQueueCriticalKeys.ToDictionary(key => key, key => map[key], StringComparer.Ordinal);

        foreach (var language in new[] { "ja-jp", "ko-kr", "zh-tw" })
        {
            map.Language = language;
            foreach (var key in TaskQueueCriticalKeys)
            {
                Assert.NotEqual(
                    enUsBaseline[key],
                    map[key]);
            }
        }
    }

    [Fact]
    public void TaskQueueCoreTitles_ShouldMatchWpf_ForJaKoZhTw()
    {
        var map = new LocalizedTextMap();

        var expected = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["ja-jp"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["StartUp.Title"] = "ウェイクアップ",
                ["Fight.Title"] = "作戦",
                ["Recruit.Title"] = "公開求人",
                ["Mall.Title"] = "FP獲得と交換",
                ["Award.Title"] = "報酬受取",
                ["Roguelike.Title"] = "自動ローグ",
                ["Reclamation.Title"] = "生息演算",
            },
            ["ko-kr"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["StartUp.Title"] = "로그인",
                ["Fight.Title"] = "이성 사용",
                ["Recruit.Title"] = "공개모집",
                ["Mall.Title"] = "크레딧 수급 및 상점",
                ["Award.Title"] = "보상 수령",
                ["Roguelike.Title"] = "통합 전략",
                ["Reclamation.Title"] = "생존 연산",
            },
            ["zh-tw"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["StartUp.Title"] = "開始喚醒",
                ["Fight.Title"] = "刷理智",
                ["Recruit.Title"] = "自動公招",
                ["Mall.Title"] = "獲取信用及購物",
                ["Award.Title"] = "領取獎勵",
                ["Roguelike.Title"] = "自動肉鴿",
                ["Reclamation.Title"] = "生息演算",
            },
        };

        foreach (var language in expected.Keys)
        {
            map.Language = language;
            foreach (var pair in expected[language])
            {
                Assert.Equal(pair.Value, map[pair.Key]);
            }
        }
    }

    [Fact]
    public void RoguelikeKeys_ShouldNotFallbackToEnglishOrZhCn_ForJaKoZhTw()
    {
        var map = new LocalizedTextMap();

        map.Language = "en-us";
        var enUsBaseline = RoguelikeNoFallbackKeys.ToDictionary(key => key, key => map[key], StringComparer.Ordinal);

        map.Language = "zh-cn";
        var zhCnBaseline = RoguelikeNoFallbackKeys.ToDictionary(key => key, key => map[key], StringComparer.Ordinal);

        foreach (var language in new[] { "ja-jp", "ko-kr", "zh-tw" })
        {
            map.Language = language;
            foreach (var key in RoguelikeNoFallbackKeys)
            {
                var value = map[key];
                Assert.False(string.IsNullOrWhiteSpace(value), $"Expected non-empty text for {language}:{key}.");
                Assert.NotEqual(key, value);
                Assert.NotEqual(enUsBaseline[key], value);

                if (language is "ja-jp" or "ko-kr")
                {
                    Assert.NotEqual(zhCnBaseline[key], value);
                }
            }
        }
    }

    private static IEnumerable<string> FindHardcodedChineseLiterals(string path)
    {
        var xaml = File.ReadAllText(path);
        foreach (Match match in ChineseUiLiteralPattern.Matches(xaml))
        {
            var value = match.Groups["value"].Value;
            if (IsMarkupExpression(value))
            {
                continue;
            }

            var line = GetLineNumber(xaml, match.Index);
            var attr = match.Groups["attr"].Value;
            yield return $"{Path.GetFileName(path)}:{line} {attr}=\"{value}\"";
        }
    }

    private static bool IsMarkupExpression(string value)
    {
        return value.Contains("{Binding", StringComparison.Ordinal)
            || value.Contains("{DynamicResource", StringComparison.Ordinal)
            || value.Contains("{StaticResource", StringComparison.Ordinal)
            || value.Contains("{x:Static", StringComparison.Ordinal);
    }

    private static int GetLineNumber(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int retry = 80, int delayMs = 20)
    {
        for (var attempt = 0; attempt < retry; attempt++)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(delayMs);
        }

        return false;
    }

    private static string GetMaaUnifiedRoot()
    {
        return TestRepoLayout.GetMaaUnifiedRoot();
    }

    private sealed class RuntimeFixture : IAsyncDisposable
    {
        private readonly bool _cleanupRoot;

        private RuntimeFixture(string root, MAAUnifiedRuntime runtime, bool cleanupRoot)
        {
            Root = root;
            Runtime = runtime;
            _cleanupRoot = cleanupRoot;
        }

        public string Root { get; }

        public MAAUnifiedRuntime Runtime { get; }

        public static async Task<RuntimeFixture> CreateAsync(string? root = null, bool cleanupRoot = true)
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

            var bridge = new MaaCoreBridgeStub();
            var session = new UnifiedSessionService(bridge, config, log, new SessionStateMachine());
            var trayService = new CapturingTrayService();
            var platform = new PlatformServiceBundle
            {
                TrayService = trayService,
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
                RemoteControlFeatureService = new RemoteControlFeatureService(),
                PlatformCapabilityService = capability,
                OverlayFeatureService = new OverlayFeatureService(capability),
                NotificationProviderFeatureService = new NotificationProviderFeatureService(),
                SettingsFeatureService = new SettingsFeatureService(config, capability, diagnostics),
                DialogFeatureService = new DialogFeatureService(diagnostics),
                PostActionFeatureService = new PostActionFeatureService(
                    config,
                    diagnostics,
                    platform.PostActionExecutorService),
            };

            return new RuntimeFixture(root, runtime, cleanupRoot);
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

    private sealed class CapturingTrayService : ITrayService
    {
        public PlatformCapabilityStatus Capability { get; } = new(
            Supported: true,
            Message: "tray test service",
            Provider: "test");

        public event EventHandler<TrayCommandEvent>? CommandInvoked;

        public Task<PlatformOperationResult> InitializeAsync(
            string appTitle,
            TrayMenuText? menuText,
            CancellationToken cancellationToken = default)
            => Task.FromResult(PlatformOperation.NativeSuccess(Capability.Provider, "initialized", "tray.initialize"));

        public Task<PlatformOperationResult> ShutdownAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(PlatformOperation.NativeSuccess(Capability.Provider, "shutdown", "tray.shutdown"));

        public Task<PlatformOperationResult> ShowAsync(string title, string message, CancellationToken cancellationToken = default)
            => Task.FromResult(PlatformOperation.NativeSuccess(Capability.Provider, "show", "tray.show"));

        public Task<PlatformOperationResult> SetMenuStateAsync(TrayMenuState state, CancellationToken cancellationToken = default)
            => Task.FromResult(PlatformOperation.NativeSuccess(Capability.Provider, "set-menu", "tray.setMenuState"));

        public Task<PlatformOperationResult> SetVisibleAsync(bool visible, CancellationToken cancellationToken = default)
            => Task.FromResult(PlatformOperation.NativeSuccess(Capability.Provider, "set-visible", "tray.setVisible"));
    }
}
