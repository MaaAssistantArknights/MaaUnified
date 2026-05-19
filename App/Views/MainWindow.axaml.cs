using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Globalization;
using System.Text.Json.Nodes;
using MAAUnified.App.Controls;
using MAAUnified.App.Features.Dialogs;
using MAAUnified.App.Infrastructure;
using MAAUnified.App.Services;
using MAAUnified.App.ViewModels;
using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.Application.Models;
using MAAUnified.Application.Services.Localization;
using MAAUnified.Platform;
using System.ComponentModel;
using System.Linq;
using LegacyConfigurationKeys = MAAUnified.Compat.Constants.ConfigurationKeys;

namespace MAAUnified.App.Views;

public partial class MainWindow : Window
{
    private const string LiveResizingClassName = "live-resizing";
    private const double CompactLayoutHeightThreshold = 720d;
    private const double ResponsiveMinWindowWidth = 1080d;
    private const double ResponsiveMarginStageEndWidth = 1160d;
    private const double ResponsiveMinPageMargin = 12d;
    private const double ResponsiveMaxPageMargin = 18d;
    private const double ResponsiveMaxLayoutWidth = 1360d;
    private const double ResponsiveMinLayoutWidth = ResponsiveMinWindowWidth - (ResponsiveMinPageMargin * 2d);
    private const double ResponsiveContentStageEndWidth = ResponsiveMarginStageEndWidth + (ResponsiveMaxLayoutWidth - ResponsiveMinLayoutWidth);
    private const double BaseWindowWidth = 1380d;
    private const double BaseWindowHeight = 900d;
    private const double BaseWindowMinWidth = 1080d;
    private const double BaseWindowMinHeight = 620d;
    private const double MacOsDefaultWindowWidth = 1104d;
    private const double MacOsDefaultWindowHeight = 720d;
    private const double MacOsDefaultWindowSizeScale = 1d;
    private const double NativeWindowShadowShellMarginCompensation = 24d;
    private const double MacOsHiDpiHeightBoostPerScaleStep = 0.12d;
    private const double MacOsHiDpiHeightBoostMax = 1.18d;
    private const int WindowPlacementSchemaVersion = 1;
    private const string WindowPlacementSchemaKey = "Schema";
    private const string WindowPlacementPlatformsKey = "Platforms";
    private const string WindowPlacementWidthKey = "Width";
    private const string WindowPlacementHeightKey = "Height";
    private const double MinimumPersistedWindowWidth = 320d;
    private const double MinimumPersistedWindowHeight = 240d;
    private const int ResponsiveMarginProgressSteps = 12;
    private const int ResponsiveWidthProgressSteps = 24;
    private static readonly TimeSpan ResizeSettleDelay = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan CloseCompletionWatchdogDelay = TimeSpan.FromSeconds(15);
    private static readonly KeyValuePair<string, object>[] CompactLayoutResourceOverrides =
    [
        new("MAA.Thickness.SectionPadding", new Thickness(6)),
        new("MAA.Thickness.SectionPaddingStrong", new Thickness(6)),
        new("MAA.Thickness.MarginTopSection", new Thickness(0, 6, 0, 0)),
        new("MAA.Thickness.MarginBottomSection", new Thickness(0, 0, 0, 6)),
        new("MAA.Thickness.MarginSectionVertical", new Thickness(0, 6, 0, 6)),
        new("MAA.Thickness.MarginRightSection", new Thickness(0, 0, 6, 0)),
        new("MAA.Thickness.TabCompactPadding", new Thickness(8, 4)),
        new("MAA.Thickness.CopilotNavTabPadding", new Thickness(8, 0, 8, 5)),
        new("MAA.Thickness.RootNavTabPadding", new Thickness(8, 0, 8, 0)),
        new("MAA.Thickness.ToolboxNavTabPadding", new Thickness(8, 1, 8, 8)),
        new("MAA.FontSize.SectionTitle", 13.5d),
        new("MAA.FontSize.CopilotNavTab", 13.5d),
        new("MAA.Size.Action.Height", 28d),
        new("MAA.Size.Action.RunPrimaryHeight", 44d),
        new("MAA.Size.Action.RunSecondaryHeight", 36d),
        new("MAA.Size.TaskQueue.RowHeight", 28d),
        new("MAA.Size.Tab.MinHeight", 28d),
    ];
    private static readonly ResponsiveDoubleResourceRange[] ResponsiveWidthResourceRanges =
    [
        new("MAA.Size.TaskQueue.ListPanelWidth", 236d, 276d),
        new("MAA.Size.TaskQueue.ConfigPanelWidth", 324d, 450d),
        new("MAA.Size.TaskQueue.LogPanelWidth", 304d, 440d),
        new("MAA.Size.TaskQueue.PostActionSummaryWidth", 170d, 185d),
        new("MAA.Size.TaskQueue.PostActionDescriptionWidth", 146d, 160d),
        new("MAA.Size.Settings.SectionListWidth", 200d, 224d),
        new("MAA.Size.Settings.FormMaxWidth", 860d, 920d),
        new("MAA.Size.Settings.FormNarrowMaxWidth", 710d, 760d),
        new("MAA.Size.Settings.ContentMaxWidth", 405d, 450d),
        new("MAA.Size.Settings.ColumnWidth", 228d, 250d),
        new("MAA.Size.Settings.FieldSlimWidth", 130d, 144d),
        new("MAA.Size.Settings.FieldNarrowWidth", 154d, 170d),
        new("MAA.Size.Settings.FieldCenteredWidth", 180d, 200d),
        new("MAA.Size.Settings.FieldInputWidth", 270d, 300d),
        new("MAA.Size.Settings.FieldPathWidth", 360d, 400d),
        new("MAA.Size.Settings.FieldPathWideWidth", 378d, 420d),
        new("MAA.Size.Settings.FieldWidth", 198d, 220d),
        new("MAA.Size.Settings.FieldTimerConfigWidth", 198d, 220d),
        new("MAA.Size.Settings.FieldWideWidth", 252d, 280d),
        new("MAA.Size.Settings.FieldExtraWideWidth", 306d, 340d),
        new("MAA.Size.Settings.WrapItemWidth", 198d, 220d),
        new("MAA.Size.Toolbox.ActionButtonWidth", 162d, 180d),
        new("MAA.Size.Toolbox.WarningTextMaxWidth", 504d, 560d),
        new("MAA.Size.Toolbox.FormPanelWidth", 580d, 640d),
        new("MAA.Size.Copilot.SidePanelWidth", 491d, 546d),
    ];

    private bool _platformBound;
    private bool _dialogErrorBound;
    private bool _processingDialogErrors;
    private bool _processingMinimizeToTray;
    private bool _allowLifecycleClose;
    private bool _closeRequestPending;
    private bool _compactLayoutEnabled;
    private ResponsiveLayoutMetrics? _responsiveLayoutMetrics;
    private bool _adaptiveLayoutUpdateQueued;
    private bool _pendingAdaptiveLayoutFlushAllHosts;
    private double _pendingAdaptiveLayoutWidth;
    private double _pendingAdaptiveLayoutHeight;
    private bool _hasAppliedUiScaleToWindowBounds;
    private bool _hasAppliedOpenedWindowBounds;
    private double _lastAppliedWindowWidthScale = 1d;
    private double _lastAppliedWindowHeightScale = 1d;
    private Size? _lastNormalWindowSize;
    private DispatcherTimer? _resizeSettleTimer;
    private CancellationTokenSource? _closeCompletionWatchdogCts;
    private bool _isLiveResizing;
    private readonly object _dialogErrorGate = new();
    private readonly Queue<DialogErrorRaisedEvent> _pendingDialogErrors = [];
    private readonly HashSet<string> _pendingDialogErrorKeys = new(StringComparer.Ordinal);
    private readonly HashSet<Control> _pendingAchievementToastPresentationControls = [];
    private readonly Dictionary<ContentControl, ResponsiveLayoutMetrics> _rootHostResponsiveMetrics = [];
    private readonly IAppDialogService _dialogService;
    private readonly ShellCloseConfirmationService _closeConfirmationService;
    private OverlayHostWindow? _overlayHostWindow;
    private RuntimeLogWindow? _runtimeLogWindow;
    private TrayContextMenuWindow? _trayContextMenuWindow;
    private RootPageHostViewModel? _settingsWarmupRootPage;
    private bool _settingsWarmupStarted;
    private bool _settingsSectionWarmupStarted;
    private MainShellViewModel? _shellBackgroundVm;
    private BlurEffect? _shellBackgroundBlurEffect;

    private readonly record struct ResponsiveDoubleResourceRange(string ResourceKey, double Minimum, double Maximum);

