using Avalonia.Controls;
using Avalonia.Interactivity;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using MAAUnified.App.Controls;
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
            DialogReturnSemantic.Details,
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
    public void TextDialogRequest_ShouldSupportReadOnlyContentMode()
    {
        var editable = new TextDialogRequest(
            Title: "Editable",
            Prompt: "Prompt",
            DefaultText: "Default");
        var readOnly = editable with
        {
            MultiLine = true,
            ReadOnlyContent = true,
        };

        Assert.False(editable.ReadOnlyContent);
        Assert.False(editable.MultiLine);
        Assert.True(readOnly.ReadOnlyContent);
        Assert.True(readOnly.MultiLine);
        Assert.Equal("Default", readOnly.DefaultText);
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
    public void DialogTextCatalog_ShouldProvideFriendlyConnectFailedMessage()
    {
        var result = UiOperationResult.Fail(
            UiErrorCode.ConnectFailed,
            "Connection command failed to exec",
            "{\"adb\":\"/usr/bin/adb\",\"address\":\"192.168.1.105:16384\"}");

        var localized = DialogTextCatalog.LocalizeErrorResult("zh-cn", result);

        Assert.Equal("连接模拟器失败。", localized.Message);
        Assert.Equal("连接模拟器失败。", localized.Error?.Message);
        Assert.Equal("连接模拟器失败", DialogTextCatalog.ErrorDialogConnectFailedTitle("zh-cn"));
        Assert.Equal("详细报错", DialogTextCatalog.ErrorDialogCopyErrorInfoButton("zh-cn"));
        Assert.Contains("原始消息", localized.Error?.Details ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("ADB", DialogTextCatalog.BuildErrorSuggestion("zh-cn", result), StringComparison.Ordinal);
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
    public void AnnouncementDialogView_ShouldHideCloseButton_AndGateEscapeUntilRead()
    {
        var root = BaselineTestSupport.GetMaaUnifiedRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "App", "Features", "Dialogs", "AnnouncementDialogView.axaml"));
        var code = File.ReadAllText(Path.Combine(root, "App", "Features", "Dialogs", "AnnouncementDialogView.axaml.cs"));

        Assert.Contains("ShowCloseButton=\"False\"", xaml, StringComparison.Ordinal);
        Assert.Contains("if (!_hasEverScrolledToBottom)", code, StringComparison.Ordinal);
        Assert.Contains("TryCompleteDialog(DialogReturnSemantic.Close);", code, StringComparison.Ordinal);
    }

    [Fact]
    public void AnnouncementDialogView_ShouldUseUnifiedSectionActivationMath()
    {
        const double activationLineY = 36d;
        const double targetHeaderTop = 240d;
        var targetOffset = AnnouncementDialogView.ComputeSectionTargetOffset(targetHeaderTop, activationLineY);

        Assert.Equal(204d, targetOffset);
        Assert.Equal(
            1,
            AnnouncementDialogView.ResolveActiveSectionIndex(
                targetOffset,
                activationLineY,
                new[] { 120d, targetHeaderTop, 420d }));
        Assert.Equal(
            -1,
            AnnouncementDialogView.ResolveActiveSectionIndex(
                20d,
                activationLineY,
                new[] { 120d, targetHeaderTop, 420d }));
    }

    [Theory]
    [InlineData(120d, 60d, 0d)]
    [InlineData(60d, 60d, 0d)]
    [InlineData(30d, 60d, 30d)]
    [InlineData(-40d, 60d, 60d)]
    public void AnnouncementDialogView_ShouldClampStickyPushOffset(double nextViewportTop, double stickyHeight, double expected)
    {
        Assert.Equal(expected, AnnouncementDialogView.ComputeStickyPushOffset(nextViewportTop, stickyHeight));
    }

    [Fact]
    public void AnnouncementDialogView_ShouldUseAppWindowFrameAndRailSelectionListStyles()
    {
        var root = BaselineTestSupport.GetMaaUnifiedRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "App", "Features", "Dialogs", "AnnouncementDialogView.axaml"));
        var code = File.ReadAllText(Path.Combine(root, "App", "Features", "Dialogs", "AnnouncementDialogView.axaml.cs"));
        var foundationStyles = File.ReadAllText(Path.Combine(root, "App", "Styles", "AppFoundationStyles.axaml"));
        var selectionListStyles = File.ReadAllText(Path.Combine(root, "App", "Styles", "AppSelectionListStyles.axaml"));
        var controlStyles = File.ReadAllText(Path.Combine(root, "App", "Styles", "ControlStyles.axaml"));

        Assert.Contains("<controls:AppWindowFrame", xaml, StringComparison.Ordinal);
        Assert.Contains("Mode=\"ResizableDialog\"", xaml, StringComparison.Ordinal);
        Assert.Contains("VisualMode=\"Rail\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ReserveTrailingAccessorySpace=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("app-selection-list-item-shell", xaml, StringComparison.Ordinal);
        Assert.Contains("announcement-dialog-sticky-title-viewport", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("announcement-dialog-section-list", xaml + controlStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("announcement-dialog-list-selection-surface", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("announcement-dialog-read-progress", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("announcement-dialog-action-lock-scrim", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("SectionListFollowAccent", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AllAnnouncementsKey", code, StringComparison.Ordinal);
        Assert.DoesNotContain("AnnouncementTitleText", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateSectionListFollowAccent", code, StringComparison.Ordinal);
        Assert.DoesNotContain("OnResizeGripPointerPressed", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Math.Max(0d, point.Value.Y", code, StringComparison.Ordinal);
        Assert.DoesNotContain("modern-dialog-title", code, StringComparison.Ordinal);
        Assert.Contains("app-window-title", code, StringComparison.Ordinal);
        Assert.Contains("_primarySectionHeader ??= header", code, StringComparison.Ordinal);
        Assert.Contains("CreateSectionHeader(item.Title)", code, StringComparison.Ordinal);
        Assert.Contains("CreateMarkdownViewer(item.MarkdownContent)", code, StringComparison.Ordinal);
        Assert.Contains("controls|AppSelectionList.selection-list-rail Border.app-selection-list-item-shell", selectionListStyles, StringComparison.Ordinal);
        Assert.Contains("selection-list-rail.selection-list-rail-trailing-accessory-space", selectionListStyles, StringComparison.Ordinal);
        Assert.Contains("controls|AppSelectionIndicatorPresenter", selectionListStyles, StringComparison.Ordinal);
        Assert.Contains("controls|AppSelectionIndicatorPresenter.selection-indicator-rail.indicator-vertical /template/ Border#PART_IndicatorGlow", selectionListStyles, StringComparison.Ordinal);
        Assert.Contains("controls|AppSelectionIndicatorPresenter.selection-indicator-rail.indicator-vertical /template/ Border#PART_Indicator", selectionListStyles, StringComparison.Ordinal);
        Assert.Contains("<x:Double x:Key=\"MAA.App.SelectionIndicator.RailMaxLength\">22</x:Double>", selectionListStyles, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Width\" Value=\"3\" />", selectionListStyles, StringComparison.Ordinal);
        Assert.Contains("<ThicknessTransition Property=\"Margin\"", selectionListStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("selection-list-rail ListBoxItem:selected Border.app-selection-list-item-shell", selectionListStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("selection-list-rail ListBoxItem:pointerover Border.app-selection-list-item-shell", selectionListStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("ListBox.announcement-dialog-list", controlStyles + selectionListStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("announcement-dialog-list-follow-accent", controlStyles + selectionListStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("Border.app-selection-list-follow-accent", selectionListStyles, StringComparison.Ordinal);
        Assert.Contains("Border.app-surface.app-section", foundationStyles, StringComparison.Ordinal);
        Assert.Contains("TextBlock.app-window-title", foundationStyles, StringComparison.Ordinal);
        Assert.Contains("ComputeSectionTargetOffset", code, StringComparison.Ordinal);
        Assert.Contains("ComputeStickyPushOffset", code, StringComparison.Ordinal);
    }

    [Fact]
    public void AchievementListDialogView_ShouldUseAppWindowFrameAndNoneSelectionListStyles()
    {
        var root = BaselineTestSupport.GetMaaUnifiedRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "App", "Features", "Dialogs", "AchievementListDialogView.axaml"));
        var code = File.ReadAllText(Path.Combine(root, "App", "Features", "Dialogs", "AchievementListDialogView.axaml.cs"));
        var foundationStyles = File.ReadAllText(Path.Combine(root, "App", "Styles", "AppFoundationStyles.axaml"));
        var selectionListStyles = File.ReadAllText(Path.Combine(root, "App", "Styles", "AppSelectionListStyles.axaml"));
        var controlStyles = File.ReadAllText(Path.Combine(root, "App", "Styles", "ControlStyles.axaml"));
        var sharedSliderAnimateStyleStart = foundationStyles.IndexOf("  <Style Selector=\"Border.app-slider-selection.animate\">", StringComparison.Ordinal);
        var sharedSliderAnimateStyleEnd = foundationStyles.IndexOf("  <Style Selector=\"Button.app-button.app-slider-chip:pointerover\">", sharedSliderAnimateStyleStart, StringComparison.Ordinal);
        var sharedSliderAnimateStyle = foundationStyles.Substring(sharedSliderAnimateStyleStart, sharedSliderAnimateStyleEnd - sharedSliderAnimateStyleStart);

        Assert.Contains("<controls:AppWindowFrame", xaml, StringComparison.Ordinal);
        Assert.Contains("Mode=\"ResizableDialog\"", xaml, StringComparison.Ordinal);
        Assert.Contains("OwnerHeightCapMargin=\"24\"", xaml, StringComparison.Ordinal);
        Assert.Contains("VisualMode=\"None\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"FilterStripTrack\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"FilterSelectionSlider\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Classes=\"app-slider-strip achievement-dialog-filter-strip\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Classes=\"app-slider-selection\"", xaml, StringComparison.Ordinal);
        Assert.Contains("app-button app-slider-chip achievement-dialog-filter-chip", xaml, StringComparison.Ordinal);
        Assert.Contains("app-input app-search-input", xaml, StringComparison.Ordinal);
        Assert.Contains("app-selection-list-item-shell achievement-dialog-item-card", xaml, StringComparison.Ordinal);
        Assert.Contains("app-body achievement-dialog-card-description", xaml, StringComparison.Ordinal);
        Assert.Contains("app-surface app-card achievement-dialog-conditions-panel", xaml, StringComparison.Ordinal);
        Assert.Contains("app-caption achievement-dialog-unlocked-at", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("achievement-dialog-list", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("OnResizeGripPointerPressed", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyOwnerHeightThreshold", code, StringComparison.Ordinal);
        Assert.Contains("FilterStripTrack.SizeChanged += OnFilterSliderLayoutMetricChanged;", code, StringComparison.Ordinal);
        Assert.Contains("FilterAllButton.SizeChanged += OnFilterSliderLayoutMetricChanged;", code, StringComparison.Ordinal);
        Assert.Contains("new AppSlidingSegmentController(FilterStripTrack, FilterSelectionSlider, GetActiveFilterButton)", code, StringComparison.Ordinal);
        Assert.Contains("_filterSlider.QueueSync(animateFilterSlider);", code, StringComparison.Ordinal);
        Assert.Contains("_filterSlider.QueueSync(resetMetrics: false);", code, StringComparison.Ordinal);
        Assert.Contains("RefreshView(animateFilterSlider: true);", code, StringComparison.Ordinal);
        Assert.Contains("controls|AppSelectionList.selection-list-none Border.app-selection-list-item-shell", selectionListStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("ListBox.achievement-dialog-list", controlStyles + selectionListStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("achievement-dialog-filter-selection-slider", controlStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("Button.app-button.app-slider-chip.achievement-dialog-filter-chip", controlStyles, StringComparison.Ordinal);
        Assert.Contains("Border.app-slider-strip", foundationStyles, StringComparison.Ordinal);
        Assert.Contains("Border.app-slider-selection", foundationStyles, StringComparison.Ordinal);
        Assert.Contains("Border.app-slider-selection.animate", foundationStyles, StringComparison.Ordinal);
        Assert.Contains("<DoubleTransition Property=\"Width\"", sharedSliderAnimateStyle, StringComparison.Ordinal);
        Assert.DoesNotContain("<TransformOperationsTransition Property=\"RenderTransform\"", sharedSliderAnimateStyle, StringComparison.Ordinal);
        Assert.Contains("Border.app-surface.app-card", foundationStyles, StringComparison.Ordinal);
        Assert.Contains("Button.app-button.app-chip", foundationStyles, StringComparison.Ordinal);
        Assert.Contains("TextBox.app-input.app-search-input", foundationStyles, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ProcessPickerDialogView.axaml", "CompactModal")]
    [InlineData("EmulatorPathSelectionDialogView.axaml", "CompactModal")]
    [InlineData("TextDialogView.axaml", "CompactModal")]
    [InlineData("ErrorDialogView.axaml", "CompactModal")]
    [InlineData("VersionUpdateDialogView.axaml", "CompactModal")]
    [InlineData("WarningConfirmDialogView.axaml", "CompactModal")]
    public void CompactDialogViews_ShouldUseAppWindowFrameAndDropLegacyShell(string fileName, string expectedMode)
    {
        var root = BaselineTestSupport.GetMaaUnifiedRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "App", "Features", "Dialogs", fileName));

        Assert.Contains("<controls:AppWindowFrame", xaml, StringComparison.Ordinal);
        Assert.Contains($"Mode=\"{expectedMode}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ModernDialogShell", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("PointerPressed=\"OnResizeGripPointerPressed\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeLogAndScreenshotPreviewWindows_ShouldSplitResponsibilities_AndDropLegacyShell()
    {
        var root = BaselineTestSupport.GetMaaUnifiedRoot();
        var runtimeLogXaml = File.ReadAllText(Path.Combine(root, "App", "Views", "RuntimeLogWindow.axaml"));
        var runtimeLogCode = File.ReadAllText(Path.Combine(root, "App", "Views", "RuntimeLogWindow.axaml.cs"));
        var screenshotPreviewXaml = File.ReadAllText(Path.Combine(root, "App", "Views", "ScreenshotPreviewWindow.axaml"));
        var screenshotPreviewCode = File.ReadAllText(Path.Combine(root, "App", "Views", "ScreenshotPreviewWindow.axaml.cs"));
        var mainWindowCode = File.ReadAllText(Path.Combine(root, "App", "Views", "MainWindow.axaml.cs"));
        var connectSettingsCode = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "ConnectSettingsView.axaml.cs"));

        Assert.Contains("<controls:AppWindowFrame", runtimeLogXaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding GrowlMessages}\"", runtimeLogXaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding RootLogs}\"", runtimeLogXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding CapabilitySummary}\"", runtimeLogXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("PreviewImage", runtimeLogXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfigureForScreenshotPreview", runtimeLogCode, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateScreenshotPreviewChrome", runtimeLogCode, StringComparison.Ordinal);
        Assert.DoesNotContain("_previewBitmap", runtimeLogCode, StringComparison.Ordinal);
        Assert.DoesNotContain("ModernDialogShell", runtimeLogXaml, StringComparison.Ordinal);
        Assert.Contains("WindowVisuals.ApplyDefaultIcon(this);", runtimeLogCode, StringComparison.Ordinal);

        Assert.Contains("<controls:AppWindowFrame", screenshotPreviewXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PreviewImage\"", screenshotPreviewXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PreviewHeaderText\"", screenshotPreviewXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PreviewSectionTitleText\"", screenshotPreviewXaml, StringComparison.Ordinal);
        Assert.Contains("SetPreview(Bitmap bitmap, string title, string subtitle, string? statusText = null)", screenshotPreviewCode, StringComparison.Ordinal);
        Assert.Contains("UpdateChrome(string title, string subtitle, string? statusText = null)", screenshotPreviewCode, StringComparison.Ordinal);
        Assert.Contains("_previewBitmap", screenshotPreviewCode, StringComparison.Ordinal);
        Assert.Contains("WindowVisuals.ApplyDefaultIcon(this);", screenshotPreviewCode, StringComparison.Ordinal);

        Assert.Contains("_runtimeLogWindow = new RuntimeLogWindow", mainWindowCode, StringComparison.Ordinal);
        Assert.Contains("private ScreenshotPreviewWindow? _screenshotPreviewWindow;", connectSettingsCode, StringComparison.Ordinal);
        Assert.Contains("_screenshotPreviewWindow.SetPreview(", connectSettingsCode, StringComparison.Ordinal);
        Assert.Contains("_screenshotPreviewWindow.UpdateChrome(", connectSettingsCode, StringComparison.Ordinal);
    }

    [Fact]
    public void TextAndReadonlyDialogs_ShouldUseAppInputBlock_ForMultilineBodyContent()
    {
        var root = BaselineTestSupport.GetMaaUnifiedRoot();
        var textDialogXaml = File.ReadAllText(Path.Combine(root, "App", "Features", "Dialogs", "TextDialogView.axaml"));
        var textDialogCode = File.ReadAllText(Path.Combine(root, "App", "Features", "Dialogs", "TextDialogView.axaml.cs"));
        var errorDialogXaml = File.ReadAllText(Path.Combine(root, "App", "Features", "Dialogs", "ErrorDialogView.axaml"));
        var versionUpdateXaml = File.ReadAllText(Path.Combine(root, "App", "Features", "Dialogs", "VersionUpdateDialogView.axaml"));
        var inputStyles = File.ReadAllText(Path.Combine(root, "App", "Styles", "AppInputStyles.axaml"));
        var appTextInputCode = File.ReadAllText(Path.Combine(root, "App", "Controls", "AppTextInput.axaml.cs"));

        Assert.Contains("<controls:AppTextInput x:Name=\"InputBox\"", textDialogXaml, StringComparison.Ordinal);
        Assert.Contains("ApplyInputMode(request.MultiLine);", textDialogCode, StringComparison.Ordinal);
        Assert.Contains("Classes.Set(\"settings-input\", true);", appTextInputCode, StringComparison.Ordinal);
        Assert.Contains("Classes=\"settings-input settings-input-block error-dialog-detail-box\"", errorDialogXaml, StringComparison.Ordinal);
        Assert.Contains("Classes=\"settings-input settings-input-block\"", versionUpdateXaml, StringComparison.Ordinal);
        Assert.Contains("Classes=\"settings-input settings-input-block\"", textDialogXaml, StringComparison.Ordinal);
        Assert.Contains("Style Selector=\"TextBox.settings-input.settings-input-block\"", inputStyles, StringComparison.Ordinal);
    }

    [Fact]
    public void ErrorDialog_ShouldShowFriendlySummaryBeforeRawDetails()
    {
        var root = BaselineTestSupport.GetMaaUnifiedRoot();
        var errorDialogXaml = File.ReadAllText(Path.Combine(root, "App", "Features", "Dialogs", "ErrorDialogView.axaml"));
        var errorDialogCode = File.ReadAllText(Path.Combine(root, "App", "Features", "Dialogs", "ErrorDialogView.axaml.cs"));

        Assert.Contains("x:Name=\"FriendlyMessageText\"", errorDialogXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SuggestionPanel\"", errorDialogXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DetailSectionTitle\"", errorDialogXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DetailHost\"", errorDialogXaml, StringComparison.Ordinal);
        Assert.Contains("FriendlyMessageText.Text = _simpleConnectFailureMode", errorDialogCode, StringComparison.Ordinal);
        Assert.Contains("SuggestionPanel.IsVisible = !_simpleConnectFailureMode && !string.IsNullOrWhiteSpace(request.Suggestion);", errorDialogCode, StringComparison.Ordinal);
        Assert.Contains("_simpleConnectFailureMode = request.Result.Error?.Code == UiErrorCode.ConnectFailed;", errorDialogCode, StringComparison.Ordinal);
        Assert.Contains("SizeToContent = SizeToContent.Height;", errorDialogCode, StringComparison.Ordinal);
        Assert.Contains("FriendlyMessageText.Classes.Set(\"error-dialog-simple-message\", _simpleConnectFailureMode);", errorDialogCode, StringComparison.Ordinal);
        Assert.Contains("DetailHost.IsVisible = !_simpleConnectFailureMode;", errorDialogCode, StringComparison.Ordinal);
        Assert.Contains("CancelButton.IsVisible = !_simpleConnectFailureMode;", errorDialogCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectionDialogs_ShouldUseSurfaceSelectionListMode_AndSharedCompactEmptyState()
    {
        var root = BaselineTestSupport.GetMaaUnifiedRoot();
        var processPickerXaml = File.ReadAllText(Path.Combine(root, "App", "Features", "Dialogs", "ProcessPickerDialogView.axaml"));
        var emulatorPathXaml = File.ReadAllText(Path.Combine(root, "App", "Features", "Dialogs", "EmulatorPathSelectionDialogView.axaml"));
        var selectionListStyles = File.ReadAllText(Path.Combine(root, "App", "Styles", "AppSelectionListStyles.axaml"));
        var foundationStyles = File.ReadAllText(Path.Combine(root, "App", "Styles", "AppFoundationStyles.axaml"));

        Assert.Contains("<controls:AppSelectionList", processPickerXaml, StringComparison.Ordinal);
        Assert.Contains("VisualMode=\"Surface\"", processPickerXaml, StringComparison.Ordinal);
        Assert.Contains("Classes=\"app-button app-primary\"", processPickerXaml, StringComparison.Ordinal);
        Assert.Contains("Classes=\"app-button app-secondary\"", processPickerXaml, StringComparison.Ordinal);
        Assert.Contains("app-compact-selection-intro", processPickerXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Running processes", processPickerXaml, StringComparison.Ordinal);
        Assert.Contains("app-compact-selection-list", processPickerXaml, StringComparison.Ordinal);
        Assert.Contains("app-compact-empty-state-content", processPickerXaml, StringComparison.Ordinal);
        Assert.Contains("<controls:AppSelectionList", emulatorPathXaml, StringComparison.Ordinal);
        Assert.Contains("VisualMode=\"Surface\"", emulatorPathXaml, StringComparison.Ordinal);
        Assert.Contains("<controls:AppTextInput x:Name=\"PathInput\"", emulatorPathXaml, StringComparison.Ordinal);
        Assert.Contains("app-compact-selection-intro", emulatorPathXaml, StringComparison.Ordinal);
        Assert.Contains("app-compact-selection-list", emulatorPathXaml, StringComparison.Ordinal);
        Assert.Contains("app-compact-empty-state-content", emulatorPathXaml, StringComparison.Ordinal);
        Assert.Contains("controls|AppSelectionList.selection-list-surface Border.app-selection-list-item-shell", selectionListStyles, StringComparison.Ordinal);
        Assert.Contains("controls|AppSelectionList.app-compact-selection-list", foundationStyles, StringComparison.Ordinal);
        Assert.Contains("StackPanel.app-compact-empty-state-content", foundationStyles, StringComparison.Ordinal);
        Assert.Contains("Button.app-button.app-secondary", foundationStyles, StringComparison.Ordinal);
    }

    [Fact]
    public void AnnouncementDialogView_ShouldBuildSectionsFromRealMarkdownHeadings()
    {
        var sections = AnnouncementDialogView.BuildSectionDefinitions(
            "Announcement",
            """
            ### 小游戏发个小公告 (NEW!!!)
            第一段

            ### Windows 端一键长草任务配置重构
            第二段
            """);

        Assert.Collection(
            sections,
            first =>
            {
                Assert.Equal("小游戏发个小公告", first.Title);
                Assert.True(first.IsNew);
                Assert.Equal("第一段", first.MarkdownContent);
            },
            second =>
            {
                Assert.Equal("Windows 端一键长草任务配置重构", second.Title);
                Assert.False(second.IsNew);
                Assert.Equal("第二段", second.MarkdownContent);
            });
    }

    [Fact]
    public void AnnouncementDialogView_ShouldFallbackToDialogTitle_WhenMarkdownHasNoSectionHeadings()
    {
        var sections = AnnouncementDialogView.BuildSectionDefinitions(
            "公告",
            """
            没有分节标题的公告正文

            第二段也应保留
            """);

        var section = Assert.Single(sections);
        Assert.Equal("公告", section.Title);
        Assert.False(section.IsNew);
        Assert.Equal(
            """
            没有分节标题的公告正文

            第二段也应保留
            """,
            section.MarkdownContent);
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
                TotalCount: 3),
            "NEW",
            "Progress: {0}");

        var initialState = presenter.BuildState(
            "Unlocked {0} / {1} · {2}% complete",
            "Showing {0} achievements");
        Assert.Equal(3, initialState.Items.Count);
        Assert.Equal("Unlocked 1 / 3 · 33% complete", initialState.OverviewText);

        presenter.SetFilter(AchievementQuickFilter.InProgress);
        var progressState = presenter.BuildState(
            "Unlocked {0} / {1} · {2}% complete",
            "Showing {0} achievements");
        var progressItem = Assert.Single(progressState.Items);
        Assert.Equal("progress", progressItem.Id);
        Assert.Equal("Progress: 3 / 5", progressItem.ProgressText);

        presenter.SetFilter(AchievementQuickFilter.NewUnlock);
        presenter.SetSearchText("contact");
        var combinedState = presenter.BuildState(
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
        SetAchievementListDialogField(view, "FilterInput", new AppTextInput { Text = "keyword" });

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
        var processList = new MAAUnified.App.Controls.AppSelectionList
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
