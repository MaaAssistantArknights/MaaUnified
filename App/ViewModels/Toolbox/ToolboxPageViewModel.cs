using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MAAUnified.App.Features.Dialogs;
using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.App.ViewModels.Settings;
using MAAUnified.Application.Models;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Localization;
using MAAUnified.CoreBridge;
using LegacyConfigurationKeys = MAAUnified.Compat.Constants.ConfigurationKeys;

namespace MAAUnified.App.ViewModels.Toolbox;

public sealed class ToolboxPageViewModel : PageViewModelBase
{
    private const string ToolboxRunOwner = "Toolbox";
    private const int MaxHistoryCount = 30;
    private const string ToolboxExecutionHistoryKey = "Toolbox.ExecutionHistory";
    private const string ToolboxHistoryLoadScope = "Toolbox.History.Load";
    private const string ToolboxHistorySaveScope = "Toolbox.History.Save";
    private const string ToolboxLegacyResultScope = "Toolbox.LegacyResult";
    private const string ToolboxGachaDisclaimerDialogScope = "Toolbox.Gacha.Disclaimer";
    private const string ToolboxBusyDialogScope = "Toolbox.Busy";
    private const string MiniGameSecretFrontTaskName = "MiniGame@SecretFront";
    private const string RecruitTaskChain = "Recruit";
    private const string DepotTaskChain = "Depot";
    private const string OperBoxTaskChain = "OperBox";
    private const int PeepPreviewDecodeWidth = 1280;
    private static readonly Random GachaTipRandom = new();
    private static readonly JsonSerializerOptions PersistedPayloadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly IReadOnlyDictionary<int, ToolboxToolKind> ToolByTabIndex = new Dictionary<int, ToolboxToolKind>
    {
        [0] = ToolboxToolKind.Recruit,
        [1] = ToolboxToolKind.OperBox,
        [2] = ToolboxToolKind.Depot,
        [3] = ToolboxToolKind.Gacha,
        [4] = ToolboxToolKind.VideoRecognition,
        [5] = ToolboxToolKind.MiniGame,
    };

    private static readonly HashSet<string> MiniGameSecretFrontEndings = new(StringComparer.Ordinal)
    {
        "A",
        "B",
        "C",
        "D",
        "E",
    };

    private static readonly HashSet<string> ExcludedDepotItemIds =
    [
        "3401",
        "3112",
        "3113",
        "3114",
        "4001",
        "4003",
        "4006",
        "5001",
    ];

    private static readonly string[] LocalizedBindingPropertyNames =
    [
        nameof(Texts),
        nameof(RootTexts),
        nameof(RecruitTabTitle),
        nameof(OperBoxTabTitle),
        nameof(DepotTabTitle),
        nameof(GachaTabTitle),
        nameof(PeepTabTitle),
        nameof(MiniGameTabTitle),
        nameof(RecruitAutoSetTimeText),
        nameof(RecruitShowPotentialText),
        nameof(RecruitAutoSelect3Text),
        nameof(RecruitAutoSelect4Text),
        nameof(RecruitAutoSelect5Text),
        nameof(RecruitAutoSelect6Text),
        nameof(RecruitFixedSixStarText),
        nameof(StartRecognitionText),
        nameof(RecruitPotentialTip),
        nameof(OperBoxCopyToClipboardText),
        nameof(OperBoxNotHaveHeader),
        nameof(OperBoxHaveHeader),
        nameof(LastOperBoxSyncTimeText),
        nameof(LastOperBoxSyncDisplayText),
        nameof(DepotExportArkPlannerText),
        nameof(DepotExportLoliconText),
        nameof(LastDepotSyncTimeText),
        nameof(LastDepotSyncDisplayText),
        nameof(GachaDisclaimerLeadText),
        nameof(GachaDisclaimerEmphasisText),
        nameof(GachaDisclaimerBodyText),
        nameof(GachaDisclaimerAcknowledgeText),
        nameof(GachaDisclaimerNoMoreText),
        nameof(GachaDrawOnceText),
        nameof(GachaDrawTenText),
        nameof(GachaWarningText),
        nameof(DialogLanguage),
        nameof(PeepTip),
        nameof(PeepCommandText),
        nameof(PeepTargetFpsText),
        nameof(ShowPeepTip),
        nameof(MiniGameNameText),
        nameof(MiniGameEndingText),
        nameof(MiniGameEventPriorityText),
        nameof(MiniGameCommandText),
        nameof(ExecutionReviewTitle),
        nameof(ExecutionReviewResultLabelText),
        nameof(ExecutionReviewParametersLabelText),
        nameof(ExecutionReviewHistoryLabelText),
        nameof(OperBoxExportText),
        nameof(ArkPlannerResult),
        nameof(LoliconResult),
    ];

    private const double OperBoxPanelItemWidth = 148d;
    private const double DepotPanelItemWidth = 166d;

    private readonly ConnectionGameSharedStateViewModel? _connectionState;
    private readonly IAppDialogService _dialogService;
    private readonly DispatcherTimer _gachaTipTimer;
    private ToolboxLocalizationTextMap _texts;
    private RootLocalizationTextMap _rootTexts;
    private readonly ConcurrentQueue<SessionCallbackEnvelope> _pendingSessionCallbacks = new();
    private readonly Dictionary<string, int> _operBoxPotential = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ToolboxOwnedOperatorState> _operBoxOwnedById = new(StringComparer.Ordinal);
    private bool _operBoxListsMaterialized;
    private ToolboxToolKind? _activeTool;
    private string _lastDispatchedParameterSummary = string.Empty;
    private string _currentLanguage = UiLanguageCatalog.DefaultLanguage;
    private JsonArray? _lastRecruitResult;
    private readonly IUiLanguageCoordinator _uiLanguageCoordinator;
    private CancellationTokenSource? _peepPollingCts;
    private Task? _peepPollingTask;
    private bool _peepWasAutoStarted;
    private int _callbackDrainScheduled;
    private DateTimeOffset _lastPeepFpsWindowStartedAt = DateTimeOffset.MinValue;
    private int _peepFramesInWindow;
    private int _selectedTabIndex;
    private string _resultText = string.Empty;
    private bool _disclaimerAccepted;
    private string _currentToolParameters = string.Empty;
    private ToolboxExecutionState _executionState;
    private string _lastExecutionErrorCode = string.Empty;
    private DateTimeOffset? _lastExecutionAt;

    private string _recruitInfo = string.Empty;
    private bool _chooseLevel3 = true;
    private bool _chooseLevel4 = true;
    private bool _chooseLevel5 = true;
    private bool _chooseLevel6 = true;
    private int _recruitLevel3Time = 540;
    private int _recruitLevel4Time = 540;
    private int _recruitLevel5Time = 540;
    private bool _recruitAutoSetTime = true;
    private bool _recruitmentShowPotential = true;

    private string _operBoxInfo = string.Empty;
    private int _operBoxSelectedIndex;
    private string _operBoxMode = "owned";
    private DateTimeOffset? _lastOperBoxSyncTime;

    private string _depotInfo = string.Empty;
    private DateTimeOffset? _lastDepotSyncTime;
    private string _depotFormat = "summary";
    private string _depotTopNInput = "50";

    private string _gachaInfo = string.Empty;
    private bool _gachaShowDisclaimer = true;
    private bool _gachaShowDisclaimerNoMore;
    private bool _isGachaInProgress;
    private string _gachaDrawCountInput = "10";

    private bool _peeping;
    private bool _isPeepTransitioning;
    private Bitmap? _peepImage;
    private double _peepScreenFps;
    private int _peepTargetFps = 20;

    private string _miniGameTaskName = "SS@Store@Begin";
    private string _miniGameSecretFrontEnding = "A";
    private string _miniGameSecretFrontEvent = string.Empty;
    private string _miniGameTip = string.Empty;
    private readonly List<ToolboxNamedOption> _miniGameSecretFrontEventOptions = [];

