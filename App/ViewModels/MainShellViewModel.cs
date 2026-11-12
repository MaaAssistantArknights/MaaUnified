using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using MAAUnified.App.Features.Dialogs;
using MAAUnified.App.Infrastructure;
using MAAUnified.App.Services;
using MAAUnified.App.ViewModels.Copilot;
using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.App.ViewModels.Settings;
using MAAUnified.App.ViewModels.TaskQueue;
using MAAUnified.App.ViewModels.Toolbox;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using MAAUnified.Application.Services.Localization;
using MAAUnified.Compat.Runtime;
using MAAUnified.CoreBridge;
using MAAUnified.Platform;
using ConfigurationKeys = MAAUnified.Compat.Constants.ConfigurationKeys;

namespace MAAUnified.App.ViewModels;

public sealed class MainShellViewModel : ObservableObject
{
    public const string AppDisplayName = "MaaAssistantArknights Unified";
    public static readonly string WindowDisplayName = MaaUnifiedBuildFlavor.BuildTitle(
        AppDisplayName,
        typeof(MainShellViewModel).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion);
    private const string DeveloperModeConfigKey = "GUI.DeveloperMode";
    private const string DefaultLogItemDateFormat = "HH:mm:ss";
    private const string WindowTitleScrollSpacer = "     ";
    private static readonly TimeSpan DeferredStartupCoreWarmupDelay = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan FloatingOverlayAnimationDuration = TimeSpan.FromMilliseconds(220);
    private readonly MAAUnifiedRuntime _runtime;
    private readonly ConnectionGameSharedStateViewModel _connectionGameSharedState;
    private readonly SemaphoreSlim _guiApplySemaphore = new(1, 1);
    private readonly HashSet<string> _reportedLocalizationFallbacks = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _localizationFallbackGate = new();
    private readonly object _secondaryPageGate = new();
    private readonly Dictionary<int, Task<bool>> _rootPageInitializationTasks = [];
    private readonly object _startupGate = new();
    private readonly object _coreWarmupGate = new();
    private readonly object _deferredCoreWarmupGate = new();
    private readonly DispatcherTimer _timerScheduleTimer;
    private readonly DispatcherTimer _windowTitleTicker;
    private readonly IAppDialogService _dialogService;
    private readonly OverlaySharedState _overlaySharedState;
    private readonly Dictionary<int, string> _timerSlotMinuteDedup = [];
    private readonly Dictionary<int, string> _timerSlotPreSwitchMinuteDedup = [];
    private readonly Queue<AchievementUnlockedEvent> _pendingAchievementToasts = [];
    private readonly TaskCompletionSource<bool> _startupAchievementToastAnnouncementGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _pendingLanguageApplyTask = Task.CompletedTask;
    private readonly CancellationTokenSource _startupCts = new();
    private readonly TaskCompletionSource<bool> _startupSnapshotReadyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _firstScreenReadyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _startupTask;
    private Task<UiOperationResult>? _coreWarmupTask;
    private Task? _deferredCoreWarmupTask;
    private CopilotPageViewModel? _copilotPage;
    private OverlayPresentationViewModel? _overlayPresentation;
    private ToolboxPageViewModel? _toolboxPage;
    private SettingsPageViewModel? _settingsPage;
    private bool _syncingConnectionState;
    private int _clientResourceReloadVersion;
    private string? _lastObservedClientType;
    private bool _sessionCallbackPumpStarted;
    private bool _startupLaunchBehaviorExecuted;
    private int _timerScheduleProcessing;
    private int _selectedRootTabIndex;
    private bool _isWindowTopMost;
    private bool _showCloseButton = true;
    private (int Current, int Max)? _taskProgress;
    private bool _isCoreReady = true;
    private string _windowTitle = WindowDisplayName;
    private string _windowVersionUpdateInfo = string.Empty;
    private string _windowResourceUpdateInfo = string.Empty;
    private bool _isWindowUpdateActionRunning;
    private string _importStatus = string.Empty;
    private string _capabilitySummary = string.Empty;
    private string _currentShellLanguage = UiLanguageCatalog.DefaultLanguage;
    private string _globalStatus = "Initializing...";
    private string _lastError = string.Empty;
    private ImportSource _selectedImportSource = ImportSource.Auto;
    private ImportSourceOptionItem? _selectedImportSourceOption;
    private bool _achievementToastWindowVisible = true;
    private bool _mainWindowBackgrounded;
    private bool _achievementToastStartupCompleted;
    private bool _startupAchievementToastReleaseStarted;
    private bool _startupAchievementToastAnnouncementGatePrepared;
    private bool _isWindowUpdateOverlayPresented;
    private bool _isWindowUpdateOverlayAnimationHidden = true;
    private bool _isShellBlockingOperationOverlayVisible;
    private bool _hasBlockingConfigIssues;
    private bool _trayHideCommandVisible = true;
    private bool _lifecycleCloseInitiated;
    private int _blockingConfigIssueCount;
    private SessionState _currentSessionState;
    private string _appliedTheme = "Light";
    private string _windowTitleSource = WindowDisplayName;
    private string _rootLogTimeFormat = DefaultLogItemDateFormat;
    private bool _windowTitleScrollable;
    private int _windowTitleScrollOffset;
    private CancellationTokenSource? _windowUpdateOverlayAnimationCts;
    private double _effectiveUiScaleFactor = ComputeEffectiveUiScaleFactor(
        StartupShellSnapshot.Default.UiScalePercent,
        OperatingSystem.IsWindows(),
        OperatingSystem.IsMacOS());
    private Bitmap? _shellBackgroundImage;
    private string? _appliedBackgroundImagePath;
    private double _shellBackgroundOpacity = 0.45;
    private int _shellBackgroundBlur = 12;
    private Stretch _shellBackgroundStretch = Stretch.UniformToFill;
    private bool _schemaMigrationNoticeShown;
    private bool _configRepairNoticeShown;
    private StartupShellSnapshot _startupSnapshot = StartupShellSnapshot.Default;
    private string _dismissedWindowUpdateOverlaySignature = string.Empty;

    public MainShellViewModel(MAAUnifiedRuntime runtime, IAppDialogService? dialogService = null)
    {
        Program.RecordStartupStage("FrameworkInit.ViewModel.MainShell.Begin", "Constructing MainShellViewModel.");
        _runtime = runtime;
        CurrentShellLanguage = StartupShellSnapshot.FromConfig(runtime.ConfigurationService.CurrentConfig).Language;
        _dialogService = dialogService ??
            (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime
                ? new AvaloniaDialogService(runtime)
                : NoOpAppDialogService.Instance);
        if (dialogService is not null
            && runtime.ConnectFeatureService is ConnectFeatureService connectFeatureService
            && connectFeatureService.MacRawByNcRiskPromptService is NoOpMacRawByNcRiskConnectionPromptService)
        {
            connectFeatureService.MacRawByNcRiskPromptService = new MacRawByNcRiskConnectionPromptService(
                _dialogService,
                runtime.UiLanguageCoordinator);
        }

        _connectionGameSharedState = new ConnectionGameSharedStateViewModel();
        _overlaySharedState = OverlaySharedStateRegistry.Get(runtime);
        _lastObservedClientType = _connectionGameSharedState.ClientType;
        _connectionGameSharedState.PropertyChanged += OnSharedConnectionStateChanged;

        RootTexts = new RootLocalizationTextMap("Root.Localization.MainShell");
        RootTexts.Language = CurrentShellLanguage;
        RootTexts.FallbackReported += ReportLocalizationFallback;
        RootTabs = new[] { "TaskQueue", "Copilot", "Toolbox", "Settings" };
        GrowlMessages = new ObservableCollection<string>();
        RootLogs = new ObservableCollection<string>();
        AchievementToasts = new ObservableCollection<AchievementToastItemViewModel>();
        ConfigIssueDetails = new ObservableCollection<ConfigIssueDetailItem>();

        ImportSourceOptions = new ObservableCollection<ImportSourceOptionItem>();
        RefreshRootTextState();

        Program.RecordStartupStage("FrameworkInit.ViewModel.MainShell.TaskQueue.Begin", "Creating TaskQueuePageViewModel.");
        TaskQueuePage = new TaskQueuePageViewModel(
            runtime,
            _connectionGameSharedState,
            ReportLocalizationFallback,
            _dialogService,
            NavigateToSettingsSection,
            EnsureCoreReadyForExecutionAsync,
            StopRunOwnerFromDialogAsync,
            gameDataReportService: null,
            stopActiveSessionOperationAsync: StopActiveSessionOperationFromTaskQueueAsync);
        Program.RecordStartupStage("FrameworkInit.ViewModel.MainShell.TaskQueue.End", "TaskQueuePageViewModel created.");
        Program.RecordStartupStage("FrameworkInit.ViewModel.MainShell.Pages.Deferred", "Secondary page view models deferred for lazy creation.");
        var (taskQueuePendingTitle, taskQueuePendingMessage) = GetRootPagePendingText(RootPageStatusKind.TaskQueue);
        var (copilotPendingTitle, copilotPendingMessage) = GetRootPagePendingText(RootPageStatusKind.Copilot);
        var (toolboxPendingTitle, toolboxPendingMessage) = GetRootPagePendingText(RootPageStatusKind.Toolbox);
        var (settingsPendingTitle, settingsPendingMessage) = GetRootPagePendingText(RootPageStatusKind.Settings);
        TaskQueueRootPage = new RootPageHostViewModel(taskQueuePendingTitle, taskQueuePendingMessage);
        CopilotRootPage = new RootPageHostViewModel(copilotPendingTitle, copilotPendingMessage);
        ToolboxRootPage = new RootPageHostViewModel(toolboxPendingTitle, toolboxPendingMessage);
        SettingsRootPage = new RootPageHostViewModel(settingsPendingTitle, settingsPendingMessage);
        RefreshRootPageHostStatusText();
        TaskQueuePage.Texts.FallbackReported += OnTaskQueueLocalizationFallbackReported;
        TaskQueuePage.PropertyChanged += OnTaskQueuePagePropertyChanged;
        _currentSessionState = runtime.SessionService.CurrentState;
        runtime.SessionService.SessionStateChanged += OnSessionStateChanged;
        runtime.PlatformCapabilityService.OverlayStateChanged += OnOverlayStateChanged;
        runtime.UiLanguageCoordinator.LanguageChanged += OnUiLanguageCoordinatorLanguageChanged;
        _timerScheduleTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _timerScheduleTimer.Tick += OnTimerScheduleTick;
        _windowTitleTicker = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(300),
        };
        _windowTitleTicker.Tick += OnWindowTitleTickerTick;

