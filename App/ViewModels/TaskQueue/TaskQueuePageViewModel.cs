using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MAAUnified.App.Features.Dialogs;
using MAAUnified.App.Services;
using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.App.ViewModels.Settings;
using MAAUnified.Application.Models;
using MAAUnified.Application.Models.TaskParams;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Localization;
using MAAUnified.Application.Services.TaskParams;
using MAAUnified.Compat.Constants;
using MAAUnified.CoreBridge;
using MAAUnified.Platform;

namespace MAAUnified.App.ViewModels.TaskQueue;

public sealed class TaskQueuePageViewModel : PageViewModelBase
{
    private const string DefaultLogItemDateFormat = "HH:mm:ss";
    private const string TaskQueueRunOwner = "TaskQueue";
    private const string TaskSelectedIndexConfigKey = "TaskSelectedIndex";
    private const string TaskQueueSaveKey = "TaskQueue.Queue";
    private static readonly TimeSpan LinkStartCancelStopTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan ConnectRetryTotalBudget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ConnectCandidateTotalBudget = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan AdbRecoveryCommandTimeout = TimeSpan.FromSeconds(8);
    private static readonly string[] WpfDefaultTaskOrder =
    [
        TaskModuleTypes.StartUp,
        TaskModuleTypes.Fight,
        TaskModuleTypes.Infrast,
        TaskModuleTypes.Recruit,
        TaskModuleTypes.Mall,
        TaskModuleTypes.Award,
        TaskModuleTypes.Roguelike,
        TaskModuleTypes.Reclamation,
    ];

    private static readonly string[] AddableTaskModules =
    [
        TaskModuleTypes.StartUp,
        TaskModuleTypes.Fight,
        TaskModuleTypes.Infrast,
        TaskModuleTypes.Recruit,
        TaskModuleTypes.Mall,
        TaskModuleTypes.Award,
        TaskModuleTypes.Roguelike,
        TaskModuleTypes.Reclamation,
        TaskModuleTypes.UserDataUpdate,
        TaskModuleTypes.Custom,
    ];

