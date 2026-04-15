using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using MAAUnified.App.Features.Dialogs;
using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.Application.Models;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;

namespace MAAUnified.Tests;

public sealed class DialogModuleP1FeatureTests
{
    [Fact]
    public void DialogContracts_ShouldCoverAllEightDialogTypes()
    {
        var values = Enum.GetValues<DialogType>();
        var expected = new[]
        {
            DialogType.Announcement,
            DialogType.VersionUpdate,
            DialogType.ProcessPicker,
            DialogType.EmulatorPath,
            DialogType.Error,
            DialogType.AchievementList,
            DialogType.Text,
            DialogType.WarningConfirm,
        };

        Assert.Equal(expected.Length, values.Length);
        Assert.Equal(expected, values);
    }

    [Fact]
    public void DialogReturnSemantic_ShouldContainConfirmCancelClose()
    {
        var values = Enum.GetValues<DialogReturnSemantic>();
        var expected = new[]
        {
            DialogReturnSemantic.Confirm,
            DialogReturnSemantic.Cancel,
            DialogReturnSemantic.Close,
        };

        Assert.Equal(expected.Length, values.Length);
        Assert.Equal(expected, values);
    }

    [Fact]
    public void ErrorDialogRequest_ShouldCarryUiOperationResult()
    {
        var result = UiOperationResult.Fail(
            UiErrorCode.PlatformOperationFailed,
            "Synthetic failure for dialog payload verification.",
            "details");
        var request = new ErrorDialogRequest(
            Title: "Error",
            Context: "Dialog.Test",
            Result: result,
            Suggestion: "Try again.");

        Assert.Equal("Dialog.Test", request.Context);
        Assert.Equal(UiErrorCode.PlatformOperationFailed, request.Result.Error?.Code);
        Assert.Equal("details", request.Result.Error?.Details);
        Assert.Equal("Try again.", request.Suggestion);
        Assert.Equal("en-us", request.Language);
    }

    [Fact]
    public void DialogTextCatalog_ShouldUseLocalizedTextsForSupportedLanguages()
    {
        Assert.Equal("警告", DialogTextCatalog.WarningDialogTitle("ja-jp"));
        Assert.Equal("끄기", DialogTextCatalog.ErrorDialogCloseButton("ko-kr"));
    }

