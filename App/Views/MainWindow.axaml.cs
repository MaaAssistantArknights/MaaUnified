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

namespace MAAUnified.App.Views;

public partial class MainWindow : Window
{
    private const double CompactLayoutHeightThreshold = 720d;
    private const double ResponsiveMinWindowWidth = 1080d;
    private const double ResponsiveMarginStageEndWidth = 1160d;
    private const double ResponsiveMinPageMargin = 12d;
    private const double ResponsiveMaxPageMargin = 18d;
    private const double ResponsiveMaxLayoutWidth = 1360d;
    private const double ResponsiveMinLayoutWidth = ResponsiveMinWindowWidth - (ResponsiveMinPageMargin * 2d);
    private const double ResponsiveContentStageEndWidth = ResponsiveMarginStageEndWidth + (ResponsiveMaxLayoutWidth - ResponsiveMinLayoutWidth);
    private const int ResponsiveMarginProgressSteps = 12;
    private const int ResponsiveWidthProgressSteps = 24;
    private static readonly TimeSpan ResizeSettleDelay = TimeSpan.FromMilliseconds(120);
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
        new("MAA.Size.Settings.FieldTimerConfigWidth", 136d, 150d),
        new("MAA.Size.Settings.FieldWideWidth", 252d, 280d),
        new("MAA.Size.Settings.FieldExtraWideWidth", 306d, 340d),
        new("MAA.Size.Settings.WrapItemWidth", 198d, 220d),
        new("MAA.Size.Toolbox.DepotWrapItemWidth", 142d, 158d),
        new("MAA.Size.Toolbox.OperBoxWrapItemWidth", 133d, 148d),
        new("MAA.Size.Toolbox.ActionButtonWidth", 162d, 180d),
        new("MAA.Size.Toolbox.WarningTextMaxWidth", 504d, 560d),
        new("MAA.Size.Toolbox.FormPanelWidth", 580d, 640d),
        new("MAA.Size.Copilot.SidePanelWidth", 378d, 420d),
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
    private DispatcherTimer? _resizeSettleTimer;
    private readonly object _dialogErrorGate = new();
    private readonly Queue<DialogErrorRaisedEvent> _pendingDialogErrors = [];
    private readonly HashSet<string> _pendingDialogErrorKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<ContentControl, ResponsiveLayoutMetrics> _rootHostResponsiveMetrics = [];
    private readonly IAppDialogService _dialogService;
    private readonly ShellCloseConfirmationService _closeConfirmationService;
    private OverlayHostWindow? _overlayHostWindow;
    private RuntimeLogWindow? _runtimeLogWindow;
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
        BindShellBackgroundVm(VM);
        App.Runtime.UiLanguageCoordinator.LanguageChanged += OnUiLanguageChanged;
    }

    private MainShellViewModel? VM => DataContext as MainShellViewModel;

    private async void OnWindowClosing(object? sender, CancelEventArgs e)
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
            if (!await CompleteConfigurationSavesBeforeCloseAsync("App.Shell.Window.Close.ConfigSave"))
            {
                return;
            }

            _ = await ExitApplicationAsync("App.Shell.Window.Close.Exit");
        }
        finally
        {
            if (!_allowLifecycleClose)
            {
                _closeRequestPending = false;
            }
        }
    }

    private async void OnWindowOpened(object? sender, EventArgs e)
    {
        Program.RecordStartupStage("MainWindow.Opened", "Main window opened.");
        FitToCurrentScreenWorkingArea();
        UpdateAdaptiveLayoutMode(flushAllHosts: true);
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
        vm.PlatformCapabilityService.GlobalHotkeyTriggered += OnGlobalHotkeyTriggered;
        vm.PlatformCapabilityService.OverlayStateChanged += OnPlatformOverlayStateChanged;
        _platformBound = true;

        var hotkeyHostContext = await vm.PlatformCapabilityService.ConfigureHotkeyHostContextAsync(
            BuildHotkeyHostContext());
        await HandlePlatformResultAsync("PlatformCapability.Hotkey.ConfigureHost", hotkeyHostContext);

        var trayInit = await vm.PlatformCapabilityService.InitializeTrayAsync(
            "MaaAssistantArknights",
            PlatformCapabilityTextMap.CreateTrayMenuText(vm.CurrentShellLanguage, vm.ReportLocalizationFallback));
        await HandlePlatformResultAsync("PlatformCapability.Tray.Initialize", trayInit);

        var trayVisible = await vm.PlatformCapabilityService.SetTrayVisibleAsync(vm.SettingsPage.UseTray);
        await HandlePlatformResultAsync("PlatformCapability.Tray.InitialVisibility", trayVisible);

        await vm.RegisterHotkeysAtStartupAsync();
        try
        {
            await EnsureOverlayHostBoundAsync();
        }
        catch (Exception ex)
        {
            await App.Runtime.DiagnosticsService.RecordErrorAsync(
                "PlatformCapability.Overlay.BindHost",
                "Overlay host initialization failed during window startup.",
                ex);
        }

        await vm.ExecuteStartupLaunchBehaviorAsync(minimizeWindowAsync: MinimizeFromStartupAsync);
        vm.MarkAchievementToastStartupCompleted();
        UpdateAchievementToastVisibility();
        Program.RecordStartupStage("MainWindow.PlatformInit.End", "Platform initialization completed.");
    }

    private async void OnWindowClosed(object? sender, EventArgs e)
    {
        UpdateAchievementToastVisibility();
        BindShellBackgroundVm(null);
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

        if (VM is not null && _platformBound)
        {
            VM.PlatformCapabilityService.TrayCommandInvoked -= OnTrayCommandInvoked;
            VM.PlatformCapabilityService.GlobalHotkeyTriggered -= OnGlobalHotkeyTriggered;
            VM.PlatformCapabilityService.OverlayStateChanged -= OnPlatformOverlayStateChanged;
            _platformBound = false;
            _ = await VM.PlatformCapabilityService.UnregisterGlobalHotkeyAsync("ShowGui");
            _ = await VM.PlatformCapabilityService.UnregisterGlobalHotkeyAsync("LinkStart");
            _ = await VM.PlatformCapabilityService.ShutdownTrayAsync();
        }

        if (_overlayHostWindow is not null)
        {
            _overlayHostWindow.Close();
            _overlayHostWindow = null;
        }

        if (_runtimeLogWindow is not null)
        {
            _runtimeLogWindow.Closed -= OnRuntimeLogWindowClosed;
            _runtimeLogWindow.Close();
            _runtimeLogWindow = null;
        }

        App.Runtime.UiLanguageCoordinator.LanguageChanged -= OnUiLanguageChanged;
    }

    private async void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty || e.Property == WindowStateProperty)
        {
            UpdateAchievementToastVisibility();
        }

        if (e.Property == WindowStateProperty)
        {
            await HandleMinimizeToTrayAsync();
        }
    }

    private void UpdateAchievementToastVisibility()
    {
        VM?.SetAchievementToastWindowVisible(IsVisible && WindowState != WindowState.Minimized);
    }

    private void OnWindowResized(object? sender, WindowResizedEventArgs e)
    {
        ScheduleAdaptiveLayoutUpdate(
            ResolveResponsiveLayoutWidth(e.ClientSize.Width),
            e.ClientSize.Height,
            flushAllHosts: false,
            immediate: false);
        RestartResizeSettleTimer();
    }

    private void OnWindowDataContextChanged(object? sender, EventArgs e)
    {
        BindShellBackgroundVm(VM);
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
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RefreshLocalizedLayout, DispatcherPriority.Loaded);
            return;
        }

        RefreshLocalizedLayout();
    }

    private void RefreshLocalizedLayout()
    {
        InvalidateLocalizedLayoutTree();
        Dispatcher.UIThread.Post(InvalidateLocalizedLayoutTree, DispatcherPriority.Render);
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
        Dispatcher.UIThread.Post(
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

        return Math.Max(0d, clientWidth - WindowShellFrame.EffectiveHorizontalContentInset.Total);
    }

    private double ResolveResponsiveLayoutHeight()
    {
        return ClientSize.Height > 0d ? ClientSize.Height : Bounds.Height;
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
                Dispatcher.UIThread.Post(() => UpdateAdaptiveLayoutMode(), DispatcherPriority.Render);
            }

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

        Dispatcher.UIThread.Post(ApplyShellBackgroundEffect, DispatcherPriority.Render);
    }

    private void ApplyShellBackgroundEffect()
    {
        var vm = _shellBackgroundVm;
        if (vm is null
            || !vm.HasShellBackgroundImage
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
        _ = WarmupSettingsPageAsync(vm);
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
    {
        if (VM is not null)
        {
            await VM.ExecuteConnectAsync();
        }
    }

    private async void OnImportClick(object? sender, RoutedEventArgs e)
    {
        if (VM is not null)
        {
            await VM.ExecuteManualImportAsync();
        }
    }

    private async void OnStartClick(object? sender, RoutedEventArgs e)
    {
        await DispatchTrayCommandAsync(TrayCommandId.Start, "window-shell-menu");
    }

    private async void OnStopClick(object? sender, RoutedEventArgs e)
    {
        await DispatchTrayCommandAsync(TrayCommandId.Stop, "window-shell-menu");
    }

    private void OnDismissAchievementToastClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button button && button.Tag is string toastId)
        {
            VM?.DismissAchievementToast(toastId);
        }
    }

    private async void OnManualUpdateClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (VM is not null)
        {
            await VM.SettingsPage.CheckVersionUpdateAsync();
        }
    }

    private async void OnManualUpdateResourceClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (VM is not null)
        {
            await VM.SettingsPage.ManualUpdateResourceAsync();
        }
    }

    private void OnDismissWindowUpdateClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        VM?.DismissWindowUpdateOverlay();
    }

    private async void OnAchievementToastTapped(object? sender, TappedEventArgs e)
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
    }

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

    private async void OnSwitchLanguageToClick(object? sender, RoutedEventArgs e)
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
    }

    private async void OnForceShowClick(object? sender, RoutedEventArgs e)
    {
        await DispatchTrayCommandAsync(TrayCommandId.ForceShow, "window-shell-menu");
    }

    private async void OnHideTrayClick(object? sender, RoutedEventArgs e)
    {
        await DispatchTrayCommandAsync(TrayCommandId.HideTray, "window-shell-menu");
    }

    private async void OnToggleOverlayClick(object? sender, RoutedEventArgs e)
    {
        await DispatchTrayCommandAsync(TrayCommandId.ToggleOverlay, "window-shell-menu");
    }

    private async void OnWindowOverlayToggleClick(object? sender, RoutedEventArgs e)
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
    }

    private async void OnWindowOverlayButtonPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (VM is null || sender is not Control control)
        {
            return;
        }

        var point = e.GetCurrentPoint(control);
        if (!point.Properties.IsRightButtonPressed)
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
    }

    private async void OnRestartClick(object? sender, RoutedEventArgs e)
    {
        await DispatchTrayCommandAsync(TrayCommandId.Restart, "window-shell-menu");
    }

    private async void OnExitClick(object? sender, RoutedEventArgs e)
    {
        await DispatchTrayCommandAsync(TrayCommandId.Exit, "window-shell-menu");
    }

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

        _overlayHostWindow = new OverlayHostWindow
        {
            DataContext = VM.OverlayPresentation,
        };
        _overlayHostWindow.Show();
        var platformHandle = _overlayHostWindow.TryGetPlatformHandle();
        var handle = platformHandle?.Handle ?? nint.Zero;
        if (handle == nint.Zero)
        {
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Loaded);
            platformHandle = _overlayHostWindow.TryGetPlatformHandle();
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

        Dispatcher.UIThread.Post(() => ApplyOverlayHostState(e));
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
            var screen = screens.ScreenFromWindow(this)
                ?? screens.ScreenFromWindow(_overlayHostWindow)
                ?? screens.Primary;
            if (screen is null)
            {
                return;
            }

            _overlayHostWindow.ApplyPreviewBounds(screen.WorkingArea);
        }
        catch (ObjectDisposedException)
        {
            // Ignore late overlay events while the shell is shutting down.
        }
    }

    private async void OnTrayCommandInvoked(object? sender, TrayCommandEvent e)
    {
        await DispatchTrayCommandAsync(e.Command, e.Source);
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

                if (!await CompleteConfigurationSavesBeforeCloseAsync(
                        $"{confirmScope}.ConfigSave",
                        cancellationToken))
                {
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

    private async void OnGlobalHotkeyTriggered(object? sender, GlobalHotkeyTriggeredEvent e)
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
    }

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

            var scale = Math.Max(0.01d, RenderScaling);
            var workingArea = screen.WorkingArea;
            var availableWidth = Math.Max(320d, workingArea.Width / scale);
            var availableHeight = Math.Max(320d, workingArea.Height / scale);

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

        var tracker = ConfigurationSaveTracker.Instance;
        ConfigurationSaveStatusDialogView? waitDialog = null;
        Task<bool?>? waitDialogTask = null;
        var waitingNames = tracker.HasActiveSaves
            ? tracker.ActiveDisplayNames
            : tracker.PendingOrFailedDisplayNames;
        if (waitingNames.Count > 0)
        {
            var savingText = $"{JoinChineseNames(waitingNames)}正在保存，请稍等";
            waitDialog = new ConfigurationSaveStatusDialogView();
            waitDialog.ApplyMessage("正在保存", savingText, showConfirmButton: false);
            waitDialogTask = waitDialog.ShowDialog<bool?>(this);
        }

        IReadOnlyList<string> failedNames;
        try
        {
            await tracker.WaitForActiveSavesAsync(cancellationToken);
            failedNames = await vm.FlushConfigurationSavesForCloseAsync(cancellationToken);
        }
        finally
        {
            if (waitDialog is not null)
            {
                waitDialog.Close(true);
                if (waitDialogTask is not null)
                {
                    try
                    {
                        await waitDialogTask;
                    }
                    catch (InvalidOperationException)
                    {
                        // Ignore late dialog close races while shutting down.
                    }
                }
            }
        }

        failedNames = ConfigurationSaveTracker.Instance.FailedDisplayNames;
        if (failedNames.Count == 0)
        {
            return true;
        }

        await App.Runtime.DialogFeatureService.ReportErrorAsync(
            sourceScope,
            MAAUnified.Application.Models.UiOperationResult.Fail(
                MAAUnified.Application.Models.UiErrorCode.SettingsSaveFailed,
                $"{JoinChineseNames(failedNames)}保存失败"),
            cancellationToken);
        return false;
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
        var result = await App.Runtime.AppLifecycleService.ExitAsync(cancellationToken);
        if (result.Success)
        {
            return true;
        }

        _allowLifecycleClose = false;
        _closeRequestPending = false;
        if (vm is not null)
        {
            vm.PushGrowl(result.Message);
        }

        await App.Runtime.DiagnosticsService.RecordFailedResultAsync(scope, result, cancellationToken);
        return false;
    }

    private void OnDialogErrorRaised(object? sender, DialogErrorRaisedEvent e)
    {
        Dispatcher.UIThread.Post(() => EnqueueDialogError(e));
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
            _ = ProcessDialogErrorQueueAsync();
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
            _ = ProcessDialogErrorQueueAsync();
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
        _ = App.Runtime.AchievementTrackerService.Unlock("CongratulationError");
        var localizedResult = DialogTextCatalog.LocalizeErrorResult(language, dialogError.Result);
        var chrome = DialogTextCatalog.CreateCatalog(
            language,
            nextLanguage => new DialogChromeSnapshot(
                title: DialogTextCatalog.ErrorDialogTitle(nextLanguage),
                confirmText: DialogTextCatalog.ErrorDialogCloseButton(nextLanguage),
                cancelText: DialogTextCatalog.ErrorDialogIgnoreButton(nextLanguage),
                namedTexts: DialogTextCatalog.CreateNamedTexts(
                    (DialogTextCatalog.ChromeKeys.SectionTitle, DialogTextCatalog.ErrorDialogSectionTitle(nextLanguage)),
                    (DialogTextCatalog.ChromeKeys.CopyButton, DialogTextCatalog.ErrorDialogCopyButton(nextLanguage)),
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