    private readonly record struct ResponsiveLayoutMetrics(double PageMargin, double LayoutWidth, double WidthProgress)
    {
        public bool ApproximatelyEquals(ResponsiveLayoutMetrics other)
        {
            return Math.Abs(PageMargin - other.PageMargin) < 0.01d
                && Math.Abs(LayoutWidth - other.LayoutWidth) < 0.01d
                && Math.Abs(WidthProgress - other.WidthProgress) < 0.001d;
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        WindowVisuals.ApplyDefaultIcon(this);
        ApplyPlatformDefaultWindowSize(OperatingSystem.IsMacOS());
        _dialogService = Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime
            ? new AvaloniaDialogService(App.Runtime)
            : NoOpAppDialogService.Instance;
        _closeConfirmationService = new ShellCloseConfirmationService(_dialogService);
        BindDialogErrorEvents();
        Opened += OnWindowOpened;
        Resized += OnWindowResized;
        KeyDown += OnWindowKeyDown;
        Closing += OnWindowClosing;
        Closed += OnWindowClosed;
        DataContextChanged += OnWindowDataContextChanged;
        PropertyChanged += OnWindowPropertyChanged;
        WindowShellFrame.PropertyChanged += OnWindowShellFramePropertyChanged;
        TaskQueueRootHost.PropertyChanged += OnRootHostPropertyChanged;
        CopilotRootHost.PropertyChanged += OnRootHostPropertyChanged;
        ToolboxRootHost.PropertyChanged += OnRootHostPropertyChanged;
        SettingsRootHost.PropertyChanged += OnRootHostPropertyChanged;
        AvaloniaDialogService.OwnerModalStateChanged += OnOwnerModalStateChanged;
        BindShellBackgroundVm(VM);
        App.Runtime.UiLanguageCoordinator.LanguageChanged += OnUiLanguageChanged;
    }

    private MainShellViewModel? VM => DataContext as MainShellViewModel;

    private async void OnWindowClosing(object? sender, CancelEventArgs e)
        => await App.RunUiTaskAsync("MainWindow.Closing", async () =>
    {
        if (_allowLifecycleClose || VM is null)
        {
            return;
        }

        e.Cancel = true;
        if (_closeRequestPending)
        {
            return;
        }

        _closeRequestPending = true;
        try
        {
            if (!await ConfirmCloseAsync("App.Shell.Window.Close.Confirm"))
            {
                await App.Runtime.DiagnosticsService.RecordEventAsync(
                    "App.Shell.Window.Close",
                    "source=window-chrome; cancelled");
                return;
            }

            await App.Runtime.DiagnosticsService.RecordEventAsync(
                "App.Shell.Window.Close",
                "source=window-chrome; confirmed");
            ArmCloseCompletionWatchdog("App.Shell.Window.Close.Watchdog");
            if (!await CompleteConfigurationSavesBeforeCloseAsync("App.Shell.Window.Close.ConfigSave"))
            {
                DisarmCloseCompletionWatchdog();
                return;
            }

            _ = await ExitApplicationAsync("App.Shell.Window.Close.Exit");
        }
        finally
        {
            if (!_allowLifecycleClose)
            {
                DisarmCloseCompletionWatchdog();
                _closeRequestPending = false;
            }
        }
    });

    private async void OnWindowOpened(object? sender, EventArgs e)
        => await App.RunUiTaskAsync("MainWindow.Opened", async () =>
    {
        Program.RecordStartupStage("MainWindow.Opened", "Main window opened.");
        if (VM is not null)
        {
            await ApplyOpenedWindowBoundsAsync(VM);
        }

        FitToCurrentScreenWorkingArea();
        UpdateAdaptiveLayoutMode(flushAllHosts: true);
        RecordUiScaleDiagnostics("opened");
        StartDialogErrorPumpIfNeeded();
        var vm = VM;
        if (vm is null || _platformBound)
        {
            UpdateAchievementToastVisibility();
            return;
        }

        Program.RecordStartupStage("MainWindow.PlatformInit.WaitFirstScreen.Begin", "Waiting for first screen before platform initialization.");
        await vm.WaitForFirstScreenReadyAsync();
        Program.RecordStartupStage("MainWindow.PlatformInit.WaitFirstScreen.End", "First screen ready; continuing platform initialization.");

        vm = VM;
        if (vm is null || _platformBound || !IsVisible)
        {
            UpdateAchievementToastVisibility();
            return;
        }

        BindSettingsWarmup(vm);

        Program.RecordStartupStage("MainWindow.PlatformInit.Begin", "Initializing tray, hotkeys, and overlay host.");
        vm.PlatformCapabilityService.TrayCommandInvoked += OnTrayCommandInvoked;
        vm.PlatformCapabilityService.TrayMenuRequested += OnTrayMenuRequested;
        vm.PlatformCapabilityService.GlobalHotkeyTriggered += OnGlobalHotkeyTriggered;
        vm.PlatformCapabilityService.OverlayStateChanged += OnPlatformOverlayStateChanged;
        _platformBound = true;

        await RunPlatformStartupStepAsync(
            "PlatformCapability.Hotkey.ConfigureHost",
            async () =>
            {
                var hotkeyHostContext = await vm.PlatformCapabilityService.ConfigureHotkeyHostContextAsync(
                    BuildHotkeyHostContext());
                await HandlePlatformResultAsync("PlatformCapability.Hotkey.ConfigureHost", hotkeyHostContext);
            });

        await RunPlatformStartupStepAsync(
            "PlatformCapability.Tray.Initialize",
            async () =>
            {
                var trayInit = await vm.PlatformCapabilityService.InitializeTrayAsync(
                    MainShellViewModel.AppDisplayName,
                    PlatformCapabilityTextMap.CreateTrayMenuText(vm.CurrentShellLanguage, vm.ReportLocalizationFallback));
                await HandlePlatformResultAsync("PlatformCapability.Tray.Initialize", trayInit);
            });

        await RunPlatformStartupStepAsync(
            "PlatformCapability.Tray.InitialVisibility",
            async () =>
            {
                var trayVisible = await vm.PlatformCapabilityService.SetTrayVisibleAsync(vm.SettingsPage.UseTray);
                await HandlePlatformResultAsync("PlatformCapability.Tray.InitialVisibility", trayVisible);
            });

        await RunPlatformStartupStepAsync(
            "PlatformCapability.Hotkey.RegisterStartup",
            async () =>
            {
                await vm.RegisterHotkeysAtStartupAsync();
            });

        await RunPlatformStartupStepAsync(
            "PlatformCapability.Overlay.BindHost",
            async () =>
            {
                await EnsureOverlayHostBoundAsync();
            });

        await RunPlatformStartupStepAsync(
            "App.Startup.LaunchBehavior",
            async () =>
            {
                await vm.ExecuteStartupLaunchBehaviorAsync(minimizeWindowAsync: MinimizeFromStartupAsync);
            });

        vm.BeginAchievementToastStartupRelease();
        UpdateAchievementToastVisibility();
        Program.RecordStartupStage("MainWindow.PlatformInit.End", "Platform initialization completed.");
    });

    private async Task RunPlatformStartupStepAsync(string scope, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (App.ShouldIgnoreUnhandledException(ex))
        {
        }
        catch (Exception ex)
        {
            Program.RecordStartupStage($"{scope}.Fail", $"{scope} failed during window startup.", ex);
            await App.Runtime.DiagnosticsService.RecordErrorAsync(
                scope,
                $"{scope} failed during window startup.",
                ex);
        }
    }

    private async void OnWindowClosed(object? sender, EventArgs e)
        => await App.RunUiTaskAsync("MainWindow.Closed", async () =>
    {
        UpdateAchievementToastVisibility();
        BindShellBackgroundVm(null);
        EndLiveResizeVisualThrottle();
        Resized -= OnWindowResized;
        WindowShellFrame.PropertyChanged -= OnWindowShellFramePropertyChanged;
        DataContextChanged -= OnWindowDataContextChanged;
        TaskQueueRootHost.PropertyChanged -= OnRootHostPropertyChanged;
        CopilotRootHost.PropertyChanged -= OnRootHostPropertyChanged;
        ToolboxRootHost.PropertyChanged -= OnRootHostPropertyChanged;
        SettingsRootHost.PropertyChanged -= OnRootHostPropertyChanged;
        if (_resizeSettleTimer is not null)
        {
            _resizeSettleTimer.Stop();
            _resizeSettleTimer.Tick -= OnResizeSettleTimerTick;
            _resizeSettleTimer = null;
        }

        _rootHostResponsiveMetrics.Clear();
        if (_dialogErrorBound)
        {
            App.Runtime.DialogFeatureService.ErrorRaised -= OnDialogErrorRaised;
            lock (_dialogErrorGate)
            {
                _pendingDialogErrors.Clear();
                _pendingDialogErrorKeys.Clear();
                _processingDialogErrors = false;
            }

            _dialogErrorBound = false;
        }

        VM?.CancelStartupInitialization();
        UnbindSettingsWarmup();

        var vm = VM;
        if (vm is not null && _platformBound)
        {
            vm.PlatformCapabilityService.TrayCommandInvoked -= OnTrayCommandInvoked;
            vm.PlatformCapabilityService.TrayMenuRequested -= OnTrayMenuRequested;
            vm.PlatformCapabilityService.GlobalHotkeyTriggered -= OnGlobalHotkeyTriggered;
            vm.PlatformCapabilityService.OverlayStateChanged -= OnPlatformOverlayStateChanged;
            _platformBound = false;
            await RunPlatformShutdownStepAsync(
                "PlatformCapability.Hotkey.Unregister.ShowGui",
                () => vm.PlatformCapabilityService.UnregisterGlobalHotkeyAsync("ShowGui"));
            await RunPlatformShutdownStepAsync(
                "PlatformCapability.Hotkey.Unregister.LinkStart",
                () => vm.PlatformCapabilityService.UnregisterGlobalHotkeyAsync("LinkStart"));
            await RunPlatformShutdownStepAsync(
                "PlatformCapability.Tray.Shutdown",
                () => vm.PlatformCapabilityService.ShutdownTrayAsync());
        }

        if (_overlayHostWindow is not null)
        {
            await RunWindowCleanupStepAsync(
                "MainWindow.OverlayHost.Close",
                () =>
                {
                    try
                    {
                        _overlayHostWindow.Close();
                    }
                    finally
                    {
                        _overlayHostWindow = null;
                    }

                    return Task.CompletedTask;
                });
        }

        if (_runtimeLogWindow is not null)
        {
            await RunWindowCleanupStepAsync(
                "MainWindow.RuntimeLogWindow.Close",
                () =>
                {
                    try
                    {
                        _runtimeLogWindow.Closed -= OnRuntimeLogWindowClosed;
                        _runtimeLogWindow.Close();
                    }
                    finally
                    {
                        _runtimeLogWindow = null;
                    }

                    return Task.CompletedTask;
                });
        }

        CloseTrayContextMenu();

        AvaloniaDialogService.OwnerModalStateChanged -= OnOwnerModalStateChanged;
        App.Runtime.UiLanguageCoordinator.LanguageChanged -= OnUiLanguageChanged;
    });

    private async Task RunPlatformShutdownStepAsync(string scope, Func<Task<UiOperationResult>> action)
    {
        try
        {
            var result = await action();
            if (!result.Success)
            {
                await App.Runtime.DiagnosticsService.RecordFailedResultAsync(scope, result);
            }
        }
        catch (Exception ex) when (App.ShouldIgnoreUnhandledException(ex))
        {
        }
        catch (Exception ex)
        {
            await App.Runtime.DiagnosticsService.RecordErrorAsync(
                scope,
                $"{scope} failed during window shutdown.",
                ex);
        }
    }

    private async Task RunWindowCleanupStepAsync(string scope, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (App.ShouldIgnoreUnhandledException(ex))
        {
        }
        catch (Exception ex)
        {
            await App.Runtime.DiagnosticsService.RecordErrorAsync(
                scope,
                $"{scope} failed during window shutdown.",
                ex);
        }
    }

    private async void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        => await App.RunUiTaskAsync("MainWindow.PropertyChanged", async () =>
    {
        if (e.Property == IsVisibleProperty || e.Property == WindowStateProperty)
        {
            UpdateAchievementToastVisibility();
        }

        if (string.Equals(e.Property.Name, nameof(RenderScaling), StringComparison.Ordinal))
        {
            ApplyUiScaleToWindowBounds(preserveLogicalSize: true);
            UpdateAdaptiveLayoutMode(flushAllHosts: true);
            RecordUiScaleDiagnostics("render-scaling-changed");
            return;
        }

        if (e.Property == WindowStateProperty)
        {
            EndLiveResizeVisualThrottle();
            CaptureLastNormalWindowSize();
            await HandleMinimizeToTrayAsync();
        }
    });

    private void UpdateAchievementToastVisibility()
    {
        VM?.SetAchievementToastWindowVisible(IsVisible && WindowState != WindowState.Minimized);
        TryStartPendingAchievementToastPresentations();
    }

    private void OnOwnerModalStateChanged(object? sender, EventArgs e)
    {
        App.PostUiCallback(
            "MainWindow.AchievementToast.OwnerModalStateChanged",
            TryStartPendingAchievementToastPresentations);
    }

    private void OnWindowResized(object? sender, WindowResizedEventArgs e)
    {
        if (ShouldTreatResizeAsLiveInteraction(e.Reason))
        {
            BeginLiveResizeVisualThrottle();
        }

        ScheduleAdaptiveLayoutUpdate(
            ResolveResponsiveLayoutWidth(e.ClientSize.Width),
            e.ClientSize.Height,
            flushAllHosts: false,
            immediate: false);
        CaptureLastNormalWindowSize();
        RestartResizeSettleTimer();
    }

    private void OnWindowDataContextChanged(object? sender, EventArgs e)
    {
        var vm = VM;
        BindShellBackgroundVm(vm);
        if (vm is not null)
        {
            ApplyUiScaleToWindowBounds(preserveLogicalSize: _hasAppliedUiScaleToWindowBounds);
        }

        UpdateAdaptiveLayoutMode(flushAllHosts: true);
    }

    private void OnRootHostPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != IsVisibleProperty || sender is not ContentControl host || !host.IsVisible)
        {
            return;
        }

        if (_responsiveLayoutMetrics is { } metrics)
        {
            ApplyResponsiveLayoutMetricsToRootHost(host, metrics);
            return;
        }