    private static readonly IReadOnlyDictionary<string, string[]> LegacyTaskNameAliases =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [TaskModuleTypes.StartUp] =
            [
                "StartUp",
                "Start Up",
                "WakeUp",
                "Wake Up",
                "开始唤醒",
                "開始喚醒",
            ],
            [TaskModuleTypes.Fight] =
            [
                "Fight",
                "Combat",
                "理智作战",
                "理智作戰",
                "刷理智",
                "作戦",
                "이성 사용",
            ],
            [TaskModuleTypes.Infrast] =
            [
                "Infrast",
                "Infrastructure",
                "Base",
                "基建换班",
                "基建換班",
                "基建",
            ],
            [TaskModuleTypes.Recruit] =
            [
                "Recruit",
                "Auto Recruit",
                "自动公招",
                "自動公招",
                "公开招募",
                "公開招募",
                "公開求人",
                "공개채용",
            ],
            [TaskModuleTypes.Mall] =
            [
                "Mall",
                "Credit",
                "信用收支",
                "信用收支",
                "商店",
            ],
            [TaskModuleTypes.Award] =
            [
                "Award",
                "Awards",
                "领取奖励",
                "領取獎勵",
                "奖励",
                "獎勵",
                "報酬",
                "보상",
            ],
            [TaskModuleTypes.Roguelike] =
            [
                "Roguelike",
                "IS",
                "自动肉鸽",
                "自動肉鴿",
                "肉鸽",
                "肉鴿",
                "ローグライク",
                "로그라이크",
            ],
            [TaskModuleTypes.Reclamation] =
            [
                "Reclamation",
                "生息演算",
                "生息演算",
                "생식연산",
            ],
            [TaskModuleTypes.UserDataUpdate] =
            [
                "UserDataUpdate",
                "Update Doctor Data",
                "更新数据",
                "更新使用者資料",
                "ユーザーデータ更新",
                "사용자 데이터 업데이트",
            ],
            [TaskModuleTypes.Custom] =
            [
                "Custom",
                "Custom Task",
                "自定义任务",
                "自訂任務",
                "カスタム",
                "커스텀",
            ],
            [TaskModuleTypes.PostAction] =
            [
                "PostAction",
                "Post Action",
                "After Completion",
                "完成后",
                "完成後",
                "後置動作",
            ],
        };

    private static readonly IReadOnlyDictionary<string, string[]> LegacyLocalizedTaskTitleKeys =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [TaskModuleTypes.Fight] =
            [
                "RemainingSanityStage",
            ],
        };

    private const int MaxLogCards = 180;
    private const int MaxOverlayLogs = 200;
    private const string UiMallCreditFightLastTime = "_ui_mall_credit_fight_last_time";
    private const string UiMallVisitFriendsLastTime = "_ui_mall_visit_friends_last_time";
    private static readonly Regex LeadingLogTimestampPattern = new(
        "^(?:\\[?\\d{2}:\\d{2}:\\d{2}\\]?\\s+)+",
        RegexOptions.Compiled);

    private readonly SemaphoreSlim _logThumbnailSemaphore = new(1, 1);
    private readonly SemaphoreSlim _queueMutationLock = new(1, 1);
    private readonly SemaphoreSlim _runTransitionLock = new(1, 1);
    private readonly object _pendingBindingGate = new();
    private readonly object _moduleAutoSaveGate = new();
    private readonly OverlaySharedState _overlaySharedState;
    private readonly ConnectionGameSharedStateViewModel _connectionGameSharedState;
    private readonly Action<LocalizationFallbackInfo>? _localizationFallbackReporter;
    private readonly IAppDialogService _dialogService;
    private readonly Action<string>? _navigateToSettingsSection;
    private readonly Func<CancellationToken, Task<bool>>? _ensureCoreReadyForExecutionAsync;
    private readonly Func<string, CancellationToken, Task>? _stopRunOwnerAsync;
    private readonly StartUpTaskModuleViewModel _fallbackStartUpModule;
    private readonly FightTaskModuleViewModel _fallbackFightModule;
    private readonly RecruitTaskModuleViewModel _fallbackRecruitModule;
    private readonly InfrastModuleViewModel _fallbackInfrastModule;
    private readonly MallModuleViewModel _fallbackMallModule;
    private readonly AwardModuleViewModel _fallbackAwardModule;
    private readonly RoguelikeModuleViewModel _fallbackRoguelikeModule;
    private readonly ReclamationModuleViewModel _fallbackReclamationModule;
    private readonly UserDataUpdateModuleViewModel _fallbackUserDataUpdateModule;
    private readonly CustomModuleViewModel _fallbackCustomModule;
    private Task _pendingBindingTask = Task.CompletedTask;
    private Task _stopStartRequestTask = Task.CompletedTask;
    private CancellationTokenSource? _moduleAutoSaveCts;
    private int _pendingBindingVersion;
    private CancellationTokenSource? _startRequestCts;
    private bool _suppressTaskEnabledSync;
    private bool _suppressModuleAutoSave;
    private SessionState _currentSessionState;
    private bool _hasBlockingConfigIssues;
    private int _blockingConfigIssueCount;
    private bool _isCoreReady = true;
    private bool _isAdvancedSettingsSelected;
    private bool _isPostActionPanelSelected;
    private bool _isWaitingForStop;
    private bool _isStartRequestActive;
    private bool _isRunButtonStopHovered;
    private SelectionBatchMode _selectionBatchMode = SelectionBatchMode.Clear;
    private bool _showBatchModeToggle;
    private bool _clearTaskStatusesWhenStopped;
    private string _dailyStageHint = string.Empty;
    private string _selectedTaskModule = TaskModuleTypes.StartUp;
    private TaskModuleOption? _selectedTaskModuleOption;
    private string _renameTargetName = string.Empty;
    private string _overlayStatusText = string.Empty;
    private OverlayTarget? _selectedOverlayTarget = new("preview", "Preview + Logs", true);
    private bool _overlayVisible;
    private OverlayRuntimeMode _overlayMode = OverlayRuntimeMode.Hidden;
    private string _currentRunId = "-";
    private string _lastPostActionRunId = string.Empty;
    private string _selectedTaskValidationSummary = string.Empty;
    private bool _selectedTaskHasBlockingValidationIssues;
    private int _selectedTaskValidationIssueCount;
    private string _coreInitializationMessage = string.Empty;
    private string _noTaskSelectedHint = "Select a task from the left list to edit its settings.";
    private TaskQueueLogEntryViewModel _downloadLogEntry = new(string.Empty, string.Empty, "INFO");
    private TaskRuntimeStatusSnapshot? _lastRuntimeStatus;
    private TaskQueueItemViewModel? _selectedTask;
    private bool _roguelikeInCombat;
    private DateTimeOffset? _runStartedAt;
    private DateTimeOffset? _fightSanityReportTime;
    private int? _fightSanityCurrent;
    private int? _fightSanityMax;
    private int? _fightSanityCost;
    private int? _fightSeries;
    private int? _fightTimesFinished;
    private int _medicineUsedTimes;
    private int _expiringMedicineUsedTimes;
    private int _stoneUsedTimes;
    private bool _nextLogEntryStartsNewCard;
    private string _logTimestampFormat = DefaultLogItemDateFormat;
    private bool _useSystemNotifications = true;
    private string _lastCompletionNotificationRunId = string.Empty;
    private string _lastFailureNotificationRunId = string.Empty;
    private bool _selectedTaskSettingsHostResetPending;
    private bool _isSelectedTaskBindingPending;
    private TaskQueueTaskPanelViewModel? _selectedTaskPanel;
    private readonly IUiLanguageCoordinator _uiLanguageCoordinator;

    public TaskQueuePageViewModel(
        MAAUnifiedRuntime runtime,
        ConnectionGameSharedStateViewModel connectionGameSharedState,
        Action<LocalizationFallbackInfo>? localizationFallbackReporter = null,
        IAppDialogService? dialogService = null,
        Action<string>? navigateToSettingsSection = null,
        Func<CancellationToken, Task<bool>>? ensureCoreReadyForExecutionAsync = null,
        Func<string, CancellationToken, Task>? stopRunOwnerAsync = null)
        : base(runtime)
    {
        _connectionGameSharedState = connectionGameSharedState;
        _localizationFallbackReporter = localizationFallbackReporter;
        _dialogService = dialogService ?? NoOpAppDialogService.Instance;
        _navigateToSettingsSection = navigateToSettingsSection;
        _ensureCoreReadyForExecutionAsync = ensureCoreReadyForExecutionAsync;
        _stopRunOwnerAsync = stopRunOwnerAsync;
        _uiLanguageCoordinator = runtime.UiLanguageCoordinator;
        _uiLanguageCoordinator.LanguageChanged += OnUnifiedLanguageChanged;
        TaskModules = new ObservableCollection<TaskModuleOption>();
        Tasks = new ObservableCollection<TaskQueueItemViewModel>();
        TaskPanels = new ObservableCollection<TaskQueueTaskPanelViewModel>();
        LogCards = new ObservableCollection<TaskQueueLogCardViewModel>();
        OverlayLogs = new ObservableCollection<TaskQueueLogEntryViewModel>();
        OverlayTargets = new ObservableCollection<OverlayTarget>();
        _overlaySharedState = OverlaySharedStateRegistry.Get(runtime);
        _overlayVisible = _overlaySharedState.Visible;
        _overlayMode = _overlaySharedState.Mode;

        Texts = new LocalizedTextMap
        {
            Language = ResolveLanguage(),
        };
        RootTexts = new RootLocalizationTextMap("Root.Localization.TaskQueue")
        {
            Language = ResolveLanguage(),
        };
        RootTexts.FallbackReported += info => _localizationFallbackReporter?.Invoke(info);
        _logTimestampFormat = ResolveLogTimestampFormat();
        _dailyStageHint = Texts.GetOrDefault("TaskQueue.DailyStageHintDefault", "Daily stage hints will be shown after resources are loaded.");
        _noTaskSelectedHint = RootTexts.GetOrDefault(
            "TaskQueue.SelectionHint",
            "Select a task from the left list to edit its settings.");
        _overlayStatusText = string.IsNullOrWhiteSpace(_overlaySharedState.StatusMessage)
            ? Texts.GetOrDefault("TaskQueue.OverlayDisconnected", "Overlay disconnected")
            : _overlaySharedState.StatusMessage;
        _fallbackStartUpModule = new StartUpTaskModuleViewModel(
            runtime,
            Texts,
            _connectionGameSharedState,
            RunAccountSwitchManualAsync);
        _fallbackFightModule = new FightTaskModuleViewModel(runtime, Texts);
        _fallbackRecruitModule = new RecruitTaskModuleViewModel(runtime, Texts);
        _fallbackInfrastModule = new InfrastModuleViewModel(runtime, Texts);
        _fallbackMallModule = new MallModuleViewModel(runtime, Texts);
        _fallbackAwardModule = new AwardModuleViewModel(runtime, Texts);
        _fallbackRoguelikeModule = new RoguelikeModuleViewModel(runtime, Texts);
        _fallbackReclamationModule = new ReclamationModuleViewModel(runtime, Texts);
        _fallbackUserDataUpdateModule = new UserDataUpdateModuleViewModel(runtime, Texts);
        _fallbackCustomModule = new CustomModuleViewModel(runtime, Texts);
        PostActionModule = new PostActionModuleViewModel(runtime, Texts);
        ApplySettingsModeToTaskModules();
        _fallbackStartUpModule.PropertyChanged += OnTypedModulePropertyChanged;
        _fallbackFightModule.PropertyChanged += OnTypedModulePropertyChanged;
        _fallbackRecruitModule.PropertyChanged += OnTypedModulePropertyChanged;
        _fallbackRoguelikeModule.PropertyChanged += OnTypedModulePropertyChanged;
        _fallbackReclamationModule.PropertyChanged += OnTypedModulePropertyChanged;
        _fallbackCustomModule.PropertyChanged += OnTypedModulePropertyChanged;
        PostActionModule.PropertyChanged += OnPostActionModulePropertyChanged;

        RebuildTaskModuleOptions();
        SelectedTaskModuleOption = TaskModules.FirstOrDefault();
        UpdatePostActionSummary();
        RefreshSelectionBatchModeFromConfig();

        runtime.LogService.LogReceived += log => Dispatcher.UIThread.Post(() => UpdateDownloadLog(log.Timestamp, log.Level, log.Message));

        runtime.SessionService.CallbackReceived += callback => _ = HandleCallbackAsync(callback);
        runtime.SessionService.SessionStateChanged += OnSessionStateChanged;
        runtime.ConfigurationService.ConfigChanged += _ =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                ApplyGuiSettingsFromConfig();
                RefreshConfigValidationState(runtime.ConfigurationService.CurrentValidationIssues);
            });
        };
        _connectionGameSharedState.PropertyChanged += OnConnectionGameSharedStateChanged;
        _currentSessionState = runtime.SessionService.CurrentState;
        _overlaySharedState.PropertyChanged += OnOverlaySharedStateChanged;
        SyncOverlayPresentationFromSharedState();
    }

    public ObservableCollection<TaskModuleOption> TaskModules { get; }

    public ObservableCollection<TaskQueueItemViewModel> Tasks { get; }

    public ObservableCollection<TaskQueueTaskPanelViewModel> TaskPanels { get; }

    public ObservableCollection<TaskQueueLogCardViewModel> LogCards { get; }

    public ObservableCollection<TaskQueueLogEntryViewModel> OverlayLogs { get; }

    public TaskQueueLogEntryViewModel DownloadLogEntry
    {
        get => _downloadLogEntry;
        private set
        {
            if (SetProperty(ref _downloadLogEntry, value))
            {
                OnPropertyChanged(nameof(HasDownloadLog));
            }
        }
    }

    public bool HasDownloadLog => !string.IsNullOrWhiteSpace(DownloadLogEntry.Content);

    public ObservableCollection<OverlayTarget> OverlayTargets { get; }

    public LocalizedTextMap Texts { get; }

    public RootLocalizationTextMap RootTexts { get; }

    public StartUpTaskModuleViewModel StartUpModule => ResolveModuleForProjection<StartUpTaskModuleViewModel>() ?? _fallbackStartUpModule;

    public FightTaskModuleViewModel FightModule => ResolveModuleForProjection<FightTaskModuleViewModel>() ?? _fallbackFightModule;

    public RecruitTaskModuleViewModel RecruitModule => ResolveModuleForProjection<RecruitTaskModuleViewModel>() ?? _fallbackRecruitModule;

    public InfrastModuleViewModel InfrastModule => ResolveModuleForProjection<InfrastModuleViewModel>() ?? _fallbackInfrastModule;

    public MallModuleViewModel MallModule => ResolveModuleForProjection<MallModuleViewModel>() ?? _fallbackMallModule;

    public AwardModuleViewModel AwardModule => ResolveModuleForProjection<AwardModuleViewModel>() ?? _fallbackAwardModule;

    public RoguelikeModuleViewModel RoguelikeModule => ResolveModuleForProjection<RoguelikeModuleViewModel>() ?? _fallbackRoguelikeModule;

    public ReclamationModuleViewModel ReclamationModule => ResolveModuleForProjection<ReclamationModuleViewModel>() ?? _fallbackReclamationModule;

    public UserDataUpdateModuleViewModel UserDataUpdateModule => ResolveModuleForProjection<UserDataUpdateModuleViewModel>() ?? _fallbackUserDataUpdateModule;

    public CustomModuleViewModel CustomModule => ResolveModuleForProjection<CustomModuleViewModel>() ?? _fallbackCustomModule;

    public PostActionModuleViewModel PostActionModule { get; }

    public TaskRuntimeStatusSnapshot? LastRuntimeStatus
    {
        get => _lastRuntimeStatus;
        private set => SetProperty(ref _lastRuntimeStatus, value);
    }

    public string SelectedTaskValidationSummary
    {
        get => _selectedTaskValidationSummary;
        private set
        {
            if (SetProperty(ref _selectedTaskValidationSummary, value))
            {
                OnPropertyChanged(nameof(HasSelectedTaskValidationSummary));
            }
        }
    }

    public bool SelectedTaskHasBlockingValidationIssues
    {
        get => _selectedTaskHasBlockingValidationIssues;
        private set => SetProperty(ref _selectedTaskHasBlockingValidationIssues, value);
    }

    public int SelectedTaskValidationIssueCount
    {
        get => _selectedTaskValidationIssueCount;
        private set
        {
            if (SetProperty(ref _selectedTaskValidationIssueCount, value))
            {
                OnPropertyChanged(nameof(SelectedTaskHasValidationIssues));
            }
        }
    }

    public bool HasSelectedTaskValidationSummary => !string.IsNullOrWhiteSpace(SelectedTaskValidationSummary);

    public bool SelectedTaskHasValidationIssues => SelectedTaskValidationIssueCount > 0;

    public string CoreInitializationMessage
    {
        get => _coreInitializationMessage;
        private set
        {
            if (SetProperty(ref _coreInitializationMessage, value))
            {
                OnPropertyChanged(nameof(HasCoreInitializationMessage));
            }
        }
    }

    public bool HasCoreInitializationMessage => !string.IsNullOrWhiteSpace(CoreInitializationMessage);

    public string NoTaskSelectedHint
    {
        get => _noTaskSelectedHint;
        private set => SetProperty(ref _noTaskSelectedHint, value);
    }

    public bool IsNoTaskSelected => SelectedTask is null;

    public bool IsStartUpTaskSelected => IsSelectedTaskType(TaskModuleTypes.StartUp);

    public bool IsFightTaskSelected => IsSelectedTaskType(TaskModuleTypes.Fight);

    public bool IsRecruitTaskSelected => IsSelectedTaskType(TaskModuleTypes.Recruit);

    public bool IsInfrastTaskSelected => IsSelectedTaskType(TaskModuleTypes.Infrast);

    public bool IsMallTaskSelected => IsSelectedTaskType(TaskModuleTypes.Mall);

    public bool IsAwardTaskSelected => IsSelectedTaskType(TaskModuleTypes.Award);

    public bool IsRoguelikeTaskSelected => IsSelectedTaskType(TaskModuleTypes.Roguelike);

    public bool IsReclamationTaskSelected => IsSelectedTaskType(TaskModuleTypes.Reclamation);

    public bool IsUserDataUpdateTaskSelected => IsSelectedTaskType(TaskModuleTypes.UserDataUpdate);

    public bool IsCustomTaskSelected => IsSelectedTaskType(TaskModuleTypes.Custom);

    public bool IsPostActionTaskSelected => IsSelectedTaskType(TaskModuleTypes.PostAction);

    public TaskQueueItemViewModel? SelectedTask
    {
        get => _selectedTask;
        set
        {
            if (!SetProperty(ref _selectedTask, value))
            {
                return;
            }

            SelectedTaskModule = value is null
                ? TaskModuleTypes.StartUp
                : TaskModuleTypes.Normalize(value.Type);
            RenameTargetName = value?.Name ?? string.Empty;
            if (value is not null)
            {
                IsPostActionPanelSelected = false;
            }
            RememberSelectedTaskIndex();
            UpdateSelectedTaskPanel();
            RaiseSelectedTaskProjectionChanged();
            ResetSettingsModeForSelectedTask();
        }
    }

    public TaskQueueTaskPanelViewModel? SelectedTaskPanel
    {
        get => _selectedTaskPanel;
        private set
        {
            if (SetProperty(ref _selectedTaskPanel, value))
            {
                OnPropertyChanged(nameof(SelectedTaskSettingsViewModel));
                ProjectSelectedTaskValidationSummary();
                RaiseModuleProjectionPropertiesChanged();
            }
        }
    }

    public string SelectedTaskModule
    {
        get => _selectedTaskModule;
        set
        {
            var normalized = TaskModuleTypes.Normalize(value);
            if (!SetProperty(ref _selectedTaskModule, normalized))
            {
                return;
            }

            var matched = TaskModules.FirstOrDefault(
                option => string.Equals(option.Type, normalized, StringComparison.OrdinalIgnoreCase));
            if (matched is not null && !Equals(_selectedTaskModuleOption, matched))
            {
                _selectedTaskModuleOption = matched;
                OnPropertyChanged(nameof(SelectedTaskModuleOption));
            }
        }
    }

    public TaskModuleOption? SelectedTaskModuleOption
    {
        get => _selectedTaskModuleOption;
        set
        {
            if (!SetProperty(ref _selectedTaskModuleOption, value))
            {
                return;
            }

            if (value is not null)
            {
                SelectedTaskModule = value.Type;
            }
        }
    }

    public string RenameTargetName
    {
        get => _renameTargetName;
        set => SetProperty(ref _renameTargetName, value);
    }

    public bool IsPostActionPanelSelected
    {
        get => _isPostActionPanelSelected;
        private set
        {
            if (!SetProperty(ref _isPostActionPanelSelected, value))
            {
                return;
            }

            if (value)
            {
                SetAdvancedSettingsSelected(false);
            }

            UpdateSelectedTaskPanel();
            RaiseSelectedTaskProjectionChanged();
        }
    }

    public SessionState CurrentSessionState
    {
        get => _currentSessionState;
        private set
        {
            if (SetProperty(ref _currentSessionState, value))
            {
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(CanEdit));
                OnPropertyChanged(nameof(CanEditSelectedTaskSettings));
                OnPropertyChanged(nameof(RunButtonText));
                OnPropertyChanged(nameof(IsRunOwnedByAnotherFeature));
                OnPropertyChanged(nameof(IsOwnRunActive));
                OnPropertyChanged(nameof(CanToggleRun));
                OnPropertyChanged(nameof(CanWaitAndStop));
            }
        }
    }

    public bool IsRunning => _isStartRequestActive || CurrentSessionState is SessionState.Running or SessionState.Stopping;

    public bool CanEdit => !IsRunning;

    public bool IsSelectedTaskBindingPending
    {
        get => _isSelectedTaskBindingPending;
        private set => SetProperty(ref _isSelectedTaskBindingPending, value);
    }

    public bool CanEditSelectedTaskSettings => CanEdit;

    public string RunButtonText
    {
        get
        {
            if (IsOwnRunActive)
            {
                return _isRunButtonStopHovered || _isStartRequestActive
                    ? RootTexts.GetOrDefault("TaskQueue.Root.Stop", "Stop")
                    : RootTexts.GetOrDefault("Toolbox.Action.Running", "Running...");
            }

            return RootTexts.GetOrDefault("TaskQueue.Root.LinkStart", "Link Start!");
        }
    }

    public bool IsOwnRunActive => _isStartRequestActive
        || (CurrentSessionState is SessionState.Running or SessionState.Stopping
            && !IsRunOwnedByAnotherFeature);

    public bool IsRunOwnedByAnotherFeature
    {
        get
        {
            if (CurrentSessionState is not (SessionState.Running or SessionState.Stopping))
            {
                return false;
            }

            var currentOwner = Runtime.SessionService.CurrentRunOwner;
            return !string.IsNullOrWhiteSpace(currentOwner)
                && !Runtime.SessionService.IsRunOwner(TaskQueueRunOwner);
        }
    }

    public bool HasBlockingConfigIssues
    {
        get => _hasBlockingConfigIssues;
        private set
        {
            if (SetProperty(ref _hasBlockingConfigIssues, value))
            {
                OnPropertyChanged(nameof(CanToggleRun));
            }
        }
    }

    public int BlockingConfigIssueCount
    {
        get => _blockingConfigIssueCount;
        private set => SetProperty(ref _blockingConfigIssueCount, value);
    }

    public bool IsCoreReady
    {
        get => _isCoreReady;
        private set
        {
            if (SetProperty(ref _isCoreReady, value))
            {
                OnPropertyChanged(nameof(CanToggleRun));
            }
        }
    }

    public bool CanToggleRun =>
        !IsWaitingForStop;

    public bool CanWaitAndStop =>
        !IsWaitingForStop
        && CurrentSessionState == SessionState.Running;

    private void SetStartRequestActive(bool value)
    {
        if (_isStartRequestActive == value)
        {
            return;
        }

        _isStartRequestActive = value;
        OnPropertyChanged(nameof(IsStartRequestActive));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanEditSelectedTaskSettings));
        OnPropertyChanged(nameof(RunButtonText));
        OnPropertyChanged(nameof(IsRunOwnedByAnotherFeature));
        OnPropertyChanged(nameof(IsOwnRunActive));
        OnPropertyChanged(nameof(CanToggleRun));
    }

    public bool IsStartRequestActive => _isStartRequestActive;

    public void SetRunButtonHover(bool hovering)
    {
        if (hovering)
        {
            if (!IsOwnRunActive || _isRunButtonStopHovered)
            {
                return;
            }

            _isRunButtonStopHovered = true;
        }
        else
        {
            if (!_isRunButtonStopHovered)
            {
                return;
            }

            _isRunButtonStopHovered = false;
        }

        OnPropertyChanged(nameof(RunButtonText));
    }

    public bool IsWaitingForStop
    {
        get => _isWaitingForStop;
        private set
        {
            if (SetProperty(ref _isWaitingForStop, value))
            {
                OnPropertyChanged(nameof(CanToggleRun));
                OnPropertyChanged(nameof(CanWaitAndStop));
                OnPropertyChanged(nameof(WaitAndStopButtonText));
            }
        }
    }

    public string WaitAndStopButtonText => IsWaitingForStop
        ? RootTexts.GetOrDefault("TaskQueue.Root.Waiting", "Waiting...")
        : RootTexts.GetOrDefault("TaskQueue.Root.WaitAndStop", "Wait & Stop");

    public SelectionBatchMode SelectionBatchMode
    {
        get => _selectionBatchMode;
        private set
        {
            if (SetProperty(ref _selectionBatchMode, value))
            {
                OnPropertyChanged(nameof(BatchActionText));
                OnPropertyChanged(nameof(BatchToggleMenuText));
            }
        }
    }

    public bool ShowBatchModeToggle
    {
        get => _showBatchModeToggle;
        private set
        {
            if (SetProperty(ref _showBatchModeToggle, value))
            {
                OnPropertyChanged(nameof(BatchToggleMenuText));
            }
        }
    }

    public string BatchActionText => SelectionBatchMode == SelectionBatchMode.Inverse
        ? RootTexts.GetOrDefault("TaskQueue.Root.Inverse", "Inverse")
        : RootTexts.GetOrDefault("TaskQueue.Root.Clear", "Clear");

    public string BatchToggleMenuText
    {
        get
        {
            var targetText = SelectionBatchMode == SelectionBatchMode.Inverse
                ? RootTexts.GetOrDefault("TaskQueue.Root.Clear", "Clear")
                : RootTexts.GetOrDefault("TaskQueue.Root.Inverse", "Inverse");
            return string.Format(
                RootTexts.GetOrDefault("TaskQueue.Root.SwitchBatchMode", "Switch to {0}"),
                targetText);
        }
    }

    public string TaskListTitleText => RootTexts.GetOrDefault("TaskQueue.Root.TaskListTitle", "Task list");

    public string TaskConfigTitleText
    {
        get
        {
            var baseTitle = RootTexts.GetOrDefault("TaskQueue.Root.TaskConfigTitle", "Task config");
            var detailTitle = IsPostActionPanelSelected || IsPostActionTaskSelected
                ? PostActionActionTitle
                : SelectedTask?.DisplayName;

            return string.IsNullOrWhiteSpace(detailTitle)
                ? baseTitle
                : $"{baseTitle} - {detailTitle}";
        }
    }

    public string LogsTitleText => RootTexts.GetOrDefault("TaskQueue.Root.LogsTitle", "Logs");

    public string OverlayButtonText => RootTexts.GetOrDefault("TaskQueue.Root.OverlayButton", "Overlay");

    public string TaskMenuMoveUpText => RootTexts.GetOrDefault("TaskQueue.Root.MoveUp", "Move up");

    public string TaskMenuMoveDownText => RootTexts.GetOrDefault("TaskQueue.Root.MoveDown", "Move down");

    public string TaskMenuRenameText => RootTexts.GetOrDefault("TaskQueue.Root.Rename", "Rename");

    public string TaskMenuRunOnceText => RootTexts.GetOrDefault("TaskQueue.Root.RunTaskOnce", "Run once");

    public string TaskMenuDeleteText => RootTexts.GetOrDefault("TaskQueue.Root.Delete", "Delete");

    public string TaskMenuIconText => RootTexts.GetOrDefault("TaskQueue.Root.TaskMenuIcon", "⚙");

    public string AddTaskButtonText => RootTexts.GetOrDefault("TaskQueue.Root.AddTaskIcon", "+");

    public string SelectAllButtonText => RootTexts.GetOrDefault("TaskQueue.Root.SelectAll", "Select all");

    public string GeneralSettingsButtonText => RootTexts.GetOrDefault("TaskQueue.Root.GeneralSettings", "General settings");

    public string AdvancedSettingsButtonText => RootTexts.GetOrDefault("TaskQueue.Root.AdvancedSettings", "Advanced settings");

    public string DailyStageLabelText => RootTexts.GetOrDefault("TaskQueue.Root.DailyStageLabel", "Daily stage");

    public string DailyStageTooltipText => RootTexts.GetOrDefault("TaskQueue.Root.DailyStageTooltip", "Daily stage tooltip");

    public string AddTaskMenuStartUpText => ResolveModuleDisplayName(TaskModuleTypes.StartUp);

    public string AddTaskMenuFightText => ResolveModuleDisplayName(TaskModuleTypes.Fight);

    public string AddTaskMenuInfrastText => ResolveModuleDisplayName(TaskModuleTypes.Infrast);

    public string AddTaskMenuRecruitText => ResolveModuleDisplayName(TaskModuleTypes.Recruit);

    public string AddTaskMenuMallText => ResolveModuleDisplayName(TaskModuleTypes.Mall);

    public string AddTaskMenuAwardText => ResolveModuleDisplayName(TaskModuleTypes.Award);

    public string AddTaskMenuRoguelikeText => ResolveModuleDisplayName(TaskModuleTypes.Roguelike);

    public string AddTaskMenuReclamationText => ResolveModuleDisplayName(TaskModuleTypes.Reclamation);

    public string AddTaskMenuUserDataUpdateText => ResolveModuleDisplayName(TaskModuleTypes.UserDataUpdate);

    public string AddTaskMenuCustomText => ResolveModuleDisplayName(TaskModuleTypes.Custom);

    public bool IsGeneralSettingsSelected
    {
        get => !_isAdvancedSettingsSelected;
        set
        {
            if (value)
            {
                SetAdvancedSettingsSelected(false);
            }
        }
    }

    public bool IsAdvancedSettingsSelected
    {
        get => _isAdvancedSettingsSelected;
        set => SetAdvancedSettingsSelected(value);
    }

    public bool CanUseAdvancedSettings =>
        IsFightTaskSelected
        || IsRecruitTaskSelected
        || IsInfrastTaskSelected
        || IsMallTaskSelected
        || IsRoguelikeTaskSelected
        || IsReclamationTaskSelected;

    public bool ShowSettingsModeSwitch =>
        !IsPostActionPanelSelected
        && !IsPostActionTaskSelected
        && SelectedTask is not null
        && !IsStartUpTaskSelected
        && !IsAwardTaskSelected
        && !IsUserDataUpdateTaskSelected;

    public bool ShowTaskConfigHint => !IsPostActionPanelSelected && IsNoTaskSelected;

    public bool ShowPostActionSettingsPanel => IsPostActionPanelSelected || IsPostActionTaskSelected;

    public object? SelectedTaskSettingsViewModel
    {
        get
        {
            if (_selectedTaskSettingsHostResetPending)
            {
                return null;
            }

            if (ShowPostActionSettingsPanel)
            {
                return PostActionModule;
            }

            if (SelectedTask is null)
            {
                return null;
            }

            return SelectedTaskPanel?.ModuleViewModel;
        }
    }

    public void SelectGeneralSettingsMode()
    {
        SetAdvancedSettingsSelected(false);
    }

    public void SelectAdvancedSettingsMode()
    {
        SetAdvancedSettingsSelected(true);
    }

    public void OpenPostActionPanel()
    {
        IsPostActionPanelSelected = true;
        SelectedTask = null;
    }

    public string PostActionActionTitle =>
        PostActionModule.Once
            ? string.Format(
                RootTexts.GetOrDefault("TaskQueue.Root.PostActionTitleWithOnce", "{0} ({1})"),
                RootTexts.GetOrDefault("TaskQueue.Root.PostActionTitle", "After Completion"),
                Texts.GetOrDefault("PostAction.Once", "Once"))
            : RootTexts.GetOrDefault("TaskQueue.Root.PostActionTitle", "After Completion");

    public string PostActionActionDescription
    {
        get
        {
            var actions = BuildPostActionActionLabels();
            if (actions.Count == 0)
            {
                return RootTexts.GetOrDefault("TaskQueue.Root.PostActionNone", "Do nothing");
            }

            return string.Join(" / ", actions);
        }
    }

    public string DailyStageHint
    {
        get => _dailyStageHint;
        set => SetProperty(ref _dailyStageHint, value);
    }

    public string OverlayStatusText
    {
        get => _overlayStatusText;
        set
        {
            if (SetProperty(ref _overlayStatusText, value))
            {
                OnPropertyChanged(nameof(OverlayButtonToolTip));
            }
        }
    }

    public OverlayTarget? SelectedOverlayTarget
    {
        get => _selectedOverlayTarget;
        set
        {
            if (!SetProperty(ref _selectedOverlayTarget, value))
            {
                return;
            }

            _overlaySharedState.SelectedTargetId = value?.Id ?? "preview";
            OnPropertyChanged(nameof(OverlayTargetSummaryText));
            OnPropertyChanged(nameof(OverlayButtonToolTip));
        }
    }

    public bool OverlayVisible
    {
        get => _overlayVisible;
        set
        {
            if (!SetProperty(ref _overlayVisible, value))
            {
                return;
            }

            _overlaySharedState.Visible = value;
        }
    }

    public OverlayRuntimeMode OverlayMode
    {
        get => _overlayMode;
        private set
        {
            if (!SetProperty(ref _overlayMode, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsOverlayHiddenMode));
            OnPropertyChanged(nameof(IsOverlayPreviewMode));
            OnPropertyChanged(nameof(IsOverlayNativeMode));
            OnPropertyChanged(nameof(OverlayButtonToolTip));
        }
    }

    public bool IsOverlayHiddenMode => OverlayMode == OverlayRuntimeMode.Hidden;

    public bool IsOverlayPreviewMode => OverlayMode == OverlayRuntimeMode.Preview;

    public bool IsOverlayNativeMode => OverlayMode == OverlayRuntimeMode.Native;

    public string OverlayTargetSummaryText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(SelectedOverlayTarget?.DisplayName))
            {
                return SelectedOverlayTarget.DisplayName;
            }

            return string.Equals(_overlaySharedState.SelectedTargetId, "preview", StringComparison.OrdinalIgnoreCase)
                ? "Preview + Logs"
                : _overlaySharedState.SelectedTargetId;
        }
    }

    public string OverlayButtonToolTip =>
        $"{RootTexts.GetOrDefault("TaskQueue.Root.LeftClick", "Left click")}: {RootTexts.GetOrDefault("TaskQueue.Root.ToggleOverlay", "Toggle overlay")}{Environment.NewLine}" +
        $"{RootTexts.GetOrDefault("TaskQueue.Root.RightClick", "Right click")}: {RootTexts.GetOrDefault("TaskQueue.Root.PickTarget", "Pick target")}{Environment.NewLine}" +
        $"{OverlayStatusText}{Environment.NewLine}" +
        $"{RootTexts.GetOrDefault("TaskQueue.Root.PickTarget", "Pick target")}: {OverlayTargetSummaryText}";

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await InitializeFirstScreenAsync(cancellationToken);
        await InitializeDeferredStartupAsync(cancellationToken);
    }

    public async Task InitializeFirstScreenAsync(CancellationToken cancellationToken = default)
    {
        SetLanguage(ResolveLanguage());
        RefreshConfigValidationState(Runtime.ConfigurationService.CurrentValidationIssues);
        await ReloadTasksAsync(cancellationToken, preferProfileSelectedIndex: true, waitForPendingBinding: false);

        ApplyGuiSettingsFromConfig();
        UpdatePostActionSummary();
    }

    public async Task InitializeDeferredStartupAsync(CancellationToken cancellationToken = default)
    {
        await WaitForPendingBindingAsync(cancellationToken);
        await ReloadTaskPanelPersistentConfigAsync(cancellationToken);
        RefreshStagePresentation();
        await ReloadOverlayTargetsAsync(cancellationToken);
        await PostActionModule.InitializeAsync(cancellationToken);
        UpdatePostActionSummary();
    }

    public async Task ReloadConfigurationContextAsync(
        bool forceReloadStageOptions = false,
        CancellationToken cancellationToken = default)
    {
        PrepareForConfigurationContextSwitch();
        await ReloadTasksAsync(
            cancellationToken,
            preferProfileSelectedIndex: true,
            waitForPendingBinding: false);
        await ReloadTaskPanelPersistentConfigAsync(cancellationToken);
        ApplyGuiSettingsFromConfig();
        await PostActionModule.InitializeAsync(cancellationToken);

        RefreshStagePresentation(forceReloadStageOptions);
        UpdatePostActionSummary();
    }

    public void SetLanguage(string language)
    {
        var normalized = UiLanguageCatalog.Normalize(language);
        if (string.Equals(Texts.Language, normalized, StringComparison.OrdinalIgnoreCase)
            && string.Equals(RootTexts.Language, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Texts.Language = normalized;
        RootTexts.Language = normalized;
        OnPropertyChanged(nameof(Texts));
        OnPropertyChanged(nameof(RootTexts));

        DailyStageHint = FightTaskModuleViewModel.BuildDailyResourceHint(
            Texts.Language,
            _connectionGameSharedState.ClientType,
            Runtime.ConfigurationService.CurrentConfig);
        NoTaskSelectedHint = RootTexts.GetOrDefault(
            "TaskQueue.SelectionHint",
            "Select a task from the left list to edit its settings.");
        OverlayStatusText = string.IsNullOrWhiteSpace(_overlaySharedState.StatusMessage)
            ? Texts.GetOrDefault("TaskQueue.OverlayDisconnected", "Overlay disconnected")
            : _overlaySharedState.StatusMessage;

        RefreshFightStageOptions(_connectionGameSharedState.ClientType);
        RebuildTaskModuleOptions();
        RefreshTaskItemsLocalization();

        UpdatePostActionSummary();
        RefreshSelectedTaskValidationSummaryLocalization();
        NotifyRootChromeTextChanged();
        RaiseSelectedTaskProjectionChanged();

        if (SelectedTask is not null || ShowPostActionSettingsPanel)
        {
            ResetSelectedTaskSettingsHost();
        }

        OnPropertyChanged(nameof(RunButtonText));
        OnPropertyChanged(nameof(WaitAndStopButtonText));
        OnPropertyChanged(nameof(BatchActionText));
        OnPropertyChanged(nameof(BatchToggleMenuText));
        OnPropertyChanged(nameof(OverlayMode));
        OnPropertyChanged(nameof(IsOverlayHiddenMode));
        OnPropertyChanged(nameof(IsOverlayPreviewMode));
        OnPropertyChanged(nameof(IsOverlayNativeMode));
        OnPropertyChanged(nameof(OverlayTargetSummaryText));
        OnPropertyChanged(nameof(OverlayButtonToolTip));
        OnPropertyChanged(string.Empty);
        _ = RefreshOverlayStatusTextAsync();
    }

    private void NotifyRootChromeTextChanged()
    {
        OnPropertyChanged(nameof(TaskListTitleText));
        OnPropertyChanged(nameof(TaskConfigTitleText));
        OnPropertyChanged(nameof(LogsTitleText));
        OnPropertyChanged(nameof(OverlayButtonText));
        OnPropertyChanged(nameof(TaskMenuMoveUpText));
        OnPropertyChanged(nameof(TaskMenuMoveDownText));
        OnPropertyChanged(nameof(TaskMenuRenameText));
        OnPropertyChanged(nameof(TaskMenuRunOnceText));
        OnPropertyChanged(nameof(TaskMenuDeleteText));
        OnPropertyChanged(nameof(TaskMenuIconText));
        OnPropertyChanged(nameof(AddTaskButtonText));
        OnPropertyChanged(nameof(SelectAllButtonText));
        OnPropertyChanged(nameof(GeneralSettingsButtonText));
        OnPropertyChanged(nameof(AdvancedSettingsButtonText));
        OnPropertyChanged(nameof(DailyStageLabelText));
        OnPropertyChanged(nameof(DailyStageTooltipText));
        OnPropertyChanged(nameof(AddTaskMenuStartUpText));
        OnPropertyChanged(nameof(AddTaskMenuFightText));
        OnPropertyChanged(nameof(AddTaskMenuInfrastText));
        OnPropertyChanged(nameof(AddTaskMenuRecruitText));
        OnPropertyChanged(nameof(AddTaskMenuMallText));
        OnPropertyChanged(nameof(AddTaskMenuAwardText));
        OnPropertyChanged(nameof(AddTaskMenuRoguelikeText));
        OnPropertyChanged(nameof(AddTaskMenuReclamationText));
        OnPropertyChanged(nameof(AddTaskMenuUserDataUpdateText));
        OnPropertyChanged(nameof(AddTaskMenuCustomText));
    }

    private DialogChromeCatalog CreateTaskQueueDialogChrome(Func<RootLocalizationTextMap, DialogChromeSnapshot> snapshotFactory)
    {
        return DialogTextCatalog.CreateRootCatalog(
            Texts.Language,
            "Root.Localization.TaskQueue",
            snapshotFactory,
            _localizationFallbackReporter);
    }

    public void ApplyGuiSettingsPreview(GuiSettingsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        _logTimestampFormat = NormalizeLogTimestampFormat(snapshot.LogItemDateFormatString);
        _useSystemNotifications = snapshot.UseNotify;
        ApplySelectionBatchMode(snapshot.InverseClearMode);
        RefreshRoguelikeGuiDependentOptions();
    }

    public void SetCoreAvailability(bool isReady, string? message = null)
    {
        IsCoreReady = isReady;
        CoreInitializationMessage = isReady ? string.Empty : (message?.Trim() ?? string.Empty);
    }

    public void RefreshStagePresentation(bool forceReloadStageOptions = false)
    {
        RefreshFightStageOptions(_connectionGameSharedState.ClientType, forceReloadStageOptions);
        DailyStageHint = FightTaskModuleViewModel.BuildDailyResourceHint(
            Texts.Language,
            _connectionGameSharedState.ClientType,
            Runtime.ConfigurationService.CurrentConfig);
    }

    private void OnConnectionGameSharedStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(ConnectionGameSharedStateViewModel.ClientType), StringComparison.Ordinal))
        {
            return;
        }

        RefreshStagePresentation();
    }

    private void RebuildTaskModuleOptions()
    {
        var currentType = TaskModuleTypes.Normalize(_selectedTaskModule);
        var options = AddableTaskModules
            .Select(moduleType => new TaskModuleOption(moduleType, ResolveModuleDisplayName(moduleType)))
            .ToArray();

        TaskModules.Clear();
        foreach (var option in options)
        {
            TaskModules.Add(option);
        }

        var next = options.FirstOrDefault(
                       option => option is not null
                                 && string.Equals(option.Type, currentType, StringComparison.OrdinalIgnoreCase))
                   ?? options.FirstOrDefault();
        _selectedTaskModuleOption = next;
        _selectedTaskModule = next?.Type ?? TaskModuleTypes.StartUp;
        OnPropertyChanged(nameof(SelectedTaskModuleOption));
        OnPropertyChanged(nameof(SelectedTaskModule));
    }

    private string ResolveModuleDisplayName(string moduleType)
    {
        var normalized = TaskModuleTypes.Normalize(moduleType);
        var key = $"TaskQueue.Module.{normalized}";
        return RootTexts.GetOrDefault(key, normalized);
    }

    private string ResolveStatusDisplayName(string status)
    {
        return status switch
        {
            TaskQueueItemStatus.Running => RootTexts.GetOrDefault("TaskQueue.Status.Running", "Running"),
            TaskQueueItemStatus.Success => RootTexts.GetOrDefault("TaskQueue.Status.Success", "Completed"),
            TaskQueueItemStatus.Error => RootTexts.GetOrDefault("TaskQueue.Status.Error", "Error"),
            TaskQueueItemStatus.Skipped => RootTexts.GetOrDefault("TaskQueue.Status.Skipped", "Skipped"),
            TaskQueueItemStatus.Idle => RootTexts.GetOrDefault("TaskQueue.Status.Idle", "Idle"),
            _ => RootTexts.GetOrDefault("TaskQueue.Status.Observed", status),
        };
    }

    private void RefreshTaskItemLocalization(TaskQueueItemViewModel item)
    {
        item.RefreshLocalizedText(ResolveModuleDisplayName, ResolveStatusDisplayName);
        item.DisplayName = ResolveTaskDisplayName(item);
        item.RefreshToolTipText();
        if (ReferenceEquals(item, SelectedTask))
        {
            OnPropertyChanged(nameof(TaskConfigTitleText));
        }
    }

    private void RefreshTaskItemsLocalization()
    {
        // Take a snapshot to avoid enumeration invalidation when callbacks mutate Tasks.
        foreach (var task in Tasks.ToArray())
        {
            RefreshTaskItemLocalization(task);
        }
    }

    private string ResolveTaskDisplayName(TaskQueueItemViewModel item)
    {
        var moduleDisplay = ResolveModuleDisplayName(item.Type);
        var name = (item.Name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return moduleDisplay;
        }

        if (TryResolveLegacyLocalizedTaskTitle(name, item.Type, out var localizedTitle))
        {
            return localizedTitle;
        }

        if (IsDefaultTaskName(name, item.Type, moduleDisplay))
        {
            return moduleDisplay;
        }

        return name;
    }

    private static bool IsDefaultTaskName(string name, string moduleType, string localizedDisplayName)
    {
        var normalizedModuleType = TaskModuleTypes.Normalize(moduleType);
        if (string.Equals(name, normalizedModuleType, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, localizedDisplayName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return LegacyTaskNameAliases.TryGetValue(normalizedModuleType, out var aliases)
               && aliases.Any(alias => string.Equals(name, alias, StringComparison.OrdinalIgnoreCase));
    }

    private bool TryResolveLegacyLocalizedTaskTitle(string name, string moduleType, out string localizedTitle)
    {
        localizedTitle = string.Empty;
        var normalizedModuleType = TaskModuleTypes.Normalize(moduleType);
        if (!LegacyLocalizedTaskTitleKeys.TryGetValue(normalizedModuleType, out var keys))
        {
            return false;
        }

        foreach (var key in keys)
        {
            if (!IsLegacyLocalizedTaskTitleAlias(name, key))
            {
                continue;
            }

            localizedTitle = AchievementTextCatalog.GetString(key, Texts.Language, name);
            return true;
        }

        return false;
    }

    private static bool IsLegacyLocalizedTaskTitleAlias(string name, string key)
    {
        foreach (var language in UiLanguageCatalog.Ordered)
        {
            if (string.Equals(language, "pallas", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var alias = AchievementTextCatalog.GetString(key, language, key);
            if (string.Equals(name, alias, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void OnPostActionModulePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(PostActionModuleViewModel.StatusMessage), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(PostActionModuleViewModel.LastErrorMessage), StringComparison.Ordinal))
        {
            return;
        }

        UpdatePostActionSummary();
    }

    private void UpdatePostActionSummary()
    {
        OnPropertyChanged(nameof(PostActionActionTitle));
        OnPropertyChanged(nameof(TaskConfigTitleText));
        OnPropertyChanged(nameof(PostActionActionDescription));
    }

    private List<string> BuildPostActionActionLabels()
    {
        var actions = new List<string>();
        if (PostActionModule.BackToAndroidHome)
        {
            actions.Add(Texts.GetOrDefault("PostAction.BackToHome", "Back to home"));
        }

        if (PostActionModule.ExitArknights)
        {
            actions.Add(Texts.GetOrDefault("PostAction.ExitArknights", "Exit Arknights"));
        }

        if (PostActionModule.ExitEmulator)
        {
            actions.Add(Texts.GetOrDefault("PostAction.ExitEmulator", "Exit emulator"));
        }

        if (PostActionModule.ExitSelf)
        {
            actions.Add(Texts.GetOrDefault("PostAction.ExitSelf", "Exit MAA"));
        }

        if (PostActionModule.IfNoOtherMaa)
        {
            actions.Add(Texts.GetOrDefault("PostAction.IfNoOther", "Only when no other MAA"));
        }

        if (PostActionModule.Sleep)
        {
            actions.Add(Texts.GetOrDefault("PostAction.Sleep", "Sleep"));
        }

        if (PostActionModule.Hibernate)
        {
            actions.Add(Texts.GetOrDefault("PostAction.Hibernate", "Hibernate"));
        }

        if (PostActionModule.Shutdown)
        {
            actions.Add(Texts.GetOrDefault("PostAction.Shutdown", "Shutdown"));
        }

        return actions;
    }

    public async Task ReloadTasksAsync(
        CancellationToken cancellationToken = default,
        bool preferProfileSelectedIndex = false,
        bool waitForPendingBinding = true)
    {
        var previousSelectedIndex = SelectedTask is null ? -1 : Tasks.IndexOf(SelectedTask);
        var tasks = await ApplyResultAsync(
            await Runtime.TaskQueueFeatureService.GetCurrentTaskQueueAsync(cancellationToken),
            "TaskQueue.Reload",
            cancellationToken);

        if (tasks is null)
        {
            return;
        }

        if (tasks.Count == 0)
        {
            if (!await SeedDefaultTaskQueueAsync(cancellationToken))
            {
                return;
            }

            tasks = await ApplyResultAsync(
                await Runtime.TaskQueueFeatureService.GetCurrentTaskQueueAsync(cancellationToken),
                "TaskQueue.Reload",
                cancellationToken);
            if (tasks is null)
            {
                return;
            }
        }

        foreach (var task in Tasks)
        {
            task.PropertyChanged -= OnTaskPropertyChanged;
        }

        _suppressTaskEnabledSync = true;
        try
        {
            Tasks.Clear();
            foreach (var task in tasks)
            {
                var item = TaskQueueItemViewModel.FromUnifiedTask(task);
                RefreshTaskItemLocalization(item);
                item.PropertyChanged += OnTaskPropertyChanged;
                Tasks.Add(item);
            }
        }
        finally
        {
            _suppressTaskEnabledSync = false;
        }

        await RebuildTaskPanelsAsync(cancellationToken);

        var reloadSelectionIndex = ResolveReloadSelectionIndex(preferProfileSelectedIndex, previousSelectedIndex, Tasks.Count);
        if (reloadSelectionIndex.HasValue)
        {
            SelectedTask = Tasks[reloadSelectionIndex.Value];
        }
        else
        {
            SelectedTask = preferProfileSelectedIndex
                ? null
                : Tasks.FirstOrDefault();
        }

        if (waitForPendingBinding)
        {
            await WaitForPendingBindingAsync(cancellationToken);
        }
    }

    public async Task WaitForPendingBindingAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task pending;
            lock (_pendingBindingGate)
            {
                pending = _pendingBindingTask;
            }

            await pending.WaitAsync(cancellationToken);

            lock (_pendingBindingGate)
            {
                if (ReferenceEquals(pending, _pendingBindingTask))
                {
                    return;
                }
            }
        }
    }

    private async Task<bool> ExecuteQueueMutationAsync(
        string scope,
        Func<CancellationToken, Task<UiOperationResult>> mutationAsync,
        Func<CancellationToken, Task>? onSuccessAsync = null,
        bool resetBindingsBeforeReload = false,
        CancellationToken cancellationToken = default)
    {
        if (!await EnsureEditableAsync(scope, cancellationToken))
        {
            return false;
        }

        await _queueMutationLock.WaitAsync(cancellationToken);
        try
        {
            await WaitForPendingBindingAsync(cancellationToken);
            if (!await SaveBoundTaskModulesAsync(cancellationToken))
            {
                return false;
            }

            var result = await mutationAsync(cancellationToken);
            if (!await ApplyResultAsync(result, scope, cancellationToken))
            {
                return false;
            }

            if (resetBindingsBeforeReload)
            {
                ResetBindingsForStructuralQueueMutation();
            }

            await ReloadTasksAsync(cancellationToken);
            await WaitForPendingBindingAsync(cancellationToken);

            if (onSuccessAsync is not null)
            {
                await onSuccessAsync(cancellationToken);
                await WaitForPendingBindingAsync(cancellationToken);
            }

            RegisterTaskQueueSavePending();
            return true;
        }
        finally
        {
            _queueMutationLock.Release();
        }
    }

    private async Task SelectTaskByIndexAsync(int index, CancellationToken cancellationToken)
    {
        if (index < 0 || index >= Tasks.Count)
        {
            return;
        }

        SelectedTask = Tasks[index];
        await WaitForPendingBindingAsync(cancellationToken);
    }

    private async Task RebuildTaskPanelsAsync(CancellationToken cancellationToken)
    {
        ClearTaskPanels();
        for (var index = 0; index < Tasks.Count; index++)
        {
            var panel = await CreateTaskPanelAsync(Tasks[index], index, cancellationToken);
            TaskPanels.Add(panel);
        }

        ApplySettingsModeToTaskModules();
        UpdateSelectedTaskPanel();
        RaiseModuleProjectionPropertiesChanged();
    }

    private async Task<TaskQueueTaskPanelViewModel> CreateTaskPanelAsync(
        TaskQueueItemViewModel task,
        int index,
        CancellationToken cancellationToken)
    {
        var moduleType = TaskModuleTypes.Normalize(task.Type);
        var module = CreateTaskModuleViewModel(moduleType);
        if (module is INotifyPropertyChanged typedModule
            && module is not TaskModuleSettingsViewModelBase)
        {
            typedModule.PropertyChanged += OnTypedModulePropertyChanged;
        }

        var panel = new TaskQueueTaskPanelViewModel(task, index, moduleType, module);
        try
        {
            await BindTaskPanelAsync(panel, cancellationToken);
            await RefreshTaskPanelValidationSummaryAsync(panel, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            panel.ApplyLoadError(ex.Message);
            await RecordUnhandledExceptionAsync(
                "TaskQueue.PreloadTaskPanel",
                ex,
                UiErrorCode.TaskLoadFailed,
                "Preload task panel failed.");
        }

        return panel;
    }

    private IEnumerable<TModule> EnumeratePanelModules<TModule>()
        where TModule : class
    {
        return TaskPanels
            .Select(static panel => panel.ModuleViewModel)
            .OfType<TModule>();
    }

    private async Task ReloadTaskPanelPersistentConfigAsync(CancellationToken cancellationToken)
    {
        foreach (var module in EnumeratePanelModules<FightTaskModuleViewModel>())
        {
            await module.ReloadPersistentConfigAsync(cancellationToken);
        }

        foreach (var module in EnumeratePanelModules<InfrastModuleViewModel>())
        {
            await module.ReloadPersistentConfigAsync(cancellationToken);
        }

        foreach (var module in EnumeratePanelModules<RoguelikeModuleViewModel>())
        {
            await module.ReloadPersistentConfigAsync(cancellationToken);
        }
    }

    private void RefreshFightStageOptions(string? clientType = null, bool forceReload = false)
    {
        _fallbackFightModule.RefreshStageOptions(clientType, forceReload);
        foreach (var module in EnumeratePanelModules<FightTaskModuleViewModel>())
        {
            module.RefreshStageOptions(clientType, forceReload);
        }
    }

    private void RefreshRoguelikeGuiDependentOptions()
    {
        _fallbackRoguelikeModule.RefreshGuiDependentOptions();
        foreach (var module in EnumeratePanelModules<RoguelikeModuleViewModel>())
        {
            module.RefreshGuiDependentOptions();
        }
    }

    private ITaskModulePanelViewModel CreateTaskModuleViewModel(string moduleType)
    {
        return moduleType switch
        {
            TaskModuleTypes.StartUp => new StartUpTaskModuleViewModel(
                Runtime,
                Texts,
                _connectionGameSharedState,
                RunAccountSwitchManualAsync),
            TaskModuleTypes.Fight => new FightTaskModuleViewModel(Runtime, Texts),
            TaskModuleTypes.Recruit => new RecruitTaskModuleViewModel(Runtime, Texts),
            TaskModuleTypes.Infrast => new InfrastModuleViewModel(Runtime, Texts),
            TaskModuleTypes.Mall => new MallModuleViewModel(Runtime, Texts),
            TaskModuleTypes.Award => new AwardModuleViewModel(Runtime, Texts),
            TaskModuleTypes.Roguelike => new RoguelikeModuleViewModel(Runtime, Texts),
            TaskModuleTypes.Reclamation => new ReclamationModuleViewModel(Runtime, Texts),
            TaskModuleTypes.UserDataUpdate => new UserDataUpdateModuleViewModel(Runtime, Texts),
            TaskModuleTypes.Custom => new CustomModuleViewModel(Runtime, Texts),
            _ => new CustomModuleViewModel(Runtime, Texts),
        };
    }

    private async Task BindTaskPanelAsync(TaskQueueTaskPanelViewModel panel, CancellationToken cancellationToken)
    {
        switch (panel.ModuleViewModel)
        {
            case StartUpTaskModuleViewModel startUp:
                await startUp.BindAsync(panel.TaskIndex, cancellationToken);
                break;
            case FightTaskModuleViewModel fight:
                await fight.BindAsync(panel.TaskIndex, cancellationToken);
                break;
            case RecruitTaskModuleViewModel recruit:
                await recruit.BindAsync(panel.TaskIndex, cancellationToken);
                break;
            case RoguelikeModuleViewModel roguelike:
                await roguelike.BindAsync(panel.TaskIndex, cancellationToken);
                break;
            case ReclamationModuleViewModel reclamation:
                await reclamation.BindAsync(panel.TaskIndex, cancellationToken);
                break;
            case CustomModuleViewModel custom:
                await custom.BindAsync(panel.TaskIndex, cancellationToken);
                break;
            case TaskModuleSettingsViewModelBase jsonModule:
                var paramsResult = await Runtime.TaskQueueFeatureService.GetTaskParamsAsync(panel.TaskIndex, cancellationToken);
                if (!paramsResult.Success || paramsResult.Value is null)
                {
                    LastErrorMessage = paramsResult.Message;
                    panel.ApplyLoadError(paramsResult.Message);
                    return;
                }

                await jsonModule.BindAsync(panel.TaskIndex, paramsResult.Value, cancellationToken);
                break;
        }
    }

    private async Task RefreshTaskPanelValidationSummaryAsync(
        TaskQueueTaskPanelViewModel panel,
        CancellationToken cancellationToken)
    {
        var result = await Runtime.TaskQueueFeatureService.ValidateTaskAsync(panel.TaskIndex, cancellationToken);
        if (!result.Success || result.Value is null)
        {
            panel.ApplyValidationLoadFailure(Texts, result.Message);
            LastErrorMessage = result.Message;
            return;
        }

        panel.ClearLoadError();
        panel.ApplyValidationReport(result.Value, Texts);
        if (ReferenceEquals(panel, SelectedTaskPanel))
        {
            ProjectSelectedTaskValidationSummary();
        }
    }

    private async void OnTaskPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is TaskQueueItemViewModel task
            && ReferenceEquals(task, SelectedTask)
            && (string.Equals(e.PropertyName, nameof(TaskQueueItemViewModel.DisplayName), StringComparison.Ordinal)
                || string.Equals(e.PropertyName, nameof(TaskQueueItemViewModel.Name), StringComparison.Ordinal)))
        {
            OnPropertyChanged(nameof(TaskConfigTitleText));
        }

        if (!string.Equals(e.PropertyName, nameof(TaskQueueItemViewModel.IsEnabled), StringComparison.Ordinal))
        {
            return;
        }

        if (sender is not TaskQueueItemViewModel changedTask || _suppressTaskEnabledSync)
        {
            return;
        }

        try
        {
            await PersistTaskEnabledStateAsync(changedTask);
        }
        catch (Exception ex)
        {
            await RecordUnhandledExceptionAsync(
                "TaskQueue.SetTaskEnabled",
                ex,
                UiErrorCode.UiOperationFailed,
                "Failed to persist task enabled state.");
        }
    }

    private async Task PersistTaskEnabledStateAsync(TaskQueueItemViewModel task, CancellationToken cancellationToken = default)
    {
        var desiredEnabled = task.IsEnabled;
        if (!await EnsureEditableAsync("TaskQueue.SetTaskEnabled", cancellationToken))
        {
            _suppressTaskEnabledSync = true;
            try
            {
                task.IsEnabled = !desiredEnabled;
            }
            finally
            {
                _suppressTaskEnabledSync = false;
            }

            return;
        }

        var index = Tasks.IndexOf(task);
        if (index < 0)
        {
            return;
        }

        await _queueMutationLock.WaitAsync(cancellationToken);
        try
        {
            await WaitForPendingBindingAsync(cancellationToken);
            if (!await SaveBoundTaskModulesAsync(cancellationToken))
            {
                return;
            }

            var result = await Runtime.TaskQueueFeatureService.SetTaskEnabledAsync(index, desiredEnabled, cancellationToken);
            if (!await ApplyResultAsync(result, "TaskQueue.SetTaskEnabled", cancellationToken))
            {
                _suppressTaskEnabledSync = true;
                try
                {
                    task.IsEnabled = !desiredEnabled;
                }
                finally
                {
                    _suppressTaskEnabledSync = false;
                }
            }
        }
        finally
        {
            _queueMutationLock.Release();
        }
    }

    public async Task AddTaskAsync(string? taskType = null, CancellationToken cancellationToken = default)
    {
        var normalizedTaskType = TaskModuleTypes.Normalize(taskType ?? SelectedTaskModuleOption?.Type ?? SelectedTaskModule);
        var taskName = ResolveModuleDisplayName(normalizedTaskType);
        await ExecuteQueueMutationAsync(
            "TaskQueue.AddTask",
            ct => Runtime.TaskQueueFeatureService.AddTaskAsync(normalizedTaskType, taskName, true, ct),
            resetBindingsBeforeReload: true,
            cancellationToken: cancellationToken);
    }

    public async Task RemoveSelectedTaskAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedTask is null)
        {
            LastErrorMessage = Texts["TaskQueue.Error.SelectTaskToRemove"];
            return;
        }

        var index = Tasks.IndexOf(SelectedTask);
        if (index < 0)
        {
            LastErrorMessage = Texts["TaskQueue.Error.SelectTaskToRemove"];
            return;
        }

        await ExecuteQueueMutationAsync(
            "TaskQueue.RemoveTask",
            ct => Runtime.TaskQueueFeatureService.RemoveTaskAsync(index, ct),
            resetBindingsBeforeReload: true,
            cancellationToken: cancellationToken);
    }

    public async Task RenameSelectedTaskAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedTask is null)
        {
            LastErrorMessage = Texts["TaskQueue.Error.SelectTaskToRename"];
            return;
        }

        var index = Tasks.IndexOf(SelectedTask);
        if (index < 0)
        {
            LastErrorMessage = Texts["TaskQueue.Error.SelectTaskToRename"];
            return;
        }

        await ExecuteQueueMutationAsync(
            "TaskQueue.RenameTask",
            ct => Runtime.TaskQueueFeatureService.RenameTaskAsync(index, RenameTargetName, ct),
            cancellationToken: cancellationToken);
    }

    public async Task RenameSelectedTaskWithDialogAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedTask is null)
        {
            LastErrorMessage = Texts["TaskQueue.Error.SelectTaskToRename"];
            return;
        }

        var index = Tasks.IndexOf(SelectedTask);
        if (index < 0)
        {
            LastErrorMessage = Texts["TaskQueue.Error.SelectTaskToRename"];
            return;
        }

        var chrome = CreateTaskQueueDialogChrome(
            texts => new DialogChromeSnapshot(
                title: string.Format(
                    texts.GetOrDefault("TaskQueue.Root.RenameDialogTitle", "Rename Task {0}"),
                    index + 1),
                confirmText: texts.GetOrDefault("TaskQueue.Root.RenameDialogConfirm", "Confirm"),
                cancelText: texts.GetOrDefault("TaskQueue.Root.RenameDialogCancel", "Cancel"),
                namedTexts: DialogTextCatalog.CreateNamedTexts(
                    (DialogTextCatalog.ChromeKeys.Prompt, texts.GetOrDefault("TaskQueue.Root.RenameDialogPrompt", "Rename task")))));
        var chromeSnapshot = chrome.GetSnapshot();
        var request = new TextDialogRequest(
            Title: chromeSnapshot.Title,
            Prompt: chromeSnapshot.GetNamedTextOrDefault(
                DialogTextCatalog.ChromeKeys.Prompt,
                RootTexts.GetOrDefault("TaskQueue.Root.RenameDialogPrompt", "Rename task")),
            DefaultText: SelectedTask.Name,
            MultiLine: false,
            ConfirmText: chromeSnapshot.ConfirmText ?? RootTexts.GetOrDefault("TaskQueue.Root.RenameDialogConfirm", "Confirm"),
            CancelText: chromeSnapshot.CancelText ?? RootTexts.GetOrDefault("TaskQueue.Root.RenameDialogCancel", "Cancel"),
            Chrome: chrome);
        var dialogResult = await _dialogService.ShowTextAsync(request, "TaskQueue.RenameTask.Dialog", cancellationToken);
        if (dialogResult.Return == DialogReturnSemantic.Confirm && dialogResult.Payload is not null)
        {
            var nextName = (dialogResult.Payload.Text ?? string.Empty).Trim();
            if (nextName.Length == 0)
            {
                LastErrorMessage = Texts.GetOrDefault("TaskQueue.Error.TaskNameMissingShort", "Task name cannot be empty.");
                return;
            }

            RenameTargetName = nextName;
            await RenameSelectedTaskAsync(cancellationToken);
            return;
        }

        StatusMessage = dialogResult.Return == DialogReturnSemantic.Cancel
            ? RootTexts.GetOrDefault("TaskQueue.Root.RenameDialogCancelStatus", "Rename cancelled.")
            : RootTexts.GetOrDefault("TaskQueue.Root.RenameDialogClosedStatus", "Rename dialog closed.");
    }

    public async Task MoveSelectedTaskAsync(int delta, CancellationToken cancellationToken = default)
    {
        if (SelectedTask is null)
        {
            LastErrorMessage = Texts["TaskQueue.Error.SelectTaskToMove"];
            return;
        }

        var from = Tasks.IndexOf(SelectedTask);
        if (from < 0)
        {
            return;
        }

        await MoveSelectedTaskToAsync(from + delta, cancellationToken);
    }

    public async Task MoveSelectedTaskToAsync(int targetIndex, CancellationToken cancellationToken = default)
    {
        if (SelectedTask is null)
        {
            LastErrorMessage = Texts["TaskQueue.Error.SelectTaskToMove"];
            return;
        }

        var from = Tasks.IndexOf(SelectedTask);
        var to = Math.Clamp(targetIndex, 0, Tasks.Count - 1);
        if (from < 0 || from == to)
        {
            return;
        }

        if (!await EnsureEditableAsync("TaskQueue.MoveTask", cancellationToken))
        {
            return;
        }

        await _queueMutationLock.WaitAsync(cancellationToken);
        try
        {
            await WaitForPendingBindingAsync(cancellationToken);
            if (!await SaveBoundTaskModulesAsync(cancellationToken))
            {
                return;
            }

            var result = await Runtime.TaskQueueFeatureService.MoveTaskAsync(from, to, cancellationToken);
            if (!await ApplyResultAsync(result, "TaskQueue.MoveTask", cancellationToken))
            {
                return;
            }

            ApplyLocalTaskMove(from, to);
            RegisterTaskQueueSavePending();

            if (SelectedTaskPanel is not null)
            {
                await RefreshTaskPanelValidationSummaryAsync(SelectedTaskPanel, cancellationToken);
            }
        }
        finally
        {
            _queueMutationLock.Release();
        }
    }

    private void ApplyLocalTaskMove(int from, int to)
    {
        if (from < 0 || to < 0 || from >= Tasks.Count || to >= Tasks.Count || from == to)
        {
            return;
        }

        Tasks.Move(from, to);

        if (from < TaskPanels.Count && to < TaskPanels.Count)
        {
            TaskPanels.Move(from, to);
            for (var index = 0; index < TaskPanels.Count; index++)
            {
                TaskPanels[index].RebindTaskIndex(index);
            }
        }

        RememberSelectedTaskIndex();
        UpdateSelectedTaskPanel();
        RaiseSelectedTaskProjectionChanged();
    }

    public async Task SelectAllAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await ExecuteTaskEnabledBatchAsync(
            "TaskQueue.SelectAll",
            ct => Runtime.TaskQueueFeatureService.SetAllTasksEnabledAsync(enabled, ct),
            _ => enabled,
            cancellationToken: cancellationToken);
    }

    public async Task InverseSelectionAsync(CancellationToken cancellationToken = default)
    {
        await ExecuteTaskEnabledBatchAsync(
            "TaskQueue.InverseSelection",
            ct => Runtime.TaskQueueFeatureService.InvertTasksEnabledAsync(ct),
            current => !current,
            cancellationToken: cancellationToken);
    }

    private async Task<bool> ExecuteTaskEnabledBatchAsync(
        string scope,
        Func<CancellationToken, Task<UiOperationResult>> mutationAsync,
        Func<bool, bool> resolveEnabled,
        CancellationToken cancellationToken = default)
    {
        if (!await EnsureEditableAsync(scope, cancellationToken))
        {
            return false;
        }

        await _queueMutationLock.WaitAsync(cancellationToken);
        try
        {
            var result = await mutationAsync(cancellationToken);
            if (!await ApplyResultAsync(result, scope, cancellationToken))
            {
                return false;
            }

            ApplyLocalTaskEnabledBatch(resolveEnabled);
            RegisterTaskQueueSavePending();
            return true;
        }
        finally
        {
            _queueMutationLock.Release();
        }
    }

    private void ApplyLocalTaskEnabledBatch(Func<bool, bool> resolveEnabled)
    {
        _suppressTaskEnabledSync = true;
        try
        {
            foreach (var task in Tasks)
            {
                var next = resolveEnabled(task.IsEnabled);
                if (task.IsEnabled != next)
                {
                    task.IsEnabled = next;
                }
            }
        }
        finally
        {
            _suppressTaskEnabledSync = false;
        }
    }

    public async Task ExecuteBatchActionAsync(CancellationToken cancellationToken = default)
    {
        if (SelectionBatchMode == SelectionBatchMode.Inverse)
        {
            await InverseSelectionAsync(cancellationToken);
            return;
        }

        await SelectAllAsync(false, cancellationToken);
    }

    public async Task ToggleSelectionBatchModeAsync(CancellationToken cancellationToken = default)
    {
        if (!ShowBatchModeToggle)
        {
            return;
        }

        SelectionBatchMode = SelectionBatchMode == SelectionBatchMode.Inverse
            ? SelectionBatchMode.Clear
            : SelectionBatchMode.Inverse;
        await PersistSelectionBatchModeAsync(cancellationToken);
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        if (!await EnsureEditableAsync("TaskQueue.Save", cancellationToken))
        {
            return;
        }

        if (!await SaveBoundTaskModulesAsync(cancellationToken))
        {
            return;
        }

        _ = await SaveTaskQueueTrackedAsync(cancellationToken);
    }

    private async Task<bool> SeedDefaultTaskQueueAsync(CancellationToken cancellationToken)
    {
        foreach (var moduleType in WpfDefaultTaskOrder)
        {
            var addResult = await Runtime.TaskQueueFeatureService.AddTaskAsync(
                moduleType,
                ResolveModuleDisplayName(moduleType),
                enabled: true,
                cancellationToken);
            if (!await ApplyResultAsync(addResult, "TaskQueue.SeedDefaults.Add", cancellationToken))
            {
                return false;
            }
        }

        return await SaveTaskQueueTrackedAsync(cancellationToken, "TaskQueue.SeedDefaults.Save");
    }

    private bool IsSelectedTaskType(string moduleType)
    {
        return SelectedTask is not null
            && string.Equals(
                TaskModuleTypes.Normalize(SelectedTask.Type),
                moduleType,
                StringComparison.OrdinalIgnoreCase);
    }

    private TModule? ResolveModuleForProjection<TModule>()
        where TModule : class
    {
        if (SelectedTaskPanel?.ModuleViewModel is TModule selectedModule)
        {
            return selectedModule;
        }

        return TaskPanels
            .Select(static panel => panel.ModuleViewModel)
            .OfType<TModule>()
            .FirstOrDefault();
    }

    private void UpdateSelectedTaskPanel()
    {
        var next = SelectedTask is null
            ? null
            : TaskPanels.FirstOrDefault(panel => ReferenceEquals(panel.Task, SelectedTask));

        foreach (var panel in TaskPanels)
        {
            panel.IsSelected = ReferenceEquals(panel, next) && !IsPostActionPanelSelected;
        }

        SelectedTaskPanel = next;
    }

    private void RaiseModuleProjectionPropertiesChanged()
    {
        OnPropertyChanged(nameof(StartUpModule));
        OnPropertyChanged(nameof(FightModule));
        OnPropertyChanged(nameof(RecruitModule));
        OnPropertyChanged(nameof(InfrastModule));
        OnPropertyChanged(nameof(MallModule));
        OnPropertyChanged(nameof(AwardModule));
        OnPropertyChanged(nameof(RoguelikeModule));
        OnPropertyChanged(nameof(ReclamationModule));
        OnPropertyChanged(nameof(UserDataUpdateModule));
        OnPropertyChanged(nameof(CustomModule));
    }

    private void RaiseSelectedTaskProjectionChanged()
    {
        OnPropertyChanged(nameof(IsNoTaskSelected));
        OnPropertyChanged(nameof(ShowTaskConfigHint));
        OnPropertyChanged(nameof(TaskConfigTitleText));
        OnPropertyChanged(nameof(IsStartUpTaskSelected));
        OnPropertyChanged(nameof(IsFightTaskSelected));
        OnPropertyChanged(nameof(IsRecruitTaskSelected));
        OnPropertyChanged(nameof(IsInfrastTaskSelected));
        OnPropertyChanged(nameof(IsMallTaskSelected));
        OnPropertyChanged(nameof(IsAwardTaskSelected));
        OnPropertyChanged(nameof(IsRoguelikeTaskSelected));
        OnPropertyChanged(nameof(IsReclamationTaskSelected));
        OnPropertyChanged(nameof(IsUserDataUpdateTaskSelected));
        OnPropertyChanged(nameof(IsCustomTaskSelected));
        OnPropertyChanged(nameof(IsPostActionTaskSelected));
        OnPropertyChanged(nameof(ShowPostActionSettingsPanel));
        OnPropertyChanged(nameof(SelectedTaskPanel));
        OnPropertyChanged(nameof(SelectedTaskSettingsViewModel));
        OnPropertyChanged(nameof(CanUseAdvancedSettings));
        OnPropertyChanged(nameof(ShowSettingsModeSwitch));
    }

    private void ResetSelectedTaskSettingsHost()
    {
        if (_selectedTaskSettingsHostResetPending)
        {
            return;
        }

        _selectedTaskSettingsHostResetPending = true;
        OnPropertyChanged(nameof(SelectedTaskSettingsViewModel));
        Dispatcher.UIThread.Post(() =>
        {
            _selectedTaskSettingsHostResetPending = false;
            OnPropertyChanged(nameof(SelectedTaskSettingsViewModel));
        });
    }

    private void ResetSettingsModeForSelectedTask()
    {
        SetAdvancedSettingsSelected(false);
    }

    private void SetAdvancedSettingsSelected(bool selected)
    {
        var normalized = selected && CanUseAdvancedSettings;
        var changed = SetProperty(ref _isAdvancedSettingsSelected, normalized, nameof(IsAdvancedSettingsSelected));
        if (changed)
        {
            OnPropertyChanged(nameof(IsGeneralSettingsSelected));
        }

        ApplySettingsModeToTaskModules();
    }

    private void ApplySettingsModeToTaskModules()
    {
        _fallbackStartUpModule.IsAdvancedMode = _isAdvancedSettingsSelected;
        _fallbackFightModule.IsAdvancedMode = _isAdvancedSettingsSelected;
        _fallbackRecruitModule.IsAdvancedMode = _isAdvancedSettingsSelected;
        _fallbackInfrastModule.IsAdvancedMode = _isAdvancedSettingsSelected;
        _fallbackMallModule.IsAdvancedMode = _isAdvancedSettingsSelected;
        _fallbackAwardModule.IsAdvancedMode = _isAdvancedSettingsSelected;
        _fallbackRoguelikeModule.IsAdvancedMode = _isAdvancedSettingsSelected;
        _fallbackReclamationModule.IsAdvancedMode = _isAdvancedSettingsSelected;
        _fallbackUserDataUpdateModule.IsAdvancedMode = _isAdvancedSettingsSelected;
        _fallbackCustomModule.IsAdvancedMode = _isAdvancedSettingsSelected;

        foreach (var panel in TaskPanels)
        {
            panel.Module.IsAdvancedMode = _isAdvancedSettingsSelected;
        }
    }

    private void OnTypedModulePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressModuleAutoSave || !CanEdit || string.IsNullOrWhiteSpace(e.PropertyName))
        {
            return;
        }

        var shouldSave = sender switch
        {
            StartUpTaskModuleViewModel startUp => startUp.IsDirty,
            FightTaskModuleViewModel fight => fight.IsDirty,
            RecruitTaskModuleViewModel recruit => recruit.IsDirty,
            RoguelikeModuleViewModel roguelike => roguelike.IsDirty,
            ReclamationModuleViewModel reclamation => reclamation.IsDirty,
            CustomModuleViewModel custom => custom.IsDirty,
            _ => false,
        };
        if (!shouldSave
            || string.Equals(e.PropertyName, nameof(StartUpTaskModuleViewModel.StatusMessage), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(StartUpTaskModuleViewModel.LastErrorMessage), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(StartUpTaskModuleViewModel.IsTaskBound), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(StartUpTaskModuleViewModel.IsDirty), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(StartUpTaskModuleViewModel.HasValidationIssues), StringComparison.Ordinal))
        {
            return;
        }

        ScheduleTypedModuleAutoSave();
    }

    private void ScheduleTypedModuleAutoSave()
    {
        lock (_moduleAutoSaveGate)
        {
            ConfigurationSaveTracker.Instance.MarkPending(
                "TaskQueue.TypedModules",
                ResolveTypedModulesSaveDisplayName(),
                "TaskQueue.TypedModules.Flush",
                Runtime.DiagnosticsService,
                SaveBoundTaskModulesAsync);
            _moduleAutoSaveCts?.Cancel();
            _moduleAutoSaveCts?.Dispose();
            _moduleAutoSaveCts = new CancellationTokenSource();
            _ = PersistTypedModulesDebouncedAsync(_moduleAutoSaveCts.Token);
        }
    }

    private async Task PersistTypedModulesDebouncedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(450, cancellationToken);
            await _queueMutationLock.WaitAsync(cancellationToken);
            try
            {
                await WaitForPendingBindingAsync(cancellationToken);
                if (!CanEdit)
                {
                    return;
                }

                _suppressModuleAutoSave = true;
                _ = await SaveTypedModulesTrackedAsync(cancellationToken);
            }
            finally
            {
                _suppressModuleAutoSave = false;
                _queueMutationLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Debounced autosave canceled by newer edits.
        }
        catch (Exception ex)
        {
            await RecordUnhandledExceptionAsync(
                "TaskQueue.AutoSave",
                ex,
                UiErrorCode.TaskParamFlushFailed,
                "TaskQueue autosave failed.");
        }
    }

    public async Task ToggleRunAsync(CancellationToken cancellationToken = default)
    {
        CurrentSessionState = Runtime.SessionService.CurrentState;
        if (_isStartRequestActive)
        {
            await StopStartRequestAsync(cancellationToken);
            return;
        }

        if (CurrentSessionState is SessionState.Running or SessionState.Stopping)
        {
            if (IsOwnRunActive)
            {
                await StopAsync(cancellationToken);
                return;
            }

            await ShowRunOwnerDialogAsync(cancellationToken);
            return;
        }

        await StartAsync(cancellationToken);
    }

    private async Task ShowRunOwnerDialogAsync(CancellationToken cancellationToken)
    {
        var owner = Runtime.SessionService.CurrentRunOwner;
        if (string.IsNullOrWhiteSpace(owner))
        {
            owner = TaskQueueRunOwner;
        }

        var displayOwner = Runtime.SessionService.CurrentRunOwnerDisplayName;
        if (string.IsNullOrWhiteSpace(displayOwner))
        {
            displayOwner = owner;
        }

        var message = string.Format(
            RootTexts.GetOrDefault(
                "Copilot.RunOwnerBlockedDialog.Message",
                "{0} is still running. Stop the current task before starting Copilot."),
            displayOwner);

        var chrome = CreateRunOwnerDialogChrome(RootTexts.Language, displayOwner);
        var snapshot = chrome.GetSnapshot(RootTexts.Language);
        var request = new WarningConfirmDialogRequest(
            Title: snapshot.Title,
            Message: snapshot.GetNamedTextOrDefault(DialogTextCatalog.ChromeKeys.Prompt, message),
            ConfirmText: snapshot.ConfirmText ?? DialogTextCatalog.WarningDialogConfirmButton(RootTexts.Language),
            CancelText: snapshot.CancelText ?? RootTexts.GetOrDefault("Copilot.RunOwnerBlockedDialog.StopButton", "Stop task"),
            Language: RootTexts.Language,
            Chrome: chrome);

        var result = await _dialogService.ShowWarningConfirmAsync(request, "TaskQueue.RunOwnerBlocked.Dialog", cancellationToken);
        if (result.Return != DialogReturnSemantic.Cancel)
        {
            return;
        }

        if (_stopRunOwnerAsync is not null)
        {
            await _stopRunOwnerAsync(owner, cancellationToken);
            CurrentSessionState = Runtime.SessionService.CurrentState;
            return;
        }

        if (Runtime.SessionService.IsRunOwner(TaskQueueRunOwner))
        {
            await StopAsync(cancellationToken);
        }
    }

    private static DialogChromeCatalog CreateRunOwnerDialogChrome(string language, string owner)
    {
        return DialogTextCatalog.CreateCatalog(
            language,
            nextLanguage =>
            {
                var texts = new RootLocalizationTextMap("Root.Localization.TaskQueue")
                {
                    Language = nextLanguage,
                };
                var title = texts.GetOrDefault("Toolbox.BusyDialog.Title", "Task is running");
                return new DialogChromeSnapshot(
                    title: title,
                    confirmText: texts.GetOrDefault("Toolbox.BusyDialog.ConfirmButton", "Cancel"),
                    cancelText: texts.GetOrDefault("Toolbox.BusyDialog.StopButton", "Stop current task"),
                    namedTexts: DialogTextCatalog.CreateNamedTexts(
                        (DialogTextCatalog.ChromeKeys.SectionTitle, title),
                        (DialogTextCatalog.ChromeKeys.DetailsButton, texts.GetOrDefault(
                            "Toolbox.BusyDialog.DetailsButton",
                            DialogTextCatalog.WarningDialogDetailsButton(nextLanguage))),
                        (DialogTextCatalog.ChromeKeys.Prompt, string.Format(
                            CultureInfo.InvariantCulture,
                            texts.GetOrDefault(
                                "Copilot.RunOwnerBlockedDialog.Message",
                                "{0} is still running. Stop the current task before starting."),
                            owner))));
            });
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _runTransitionLock.WaitAsync(cancellationToken);
        CancellationTokenSource? startRequestCts = null;
        try
        {
            ClearVisibleRuntimeLogs();
            if (!Runtime.SessionService.TryBeginRun(TaskQueueRunOwner, out var currentOwner))
            {
                var owner = string.IsNullOrWhiteSpace(currentOwner) ? "Unknown" : currentOwner;
                var message = $"TaskQueue start blocked by active run owner `{owner}`.";
                LastErrorMessage = message;
                AppendStartFailureLog(LastErrorMessage);
                await RecordFailedResultAsync(
                    "TaskQueue.Start.RunOwner",
                    UiOperationResult.Fail(UiErrorCode.TaskQueueEditBlocked, message),
                    cancellationToken);
                await ShowRunOwnerDialogAsync(cancellationToken);
                return;
            }

            var keepRunOwner = false;
            startRequestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _startRequestCts = startRequestCts;
            var startCancellationToken = startRequestCts.Token;
            SetStartRequestActive(true);
            try
            {
                if (!await EnsureCoreReadyAsync("TaskQueue.Start.CoreWarmup", startCancellationToken))
                {
                    return;
                }

                await WaitForPendingBindingAsync(startCancellationToken);
                if (!await SaveBoundTaskModulesAsync(startCancellationToken))
                {
                    return;
                }

                RefreshConfigValidationState(Runtime.ConfigurationService.RevalidateCurrentConfig());
                if (HasBlockingConfigIssues)
                {
                    var first = Runtime.ConfigurationService.CurrentValidationIssues.FirstOrDefault(i => i.Blocking);
                    LastErrorMessage = first is null
                        ? "Config validation has blocking issues."
                        : $"{first.Scope}:{first.Code}:{first.Field}:{first.Message}";
                    await NavigateToFirstBlockingIssueAsync(first, startCancellationToken);
                    if (!string.IsNullOrWhiteSpace(SelectedTask?.Name)
                        && !LastErrorMessage.Contains(SelectedTask.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        LastErrorMessage = $"{LastErrorMessage} ({SelectedTask.Name})";
                    }

                    AppendStartFailureLog(LastErrorMessage);

                    await RecordConfigValidationFailureAsync(first, startCancellationToken);
                    return;
                }

                if (!await EnsureConnectedForLinkStartAsync("TaskQueue.Start", startCancellationToken))
                {
                    return;
                }

                _clearTaskStatusesWhenStopped = false;

                var precheckWarnings = await Runtime.TaskQueueFeatureService.GetStartPrecheckWarningsAsync(startCancellationToken);
                if (!precheckWarnings.Success)
                {
                    _ = await ApplyResultAsync(precheckWarnings, "TaskQueue.Start.Precheck", startCancellationToken);
                    return;
                }

                var warnings = precheckWarnings.Value ?? [];
                if (warnings.Count > 0)
                {
                    var warningMessage = string.Join(
                        " ",
                        warnings.Select(static warning => warning.Message));
                    await RecordEventAsync(
                        "TaskQueue.Start.PrecheckWarning",
                        warningMessage,
                        startCancellationToken);
                }

                if (warnings.Any(static warning => string.Equals(
                        warning.Code,
                        UiErrorCode.MallCreditFightDowngraded,
                        StringComparison.Ordinal)))
                {
                    var downgradeResult = await Runtime.TaskQueueFeatureService.ApplyStartPrecheckDowngradesAsync(startCancellationToken);
                    if (!downgradeResult.Success)
                    {
                        _ = await ApplyResultAsync(downgradeResult, "TaskQueue.Start.PrecheckApply", startCancellationToken);
                        return;
                    }
                }

                if (!await ValidateEnabledTasksBeforeStartAsync(startCancellationToken))
                {
                    return;
                }

                var appendResult = await Runtime.TaskQueueFeatureService.QueueEnabledTasksAsync(startCancellationToken);
                if (!appendResult.Success)
                {
                    var error = UiOperationResult<int>.FromCore(appendResult, "Tasks queued.");
                    _ = await ApplyResultAsync(error, "TaskQueue.Append", startCancellationToken);
                    return;
                }

                if (!await ApplyResultAsync(
                        await Runtime.ConnectFeatureService.StartAsync(startCancellationToken),
                        "TaskQueue.Start",
                        startCancellationToken))
                {
                    return;
                }

                ResetRuntimeLogState();
                ResetAllTaskStatuses();
                CurrentSessionState = Runtime.SessionService.CurrentState;
                _currentRunId = Guid.NewGuid().ToString("N");
                _lastPostActionRunId = string.Empty;
                TrackAchievementsAfterStart(appendResult.Value);
                keepRunOwner = true;
                SetStartRequestActive(false);
            }
            catch (OperationCanceledException) when (startRequestCts.IsCancellationRequested)
            {
                CurrentSessionState = Runtime.SessionService.CurrentState;
                SyncStoppedUiStateIfSessionNotActive();
            }
            finally
            {
                if (!keepRunOwner)
                {
                    Runtime.SessionService.EndRun(TaskQueueRunOwner);
                    SetStartRequestActive(false);
                }

                if (ReferenceEquals(_startRequestCts, startRequestCts))
                {
                    _startRequestCts = null;
                }
            }
        }
        finally
        {
            startRequestCts?.Dispose();
            _runTransitionLock.Release();
        }
    }

    private async Task StopStartRequestAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sessionStateAtCancelRequest = Runtime.SessionService.CurrentState;
        _startRequestCts?.Cancel();
        Runtime.SessionService.EndRun(TaskQueueRunOwner);
        _clearTaskStatusesWhenStopped = true;
        _ = Runtime.AchievementTrackerService.Unlock("TacticalRetreat");

        CurrentSessionState = Runtime.SessionService.CurrentState;
        SyncStoppedUiStateIfSessionNotActive();
        SetStartRequestActive(false);
        if (sessionStateAtCancelRequest is not (SessionState.Connecting or SessionState.Running or SessionState.Stopping))
        {
            return;
        }

        _stopStartRequestTask = StopStartRequestCoreWithTimeoutAsync(cancellationToken);
        var settleWindow = Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        await Task.WhenAny(_stopStartRequestTask, settleWindow);
    }

    private async Task StopStartRequestCoreWithTimeoutAsync(CancellationToken cancellationToken)
    {
        using var stopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stopCts.CancelAfter(LinkStartCancelStopTimeout);
        await StopStartRequestCoreAsync(stopCts.Token);
    }

    private async Task StopStartRequestCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            var stopResult = await Runtime.ConnectFeatureService.StopAsync(cancellationToken);
            if (!stopResult.Success
                && Runtime.SessionService.CurrentState is SessionState.Running or SessionState.Stopping)
            {
                await RecordFailedResultAsync(
                    "TaskQueue.StopStartRequest",
                    stopResult,
                    CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            await RecordFailedResultAsync(
                "TaskQueue.StopStartRequest",
                UiOperationResult.Fail(
                    UiErrorCode.OperationAlreadyStopped,
                    $"Stop for canceled LinkStart did not settle within {LinkStartCancelStopTimeout.TotalSeconds:N0}s."),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            await RecordErrorAsync(
                "TaskQueue.StopStartRequest",
                "Background stop for canceled LinkStart failed.",
                ex);
        }
        finally
        {
            Dispatcher.UIThread.Post(() =>
            {
                CurrentSessionState = Runtime.SessionService.CurrentState;
                SyncStoppedUiStateIfSessionNotActive();
            });
        }
    }

    private async Task<bool> EnsureCoreReadyAsync(string scope, CancellationToken cancellationToken)
    {
        if (IsCoreReady)
        {
            return true;
        }

        if (_ensureCoreReadyForExecutionAsync is not null)
        {
            var warmed = await _ensureCoreReadyForExecutionAsync(cancellationToken);
            if (warmed)
            {
                return true;
            }
        }

        LastErrorMessage = string.IsNullOrWhiteSpace(CoreInitializationMessage)
            ? BuildLocalizedMessage(
                "核心初始化尚未完成，请稍候重试。",
                "Core initialization is still in progress. Please wait a moment and try again.")
            : CoreInitializationMessage;
        AppendStartFailureLog(LastErrorMessage);
        await RecordFailedResultAsync(
            scope,
            UiOperationResult.Fail(UiErrorCode.SessionStateNotAllowed, LastErrorMessage),
            cancellationToken);
        return false;
    }

    private string BuildLinkStartStateNotAllowedMessage(SessionState state)
    {
        var zh = $"会话状态 `{state}` 不允许 LinkStart。请先前往“设置 > 连接设置”完成连接。";
        var en = $"Session state `{state}` does not allow LinkStart. Go to Settings > Connection and connect first.";
        return BuildLocalizedMessage(zh, en);
    }

    private async Task<bool> EnsureConnectedForLinkStartAsync(string scope, CancellationToken cancellationToken)
    {
        if (!await EnsureCoreReadyAsync(scope, cancellationToken))
        {
            return false;
        }

        CurrentSessionState = Runtime.SessionService.CurrentState;
        Runtime.LogService.Debug($"LinkStart precheck: state={CurrentSessionState}");
        CoreConnectionInfo? requiredConnection = null;
        if (CurrentSessionState == SessionState.Connected)
        {
            var currentConnection = BuildCurrentConnectionInfo();
            if (Runtime.SessionService.IsConnectedWith(currentConnection))
            {
                return true;
            }

            requiredConnection = currentConnection;
            Runtime.LogService.Info(
                $"LinkStart reconnect required because connection settings changed: current address={currentConnection.Address}, config={currentConnection.ConnectConfig}, adb={currentConnection.AdbPath ?? "<null>"}; previous address={Runtime.SessionService.LastSuccessfulConnectionInfo?.Address ?? "<null>"}, config={Runtime.SessionService.LastSuccessfulConnectionInfo?.ConnectConfig ?? "<null>"}, adb={Runtime.SessionService.LastSuccessfulConnectionInfo?.AdbPath ?? "<null>"}");
        }

        if (CurrentSessionState is SessionState.Running or SessionState.Stopping)
        {
            LastErrorMessage = BuildLinkStartStateNotAllowedMessage(CurrentSessionState);
            AppendStartFailureLog(LastErrorMessage);
            await RecordFailedResultAsync(
                scope,
                UiOperationResult.Fail(UiErrorCode.SessionStateNotAllowed, LastErrorMessage),
                cancellationToken);
            return false;
        }

        var connectResult = await TryConnectWithRetryAsync(cancellationToken, requiredConnection);
        CurrentSessionState = Runtime.SessionService.CurrentState;
        if (connectResult.Success && CurrentSessionState == SessionState.Connected)
        {
            return true;
        }

        var connectMessage = string.Equals(connectResult.Error?.Code, UiErrorCode.OperationAlreadyRunning, StringComparison.Ordinal)
            ? connectResult.Message
            : BuildConnectFailureMessage(connectResult);
        LastErrorMessage = connectMessage;
        AppendStartFailureLog(LastErrorMessage);
        await Runtime.DialogFeatureService.ReportErrorAsync(
            scope,
            UiOperationResult.Fail(
                connectResult.Error?.Code ?? UiErrorCode.ConnectFailed,
                LastErrorMessage,
                connectResult.Error?.Details),
            cancellationToken);
        return false;
    }

    private CoreConnectionInfo BuildCurrentConnectionInfo()
    {
        var effectiveAdbPath = _connectionGameSharedState.ResolveEffectiveAdbPath(updateStateWhenResolved: true);
        return _connectionGameSharedState.BuildCoreConnectionInfo(effectiveAdbPath: effectiveAdbPath);
    }

    private async Task<UiOperationResult> TryConnectWithRetryAsync(
        CancellationToken cancellationToken,
        CoreConnectionInfo? requiredConnection = null)
    {
        using var retryCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        retryCts.CancelAfter(ConnectRetryTotalBudget);
        var retryToken = retryCts.Token;

        try
        {
            var connectResult = await TryConnectWithCurrentSettingsAsync(retryToken, requiredConnection);
            if (connectResult.Success)
            {
                return connectResult;
            }

            if (string.Equals(connectResult.Error?.Code, UiErrorCode.OperationAlreadyRunning, StringComparison.Ordinal))
            {
                return connectResult;
            }

            AdbCommandFailureInfo? lastAdbFailure = null;
            var address = (_connectionGameSharedState.ConnectAddress ?? string.Empty).Trim();
            var adbExecutableResult = await ResolveAdbExecutableForRecoveryAsync(retryToken);
            if (!adbExecutableResult.Success)
            {
                return BuildDiagnosticConnectFailureResult(adbExecutableResult);
            }

            var adbExecutable = adbExecutableResult.Message;

            if (_connectionGameSharedState.RetryOnDisconnected)
            {
                AppendConnectionRecoveryAttemptLog("正在尝试启动模拟器。", "Trying to start the emulator.");
                _ = await TryStartEmulatorForReconnectAsync(retryToken);
                connectResult = await TryConnectWithCurrentSettingsAsync(retryToken, requiredConnection);
                if (connectResult.Success)
                {
                    return connectResult;
                }
            }

            if (!string.IsNullOrWhiteSpace(address)
                && AppendConnectionRecoveryAttemptLog("正在尝试通过 ADB 重新连接。", "Trying to reconnect by ADB."))
            {
                var reconnect = await TryReconnectByAdbAsync(adbExecutable, address, retryToken);
                if (reconnect.Success)
                {
                    connectResult = await TryConnectWithCurrentSettingsAsync(retryToken, requiredConnection);
                    if (connectResult.Success)
                    {
                        return connectResult;
                    }
                }

                lastAdbFailure = reconnect.Failure ?? lastAdbFailure;
            }

            if (_connectionGameSharedState.AllowAdbRestart
                && AppendConnectionRecoveryAttemptLog("正在尝试重启 ADB。", "Trying to restart ADB."))
            {
                var restart = await TryRestartAdbServerAsync(adbExecutable, retryToken);
                if (restart.Success)
                {
                    connectResult = await TryConnectWithCurrentSettingsAsync(retryToken, requiredConnection);
                    if (connectResult.Success)
                    {
                        return connectResult;
                    }
                }

                lastAdbFailure = restart.Failure ?? lastAdbFailure;
            }

            if (_connectionGameSharedState.AllowAdbHardRestart
                && AppendConnectionRecoveryAttemptLog("正在尝试强制重启 ADB。", "Trying to hard-restart ADB."))
            {
                var hardRestart = await TryHardRestartAdbServerAsync(adbExecutable, retryToken);
                if (hardRestart.Success)
                {
                    connectResult = await TryConnectWithCurrentSettingsAsync(retryToken, requiredConnection);
                }

                lastAdbFailure = hardRestart.Failure ?? lastAdbFailure;
            }

            return lastAdbFailure is null
                ? connectResult
                : BuildDiagnosticConnectFailureResult(connectResult, adbCommandFailure: lastAdbFailure);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && retryCts.IsCancellationRequested)
        {
            return UiOperationResult.Fail(
                UiErrorCode.ConnectFailed,
                $"Connection attempts timed out after {ConnectRetryTotalBudget.TotalSeconds:N0}s.");
        }
    }

    private async Task<UiOperationResult> ResolveAdbExecutableForRecoveryAsync(CancellationToken cancellationToken)
    {
        var consent = await MacBundledAdbConsentService.EnsureAcceptedAsync(
            Runtime,
            _dialogService,
            _connectionGameSharedState.UseMacBundledAdbEffective,
            "TaskQueue.Recovery.MacBundledAdbConsent",
            Texts.Language,
            cancellationToken);
        if (!consent.Success)
        {
            return consent;
        }

        var effectiveAdbPath = _connectionGameSharedState.ResolveEffectiveAdbPath(updateStateWhenResolved: true);
        if (MacBundledAdbPolicy.IsSupportedPlatform)
        {
            Runtime.LogService.Debug(MacBundledAdbPolicy.BuildResolutionContext(
                _connectionGameSharedState.AdbPath,
                effectiveAdbPath,
                _connectionGameSharedState.UseMacBundledAdbEffective));
        }

        return UiOperationResult.Ok(string.IsNullOrWhiteSpace(effectiveAdbPath) ? "adb" : effectiveAdbPath);
    }

    private async Task<UiOperationResult> TryConnectWithCurrentSettingsAsync(
        CancellationToken cancellationToken,
        CoreConnectionInfo? requiredConnection = null)
    {
        using var candidateCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        candidateCts.CancelAfter(ConnectCandidateTotalBudget);
        var candidateToken = candidateCts.Token;

        try
        {
            var consent = await MacBundledAdbConsentService.EnsureAcceptedAsync(
                Runtime,
                _dialogService,
                _connectionGameSharedState.UseMacBundledAdbEffective,
                "TaskQueue.Connect.MacBundledAdbConsent",
                Texts.Language,
                candidateToken);
            if (!consent.Success)
            {
                return consent;
            }

            if (requiredConnection is not null)
            {
                Runtime.LogService.Debug(
                    $"TaskQueue connect required target prepared: address={requiredConnection.Address}, config={requiredConnection.ConnectConfig}, adb={requiredConnection.AdbPath ?? "<null>"}");
                return await Runtime.ConnectFeatureService.ConnectAsync(requiredConnection, candidateToken);
            }

            var effectiveAdbPath = _connectionGameSharedState.ResolveEffectiveAdbPath(updateStateWhenResolved: true);
            var candidatesResult = Runtime.ConnectFeatureService.BuildConnectionCandidates(
                _connectionGameSharedState.ConnectAddress,
                _connectionGameSharedState.ConnectConfig,
                effectiveAdbPath,
                _connectionGameSharedState.BuildCoreConnectionExtras(),
                _connectionGameSharedState.AutoDetect,
                _connectionGameSharedState.AlwaysAutoDetect,
                includeConfiguredAddress: true,
                timeout: ConnectCandidateTotalBudget);
            if (!candidatesResult.Success || candidatesResult.Value is null)
            {
                return UiOperationResult.Fail(
                    candidatesResult.Error?.Code ?? UiErrorCode.ConnectFailed,
                    candidatesResult.Message,
                    candidatesResult.Error?.Details);
            }

            var candidates = candidatesResult.Value;
            Runtime.LogService.Debug(
                $"TaskQueue connect candidates prepared: count={candidates.Count}, config={_connectionGameSharedState.ConnectConfig}, adb={effectiveAdbPath ?? "<null>"}");
            if (MacBundledAdbPolicy.IsSupportedPlatform)
            {
                Runtime.LogService.Debug(MacBundledAdbPolicy.BuildResolutionContext(
                    _connectionGameSharedState.AdbPath,
                    effectiveAdbPath,
                    _connectionGameSharedState.UseMacBundledAdbEffective));
            }

            var result = await Runtime.ConnectFeatureService.ConnectCandidatesAsync(candidates, candidateToken);
            if (result.Success)
            {
                Runtime.LogService.Debug($"TaskQueue connect succeeded: {result.SuccessfulAddress}");
                if (!string.IsNullOrWhiteSpace(result.SuccessfulAddress))
                {
                    _connectionGameSharedState.ConnectAddress = result.SuccessfulAddress;
                }

                return result.Result;
            }

            if (string.Equals(result.Result.Error?.Code, UiErrorCode.OperationAlreadyRunning, StringComparison.Ordinal))
            {
                return result.Result;
            }

            var candidateFailures = result.CandidateFailures
                .Select(static failure => new ConnectionAttemptFailure(failure.Candidate, failure.Result))
                .ToList();
            return BuildDiagnosticConnectFailureResult(
                result.Result,
                candidateFailures);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && candidateCts.IsCancellationRequested)
        {
            return BuildDiagnosticConnectFailureResult(
                UiOperationResult.Fail(
                    UiErrorCode.ConnectFailed,
                    $"Connection candidates timed out after {ConnectCandidateTotalBudget.TotalSeconds:N0}s."));
        }
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
            Texts.Language);
        return UiOperationResult.Fail(
            UiErrorCode.ConnectFailed,
            diagnostic.BuildDialogMessage(),
            diagnostic.Details);
    }

    private async Task<AdbRecoveryResult> TryReconnectByAdbAsync(
        string adbExecutable,
        string address,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return AdbRecoveryResult.Failed(null);
        }

        var disconnect = await RunProcessAsync(
            "adb disconnect",
            adbExecutable,
            $"disconnect {address}",
            cancellationToken,
            logDiagnostic: Runtime.LogService.Warn);
        if (!disconnect.Success && !IsBenignAdbDisconnectFailure(disconnect))
        {
            return AdbRecoveryResult.Failed(disconnect);
        }

        var connect = await RunProcessAsync(
            "adb connect",
            adbExecutable,
            $"connect {address}",
            cancellationToken,
            logDiagnostic: Runtime.LogService.Warn);
        return connect.Success
            ? AdbRecoveryResult.Ok()
            : AdbRecoveryResult.Failed(connect);
    }

    private static bool IsBenignAdbDisconnectFailure(AdbCommandFailureInfo failure)
    {
        if (!string.Equals(failure.CommandName, "adb disconnect", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var text = string.Join(
            "\n",
            failure.StandardError,
            failure.StandardOutput,
            failure.ExceptionMessage);
        return text.Contains("no such device", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<AdbRecoveryResult> TryRestartAdbServerAsync(string adbExecutable, CancellationToken cancellationToken)
    {
        var killServer = await RunProcessAsync(
            "adb kill-server",
            adbExecutable,
            "kill-server",
            cancellationToken,
            logDiagnostic: Runtime.LogService.Warn);
        if (!killServer.Success)
        {
            return AdbRecoveryResult.Failed(killServer);
        }

        var startServer = await RunProcessAsync(
            "adb start-server",
            adbExecutable,
            "start-server",
            cancellationToken,
            logDiagnostic: Runtime.LogService.Warn);
        return startServer.Success
            ? AdbRecoveryResult.Ok()
            : AdbRecoveryResult.Failed(startServer);
    }

    private async Task<AdbRecoveryResult> TryHardRestartAdbServerAsync(string adbExecutable, CancellationToken cancellationToken)
    {
        AdbCommandFailureInfo? taskkillFailure = null;
        if (OperatingSystem.IsWindows())
        {
            var taskkill = await RunProcessAsync(
                "taskkill adb.exe",
                "taskkill",
                "/F /IM adb.exe",
                cancellationToken,
                logDiagnostic: Runtime.LogService.Warn);
            if (!taskkill.Success)
            {
                taskkillFailure = taskkill;
            }
        }

        var restart = await TryRestartAdbServerAsync(adbExecutable, cancellationToken);
        return restart.Success
            ? new AdbRecoveryResult(true, taskkillFailure)
            : AdbRecoveryResult.Failed(restart.Failure ?? taskkillFailure);
    }

    internal Task<bool> TryStartEmulatorOnStartupAsync(CancellationToken cancellationToken = default)
        => TryStartEmulatorAsync("startup", cancellationToken);

    private Task<bool> TryStartEmulatorForReconnectAsync(CancellationToken cancellationToken)
        => TryStartEmulatorAsync("reconnect", cancellationToken);

    private async Task<bool> TryStartEmulatorAsync(string source, CancellationToken cancellationToken)
    {
        var config = Runtime.ConfigurationService.CurrentConfig;
        var startEnabled = TryReadProfileBool(config, ConfigurationKeys.StartEmulator, false);
        if (!startEnabled)
        {
            return false;
        }

        var emulatorPath = TryReadProfileString(config, ConfigurationKeys.EmulatorPath, string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(emulatorPath))
        {
            Runtime.LogService.Warn("Auto start emulator skipped because emulator path is empty.");
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = emulatorPath,
                Arguments = TryReadProfileString(config, ConfigurationKeys.EmulatorAddCommand, string.Empty).Trim(),
                UseShellExecute = true,
            };

            Runtime.LogService.Debug($"Auto start emulator triggered from {source}: `{emulatorPath}`");
            _ = Process.Start(startInfo);

            var waitSeconds = Math.Clamp(
                TryReadProfileInt(config, ConfigurationKeys.EmulatorWaitSeconds, 60),
                0,
                600);
            if (waitSeconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(waitSeconds), cancellationToken);
            }

            return true;
        }
        catch (Exception ex)
        {
            Runtime.LogService.Warn($"Auto start emulator failed during {source}: {ex.Message}");
            return false;
        }
    }

    internal static Task<AdbCommandFailureInfo> RunAdbRecoveryProcessForTestAsync(
        string commandName,
        string fileName,
        string arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        return RunProcessAsync(commandName, fileName, arguments, cancellationToken, timeout);
    }

    private static async Task<AdbCommandFailureInfo> RunProcessAsync(
        string commandName,
        string fileName,
        string arguments,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null,
        Action<string>? logDiagnostic = null)
    {
        var effectiveTimeout = timeout ?? AdbRecoveryCommandTimeout;
        var resolutionDiagnosticContext = MacBundledAdbPolicy.IsSupportedPlatform
            ? MacBundledAdbPolicy.BuildResolutionContext(fileName, fileName)
            : null;
        AdbCommandFailureInfo BuildFailure(
            int? exitCode,
            string? standardError,
            string? standardOutput,
            string? exceptionMessage)
        {
            var failure = new AdbCommandFailureInfo(
                commandName,
                fileName,
                arguments,
                exitCode,
                standardError,
                standardOutput,
                exceptionMessage,
                resolutionDiagnosticContext);
            logDiagnostic?.Invoke(
                $"ADB recovery command `{commandName}` failed: file=`{fileName}`, args=`{arguments}`, timeout={effectiveTimeout.TotalSeconds:N1}s, exitCode={exitCode?.ToString() ?? "<null>"}, error={standardError ?? "<null>"}, output={standardOutput ?? "<null>"}, exception={exceptionMessage ?? "<null>"}, adbResolution={resolutionDiagnosticContext ?? "<not-macos>"}");
            return failure;
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                },
            };

            if (!process.Start())
            {
                return BuildFailure(null, null, null, $"Failed to start process `{fileName}`.");
            }

            var errorTask = process.StandardError.ReadToEndAsync();
            var outputTask = process.StandardOutput.ReadToEndAsync();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(effectiveTimeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
            {
                var killDiagnostic = TryKillProcessTree(process);
                return BuildFailure(
                    null,
                    null,
                    null,
                    $"Process timed out after {effectiveTimeout.TotalSeconds:N1}s and was killed. {killDiagnostic}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                var killDiagnostic = TryKillProcessTree(process);
                return BuildFailure(null, null, null, $"Process was canceled and killed. {killDiagnostic}");
            }

            var error = await errorTask;
            var output = await outputTask;
            if (process.ExitCode == 0)
            {
                return new AdbCommandFailureInfo(
                    commandName,
                    fileName,
                    arguments,
                    process.ExitCode,
                    error,
                    output,
                    ResolutionDiagnosticContext: resolutionDiagnosticContext);
            }

            return BuildFailure(process.ExitCode, error, output, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return BuildFailure(null, null, null, ex.Message);
        }
    }

    private static string TryKillProcessTree(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return "Process had already exited before kill.";
            }

            process.Kill(entireProcessTree: true);
            var exited = process.WaitForExit(milliseconds: 1_000);
            return exited
                ? "Kill(entireProcessTree:true) completed."
                : "Kill(entireProcessTree:true) was requested, but the process did not exit within 1.0s.";
        }
        catch (Exception ex)
        {
            return $"Kill(entireProcessTree:true) failed: {ex.Message}";
        }
    }

    private void NavigateToConnectionSettingsIfAvailable()
    {
        _navigateToSettingsSection?.Invoke("Connect");
    }

    private sealed record AdbRecoveryResult(bool Success, AdbCommandFailureInfo? Failure)
    {
        public static AdbRecoveryResult Ok() => new(true, null);

        public static AdbRecoveryResult Failed(AdbCommandFailureInfo? failure) => new(false, failure);
    }

    private string BuildSessionStateNotAllowedMessage(SessionState state, string actionZh, string actionEn)
    {
        return BuildLocalizedMessage(
            $"会话状态 `{state}` 不允许{actionZh}。",
            $"Session state `{state}` does not allow {actionEn}.");
    }

    private string BuildSessionAlreadyNonRunningMessage(SessionState state)
    {
        return BuildLocalizedMessage(
            $"会话状态 `{state}` 已处于非运行状态。",
            $"Session state `{state}` is already non-running.");
    }

    private string BuildConnectFailureMessage(UiOperationResult connectResult)
    {
        var genericHint = BuildLocalizedMessage(
            "连接失败。请“检查连接设置” -> “尝试重启模拟器与 ADB” -> “重启电脑”。",
            "Connection failed. Check connection settings -> try restarting the emulator and ADB -> reboot the computer.");
        var segments = new List<string>();
        var hasDiagnosticMessage = HasSpecificConnectFailureDiagnostic(connectResult);
        if (hasDiagnosticMessage && !string.IsNullOrWhiteSpace(connectResult.Message))
        {
            segments.Add(connectResult.Message.Trim());
        }
        else
        {
            segments.Add(genericHint);
        }

        var settingsHint = _connectionGameSharedState.BuildConnectionSettingsHintMessage();
        if (!string.IsNullOrWhiteSpace(settingsHint))
        {
            segments.Add(settingsHint);
        }

        if (!hasDiagnosticMessage
            && !string.IsNullOrWhiteSpace(connectResult.Message)
            && !string.Equals(connectResult.Message, "Connection failed.", StringComparison.OrdinalIgnoreCase))
        {
            segments.Add(BuildLocalizedMessage(
                $"连接回调：{connectResult.Message}",
                $"Connection callback: {connectResult.Message}"));
        }

        if (hasDiagnosticMessage && !segments.Contains(genericHint, StringComparer.Ordinal))
        {
            segments.Add(genericHint);
        }

        return string.Join(Environment.NewLine, segments);
    }

    private bool HasSpecificConnectFailureDiagnostic(UiOperationResult connectResult)
    {
        if (!string.Equals(connectResult.Error?.Code, UiErrorCode.ConnectFailed, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(connectResult.Message))
        {
            return false;
        }

        var localized = DialogTextCatalog.LocalizeErrorResult(Texts.Language, connectResult);
        var generic = DialogTextCatalog.LocalizeErrorResult(
            Texts.Language,
            UiOperationResult.Fail(UiErrorCode.ConnectFailed, "Connection failed."));
        return !string.Equals(localized.Message, generic.Message, StringComparison.Ordinal);
    }

    private string BuildRunOwnerBlockedMessage(string actionZh, string actionEn, string owner)
    {
        return BuildLocalizedMessage(
            $"当前运行所有者 `{owner}` 正在占用，会阻止{actionZh}。",
            $"Active run owner `{owner}` blocks {actionEn}.");
    }

    public async Task RunSelectedTaskOnceAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedTask is null)
        {
            LastErrorMessage = BuildLocalizedMessage(
                "请先选择要单次运行的任务。",
                "Select a task to run once.");
            return;
        }

        var selectedIndex = Tasks.IndexOf(SelectedTask);
        if (selectedIndex < 0)
        {
            LastErrorMessage = BuildLocalizedMessage(
                "当前选中的任务不存在。",
                "The selected task no longer exists.");
            return;
        }

        await _runTransitionLock.WaitAsync(cancellationToken);
        try
        {
            ClearVisibleRuntimeLogs();
            if (!Runtime.SessionService.TryBeginRun(TaskQueueRunOwner, out var currentOwner))
            {
                var owner = string.IsNullOrWhiteSpace(currentOwner) ? "Unknown" : currentOwner;
                var message = BuildRunOwnerBlockedMessage("单次运行", "run-once", owner);
                LastErrorMessage = message;
                AppendStartFailureLog(LastErrorMessage);
                await RecordFailedResultAsync(
                    "TaskQueue.RunOnce.RunOwner",
                    UiOperationResult.Fail(UiErrorCode.TaskQueueEditBlocked, message),
                    cancellationToken);
                return;
            }

            var keepRunOwner = false;
            try
            {
                if (!await EnsureCoreReadyAsync("TaskQueue.RunOnce.CoreWarmup", cancellationToken))
                {
                    return;
                }

                if (!await EnsureConnectedForLinkStartAsync("TaskQueue.RunOnce", cancellationToken))
                {
                    return;
                }

                await WaitForPendingBindingAsync(cancellationToken);
                if (!await SaveBoundTaskModulesAsync(cancellationToken))
                {
                    return;
                }

                _clearTaskStatusesWhenStopped = false;

                var validationResult = await Runtime.TaskQueueFeatureService.ValidateTaskAsync(selectedIndex, cancellationToken);
                if (!validationResult.Success || validationResult.Value is null)
                {
                    LastErrorMessage = validationResult.Message;
                    await SelectTaskByIndexAsync(selectedIndex, cancellationToken);
                    AppendStartFailureLog(LastErrorMessage);
                    await RecordFailedResultAsync(
                        "TaskQueue.RunOnce.ValidateTask",
                        UiOperationResult.Fail(
                            validationResult.Error?.Code ?? UiErrorCode.TaskValidationFailed,
                            validationResult.Message,
                            validationResult.Error?.Details),
                        cancellationToken);
                    return;
                }

                UpdateSelectedTaskValidationSummary(validationResult.Value);
                if (validationResult.Value.HasBlockingIssues)
                {
                    var firstBlocking = validationResult.Value.Issues.First(static issue => issue.Blocking);
                    var issueDetail = $"{firstBlocking.Code}:{firstBlocking.Field}:{firstBlocking.Message}";
                    LastErrorMessage = string.Format(
                        Texts.GetOrDefault(
                            "TaskQueue.Error.BlockingValidation",
                            "Task `{0}` blocked by validation: {1}"),
                        validationResult.Value.TaskName,
                        issueDetail);
                    await SelectTaskByIndexAsync(selectedIndex, cancellationToken);
                    AppendStartFailureLog(LastErrorMessage);
                    await RecordFailedResultAsync(
                        "TaskQueue.RunOnce.ValidateTask",
                        UiOperationResult.Fail(UiErrorCode.TaskValidationFailed, LastErrorMessage, issueDetail),
                        cancellationToken);
                    return;
                }

                RefreshConfigValidationState(Runtime.ConfigurationService.RevalidateCurrentConfig());
                if (HasBlockingConfigIssues)
                {
                    var first = Runtime.ConfigurationService.CurrentValidationIssues.FirstOrDefault(i => i.Blocking);
                    LastErrorMessage = first is null
                        ? "Config validation has blocking issues."
                        : $"{first.Scope}:{first.Code}:{first.Field}:{first.Message}";
                    await NavigateToFirstBlockingIssueAsync(first, cancellationToken);
                    AppendStartFailureLog(LastErrorMessage);
                    await RecordConfigValidationFailureAsync(first, cancellationToken);
                    return;
                }

                if (!Runtime.ConfigurationService.TryGetCurrentProfile(out var profile))
                {
                    LastErrorMessage = Texts.GetOrDefault("TaskQueue.Error.ProfileMissingShort", "Current profile is missing.");
                    await RecordFailedResultAsync(
                        "TaskQueue.RunOnce.Profile",
                        UiOperationResult.Fail(UiErrorCode.ProfileMissing, LastErrorMessage),
                        cancellationToken);
                    return;
                }

                if (selectedIndex >= profile.TaskQueue.Count)
                {
                    LastErrorMessage = BuildLocalizedMessage(
                        "当前选中的任务不存在。",
                        "The selected task no longer exists.");
                    await RecordFailedResultAsync(
                        "TaskQueue.RunOnce.Task",
                        UiOperationResult.Fail(UiErrorCode.TaskNotFound, LastErrorMessage),
                        cancellationToken);
                    return;
                }

                CoreResult<int> appendResult;
                var enabledSnapshot = profile.TaskQueue.Select(task => task.IsEnabled).ToArray();
                try
                {
                    for (var index = 0; index < profile.TaskQueue.Count; index++)
                    {
                        profile.TaskQueue[index].IsEnabled = index == selectedIndex;
                    }

                    appendResult = await Runtime.TaskQueueFeatureService.QueueEnabledTasksAsync(cancellationToken);
                }
                finally
                {
                    for (var index = 0; index < profile.TaskQueue.Count && index < enabledSnapshot.Length; index++)
                    {
                        profile.TaskQueue[index].IsEnabled = enabledSnapshot[index];
                    }
                }

                if (!appendResult.Success)
                {
                    var error = UiOperationResult<int>.FromCore(appendResult, "Task queued.");
                    _ = await ApplyResultAsync(error, "TaskQueue.RunOnce.Append", cancellationToken);
                    return;
                }

                if (appendResult.Value <= 0)
                {
                    LastErrorMessage = BuildLocalizedMessage(
                        "当前任务本次没有产生可执行的核心任务。",
                        "The selected task did not produce any runnable core task this time.");
                    AppendStartFailureLog(LastErrorMessage);
                    await RecordFailedResultAsync(
                        "TaskQueue.RunOnce.AppendEmpty",
                        UiOperationResult.Fail(UiErrorCode.TaskValidationFailed, LastErrorMessage),
                        cancellationToken);
                    return;
                }

                if (!await ApplyResultAsync(
                        await Runtime.ConnectFeatureService.StartAsync(cancellationToken),
                        "TaskQueue.RunOnce.Start",
                        cancellationToken))
                {
                    return;
                }

                ResetRuntimeLogState();
                ResetAllTaskStatuses();
                CurrentSessionState = Runtime.SessionService.CurrentState;
                _currentRunId = Guid.NewGuid().ToString("N");
                _lastPostActionRunId = string.Empty;
                TrackAchievementsAfterStart(appendResult.Value);
                keepRunOwner = true;
            }
            finally
            {
                if (!keepRunOwner)
                {
                    Runtime.SessionService.EndRun(TaskQueueRunOwner);
                }
            }
        }
        finally
        {
            _runTransitionLock.Release();
        }
    }

    private async Task RunAccountSwitchManualAsync(CancellationToken cancellationToken)
    {
        await _runTransitionLock.WaitAsync(cancellationToken);
        try
        {
            CurrentSessionState = Runtime.SessionService.CurrentState;
            if (CurrentSessionState != SessionState.Connected)
            {
                LastErrorMessage = BuildSessionStateNotAllowedMessage(CurrentSessionState, "账号切换", "account switch");
                await RecordFailedResultAsync(
                    "TaskQueue.AccountSwitch",
                    UiOperationResult.Fail(UiErrorCode.SessionStateNotAllowed, LastErrorMessage),
                    cancellationToken);
                return;
            }

            if (!Runtime.SessionService.TryBeginRun(TaskQueueRunOwner, out var currentOwner))
            {
                var owner = string.IsNullOrWhiteSpace(currentOwner) ? "Unknown" : currentOwner;
                var message = BuildRunOwnerBlockedMessage("账号切换", "account switch", owner);
                LastErrorMessage = message;
                await RecordFailedResultAsync(
                    "TaskQueue.AccountSwitch.RunOwner",
                    UiOperationResult.Fail(UiErrorCode.TaskQueueEditBlocked, message),
                    cancellationToken);
                return;
            }

            var keepRunOwner = false;
            try
            {
                await WaitForPendingBindingAsync(cancellationToken);
                if (!await SaveBoundTaskModulesAsync(cancellationToken))
                {
                    return;
                }

                if (!Runtime.ConfigurationService.TryGetCurrentProfile(out var profile))
                {
                    LastErrorMessage = Texts.GetOrDefault("TaskQueue.Error.ProfileMissingShort", "Current profile is missing.");
                    await RecordFailedResultAsync(
                        "TaskQueue.AccountSwitch.Profile",
                        UiOperationResult.Fail(UiErrorCode.ProfileMissing, LastErrorMessage),
                        cancellationToken);
                    return;
                }

                var startupIndex = ResolveAccountSwitchStartUpIndex(profile);
                if (startupIndex < 0 || startupIndex >= profile.TaskQueue.Count)
                {
                    LastErrorMessage = BuildLocalizedMessage(
                        "账号切换所需的 StartUp 任务不存在。",
                        "StartUp task is missing for account switch.");
                    await RecordFailedResultAsync(
                        "TaskQueue.AccountSwitch.Task",
                        UiOperationResult.Fail(UiErrorCode.TaskNotFound, LastErrorMessage),
                        cancellationToken);
                    return;
                }

                var enabledSnapshot = profile.TaskQueue.Select(task => task.IsEnabled).ToArray();
                try
                {
                    for (var index = 0; index < profile.TaskQueue.Count; index++)
                    {
                        profile.TaskQueue[index].IsEnabled = index == startupIndex;
                    }

                    var appendResult = await Runtime.TaskQueueFeatureService.QueueEnabledTasksAsync(cancellationToken);
                    if (!appendResult.Success)
                    {
                        var error = UiOperationResult<int>.FromCore(appendResult, "Task queued.");
                        _ = await ApplyResultAsync(error, "TaskQueue.AccountSwitch.Append", cancellationToken);
                        return;
                    }
                }
                finally
                {
                    for (var index = 0; index < profile.TaskQueue.Count && index < enabledSnapshot.Length; index++)
                    {
                        profile.TaskQueue[index].IsEnabled = enabledSnapshot[index];
                    }
                }

                if (!await ApplyResultAsync(
                        await Runtime.ConnectFeatureService.StartAsync(cancellationToken),
                        "TaskQueue.AccountSwitch.Start",
                        cancellationToken))
                {
                    return;
                }

                CurrentSessionState = Runtime.SessionService.CurrentState;
                _currentRunId = Guid.NewGuid().ToString("N");
                _lastPostActionRunId = string.Empty;
                keepRunOwner = true;
            }
            finally
            {
                if (!keepRunOwner)
                {
                    Runtime.SessionService.EndRun(TaskQueueRunOwner);
                }
            }
        }
        finally
        {
            _runTransitionLock.Release();
        }
    }

    private int ResolveAccountSwitchStartUpIndex(UnifiedProfile profile)
    {
        if (SelectedTask is not null)
        {
            var selectedIndex = Tasks.IndexOf(SelectedTask);
            if (selectedIndex >= 0
                && selectedIndex < profile.TaskQueue.Count
                && string.Equals(
                    TaskModuleTypes.Normalize(profile.TaskQueue[selectedIndex].Type),
                    TaskModuleTypes.StartUp,
                    StringComparison.OrdinalIgnoreCase))
            {
                return selectedIndex;
            }
        }

        for (var index = 0; index < profile.TaskQueue.Count; index++)
        {
            if (string.Equals(
                    TaskModuleTypes.Normalize(profile.TaskQueue[index].Type),
                    TaskModuleTypes.StartUp,
                    StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default, bool userInitiated = true)
    {
        await _runTransitionLock.WaitAsync(cancellationToken);
        try
        {
            CurrentSessionState = Runtime.SessionService.CurrentState;
            if (CurrentSessionState != SessionState.Running)
            {
                LastErrorMessage = BuildSessionAlreadyNonRunningMessage(CurrentSessionState);
                await RecordFailedResultAsync(
                    "TaskQueue.Stop",
                    UiOperationResult.Fail(UiErrorCode.OperationAlreadyStopped, LastErrorMessage),
                    cancellationToken);
                SyncStoppedUiStateIfSessionNotActive();

                return;
            }

            var currentOwner = Runtime.SessionService.CurrentRunOwner;
            if (!string.IsNullOrWhiteSpace(currentOwner) && !Runtime.SessionService.IsRunOwner(TaskQueueRunOwner))
            {
                var owner = currentOwner;
                LastErrorMessage = BuildRunOwnerBlockedMessage("停止任务", "task stop", owner);
                await RecordFailedResultAsync(
                    "TaskQueue.Stop.RunOwner",
                    UiOperationResult.Fail(UiErrorCode.TaskQueueEditBlocked, LastErrorMessage),
                    cancellationToken);
                return;
            }

            await WaitForPendingBindingAsync(cancellationToken);
            if (!await SaveBoundTaskModulesAsync(cancellationToken))
            {
                return;
            }

            if (ShouldDelayStopUntilRoguelikeCombatComplete())
            {
                await WaitUntilRoguelikeCombatCompleteAsync(cancellationToken);
                CurrentSessionState = Runtime.SessionService.CurrentState;
                if (CurrentSessionState != SessionState.Running)
                {
                    SyncStoppedUiStateIfSessionNotActive();
                    return;
                }
            }

            _clearTaskStatusesWhenStopped = true;
            if (userInitiated)
            {
                _ = Runtime.AchievementTrackerService.Unlock("TacticalRetreat");
            }

            if (!await ApplyResultAsync(await Runtime.ConnectFeatureService.StopAsync(cancellationToken), "TaskQueue.Stop", cancellationToken))
            {
                CurrentSessionState = Runtime.SessionService.CurrentState;
                if (CurrentSessionState is SessionState.Running or SessionState.Stopping)
                {
                    _clearTaskStatusesWhenStopped = false;
                }
                SyncStoppedUiStateIfSessionNotActive();

                return;
            }

            ResetAllTaskStatuses();
            CurrentSessionState = Runtime.SessionService.CurrentState;
            SyncStoppedUiStateIfSessionNotActive();
        }
        finally
        {
            _runTransitionLock.Release();
        }
    }

    public async Task WaitAndStopAsync(CancellationToken cancellationToken = default)
    {
        await _runTransitionLock.WaitAsync(cancellationToken);
        try
        {
            if (IsWaitingForStop)
            {
                LastErrorMessage = BuildLocalizedMessage(
                    "等待并停止流程已在执行中。",
                    "WaitAndStop is already in progress.");
                await RecordFailedResultAsync(
                    "TaskQueue.WaitAndStop",
                    UiOperationResult.Fail(UiErrorCode.OperationAlreadyRunning, LastErrorMessage),
                    cancellationToken);
                return;
            }

            CurrentSessionState = Runtime.SessionService.CurrentState;
            if (CurrentSessionState != SessionState.Running)
            {
                LastErrorMessage = BuildSessionAlreadyNonRunningMessage(CurrentSessionState);
                await RecordFailedResultAsync(
                    "TaskQueue.WaitAndStop",
                    UiOperationResult.Fail(UiErrorCode.OperationAlreadyStopped, LastErrorMessage),
                    cancellationToken);
                SyncStoppedUiStateIfSessionNotActive();

                return;
            }

            var currentOwner = Runtime.SessionService.CurrentRunOwner;
            if (!string.IsNullOrWhiteSpace(currentOwner) && !Runtime.SessionService.IsRunOwner(TaskQueueRunOwner))
            {
                var owner = currentOwner;
                LastErrorMessage = BuildRunOwnerBlockedMessage("等待并停止", "wait-stop", owner);
                await RecordFailedResultAsync(
                    "TaskQueue.WaitAndStop.RunOwner",
                    UiOperationResult.Fail(UiErrorCode.TaskQueueEditBlocked, LastErrorMessage),
                    cancellationToken);
                return;
            }

            IsWaitingForStop = true;
            _clearTaskStatusesWhenStopped = true;
            if (!await ApplyResultAsync(
                    await Runtime.ConnectFeatureService.WaitAndStopAsync(TimeSpan.FromSeconds(15), cancellationToken),
                    "TaskQueue.WaitAndStop",
                    cancellationToken))
            {
                CurrentSessionState = Runtime.SessionService.CurrentState;
                if (CurrentSessionState is SessionState.Running or SessionState.Stopping)
                {
                    _clearTaskStatusesWhenStopped = false;
                }
                SyncStoppedUiStateIfSessionNotActive();

                return;
            }

            ResetAllTaskStatuses();
            CurrentSessionState = Runtime.SessionService.CurrentState;
            SyncStoppedUiStateIfSessionNotActive();
        }
        finally
        {
            IsWaitingForStop = false;
            _runTransitionLock.Release();
        }
    }

    public async Task ReloadOverlayTargetsAsync(CancellationToken cancellationToken = default)
    {
        var targets = await ApplyResultAsync(
            await Runtime.OverlayFeatureService.GetOverlayTargetsAsync(cancellationToken),
            "Overlay.QueryTargets",
            cancellationToken);

        if (targets is null)
        {
            return;
        }

        OverlayTargets.Clear();
        foreach (var target in targets)
        {
            OverlayTargets.Add(target);
        }

        if (OverlayTargets.Count > 0)
        {
            var resolvedSelection = OverlayTargetPersistence.ResolveSelection(
                OverlayTargets,
                Runtime.ConfigurationService.CurrentConfig.GlobalValues,
                _overlaySharedState.SelectedTargetId);
            if (OverlayTargetPersistence.ShouldDefaultToPreview(
                Runtime.ConfigurationService.CurrentConfig.GlobalValues,
                _overlaySharedState.SelectedTargetId))
            {
                resolvedSelection = OverlayTargets.FirstOrDefault(t => string.Equals(t.Id, "preview", StringComparison.Ordinal))
                    ?? resolvedSelection;
            }

            SelectedOverlayTarget = resolvedSelection
                ?? OverlayTargets.FirstOrDefault(t => t.IsPrimary)
                ?? OverlayTargets[0];
        }

        if (!string.IsNullOrWhiteSpace(_overlaySharedState.StatusMessage))
        {
            OverlayStatusText = _overlaySharedState.StatusMessage;
        }
        else
        {
            var snapshotResult = await Runtime.PlatformCapabilityService.GetSnapshotAsync(cancellationToken);
            if (snapshotResult.Success && snapshotResult.Value is not null)
            {
                OverlayStatusText = BuildCapabilityLine(PlatformCapabilityId.Overlay, snapshotResult.Value.Overlay);
            }
            else
            {
                OverlayStatusText = PlatformCapabilityTextMap.FormatSnapshotUnavailable(
                    Texts.Language,
                    snapshotResult.Message,
                    _localizationFallbackReporter);
            }
        }

        SyncOverlayPresentationFromSharedState();
    }

    public async Task PickOverlayTargetWithDialogAsync(CancellationToken cancellationToken = default)
    {
        if (OverlayTargets.Count == 0)
        {
            await ReloadOverlayTargetsAsync(cancellationToken);
        }

        if (OverlayTargets.Count == 0)
        {
            LastErrorMessage = BuildLocalizedMessage("当前没有可用的 Overlay 目标。", "No overlay target is available.");
            return;
        }

        var chrome = CreateTaskQueueDialogChrome(
            texts => new DialogChromeSnapshot(
                title: texts.GetOrDefault("TaskQueue.Root.OverlayTargetPickerTitle", "Overlay Target Picker"),
                confirmText: texts.GetOrDefault("TaskQueue.Root.OverlayTargetPickerConfirm", "Select"),
                cancelText: texts.GetOrDefault("TaskQueue.Root.OverlayTargetPickerCancel", "Cancel"),
                namedTexts: DialogTextCatalog.CreateNamedTexts(
                    (DialogTextCatalog.ChromeKeys.RefreshButton, texts.GetOrDefault("TaskQueue.Root.OverlayTargetPickerRefresh", "Refresh")),
                    (DialogTextCatalog.ChromeKeys.RefreshingButton, texts.GetOrDefault("TaskQueue.Root.OverlayTargetPickerRefresh", "Refresh")),
                    (DialogTextCatalog.ChromeKeys.EmptyStateTitle, texts.GetOrDefault("TaskQueue.Root.OverlayTargetPickerEmptyTitle", "No running process found")),
                    (DialogTextCatalog.ChromeKeys.EmptyStateBody, texts.GetOrDefault("TaskQueue.Root.OverlayTargetPickerEmptyBody", "Refresh to scan again, or start the target app first.")))));
        var chromeSnapshot = chrome.GetSnapshot();
        var pickerItems = BuildOverlayPickerItems(OverlayTargets);
        var selectedPickerId = ResolveOverlayPickerSelectedId(SelectedOverlayTarget);
        var request = new ProcessPickerDialogRequest(
            Title: chromeSnapshot.Title,
            Items: pickerItems,
            SelectedId: selectedPickerId,
            ConfirmText: chromeSnapshot.ConfirmText ?? RootTexts.GetOrDefault("TaskQueue.Root.OverlayTargetPickerConfirm", "Select"),
            CancelText: chromeSnapshot.CancelText ?? RootTexts.GetOrDefault("TaskQueue.Root.OverlayTargetPickerCancel", "Cancel"),
            RefreshItemsAsync: RefreshOverlayPickerItemsAsync,
            Chrome: chrome);
        var dialogResult = await _dialogService.ShowProcessPickerAsync(request, "TaskQueue.Overlay.PickTarget", cancellationToken);
        if (dialogResult.Return != DialogReturnSemantic.Confirm || dialogResult.Payload is null)
        {
            StatusMessage = dialogResult.Return == DialogReturnSemantic.Cancel
                ? RootTexts.GetOrDefault("TaskQueue.Root.OverlayTargetPickerCancelStatus", "Overlay target selection cancelled.")
                : RootTexts.GetOrDefault("TaskQueue.Root.OverlayTargetPickerClosedStatus", "Overlay target picker closed.");
            return;
        }

        await ApplyOverlayTargetAsync(dialogResult.Payload.SelectedId, cancellationToken);
    }

    private async Task<IReadOnlyList<ProcessPickerItem>> RefreshOverlayPickerItemsAsync(CancellationToken cancellationToken)
    {
        await ReloadOverlayTargetsAsync(cancellationToken);
        return BuildOverlayPickerItems(OverlayTargets);
    }

    private static IReadOnlyList<ProcessPickerItem> BuildOverlayPickerItems(IEnumerable<OverlayTarget> targets)
    {
        return targets
            .Where(target => !IsPreviewOverlayTarget(target))
            .Select(target => new ProcessPickerItem(target.Id, target.DisplayName, target.IsPrimary))
            .ToArray();
    }

    private static string? ResolveOverlayPickerSelectedId(OverlayTarget? target)
    {
        return target is null || IsPreviewOverlayTarget(target)
            ? null
            : target.Id;
    }

    private static bool IsPreviewOverlayTarget(OverlayTarget target)
    {
        return string.Equals(target.Id, "preview", StringComparison.OrdinalIgnoreCase);
    }

    public async Task ApplyOverlayTargetAsync(string targetId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetId))
        {
            LastErrorMessage = BuildLocalizedMessage("Overlay 目标 ID 为空。", "Overlay target id is missing.");
            return;
        }

        if (!await SelectAndPersistOverlayTargetAsync(targetId, cancellationToken))
        {
            return;
        }
    }

    public async Task ToggleOverlayAsync(CancellationToken cancellationToken = default)
    {
        if (!await SelectAndPersistOverlayTargetAsync(SelectedOverlayTarget?.Id ?? "preview", cancellationToken))
        {
            return;
        }

        var requestedVisible = !OverlayVisible;
        var visibleResult = await Runtime.OverlayFeatureService.ToggleOverlayVisibilityAsync(requestedVisible, cancellationToken);
        if (!await ApplyResultAsync(visibleResult, "Overlay.Toggle", cancellationToken))
        {
            return;
        }

        OverlayVisible = requestedVisible;
    }

    private string BuildCapabilityLine(PlatformCapabilityId capability, PlatformCapabilityStatus status)
    {
        return PlatformCapabilityTextMap.FormatCapabilityLine(
            Texts.Language,
            capability,
            status,
            _localizationFallbackReporter);
    }

    private void RefreshConfigValidationState(IReadOnlyList<ConfigValidationIssue> issues)
    {
        BlockingConfigIssueCount = issues.Count(i => i.Blocking);
        HasBlockingConfigIssues = BlockingConfigIssueCount > 0;
    }

    private void AppendStartFailureLog(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        AppendLogEntry(
            timestamp: DateTimeOffset.Now,
            content: $"Link Start failed: {message}",
            level: "ERROR",
            splitMode: TaskQueueLogSplitMode.Before,
            updateThumbnail: false);
    }

    private bool AppendConnectionRecoveryAttemptLog(string zh, string en)
    {
        AppendSystemLog(
            BuildLocalizedMessage(
                $"连接失败，{zh}",
                $"Connection failed. {en}"),
            "WARN");
        return true;
    }

    private string BuildLocalizedMessage(string zh, string en)
    {
        return DialogTextCatalog.Select(Texts.Language, zh, en);
    }

    private void ResetSelectedTaskValidationSummary()
    {
        SelectedTaskValidationIssueCount = 0;
        SelectedTaskHasBlockingValidationIssues = false;
        SelectedTaskValidationSummary = string.Empty;
    }

    private void ProjectSelectedTaskValidationSummary()
    {
        if (SelectedTaskPanel is null)
        {
            ResetSelectedTaskValidationSummary();
            return;
        }

        SelectedTaskValidationIssueCount = SelectedTaskPanel.ValidationIssueCount;
        SelectedTaskHasBlockingValidationIssues = SelectedTaskPanel.HasBlockingValidationIssues;
        SelectedTaskValidationSummary = SelectedTaskPanel.ValidationSummary;
    }

    private async Task RefreshSelectedTaskValidationSummaryAsync(int index, CancellationToken cancellationToken)
    {
        var panel = TaskPanels.FirstOrDefault(candidate => candidate.TaskIndex == index);
        var result = await Runtime.TaskQueueFeatureService.ValidateTaskAsync(index, cancellationToken);
        if (!result.Success || result.Value is null)
        {
            if (panel is not null)
            {
                panel.ApplyValidationLoadFailure(Texts, result.Message);
            }

            LastErrorMessage = result.Message;
            ProjectSelectedTaskValidationSummary();
            return;
        }

        if (panel is not null)
        {
            panel.ApplyValidationReport(result.Value, Texts);
        }

        UpdateSelectedTaskValidationSummary(result.Value);
    }

    private void UpdateSelectedTaskValidationSummary(TaskValidationReport report)
    {
        var blockingCount = report.Issues.Count(i => i.Blocking);
        SelectedTaskPanel?.ApplyValidationReport(report, Texts);
        SelectedTaskValidationIssueCount = blockingCount;
        SelectedTaskHasBlockingValidationIssues = blockingCount > 0;

        if (blockingCount == 0)
        {
            SelectedTaskValidationSummary = string.Empty;
            return;
        }

        SelectedTaskValidationSummary = string.Format(
            Texts.GetOrDefault("TaskQueue.Validation.BlockingCount", "{0} blocking issue(s)."),
            blockingCount);
    }

    private void RefreshSelectedTaskValidationSummaryLocalization()
    {
        foreach (var panel in TaskPanels)
        {
            panel.RefreshValidationSummaryLocalization(Texts);
        }

        if (SelectedTaskValidationIssueCount > 0)
        {
            SelectedTaskValidationSummary = string.Format(
                Texts.GetOrDefault("TaskQueue.Validation.BlockingCount", "{0} blocking issue(s)."),
                SelectedTaskValidationIssueCount);
            return;
        }

        if (!string.IsNullOrWhiteSpace(SelectedTaskValidationSummary))
        {
            SelectedTaskValidationSummary = Texts.GetOrDefault(
                "TaskQueue.Validation.LoadFailed",
                "Failed to load validation report.");
        }
    }

    private async Task<bool> ValidateEnabledTasksBeforeStartAsync(CancellationToken cancellationToken)
    {
        for (var index = 0; index < Tasks.Count; index++)
        {
            if (!Tasks[index].IsEnabled)
            {
                continue;
            }

            var result = await Runtime.TaskQueueFeatureService.ValidateTaskAsync(index, cancellationToken);
            if (!result.Success || result.Value is null)
            {
                LastErrorMessage = result.Message;
                await SelectTaskByIndexAsync(index, cancellationToken);
                AppendStartFailureLog(LastErrorMessage);
                await RecordFailedResultAsync(
                    "TaskQueue.ValidateTask",
                    UiOperationResult.Fail(
                        result.Error?.Code ?? UiErrorCode.TaskValidationFailed,
                        result.Message,
                        result.Error?.Details),
                    cancellationToken);
                return false;
            }

            var report = result.Value;
            if (SelectedTask is not null && Tasks.IndexOf(SelectedTask) == index)
            {
                UpdateSelectedTaskValidationSummary(report);
            }

            if (!report.HasBlockingIssues)
            {
                continue;
            }

            var firstBlocking = report.Issues.First(i => i.Blocking);
            var issueDetail = $"{firstBlocking.Code}:{firstBlocking.Field}:{firstBlocking.Message}";
            LastErrorMessage = string.Format(
                Texts.GetOrDefault(
                    "TaskQueue.Error.BlockingValidation",
                    "Task `{0}` blocked by validation: {1}"),
                report.TaskName,
                issueDetail);
            await SelectTaskByIndexAsync(index, cancellationToken);
            AppendStartFailureLog(LastErrorMessage);
            await RecordFailedResultAsync(
                "TaskQueue.ValidateTask",
                UiOperationResult.Fail(
                    UiErrorCode.TaskValidationFailed,
                    LastErrorMessage,
                    issueDetail),
                cancellationToken);
            return false;
        }

        return true;
    }

    private async Task NavigateToFirstBlockingIssueAsync(
        ConfigValidationIssue? issue,
        CancellationToken cancellationToken)
    {
        if (issue?.TaskIndex is int taskIndex)
        {
            await SelectTaskByIndexAsync(taskIndex, cancellationToken);
            return;
        }

        if (!string.IsNullOrWhiteSpace(issue?.TaskName))
        {
            for (var index = 0; index < Tasks.Count; index++)
            {
                if (string.Equals(Tasks[index].Name, issue.TaskName, StringComparison.OrdinalIgnoreCase))
                {
                    await SelectTaskByIndexAsync(index, cancellationToken);
                    return;
                }
            }
        }

        for (var index = 0; index < Tasks.Count; index++)
        {
            if (!Tasks[index].IsEnabled)
            {
                continue;
            }

            var result = await Runtime.TaskQueueFeatureService.ValidateTaskAsync(index, cancellationToken);
            if (result.Success && result.Value?.HasBlockingIssues == true)
            {
                await SelectTaskByIndexAsync(index, cancellationToken);
                return;
            }
        }
    }

    private async Task BindSelectedTaskAsync(CancellationToken cancellationToken = default)
    {
        IsSelectedTaskBindingPending = true;
        try
        {
            if (SelectedTaskPanel is not null)
            {
                await RefreshTaskPanelValidationSummaryAsync(SelectedTaskPanel, cancellationToken);
            }
            else
            {
                ResetSelectedTaskValidationSummary();
            }
        }
        finally
        {
            IsSelectedTaskBindingPending = false;
        }
    }

    private void ClearTaskModuleBindings()
    {
        ClearTaskPanels();
        _fallbackStartUpModule.ClearBinding();
        _fallbackFightModule.ClearBinding();
        _fallbackRecruitModule.ClearBinding();
        _fallbackInfrastModule.ClearBinding();
        _fallbackMallModule.ClearBinding();
        _fallbackAwardModule.ClearBinding();
        _fallbackRoguelikeModule.ClearBinding();
        _fallbackReclamationModule.ClearBinding();
        _fallbackUserDataUpdateModule.ClearBinding();
        _fallbackCustomModule.ClearBinding();
        ResetSelectedTaskValidationSummary();
    }

    private void ClearTaskPanels()
    {
        foreach (var panel in TaskPanels)
        {
            if (panel.ModuleViewModel is INotifyPropertyChanged typedModule
                && panel.ModuleViewModel is not TaskModuleSettingsViewModelBase)
            {
                typedModule.PropertyChanged -= OnTypedModulePropertyChanged;
            }

            panel.Module.ClearBinding();
        }

        TaskPanels.Clear();
        SelectedTaskPanel = null;
    }

    private void ResetBindingsForStructuralQueueMutation()
    {
        CancelTypedModuleAutoSave();
        CancelPendingBinding();
        ClearTaskModuleBindings();
    }

    private async Task<bool> SaveBoundTaskModulesAsync(CancellationToken cancellationToken = default)
    {
        var succeeded = true;
        var lastErrorMessage = string.Empty;

        foreach (var panel in TaskPanels)
        {
            if (await panel.Module.FlushPendingChangesAsync(cancellationToken))
            {
                continue;
            }

            succeeded = false;
            lastErrorMessage = panel.LastErrorMessage;
        }

        if (!await PostActionModule.FlushPendingChangesAsync(cancellationToken))
        {
            succeeded = false;
            lastErrorMessage = PostActionModule.LastErrorMessage;
        }

        if (succeeded)
        {
            var flushResult = await Runtime.TaskQueueFeatureService.FlushTaskParamWritesAsync(cancellationToken);
            if (!flushResult.Success)
            {
                LastErrorMessage = flushResult.Message;
                await RecordFailedResultAsync("TaskQueue.FlushParams", flushResult, cancellationToken);
                return false;
            }

            return true;
        }

        LastErrorMessage = lastErrorMessage;
        return false;
    }

    public async Task<bool> FlushConfigurationSavesForCloseAsync(CancellationToken cancellationToken = default)
    {
        CancelTypedModuleAutoSave();

        await _queueMutationLock.WaitAsync(cancellationToken);
        try
        {
            await WaitForPendingBindingAsync(cancellationToken);
            if (CanEdit)
            {
                _suppressModuleAutoSave = true;
                if (!await SaveTypedModulesTrackedAsync(cancellationToken))
                {
                    return false;
                }

                var failedNames = await ConfigurationSaveTracker.Instance.RetryPendingOrFailedAsync(
                    static key => key.StartsWith("TaskQueue.", StringComparison.Ordinal),
                    cancellationToken,
                    Runtime.DiagnosticsService);
                return failedNames.Count == 0;
            }

            ConfigurationSaveTracker.Instance.ClearPending("TaskQueue.TypedModules");
            ConfigurationSaveTracker.Instance.ClearFailure("TaskQueue.TypedModules");
            return true;
        }
        finally
        {
            _suppressModuleAutoSave = false;
            _queueMutationLock.Release();
        }
    }

    private Task<bool> SaveTypedModulesTrackedAsync(CancellationToken cancellationToken = default)
    {
        return ConfigurationSaveTracker.Instance.RunTrackedAsync(
            "TaskQueue.TypedModules",
            ResolveTypedModulesSaveDisplayName(),
            "TaskQueue.TypedModules.Flush",
            Runtime.DiagnosticsService,
            SaveBoundTaskModulesAsync,
            cancellationToken);
    }

    private static string ResolveTypedModulesSaveDisplayName()
    {
        return "一键长草设置";
    }

    private void RegisterTaskQueueSavePending()
    {
        ConfigurationSaveTracker.Instance.MarkPending(
            TaskQueueSaveKey,
            ResolveTaskQueueSaveDisplayName(),
            "TaskQueue.Save",
            Runtime.DiagnosticsService,
            SaveTaskQueueCoreAsync);
    }

    private Task<bool> SaveTaskQueueTrackedAsync(
        CancellationToken cancellationToken = default,
        string scope = "TaskQueue.Save")
    {
        return ConfigurationSaveTracker.Instance.RunTrackedAsync(
            TaskQueueSaveKey,
            ResolveTaskQueueSaveDisplayName(),
            scope,
            Runtime.DiagnosticsService,
            SaveTaskQueueCoreAsync,
            cancellationToken);
    }

    private async Task<bool> SaveTaskQueueCoreAsync(CancellationToken cancellationToken)
    {
        var result = await Runtime.TaskQueueFeatureService.SaveAsync(cancellationToken);
        if (!result.Success)
        {
            await RecordFailedResultAsync("TaskQueue.Save", result, cancellationToken);
            return false;
        }

        return true;
    }

    private static string ResolveTaskQueueSaveDisplayName()
    {
        return "一键长草";
    }

    private void UpdateDownloadLog(DateTimeOffset timestamp, string level, string message)
    {
        const string updatePrefix = "[update]";
        var isExplicitUpdateLog = message.StartsWith(updatePrefix, StringComparison.OrdinalIgnoreCase);
        if (isExplicitUpdateLog
            || message.Contains("download", StringComparison.OrdinalIgnoreCase)
            || message.Contains("下载", StringComparison.Ordinal))
        {
            var content = isExplicitUpdateLog
                ? message[updatePrefix.Length..].TrimStart()
                : message;
            DownloadLogEntry = new TaskQueueLogEntryViewModel(
                FormatLogTimestamp(timestamp),
                content,
                NormalizeLogLevel(level));
        }
    }

    private void ClearVisibleRuntimeLogs()
    {
        foreach (var card in LogCards)
        {
            card.Thumbnail = null;
        }

        LogCards.Clear();
        OverlayLogs.Clear();
        DownloadLogEntry = new TaskQueueLogEntryViewModel(string.Empty, string.Empty, "INFO");
        LastRuntimeStatus = null;
        _nextLogEntryStartsNewCard = false;
    }

    public void AppendSystemLog(string message, string level = "INFO")
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        AppendLogEntry(
            DateTimeOffset.UtcNow,
            message,
            NormalizeLogLevel(level),
            TaskQueueLogSplitMode.Before,
            updateThumbnail: false);
    }

    private string NormalizeLogLevel(string? level)
    {
        if (string.IsNullOrWhiteSpace(level))
        {
            return "INFO";
        }

        if (string.Equals(level, "WARN", StringComparison.OrdinalIgnoreCase)
            || string.Equals(level, "WARNING", StringComparison.OrdinalIgnoreCase))
        {
            return "WARN";
        }

        if (string.Equals(level, "ERROR", StringComparison.OrdinalIgnoreCase))
        {
            return "ERROR";
        }

        if (string.Equals(level, "SUCCESS", StringComparison.OrdinalIgnoreCase))
        {
            return "SUCCESS";
        }

        return "INFO";
    }

    private void AppendLogEntry(
        DateTimeOffset timestamp,
        string content,
        string level,
        TaskQueueLogSplitMode splitMode,
        bool updateThumbnail,
        bool forceScreenshot = false)
    {
        var logTime = FormatLogTimestamp(timestamp);
        var hasContent = !string.IsNullOrWhiteSpace(content);
        var contentEntries = hasContent
            ? SplitLogContentEntries(content).ToArray()
            : [];
        var needsBeforeSplit = splitMode is TaskQueueLogSplitMode.Before or TaskQueueLogSplitMode.Both;
        var needsAfterSplit = splitMode is TaskQueueLogSplitMode.After or TaskQueueLogSplitMode.Both;
        var shouldStartNextEntryOnNewCard = needsBeforeSplit || _nextLogEntryStartsNewCard;

        if (contentEntries.Length == 0 && !updateThumbnail)
        {
            _nextLogEntryStartsNewCard = shouldStartNextEntryOnNewCard || needsAfterSplit;
            TrimLogCards();
            return;
        }

        TaskQueueLogCardViewModel? lastEntryCard = null;
        for (var i = 0; i < contentEntries.Length; i++)
        {
            var card = GetOrCreateLogCardForEntry(shouldStartNextEntryOnNewCard || i > 0);
            var entry = new TaskQueueLogEntryViewModel(
                logTime,
                NormalizeLogContent(contentEntries[i], logTime),
                level);
            card.Append(entry);
            OverlayLogs.Add(entry);
            TrimOverlayLogs();
            lastEntryCard = card;
        }

        if (updateThumbnail)
        {
            var card = lastEntryCard ?? FindLatestNonEmptyLogCard();
            if (card is not null)
            {
                _ = AttachThumbnailToCardAsync(card, forceScreenshot);
            }
        }

        _nextLogEntryStartsNewCard = contentEntries.Length > 0
            ? needsAfterSplit
            : shouldStartNextEntryOnNewCard || needsAfterSplit;
        TrimLogCards();
    }

    private TaskQueueLogCardViewModel GetOrCreateLogCardForEntry(bool startNewCard)
    {
        if (!startNewCard && LogCards.Count > 0)
        {
            return LogCards[^1];
        }

        var card = new TaskQueueLogCardViewModel();
        LogCards.Add(card);
        return card;
    }

    private TaskQueueLogCardViewModel? FindLatestNonEmptyLogCard()
    {
        for (var i = LogCards.Count - 1; i >= 0; i--)
        {
            if (LogCards[i].Items.Count > 0)
            {
                return LogCards[i];
            }
        }

        return null;
    }

    private static IEnumerable<string> SplitLogContentEntries(string content)
    {
        var normalized = content.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        if (lines.Length <= 1)
        {
            yield return content;
            yield break;
        }

        var builder = new StringBuilder();
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var startsNewTimestampedEntry = LeadingLogTimestampPattern.IsMatch(line.TrimStart());
            if (startsNewTimestampedEntry && builder.Length > 0)
            {
                yield return builder.ToString();
                builder.Clear();
            }

            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.Append(line);
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString();
        }
    }

    private void TrimOverlayLogs()
    {
        while (OverlayLogs.Count > MaxOverlayLogs)
        {
            OverlayLogs.RemoveAt(0);
        }
    }

    private async Task<bool> SelectAndPersistOverlayTargetAsync(string targetId, CancellationToken cancellationToken)
    {
        var targetResult = await Runtime.OverlayFeatureService.SelectOverlayTargetAsync(targetId, cancellationToken);
        if (!await ApplyResultAsync(targetResult, "Overlay.Select", cancellationToken))
        {
            return false;
        }

        SelectedOverlayTarget = OverlayTargets.FirstOrDefault(t => string.Equals(t.Id, targetId, StringComparison.Ordinal))
                                ?? SelectedOverlayTarget
                                ?? new OverlayTarget(targetId, targetId, false);

        await PersistOverlayTargetSelectionBestEffortAsync(SelectedOverlayTarget, cancellationToken);
        return true;
    }

    private async Task PersistOverlayTargetSelectionBestEffortAsync(
        OverlayTarget? selectedTarget,
        CancellationToken cancellationToken)
    {
        if (selectedTarget is null)
        {
            return;
        }

        _ = await RunTrackedConfigurationSaveAsync(
            "Overlay.TargetSelection",
            "悬浮窗目标",
            "Overlay.SaveTarget",
            ct => Runtime.SettingsFeatureService.SaveGlobalSettingsAsync(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [ConfigurationKeys.OverlayTarget] = OverlayTargetPersistence.Serialize(selectedTarget),
                    [ConfigurationKeys.OverlayPreviewPinned] = OverlayTargetPersistence.SerializePreviewPreference(selectedTarget),
                },
                ct),
            cancellationToken);
    }

    private void OnOverlaySharedStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(OverlaySharedState.Visible), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(OverlaySharedState.SelectedTargetId), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(OverlaySharedState.Mode), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(OverlaySharedState.StatusMessage), StringComparison.Ordinal))
        {
            if (Dispatcher.UIThread.CheckAccess() || Avalonia.Application.Current is null)
            {
                SyncOverlayPresentationFromSharedState();
                return;
            }

            Dispatcher.UIThread.Post(SyncOverlayPresentationFromSharedState);
        }
    }

    private void SyncOverlayPresentationFromSharedState()
    {
        if (_overlayVisible != _overlaySharedState.Visible)
        {
            _overlayVisible = _overlaySharedState.Visible;
            OnPropertyChanged(nameof(OverlayVisible));
        }

        if (_overlayMode != _overlaySharedState.Mode)
        {
            _overlayMode = _overlaySharedState.Mode;
            OnPropertyChanged(nameof(OverlayMode));
            OnPropertyChanged(nameof(IsOverlayHiddenMode));
            OnPropertyChanged(nameof(IsOverlayPreviewMode));
            OnPropertyChanged(nameof(IsOverlayNativeMode));
        }

        if (!string.IsNullOrWhiteSpace(_overlaySharedState.StatusMessage)
            && !string.Equals(_overlayStatusText, _overlaySharedState.StatusMessage, StringComparison.Ordinal))
        {
            _overlayStatusText = _overlaySharedState.StatusMessage;
            OnPropertyChanged(nameof(OverlayStatusText));
        }

        SyncSelectedOverlayTargetFromSharedState();
        OnPropertyChanged(nameof(OverlayTargetSummaryText));
        OnPropertyChanged(nameof(OverlayButtonToolTip));
    }

    private void SyncSelectedOverlayTargetFromSharedState()
    {
        if (OverlayTargets.Count == 0 || string.IsNullOrWhiteSpace(_overlaySharedState.SelectedTargetId))
        {
            OnPropertyChanged(nameof(OverlayTargetSummaryText));
            return;
        }

        var selected = OverlayTargets.FirstOrDefault(target =>
            string.Equals(target.Id, _overlaySharedState.SelectedTargetId, StringComparison.Ordinal));
        if (selected is null || Equals(_selectedOverlayTarget, selected))
        {
            OnPropertyChanged(nameof(OverlayTargetSummaryText));
            return;
        }

        _selectedOverlayTarget = selected;
        OnPropertyChanged(nameof(SelectedOverlayTarget));
        OnPropertyChanged(nameof(OverlayTargetSummaryText));
        OnPropertyChanged(nameof(OverlayButtonToolTip));
    }

    private static string NormalizeLogContent(string? content, string displayTime)
    {
        var text = string.IsNullOrWhiteSpace(content) ? "-" : content.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return "-";
        }

        if (text.StartsWith(displayTime, StringComparison.Ordinal))
        {
            text = text[displayTime.Length..].TrimStart();
        }

        text = LeadingLogTimestampPattern.Replace(text, string.Empty).TrimStart();
        return string.IsNullOrWhiteSpace(text) ? "-" : text;
    }

    private void TrimLogCards()
    {
        while (LogCards.Count > MaxLogCards)
        {
            var first = LogCards[0];
            first.Thumbnail = null;
            LogCards.RemoveAt(0);
        }
    }

    private async Task AttachThumbnailToCardAsync(TaskQueueLogCardViewModel card, bool forceScreenshot)
    {
        if (!await _logThumbnailSemaphore.WaitAsync(100))
        {
            return;
        }

        try
        {
            var imageResult = await Runtime.CoreBridge.GetImageAsync();
            if (!imageResult.Success || imageResult.Value is null || imageResult.Value.Length == 0)
            {
                return;
            }

            using var stream = new MemoryStream(imageResult.Value, writable: false);
            var bitmap = new Bitmap(stream);
            if (Dispatcher.UIThread.CheckAccess())
            {
                if (!LogCards.Contains(card))
                {
                    bitmap.Dispose();
                    return;
                }

                card.Thumbnail = bitmap;
                TrimLogThumbnails();
            }
            else
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!LogCards.Contains(card))
                    {
                        bitmap.Dispose();
                        return;
                    }

                    card.Thumbnail = bitmap;
                    TrimLogThumbnails();
                });
            }
        }
        catch
        {
            if (forceScreenshot)
            {
                // Ignore screencap errors for log card UI.
            }
        }
        finally
        {
            _logThumbnailSemaphore.Release();
        }
    }

    private void TrimLogThumbnails()
    {
        var maxThumbnails = Math.Max(
            0,
            TryReadGlobalInt(
                Runtime.ConfigurationService.CurrentConfig.GlobalValues,
                MAAUnified.Compat.Constants.ConfigurationKeys.MaxNumberOfLogThumbnails,
                100));
        if (maxThumbnails == 0)
        {
            foreach (var card in LogCards)
            {
                card.Thumbnail = null;
            }

            return;
        }

        var cardsWithThumbnails = LogCards
            .Where(static card => card.Thumbnail is not null)
            .ToList();
        if (cardsWithThumbnails.Count <= maxThumbnails)
        {
            return;
        }

        for (var i = 0; i < cardsWithThumbnails.Count - maxThumbnails; i++)
        {
            cardsWithThumbnails[i].Thumbnail = null;
        }
    }

    private void AppendSemanticCallbackLog(
        string callbackName,
        string module,
        int? taskIndex,
        string? subTask)
    {
        var moduleDisplay = ResolveModuleDisplayName(module);
        var taskLabel = IsValidTaskIndex(taskIndex, Tasks.Count)
            ? Tasks[taskIndex!.Value].Name
            : moduleDisplay;
        var (message, level, splitMode, withThumbnail) = callbackName switch
        {
            "TaskChainStart" => (
                string.Format(RootTexts.GetOrDefault("TaskQueue.Log.TaskStart", "Task started: {0}"), taskLabel),
                "INFO",
                TaskQueueLogSplitMode.Before,
                false),
            "SubTaskStart" => (
                string.Format(
                    RootTexts.GetOrDefault("TaskQueue.Log.SubTaskRunning", "{0}: {1} running"),
                    moduleDisplay,
                    subTask ?? "SubTask"),
                "INFO",
                TaskQueueLogSplitMode.None,
                false),
            "SubTaskCompleted" => (
                string.Format(
                    RootTexts.GetOrDefault("TaskQueue.Log.SubTaskCompleted", "{0}: {1} completed"),
                    moduleDisplay,
                    subTask ?? "SubTask"),
                "SUCCESS",
                TaskQueueLogSplitMode.None,
                true),
            "TaskChainCompleted" => (
                string.Format(RootTexts.GetOrDefault("TaskQueue.Log.TaskCompleted", "Task completed: {0}"), taskLabel),
                "SUCCESS",
                TaskQueueLogSplitMode.After,
                true),
            "TaskChainError" => (
                string.Format(RootTexts.GetOrDefault("TaskQueue.Log.TaskError", "{0} failed"), taskLabel),
                "ERROR",
                TaskQueueLogSplitMode.Both,
                true),
            "SubTaskError" => (
                string.Format(
                    RootTexts.GetOrDefault("TaskQueue.Log.SubTaskError", "{0}: {1} failed"),
                    moduleDisplay,
                    subTask ?? "SubTask"),
                "ERROR",
                TaskQueueLogSplitMode.None,
                true),
            "TaskChainStopped" => (
                RootTexts.GetOrDefault("TaskQueue.Log.TaskStopped", "Task stopped"),
                "WARN",
                TaskQueueLogSplitMode.Both,
                false),
            "AllTasksCompleted" => (
                RootTexts.GetOrDefault("TaskQueue.Log.AllCompleted", "All tasks completed"),
                "SUCCESS",
                TaskQueueLogSplitMode.Both,
                true),
            _ => (
                string.Format(
                    RootTexts.GetOrDefault("TaskQueue.Log.Observed", "{0}: {1}"),
                    moduleDisplay,
                    callbackName),
                "INFO",
                TaskQueueLogSplitMode.None,
                false),
        };

        AppendLogEntry(
            timestamp: DateTimeOffset.Now,
            content: message,
            level: level,
            splitMode: splitMode,
            updateThumbnail: withThumbnail,
            forceScreenshot: withThumbnail);
    }

    private void AppendWpfCallbackLog(CoreCallbackEvent callback, CallbackPayload payload, int? taskIndex)
    {
        TaskQueueCallbackUserLog? log = callback.MsgName switch
        {
            "TaskChainStart" => BuildTaskChainStartLog(taskIndex, payload.TaskChain),
            "TaskChainCompleted" => BuildTaskChainCompletedLog(taskIndex, payload.TaskChain),
            "TaskChainError" => BuildTaskChainErrorLog(payload.TaskChain),
            "TaskChainExtraInfo" => BuildTaskChainExtraInfoLog(payload),
            "SubTaskError" => BuildSubTaskErrorLog(payload, taskIndex),
            "SubTaskStart" => BuildSubTaskStartLog(payload),
            "SubTaskCompleted" => BuildSubTaskCompletedLog(payload, taskIndex),
            "SubTaskExtraInfo" => BuildSubTaskExtraInfoLog(payload, taskIndex),
            "AllTasksCompleted" => BuildAllTasksCompletedLog(callback.Timestamp),
            _ => null,
        };

        if (!log.HasValue)
        {
            return;
        }

        var value = log.Value;
        if (string.IsNullOrWhiteSpace(value.Content) && !value.UpdateThumbnail)
        {
            return;
        }

        AppendLogEntry(
            timestamp: callback.Timestamp,
            content: value.Content,
            level: value.Level,
            splitMode: value.SplitMode,
            updateThumbnail: value.UpdateThumbnail,
            forceScreenshot: value.ForceScreenshot);
    }

    private TaskQueueCallbackUserLog BuildTaskChainStartLog(int? taskIndex, string? taskChain)
    {
        return new(
            Content: GetRootText("StartTask", "Start task: ") + ResolveTaskLogName(taskIndex, taskChain),
            SplitMode: TaskQueueLogSplitMode.Before);
    }

    private TaskQueueCallbackUserLog BuildTaskChainCompletedLog(int? taskIndex, string? taskChain)
    {
        var content = GetRootText("CompleteTask", "Complete task: ") + ResolveTaskLogName(taskIndex, taskChain);
        if (string.Equals(TaskModuleTypes.Normalize(taskChain), TaskModuleTypes.Fight, StringComparison.OrdinalIgnoreCase)
            && _fightSanityCurrent.HasValue
            && _fightSanityMax.HasValue)
        {
            content += Environment.NewLine + string.Format(
                GetRootText("CurrentSanity", "Sanity: {0}/{1}  "),
                _fightSanityCurrent.Value,
                _fightSanityMax.Value);
        }

        return new(
            content,
            "SUCCESS",
            SplitMode: TaskQueueLogSplitMode.Before,
            UpdateThumbnail: true,
            ForceScreenshot: true);
    }

    private TaskQueueCallbackUserLog BuildTaskChainErrorLog(string? taskChain)
    {
        return new(
            Content: GetRootText("TaskError", "Task error: ") + ResolveModuleDisplayName(taskChain ?? "TaskQueue"),
            Level: "ERROR",
            UpdateThumbnail: true,
            ForceScreenshot: true);
    }

    private TaskQueueCallbackUserLog? BuildTaskChainExtraInfoLog(CallbackPayload payload)
    {
        if (!string.Equals(payload.What, "RoutingRestart", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!string.Equals(payload.Why, "TooManyBattlesAhead", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var cost = GetIntValue(payload.Root, "node_cost")?.ToString() ?? "?";
        return new(
            Content: string.Format(
                GetRootText("RoutingRestartTooManyBattles", "Too many battles ahead: {0}, restarting route"),
                cost),
            Level: "WARN");
    }

    private TaskQueueCallbackUserLog BuildAllTasksCompletedLog(DateTimeOffset timestamp)
    {
        var startedAt = _runStartedAt ?? timestamp;
        var duration = timestamp.ToLocalTime() - startedAt.ToLocalTime();
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        var content = string.Format(
            GetRootText("AllTasksComplete", "All task(s) completed!\n(in {0})"),
            duration.ToString(@"h\h\ m\m\ s\s"));
        var sanityReport = BuildSanityRecoveryReport(timestamp);
        if (!string.IsNullOrWhiteSpace(sanityReport))
        {
            content += Environment.NewLine + sanityReport;
        }

        return new(
            Content: content,
            Level: "SUCCESS",
            SplitMode: TaskQueueLogSplitMode.Both,
            UpdateThumbnail: true,
            ForceScreenshot: true);
    }

    private TaskQueueCallbackUserLog? BuildSubTaskErrorLog(CallbackPayload payload, int? taskIndex)
    {
        return payload.SubTask switch
        {
            "StartGameTask" => new(GetRootText("FailedToOpenClient", "Failed to open the client. Please check the configuration file"), "ERROR"),
            "StopGameTask" => new(GetRootText("CloseArknightsFailed", "Shutdown Arknights failed"), "ERROR"),
            "AutoRecruitTask" => new(
                $"{payload.Why ?? GetRootText("ErrorOccurred", "Error occurred")}, {GetRootText("HasReturned", "Has returned")}",
                "ERROR"),
            "RecognizeDrops" => new(GetRootText("DropRecognitionError", "Drops recognition error"), "ERROR"),
            "ReportToPenguinStats" => new(BuildPenguinUploadFailureLog(payload, taskIndex), "WARN"),
            "CheckStageValid" => new(GetRootText("TheEx", "No bonus stage, stopped"), "ERROR"),
            _ => null,
        };
    }

    private void QueueAutomaticSystemNotification(
        CoreCallbackEvent callback,
        CallbackPayload payload,
        int? taskIndex,
        string runId)
    {
        if (!_useSystemNotifications)
        {
            return;
        }

        var request = TryBuildAutomaticSystemNotification(callback, payload, taskIndex, runId);
        if (!request.HasValue)
        {
            return;
        }

        var notification = request.Value;
        _ = Task.Run(async () =>
        {
            try
            {
                await Runtime.PlatformCapabilityService.SendSystemNotificationAsync(
                    notification.Title,
                    notification.Message);
            }
            catch (Exception ex)
            {
                await RecordErrorAsync(
                    notification.Scope,
                    $"Failed to dispatch automatic system notification for {notification.Reason}.",
                    ex);
            }
        });
    }

    private TaskQueueSystemNotification? TryBuildAutomaticSystemNotification(
        CoreCallbackEvent callback,
        CallbackPayload payload,
        int? taskIndex,
        string runId)
    {
        switch (callback.MsgName)
        {
            case "AllTasksCompleted":
                if (string.Equals(_lastCompletionNotificationRunId, runId, StringComparison.Ordinal))
                {
                    return null;
                }

                _lastCompletionNotificationRunId = runId;
                var completionLog = BuildAllTasksCompletedLog(callback.Timestamp);
                return new TaskQueueSystemNotification(
                    NormalizeNotificationText(
                        RootTexts.GetOrDefault("TaskQueue.Log.AllCompleted", "All tasks completed"),
                        "All tasks completed"),
                    NormalizeNotificationText(completionLog.Content, "All tasks completed"),
                    "TaskQueue.Notification.Complete",
                    "task completion");
            case "TaskChainError":
                if (string.Equals(_lastFailureNotificationRunId, runId, StringComparison.Ordinal))
                {
                    return null;
                }

                _lastFailureNotificationRunId = runId;
                var taskFailureLog = BuildTaskChainErrorLog(payload.TaskChain);
                return new TaskQueueSystemNotification(
                    NormalizeNotificationText(
                        string.Format(
                            RootTexts.GetOrDefault("TaskQueue.Log.TaskError", "{0} failed"),
                            ResolveTaskLogName(taskIndex, payload.TaskChain)),
                        "Task failed"),
                    NormalizeNotificationText(taskFailureLog.Content, "Task failed"),
                    "TaskQueue.Notification.Error",
                    "task failure");
            case "SubTaskError":
                if (string.Equals(_lastFailureNotificationRunId, runId, StringComparison.Ordinal))
                {
                    return null;
                }

                _lastFailureNotificationRunId = runId;
                var subTaskFailureLog = BuildSubTaskErrorLog(payload, taskIndex) ?? BuildTaskChainErrorLog(payload.TaskChain);
                return new TaskQueueSystemNotification(
                    NormalizeNotificationText(
                        string.Format(
                            RootTexts.GetOrDefault("TaskQueue.Log.SubTaskError", "{0}: {1} failed"),
                            ResolveTaskLogName(taskIndex, payload.TaskChain),
                            payload.SubTask ?? "SubTask"),
                        "Task failed"),
                    NormalizeNotificationText(subTaskFailureLog.Content, "Task failed"),
                    "TaskQueue.Notification.Error",
                    "sub-task failure");
            default:
                return null;
        }
    }

    private static string NormalizeNotificationText(string? text, string fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        return text.Trim();
    }

    private string BuildPenguinUploadFailureLog(CallbackPayload payload, int? taskIndex)
    {
        if (IsAnnihilationFightTask(taskIndex))
        {
            return $"AnnihilationStage, {GetRootText("GiveUpUploadingPenguins", "Abort upload to Penguin Statistics")}";
        }

        var why = string.IsNullOrWhiteSpace(payload.Why)
            ? GetRootText("GiveUpUploadingPenguins", "Abort upload to Penguin Statistics")
            : payload.Why!;
        return $"{why}, {GetRootText("GiveUpUploadingPenguins", "Abort upload to Penguin Statistics")}";
    }

    private TaskQueueCallbackUserLog? BuildSubTaskStartLog(CallbackPayload payload)
    {
        if (string.Equals(payload.SubTask, "CombatRecordRecognitionTask", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(payload.What)
                ? null
                : new(payload.What!);
        }

        if (!string.Equals(payload.SubTask, "ProcessTask", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var taskName = GetStringValue(payload.Details, "task");
        var execTimes = GetIntValue(payload.Details, "exec_times") ?? 0;
        return taskName switch
        {
            "StartButton2" or "AnnihilationConfirm" => new(
                BuildFightMissionStartLog(),
                SplitMode: TaskQueueLogSplitMode.Before),
            "StoneConfirm" => BuildStoneUsedLog(execTimes),
            "AbandonAction" => new(GetRootText("ActingCommandError", "PRTS error"), "ERROR"),
            "FightMissionFailedAndStop" => new(GetRootText("FightMissionFailedAndStop", "Proxy failed too many times, task stopped"), "ERROR"),
            "RecruitRefreshConfirm" => new(GetRootText("LabelsRefreshed", "Labels refreshed")),
            "RecruitConfirm" => new(GetRootText("RecruitConfirm", "Recruit confirm")),
            "InfrastDormDoubleConfirmButton" => new(GetRootText("InfrastDormDoubleConfirmed", "Operator conflict"), "ERROR"),
            "ExitThenAbandon" => new(GetRootText("ExplorationAbandoned", "Abandoned this Exploration")),
            "MissionCompletedFlag" => new(GetRootText("FightCompleted", "Combat completed"), "SUCCESS", UpdateThumbnail: true),
            "MissionFailedFlag" => new(GetRootText("FightFailed", "Combat failed"), "ERROR", UpdateThumbnail: true),
            "StageTrader" => new(GetRootText("Trader", "Node: Rogue Trader")),
            "StageSafeHouse" => new(GetRootText("SafeHouse", "Node: Safe House")),
            "StageFilterTruth" => new(GetRootText("FilterTruth", "Node: Idea Filter")),
            "StageCombatOps" => new(GetRootText("CombatOps", "Node: Combat Operation")),
            "StageEmergencyOps" => new(GetRootText("EmergencyOps", "Stage: Emergency Operation")),
            "StageDreadfulFoe" or "StageDreadfulFoe-5" => new(GetRootText("DreadfulFoe", "Stage: Dreadful Foe")),
            "StageTraderInvestSystemFull" => new(GetRootText("UpperLimit", "Investment limit reached")),
            "OfflineConfirm" => new(
                GetRootText(
                    IsAutoRestartOnDropEnabled() ? "GameDrop" : "GameDropNoRestart",
                    IsAutoRestartOnDropEnabled()
                        ? "Game disconnected, pending reconnect"
                        : "Game disconnected, not restarting, stopping"),
                "WARN"),
            "GamePass" => new(GetRootText("RoguelikeGamePass", "Exploration completed! Congratulations!")),
            "StageTraderSpecialShoppingAfterRefresh" => new(GetRootText("RoguelikeSpecialItemBought", "Special Item Purchased!")),
            "DeepExplorationNotUnlockedComplain" => new(GetRootText("DeepExplorationNotUnlockedComplain", "Deep Investigation not unlocked yet"), "WARN"),
            "PNS-Resume" => new(GetRootText("ReclamationPnsModeError", "Current task mode does not support the current save."), "ERROR"),
            "PIS-Commence" => new(GetRootText("ReclamationPisModeError", "Current task mode requires a compatible save."), "ERROR"),
            _ => null,
        };
    }

    private TaskQueueCallbackUserLog BuildStoneUsedLog(int execTimes)
    {
        var displayTimes = execTimes > 0 ? execTimes : _stoneUsedTimes + 1;
        _stoneUsedTimes = Math.Max(_stoneUsedTimes + 1, displayTimes);
        return new($"{GetRootText("StoneUsed", "Originite Prime used")} {displayTimes} {GetRootText("UnitTime", "times")}");
    }

    private string BuildFightMissionStartLog()
    {
        var times = "???";
        var sanityCost = "???";
        if (_fightTimesFinished.HasValue && _fightSeries.HasValue && _fightSeries.Value > 0)
        {
            sanityCost = (_fightSanityCost ?? -1) >= 0 ? _fightSanityCost!.Value.ToString() : "???";
            times = _fightSeries.Value == 1
                ? (_fightTimesFinished.Value + 1).ToString()
                : $"{_fightTimesFinished.Value + 1}~{_fightTimesFinished.Value + _fightSeries.Value}";
        }

        var builder = new StringBuilder();
        builder.AppendLine(string.Format(
            GetRootText("MissionStart.FightTask", "Mission started {0} times (-{1} Sanity)"),
            times,
            sanityCost));
        if (_fightSanityCurrent.HasValue && _fightSanityMax.HasValue)
        {
            builder.AppendFormat(
                GetRootText("CurrentSanity", "Sanity: {0}/{1}  "),
                _fightSanityCurrent.Value,
                _fightSanityMax.Value);
        }

        if (_expiringMedicineUsedTimes > 0)
        {
            builder.AppendFormat(
                GetRootText("MedicineUsedTimesWithExpiring", "Medicine: {0},{1}(Expiring)  "),
                _medicineUsedTimes,
                _expiringMedicineUsedTimes);
        }
        else if (_medicineUsedTimes > 0)
        {
            builder.AppendFormat(
                GetRootText("MedicineUsedTimes", "Medicine: {0}  "),
                _medicineUsedTimes);
        }

        if (_stoneUsedTimes > 0)
        {
            builder.AppendFormat(
                GetRootText("StoneUsedTimes", "Stone: {0}  "),
                _stoneUsedTimes);
        }

        return builder.ToString().TrimEnd();
    }

    private TaskQueueCallbackUserLog? BuildSubTaskCompletedLog(CallbackPayload payload, int? taskIndex)
    {
        if (!string.Equals(payload.SubTask, "ProcessTask", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var taskName = GetStringValue(payload.Details, "task");
        var taskChain = TaskModuleTypes.Normalize(payload.TaskChain);
        var execTimes = GetIntValue(payload.Details, "exec_times") ?? 0;
        if (string.Equals(taskChain, TaskModuleTypes.Infrast, StringComparison.OrdinalIgnoreCase)
            && string.Equals(taskName, "UnlockClues", StringComparison.Ordinal))
        {
            return new(GetRootText("ClueExchangeUnlocked", "Clue Exchange Unlocked"));
        }

        if (string.Equals(taskChain, TaskModuleTypes.Roguelike, StringComparison.OrdinalIgnoreCase)
            && string.Equals(taskName, "StartExplore", StringComparison.Ordinal))
        {
            return new(
                $"{GetRootText("BegunToExplore", "Exploration started")} {execTimes} {GetRootText("UnitTime", "times")}",
                SplitMode: TaskQueueLogSplitMode.Before);
        }

        if (!string.Equals(taskChain, TaskModuleTypes.Mall, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return taskName switch
        {
            "EndOfActionThenStop" => new(GetRootText("CompleteTask", "Complete task: ") + GetRootText("CreditFight", "Combat with Support to earn Credits")),
            "VisitLimited" or "VisitNextBlack" => new(GetRootText("CompleteTask", "Complete task: ") + GetRootText("Visiting", "Visit Friends")),
            _ => null,
        };
    }

    private TaskQueueCallbackUserLog? BuildSubTaskExtraInfoLog(CallbackPayload payload, int? taskIndex)
    {
        switch (payload.What)
        {
            case "SanityBeforeStage":
                UpdateFightSanityState(payload.Details);
                return null;
            case "FightTimes":
                return UpdateFightTimesState(payload.Details, taskIndex);
            case "UseMedicine":
                return UpdateMedicineUsageState(payload.Details);
            case "StageDrops":
                return BuildStageDropsLog(payload.Details);
            case "EnterFacility":
                return BuildEnterFacilityLog(payload.Details);
            case "ProductIncorrect":
                return new(GetRootText("ProductIncorrect", "Product does NOT match the configuration."), "ERROR");
            case "ProductUnknown":
                return new(GetRootText("ProductUnknown", "Unknown Product"), "ERROR");
            case "ProductChanged":
                return new(GetRootText("ProductChanged", "Product has changed"));
            case "InfrastConfirmButton":
                return new(
                    string.Empty,
                    UpdateThumbnail: true,
                    ForceScreenshot: true);
            case "RecruitTagsDetected":
                return BuildRecruitTagsDetectedLog(payload.Details);
            case "RecruitSupportOperator":
                return BuildRecruitSupportOperatorLog(payload.Details);
            case "RecruitTagsSelected":
                return BuildRecruitTagsSelectedLog(payload.Details);
            case "RecruitTagsRefreshed":
                return new($"{GetRootText("Refreshed", "Refreshed")}{GetIntValue(payload.Details, "count") ?? 0}{GetRootText("UnitTime", "times")}");
            case "RecruitNoPermit":
                return new(GetRootText(
                    GetBoolValue(payload.Details, "continue") == true ? "ContinueRefresh" : "NoRecruitmentPermit",
                    GetBoolValue(payload.Details, "continue") == true
                        ? "No recruitment permit, trying to refresh Tags"
                        : "No recruitment permit, returned"));
            case "NotEnoughStaff":
                return new(GetRootText("NotEnoughStaff", "Insufficient Operators"), "ERROR");
            case "CreditFullOnlyBuyDiscount":
                return new($"{GetRootText("CreditFullOnlyBuyDiscount", "Remaining credits: ")}{GetIntValue(payload.Details, "credit") ?? 0}");
            case "StageInfo":
                return new($"{GetRootText("StartCombat", "Start combat: ")}{GetStringValue(payload.Details, "name") ?? string.Empty}");
            case "StageInfoError":
                return new(
                    GetRootText("StageInfoError", "Stage recognition error"),
                    "ERROR",
                    TaskQueueLogSplitMode.Both,
                    UpdateThumbnail: true,
                    ForceScreenshot: true);
            case "CustomInfrastRoomGroupsMatch":
                return new($"{GetRootText("RoomGroupsMatch", "Match Group: ")}{GetStringValue(payload.Details, "group") ?? string.Empty}");
            case "CustomInfrastRoomGroupsMatchFailed":
                return BuildRoomGroupsMatchFailedLog(payload.Details);
            case "CustomInfrastRoomOperators":
                return BuildRoomOperatorsLog(payload.Details);
            case "InfrastTrainingIdle":
                return new(GetRootText("TrainingIdle", "Training room is vacant"));
            case "InfrastTrainingCompleted":
                return BuildTrainingCompletedLog(payload.Details);
            case "InfrastTrainingTimeLeft":
                return BuildTrainingTimeLeftLog(payload.Details);
            case "ReclamationReport":
                return BuildReclamationReportLog(payload.Details);
            case "ReclamationProcedureStart":
                return new($"{GetRootText("MissionStart", "Mission started")} {GetIntValue(payload.Details, "times") ?? 0} {GetRootText("UnitTime", "times")}");
            case "StageQueueUnableToAgent":
                return new($"{GetRootText("StageQueue", "Stage Queue: ")} {GetStringValue(payload.Details, "stage_code")} {GetRootText("UnableToAgent", "Unable to use PRTS")}");
            case "StageQueueMissionCompleted":
                return new($"{GetRootText("StageQueue", "Stage Queue: ")} {GetStringValue(payload.Details, "stage_code")} - {GetIntValue(payload.Details, "stars") ?? 0} ★");
            default:
                return null;
        }
    }

    private void UpdateFightSanityState(JsonObject? details)
    {
        _fightSanityCurrent = GetIntValue(details, "current_sanity");
        _fightSanityMax = GetIntValue(details, "max_sanity");
        var reportTime = GetStringValue(details, "report_time");
        if (DateTimeOffset.TryParse(reportTime, out var parsed))
        {
            _fightSanityReportTime = parsed;
        }
    }

    private TaskQueueCallbackUserLog? UpdateFightTimesState(JsonObject? details, int? taskIndex)
    {
        _fightSanityCost = GetIntValue(details, "sanity_cost");
        _fightSeries = GetIntValue(details, "series");
        _fightTimesFinished = GetIntValue(details, "times_finished");

        var finished = GetBoolValue(details, "finished") == true;
        if (!finished || !_fightTimesFinished.HasValue || !_fightSeries.HasValue)
        {
            return null;
        }

        var maxTimes = GetFightTaskTimesLimit(taskIndex);
        if (!maxTimes.HasValue || _fightTimesFinished.Value >= maxTimes.Value)
        {
            return null;
        }

        return new(
            string.Format(
                GetRootText(
                    "FightTimesUnused",
                    "Completed {0} battles, will execute {1} multiplier proxy next time, will complete {2} battles after entering, exceeds {3} limit, will not enter battle"),
                _fightTimesFinished.Value,
                _fightSeries.Value,
                _fightTimesFinished.Value + _fightSeries.Value,
                maxTimes.Value),
            "WARN");
    }

    private TaskQueueCallbackUserLog? UpdateMedicineUsageState(JsonObject? details)
    {
        var medicineCount = GetIntValue(details, "count");
        if (!medicineCount.HasValue || medicineCount.Value <= 0)
        {
            return null;
        }

        var isExpiring = GetBoolValue(details, "is_expiring") == true;
        if (isExpiring)
        {
            _expiringMedicineUsedTimes += medicineCount.Value;
        }

        _medicineUsedTimes += medicineCount.Value;
        return new(
            isExpiring
                ? $"{GetRootText("ExpiringMedicineUsed", "Expiring medicine used")} {_expiringMedicineUsedTimes}(+{medicineCount.Value})"
                : $"{GetRootText("MedicineUsed", "Medicine used")} {_medicineUsedTimes}(+{medicineCount.Value})");
    }

    private TaskQueueCallbackUserLog? BuildStageDropsLog(JsonObject? details)
    {
        if (details is null)
        {
            return null;
        }

        var builder = new StringBuilder();
        var stats = GetArrayValue(details, "stats");
        if (stats is not null)
        {
            foreach (var itemNode in stats)
            {
                if (itemNode is not JsonObject item)
                {
                    continue;
                }

                var itemName = GetStringValue(item, "itemName") ?? string.Empty;
                if (string.Equals(itemName, "furni", StringComparison.OrdinalIgnoreCase))
                {
                    itemName = GetRootText("FurnitureDrop", "Furniture");
                }

                var total = GetIntValue(item, "quantity") ?? 0;
                var add = GetIntValue(item, "addQuantity") ?? 0;
                builder.Append(itemName);
                builder.Append(" : ");
                builder.Append(total);
                if (add > 0)
                {
                    builder.Append(" (+");
                    builder.Append(add);
                    builder.Append(')');
                }

                builder.AppendLine();
            }
        }

        var stage = GetObjectValue(details, "stage");
        var stageCode = GetStringValue(stage, "stageCode") ?? string.Empty;
        var dropText = builder.Length > 0 ? builder.ToString().TrimEnd() : GetRootText("NoDrop", "Nothing");
        var curTimes = GetIntValue(details, "cur_times");
        var content = new StringBuilder()
            .Append(stageCode)
            .Append(' ')
            .Append(GetRootText("TotalDrop", "Total Drops: "))
            .AppendLine()
            .Append(dropText);
        if (curTimes.HasValue && curTimes.Value >= 0)
        {
            content.AppendLine()
                .Append(GetRootText("CurTimes", "Current times"))
                .Append(" : ")
                .Append(curTimes.Value);
        }

        return new(content.ToString(), UpdateThumbnail: true);
    }

    private TaskQueueCallbackUserLog? BuildEnterFacilityLog(JsonObject? details)
    {
        if (details is null)
        {
            return null;
        }

        var facility = GetStringValue(details, "facility") ?? string.Empty;
        var index = (GetIntValue(details, "index") ?? -1) + 1;
        return new(
            $"{GetRootText("ThisFacility", "Current Facility: ")}{facility} {index:D2}",
            SplitMode: TaskQueueLogSplitMode.Before);
    }

    private TaskQueueCallbackUserLog? BuildRecruitTagsDetectedLog(JsonObject? details)
    {
        var tags = GetArrayValue(details, "tags");
        if (tags is null)
        {
            return null;
        }

        var lines = tags
            .Select(tag => tag?.GetValue<string>() ?? string.Empty)
            .Where(static tag => !string.IsNullOrWhiteSpace(tag))
            .ToArray();
        var content = lines.Length == 0 ? GetRootText("NoDrop", "Nothing") : string.Join(Environment.NewLine, lines);
        return new(
            $"{GetRootText("RecruitingResults", "Recruitment Results: ")}{Environment.NewLine}{content}",
            SplitMode: TaskQueueLogSplitMode.Before,
            UpdateThumbnail: true);
    }

    private TaskQueueCallbackUserLog? BuildRecruitSupportOperatorLog(JsonObject? details)
    {
        var name = GetStringValue(details, "name");
        return string.IsNullOrWhiteSpace(name) ? null : new($"Support Operator: {name}");
    }

    private TaskQueueCallbackUserLog? BuildRecruitTagsSelectedLog(JsonObject? details)
    {
        var tags = GetArrayValue(details, "tags");
        if (tags is null)
        {
            return null;
        }

        var selected = tags
            .Select(tag => tag?.GetValue<string>() ?? string.Empty)
            .Where(static tag => !string.IsNullOrWhiteSpace(tag))
            .ToArray();
        var content = selected.Length == 0 ? GetRootText("NoDrop", "Nothing") : string.Join(Environment.NewLine, selected);
        return new($"{GetRootText("Choose", "Choose")} Tags：{Environment.NewLine}{content}");
    }

    private TaskQueueCallbackUserLog? BuildRoomGroupsMatchFailedLog(JsonObject? details)
    {
        var groups = GetArrayValue(details, "groups");
        if (groups is null)
        {
            return null;
        }

        var content = string.Join(", ", groups.Select(group => group?.ToString()).Where(static group => !string.IsNullOrWhiteSpace(group)));
        return string.IsNullOrWhiteSpace(content)
            ? null
            : new($"{GetRootText("RoomGroupsMatchFailed", "Failed to match operator groups, group list: ")}{content}");
    }

    private TaskQueueCallbackUserLog? BuildRoomOperatorsLog(JsonObject? details)
    {
        var names = GetArrayValue(details, "names");
        if (names is null)
        {
            return null;
        }

        var content = string.Join(", ", names.Select(name => name?.ToString()).Where(static name => !string.IsNullOrWhiteSpace(name)));
        return string.IsNullOrWhiteSpace(content)
            ? null
            : new($"{GetRootText("RoomOperators", "Preferred Operators: ")}{content}");
    }

    private TaskQueueCallbackUserLog? BuildTrainingCompletedLog(JsonObject? details)
    {
        if (details is null)
        {
            return null;
        }

        var oper = GetStringValue(details, "operator") ?? "Unknown";
        var skill = GetStringValue(details, "skill") ?? "Unknown";
        var level = GetIntValue(details, "level") ?? -1;
        return new(
            $"[{oper}] {skill}{Environment.NewLine}{GetRootText("TrainingLevel", "Skill Rank")}: {level} {GetRootText("TrainingCompleted", "Training completed")}");
    }

    private TaskQueueCallbackUserLog? BuildTrainingTimeLeftLog(JsonObject? details)
    {
        if (details is null)
        {
            return null;
        }

        var oper = GetStringValue(details, "operator") ?? "Unknown";
        var skill = GetStringValue(details, "skill") ?? "Unknown";
        var level = GetIntValue(details, "level") ?? -1;
        var time = GetStringValue(details, "time") ?? string.Empty;
        return new(
            $"[{oper}] {skill}{Environment.NewLine}{GetRootText("TrainingLevel", "Skill Rank")}: {level}{Environment.NewLine}{GetRootText("TrainingTimeLeft", "Remaining Time")}: {time}");
    }

    private TaskQueueCallbackUserLog? BuildReclamationReportLog(JsonObject? details)
    {
        if (details is null)
        {
            return null;
        }

        return new(
            $"{GetRootText("AlgorithmFinish", "Algorithm Finish")}{Environment.NewLine}" +
            $"{GetRootText("AlgorithmBadge", "Algorithm Badge")}: {GetIntValue(details, "total_badges") ?? -1}(+{GetIntValue(details, "badges") ?? -1}){Environment.NewLine}" +
            $"{GetRootText("AlgorithmConstructionPoint", "Algorithm Construction Point")}: {GetIntValue(details, "total_construction_points") ?? -1}(+{GetIntValue(details, "construction_points") ?? -1})");
    }

    private string? BuildSanityRecoveryReport(DateTimeOffset timestamp)
    {
        if (!_fightSanityCurrent.HasValue || !_fightSanityMax.HasValue || !_fightSanityReportTime.HasValue)
        {
            return null;
        }

        var recoveryTime = _fightSanityReportTime.Value.AddMinutes(
            _fightSanityCurrent.Value < _fightSanityMax.Value
                ? (_fightSanityMax.Value - _fightSanityCurrent.Value) * 6
                : 0);
        var diff = recoveryTime - timestamp;
        if (diff < TimeSpan.Zero)
        {
            diff = TimeSpan.Zero;
        }

        return GetRootText("SanityReport", "Sanity will be full at {DateTime} (in {TimeDiff})")
            .Replace("{DateTime}", recoveryTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), StringComparison.Ordinal)
            .Replace("{TimeDiff}", diff.ToString(@"h\h\ m\m"), StringComparison.Ordinal);
    }

    private string ResolveTaskLogName(int? taskIndex, string? taskChain)
    {
        if (IsValidTaskIndex(taskIndex, Tasks.Count))
        {
            return ResolveTaskDisplayName(Tasks[taskIndex!.Value]);
        }

        return string.IsNullOrWhiteSpace(taskChain)
            ? "Task"
            : $"({ResolveModuleDisplayName(taskChain)})";
    }

    private void ResetRuntimeLogState()
    {
        _runStartedAt = DateTimeOffset.Now;
        _fightSanityReportTime = null;
        _fightSanityCurrent = null;
        _fightSanityMax = null;
        _fightSanityCost = null;
        _fightSeries = null;
        _fightTimesFinished = null;
        _medicineUsedTimes = 0;
        _expiringMedicineUsedTimes = 0;
        _stoneUsedTimes = 0;
    }

    private int? GetFightTaskTimesLimit(int? taskIndex)
    {
        var taskParams = GetCurrentTaskParams(taskIndex);
        return GetIntValue(taskParams, "times");
    }

    private bool IsAnnihilationFightTask(int? taskIndex)
    {
        var taskParams = GetCurrentTaskParams(taskIndex);
        var stage = GetStringValue(taskParams, "stage");
        return string.Equals(stage, "Annihilation", StringComparison.OrdinalIgnoreCase);
    }

    private JsonObject? GetCurrentTaskParams(int? taskIndex)
    {
        if (!IsValidTaskIndex(taskIndex, Tasks.Count))
        {
            return null;
        }

        if (!Runtime.ConfigurationService.TryGetCurrentProfile(out var profile))
        {
            return null;
        }

        var index = taskIndex!.Value;
        if (index < 0 || index >= profile.TaskQueue.Count)
        {
            return null;
        }

        return profile.TaskQueue[index].Params;
    }

    private bool IsAutoRestartOnDropEnabled()
    {
        return TryReadProfileBool(Runtime.ConfigurationService.CurrentConfig, ConfigurationKeys.AutoRestartOnDrop, true);
    }

    private string GetRootText(string key, string fallback)
    {
        return RootTexts.GetOrDefault(key, fallback);
    }

    private static string? GetStringValue(JsonObject? obj, string key)
    {
        if (obj is null || !obj.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        if (node is JsonValue value && value.TryGetValue(out string? text))
        {
            return text;
        }

        return node.ToString();
    }

    private static int? GetIntValue(JsonObject? obj, string key)
    {
        if (obj is null || !obj.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue(out int number))
            {
                return number;
            }

            if (value.TryGetValue(out string? text) && int.TryParse(text, out number))
            {
                return number;
            }
        }

        return null;
    }

    private static bool? GetBoolValue(JsonObject? obj, string key)
    {
        if (obj is null || !obj.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue(out bool flag))
            {
                return flag;
            }

            if (value.TryGetValue(out string? text) && bool.TryParse(text, out flag))
            {
                return flag;
            }
        }

        return null;
    }

    private static JsonObject? GetObjectValue(JsonObject? obj, string key)
    {
        if (obj is null || !obj.TryGetPropertyValue(key, out var node))
        {
            return null;
        }

        return node as JsonObject;
    }

    private static JsonArray? GetArrayValue(JsonObject? obj, string key)
    {
        if (obj is null || !obj.TryGetPropertyValue(key, out var node))
        {
            return null;
        }

        return node as JsonArray;
    }

    private async Task HandleCallbackAsync(CoreCallbackEvent callback)
    {
        await Dispatcher.UIThread.InvokeAsync(() => HandleCallbackCoreAsync(callback));
    }

    private async Task HandleCallbackCoreAsync(CoreCallbackEvent callback)
    {
        var currentOwner = Runtime.SessionService.CurrentRunOwner;
        if (!string.IsNullOrWhiteSpace(currentOwner) && !Runtime.SessionService.IsRunOwner(TaskQueueRunOwner))
        {
            return;
        }

        var metadata = ParseCallbackPayload(callback.PayloadJson);
        if (metadata.HasParseError)
        {
            var warning = $"msgId={callback.MsgId}; msgName={callback.MsgName}; {metadata.ParseError}";
            Runtime.LogService.Warn($"TaskQueue callback payload parse failed: {warning}");
            await RecordEventAsync("TaskQueue.Callback.Parse", warning);
        }

        UpdateRoguelikeCombatState(callback.MsgName, metadata);

        var taskChain = metadata.TaskChain;
        var runId = ResolveRunId(metadata.RunId);
        var taskResolution = ResolveCallbackTaskIndex(
            metadata.TaskIndex,
            metadata.TaskId,
            taskChain,
            callback.MsgName);
        var taskIndex = taskResolution.TaskIndex;
        var resolveSource = taskResolution.ResolveSource;
        var module = ResolveCallbackModule(taskChain, taskIndex);
        await RecordTaskResolutionWarningIfNeededAsync(
            taskResolution,
            callback.MsgName,
            runId,
            taskChain,
            metadata.TaskIndex,
            metadata.TaskId);

        switch (callback.MsgName)
        {
            case "TaskChainStart":
                _currentRunId = runId;
                UpdateTaskStatus(taskIndex, taskChain, TaskQueueItemStatus.Running);
                AppendWpfCallbackLog(callback, metadata, taskIndex);
                await RecordRuntimeStatusAsync(
                    runId,
                    taskIndex,
                    module,
                    callback.MsgName,
                    TaskQueueItemStatus.Running,
                    callback.PayloadJson,
                    resolveSource: resolveSource);
                break;
            case "SubTaskStart":
                UpdateTaskStatus(taskIndex, taskChain, TaskQueueItemStatus.Running);
                StatusMessage = $"{taskChain ?? "Task"}::{metadata.SubTask ?? "SubTask"} running.";
                AppendWpfCallbackLog(callback, metadata, taskIndex);
                await RecordRuntimeStatusAsync(
                    runId,
                    taskIndex,
                    module,
                    callback.MsgName,
                    TaskQueueItemStatus.Running,
                    callback.PayloadJson,
                    resolveSource: resolveSource);
                break;
            case "SubTaskCompleted":
                UpdateTaskStatus(taskIndex, taskChain, TaskQueueItemStatus.Running);
                StatusMessage = $"{taskChain ?? "Task"}::{metadata.SubTask ?? "SubTask"} completed.";
                await UpdateMallDailyExecutionMarkerAsync(metadata, taskIndex);
                TrackAchievementsFromCallback(metadata, callback.MsgName);
                AppendWpfCallbackLog(callback, metadata, taskIndex);
                await RecordRuntimeStatusAsync(
                    runId,
                    taskIndex,
                    module,
                    callback.MsgName,
                    TaskQueueItemStatus.Running,
                    callback.PayloadJson,
                    resolveSource: resolveSource);
                break;
            case "TaskChainCompleted":
                UpdateTaskStatus(taskIndex, taskChain, TaskQueueItemStatus.Success);
                ClearRoguelikeCombatStateIfTaskCompleted(taskChain);
                TrackAchievementsFromCallback(metadata, callback.MsgName);
                AppendWpfCallbackLog(callback, metadata, taskIndex);
                await RecordRuntimeStatusAsync(
                    runId,
                    taskIndex,
                    module,
                    callback.MsgName,
                    TaskQueueItemStatus.Success,
                    callback.PayloadJson,
                    resolveSource: resolveSource);
                CompleteTaskQueueRunOwnership();
                break;
            case "TaskChainError":
            case "SubTaskError":
                UpdateTaskStatus(taskIndex, taskChain, TaskQueueItemStatus.Error);
                if (string.Equals(callback.MsgName, "TaskChainError", StringComparison.OrdinalIgnoreCase))
                {
                    ClearRoguelikeCombatStateIfTaskCompleted(taskChain);
                }
                LastErrorMessage = $"{callback.MsgName}: {callback.PayloadJson}";
                AppendWpfCallbackLog(callback, metadata, taskIndex);
                await RecordRuntimeStatusAsync(
                    runId,
                    taskIndex,
                    module,
                    callback.MsgName,
                    TaskQueueItemStatus.Error,
                    callback.PayloadJson,
                    UiErrorCode.TaskRuntimeCallbackError,
                    resolveSource);
                QueueAutomaticSystemNotification(callback, metadata, taskIndex, runId);
                CompleteTaskQueueRunOwnership();
                break;
            case "TaskChainStopped":
                if (_clearTaskStatusesWhenStopped)
                {
                    ResetAllTaskStatuses();
                }
                else if (taskIndex.HasValue || !string.IsNullOrWhiteSpace(taskChain))
                {
                    UpdateTaskStatus(taskIndex, taskChain, TaskQueueItemStatus.Skipped);
                }
                else
                {
                    MarkRunningTasks(TaskQueueItemStatus.Skipped);
                }

                ClearRoguelikeCombatStateIfTaskCompleted(taskChain);

                await RecordRuntimeStatusAsync(
                    runId,
                    taskIndex,
                    module,
                    callback.MsgName,
                    TaskQueueItemStatus.Skipped,
                    callback.PayloadJson,
                    resolveSource: resolveSource);
                CompleteTaskQueueRunOwnership();
                break;
            case "AllTasksCompleted":
                MarkRunningTasks(TaskQueueItemStatus.Success);
                AppendWpfCallbackLog(callback, metadata, taskIndex);
                await RecordRuntimeStatusAsync(
                    runId,
                    taskIndex,
                    module,
                    callback.MsgName,
                    TaskQueueItemStatus.Success,
                    callback.PayloadJson,
                    resolveSource: resolveSource);
                QueueAutomaticSystemNotification(callback, metadata, taskIndex, runId);
                if (!string.Equals(_lastPostActionRunId, runId, StringComparison.Ordinal))
                {
                    _lastPostActionRunId = runId;
                    await ExecutePostActionAfterCompletionAsync(callback, runId, taskIndex);
                }

                CompleteTaskQueueRunOwnership();

                break;
            case "TaskChainExtraInfo":
            case "SubTaskExtraInfo":
                TrackAchievementsFromCallback(metadata, callback.MsgName);
                AppendWpfCallbackLog(callback, metadata, taskIndex);
                await RecordRuntimeStatusAsync(
                    runId,
                    taskIndex,
                    module,
                    callback.MsgName,
                    "Observed",
                    callback.PayloadJson,
                    resolveSource: resolveSource);
                break;
            default:
                await RecordRuntimeStatusAsync(
                    runId,
                    taskIndex,
                    module,
                    callback.MsgName,
                    "Observed",
                    callback.PayloadJson,
                    resolveSource: resolveSource);
                break;
        }
    }

    private async Task UpdateMallDailyExecutionMarkerAsync(CallbackPayload payload, int? taskIndex)
    {
        if (!string.Equals(TaskModuleTypes.Normalize(payload.TaskChain), TaskModuleTypes.Mall, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!IsValidTaskIndex(taskIndex, Tasks.Count) || string.IsNullOrWhiteSpace(payload.SubTask))
        {
            return;
        }

        var updateCreditFight = string.Equals(payload.SubTask, "EndOfActionThenStop", StringComparison.Ordinal);
        var updateVisitFriends = string.Equals(payload.SubTask, "VisitLimited", StringComparison.Ordinal)
                                 || string.Equals(payload.SubTask, "VisitNextBlack", StringComparison.Ordinal);
        if (!updateCreditFight && !updateVisitFriends)
        {
            return;
        }

        if (!Runtime.ConfigurationService.TryGetCurrentProfile(out var profile))
        {
            return;
        }

        var index = taskIndex!.Value;
        if (index < 0 || index >= profile.TaskQueue.Count)
        {
            return;
        }

        var task = profile.TaskQueue[index];
        if (!string.Equals(TaskModuleTypes.Normalize(task.Type), TaskModuleTypes.Mall, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        task.Params ??= new JsonObject();
        var clientType = ResolveProfileClientType(profile);
        var mark = MallDailyResetHelper.GetCurrentYjDateString(DateTime.UtcNow, clientType);
        var updated = false;
        if (updateCreditFight)
        {
            task.Params[UiMallCreditFightLastTime] = mark;
            updated = true;
        }

        if (updateVisitFriends)
        {
            task.Params[UiMallVisitFriendsLastTime] = mark;
            updated = true;
        }

        if (!updated)
        {
            return;
        }

        _ = await ConfigurationSaveTracker.Instance.RunTrackedAsync(
            "TaskQueue.MallDailyMarker",
            Texts.GetOrDefault("Mall.Title", "信用收取"),
            "TaskQueue.MallDailyMarker.Save",
            Runtime.DiagnosticsService,
            async cancellationToken =>
            {
                await Runtime.ConfigurationService.SaveAsync(cancellationToken);
                return true;
            });
    }

    private void TrackAchievementsAfterStart(int enabledTaskCount)
    {
        if (enabledTaskCount > 0)
        {
            _ = Runtime.AchievementTrackerService.SetProgress("TaskChainKing", enabledTaskCount);
        }

        Runtime.AchievementTrackerService.MissionStartCountAdd();
        Runtime.AchievementTrackerService.UseDailyAdd();
    }

    private void TrackAchievementsFromCallback(CallbackPayload payload, string callbackName)
    {
        if (!string.Equals(callbackName, "SubTaskCompleted", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(callbackName, "TaskChainCompleted", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(callbackName, "SubTaskExtraInfo", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.Equals(callbackName, "SubTaskCompleted", StringComparison.OrdinalIgnoreCase)
            && string.Equals(payload.SubTask, "ProcessTask", StringComparison.OrdinalIgnoreCase))
        {
            var taskChain = TaskModuleTypes.Normalize(payload.TaskChain);
            var taskName = GetStringValue(payload.Details, "task");
            if (string.Equals(taskChain, TaskModuleTypes.Infrast, StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(taskName, "UnlockClues", StringComparison.Ordinal))
                {
                    _ = Runtime.AchievementTrackerService.AddProgressToGroup("ClueUse");
                    Runtime.AchievementTrackerService.ClueObsessionAdd();
                }
                else if (string.Equals(taskName, "SendClues", StringComparison.Ordinal))
                {
                    _ = Runtime.AchievementTrackerService.AddProgressToGroup("ClueSend");
                }
            }
        }
    }

    private static string ResolveProfileClientType(UnifiedProfile profile)
    {
        if (!profile.Values.TryGetValue("ClientType", out var node) || node is not JsonValue value)
        {
            return "Official";
        }

        if (!value.TryGetValue(out string? clientType) || string.IsNullOrWhiteSpace(clientType))
        {
            return "Official";
        }

        return clientType.Trim();
    }

    private async Task<bool> EnsureEditableAsync(string scope, CancellationToken cancellationToken = default)
    {
        if (CanEdit)
        {
            return true;
        }

        return await ApplyResultAsync(
            UiOperationResult.Fail(
                UiErrorCode.TaskQueueEditBlocked,
                Texts.GetOrDefault(
                    "TaskQueue.Error.EditBlockedWhileRunning",
                    "Task editing is blocked while running.")),
            scope,
            cancellationToken);
    }

    private static bool IsValidTaskIndex(int? taskIndex, int count)
    {
        return taskIndex.HasValue && taskIndex.Value >= 0 && taskIndex.Value < count;
    }

    private CallbackTaskResolution ResolveCallbackTaskIndex(
        int? callbackTaskIndex,
        int? callbackTaskId,
        string? taskChain,
        string action)
    {
        if (IsValidTaskIndex(callbackTaskIndex, Tasks.Count))
        {
            return new CallbackTaskResolution(callbackTaskIndex, "task_index");
        }

        if (callbackTaskId.HasValue
            && Runtime.SessionService.TryResolveTaskIndexByCoreTaskId(callbackTaskId.Value, out var mappedIndex)
            && IsValidTaskIndex(mappedIndex, Tasks.Count))
        {
            return new CallbackTaskResolution(mappedIndex, "task_id_map");
        }

        return ResolveCallbackTaskByChain(taskChain, action);
    }

    private CallbackTaskResolution ResolveCallbackTaskByChain(string? taskChain, string action)
    {
        var matchedIndices = FindTaskIndicesByChain(taskChain);
        if (matchedIndices.Count == 0)
        {
            return new CallbackTaskResolution(
                null,
                "unresolved",
                WarningDetail: $"taskChain={taskChain ?? "-"} action={action} reason=no-matching-task-chain");
        }

        if (matchedIndices.Count == 1)
        {
            return new CallbackTaskResolution(matchedIndices[0], "chain_unique");
        }

        int selectedIndex;
        var strategy = "fallback-min-index";
        if (ShouldPreferRunningTask(action))
        {
            if (TryFindIndexByStatus(matchedIndices, TaskQueueItemStatus.Running, out selectedIndex))
            {
                strategy = "prefer-running";
            }
            else
            {
                selectedIndex = matchedIndices[0];
            }
        }
        else if (ShouldPreferIdleTask(action))
        {
            if (TryFindIndexByStatus(matchedIndices, TaskQueueItemStatus.Idle, out selectedIndex))
            {
                strategy = "prefer-idle";
            }
            else
            {
                selectedIndex = matchedIndices[0];
            }
        }
        else
        {
            selectedIndex = matchedIndices[0];
        }

        return new CallbackTaskResolution(
            selectedIndex,
            "chain_heuristic",
            WarningDetail: $"taskChain={taskChain ?? "-"} action={action} candidates={string.Join(",", matchedIndices)} selected={selectedIndex} strategy={strategy}");
    }

    private static bool ShouldPreferRunningTask(string action)
    {
        return action is "TaskChainCompleted" or "TaskChainError" or "SubTaskError" or "TaskChainStopped";
    }

    private static bool ShouldPreferIdleTask(string action)
    {
        return action is "TaskChainStart" or "SubTaskStart" or "SubTaskCompleted";
    }

    private bool TryFindIndexByStatus(IReadOnlyList<int> candidateIndices, string expectedStatus, out int selectedIndex)
    {
        foreach (var index in candidateIndices)
        {
            var task = Tasks[index];
            if (string.Equals(expectedStatus, TaskQueueItemStatus.Idle, StringComparison.OrdinalIgnoreCase))
            {
                if (!task.IsStatusIdle)
                {
                    continue;
                }
            }
            else if (!string.Equals(task.Status, expectedStatus, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            selectedIndex = index;
            return true;
        }

        selectedIndex = -1;
        return false;
    }

    private List<int> FindTaskIndicesByChain(string? taskChain)
    {
        var matches = new List<int>();
        if (string.IsNullOrWhiteSpace(taskChain))
        {
            return matches;
        }

        for (var i = 0; i < Tasks.Count; i++)
        {
            if (string.Equals(
                    TaskModuleTypes.Normalize(Tasks[i].Type),
                    taskChain,
                    StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(i);
            }
        }

        return matches;
    }

    private async Task RecordTaskResolutionWarningIfNeededAsync(
        CallbackTaskResolution resolution,
        string action,
        string runId,
        string? taskChain,
        int? callbackTaskIndex,
        int? callbackTaskId)
    {
        if (resolution.ResolveSource is not ("chain_heuristic" or "unresolved"))
        {
            return;
        }

        var payload =
            $"runId={runId} action={action} taskChain={taskChain ?? "-"} " +
            $"taskIndex={callbackTaskIndex?.ToString() ?? "-"} taskId={callbackTaskId?.ToString() ?? "-"} " +
            $"resolveSource={resolution.ResolveSource} detail={resolution.WarningDetail ?? "-"}";
        await RecordEventAsync("TaskQueue.Callback.ResolveTask", payload);
    }

    private string ResolveCallbackModule(string? taskChain, int? taskIndex)
    {
        if (!string.IsNullOrWhiteSpace(taskChain))
        {
            return taskChain!;
        }

        if (IsValidTaskIndex(taskIndex, Tasks.Count))
        {
            return TaskModuleTypes.Normalize(Tasks[taskIndex!.Value].Type);
        }

        return "TaskQueue";
    }

    private void UpdateTaskStatus(int? taskIndex, string? taskChain, string status)
    {
        if (IsValidTaskIndex(taskIndex, Tasks.Count))
        {
            var task = Tasks[taskIndex!.Value];
            task.Status = status;
            RefreshTaskItemLocalization(task);
        }
    }

    private async Task ExecutePostActionAfterCompletionAsync(CoreCallbackEvent callback, string runId, int? taskIndex)
    {
        var result = await Runtime.PostActionFeatureService.ExecuteAfterCompletionAsync(
            new PostActionExecutionContext(
                callback.MsgName,
                WasSuccessfulTaskChain: true,
                RunId: runId,
                TaskIndex: taskIndex),
            PostActionModule.BuildRuntimeConfig());

        if (!result.Success)
        {
            LastErrorMessage = result.Message;
            await RecordFailedResultAsync("PostAction.Execute", result);
            return;
        }

        StatusMessage = result.Message;
        if (PostActionModule.Once)
        {
            await PostActionModule.ReloadPersistentConfigAsync();
        }
    }

    private void MarkRunningTasks(string status)
    {
        foreach (var task in Tasks.Where(t => string.Equals(t.Status, TaskQueueItemStatus.Running, StringComparison.OrdinalIgnoreCase)))
        {
            task.Status = status;
            RefreshTaskItemLocalization(task);
        }
    }

    private void ResetAllTaskStatuses()
    {
        foreach (var task in Tasks)
        {
            if (string.Equals(task.Status, TaskQueueItemStatus.Idle, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            task.Status = TaskQueueItemStatus.Idle;
            RefreshTaskItemLocalization(task);
        }
    }

    private void SyncStoppedUiStateIfSessionNotActive()
    {
        if (CurrentSessionState is SessionState.Running or SessionState.Stopping)
        {
            return;
        }

        if (_clearTaskStatusesWhenStopped)
        {
            ResetAllTaskStatuses();
            _clearTaskStatusesWhenStopped = false;
        }
        else
        {
            MarkRunningTasks(TaskQueueItemStatus.Skipped);
        }

        CompleteTaskQueueRunOwnership();
    }

    private void CompleteTaskQueueRunOwnership()
    {
        Runtime.SessionService.EndRun(TaskQueueRunOwner);
    }

    private async Task RecordRuntimeStatusAsync(
        string runId,
        int? taskIndex,
        string module,
        string action,
        string status,
        string message,
        string? errorCode = null,
        string resolveSource = "unresolved")
    {
        LastRuntimeStatus = new TaskRuntimeStatusSnapshot(
            RunId: runId,
            TaskIndex: taskIndex,
            Module: module,
            Action: action,
            Status: status,
            Message: message,
            Timestamp: DateTimeOffset.UtcNow);

        var code = string.IsNullOrWhiteSpace(errorCode) ? "-" : errorCode;
        var payload =
            $"runId={runId} taskIndex={taskIndex?.ToString() ?? "-"} module={module} action={action} " +
            $"resolveSource={resolveSource} errorCode={code} message={message}";
        if (code == "-")
        {
            await RecordEventAsync("TaskQueue.Callback", payload);
        }
        else
        {
            await RecordFailedResultAsync(
                "TaskQueue.Callback",
                UiOperationResult.Fail(code, payload));
        }
    }

    private readonly record struct CallbackTaskResolution(int? TaskIndex, string ResolveSource, string? WarningDetail = null);

    private readonly record struct TaskQueueSystemNotification(
        string Title,
        string Message,
        string Scope,
        string Reason);

    private string ResolveRunId(string? callbackRunId)
    {
        if (!string.IsNullOrWhiteSpace(callbackRunId))
        {
            return callbackRunId!;
        }

        if (string.IsNullOrWhiteSpace(_currentRunId) || _currentRunId == "-")
        {
            _currentRunId = Guid.NewGuid().ToString("N");
        }

        return _currentRunId;
    }

    private static CallbackPayload ParseCallbackPayload(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return CallbackPayload.Empty;
        }

        try
        {
            if (JsonNode.Parse(payloadJson) is not JsonObject root)
            {
                return new CallbackPayload(null, null, null, null, null, null, null, null, null, "payload is not a JSON object");
            }

            var taskChain = GetStringValue(root, "task_chain") ?? GetStringValue(root, "taskchain");
            var subTask = GetStringValue(root, "sub_task") ?? GetStringValue(root, "subtask");
            var runId = GetStringValue(root, "run_id")
                        ?? GetStringValue(root, "runid")
                        ?? GetStringValue(root, "uuid")
                        ?? GetStringValue(root, "id");
            var taskIndex = GetIntValue(root, "task_index") ?? GetIntValue(root, "taskindex");
            var taskId = GetIntValue(root, "task_id") ?? GetIntValue(root, "taskid");
            var what = GetStringValue(root, "what");
            var why = GetStringValue(root, "why");
            var details = GetObjectValue(root, "details");

            return new CallbackPayload(taskChain, subTask, runId, taskIndex, taskId, what, why, details, root, null);
        }
        catch (JsonException ex)
        {
            return new CallbackPayload(null, null, null, null, null, null, null, null, null, $"payload parse failed: {ex.Message}");
        }
    }

    private readonly record struct CallbackPayload(
        string? TaskChain,
        string? SubTask,
        string? RunId,
        int? TaskIndex,
        int? TaskId,
        string? What,
        string? Why,
        JsonObject? Details,
        JsonObject? Root,
        string? ParseError = null)
    {
        public static CallbackPayload Empty { get; } = new(null, null, null, null, null, null, null, null, null, null);

        public bool HasParseError => !string.IsNullOrWhiteSpace(ParseError);
    }

    private readonly record struct TaskQueueCallbackUserLog(
        string Content,
        string Level = "INFO",
        TaskQueueLogSplitMode SplitMode = TaskQueueLogSplitMode.None,
        bool UpdateThumbnail = false,
        bool ForceScreenshot = false);

    private bool ShouldDelayStopUntilRoguelikeCombatComplete()
    {
        return _roguelikeInCombat && TryReadProfileBool(
            Runtime.ConfigurationService.CurrentConfig,
            ConfigurationKeys.RoguelikeDelayAbortUntilCombatComplete,
            false);
    }

    private async Task WaitUntilRoguelikeCombatCompleteAsync(CancellationToken cancellationToken)
    {
        const int MaxWaitSeconds = 600;
        var elapsed = 0;
        while (elapsed < MaxWaitSeconds && _roguelikeInCombat)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var delayEnabled = TryReadProfileBool(
                Runtime.ConfigurationService.CurrentConfig,
                ConfigurationKeys.RoguelikeDelayAbortUntilCombatComplete,
                false);
            if (!delayEnabled)
            {
                break;
            }

            CurrentSessionState = Runtime.SessionService.CurrentState;
            if (CurrentSessionState != SessionState.Running)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            elapsed++;
        }
    }

    private void UpdateRoguelikeCombatState(string callbackName, CallbackPayload payload)
    {
        if (!string.Equals(callbackName, "SubTaskExtraInfo", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!string.Equals(
                TaskModuleTypes.Normalize(payload.TaskChain),
                TaskModuleTypes.Roguelike,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.Equals(payload.What, "RoguelikeCombatEnd", StringComparison.OrdinalIgnoreCase))
        {
            _roguelikeInCombat = false;
            return;
        }

        var delayEnabled = TryReadProfileBool(
            Runtime.ConfigurationService.CurrentConfig,
            ConfigurationKeys.RoguelikeDelayAbortUntilCombatComplete,
            false);
        if (!delayEnabled)
        {
            return;
        }

        if (string.Equals(payload.What, "StageInfo", StringComparison.OrdinalIgnoreCase))
        {
            _roguelikeInCombat = true;
        }
    }

    private void ClearRoguelikeCombatStateIfTaskCompleted(string? taskChain)
    {
        if (string.Equals(TaskModuleTypes.Normalize(taskChain), TaskModuleTypes.Roguelike, StringComparison.OrdinalIgnoreCase))
        {
            _roguelikeInCombat = false;
        }
    }

    private string ResolveLanguage()
    {
        if (Runtime.ConfigurationService.CurrentConfig.GlobalValues.TryGetValue(ConfigurationKeys.Localization, out var value)
            && value is JsonValue jsonValue
            && jsonValue.TryGetValue(out string? language)
            && !string.IsNullOrWhiteSpace(language))
        {
            return UiLanguageCatalog.Normalize(language);
        }

        return UiLanguageCatalog.Normalize(_uiLanguageCoordinator.CurrentLanguage);
    }

    private void OnUnifiedLanguageChanged(object? sender, UiLanguageChangedEventArgs e)
    {
        if (Avalonia.Application.Current is null)
        {
            SetLanguage(e.CurrentLanguage);
            return;
        }

        Dispatcher.UIThread.Post(() => SetLanguage(e.CurrentLanguage), DispatcherPriority.Send);
    }

    private void RefreshSelectionBatchModeFromConfig()
    {
        var config = Runtime.ConfigurationService.CurrentConfig;
        var inverseMode = TryReadProfileBool(config, ConfigurationKeys.MainFunctionInverseMode, false);
        ApplySelectionBatchMode(
            TryReadProfileString(config, ConfigurationKeys.InverseClearMode, "Clear"),
            inverseMode);
    }

    private async Task PersistSelectionBatchModeAsync(CancellationToken cancellationToken = default)
    {
        var config = Runtime.ConfigurationService.CurrentConfig;
        if (config.Profiles.TryGetValue(config.CurrentProfile, out var profile))
        {
            profile.Values[ConfigurationKeys.MainFunctionInverseMode] = JsonValue.Create(SelectionBatchMode == SelectionBatchMode.Inverse);
            if (ShowBatchModeToggle)
            {
                profile.Values[ConfigurationKeys.InverseClearMode] = JsonValue.Create("ClearInverse");
            }
        }
        else
        {
            config.GlobalValues[ConfigurationKeys.MainFunctionInverseMode] = JsonValue.Create(SelectionBatchMode == SelectionBatchMode.Inverse);
            if (ShowBatchModeToggle)
            {
                config.GlobalValues[ConfigurationKeys.InverseClearMode] = JsonValue.Create("ClearInverse");
            }
        }

        _ = await ConfigurationSaveTracker.Instance.RunTrackedAsync(
            "TaskQueue.SelectionBatchMode",
            ResolveTaskQueueSaveDisplayName(),
            "TaskQueue.SelectionBatchMode.Save",
            Runtime.DiagnosticsService,
            async ct =>
            {
                await Runtime.ConfigurationService.SaveAsync(ct);
                return true;
            },
            cancellationToken);
    }

    private static string TryReadGlobalString(
        IReadOnlyDictionary<string, JsonNode?> values,
        string key,
        string fallback)
    {
        if (!values.TryGetValue(key, out var node) || node is null)
        {
            return fallback;
        }

        if (node is JsonValue jsonValue && jsonValue.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        return fallback;
    }

    private static bool TryReadGlobalBool(
        IReadOnlyDictionary<string, JsonNode?> values,
        string key,
        bool fallback)
    {
        if (!values.TryGetValue(key, out var node) || node is null)
        {
            return fallback;
        }

        if (node is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue(out bool parsedBool))
            {
                return parsedBool;
            }

            if (jsonValue.TryGetValue(out int parsedInt))
            {
                return parsedInt != 0;
            }

            if (jsonValue.TryGetValue(out string? text) && bool.TryParse(text, out parsedBool))
            {
                return parsedBool;
            }
        }

        return fallback;
    }

    private static int TryReadGlobalInt(
        IReadOnlyDictionary<string, JsonNode?> values,
        string key,
        int fallback)
    {
        if (!values.TryGetValue(key, out var node) || node is null)
        {
            return fallback;
        }

        if (node is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue(out int parsedInt))
            {
                return parsedInt;
            }

            if (jsonValue.TryGetValue(out string? text) && int.TryParse(text, out parsedInt))
            {
                return parsedInt;
            }
        }

        return fallback;
    }

    private static string TryReadProfileString(UnifiedConfig config, string key, string fallback)
    {
        if (TryGetProfileNode(config, key, out var node)
            && TryReadStringNode(node, out var value))
        {
            return value;
        }

        return TryReadGlobalString(config.GlobalValues, key, fallback);
    }

    private static bool TryReadProfileBool(UnifiedConfig config, string key, bool fallback)
    {
        if (TryGetProfileNode(config, key, out var node)
            && TryReadBoolNode(node, out var value))
        {
            return value;
        }

        return TryReadGlobalBool(config.GlobalValues, key, fallback);
    }

    private static int TryReadProfileInt(UnifiedConfig config, string key, int fallback)
    {
        if (TryGetProfileNode(config, key, out var node)
            && TryReadIntNode(node, out var value))
        {
            return value;
        }

        return TryReadGlobalInt(config.GlobalValues, key, fallback);
    }

    private static bool TryGetProfileNode(UnifiedConfig config, string key, out JsonNode? node)
    {
        if (!string.IsNullOrWhiteSpace(config.CurrentProfile)
            && config.Profiles.TryGetValue(config.CurrentProfile, out var profile)
            && profile.Values.TryGetValue(key, out node)
            && node is not null)
        {
            return true;
        }

        node = null;
        return false;
    }

    private static bool TryReadStringNode(JsonNode? node, out string value)
    {
        if (node is JsonValue jsonValue
            && jsonValue.TryGetValue(out string? text)
            && !string.IsNullOrWhiteSpace(text))
        {
            value = text.Trim();
            return true;
        }

        var raw = node?.ToString();
        if (!string.IsNullOrWhiteSpace(raw))
        {
            value = raw.Trim();
            return true;
        }

        value = string.Empty;
        return false;
    }

    private void ApplyGuiSettingsFromConfig()
    {
        if (Runtime.ConfigurationService.TryGetCurrentProfile(out var profile))
        {
            ConnectionGameProfileSync.ReadFromProfile(profile, _connectionGameSharedState, tolerateMissing: true);
        }

        _logTimestampFormat = ResolveLogTimestampFormat();
        _useSystemNotifications = TryReadGlobalBool(
            Runtime.ConfigurationService.CurrentConfig.GlobalValues,
            ConfigurationKeys.UseNotify,
            fallback: true);
        RefreshSelectionBatchModeFromConfig();
        RefreshRoguelikeGuiDependentOptions();
    }

    private void ApplySelectionBatchMode(string inverseClearMode, bool? persistedInverseMode = null)
    {
        var normalized = string.IsNullOrWhiteSpace(inverseClearMode)
            ? "Clear"
            : inverseClearMode.Trim();

        if (string.Equals(normalized, "Inverse", StringComparison.OrdinalIgnoreCase))
        {
            ShowBatchModeToggle = false;
            SelectionBatchMode = SelectionBatchMode.Inverse;
            return;
        }

        if (string.Equals(normalized, "ClearInverse", StringComparison.OrdinalIgnoreCase))
        {
            ShowBatchModeToggle = true;
            if (persistedInverseMode.HasValue)
            {
                SelectionBatchMode = persistedInverseMode.Value
                    ? SelectionBatchMode.Inverse
                    : SelectionBatchMode.Clear;
            }

            return;
        }

        ShowBatchModeToggle = false;
        SelectionBatchMode = SelectionBatchMode.Clear;
    }

    private string ResolveLogTimestampFormat()
    {
        return NormalizeLogTimestampFormat(
            TryReadGlobalString(
                Runtime.ConfigurationService.CurrentConfig.GlobalValues,
                ConfigurationKeys.LogItemDateFormat,
                DefaultLogItemDateFormat));
    }

    private string FormatLogTimestamp(DateTimeOffset timestamp)
    {
        try
        {
            return timestamp.ToLocalTime().ToString(_logTimestampFormat, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            return timestamp.ToLocalTime().ToString(DefaultLogItemDateFormat, System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private static string NormalizeLogTimestampFormat(string? format)
    {
        return string.IsNullOrWhiteSpace(format) ? DefaultLogItemDateFormat : format.Trim();
    }

    private static bool TryReadBoolNode(JsonNode? node, out bool value)
    {
        if (node is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue(out bool parsedBool))
            {
                value = parsedBool;
                return true;
            }

            if (jsonValue.TryGetValue(out int parsedInt))
            {
                value = parsedInt != 0;
                return true;
            }

            if (jsonValue.TryGetValue(out string? text))
            {
                if (bool.TryParse(text, out parsedBool))
                {
                    value = parsedBool;
                    return true;
                }

                if (int.TryParse(text, out parsedInt))
                {
                    value = parsedInt != 0;
                    return true;
                }
            }
        }

        value = false;
        return false;
    }

    private static bool TryReadIntNode(JsonNode? node, out int value)
    {
        if (node is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue(out int parsedInt))
            {
                value = parsedInt;
                return true;
            }

            if (jsonValue.TryGetValue(out bool parsedBool))
            {
                value = parsedBool ? 1 : 0;
                return true;
            }

            if (jsonValue.TryGetValue(out string? text) && int.TryParse(text, out parsedInt))
            {
                value = parsedInt;
                return true;
            }
        }

        value = 0;
        return false;
    }

    private void PrepareForConfigurationContextSwitch()
    {
        CancelTypedModuleAutoSave();
        CancelPendingBinding();
        ClearTaskModuleBindings();
    }

    private void CancelTypedModuleAutoSave()
    {
        lock (_moduleAutoSaveGate)
        {
            _moduleAutoSaveCts?.Cancel();
            _moduleAutoSaveCts?.Dispose();
            _moduleAutoSaveCts = null;
        }
    }

    private void CancelPendingBinding()
    {
        lock (_pendingBindingGate)
        {
            _pendingBindingTask = Task.CompletedTask;
        }

        IsSelectedTaskBindingPending = false;
    }

    private void RememberSelectedTaskIndex()
    {
        if (!Runtime.ConfigurationService.TryGetCurrentProfile(out var profile))
        {
            return;
        }

        var selectedIndex = _selectedTask is null ? -1 : Tasks.IndexOf(_selectedTask);
        profile.Values[TaskSelectedIndexConfigKey] = JsonValue.Create(selectedIndex);
    }

    private int? ResolveReloadSelectionIndex(bool preferProfileSelectedIndex, int previousSelectedIndex, int taskCount)
    {
        if (taskCount <= 0)
        {
            return null;
        }

        if (preferProfileSelectedIndex && TryReadPersistedSelectedTaskIndex(out var persistedIndex))
        {
            return persistedIndex switch
            {
                < 0 when previousSelectedIndex >= 0 && previousSelectedIndex < taskCount => previousSelectedIndex,
                < 0 => null,
                _ when persistedIndex < taskCount => persistedIndex,
                _ => 0,
            };
        }

        if (previousSelectedIndex >= 0 && previousSelectedIndex < taskCount)
        {
            return previousSelectedIndex;
        }

        return 0;
    }

    private bool TryReadPersistedSelectedTaskIndex(out int index)
    {
        index = -1;
        if (!TryGetProfileNode(Runtime.ConfigurationService.CurrentConfig, TaskSelectedIndexConfigKey, out var node)
            || node is null)
        {
            return false;
        }

        return TryReadIntNode(node, out index);
    }

    private async Task RefreshOverlayStatusTextAsync(CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(_overlaySharedState.StatusMessage))
        {
            OverlayStatusText = _overlaySharedState.StatusMessage;
            return;
        }

        var snapshotResult = await Runtime.PlatformCapabilityService.GetSnapshotAsync(cancellationToken);
        if (snapshotResult.Success && snapshotResult.Value is not null)
        {
            OverlayStatusText = BuildCapabilityLine(PlatformCapabilityId.Overlay, snapshotResult.Value.Overlay);
            return;
        }

        OverlayStatusText = PlatformCapabilityTextMap.FormatSnapshotUnavailable(
            Texts.Language,
            snapshotResult.Message,
            _localizationFallbackReporter);
    }

    private void OnSessionStateChanged(SessionState state)
    {
        void Apply(SessionState changedState)
        {
            CurrentSessionState = changedState;
            OnPropertyChanged(nameof(IsRunOwnedByAnotherFeature));
            OnPropertyChanged(nameof(IsOwnRunActive));
            if (!IsOwnRunActive)
            {
                _isRunButtonStopHovered = false;
                OnPropertyChanged(nameof(RunButtonText));
            }

            if (changedState is SessionState.Running or SessionState.Stopping)
            {
                return;
            }

            if (changedState == SessionState.Connecting && _isStartRequestActive)
            {
                return;
            }

            _roguelikeInCombat = false;
            IsWaitingForStop = false;
            SyncStoppedUiStateIfSessionNotActive();
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Apply(state);
            return;
        }

        Dispatcher.UIThread.Post(() => Apply(state));
    }
}