    public ToolboxPageViewModel(
        MAAUnifiedRuntime runtime,
        ConnectionGameSharedStateViewModel? connectionState = null,
        IAppDialogService? dialogService = null)
        : base(runtime)
    {
        _connectionState = connectionState;
        _dialogService = dialogService ?? NoOpAppDialogService.Instance;
        if (_connectionState is not null)
        {
            _connectionState.PropertyChanged += OnConnectionStatePropertyChanged;
        }
        _uiLanguageCoordinator = runtime.UiLanguageCoordinator;
        _uiLanguageCoordinator.LanguageChanged += OnUnifiedLanguageChanged;
        _currentLanguage = ResolveCurrentLanguage();
        _texts = CreateTexts(_currentLanguage);
        _rootTexts = CreateRootTexts(_currentLanguage);

        Tabs =
        [
            "recruit",
            "operbox",
            "depot",
            "gacha",
            "peep",
            "minigame",
        ];

        _resultText = T("Toolbox.Status.WaitingForExecution", "Waiting to execute tool.");
        _recruitInfo = T("Toolbox.Tip.RecruitRecognition", "Tip: this feature is independent from the main-page auto recruit flow.");
        _operBoxInfo = T("Toolbox.Tip.OperBoxRecognition", "Special markers may affect recognition accuracy.");
        _depotInfo = T("Toolbox.Tip.DepotRecognition", "This feature is experimental. Please verify recognition results.");
        _gachaInfo = T("Toolbox.Tip.GachaInit", "Gacha hint.");
        _miniGameTip = T("Toolbox.Tip.MiniGameNameEmpty", "Select a mini-game above to start.");

        ExecutionHistory = new ObservableCollection<ToolExecutionRecord>();
        ExecutionHistory.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasExecutionHistory));
        RecruitResultLines = new ObservableCollection<RecruitResultLineViewModel>();
        OperBoxHaveList = new BatchedObservableCollection<OperBoxOperatorItemViewModel>();
        OperBoxNotHaveList = new BatchedObservableCollection<OperBoxOperatorItemViewModel>();
        DepotResult = new ObservableCollection<DepotItemViewModel>();
        MiniGameTaskList = new ObservableCollection<ToolboxMiniGameEntry>();
        RebuildMiniGameSecretFrontEventOptions();
        MiniGameSecretFrontEndingOptions =
        [
            "A",
            "B",
            "C",
            "D",
            "E",
        ];

        _gachaTipTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _gachaTipTimer.Tick += (_, _) => RefreshGachaTip();

        RecruitResultLines.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRecruitResults));
        OperBoxHaveList.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(OperBoxHaveHeader));
            OnPropertyChanged(nameof(OperBoxHavePanelWidth));
        };
        OperBoxNotHaveList.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(OperBoxNotHaveHeader));
            OnPropertyChanged(nameof(OperBoxNotHavePanelWidth));
        };
        DepotResult.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasDepotResult));
            OnPropertyChanged(nameof(DepotPanelWidth));
            OnPropertyChanged(nameof(ArkPlannerResult));
            OnPropertyChanged(nameof(LoliconResult));
        };

        runtime.SessionService.CallbackProjected += OnSessionCallbackProjected;
        RefreshCurrentToolParametersPreview();
    }

    public IReadOnlyList<string> Tabs { get; }

    public ObservableCollection<ToolExecutionRecord> ExecutionHistory { get; }

    public ToolboxLocalizationTextMap Texts => _texts;

    public RootLocalizationTextMap RootTexts => _rootTexts;

    public string RecruitTabTitle => T("Toolbox.Tab.Recruit", "Recruit Recognition");

    public string OperBoxTabTitle => T("Toolbox.Tab.OperBox", "Operator Recognition");

    public string DepotTabTitle => T("Toolbox.Tab.Depot", "Depot Recognition");

    public string GachaTabTitle => T("Toolbox.Tab.Gacha", "Gacha");

    public string PeepTabTitle => T("Toolbox.Tab.Peep", "Peep");

    public string MiniGameTabTitle => T("Toolbox.Tab.MiniGame", "Mini-Game");

    public string RecruitAutoSetTimeText => T("Toolbox.Recruit.AutoSetTime", "Auto set time");

    public string RecruitShowPotentialText => T("Toolbox.Recruit.ShowPotential", "Show operator potential (4/5/6★ tags)");

    public string RecruitAutoSelect3Text => T("Toolbox.Recruit.AutoSelect3", "Auto select 3★ tags");

    public string RecruitAutoSelect4Text => T("Toolbox.Recruit.AutoSelect4", "Auto select 4★ tags");

    public string RecruitAutoSelect5Text => T("Toolbox.Recruit.AutoSelect5", "Auto select 5★ tags");

    public string RecruitAutoSelect6Text => T("Toolbox.Recruit.AutoSelect6", "Auto select 6★ tags");

    public string RecruitFixedSixStarText => T("Toolbox.Recruit.FixedSixStar", "Fixed 09:00 (guaranteed 6★)");

    public string StartRecognitionText => T("Toolbox.Action.StartRecognition", "Start recognition");

    public string OperBoxCopyToClipboardText => T("Toolbox.OperBox.CopyToClipboard", "Copy to clipboard");

    public string DepotExportArkPlannerText => T("Toolbox.Depot.ExportArkPlanner", "Export to Penguin Stats planner");

    public string DepotExportLoliconText => T("Toolbox.Depot.ExportLolicon", "Export to Arknights Toolbox");

    public string GachaDisclaimerLeadText => T("Toolbox.Gacha.Disclaimer.Lead", "Please note, this is");

    public string GachaDisclaimerEmphasisText => T("Toolbox.Gacha.Disclaimer.Emphasis", "REAL GACHA");

    public string GachaDisclaimerBodyText => T(
        "Toolbox.Gacha.Disclaimer.Body",
        "The gacha tool directly operates the current client. Make sure this is not your main account and the emulator is already on the gacha screen.");

    public string GachaDisclaimerAcknowledgeText => T("Toolbox.Gacha.Disclaimer.Acknowledge", "I understand");

    public string GachaDisclaimerNoMoreText => T("Toolbox.Gacha.Disclaimer.NoMore", "Don't show again");

    public string GachaDrawOnceText => T("Toolbox.Gacha.DrawOnce", "Draw once");

    public string GachaDrawTenText => T("Toolbox.Gacha.DrawTen", "Draw ten");

    public string PeepTargetFpsText => T("Toolbox.Peep.TargetFps", "Target FPS");

    public string MiniGameNameText => T("Toolbox.MiniGame.Name", "Mini-game");

    public string MiniGameEndingText => T("Toolbox.MiniGame.Ending", "Ending");

    public string MiniGameEventPriorityText => T("Toolbox.MiniGame.EventPriority", "Preferred event chain");

    public string ExecutionReviewTitle => T("Toolbox.Section.ExecutionReview", "Execution Review");

    public string ExecutionReviewResultLabelText => T("Toolbox.ExecutionReview.ResultLabel", "Latest Result");

    public string ExecutionReviewParametersLabelText => T("Toolbox.ExecutionReview.ParametersLabel", "Parameters");

    public string ExecutionReviewHistoryLabelText => T("Toolbox.ExecutionReview.HistoryLabel", "History");

    public ObservableCollection<RecruitResultLineViewModel> RecruitResultLines { get; }

    public ObservableCollection<OperBoxOperatorItemViewModel> OperBoxHaveList { get; }

    public ObservableCollection<OperBoxOperatorItemViewModel> OperBoxNotHaveList { get; }

    public ObservableCollection<DepotItemViewModel> DepotResult { get; }

    public ObservableCollection<ToolboxMiniGameEntry> MiniGameTaskList { get; }

    public IReadOnlyList<ToolboxNamedOption> MiniGameSecretFrontEventOptions => _miniGameSecretFrontEventOptions;

    public IReadOnlyList<string> MiniGameSecretFrontEndingOptions { get; }

    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            var normalized = Math.Clamp(value, 0, Tabs.Count - 1);
            if (SetProperty(ref _selectedTabIndex, normalized))
            {
                if (normalized == 1)
                {
                    Dispatcher.UIThread.Post(EnsureOperBoxListsMaterialized, DispatcherPriority.Background);
                }

                RefreshCurrentToolParametersPreview();
                OnPropertyChanged(nameof(IsGachaTabSelected));
            }
        }
    }

    public string ResultText
    {
        get => _resultText;
        set => SetProperty(ref _resultText, value);
    }

    public bool DisclaimerAccepted
    {
        get => _disclaimerAccepted;
        set => SetProperty(ref _disclaimerAccepted, value);
    }

    public ToolboxExecutionState ExecutionState
    {
        get => _executionState;
        private set
        {
            if (SetProperty(ref _executionState, value))
            {
                OnPropertyChanged(nameof(IsExecuting));
            }
        }
    }

    public string CurrentToolParameters
    {
        get => _currentToolParameters;
        private set => SetProperty(ref _currentToolParameters, value ?? string.Empty);
    }

    public bool IsExecuting => ExecutionState == ToolboxExecutionState.Executing;

    public bool HasExecutionHistory => ExecutionHistory.Count > 0;

    public bool IsToolboxBusy => _activeTool is not null || Peeping || IsPeepTransitioning || IsGachaInProgress;

    public string LastExecutionErrorCode
    {
        get => _lastExecutionErrorCode;
        private set => SetProperty(ref _lastExecutionErrorCode, value ?? string.Empty);
    }

    public DateTimeOffset? LastExecutionAt
    {
        get => _lastExecutionAt;
        private set => SetProperty(ref _lastExecutionAt, value);
    }

    public bool HasRecruitResults => RecruitResultLines.Count > 0;

    public bool ShowRecruitAutoSetTimeControls => RecruitAutoSetTime;

    public string RecruitPotentialTip => T("Toolbox.Tip.RecruitPotential", "Use Operator Recognition first to load operator data.");

    public string RecruitInfo
    {
        get => _recruitInfo;
        set => SetProperty(ref _recruitInfo, value);
    }

    public bool ChooseLevel3
    {
        get => _chooseLevel3;
        set => SetTrackedProperty(ref _chooseLevel3, value);
    }

    public bool ChooseLevel4
    {
        get => _chooseLevel4;
        set => SetTrackedProperty(ref _chooseLevel4, value);
    }

    public bool ChooseLevel5
    {
        get => _chooseLevel5;
        set => SetTrackedProperty(ref _chooseLevel5, value);
    }

    public bool ChooseLevel6
    {
        get => _chooseLevel6;
        set => SetTrackedProperty(ref _chooseLevel6, value);
    }

    public int RecruitLevel3Time
    {
        get => _recruitLevel3Time;
        set
        {
            var normalized = NormalizeRecruitMinutes(value);
            if (SetTrackedProperty(ref _recruitLevel3Time, normalized))
            {
                OnPropertyChanged(nameof(RecruitLevel3Hour));
                OnPropertyChanged(nameof(RecruitLevel3Minute));
                OnPropertyChanged(nameof(RecruitLevel3TimeInput));
            }
        }
    }

    public int RecruitLevel4Time
    {
        get => _recruitLevel4Time;
        set
        {
            var normalized = NormalizeRecruitMinutes(value);
            if (SetTrackedProperty(ref _recruitLevel4Time, normalized))
            {
                OnPropertyChanged(nameof(RecruitLevel4Hour));
                OnPropertyChanged(nameof(RecruitLevel4Minute));
                OnPropertyChanged(nameof(RecruitLevel4TimeInput));
            }
        }
    }

    public int RecruitLevel5Time
    {
        get => _recruitLevel5Time;
        set
        {
            var normalized = NormalizeRecruitMinutes(value);
            if (SetTrackedProperty(ref _recruitLevel5Time, normalized))
            {
                OnPropertyChanged(nameof(RecruitLevel5Hour));
                OnPropertyChanged(nameof(RecruitLevel5Minute));
                OnPropertyChanged(nameof(RecruitLevel5TimeInput));
            }
        }
    }

    public int RecruitLevel3Hour
    {
        get => RecruitLevel3Time / 60;
        set => RecruitLevel3Time = (ClampRecruitHour(value, RecruitLevel3Minute) * 60) + RecruitLevel3Minute;
    }

    public int RecruitLevel3Minute
    {
        get => RecruitLevel3Time % 60;
        set => RecruitLevel3Time = (RecruitLevel3Hour * 60) + NormalizeRecruitMinutePart(value);
    }

    public int RecruitLevel4Hour
    {
        get => RecruitLevel4Time / 60;
        set => RecruitLevel4Time = (ClampRecruitHour(value, RecruitLevel4Minute) * 60) + RecruitLevel4Minute;
    }

    public int RecruitLevel4Minute
    {
        get => RecruitLevel4Time % 60;
        set => RecruitLevel4Time = (RecruitLevel4Hour * 60) + NormalizeRecruitMinutePart(value);
    }

    public int RecruitLevel5Hour
    {
        get => RecruitLevel5Time / 60;
        set => RecruitLevel5Time = (ClampRecruitHour(value, RecruitLevel5Minute) * 60) + RecruitLevel5Minute;
    }

    public int RecruitLevel5Minute
    {
        get => RecruitLevel5Time % 60;
        set => RecruitLevel5Time = (RecruitLevel5Hour * 60) + NormalizeRecruitMinutePart(value);
    }

    public string RecruitLevel3TimeInput
    {
        get => RecruitLevel3Time.ToString(CultureInfo.InvariantCulture);
        set
        {
            if (TryParseRecruitMinutes(value, out var minutes))
            {
                RecruitLevel3Time = minutes;
            }
        }
    }

    public string RecruitLevel4TimeInput
    {
        get => RecruitLevel4Time.ToString(CultureInfo.InvariantCulture);
        set
        {
            if (TryParseRecruitMinutes(value, out var minutes))
            {
                RecruitLevel4Time = minutes;
            }
        }
    }

    public string RecruitLevel5TimeInput
    {
        get => RecruitLevel5Time.ToString(CultureInfo.InvariantCulture);
        set
        {
            if (TryParseRecruitMinutes(value, out var minutes))
            {
                RecruitLevel5Time = minutes;
            }
        }
    }

    public bool RecruitAutoSetTime
    {
        get => _recruitAutoSetTime;
        set
        {
            if (SetTrackedProperty(ref _recruitAutoSetTime, value))
            {
                OnPropertyChanged(nameof(ShowRecruitAutoSetTimeControls));
            }
        }
    }

    public bool RecruitmentShowPotential
    {
        get => _recruitmentShowPotential;
        set => SetTrackedProperty(ref _recruitmentShowPotential, value);
    }

    public string OperBoxInfo
    {
        get => _operBoxInfo;
        set => SetProperty(ref _operBoxInfo, value);
    }

    public int OperBoxSelectedIndex
    {
        get => _operBoxSelectedIndex;
        set => SetProperty(ref _operBoxSelectedIndex, Math.Clamp(value, 0, 1));
    }

    public string OperBoxNotHaveHeader => string.Format(
        CultureInfo.InvariantCulture,
        T("Toolbox.OperBox.Header.NotOwned", "Not owned ({0})"),
        OperBoxNotHaveList.Count);

    public string OperBoxHaveHeader => string.Format(
        CultureInfo.InvariantCulture,
        T("Toolbox.OperBox.Header.Owned", "Owned ({0})"),
        OperBoxHaveList.Count);

    public double OperBoxNotHavePanelWidth => ResolvePanelWidth(OperBoxNotHaveList.Count, OperBoxPanelItemWidth);

    public double OperBoxHavePanelWidth => ResolvePanelWidth(OperBoxHaveList.Count, OperBoxPanelItemWidth);

    public string OperBoxMode
    {
        get => _operBoxMode;
        set => SetTrackedProperty(ref _operBoxMode, string.IsNullOrWhiteSpace(value) ? "owned" : value.Trim());
    }

    public DateTimeOffset? LastOperBoxSyncTime
    {
        get => _lastOperBoxSyncTime;
        private set
        {
            if (SetProperty(ref _lastOperBoxSyncTime, value))
            {
                OnPropertyChanged(nameof(LastOperBoxSyncTimeText));
                OnPropertyChanged(nameof(HasLastOperBoxSyncTime));
                OnPropertyChanged(nameof(LastOperBoxSyncDisplayText));
            }
        }
    }

    public string LastOperBoxSyncTimeText
    {
        get
        {
            if (LastOperBoxSyncTime is null)
            {
                return T("Toolbox.Depot.NeverSynced", "Not synced yet");
            }

            return LastOperBoxSyncTime.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
    }

    public bool HasLastOperBoxSyncTime => LastOperBoxSyncTime is not null;

    public string LastOperBoxSyncDisplayText => HasLastOperBoxSyncTime
        ? string.Format(
            CultureInfo.InvariantCulture,
            T("Toolbox.Depot.LastSync", "Last sync: {0}"),
            LastOperBoxSyncTimeText)
        : string.Empty;

    public string DepotInfo
    {
        get => _depotInfo;
        set => SetProperty(ref _depotInfo, value);
    }

    public DateTimeOffset? LastDepotSyncTime
    {
        get => _lastDepotSyncTime;
        private set
        {
            if (SetProperty(ref _lastDepotSyncTime, value))
            {
                OnPropertyChanged(nameof(LastDepotSyncTimeText));
                OnPropertyChanged(nameof(HasLastDepotSyncTime));
                OnPropertyChanged(nameof(LastDepotSyncDisplayText));
            }
        }
    }

    public string LastDepotSyncTimeText
    {
        get
        {
            if (LastDepotSyncTime is null)
            {
                return T("Toolbox.Depot.NeverSynced", "Not synced yet");
            }

            return LastDepotSyncTime.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
    }

    public bool HasLastDepotSyncTime => LastDepotSyncTime is not null;

    public bool HasDepotResult => DepotResult.Count > 0;

    public string LastDepotSyncDisplayText => HasLastDepotSyncTime
        ? string.Format(
            CultureInfo.InvariantCulture,
            T("Toolbox.Depot.LastSync", "Last sync: {0}"),
            LastDepotSyncTimeText)
        : string.Empty;

    public double DepotPanelWidth => ResolvePanelWidth(DepotResult.Count, DepotPanelItemWidth);

    public string DepotFormat
    {
        get => _depotFormat;
        set => SetTrackedProperty(ref _depotFormat, string.IsNullOrWhiteSpace(value) ? "summary" : value.Trim());
    }

    public string DepotTopNInput
    {
        get => _depotTopNInput;
        set => SetTrackedProperty(ref _depotTopNInput, string.IsNullOrWhiteSpace(value) ? "50" : value.Trim());
    }

    public string GachaInfo
    {
        get => _gachaInfo;
        set => SetProperty(ref _gachaInfo, value);
    }

    public bool GachaShowDisclaimer
    {
        get => _gachaShowDisclaimer;
        set
        {
            if (SetProperty(ref _gachaShowDisclaimer, value))
            {
                OnPropertyChanged(nameof(ShowGachaControls));
                OnPropertyChanged(nameof(ShowGachaPreview));
            }
        }
    }

    public bool GachaShowDisclaimerNoMore
    {
        get => _gachaShowDisclaimerNoMore;
        set => SetTrackedProperty(ref _gachaShowDisclaimerNoMore, value);
    }

    public bool IsGachaInProgress
    {
        get => _isGachaInProgress;
        private set
        {
            if (!SetProperty(ref _isGachaInProgress, value))
            {
                return;
            }

            OnPropertyChanged(nameof(PeepCommandText));
            OnPropertyChanged(nameof(ShowGachaPreview));
            if (!value)
            {
                GachaInfo = T("Toolbox.Tip.GachaInit", "Gacha hint.");
            }
        }
    }

    public bool ShowGachaControls => !GachaShowDisclaimer;

    public string GachaWarningText => T("Toolbox.Warning.GachaMessage", "This feature is risky and may cause unintended pulls.");

    public string DialogLanguage => _currentLanguage;

    public bool ShowGachaPreview => ShowGachaControls && Peeping && PeepImage is not null;

    public string GachaDrawCountInput
    {
        get => _gachaDrawCountInput;
        set => SetTrackedProperty(ref _gachaDrawCountInput, string.IsNullOrWhiteSpace(value) ? "10" : value.Trim());
    }

    public bool IsGachaTabSelected => SelectedTabIndex == 3;

    public bool Peeping
    {
        get => _peeping;
        private set
        {
            if (!SetProperty(ref _peeping, value))
            {
                return;
            }

            OnPropertyChanged(nameof(PeepCommandText));
            OnPropertyChanged(nameof(ShowPeepTip));
            OnPropertyChanged(nameof(ShowPeepPreview));
            OnPropertyChanged(nameof(ShowGachaPreview));
        }
    }

    public bool IsPeepTransitioning
    {
        get => _isPeepTransitioning;
        private set
        {
            if (SetProperty(ref _isPeepTransitioning, value))
            {
                OnPropertyChanged(nameof(CanTogglePeep));
            }
        }
    }

    public Bitmap? PeepImage
    {
        get => _peepImage;
        private set
        {
            if (ReferenceEquals(_peepImage, value))
            {
                return;
            }

            var previous = _peepImage;
            if (SetProperty(ref _peepImage, value))
            {
                OnPropertyChanged(nameof(ShowPeepPreview));
                OnPropertyChanged(nameof(ShowGachaPreview));
                previous?.Dispose();
            }
        }
    }

    public double PeepScreenFps
    {
        get => _peepScreenFps;
        private set => SetProperty(ref _peepScreenFps, value);
    }

    public int PeepTargetFps
    {
        get => _peepTargetFps;
        set
        {
            var normalized = Math.Clamp(value, 1, 60);
            if (!SetTrackedProperty(ref _peepTargetFps, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(VideoRecognitionTargetFpsInput));
            UpdatePeepTimerInterval();
        }
    }

    public string VideoRecognitionTargetFpsInput
    {
        get => PeepTargetFps.ToString(CultureInfo.InvariantCulture);
        set
        {
            if (int.TryParse(NormalizeToken(value), out var parsed))
            {
                PeepTargetFps = parsed;
            }
        }
    }

    public string PeepTip => T("Toolbox.Tip.Peep", "Peek through MAA's eyes?");

    public bool ShowPeepTip => !Peeping;

    public bool ShowPeepPreview => Peeping && PeepImage is not null;

    public bool CanTogglePeep => !IsPeepTransitioning;

    public string PeepCommandText => !Peeping
        ? T("Toolbox.Action.PeepStart", "Peep!")
        : IsGachaInProgress
            ? T("Toolbox.Action.PeepStopStrong", "Stop!!!!!")
            : T("Toolbox.Action.PeepStop", "Stop!");

    public string MiniGameTaskName
    {
        get => _miniGameTaskName;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "SS@Store@Begin" : value.Trim();
            if (!SetTrackedProperty(ref _miniGameTaskName, normalized))
            {
                return;
            }

            UpdateMiniGameSelectionFromTaskName();
            MiniGameTip = ResolveMiniGameTip(normalized);
            OnPropertyChanged(nameof(IsMiniGameSecretFront));
        }
    }

    public string MiniGameSecretFrontEnding
    {
        get => _miniGameSecretFrontEnding;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "A" : value.Trim();
            if (SetTrackedProperty(ref _miniGameSecretFrontEnding, normalized))
            {
                RefreshCurrentToolParametersPreview();
            }
        }
    }

    public string MiniGameSecretFrontEvent
    {
        get => _miniGameSecretFrontEvent;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (!SetTrackedProperty(ref _miniGameSecretFrontEvent, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedMiniGameSecretFrontEventOption));
        }
    }

    public ToolboxMiniGameEntry? SelectedMiniGameEntry
    {
        get => MiniGameTaskList.FirstOrDefault(entry => string.Equals(entry.Value, MiniGameTaskName, StringComparison.Ordinal));
        set
        {
            if (value is null)
            {
                return;
            }

            MiniGameTaskName = value.Value;
        }
    }

    public ToolboxNamedOption? SelectedMiniGameSecretFrontEventOption
    {
        get => MiniGameSecretFrontEventOptions.FirstOrDefault(option => string.Equals(option.Value, MiniGameSecretFrontEvent, StringComparison.Ordinal))
            ?? MiniGameSecretFrontEventOptions[0];
        set
        {
            if (value is null)
            {
                return;
            }

            MiniGameSecretFrontEvent = value.Value;
        }
    }

    public string MiniGameTip
    {
        get => _miniGameTip;
        private set => SetProperty(ref _miniGameTip, value);
    }

    public bool IsMiniGameSecretFront => string.Equals(MiniGameTaskName, MiniGameSecretFrontTaskName, StringComparison.Ordinal);

    public bool IsMiniGameRunning => _activeTool == ToolboxToolKind.MiniGame;

    public string MiniGameCommandText => IsMiniGameRunning
        ? T("Toolbox.Action.PeepStop", "Stop!")
        : T("Toolbox.Action.LinkStart", "Link Start!");

    public string OperBoxExportText => BuildOperBoxExportText();

    public string ArkPlannerResult => BuildArkPlannerExportText();

    public string LoliconResult => BuildLoliconExportText();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LoadBridgeSettings();
        LoadMiniGameEntries();
        LoadOperBoxDetails();
        LoadDepotDetails();
        if (SelectedTabIndex == 1)
        {
            EnsureOperBoxListsMaterialized();
        }

        await LoadExecutionHistoryAsync(cancellationToken);
        RefreshCurrentToolParametersPreview();
        await Runtime.DiagnosticsService.RecordEventAsync("Toolbox", "Toolbox page initialized.", cancellationToken);
    }

    public void ApplySuccessPresetForCurrentTool()
    {
        if (!TryResolveTool(SelectedTabIndex, out var tool))
        {
            return;
        }

        switch (tool)
        {
            case ToolboxToolKind.Recruit:
                ChooseLevel3 = true;
                ChooseLevel4 = true;
                ChooseLevel5 = true;
                ChooseLevel6 = true;
                RecruitLevel3Time = 540;
                RecruitLevel4Time = 540;
                RecruitLevel5Time = 540;
                RecruitAutoSetTime = true;
                break;
            case ToolboxToolKind.OperBox:
                OperBoxMode = "owned";
                break;
            case ToolboxToolKind.Depot:
                DepotFormat = "summary";
                DepotTopNInput = "50";
                break;
            case ToolboxToolKind.Gacha:
                GachaDrawCountInput = "10";
                break;
            case ToolboxToolKind.VideoRecognition:
                PeepTargetFps = 20;
                break;
            case ToolboxToolKind.MiniGame:
                MiniGameTaskName = "SS@Store@Begin";
                MiniGameSecretFrontEnding = "A";
                MiniGameSecretFrontEvent = string.Empty;
                break;
        }

        RefreshCurrentToolParametersPreview();
    }

    public void ApplyFailurePresetForCurrentTool()
    {
        if (!TryResolveTool(SelectedTabIndex, out var tool))
        {
            return;
        }

        switch (tool)
        {
            case ToolboxToolKind.Recruit:
                ChooseLevel3 = false;
                ChooseLevel4 = false;
                ChooseLevel5 = false;
                ChooseLevel6 = false;
                RecruitLevel3Time = 55;
                break;
            case ToolboxToolKind.OperBox:
                OperBoxMode = "invalid";
                break;
            case ToolboxToolKind.Depot:
                DepotTopNInput = "0";
                break;
            case ToolboxToolKind.Gacha:
                GachaDrawCountInput = "3";
                break;
            case ToolboxToolKind.VideoRecognition:
                PeepTargetFps = 0;
                break;
            case ToolboxToolKind.MiniGame:
                MiniGameTaskName = MiniGameSecretFrontTaskName;
                MiniGameSecretFrontEnding = string.Empty;
                break;
        }

        RefreshCurrentToolParametersPreview();
    }

    public async Task ExecuteCurrentToolAsync(CancellationToken cancellationToken = default)
    {
        if (!TryResolveTool(SelectedTabIndex, out var tool))
        {
            await ApplyFailureAsync(
                null,
                UiOperationResult.Fail(UiErrorCode.ToolNotSupported, $"Tool tab index `{SelectedTabIndex}` is not supported."),
                "resolve",
                cancellationToken);
            return;
        }

        switch (tool)
        {
            case ToolboxToolKind.Recruit:
                await StartRecruitAsync(cancellationToken);
                break;
            case ToolboxToolKind.OperBox:
                await StartOperBoxAsync(cancellationToken);
                break;
            case ToolboxToolKind.Depot:
                await StartDepotAsync(cancellationToken);
                break;
            case ToolboxToolKind.Gacha:
            {
                var once = string.Equals(NormalizeToken(GachaDrawCountInput), "1", StringComparison.Ordinal);
                await StartGachaAsync(once, cancellationToken);
                break;
            }
            case ToolboxToolKind.VideoRecognition:
                await TogglePeepAsync(cancellationToken);
                break;
            case ToolboxToolKind.MiniGame:
                await StartMiniGameAsync(cancellationToken);
                break;
        }
    }

    public async Task StartRecruitAsync(CancellationToken cancellationToken = default)
    {
        var request = BuildRecruitRequest();
        await DispatchToolAsync(ToolboxToolKind.Recruit, request, PrepareRecruitForStart, cancellationToken);
    }

    public async Task StartOperBoxAsync(CancellationToken cancellationToken = default)
    {
        var request = new ToolboxDispatchRequest(
            ToolboxToolKind.OperBox,
            ParameterSummary: BuildCurrentParameterText(ToolboxToolKind.OperBox));
        await DispatchToolAsync(ToolboxToolKind.OperBox, request, PrepareOperBoxForStart, cancellationToken);
    }

    public async Task StartDepotAsync(CancellationToken cancellationToken = default)
    {
        ClearDepotRecognitionResults();
        DepotInfo = T("Toolbox.Status.ConnectingEmulator", "Connecting to emulator...");
        var request = new ToolboxDispatchRequest(
            ToolboxToolKind.Depot,
            ParameterSummary: BuildCurrentParameterText(ToolboxToolKind.Depot));
        await DispatchToolAsync(ToolboxToolKind.Depot, request, PrepareDepotForStart, cancellationToken);
    }

    public async Task StartGachaAsync(bool once = true, CancellationToken cancellationToken = default)
    {
        if (GachaShowDisclaimer)
        {
            await ApplyFailureAsync(
                ToolboxToolKind.Gacha,
                UiOperationResult.Fail(
                    UiErrorCode.ToolboxDisclaimerNotAccepted,
                    T("Toolbox.Error.GachaDisclaimerRequired", "Please accept the gacha risk disclaimer first.")),
                "gacha-disclaimer",
                cancellationToken);
            return;
        }

        GachaDrawCountInput = once ? "1" : "10";
        var request = new ToolboxDispatchRequest(
            ToolboxToolKind.Gacha,
            Gacha: new ToolboxGachaRequest(once),
            ParameterSummary: BuildCurrentParameterText(ToolboxToolKind.Gacha));
        await DispatchToolAsync(ToolboxToolKind.Gacha, request, PrepareGachaForStart, cancellationToken, startPeepAfterDispatch: true);
    }

    public async Task<bool> ConfirmGachaDisclaimerAsync(CancellationToken cancellationToken = default)
    {
        var chrome = CreateGachaDisclaimerDialogChrome(DialogLanguage);
        var chromeSnapshot = chrome.GetSnapshot(DialogLanguage);
        var request = new WarningConfirmDialogRequest(
            Title: chromeSnapshot.Title,
            Message: chromeSnapshot.GetNamedTextOrDefault(DialogTextCatalog.ChromeKeys.Prompt, GachaWarningText),
            ConfirmText: chromeSnapshot.ConfirmText ?? DialogTextCatalog.WarningDialogConfirmButton(DialogLanguage),
            CancelText: chromeSnapshot.CancelText ?? DialogTextCatalog.WarningDialogCancelButton(DialogLanguage),
            Language: DialogLanguage,
            Chrome: chrome);
        var dialogResult = await _dialogService.ShowWarningConfirmAsync(
            request,
            ToolboxGachaDisclaimerDialogScope,
            cancellationToken);
        if (dialogResult.Return != DialogReturnSemantic.Confirm)
        {
            return false;
        }

        AgreeGachaDisclaimer();
        return true;
    }

    public async Task StartMiniGameAsync(CancellationToken cancellationToken = default)
    {
        var request = new ToolboxDispatchRequest(
            ToolboxToolKind.MiniGame,
            MiniGame: new ToolboxMiniGameRequest(GetMiniGameTask()),
            ParameterSummary: BuildCurrentParameterText(ToolboxToolKind.MiniGame));
        await DispatchToolAsync(ToolboxToolKind.MiniGame, request, PrepareMiniGameForStart, cancellationToken);
    }

    public async Task TogglePeepAsync(CancellationToken cancellationToken = default)
    {
        if (IsPeepTransitioning)
        {
            return;
        }

        IsPeepTransitioning = true;
        try
        {
            if (Peeping && _activeTool is null)
            {
                StopPeepPolling(clearImage: false, releaseRunOwner: true);
                ResultText = T("Toolbox.Status.PeepStopped", "Peep stopped.");
                ExecutionState = ToolboxExecutionState.Succeeded;
                LastExecutionErrorCode = string.Empty;
                LastExecutionAt = DateTimeOffset.Now;
                ExecutionHistory.Insert(0, ToolExecutionRecord.Succeeded(
                    T("Toolbox.ToolName.VideoRecognition", "Peep"),
                    BuildCurrentParameterText(ToolboxToolKind.VideoRecognition),
                    T("Toolbox.Status.PeepStopped", "Peep stopped.")));
                TrimExecutionHistory();
                await PersistExecutionHistoryAsync(cancellationToken);
                return;
            }

            if (_activeTool is not null && _activeTool != ToolboxToolKind.Gacha)
            {
                await ApplyFailureAsync(
                    ToolboxToolKind.VideoRecognition,
                    UiOperationResult.Fail(
                        UiErrorCode.ToolboxExecutionFailed,
                        string.Format(
                            CultureInfo.InvariantCulture,
                            T("Toolbox.Error.BusyWithTool", "`{0}` is running. Stop it first before peep."),
                            _activeTool)),
                    "peep-busy",
                    cancellationToken);
                return;
            }

            if (!Runtime.SessionService.TryBeginRun(ToolboxRunOwner, out var currentOwner))
            {
                await ApplyFailureAsync(
                    ToolboxToolKind.VideoRecognition,
                    UiOperationResult.Fail(
                        UiErrorCode.ToolboxExecutionFailed,
                        string.Format(
                            CultureInfo.InvariantCulture,
                            T("Toolbox.Error.OwnerRunning", "`{0}` is already running."),
                            currentOwner)),
                    "peep-owner",
                    cancellationToken);
                return;
            }

            var readyResult = await EnsureConnectedAsync(cancellationToken);
            if (!readyResult.Success)
            {
                Runtime.SessionService.EndRun(ToolboxRunOwner);
                await ApplyFailureAsync(ToolboxToolKind.VideoRecognition, readyResult, "peep-connect", cancellationToken);
                return;
            }

            await PersistBridgeSettingsForToolAsync(ToolboxToolKind.VideoRecognition, cancellationToken);
            _peepWasAutoStarted = false;
            _ = Runtime.AchievementTrackerService.Unlock("PeekScreen");
            StartPeepPolling();
            ResultText = T("Toolbox.Status.PeepStarted", "Peep started.");
            ExecutionState = ToolboxExecutionState.Succeeded;
            LastExecutionErrorCode = string.Empty;
            LastExecutionAt = DateTimeOffset.Now;
        }
        finally
        {
            IsPeepTransitioning = false;
        }
    }

    public async Task StopActiveToolAsync(CancellationToken cancellationToken = default)
    {
        if (_activeTool is null)
        {
            if (Peeping)
            {
                StopPeepPolling(clearImage: false, releaseRunOwner: true);
                ResultText = T("Toolbox.Status.PeepStopped", "Peep stopped.");
                ExecutionState = ToolboxExecutionState.Succeeded;
                LastExecutionErrorCode = string.Empty;
                LastExecutionAt = DateTimeOffset.Now;
            }

            return;
        }

        var activeTool = _activeTool.Value;
        var stopResult = await Runtime.ToolboxFeatureService.StopAsync(cancellationToken);
        if (!stopResult.Success)
        {
            await ApplyFailureAsync(activeTool, stopResult, "stop", cancellationToken);
            return;
        }

        CompleteActiveToolRun(
            activeTool,
            success: false,
            T("Toolbox.Status.CurrentToolStopped", "Current tool stopped."),
            UiErrorCode.ToolboxExecutionCancelled);
    }

    public void AgreeGachaDisclaimer()
    {
        _ = Runtime.AchievementTrackerService.Unlock("RealGacha");
        GachaShowDisclaimer = false;
        if (GachaShowDisclaimerNoMore)
        {
            _ = PersistBridgeSettingsForToolAsync(ToolboxToolKind.Gacha, CancellationToken.None);
        }

        GachaInfo = T("Toolbox.Tip.GachaInit", "Gacha hint.");
    }

    public void NotifyOperBoxExportCopied()
    {
        OperBoxInfo = T("Toolbox.Status.CopiedToClipboard", "Copied to clipboard");
    }

    public void NotifyDepotExportCopied(string target)
    {
        DepotInfo = T("Toolbox.Status.CopiedToClipboard", "Copied to clipboard");
    }

    public string GetMiniGameTask()
    {
        return MiniGameTaskName switch
        {
            MiniGameSecretFrontTaskName => $"{MiniGameTaskName}@Begin@Ending{MiniGameSecretFrontEnding}{(string.IsNullOrEmpty(MiniGameSecretFrontEvent) ? string.Empty : $"@{MiniGameSecretFrontEvent}")}",
            _ => MiniGameTaskName,
        };
    }

    internal void ApplyRuntimeCallback(CoreCallbackEvent callback)
    {
        ApplyRuntimeCallback(SessionCallbackEnvelope.FromRaw(callback));
    }

    internal void ApplyRuntimeCallback(SessionCallbackEnvelope callbackEnvelope)
    {
        var callback = callbackEnvelope.Callback;
        var payload = callbackEnvelope.Payload;
        if (!string.IsNullOrWhiteSpace(callbackEnvelope.ParseError))
        {
            Runtime.LogService.Warn(
                $"Toolbox callback payload parse failed: msgName={callback.MsgName}; msgId={callback.MsgId}; error={callbackEnvelope.ParseError}");
        }

        if (string.Equals(callback.MsgName, "SubTaskExtraInfo", StringComparison.OrdinalIgnoreCase))
        {
            HandleSubTaskExtraInfo(payload);
            return;
        }

        if (_activeTool is null)
        {
            return;
        }

        switch (callback.MsgName)
        {
            case "TaskChainCompleted":
            case "AllTasksCompleted":
                CompleteActiveToolRun(
                    _activeTool.Value,
                    success: true,
                    T("Toolbox.Status.ToolCompleted", "Tool execution completed."));
                break;
            case "TaskChainStopped":
                CompleteActiveToolRun(
                    _activeTool.Value,
                    success: false,
                    T("Toolbox.Status.ToolStopped", "Tool stopped."),
                    UiErrorCode.ToolboxExecutionCancelled);
                break;
            case "TaskChainError":
                if (_activeTool == ToolboxToolKind.Recruit)
                {
                    RecruitInfo = T(
                        "Toolbox.Error.RecruitFailed",
                        "Recruit recognition failed. Check current screen and connection status.");
                }

                CompleteActiveToolRun(
                    _activeTool.Value,
                    success: false,
                    T("Toolbox.Error.ExecutionFailedDefault", "Tool execution failed."),
                    UiErrorCode.ToolboxExecutionFailed);
                break;
        }
    }

    internal Task HandleCallbackAsync(CoreCallbackEvent callback)
    {
        OnSessionCallbackProjected(SessionCallbackEnvelope.FromRaw(callback));
        return Task.CompletedTask;
    }

    private void OnSessionCallbackProjected(SessionCallbackEnvelope callback)
    {
        _pendingSessionCallbacks.Enqueue(callback);
        if (Interlocked.CompareExchange(ref _callbackDrainScheduled, 1, 0) != 0)
        {
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            DrainPendingSessionCallbacksOnUiThread();
            return;
        }

        Dispatcher.UIThread.Post(DrainPendingSessionCallbacksOnUiThread, DispatcherPriority.Background);
    }

    private void DrainPendingSessionCallbacksOnUiThread()
    {
        var shouldReschedule = false;
        try
        {
            while (_pendingSessionCallbacks.TryDequeue(out var callback))
            {
                ApplyRuntimeCallback(callback);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _callbackDrainScheduled, 0);
            shouldReschedule = !_pendingSessionCallbacks.IsEmpty
                && Interlocked.CompareExchange(ref _callbackDrainScheduled, 1, 0) == 0;
        }

        if (shouldReschedule)
        {
            Dispatcher.UIThread.Post(DrainPendingSessionCallbacksOnUiThread, DispatcherPriority.Background);
        }
    }

    private async Task DispatchToolAsync(
        ToolboxToolKind tool,
        ToolboxDispatchRequest request,
        Action prepareUiAction,
        CancellationToken cancellationToken,
        bool startPeepAfterDispatch = false)
    {
        CurrentToolParameters = BuildCurrentParameterText(tool);
        var validation = ValidateCurrentToolParameters(tool);
        if (!validation.Success)
        {
            await ApplyFailureAsync(tool, validation, "validation", cancellationToken);
            return;
        }

        if (IsToolboxBusy)
        {
            await ApplyToolboxBusyAsync(
                tool,
                UiOperationResult.Fail(
                    UiErrorCode.ToolboxExecutionFailed,
                    T("Toolbox.Error.ToolboxBusy", "Toolbox already has a running task. Stop it first.")),
                cancellationToken);
            return;
        }

        if (!Runtime.SessionService.TryBeginRun(ToolboxRunOwner, out var currentOwner))
        {
            await ApplyFailureAsync(
                tool,
                UiOperationResult.Fail(
                    UiErrorCode.ToolboxExecutionFailed,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        T("Toolbox.Error.OwnerRunning", "`{0}` is already running."),
                        currentOwner)),
                "owner",
                cancellationToken);
            return;
        }

        var connectResult = await EnsureConnectedAsync(cancellationToken);
        if (!connectResult.Success)
        {
            Runtime.SessionService.EndRun(ToolboxRunOwner);
            await ApplyFailureAsync(tool, connectResult, "connect", cancellationToken);
            return;
        }

        await PersistBridgeSettingsForToolAsync(tool, cancellationToken);

        prepareUiAction();
        TransitionToExecuting(tool, request.ParameterSummary ?? CurrentToolParameters);

        UiOperationResult<ToolboxDispatchResult> dispatchResult;
        try
        {
            dispatchResult = await Runtime.ToolboxFeatureService.DispatchToolAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Runtime.SessionService.EndRun(ToolboxRunOwner);
            await ApplyFailureAsync(
                tool,
                UiOperationResult.Fail(
                    UiErrorCode.ToolboxExecutionCancelled,
                    T("Toolbox.Error.ToolExecutionCancelled", "Tool execution cancelled.")),
                "dispatch-cancelled",
                CancellationToken.None);
            return;
        }
        catch (Exception ex)
        {
            Runtime.SessionService.EndRun(ToolboxRunOwner);
            await ApplyFailureAsync(
                tool,
                UiOperationResult.Fail(UiErrorCode.ToolboxExecutionFailed, ex.Message, ex.ToString()),
                "dispatch-exception",
                CancellationToken.None);
            return;
        }

        if (!dispatchResult.Success || dispatchResult.Value is null)
        {
            Runtime.SessionService.EndRun(ToolboxRunOwner);
            await ApplyFailureAsync(tool, dispatchResult.ToUntyped(), "dispatch", cancellationToken);
            return;
        }

        LastExecutionAt = dispatchResult.Value.StartedAt;
        ResultText = string.Format(
            CultureInfo.InvariantCulture,
            T("Toolbox.Status.ToolStarted", "{0} started."),
            GetToolDisplayName(tool));
        StatusMessage = dispatchResult.Message;

        if (startPeepAfterDispatch)
        {
            _peepWasAutoStarted = true;
            StartPeepPolling();
            IsGachaInProgress = true;
            RefreshGachaTip();
            _gachaTipTimer.Start();
        }
    }

    private void PrepareRecruitForStart()
    {
        RecruitInfo = T("Toolbox.Status.Recognizing", "Recognizing...");
        RecruitResultLines.Clear();
        _lastRecruitResult = null;
    }

    private void PrepareOperBoxForStart()
    {
        OperBoxInfo = T("Toolbox.Status.Recognizing", "Recognizing...");
        _operBoxOwnedById.Clear();
        _operBoxPotential.Clear();
        OperBoxHaveList.Clear();
        OperBoxNotHaveList.Clear();
        _operBoxListsMaterialized = false;
        OperBoxSelectedIndex = 1;
        LastOperBoxSyncTime = null;
    }

    private void PrepareDepotForStart()
    {
        DepotInfo = T("Toolbox.Status.Recognizing", "Recognizing...");
        ClearDepotRecognitionResults();
    }

    private void ClearDepotRecognitionResults()
    {
        if (DepotResult.Count > 0)
        {
            DepotResult.Clear();
        }
        else
        {
            OnPropertyChanged(nameof(ArkPlannerResult));
            OnPropertyChanged(nameof(LoliconResult));
        }
    }

    private void PrepareGachaForStart()
    {
        GachaInfo = T("Toolbox.Status.ConnectingEmulator", "Connecting to emulator...");
    }

    private void PrepareMiniGameForStart()
    {
        ResultText = T("Toolbox.Status.MiniGameStarting", "Starting mini-game task...");
    }

    private ToolboxDispatchRequest BuildRecruitRequest()
    {
        var selectedLevels = new List<int>();
        if (ChooseLevel3)
        {
            selectedLevels.Add(3);
        }

        if (ChooseLevel4)
        {
            selectedLevels.Add(4);
        }

        if (ChooseLevel5)
        {
            selectedLevels.Add(5);
        }

        if (ChooseLevel6)
        {
            selectedLevels.Add(6);
        }

        return new ToolboxDispatchRequest(
            ToolboxToolKind.Recruit,
            Recruit: new ToolboxRecruitRequest(
                selectedLevels,
                RecruitAutoSetTime,
                RecruitLevel3Time,
                RecruitLevel4Time,
                RecruitLevel5Time,
                ResolveServerType()),
            ParameterSummary: BuildCurrentParameterText(ToolboxToolKind.Recruit));
    }

    private void TransitionToExecuting(ToolboxToolKind tool, string parameterSummary)
    {
        _activeTool = tool;
        _lastDispatchedParameterSummary = parameterSummary;
        ExecutionState = ToolboxExecutionState.Executing;
        LastExecutionErrorCode = string.Empty;
        LastExecutionAt = null;
        OnPropertyChanged(nameof(IsMiniGameRunning));
        OnPropertyChanged(nameof(MiniGameCommandText));
    }

    private async Task<UiOperationResult> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (Runtime.SessionService.CurrentState is SessionState.Connected or SessionState.Running or SessionState.Stopping)
        {
            return UiOperationResult.Ok("Session already connected.");
        }

        return await TryConnectWithCurrentSettingsAsync(cancellationToken);
    }

    private async Task<UiOperationResult> TryConnectWithCurrentSettingsAsync(CancellationToken cancellationToken)
    {
        var connectConfig = ResolveConnectConfig();
        var adbPath = ResolveEffectiveAdbPath();
        var candidates = BuildConnectAddressCandidates();
        UiOperationResult? lastFailure = null;

        foreach (var candidate in candidates)
        {
            var result = await Runtime.ConnectFeatureService.ConnectAsync(candidate, connectConfig, adbPath, cancellationToken);
            if (result.Success)
            {
                return result;
            }

            lastFailure = result;
        }

        return lastFailure ?? UiOperationResult.Fail(
            UiErrorCode.UiOperationFailed,
            T("Toolbox.Error.ConnectionFailed", "Connection failed."));
    }

    private IReadOnlyList<string> BuildConnectAddressCandidates()
    {
        if (_connectionState is not null)
        {
            return _connectionState.BuildConnectAddressCandidates(includeConfiguredAddress: true);
        }

        var configured = ResolveProfileString("ConnectAddress", LegacyConfigurationKeys.ConnectAddress) ?? "127.0.0.1:5555";
        var connectConfig = ResolveConnectConfig();
        var autoDetect = ResolveProfileBool("AutoDetect", fallback: true);
        var alwaysAutoDetect = ResolveProfileBool("AlwaysAutoDetect", fallback: false, LegacyConfigurationKeys.AlwaysAutoDetect);
        var candidates = new List<string>();
        AddAddressCandidate(candidates, configured);

        if (autoDetect || alwaysAutoDetect || candidates.Count == 0)
        {
            foreach (var candidate in GetDefaultAddresses(connectConfig))
            {
                AddAddressCandidate(candidates, candidate);
            }
        }

        return candidates.Count == 0 ? ["127.0.0.1:5555"] : candidates;
    }

    private string ResolveConnectConfig()
    {
        if (_connectionState is not null)
        {
            return string.IsNullOrWhiteSpace(_connectionState.ConnectConfig) ? "General" : _connectionState.ConnectConfig.Trim();
        }

        return ResolveProfileString("ConnectConfig", LegacyConfigurationKeys.ConnectConfig) ?? "General";
    }

    private string? ResolveEffectiveAdbPath()
    {
        if (_connectionState is not null)
        {
            var resolved = _connectionState.ResolveEffectiveAdbPath(updateStateWhenResolved: true);
            return string.IsNullOrWhiteSpace(resolved) ? null : resolved;
        }

        var adb = ResolveProfileString("AdbPath", LegacyConfigurationKeys.AdbPath);
        return string.IsNullOrWhiteSpace(adb) ? null : adb;
    }

    private string ResolveServerType()
    {
        return ResolveProfileString("ServerType", fallback: "CN") ?? "CN";
    }

    private string ResolveClientType()
    {
        if (_connectionState is not null && !string.IsNullOrWhiteSpace(_connectionState.ClientType))
        {
            return _connectionState.ClientType.Trim();
        }

        return ResolveProfileString("ClientType", fallback: "Official") ?? "Official";
    }

    private string ResolveCurrentLanguage()
    {
        if (Runtime.ConfigurationService.CurrentConfig.GlobalValues.TryGetValue(LegacyConfigurationKeys.Localization, out var node)
            && node is JsonValue value
            && value.TryGetValue(out string? language)
            && !string.IsNullOrWhiteSpace(language))
        {
            return UiLanguageCatalog.Normalize(language);
        }

        return UiLanguageCatalog.Normalize(_uiLanguageCoordinator.CurrentLanguage);
    }

    public void SetLanguage(string language)
    {
        var normalized = UiLanguageCatalog.Normalize(language);
        if (string.Equals(_currentLanguage, normalized, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_texts.Language, normalized, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_rootTexts.Language, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _currentLanguage = normalized;
        _texts = CreateTexts(normalized);
        _rootTexts = CreateRootTexts(normalized);
        RefreshLocalizedUiState();
    }

    private static ToolboxLocalizationTextMap CreateTexts(string language)
    {
        return new ToolboxLocalizationTextMap
        {
            Language = language,
        };
    }

    private static RootLocalizationTextMap CreateRootTexts(string language)
    {
        return new RootLocalizationTextMap("Root.Localization.Toolbox")
        {
            Language = language,
        };
    }

    private static DialogChromeCatalog CreateGachaDisclaimerDialogChrome(string language)
    {
        return DialogTextCatalog.CreateCatalog(
            language,
            nextLanguage =>
            {
                var texts = CreateTexts(nextLanguage);
                var title = DialogTextCatalog.WarningDialogTitle(nextLanguage);
                var prompt = texts.GetOrDefault(
                    "Toolbox.Warning.GachaMessage",
                    "This feature is risky and may cause unintended pulls.");
                return new DialogChromeSnapshot(
                    title: title,
                    confirmText: DialogTextCatalog.WarningDialogConfirmButton(nextLanguage),
                    cancelText: DialogTextCatalog.WarningDialogCancelButton(nextLanguage),
                    namedTexts: DialogTextCatalog.CreateNamedTexts(
                        (DialogTextCatalog.ChromeKeys.SectionTitle, title),
                        (DialogTextCatalog.ChromeKeys.Prompt, prompt),
                        (DialogTextCatalog.ChromeKeys.LeadText, texts.GetOrDefault(
                            "Toolbox.Gacha.Disclaimer.Lead",
                            "Please note, this is")),
                        (DialogTextCatalog.ChromeKeys.EmphasisText, texts.GetOrDefault(
                            "Toolbox.Gacha.Disclaimer.Emphasis",
                            "REAL GACHA")),
                        (DialogTextCatalog.ChromeKeys.DetailText, texts.GetOrDefault(
                            "Toolbox.Gacha.Disclaimer.Body",
                            "The gacha tool directly operates the current client. Make sure this is not your main account and the emulator is already on the gacha screen."))));
            });
    }

    private void RefreshLocalizedUiState()
    {
        RefreshLocalizedBindingProperties();
        RefreshLocalizedStatusTexts();
        RefreshLocalizedCollections();
        OnPropertyChanged(string.Empty);
    }

    private void RefreshLocalizedBindingProperties()
    {
        foreach (var propertyName in LocalizedBindingPropertyNames)
        {
            OnPropertyChanged(propertyName);
        }
    }

    private void RefreshLocalizedStatusTexts()
    {
        if (ExecutionState == ToolboxExecutionState.Idle && !IsToolboxBusy)
        {
            ResultText = T("Toolbox.Status.WaitingForExecution", "Waiting to execute tool.");
        }

        if (_lastRecruitResult is null && _activeTool != ToolboxToolKind.Recruit)
        {
            RecruitInfo = T("Toolbox.Tip.RecruitRecognition", "Tip: this feature is independent from the main-page auto recruit flow.");
        }

        if (_activeTool != ToolboxToolKind.OperBox)
        {
            OperBoxInfo = T("Toolbox.Tip.OperBoxRecognition", "Special markers may affect recognition accuracy.");
        }

        if (!HasDepotResult && _activeTool != ToolboxToolKind.Depot)
        {
            DepotInfo = T("Toolbox.Tip.DepotRecognition", "This feature is experimental. Please verify recognition results.");
        }

        if (!IsGachaInProgress)
        {
            GachaInfo = T("Toolbox.Tip.GachaInit", "Gacha hint.");
        }
    }

    private void RefreshLocalizedCollections()
    {
        RebuildMiniGameSecretFrontEventOptions();
        if (_operBoxListsMaterialized)
        {
            RebuildOperBoxLists();
        }

        RelocalizeDepotItems();
        LoadMiniGameEntries();
        OnPropertyChanged(nameof(SelectedMiniGameEntry));
        OnPropertyChanged(nameof(SelectedMiniGameSecretFrontEventOption));
        MiniGameTip = ResolveMiniGameTip(MiniGameTaskName);
        if (_lastRecruitResult is not null)
        {
            ApplyRecruitResult(_lastRecruitResult, cacheResult: false);
        }
    }

    private void RelocalizeDepotItems()
    {
        if (DepotResult.Count == 0)
        {
            return;
        }

        var snapshot = DepotResult
            .Select(item => new KeyValuePair<string, int>(item.Id, item.Count))
            .ToArray();
        var itemNames = ToolboxAssetCatalog.GetItemNames(_currentLanguage);
        DepotResult.Clear();

        foreach (var pair in snapshot.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            DepotResult.Add(new DepotItemViewModel(
                pair.Key,
                itemNames.TryGetValue(pair.Key, out var name) ? name : pair.Key,
                pair.Value,
                ToolboxAssetCatalog.ResolveItemImagePath(pair.Key)));
        }
    }

    private void OnUnifiedLanguageChanged(object? sender, UiLanguageChangedEventArgs e)
    {
        if (Avalonia.Application.Current is null)
        {
            SetLanguage(e.CurrentLanguage);
            return;
        }

        Dispatcher.UIThread.Post(() => SetLanguage(e.CurrentLanguage), DispatcherPriority.Background);
    }

    private static IReadOnlyList<string> GetDefaultAddresses(string connectConfig)
    {
        return connectConfig.Trim() switch
        {
            "BlueStacks" => ["127.0.0.1:5555", "127.0.0.1:5556", "127.0.0.1:5565", "127.0.0.1:5575", "127.0.0.1:5585", "127.0.0.1:5595", "127.0.0.1:5554"],
            "MuMuEmulator12" => ["127.0.0.1:16384", "127.0.0.1:16416", "127.0.0.1:16448", "127.0.0.1:16480", "127.0.0.1:16512", "127.0.0.1:16544", "127.0.0.1:16576"],
            "LDPlayer" => ["emulator-5554", "emulator-5556", "emulator-5558", "emulator-5560", "127.0.0.1:5555", "127.0.0.1:5557", "127.0.0.1:5559", "127.0.0.1:5561"],
            "Nox" => ["127.0.0.1:62001", "127.0.0.1:59865"],
            "XYAZ" => ["127.0.0.1:21503"],
            "WSA" => ["127.0.0.1:58526"],
            _ => ["127.0.0.1:5555"],
        };
    }

    private static void AddAddressCandidate(ICollection<string> candidates, string? raw)
    {
        var normalized = NormalizeToken(raw)
            .Replace("：", ":", StringComparison.Ordinal)
            .Replace("；", ":", StringComparison.Ordinal)
            .Replace(';', ':');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        if (candidates.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        candidates.Add(normalized);
    }

    private void HandleSubTaskExtraInfo(JsonObject? payload)
    {
        if (payload is null)
        {
            return;
        }

        var taskChain = ReadString(payload, "taskchain");
        var what = ReadString(payload, "what");
        var details = payload["details"] as JsonObject;

        if (string.Equals(what, "StageDrops", StringComparison.OrdinalIgnoreCase) && details is not null)
        {
            UpdateDepotFromDrops(details);
        }

        if (!Runtime.SessionService.IsRunOwner(ToolboxRunOwner) && _activeTool is null)
        {
            return;
        }

        if (string.Equals(taskChain, RecruitTaskChain, StringComparison.OrdinalIgnoreCase))
        {
            HandleRecruitCallback(what, details);
            TryCompleteRecognitionToolFromSubTask(ToolboxToolKind.Recruit, what, details);
            return;
        }

        if (string.Equals(taskChain, DepotTaskChain, StringComparison.OrdinalIgnoreCase) && details is not null)
        {
            ApplyDepotRecognition(details, updateSyncTime: true);
            TryCompleteRecognitionToolFromSubTask(ToolboxToolKind.Depot, what, details);
            return;
        }

        if (string.Equals(taskChain, OperBoxTaskChain, StringComparison.OrdinalIgnoreCase) && details is not null)
        {
            ApplyOperBoxRecognition(details);
            TryCompleteRecognitionToolFromSubTask(ToolboxToolKind.OperBox, what, details);
        }
    }

    private void TryCompleteRecognitionToolFromSubTask(ToolboxToolKind tool, string what, JsonObject? details)
    {
        if (_activeTool != tool)
        {
            return;
        }

        var completed = tool switch
        {
            ToolboxToolKind.Recruit => string.Equals(what, "RecruitResult", StringComparison.OrdinalIgnoreCase),
            ToolboxToolKind.Depot => details is not null && ReadBool(details, "done"),
            ToolboxToolKind.OperBox => details is not null && ReadBool(details, "done"),
            _ => false,
        };

        if (!completed)
        {
            return;
        }

        CompleteActiveToolRun(
            tool,
            success: true,
            T("Toolbox.Status.RecognitionCompleted", "Recognition completed."));
    }

    private void HandleRecruitCallback(string what, JsonObject? details)
    {
        if (details is null)
        {
            return;
        }

        switch (what)
        {
            case "RecruitTagsDetected":
            {
                var tags = ReadStringArray(details["tags"]);
                RecruitInfo = tags.Count == 0
                    ? T("Toolbox.Recruit.TagsDetected", "Recruit tags detected.")
                    : string.Format(
                        CultureInfo.InvariantCulture,
                        T("Toolbox.Recruit.TagsDetectedWithList", "Tags detected: {0}"),
                        string.Join(" / ", tags));
                break;
            }
            case "RecruitResult":
                ApplyRecruitResult(details["result"] as JsonArray);
                break;
        }
    }

    private void ApplyRecruitResult(JsonArray? resultArray, bool cacheResult = true)
    {
        if (cacheResult)
        {
            _lastRecruitResult = resultArray?.DeepClone() as JsonArray;
        }

        RecruitResultLines.Clear();
        var language = _currentLanguage;

        foreach (var comboNode in resultArray ?? [])
        {
            if (comboNode is not JsonObject combo)
            {
                continue;
            }

            var tagLevel = ReadInt(combo, "level");
            var tags = ReadStringArray(combo["tags"]);
            var tagSegments = new List<RecruitResultSegmentViewModel>
            {
                new($"{tagLevel}★", null, RecruitResultSegmentKind.Level),
            };
            tagSegments.AddRange(tags.Select(tag => new RecruitResultSegmentViewModel(
                tag,
                null,
                RecruitResultSegmentKind.Tag)));
            RecruitResultLines.Add(new RecruitResultLineViewModel(tagSegments, RecruitResultLineKind.TagLine));

            var operatorsWithPotential = (combo["opers"] as JsonArray ?? [])
                .OfType<JsonObject>()
                .Select(oper =>
                {
                    var operLevel = ReadInt(oper, "level");
                    var operId = ReadString(oper, "id");
                    var potential = -1;

                    if (RecruitmentShowPotential
                        && !string.IsNullOrWhiteSpace(operId)
                        && (tagLevel >= 4 || operLevel == 1)
                        && _operBoxPotential.TryGetValue(operId, out var potentialValue))
                    {
                        potential = potentialValue;
                    }

                    return new RecruitOperatorProjection(oper, operLevel, potential);
                })
                .OrderByDescending(item => item.Level)
                .ThenBy(item => item.Potential)
                .ToList();

            var operatorSegments = new List<RecruitResultSegmentViewModel>();
            foreach (var candidate in operatorsWithPotential)
            {
                var oper = candidate.Operator;
                var operLevel = ReadInt(oper, "level");
                var operId = ReadString(oper, "id");
                var operName = ReadString(oper, "name");

                if (!string.IsNullOrWhiteSpace(operId)
                    && ToolboxAssetCatalog.GetOperators().TryGetValue(operId, out var asset))
                {
                    operName = ToolboxAssetCatalog.GetLocalizedOperatorName(asset, language);
                }

                var suffix = string.Empty;
                if (RecruitmentShowPotential
                    && !string.IsNullOrWhiteSpace(operId)
                    && (tagLevel >= 4 || operLevel == 1))
                {
                    if (_operBoxPotential.TryGetValue(operId, out var potential))
                    {
                        suffix = potential >= 6
                            ? T("Toolbox.Recruit.Suffix.Max", " (MAX)")
                            : $" ({potential})";
                    }
                    else
                    {
                        suffix = T("Toolbox.Recruit.Suffix.New", " (NEW)");
                    }
                }

                operatorSegments.Add(new RecruitResultSegmentViewModel(
                    $"{operName}{suffix}",
                    ResolveStarBrush(operLevel),
                    RecruitResultSegmentKind.Operator));
            }

            if (operatorSegments.Count > 0)
            {
                RecruitResultLines.Add(new RecruitResultLineViewModel(
                    operatorSegments,
                    RecruitResultLineKind.OperatorLine));
            }

            RecruitResultLines.Add(RecruitResultLineViewModel.CreateSpacer());
        }
    }

    private void ApplyOperBoxRecognition(JsonObject details)
    {
        if (details["own_opers"] is not JsonArray ownOpers)
        {
            return;
        }

        var operators = ToolboxAssetCatalog.GetOperators();
        var language = _currentLanguage;

        foreach (var node in ownOpers)
        {
            if (node is not JsonObject oper)
            {
                continue;
            }

            var id = ReadString(oper, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var rarity = ReadInt(oper, "rarity");
            var elite = ReadInt(oper, "elite");
            var level = ReadInt(oper, "level");
            var potential = ReadInt(oper, "potential");
            var name = ReadString(oper, "name");

            if (operators.TryGetValue(id, out var asset))
            {
                name = ToolboxAssetCatalog.GetLocalizedOperatorName(asset, language);
                rarity = asset.Rarity;
            }

            if (string.Equals(id, "char_485_pallas", StringComparison.Ordinal))
            {
                _ = Runtime.AchievementTrackerService.Unlock("WarehouseKeeper");
            }

            _operBoxOwnedById[id] = new ToolboxOwnedOperatorState(id, name, rarity, elite, level, potential);
            _operBoxPotential[id] = potential;
        }

        var done = ReadBool(details, "done");
        if (!done)
        {
            return;
        }

        RebuildOperBoxLists();
        LastOperBoxSyncTime = DateTimeOffset.UtcNow;
        OperBoxInfo = $"{T("Toolbox.Status.RecognitionCompleted", "Recognition completed.")}{Environment.NewLine}" +
            T("Toolbox.Tip.OperBoxRecognition", "Special markers may affect recognition accuracy.");
        _ = PersistOperBoxAsync(CancellationToken.None);
    }

    private void EnsureOperBoxListsMaterialized()
    {
        if (_operBoxListsMaterialized || _operBoxOwnedById.Count == 0)
        {
            return;
        }

        RebuildOperBoxLists();
    }

    private void RebuildOperBoxLists()
    {
        var haveItems = new List<OperBoxOperatorItemViewModel>();
        var notHaveItems = new List<OperBoxOperatorItemViewModel>();
        var operators = ToolboxAssetCatalog.GetOperators();
        var clientType = ResolveClientType();
        var language = _currentLanguage;
        var ownedSubtitleTemplate = T(
            "Toolbox.OperBox.Subtitle.Owned",
            "{0}★ / Elite {1} / Level {2} / Potential {3}");
        var notOwnedSubtitleTemplate = T(
            "Toolbox.OperBox.Subtitle.NotOwned",
            "{0}★ / Not owned");

        foreach (var owned in _operBoxOwnedById.Values
                     .OrderByDescending(item => item.Rarity)
                     .ThenByDescending(item => item.Elite)
                     .ThenByDescending(item => item.Level)
                     .ThenByDescending(item => item.Potential)
                     .ThenBy(item => item.Id, StringComparer.Ordinal))
        {
            var displayName = owned.Name;
            var rarity = owned.Rarity;
            if (operators.TryGetValue(owned.Id, out var asset))
            {
                displayName = ToolboxAssetCatalog.GetLocalizedOperatorName(asset, language);
                rarity = asset.Rarity;
            }

            haveItems.Add(new OperBoxOperatorItemViewModel(
                owned.Id,
                displayName,
                rarity,
                owned.Elite,
                owned.Level,
                owned.Potential,
                own: true,
                ownedSubtitleTemplate,
                notOwnedSubtitleTemplate));
        }

        foreach (var asset in operators.Values
                     .Where(asset => ToolboxAssetCatalog.IsOperatorAvailableInClient(asset, clientType) && !_operBoxOwnedById.ContainsKey(asset.Id))
                     .OrderByDescending(asset => asset.Rarity)
                     .ThenBy(asset => asset.Id, StringComparer.Ordinal))
        {
            notHaveItems.Add(new OperBoxOperatorItemViewModel(
                asset.Id,
                ToolboxAssetCatalog.GetLocalizedOperatorName(asset, language),
                asset.Rarity,
                elite: 0,
                level: 0,
                potential: 0,
                own: false,
                ownedSubtitleTemplate,
                notOwnedSubtitleTemplate));
        }

        ReplaceCollectionItems(OperBoxHaveList, haveItems);
        ReplaceCollectionItems(OperBoxNotHaveList, notHaveItems);
        _operBoxListsMaterialized = haveItems.Count > 0 || notHaveItems.Count > 0;
        OperBoxSelectedIndex = OperBoxNotHaveList.Count > 0 ? 0 : 1;
    }

    private void ApplyDepotRecognition(JsonObject details, bool updateSyncTime)
    {
        var counts = ParseDepotCounts(details);
        DepotResult.Clear();
        var itemNames = ToolboxAssetCatalog.GetItemNames(_currentLanguage);

        foreach (var pair in counts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (pair.Value > Runtime.AchievementTrackerService.GetProgress("WarehouseMiser"))
            {
                _ = Runtime.AchievementTrackerService.SetProgress("WarehouseMiser", pair.Value);
            }

            DepotResult.Add(new DepotItemViewModel(
                pair.Key,
                itemNames.TryGetValue(pair.Key, out var name) ? name : pair.Key,
                pair.Value,
                ToolboxAssetCatalog.ResolveItemImagePath(pair.Key)));
        }

        var done = ReadBool(details, "done");
        if (!done)
        {
            return;
        }

        if (updateSyncTime)
        {
            LastDepotSyncTime = DateTimeOffset.UtcNow;
        }
        else
        {
            var syncTime = ReadString(details, "syncTime");
            if (DateTimeOffset.TryParse(syncTime, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                LastDepotSyncTime = parsed.ToUniversalTime();
            }
        }

        DepotInfo = T("Toolbox.Status.RecognitionCompleted", "Recognition completed.");
        _ = PersistDepotAsync(CancellationToken.None);
    }

    private Dictionary<string, int> ParseDepotCounts(JsonObject details)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (details["data"] is JsonValue dataValue && dataValue.TryGetValue(out string? rawData) && !string.IsNullOrWhiteSpace(rawData))
        {
            TryReadDepotCountsFromJson(rawData, result);
        }
        else if (details["data"] is JsonObject directData)
        {
            TryReadDepotCountsFromJson(directData.ToJsonString(), result);
        }

        if (result.Count > 0)
        {
            return result;
        }

        if (details["arkplanner"] is JsonObject arkPlanner
            && arkPlanner["object"] is JsonObject objectNode
            && objectNode["items"] is JsonArray items)
        {
            foreach (var node in items)
            {
                if (node is not JsonObject item)
                {
                    continue;
                }

                var id = ReadString(item, "id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                result[id] = ReadInt(item, "have");
            }
        }

        return result;
    }

    private void UpdateDepotFromDrops(JsonObject details)
    {
        if (details["stats"] is not JsonArray stats)
        {
            return;
        }

        var itemNames = ToolboxAssetCatalog.GetItemNames(_currentLanguage);
        var byId = DepotResult.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var changed = false;

        foreach (var node in stats)
        {
            if (node is not JsonObject item)
            {
                continue;
            }

            var itemId = ReadString(item, "itemId");
            if (string.IsNullOrWhiteSpace(itemId))
            {
                continue;
            }

            if (string.Equals(ReadString(item, "itemName"), "furni", StringComparison.OrdinalIgnoreCase))
            {
                itemId = "3401";
            }

            if (ExcludedDepotItemIds.Contains(itemId) || !int.TryParse(itemId, out _))
            {
                continue;
            }

            var addQuantity = ReadInt(item, "addQuantity");
            if (addQuantity <= 0)
            {
                continue;
            }

            if (byId.TryGetValue(itemId, out var existing))
            {
                existing.Count += addQuantity;
                if (existing.Count > Runtime.AchievementTrackerService.GetProgress("WarehouseMiser"))
                {
                    _ = Runtime.AchievementTrackerService.SetProgress("WarehouseMiser", existing.Count);
                }
            }
            else
            {
                var name = itemNames.TryGetValue(itemId, out var itemName) ? itemName : itemId;
                var created = new DepotItemViewModel(itemId, name, addQuantity, ToolboxAssetCatalog.ResolveItemImagePath(itemId));
                byId[itemId] = created;
                DepotResult.Add(created);
                if (addQuantity > Runtime.AchievementTrackerService.GetProgress("WarehouseMiser"))
                {
                    _ = Runtime.AchievementTrackerService.SetProgress("WarehouseMiser", addQuantity);
                }
            }

            changed = true;
        }

        if (!changed)
        {
            return;
        }

        var ordered = DepotResult.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        DepotResult.Clear();
        foreach (var item in ordered)
        {
            DepotResult.Add(item);
        }

        _ = PersistDepotAsync(CancellationToken.None);
    }

    private void CompleteActiveToolRun(ToolboxToolKind tool, bool success, string message, string errorCode = "")
    {
        if (_activeTool is null)
        {
            return;
        }

        if (tool == ToolboxToolKind.Gacha)
        {
            _gachaTipTimer.Stop();
            IsGachaInProgress = false;
            if (!success)
            {
                GachaInfo = message;
            }
        }

        if (_peepWasAutoStarted)
        {
            StopPeepPolling(clearImage: false, releaseRunOwner: false);
            _peepWasAutoStarted = false;
        }

        ExecutionState = success ? ToolboxExecutionState.Succeeded : ToolboxExecutionState.Failed;
        ResultText = message;
        LastExecutionErrorCode = errorCode;
        LastExecutionAt = DateTimeOffset.Now;

        ExecutionHistory.Insert(0, success
            ? ToolExecutionRecord.Succeeded(GetToolDisplayName(tool), _lastDispatchedParameterSummary, BuildResultSummary(message))
            : ToolExecutionRecord.Failed(GetToolDisplayName(tool), _lastDispatchedParameterSummary, BuildResultSummary(message), string.IsNullOrWhiteSpace(errorCode) ? UiErrorCode.ToolboxExecutionFailed : errorCode));
        TrimExecutionHistory();
        _ = PersistExecutionHistoryAsync(CancellationToken.None);

        Runtime.SessionService.EndRun(ToolboxRunOwner);
        _activeTool = null;
        _lastDispatchedParameterSummary = string.Empty;
        OnPropertyChanged(nameof(IsMiniGameRunning));
        OnPropertyChanged(nameof(MiniGameCommandText));
    }

    private async Task ApplyFailureAsync(
        ToolboxToolKind? tool,
        UiOperationResult result,
        string stage,
        CancellationToken cancellationToken)
    {
        var errorCode = string.IsNullOrWhiteSpace(result.Error?.Code)
            ? UiErrorCode.ToolboxExecutionFailed
            : result.Error.Code;
        var formatted = FormatFailureMessage(errorCode, result.Message);
        var details = MergeDetails(
            BuildFailureContextDetails(tool, errorCode, stage),
            result.Error?.Details);
        var normalized = UiOperationResult.Fail(errorCode, formatted, details);
        _ = await ApplyResultAsync(normalized, ScopeOf(tool), cancellationToken);

        ResultText = formatted;
        LastErrorMessage = formatted;
        LastExecutionErrorCode = errorCode;
        LastExecutionAt = DateTimeOffset.Now;
        ExecutionState = ToolboxExecutionState.Failed;

        ExecutionHistory.Insert(0, ToolExecutionRecord.Failed(
            ToolNameOf(tool),
            BuildParameterSummary(CurrentToolParameters),
            BuildResultSummary(formatted),
            errorCode));
        TrimExecutionHistory();
        await PersistExecutionHistoryAsync(cancellationToken);
    }

    private async Task ApplyToolboxBusyAsync(
        ToolboxToolKind? tool,
        UiOperationResult result,
        CancellationToken cancellationToken)
    {
        var errorCode = string.IsNullOrWhiteSpace(result.Error?.Code)
            ? UiErrorCode.ToolboxExecutionFailed
            : result.Error.Code;
        var formatted = FormatFailureMessage(errorCode, result.Message);
        var details = MergeDetails(
            BuildFailureContextDetails(tool, errorCode, "busy"),
            result.Error?.Details);
        var normalized = UiOperationResult.Fail(errorCode, formatted, details);

        await RecordFailedResultAsync(ScopeOf(tool), normalized, cancellationToken);

        LastErrorMessage = formatted;
        LastExecutionErrorCode = errorCode;
        LastExecutionAt = DateTimeOffset.Now;

        ExecutionHistory.Insert(0, ToolExecutionRecord.Failed(
            ToolNameOf(tool),
            BuildParameterSummary(CurrentToolParameters),
            BuildResultSummary(formatted),
            errorCode));
        TrimExecutionHistory();
        await PersistExecutionHistoryAsync(cancellationToken);
        await ShowToolboxBusyDialogAsync(cancellationToken);
    }

    private async Task ShowToolboxBusyDialogAsync(CancellationToken cancellationToken)
    {
        var language = DialogLanguage;
        var activeTool = _activeTool ?? (Peeping ? ToolboxToolKind.VideoRecognition : null);
        var chrome = CreateToolboxBusyDialogChrome(language, activeTool);
        var chromeSnapshot = chrome.GetSnapshot(language);
        var message = chromeSnapshot.GetNamedTextOrDefault(
            DialogTextCatalog.ChromeKeys.Prompt,
            BuildToolboxBusyDialogMessage(language, activeTool));
        var request = new WarningConfirmDialogRequest(
            Title: chromeSnapshot.Title,
            Message: message,
            ConfirmText: chromeSnapshot.ConfirmText ?? DialogTextCatalog.WarningDialogConfirmButton(language),
            CancelText: chromeSnapshot.CancelText ?? DialogTextCatalog.WarningDialogCancelButton(language),
            Language: language,
            Chrome: chrome);
        var dialogResult = await _dialogService.ShowWarningConfirmAsync(request, ToolboxBusyDialogScope, cancellationToken);
        if (dialogResult.Return == DialogReturnSemantic.Confirm)
        {
            await StopActiveToolAsync(cancellationToken);
        }
    }

    private static DialogChromeCatalog CreateToolboxBusyDialogChrome(string language, ToolboxToolKind? activeTool)
    {
        return DialogTextCatalog.CreateCatalog(
            language,
            nextLanguage =>
            {
                var texts = CreateTexts(nextLanguage);
                var title = texts.GetOrDefault("Toolbox.BusyDialog.Title", "Toolbox is busy");
                return new DialogChromeSnapshot(
                    title: title,
                    confirmText: texts.GetOrDefault("Toolbox.BusyDialog.StopButton", "Stop current task"),
                    cancelText: texts.GetOrDefault("Toolbox.BusyDialog.CloseButton", "Got it"),
                    namedTexts: DialogTextCatalog.CreateNamedTexts(
                        (DialogTextCatalog.ChromeKeys.SectionTitle, title),
                        (DialogTextCatalog.ChromeKeys.Prompt, BuildToolboxBusyDialogMessage(nextLanguage, activeTool))));
            });
    }

    private static string BuildToolboxBusyDialogMessage(string language, ToolboxToolKind? activeTool)
    {
        var texts = CreateTexts(language);
        var activeToolName = activeTool is { } tool
            ? GetToolDisplayName(tool, texts)
            : texts.GetOrDefault("Toolbox.BusyDialog.CurrentTask", "current task");
        return string.Format(
            CultureInfo.InvariantCulture,
            texts.GetOrDefault(
                "Toolbox.BusyDialog.Message",
                "{0} is still running. Stop it before starting another toolbox task."),
            activeToolName);
    }

    private void StartPeepPolling()
    {
        CancelPeepPollingLoop();
        Peeping = true;
        PeepScreenFps = 0;
        _lastPeepFpsWindowStartedAt = DateTimeOffset.UtcNow;
        _peepFramesInWindow = 0;
        _peepPollingCts = new CancellationTokenSource();
        _peepPollingTask = RunPeepPollingAsync(_peepPollingCts.Token);
    }

    private void StopPeepPolling(bool clearImage, bool releaseRunOwner)
    {
        CancelPeepPollingLoop();
        Peeping = false;
        PeepScreenFps = 0;
        if (clearImage)
        {
            PeepImage = null;
        }

        if (releaseRunOwner && _activeTool is null)
        {
            Runtime.SessionService.EndRun(ToolboxRunOwner);
        }
    }

    private void UpdatePeepTimerInterval()
    {
        // The polling loop reads PeepTargetFps before each delay, so changes take
        // effect after the current frame without restarting the capture task.
    }

    private void CancelPeepPollingLoop()
    {
        var cts = _peepPollingCts;
        if (cts is null)
        {
            return;
        }

        _peepPollingCts = null;
        var task = _peepPollingTask;
        _peepPollingTask = null;
        cts.Cancel();

        if (task is null || task.IsCompleted)
        {
            cts.Dispose();
            return;
        }

        _ = task.ContinueWith(
            _ => cts.Dispose(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task RunPeepPollingAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var frameStartedAt = Stopwatch.GetTimestamp();
            await RefreshPeepImageAsync(cancellationToken).ConfigureAwait(false);
            var elapsed = Stopwatch.GetElapsedTime(frameStartedAt);
            var frameInterval = TimeSpan.FromMilliseconds(1000d / Math.Max(1, PeepTargetFps));
            var remainingDelay = frameInterval - elapsed;
            remainingDelay = remainingDelay <= TimeSpan.Zero
                ? TimeSpan.FromMilliseconds(1)
                : remainingDelay;

            try
            {
                await Task.Delay(remainingDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task RefreshPeepImageAsync(CancellationToken cancellationToken)
    {
        if (!Peeping)
        {
            return;
        }

        try
        {
            var screenshotStopwatch = Stopwatch.StartNew();
            var imageResult = await Runtime.CoreBridge.GetImageAsync(cancellationToken).ConfigureAwait(false);
            screenshotStopwatch.Stop();
            if (!imageResult.Success || imageResult.Value is null || imageResult.Value.Length == 0)
            {
                _ = Runtime.DiagnosticsService.RecordScreenshotTestAsync(
                    "Toolbox.Peep.Refresh",
                    success: false,
                    elapsedMs: screenshotStopwatch.Elapsed.TotalMilliseconds,
                    provider: "CoreBridge.GetImageAsync",
                    details: imageResult.Error?.Message,
                    minInterval: TimeSpan.FromSeconds(10));
                return;
            }

            using var stream = new MemoryStream(imageResult.Value, writable: false);
            var bitmap = DecodePeepBitmap(stream);
            _ = Runtime.DiagnosticsService.RecordScreenshotTestAsync(
                "Toolbox.Peep.Refresh",
                success: true,
                elapsedMs: screenshotStopwatch.Elapsed.TotalMilliseconds,
                provider: "CoreBridge.GetImageAsync",
                width: bitmap.PixelSize.Width,
                height: bitmap.PixelSize.Height,
                minInterval: TimeSpan.FromSeconds(30));

            try
            {
                await Dispatcher.UIThread.InvokeAsync(
                    () => ApplyPeepFrame(bitmap),
                    DispatcherPriority.Render,
                    cancellationToken);
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal stop path.
        }
        catch
        {
            // Ignore peep frame refresh errors to keep the UI responsive.
        }
    }

    private static Bitmap DecodePeepBitmap(Stream stream)
    {
        return Bitmap.DecodeToWidth(stream, PeepPreviewDecodeWidth, BitmapInterpolationMode.LowQuality);
    }

    private void ApplyPeepFrame(Bitmap bitmap)
    {
        if (!Peeping)
        {
            bitmap.Dispose();
            return;
        }

        PeepImage = bitmap;

        _peepFramesInWindow += 1;
        var now = DateTimeOffset.UtcNow;
        var elapsed = now - _lastPeepFpsWindowStartedAt;
        if (elapsed >= TimeSpan.FromSeconds(1))
        {
            PeepScreenFps = elapsed.TotalSeconds <= 0 ? 0 : _peepFramesInWindow / elapsed.TotalSeconds;
            _lastPeepFpsWindowStartedAt = now;
            _peepFramesInWindow = 0;
        }
    }

    private void RefreshGachaTip()
    {
        var tips = GetGachaTips();
        if (tips.Count == 0)
        {
            return;
        }

        GachaInfo = tips[GachaTipRandom.Next(tips.Count)];
    }

    private UiOperationResult ValidateCurrentToolParameters(ToolboxToolKind tool)
    {
        switch (tool)
        {
            case ToolboxToolKind.Recruit:
                if (!ChooseLevel3 && !ChooseLevel4 && !ChooseLevel5 && !ChooseLevel6)
                {
                    return UiOperationResult.Fail(
                        UiErrorCode.ToolboxInvalidParameters,
                        T("Toolbox.Validation.RecruitSelectAtLeastOne", "Select at least one star level for recruit calculation."));
                }

                if (!TryParseRecruitMinutes(RecruitLevel3TimeInput, out _)
                    || !TryParseRecruitMinutes(RecruitLevel4TimeInput, out _)
                    || !TryParseRecruitMinutes(RecruitLevel5TimeInput, out _))
                {
                    return UiOperationResult.Fail(
                        UiErrorCode.ToolboxInvalidParameters,
                        T("Toolbox.Validation.RecruitTimeInvalid", "Recruit times must be 10-minute multiples between 60 and 540."));
                }

                return UiOperationResult.Ok("Recruit parameters validated.");
            case ToolboxToolKind.OperBox:
                if (!string.Equals(OperBoxMode, "owned", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(OperBoxMode, "all", StringComparison.OrdinalIgnoreCase))
                {
                    return UiOperationResult.Fail(
                        UiErrorCode.ToolboxInvalidParameters,
                        T("Toolbox.Validation.OperBoxModeInvalid", "OperBox mode only supports `owned` or `all`."));
                }

                return UiOperationResult.Ok("OperBox parameters validated.");
            case ToolboxToolKind.Depot:
                if (!TryParseInt(DepotTopNInput, 1, 500, out _))
                {
                    return UiOperationResult.Fail(
                        UiErrorCode.ToolboxInvalidParameters,
                        T("Toolbox.Validation.DepotTopNInvalid", "Depot TopN must be between 1 and 500."));
                }

                return UiOperationResult.Ok("Depot parameters validated.");
            case ToolboxToolKind.Gacha:
                if (!TryParseInt(GachaDrawCountInput, 1, 10, out var drawCount) || (drawCount != 1 && drawCount != 10))
                {
                    return UiOperationResult.Fail(
                        UiErrorCode.ToolboxInvalidParameters,
                        T("Toolbox.Validation.GachaDrawCountInvalid", "Gacha draw count only supports 1 or 10."));
                }

                return UiOperationResult.Ok("Gacha parameters validated.");
            case ToolboxToolKind.VideoRecognition:
                if (!TryParseInt(VideoRecognitionTargetFpsInput, 1, 60, out _))
                {
                    return UiOperationResult.Fail(
                        UiErrorCode.ToolboxInvalidParameters,
                        T("Toolbox.Validation.PeepFpsInvalid", "Target peep FPS must be between 1 and 60."));
                }

                return UiOperationResult.Ok("Peep parameters validated.");
            case ToolboxToolKind.MiniGame:
                if (string.IsNullOrWhiteSpace(MiniGameTaskName))
                {
                    return UiOperationResult.Fail(
                        UiErrorCode.ToolboxInvalidParameters,
                        T("Toolbox.Validation.MiniGameTaskNameEmpty", "Mini-game task name cannot be empty."));
                }

                if (string.Equals(MiniGameTaskName, MiniGameSecretFrontTaskName, StringComparison.Ordinal)
                    && !MiniGameSecretFrontEndings.Contains(NormalizeToken(MiniGameSecretFrontEnding)))
                {
                    return UiOperationResult.Fail(
                        UiErrorCode.ToolboxInvalidParameters,
                        T("Toolbox.Validation.MiniGameSecretFrontEndingInvalid", "When task is `MiniGame@SecretFront`, ending must be one of A~E."));
                }

                return UiOperationResult.Ok("MiniGame parameters validated.");
            default:
                return UiOperationResult.Fail(UiErrorCode.ToolNotSupported, $"Tool `{tool}` is not supported.");
        }
    }

    private async Task PersistBridgeSettingsForToolAsync(ToolboxToolKind tool, CancellationToken cancellationToken)
    {
        var updates = BuildBridgeUpdates(tool);
        if (updates.Count == 0)
        {
            return;
        }

        _ = await RunTrackedConfigurationSaveAsync(
            $"Toolbox.ConfigBridge.{tool}",
            T("Toolbox.Title", "工具箱"),
            "Toolbox.ConfigBridge.Save",
            ct => Runtime.SettingsFeatureService.SaveGlobalSettingsAsync(updates, ct),
            cancellationToken);
    }

    private IReadOnlyDictionary<string, string> BuildBridgeUpdates(ToolboxToolKind tool)
    {
        var updates = new Dictionary<string, string>(StringComparer.Ordinal);
        switch (tool)
        {
            case ToolboxToolKind.Recruit:
                updates[LegacyConfigurationKeys.ChooseLevel3] = ChooseLevel3.ToString();
                updates[LegacyConfigurationKeys.ChooseLevel4] = ChooseLevel4.ToString();
                updates[LegacyConfigurationKeys.ChooseLevel5] = ChooseLevel5.ToString();
                updates[LegacyConfigurationKeys.ChooseLevel6] = ChooseLevel6.ToString();
                updates[LegacyConfigurationKeys.ToolBoxChooseLevel3Time] = RecruitLevel3Time.ToString(CultureInfo.InvariantCulture);
                updates[LegacyConfigurationKeys.ToolBoxChooseLevel4Time] = RecruitLevel4Time.ToString(CultureInfo.InvariantCulture);
                updates[LegacyConfigurationKeys.ToolBoxChooseLevel5Time] = RecruitLevel5Time.ToString(CultureInfo.InvariantCulture);
                updates[LegacyConfigurationKeys.AutoSetTime] = RecruitAutoSetTime.ToString();
                updates[LegacyConfigurationKeys.RecruitmentShowPotential] = RecruitmentShowPotential.ToString();
                break;
            case ToolboxToolKind.Gacha:
                updates[LegacyConfigurationKeys.GachaShowDisclaimerNoMore] = GachaShowDisclaimerNoMore.ToString();
                break;
            case ToolboxToolKind.VideoRecognition:
                updates[LegacyConfigurationKeys.PeepTargetFps] = PeepTargetFps.ToString(CultureInfo.InvariantCulture);
                break;
            case ToolboxToolKind.MiniGame:
                updates[LegacyConfigurationKeys.MiniGameTaskName] = MiniGameTaskName;
                updates[LegacyConfigurationKeys.MiniGameSecretFrontEnding] = MiniGameSecretFrontEnding;
                updates[LegacyConfigurationKeys.MiniGameSecretFrontEvent] = MiniGameSecretFrontEvent;
                break;
        }

        return updates;
    }

    private async Task LoadExecutionHistoryAsync(CancellationToken cancellationToken)
    {
        ExecutionHistory.Clear();
        if (!TryReadPersistedHistoryPayload(out var payload))
        {
            return;
        }

        if (!TryDeserializeExecutionHistory(payload, out var history, out var warning))
        {
            var failed = UiOperationResult.Fail(UiErrorCode.ToolboxExecutionFailed, warning);
            await RecordFailedResultAsync(ToolboxHistoryLoadScope, failed, cancellationToken);
            return;
        }

        foreach (var record in history)
        {
            ExecutionHistory.Add(record);
        }
    }

    private bool TryReadPersistedHistoryPayload(out string payload)
    {
        payload = string.Empty;
        if (!Runtime.ConfigurationService.CurrentConfig.GlobalValues.TryGetValue(ToolboxExecutionHistoryKey, out var node) || node is null)
        {
            return false;
        }

        if (node is JsonValue jsonValue && jsonValue.TryGetValue(out string? raw))
        {
            payload = raw ?? string.Empty;
            return !string.IsNullOrWhiteSpace(payload);
        }

        payload = node.ToJsonString();
        return !string.IsNullOrWhiteSpace(payload);
    }

    private bool TryDeserializeExecutionHistory(
        string payload,
        out List<ToolExecutionRecord> history,
        out string warning)
    {
        history = [];
        warning = string.Empty;
        try
        {
            var entries = JsonSerializer.Deserialize<List<PersistedToolExecutionRecord>>(payload, PersistedPayloadJsonOptions);
            if (entries is null)
            {
                warning = T("Toolbox.History.ReadFailedEmpty", "Failed to load execution history: empty payload.");
                return false;
            }

            foreach (var entry in entries.Where(entry => entry is not null))
            {
                history.Add(new ToolExecutionRecord(
                    entry!.ExecutedAt,
                    entry.ToolName,
                    BuildParameterSummary(entry.ParameterSummary),
                    entry.Success,
                    BuildResultSummary(entry.ResultSummary),
                    NormalizeToken(entry.ErrorCode)));
            }
        }
        catch (Exception ex)
        {
            warning = string.Format(
                CultureInfo.InvariantCulture,
                T("Toolbox.History.ReadFailedWithReason", "Failed to load execution history: {0}"),
                ex.Message);
            return false;
        }

        history = history
            .OrderByDescending(record => record.ExecutedAt)
            .Take(MaxHistoryCount)
            .ToList();
        return history.Count > 0 || string.IsNullOrWhiteSpace(warning);
    }

    private async Task PersistExecutionHistoryAsync(CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(
            ExecutionHistory.Take(MaxHistoryCount).Select(record => new PersistedToolExecutionRecord(
                record.ExecutedAt,
                record.ToolName,
                record.ParameterSummary,
                record.Success,
                record.ResultSummary,
                record.ErrorCode)));
        _ = await RunTrackedConfigurationSaveAsync(
            ToolboxHistorySaveScope,
            T("Toolbox.Title", "工具箱"),
            ToolboxHistorySaveScope,
            ct => Runtime.SettingsFeatureService.SaveGlobalSettingAsync(ToolboxExecutionHistoryKey, payload, ct),
            cancellationToken);
    }

    private async Task PersistOperBoxAsync(CancellationToken cancellationToken)
    {
        var ownOpers = _operBoxOwnedById.Values
            .OrderByDescending(item => item.Rarity)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => new PersistedOperBoxOperator(
                item.Id,
                item.Name,
                item.Rarity,
                item.Elite,
                item.Level,
                true,
                item.Potential))
            .ToArray();
        var payload = new JsonObject
        {
            ["done"] = true,
            ["own_opers"] = JsonSerializer.SerializeToNode(ownOpers),
        };
        if (LastOperBoxSyncTime is not null)
        {
            payload["syncTime"] = LastOperBoxSyncTime.Value.ToString("O", CultureInfo.InvariantCulture);
        }

        _ = await RunTrackedConfigurationSaveAsync(
            $"{ToolboxLegacyResultScope}.OperBox",
            T("Toolbox.OperBox.Title", "干员识别"),
            $"{ToolboxLegacyResultScope}.OperBox.Save",
            ct => Runtime.SettingsFeatureService.SaveGlobalSettingAsync(LegacyConfigurationKeys.OperBoxData, payload.ToJsonString(), ct),
            cancellationToken);
    }

    private async Task PersistDepotAsync(CancellationToken cancellationToken)
    {
        var data = new JsonObject();
        foreach (var item in DepotResult.Where(item => item.Count >= 0))
        {
            data[item.Id] = item.Count;
        }

        var payload = new JsonObject
        {
            ["done"] = true,
            ["data"] = data.ToJsonString(),
        };
        if (LastDepotSyncTime is not null)
        {
            payload["syncTime"] = LastDepotSyncTime.Value.ToString("O", CultureInfo.InvariantCulture);
        }

        _ = await RunTrackedConfigurationSaveAsync(
            $"{ToolboxLegacyResultScope}.Depot",
            T("Toolbox.Depot.Title", "仓库识别"),
            $"{ToolboxLegacyResultScope}.Depot.Save",
            ct => Runtime.SettingsFeatureService.SaveGlobalSettingAsync(LegacyConfigurationKeys.DepotResult, payload.ToJsonString(), ct),
            cancellationToken);
    }

    private void LoadOperBoxDetails()
    {
        _operBoxOwnedById.Clear();
        _operBoxPotential.Clear();
        OperBoxHaveList.Clear();
        OperBoxNotHaveList.Clear();
        _operBoxListsMaterialized = false;
        LastOperBoxSyncTime = null;

        if (!Runtime.ConfigurationService.CurrentConfig.GlobalValues.TryGetValue(LegacyConfigurationKeys.OperBoxData, out var node) || node is null)
        {
            return;
        }

        try
        {
            var payload = node is JsonValue value && value.TryGetValue(out string? raw)
                ? raw
                : node.ToJsonString();
            if (string.IsNullOrWhiteSpace(payload))
            {
                return;
            }

            var parsed = JsonNode.Parse(payload);
            var ownNode = parsed switch
            {
                JsonArray legacyArray => legacyArray,
                JsonObject modernObject when modernObject["own_opers"] is JsonArray ownOpers => ownOpers,
                _ => null,
            };
            if (ownNode is null)
            {
                return;
            }

            var items = JsonSerializer.Deserialize<List<PersistedOperBoxOperator>>(ownNode.ToJsonString(), PersistedPayloadJsonOptions);
            if (items is not null && items.Count > 0)
            {
                foreach (var item in items.Where(item => item is not null && item.Own))
                {
                    _operBoxOwnedById[item!.Id] = new ToolboxOwnedOperatorState(item.Id, item.Name, item.Rarity, item.Elite, item.Level, item.Potential);
                    _operBoxPotential[item.Id] = item.Potential;
                }
            }

            if (parsed is JsonObject persistedObject)
            {
                var syncTime = ReadString(persistedObject, "syncTime");
                if (DateTimeOffset.TryParse(syncTime, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedSyncTime))
                {
                    LastOperBoxSyncTime = parsedSyncTime.ToUniversalTime();
                }
            }
        }
        catch
        {
            // Ignore incompatible persisted payloads.
        }
    }

    private void LoadDepotDetails()
    {
        DepotResult.Clear();
        if (!Runtime.ConfigurationService.CurrentConfig.GlobalValues.TryGetValue(LegacyConfigurationKeys.DepotResult, out var node) || node is null)
        {
            return;
        }

        try
        {
            var payload = node is JsonValue value && value.TryGetValue(out string? raw)
                ? JsonNode.Parse(raw ?? string.Empty) as JsonObject
                : node as JsonObject ?? JsonNode.Parse(node.ToJsonString()) as JsonObject;
            if (payload is not null)
            {
                ApplyDepotRecognition(payload, updateSyncTime: false);
            }
        }
        catch
        {
            // Ignore incompatible persisted payloads.
        }
    }

    private void LoadMiniGameEntries()
    {
        var clientType = ResolveClientType();
        MiniGameTaskList.Clear();
        foreach (var entry in ToolboxAssetCatalog.GetMiniGameEntries(clientType, _currentLanguage))
        {
            MiniGameTaskList.Add(entry);
        }

        if (!MiniGameTaskList.Any(entry => string.Equals(entry.Value, MiniGameTaskName, StringComparison.Ordinal)))
        {
            MiniGameTaskList.Insert(0, new ToolboxMiniGameEntry(MiniGameTaskName, MiniGameTaskName, string.Empty));
        }

        MiniGameTip = ResolveMiniGameTip(MiniGameTaskName);
    }

    private void UpdateMiniGameSelectionFromTaskName()
    {
        if (MiniGameTaskList.Any(entry => string.Equals(entry.Value, MiniGameTaskName, StringComparison.Ordinal)))
        {
            OnPropertyChanged(nameof(SelectedMiniGameEntry));
            return;
        }

        MiniGameTaskList.Insert(0, new ToolboxMiniGameEntry(MiniGameTaskName, MiniGameTaskName, string.Empty));
        OnPropertyChanged(nameof(SelectedMiniGameEntry));
    }

    private string ResolveMiniGameTip(string taskName)
    {
        var entry = MiniGameTaskList.FirstOrDefault(item => string.Equals(item.Value, taskName, StringComparison.Ordinal));
        if (entry is null)
        {
            return T("Toolbox.Tip.MiniGameNameEmpty", "Select a mini-game above to start.");
        }

        return string.IsNullOrWhiteSpace(entry.Tip)
            ? string.Format(
                CultureInfo.InvariantCulture,
                T("Toolbox.MiniGame.CurrentTask", "Current task: {0}"),
                entry.Display)
            : entry.Tip;
    }

    private void OnConnectionStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(ConnectionGameSharedStateViewModel.ClientType), StringComparison.Ordinal))
        {
            return;
        }

        LoadMiniGameEntries();
        OnPropertyChanged(nameof(SelectedMiniGameEntry));
    }

    private string T(string key, string fallback)
    {
        return _texts.GetOrDefault(key, fallback);
    }

    private IReadOnlyList<string> GetGachaTips()
    {
        return
        [
            T("Toolbox.Gacha.Tip.1", "Gacha running: peep mode has started automatically."),
            T("Toolbox.Gacha.Tip.2", "You can stop gacha directly on this page."),
            T("Toolbox.Gacha.Tip.3", "If FPS is too high, reduce the peep FPS target."),
            T("Toolbox.Gacha.Tip.4", "If emulator stutters, check screencap mode and connection quality first."),
            T("Toolbox.Gacha.Tip.5", "Gacha uses a custom task chain and will clean up automatically."),
        ];
    }

    private void RebuildMiniGameSecretFrontEventOptions()
    {
        _miniGameSecretFrontEventOptions.Clear();
        _miniGameSecretFrontEventOptions.Add(new ToolboxNamedOption(T("Toolbox.MiniGame.Event.None", "No preference"), string.Empty));
        _miniGameSecretFrontEventOptions.Add(new ToolboxNamedOption(T("Toolbox.MiniGame.Event.SupportPlatform", "Support platform"), "支援作战平台"));
        _miniGameSecretFrontEventOptions.Add(new ToolboxNamedOption(T("Toolbox.MiniGame.Event.Ranger", "Ranger"), "游侠"));
        _miniGameSecretFrontEventOptions.Add(new ToolboxNamedOption(T("Toolbox.MiniGame.Event.Phantom", "Phantom Trails"), "诡影迷踪"));
        OnPropertyChanged(nameof(MiniGameSecretFrontEventOptions));
    }

    private void LoadBridgeSettings()
    {
        ChooseLevel3 = ReadBoolSetting(LegacyConfigurationKeys.ChooseLevel3, true);
        ChooseLevel4 = ReadBoolSetting(LegacyConfigurationKeys.ChooseLevel4, true);
        ChooseLevel5 = ReadBoolSetting(LegacyConfigurationKeys.ChooseLevel5, true);
        ChooseLevel6 = ReadBoolSetting(LegacyConfigurationKeys.ChooseLevel6, true);
        RecruitLevel3Time = ReadIntSetting(LegacyConfigurationKeys.ToolBoxChooseLevel3Time, 540);
        RecruitLevel4Time = ReadIntSetting(LegacyConfigurationKeys.ToolBoxChooseLevel4Time, 540);
        RecruitLevel5Time = ReadIntSetting(LegacyConfigurationKeys.ToolBoxChooseLevel5Time, 540);
        RecruitAutoSetTime = ReadBoolSetting(LegacyConfigurationKeys.AutoSetTime, true);
        RecruitmentShowPotential = ReadBoolSetting(LegacyConfigurationKeys.RecruitmentShowPotential, true);

        GachaShowDisclaimerNoMore = ReadBoolSetting(LegacyConfigurationKeys.GachaShowDisclaimerNoMore, false);
        GachaShowDisclaimer = !GachaShowDisclaimerNoMore;
        PeepTargetFps = ReadIntSetting(LegacyConfigurationKeys.PeepTargetFps, 20);

        MiniGameTaskName = ReadStringSetting(LegacyConfigurationKeys.MiniGameTaskName, "SS@Store@Begin");
        MiniGameSecretFrontEnding = ReadStringSetting(LegacyConfigurationKeys.MiniGameSecretFrontEnding, "A");
        MiniGameSecretFrontEvent = ReadStringSetting(LegacyConfigurationKeys.MiniGameSecretFrontEvent, string.Empty);
    }

    private int ReadIntSetting(string key, int fallback)
    {
        if (!Runtime.ConfigurationService.CurrentConfig.GlobalValues.TryGetValue(key, out var node) || node is null)
        {
            return fallback;
        }

        return int.TryParse(NormalizeToken(node.ToString()), out var parsed) ? parsed : fallback;
    }

    private bool ReadBoolSetting(string key, bool fallback)
    {
        if (!Runtime.ConfigurationService.CurrentConfig.GlobalValues.TryGetValue(key, out var node) || node is null)
        {
            return fallback;
        }

        var normalized = NormalizeToken(node.ToString());
        if (bool.TryParse(normalized, out var parsed))
        {
            return parsed;
        }

        return int.TryParse(normalized, out var number) ? number != 0 : fallback;
    }

    private string ReadStringSetting(string key, string fallback)
    {
        if (!Runtime.ConfigurationService.CurrentConfig.GlobalValues.TryGetValue(key, out var node) || node is null)
        {
            return fallback;
        }

        var normalized = NormalizeToken(node.ToString());
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    private static bool TryResolveTool(int tabIndex, out ToolboxToolKind tool)
    {
        return ToolByTabIndex.TryGetValue(tabIndex, out tool);
    }

    private bool SetTrackedProperty<T>(ref T storage, T value)
    {
        if (!SetProperty(ref storage, value))
        {
            return false;
        }

        RefreshCurrentToolParametersPreview();
        return true;
    }

    private void RefreshCurrentToolParametersPreview()
    {
        CurrentToolParameters = TryResolveTool(SelectedTabIndex, out var tool)
            ? BuildCurrentParameterText(tool)
            : string.Empty;
    }

    private string BuildCurrentParameterText(ToolboxToolKind tool)
    {
        return tool switch
        {
            ToolboxToolKind.Recruit => string.Join(
                ';',
                new[]
                {
                    $"select={BuildRecruitSelectedLevelsSummary()}",
                    $"autoSetTime={RecruitAutoSetTime.ToString().ToLowerInvariant()}",
                    $"level3Time={RecruitLevel3Time}",
                    $"level4Time={RecruitLevel4Time}",
                    $"level5Time={RecruitLevel5Time}",
                    $"showPotential={RecruitmentShowPotential.ToString().ToLowerInvariant()}",
                }),
            ToolboxToolKind.OperBox => $"mode={NormalizeToken(OperBoxMode)}",
            ToolboxToolKind.Depot => $"format={NormalizeToken(DepotFormat)};topN={NormalizeToken(DepotTopNInput)}",
            ToolboxToolKind.Gacha => $"drawCount={NormalizeToken(GachaDrawCountInput)};showDisclaimerNoMore={GachaShowDisclaimerNoMore.ToString().ToLowerInvariant()}",
            ToolboxToolKind.VideoRecognition => $"targetFps={PeepTargetFps}",
            ToolboxToolKind.MiniGame => $"taskName={NormalizeToken(MiniGameTaskName)};secretFrontEnding={NormalizeToken(MiniGameSecretFrontEnding)};secretFrontEvent={NormalizeToken(MiniGameSecretFrontEvent)}",
            _ => string.Empty,
        };
    }

    private string BuildRecruitSelectedLevelsSummary()
    {
        var selected = new List<string>();
        if (ChooseLevel3)
        {
            selected.Add("3");
        }

        if (ChooseLevel4)
        {
            selected.Add("4");
        }

        if (ChooseLevel5)
        {
            selected.Add("5");
        }

        if (ChooseLevel6)
        {
            selected.Add("6");
        }

        return selected.Count == 0 ? "none" : string.Join(',', selected);
    }

    private static int NormalizeRecruitMinutes(int value)
    {
        return value switch
        {
            < 60 => 60,
            > 540 => 540,
            _ => value / 10 * 10,
        };
    }

    private static int NormalizeRecruitMinutePart(int value)
    {
        return Math.Clamp(value / 10 * 10, 0, 50);
    }

    private static int ClampRecruitHour(int hour, int minutePart)
    {
        var maxHour = minutePart > 0 ? 8 : 9;
        return Math.Clamp(hour, 1, maxHour);
    }

    private static bool TryParseRecruitMinutes(string value, out int parsed)
    {
        if (!TryParseInt(value, 60, 540, out parsed))
        {
            return false;
        }

        return parsed % 10 == 0;
    }

    private static bool TryParseInt(string text, int min, int max, out int value)
    {
        value = 0;
        if (!int.TryParse(NormalizeToken(text), out var parsed))
        {
            return false;
        }

        if (parsed < min || parsed > max)
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private string? ResolveProfileString(string key, string? legacyKey = null, string? fallback = null)
    {
        if (Runtime.ConfigurationService.TryGetCurrentProfile(out var profile))
        {
            if (TryReadProfileString(profile, key, out var value))
            {
                return value;
            }

            if (!string.IsNullOrWhiteSpace(legacyKey) && TryReadProfileString(profile, legacyKey!, out value))
            {
                return value;
            }
        }

        if (!string.IsNullOrWhiteSpace(legacyKey)
            && Runtime.ConfigurationService.CurrentConfig.GlobalValues.TryGetValue(legacyKey!, out var node)
            && node is JsonValue globalValue
            && globalValue.TryGetValue(out string? globalText)
            && !string.IsNullOrWhiteSpace(globalText))
        {
            return globalText.Trim();
        }

        return fallback;
    }

    private bool ResolveProfileBool(string key, bool fallback, string? legacyKey = null)
    {
        if (Runtime.ConfigurationService.TryGetCurrentProfile(out var profile))
        {
            if (TryReadProfileBool(profile, key, out var profileValue))
            {
                return profileValue;
            }

            if (!string.IsNullOrWhiteSpace(legacyKey) && TryReadProfileBool(profile, legacyKey!, out profileValue))
            {
                return profileValue;
            }
        }

        if (!string.IsNullOrWhiteSpace(legacyKey)
            && Runtime.ConfigurationService.CurrentConfig.GlobalValues.TryGetValue(legacyKey!, out var node)
            && TryReadBoolNode(node, out var globalValue))
        {
            return globalValue;
        }

        return fallback;
    }

    private static bool TryReadProfileString(UnifiedProfile profile, string key, out string value)
    {
        value = string.Empty;
        if (!profile.Values.TryGetValue(key, out var node) || node is not JsonValue jsonValue || !jsonValue.TryGetValue(out string? raw) || string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        value = raw.Trim();
        return true;
    }

    private static bool TryReadProfileBool(UnifiedProfile profile, string key, out bool value)
    {
        value = false;
        return profile.Values.TryGetValue(key, out var node) && TryReadBoolNode(node, out value);
    }

    private static bool TryReadBoolNode(JsonNode? node, out bool value)
    {
        value = false;
        if (node is not JsonValue jsonValue)
        {
            return false;
        }

        if (jsonValue.TryGetValue(out bool parsedBool))
        {
            value = parsedBool;
            return true;
        }

        if (jsonValue.TryGetValue(out string? parsedText))
        {
            if (bool.TryParse(parsedText, out parsedBool))
            {
                value = parsedBool;
                return true;
            }

            if (int.TryParse(parsedText, out var number))
            {
                value = number != 0;
                return true;
            }
        }

        return false;
    }

    private string BuildFailureContextDetails(ToolboxToolKind? tool, string errorCode, string stage)
    {
        return JsonSerializer.Serialize(new
        {
            tool = ToolNameOf(tool),
            selectedTabIndex = SelectedTabIndex,
            executionState = ExecutionState.ToString(),
            parameterSummary = BuildParameterSummary(CurrentToolParameters),
            errorCode,
            stage,
            occurredAt = DateTimeOffset.Now,
        });
    }

    private string BuildOperBoxExportText()
    {
        var operators = ToolboxAssetCatalog.GetOperators();
        var clientType = ResolveClientType();
        var language = _currentLanguage;
        var exportItems = new List<PersistedOperBoxOperator>();

        foreach (var asset in operators.Values
                     .Where(asset => ToolboxAssetCatalog.IsOperatorAvailableInClient(asset, clientType))
                     .OrderByDescending(asset => asset.Rarity)
                     .ThenBy(asset => asset.Id, StringComparer.Ordinal))
        {
            if (_operBoxOwnedById.TryGetValue(asset.Id, out var owned))
            {
                exportItems.Add(new PersistedOperBoxOperator(
                    owned.Id,
                    owned.Name,
                    owned.Rarity,
                    owned.Elite,
                    owned.Level,
                    true,
                    owned.Potential));
            }
            else
            {
                exportItems.Add(new PersistedOperBoxOperator(
                    asset.Id,
                    ToolboxAssetCatalog.GetLocalizedOperatorName(asset, language),
                    asset.Rarity,
                    0,
                    0,
                    false,
                    0));
            }
        }

        return JsonSerializer.Serialize(exportItems, new JsonSerializerOptions { WriteIndented = true });
    }

    private string BuildArkPlannerExportText()
    {
        var items = DepotResult
            .Where(item => item.Count >= 0)
            .Select(item => new JsonObject
            {
                ["id"] = item.Id,
                ["have"] = item.Count,
                ["name"] = item.Name,
            })
            .ToArray();

        return new JsonObject
        {
            ["@type"] = "@penguin-statistics/depot",
            ["items"] = new JsonArray(items),
        }.ToJsonString();
    }

    private string BuildLoliconExportText()
    {
        var data = new JsonObject();
        foreach (var item in DepotResult.Where(item => item.Count >= 0))
        {
            data[item.Id] = item.Count;
        }

        return data.ToJsonString();
    }

    private static void TryReadDepotCountsFromJson(string payload, IDictionary<string, int> counts)
    {
        if (JsonNode.Parse(payload) is not JsonObject data)
        {
            return;
        }

        foreach (var pair in data)
        {
            if (pair.Value is null)
            {
                continue;
            }

            if (pair.Value is JsonValue value && value.TryGetValue(out int intValue))
            {
                counts[pair.Key] = intValue;
                continue;
            }

            if (int.TryParse(pair.Value.ToString(), out intValue))
            {
                counts[pair.Key] = intValue;
            }
        }
    }

    private static IReadOnlyList<string> ReadStringArray(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return Array.Empty<string>();
        }

        return array
            .OfType<JsonValue>()
            .Select(value => value.TryGetValue(out string? text) ? NormalizeToken(text) : string.Empty)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToArray();
    }

    private static string ReadString(JsonObject node, string property)
    {
        return node[property] is JsonValue value && value.TryGetValue(out string? text)
            ? NormalizeToken(text)
            : string.Empty;
    }

    private static int ReadInt(JsonObject node, string property)
    {
        if (node[property] is not JsonValue value)
        {
            return 0;
        }

        if (value.TryGetValue(out int intValue))
        {
            return intValue;
        }

        return value.TryGetValue(out string? text) && int.TryParse(text, out intValue)
            ? intValue
            : 0;
    }

    private static bool ReadBool(JsonObject node, string property)
    {
        return TryReadBoolNode(node[property], out var value) && value;
    }

    private string GetToolDisplayName(ToolboxToolKind tool)
    {
        return GetToolDisplayName(tool, _texts);
    }

    private static string GetToolDisplayName(ToolboxToolKind tool, ToolboxLocalizationTextMap texts)
    {
        return tool switch
        {
            ToolboxToolKind.Recruit => texts.GetOrDefault("Toolbox.ToolName.Recruit", "Recruit Recognition"),
            ToolboxToolKind.OperBox => texts.GetOrDefault("Toolbox.ToolName.OperBox", "Operator Recognition"),
            ToolboxToolKind.Depot => texts.GetOrDefault("Toolbox.ToolName.Depot", "Depot Recognition"),
            ToolboxToolKind.Gacha => texts.GetOrDefault("Toolbox.ToolName.Gacha", "Gacha"),
            ToolboxToolKind.VideoRecognition => texts.GetOrDefault("Toolbox.ToolName.VideoRecognition", "Peep"),
            ToolboxToolKind.MiniGame => texts.GetOrDefault("Toolbox.ToolName.MiniGame", "Mini-Game"),
            _ => tool.ToString(),
        };
    }

    private static string ToolNameOf(ToolboxToolKind? tool)
    {
        return tool?.ToString() ?? "Unknown";
    }

    private static string ScopeOf(ToolboxToolKind? tool)
    {
        return tool is null ? "Toolbox.Unknown" : $"Toolbox.{tool}";
    }

    private void TrimExecutionHistory()
    {
        while (ExecutionHistory.Count > MaxHistoryCount)
        {
            ExecutionHistory.RemoveAt(ExecutionHistory.Count - 1);
        }
    }

    private static string NormalizeToken(string? text)
    {
        return string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim();
    }

    private static string BuildParameterSummary(string? text)
    {
        return BuildTextSummary(text, 180);
    }

    private static string BuildResultSummary(string? text)
    {
        return BuildTextSummary(text, 240);
    }

    private static string BuildTextSummary(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "none";
        }

        var normalized = text.Trim()
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\n', ';')
            .Replace('\t', ' ');
        return normalized.Length <= maxLength ? normalized : normalized[..(maxLength - 3)] + "...";
    }

    private static string MergeDetails(string context, string? details)
    {
        if (string.IsNullOrWhiteSpace(details))
        {
            return context;
        }

        return $"{context} | {details}";
    }

    private string FormatFailureMessage(string code, string message)
    {
        var fallback = string.IsNullOrWhiteSpace(message)
            ? T("Toolbox.Error.ExecutionFailedDefault", "Tool execution failed.")
            : message.Trim();
        return code switch
        {
            UiErrorCode.ToolboxInvalidParameters => string.Format(
                CultureInfo.InvariantCulture,
                T("Toolbox.Error.InvalidParameters", "Invalid parameters: {0} ({1})"),
                fallback,
                UiErrorCode.ToolboxInvalidParameters),
            UiErrorCode.ToolboxExecutionCancelled => string.Format(
                CultureInfo.InvariantCulture,
                T("Toolbox.Error.ExecutionCancelled", "Execution cancelled: {0} ({1})"),
                fallback,
                UiErrorCode.ToolboxExecutionCancelled),
            UiErrorCode.ToolNotSupported => string.Format(
                CultureInfo.InvariantCulture,
                T("Toolbox.Error.ToolNotSupported", "Tool not supported: {0} ({1})"),
                fallback,
                UiErrorCode.ToolNotSupported),
            _ when string.IsNullOrWhiteSpace(code) => string.Format(
                CultureInfo.InvariantCulture,
                T("Toolbox.Error.ExecutionFailed", "Tool execution failed: {0}"),
                fallback),
            _ => string.Format(
                CultureInfo.InvariantCulture,
                T("Toolbox.Error.ExecutionFailedWithCode", "Tool execution failed: {0} ({1})"),
                fallback,
                code),
        };
    }

    private static IBrush ResolveStarBrush(int star)
    {
        return star switch
        {
            >= 6 => Brushes.Gold,
            5 => Brushes.Orange,
            4 => Brushes.SkyBlue,
            3 => Brushes.LightGreen,
            _ => Brushes.LightGray,
        };
    }

    private static double ResolvePanelWidth(int count, double itemWidth)
    {
        var columns = Math.Clamp(count, 1, 5);
        return columns * itemWidth;
    }

    private static void ReplaceCollectionItems<T>(ObservableCollection<T> collection, IEnumerable<T> items)
    {
        if (collection is BatchedObservableCollection<T> batched)
        {
            batched.ReplaceAll(items);
            return;
        }

        collection.Clear();
        foreach (var item in items)
        {
            collection.Add(item);
        }
    }
}

public sealed record ToolExecutionRecord(
    DateTimeOffset ExecutedAt,
    string ToolName,
    string ParameterSummary,
    bool Success,
    string ResultSummary,
    string ErrorCode)
{
    public static ToolExecutionRecord Succeeded(string toolName, string parameterSummary, string resultSummary)
        => new(DateTimeOffset.Now, toolName, parameterSummary, true, resultSummary, string.Empty);

    public static ToolExecutionRecord Failed(string toolName, string parameterSummary, string resultSummary, string errorCode)
        => new(DateTimeOffset.Now, toolName, parameterSummary, false, resultSummary, errorCode);

    public bool HasErrorCode => !string.IsNullOrWhiteSpace(ErrorCode);
}

public sealed record PersistedToolExecutionRecord(
    DateTimeOffset ExecutedAt,
    string ToolName,
    string ParameterSummary,
    bool Success,
    string ResultSummary,
    string ErrorCode);

public enum ToolboxExecutionState
{
    Idle = 0,
    Executing = 1,
    Succeeded = 2,
    Failed = 3,
}

public enum RecruitResultLineKind
{
    Plain = 0,
    TagLine = 1,
    OperatorLine = 2,
}

public enum RecruitResultSegmentKind
{
    Plain = 0,
    Level = 1,
    Tag = 2,
    Operator = 3,
}

public sealed class RecruitResultLineViewModel : ObservableObject
{
    public RecruitResultLineViewModel(
        IEnumerable<RecruitResultSegmentViewModel> segments,
        RecruitResultLineKind kind = RecruitResultLineKind.Plain,
        double spacerHeight = 0)
    {
        Segments = new ObservableCollection<RecruitResultSegmentViewModel>(segments);
        Kind = kind;
        SpacerHeight = spacerHeight;
    }

    public ObservableCollection<RecruitResultSegmentViewModel> Segments { get; }

    public RecruitResultLineKind Kind { get; }

    public bool IsTagLine => Kind == RecruitResultLineKind.TagLine;

    public bool IsOperatorLine => Kind == RecruitResultLineKind.OperatorLine;

    public bool HasSegments => Segments.Count > 0;

    public double SpacerHeight { get; }

    public bool HasSpacer => SpacerHeight > 0;

    public string Text => string.Join("    ", Segments.Select(segment => segment.Text));

    public IBrush? Foreground => Segments.LastOrDefault()?.Foreground;

    public static RecruitResultLineViewModel CreateSpacer(double spacerHeight = 10)
        => new([], spacerHeight: spacerHeight);
}

public sealed class RecruitResultSegmentViewModel : ObservableObject
{
    private string _text;
    private IBrush? _foreground;

    public RecruitResultSegmentViewModel(
        string text,
        IBrush? foreground,
        RecruitResultSegmentKind kind = RecruitResultSegmentKind.Plain)
    {
        _text = text;
        _foreground = foreground;
        Kind = kind;
    }

    public RecruitResultSegmentKind Kind { get; }

    public bool IsLevelToken => Kind == RecruitResultSegmentKind.Level;

    public bool IsTagToken => Kind == RecruitResultSegmentKind.Tag;

    public bool IsOperatorToken => Kind == RecruitResultSegmentKind.Operator;

    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
    }

    public IBrush? Foreground
    {
        get => _foreground;
        set => SetProperty(ref _foreground, value);
    }
}

public sealed class OperBoxOperatorItemViewModel : ObservableObject
{
    private int _elite;
    private int _level;
    private int _potential;
    private readonly string _ownedSubtitleTemplate;
    private readonly string _notOwnedSubtitleTemplate;

    public OperBoxOperatorItemViewModel(
        string id,
        string name,
        int rarity,
        int elite,
        int level,
        int potential,
        bool own,
        string ownedSubtitleTemplate,
        string notOwnedSubtitleTemplate)
    {
        Id = id;
        Name = name;
        Rarity = rarity;
        RarityBrush = ResolveRarityBrush(rarity);
        _elite = elite;
        _level = level;
        _potential = potential;
        Own = own;
        _ownedSubtitleTemplate = string.IsNullOrWhiteSpace(ownedSubtitleTemplate)
            ? "{0}★ / Elite {1} / Level {2} / Potential {3}"
            : ownedSubtitleTemplate;
        _notOwnedSubtitleTemplate = string.IsNullOrWhiteSpace(notOwnedSubtitleTemplate)
            ? "{0}★ / Not owned"
            : notOwnedSubtitleTemplate;
    }

    public string Id { get; }

    public string Name { get; }

    public int Rarity { get; }

    public IBrush RarityBrush { get; }

    public bool Own { get; }

    public int Elite
    {
        get => _elite;
        set
        {
            if (SetProperty(ref _elite, value))
            {
                OnPropertyChanged(nameof(EliteIconImage));
                OnPropertyChanged(nameof(Subtitle));
            }
        }
    }

    public int Level
    {
        get => _level;
        set
        {
            if (SetProperty(ref _level, value))
            {
                OnPropertyChanged(nameof(LevelDisplay));
                OnPropertyChanged(nameof(Subtitle));
            }
        }
    }

    public int Potential
    {
        get => _potential;
        set
        {
            if (SetProperty(ref _potential, value))
            {
                OnPropertyChanged(nameof(PotentialIconImage));
                OnPropertyChanged(nameof(Subtitle));
            }
        }
    }

    public string RarityStars => Rarity <= 0 ? string.Empty : new string('★', Rarity);

    public string LevelDisplay => $"Lv.{Level}";

    public Bitmap? EliteIconImage => ToolboxAssetCatalog.ResolveOperatorEliteBitmap(Elite);

    public Bitmap? PotentialIconImage => ToolboxAssetCatalog.ResolveOperatorPotentialBitmap(Potential);

    public string Subtitle => Own
        ? string.Format(CultureInfo.InvariantCulture, _ownedSubtitleTemplate, Rarity, Elite, Level, Potential)
        : string.Format(CultureInfo.InvariantCulture, _notOwnedSubtitleTemplate, Rarity);

    private static int NormalizePotential(int potential)
    {
        return potential is >= 1 and <= 6 ? potential : 1;
    }

    private static IBrush ResolveRarityBrush(int rarity)
    {
        return rarity switch
        {
            >= 6 => Brushes.Gold,
            5 => Brushes.Orange,
            4 => Brushes.SkyBlue,
            3 => Brushes.LightGreen,
            _ => Brushes.LightGray,
        };
    }
}

internal sealed class BatchedObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

public sealed class DepotItemViewModel : ObservableObject
{
    private int _count;
    private readonly Bitmap? _itemImage;

    public DepotItemViewModel(string id, string name, int count, string? imagePath)
    {
        Id = id;
        Name = name;
        _count = count;
        ImagePath = imagePath;
        _itemImage = ToolboxAssetCatalog.ResolveItemBitmap(id);
    }

    public string Id { get; }

    public string Name { get; }

    public string? ImagePath { get; }

    public Bitmap? ItemImage => _itemImage;

    public int Count
    {
        get => _count;
        set
        {
            if (SetProperty(ref _count, value))
            {
                OnPropertyChanged(nameof(DisplayCount));
            }
        }
    }

    public string DisplayCount => Count.ToString(CultureInfo.InvariantCulture);
}

file sealed record RecruitOperatorProjection(
    JsonObject Operator,
    int Level,
    int Potential);

public sealed record ToolboxNamedOption(string Label, string Value)
{
    public override string ToString() => Label;
}

internal sealed record ToolboxOwnedOperatorState(
    string Id,
    string Name,
    int Rarity,
    int Elite,
    int Level,
    int Potential);

internal sealed record PersistedOperBoxOperator(
    string Id,
    string Name,
    int Rarity,
    int Elite,
    int Level,
    bool Own,
    int Potential);

internal static class ToolboxUiOperationResultExtensions
{
    public static UiOperationResult ToUntyped<T>(this UiOperationResult<T> result)
    {
        return result.Success
            ? UiOperationResult.Ok(result.Message)
            : UiOperationResult.Fail(
                result.Error?.Code ?? UiErrorCode.UiOperationFailed,
                result.Message,
                result.Error?.Details);
    }
}
