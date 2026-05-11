using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
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
    private const int PeepPreviewPixelWidth = 1280;
    private const int PeepPreviewPixelHeight = 720;
    private const int PeepPreviewPixelChannels = 3;
    private const int PeepBitmapCacheSize = 3;
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
        nameof(RecruitStartRecognitionText),
        nameof(OperBoxStartRecognitionText),
        nameof(DepotStartRecognitionText),
        nameof(RecruitPotentialTip),
        nameof(OperBoxCopyToClipboardText),
        nameof(OperBoxTipText),
        nameof(OperBoxNotHaveHeader),
        nameof(OperBoxHaveHeader),
        nameof(LastOperBoxSyncTimeText),
        nameof(LastOperBoxSyncDisplayText),
        nameof(DepotTipText),
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
    private const double DepotGroupPanelItemWidth = 194d;
    private const double DepotGroupPanelColumnGap = 16d;
    private const double DepotGroupPanelHorizontalInset = 16d;

    private readonly ConnectionGameSharedStateViewModel? _connectionState;
    private readonly IAppDialogService _dialogService;
    private readonly Func<string, CancellationToken, Task>? _stopRunOwnerAsync;
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
    private readonly WriteableBitmap?[] _peepBitmapCache = new WriteableBitmap?[PeepBitmapCacheSize];
    private int _callbackDrainScheduled;
    private int _peepBitmapSequence;
    private DateTimeOffset _lastPeepFpsWindowStartedAt = DateTimeOffset.MinValue;
    private int _peepFramesInWindow;
    private int _selectedTabIndex;
    private string _resultText = string.Empty;
    private bool _disclaimerAccepted;
    private string _currentToolParameters = string.Empty;
    private ToolboxExecutionState _executionState;
    private string _lastExecutionErrorCode = string.Empty;
    private DateTimeOffset? _lastExecutionAt;
    private ToolboxToolKind? _hoveredStopTool;

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
        IAppDialogService? dialogService = null,
        Func<string, CancellationToken, Task>? stopRunOwnerAsync = null)
        : base(runtime)
    {
        _connectionState = connectionState;
        _dialogService = dialogService ?? NoOpAppDialogService.Instance;
        _stopRunOwnerAsync = stopRunOwnerAsync;
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
        _operBoxInfo = string.Empty;
        _depotInfo = string.Empty;
        _gachaInfo = T("Toolbox.Tip.GachaInit", "Gacha hint.");
        _miniGameTip = T("Toolbox.Tip.MiniGameNameEmpty", "Select a mini-game above to start.");

        ExecutionHistory = new ObservableCollection<ToolExecutionRecord>();
        ExecutionHistory.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasExecutionHistory));
        RecruitResultLines = new BatchedObservableCollection<RecruitResultLineViewModel>();
        RecruitResultGroups = new BatchedObservableCollection<RecruitResultGroupViewModel>();
        OperBoxHaveList = new BatchedObservableCollection<OperBoxOperatorItemViewModel>();
        OperBoxNotHaveList = new BatchedObservableCollection<OperBoxOperatorItemViewModel>();
        OperBoxHaveGroups = new BatchedObservableCollection<OperBoxOperatorGroupViewModel>();
        OperBoxNotHaveGroups = new BatchedObservableCollection<OperBoxOperatorGroupViewModel>();
        DepotResult = new BatchedObservableCollection<DepotItemViewModel>();
        DepotGroups = new BatchedObservableCollection<DepotItemGroupViewModel>();
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
        RecruitResultGroups.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRecruitResults));
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
            RebuildDepotGroups();
        };

        runtime.SessionService.CallbackProjected += OnSessionCallbackProjected;
        runtime.SessionService.SessionStateChanged += OnSessionStateChanged;
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

    public string RecruitStartRecognitionText => ResolveRecognitionActionText(ToolboxToolKind.Recruit);

    public string OperBoxStartRecognitionText => ResolveRecognitionActionText(ToolboxToolKind.OperBox);

    public string DepotStartRecognitionText => ResolveRecognitionActionText(ToolboxToolKind.Depot);

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

    public ObservableCollection<RecruitResultGroupViewModel> RecruitResultGroups { get; }

    public ObservableCollection<OperBoxOperatorItemViewModel> OperBoxHaveList { get; }

    public ObservableCollection<OperBoxOperatorItemViewModel> OperBoxNotHaveList { get; }

    public ObservableCollection<OperBoxOperatorGroupViewModel> OperBoxHaveGroups { get; }

    public ObservableCollection<OperBoxOperatorGroupViewModel> OperBoxNotHaveGroups { get; }

    public ObservableCollection<DepotItemViewModel> DepotResult { get; }

    public ObservableCollection<DepotItemGroupViewModel> DepotGroups { get; }

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
                NotifyToolboxExecutionStateChanged();
            }
        }
    }

    public string CurrentToolParameters
    {
        get => _currentToolParameters;
        private set => SetProperty(ref _currentToolParameters, value ?? string.Empty);
    }

    public bool IsExecuting => ExecutionState == ToolboxExecutionState.Executing;

    public bool IsRecruitExecuting => _activeTool == ToolboxToolKind.Recruit && IsExecuting;

    public bool IsOperBoxExecuting => _activeTool == ToolboxToolKind.OperBox && IsExecuting;

    public bool IsDepotExecuting => _activeTool == ToolboxToolKind.Depot && IsExecuting;

    public bool CanStartRecruitRecognition => true;

    public bool CanStartOperBoxRecognition => true;

    public bool CanStartDepotRecognition => true;

    public bool HasExecutionHistory => ExecutionHistory.Count > 0;

    public bool IsToolboxBusy => _activeTool is not null || Peeping || IsPeepTransitioning || IsGachaInProgress;

    private bool IsAnotherRunOwnerActive
    {
        get
        {
            var owner = Runtime.SessionService.CurrentRunOwner;
            return !string.IsNullOrWhiteSpace(owner)
                && !Runtime.SessionService.IsRunOwner(ToolboxRunOwner);
        }
    }

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

    public bool HasRecruitResults => RecruitResultLines.Count > 0 || RecruitResultGroups.Count > 0;

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
        set
        {
            if (SetProperty(ref _operBoxInfo, value))
            {
                OnPropertyChanged(nameof(HasOperBoxInfo));
            }
        }
    }

    public bool HasOperBoxInfo => !string.IsNullOrWhiteSpace(OperBoxInfo);

    public string OperBoxTipText => T("Toolbox.Tip.OperBoxRecognition", "Special markers may affect recognition accuracy.");

    public int OperBoxSelectedIndex
    {
        get => _operBoxSelectedIndex;
        set
        {
            if (SetProperty(ref _operBoxSelectedIndex, Math.Clamp(value, 0, 1)))
            {
                OnPropertyChanged(nameof(IsOperBoxNotHaveSelected));
                OnPropertyChanged(nameof(IsOperBoxHaveSelected));
            }
        }
    }

    public bool IsOperBoxNotHaveSelected => OperBoxSelectedIndex == 0;

    public bool IsOperBoxHaveSelected => OperBoxSelectedIndex == 1;

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
        set
        {
            if (SetProperty(ref _depotInfo, value))
            {
                OnPropertyChanged(nameof(HasDepotInfo));
            }
        }
    }

    public bool HasDepotInfo => !string.IsNullOrWhiteSpace(DepotInfo);

    public string DepotTipText => T("Toolbox.Tip.DepotRecognition", "This feature is experimental. Please verify recognition results.");

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
                OnPropertyChanged(nameof(ShowGachaIdleControls));
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
            OnPropertyChanged(nameof(ShowGachaIdleControls));
            NotifyToolboxBusyStateChanged();
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

    public bool ShowGachaIdleControls => ShowGachaControls && !ShowGachaPreview;

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
            OnPropertyChanged(nameof(ShowGachaIdleControls));
            NotifyToolboxBusyStateChanged();
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
                OnPropertyChanged(nameof(PeepCommandText));
                NotifyToolboxBusyStateChanged();
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
                OnPropertyChanged(nameof(ShowGachaIdleControls));
                if (previous is not null
                    && !ReferenceEquals(previous, value)
                    && !IsCachedPeepBitmap(previous))
                {
                    previous.Dispose();
                }
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

    public string PeepCommandText => IsPeepTransitioning
        ? Peeping || IsGachaInProgress
            ? T("Toolbox.Action.PeepStopping", "停止中...")
            : T("Toolbox.Action.PeepStarting", "启动中...")
        : !Peeping
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

    public bool IsMiniGameRunning => _activeTool == ToolboxToolKind.MiniGame && IsExecuting;

    public string MiniGameCommandText => IsMiniGameRunning
        ? IsToolActionStopHovered(ToolboxToolKind.MiniGame)
            ? StopActionText
            : T("Toolbox.Action.Running", "Running...")
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

    public void SetToolActionHover(ToolboxToolKind tool, bool hovering)
    {
        if (hovering)
        {
            if (_activeTool != tool || !IsExecuting)
            {
                return;
            }

            if (_hoveredStopTool == tool)
            {
                return;
            }

            _hoveredStopTool = tool;
        }
        else
        {
            if (_hoveredStopTool != tool)
            {
                return;
            }

            _hoveredStopTool = null;
        }

        NotifyToolActionTextChanged(tool);
    }

    public async Task StartRecruitAsync(CancellationToken cancellationToken = default)
    {
        var request = BuildRecruitRequest();
        await DispatchToolAsync(
            ToolboxToolKind.Recruit,
            request,
            PrepareRecruitForStart,
            cancellationToken,
            transitionBeforeConnect: true);
    }

    public async Task StartOperBoxAsync(CancellationToken cancellationToken = default)
    {
        var request = new ToolboxDispatchRequest(
            ToolboxToolKind.OperBox,
            ParameterSummary: BuildCurrentParameterText(ToolboxToolKind.OperBox));
        await DispatchToolAsync(
            ToolboxToolKind.OperBox,
            request,
            PrepareOperBoxForStart,
            cancellationToken,
            transitionBeforeConnect: true);
    }

    public async Task StartDepotAsync(CancellationToken cancellationToken = default)
    {
        var request = new ToolboxDispatchRequest(
            ToolboxToolKind.Depot,
            ParameterSummary: BuildCurrentParameterText(ToolboxToolKind.Depot));
        await DispatchToolAsync(
            ToolboxToolKind.Depot,
            request,
            PrepareDepotForStart,
            cancellationToken,
            transitionBeforeConnect: true);
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
                await ApplyToolboxBusyAsync(
                    ToolboxToolKind.VideoRecognition,
                    UiOperationResult.Fail(
                        UiErrorCode.ToolboxExecutionFailed,
                        string.Format(
                            CultureInfo.InvariantCulture,
                            T("Toolbox.Error.BusyWithTool", "`{0}` is running. Stop it first before peep."),
                            _activeTool)),
                    cancellationToken);
                return;
            }

            if (!Runtime.SessionService.TryBeginRun(
                    ToolboxRunOwner,
                    GetToolDisplayName(ToolboxToolKind.VideoRecognition),
                    out var currentOwner))
            {
                await ApplyToolboxBusyAsync(
                    ToolboxToolKind.VideoRecognition,
                    UiOperationResult.Fail(
                        UiErrorCode.ToolboxExecutionFailed,
                        string.Format(
                        CultureInfo.InvariantCulture,
                        T("Toolbox.Error.OwnerRunning", "`{0}` is already running."),
                        currentOwner)),
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
            ManualStoppedText,
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
                    ManualStoppedText,
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
        bool startPeepAfterDispatch = false,
        bool transitionBeforeConnect = false)
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

        if (!Runtime.SessionService.TryBeginRun(ToolboxRunOwner, GetToolDisplayName(tool), out var currentOwner))
        {
            await ApplyToolboxBusyAsync(
                tool,
                UiOperationResult.Fail(
                    UiErrorCode.ToolboxExecutionFailed,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        T("Toolbox.Error.OwnerRunning", "`{0}` is already running."),
                        currentOwner)),
                cancellationToken);
            return;
        }

        if (transitionBeforeConnect)
        {
            prepareUiAction();
            TransitionToExecuting(tool, request.ParameterSummary ?? CurrentToolParameters);
        }

        var connectResult = await EnsureConnectedAsync(cancellationToken);
        if (!connectResult.Success)
        {
            Runtime.SessionService.EndRun(ToolboxRunOwner);
            ClearActiveToolState(tool);
            await ApplyFailureAsync(tool, connectResult, "connect", cancellationToken);
            return;
        }

        await PersistBridgeSettingsForToolAsync(tool, cancellationToken);

        if (!transitionBeforeConnect)
        {
            prepareUiAction();
            TransitionToExecuting(tool, request.ParameterSummary ?? CurrentToolParameters);
        }

        UiOperationResult<ToolboxDispatchResult> dispatchResult;
        try
        {
            dispatchResult = await Runtime.ToolboxFeatureService.DispatchToolAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Runtime.SessionService.EndRun(ToolboxRunOwner);
            ClearActiveToolState(tool);
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
            ClearActiveToolState(tool);
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
            ClearActiveToolState(tool);
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
        RecruitResultGroups.Clear();
        _lastRecruitResult = null;
    }

    private void PrepareOperBoxForStart()
    {
        OperBoxInfo = T("Toolbox.Status.Recognizing", "Recognizing...");
        _operBoxOwnedById.Clear();
        _operBoxPotential.Clear();
        OperBoxHaveList.Clear();
        OperBoxNotHaveList.Clear();
        OperBoxHaveGroups.Clear();
        OperBoxNotHaveGroups.Clear();
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
        ReplaceCollectionItems(DepotResult, Array.Empty<DepotItemViewModel>());
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
        NotifyToolActionTextChanged(tool);
        NotifyToolboxBusyStateChanged();
    }

    private void ClearActiveToolState(ToolboxToolKind expectedTool)
    {
        if (_activeTool != expectedTool)
        {
            return;
        }

        _activeTool = null;
        if (_hoveredStopTool == expectedTool)
        {
            _hoveredStopTool = null;
        }

        _lastDispatchedParameterSummary = string.Empty;
        OnPropertyChanged(nameof(IsMiniGameRunning));
        OnPropertyChanged(nameof(MiniGameCommandText));
        NotifyToolActionTextChanged(expectedTool);
        NotifyToolboxBusyStateChanged();
    }

    private void NotifyToolboxBusyStateChanged()
    {
        OnPropertyChanged(nameof(IsToolboxBusy));
        OnPropertyChanged(nameof(CanStartRecruitRecognition));
        OnPropertyChanged(nameof(CanStartOperBoxRecognition));
        OnPropertyChanged(nameof(CanStartDepotRecognition));
        NotifyToolboxExecutionStateChanged();
    }

    private void NotifyToolboxExecutionStateChanged()
    {
        OnPropertyChanged(nameof(IsRecruitExecuting));
        OnPropertyChanged(nameof(IsOperBoxExecuting));
        OnPropertyChanged(nameof(IsDepotExecuting));
        OnPropertyChanged(nameof(CanStartRecruitRecognition));
        OnPropertyChanged(nameof(CanStartOperBoxRecognition));
        OnPropertyChanged(nameof(CanStartDepotRecognition));
        OnPropertyChanged(nameof(RecruitStartRecognitionText));
        OnPropertyChanged(nameof(OperBoxStartRecognitionText));
        OnPropertyChanged(nameof(DepotStartRecognitionText));
        OnPropertyChanged(nameof(MiniGameCommandText));
    }

    private void NotifyToolActionTextChanged(ToolboxToolKind tool)
    {
        switch (tool)
        {
            case ToolboxToolKind.Recruit:
                OnPropertyChanged(nameof(RecruitStartRecognitionText));
                break;
            case ToolboxToolKind.OperBox:
                OnPropertyChanged(nameof(OperBoxStartRecognitionText));
                break;
            case ToolboxToolKind.Depot:
                OnPropertyChanged(nameof(DepotStartRecognitionText));
                break;
            case ToolboxToolKind.MiniGame:
                OnPropertyChanged(nameof(MiniGameCommandText));
                break;
        }
    }

    private void OnSessionStateChanged(SessionState _)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            NotifyToolboxExecutionStateChanged();
            return;
        }

        Dispatcher.UIThread.Post(NotifyToolboxExecutionStateChanged, DispatcherPriority.Background);
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
            return _connectionState.EffectiveConnectConfig;
        }

        var connectConfig = ResolveProfileString("ConnectConfig", LegacyConfigurationKeys.ConnectConfig) ?? "General";
        var playCoverScreencapMode = ResolveProfileString("PlayCoverScreencapMode", "PlayCoverScreencapMode");
        return PlayCoverConnectConfigResolver.ResolveEffectiveConnectConfig(connectConfig, playCoverScreencapMode);
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
        var itemAssets = ToolboxAssetCatalog.GetItemAssets(_currentLanguage);
        var items = snapshot
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair =>
            {
                itemAssets.TryGetValue(pair.Key, out var asset);
                return new DepotItemViewModel(
                    pair.Key,
                    asset?.Name ?? (itemNames.TryGetValue(pair.Key, out var name) ? name : pair.Key),
                    pair.Value,
                    ToolboxAssetCatalog.ResolveItemImagePath(pair.Key),
                    asset?.ClassifyType,
                    asset?.SortId ?? 0);
            })
            .ToArray();
        ReplaceCollectionItems(DepotResult, items);
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

        var lines = new List<RecruitResultLineViewModel>();
        var groups = new List<RecruitResultGroupViewModel>();
        var language = _currentLanguage;
        var operators = ToolboxAssetCatalog.GetOperators();

        foreach (var comboNode in resultArray ?? [])
        {
            if (comboNode is not JsonObject combo)
            {
                continue;
            }

            var tagLevel = ReadInt(combo, "level");
            var tags = ReadStringArray(combo["tags"]);
            var recruitCards = new List<RecruitOperatorCardViewModel>();
            var tagSegments = new List<RecruitResultSegmentViewModel>
            {
                new($"{tagLevel}★", null, RecruitResultSegmentKind.Level),
            };
            tagSegments.AddRange(tags.Select(tag => new RecruitResultSegmentViewModel(
                tag,
                null,
                RecruitResultSegmentKind.Tag)));
            lines.Add(new RecruitResultLineViewModel(tagSegments, RecruitResultLineKind.TagLine));

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
                    && operators.TryGetValue(operId, out var asset))
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
                recruitCards.Add(new RecruitOperatorCardViewModel(
                    operName,
                    operLevel,
                    ResolveProfessionText(!string.IsNullOrWhiteSpace(operId) && operators.TryGetValue(operId, out var cardAsset)
                        ? cardAsset.Profession
                        : string.Empty),
                    !string.IsNullOrWhiteSpace(operId) && operators.TryGetValue(operId, out var iconAsset)
                        ? iconAsset.Profession
                        : string.Empty,
                    suffix.Trim(),
                    ResolveRarityAccentBrush(operLevel)));
            }

            if (operatorSegments.Count > 0)
            {
                lines.Add(new RecruitResultLineViewModel(
                    operatorSegments,
                    RecruitResultLineKind.OperatorLine));
            }

            if (recruitCards.Count > 0)
            {
                groups.Add(new RecruitResultGroupViewModel(
                    tagLevel,
                    $"{tagLevel}★",
                    tags,
                    ResolveRarityAccentBrush(tagLevel),
                    SortRecruitOperatorCards(recruitCards)));
            }

            lines.Add(RecruitResultLineViewModel.CreateSpacer());
        }

        ReplaceCollectionItems(RecruitResultLines, lines);
        ReplaceCollectionItems(
            RecruitResultGroups,
            groups
                .OrderBy(group => ResolveRecruitTagSortRank(group.TagLevel))
                .ThenByDescending(group => group.TagLevel));
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
        OperBoxInfo = T("Toolbox.Status.RecognitionCompleted", "Recognition completed.");
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
            var profession = operators.TryGetValue(owned.Id, out var professionAsset)
                ? professionAsset.Profession
                : string.Empty;

            haveItems.Add(new OperBoxOperatorItemViewModel(
                owned.Id,
                displayName,
                rarity,
                ResolveProfessionText(profession),
                profession,
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
                ResolveProfessionText(asset.Profession),
                asset.Profession,
                elite: 0,
                level: 0,
                potential: 0,
                own: false,
                ownedSubtitleTemplate,
                notOwnedSubtitleTemplate));
        }

        ReplaceCollectionItems(OperBoxHaveList, haveItems);
        ReplaceCollectionItems(OperBoxNotHaveList, notHaveItems);
        ReplaceCollectionItems(OperBoxHaveGroups, GroupOperBoxOperators(haveItems));
        ReplaceCollectionItems(OperBoxNotHaveGroups, GroupOperBoxOperators(notHaveItems));
        _operBoxListsMaterialized = haveItems.Count > 0 || notHaveItems.Count > 0;
        OperBoxSelectedIndex = OperBoxNotHaveList.Count > 0 ? 0 : 1;
    }

    private void ApplyDepotRecognition(JsonObject details, bool updateSyncTime)
    {
        var counts = ParseDepotCounts(details);
        var itemNames = ToolboxAssetCatalog.GetItemNames(_currentLanguage);
        var itemAssets = ToolboxAssetCatalog.GetItemAssets(_currentLanguage);
        var items = new List<DepotItemViewModel>(counts.Count);

        foreach (var pair in counts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            itemAssets.TryGetValue(pair.Key, out var asset);
            if (pair.Value > Runtime.AchievementTrackerService.GetProgress("WarehouseMiser"))
            {
                _ = Runtime.AchievementTrackerService.SetProgress("WarehouseMiser", pair.Value);
            }

            items.Add(new DepotItemViewModel(
                pair.Key,
                asset?.Name ?? (itemNames.TryGetValue(pair.Key, out var name) ? name : pair.Key),
                pair.Value,
                ToolboxAssetCatalog.ResolveItemImagePath(pair.Key),
                asset?.ClassifyType,
                asset?.SortId ?? 0));
        }

        ReplaceCollectionItems(DepotResult, items);

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

        var itemAssets = ToolboxAssetCatalog.GetItemAssets(_currentLanguage);
        var itemNames = ToolboxAssetCatalog.GetItemNames(_currentLanguage);
        var countsById = DepotResult.ToDictionary(item => item.Id, item => item.Count, StringComparer.Ordinal);
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

            if (countsById.TryGetValue(itemId, out var existingCount))
            {
                var newCount = existingCount + addQuantity;
                countsById[itemId] = newCount;
                if (newCount > Runtime.AchievementTrackerService.GetProgress("WarehouseMiser"))
                {
                    _ = Runtime.AchievementTrackerService.SetProgress("WarehouseMiser", newCount);
                }
            }
            else
            {
                countsById[itemId] = addQuantity;
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

        var items = countsById
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair =>
            {
                itemAssets.TryGetValue(pair.Key, out var asset);
                return new DepotItemViewModel(
                    pair.Key,
                    asset?.Name ?? (itemNames.TryGetValue(pair.Key, out var name) ? name : pair.Key),
                    pair.Value,
                    ToolboxAssetCatalog.ResolveItemImagePath(pair.Key),
                    asset?.ClassifyType,
                    asset?.SortId ?? 0);
            })
            .ToArray();
        ReplaceCollectionItems(DepotResult, items);

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
        if (!success && string.Equals(errorCode, UiErrorCode.ToolboxExecutionCancelled, StringComparison.Ordinal))
        {
            ApplyStoppedStatusToTool(tool, message);
        }

        ExecutionHistory.Insert(0, success
            ? ToolExecutionRecord.Succeeded(GetToolDisplayName(tool), _lastDispatchedParameterSummary, BuildResultSummary(message))
            : ToolExecutionRecord.Failed(GetToolDisplayName(tool), _lastDispatchedParameterSummary, BuildResultSummary(message), string.IsNullOrWhiteSpace(errorCode) ? UiErrorCode.ToolboxExecutionFailed : errorCode));
        TrimExecutionHistory();
        _ = PersistExecutionHistoryAsync(CancellationToken.None);

        Runtime.SessionService.EndRun(ToolboxRunOwner);
        ClearActiveToolState(tool);
    }

    private string StopActionText => T("Toolbox.Action.Stop", "Stop");

    private string ManualStoppedText => T("Toolbox.Status.ManuallyStopped", "Manually stopped.");

    private string ResolveRecognitionActionText(ToolboxToolKind tool)
    {
        if (_activeTool != tool || !IsExecuting)
        {
            return StartRecognitionText;
        }

        return IsToolActionStopHovered(tool)
            ? StopActionText
            : T("Toolbox.Action.Recognizing", "Recognizing...");
    }

    private bool IsToolActionStopHovered(ToolboxToolKind tool)
    {
        return _hoveredStopTool == tool && _activeTool == tool && IsExecuting;
    }

    private void ApplyStoppedStatusToTool(ToolboxToolKind tool, string message)
    {
        switch (tool)
        {
            case ToolboxToolKind.Recruit:
                RecruitInfo = message;
                break;
            case ToolboxToolKind.OperBox:
                OperBoxInfo = message;
                break;
            case ToolboxToolKind.Depot:
                DepotInfo = message;
                break;
            case ToolboxToolKind.Gacha:
                GachaInfo = message;
                break;
            case ToolboxToolKind.MiniGame:
                MiniGameTip = message;
                break;
        }
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
        var showRetryableDialog = ShouldShowRetryableToolboxDialog(errorCode, stage);
        if (showRetryableDialog)
        {
            await RecordFailedResultAsync(ScopeOf(tool), normalized, cancellationToken);
        }
        else
        {
            _ = await ApplyResultAsync(normalized, ScopeOf(tool), cancellationToken);
        }

        ResultText = formatted;
        LastErrorMessage = formatted;
        LastExecutionErrorCode = errorCode;
        LastExecutionAt = DateTimeOffset.Now;
        if (!IsToolboxBusy)
        {
            ExecutionState = ToolboxExecutionState.Failed;
        }

        ExecutionHistory.Insert(0, ToolExecutionRecord.Failed(
            ToolNameOf(tool),
            BuildParameterSummary(CurrentToolParameters),
            BuildResultSummary(formatted),
            errorCode));
        TrimExecutionHistory();
        await PersistExecutionHistoryAsync(cancellationToken);
        if (showRetryableDialog)
        {
            await ShowToolboxRetryableErrorDialogAsync(normalized, tool, cancellationToken);
        }
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
        if (!IsToolboxBusy)
        {
            ExecutionState = ToolboxExecutionState.Failed;
        }

        ExecutionHistory.Insert(0, ToolExecutionRecord.Failed(
            ToolNameOf(tool),
            BuildParameterSummary(CurrentToolParameters),
            BuildResultSummary(formatted),
            errorCode));
        TrimExecutionHistory();
        await PersistExecutionHistoryAsync(cancellationToken);
        await ShowToolboxBusyDialogAsync(normalized, cancellationToken);
    }

    private static bool ShouldShowRetryableToolboxDialog(string errorCode, string stage)
    {
        return string.Equals(errorCode, CoreErrorCode.NotInitialized.ToString(), StringComparison.Ordinal)
            && string.Equals(stage, "connect", StringComparison.OrdinalIgnoreCase);
    }

    private async Task ShowToolboxRetryableErrorDialogAsync(
        UiOperationResult errorResult,
        ToolboxToolKind? tool,
        CancellationToken cancellationToken)
    {
        var language = DialogLanguage;
        var chrome = CreateToolboxRetryableErrorDialogChrome(language);
        var chromeSnapshot = chrome.GetSnapshot(language);
        var request = new WarningConfirmDialogRequest(
            Title: chromeSnapshot.Title,
            Message: chromeSnapshot.GetNamedTextOrDefault(
                DialogTextCatalog.ChromeKeys.Prompt,
                T(
                    "Toolbox.RetryableDialog.Message",
                    "你的手速怎么这么快，不过被我料到了。小工具还没准备好，稍后再试一下。")),
            ConfirmText: chromeSnapshot.ConfirmText ?? T("Toolbox.RetryableDialog.RetryButton", "重试"),
            CancelText: chromeSnapshot.CancelText ?? T("Toolbox.RetryableDialog.LaterButton", "稍后再试"),
            Language: language,
            Chrome: chrome);

        var dialogResult = await _dialogService.ShowWarningConfirmAsync(
            request,
            "Toolbox.RetryableError",
            cancellationToken);
        if (dialogResult.Return == DialogReturnSemantic.Details)
        {
            await ShowToolboxErrorDetailsAsync(errorResult, tool, cancellationToken);
            return;
        }

        if (dialogResult.Return == DialogReturnSemantic.Confirm && tool is not null)
        {
            await RetryToolAsync(tool.Value, cancellationToken);
        }
    }

    private async Task RetryToolAsync(ToolboxToolKind tool, CancellationToken cancellationToken)
    {
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
                await StartGachaAsync(string.Equals(NormalizeToken(GachaDrawCountInput), "1", StringComparison.Ordinal), cancellationToken);
                break;
            case ToolboxToolKind.VideoRecognition:
                await TogglePeepAsync(cancellationToken);
                break;
            case ToolboxToolKind.MiniGame:
                await StartMiniGameAsync(cancellationToken);
                break;
        }
    }

    private async Task ShowToolboxBusyDialogAsync(UiOperationResult errorResult, CancellationToken cancellationToken)
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
        if (dialogResult.Return == DialogReturnSemantic.Cancel)
        {
            var owner = Runtime.SessionService.CurrentRunOwner;
            if (!string.IsNullOrWhiteSpace(owner)
                && !Runtime.SessionService.IsRunOwner(ToolboxRunOwner)
                && _stopRunOwnerAsync is not null)
            {
                await _stopRunOwnerAsync(owner, cancellationToken);
                return;
            }

            await StopActiveToolAsync(cancellationToken);
            return;
        }

        if (dialogResult.Return == DialogReturnSemantic.Details)
        {
            await ShowToolboxErrorDetailsAsync(errorResult, activeTool, cancellationToken);
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
                    confirmText: texts.GetOrDefault("Toolbox.BusyDialog.ConfirmButton", "Cancel"),
                    cancelText: texts.GetOrDefault("Toolbox.BusyDialog.StopButton", "Stop current task"),
                    namedTexts: DialogTextCatalog.CreateNamedTexts(
                        (DialogTextCatalog.ChromeKeys.SectionTitle, title),
                        (DialogTextCatalog.ChromeKeys.DetailsButton, texts.GetOrDefault(
                            "Toolbox.BusyDialog.DetailsButton",
                            DialogTextCatalog.WarningDialogDetailsButton(nextLanguage))),
                        (DialogTextCatalog.ChromeKeys.Prompt, BuildToolboxBusyDialogMessage(nextLanguage, activeTool))));
            });
    }

    private static DialogChromeCatalog CreateToolboxRetryableErrorDialogChrome(string language)
    {
        return DialogTextCatalog.CreateCatalog(
            language,
            nextLanguage =>
            {
                var texts = CreateTexts(nextLanguage);
                var title = texts.GetOrDefault("Toolbox.RetryableDialog.Title", "Toolbox is not ready yet");
                return new DialogChromeSnapshot(
                    title: title,
                    confirmText: texts.GetOrDefault("Toolbox.RetryableDialog.RetryButton", "Retry"),
                    cancelText: texts.GetOrDefault("Toolbox.RetryableDialog.LaterButton", "Try later"),
                    namedTexts: DialogTextCatalog.CreateNamedTexts(
                        (DialogTextCatalog.ChromeKeys.SectionTitle, title),
                        (DialogTextCatalog.ChromeKeys.DetailsButton, texts.GetOrDefault(
                            "Toolbox.RetryableDialog.DetailsButton",
                            DialogTextCatalog.WarningDialogDetailsButton(nextLanguage))),
                        (DialogTextCatalog.ChromeKeys.Prompt, texts.GetOrDefault(
                            "Toolbox.RetryableDialog.Message",
                            "The toolbox is still getting ready. Try again in a moment."))));
            });
    }

    private async Task ShowToolboxErrorDetailsAsync(
        UiOperationResult errorResult,
        ToolboxToolKind? activeTool,
        CancellationToken cancellationToken)
    {
        var language = DialogLanguage;
        var localizedResult = DialogTextCatalog.LocalizeErrorResult(language, errorResult);
        var chrome = CreateErrorDetailsDialogChrome(language);
        var chromeSnapshot = chrome.GetSnapshot(language);
        var request = new ErrorDialogRequest(
            Title: chromeSnapshot.Title,
            Context: ScopeOf(activeTool),
            Result: localizedResult,
            Suggestion: DialogTextCatalog.BuildErrorSuggestion(language, errorResult),
            ConfirmText: chromeSnapshot.ConfirmText ?? DialogTextCatalog.ErrorDialogCloseButton(language),
            CancelText: chromeSnapshot.CancelText ?? DialogTextCatalog.ErrorDialogIgnoreButton(language),
            Language: language,
            Chrome: chrome);
        await _dialogService.ShowErrorAsync(request, "Toolbox.Busy.ErrorDetails", cancellationToken: cancellationToken);
    }

    private static DialogChromeCatalog CreateErrorDetailsDialogChrome(string language)
    {
        return DialogTextCatalog.CreateCatalog(
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
            DisposePeepBitmapCache();
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
            if (await TryRefreshPeepRawFrameAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }

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

    private async Task<bool> TryRefreshPeepRawFrameAsync(CancellationToken cancellationToken)
    {
        var screenshotStopwatch = Stopwatch.StartNew();
        var imageResult = await Runtime.CoreBridge.GetImageBgrAsync(forceScreencap: true, cancellationToken).ConfigureAwait(false);
        screenshotStopwatch.Stop();
        if (!imageResult.Success || imageResult.Value is null || imageResult.Value.Length == 0)
        {
            _ = Runtime.DiagnosticsService.RecordScreenshotTestAsync(
                "Toolbox.Peep.RefreshRaw",
                success: false,
                elapsedMs: screenshotStopwatch.Elapsed.TotalMilliseconds,
                provider: "CoreBridge.GetImageBgrAsync",
                details: imageResult.Error?.Message,
                minInterval: TimeSpan.FromSeconds(10));
            return imageResult.Error?.Code != CoreErrorCode.NotSupported;
        }

        _ = Runtime.DiagnosticsService.RecordScreenshotTestAsync(
            "Toolbox.Peep.RefreshRaw",
            success: true,
            elapsedMs: screenshotStopwatch.Elapsed.TotalMilliseconds,
            provider: "CoreBridge.GetImageBgrAsync",
            width: PeepPreviewPixelWidth,
            height: PeepPreviewPixelHeight,
            minInterval: TimeSpan.FromSeconds(30));

        try
        {
            await Dispatcher.UIThread.InvokeAsync(
                () => ApplyPeepRawFrame(imageResult.Value),
                DispatcherPriority.Render,
                cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _ = Runtime.DiagnosticsService.RecordScreenshotTestAsync(
                "Toolbox.Peep.RefreshRaw",
                success: false,
                elapsedMs: screenshotStopwatch.Elapsed.TotalMilliseconds,
                provider: "CoreBridge.GetImageBgrAsync",
                details: ex.Message,
                minInterval: TimeSpan.FromSeconds(10));
            return false;
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
        AdvancePeepFpsWindow();
    }

    private void ApplyPeepRawFrame(byte[] bgrData)
    {
        if (!Peeping)
        {
            return;
        }

        var bytesPerFrame = PeepPreviewPixelWidth * PeepPreviewPixelHeight * PeepPreviewPixelChannels;
        if (bgrData.Length < bytesPerFrame)
        {
            return;
        }

        var sequence = _peepBitmapSequence++;
        var bitmap = GetOrCreatePeepBitmap(sequence % _peepBitmapCache.Length);
        WriteBgrFrame(bitmap, bgrData);
        PeepImage = bitmap;
        AdvancePeepFpsWindow();
    }

    private void AdvancePeepFpsWindow()
    {
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

    private WriteableBitmap GetOrCreatePeepBitmap(int index)
    {
        return _peepBitmapCache[index] ??= new WriteableBitmap(
            new PixelSize(PeepPreviewPixelWidth, PeepPreviewPixelHeight),
            new Vector(96, 96),
            PixelFormats.Bgr24,
            AlphaFormat.Opaque);
    }

    private static void WriteBgrFrame(WriteableBitmap bitmap, byte[] bgrData)
    {
        var frameStride = PeepPreviewPixelWidth * PeepPreviewPixelChannels;
        var frameBytes = PeepPreviewPixelHeight * frameStride;
        using var framebuffer = bitmap.Lock();
        if (framebuffer.RowBytes == frameStride)
        {
            Marshal.Copy(bgrData, 0, framebuffer.Address, frameBytes);
            return;
        }

        for (var row = 0; row < PeepPreviewPixelHeight; row++)
        {
            var sourceOffset = row * frameStride;
            var destination = IntPtr.Add(framebuffer.Address, row * framebuffer.RowBytes);
            Marshal.Copy(bgrData, sourceOffset, destination, frameStride);
        }
    }

    private bool IsCachedPeepBitmap(Bitmap bitmap)
    {
        foreach (var cached in _peepBitmapCache)
        {
            if (ReferenceEquals(cached, bitmap))
            {
                return true;
            }
        }

        return false;
    }

    private void DisposePeepBitmapCache()
    {
        for (var i = 0; i < _peepBitmapCache.Length; i++)
        {
            _peepBitmapCache[i]?.Dispose();
            _peepBitmapCache[i] = null;
        }

        _peepBitmapSequence = 0;
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
        OperBoxHaveGroups.Clear();
        OperBoxNotHaveGroups.Clear();
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
        ReplaceCollectionItems(DepotResult, Array.Empty<DepotItemViewModel>());
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
            UiErrorCode.ConnectFailed => T("Toolbox.Error.ConnectionFailed", "Connection failed."),
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

    private void RebuildDepotGroups()
    {
        var groups = DepotResult
            .OrderBy(item => ResolveDepotCategorySort(item.ClassifyType))
            .ThenByDescending(item => item.SortId)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .GroupBy(item => ResolveDepotGroupKey(item), StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First();
                var items = group.ToArray();
                return new DepotItemGroupViewModel(
                    ResolveDepotGroupTitle(first),
                    ResolveDepotCategoryTitle(first.ClassifyType),
                    ResolveDepotGroupPanelWidth(items.Length),
                    items);
            })
            .ToArray();

        ReplaceCollectionItems(DepotGroups, groups);
    }

    private IReadOnlyList<RecruitOperatorCardViewModel> SortRecruitOperatorCards(IEnumerable<RecruitOperatorCardViewModel> items)
    {
        return items
            .OrderByDescending(item => item.Rarity)
            .ThenBy(item => ResolveProfessionSort(item.ProfessionText))
            .ThenBy(item => item.Name, StringComparer.CurrentCulture)
            .ToArray();
    }

    private static int ResolveRecruitTagSortRank(int tagLevel)
    {
        return tagLevel switch
        {
            >= 6 => 0,
            5 => 1,
            4 => 2,
            1 => 3,
            3 => 4,
            _ => 9,
        };
    }

    private IReadOnlyList<OperBoxOperatorGroupViewModel> GroupOperBoxOperators(IEnumerable<OperBoxOperatorItemViewModel> items)
    {
        return items
            .GroupBy(item => item.Rarity)
            .OrderByDescending(group => group.Key)
            .Select(group => new OperBoxOperatorGroupViewModel(
                $"{group.Key}★",
                ResolveRarityAccentBrush(group.Key),
                group
                    .OrderBy(item => ResolveProfessionSort(item.ProfessionText))
                    .ThenBy(item => item.Name, StringComparer.CurrentCulture)
                    .ToArray()))
            .ToArray();
    }

    private string ResolveProfessionText(string? profession)
    {
        var normalized = (profession ?? string.Empty).Trim().ToUpperInvariant();
        var useChinese = _currentLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        return normalized switch
        {
            "PIONEER" => useChinese ? "先锋" : "Vanguard",
            "WARRIOR" => useChinese ? "近卫" : "Guard",
            "TANK" => useChinese ? "重装" : "Defender",
            "SNIPER" => useChinese ? "狙击" : "Sniper",
            "CASTER" => useChinese ? "术师" : "Caster",
            "MEDIC" => useChinese ? "医疗" : "Medic",
            "SUPPORT" => useChinese ? "辅助" : "Supporter",
            "SPECIAL" => useChinese ? "特种" : "Specialist",
            _ => useChinese ? "干员" : "Operator",
        };
    }

    private static int ResolveProfessionSort(string professionText)
    {
        return professionText switch
        {
            "先锋" or "Vanguard" => 0,
            "近卫" or "Guard" => 1,
            "重装" or "Defender" => 2,
            "狙击" or "Sniper" => 3,
            "术师" or "Caster" => 4,
            "医疗" or "Medic" => 5,
            "辅助" or "Supporter" => 6,
            "特种" or "Specialist" => 7,
            _ => 99,
        };
    }

    private string ResolveDepotCategoryTitle(string? classifyType)
    {
        var normalized = (classifyType ?? string.Empty).Trim().ToUpperInvariant();
        var useChinese = _currentLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        return normalized switch
        {
            "MATERIAL" => useChinese ? "材料" : "Materials",
            "CONSUME" => useChinese ? "消耗品" : "Consumables",
            "NORMAL" => useChinese ? "常规物品" : "Regular Items",
            "NONE" => useChinese ? "其他" : "Other",
            _ => useChinese ? "其他" : "Other",
        };
    }

    private static int ResolveDepotCategorySort(string? classifyType)
    {
        return (classifyType ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "MATERIAL" => 0,
            "CONSUME" => 1,
            "NORMAL" => 2,
            _ => 9,
        };
    }

    private static string ResolveDepotGroupKey(DepotItemViewModel item)
    {
        var id = item.Id;
        var normalizedId = NormalizeDepotItemId(id);

        if (item.Id is "2001" or "2002" or "2003" or "2004")
        {
            return "battle-record";
        }

        if (item.Id.StartsWith("32", StringComparison.Ordinal) && item.Id.Length >= 3)
        {
            return $"chip-{item.Id[..3]}";
        }

        if (TryResolveDepotMaterialFamilyKey(item, out var materialFamilyKey))
        {
            return materialFamilyKey;
        }

        if (IsDepotBaseMaterial(item))
        {
            return "base-materials";
        }

        if (IsDepotHeadhuntingPermit(item))
        {
            return "headhunting-permits";
        }

        if (IsDepotRecruitmentPermit(id))
        {
            return "recruitment-permits";
        }

        if (IsDepotMaterialVoucher(normalizedId))
        {
            return "material-vouchers";
        }

        if (IsDepotMaterialSupply(normalizedId))
        {
            return "material-supplies";
        }

        if (IsDepotSanitySupply(normalizedId))
        {
            return "sanity-supplies";
        }

        if (normalizedId.StartsWith("ITEMPACK_MOD_", StringComparison.Ordinal))
        {
            return int.TryParse(id["itempack_mod_".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var modPackIndex)
                && modPackIndex is >= 7 and <= 14
                    ? "chip-pack-instruments"
                    : "module-item-packs";
        }

        if (normalizedId.StartsWith("ITEMPACK_STICKERS_", StringComparison.Ordinal))
        {
            return "sticker-packs";
        }

        if (normalizedId.StartsWith("ITEMPACK_", StringComparison.Ordinal))
        {
            return "item-packs";
        }

        if (normalizedId.StartsWith("LMTGS_", StringComparison.Ordinal))
        {
            return "limited-gacha-contracts";
        }

        if (normalizedId.StartsWith("GIFTPACKAGETICKET_", StringComparison.Ordinal))
        {
            return "closure-tickets";
        }

        if (IsDepotCoreCurrency(item))
        {
            return "core-currency";
        }

        if (IsDepotEventShopCurrency(normalizedId))
        {
            return "event-shop-currency";
        }

        if (normalizedId.StartsWith("MAIN", StringComparison.Ordinal) && normalizedId.Contains("_SPITEM_", StringComparison.Ordinal))
        {
            return "plot-items";
        }

        if (normalizedId.StartsWith("EMOTICON_", StringComparison.Ordinal))
        {
            return "emoticon-sets";
        }

        if (normalizedId.StartsWith("ROGUE_", StringComparison.Ordinal))
        {
            return "roguelike-items";
        }

        if (normalizedId.StartsWith("SANDBOX_", StringComparison.Ordinal))
        {
            return "reclamation-items";
        }

        if (normalizedId.StartsWith("RETURN_CREDIT_", StringComparison.Ordinal))
        {
            return "return-credit-items";
        }

        if (IsDepotActivityItem(normalizedId))
        {
            return "activity-items";
        }

        if (IsDepotSystemResource(normalizedId))
        {
            return "system-resources";
        }

        if (item.SortId > 0)
        {
            return $"{item.ClassifyType}:{item.SortId / 10}";
        }

        return $"{item.ClassifyType}:{item.Id}";
    }

    private string ResolveDepotGroupTitle(DepotItemViewModel first)
    {
        if (first.Id is "2001" or "2002" or "2003" or "2004")
        {
            return _currentLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                ? "作战记录"
                : "Battle Records";
        }

        var useChinese = _currentLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        var normalizedId = NormalizeDepotItemId(first.Id);

        var sortBand = first.SortId / 1000;
        if (sortBand == 80)
        {
            return useChinese ? "技巧概要" : "Skill Summaries";
        }

        if (sortBand == 90)
        {
            return useChinese ? "模组材料" : "Module Materials";
        }

        if (first.Id.StartsWith("32", StringComparison.Ordinal))
        {
            return ResolveDepotChipGroupTitle(first.Name, useChinese);
        }

        if (TryResolveDepotMaterialFamilyKey(first, out _))
        {
            return IsDepotTierFiveMaterial(first.Id)
                ? useChinese ? "高级材料" : "Advanced Materials"
                : ResolveDepotMaterialFamilyTitle(first, useChinese);
        }

        if (IsDepotBaseMaterial(first))
        {
            return useChinese ? "基建材料" : "Base Materials";
        }

        if (IsDepotHeadhuntingPermit(first))
        {
            return useChinese ? "寻访凭证" : "Headhunting Permits";
        }

        if (IsDepotRecruitmentPermit(first.Id))
        {
            return useChinese ? "公开招募许可" : "Recruitment Permits";
        }

        if (IsDepotMaterialVoucher(normalizedId))
        {
            return useChinese ? "材料提货券" : "Material Vouchers";
        }

        if (IsDepotMaterialSupply(normalizedId))
        {
            return useChinese ? "物资补给" : "Material Supplies";
        }

        if (IsDepotSanitySupply(normalizedId))
        {
            return useChinese ? "理智补给" : "Sanity Supplies";
        }

        if (normalizedId.StartsWith("ITEMPACK_MOD_", StringComparison.Ordinal))
        {
            return int.TryParse(first.Id["itempack_mod_".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var modPackIndex)
                && modPackIndex is >= 7 and <= 14
                    ? useChinese ? "芯片组印刻仪" : "Chip Pack Instruments"
                    : useChinese ? "模组数据整合" : "Module Data Packs";
        }

        if (normalizedId.StartsWith("ITEMPACK_STICKERS_", StringComparison.Ordinal))
        {
            return useChinese ? "贴纸包" : "Sticker Packs";
        }

        if (normalizedId.StartsWith("ITEMPACK_", StringComparison.Ordinal))
        {
            return useChinese ? "补给包" : "Item Packs";
        }

        if (normalizedId.StartsWith("LMTGS_", StringComparison.Ordinal))
        {
            return useChinese ? "寻访数据契约" : "Headhunting Data Contracts";
        }

        if (normalizedId.StartsWith("GIFTPACKAGETICKET_", StringComparison.Ordinal))
        {
            return useChinese ? "可露希尔券" : "Closure Tickets";
        }

        if (IsDepotCoreCurrency(first))
        {
            return useChinese ? "货币与凭证" : "Currencies and Certificates";
        }

        if (IsDepotEventShopCurrency(normalizedId))
        {
            return useChinese ? "活动商店货币" : "Event Shop Currencies";
        }

        if (normalizedId.StartsWith("MAIN", StringComparison.Ordinal) && normalizedId.Contains("_SPITEM_", StringComparison.Ordinal))
        {
            return useChinese ? "剧情道具" : "Story Items";
        }

        if (normalizedId.StartsWith("EMOTICON_", StringComparison.Ordinal))
        {
            return useChinese ? "表情套组" : "Emoticon Sets";
        }

        if (normalizedId.StartsWith("ROGUE_", StringComparison.Ordinal))
        {
            return useChinese ? "集成战略" : "Integrated Strategies";
        }

        if (normalizedId.StartsWith("SANDBOX_", StringComparison.Ordinal))
        {
            return useChinese ? "生息演算" : "Reclamation Algorithm";
        }

        if (normalizedId.StartsWith("RETURN_CREDIT_", StringComparison.Ordinal))
        {
            return useChinese ? "回归奖励" : "Returnee Rewards";
        }

        if (IsDepotActivityItem(normalizedId))
        {
            return useChinese ? "活动道具" : "Event Items";
        }

        if (IsDepotSystemResource(normalizedId))
        {
            return useChinese ? "系统资源" : "System Resources";
        }

        if (first.ClassifyType.Equals("MATERIAL", StringComparison.OrdinalIgnoreCase))
        {
            return first.SortId / 10 == 10000
                ? useChinese ? "高级材料" : "Advanced Materials"
                : first.Name;
        }

        return ResolveDepotCategoryTitle(first.ClassifyType);
    }

    private static string ResolveDepotChipGroupTitle(string name, bool useChinese)
    {
        if (!useChinese)
        {
            return name
                .Replace(" Dualchip", " Chips", StringComparison.OrdinalIgnoreCase)
                .Replace(" Chip Pack", " Chips", StringComparison.OrdinalIgnoreCase);
        }

        foreach (var suffix in new[] { "双芯片", "芯片组", "芯片" })
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal))
            {
                return $"{name[..^suffix.Length]}芯片";
            }
        }

        return name;
    }

    private static bool TryResolveDepotMaterialFamilyKey(DepotItemViewModel item, out string key)
    {
        key = string.Empty;
        if (!item.ClassifyType.Equals("MATERIAL", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (IsDepotTierFiveMaterial(item.Id))
        {
            key = "tier-five-materials";
            return true;
        }

        if (item.Id.Length == 5
            && (item.Id.StartsWith("30", StringComparison.Ordinal) || item.Id.StartsWith("31", StringComparison.Ordinal))
            && item.Id.All(char.IsDigit))
        {
            key = $"material-family-{item.Id[..4]}";
            return true;
        }

        return false;
    }

    private static bool IsDepotTierFiveMaterial(string id)
    {
        return id is "30115" or "30125" or "30135" or "30145" or "30155" or "30165";
    }

    private static string ResolveDepotMaterialFamilyTitle(DepotItemViewModel item, bool useChinese)
    {
        if (item.Id.Length < 4)
        {
            return item.Name;
        }

        return item.Id[..4] switch
        {
            "3001" => useChinese ? "源岩" : "Orirock",
            "3002" => useChinese ? "糖" : "Sugar",
            "3003" => useChinese ? "聚酸酯" : "Polyester",
            "3004" => useChinese ? "异铁" : "Oriron",
            "3005" => useChinese ? "酮凝集" : "Ketone",
            "3006" => useChinese ? "装置" : "Device",
            "3007" => useChinese ? "扭转醇" : "Loxic Kohl",
            "3008" => useChinese ? "轻锰矿" : "Manganese Ore",
            "3009" => useChinese ? "研磨石" : "Grindstone",
            "3010" => "RMA70",
            "3101" => useChinese ? "凝胶" : "Gel",
            "3102" => useChinese ? "炽合金" : "Incandescent Alloy",
            "3103" => useChinese ? "晶体元件" : "Crystalline Component",
            "3104" => useChinese ? "半自然溶剂" : "Semi-Synthetic Solvent",
            "3105" => useChinese ? "化合切削液" : "Cutting Fluid",
            "3106" => useChinese ? "转质盐" : "Transmuted Salt",
            "3107" => useChinese ? "褐素纤维" : "Fuscous Fiber",
            "3108" => useChinese ? "环烃聚质" : "Cyclicene",
            "3109" => useChinese ? "类凝结核" : "Coagulative Nodule",
            "3110" => useChinese ? "液化高能气体" : "Liquefied Energy Gas",
            "3111" => useChinese ? "电极单元" : "Electrode Unit",
            _ => item.Name,
        };
    }

    private static bool IsDepotBaseMaterial(DepotItemViewModel item)
    {
        return item.ClassifyType.Equals("NORMAL", StringComparison.OrdinalIgnoreCase)
            && item.Id is "3105" or "3112" or "3113" or "3114" or "3131" or "3132" or "3133" or "3401";
    }

    private static bool IsDepotHeadhuntingPermit(DepotItemViewModel item)
    {
        var normalizedId = NormalizeDepotItemId(item.Id);
        return item.Id is "7003" or "7004" or "classic_gacha" or "classic_gacha_10"
            || normalizedId.StartsWith("LINKAGE_TKT_GACHA_", StringComparison.Ordinal)
            || normalizedId.StartsWith("SINGLE_", StringComparison.Ordinal) && normalizedId.Contains("_GACHA", StringComparison.Ordinal)
            || normalizedId.StartsWith("2026RECRUITMENT10_", StringComparison.Ordinal)
            || item.Name.Contains("寻访凭证", StringComparison.Ordinal)
            || item.Name.Contains("Headhunting Permit", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDepotRecruitmentPermit(string id)
    {
        return id is "7001" or "7002";
    }

    private static bool IsDepotMaterialVoucher(string normalizedId)
    {
        return normalizedId.Contains("MATERIAL_ISSUE_VOUCHER", StringComparison.Ordinal)
            || normalizedId.EndsWith("_MATERIAL_VOUCHER_PERM", StringComparison.Ordinal);
    }

    private static bool IsDepotMaterialSupply(string normalizedId)
    {
        return normalizedId.StartsWith("RANDOMMATERIAL", StringComparison.Ordinal)
            || normalizedId.StartsWith("RANDOMDIAMONDSHD_", StringComparison.Ordinal);
    }

    private static bool IsDepotSanitySupply(string normalizedId)
    {
        return normalizedId.StartsWith("AP_SUPPLY_", StringComparison.Ordinal);
    }

    private static bool IsDepotCoreCurrency(DepotItemViewModel item)
    {
        return item.Id is "4001" or "4002" or "4003" or "4004" or "4005" or "4006" or "3003" or "3141" or "classic_normal_ticket";
    }

    private static bool IsDepotEventShopCurrency(string normalizedId)
    {
        return normalizedId is "STORY_REVIEW_COIN"
            or "CRISIS_SHOP_COIN"
            or "CRISIS_SHOP_COIN_V2"
            or "REP_COIN"
            or "EPGS_COIN"
            or "RETRO_COIN";
    }

    private static bool IsDepotActivityItem(string normalizedId)
    {
        return normalizedId.StartsWith("ACT", StringComparison.Ordinal)
            || normalizedId.StartsWith("TOKEN_", StringComparison.Ordinal)
            || normalizedId.StartsWith("ET_", StringComparison.Ordinal)
            || normalizedId is "1STACT" or "CRISIS_RUNE_COIN" or "FAVOR_ADD_ULIKA";
    }

    private static bool IsDepotSystemResource(string normalizedId)
    {
        return normalizedId is "AP_GAMEPLAY"
            or "BASE_AP"
            or "SOCIAL_PT"
            or "5001"
            or "6001"
            or "MCARDVOUCHER"
            or "BILIBILI001"
            or "LOGISTICS_SPECIAL_PERMIT"
            or "SO_CHAR_EXP_1";
    }

    private static string NormalizeDepotItemId(string id)
    {
        return id.Trim().ToUpperInvariant();
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

    public static IBrush ResolveRarityAccentBrushForBinding(int star)
    {
        return ResolveRarityAccentBrush(star);
    }

    private static IBrush ResolveRarityAccentBrush(int star)
    {
        if (star >= 6)
        {
            return new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.Parse("#FF65D9FF"), 0),
                    new GradientStop(Color.Parse("#FF9B6CFF"), 0.35),
                    new GradientStop(Color.Parse("#FFFFC857"), 0.68),
                    new GradientStop(Color.Parse("#FFFF6B8A"), 1),
                },
            };
        }

        return star switch
        {
            5 => new SolidColorBrush(Color.Parse("#FFD89A29")),
            4 => new SolidColorBrush(Color.Parse("#FF8D68D8")),
            3 => new SolidColorBrush(Color.Parse("#FFE9EDF4")),
            _ => new SolidColorBrush(Color.Parse("#FFB8C0CC")),
        };
    }

    private static double ResolvePanelWidth(int count, double itemWidth)
    {
        var columns = Math.Clamp(count, 1, 5);
        return columns * itemWidth;
    }

    private static double ResolveDepotGroupPanelWidth(int count)
    {
        var columns = Math.Clamp(count, 1, 5);
        return DepotGroupPanelHorizontalInset
            + (columns * DepotGroupPanelItemWidth)
            + (Math.Max(0, columns - 1) * DepotGroupPanelColumnGap);
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

public sealed record RecruitResultGroupViewModel(
    int TagLevel,
    string LevelText,
    IReadOnlyList<string> Tags,
    IBrush AccentBrush,
    IReadOnlyList<RecruitOperatorCardViewModel> Operators)
{
    public bool HasTags => Tags.Count > 0;
}

public sealed record RecruitOperatorCardViewModel(
    string Name,
    int Rarity,
    string ProfessionText,
    string Profession,
    string PotentialText,
    IBrush AccentBrush)
{
    public string RarityStars => Rarity <= 0 ? string.Empty : new string('★', Rarity);

    public Bitmap? ProfessionIconImage => ToolboxAssetCatalog.ResolveOperatorProfessionBitmap(Profession);

    public bool HasPotentialText => !string.IsNullOrWhiteSpace(PotentialText);
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
        string professionText,
        string profession,
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
        ProfessionText = professionText;
        Profession = profession;
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

    public string ProfessionText { get; }

    public string Profession { get; }

    public Bitmap? ProfessionIconImage => ToolboxAssetCatalog.ResolveOperatorProfessionBitmap(Profession);

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
        return ToolboxPageViewModel.ResolveRarityAccentBrushForBinding(rarity);
    }
}

public sealed record OperBoxOperatorGroupViewModel(
    string Title,
    IBrush AccentBrush,
    IReadOnlyList<OperBoxOperatorItemViewModel> Operators);

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

    public DepotItemViewModel(string id, string name, int count, string? imagePath, string? classifyType = null, int sortId = 0)
    {
        Id = id;
        Name = name;
        _count = count;
        ImagePath = imagePath;
        ClassifyType = string.IsNullOrWhiteSpace(classifyType) ? "NONE" : classifyType;
        SortId = sortId;
        AccentBrush = ResolveDepotAccentBrush(ClassifyType);
        _itemImage = ToolboxAssetCatalog.ResolveItemBitmap(id);
    }

    public string Id { get; }

    public string Name { get; }

    public string? ImagePath { get; }

    public string ClassifyType { get; }

    public int SortId { get; }

    public IBrush AccentBrush { get; }

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

    private static IBrush ResolveDepotAccentBrush(string classifyType)
    {
        return classifyType.Trim().ToUpperInvariant() switch
        {
            "MATERIAL" => new SolidColorBrush(Color.Parse("#FF2F6FB2")),
            "CONSUME" => new SolidColorBrush(Color.Parse("#FF64748B")),
            "NORMAL" => new SolidColorBrush(Color.Parse("#FF7C8BA3")),
            _ => new SolidColorBrush(Color.Parse("#FFB8C0CC")),
        };
    }
}

public sealed record DepotItemGroupViewModel(
    string Title,
    string Category,
    double PanelWidth,
    IReadOnlyList<DepotItemViewModel> Items);

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