        UpdateAdaptiveLayoutMode();
    }

    private void OnUiLanguageChanged(object? sender, UiLanguageChangedEventArgs e)
    {
        App.PostUiCallback("MainWindow.LanguageChanged.RefreshLayout", RefreshLocalizedLayout, DispatcherPriority.Background);
    }

    private void RefreshLocalizedLayout()
    {
        InvalidateLocalizedLayoutTree();
        App.PostUiCallback(
            "MainWindow.LanguageChanged.InvalidateLayout",
            InvalidateLocalizedLayoutTree,
            DispatcherPriority.Render);
    }

    private void UpdateAdaptiveLayoutMode(bool flushAllHosts = false)
    {
        ScheduleAdaptiveLayoutUpdate(
            ResolveResponsiveLayoutWidth(),
            ResolveResponsiveLayoutHeight(),
            flushAllHosts,
            immediate: true);
    }

    private void ScheduleAdaptiveLayoutUpdate(double width, double height, bool flushAllHosts, bool immediate)
    {
        if (width <= 0d || height <= 0d)
        {
            return;
        }

        _pendingAdaptiveLayoutWidth = width;
        _pendingAdaptiveLayoutHeight = height;
        _pendingAdaptiveLayoutFlushAllHosts |= flushAllHosts;
        if (immediate)
        {
            ApplyPendingAdaptiveLayoutUpdate();
            return;
        }

        if (_adaptiveLayoutUpdateQueued)
        {
            return;
        }

        _adaptiveLayoutUpdateQueued = true;
        App.PostUiCallback(
            "MainWindow.AdaptiveLayout.ApplyPending",
            () =>
            {
                _adaptiveLayoutUpdateQueued = false;
                ApplyPendingAdaptiveLayoutUpdate();
            },
            DispatcherPriority.Render);
    }

    private void ApplyPendingAdaptiveLayoutUpdate()
    {
        var width = _pendingAdaptiveLayoutWidth;
        var height = _pendingAdaptiveLayoutHeight;
        var flushAllHosts = _pendingAdaptiveLayoutFlushAllHosts;

        _pendingAdaptiveLayoutWidth = 0d;
        _pendingAdaptiveLayoutHeight = 0d;
        _pendingAdaptiveLayoutFlushAllHosts = false;

        if (width <= 0d || height <= 0d)
        {
            return;
        }

        var metrics = CalculateResponsiveLayoutMetrics(width, maxContentWidth: width);
        _ = ApplyResponsiveLayoutMetrics(metrics);
        var compactChanged = UpdateCompactLayoutMode(height <= CompactLayoutHeightThreshold);

        if (flushAllHosts)
        {
            ApplyResponsiveLayoutMetricsToAllRootHosts(metrics);
        }
        else
        {
            ApplyResponsiveLayoutMetricsToActiveRootHost(metrics);
        }

        if (compactChanged)
        {
            RefreshLocalizedLayout();
        }
    }

    private bool ApplyResponsiveLayoutMetrics(ResponsiveLayoutMetrics metrics)
    {
        if (_responsiveLayoutMetrics is { } current && current.ApproximatelyEquals(metrics))
        {
            return false;
        }

        _responsiveLayoutMetrics = metrics;
        Resources["MAA.Thickness.PageMargin"] = new Thickness(metrics.PageMargin);
        ApplyDoubleResource(Resources, "MAA.Size.MainWindow.LayoutWidth", metrics.LayoutWidth, ResponsiveMaxLayoutWidth);
        return true;
    }

    private bool UpdateCompactLayoutMode(bool useCompactLayout)
    {
        if (_compactLayoutEnabled == useCompactLayout)
        {
            return false;
        }

        _compactLayoutEnabled = useCompactLayout;
        if (useCompactLayout)
        {
            foreach (var (key, value) in CompactLayoutResourceOverrides)
            {
                Resources[key] = value;
            }
        }
        else
        {
            foreach (var (key, _) in CompactLayoutResourceOverrides)
            {
                Resources.Remove(key);
            }
        }

        return true;
    }

    private static ResponsiveLayoutMetrics CalculateResponsiveLayoutMetrics(double windowWidth, double maxContentWidth)
    {
        var marginProgress = QuantizeProgress(
            InverseLerp(ResponsiveMinWindowWidth, ResponsiveMarginStageEndWidth, windowWidth),
            ResponsiveMarginProgressSteps);
        var pageMargin = Lerp(ResponsiveMinPageMargin, ResponsiveMaxPageMargin, marginProgress);
        var maxSafePageMargin = Math.Max(0d, maxContentWidth / 2d);
        pageMargin = Math.Min(pageMargin, maxSafePageMargin);
        var maxLayoutWidth = Math.Max(0d, maxContentWidth - (pageMargin * 2d));

        if (windowWidth <= ResponsiveMarginStageEndWidth)
        {
            return new ResponsiveLayoutMetrics(pageMargin, Math.Min(ResponsiveMinLayoutWidth, maxLayoutWidth), 0d);
        }

        var widthProgress = QuantizeProgress(
            InverseLerp(ResponsiveMarginStageEndWidth, ResponsiveContentStageEndWidth, windowWidth),
            ResponsiveWidthProgressSteps);
        var expandedPageMargin = Math.Min(ResponsiveMaxPageMargin, maxSafePageMargin);
        var expandedLayoutWidth = Math.Max(0d, maxContentWidth - (expandedPageMargin * 2d));
        var layoutWidth = Math.Min(Lerp(ResponsiveMinLayoutWidth, ResponsiveMaxLayoutWidth, widthProgress), expandedLayoutWidth);
        return new ResponsiveLayoutMetrics(expandedPageMargin, layoutWidth, widthProgress);
    }

    private void ApplyResponsiveLayoutMetricsToActiveRootHost(ResponsiveLayoutMetrics metrics)
    {
        if (ResolveActiveRootHost() is not { } host)
        {
            return;
        }

        ApplyResponsiveLayoutMetricsToRootHost(host, metrics);
    }

    private void ApplyResponsiveLayoutMetricsToAllRootHosts(ResponsiveLayoutMetrics metrics)
    {
        foreach (var host in EnumerateRootHosts())
        {
            ApplyResponsiveLayoutMetricsToRootHost(host, metrics);
        }
    }

    private void ApplyResponsiveLayoutMetricsToRootHost(ContentControl host, ResponsiveLayoutMetrics metrics)
    {
        if (_rootHostResponsiveMetrics.TryGetValue(host, out var current) && current.ApproximatelyEquals(metrics))
        {
            return;
        }

        var resources = EnsureResources(host);
        var copilotSideMargin = Lerp(2d, 4d, metrics.WidthProgress);
        resources["MAA.Thickness.CopilotRootMargin"] = new Thickness(copilotSideMargin, 0d, copilotSideMargin, copilotSideMargin);
        foreach (var range in ResponsiveWidthResourceRanges)
        {
            var value = Lerp(range.Minimum, range.Maximum, metrics.WidthProgress);
            ApplyDoubleResource(resources, range.ResourceKey, value, range.Maximum);
        }

        _rootHostResponsiveMetrics[host] = metrics;
    }

    private ContentControl? ResolveActiveRootHost()
    {
        foreach (var host in EnumerateRootHosts())
        {
            if (host.IsVisible)
            {
                return host;
            }
        }

        return null;
    }

    private IEnumerable<ContentControl> EnumerateRootHosts()
    {
        yield return TaskQueueRootHost;
        yield return CopilotRootHost;
        yield return ToolboxRootHost;
        yield return SettingsRootHost;
    }

    private static IResourceDictionary EnsureResources(StyledElement element)
    {
        if (element.Resources is null)
        {
            element.Resources = new ResourceDictionary();
        }

        return element.Resources;
    }

    private static void ApplyDoubleResource(IResourceDictionary resources, string key, double value, double defaultValue)
    {
        if (Math.Abs(value - defaultValue) < 0.01d)
        {
            resources.Remove(key);
            return;
        }

        resources[key] = value;
    }

    private void RestartResizeSettleTimer()
    {
        if (_resizeSettleTimer is null)
        {
            _resizeSettleTimer = new DispatcherTimer
            {
                Interval = ResizeSettleDelay,
            };
            _resizeSettleTimer.Tick += OnResizeSettleTimerTick;
        }

        _resizeSettleTimer.Stop();
        _resizeSettleTimer.Start();
    }

    private void OnResizeSettleTimerTick(object? sender, EventArgs e)
    {
        _resizeSettleTimer?.Stop();
        UpdateAdaptiveLayoutMode(flushAllHosts: true);
        EndLiveResizeVisualThrottle();
    }

    private void BeginLiveResizeVisualThrottle()
    {
        if (_isLiveResizing)
        {
            return;
        }

        _isLiveResizing = true;
        Classes.Set(LiveResizingClassName, true);
        WindowShellFrame.Classes.Set(LiveResizingClassName, true);
        ApplyShellBackgroundEffect();
    }

    private void EndLiveResizeVisualThrottle()
    {
        if (!_isLiveResizing)
        {
            return;
        }

        _isLiveResizing = false;
        Classes.Set(LiveResizingClassName, false);
        WindowShellFrame.Classes.Set(LiveResizingClassName, false);
        ApplyShellBackgroundEffect();
    }

    private static double InverseLerp(double start, double end, double value)
    {
        if (end <= start)
        {
            return 1d;
        }

        return Math.Clamp((value - start) / (end - start), 0d, 1d);
    }

    private static double Lerp(double start, double end, double progress)
    {
        return start + ((end - start) * Math.Clamp(progress, 0d, 1d));
    }

    private static double QuantizeProgress(double progress, int steps)
    {
        if (steps <= 0)
        {
            return Math.Clamp(progress, 0d, 1d);
        }

        var clamped = Math.Clamp(progress, 0d, 1d);
        return Math.Round(clamped * steps, MidpointRounding.AwayFromZero) / steps;
    }

    private double ResolveResponsiveLayoutWidth()
    {
        var clientWidth = ClientSize.Width > 0d ? ClientSize.Width : Bounds.Width;
        return ResolveResponsiveLayoutWidth(clientWidth);
    }

    private double ResolveResponsiveLayoutWidth(double clientWidth)
    {
        if (clientWidth <= 0d)
        {
            return 0d;
        }

        return Math.Max(0d, (clientWidth - WindowShellFrame.EffectiveHorizontalContentInset.Total) / GetEffectiveUiScaleFactor());
    }

    private double ResolveResponsiveLayoutHeight()
    {
        var height = ClientSize.Height > 0d ? ClientSize.Height : Bounds.Height;
        return height <= 0d ? 0d : height / GetEffectiveUiScaleFactor();
    }

    private double GetEffectiveUiScaleFactor()
    {
        var scale = VM?.EffectiveUiScaleFactor ?? 1d;
        return double.IsFinite(scale) && scale > 0d ? scale : 1d;
    }

    private async Task ApplyOpenedWindowBoundsAsync(MainShellViewModel vm)
    {
        try
        {
            await vm.WaitForStartupSnapshotReadyAsync();
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var restored = TryApplyPersistedWindowSize();
        if (!restored)
        {
            var forcePlatformDefaultSize = OperatingSystem.IsMacOS() && !_hasAppliedOpenedWindowBounds;
            ApplyUiScaleToWindowBounds(
                preserveLogicalSize: _hasAppliedUiScaleToWindowBounds && !forcePlatformDefaultSize,
                force: forcePlatformDefaultSize);
        }

        _hasAppliedOpenedWindowBounds = true;
        CaptureLastNormalWindowSize();
    }

    private void ApplyUiScaleToWindowBounds(bool preserveLogicalSize, bool force = false)
    {
        try
        {
            var isMacOS = OperatingSystem.IsMacOS();
            var widthScale = ComputeWindowWidthScale(GetEffectiveUiScaleFactor());
            var heightScale = ComputeWindowHeightScale(GetEffectiveUiScaleFactor(), RenderScaling, isMacOS);
            if (!force
                && _hasAppliedUiScaleToWindowBounds
                && Math.Abs(_lastAppliedWindowWidthScale - widthScale) < 0.001d
                && Math.Abs(_lastAppliedWindowHeightScale - heightScale) < 0.001d)
            {
                return;
            }

            var previousWidthScale = _hasAppliedUiScaleToWindowBounds ? _lastAppliedWindowWidthScale : 1d;
            var previousHeightScale = _hasAppliedUiScaleToWindowBounds ? _lastAppliedWindowHeightScale : 1d;
            var defaultWidth = ComputeDefaultWindowWidth(isMacOS);
            var defaultHeight = ComputeDefaultWindowHeight(isMacOS);

            MinWidth = ComputeMinimumWindowWidth(isMacOS) * widthScale;
            MinHeight = ComputeMinimumWindowHeight(isMacOS) * heightScale;
            if (WindowState == WindowState.Normal)
            {
                Width = ResolveWindowSizeTarget(
                    Width,
                    previousWidthScale,
                    widthScale,
                    defaultWidth,
                    MinWidth,
                    preserveLogicalSize,
                    keepMacPlatformDefaultSize: isMacOS && !preserveLogicalSize);
                Height = ResolveWindowSizeTarget(
                    Height,
                    previousHeightScale,
                    heightScale,
                    defaultHeight,
                    MinHeight,
                    preserveLogicalSize,
                    keepMacPlatformDefaultSize: isMacOS && !preserveLogicalSize);
            }

            _lastAppliedWindowWidthScale = widthScale;
            _lastAppliedWindowHeightScale = heightScale;
            _hasAppliedUiScaleToWindowBounds = true;
            FitToCurrentScreenWorkingArea();
        }
        catch (ObjectDisposedException)
        {
            // Ignore late size adjustments while the shell is shutting down.
        }
    }

    private bool TryApplyPersistedWindowSize()
    {
        try
        {
            var globalValues = App.Runtime.ConfigurationService.CurrentConfig.GlobalValues;
            if (!ShouldLoadPersistedWindowSize(globalValues)
                || !TryReadPersistedWindowSize(
                    globalValues,
                    ResolveCurrentWindowPlacementPlatformKey(),
                    out var persistedSize))
            {
                return false;
            }

            var isMacOS = OperatingSystem.IsMacOS();
            var widthScale = ComputeWindowWidthScale(GetEffectiveUiScaleFactor());
            var heightScale = ComputeWindowHeightScale(GetEffectiveUiScaleFactor(), RenderScaling, isMacOS);
            MinWidth = ComputeMinimumWindowWidth(isMacOS) * widthScale;
            MinHeight = ComputeMinimumWindowHeight(isMacOS) * heightScale;
            if (WindowState == WindowState.Normal)
            {
                Width = Math.Max(MinWidth, persistedSize.Width);
                Height = Math.Max(MinHeight, persistedSize.Height);
            }

            _lastAppliedWindowWidthScale = widthScale;
            _lastAppliedWindowHeightScale = heightScale;
            _hasAppliedUiScaleToWindowBounds = true;
            FitToCurrentScreenWorkingArea();
            Program.RecordStartupStage(
                "MainWindow.WindowPlacement.Restore",
                FormattableString.Invariant($"size={Width:0.#}x{Height:0.#}; platform={ResolveCurrentWindowPlacementPlatformKey()}"));
            return true;
        }
        catch (Exception ex) when (App.ShouldIgnoreUnhandledException(ex))
        {
            return false;
        }
        catch (Exception ex)
        {
            Program.RecordStartupStage(
                "MainWindow.WindowPlacement.Restore.Fail",
                "Failed to restore persisted main window size.",
                ex);
            return false;
        }
    }

    private void ApplyPlatformDefaultWindowSize(bool isMacOS)
    {
        if (!isMacOS)
        {
            return;
        }

        Width = ComputeDefaultWindowWidth(isMacOS);
        Height = ComputeDefaultWindowHeight(isMacOS);
        MinWidth = ComputeMinimumWindowWidth(isMacOS);
        MinHeight = ComputeMinimumWindowHeight(isMacOS);
    }

    internal static double ComputeDefaultWindowWidth(bool isMacOS)
    {
        return (isMacOS ? MacOsDefaultWindowWidth : BaseWindowWidth)
            * (isMacOS ? MacOsDefaultWindowSizeScale : 1d);
    }

    internal static double ComputeDefaultWindowHeight(bool isMacOS)
    {
        return (isMacOS ? MacOsDefaultWindowHeight : BaseWindowHeight)
            * (isMacOS ? MacOsDefaultWindowSizeScale : 1d);
    }

    internal static double ComputeMinimumWindowWidth(bool isMacOS)
    {
        return ComputeNativeShadowCompensatedWindowSize(BaseWindowMinWidth, isMacOS);
    }

    internal static double ComputeMinimumWindowHeight(bool isMacOS)
    {
        return ComputeNativeShadowCompensatedWindowSize(BaseWindowMinHeight, isMacOS);
    }

    private static double ComputeNativeShadowCompensatedWindowSize(double baseSize, bool isMacOS)
    {
        return isMacOS
            ? Math.Max(0d, baseSize - NativeWindowShadowShellMarginCompensation)
            : baseSize;
    }

    private static double ResolveLogicalWindowSize(double scaledValue, double scale, double fallback)
    {
        if (!double.IsFinite(scaledValue) || scaledValue <= 0d || scale <= 0d)
        {
            return fallback;
        }

        return scaledValue / scale;
    }

    internal static double ConvertScreenPixelsToWindowUnits(double pixelLength, double desktopScaling)
    {
        return Math.Max(320d, pixelLength / NormalizeScaleFactor(desktopScaling));
    }

    internal static double ResolveWindowSizeTarget(
        double currentSize,
        double previousScale,
        double nextScale,
        double defaultSize,
        double minSize,
        bool preserveLogicalSize,
        bool keepMacPlatformDefaultSize)
    {
        if (keepMacPlatformDefaultSize && !preserveLogicalSize)
        {
            return Math.Max(minSize, defaultSize);
        }

        var logicalSize = preserveLogicalSize
            ? ResolveLogicalWindowSize(currentSize, previousScale, defaultSize)
            : defaultSize;
        return Math.Max(minSize, logicalSize * nextScale);
    }

    internal static bool ShouldLoadPersistedWindowSize(IReadOnlyDictionary<string, JsonNode?> globalValues)
        => ReadBooleanSetting(globalValues, LegacyConfigurationKeys.LoadWindowPlacement, defaultValue: true);

    internal static bool ShouldSavePersistedWindowSize(IReadOnlyDictionary<string, JsonNode?> globalValues)
        => ReadBooleanSetting(globalValues, LegacyConfigurationKeys.SaveWindowPlacement, defaultValue: true);

    internal static bool TryReadPersistedWindowSize(
        IReadOnlyDictionary<string, JsonNode?> globalValues,
        string platformKey,
        out Size size)
    {
        size = default;
        if (string.IsNullOrWhiteSpace(platformKey)
            || !globalValues.TryGetValue(LegacyConfigurationKeys.WindowPlacement, out var node)
            || node is not JsonObject placement)
        {
            return false;
        }

        var sizeNode = placement;
        if (placement[WindowPlacementPlatformsKey] is JsonObject platforms
            && platforms[platformKey] is JsonObject platformPlacement)
        {
            sizeNode = platformPlacement;
        }

        if (!TryReadFiniteDouble(sizeNode, WindowPlacementWidthKey, out var width)
            || !TryReadFiniteDouble(sizeNode, WindowPlacementHeightKey, out var height)
            || !TryCreatePersistedWindowSize(width, height, 0d, 0d, out size))
        {
            return false;
        }

        return true;
    }

    internal static void WritePersistedWindowSize(
        IDictionary<string, JsonNode?> globalValues,
        string platformKey,
        Size size)
    {
        if (string.IsNullOrWhiteSpace(platformKey)
            || !TryCreatePersistedWindowSize(size.Width, size.Height, 0d, 0d, out var normalizedSize))
        {
            return;
        }

        if (!globalValues.TryGetValue(LegacyConfigurationKeys.WindowPlacement, out var node)
            || node is not JsonObject placement)
        {
            placement = [];
            globalValues[LegacyConfigurationKeys.WindowPlacement] = placement;
        }

        placement[WindowPlacementSchemaKey] = JsonValue.Create(WindowPlacementSchemaVersion);
        if (placement[WindowPlacementPlatformsKey] is not JsonObject platforms)
        {
            platforms = [];
            placement[WindowPlacementPlatformsKey] = platforms;
        }

        platforms[platformKey] = new JsonObject
        {
            [WindowPlacementWidthKey] = JsonValue.Create(Math.Round(normalizedSize.Width, 2)),
            [WindowPlacementHeightKey] = JsonValue.Create(Math.Round(normalizedSize.Height, 2)),
        };
    }

    internal static bool TryCreatePersistedWindowSize(
        double width,
        double height,
        double minWidth,
        double minHeight,
        out Size size)
    {
        size = default;
        if (!double.IsFinite(width)
            || !double.IsFinite(height)
            || width <= 0d
            || height <= 0d)
        {
            return false;
        }

        size = new Size(
            Math.Max(width, Math.Max(minWidth, MinimumPersistedWindowWidth)),
            Math.Max(height, Math.Max(minHeight, MinimumPersistedWindowHeight)));
        return true;
    }

    internal static double ComputeWindowWidthScale(double effectiveUiScaleFactor)
    {
        return NormalizeScaleFactor(effectiveUiScaleFactor);
    }

    internal static double ComputeWindowHeightScale(double effectiveUiScaleFactor, double renderScaling, bool isMacOS)
    {
        var uiScale = NormalizeScaleFactor(effectiveUiScaleFactor);
        if (!isMacOS)
        {
            return uiScale;
        }

        var normalizedRenderScaling = NormalizeScaleFactor(renderScaling);
        var hiDpiHeightBoost = Math.Clamp(
            1d + Math.Max(0d, normalizedRenderScaling - 1d) * MacOsHiDpiHeightBoostPerScaleStep,
            1d,
            MacOsHiDpiHeightBoostMax);
        return uiScale * hiDpiHeightBoost;
    }

    internal static bool ShouldTreatResizeAsLiveInteraction(WindowResizeReason reason)
    {
        return reason == WindowResizeReason.User;
    }

    private static double NormalizeScaleFactor(double scale)
    {
        return double.IsFinite(scale) && scale > 0d ? scale : 1d;
    }

    private static bool ReadBooleanSetting(
        IReadOnlyDictionary<string, JsonNode?> globalValues,
        string key,
        bool defaultValue)
    {
        if (!globalValues.TryGetValue(key, out var node) || node is not JsonValue value)
        {
            return defaultValue;
        }

        if (value.TryGetValue<bool>(out var boolValue))
        {
            return boolValue;
        }

        if (value.TryGetValue<string>(out var stringValue)
            && bool.TryParse(stringValue, out var parsedValue))
        {
            return parsedValue;
        }

        if (value.TryGetValue<int>(out var intValue))
        {
            return intValue != 0;
        }

        return defaultValue;
    }

    private static bool TryReadFiniteDouble(JsonObject source, string key, out double value)
    {
        value = 0d;
        if (source[key] is not JsonValue jsonValue)
        {
            return false;
        }

        if (jsonValue.TryGetValue<double>(out var doubleValue))
        {
            value = doubleValue;
            return double.IsFinite(value);
        }

        if (jsonValue.TryGetValue<string>(out var stringValue)
            && double.TryParse(
                stringValue,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsedValue))
        {
            value = parsedValue;
            return double.IsFinite(value);
        }

        return false;
    }

    private static string ResolveCurrentWindowPlacementPlatformKey()
    {
        if (OperatingSystem.IsMacOS())
        {
            return "macOS";
        }

        if (OperatingSystem.IsWindows())
        {
            return "Windows";
        }

        return OperatingSystem.IsLinux() ? "Linux" : "Other";
    }

    private void RecordUiScaleDiagnostics(string stage)
    {
        try
        {
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            var workingArea = screen?.WorkingArea;
            var message = FormattableString.Invariant(
                $"stage={stage}; platform={(OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "MacOS" : "Other")}; effectiveUiScale={VM?.EffectiveUiScaleFactor ?? 1d:0.###}; renderScaling={RenderScaling:0.###}; screenScaling={screen?.Scaling ?? 0d:0.###}; clientDip={ClientSize.Width:0.#}x{ClientSize.Height:0.#}; boundsDip={Bounds.Width:0.#}x{Bounds.Height:0.#}; workingAreaPx={workingArea?.Width ?? 0}x{workingArea?.Height ?? 0}");
            Program.RecordStartupStage("MainWindow.UiScaleDiagnostics", message);
            App.ForgetTask(
                App.Runtime.DiagnosticsService.RecordEventAsync("App.Window.UiScaleDiagnostics", message),
                "App.Window.UiScaleDiagnostics");
        }
        catch (ObjectDisposedException)
        {
            // Ignore late diagnostics while the shell is shutting down.
        }
    }

    private void OnWindowShellFramePropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == AppWindowFrame.EffectiveHorizontalContentInsetProperty)
        {
            UpdateAdaptiveLayoutMode();
        }
    }

    private void BindShellBackgroundVm(MainShellViewModel? vm)
    {
        if (ReferenceEquals(_shellBackgroundVm, vm))
        {
            ApplyShellBackgroundEffect();
            return;
        }

        if (_shellBackgroundVm is not null)
        {
            _shellBackgroundVm.PropertyChanged -= OnShellBackgroundVmPropertyChanged;
        }

        _shellBackgroundVm = vm;
        if (_shellBackgroundVm is not null)
        {
            _shellBackgroundVm.PropertyChanged += OnShellBackgroundVmPropertyChanged;
        }

        ApplyShellBackgroundEffect();
    }

    private void OnShellBackgroundVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(MainShellViewModel.SelectedRootTabIndex), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(MainShellViewModel.IsTaskQueueRootTabSelected), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(MainShellViewModel.IsCopilotRootTabSelected), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(MainShellViewModel.IsToolboxRootTabSelected), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(MainShellViewModel.IsSettingsRootTabSelected), StringComparison.Ordinal))
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                UpdateAdaptiveLayoutMode();
            }
            else
            {
                App.PostUiCallback("MainWindow.AdaptiveLayout.Update", () => UpdateAdaptiveLayoutMode(), DispatcherPriority.Render);
            }

            return;
        }

        if (string.Equals(e.PropertyName, nameof(MainShellViewModel.EffectiveUiScaleFactor), StringComparison.Ordinal))
        {
            ApplyUiScaleToWindowBounds(preserveLogicalSize: true);
            UpdateAdaptiveLayoutMode(flushAllHosts: true);
            RecordUiScaleDiagnostics("settings-applied");
            return;
        }

        if (!string.Equals(e.PropertyName, nameof(MainShellViewModel.ShellBackgroundBlur), StringComparison.Ordinal)
            && !string.Equals(e.PropertyName, nameof(MainShellViewModel.ShellBackgroundImage), StringComparison.Ordinal)
            && !string.Equals(e.PropertyName, nameof(MainShellViewModel.HasShellBackgroundImage), StringComparison.Ordinal)
            && !string.Equals(e.PropertyName, nameof(MainShellViewModel.ShellBackgroundOpacity), StringComparison.Ordinal))
        {
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyShellBackgroundEffect();
            return;
        }

        App.PostUiCallback("MainWindow.ShellBackground.ApplyEffect", ApplyShellBackgroundEffect, DispatcherPriority.Render);
    }

    private void ApplyShellBackgroundEffect()
    {
        var vm = _shellBackgroundVm;
        if (vm is null
            || !vm.HasShellBackgroundImage
            || _isLiveResizing
            || vm.ShellBackgroundBlur <= 0
            || vm.ShellBackgroundOpacity <= 0d)
        {
            if (ShellBackgroundImageHost.Effect is not null)
            {
                ShellBackgroundImageHost.Effect = null;
            }

            return;
        }

        _shellBackgroundBlurEffect ??= new BlurEffect();
        _shellBackgroundBlurEffect.Radius = vm.ShellBackgroundBlur;
        if (!ReferenceEquals(ShellBackgroundImageHost.Effect, _shellBackgroundBlurEffect))
        {
            ShellBackgroundImageHost.Effect = _shellBackgroundBlurEffect;
        }
    }

    private void InvalidateLocalizedLayoutTree()
    {
        InvalidateMeasure();
        InvalidateArrange();

        foreach (var control in this.GetVisualDescendants().OfType<Control>())
        {
            control.InvalidateMeasure();
            control.InvalidateArrange();
        }
    }

    private void BindSettingsWarmup(MainShellViewModel vm)
    {
        if (ReferenceEquals(_settingsWarmupRootPage, vm.SettingsRootPage))
        {
            TryStartSettingsWarmup(vm);
            return;
        }

        UnbindSettingsWarmup();
        _settingsWarmupRootPage = vm.SettingsRootPage;
        _settingsWarmupRootPage.PropertyChanged += OnSettingsWarmupRootPagePropertyChanged;
        TryStartSettingsWarmup(vm);
    }

    private void UnbindSettingsWarmup()
    {
        if (_settingsWarmupRootPage is null)
        {
            return;
        }

        _settingsWarmupRootPage.PropertyChanged -= OnSettingsWarmupRootPagePropertyChanged;
        _settingsWarmupRootPage = null;
        _settingsWarmupStarted = false;
        _settingsSectionWarmupStarted = false;
    }

    private void OnSettingsWarmupRootPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(RootPageHostViewModel.LoadState), StringComparison.Ordinal)
            && !string.Equals(e.PropertyName, nameof(RootPageHostViewModel.IsLoaded), StringComparison.Ordinal)
            && !string.Equals(e.PropertyName, nameof(RootPageHostViewModel.PageContent), StringComparison.Ordinal))
        {
            return;
        }

        if (VM is { } vm)
        {
            TryStartSettingsWarmup(vm);
        }
    }

    private void TryStartSettingsWarmup(MainShellViewModel vm)
    {
        if (!_settingsSectionWarmupStarted)
        {
            _settingsSectionWarmupStarted = true;
            MAAUnified.App.Features.Root.SettingsView.StartBackgroundSectionWarmup();
        }

        if (_settingsWarmupRootPage?.IsLoaded != true)
        {
            return;
        }

        if (_settingsWarmupStarted)
        {
            return;
        }

        _settingsWarmupStarted = true;
        App.ForgetTask(WarmupSettingsPageAsync(vm), "MainWindow.Settings.Warmup");
    }

    private static async Task WarmupSettingsPageAsync(MainShellViewModel vm)
    {
        try
        {
            await vm.SettingsPage.WarmupDeferredSectionDataAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await App.Runtime.DiagnosticsService.RecordErrorAsync(
                "App.Settings.Warmup",
                "Settings page background warmup failed.",
                ex);
        }
    }

    private Task MinimizeFromStartupAsync(CancellationToken cancellationToken)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            WindowState = WindowState.Minimized;
            return Task.CompletedTask;
        }

        return Dispatcher.UIThread.InvokeAsync(
            () => WindowState = WindowState.Minimized,
            DispatcherPriority.Background,
            cancellationToken).GetTask();
    }

    private async void OnConnectClick(object? sender, RoutedEventArgs e)
        => await App.RunUiTaskAsync("MainWindow.ConnectClick", async () =>
    {
        if (VM is not null)
        {
            await VM.ExecuteConnectAsync();
        }
    });

    private async void OnImportClick(object? sender, RoutedEventArgs e)
        => await App.RunUiTaskAsync("MainWindow.ImportClick", async () =>
    {
        if (VM is not null)
        {
            await VM.ExecuteManualImportAsync();
        }
    });

    private async void OnStartClick(object? sender, RoutedEventArgs e)
        => await App.RunUiTaskAsync(
            "MainWindow.StartClick",
            () => DispatchTrayCommandAsync(TrayCommandId.Start, "window-shell-menu"));

    private async void OnStopClick(object? sender, RoutedEventArgs e)
        => await App.RunUiTaskAsync(
            "MainWindow.StopClick",
            () => DispatchTrayCommandAsync(TrayCommandId.Stop, "window-shell-menu"));

    private void OnDismissAchievementToastClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button button && button.Tag is string toastId)
        {
            VM?.DismissAchievementToast(toastId);
        }
    }

    private async void OnManualUpdateClick(object? sender, RoutedEventArgs e)
        => await App.RunUiTaskAsync("MainWindow.ManualUpdateClick", async () =>
    {
        e.Handled = true;
        if (VM is not null)
        {
            await VM.SettingsPage.CheckVersionUpdateAsync();
        }
    });

    private async void OnManualUpdateResourceClick(object? sender, RoutedEventArgs e)
        => await App.RunUiTaskAsync("MainWindow.ManualUpdateResourceClick", async () =>
    {
        e.Handled = true;
        if (VM is not null)
        {
            await VM.SettingsPage.ManualUpdateResourceAsync();
        }
    });

    private void OnDismissWindowUpdateClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        VM?.DismissWindowUpdateOverlay();
    }

    private async void OnAchievementToastTapped(object? sender, TappedEventArgs e)
        => await App.RunUiTaskAsync("MainWindow.AchievementToastTapped", async () =>
    {
        if (e.Handled
            || sender is not Control { DataContext: AchievementToastItemViewModel toast }
            || VM is null
            || IsEventFromButton(e.Source))
        {
            return;
        }

        e.Handled = true;
        await VM.ShowAchievementListDialogFromToastAsync(toast.Id);
    });

    private void OnWindowUpdateOverlayTapped(object? sender, TappedEventArgs e)
    {
        if (e.Handled || VM is null || IsEventFromButton(e.Source))
        {
            return;
        }

        e.Handled = true;
        VM.OpenVersionUpdateSectionFromWindowOverlay();
    }

    private void OnFloatingOverlayCloseButtonTapped(object? sender, TappedEventArgs e)
    {
        e.Handled = true;
    }

    private void OnAchievementToastPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Control { DataContext: AchievementToastItemViewModel toast })
        {
            toast.PauseCloseCountdown();
        }
    }

    private void OnAchievementToastPointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is Control { DataContext: AchievementToastItemViewModel toast })
        {
            toast.ResumeCloseCountdown();
        }
    }

    private void OnAchievementToastAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        QueueAchievementToastPresentation(sender as Control);
    }

    private void OnAchievementToastDataContextChanged(object? sender, EventArgs e)
    {
        QueueAchievementToastPresentation(sender as Control);
    }

    private void OnAchievementToastDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control control)
        {
            _pendingAchievementToastPresentationControls.Remove(control);
        }
    }

    private void QueueAchievementToastPresentation(Control? control)
    {
        if (control is null)
        {
            return;
        }

        _pendingAchievementToastPresentationControls.Add(control);
        TryStartPendingAchievementToastPresentations();
    }

    private void TryStartPendingAchievementToastPresentations()
    {
        if (AvaloniaDialogService.HasActiveOwnerModal)
        {
            return;
        }

        foreach (var control in _pendingAchievementToastPresentationControls.ToArray())
        {
            if (control.GetVisualRoot() is null)
            {
                _pendingAchievementToastPresentationControls.Remove(control);
                continue;
            }

            if (control.DataContext is not AchievementToastItemViewModel toast)
            {
                continue;
            }

            toast.StartPresentation();
            _pendingAchievementToastPresentationControls.Remove(control);
        }
    }

    private async void OnSwitchLanguageToClick(object? sender, RoutedEventArgs e)
        => await App.RunUiTaskAsync("MainWindow.SwitchLanguageClick", async () =>
    {
        if (VM is null)
        {
            return;
        }

        var targetLanguage = (sender as MenuItem)?.Tag as string;
        if (string.IsNullOrWhiteSpace(targetLanguage))
        {
            return;
        }

        await VM.ExecuteTrayLanguageSwitchAsync(targetLanguage, "window-shell-menu");
    });

    private async void OnForceShowClick(object? sender, RoutedEventArgs e)
        => await App.RunUiTaskAsync(
            "MainWindow.ForceShowClick",
            () => DispatchTrayCommandAsync(TrayCommandId.ForceShow, "window-shell-menu"));

    private async void OnHideTrayClick(object? sender, RoutedEventArgs e)
        => await App.RunUiTaskAsync(
            "MainWindow.HideTrayClick",
            () => DispatchTrayCommandAsync(TrayCommandId.HideTray, "window-shell-menu"));

    private async void OnToggleOverlayClick(object? sender, RoutedEventArgs e)
        => await App.RunUiTaskAsync(
            "MainWindow.ToggleOverlayClick",
            () => DispatchTrayCommandAsync(TrayCommandId.ToggleOverlay, "window-shell-menu"));

    private async void OnWindowOverlayToggleClick(object? sender, RoutedEventArgs e)
        => await App.RunUiTaskAsync("MainWindow.WindowOverlayToggleClick", async () =>
    {
        if (VM is null)
        {
            return;
        }

        if (VM.IsCopilotRootTabSelected)
        {
            await VM.ToggleOverlayFromCopilotAsync();
            return;
        }

        await VM.ToggleOverlayFromTaskQueueAsync();
    });

    private async void OnWindowOverlayButtonPointerPressed(object? sender, PointerPressedEventArgs e)
        => await App.RunUiTaskAsync("MainWindow.WindowOverlayButtonPointerPressed", async () =>
    {
        if (VM is null || sender is not Control control)
        {
            return;
        }

        if (!PointerPressedGestures.IsSecondaryClick(control, e))
        {
            return;
        }

        e.Handled = true;
        if (VM.IsCopilotRootTabSelected)
        {
            await VM.PickOverlayTargetFromCopilotAsync();
            return;
        }

        await VM.PickOverlayTargetFromTaskQueueAsync();
    });

    private async void OnRestartClick(object? sender, RoutedEventArgs e)
        => await App.RunUiTaskAsync(
            "MainWindow.RestartClick",
            () => DispatchTrayCommandAsync(TrayCommandId.Restart, "window-shell-menu"));

    private async void OnExitClick(object? sender, RoutedEventArgs e)
        => await App.RunUiTaskAsync(
            "MainWindow.ExitClick",
            () => DispatchTrayCommandAsync(TrayCommandId.Exit, "window-shell-menu"));

    private void OnToggleTopMostClick(object? sender, RoutedEventArgs e)
    {
        if (VM is null)
        {
            return;
        }

        VM.IsWindowTopMost = !VM.IsWindowTopMost;
    }

    public void OpenRuntimeLogWindow()
    {
        if (!global::MAAUnified.Platform.MaaUnifiedBuildFlavor.ExposesDeveloperTools || VM is null)
        {
            return;
        }

        if (_runtimeLogWindow is null)
        {
            _runtimeLogWindow = new RuntimeLogWindow
            {
                DataContext = VM,
            };
            _runtimeLogWindow.Closed += OnRuntimeLogWindowClosed;
            DialogWindowScaling.ApplyOwnerUiScale(_runtimeLogWindow, this);
            _runtimeLogWindow.Show(this);
            return;
        }

        if (_runtimeLogWindow.WindowState == WindowState.Minimized)
        {
            _runtimeLogWindow.WindowState = WindowState.Normal;
        }

        _runtimeLogWindow.Activate();
    }

    private static bool IsEventFromButton(object? source)
    {
        if (source is Button)
        {
            return true;
        }

        return source is Visual visual && visual.FindAncestorOfType<Button>() is not null;
    }

    private void OnRuntimeLogWindowClosed(object? sender, EventArgs e)
    {
        if (_runtimeLogWindow is not null)
        {
            _runtimeLogWindow.Closed -= OnRuntimeLogWindowClosed;
            _runtimeLogWindow = null;
        }
    }

    private async Task EnsureOverlayHostBoundAsync(CancellationToken cancellationToken = default)
    {
        if (VM is null || _overlayHostWindow is not null)
        {
            return;
        }

        var overlayHostWindow = new OverlayHostWindow
        {
            DataContext = VM.OverlayPresentation,
        };
        _overlayHostWindow = overlayHostWindow;
        overlayHostWindow.Show();
        var platformHandle = overlayHostWindow.TryGetPlatformHandle();
        var handle = platformHandle?.Handle ?? nint.Zero;
        if (handle == nint.Zero)
        {
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Loaded);
            platformHandle = overlayHostWindow.TryGetPlatformHandle();
            handle = platformHandle?.Handle ?? nint.Zero;
        }

        if (handle == nint.Zero)
        {
            VM.PushGrowl(PlatformCapabilityTextMap.GetUiText(
                VM.CurrentShellLanguage,
                "Ui.OverlayHostUnavailable",
                "Overlay host handle unavailable.",
                VM.ReportLocalizationFallback));
            await App.Runtime.DiagnosticsService.RecordFailedResultAsync(
                "PlatformCapability.Overlay.BindHost",
                UiOperationResult.Fail(PlatformErrorCodes.OverlayHostNotBound, "Overlay host handle unavailable."),
                cancellationToken);
            return;
        }

        if (OperatingSystem.IsLinux()
            && !string.Equals(platformHandle?.HandleDescriptor, "XID", StringComparison.OrdinalIgnoreCase))
        {
            var descriptor = string.IsNullOrWhiteSpace(platformHandle?.HandleDescriptor)
                ? "<empty>"
                : platformHandle.HandleDescriptor;
            var message = $"Linux overlay host requires an X11 XID handle, but Avalonia returned '{descriptor}'.";
            VM.PushGrowl(message);
            await App.Runtime.DiagnosticsService.RecordFailedResultAsync(
                "PlatformCapability.Overlay.BindHost",
                UiOperationResult.Fail(PlatformErrorCodes.OverlayHostNotBound, message),
                cancellationToken);
            return;
        }

        var result = await VM.PlatformCapabilityService.BindOverlayHostAsync(
            handle,
            clickThrough: true,
            opacity: 0.85,
            cancellationToken);
        await HandlePlatformResultAsync("PlatformCapability.Overlay.BindHost", result, cancellationToken);
    }

    private void OnPlatformOverlayStateChanged(object? sender, OverlayStateChangedEvent e)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyOverlayHostState(e);
            return;
        }

        App.PostUiCallback(
            "MainWindow.OverlayStateChanged.Apply",
            () => ApplyOverlayHostState(e));
    }

    private void ApplyOverlayHostState(OverlayStateChangedEvent e)
    {
        if (_overlayHostWindow is null)
        {
            return;
        }

        _overlayHostWindow.SetOverlayActive(e.Visible, e.Mode);
        if (!e.Visible || e.Mode != OverlayRuntimeMode.Preview)
        {
            return;
        }

        try
        {
            var screens = _overlayHostWindow.Screens;
            PixelRect? anchorBounds = null;
            if (OperatingSystem.IsMacOS()
                && !string.Equals(e.TargetId, "preview", StringComparison.OrdinalIgnoreCase)
                && MacOverlayCapabilityService.TryGetTargetBounds(e.TargetId, out var targetBounds))
            {
                anchorBounds = targetBounds;
            }

            var screen = screens.ScreenFromWindow(this)
                ?? screens.ScreenFromWindow(_overlayHostWindow)
                ?? screens.Primary;
            if (screen is null)
            {
                return;
            }

            _overlayHostWindow.ApplyPreviewBounds(screen.WorkingArea, anchorBounds);
        }
        catch (ObjectDisposedException)
        {
            // Ignore late overlay events while the shell is shutting down.
        }
    }

    private async void OnTrayCommandInvoked(object? sender, TrayCommandEvent e)
        => await App.RunUiTaskAsync(
            "MainWindow.TrayCommandInvoked",
            () => DispatchTrayCommandAsync(e.Command, e.Source));

    private void OnTrayMenuRequested(object? sender, TrayMenuRequestEvent e)
    {
        App.PostUiCallback(
            "MainWindow.TrayMenuRequested",
            () => ShowTrayContextMenu(e));
    }

    private async Task DispatchTrayCommandAsync(
        TrayCommandId command,
        string source,
        CancellationToken cancellationToken = default)
    {
        if (VM is null)
        {
            return;
        }

        try
        {
            if (command is TrayCommandId.Exit or TrayCommandId.Restart)
            {
                var confirmScope = command == TrayCommandId.Restart
                    ? "App.Shell.Tray.Restart.Confirm"
                    : "App.Shell.Tray.Exit.Confirm";
                if (!await ConfirmCloseAsync(confirmScope, cancellationToken))
                {
                    await App.Runtime.DiagnosticsService.RecordEventAsync(
                        confirmScope,
                        $"source={source}; cancelled",
                        cancellationToken);
                    return;
                }

                ArmCloseCompletionWatchdog($"{confirmScope}.Watchdog");
                if (!await CompleteConfigurationSavesBeforeCloseAsync(
                        $"{confirmScope}.ConfigSave",
                        cancellationToken))
                {
                    DisarmCloseCompletionWatchdog();
                    return;
                }
            }

            var action = await VM.ExecuteTrayCommandAsync(command, source, cancellationToken);
            switch (action)
            {
                case ShellUiAction.None:
                    break;
                case ShellUiAction.ShowMainWindow:
                    ShowAndActivateMainWindow();
                    break;
                case ShellUiAction.CloseMainWindow:
                    var exitScope = command == TrayCommandId.Restart
                        ? "App.Shell.Tray.Restart.Exit"
                        : "App.Shell.Tray.Exit";
                    ArmCloseCompletionWatchdog($"{exitScope}.Watchdog");
                    _ = await ExitApplicationAsync(exitScope, cancellationToken);
                    break;
                default:
                    break;
            }
        }
        catch (Exception ex)
        {
            await App.Runtime.DiagnosticsService.RecordErrorAsync(
                "PlatformCapability.TrayCommand",
                $"Tray command execution failed. command={command} source={source}",
                ex);
        }
    }

    private void ShowTrayContextMenu(TrayMenuRequestEvent e)
    {
        if (VM is null)
        {
            return;
        }

        CloseTrayContextMenu();

        var popup = new TrayContextMenuWindow
        {
            CommandSource = e.Source,
        };
        popup.SetEntries(BuildTrayContextMenuEntries());
        popup.CommandInvoked += OnTrayContextMenuCommandInvoked;
        popup.Closed += OnTrayContextMenuClosed;
        DialogWindowScaling.ApplyOwnerUiScale(popup, this);
        popup.OpenAt(new PixelPoint(e.ScreenX, e.ScreenY));
        _trayContextMenuWindow = popup;
    }

    private IReadOnlyList<TrayContextMenuEntry> BuildTrayContextMenuEntries()
    {
        var vm = VM;
        if (vm is null)
        {
            return Array.Empty<TrayContextMenuEntry>();
        }

        var text = PlatformCapabilityTextMap.CreateTrayMenuText(vm.CurrentShellLanguage, vm.ReportLocalizationFallback);
        return
        [
            new TrayContextMenuItemEntry(text.Start, TrayCommandId.Start, vm.CanStartExecution),
            new TrayContextMenuItemEntry(text.Stop, TrayCommandId.Stop, vm.CanStopExecution),
            new TrayContextMenuSeparatorEntry(),
            new TrayContextMenuItemEntry(text.ForceShow, TrayCommandId.ForceShow, true),
            new TrayContextMenuItemEntry(text.HideTray, TrayCommandId.HideTray, true),
            new TrayContextMenuItemEntry(text.ToggleOverlay, TrayCommandId.ToggleOverlay, true),
            new TrayContextMenuItemEntry(text.SwitchLanguage, TrayCommandId.SwitchLanguage, true),
            new TrayContextMenuItemEntry(text.Restart, TrayCommandId.Restart, true),
            new TrayContextMenuSeparatorEntry(),
            new TrayContextMenuItemEntry(text.Exit, TrayCommandId.Exit, true),
        ];
    }

    private async void OnTrayContextMenuCommandInvoked(object? sender, TrayContextMenuCommandInvokedEventArgs e)
        => await App.RunUiTaskAsync("MainWindow.TrayPopupCommand", async () =>
    {
        var source = _trayContextMenuWindow?.CommandSource ?? "tray-popup";
        CloseTrayContextMenu();
        await DispatchTrayCommandAsync(e.Command, source);
    });

    private void OnTrayContextMenuClosed(object? sender, EventArgs e)
    {
        var popup = _trayContextMenuWindow;
        if (!ReferenceEquals(sender, popup) || popup is null)
        {
            return;
        }

        popup.CommandInvoked -= OnTrayContextMenuCommandInvoked;
        popup.Closed -= OnTrayContextMenuClosed;
        _trayContextMenuWindow = null;
    }

    private void CloseTrayContextMenu()
    {
        if (_trayContextMenuWindow is null)
        {
            return;
        }

        var popup = _trayContextMenuWindow;
        _trayContextMenuWindow = null;
        popup.CommandInvoked -= OnTrayContextMenuCommandInvoked;
        popup.Closed -= OnTrayContextMenuClosed;
        popup.Close();
    }

    private async void OnGlobalHotkeyTriggered(object? sender, GlobalHotkeyTriggeredEvent e)
        => await App.RunUiTaskAsync("MainWindow.GlobalHotkeyTriggered", async () =>
    {
        if (VM is null)
        {
            return;
        }

        try
        {
            if (string.Equals(e.Name, "ShowGui", StringComparison.OrdinalIgnoreCase))
            {
                ShowAndActivateMainWindow();
                return;
            }

            if (string.Equals(e.Name, "LinkStart", StringComparison.OrdinalIgnoreCase))
            {
                await DispatchTrayCommandAsync(TrayCommandId.Start, "hotkey-linkstart");
            }
        }
        catch (Exception ex)
        {
            await App.Runtime.DiagnosticsService.RecordErrorAsync(
                "PlatformCapability.HotkeyTriggered",
                "Global hotkey execution failed.",
                ex);
        }
    });

    private async Task HandleMinimizeToTrayAsync(CancellationToken cancellationToken = default)
    {
        var vm = VM;
        if (_processingMinimizeToTray
            || vm is null
            || WindowState != WindowState.Minimized
            || !vm.SettingsPage.UseTray
            || !vm.SettingsPage.MinimizeToTray)
        {
            return;
        }

        try
        {
            _processingMinimizeToTray = true;
            var trayVisible = await vm.PlatformCapabilityService.SetTrayVisibleAsync(true, cancellationToken);
            await HandlePlatformResultAsync("PlatformCapability.Tray.MinimizeToTray", trayVisible, cancellationToken);
            WindowState = WindowState.Normal;
            Hide();
        }
        finally
        {
            _processingMinimizeToTray = false;
            UpdateAchievementToastVisibility();
        }
    }

    private void ShowAndActivateMainWindow()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        FitToCurrentScreenWorkingArea();
        UpdateAdaptiveLayoutMode();
        Activate();
    }

    private void FitToCurrentScreenWorkingArea()
    {
        try
        {
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            if (screen is null)
            {
                return;
            }

            var scale = NormalizeScaleFactor(DesktopScaling);
            var workingArea = screen.WorkingArea;
            var availableWidth = ConvertScreenPixelsToWindowUnits(workingArea.Width, scale);
            var availableHeight = ConvertScreenPixelsToWindowUnits(workingArea.Height, scale);

            MinWidth = Math.Min(MinWidth, availableWidth);
            MinHeight = Math.Min(MinHeight, availableHeight);
            Width = Math.Min(Width, availableWidth);
            Height = Math.Min(Height, availableHeight);

            var widthPx = (int)Math.Round(Width * scale);
            var heightPx = (int)Math.Round(Height * scale);
            var minX = workingArea.X;
            var minY = workingArea.Y;
            var maxX = workingArea.X + Math.Max(0, workingArea.Width - widthPx);
            var maxY = workingArea.Y + Math.Max(0, workingArea.Height - heightPx);
            Position = new PixelPoint(
                Math.Clamp(Position.X, minX, maxX),
                Math.Clamp(Position.Y, minY, maxY));
        }
        catch (ObjectDisposedException)
        {
            // Ignore late window size adjustments while the shell is shutting down.
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || VM is null)
        {
            return;
        }

        var capture = HotkeyGestureCodec.Capture(e.Key, e.KeyModifiers);
        if (capture.Kind != HotkeyCaptureResultKind.Captured || capture.Gesture is null)
        {
            return;
        }

        if (VM.PlatformCapabilityService.TryDispatchWindowScopedHotkey(capture.Gesture))
        {
            e.Handled = true;
        }
    }

    private HotkeyHostContext BuildHotkeyHostContext()
    {
        var platformHandle = TryGetPlatformHandle();
        var nativeHandle = platformHandle?.Handle ?? nint.Zero;
        var descriptor = platformHandle?.HandleDescriptor ?? string.Empty;
        var parentWindowIdentifier = string.Empty;

        if (OperatingSystem.IsLinux()
            && nativeHandle != nint.Zero
            && descriptor.Equals("XID", StringComparison.OrdinalIgnoreCase))
        {
            parentWindowIdentifier = $"x11:{nativeHandle.ToInt64():x}";
        }

        var sessionType = OperatingSystem.IsWindows()
            ? "windows"
            : OperatingSystem.IsMacOS()
                ? "macos"
                : OperatingSystem.IsLinux()
                    ? LinuxDesktopSessionDetector.Detect().ToString().ToLowerInvariant()
                    : "unknown";
        return new HotkeyHostContext(nativeHandle, parentWindowIdentifier, sessionType);
    }

    private Task<bool> ConfirmCloseAsync(string sourceScope, CancellationToken cancellationToken = default)
    {
        var vm = VM;
        if (vm is null)
        {
            return Task.FromResult(true);
        }

        return _closeConfirmationService.ConfirmCloseAsync(
            vm.RootTexts,
            vm.CurrentShellLanguage,
            vm.TaskQueuePage.IsRunning,
            vm.SettingsPage.IsVersionUpdateActionRunning,
            sourceScope,
            cancellationToken);
    }

    private async Task<bool> CompleteConfigurationSavesBeforeCloseAsync(
        string sourceScope,
        CancellationToken cancellationToken = default)
    {
        var vm = VM;
        if (vm is null)
        {
            return true;
        }

        Program.RecordStartupStage($"{sourceScope}.Begin", "Starting close-time configuration flush.");
        await PersistWindowSizeBeforeCloseAsync($"{sourceScope}.WindowPlacement", cancellationToken);
        Program.RecordStartupStage($"{sourceScope}.WindowPlacement.End", "Close-time window placement persistence completed.");

        var tracker = ConfigurationSaveTracker.Instance;
        var waitingNames = tracker.HasActiveSaves
            ? tracker.ActiveDisplayNames
            : tracker.PendingOrFailedDisplayNames;
        if (waitingNames.Count > 0)
        {
            Program.RecordStartupStage(
                $"{sourceScope}.PendingBeforeWait",
                $"Close-time save tracker has pending entries: {string.Join(", ", waitingNames)}");
        }

        IReadOnlyList<string> failedNames;
        Program.RecordStartupStage($"{sourceScope}.WaitForActive.Begin", "Waiting for close-time active saves to finish.");
        await tracker.WaitForActiveSavesAsync(cancellationToken);
        Program.RecordStartupStage($"{sourceScope}.WaitForActive.End", "Close-time active saves finished.");
        Program.RecordStartupStage($"{sourceScope}.Flush.Begin", "Flushing close-time configuration saves.");
        failedNames = await vm.FlushConfigurationSavesForCloseAsync(cancellationToken);
        Program.RecordStartupStage(
            $"{sourceScope}.Flush.End",
            $"Close-time configuration flush returned failedCount={failedNames.Count}.");

        failedNames = ConfigurationSaveTracker.Instance.FailedDisplayNames;
        if (failedNames.Count == 0)
        {
            Program.RecordStartupStage($"{sourceScope}.Complete", "Close-time configuration save gate completed successfully.");
            return true;
        }

        Program.RecordStartupStage(
            $"{sourceScope}.Failed",
            $"Close-time configuration save gate completed with failedCount={failedNames.Count}.");
        await App.Runtime.DialogFeatureService.ReportErrorAsync(
            sourceScope,
            MAAUnified.Application.Models.UiOperationResult.Fail(
                MAAUnified.Application.Models.UiErrorCode.SettingsSaveFailed,
                $"{JoinChineseNames(failedNames)}保存失败"),
            cancellationToken);
        return false;
    }

    private void CaptureLastNormalWindowSize()
    {
        if (WindowState != WindowState.Normal)
        {
            return;
        }

        if (TryCaptureCurrentWindowSize(out var size))
        {
            _lastNormalWindowSize = size;
        }
    }

    private bool TryCaptureCurrentWindowSize(out Size size)
    {
        var width = Bounds.Width > 0d ? Bounds.Width : Width;
        var height = Bounds.Height > 0d ? Bounds.Height : Height;
        return TryCreatePersistedWindowSize(width, height, MinWidth, MinHeight, out size);
    }

    private async Task PersistWindowSizeBeforeCloseAsync(
        string sourceScope,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!_hasAppliedOpenedWindowBounds)
            {
                return;
            }

            var config = App.Runtime.ConfigurationService.CurrentConfig;
            if (!ShouldSavePersistedWindowSize(config.GlobalValues))
            {
                return;
            }

            var size = WindowState == WindowState.Normal && TryCaptureCurrentWindowSize(out var currentSize)
                ? currentSize
                : _lastNormalWindowSize;
            if (size is null)
            {
                return;
            }

            WritePersistedWindowSize(
                config.GlobalValues,
                ResolveCurrentWindowPlacementPlatformKey(),
                size.Value);
            await App.Runtime.ConfigurationService.SaveAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (App.ShouldIgnoreUnhandledException(ex))
        {
        }
        catch (Exception ex)
        {
            await App.Runtime.DiagnosticsService.RecordErrorAsync(
                sourceScope,
                "Failed to persist main window size before close.",
                ex,
                CancellationToken.None);
        }
    }

    private static string JoinChineseNames(IReadOnlyList<string> names)
    {
        var cleanNames = names
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return cleanNames.Length switch
        {
            0 => "配置",
            1 => cleanNames[0],
            2 => $"{cleanNames[0]}和{cleanNames[1]}",
            _ => string.Concat(string.Join("、", cleanNames.Take(cleanNames.Length - 1)), "和", cleanNames[^1]),
        };
    }

    private async Task<bool> ExitApplicationAsync(string scope, CancellationToken cancellationToken = default)
    {
        var vm = VM;
        _allowLifecycleClose = true;
        Program.RecordStartupStage(scope, "Invoking AppLifecycleService.ExitAsync.");
        var result = await App.Runtime.AppLifecycleService.ExitAsync(cancellationToken);
        Program.RecordStartupStage(scope, $"AppLifecycleService.ExitAsync returned success={result.Success}.");
        if (result.Success)
        {
            return true;
        }

        _allowLifecycleClose = false;
        _closeRequestPending = false;
        DisarmCloseCompletionWatchdog();
        if (vm is not null)
        {
            vm.PushGrowl(result.Message);
        }

        await App.Runtime.DiagnosticsService.RecordFailedResultAsync(scope, result, cancellationToken);
        return false;
    }

    private void ArmCloseCompletionWatchdog(string scope)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (_closeCompletionWatchdogCts is not null)
        {
            return;
        }

        var watchdogCts = new CancellationTokenSource();
        _closeCompletionWatchdogCts = watchdogCts;
        Program.RecordStartupStage(
            scope,
            $"Close completion watchdog armed for {CloseCompletionWatchdogDelay.TotalSeconds:0} seconds.");
        App.ForgetTask(
            RunCloseCompletionWatchdogAsync(scope, watchdogCts.Token),
            $"{scope}.Task");
    }

    private void DisarmCloseCompletionWatchdog()
    {
        var watchdogCts = Interlocked.Exchange(ref _closeCompletionWatchdogCts, null);
        if (watchdogCts is null)
        {
            return;
        }

        try
        {
            watchdogCts.Cancel();
            Program.RecordStartupStage("App.Shell.Close.Watchdog", "Close completion watchdog disarmed.");
        }
        catch
        {
            // Ignore watchdog cancellation errors during shutdown.
        }
        finally
        {
            watchdogCts.Dispose();
        }
    }

    private async Task RunCloseCompletionWatchdogAsync(string scope, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(CloseCompletionWatchdogDelay, cancellationToken);
            Program.RecordStartupStage(
                scope,
                $"Close completion watchdog fired after {CloseCompletionWatchdogDelay.TotalSeconds:0} seconds.");
            Environment.Exit(0);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await App.Runtime.DiagnosticsService.RecordErrorAsync(
                scope,
                "Close completion watchdog failed.",
                ex,
                CancellationToken.None);
        }
    }

    private void OnDialogErrorRaised(object? sender, DialogErrorRaisedEvent e)
    {
        App.PostUiCallback(
            "MainWindow.DialogError.Enqueue",
            () => EnqueueDialogError(e));
    }

    private void EnqueueDialogError(DialogErrorRaisedEvent dialogError)
    {
        var key = BuildDialogErrorKey(dialogError);
        var shouldPump = false;
        lock (_dialogErrorGate)
        {
            if (!_pendingDialogErrorKeys.Add(key))
            {
                return;
            }

            _pendingDialogErrors.Enqueue(dialogError);
            if (!_processingDialogErrors && IsVisible)
            {
                _processingDialogErrors = true;
                shouldPump = true;
            }
        }

        if (shouldPump)
        {
            App.ForgetTask(
                ProcessDialogErrorQueueAsync(),
                "MainWindow.DialogError.ProcessQueue");
        }
    }

    private void BindDialogErrorEvents()
    {
        if (_dialogErrorBound)
        {
            return;
        }

        App.Runtime.DialogFeatureService.ErrorRaised += OnDialogErrorRaised;
        _dialogErrorBound = true;
    }

    private void StartDialogErrorPumpIfNeeded()
    {
        var shouldPump = false;
        lock (_dialogErrorGate)
        {
            if (_processingDialogErrors || _pendingDialogErrors.Count == 0 || !IsVisible)
            {
                return;
            }

            _processingDialogErrors = true;
            shouldPump = true;
        }

        if (shouldPump)
        {
            App.ForgetTask(
                ProcessDialogErrorQueueAsync(),
                "MainWindow.DialogError.ProcessQueue");
        }
    }

    private async Task ProcessDialogErrorQueueAsync()
    {
        while (true)
        {
            DialogErrorRaisedEvent dialogError;
            string key;
            lock (_dialogErrorGate)
            {
                if (_pendingDialogErrors.Count == 0)
                {
                    _processingDialogErrors = false;
                    return;
                }

                dialogError = _pendingDialogErrors.Dequeue();
                key = BuildDialogErrorKey(dialogError);
            }

            try
            {
                await ShowErrorDialogAsync(dialogError);
            }
            catch (Exception ex)
            {
                await App.Runtime.DiagnosticsService.RecordErrorAsync(
                    "Dialog.ErrorPopup",
                    $"Failed to show error dialog. context={dialogError.Context} code={dialogError.Result.Error?.Code ?? UiErrorCode.UiOperationFailed}",
                    ex);
            }
            finally
            {
                lock (_dialogErrorGate)
                {
                    _pendingDialogErrorKeys.Remove(key);
                }
            }
        }
    }

    private async Task ShowErrorDialogAsync(DialogErrorRaisedEvent dialogError)
    {
        var language = VM?.CurrentShellLanguage ?? UiLanguageCatalog.FallbackLanguage;
        App.Runtime.AchievementTrackerService.SetCurrentLanguage(language);
        try
        {
            var achievementResult = App.Runtime.AchievementTrackerService.Unlock("CongratulationError");
            if (!achievementResult.Success)
            {
                await App.Runtime.DiagnosticsService.RecordFailedResultAsync(
                    "MainWindow.DialogError.UnlockAchievement",
                    achievementResult);
            }
        }
        catch (Exception ex) when (App.ShouldIgnoreUnhandledException(ex))
        {
        }
        catch (Exception ex)
        {
            await App.Runtime.DiagnosticsService.RecordErrorAsync(
                "MainWindow.DialogError.UnlockAchievement",
                "Failed to unlock error dialog achievement.",
                ex);
        }

        var localizedResult = DialogTextCatalog.LocalizeErrorResult(language, dialogError.Result);
        var isConnectFailed = dialogError.Result.Error?.Code == UiErrorCode.ConnectFailed;
        var chrome = DialogTextCatalog.CreateCatalog(
            language,
            nextLanguage => new DialogChromeSnapshot(
                title: isConnectFailed
                    ? DialogTextCatalog.ErrorDialogConnectFailedTitle(nextLanguage)
                    : DialogTextCatalog.ErrorDialogTitle(nextLanguage),
                confirmText: isConnectFailed
                    ? DialogTextCatalog.WarningDialogConfirmButton(nextLanguage)
                    : DialogTextCatalog.ErrorDialogCloseButton(nextLanguage),
                cancelText: DialogTextCatalog.ErrorDialogIgnoreButton(nextLanguage),
                namedTexts: DialogTextCatalog.CreateNamedTexts(
                    (DialogTextCatalog.ChromeKeys.Prompt, isConnectFailed
                        ? DialogTextCatalog.BuildErrorSuggestion(nextLanguage, dialogError.Result)
                        : string.Empty),
                    (DialogTextCatalog.ChromeKeys.SectionTitle, DialogTextCatalog.ErrorDialogSectionTitle(nextLanguage)),
                    (DialogTextCatalog.ChromeKeys.CopyButton, isConnectFailed
                        ? DialogTextCatalog.ErrorDialogCopyErrorInfoButton(nextLanguage)
                        : DialogTextCatalog.ErrorDialogCopyButton(nextLanguage)),
                    (DialogTextCatalog.ChromeKeys.IssueReportButton, DialogTextCatalog.ErrorDialogIssueReportButton(nextLanguage)),
                    (DialogTextCatalog.ChromeKeys.TimestampLabel, DialogTextCatalog.ErrorDialogTimestampLabel(nextLanguage)),
                    (DialogTextCatalog.ChromeKeys.ContextLabel, DialogTextCatalog.ErrorDialogContextLabel(nextLanguage)),
                    (DialogTextCatalog.ChromeKeys.CodeLabel, DialogTextCatalog.ErrorDialogCodeLabel(nextLanguage)),
                    (DialogTextCatalog.ChromeKeys.MessageLabel, DialogTextCatalog.ErrorDialogMessageLabel(nextLanguage)),
                    (DialogTextCatalog.ChromeKeys.DetailsLabel, DialogTextCatalog.ErrorDialogDetailsLabel(nextLanguage)),
                    (DialogTextCatalog.ChromeKeys.SuggestionLabel, DialogTextCatalog.ErrorDialogSuggestionLabel(nextLanguage)))));
        var chromeSnapshot = chrome.GetSnapshot();
        var request = new ErrorDialogRequest(
            Title: chromeSnapshot.Title,
            Context: dialogError.Context,
            Result: localizedResult,
            Suggestion: DialogTextCatalog.BuildErrorSuggestion(language, dialogError.Result),
            ConfirmText: chromeSnapshot.ConfirmText ?? DialogTextCatalog.ErrorDialogCloseButton(language),
            CancelText: chromeSnapshot.CancelText ?? DialogTextCatalog.ErrorDialogIgnoreButton(language),
            Language: language,
            Chrome: chrome);
        Func<CancellationToken, Task<UiOperationResult>>? openIssueReportAsync = VM is null
            ? null
            : VM.SettingsPage.OpenIssueReportEntryForDialogAsync;
        await _dialogService.ShowErrorAsync(
            request,
            "MainWindow.DialogFeature.ErrorPopup",
            openIssueReportAsync,
            CancellationToken.None);
    }

    private string Localize(UiOperationResult result)
    {
        var vm = VM;
        var language = vm?.CurrentShellLanguage ?? "en-us";
        Action<LocalizationFallbackInfo>? reporter = vm is null ? null : vm.ReportLocalizationFallback;
        return PlatformCapabilityTextMap.FormatErrorCode(
            language,
            result.Error?.Code,
            result.Message,
            reporter);
    }

    private async Task HandlePlatformResultAsync(
        string scope,
        UiOperationResult result,
        CancellationToken cancellationToken = default)
    {
        if (VM is null)
        {
            return;
        }

        if (!result.Success)
        {
            VM.PushGrowl(Localize(result));
            await App.Runtime.DiagnosticsService.RecordFailedResultAsync(scope, result, cancellationToken);
            return;
        }

        if (IsFallbackMessage(result.Message))
        {
            VM.PushGrowl(result.Message);
        }
    }

    private static bool IsFallbackMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("fallback", StringComparison.OrdinalIgnoreCase)
               || message.Contains("降级", StringComparison.OrdinalIgnoreCase)
               || message.Contains("preview", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildDialogErrorKey(DialogErrorRaisedEvent dialogError)
    {
        var code = dialogError.Result.Error?.Code ?? UiErrorCode.UiOperationFailed;
        return $"{dialogError.Context}|{code}|{dialogError.Result.Message}";
    }
}