        _runtime.LogService.LogReceived += log =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                AppendRootLogEntry(log.Timestamp, $"{log.Level} {log.Message}");
            });
        };

        // WPF parity (AsstProxy.Init -> TaskQueueViewModel.AddLog, freeze 23ddd27): GPU driver
        // warnings must surface in the task queue log panel and the overlay log, not only in the
        // runtime log window.
        _runtime.ResourceWorkflowService.GpuUserNotice += message =>
        {
            Dispatcher.UIThread.Post(() => TaskQueuePage.AppendSystemLog(message, "WARN"));
        };

        // WPF parity (AsstProxy.AsstAttachWindowConnect -> TaskQueueViewModel.AddLog, freeze 23ddd27):
        // PC window binding prompts (UseAttachWindowWarning / NotFound / MultipleFound / Found /
        // AttachWindowFailed) must be visible in the task queue log panel and the overlay log.
        if (_runtime.ConnectFeatureService is ConnectFeatureService attachWindowConnectService)
        {
            attachWindowConnectService.AttachWindowUserNotice += (message, level) =>
            {
                Dispatcher.UIThread.Post(() => TaskQueuePage.AppendSystemLog(message, level));
            };
        }

        _runtime.ConfigurationService.ConfigChanged += _ =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                SyncConnectionFromProfile();
                RefreshConfigValidationState(_runtime.ConfigurationService.CurrentValidationIssues);
                RefreshWindowTitle();
            });
        };
        _runtime.AchievementTrackerService.AchievementUnlocked += OnAchievementUnlocked;
        Program.RecordStartupStage("FrameworkInit.ViewModel.MainShell.End", "MainShellViewModel constructed.");
        RefreshWindowTitle();
    }

    public IReadOnlyList<string> RootTabs { get; }

    public RootLocalizationTextMap RootTexts { get; }

    public ObservableCollection<ImportSourceOptionItem> ImportSourceOptions { get; }

    public ObservableCollection<string> GrowlMessages { get; }

    public ObservableCollection<string> RootLogs { get; }

    public ObservableCollection<AchievementToastItemViewModel> AchievementToasts { get; }

    public ObservableCollection<ConfigIssueDetailItem> ConfigIssueDetails { get; }

    public TaskQueuePageViewModel TaskQueuePage { get; }

    public CopilotPageViewModel CopilotPage => EnsureCopilotPage();

    public OverlayPresentationViewModel OverlayPresentation
        => EnsureOverlayPresentation();

    public ToolboxPageViewModel ToolboxPage => EnsureToolboxPage();

    public SettingsPageViewModel SettingsPage => EnsureSettingsPage();

    public RootPageHostViewModel TaskQueueRootPage { get; }

    public RootPageHostViewModel CopilotRootPage { get; }

    public RootPageHostViewModel ToolboxRootPage { get; }

    public RootPageHostViewModel SettingsRootPage { get; }

    public ConnectionGameSharedStateViewModel ConnectionGameSharedState => _connectionGameSharedState;

    public async Task<IReadOnlyList<string>> FlushConfigurationSavesForCloseAsync(
        CancellationToken cancellationToken = default,
        bool forProfileSwitch = false)
    {
        var failedNames = new List<string>();
        var taskQueueFlushed = forProfileSwitch
            ? await TaskQueuePage.FlushConfigurationSavesForProfileSwitchAsync(cancellationToken)
            : await TaskQueuePage.FlushConfigurationSavesForCloseAsync(cancellationToken);
        if (!taskQueueFlushed)
        {
            failedNames.AddRange(ConfigurationSaveTracker.Instance.GetFailedDisplayNames(_runtime.DiagnosticsService));
        }

        if (TryGetSettingsPage(out var settingsPage))
        {
            if (!await settingsPage.FlushConfigurationSavesForCloseAsync(cancellationToken))
            {
                failedNames.AddRange(ConfigurationSaveTracker.Instance.GetFailedDisplayNames(_runtime.DiagnosticsService));
            }
        }

        failedNames.AddRange(await ConfigurationSaveTracker.Instance.RetryPendingOrFailedAsync(
            cancellationToken: cancellationToken,
            diagnosticsService: _runtime.DiagnosticsService));
        failedNames.AddRange(ConfigurationSaveTracker.Instance.GetFailedDisplayNames(_runtime.DiagnosticsService));
        return failedNames
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public IPlatformCapabilityService PlatformCapabilityService => _runtime.PlatformCapabilityService;

    public string CurrentShellLanguage
    {
        get => _currentShellLanguage;
        private set => SetProperty(ref _currentShellLanguage, UiLanguageCatalog.Normalize(value));
    }

    public int SelectedRootTabIndex
    {
        get => _selectedRootTabIndex;
        set
        {
            if (SetProperty(ref _selectedRootTabIndex, Math.Clamp(value, 0, RootTabs.Count - 1)))
            {
                OnPropertyChanged(nameof(IsTaskQueueRootTabSelected));
                OnPropertyChanged(nameof(IsCopilotRootTabSelected));
                OnPropertyChanged(nameof(IsToolboxRootTabSelected));
                OnPropertyChanged(nameof(IsSettingsRootTabSelected));
                OnPropertyChanged(nameof(ShowWindowOverlayButton));
                PrioritizeSelectedRootPageInitialization(_selectedRootTabIndex);
            }
        }
    }

    public bool IsTaskQueueRootTabSelected => SelectedRootTabIndex == 0;

    public bool IsCopilotRootTabSelected => SelectedRootTabIndex == 1;

    public bool IsToolboxRootTabSelected => SelectedRootTabIndex == 2;

    public bool IsSettingsRootTabSelected => SelectedRootTabIndex == 3;

    public bool ShowWindowOverlayButton => IsTaskQueueRootTabSelected || IsCopilotRootTabSelected;

    public bool IsBlockingOperationOverlayVisible =>
        _isShellBlockingOperationOverlayVisible ||
        (TryGetSettingsPage(out var settingsPage) && settingsPage.IsBlockingOperationOverlayVisible);

    public bool IsWindowTopMost
    {
        get => _isWindowTopMost;
        set => SetProperty(ref _isWindowTopMost, value);
    }

    // WPF parity (RootViewModel.ShowCloseButton): hides the main window close button when
    // the legacy GUI.HideCloseButton configuration key is enabled.
    public bool ShowCloseButton
    {
        get => _showCloseButton;
        private set => SetProperty(ref _showCloseButton, value);
    }

    // WPF parity (RootViewModel.TaskProgress): current main-task progress projected to the
    // Windows taskbar; null hides the taskbar progress bar.
    public (int Current, int Max)? TaskProgress
    {
        get => _taskProgress;
        private set => SetProperty(ref _taskProgress, value);
    }

    public double EffectiveUiScaleFactor
    {
        get => _effectiveUiScaleFactor;
        private set => SetProperty(ref _effectiveUiScaleFactor, value);
    }

    public string WindowTitle
    {
        get => _windowTitle;
        set => SetProperty(ref _windowTitle, value);
    }

    public string WindowVersionUpdateInfo
    {
        get => _windowVersionUpdateInfo;
        set
        {
            if (SetProperty(ref _windowVersionUpdateInfo, value))
            {
                OnPropertyChanged(nameof(HasWindowVersionUpdateInfo));
                OnPropertyChanged(nameof(HasWindowUpdateInfo));
                NotifyWindowUpdateOverlayVisibilityChanged();
                RefreshWindowTitle();
            }
        }
    }

    public string WindowResourceUpdateInfo
    {
        get => _windowResourceUpdateInfo;
        set
        {
            if (SetProperty(ref _windowResourceUpdateInfo, value))
            {
                OnPropertyChanged(nameof(HasWindowResourceUpdateInfo));
                OnPropertyChanged(nameof(HasWindowUpdateInfo));
                NotifyWindowUpdateOverlayVisibilityChanged();
                RefreshWindowTitle();
            }
        }
    }

    public string ImportStatus
    {
        get => _importStatus;
        set => SetProperty(ref _importStatus, value);
    }

    public string CapabilitySummary
    {
        get => _capabilitySummary;
        set => SetProperty(ref _capabilitySummary, value);
    }

    public ImportSource SelectedImportSource
    {
        get => _selectedImportSource;
        set => SetProperty(ref _selectedImportSource, value);
    }

    public ImportSourceOptionItem? SelectedImportSourceOption
    {
        get => _selectedImportSourceOption;
        set
        {
            if (!SetProperty(ref _selectedImportSourceOption, value))
            {
                return;
            }

            SelectedImportSource = value?.Source ?? ImportSource.Auto;
        }
    }

    public string GlobalStatus
    {
        get => _globalStatus;
        set => SetProperty(ref _globalStatus, value);
    }

    public string LastError
    {
        get => _lastError;
        set
        {
            if (SetProperty(ref _lastError, value))
            {
                OnPropertyChanged(nameof(HasLastError));
            }
        }
    }

    public string AppliedTheme
    {
        get => _appliedTheme;
        private set => SetProperty(ref _appliedTheme, value);
    }

    public Bitmap? ShellBackgroundImage
    {
        get => _shellBackgroundImage;
        private set
        {
            if (ReferenceEquals(_shellBackgroundImage, value))
            {
                return;
            }

            var old = _shellBackgroundImage;
            _shellBackgroundImage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasShellBackgroundImage));
            old?.Dispose();
        }
    }

    public bool HasShellBackgroundImage => ShellBackgroundImage is not null;

    public double ShellBackgroundOpacity
    {
        get => _shellBackgroundOpacity;
        private set => SetProperty(ref _shellBackgroundOpacity, Math.Clamp(value, 0, 1));
    }

    public int ShellBackgroundBlur
    {
        get => _shellBackgroundBlur;
        private set => SetProperty(ref _shellBackgroundBlur, Math.Clamp(value, 0, 80));
    }

    public Stretch ShellBackgroundStretch
    {
        get => _shellBackgroundStretch;
        private set => SetProperty(ref _shellBackgroundStretch, value);
    }

    public bool HasWindowVersionUpdateInfo => !string.IsNullOrWhiteSpace(WindowVersionUpdateInfo);

    public bool HasWindowResourceUpdateInfo => !string.IsNullOrWhiteSpace(WindowResourceUpdateInfo);

    public bool HasWindowUpdateInfo => HasWindowVersionUpdateInfo || HasWindowResourceUpdateInfo;

    public bool HasVisibleWindowVersionUpdateInfo => HasWindowVersionUpdateInfo && HasVisibleWindowUpdateInfo;

    public bool HasVisibleWindowResourceUpdateInfo => HasWindowResourceUpdateInfo && HasVisibleWindowUpdateInfo;

    public bool HasMultipleVisibleWindowUpdates => HasVisibleWindowVersionUpdateInfo && HasVisibleWindowResourceUpdateInfo;

    public bool HasVisibleWindowUpdateInfo
        => HasWindowUpdateInfo
           && !string.Equals(CurrentWindowUpdateOverlaySignature, _dismissedWindowUpdateOverlaySignature, StringComparison.Ordinal);

    public bool HasMultipleWindowUpdates => HasWindowVersionUpdateInfo && HasWindowResourceUpdateInfo;

    public static double ComputeEffectiveUiScaleFactor(int uiScalePercent, bool isWindows, bool isMacOS = false)
    {
        var userScale = Math.Clamp(uiScalePercent, 70, 140) / 100d;
        var platformScale = isMacOS
            ? 0.81d
            : isWindows
                ? 0.9d
                : 1d;
        return userScale * platformScale;
    }

    public bool IsWindowUpdateOverlayPresented
    {
        get => _isWindowUpdateOverlayPresented;
        private set => SetProperty(ref _isWindowUpdateOverlayPresented, value);
    }

    public bool IsWindowUpdateOverlayAnimationHidden
    {
        get => _isWindowUpdateOverlayAnimationHidden;
        private set => SetProperty(ref _isWindowUpdateOverlayAnimationHidden, value);
    }

    public bool IsWindowUpdateActionRunning
    {
        get => _isWindowUpdateActionRunning;
        private set
        {
            if (SetProperty(ref _isWindowUpdateActionRunning, value))
            {
                OnPropertyChanged(nameof(CanTriggerWindowUpdateActions));
            }
        }
    }

    public bool CanTriggerWindowUpdateActions => !IsWindowUpdateActionRunning;

    public bool HasLastError => !string.IsNullOrWhiteSpace(LastError);

    public bool HasBlockingConfigIssues
    {
        get => _hasBlockingConfigIssues;
        private set
        {
            if (SetProperty(ref _hasBlockingConfigIssues, value))
            {
                OnPropertyChanged(nameof(CanStartExecution));
            }
        }
    }

    public int BlockingConfigIssueCount
    {
        get => _blockingConfigIssueCount;
        private set
        {
            if (SetProperty(ref _blockingConfigIssueCount, value))
            {
                OnPropertyChanged(nameof(BlockingConfigIssueSummary));
            }
        }
    }

    public string BlockingConfigIssueSummary
        => string.Format(
            CultureInfo.CurrentCulture,
            RootTexts["Main.Blocking.Title"],
            BlockingConfigIssueCount);

    public string TaskQueueTabTitle => RootTexts["Main.Tab.TaskQueue"];

    public string CopilotTabTitle => RootTexts["Main.Tab.Copilot"];

    public string ToolboxTabTitle => RootTexts["Main.Tab.Toolbox"];

    public string SettingsTabTitle => RootTexts["Main.Tab.Settings"];

    public bool IsCoreReady
    {
        get => _isCoreReady;
        private set
        {
            if (SetProperty(ref _isCoreReady, value))
            {
                OnPropertyChanged(nameof(CanStartExecution));
            }
        }
    }

    public SessionState CurrentSessionState
    {
        get => _currentSessionState;
        private set
        {
            if (SetProperty(ref _currentSessionState, value))
            {
                OnPropertyChanged(nameof(CanStartExecution));
                OnPropertyChanged(nameof(CanStopExecution));
            }
        }
    }

    // Incremented when a stop is requested while a connect is in flight. A start
    // that observes a version change across its own connect aborts instead of
    // falling through to a fresh connect+start (WPF: LinkStart checks
    // RunningState.GetStopping() after the connect and SetStopped() instead).
    private int _connectStopRequestVersion;

    public bool CanStartExecution
        => CurrentSessionState is not (SessionState.Running or SessionState.Stopping) && !HasBlockingConfigIssues;

    public bool CanStopExecution => CurrentSessionState == SessionState.Running;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        lock (_startupGate)
        {
            if (_startupTask is not null)
            {
                return _startupTask;
            }

            var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(_startupCts.Token, cancellationToken);
            _startupTask = RunStartupPipelineAsync(linkedSource);
            return _startupTask;
        }
    }

    public Task WaitForStartupSnapshotReadyAsync(CancellationToken cancellationToken = default)
        => _startupSnapshotReadyTcs.Task.WaitAsync(cancellationToken);

    public Task WaitForFirstScreenReadyAsync(CancellationToken cancellationToken = default)
        => _firstScreenReadyTcs.Task.WaitAsync(cancellationToken);

    internal async Task ExecuteStartupLaunchBehaviorAsync(
        Func<CancellationToken, Task<bool>>? startEmulatorAsync = null,
        Func<CancellationToken, Task>? startTaskQueueAsync = null,
        Func<CancellationToken, Task>? minimizeWindowAsync = null,
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task<bool>>? confirmStartupAutoRunAsync = null,
        bool? skipStartupAutoRun = null)
    {
        if (_startupLaunchBehaviorExecuted)
        {
            return;
        }

        _startupLaunchBehaviorExecuted = true;
        var snapshot = StartupLaunchBehaviorSnapshot.FromConfig(_runtime.ConfigurationService.CurrentConfig);
        if (!snapshot.RunDirectly && !snapshot.MinimizeDirectly && !snapshot.OpenEmulatorAfterLaunch)
        {
            RecordStartupPhase("LaunchBehavior.Skip", "No startup launch behavior configured.");
            return;
        }

        RecordStartupPhase(
            "LaunchBehavior.Begin",
            $"runDirectly={snapshot.RunDirectly}; minimizeDirectly={snapshot.MinimizeDirectly}; openEmulator={snapshot.OpenEmulatorAfterLaunch}; skipAutoRun={skipStartupAutoRun ?? Program.ShouldSkipStartupAutoRun}");

        try
        {
            if (snapshot.MinimizeDirectly && minimizeWindowAsync is not null)
            {
                await minimizeWindowAsync(cancellationToken);
                RecordStartupPhase("LaunchBehavior.Minimize", "Main window minimized by startup behavior.");
            }

            // WPF 60cf643a2d：更新后「立即重启」链写入 --skip-startup-auto-run，本次进程只保留最小化，
            // 不再自动开模拟器 / 自动运行任务。
            if (skipStartupAutoRun ?? Program.ShouldSkipStartupAutoRun)
            {
                RecordStartupPhase(
                    "LaunchBehavior.SkipAutoRun",
                    "Startup auto-run skipped because the update restart chain requested it.");
                return;
            }

            // WPF 60cf643a2d：会自动运行任务或开模拟器时先给倒计时确认，用户可在倒计时内取消。
            if ((snapshot.RunDirectly || snapshot.OpenEmulatorAfterLaunch)
                && snapshot.StartupAutoRunCountdownSeconds > 0
                && confirmStartupAutoRunAsync is not null)
            {
                var confirmed = await confirmStartupAutoRunAsync(cancellationToken);
                if (!confirmed)
                {
                    RecordStartupPhase(
                        "LaunchBehavior.AutoRunCancelled",
                        "Startup auto-run cancelled by the user during the countdown.");
                    return;
                }
            }

            if (snapshot.OpenEmulatorAfterLaunch)
            {
                var started = await (startEmulatorAsync ?? TaskQueuePage.TryStartEmulatorOnStartupAsync)(cancellationToken);
                RecordStartupPhase(
                    "LaunchBehavior.Emulator",
                    started
                        ? "Startup behavior launched emulator."
                        : "Startup behavior skipped emulator launch or it failed.");
            }

            if (snapshot.RunDirectly)
            {
                await (startTaskQueueAsync ?? StartAsync)(cancellationToken);
                RecordStartupPhase("LaunchBehavior.RunDirectly", "Startup behavior requested automatic task start.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            RecordStartupPhase("LaunchBehavior.Cancelled", "Startup launch behavior canceled.");
            throw;
        }
        catch (Exception ex)
        {
            RecordStartupPhase("LaunchBehavior.Fail", "Startup launch behavior failed unexpectedly.", ex);
            await RecordUnhandledExceptionAsync(
                "App.Startup.LaunchBehavior",
                ex,
                UiErrorCode.UiError,
                $"启动行为执行异常: {ex.Message}",
                cancellationToken);
        }
        finally
        {
            RecordStartupPhase("LaunchBehavior.End", "Startup launch behavior finished.");
        }
    }

    /// <summary>
    /// 启动自动运行前的倒计时确认（WPF <c>60cf643a2d</c>：10 s 反悔倒计时，倒计时结束自动确认）。
    /// 返回 <c>true</c> 表示继续自动运行，<c>false</c> 表示用户在倒计时内取消。
    /// 倒计时秒数由配置 <c>Start.StartupAutoRunCountdownSeconds</c> 决定（0 = 关闭确认）。
    /// </summary>
    internal async Task<bool> ConfirmStartupAutoRunCountdownAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = StartupLaunchBehaviorSnapshot.FromConfig(_runtime.ConfigurationService.CurrentConfig);
        var seconds = snapshot.StartupAutoRunCountdownSeconds;
        if (seconds <= 0)
        {
            return true;
        }

        var tipKey = (snapshot.RunDirectly, snapshot.OpenEmulatorAfterLaunch) switch
        {
            (true, true) => "Startup.AutoRunCountdown.TaskAndEmulator",
            (true, false) => "Startup.AutoRunCountdown.TaskOnly",
            _ => "Startup.AutoRunCountdown.EmulatorOnly",
        };
        var result = await _dialogService.ShowWarningConfirmAsync(
            new WarningConfirmDialogRequest(
                Title: RootTexts.GetOrDefault("Startup.AutoRunCountdown.Title", "Startup auto-run"),
                Message: RootTexts.GetOrDefault(
                    tipKey,
                    "MAAUnified will start automatically. Cancel to stay idle."),
                ConfirmText: RootTexts.GetOrDefault("Startup.AutoRunCountdown.Continue", "Start now"),
                CancelText: RootTexts.GetOrDefault("Startup.AutoRunCountdown.Cancel", "Cancel auto-run"),
                Language: CurrentShellLanguage,
                CountdownSeconds: seconds,
                ShowConfirmButton: false),
            "App.Startup.AutoRunCountdown",
            cancellationToken);
        return result.Return == DialogReturnSemantic.Confirm;
    }

    public void CancelStartupInitialization()
    {
        _startupCts.Cancel();
    }

    public async Task RegisterHotkeysAtStartupAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await SettingsPage.RegisterHotkeysAsync(HotkeyRegistrationSource.Startup, cancellationToken);

            if (!string.IsNullOrWhiteSpace(SettingsPage.HotkeyWarningMessage))
            {
                PushGrowl(SettingsPage.HotkeyWarningMessage);
            }

            if (!string.IsNullOrWhiteSpace(SettingsPage.HotkeyErrorMessage))
            {
                LastError = SettingsPage.HotkeyErrorMessage;
                PushGrowl(SettingsPage.HotkeyErrorMessage);
            }
        }
        catch (Exception ex)
        {
            var message = $"Startup hotkey registration failed: {ex.Message}";
            await RecordUnhandledExceptionAsync(
                "App.Shell.Hotkey.Startup",
                ex,
                UiErrorCode.HotkeyRegistrationFailed,
                message,
                cancellationToken);
        }
    }

    private async Task RunStartupPipelineAsync(CancellationTokenSource linkedSource)
    {
        using (linkedSource)
        {
            var cancellationToken = linkedSource.Token;
            try
            {
                IsCoreReady = false;
                TaskQueuePage.SetCoreAvailability(false);

                RecordStartupPhase("ConfigBootstrap.Begin", "Loading or bootstrapping config/avalonia.json.");
                UpdateStartupPhase("正在加载配置", "配置引导开始。");
                var loadResult = await _runtime.ConfigurationService.LoadOrBootstrapAsync(
                    ConfigValidationMode.Minimal,
                    cancellationToken);
                if (loadResult.ImportReport is not null)
                {
                    ImportStatus = ImportReportTextFormatter.BuildStatusMessage(loadResult.ImportReport, manualImport: false);
                }
                else if (loadResult.LoadedFromExistingConfig)
                {
                    ImportStatus = string.Empty;
                }

                await ReportValidationIssuesIfAnyAsync(
                    loadResult.ValidationIssues,
                    "Config.LoadValidation",
                    cancellationToken);
                if (!string.IsNullOrWhiteSpace(Program.RequestedConfigurationName))
                {
                    var requestedProfile = Program.RequestedConfigurationName;
                    var switchResult = await _runtime.ConfigurationProfileFeatureService
                        .SwitchProfileAsync(requestedProfile, cancellationToken);
                    if (!switchResult.Success)
                    {
                        throw new InvalidOperationException(
                            $"The requested configuration profile `{requestedProfile}` could not be selected: {switchResult.Message}");
                    }

                    RecordStartupPhase(
                        "ConfigBootstrap.CommandLineProfile",
                        $"Switched to requested configuration profile `{requestedProfile}`; restarting without command-line configuration arguments.");
                    var restartResult = await _runtime.AppLifecycleService.RestartWithoutArgumentsAsync(cancellationToken);
                    if (!restartResult.Success)
                    {
                        throw new InvalidOperationException(
                            $"Configuration profile `{requestedProfile}` was selected, but restart failed: {restartResult.Message}");
                    }

                    if (_runtime.AppLifecycleService.SupportsExit)
                    {
                        await _runtime.AppLifecycleService.ExitAsync(cancellationToken);
                    }

                    return;
                }

                await PersistPlatformHotkeyDefaultsMigrationIfNeededAsync(cancellationToken);

                ApplyDeveloperModeFromConfig();
                // WPF loads localization (`Main/Bootstrapper.cs:376` `LocalizationHelper.Load()`)
                // before `AchievementTrackerHelper.Events.Startup()` (`:605`). The tracker localizes
                // an unlock's title/description at unlock time, so set the configured language from
                // the freshly loaded config before recording the startup achievements.
                _runtime.AchievementTrackerService.SetCurrentLanguage(
                    StartupShellSnapshot.FromConfig(_runtime.ConfigurationService.CurrentConfig).Language);
                _runtime.AchievementTrackerService.RecordStartup(
                    await BuildAchievementStartupContextAsync(cancellationToken));
                RecordStartupPhase(
                    "ConfigBootstrap.End",
                    $"loadedExisting={loadResult.LoadedFromExistingConfig}; validationIssues={loadResult.ValidationIssues.Count}");

                RecordStartupPhase("StartupSnapshot.Begin", "Building startup shell snapshot.");
                UpdateStartupPhase("正在应用启动配置", "启动最小配置快照准备中。");
                _startupSnapshot = StartupShellSnapshot.FromConfig(_runtime.ConfigurationService.CurrentConfig);
                ApplyStartupShellSnapshot(_startupSnapshot);
                await SyncLanguageCoordinatorWithConfigAsync(cancellationToken);
                _runtime.AchievementTrackerService.SetCurrentLanguage(CurrentShellLanguage);
                ApplyStartupSnapshotToSettingsPageIfCreated();
                _startupSnapshotReadyTcs.TrySetResult(true);
                RecordStartupPhase(
                    "StartupSnapshot.End",
                    $"language={CurrentShellLanguage}; theme={_startupSnapshot.Theme}; useTray={_startupSnapshot.UseTray}; uiScalePercent={_startupSnapshot.UiScalePercent}; effectiveUiScale={EffectiveUiScaleFactor.ToString("0.###", CultureInfo.InvariantCulture)}");

                _runtime.LogService.Debug(
                    $"App init start: profile={_runtime.ConfigurationService.CurrentConfig.CurrentProfile}, state={_runtime.SessionService.CurrentState}");

                SyncConnectionFromProfile();
                RefreshConfigValidationState(loadResult.ValidationIssues);

                await InitializeFirstScreenAsync(loadResult.ImportReport, cancellationToken);
                await RunDeferredStartupAfterFirstScreenAsync(loadResult, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TaskQueuePage.SetCoreAvailability(false, BuildLocalizedMessage("启动已取消。", "Startup was canceled."));
                UpdateStartupPhase("启动已取消", "后台启动阶段已取消。");
                RecordStartupPhase("Cancelled", "Startup pipeline canceled.");
            }
            catch (Exception ex)
            {
                RecordStartupPhase("Fail", "Startup pipeline failed unexpectedly.", ex);
                await RecordUnhandledExceptionAsync(
                    "App.Initialize",
                    ex,
                    UiErrorCode.UiError,
                    $"初始化异常: {ex.Message}",
                    cancellationToken);

                if (!TaskQueueRootPage.IsLoaded)
                {
                    TaskQueueRootPage.MarkFailed("首屏初始化失败", "TaskQueue 首屏未能完成加载。", ex.Message);
                }

                UpdateStartupPhase("初始化异常", $"启动流程异常：{ex.Message}");
            }
            finally
            {
                _startupSnapshotReadyTcs.TrySetResult(true);
                _firstScreenReadyTcs.TrySetResult(true);
            }
        }
    }

    private async Task PersistPlatformHotkeyDefaultsMigrationIfNeededAsync(CancellationToken cancellationToken)
    {
        if (!HotkeyConfigurationCodec.ApplyPlatformDefaultsMigration(_runtime.ConfigurationService.CurrentConfig))
        {
            return;
        }

        try
        {
            await _runtime.ConfigurationService.SaveAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _runtime.LogService.Warn($"Failed to persist platform hotkey defaults migration during startup: {ex.Message}");
        }
    }

    private async Task RunDeferredStartupAfterFirstScreenAsync(
        ConfigLoadResult loadResult,
        CancellationToken cancellationToken)
    {
        RecordStartupPhase("Deferred.Begin", "Running deferred startup stages after first screen ready.");
        var strictValidationTask = RunStrictConfigValidationAsync(cancellationToken);
        var settingsLoaded = await InitializeDeferredSettingsPageAsync(cancellationToken);
        await strictValidationTask;
        if (settingsLoaded && TryGetSettingsPage(out var settingsPage))
        {
            await ApplyGuiSettingsAsync(settingsPage.CurrentGuiSnapshot, cancellationToken);
        }

        SettingsPageViewModel? startupAnnouncementSettingsPage = null;
        if (settingsLoaded && TryGetSettingsPage(out var loadedSettingsPage))
        {
            startupAnnouncementSettingsPage = loadedSettingsPage;
        }

        PrepareStartupAchievementToastAnnouncementGate(
            startupAnnouncementSettingsPage?.PrepareStartupAnnouncementCompletionTask() ?? Task.CompletedTask);

        StartBackgroundStartupDialog(
            ct => ShowSchemaMigrationNoticeIfNeededAsync(loadResult, ct),
            cancellationToken);
        StartBackgroundStartupDialog(
            ct => ShowConfigRepairNoticeIfNeededAsync(loadResult, ct),
            cancellationToken);
        if (startupAnnouncementSettingsPage is not null)
        {
            StartBackgroundStartupDialog(
                ct => RunStartupAnnouncementWorkflowAsync(startupAnnouncementSettingsPage, ct),
                cancellationToken);
        }

        await InitializeRemainingDeferredPagesAsync(cancellationToken);
        ScheduleDeferredCoreWarmupAfterStartup();
        await RefreshCapabilitySummaryAsync(cancellationToken);
        RefreshRootTextState();
        await SyncTrayMenuStateAsync(cancellationToken);
        StartTimerScheduler();
        if (settingsLoaded && TryGetSettingsPage(out var versionUpdateSettingsPage))
        {
            StartBackgroundStartupDialog(
                ct => RunStartupVersionUpdateWorkflowAsync(versionUpdateSettingsPage, ct),
                cancellationToken);
        }

        UpdateStartupPhase("启动完成", "后台页面初始化完成，核心组件将在界面就绪后继续后台预热。");
        RecordStartupPhase("Deferred.End", "Deferred startup stages completed.");
    }

    private async Task RunStrictConfigValidationAsync(CancellationToken cancellationToken)
    {
        RecordStartupPhase("ConfigValidation.Strict.Begin", "Running full config validation in deferred startup.");
        UpdateStartupPhase("正在后台校验配置", "完整配置校验已后移到后台阶段。");
        var strictIssues = _runtime.ConfigurationService.RevalidateCurrentConfig(ConfigValidationMode.Full, logIssues: true);
        RefreshConfigValidationState(strictIssues);
        await ReportValidationIssuesIfAnyAsync(strictIssues, "Config.Validation.Strict", cancellationToken);
        RecordStartupPhase("ConfigValidation.Strict.End", $"validationIssues={strictIssues.Count}");
    }

    private async Task InitializeFirstScreenAsync(ImportReport? importReport, CancellationToken cancellationToken)
    {
        RecordStartupPhase("TaskQueue.Begin", "Initializing TaskQueue first screen.");
        UpdateStartupPhase("正在初始化首屏", "TaskQueue 首屏正在初始化。");
        TaskQueueRootPage.MarkLoading("正在初始化首屏", "TaskQueue 页面正在加载。");
        try
        {
            await TaskQueuePage.InitializeFirstScreenAsync(cancellationToken);
            TaskQueueRootPage.MarkLoaded(TaskQueuePage);
            AppendImportReportToTaskQueue(importReport, manualImport: false);
            RecordStartupPhase("TaskQueue.End", "TaskQueue first screen initialized.");
            UpdateStartupPhase("首屏已就绪", "TaskQueue 首屏已可交互。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            TaskQueueRootPage.MarkFailed("首屏初始化失败", "TaskQueue 首屏初始化失败。", ex.Message);
            await RecordUnhandledExceptionAsync(
                "App.Initialize.TaskQueue",
                ex,
                UiErrorCode.UiError,
                $"TaskQueue 初始化异常: {ex.Message}",
                cancellationToken);
            RecordStartupPhase("TaskQueue.Fail", "TaskQueue first screen failed.", ex);
        }
        finally
        {
            _firstScreenReadyTcs.TrySetResult(true);
            RecordStartupPhase("FirstScreenReady", "First screen gate opened.");
        }
    }

    private async Task InitializeTaskQueueDeferredStartupAsync(CancellationToken cancellationToken)
    {
        RecordStartupPhase("TaskQueue.Deferred.Begin", "Initializing deferred TaskQueue startup state.");
        try
        {
            await TaskQueuePage.InitializeDeferredStartupAsync(cancellationToken);
            RecordStartupPhase("TaskQueue.Deferred.End", "Deferred TaskQueue startup state initialized.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await RecordUnhandledExceptionAsync(
                "App.Initialize.TaskQueue.Deferred",
                ex,
                UiErrorCode.UiError,
                $"TaskQueue 延迟初始化异常: {ex.Message}",
                cancellationToken);
            RecordStartupPhase("TaskQueue.Deferred.Fail", "Deferred TaskQueue startup state failed.", ex);
        }
    }

    private async Task<bool> InitializeCoreWarmupAsync(CancellationToken cancellationToken)
    {
        UpdateStartupPhase("正在后台初始化核心", "MaaCore 正在后台预热。");
        RecordStartupPhase("CoreWarmup.Begin", "Initializing MaaCore in background startup stage.");
        _runtime.LogService.Debug("Begin core initialization from startup pipeline.");
        var initResult = await _runtime.ResourceWorkflowService.InitializeCoreAsync(_runtime.ConfigurationService.CurrentConfig, cancellationToken);
        if (!initResult.Success)
        {
            var initCode = initResult.Error?.Code.ToString() ?? UiErrorCode.CoreUnknown;
            var initMessage = initResult.Error?.Message ?? "Core initialize failed.";
            var failureMessage = BuildCoreWarmupFailureMessage(initCode, initMessage);
            IsCoreReady = false;
            TaskQueuePage.SetCoreAvailability(false, failureMessage);
            await ApplyResultAsync(
                UiOperationResult.Fail(
                    initCode,
                    failureMessage,
                    initResult.Error?.Exception),
                "App.CoreWarmup",
                cancellationToken);
            RecordStartupPhase("CoreWarmup.Fail", $"code={initCode}; message={initMessage}");
            return false;
        }

        _runtime.LogService.Debug($"Core initialization succeeded: version={initResult.Value?.CoreVersion}");
        IsCoreReady = true;
        TaskQueuePage.SetCoreAvailability(true);
        if (!_sessionCallbackPumpStarted)
        {
            _sessionCallbackPumpStarted = true;
            _ = Task.Run(
                () => _runtime.SessionService.StartCallbackPumpAsync(
                    callback => Dispatcher.UIThread.InvokeAsync(() => ApplySessionCallback(callback)).GetTask(),
                    _startupCts.Token),
                _startupCts.Token);
        }
        RecordStartupPhase(
            "CoreWarmup.End",
            $"Core initialization succeeded. version={initResult.Value?.CoreVersion ?? "<unknown>"}");
        return true;
    }

    private async Task<bool> InitializeDeferredSettingsPageAsync(CancellationToken cancellationToken)
    {
        return await GetOrStartRootPageInitialization(3, cancellationToken);
    }

    private async Task<bool> InitializeSettingsPageCoreAsync(CancellationToken cancellationToken)
    {
        var settingsPage = EnsureSettingsPage();
        return await InitializeDeferredRootPageAsync(
            "Settings",
            "Settings",
            "Settings 页面正在后台初始化。",
            SettingsRootPage,
            ct => settingsPage.InitializeAsync(ct),
            settingsPage,
            cancellationToken);
    }

    private async Task InitializeRemainingDeferredPagesAsync(CancellationToken cancellationToken)
    {
        var taskQueueDeferredTask = InitializeTaskQueueDeferredStartupAsync(cancellationToken);
        await GetOrStartRootPageInitialization(1, cancellationToken);
        await GetOrStartRootPageInitialization(2, cancellationToken);
        await taskQueueDeferredTask;
    }

    private void PrioritizeSelectedRootPageInitialization(int selectedIndex)
    {
        if (selectedIndex is < 1 or > 3)
        {
            return;
        }

        _ = PrioritizeRootPageInitializationAsync(selectedIndex);
    }

    private async Task PrioritizeRootPageInitializationAsync(int selectedIndex)
    {
        try
        {
            await _firstScreenReadyTcs.Task.WaitAsync(_startupCts.Token);
            await GetOrStartRootPageInitialization(selectedIndex, _startupCts.Token);
        }
        catch (OperationCanceledException) when (_startupCts.IsCancellationRequested)
        {
        }
    }

    private Task<bool> GetOrStartRootPageInitialization(int pageIndex, CancellationToken cancellationToken)
    {
        lock (_secondaryPageGate)
        {
            if (_rootPageInitializationTasks.TryGetValue(pageIndex, out var existing))
            {
                return existing;
            }

            var task = pageIndex switch
            {
                1 => InitializeCopilotPageCoreAsync(cancellationToken),
                2 => InitializeToolboxPageCoreAsync(cancellationToken),
                3 => InitializeSettingsPageCoreAsync(cancellationToken),
                _ => Task.FromResult(true),
            };
            _rootPageInitializationTasks[pageIndex] = task;
            return task;
        }
    }

    private Task<bool> InitializeCopilotPageCoreAsync(CancellationToken cancellationToken)
    {
        var page = EnsureCopilotPage();
        return InitializeDeferredRootPageAsync(
            "Copilot",
            "Copilot",
            "Copilot 页面正在后台初始化。",
            CopilotRootPage,
            ct => page.InitializeAsync(ct),
            page,
            cancellationToken);
    }

    private Task<bool> InitializeToolboxPageCoreAsync(CancellationToken cancellationToken)
    {
        var page = EnsureToolboxPage();
        return InitializeDeferredRootPageAsync(
            "Toolbox",
            "Toolbox",
            "Toolbox 页面正在后台初始化。",
            ToolboxRootPage,
            ct => page.InitializeAsync(ct),
            page,
            cancellationToken);
    }

    private void ScheduleDeferredCoreWarmupAfterStartup()
    {
        lock (_deferredCoreWarmupGate)
        {
            if (_deferredCoreWarmupTask is not null || HasCoreWarmupStarted())
            {
                return;
            }

            RecordStartupPhase(
                "CoreWarmup.Deferred.Schedule",
                $"Scheduling deferred core warmup after {DeferredStartupCoreWarmupDelay.TotalMilliseconds:0}ms.");
            _deferredCoreWarmupTask = RunDeferredCoreWarmupAfterStartupAsync(_startupCts.Token);
        }
    }

    private async Task RunDeferredCoreWarmupAfterStartupAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(DeferredStartupCoreWarmupDelay, cancellationToken);

            if (HasCoreWarmupStarted())
            {
                RecordStartupPhase("CoreWarmup.Deferred.Skip", "Core warmup already started before deferred kickoff.");
                return;
            }

            RecordStartupPhase("CoreWarmup.Deferred.Dispatch", "Dispatching deferred core warmup.");
            var result = await GetOrStartCoreWarmupTask().WaitAsync(cancellationToken);
            UpdateStartupPhase(
                result.Success ? "启动完成" : "核心初始化失败",
                result.Success
                    ? "核心后台预热完成。"
                    : "核心初始化失败，但界面仍可继续浏览。");
            RecordStartupPhase("CoreWarmup.Deferred.Complete", $"Deferred core warmup finished. isCoreReady={IsCoreReady}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            RecordStartupPhase("CoreWarmup.Deferred.Cancelled", "Deferred core warmup canceled.");
        }
    }

    private Task<UiOperationResult> GetOrStartCoreWarmupTask()
    {
        lock (_coreWarmupGate)
        {
            if (_coreWarmupTask is not null)
            {
                return _coreWarmupTask;
            }

            IsCoreReady = false;
            TaskQueuePage.SetCoreAvailability(false, BuildCoreWarmupPendingMessage());
            RecordStartupPhase("CoreWarmup.Requested", "Core warmup requested by execution path.");
            _coreWarmupTask = RunCoreWarmupTaskAsync(_startupCts.Token);
            return _coreWarmupTask;
        }
    }

    private bool HasCoreWarmupStarted()
    {
        lock (_coreWarmupGate)
        {
            return _coreWarmupTask is not null;
        }
    }

    private async Task<UiOperationResult> RunCoreWarmupTaskAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await InitializeCoreWarmupAsync(cancellationToken)
                ? UiOperationResult.Ok("Core warmup completed.")
                : UiOperationResult.Fail(UiErrorCode.CoreUnknown, GetCoreUnavailableMessage());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return UiOperationResult.Fail(UiErrorCode.CoreUnknown, $"Core warmup failed unexpectedly: {ex.Message}", ex.ToString());
        }
    }

    private async Task<bool> EnsureCoreReadyForExecutionAsync(CancellationToken cancellationToken)
    {
        var result = await GetOrStartCoreWarmupTask().WaitAsync(cancellationToken);
        return result.Success;
    }

    private async Task<bool> InitializeDeferredRootPageAsync(
        string stageKey,
        string pageTitle,
        string loadingMessage,
        RootPageHostViewModel host,
        Func<CancellationToken, Task> initializeAsync,
        object pageContent,
        CancellationToken cancellationToken)
    {
        RecordStartupPhase($"{stageKey}.Begin", $"Initializing {pageTitle} page.");
        UpdateStartupPhase($"正在后台初始化 {pageTitle}", loadingMessage);
        host.MarkLoading($"正在后台初始化 {pageTitle}", loadingMessage);
        try
        {
            await initializeAsync(cancellationToken);
            host.MarkLoaded(pageContent);
            RecordStartupPhase($"{stageKey}.End", $"{pageTitle} page initialized.");
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            host.MarkFailed($"{pageTitle} 初始化失败", $"{pageTitle} 页面未能完成初始化。", ex.Message);
            await RecordUnhandledExceptionAsync(
                $"App.Initialize.{stageKey}",
                ex,
                UiErrorCode.UiError,
                $"{pageTitle} 初始化异常: {ex.Message}",
                cancellationToken);
            RecordStartupPhase($"{stageKey}.Fail", $"{pageTitle} page failed.", ex);
            return false;
        }
    }

    private void ApplyStartupShellSnapshot(StartupShellSnapshot snapshot)
    {
        var language = snapshot.Language;
        CurrentShellLanguage = language;
        AppliedTheme = snapshot.Theme;
        _rootLogTimeFormat = snapshot.LogItemDateFormatString;
        RootTexts.Language = language;
        RefreshRootTextState();
        EffectiveUiScaleFactor = ComputeEffectiveUiScaleFactor(
            snapshot.UiScalePercent,
            OperatingSystem.IsWindows(),
            OperatingSystem.IsMacOS());

        if (Avalonia.Application.Current is not null)
        {
            Avalonia.Application.Current.RequestedThemeVariant =
                string.Equals(snapshot.Theme, "Dark", StringComparison.OrdinalIgnoreCase)
                    ? ThemeVariant.Dark
                    : string.Equals(snapshot.Theme, "SyncWithOs", StringComparison.OrdinalIgnoreCase)
                        ? ThemeVariant.Default
                        : ThemeVariant.Light;
        }

        ShowCloseButton = !snapshot.HideCloseButton;
        TaskQueuePage.SetLanguage(language);
        if (TryGetCopilotPage(out var copilotPage))
        {
            copilotPage.SetLanguage(language);
        }

        if (TryGetToolboxPage(out var toolboxPage))
        {
            toolboxPage.SetLanguage(language);
        }

        RefreshRootPageHostStatusText();
        ApplyWindowTitleScrolling(snapshot.WindowTitleScrollable);
        ShowCloseButton = !snapshot.HideCloseButton;
        ShellBackgroundOpacity = snapshot.BackgroundOpacity / 100d;
        ShellBackgroundBlur = snapshot.BackgroundBlur;
        ShellBackgroundStretch = ParseStretch(snapshot.BackgroundStretchMode);
        ApplyShellBackgroundImage(snapshot.BackgroundImagePath);
        RefreshWindowTitle();
    }

    private CopilotPageViewModel EnsureCopilotPage()
    {
        lock (_secondaryPageGate)
        {
            _copilotPage ??= CreateCopilotPage();
            return _copilotPage;
        }
    }

    private ToolboxPageViewModel EnsureToolboxPage()
    {
        lock (_secondaryPageGate)
        {
            _toolboxPage ??= CreateToolboxPage();
            return _toolboxPage;
        }
    }

    private SettingsPageViewModel EnsureSettingsPage()
    {
        lock (_secondaryPageGate)
        {
            _settingsPage ??= CreateSettingsPage();
            return _settingsPage;
        }
    }

    private OverlayPresentationViewModel EnsureOverlayPresentation()
    {
        lock (_secondaryPageGate)
        {
            _copilotPage ??= CreateCopilotPage();
            _overlayPresentation ??= new OverlayPresentationViewModel(_runtime, TaskQueuePage, _copilotPage);
            return _overlayPresentation;
        }
    }

    private bool TryGetCopilotPage(out CopilotPageViewModel page)
    {
        lock (_secondaryPageGate)
        {
            if (_copilotPage is null)
            {
                page = null!;
                return false;
            }

            page = _copilotPage;
            return true;
        }
    }

    private bool TryGetToolboxPage(out ToolboxPageViewModel page)
    {
        lock (_secondaryPageGate)
        {
            if (_toolboxPage is null)
            {
                page = null!;
                return false;
            }

            page = _toolboxPage;
            return true;
        }
    }

    private bool TryGetSettingsPage(out SettingsPageViewModel page)
    {
        lock (_secondaryPageGate)
        {
            if (_settingsPage is null)
            {
                page = null!;
                return false;
            }

            page = _settingsPage;
            return true;
        }
    }

    private CopilotPageViewModel CreateCopilotPage()
    {
        var page = new CopilotPageViewModel(
            _runtime,
            _dialogService,
            StopRunOwnerFromDialogAsync,
            EnsureConnectedForSecondaryRunAsync,
            connectionState: _connectionGameSharedState,
            stopActiveSessionOperationAsync: StopActiveSessionOperationFromTaskQueueAsync);
        page.SetLanguage(CurrentShellLanguage);
        return page;
    }

    private static readonly TimeSpan ForcedScheduledStartActivityWaitTimeout = TimeSpan.FromSeconds(30);

    // Central Stop cancels an in-flight forced scheduled start (including a profile switch
    // blocked behind another session operation) so it cannot start after the block releases.
    private CancellationTokenSource? _scheduledStartCancellation;

    private async Task<IDisposable?> AcquireExclusiveSessionOperationAsync(
        string operationName,
        CancellationToken cancellationToken)
    {
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        waitCts.CancelAfter(ForcedScheduledStartActivityWaitTimeout);
        try
        {
            return await _connectionGameSharedState.AcquireExclusiveSessionOperationAsync(
                operationName,
                waitCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task StopActiveSessionOperationFromTaskQueueAsync(CancellationToken cancellationToken)
    {
        Volatile.Read(ref _scheduledStartCancellation)?.Cancel();
        var canceledLifecycle = _runtime.ConnectFeatureService.TryCancelActiveLifecycleOperation(
            "TaskQueue central Stop");
        if (!canceledLifecycle)
        {
            var currentOwner = _runtime.SessionService.CurrentRunOwner;
            if (!string.IsNullOrWhiteSpace(currentOwner))
            {
                await StopRunOwnerFromDialogAsync(currentOwner, cancellationToken);
                return;
            }
        }

        if (canceledLifecycle || _connectionGameSharedState.IsSessionOperationActive)
        {
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            waitCts.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await _connectionGameSharedState.WaitForSessionOperationsIdleAsync(waitCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _runtime.LogService.Warn(
                    "TaskQueue central Stop timed out waiting for the active session operation to settle.");
            }
        }

        var pendingOwner = _runtime.SessionService.CurrentRunOwner;
        if (!string.IsNullOrWhiteSpace(pendingOwner))
        {
            await StopRunOwnerFromDialogAsync(pendingOwner, cancellationToken);
            return;
        }

        CurrentSessionState = _runtime.SessionService.CurrentState;
        if (CurrentSessionState == SessionState.Running)
        {
            await TaskQueuePage.StopAsync(cancellationToken, userInitiated: false);
        }
        else if (CurrentSessionState is SessionState.Connecting or SessionState.Connected)
        {
            await _runtime.ConnectFeatureService.StopAsync(cancellationToken);
        }

        CurrentSessionState = _runtime.SessionService.CurrentState;
        await SyncTrayMenuStateAsync(cancellationToken);
    }

    private async Task<UiOperationResult> EnsureConnectedForSecondaryRunAsync(CancellationToken cancellationToken)
    {
        CurrentSessionState = _runtime.SessionService.CurrentState;
        if (CurrentSessionState == SessionState.Connected)
        {
            var currentConnection = BuildCurrentConnectionInfo();
            if (_runtime.SessionService.IsConnectedWith(currentConnection))
            {
                return UiOperationResult.Ok("Session already connected.");
            }

            _runtime.LogService.Info(
                $"Secondary run reconnect required because connection settings changed: current address={currentConnection.Address}, config={currentConnection.ConnectConfig}, adb={currentConnection.AdbPath ?? "<null>"}; previous address={_runtime.SessionService.LastSuccessfulConnectionInfo?.Address ?? "<null>"}, config={_runtime.SessionService.LastSuccessfulConnectionInfo?.ConnectConfig ?? "<null>"}, adb={_runtime.SessionService.LastSuccessfulConnectionInfo?.AdbPath ?? "<null>"}");
        }

        if (CurrentSessionState is SessionState.Running or SessionState.Stopping)
        {
            return UiOperationResult.Fail(
                UiErrorCode.OperationAlreadyRunning,
                "Execution is already running.");
        }

        if (!await EnsureCoreReadyForExecutionAsync(cancellationToken))
        {
            return UiOperationResult.Fail(
                UiErrorCode.SessionStateNotAllowed,
                GetCoreUnavailableMessage());
        }

        var connectResult = await ConnectWithCurrentSettingsAsync(cancellationToken);
        CurrentSessionState = _runtime.SessionService.CurrentState;
        if (connectResult.Success && CurrentSessionState == SessionState.Connected)
        {
            GlobalStatus = connectResult.Message;
            PushGrowl(connectResult.Message);
            return connectResult;
        }

        return connectResult;
    }

    private CoreConnectionInfo BuildCurrentConnectionInfo()
    {
        var effectiveAdbPath = _connectionGameSharedState.ResolveEffectiveAdbPath(updateStateWhenResolved: true);
        return _connectionGameSharedState.BuildCoreConnectionInfo(effectiveAdbPath: effectiveAdbPath);
    }

    private async Task StopRunOwnerFromDialogAsync(string owner, CancellationToken cancellationToken)
    {
        var normalizedOwner = string.IsNullOrWhiteSpace(owner) ? string.Empty : owner.Trim();
        if (string.Equals(normalizedOwner, "Toolbox", StringComparison.Ordinal))
        {
            await EnsureToolboxPage().StopActiveToolAsync(cancellationToken);
        }
        else if (string.Equals(normalizedOwner, "TaskQueue", StringComparison.Ordinal))
        {
            await TaskQueuePage.StopAsync(cancellationToken);
        }
        else if (string.Equals(normalizedOwner, "Copilot", StringComparison.Ordinal) && TryGetCopilotPage(out var copilotPage))
        {
            await copilotPage.StopAsync(cancellationToken);
        }
        else
        {
            // An owner without a UI page of its own (today the WebApi run owner) must
            // still be stoppable from the UI, otherwise every enabled Stop button
            // silently does nothing. Stopping the Core session is the common part of
            // every owner's stop route; release the owner only after the session really
            // left Running/Stopping so a failed stop cannot leave an anonymous run behind.
            await StopRunSessionDirectlyAsync(normalizedOwner, cancellationToken);
        }

        CurrentSessionState = _runtime.SessionService.CurrentState;
        var remainingOwner = _runtime.SessionService.CurrentRunOwner;
        if (!string.IsNullOrWhiteSpace(remainingOwner)
            && CurrentSessionState is not (SessionState.Running or SessionState.Stopping))
        {
            // The session already left the running states; a leftover owner would block the
            // next start with a phantom run-owner conflict.
            _runtime.SessionService.EndRun(remainingOwner);
            remainingOwner = _runtime.SessionService.CurrentRunOwner;
        }

        if (CurrentSessionState is SessionState.Running or SessionState.Stopping
            && !string.IsNullOrWhiteSpace(remainingOwner))
        {
            // A page-level stop can be a no-op when its page is not driving the session;
            // fall back to the common stop route so global Stop cannot silently do nothing.
            await StopRunSessionDirectlyAsync(remainingOwner, cancellationToken);
            CurrentSessionState = _runtime.SessionService.CurrentState;
        }

        await SyncTrayMenuStateAsync(cancellationToken);
    }

    private async Task StopRunSessionDirectlyAsync(string owner, CancellationToken cancellationToken)
    {
        if (_runtime.SessionService.CurrentState is SessionState.Running or SessionState.Stopping)
        {
            await _runtime.ConnectFeatureService.StopAsync(cancellationToken);
        }

        if (_runtime.SessionService.CurrentState is not (SessionState.Running or SessionState.Stopping))
        {
            _runtime.SessionService.EndRun(owner);
        }
    }

    private ToolboxPageViewModel CreateToolboxPage()
    {
        var page = new ToolboxPageViewModel(_runtime, _connectionGameSharedState, _dialogService, StopRunOwnerFromDialogAsync);
        page.SetLanguage(CurrentShellLanguage);
        return page;
    }

    private StartupShellSnapshot BuildLatestShellSnapshot()
    {
        var snapshot = StartupShellSnapshot.FromConfig(_runtime.ConfigurationService.CurrentConfig);
        var normalizedShellLanguage = UiLanguageCatalog.Normalize(CurrentShellLanguage);
        if (!string.Equals(snapshot.Language, normalizedShellLanguage, StringComparison.OrdinalIgnoreCase))
        {
            snapshot = snapshot with { Language = normalizedShellLanguage };
        }

        _startupSnapshot = snapshot;
        return snapshot;
    }

    private SettingsPageViewModel CreateSettingsPage()
    {
        var page = new SettingsPageViewModel(
            _runtime,
            _connectionGameSharedState,
            ReportLocalizationFallback,
            dialogService: _dialogService);
        page.BeforeConfigurationProfileSwitchAsync = async ct =>
        {
            if (await TaskQueuePage.FlushConfigurationSavesForProfileSwitchAsync(ct))
            {
                return UiOperationResult.Ok("Task queue configuration changes flushed.");
            }

            var message = string.IsNullOrWhiteSpace(TaskQueuePage.LastErrorMessage)
                ? "Failed to save task queue configuration changes before switching profiles."
                : TaskQueuePage.LastErrorMessage;
            return UiOperationResult.Fail(UiErrorCode.ConfigurationProfileSaveFailed, message);
        };
        page.GuiSettingsPreviewChanged += OnGuiSettingsPreviewChanged;
        page.GuiSettingsApplied += OnGuiSettingsApplied;
        page.ResourceVersionUpdated += OnSettingsResourceVersionUpdated;
        page.UpdateAvailabilityChanged += OnSettingsUpdateAvailabilityChanged;
        page.ApplyConfigurationContextChangedAsync = ApplySettingsConfigurationContextChangedAsync;
        page.PropertyChanged += OnSettingsPagePropertyChanged;
        page.ApplyStartupSnapshot(BuildLatestShellSnapshot());
        ApplySettingsUpdateAvailabilityState(page);

        return page;
    }

    private void ApplyStartupSnapshotToSettingsPageIfCreated()
    {
        if (TryGetSettingsPage(out var settingsPage))
        {
            settingsPage.ApplyStartupSnapshot(_startupSnapshot);
        }
    }

    private void UpdateStartupPhase(string status, string logMessage)
    {
        GlobalStatus = status;
        AppendRootLogEntrySafe($"[startup] {logMessage}");
    }

    private void AppendRootLogEntrySafe(string message)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            AppendRootLogEntry(message);
            return;
        }

        Dispatcher.UIThread.Post(() => AppendRootLogEntry(message));
    }

    private static void RecordStartupPhase(string stage, string message, Exception? exception = null)
    {
        Program.RecordStartupStage($"Stage.{stage}", message, exception);
    }

    private string BuildCoreWarmupPendingMessage()
    {
        return BuildLocalizedMessage(
            "核心初始化中，可继续浏览界面；Start/LinkStart 会在预热完成后继续执行。",
            "Core initialization is in progress. You can keep browsing the UI; Start/LinkStart will continue after warmup finishes.");
    }

    private string GetCoreUnavailableMessage()
    {
        return string.IsNullOrWhiteSpace(TaskQueuePage.CoreInitializationMessage)
            ? BuildCoreWarmupPendingMessage()
            : TaskQueuePage.CoreInitializationMessage;
    }

    private string BuildCoreWarmupFailureMessage(string initCode, string initMessage)
    {
        return BuildLocalizedMessage(
            $"Core 初始化失败: {initCode} {initMessage}",
            $"Core initialize failed: {initCode} {initMessage}");
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
        => ExecuteConnectAsync(cancellationToken);

    public async Task ExecuteConnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await EnsureCoreReadyForExecutionAsync(cancellationToken))
            {
                var pendingMessage = GetCoreUnavailableMessage();
                LastError = pendingMessage;
                PushGrowl(pendingMessage);
                await RecordFailedResultAsync(
                    "App.Shell.Connect.CoreWarmup",
                    UiOperationResult.Fail(UiErrorCode.SessionStateNotAllowed, pendingMessage),
                    cancellationToken);
                return;
            }

            var result = await ConnectWithCurrentSettingsAsync(cancellationToken);
            CurrentSessionState = _runtime.SessionService.CurrentState;
            if (!await ApplyResultAsync(result, "App.Shell.Connect", cancellationToken))
            {
                PushGrowl(result.Message);
                return;
            }

            GlobalStatus = result.Message;
            PushGrowl(result.Message);
        }
        catch (Exception ex)
        {
            await RecordUnhandledExceptionAsync(
                "App.Shell.Connect",
                ex,
                UiErrorCode.CoreUnknown,
                "Connection failed unexpectedly.",
                cancellationToken);
            PushGrowl(LastError);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        CurrentSessionState = _runtime.SessionService.CurrentState;
        var latestIssues = _runtime.ConfigurationService.RevalidateCurrentConfig();
        RefreshConfigValidationState(latestIssues);
        UiOperationResult? startConnectResult = null;

        if (CurrentSessionState is SessionState.Running or SessionState.Stopping)
        {
            await ApplyResultAsync(
                UiOperationResult.Fail(UiErrorCode.OperationAlreadyRunning, "Execution is already running."),
                "App.Shell.Start",
                cancellationToken);
            return;
        }

        if (!await EnsureCoreReadyForExecutionAsync(cancellationToken))
        {
            await ApplyResultAsync(
                UiOperationResult.Fail(UiErrorCode.SessionStateNotAllowed, GetCoreUnavailableMessage()),
                "App.Shell.Start",
                cancellationToken);
            return;
        }

        CoreConnectionInfo? requiredConnection = null;
        var shouldConnect = CurrentSessionState != SessionState.Connected;
        if (CurrentSessionState == SessionState.Connected)
        {
            var currentConnection = BuildCurrentConnectionInfo();
            if (!_runtime.SessionService.IsConnectedWith(currentConnection))
            {
                shouldConnect = true;
                requiredConnection = currentConnection;
                _runtime.LogService.Info(
                    $"Shell Start reconnect required because connection settings changed: current address={currentConnection.Address}, config={currentConnection.ConnectConfig}, adb={currentConnection.AdbPath ?? "<null>"}; previous address={_runtime.SessionService.LastSuccessfulConnectionInfo?.Address ?? "<null>"}, config={_runtime.SessionService.LastSuccessfulConnectionInfo?.ConnectConfig ?? "<null>"}, adb={_runtime.SessionService.LastSuccessfulConnectionInfo?.AdbPath ?? "<null>"}");
            }
        }

        if (shouldConnect)
        {
            var stopVersionBeforeConnect = Volatile.Read(ref _connectStopRequestVersion);
            var connectResult = await ConnectWithCurrentSettingsAsync(cancellationToken, requiredConnection);
            startConnectResult = connectResult;
            CurrentSessionState = _runtime.SessionService.CurrentState;
            if (Volatile.Read(ref _connectStopRequestVersion) != stopVersionBeforeConnect
                && CurrentSessionState != SessionState.Connected)
            {
                // WPF parity (TaskQueueViewModel.LinkStart): a stop requested while the
                // connect was in flight aborts the start here instead of falling
                // through to a fresh connect+start after the superseded connect.
                await RecordEventAsync(
                    "App.Shell.Start",
                    "start aborted: stop requested during connect",
                    cancellationToken);
                return;
            }

            if (connectResult.Success)
            {
                GlobalStatus = connectResult.Message;
                PushGrowl(connectResult.Message);
            }
        }

        if (!CanStartExecution)
        {
            if (CurrentSessionState != SessionState.Connected)
            {
                var stateMessage = startConnectResult is { Success: false } failedConnect
                    ? failedConnect.Message
                    : BuildLinkStartStateNotAllowedMessage(CurrentSessionState);

                await ApplyResultAsync(
                    UiOperationResult.Fail(UiErrorCode.ConnectFailed, stateMessage, startConnectResult?.Error?.Details),
                    "App.Shell.Start",
                    cancellationToken);
                return;
            }

            var first = _runtime.ConfigurationService.CurrentValidationIssues.FirstOrDefault(i => i.Blocking);
            var message = first is null
                ? "Config validation has blocking issues."
                : $"{first.Scope}:{first.Code}:{first.Field}:{first.Message}";
            await ApplyResultAsync(
                UiOperationResult.Fail(UiErrorCode.TaskValidationFailed, message),
                "App.Shell.Start",
                cancellationToken);
            await RecordConfigValidationFailureAsync(first, cancellationToken);
            return;
        }

        await TaskQueuePage.StartAsync(cancellationToken);
        CurrentSessionState = _runtime.SessionService.CurrentState;
        await SyncTrayMenuStateAsync(cancellationToken);
    }

    private async Task<UiOperationResult> ConnectWithCurrentSettingsAsync(
        CancellationToken cancellationToken,
        CoreConnectionInfo? requiredConnection = null)
    {
        var consent = await MacBundledAdbConsentService.EnsureAcceptedAsync(
            _runtime,
            _dialogService,
            _connectionGameSharedState.UseMacBundledAdbEffective,
            "App.Shell.Connect.MacBundledAdbConsent",
            CurrentShellLanguage,
            cancellationToken);
        if (!consent.Success)
        {
            return consent;
        }

        if (requiredConnection is not null)
        {
            _runtime.LogService.Debug(
                $"Connect required target prepared: address={requiredConnection.Address}, config={requiredConnection.ConnectConfig}, adb={requiredConnection.AdbPath ?? "<null>"}");
            return await _runtime.ConnectFeatureService.ConnectAsync(requiredConnection, cancellationToken);
        }

        var effectiveAdbPath = _connectionGameSharedState.ResolveEffectiveAdbPath(updateStateWhenResolved: true);
        var adbPath = string.IsNullOrWhiteSpace(effectiveAdbPath) ? null : effectiveAdbPath;
        var candidates = _connectionGameSharedState
            .BuildConnectAddressCandidates(includeConfiguredAddress: true)
            .Select(candidate => _connectionGameSharedState.BuildCoreConnectionInfo(
                address: candidate,
                effectiveAdbPath: adbPath,
                timeout: TimeSpan.FromSeconds(5),
                discoverRuntimeCandidates: _connectionGameSharedState.AutoDetect || _connectionGameSharedState.AlwaysAutoDetect))
            .ToList();
        _runtime.LogService.Debug(
            $"Connect candidates prepared: count={candidates.Count}, config={_connectionGameSharedState.ConnectConfig}, adb={adbPath ?? "<null>"}");

        var result = await _runtime.ConnectFeatureService.ConnectCandidatesAsync(candidates, cancellationToken);
        if (result.Success)
        {
            _runtime.LogService.Debug($"Connect candidate succeeded: {result.SuccessfulAddress}");
            if (!string.IsNullOrWhiteSpace(result.SuccessfulAddress))
            {
                _connectionGameSharedState.ConnectAddress = result.SuccessfulAddress;
            }

            if (_connectionGameSharedState.AutoDetect && !_connectionGameSharedState.AlwaysAutoDetect)
            {
                _connectionGameSharedState.AutoDetect = false;
            }

            return result.Result;
        }

        foreach (var failure in result.CandidateFailures)
        {
            _runtime.LogService.Debug(
                $"Connect candidate failed: {failure.Candidate}, code={failure.Result.Error?.Code}, message={failure.Result.Message}");
        }

        var candidateFailures = result.CandidateFailures
            .Select(static failure => new ConnectionAttemptFailure(failure.Candidate, failure.Result))
            .ToList();
        return BuildDiagnosticConnectFailureResult(
            result.Result,
            candidateFailures);
    }

    private UiOperationResult BuildDiagnosticConnectFailureResult(
        UiOperationResult connectResult,
        IReadOnlyList<ConnectionAttemptFailure>? candidateFailures = null,
        AdbCommandFailureInfo? adbCommandFailure = null)
    {
        var diagnostic = ConnectionFailureDiagnosticBuilder.Build(
            connectResult,
            _connectionGameSharedState,
            candidateFailures,
            adbCommandFailure,
            CurrentShellLanguage);
        return UiOperationResult.Fail(
            UiErrorCode.ConnectFailed,
            diagnostic.BuildDialogMessage(),
            diagnostic.Details);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default, bool userInitiated = true)
    {
        CurrentSessionState = _runtime.SessionService.CurrentState;
        if (!CanStopExecution)
        {
            await ApplyResultAsync(
                UiOperationResult.Fail(UiErrorCode.SessionStateNotAllowed, BuildStopStateNotAllowedMessage(CurrentSessionState)),
                "App.Shell.Stop",
                cancellationToken);
            return;
        }

        var runOwner = _runtime.SessionService.CurrentRunOwner;
        if (!string.IsNullOrWhiteSpace(runOwner)
            && !_runtime.SessionService.IsRunOwner("TaskQueue"))
        {
            // The tray/hotkey Stop is global: an owner without a TaskQueue run in flight
            // (Copilot, Toolbox, WebApi) must be stopped through its own route instead of
            // making TaskQueue report a run-owner conflict.
            await StopRunOwnerFromDialogAsync(runOwner, cancellationToken);
        }
        else
        {
            await TaskQueuePage.StopAsync(cancellationToken, userInitiated);
        }

        CurrentSessionState = _runtime.SessionService.CurrentState;
        await SyncTrayMenuStateAsync(cancellationToken);
    }

    public Task ManualImportAsync(CancellationToken cancellationToken = default)
        => ExecuteManualImportAsync(cancellationToken);

    public async Task ExecuteManualImportAsync(CancellationToken cancellationToken = default)
    {
        var result = await _runtime.ShellFeatureService.ImportLegacyConfigAsync(SelectedImportSource, manualImport: true, cancellationToken);
        var report = await ApplyResultAsync(result, "App.Shell.ImportLegacy", cancellationToken);
        if (report is null)
        {
            ImportStatus = result.Message;
            PushGrowl(result.Message);
            return;
        }

        ImportStatus = ImportReportTextFormatter.BuildStatusMessage(report, manualImport: true);
        GlobalStatus = ImportStatus;
        await TaskQueuePage.ReloadTasksAsync(cancellationToken, seedDefaultsWhenEmpty: true);
        await SettingsPage.InitializeAsync(cancellationToken);
        await SyncLanguageCoordinatorWithConfigAsync(cancellationToken);
        await ApplyGuiSettingsAsync(SettingsPage.CurrentGuiSnapshot, cancellationToken);
        SyncConnectionFromProfile();
        RefreshConfigValidationState(_runtime.ConfigurationService.CurrentValidationIssues);
        AppendImportReportToTaskQueue(report, manualImport: true);
        PushGrowl(ImportStatus);
        await RecordEventAsync("App.Shell.ImportLegacy.Refresh", ImportStatus, cancellationToken);
    }

    public Task SwitchLanguageCycleAsync(CancellationToken cancellationToken = default)
    {
        return SwitchLanguageCoreAsync(
            targetLanguage: null,
            successScope: "App.Shell.SwitchLanguage",
            source: null,
            cancellationToken);
    }

    public Task SwitchLanguageToAsync(string targetLanguage, CancellationToken cancellationToken = default)
    {
        return SwitchLanguageCoreAsync(
            targetLanguage,
            successScope: "App.Shell.SwitchLanguage",
            source: null,
            cancellationToken);
    }

    public async Task ExecuteSwitchLanguageAsync(string? targetLanguage = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetLanguage))
        {
            await SwitchLanguageCycleAsync(cancellationToken);
            return;
        }

        await SwitchLanguageToAsync(targetLanguage, cancellationToken);
    }

    public async Task ExecuteTrayLanguageSwitchAsync(
        string? targetLanguage,
        string source,
        CancellationToken cancellationToken = default)
    {
        await SwitchLanguageCoreAsync(
            targetLanguage,
            successScope: "App.Shell.Tray.SwitchLanguage",
            source,
            cancellationToken);
    }

    // WPF parity (frozen MaaHotKeyActionHandler.HandleLinkStart): the LinkStart
    // hotkey is a start/stop toggle - while stopping it is a no-op, while idle it
    // starts, and otherwise (connecting/running) it stops. WPF idle covers both
    // MAAUnified Idle and Connected (no run in flight). Tray menu Start/Stop keep
    // their own always-enabled, state-guarded no-op semantics; this toggle applies
    // to the hotkey dispatch path only.
    internal static TrayCommandId? ResolveLinkStartHotkeyCommand(SessionState state)
        => state switch
        {
            SessionState.Stopping => null,
            SessionState.Idle or SessionState.Connected => TrayCommandId.Start,
            _ => TrayCommandId.Stop,
        };

    public async Task<LinkStartHotkeyOutcome> ExecuteLinkStartHotkeyAsync(
        string source,
        CancellationToken cancellationToken = default)
    {
        var state = _runtime.SessionService.CurrentState;
        var command = ResolveLinkStartHotkeyCommand(state);
        if (command is null)
        {
            await RecordEventAsync(
                "App.Shell.Tray.LinkStartHotkey",
                $"source={source}; ignored-state={state}",
                cancellationToken);
            return LinkStartHotkeyOutcome.Ignored;
        }

        await ExecuteTrayCommandAsync(command.Value, source, cancellationToken);
        return command.Value == TrayCommandId.Start
            ? LinkStartHotkeyOutcome.Started
            : LinkStartHotkeyOutcome.Stopped;
    }

    public async Task<ShellUiAction> ExecuteTrayCommandAsync(
        TrayCommandId command,
        string source,
        CancellationToken cancellationToken = default,
        string? targetLanguage = null)
    {
        var scope = GetTrayCommandScope(command);
        try
        {
            switch (command)
            {
                case TrayCommandId.Start:
                    CurrentSessionState = _runtime.SessionService.CurrentState;
                    if (CurrentSessionState == SessionState.Connecting || TaskQueuePage.IsStartRequestActive)
                    {
                        // WPF parity (MaaHotKeyActionHandler.HandleLinkStart): WPF flips
                        // RunningState.SetIdle(false) the moment LinkStart() is
                        // dispatched, so pressing the LinkStart hotkey again after
                        // dispatch and before Running dispatches TaskQueueViewModel.Stop()
                        // instead of a second start. That covers both an in-flight
                        // connect and the Connected start preamble
                        // (TaskQueuePage.IsStartRequestActive), during which a second
                        // dispatch must cancel the start request instead of re-running
                        // the preamble and hitting a spurious already-running failure.
                        // An in-flight task-queue start request is canceled through the
                        // same path as the run button; any other in-flight connect is
                        // stopped through the session's Connecting stop support. The
                        // general CanStartExecution/CanStopExecution shell gating is
                        // intentionally left unchanged.
                        var canceledStartRequest = TaskQueuePage.IsStartRequestActive;
                        if (canceledStartRequest)
                        {
                            await TaskQueuePage.ToggleRunAsync(cancellationToken);
                        }
                        else
                        {
                            Interlocked.Increment(ref _connectStopRequestVersion);
                            _ = await _runtime.ConnectFeatureService.StopAsync(cancellationToken);
                        }

                        CurrentSessionState = _runtime.SessionService.CurrentState;
                        if (ShouldSendBackgroundLinkFeedback(source))
                        {
                            // WPF parity (MaaHotKeyActionHandler.HandleLinkStart):
                            // stopping from the LinkStart hotkey while the window is
                            // minimized shows the BackgroundLinkStopped toast.
                            await SendBackgroundLinkFeedbackAsync(started: false, cancellationToken);
                        }

                        await RecordEventAsync(
                            scope,
                            $"source={source}; {(canceledStartRequest ? "start-request-stop" : "connecting-stop")}",
                            cancellationToken);
                        return ShellUiAction.None;
                    }

                    if (!CanStartExecution)
                    {
                        CurrentSessionState = _runtime.SessionService.CurrentState;
                        if (CurrentSessionState == SessionState.Stopping)
                        {
                            // WPF parity (MaaHotKeyActionHandler.HandleLinkStart): while the
                            // task queue is stopping, the LinkStart hotkey is a no-op instead
                            // of a second stop request that would surface a false stop
                            // failure diagnostic (OperationAlreadyStopped).
                            await RecordEventAsync(
                                scope,
                                $"source={source}; stopping-noop",
                                cancellationToken);
                            return ShellUiAction.None;
                        }

                        if (CurrentSessionState == SessionState.Running)
                        {
                            var dispatchedStopForOwnRun = TaskQueuePage.IsOwnRunActive;
                            await TaskQueuePage.ToggleRunAsync(cancellationToken);
                            if (dispatchedStopForOwnRun && ShouldSendBackgroundLinkFeedback(source))
                            {
                                // WPF parity (MaaHotKeyActionHandler.HandleLinkStart):
                                // the LinkStart hotkey stops the queue and, while the
                                // window is minimized, shows a background toast.
                                await SendBackgroundLinkFeedbackAsync(started: false, cancellationToken);
                            }

                            await RecordEventAsync(
                                scope,
                                $"source={source}; running-dialog",
                                cancellationToken);
                            return ShellUiAction.None;
                        }

                        var blockedMessage = CurrentSessionState switch
                        {
                            SessionState.Running or SessionState.Stopping => "任务正在执行中，Start 已禁用。",
                            _ when HasBlockingConfigIssues => "存在阻断级配置错误，Start/LinkStart 已禁用。",
                            _ => BuildLinkStartStateNotAllowedMessage(CurrentSessionState),
                        };
                        PushGrowl(blockedMessage);
                        if (CurrentSessionState != SessionState.Connected && !HasBlockingConfigIssues)
                        {
                            NavigateToSettingsSection("Connect");
                        }

                        if (HasBlockingConfigIssues && ShouldSendBackgroundLinkFeedback(source))
                        {
                            // WPF parity (MaaHotKeyActionHandler.HandleLinkStart): from the
                            // idle state the LinkStart hotkey dispatches LinkStart() and, while
                            // the window is minimized, shows the BackgroundLinkStarted toast
                            // unconditionally - even when the start is then rejected by
                            // configuration validation.
                            await SendBackgroundLinkFeedbackAsync(started: true, cancellationToken);
                        }

                        await RecordEventAsync(
                            scope,
                            $"source={source}; ignored-state={CurrentSessionState}",
                            cancellationToken);
                        return ShellUiAction.None;
                    }

                    var stopVersionBeforeStart = Volatile.Read(ref _connectStopRequestVersion);
                    await StartAsync(cancellationToken);
                    CurrentSessionState = _runtime.SessionService.CurrentState;
                    if (Volatile.Read(ref _connectStopRequestVersion) == stopVersionBeforeStart)
                    {
                        PushGrowl(CurrentSessionState == SessionState.Running ? "开始执行" : "启动被阻断，请先修复错误。");
                    }
                    if (ShouldSendBackgroundLinkFeedback(source))
                    {
                        // WPF parity (MaaHotKeyActionHandler.HandleLinkStart):
                        // the LinkStart hotkey starts the queue and, while the
                        // window is minimized, shows a background toast.
                        await SendBackgroundLinkFeedbackAsync(started: true, cancellationToken);
                    }

                    await RecordEventAsync(
                        scope,
                        $"source={source}; session={CurrentSessionState}",
                        cancellationToken);
                    return ShellUiAction.None;

                case TrayCommandId.Stop:
                    CurrentSessionState = _runtime.SessionService.CurrentState;
                    // WPF ManualStop returns only when already stopping or idle; Connected
                    // (no run in flight) maps to WPF idle, so it is a no-op as well.
                    if (CurrentSessionState is SessionState.Idle or SessionState.Connected or SessionState.Stopping)
                    {
                        await RecordEventAsync(
                            scope,
                            $"source={source}; ignored-state={CurrentSessionState}",
                            cancellationToken);
                        return ShellUiAction.None;
                    }

                    if (CurrentSessionState == SessionState.Connecting)
                    {
                        // WPF ManualStop cancels an in-flight connect; the feature service
                        // supersedes the pending connect and settles the session to Idle.
                        var cancelConnectResult = await _runtime.ConnectFeatureService.StopAsync(cancellationToken);
                        CurrentSessionState = _runtime.SessionService.CurrentState;
                        await SyncTrayMenuStateAsync(cancellationToken);
                        if (!cancelConnectResult.Success)
                        {
                            LastError = cancelConnectResult.Message;
                            await RecordFailedResultAsync(scope, cancelConnectResult, cancellationToken);
                            PushGrowl(cancelConnectResult.Message);
                            return ShellUiAction.None;
                        }

                        PushGrowl("停止执行");
                        await RecordEventAsync(
                            scope,
                            $"source={source}; cancel-connecting; session={CurrentSessionState}",
                            cancellationToken);
                        return ShellUiAction.None;
                    }

                    await StopAsync(cancellationToken);
                    CurrentSessionState = _runtime.SessionService.CurrentState;
                    if (CurrentSessionState is SessionState.Running or SessionState.Stopping)
                    {
                        // Never report a successful stop while the session is still running.
                        await RecordEventAsync(
                            scope,
                            $"source={source}; stop-failed; session={CurrentSessionState}",
                            cancellationToken);
                        return ShellUiAction.None;
                    }

                    PushGrowl("停止执行");
                    await RecordEventAsync(
                        scope,
                        $"source={source}; stopped",
                        cancellationToken);
                    return ShellUiAction.None;

                case TrayCommandId.ForceShow:
                    PushGrowl("主窗口已强制显示");
                    await RecordEventAsync(
                        scope,
                        $"source={source}; show",
                        cancellationToken);
                    return ShellUiAction.ShowMainWindow;

                case TrayCommandId.HideTray:
                    SettingsPage.UseTray = false;
                    var trayDisabledSnapshot = SettingsPage.CurrentGuiSnapshot;
                    await SettingsPage.SaveGuiSettingsAsync(cancellationToken);
                    await ApplyGuiSettingsAsync(trayDisabledSnapshot, cancellationToken);
                    await RecordEventAsync(
                        scope,
                        $"source={source}; hide-requested",
                        cancellationToken);
                    return ShellUiAction.None;

                case TrayCommandId.ToggleOverlay:
                    await ToggleOverlayFromTrayAsync(cancellationToken);
                    await RecordEventAsync(
                        scope,
                        $"source={source}; toggled",
                        cancellationToken);
                    return ShellUiAction.None;

                case TrayCommandId.SwitchLanguage:
                    if (string.IsNullOrWhiteSpace(targetLanguage))
                    {
                        await RecordEventAsync(
                            scope,
                            $"source={source}; ignored-without-target",
                            cancellationToken);
                        return ShellUiAction.None;
                    }

                    await ExecuteTrayLanguageSwitchAsync(targetLanguage, source, cancellationToken);
                    return ShellUiAction.None;

                case TrayCommandId.Exit:
                    // WPF parity (frozen TaskQueueViewModel.ConfirmExit): on the
                    // idle path the !Running early return never resets Closing, so
                    // repeat Exit/Restart clicks during shutdown are inert there.
                    // The dialog-passed path diverges by design: WPF's App_restart
                    // can still set the restart flag before OnExit reads it (a
                    // sub-second, racy window), while this latch keeps
                    // first-intent-wins - recorded as M22 R13-F2 wontfix-by-design.
                    if (_lifecycleCloseInitiated)
                    {
                        await RecordEventAsync(
                            scope,
                            $"source={source}; ignored-lifecycle-close-initiated",
                            cancellationToken);
                        return ShellUiAction.None;
                    }

                    _lifecycleCloseInitiated = true;
                    await RecordEventAsync(
                        scope,
                        $"source={source}; close",
                        cancellationToken);
                    return ShellUiAction.CloseMainWindow;

                case TrayCommandId.Restart:
                    // WPF parity: the frozen bootstrapper starts the successor exactly
                    // once at the end of OnExit, so a repeat Restart command before the
                    // process finishes exiting must not reach the lifecycle service
                    // again (a second spawn would race the single-instance mutex).
                    // Restart-after-Exit is likewise inert here; frozen WPF can honor
                    // it only in the dialog-passed case (see the Exit case, M22 R13-F2
                    // wontfix-by-design).
                    if (_lifecycleCloseInitiated)
                    {
                        await RecordEventAsync(
                            scope,
                            $"source={source}; ignored-lifecycle-close-initiated",
                            cancellationToken);
                        return ShellUiAction.None;
                    }

                    _lifecycleCloseInitiated = true;
                    var restartResult = await _runtime.AppLifecycleService.RestartAsync(cancellationToken);
                    if (!await ApplyResultAsync(restartResult, scope, cancellationToken))
                    {
                        // The spawn failed and the app keeps running; reopen the latch
                        // so the user can retry the Restart command.
                        _lifecycleCloseInitiated = false;
                        PushGrowl(restartResult.Message);
                        return ShellUiAction.None;
                    }

                    PushGrowl("重启命令已触发。");
                    await RecordEventAsync(
                        scope,
                        $"source={source}; restart-launched",
                        cancellationToken);
                    return ShellUiAction.CloseMainWindow;

                default:
                    var unknownMessage = $"未知托盘命令: {command}";
                    PushGrowl(unknownMessage);
                    _ = await ApplyResultAsync(
                        UiOperationResult.Fail(UiErrorCode.UnknownTrayCommand, unknownMessage),
                        scope,
                        cancellationToken);
                    return ShellUiAction.None;
            }
        }
        catch (Exception ex)
        {
            await RecordUnhandledExceptionAsync(
                scope,
                ex,
                UiErrorCode.UiOperationFailed,
                $"Tray command execution failed. source={source}",
                cancellationToken);
            PushGrowl(ex.Message);
            return ShellUiAction.None;
        }
    }

    public void PushGrowl(string message)
    {
        GrowlMessages.Add($"{DateTime.Now:HH:mm:ss} {message}");
        const int max = 8;
        while (GrowlMessages.Count > max)
        {
            GrowlMessages.RemoveAt(0);
        }
    }

    private void SetShellBlockingOperationOverlayVisible(bool visible)
    {
        if (_isShellBlockingOperationOverlayVisible == visible)
        {
            return;
        }

        _isShellBlockingOperationOverlayVisible = visible;
        OnPropertyChanged(nameof(IsBlockingOperationOverlayVisible));
    }

    private static Task WaitForBlockingOperationOverlayRenderAsync()
    {
        if (Avalonia.Application.Current is null || !Dispatcher.UIThread.CheckAccess())
        {
            return Task.CompletedTask;
        }

        return Dispatcher.UIThread.InvokeAsync(
            static () => { },
            DispatcherPriority.Loaded).GetTask();
    }

    public void SetAchievementToastWindowVisible(bool visible)
    {
        _achievementToastWindowVisible = visible;
        TryFlushPendingAchievementToasts();
    }

    public void SetMainWindowBackgrounded(bool backgrounded)
    {
        _mainWindowBackgrounded = backgrounded;
    }

    private bool ShouldSendBackgroundLinkFeedback(string source)
        => _mainWindowBackgrounded && string.Equals(source, "hotkey-linkstart", StringComparison.Ordinal);

    private async Task SendBackgroundLinkFeedbackAsync(bool started, CancellationToken cancellationToken)
    {
        try
        {
            var text = AchievementTextCatalog.GetString(
                started ? "BackgroundLinkStarted" : "BackgroundLinkStopped",
                CurrentShellLanguage,
                started ? "Tasks started" : "Tasks stopped");
            await _runtime.PlatformCapabilityService.SendSystemNotificationAsync(text, string.Empty, cancellationToken);
        }
        catch (Exception ex)
        {
            await RecordErrorAsync(
                "App.Shell.Tray.Start",
                "Failed to send the LinkStart background feedback notification.",
                ex);
        }
    }

    public void DismissAchievementToast(string id)
    {
        var toast = AchievementToasts.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
        if (toast is null || !toast.BeginDismissAnimation())
        {
            return;
        }

        _ = RemoveAchievementToastAfterAnimationAsync(toast);
    }

    public async Task ShowAchievementListDialogFromToastAsync(string toastId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(toastId))
        {
            return;
        }

        DismissAchievementToast(toastId);
        await SettingsPage.ShowAchievementListDialogAsync(cancellationToken);
    }

    private void OnAchievementUnlocked(object? sender, AchievementUnlockedEvent notification)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            HandleAchievementUnlocked(notification);
            return;
        }

        Dispatcher.UIThread.Post(() => HandleAchievementUnlocked(notification));
    }

    private void HandleAchievementUnlocked(AchievementUnlockedEvent notification)
    {
        if (!_achievementToastWindowVisible || !_achievementToastStartupCompleted)
        {
            if (_pendingAchievementToasts.All(item => !string.Equals(item.Id, notification.Id, StringComparison.Ordinal)))
            {
                _pendingAchievementToasts.Enqueue(notification);
            }

            return;
        }

        PresentAchievementToast(notification);
    }

    internal void BeginAchievementToastStartupRelease()
    {
        if (_startupAchievementToastReleaseStarted)
        {
            return;
        }

        _startupAchievementToastReleaseStarted = true;
        _ = ReleaseAchievementToastsAfterStartupAnnouncementAsync();
    }

    internal void MarkAchievementToastStartupCompleted()
    {
        if (_achievementToastStartupCompleted)
        {
            return;
        }

        _achievementToastStartupCompleted = true;
        RecordStartupPhase("AchievementToast.Ready", "Achievement toast gate opened after deferred startup completed.");
        TryFlushPendingAchievementToasts();
    }

    internal void PrepareStartupAchievementToastAnnouncementGate(Task startupAnnouncementCompletionTask)
    {
        if (_startupAchievementToastAnnouncementGatePrepared)
        {
            return;
        }

        _startupAchievementToastAnnouncementGatePrepared = true;
        _ = ObserveStartupAchievementToastAnnouncementGateAsync(startupAnnouncementCompletionTask);
    }

    private void TryFlushPendingAchievementToasts()
    {
        if (!_achievementToastWindowVisible || !_achievementToastStartupCompleted)
        {
            return;
        }

        FlushPendingAchievementToasts();
    }

    private async Task ReleaseAchievementToastsAfterStartupAnnouncementAsync()
    {
        try
        {
            await _startupAchievementToastAnnouncementGate.Task;
        }
        finally
        {
            Dispatcher.UIThread.Post(MarkAchievementToastStartupCompleted, DispatcherPriority.Send);
        }
    }

    private async Task ObserveStartupAchievementToastAnnouncementGateAsync(Task startupAnnouncementCompletionTask)
    {
        try
        {
            await startupAnnouncementCompletionTask;
        }
        catch
        {
            // Announcement failures are handled by the startup workflow; toast startup should not remain blocked.
        }
        finally
        {
            _startupAchievementToastAnnouncementGate.TrySetResult(true);
        }
    }

    private void FlushPendingAchievementToasts()
    {
        while (_pendingAchievementToasts.Count > 0)
        {
            PresentAchievementToast(_pendingAchievementToasts.Dequeue());
        }
    }

    private void PresentAchievementToast(AchievementUnlockedEvent notification)
    {
        if (AchievementToasts.Any(item => string.Equals(item.Id, notification.Id, StringComparison.Ordinal)))
        {
            return;
        }

        AchievementToasts.Insert(
            0,
            new AchievementToastItemViewModel(
                notification.Id,
                FormatAchievementCelebrateText(
                    AchievementTextCatalog.GetString("AchievementCelebrate", CurrentShellLanguage, "Achievement Unlocked")
                        .Replace("🎉", string.Empty, StringComparison.Ordinal)
                        .Trim()),
                notification.Title,
                notification.Description,
                notification.MedalColor,
                notification.AutoClose,
                notification.UnlockedAtUtc,
                DismissAchievementToast));
        _ = RecordEventAsync(
            "AchievementToast.Present",
            $"id={notification.Id}; autoClose={notification.AutoClose}; visible={_achievementToastWindowVisible}; startupCompleted={_achievementToastStartupCompleted}; count={AchievementToasts.Count}");

        const int maxVisible = 4;
        while (AchievementToasts.Count(item => !item.IsDismissAnimationActive) > maxVisible)
        {
            var removedToast = AchievementToasts.Last(item => !item.IsDismissAnimationActive);
            DismissAchievementToast(removedToast.Id);
        }
    }

    private async Task RemoveAchievementToastAfterAnimationAsync(AchievementToastItemViewModel toast)
    {
        await Task.Delay(FloatingOverlayAnimationDuration);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (AchievementToasts.Contains(toast))
            {
                AchievementToasts.Remove(toast);
            }

            toast.Dispose();
        });
    }

    private string BuildLinkStartStateNotAllowedMessage(SessionState state)
    {
        var zh = $"会话状态 `{state}` 不允许 Start/LinkStart。请先前往“设置 > 连接设置”完成连接。";
        var en = $"Session state `{state}` does not allow Start/LinkStart. Go to Settings > Connection and connect first.";
        return BuildLocalizedMessage(zh, en);
    }

    private string BuildStopStateNotAllowedMessage(SessionState state)
    {
        var zh = $"会话状态 `{state}` 不允许 Stop。";
        var en = $"Session state `{state}` does not allow Stop.";
        return BuildLocalizedMessage(zh, en);
    }

    private string BuildLocalizedMessage(string zh, string en)
    {
        return DialogTextCatalog.Select(CurrentShellLanguage, zh, en);
    }

    private void ApplyDeveloperModeFromConfig()
    {
        var enabled = TryReadGlobalBool(_runtime.ConfigurationService.CurrentConfig.GlobalValues, DeveloperModeConfigKey, false);
        _runtime.LogService.SetVerboseEnabled(enabled);
    }

    private static bool TryReadGlobalBool(IReadOnlyDictionary<string, JsonNode?> globals, string key, bool fallback)
    {
        if (!globals.TryGetValue(key, out var node) || node is null)
        {
            return fallback;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var boolValue))
            {
                return boolValue;
            }

            if (value.TryGetValue<string>(out var textValue))
            {
                if (bool.TryParse(textValue, out var parsed))
                {
                    return parsed;
                }

                if (int.TryParse(textValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
                {
                    return intValue != 0;
                }
            }

            if (value.TryGetValue<int>(out var intBool))
            {
                return intBool != 0;
            }
        }

        var raw = node.ToString();
        if (bool.TryParse(raw, out var fallbackParsed))
        {
            return fallbackParsed;
        }

        return fallback;
    }

    private void NavigateToSettingsSection(string sectionKey)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var settingsTabIndex = -1;
            for (var i = 0; i < RootTabs.Count; i++)
            {
                if (string.Equals(RootTabs[i], "Settings", StringComparison.OrdinalIgnoreCase))
                {
                    settingsTabIndex = i;
                    break;
                }
            }

            if (settingsTabIndex >= 0)
            {
                SelectedRootTabIndex = settingsTabIndex;
            }

            SettingsPage.SelectSection(sectionKey);
        });
    }

    public void OpenConnectionSettings()
    {
        NavigateToSettingsSection("Connect");
    }

    private void OnSettingsResourceVersionUpdated(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            TaskQueuePage.RefreshStagePresentation(forceReloadStageOptions: true);
            RefreshWindowTitle();
        });
    }

    private void OnSettingsUpdateAvailabilityChanged(object? sender, EventArgs e)
    {
        if (sender is not SettingsPageViewModel page)
        {
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplySettingsUpdateAvailabilityState(page);
            return;
        }

        Dispatcher.UIThread
            .InvokeAsync(
                () => ApplySettingsUpdateAvailabilityState(page),
                DispatcherPriority.Send)
            .GetTask()
            .GetAwaiter()
            .GetResult();
    }

    private void OnSettingsPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(SettingsPageViewModel.IsBlockingOperationOverlayVisible), StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(IsBlockingOperationOverlayVisible));
        }

        if (string.Equals(e.PropertyName, nameof(SettingsPageViewModel.UpdatePanelUiVersion), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(SettingsPageViewModel.UpdatePanelResourceVersion), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(SettingsPageViewModel.UpdatePanelResourceDateTime), StringComparison.Ordinal))
        {
            RefreshWindowTitle();
        }
    }

    private async void OnSettingsConfigurationContextChanged(object? sender, ConfigurationContextChangedEventArgs e)
    {
        try
        {
            await HandleSettingsConfigurationContextChangedAsync(e);
        }
        catch (Exception ex)
        {
            await RecordUnhandledExceptionAsync(
                "Settings.ConfigurationContextChanged",
                ex,
                UiErrorCode.UiOperationFailed,
                $"配置上下文刷新失败: {ex.Message}");
        }
    }

    private void RefreshRootTextState()
    {
        ApplySettingsUpdateAvailabilityState();
        RefreshWindowTitle();
        OnPropertyChanged(nameof(RootTexts));
        OnPropertyChanged(nameof(BlockingConfigIssueSummary));
        OnPropertyChanged(nameof(TaskQueueTabTitle));
        OnPropertyChanged(nameof(CopilotTabTitle));
        OnPropertyChanged(nameof(ToolboxTabTitle));
        OnPropertyChanged(nameof(SettingsTabTitle));

        var selected = SelectedImportSource;
        ImportSourceOptions.Clear();
        ImportSourceOptions.Add(new ImportSourceOptionItem(ImportSource.Auto, RootTexts["Main.ImportSource.Auto"]));
        ImportSourceOptions.Add(new ImportSourceOptionItem(ImportSource.GuiNewOnly, RootTexts["Main.ImportSource.GuiNewOnly"]));
        ImportSourceOptions.Add(new ImportSourceOptionItem(ImportSource.GuiOnly, RootTexts["Main.ImportSource.GuiOnly"]));
        SelectedImportSource = selected;
        SelectedImportSourceOption = ImportSourceOptions.FirstOrDefault(item => item.Source == SelectedImportSource)
            ?? ImportSourceOptions.FirstOrDefault();
        RefreshRootPageHostStatusText();
    }

    private void ApplySettingsUpdateAvailabilityState(SettingsPageViewModel? page = null)
    {
        if (page is null && !TryGetSettingsPage(out page))
        {
            WindowVersionUpdateInfo = string.Empty;
            WindowResourceUpdateInfo = string.Empty;
            IsWindowUpdateActionRunning = false;
            return;
        }

        IsWindowUpdateActionRunning = page.IsVersionUpdateActionRunning;
        WindowVersionUpdateInfo = page.HasPendingVersionUpdateAvailability
            ? RootTexts["Main.Update.VersionAvailable"]
            : string.Empty;
        WindowResourceUpdateInfo = page.PendingResourceUpdateSummary;
    }

    private async Task RunStartupVersionUpdateWorkflowAsync(
        SettingsPageViewModel settingsPage,
        CancellationToken cancellationToken)
    {
        try
        {
            await settingsPage.EnsureSectionDataLoadedAsync("VersionUpdate", cancellationToken);
            await settingsPage.RunStartupVersionUpdateCheckAsync(cancellationToken);
            await Dispatcher.UIThread.InvokeAsync(
                () => ApplySettingsUpdateAvailabilityState(settingsPage),
                DispatcherPriority.Send,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // no-op
        }
        catch (Exception ex)
        {
            await RecordUnhandledExceptionAsync(
                "App.Initialize.VersionUpdate",
                ex,
                UiErrorCode.UiOperationFailed,
                $"启动更新检查失败: {ex.Message}",
                cancellationToken);
        }
    }

    private async Task RunStartupAnnouncementWorkflowAsync(
        SettingsPageViewModel settingsPage,
        CancellationToken cancellationToken)
    {
        try
        {
            await settingsPage.ShowStartupAnnouncementAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // no-op
        }
        catch (Exception ex)
        {
            await RecordUnhandledExceptionAsync(
                "App.Initialize.Announcement",
                ex,
                UiErrorCode.UiOperationFailed,
                $"启动公告检查失败: {ex.Message}",
                cancellationToken);
        }
    }

    private void StartBackgroundStartupDialog(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        _ = ObserveBackgroundStartupDialogAsync(operation, cancellationToken);
    }

    private static async Task ObserveBackgroundStartupDialogAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            await operation(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // no-op
        }
    }

    private static void ShowAndActivateMainWindow()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } mainWindow })
        {
            return;
        }

        if (!mainWindow.IsVisible)
        {
            mainWindow.Show();
        }

        if (mainWindow.WindowState == WindowState.Minimized)
        {
            mainWindow.WindowState = WindowState.Normal;
        }

        mainWindow.Activate();
    }

    private void RefreshWindowTitle()
    {
        var titleParts = new List<string>();
        var prefix = ReadTitleSetting(ConfigurationKeys.WindowTitlePrefix, string.Empty, globalOnly: false);
        var selectedPartsText = ReadTitleSetting(ConfigurationKeys.WindowTitleSelectShowList, "2 3 4", globalOnly: true);
        var selectedParts = (string.IsNullOrWhiteSpace(selectedPartsText) ? "2 3 4" : selectedPartsText)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        var productName = string.IsNullOrWhiteSpace(prefix)
            ? WindowDisplayName
            : $"{prefix.Trim()} - {WindowDisplayName}";
        var config = _runtime.ConfigurationService.CurrentConfig;
        if (selectedParts.Contains("1")
            && config.Profiles.Count > 1
            && !string.IsNullOrWhiteSpace(config.CurrentProfile))
        {
            productName += $" ({config.CurrentProfile.Trim()})";
        }

        titleParts.Add(productName);
        var uiVersion = typeof(MainShellViewModel).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion?.Split('+')[0];
        if (!string.IsNullOrWhiteSpace(uiVersion))
        {
            titleParts.Add(FormatWindowTitleVersion(uiVersion.Trim(), GetUiBuildDateTime()));
        }

        if (TryGetSettingsPage(out var settingsPage)
            && !string.IsNullOrWhiteSpace(settingsPage.UpdatePanelResourceVersion))
        {
            titleParts.Add(FormatWindowTitleVersion(
                settingsPage.UpdatePanelResourceVersion.Trim(),
                settingsPage.UpdatePanelResourceDateTime));
        }

        // WPF SettingsViewModel glues the connect address to the connect-mode segment with a
        // single space ("... - {mode} ({address}) - ...") instead of treating it as its own
        // " - "-separated part; without the mode segment it still appends to the preceding text.
        var titleConnectAddress = selectedParts.Contains("3") ? GetWindowTitleConnectAddress() : null;
        if (selectedParts.Contains("2"))
        {
            var connectConfigName = _connectionGameSharedState.SelectedConnectConfigOption?.DisplayName
                ?? _connectionGameSharedState.ConnectConfig;
            if (titleConnectAddress is not null)
            {
                connectConfigName += $" ({titleConnectAddress})";
            }

            titleParts.Add(connectConfigName);
        }
        else if (titleConnectAddress is not null && titleParts.Count > 0)
        {
            titleParts[^1] += $" ({titleConnectAddress})";
        }

        if (selectedParts.Contains("4"))
        {
            titleParts.Add(_connectionGameSharedState.SelectedClientTypeOption?.DisplayName
                ?? _connectionGameSharedState.ClientType);
        }

        var updateMessages = new List<string>();
        if (HasWindowVersionUpdateInfo)
        {
            updateMessages.Add(WindowVersionUpdateInfo.Trim());
        }

        if (HasWindowResourceUpdateInfo)
        {
            updateMessages.Add(WindowResourceUpdateInfo.Trim());
        }

        var baseTitle = ComposeWindowTitleBase();
        _windowTitleSource = updateMessages.Count == 0
            ? baseTitle
            : $"{baseTitle} - {string.Join(" / ", updateMessages)}";
        _windowTitleScrollOffset = 0;
        UpdateWindowTitleDisplay();
    }

    private string ComposeWindowTitleBase()
    {
        var config = _runtime.ConfigurationService.CurrentConfig;
        // WPF parity (SettingsViewModel.UpdateWindowTitle uses ConfigurationHelper.GetValue):
        // the prefix is read profile-first with global fallback, so an imported profile-scoped
        // prefix is honored even before any GUI save.
        var prefix = ReadProfileFirstConfigString(
            config,
            MAAUnified.Compat.Constants.ConfigurationKeys.WindowTitlePrefix,
            string.Empty).Trim();
        if (prefix.Length > 0)
        {
            prefix += " - ";
        }

        var selectList = ReadGlobalConfigString(
                config,
                MAAUnified.Compat.Constants.ConfigurationKeys.WindowTitleSelectShowList,
                "2 3 4")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var profilePart = string.Empty;
        var presetPart = string.Empty;
        var addressPart = string.Empty;
        var clientPart = string.Empty;
        foreach (var select in selectList)
        {
            switch (select)
            {
                case "1": // configuration profile name (only when multiple profiles exist)
                    if (config.Profiles.Count > 1)
                    {
                        profilePart = $" ({config.CurrentProfile})";
                    }

                    break;
                case "2": // connection preset display name
                    var preset = _connectionGameSharedState.ConnectConfigOptions.FirstOrDefault(
                        option => string.Equals(option.Value, _connectionGameSharedState.ConnectConfig, StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrWhiteSpace(preset?.DisplayName))
                    {
                        presetPart = $" - {preset.DisplayName}";
                    }

                    break;
                case "3": // connection address, loopback host stripped (WPF parity)
                    var address = _connectionGameSharedState.ConnectAddress;
                    if (!string.IsNullOrWhiteSpace(address))
                    {
                        addressPart = $" ({address})"
                            .Replace("127.0.0.1:", string.Empty, StringComparison.Ordinal)
                            .Replace("localhost:", string.Empty, StringComparison.Ordinal);
                    }

                    break;
                case "4": // client type display name
                    var client = _connectionGameSharedState.ClientTypeOptions.FirstOrDefault(
                        option => string.Equals(option.Value, _connectionGameSharedState.ClientType, StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrWhiteSpace(client?.DisplayName))
                    {
                        clientPart = $" - {client.DisplayName}";
                    }

                    break;
            }
        }

        var title = $"{prefix}{WindowDisplayName}{profilePart}";

        var uiVersion = typeof(MainShellViewModel).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion?.Split('+')[0];
        if (!string.IsNullOrWhiteSpace(uiVersion))
        {
            title += $" - {FormatWindowTitleVersion(uiVersion.Trim(), GetUiBuildDateTime())}";
        }

        if (TryGetSettingsPage(out var settingsPage)
            && !string.IsNullOrWhiteSpace(settingsPage.UpdatePanelResourceVersion))
        {
            title += $" - {FormatWindowTitleVersion(
                settingsPage.UpdatePanelResourceVersion.Trim(),
                settingsPage.UpdatePanelResourceDateTime)}";
        }

        return $"{title}{presetPart}{addressPart}{clientPart}";
    }

    private static string ReadGlobalConfigString(UnifiedConfig config, string key, string fallback)
    {
        if (!config.GlobalValues.TryGetValue(key, out var node) || node is null)
        {
            return fallback;
        }

        return node is System.Text.Json.Nodes.JsonValue value && value.TryGetValue(out string? text) && text is not null
            ? text
            : node.ToString();
    }

    private static string ReadProfileFirstConfigString(UnifiedConfig config, string key, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(config.CurrentProfile)
            && config.Profiles.TryGetValue(config.CurrentProfile, out var profile)
            && profile.Values.TryGetValue(key, out var profileNode)
            && profileNode is not null)
        {
            return profileNode is System.Text.Json.Nodes.JsonValue profileValue
                   && profileValue.TryGetValue(out string? profileText)
                   && profileText is not null
                ? profileText
                : profileNode.ToString();
        }

        return ReadGlobalConfigString(config, key, fallback);
    }

    private string? GetWindowTitleConnectAddress()
    {
        if (string.IsNullOrWhiteSpace(_connectionGameSharedState.ConnectAddress))
        {
            return null;
        }

        return _connectionGameSharedState.ConnectAddress.Trim()
            .Replace("127.0.0.1:", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("localhost:", string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    // WPF LocalizationHelper.FormatVersion appends a localized date suffix to both the UI
    // version (build time) and the resource version (resource last-updated time). When the
    // timestamp is unavailable the bare version is kept instead of rendering a bogus date.
    // The date format culture mirrors WPF LocalizationHelper.CustomCultureInfo: the global
    // GUI.CustomCulture config value when it names a valid culture, otherwise the OS
    // CultureInfo.CurrentCulture - the shell UI language is intentionally not consulted.
    private string FormatWindowTitleVersion(string version, DateTime? dateTime)
    {
        return dateTime is { } value && value != DateTime.MinValue
            ? VersionDisplayFormatter.FormatVersion(
                version,
                value,
                ReadTitleSetting(ConfigurationKeys.CustomCulture, string.Empty, globalOnly: true))
            : version;
    }

    private static DateTime? GetUiBuildDateTime()
    {
        return typeof(MainShellViewModel).Assembly
            .GetCustomAttribute<MAAUnified.App.Properties.BuildDateTimeAttribute>()
            ?.BuildTime.UtcDateTime;
    }

    private string ReadTitleSetting(string key, string fallback, bool globalOnly)
    {
        var config = _runtime.ConfigurationService.CurrentConfig;
        if (!globalOnly
            && config.Profiles.TryGetValue(config.CurrentProfile, out var profile)
            && TryReadString(profile.Values, key, out var profileValue))
        {
            return profileValue;
        }

        return TryReadString(config.GlobalValues, key, out var globalValue) ? globalValue : fallback;
    }

    private static bool TryReadString(IReadOnlyDictionary<string, JsonNode?> values, string key, out string value)
    {
        value = string.Empty;
        if (!values.TryGetValue(key, out var node) || node is null)
        {
            return false;
        }

        if (node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text))
        {
            value = text;
            return true;
        }

        value = node.ToString();
        return true;
    }

    public void DismissWindowUpdateOverlay()
    {
        if (!HasWindowUpdateInfo)
        {
            return;
        }

        _dismissedWindowUpdateOverlaySignature = CurrentWindowUpdateOverlaySignature;
        NotifyWindowUpdateOverlayVisibilityChanged();
    }

    public void OpenVersionUpdateSectionFromWindowOverlay()
    {
        DismissWindowUpdateOverlay();
        NavigateToSettingsSection("VersionUpdate");
    }

    private string CurrentWindowUpdateOverlaySignature
        => string.Concat(WindowVersionUpdateInfo.Trim(), "\n", WindowResourceUpdateInfo.Trim());

    private void NotifyWindowUpdateOverlayVisibilityChanged()
    {
        if (string.IsNullOrEmpty(CurrentWindowUpdateOverlaySignature))
        {
            _dismissedWindowUpdateOverlaySignature = string.Empty;
        }

        OnPropertyChanged(nameof(HasVisibleWindowVersionUpdateInfo));
        OnPropertyChanged(nameof(HasVisibleWindowResourceUpdateInfo));
        OnPropertyChanged(nameof(HasMultipleVisibleWindowUpdates));
        OnPropertyChanged(nameof(HasMultipleWindowUpdates));
        OnPropertyChanged(nameof(HasVisibleWindowUpdateInfo));
        UpdateWindowUpdateOverlayPresentation();
    }

    private void UpdateWindowUpdateOverlayPresentation()
    {
        _windowUpdateOverlayAnimationCts?.Cancel();
        _windowUpdateOverlayAnimationCts?.Dispose();
        _windowUpdateOverlayAnimationCts = null;

        if (HasVisibleWindowUpdateInfo)
        {
            var wasPresented = IsWindowUpdateOverlayPresented;
            IsWindowUpdateOverlayPresented = true;

            if (!wasPresented || IsWindowUpdateOverlayAnimationHidden)
            {
                IsWindowUpdateOverlayAnimationHidden = true;
                Dispatcher.UIThread.Post(
                    () =>
                    {
                        if (HasVisibleWindowUpdateInfo)
                        {
                            IsWindowUpdateOverlayAnimationHidden = false;
                        }
                    },
                    DispatcherPriority.Loaded);
            }
            else
            {
                IsWindowUpdateOverlayAnimationHidden = false;
            }

            return;
        }

        if (!IsWindowUpdateOverlayPresented)
        {
            IsWindowUpdateOverlayAnimationHidden = true;
            return;
        }

        IsWindowUpdateOverlayAnimationHidden = true;
        _windowUpdateOverlayAnimationCts = new CancellationTokenSource();
        _ = CompleteWindowUpdateOverlayDismissAsync(_windowUpdateOverlayAnimationCts.Token);
    }

    private async Task CompleteWindowUpdateOverlayDismissAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(FloatingOverlayAnimationDuration, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!cancellationToken.IsCancellationRequested && !HasVisibleWindowUpdateInfo)
            {
                IsWindowUpdateOverlayPresented = false;
            }
        });
    }

    private static string FormatAchievementCelebrateText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        if (!text.Contains(' ', StringComparison.Ordinal)
            || !text.Any(static character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z'))
        {
            return text;
        }

        var firstSpaceIndex = text.IndexOf(' ');
        return firstSpaceIndex > 0
            ? text[..firstSpaceIndex] + Environment.NewLine + text[(firstSpaceIndex + 1)..]
            : text;
    }

    private async Task ApplySettingsConfigurationContextChangedAsync(
        ConfigurationContextChangedEventArgs change,
        CancellationToken cancellationToken)
    {
        try
        {
            await HandleSettingsConfigurationContextChangedAsync(change);
        }
        catch (Exception ex)
        {
            await RecordUnhandledExceptionAsync(
                "Settings.ConfigurationContextChanged",
                ex,
                UiErrorCode.UiOperationFailed,
                $"配置上下文刷新失败: {ex.Message}");
        }
    }

    private async Task HandleSettingsConfigurationContextChangedAsync(ConfigurationContextChangedEventArgs change)
    {
        var previousClientType = _connectionGameSharedState.ClientType;
        SyncConnectionFromProfile();
        await ReloadClientResourceIfChangedAsync(previousClientType, _connectionGameSharedState.ClientType);
        RefreshConfigValidationState(_runtime.ConfigurationService.CurrentValidationIssues);

        switch (change.Reason)
        {
            case ConfigurationContextChangeReason.ProfileSwitched:
                await TaskQueuePage.ReloadConfigurationContextAsync();
                if (TryGetCopilotPage(out var switchedCopilotPage))
                {
                    switchedCopilotPage.ReloadConfigurationContext();
                }

                break;

            case ConfigurationContextChangeReason.LegacyImport:
            case ConfigurationContextChangeReason.UnifiedImport:
                await SyncLanguageCoordinatorWithConfigAsync();
                await ApplyGuiSettingsAsync(SettingsPage.CurrentGuiSnapshot);
                RefreshShellCloseButtonVisibilityFromConfig();
                await TaskQueuePage.ReloadConfigurationContextAsync(forceReloadStageOptions: true);
                if (TryGetCopilotPage(out var importedCopilotPage))
                {
                    importedCopilotPage.ReloadConfigurationContext();
                }

                break;
        }

        if (change.Report is not null)
        {
            AppendImportReportToTaskQueue(change.Report, manualImport: true);
        }
        else if (!string.IsNullOrWhiteSpace(change.Message))
        {
            TaskQueuePage.AppendSystemLog(change.Message);
        }
    }

    private void SyncConnectionToProfile(string? changedPropertyName = null)
    {
        if (_syncingConnectionState)
        {
            return;
        }

        if (!_runtime.ConfigurationService.TryGetCurrentProfile(out var profile))
        {
            return;
        }

        ConnectionGameProfileSync.WritePropertyToProfile(profile, _connectionGameSharedState, changedPropertyName);
    }

    private async Task<AchievementStartupContext> BuildAchievementStartupContextAsync(CancellationToken cancellationToken)
    {
        return new AchievementStartupContext(
            DateTimeOffset.UtcNow,
            DateTimeOffset.Now,
            ResolveBuildTimestampUtc(),
            await ResolveResourceTimestampUtcAsync(cancellationToken),
            MaaCoreBridgeNative.TryReadInstalledVersion(RuntimeLayout.ResolveRuntimeBaseDirectory()));
    }

    // WPF reads `BuildDateTimeAttribute`; the unified app stamps the same attribute at compile
    // time. Fall back to the app assembly write time (what the Version Update panel shows) when
    // the attribute is missing.
    private static DateTimeOffset? ResolveBuildTimestampUtc()
    {
        var buildTime = typeof(MainShellViewModel).Assembly
            .GetCustomAttribute<MAAUnified.App.Properties.BuildDateTimeAttribute>()
            ?.BuildTime;
        if (buildTime.HasValue)
        {
            return buildTime.Value;
        }

        try
        {
            var location = typeof(MainShellViewModel).Assembly.Location;
            if (!string.IsNullOrWhiteSpace(location) && File.Exists(location))
            {
                return new DateTimeOffset(File.GetLastWriteTimeUtc(location), TimeSpan.Zero);
            }
        }
        catch
        {
            // Best effort: an unavailable build stamp skips the time based achievements.
        }

        return null;
    }

    private async Task<DateTimeOffset?> ResolveResourceTimestampUtcAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _runtime.VersionUpdateFeatureService.LoadResourceVersionInfoAsync(null, cancellationToken);
            if (result.Success
                && result.Value is { LastUpdatedAt: var lastUpdatedAt }
                && lastUpdatedAt != DateTime.MinValue)
            {
                return new DateTimeOffset(DateTime.SpecifyKind(lastUpdatedAt, DateTimeKind.Utc));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Best effort: a missing resource version file skips the resource age check.
        }

        return null;
    }

    private void SyncConnectionFromProfile()
    {
        if (!_runtime.ConfigurationService.TryGetCurrentProfile(out var profile))
        {
            return;
        }

        _syncingConnectionState = true;
        try
        {
            ConnectionGameProfileSync.ReadFromProfile(profile, _connectionGameSharedState, tolerateMissing: false);
        }
        finally
        {
            _syncingConnectionState = false;
            _lastObservedClientType = _connectionGameSharedState.ClientType;
        }
    }

    private void AppendImportReportToTaskQueue(ImportReport? report, bool manualImport)
    {
        if (report is null)
        {
            return;
        }

        foreach (var line in ImportReportTextFormatter.BuildLogLines(report, manualImport))
        {
            TaskQueuePage.AppendSystemLog(line.Message, line.Level);
        }
    }

    private void ApplySessionCallback(CoreCallbackEvent callback)
    {
        AppendRootLogEntry(callback.Timestamp, $"CORE {callback.MsgName}({callback.MsgId}) {callback.PayloadJson}");

        if (!string.Equals(callback.MsgName, "ConnectionInfo", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!TryParseScreencapCost(callback.PayloadJson, out var min, out var avg, out var max))
        {
            var status = _runtime.SessionService.LastConnectionCallbackStatus;
            if (status is null)
            {
                return;
            }

            if (string.Equals(status.What, "Connected", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status.What, "Reconnected", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(status.Address))
                {
                    _connectionGameSharedState.ConnectAddress = status.Address;
                }
            }
            else if (string.Equals(status.What, "FastestWayToScreencap", StringComparison.OrdinalIgnoreCase))
            {
                _connectionGameSharedState.FastestScreencapMethod = status.Method ?? string.Empty;
            }

            return;
        }

        _connectionGameSharedState.UpdateScreencapCost(min, avg, max, callback.Timestamp);
    }

    private void OnSharedConnectionStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_syncingConnectionState)
        {
            return;
        }

        if (ConnectionGameProfileSync.ShouldSyncProperty(e.PropertyName))
        {
            SyncConnectionToProfile(e.PropertyName);
        }

        if (string.Equals(e.PropertyName, nameof(ConnectionGameSharedStateViewModel.ClientType), StringComparison.Ordinal))
        {
            var previousClientType = _lastObservedClientType;
            _lastObservedClientType = _connectionGameSharedState.ClientType;
            _ = ReloadClientResourceIfChangedAsync(previousClientType, _connectionGameSharedState.ClientType);
        }

        if (e.PropertyName is nameof(ConnectionGameSharedStateViewModel.ConnectConfig)
            or nameof(ConnectionGameSharedStateViewModel.ConnectAddress)
            or nameof(ConnectionGameSharedStateViewModel.ClientType)
            or nameof(ConnectionGameSharedStateViewModel.ConnectConfigOptions)
            or nameof(ConnectionGameSharedStateViewModel.ClientTypeOptions))
        {
            RefreshWindowTitle();
        }
    }

    private async Task ReloadClientResourceIfChangedAsync(string? previousClientType, string? currentClientType)
    {
        if (!IsClientResourceReloadRequired(previousClientType, currentClientType))
        {
            return;
        }

        var requestedClientType = currentClientType!.Trim();
        var version = Interlocked.Increment(ref _clientResourceReloadVersion);
        var result = await _runtime.SessionService.ReloadResourceWhenIdleAsync(
            requestedClientType,
            waitTimeout: TimeSpan.FromSeconds(30),
            isStillRequested: () => Volatile.Read(ref _clientResourceReloadVersion) == version);
        if (!result.Success
            && result.Error?.Code is not CoreErrorCode.NotInitialized
                and not CoreErrorCode.NotSupported
                and not CoreErrorCode.Disposed)
        {
            _runtime.LogService.Warn($"Client resource reload failed for `{currentClientType}`: {result.Error?.Message}");
        }
    }

    internal static bool IsClientResourceReloadRequired(string? previousClientType, string? currentClientType)
    {
        if (string.IsNullOrWhiteSpace(currentClientType))
        {
            return false;
        }

        var previous = previousClientType?.Trim();
        var current = currentClientType.Trim();
        if (string.IsNullOrWhiteSpace(previous))
        {
            return true;
        }

        if (string.Equals(previous, current, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // WPF parity (GameSettingsUserControlModel.NeedRestartAfterClientTypeChange): switching
        // between Official and Bilibili reuses the same resource set and needs no reload.
        return !(IsOfficialOrBilibiliClientType(previous) && IsOfficialOrBilibiliClientType(current));
    }

    private static bool IsOfficialOrBilibiliClientType(string clientType)
    {
        return string.Equals(clientType, "Official", StringComparison.OrdinalIgnoreCase)
            || string.Equals(clientType, "Bilibili", StringComparison.OrdinalIgnoreCase);
    }

    private void StartTimerScheduler()
    {
        if (_timerScheduleTimer.IsEnabled)
        {
            return;
        }

        _timerScheduleTimer.Start();
    }

    private void OnTimerScheduleTick(object? sender, EventArgs e)
    {
        TaskQueuePage.RefreshStagePresentationForClock(DateTime.UtcNow);
        _ = EvaluateTimerScheduleAsync(DateTimeOffset.Now);
    }

    private async Task EvaluateTimerScheduleAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _timerScheduleProcessing, 1) == 1)
        {
            return;
        }

        try
        {
            var minuteKey = now.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
            var nowHour = now.Hour;
            var nowMinute = now.Minute;

            foreach (var slot in SettingsPage.Timers.OrderBy(static slot => slot.Index))
            {
                if (slot.Enabled == false)
                {
                    continue;
                }

                if (!TryParseTimerTime(slot.Time, out var slotHour, out var slotMinute))
                {
                    continue;
                }

                // WPF parity (TaskQueueViewModel.CheckTimers): two minutes before the scheduled
                // time the configuration is pre-switched; the minute wrap keeps a 00:00 slot
                // switching at 23:58 of the same day.
                var slotTotalMinutes = (slotHour * 60) + slotMinute;
                var nowTotalMinutes = (nowHour * 60) + nowMinute;
                var preSwitchMinutes = (slotTotalMinutes - 2 + 1440) % 1440;
                if (nowTotalMinutes == preSwitchMinutes)
                {
                    if (_timerSlotPreSwitchMinuteDedup.TryGetValue(slot.Index, out var lastPreSwitchMinute)
                        && string.Equals(lastPreSwitchMinute, minuteKey, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    _timerSlotPreSwitchMinuteDedup[slot.Index] = minuteKey;
                    await PreSwitchTimerProfileAsync(slot, minuteKey, cancellationToken);
                    continue;
                }

                if (slotHour != nowHour || slotMinute != nowMinute)
                {
                    continue;
                }

                if (_timerSlotMinuteDedup.TryGetValue(slot.Index, out var lastMinute)
                    && string.Equals(lastMinute, minuteKey, StringComparison.Ordinal))
                {
                    continue;
                }

                _timerSlotMinuteDedup[slot.Index] = minuteKey;
                await TriggerScheduledSlotAsync(slot, minuteKey, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // no-op
        }
        catch (Exception ex)
        {
            await RecordTimerScheduleErrorAsync($"Timer scheduler tick failed: {ex.Message}", ex, cancellationToken);
        }
        finally
        {
            Volatile.Write(ref _timerScheduleProcessing, 0);
        }
    }

    private async Task TriggerScheduledSlotAsync(
        TimerSlotViewModel slot,
        string minuteKey,
        CancellationToken cancellationToken)
    {
        var resetOneShot = slot.Enabled is null;
        var resetOneShotAfterAttempt = false;
        CancellationTokenSource? scheduledStartCancellation = null;
        try
        {
            CurrentSessionState = _runtime.SessionService.CurrentState;
            var sessionRunning = CurrentSessionState is SessionState.Running or SessionState.Stopping;
            var hasActiveSessionOperation = _connectionGameSharedState.IsSessionOperationActive;
            // WPF parity (TaskQueueViewModel.HandleScheduledStart): an in-flight operation or a
            // running/connecting session blocks an unforced scheduled start. A merely
            // connected session is idle in WPF terms, so LinkStart can reuse it.
            var sessionBlocked = hasActiveSessionOperation
                || CurrentSessionState is SessionState.Connecting
                    or SessionState.Running
                    or SessionState.Stopping;
            var triggerMessage =
                $"slot={slot.Index}; time={slot.Time}; minute={minuteKey}; session={CurrentSessionState}; activeOperation={hasActiveSessionOperation}; force={SettingsPage.ForceScheduledStart}";
            await RecordEventAsync("Timer.Schedule.Trigger", triggerMessage, cancellationToken);

            if (sessionBlocked && !SettingsPage.ForceScheduledStart)
            {
                await RecordEventAsync(
                    "Timer.Schedule.Skip",
                    $"slot={slot.Index}; reason=session-active-without-force; session={CurrentSessionState}; activeOperation={hasActiveSessionOperation}",
                    cancellationToken);
                return;
            }

            resetOneShotAfterAttempt = true;

            var force = SettingsPage.ForceScheduledStart;

            // WPF parity (TaskQueueViewModel.HandleScheduledStart): with ForceScheduledStart and
            // CustomConfig enabled, a mismatched current configuration skips the scheduled start.
            // The T-2 minute pre-switch is expected to have aligned the configuration already.
            if (force && sessionRunning && SettingsPage.CustomTimerConfig && !IsCurrentProfileMatchingSlot(slot))
            {
                var currentProfile = _runtime.ConfigurationService.CurrentConfig.CurrentProfile;
                await RecordEventAsync(
                    "Timer.Schedule.Skip",
                    $"slot={slot.Index}; reason=profile-mismatch; current={currentProfile}; expected={slot.Profile}",
                    cancellationToken);
                PushGrowl(RootTexts.GetOrDefault(
                    "Timer.Schedule.ProfileMismatchWarning",
                    "定时启动已跳过：当前配置与定时配置不一致。"));
                return;
            }

            if (force)
            {
                if (SettingsPage.ShowWindowBeforeForceScheduledStart)
                {
                    await Dispatcher.UIThread.InvokeAsync(
                        ShowAndActivateMainWindow,
                        DispatcherPriority.Send,
                        cancellationToken);
                    PushGrowl("定时触发：强制执行前显示窗口。");
                }

                // WPF parity (TaskQueueViewModel.TimerCanceledAsync): force scheduled start shows
                // a 10-second countdown that can cancel the run before anything is stopped.
                if (await ConfirmForceScheduledStartCanceledAsync(cancellationToken))
                {
                    await RecordEventAsync(
                        "Timer.Schedule.Canceled",
                        $"slot={slot.Index}; reason=user-canceled-countdown",
                        cancellationToken);
                    return;
                }
            }

            if (sessionRunning)
            {
                await StopAsync(cancellationToken, userInitiated: false);
                CurrentSessionState = _runtime.SessionService.CurrentState;
            }
            else if (CurrentSessionState is SessionState.Connecting or SessionState.Connected)
            {
                // Forced scheduled start must restart from a clean idle session: stop the
                // pending/active connection before switching profiles and starting.
                var stopConnectingResult = await _runtime.ConnectFeatureService.StopAsync(cancellationToken);
                if (!stopConnectingResult.Success)
                {
                    await RecordTimerScheduleErrorAsync(
                        $"slot={slot.Index}; failed to stop non-idle session before forced scheduled start: {stopConnectingResult.Message}",
                        cancellationToken: cancellationToken);
                    return;
                }

                CurrentSessionState = _runtime.SessionService.CurrentState;
            }

            if (CurrentSessionState is SessionState.Connecting or SessionState.Running or SessionState.Stopping)
            {
                await RecordTimerScheduleErrorAsync(
                    $"slot={slot.Index}; stop failed before force scheduled restart; lastError={LastError}",
                    cancellationToken: cancellationToken);
                return;
            }

            IDisposable? forcedStartLease = null;
            if (force)
            {
                scheduledStartCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Volatile.Write(ref _scheduledStartCancellation, scheduledStartCancellation);
                await RecordEventAsync(
                    "Timer.Schedule.WaitForIdle",
                    $"slot={slot.Index}; waiting for active session operation before forced scheduled start",
                    cancellationToken);
                await AppendCloseDownForForceScheduledRestartAsync(slot, cancellationToken);
                forcedStartLease = await AcquireExclusiveSessionOperationAsync(
                    "Timer.ForceScheduledStart",
                    cancellationToken);
                if (forcedStartLease is null)
                {
                    await RecordTimerScheduleErrorAsync(
                        $"slot={slot.Index}; timed out waiting for active session operation before forced scheduled start.",
                        cancellationToken: cancellationToken);
                    return;
                }
            }


            // WPF parity (TaskQueueViewModel.HandleScheduledStart): a non-forced scheduled start
            // goes straight to LinkStart without switching or validating the configuration; the
            // configuration switch only happens at the T-2 pre-switch (HandleConfigChange). In
            // the force path the switch happens here while the forced-start lease is held.
            var timerStartLease = forcedStartLease;
            var scheduledStartToken = scheduledStartCancellation?.Token ?? cancellationToken;
            try
            {
                if (force && !await SwitchTimerProfileIfNeededAsync(slot, scheduledStartToken))
                {
                    return;
                }

                if (scheduledStartToken.IsCancellationRequested)
                {
                    await RecordEventAsync(
                        "Timer.Schedule.Canceled",
                        $"slot={slot.Index}; reason=central-stop",
                        CancellationToken.None);
                    return;
                }

                timerStartLease ??= _connectionGameSharedState.TryBeginExclusiveSessionOperation(
                    "Timer.ScheduledStart");
                if (timerStartLease is null)
                {
                    await RecordTimerScheduleErrorAsync(
                        $"slot={slot.Index}; another session operation became active before the scheduled start.",
                        cancellationToken: cancellationToken);
                    return;
                }

                await TaskQueuePage.StartAsyncUnderExclusiveSessionOperationAsync(timerStartLease, scheduledStartToken);
                CurrentSessionState = _runtime.SessionService.CurrentState;
            if (CurrentSessionState == SessionState.Running)
            {
                _ = _runtime.AchievementTrackerService.AddProgressToGroup("ScheduleMaster");
                await RecordEventAsync(
                    sessionRunning || SettingsPage.ForceScheduledStart
                        ? "Timer.Schedule.StopAndStart"
                        : "Timer.Schedule.Start",
                    $"slot={slot.Index}; profile={_runtime.ConfigurationService.CurrentConfig.CurrentProfile}",
                    cancellationToken);
                return;
            }

                await RecordTimerScheduleErrorAsync(
                    sessionRunning
                        ? $"slot={slot.Index}; start failed after forced stop; lastError={LastError}"
                        : $"slot={slot.Index}; start failed; lastError={LastError}",
                    cancellationToken: cancellationToken);
            }
            finally
            {
                // The page treats a supplied lease as caller-owned. A non-forced scheduled
                // start creates its own lease here, so it must be released after the start;
                // otherwise the next slot is permanently blocked by a stale exclusive lease.
                if (!force)
                {
                    timerStartLease?.Dispose();
                }

                forcedStartLease?.Dispose();
            }
        }
        finally
        {
            if (resetOneShotAfterAttempt && resetOneShot && slot.Enabled is null)
            {
                slot.Enabled = false;
                await SettingsPage.SaveTimerSettingsAsync(cancellationToken);
            }

            if (scheduledStartCancellation is not null
                && ReferenceEquals(Volatile.Read(ref _scheduledStartCancellation), scheduledStartCancellation))
            {
                Volatile.Write(ref _scheduledStartCancellation, null);
            }

            scheduledStartCancellation?.Dispose();
        }
    }

    private async Task PreSwitchTimerProfileAsync(
        TimerSlotViewModel slot,
        string minuteKey,
        CancellationToken cancellationToken)
    {
        CurrentSessionState = _runtime.SessionService.CurrentState;
        var sessionRunning = CurrentSessionState is SessionState.Running or SessionState.Stopping;

        // WPF parity (TaskQueueViewModel.HandleTimerLogic/HandleConfigChange): the T-2 minute
        // configuration pre-switch requires CustomConfig and (idle || ForceScheduledStart).
        if (sessionRunning && !SettingsPage.ForceScheduledStart)
        {
            return;
        }

        if (!SettingsPage.CustomTimerConfig)
        {
            return;
        }

        var targetProfile = slot.Profile?.Trim() ?? string.Empty;
        var currentProfile = _runtime.ConfigurationService.CurrentConfig.CurrentProfile;
        if (string.IsNullOrWhiteSpace(targetProfile)
            || string.Equals(currentProfile, targetProfile, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await RecordEventAsync(
            "Timer.Schedule.PreSwitchProfile",
            $"slot={slot.Index}; minute={minuteKey}; from={currentProfile}; to={targetProfile}",
            cancellationToken);
        await SwitchTimerProfileIfNeededAsync(slot, cancellationToken);
    }

    private bool IsCurrentProfileMatchingSlot(TimerSlotViewModel slot)
    {
        var targetProfile = slot.Profile?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(targetProfile))
        {
            return false;
        }

        return string.Equals(
            _runtime.ConfigurationService.CurrentConfig.CurrentProfile,
            targetProfile,
            StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> ConfirmForceScheduledStartCanceledAsync(CancellationToken cancellationToken)
    {
        var request = new WarningConfirmDialogRequest(
            Title: RootTexts.GetOrDefault("Timer.Schedule.ForceCountdown.Title", "定时启动"),
            Message: RootTexts.GetOrDefault(
                "Timer.Schedule.ForceCountdown.Message",
                "定时启动将在倒计时结束后强制执行：正在运行的任务会被停止并重新开始。"),
            ConfirmText: RootTexts.GetOrDefault("Timer.Schedule.ForceCountdown.Confirm", "立即开始"),
            CancelText: RootTexts.GetOrDefault("Timer.Schedule.ForceCountdown.Cancel", "取消"),
            Language: CurrentShellLanguage,
            CountdownSeconds: 10);
        var completion = await _dialogService.ShowWarningConfirmAsync(
            request,
            "Timer.Schedule.ForceCountdown",
            cancellationToken);

        // WPF auto-proceeds when the countdown elapses; only an explicit cancel aborts the run.
        // Confirm/Close/dialog-unavailable all proceed (headless environments never block).
        return completion.Return == DialogReturnSemantic.Cancel;
    }

    private async Task AppendCloseDownForForceScheduledRestartAsync(
        TimerSlotViewModel slot,
        CancellationToken cancellationToken)
    {
        if (_runtime.CoreBridge is not { SupportsStartCloseDown: true } bridge)
        {
            return;
        }

        // WPF parity (TaskQueueViewModel.HandleScheduledStart -> AsstAppendCloseDown): a forced
        // restart appends a CloseDown task so the game client is closed before the new queue
        // starts. The task is appended only; the following StartAsync runs it first.
        var clientType = _connectionGameSharedState.ClientType?.Trim() ?? string.Empty;
        var payload = new JsonObject
        {
            ["client_type"] = clientType,
        }.ToJsonString();
        var append = await bridge.AppendTaskAsync(
            new CoreTaskRequest("CloseDown", "CloseDown", true, payload),
            cancellationToken);
        if (!append.Success)
        {
            await RecordEventAsync(
                "Timer.Schedule.CloseDownFailed",
                $"slot={slot.Index}; error={append.Error?.Message}",
                cancellationToken);
            TaskQueuePage.AppendSystemLog(
                RootTexts.GetOrDefault("Timer.Schedule.CloseDownFailed", "关闭游戏失败。"),
                "WARN");
            return;
        }

        await RecordEventAsync(
            "Timer.Schedule.CloseDown",
            $"slot={slot.Index}; clientType={clientType}",
            cancellationToken);
    }

    private async Task<bool> SwitchTimerProfileIfNeededAsync(TimerSlotViewModel slot, CancellationToken cancellationToken)
    {
        if (!SettingsPage.CustomTimerConfig)
        {
            return true;
        }

        var targetProfile = slot.Profile?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(targetProfile))
        {
            await RecordTimerScheduleErrorAsync(
                $"slot={slot.Index}; custom config enabled but profile is empty.",
                cancellationToken: cancellationToken);
            return false;
        }

        var config = _runtime.ConfigurationService.CurrentConfig;
        if (!config.Profiles.ContainsKey(targetProfile))
        {
            await RecordTimerScheduleErrorAsync(
                $"slot={slot.Index}; profile `{targetProfile}` does not exist.",
                cancellationToken: cancellationToken);
            return false;
        }

        if (string.Equals(config.CurrentProfile, targetProfile, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var previousClientType = _connectionGameSharedState.ClientType;
        try
        {
            var failedNames = await FlushConfigurationSavesForCloseAsync(cancellationToken, forProfileSwitch: true);
            if (failedNames.Count > 0)
            {
                await RecordTimerScheduleErrorAsync(
                    $"slot={slot.Index}; failed to flush pending changes before switching profile: {string.Join(", ", failedNames)}",
                    cancellationToken: cancellationToken);
                return false;
            }

            var switchResult = await _runtime.ConfigurationProfileFeatureService.SwitchProfileAsync(
                targetProfile,
                cancellationToken);
            if (!switchResult.Success)
            {
                await RecordTimerScheduleErrorAsync(
                    $"slot={slot.Index}; failed to switch profile to `{targetProfile}`: {switchResult.Message}",
                    cancellationToken: cancellationToken);
                return false;
            }

            SettingsPage.ConfigurationManagerSelectedProfile = targetProfile;
            await TaskQueuePage.ReloadConfigurationContextAsync(cancellationToken: cancellationToken);
            await TaskQueuePage.WaitForPendingBindingAsync(cancellationToken);
            SyncConnectionFromProfile();
            await ReloadClientResourceIfChangedAsync(previousClientType, _connectionGameSharedState.ClientType);
            await RecordEventAsync(
                "Timer.Schedule.SwitchProfile",
                $"slot={slot.Index}; profile={targetProfile}",
                cancellationToken);
            return true;

#if false
            config.CurrentProfile = targetProfile;
            var saved = await ConfigurationSaveTracker.Instance.RunTrackedAsync(
                "Timer.Schedule.SwitchProfile",
                "定时执行配置",
                "Timer.Schedule.SwitchProfile.Save",
                _runtime.DiagnosticsService,
                async ct =>
                {
                    await _runtime.ConfigurationService.SaveAsync(ct);
                    return true;
                },
                cancellationToken);
            if (!saved)
            {
                return false;
            }

            await TaskQueuePage.ReloadTasksAsync(cancellationToken, seedDefaultsWhenEmpty: true);
            await TaskQueuePage.WaitForPendingBindingAsync(cancellationToken);
            SyncConnectionFromProfile();
            await RecordEventAsync(
                "Timer.Schedule.SwitchProfile",
                $"slot={slot.Index}; profile={targetProfile}",
                cancellationToken);
            return true;
#endif
        }
        catch (Exception ex)
        {
            await RecordTimerScheduleErrorAsync(
                $"slot={slot.Index}; failed to switch profile to `{targetProfile}`.",
                ex,
                cancellationToken);
            return false;
        }
    }

    private async Task RecordTimerScheduleErrorAsync(
        string message,
        Exception? exception = null,
        CancellationToken cancellationToken = default)
    {
        LastError = message;
        await RecordErrorAsync(
            "Timer.Schedule.Error",
            message,
            exception,
            cancellationToken);
        await RecordFailedResultAsync(
            "Timer.Schedule.Error",
            UiOperationResult.Fail(UiErrorCode.UiOperationFailed, message, exception?.ToString()),
            cancellationToken);
    }

    private static bool TryParseTimerTime(string? value, out int hour, out int minute)
    {
        hour = default;
        minute = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (normalized.Length != 5 || normalized[2] != ':')
        {
            return false;
        }

        if (!int.TryParse(normalized.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out hour))
        {
            return false;
        }

        if (!int.TryParse(normalized.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out minute))
        {
            return false;
        }

        if (hour is < 0 or > 23)
        {
            return false;
        }

        if (minute is < 0 or > 59)
        {
            return false;
        }

        return true;
    }

    private void OnGuiSettingsApplied(object? sender, GuiSettingsAppliedEventArgs e)
    {
        Dispatcher.UIThread.Post(() => _ = ApplyGuiSettingsAsync(e.Snapshot));
    }

    private void OnGuiSettingsPreviewChanged(object? sender, GuiSettingsPreviewChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() => _ = ApplyGuiSettingsAsync(e.Snapshot));
    }

    private async Task ApplyGuiSettingsAsync(GuiSettingsSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var lockAcquired = false;
        try
        {
            await _guiApplySemaphore.WaitAsync(cancellationToken);
            lockAcquired = true;

            AppliedTheme = snapshot.Theme;
            EffectiveUiScaleFactor = ComputeEffectiveUiScaleFactor(
                snapshot.UiScalePercent,
                OperatingSystem.IsWindows(),
                OperatingSystem.IsMacOS());
            _rootLogTimeFormat = snapshot.LogItemDateFormatString;
            RootTexts.Language = CurrentShellLanguage;
            RefreshRootTextState();
            if (Avalonia.Application.Current is not null)
            {
                Avalonia.Application.Current.RequestedThemeVariant =
                    string.Equals(snapshot.Theme, "Dark", StringComparison.OrdinalIgnoreCase)
                        ? ThemeVariant.Dark
                        : string.Equals(snapshot.Theme, "SyncWithOs", StringComparison.OrdinalIgnoreCase)
                            ? ThemeVariant.Default
                            : ThemeVariant.Light;
            }

            ShowCloseButton = !snapshot.HideCloseButton;
            TaskQueuePage.ApplyGuiSettingsPreview(snapshot);
            ApplyWindowTitleScrolling(snapshot.WindowTitleScrollable);
            RefreshWindowTitle();
            SettingsPage.AutostartStatus = PlatformCapabilityTextMap.FormatAutostartStatus(
                CurrentShellLanguage,
                SettingsPage.StartSelf,
                ReportLocalizationFallback);

            ShellBackgroundOpacity = snapshot.BackgroundOpacity / 100d;
            ShellBackgroundBlur = snapshot.BackgroundBlur;
            ShellBackgroundStretch = ParseStretch(snapshot.BackgroundStretchMode);
            ApplyShellBackgroundImage(snapshot.BackgroundImagePath);
            await RecordEventAsync(
                "App.Gui.Apply.UiScale",
                $"platform={(OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "MacOS" : "Other")}; uiScalePercent={snapshot.UiScalePercent}; platformBase={(OperatingSystem.IsMacOS() ? "0.81" : OperatingSystem.IsWindows() ? "0.9" : "1.0")}; effectiveUiScale={EffectiveUiScaleFactor.ToString("0.###", CultureInfo.InvariantCulture)}",
                cancellationToken);

            await RefreshCapabilitySummaryAsync(cancellationToken);

            var trayRefresh = await _runtime.PlatformCapabilityService.InitializeTrayAsync(
                AppDisplayName,
                PlatformCapabilityTextMap.CreateTrayMenuText(CurrentShellLanguage, ReportLocalizationFallback),
                cancellationToken);
            if (!trayRefresh.Success)
            {
                LastError = trayRefresh.Message;
                await RecordFailedResultAsync(
                    "App.Gui.Apply.TrayRefresh",
                    trayRefresh,
                    cancellationToken);
                await _runtime.DialogFeatureService.ReportErrorAsync(
                    "App.Gui.Apply.TrayRefresh",
                    trayRefresh,
                    cancellationToken);
            }

            var trayVisibility = await _runtime.PlatformCapabilityService.SetTrayVisibleAsync(
                snapshot.UseTray,
                cancellationToken);
            if (!trayVisibility.Success)
            {
                LastError = trayVisibility.Message;
                await RecordFailedResultAsync(
                    "App.Gui.Apply.TrayVisibility",
                    trayVisibility,
                    cancellationToken);
                await _runtime.DialogFeatureService.ReportErrorAsync(
                    "App.Gui.Apply.TrayVisibility",
                    trayVisibility,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // No-op for canceled apply requests.
        }
        catch (Exception ex)
        {
            await RecordUnhandledExceptionAsync(
                "App.Gui.Apply",
                ex,
                UiErrorCode.SettingsSaveFailed,
                $"GUI apply failed: {ex.Message}",
                cancellationToken);
        }
        finally
        {
            if (lockAcquired)
            {
                _guiApplySemaphore.Release();
            }
        }
    }

    private void ApplyShellBackgroundImage(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            ShellBackgroundImage = null;
            _appliedBackgroundImagePath = null;
            return;
        }

        // WPF parity (BackgroundSettingsUserControlModel.RefreshBackgroundImage): a missing file
        // means "no background rendered"; it is not an error worth logging on every GUI apply.
        if (!File.Exists(path))
        {
            ShellBackgroundImage = null;
            _appliedBackgroundImagePath = null;
            return;
        }

        // Background draft previews (opacity/blur/stretch) re-apply the unchanged path; reuse the
        // already decoded bitmap instead of re-decoding on every preview step.
        if (ShellBackgroundImage is not null
            && string.Equals(_appliedBackgroundImagePath, path, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            ShellBackgroundImage = new Bitmap(path);
            _appliedBackgroundImagePath = path;
        }
        catch (Exception ex)
        {
            ShellBackgroundImage = null;
            _appliedBackgroundImagePath = null;
            LastError = $"Background image load failed: {ex.Message}";
            _ = RecordErrorAsync(
                "App.Gui.Apply.Background",
                LastError,
                ex);
            _ = RecordFailedResultAsync(
                "App.Gui.Apply.Background",
                UiOperationResult.Fail(UiErrorCode.BackgroundImagePathNotFound, LastError, ex.ToString()));
        }
    }

    private static Stretch ParseStretch(string stretch)
    {
        return Enum.TryParse<Stretch>(stretch, ignoreCase: true, out var parsed)
            ? parsed
            : Stretch.UniformToFill;
    }

    private void RefreshConfigValidationState(IReadOnlyList<ConfigValidationIssue> issues)
    {
        var blockingIssues = issues.Where(i => i.Blocking).ToArray();
        ConfigIssueDetails.Clear();
        foreach (var issue in blockingIssues)
        {
            ConfigIssueDetails.Add(new ConfigIssueDetailItem
            {
                Scope = NormalizeIssueText(issue.Scope),
                Code = NormalizeIssueText(issue.Code),
                Field = NormalizeIssueText(issue.Field),
                Blocking = issue.Blocking,
                ProfileName = NormalizeIssueText(issue.ProfileName),
                TaskIndex = issue.TaskIndex?.ToString(CultureInfo.InvariantCulture) ?? "-",
                TaskName = NormalizeIssueText(issue.TaskName),
                Message = NormalizeIssueText(issue.Message),
                SuggestedAction = NormalizeIssueText(issue.SuggestedAction),
            });
        }

        BlockingConfigIssueCount = blockingIssues.Length;
        HasBlockingConfigIssues = BlockingConfigIssueCount > 0;
        _ = SyncTrayMenuStateAsync();
    }

    private async Task ReportValidationIssuesIfAnyAsync(
        IReadOnlyList<ConfigValidationIssue> issues,
        string scope,
        CancellationToken cancellationToken)
    {
        if (issues.Count == 0)
        {
            return;
        }

        var blockingCount = issues.Count(i => i.Blocking);
        var warningCount = issues.Count - blockingCount;
        var summary = $"配置校验异常: 阻断 {blockingCount} / 预警 {warningCount}";
        LastError = string.Join(
            "; ",
            issues.Take(3).Select(i => $"{i.Code}:{i.Field}:{i.Message}"));
        await RecordFailedResultAsync(
            scope,
            UiOperationResult.Fail(UiErrorCode.TaskValidationFailed, $"{summary} | {LastError}"),
            cancellationToken);
    }

    private async Task ShowConfigRepairNoticeIfNeededAsync(
        ConfigLoadResult loadResult,
        CancellationToken cancellationToken = default)
    {
        var notice = loadResult.ConfigRepairNotice;
        if (_configRepairNoticeShown || notice is null)
        {
            return;
        }

        _configRepairNoticeShown = true;
        try
        {
            var noticeBody = string.Format(
                CultureInfo.CurrentCulture,
                RootTexts.GetOrDefault(
                    "Main.ConfigRepair.Dialog.Prompt",
                    "Problems were found while reading the configuration and an automatic recovery was applied.{2}{0}{2}Suggested action: {1}"),
                notice.Message,
                notice.SuggestedAction,
                Environment.NewLine);
            var chrome = DialogTextCatalog.CreateRootCatalog(
                CurrentShellLanguage,
                "Root.Localization.MainShell",
                texts => new DialogChromeSnapshot(
                    title: texts.GetOrDefault(
                        "Main.ConfigRepair.Dialog.Title",
                        "Configuration Recovery Notice"),
                    confirmText: texts.GetOrDefault(
                        "Main.ConfigRepair.Dialog.Confirm",
                        "I Understand"),
                    cancelText: texts.GetOrDefault(
                        "Main.ConfigRepair.Dialog.Cancel",
                        "Close")));
            var chromeSnapshot = chrome.GetSnapshot();
            var completion = await _dialogService.ShowTextAsync(
                new TextDialogRequest(
                    Title: chromeSnapshot.Title,
                    Prompt: notice.Message,
                    DefaultText: noticeBody,
                    MultiLine: true,
                    ReadOnlyContent: true,
                    ConfirmText: chromeSnapshot.ConfirmText ?? RootTexts.GetOrDefault("Main.ConfigRepair.Dialog.Confirm", "I Understand"),
                    CancelText: chromeSnapshot.CancelText ?? RootTexts.GetOrDefault("Main.ConfigRepair.Dialog.Cancel", "Close"),
                    Chrome: chrome),
                "App.Shell.Config.Repair",
                cancellationToken);
            await RecordEventAsync(
                "Config.Repair.Notice",
                $"removed={notice.RemovedJsonPaths.Count}; rebuiltDefaults={notice.RebuiltDefaults}; backup={notice.BrokenConfigBackupPath ?? "-"}; return={completion.Return}; summary={completion.Summary}",
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await RecordErrorAsync(
                "Config.Repair.DialogError",
                "Failed to show config recovery notice dialog.",
                ex,
                cancellationToken);
        }
    }

    private async Task ShowSchemaMigrationNoticeIfNeededAsync(
        ConfigLoadResult loadResult,
        CancellationToken cancellationToken = default)
    {
        var notice = loadResult.SchemaMigrationNotice;
        if (_schemaMigrationNoticeShown || notice is null)
        {
            return;
        }

        _schemaMigrationNoticeShown = true;
        try
        {
            var noticeBody = string.Format(
                CultureInfo.CurrentCulture,
                RootTexts.GetOrDefault(
                    "Main.SchemaMigration.Dialog.Prompt",
                    "The configuration schema is older than the latest version.{4}Current version: v{0}{4}Latest version: v{1}{4}{4}{2}{4}Suggested action: {3}{4}A backup named avalonia.json.schema-v{0}.bak.<timestamp> will be created before writing the latest schema."),
                notice.CurrentSchemaVersion,
                notice.LatestSchemaVersion,
                notice.Message,
                notice.SuggestedAction,
                Environment.NewLine);
            var chrome = DialogTextCatalog.CreateRootCatalog(
                CurrentShellLanguage,
                "Root.Localization.MainShell",
                texts => new DialogChromeSnapshot(
                    title: texts.GetOrDefault(
                        "Main.SchemaMigration.Dialog.Title",
                        "Configuration Schema Migration Notice"),
                    confirmText: texts.GetOrDefault(
                        "Main.SchemaMigration.Dialog.Confirm",
                        "I Understand"),
                    cancelText: texts.GetOrDefault(
                        "Main.SchemaMigration.Dialog.Cancel",
                        "Close")));
            var chromeSnapshot = chrome.GetSnapshot();
            var completion = await _dialogService.ShowTextAsync(
                new TextDialogRequest(
                    Title: chromeSnapshot.Title,
                    Prompt: notice.Message,
                    DefaultText: noticeBody,
                    MultiLine: true,
                    ReadOnlyContent: true,
                    ConfirmText: chromeSnapshot.ConfirmText ?? RootTexts.GetOrDefault("Main.SchemaMigration.Dialog.Confirm", "I Understand"),
                    CancelText: chromeSnapshot.CancelText ?? RootTexts.GetOrDefault("Main.SchemaMigration.Dialog.Cancel", "Close"),
                    Chrome: chrome),
                "App.Shell.Config.SchemaMigration",
                cancellationToken);
            await RecordEventAsync(
                "Config.SchemaMigration.Notice",
                $"schema={notice.CurrentSchemaVersion}->{notice.LatestSchemaVersion}; return={completion.Return}; summary={completion.Summary}",
                cancellationToken);
            if (string.Equals(completion.Summary, "dialog-service-unavailable", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(completion.Summary, "owner-unavailable", StringComparison.OrdinalIgnoreCase))
            {
                await RecordEventAsync(
                    "Config.SchemaMigration.DialogUnavailable",
                    $"schema={notice.CurrentSchemaVersion}->{notice.LatestSchemaVersion}",
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await RecordErrorAsync(
                "Config.SchemaMigration.DialogError",
                "Failed to show schema migration notice dialog.",
                ex,
                cancellationToken);
        }
    }

    private async Task RefreshCapabilitySummaryAsync(CancellationToken cancellationToken = default)
    {
        var snapshotResult = await _runtime.PlatformCapabilityService.GetSnapshotAsync(cancellationToken);
        if (!snapshotResult.Success || snapshotResult.Value is null)
        {
            CapabilitySummary = PlatformCapabilityTextMap.FormatSnapshotUnavailable(
                CurrentShellLanguage,
                snapshotResult.Message,
                ReportLocalizationFallback);
            return;
        }

        var snapshot = snapshotResult.Value;
        var lang = CurrentShellLanguage;
        CapabilitySummary = string.Join(
            Environment.NewLine,
            BuildCapabilityLine(lang, PlatformCapabilityId.Tray, snapshot.Tray),
            BuildCapabilityLine(lang, PlatformCapabilityId.Notification, snapshot.Notification),
            BuildCapabilityLine(lang, PlatformCapabilityId.Hotkey, snapshot.Hotkey),
            BuildCapabilityLine(lang, PlatformCapabilityId.Autostart, snapshot.Autostart),
            BuildCapabilityLine(lang, PlatformCapabilityId.Overlay, snapshot.Overlay));
    }

    public async Task SetTrayVisibleAsync(bool visible, CancellationToken cancellationToken = default)
    {
        var result = await _runtime.PlatformCapabilityService.SetTrayVisibleAsync(visible, cancellationToken);
        if (!await ApplyResultAsync(result, "App.Shell.Tray.SetVisible", cancellationToken))
        {
            PushGrowl(result.Message);
            return;
        }

        PushGrowl(result.Message);
    }

    public async Task SetTrayHideCommandVisibleAsync(bool visible, CancellationToken cancellationToken = default)
    {
        if (_trayHideCommandVisible == visible)
        {
            return;
        }

        _trayHideCommandVisible = visible;
        await SyncTrayMenuStateAsync(cancellationToken);
    }

    public async Task ToggleOverlayFromTrayAsync(CancellationToken cancellationToken = default)
    {
        var scope = "App.Shell.Tray.ToggleOverlay";
        try
        {
            OverlayPresentation.RefreshResolvedSource();
            await TaskQueuePage.ToggleOverlayAsync(cancellationToken);
            await SyncTrayMenuStateAsync(cancellationToken);

            var message = TaskQueuePage.OverlayMode switch
            {
                OverlayRuntimeMode.Native => "Overlay 已开启。",
                OverlayRuntimeMode.Preview => "Overlay 已切换为预览模式。",
                _ => "Overlay 已关闭。",
            };
            PushGrowl(message);
            await RecordEventAsync(
                scope,
                $"visible={TaskQueuePage.OverlayVisible}; mode={TaskQueuePage.OverlayMode}",
                cancellationToken);
        }
        catch (Exception ex)
        {
            await RecordUnhandledExceptionAsync(
                scope,
                ex,
                UiErrorCode.PlatformOperationFailed,
                "Toggle overlay from tray failed.",
                cancellationToken);
            PushGrowl(ex.Message);
        }
    }

    public async Task ToggleOverlayFromTaskQueueAsync(CancellationToken cancellationToken = default)
    {
        OverlayPresentation.PreferTaskQueue();
        await TaskQueuePage.ToggleOverlayAsync(cancellationToken);
    }

    public async Task PickOverlayTargetFromTaskQueueAsync(CancellationToken cancellationToken = default)
    {
        OverlayPresentation.PreferTaskQueue();
        await TaskQueuePage.PickOverlayTargetWithDialogAsync(cancellationToken);
    }

    public async Task ToggleOverlayFromCopilotAsync(CancellationToken cancellationToken = default)
    {
        OverlayPresentation.PreferCopilot();
        await TaskQueuePage.ToggleOverlayAsync(cancellationToken);
    }

    public async Task PickOverlayTargetFromCopilotAsync(CancellationToken cancellationToken = default)
    {
        OverlayPresentation.PreferCopilot();
        await TaskQueuePage.PickOverlayTargetWithDialogAsync(cancellationToken);
    }

    private void OnUiLanguageCoordinatorLanguageChanged(object? sender, UiLanguageChangedEventArgs e)
    {
        _pendingLanguageApplyTask = ScheduleCoordinatedLanguageChangeAsync(e.PreviousLanguage, e.CurrentLanguage);
    }

    private Task ScheduleCoordinatedLanguageChangeAsync(
        string previousLanguage,
        string nextLanguage)
    {
        if (Avalonia.Application.Current is null)
        {
            return ApplyCoordinatedLanguageChangeAsync(previousLanguage, nextLanguage);
        }

        return Dispatcher.UIThread.InvokeAsync(
            () => ApplyCoordinatedLanguageChangeAsync(previousLanguage, nextLanguage),
            DispatcherPriority.Send);
    }

    private async Task ApplyCoordinatedLanguageChangeAsync(
        string previousLanguage,
        string nextLanguage,
        CancellationToken cancellationToken = default)
    {
        var total = Stopwatch.StartNew();
        var step = Stopwatch.StartNew();
        try
        {
            CurrentShellLanguage = nextLanguage;
            _runtime.AchievementTrackerService.SetCurrentLanguage(nextLanguage);
            RootTexts.Language = nextLanguage;
            _ = RecordLanguageTimingAsync(
                "Shell.ApplyLanguage.Root",
                step,
                previousLanguage,
                nextLanguage,
                cancellationToken);
            await YieldForBlockingOperationOverlayFrameAsync();

            step.Restart();
            TaskQueuePage.SetLanguage(nextLanguage);
            _ = RecordLanguageTimingAsync(
                "Shell.ApplyLanguage.TaskQueue",
                step,
                previousLanguage,
                nextLanguage,
                cancellationToken);
            await YieldForBlockingOperationOverlayFrameAsync();

            step.Restart();
            if (TryGetCopilotPage(out var copilotPage))
            {
                copilotPage.SetLanguage(nextLanguage);
                _ = RecordLanguageTimingAsync(
                    "Shell.ApplyLanguage.Copilot",
                    step,
                    previousLanguage,
                    nextLanguage,
                    cancellationToken,
                    ("loaded", true));
            }
            else
            {
                _ = RecordLanguageTimingAsync(
                    "Shell.ApplyLanguage.Copilot",
                    step,
                    previousLanguage,
                    nextLanguage,
                    cancellationToken,
                    ("loaded", false));
            }

            await YieldForBlockingOperationOverlayFrameAsync();

            step.Restart();
            if (TryGetToolboxPage(out var toolboxPage))
            {
                toolboxPage.SetLanguage(nextLanguage);
                _ = RecordLanguageTimingAsync(
                    "Shell.ApplyLanguage.Toolbox",
                    step,
                    previousLanguage,
                    nextLanguage,
                    cancellationToken,
                    ("loaded", true));
            }
            else
            {
                _ = RecordLanguageTimingAsync(
                    "Shell.ApplyLanguage.Toolbox",
                    step,
                    previousLanguage,
                    nextLanguage,
                    cancellationToken,
                    ("loaded", false));
            }

            await YieldForBlockingOperationOverlayFrameAsync();

            step.Restart();
            if (TryGetSettingsPage(out var settingsPage))
            {
                settingsPage.Language = nextLanguage;
                settingsPage.AutostartStatus = PlatformCapabilityTextMap.FormatAutostartStatus(
                    nextLanguage,
                    settingsPage.StartSelf,
                    ReportLocalizationFallback);
                ApplySettingsUpdateAvailabilityState(settingsPage);
                _ = RecordLanguageTimingAsync(
                    "Shell.ApplyLanguage.SettingsShellState",
                    step,
                    previousLanguage,
                    nextLanguage,
                    cancellationToken,
                    ("loaded", true));
            }
            else
            {
                _ = RecordLanguageTimingAsync(
                    "Shell.ApplyLanguage.SettingsShellState",
                    step,
                    previousLanguage,
                    nextLanguage,
                    cancellationToken,
                    ("loaded", false));
            }

            await YieldForBlockingOperationOverlayFrameAsync();

            step.Restart();
            RefreshRootTextState();
            await RefreshCapabilitySummaryAsync(cancellationToken);
            _ = RecordLanguageTimingAsync(
                "Shell.ApplyLanguage.CapabilitySummary",
                step,
                previousLanguage,
                nextLanguage,
                cancellationToken);

            step.Restart();
            var trayRefresh = await _runtime.PlatformCapabilityService.InitializeTrayAsync(
                AppDisplayName,
                PlatformCapabilityTextMap.CreateTrayMenuText(nextLanguage, ReportLocalizationFallback),
                cancellationToken);
            _ = RecordLanguageTimingAsync(
                "Shell.ApplyLanguage.TrayInitialize",
                step,
                previousLanguage,
                nextLanguage,
                cancellationToken,
                ("success", trayRefresh.Success));

            step.Restart();
            if (await ApplyResultAsync(trayRefresh, "App.Shell.SwitchLanguage.TrayRefresh", cancellationToken)
                && !string.Equals(previousLanguage, nextLanguage, StringComparison.OrdinalIgnoreCase))
            {
                _ = _runtime.AchievementTrackerService.Unlock("Linguist");
            }

            _ = RecordLanguageTimingAsync(
                "Shell.ApplyLanguage.TrayApplyResult",
                step,
                previousLanguage,
                nextLanguage,
                cancellationToken);
        }
        finally
        {
            _ = RecordLanguageTimingAsync(
                "Shell.ApplyLanguage.Total",
                total,
                previousLanguage,
                nextLanguage,
                cancellationToken);
        }
    }

    private static Task YieldForBlockingOperationOverlayFrameAsync()
    {
        if (Avalonia.Application.Current is null || !Dispatcher.UIThread.CheckAccess())
        {
            return Task.CompletedTask;
        }

        return Dispatcher.UIThread.InvokeAsync(
            static () => { },
            DispatcherPriority.Loaded).GetTask();
    }

    private Task RecordLanguageTimingAsync(
        string scope,
        Stopwatch stopwatch,
        string fromLanguage,
        string targetLanguage,
        CancellationToken cancellationToken,
        params (string Key, object? Value)[] fields)
    {
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["from"] = fromLanguage,
            ["target"] = targetLanguage,
            ["current"] = CurrentShellLanguage,
            ["selectedRoot"] = SelectedRootTabIndex >= 0 && SelectedRootTabIndex < RootTabs.Count
                ? RootTabs[SelectedRootTabIndex]
                : SelectedRootTabIndex.ToString(CultureInfo.InvariantCulture),
        };

        foreach (var (key, value) in fields)
        {
            if (!string.IsNullOrWhiteSpace(key))
            {
                payload[key] = value;
            }
        }

        return _runtime.DiagnosticsService.RecordTemporaryTimingAsync(
            scope,
            stopwatch.Elapsed.TotalMilliseconds,
            payload,
            cancellationToken);
    }

    private async Task SwitchLanguageCoreAsync(
        string? targetLanguage,
        string successScope,
        string? source,
        CancellationToken cancellationToken)
    {
        SetShellBlockingOperationOverlayVisible(true);
        try
        {
            await WaitForBlockingOperationOverlayRenderAsync();
            if (TryGetSettingsPage(out var settingsPage))
            {
                await settingsPage.FlushConfigurationSavesForCloseAsync(cancellationToken);
            }
            var switchResult = await _runtime.ShellFeatureService.SwitchLanguageAsync(
                CurrentShellLanguage,
                targetLanguage,
                cancellationToken);
            if (!switchResult.Success || string.IsNullOrWhiteSpace(switchResult.Value))
            {
                PushGrowl(switchResult.Message);
                _ = await ApplyResultAsync(
                    UiOperationResult.Fail(
                        switchResult.Error?.Code ?? UiErrorCode.LanguageSwitchFailed,
                        switchResult.Message,
                        switchResult.Error?.Details),
                    successScope,
                    cancellationToken);
                return;
            }

            var next = switchResult.Value;
            var changeResult = await ChangeLanguageCoordinatorAsync(next, cancellationToken);
            var appliedLanguage = await ApplyResultAsync(changeResult, successScope, cancellationToken);
            if (appliedLanguage is null)
            {
                return;
            }

            await _pendingLanguageApplyTask;

            PushGrowl($"语言切换为: {next}");

            var message = source is null
                ? $"Language switched to {next}."
                : $"source={source}; target={(string.IsNullOrWhiteSpace(targetLanguage) ? "cycle" : targetLanguage)}; result={next}";
            await RecordEventAsync(
                successScope,
                message,
                cancellationToken);
        }
        finally
        {
            SetShellBlockingOperationOverlayVisible(false);
        }
    }

    private async Task SyncLanguageCoordinatorWithConfigAsync(CancellationToken cancellationToken = default)
    {
        var normalized = StartupShellSnapshot.FromConfig(_runtime.ConfigurationService.CurrentConfig).Language;
        if (string.Equals(_runtime.UiLanguageCoordinator.CurrentLanguage, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var result = await _runtime.UiLanguageCoordinator.ChangeLanguageAsync(normalized, cancellationToken);
        await ApplyResultAsync(result, "App.Shell.Language.SyncFromConfig", cancellationToken);
        await _pendingLanguageApplyTask;
    }

    private Task<UiOperationResult<string>> ChangeLanguageCoordinatorAsync(
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        if (Avalonia.Application.Current is null || !Dispatcher.UIThread.CheckAccess())
        {
            return _runtime.UiLanguageCoordinator.ChangeLanguageAsync(targetLanguage, cancellationToken);
        }

        return Task.Run(
            () => _runtime.UiLanguageCoordinator.ChangeLanguageAsync(targetLanguage, cancellationToken),
            cancellationToken);
    }

    private void RefreshRootPageHostStatusText()
    {
        if (TaskQueueRootPage is null || CopilotRootPage is null || ToolboxRootPage is null || SettingsRootPage is null)
        {
            return;
        }

        RefreshRootPageHostStatusText(TaskQueueRootPage, RootPageStatusKind.TaskQueue);
        RefreshRootPageHostStatusText(CopilotRootPage, RootPageStatusKind.Copilot);
        RefreshRootPageHostStatusText(ToolboxRootPage, RootPageStatusKind.Toolbox);
        RefreshRootPageHostStatusText(SettingsRootPage, RootPageStatusKind.Settings);
    }

    private void RefreshRootPageHostStatusText(RootPageHostViewModel host, RootPageStatusKind page)
    {
        switch (host.LoadState)
        {
            case RootPageLoadState.NotStarted:
            {
                var (title, message) = GetRootPagePendingText(page);
                host.MarkPending(title, message);
                break;
            }

            case RootPageLoadState.Loading:
            {
                var (title, message) = GetRootPageLoadingText(page);
                host.MarkLoading(title, message);
                break;
            }

            case RootPageLoadState.Failed:
            {
                var (title, message) = GetRootPageFailedText(page);
                host.MarkFailed(title, message, host.ErrorMessage);
                break;
            }
        }
    }

    private (string Title, string Message) GetRootPagePendingText(RootPageStatusKind page)
    {
        return page switch
        {
            RootPageStatusKind.TaskQueue => (
                RootTexts.GetOrDefault("Main.RootPage.Pending.FirstScreen.Title", "Preparing first screen"),
                RootTexts.GetOrDefault("Main.RootPage.Pending.FirstScreen.Message", "TaskQueue is preparing its first screen.")),

            RootPageStatusKind.Copilot => (
                RootTexts.GetOrDefault("Main.RootPage.Pending.Background.Title", "Page is initializing in the background"),
                RootTexts.GetOrDefault("Main.RootPage.Pending.Copilot.Message", "Copilot will continue loading after the first screen is ready.")),

            RootPageStatusKind.Toolbox => (
                RootTexts.GetOrDefault("Main.RootPage.Pending.Background.Title", "Page is initializing in the background"),
                RootTexts.GetOrDefault("Main.RootPage.Pending.Toolbox.Message", "Toolbox will continue loading after the first screen is ready.")),

            _ => (
                RootTexts.GetOrDefault("Main.RootPage.Pending.Background.Title", "Page is initializing in the background"),
                RootTexts.GetOrDefault("Main.RootPage.Pending.Settings.Message", "Settings will continue loading after the first screen is ready.")),
        };
    }

    private (string Title, string Message) GetRootPageLoadingText(RootPageStatusKind page)
    {
        return page switch
        {
            RootPageStatusKind.TaskQueue => (
                RootTexts.GetOrDefault("Main.RootPage.Loading.FirstScreen.Title", "Preparing first screen"),
                RootTexts.GetOrDefault("Main.RootPage.Loading.FirstScreen.Message", "TaskQueue is loading.")),

            RootPageStatusKind.Copilot => (
                RootTexts.GetOrDefault("Main.RootPage.Loading.Copilot.Title", "Initializing Copilot in the background"),
                RootTexts.GetOrDefault("Main.RootPage.Loading.Copilot.Message", "Copilot is initializing in the background.")),

            RootPageStatusKind.Toolbox => (
                RootTexts.GetOrDefault("Main.RootPage.Loading.Toolbox.Title", "Initializing Toolbox in the background"),
                RootTexts.GetOrDefault("Main.RootPage.Loading.Toolbox.Message", "Toolbox is initializing in the background.")),

            _ => (
                RootTexts.GetOrDefault("Main.RootPage.Loading.Settings.Title", "Initializing Settings in the background"),
                RootTexts.GetOrDefault("Main.RootPage.Loading.Settings.Message", "Settings is initializing in the background.")),
        };
    }

    private (string Title, string Message) GetRootPageFailedText(RootPageStatusKind page)
    {
        return page switch
        {
            RootPageStatusKind.TaskQueue => (
                RootTexts.GetOrDefault("Main.RootPage.Failed.FirstScreen.Title", "First screen failed to initialize"),
                RootTexts.GetOrDefault("Main.RootPage.Failed.FirstScreen.Message", "TaskQueue failed to initialize the first screen.")),

            RootPageStatusKind.Copilot => (
                RootTexts.GetOrDefault("Main.RootPage.Failed.Copilot.Title", "Copilot failed to initialize"),
                RootTexts.GetOrDefault("Main.RootPage.Failed.Copilot.Message", "Copilot failed to finish initialization.")),

            RootPageStatusKind.Toolbox => (
                RootTexts.GetOrDefault("Main.RootPage.Failed.Toolbox.Title", "Toolbox failed to initialize"),
                RootTexts.GetOrDefault("Main.RootPage.Failed.Toolbox.Message", "Toolbox failed to finish initialization.")),

            _ => (
                RootTexts.GetOrDefault("Main.RootPage.Failed.Settings.Title", "Settings failed to initialize"),
                RootTexts.GetOrDefault("Main.RootPage.Failed.Settings.Message", "Settings failed to finish initialization.")),
        };
    }

    private enum RootPageStatusKind
    {
        TaskQueue,
        Copilot,
        Toolbox,
        Settings,
    }

    public void ReportLocalizationFallback(LocalizationFallbackInfo info)
    {
        var language = UiLanguageCatalog.Normalize(info.Language);
        var key = info.Key?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        var dedupeKey = $"{info.Scope}|{language}|{key}";
        lock (_localizationFallbackGate)
        {
            if (!_reportedLocalizationFallbacks.Add(dedupeKey))
            {
                return;
            }
        }

        _ = RecordEventAsync(
            "Localization.Fallback",
            $"scope={info.Scope}; language={language}; key={key}; fallback={info.FallbackSource}");
    }

    private void OnTaskQueuePagePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(TaskQueuePageViewModel.MainTasksProgress), StringComparison.Ordinal))
        {
            TaskProgress = TaskQueuePage.MainTasksProgress;
        }
    }

    private void OnTaskQueueLocalizationFallbackReported(LocalizationFallbackInfo info)
    {
        ReportLocalizationFallback(info);
    }

    private void OnSessionStateChanged(SessionState state)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            CurrentSessionState = state;
            _ = SyncTrayMenuStateAsync();
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            CurrentSessionState = state;
            _ = SyncTrayMenuStateAsync();
        });
    }

    private void OnOverlayStateChanged(object? sender, OverlayStateChangedEvent e)
    {
        if (Dispatcher.UIThread.CheckAccess() || Avalonia.Application.Current is null)
        {
            ApplyOverlayStateChanged(e);
            return;
        }

        Dispatcher.UIThread.Post(() => ApplyOverlayStateChanged(e));
    }

    private void ApplyOverlayStateChanged(OverlayStateChangedEvent e)
    {
        _overlaySharedState.ApplyRuntimeState(e);
        if (string.Equals(e.Action, "target-lost", StringComparison.Ordinal)
            || string.Equals(e.Action, "fallback-enter", StringComparison.Ordinal))
        {
            PushGrowl(e.Message);
        }

        _ = SyncTrayMenuStateAsync();
    }

    private void AppendRootLogEntry(DateTimeOffset timestamp, string message)
    {
        AppendRootLogEntry($"[{FormatRootLogTimestamp(timestamp)}] {message}");
    }

    private void AppendRootLogEntry(string message)
    {
        RootLogs.Add(message);
        const int maxCount = 400;
        while (RootLogs.Count > maxCount)
        {
            RootLogs.RemoveAt(0);
        }
    }

    private void RefreshShellCloseButtonVisibilityFromConfig()
    {
        var snapshot = StartupShellSnapshot.FromConfig(_runtime.ConfigurationService.CurrentConfig);
        ShowCloseButton = !snapshot.HideCloseButton;
    }

    private void ApplyWindowTitleScrolling(bool enabled)
    {
        _windowTitleScrollable = enabled;
        _windowTitleScrollOffset = 0;
        UpdateWindowTitleDisplay();
    }

    private void OnWindowTitleTickerTick(object? sender, EventArgs e)
    {
        if (!ShouldAnimateWindowTitle())
        {
            UpdateWindowTitleDisplay();
            return;
        }

        _windowTitleScrollOffset = (_windowTitleScrollOffset + 1) % (_windowTitleSource.Length + WindowTitleScrollSpacer.Length);
        UpdateWindowTitleDisplay();
    }

    private void UpdateWindowTitleDisplay()
    {
        if (_windowTitleTicker is null || !ShouldAnimateWindowTitle())
        {
            if (_windowTitleTicker?.IsEnabled == true)
            {
                _windowTitleTicker.Stop();
            }

            WindowTitle = _windowTitleSource;
            return;
        }

        if (!_windowTitleTicker.IsEnabled)
        {
            _windowTitleTicker.Start();
        }

        var loopText = _windowTitleSource + WindowTitleScrollSpacer;
        if (loopText.Length == 0)
        {
            WindowTitle = WindowDisplayName;
            return;
        }

        var offset = Math.Clamp(_windowTitleScrollOffset, 0, loopText.Length - 1);
        WindowTitle = string.Concat(loopText.AsSpan(offset), loopText.AsSpan(0, offset));
    }

    private bool ShouldAnimateWindowTitle()
    {
        // WPF parity (RootView RunningBlock): the title scrolls whenever the option is enabled.
        return _windowTitleScrollable && _windowTitleSource.Length > 0;
    }

    private string FormatRootLogTimestamp(DateTimeOffset timestamp)
    {
        try
        {
            return timestamp.ToLocalTime().ToString(_rootLogTimeFormat, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            return timestamp.ToLocalTime().ToString(DefaultLogItemDateFormat, CultureInfo.InvariantCulture);
        }
    }

    private static bool TryParseScreencapCost(string? payloadJson, out long min, out long avg, out long max)
    {
        min = 0;
        avg = 0;
        max = 0;

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return false;
        }

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(payloadJson) as JsonObject;
        }
        catch
        {
            return false;
        }

        if (root is null)
        {
            return false;
        }

        if (root["what"] is not JsonValue whatValue
            || !whatValue.TryGetValue<string>(out var what)
            || !string.Equals(what, "ScreencapCost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (root["details"] is not JsonObject details)
        {
            return false;
        }

        var parsedMin = TryReadInt64(details, "min");
        var parsedAvg = TryReadInt64(details, "avg");
        var parsedMax = TryReadInt64(details, "max");
        if (!parsedMin.HasValue || !parsedAvg.HasValue || !parsedMax.HasValue)
        {
            return false;
        }

        min = parsedMin.Value;
        avg = parsedAvg.Value;
        max = parsedMax.Value;
        return true;
    }

    private static long? TryReadInt64(JsonObject node, string propertyName)
    {
        if (node[propertyName] is not JsonNode valueNode)
        {
            return null;
        }

        if (valueNode is JsonValue value)
        {
            if (value.TryGetValue<long>(out var longValue))
            {
                return longValue;
            }

            if (value.TryGetValue<int>(out var intValue))
            {
                return intValue;
            }

            if (value.TryGetValue<string>(out var textValue)
                && long.TryParse(textValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedText))
            {
                return parsedText;
            }
        }

        return long.TryParse(valueNode.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static string NormalizeIssueText(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();
    }

    private async Task SyncTrayMenuStateAsync(CancellationToken cancellationToken = default)
    {
        CurrentSessionState = _runtime.SessionService.CurrentState;
        var state = new TrayMenuState(
            StartEnabled: CanStartExecution,
            StopEnabled: CanStopExecution,
            OverlayEnabled: true,
            ForceShowEnabled: true,
            HideTrayEnabled: true,
            HideTrayVisible: _trayHideCommandVisible);
        var result = await _runtime.PlatformCapabilityService.SetTrayMenuStateAsync(state, cancellationToken);
        if (!result.Success)
        {
            LastError = result.Message;
            await RecordFailedResultAsync("App.Shell.Tray.SyncState", result, cancellationToken);
            await _runtime.DialogFeatureService.ReportErrorAsync(
                "App.Shell.Tray.SyncState",
                result,
                cancellationToken);
        }
    }

    /// <summary>
    /// Reopens the tray Exit/Restart latch when the close requested by the
    /// command failed (the window and process keep running), so the user can
    /// retry the command. Called by the view layer's exit failure path.
    /// </summary>
    public void ResetLifecycleCloseInitiated()
    {
        _lifecycleCloseInitiated = false;
    }

    private static string GetTrayCommandScope(TrayCommandId command)
    {
        return command switch
        {
            TrayCommandId.Start => "App.Shell.Tray.Start",
            TrayCommandId.Stop => "App.Shell.Tray.Stop",
            TrayCommandId.ForceShow => "App.Shell.Tray.ForceShow",
            TrayCommandId.HideTray => "App.Shell.Tray.HideTray",
            TrayCommandId.ToggleOverlay => "App.Shell.Tray.ToggleOverlay",
            TrayCommandId.SwitchLanguage => "App.Shell.Tray.SwitchLanguage",
            TrayCommandId.Restart => "App.Shell.Tray.Restart",
            TrayCommandId.Exit => "App.Shell.Tray.Exit",
            _ => "App.Shell.Tray.Unknown",
        };
    }

    private string BuildCapabilityLine(string language, PlatformCapabilityId capability, PlatformCapabilityStatus status)
    {
        return PlatformCapabilityTextMap.FormatCapabilityLine(
            language,
            capability,
            status,
            ReportLocalizationFallback);
    }

    private Task RecordEventAsync(string scope, string message, CancellationToken cancellationToken = default)
    {
        return _runtime.DiagnosticsService.RecordEventAsync(scope, message, cancellationToken);
    }

    private Task RecordFailedResultAsync(string scope, UiOperationResult result, CancellationToken cancellationToken = default)
    {
        return _runtime.DiagnosticsService.RecordFailedResultAsync(scope, result, cancellationToken);
    }

    private Task RecordErrorAsync(string scope, string message, Exception? ex = null, CancellationToken cancellationToken = default)
    {
        return _runtime.DiagnosticsService.RecordErrorAsync(scope, message, ex, cancellationToken);
    }

    private Task RecordConfigValidationFailureAsync(ConfigValidationIssue? issue, CancellationToken cancellationToken = default)
    {
        return _runtime.DiagnosticsService.RecordConfigValidationFailureAsync(issue, cancellationToken);
    }

    private async Task<bool> ApplyResultAsync(UiOperationResult result, string scope, CancellationToken cancellationToken = default)
    {
        if (result.Success)
        {
            LastError = string.Empty;
            await RecordEventAsync(scope, result.Message, cancellationToken);
            return true;
        }

        LastError = result.Message;
        await RecordFailedResultAsync(scope, result, cancellationToken);
        await _runtime.DialogFeatureService.ReportErrorAsync(scope, result, cancellationToken);
        return false;
    }

    private async Task<T?> ApplyResultAsync<T>(UiOperationResult<T> result, string scope, CancellationToken cancellationToken = default)
    {
        if (result.Success)
        {
            LastError = string.Empty;
            await RecordEventAsync(scope, result.Message, cancellationToken);
            return result.Value;
        }

        LastError = result.Message;
        var failed = UiOperationResult.Fail(
            result.Error?.Code ?? UiErrorCode.UiOperationFailed,
            result.Message,
            result.Error?.Details);
        await RecordFailedResultAsync(scope, failed, cancellationToken);
        await _runtime.DialogFeatureService.ReportErrorAsync(scope, failed, cancellationToken);
        return default;
    }

    private async Task RecordUnhandledExceptionAsync(
        string scope,
        Exception ex,
        string code,
        string contextMessage,
        CancellationToken cancellationToken = default)
    {
        LastError = contextMessage;
        await RecordErrorAsync(scope, contextMessage, ex, cancellationToken);
        var failed = UiOperationResult.Fail(code, ex.Message, ex.ToString());
        await RecordFailedResultAsync(scope, failed, cancellationToken);
        await _runtime.DialogFeatureService.ReportErrorAsync(scope, failed, cancellationToken);
    }
}

public enum ShellUiAction
{
    None = 0,
    ShowMainWindow = 1,
    CloseMainWindow = 2,
}

public enum LinkStartHotkeyOutcome
{
    Ignored = 0,
    Started = 1,
    Stopped = 2,
}

public sealed class ConfigIssueDetailItem
{
    public required string Scope { get; init; }

    public required string Code { get; init; }

    public required string Field { get; init; }

    public required bool Blocking { get; init; }

    public required string ProfileName { get; init; }

    public required string TaskIndex { get; init; }

    public required string TaskName { get; init; }

    public required string Message { get; init; }

    public required string SuggestedAction { get; init; }
}

public sealed record ImportSourceOptionItem(ImportSource Source, string DisplayName);