    [Fact]
    public void DialogTextCatalog_ShouldProvideEditableConfigHints_ForProfileNameErrors()
    {
        var result = UiOperationResult.Fail(
            UiErrorCode.ConfigurationProfileInvalidName,
            "Profile name cannot be empty.");

        var localized = DialogTextCatalog.LocalizeErrorResult("zh-cn", result);

        Assert.Equal("配置名称不能为空。", localized.Message);
        Assert.Equal("请输入配置名称后再试。", DialogTextCatalog.BuildErrorSuggestion("zh-cn", result));
        Assert.Contains("原始消息", localized.Error?.Details ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DialogFeatureService_BeginActionComplete_ShouldWriteTraceEvents()
    {
        await using var fixture = DialogFeatureFixture.Create();

        var token = await fixture.Service.BeginDialogAsync(
            DialogType.Text,
            "Dialog.P1.Trace",
            "Trace Title");
        await fixture.Service.RecordDialogActionAsync(token, "return", DialogReturnSemantic.Confirm.ToString());
        await fixture.Service.CompleteDialogAsync(token, DialogReturnSemantic.Confirm, "done");

        var eventLog = await File.ReadAllTextAsync(fixture.Diagnostics.EventLogPath);
        Assert.Contains("[EVENT] [Dialog.Open]", eventLog, StringComparison.Ordinal);
        Assert.Contains($"trace={token.TraceId}; dialog={DialogType.Text}; source=Dialog.P1.Trace; title=Trace Title", eventLog, StringComparison.Ordinal);
        Assert.Contains("[EVENT] [Dialog.Action]", eventLog, StringComparison.Ordinal);
        Assert.Contains("action=return; detail=Confirm", eventLog, StringComparison.Ordinal);
        Assert.Contains("[EVENT] [Dialog.Close]", eventLog, StringComparison.Ordinal);
        Assert.Contains("return=Confirm; summary=done", eventLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DialogFeatureService_ReportError_ShouldRaiseErrorEvent_AndWriteFailedResult()
    {
        await using var fixture = DialogFeatureFixture.Create();
        DialogErrorRaisedEvent? raised = null;
        fixture.Service.ErrorRaised += (_, e) => raised = e;

        var result = UiOperationResult.Fail(
            UiErrorCode.TaskLoadFailed,
            "Synthetic dialog failure.");
        await fixture.Service.ReportErrorAsync("Dialog.P1.ErrorReport", result);

        Assert.NotNull(raised);
        Assert.Equal("Dialog.P1.ErrorReport", raised!.Context);
        Assert.Equal(UiErrorCode.TaskLoadFailed, raised.Result.Error?.Code);
        Assert.Equal("Synthetic dialog failure.", raised.Result.Message);

        var errorLog = await File.ReadAllTextAsync(fixture.Diagnostics.ErrorLogPath);
        Assert.Contains("[FAILED] [Dialog.P1.ErrorReport]", errorLog, StringComparison.Ordinal);
        Assert.Contains($"code={UiErrorCode.TaskLoadFailed}", errorLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoOpAppDialogService_AllDialogs_ShouldReturnCloseSemantic()
    {
        var service = NoOpAppDialogService.Instance;

        var announcement = await service.ShowAnnouncementAsync(
            new AnnouncementDialogRequest("Announcement", "Info", false, false),
            "Dialog.P1.NoOp.Announcement");
        var versionUpdate = await service.ShowVersionUpdateAsync(
            new VersionUpdateDialogRequest("VersionUpdate", "1.0.0", "1.0.1", "Summary", "Body"),
            "Dialog.P1.NoOp.VersionUpdate");
        var processPicker = await service.ShowProcessPickerAsync(
            new ProcessPickerDialogRequest(
                "ProcessPicker",
                [new ProcessPickerItem("process-1", "Process 1", IsPrimary: true)],
                "process-1"),
            "Dialog.P1.NoOp.ProcessPicker");
        var emulatorPath = await service.ShowEmulatorPathAsync(
            new EmulatorPathDialogRequest("EmulatorPath", ["/tmp/emulator"], "/tmp/emulator"),
            "Dialog.P1.NoOp.EmulatorPath");
        var error = await service.ShowErrorAsync(
            new ErrorDialogRequest(
                "Error",
                "Dialog.P1.NoOp.Error",
                UiOperationResult.Fail(UiErrorCode.UiError, "no-op-error")),
            "Dialog.P1.NoOp.Error");
        var achievementList = await service.ShowAchievementListAsync(
            new AchievementListDialogRequest(
                "Achievement",
                [new AchievementListItem("a1", "Title", "Description", "Status")],
                InitialFilter: string.Empty),
            "Dialog.P1.NoOp.Achievement");
        var text = await service.ShowTextAsync(
            new TextDialogRequest("Text", "Prompt", "Default"),
            "Dialog.P1.NoOp.Text");
        var warningConfirm = await service.ShowWarningConfirmAsync(
            new WarningConfirmDialogRequest("Warning", "Prompt"),
            "Dialog.P1.NoOp.WarningConfirm");

        AssertAllCloseSemantics(
            announcement.Return,
            announcement.Payload,
            announcement.Summary);
        AssertAllCloseSemantics(
            versionUpdate.Return,
            versionUpdate.Payload,
            versionUpdate.Summary);
        AssertAllCloseSemantics(
            processPicker.Return,
            processPicker.Payload,
            processPicker.Summary);
        AssertAllCloseSemantics(
            emulatorPath.Return,
            emulatorPath.Payload,
            emulatorPath.Summary);
        AssertAllCloseSemantics(
            error.Return,
            error.Payload,
            error.Summary);
        AssertAllCloseSemantics(
            achievementList.Return,
            achievementList.Payload,
            achievementList.Summary);
        AssertAllCloseSemantics(
            text.Return,
            text.Payload,
            text.Summary);
        AssertAllCloseSemantics(
            warningConfirm.Return,
            warningConfirm.Payload,
            warningConfirm.Summary);
    }

    [Fact]
    public void AchievementListRequestSnapshots_ShouldKeepExistingItemsAndPayload_WhenLanguageChanges()
    {
        var firstRequest = CreateAchievementListRequest("zh-cn");
        var firstPayload = new AchievementListDialogPayload(firstRequest.InitialFilter ?? string.Empty, ["achievement-1"]);

        var englishChrome = Assert.IsType<DialogChromeCatalog>(firstRequest.Chrome).GetSnapshot("en-us");
        var secondRequest = CreateAchievementListRequest("en-us");

        var firstItem = Assert.Single(firstRequest.Items);
        var secondItem = Assert.Single(secondRequest.Items);

        Assert.Equal("成就列表", firstRequest.Title);
        Assert.Equal("搜索成就", firstRequest.FilterWatermark);
        Assert.Equal("关闭", firstRequest.ConfirmText);
        Assert.Equal("取消", firstRequest.CancelText);
        Assert.Equal(1, firstRequest.UnlockedCount);
        Assert.Equal(3, firstRequest.TotalCount);
        Assert.Equal("首次会面", firstItem.Title);
        Assert.Equal("旧语言描述", firstItem.Description);
        Assert.Equal("已解锁", firstItem.Status);
        Assert.Equal("首次", firstPayload.FilterText);
        Assert.Equal(["achievement-1"], firstPayload.SelectedIds);

        Assert.Equal("Achievement List", englishChrome.Title);
        Assert.Equal("Close", englishChrome.ConfirmText);
        Assert.Equal("Cancel", englishChrome.CancelText);
        Assert.Equal(
            "Filter achievements",
            englishChrome.GetNamedTextOrDefault(DialogTextCatalog.ChromeKeys.FilterWatermark));

        Assert.Equal("成就列表", firstRequest.Title);
        Assert.Equal("搜索成就", firstRequest.FilterWatermark);
        Assert.Equal("首次会面", firstItem.Title);
        Assert.Equal("首次", firstPayload.FilterText);

        Assert.Equal("Achievement List", secondRequest.Title);
        Assert.Equal("Filter achievements", secondRequest.FilterWatermark);
        Assert.Equal("Close", secondRequest.ConfirmText);
        Assert.Equal("Cancel", secondRequest.CancelText);
        Assert.Equal(1, secondRequest.UnlockedCount);
        Assert.Equal(3, secondRequest.TotalCount);
        Assert.Equal("First encounter", secondItem.Title);
        Assert.Equal("Legacy-language description", secondItem.Description);
        Assert.Equal("Unlocked", secondItem.Status);
    }

    [Fact]
    public void AchievementListDialogPresenter_ShouldFilterByQuickFilterAndSearch()
    {
        var presenter = new AchievementListDialogPresenter();
        presenter.ApplyRequest(
            new AchievementListDialogRequest(
                Title: "Achievement",
                Items:
                [
                    new AchievementListItem(
                        "unlocked-new",
                        "First contact",
                        "Unlocked description",
                        "Unlocked",
                        IsUnlocked: true,
                        IsNewUnlock: true),
                    new AchievementListItem(
                        "progress",
                        "Planner",
                        "Track progress",
                        "In progress",
                        Conditions: "Reach 5",
                        IsProgressive: true,
                        ShowProgress: true,
                        Progress: 3,
                        Target: 5),
                    new AchievementListItem(
                        "other",
                        "Explorer",
                        "General description",
                        "General")
                ],
                InitialFilter: string.Empty,
                UnlockedCount: 1,
                TotalCount: 3));

        var initialState = presenter.BuildState(
            "NEW",
            "Progress: {0}",
            "Unlocked {0} / {1} · {2}% complete",
            "Showing {0} achievements");
        Assert.Equal(3, initialState.Items.Count);
        Assert.Equal("Unlocked 1 / 3 · 33% complete", initialState.OverviewText);

        presenter.SetFilter(AchievementQuickFilter.InProgress);
        var progressState = presenter.BuildState(
            "NEW",
            "Progress: {0}",
            "Unlocked {0} / {1} · {2}% complete",
            "Showing {0} achievements");
        var progressItem = Assert.Single(progressState.Items);
        Assert.Equal("progress", progressItem.Id);
        Assert.Equal("Progress: 3 / 5", progressItem.ProgressText);

        presenter.SetFilter(AchievementQuickFilter.NewUnlock);
        presenter.SetSearchText("contact");
        var combinedState = presenter.BuildState(
            "NEW",
            "Progress: {0}",
            "Unlocked {0} / {1} · {2}% complete",
            "Showing {0} achievements");
        var combinedItem = Assert.Single(combinedState.Items);
        Assert.Equal("unlocked-new", combinedItem.Id);
        Assert.True(combinedState.HasActiveFilters);
    }

    [Fact]
    public void AchievementListDialogView_BuildPayload_ShouldReturnFilterTextAndEmptySelection()
    {
        var view = (AchievementListDialogView)RuntimeHelpers.GetUninitializedObject(typeof(AchievementListDialogView));
        SetAchievementListDialogField(view, "FilterInput", new TextBox { Text = "keyword" });

        var payload = view.BuildPayload();

        Assert.Equal("keyword", payload.FilterText);
        Assert.Empty(payload.SelectedIds);
    }

    [Fact]
    public async Task ProcessPickerDialogView_Refresh_ShouldCallProviderAndReplaceItems()
    {
        var view = CreateProcessPickerDialogViewForRefreshTest();
        ProcessPickerItem[] initialItems =
        [
            new ProcessPickerItem("first", "First", IsPrimary: true),
            new ProcessPickerItem("keep", "Keep", IsPrimary: false),
        ];
        ProcessPickerItem[] refreshedItems =
        [
            new ProcessPickerItem("keep", "Keep Updated", IsPrimary: true),
            new ProcessPickerItem("third", "Third", IsPrimary: false),
        ];
        var refreshTriggered = false;
        var refreshCompleted = new TaskCompletionSource<bool>();

        var request = new ProcessPickerDialogRequest(
            Title: "ProcessPicker",
            Items: initialItems,
            SelectedId: "keep",
            RefreshItemsAsync: async _ =>
            {
                refreshTriggered = true;
                refreshCompleted.SetResult(true);
                await Task.Delay(1);
                return refreshedItems;
            });

        SetProcessPickerDialogField(view, "_request", request);
        InvokeProcessPickerDialogMethod(view, "ApplyItems", initialItems, request.SelectedId);
        InvokeProcessPickerDialogMethod(
            view,
            "OnRefreshClick",
            view.RefreshButton,
            new RoutedEventArgs(Button.ClickEvent, view.RefreshButton));
        await refreshCompleted.Task;
        await Task.Delay(20);

        var selected = view.ProcessList.SelectedItem as ProcessPickerItem;
        Assert.True(refreshTriggered);
        Assert.NotNull(selected);
        Assert.Equal("keep", selected!.Id);
        Assert.Equal("Keep Updated", selected.DisplayName);
        Assert.Equal(2, view.ProcessList.Items.Cast<object>().Count());
        Assert.Equal(
            ["keep", "third"],
            view.ProcessList.Items.Cast<ProcessPickerItem>().Select(static item => item.Id).ToArray());
    }

    private static AchievementListDialogRequest CreateAchievementListRequest(string language)
    {
        var chrome = DialogTextCatalog.CreateCatalog(
            language,
            currentLanguage => new DialogChromeSnapshot(
                title: DialogTextCatalog.Select(currentLanguage, "成就列表", "Achievement List"),
                confirmText: DialogTextCatalog.ErrorDialogCloseButton(currentLanguage),
                cancelText: DialogTextCatalog.WarningDialogCancelButton(currentLanguage),
                namedTexts: DialogTextCatalog.CreateNamedTexts(
                    (
                        DialogTextCatalog.ChromeKeys.FilterWatermark,
                        DialogTextCatalog.Select(currentLanguage, "搜索成就", "Filter achievements")))));
        var snapshot = chrome.GetSnapshot();
        return new AchievementListDialogRequest(
            Title: snapshot.Title,
            Items:
            [
                new AchievementListItem(
                    "achievement-1",
                    DialogTextCatalog.Select(language, "首次会面", "First encounter"),
                    DialogTextCatalog.Select(language, "旧语言描述", "Legacy-language description"),
                    DialogTextCatalog.Select(language, "已解锁", "Unlocked"))
            ],
            InitialFilter: DialogTextCatalog.Select(language, "首次", "first"),
            ConfirmText: snapshot.ConfirmText ?? DialogTextCatalog.ErrorDialogCloseButton(language),
            CancelText: snapshot.CancelText ?? DialogTextCatalog.WarningDialogCancelButton(language),
            FilterWatermark: snapshot.GetNamedTextOrDefault(
                DialogTextCatalog.ChromeKeys.FilterWatermark,
                DialogTextCatalog.Select(language, "搜索成就", "Filter achievements")),
            UnlockedCount: 1,
            TotalCount: 3,
            Chrome: chrome);
    }

    private static void SetAchievementListDialogField(AchievementListDialogView view, string fieldName, object? value)
    {
        var field = typeof(AchievementListDialogView).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(view, value);
    }

    private static void AssertAllCloseSemantics<TPayload>(
        DialogReturnSemantic semantic,
        TPayload? payload,
        string summary)
    {
        Assert.Equal(DialogReturnSemantic.Close, semantic);
        Assert.Null(payload);
        Assert.Equal("dialog-service-unavailable", summary);
    }

    private static ProcessPickerDialogView CreateProcessPickerDialogViewForRefreshTest()
    {
        var view = (ProcessPickerDialogView)RuntimeHelpers.GetUninitializedObject(typeof(ProcessPickerDialogView));
        var items = new System.Collections.ObjectModel.ObservableCollection<ProcessPickerItem>();
        var processList = new ListBox
        {
            ItemsSource = items,
        };

        SetProcessPickerDialogField(view, "_items", items);
        SetProcessPickerDialogField(view, "ProcessList", processList);
        SetProcessPickerDialogField(view, "RefreshButton", new Button());
        SetProcessPickerDialogField(view, "CancelButton", new Button());
        SetProcessPickerDialogField(view, "ConfirmButton", new Button());
        return view;
    }

    private static void SetProcessPickerDialogField(ProcessPickerDialogView view, string fieldName, object? value)
    {
        var field = typeof(ProcessPickerDialogView).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(view, value);
    }

    private static void InvokeProcessPickerDialogMethod(ProcessPickerDialogView view, string methodName, params object?[] args)
    {
        var method = typeof(ProcessPickerDialogView).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(view, args);
    }

    private sealed class DialogFeatureFixture : IAsyncDisposable
    {
        private DialogFeatureFixture(
            string root,
            UiDiagnosticsService diagnostics,
            DialogFeatureService service)
        {
            Root = root;
            Diagnostics = diagnostics;
            Service = service;
        }

        public string Root { get; }

        public UiDiagnosticsService Diagnostics { get; }

        public DialogFeatureService Service { get; }

        public static DialogFeatureFixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "maa-unified-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var log = new UiLogService();
            var diagnostics = new UiDiagnosticsService(root, log);
            var service = new DialogFeatureService(diagnostics);
            return new DialogFeatureFixture(root, diagnostics, service);
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // ignore temp cleanup failures
            }

            return ValueTask.CompletedTask;
        }
    }
}
