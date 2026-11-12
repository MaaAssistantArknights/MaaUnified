using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MAAUnified.App.Services;
using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.Application.Models;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.TaskParams;
using MAAUnified.Compat.Constants;

namespace MAAUnified.App.ViewModels.TaskQueue;

public sealed class InfrastModuleViewModel : TaskModuleSettingsViewModelBase
{
    public const int InfrastModeNormal = 0;
    public const int InfrastModeCustom = 10000;
    public const int InfrastModeRotation = 20000;
    public const string UserDefinedInfrast = "user_defined";

    private static readonly string[] DefaultFacilityOrder =
    [
        "Mfg",
        "Trade",
        "Control",
        "Power",
        "Reception",
        "Office",
        "Dorm",
        "Processing",
        "Training",
    ];

    private int _mode;
    private string _drones = "Money";
    private int _dormThresholdPercent = 30;
    private string _customFilePath = string.Empty;
    private int _selectedPlanIndex = -1;
    private PlanOption? _selectedPlan;
    private bool _continueTraining;
    private bool _dormTrustEnabled = true;
    private bool _dormNotStationedEnabled = true;
    private bool _replenish = true;
    private bool _receptionMessageBoard = true;
    private bool _receptionClueExchange = true;
    private bool _receptionSendClue = true;
    private bool _fiammettaRecoveryEnabled;
    private string _fiammettaTarget1 = InfrastParams.DefaultFiammettaTargets[0];
    private string _fiammettaTarget2 = InfrastParams.DefaultFiammettaTargets[1];
    private string _fiammettaTarget3 = InfrastParams.DefaultFiammettaTargets[2];
    private bool _usePinusSylvestris;
    private bool _usePerceptionInformation;
    private bool _useWorldlyPlight;
    private bool _useAbyssalHunter;
    private bool _useWeeklySchedule;
    private bool _weeklyScheduleSunday = true;
    private bool _weeklyScheduleMonday = true;
    private bool _weeklyScheduleTuesday = true;
    private bool _weeklyScheduleWednesday = true;
    private bool _weeklyScheduleThursday = true;
    private bool _weeklyScheduleFriday = true;
    private bool _weeklyScheduleSaturday = true;
    private IReadOnlyList<StringOption>? _fiammettaTargetOptions;
    private IReadOnlyList<StringOption>? _optionalFiammettaTargetOptions;
    private IReadOnlyList<IntOption> _modeOptions = [];
    private IReadOnlyList<StringOption> _droneOptions = [];
    private IReadOnlyList<StringOption> _defaultInfrastOptions = [];
    private string _defaultInfrast = UserDefinedInfrast;
    private bool _isCustomFileReadOnly;
    private bool _suppressDefaultInfrastPersist;
    private bool _suppressFacilitySelectionChanged;
    private IReadOnlyList<InfrastPlanDefinition> _parsedPlans = [];
    private InfrastPlanCacheEntry? _planCache;
    private int _planReloadVersion;
    private bool _loadingParameters;
    private IReadOnlyList<PlanOption> _planOptions = [];
    private Task _pendingPlanReload = Task.CompletedTask;

    public InfrastModuleViewModel(MAAUnifiedRuntime runtime, LocalizedTextMap texts)
        : base(runtime, texts, TaskModuleTypes.Infrast)
    {
        Texts.PropertyChanged += OnTextsChanged;
        ApplyPersistentDefaultInfrast(ResolveDefaultInfrast());
        RebuildLocalizedOptions();
        ConfigureFacilityOptions(DefaultFacilityOrder);
    }

    public IReadOnlyList<PlanOption> PlanOptions => Volatile.Read(ref _planOptions);

    public ObservableCollection<FacilityOption> FacilityOptions { get; } = [];

    internal int PlanFileReadCount { get; private set; }

    public IReadOnlyList<IntOption> ModeOptions => _modeOptions;

    public IReadOnlyList<StringOption> DroneOptions => _droneOptions;

    public IReadOnlyList<StringOption> DefaultInfrastOptions => _defaultInfrastOptions;

    public IntOption? SelectedModeOption
    {
        get => ResolveSelectedOption(ModeOptions, Mode);
        set => Mode = value?.Value ?? InfrastModeNormal;
    }

    public StringOption? SelectedDroneOption
    {
        get => ResolveSelectedOption(DroneOptions, Drones);
        set => Drones = value?.Value ?? "Money";
    }

    public StringOption? SelectedDefaultInfrastOption
    {
        get => ResolveSelectedOption(DefaultInfrastOptions, DefaultInfrast);
        set => DefaultInfrast = value?.Value ?? UserDefinedInfrast;
    }

    public int Mode
    {
        get => _mode;
        set
        {
            var normalized = value switch
            {
                InfrastModeCustom => InfrastModeCustom,
                InfrastModeRotation => InfrastModeRotation,
                _ => InfrastModeNormal,
            };
            if (!SetProperty(ref _mode, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedModeOption));
            OnPropertyChanged(nameof(IsCustomMode));
            OnPropertyChanged(nameof(IsRotationMode));
            OnPropertyChanged(nameof(ShowCustomModeSettings));
            OnPropertyChanged(nameof(ShowRotationTip));
            OnPropertyChanged(nameof(ShowThresholdSettings));
            OnPropertyChanged(nameof(ShowDormFilterSettings));
            OnPropertyChanged(nameof(CanEditDrones));
            OnPropertyChanged(nameof(ShowCrossFacilitySettings));

            QueuePersist();
            if (IsCustomMode)
            {
                StartPlanReload(resetPlanSelection: false);
            }
            else
            {
                ReplacePlanOptions([]);
                Volatile.Write(ref _parsedPlans, []);
                SelectedPlan = null;
                StatusMessage = string.Empty;
                LastErrorMessage = string.Empty;
            }
        }
    }

    public string Drones
    {
        get => _drones;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "Money" : value.Trim();
            if (!SetProperty(ref _drones, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedDroneOption));
            QueuePersist();
        }
    }

    public int DormThresholdPercent
    {
        get => _dormThresholdPercent;
        set
        {
            var normalized = Math.Clamp(value, 0, 100);
            if (!SetProperty(ref _dormThresholdPercent, normalized))
            {
                return;
            }

            QueuePersist();
        }
    }

    public string CustomFilePath
    {
        get => _customFilePath;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (!SetProperty(ref _customFilePath, normalized))
            {
                return;
            }

            QueuePersist();
            if (IsCustomMode)
            {
                StartPlanReload(resetPlanSelection: !_loadingParameters);
            }
        }
    }

    public int SelectedPlanIndex
    {
        get => _selectedPlanIndex;
        set
        {
            if (!SetProperty(ref _selectedPlanIndex, value))
            {
                return;
            }

            if (SelectedPlan?.Index != value)
            {
                var planOptions = PlanOptions;
                _selectedPlan = planOptions.FirstOrDefault(option => option.Index == value);
                OnPropertyChanged(nameof(SelectedPlan));
            }

            QueuePersist();
        }
    }

    public PlanOption? SelectedPlan
    {
        get => _selectedPlan;
        set
        {
            if (!SetProperty(ref _selectedPlan, value))
            {
                return;
            }

            var index = value?.Index ?? -1;
            if (_selectedPlanIndex != index)
            {
                _selectedPlanIndex = index;
                OnPropertyChanged(nameof(SelectedPlanIndex));
            }

            QueuePersist();
        }
    }

    public string DefaultInfrast
    {
        get => _defaultInfrast;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? UserDefinedInfrast : value.Trim();
            if (!SetProperty(ref _defaultInfrast, normalized))
            {
                return;
            }

            var readOnly = !IsUserDefinedDefault(normalized);
            if (SetProperty(ref _isCustomFileReadOnly, readOnly, nameof(IsCustomFileReadOnly)))
            {
                // no-op
            }

            OnPropertyChanged(nameof(SelectedDefaultInfrastOption));

            if (readOnly)
            {
                var defaultPath = Path.Combine(global::MAAUnified.Compat.Runtime.RuntimeLayout.ResolveRuntimeBaseDirectory(), "resource", "custom_infrast", normalized);
                CustomFilePath = defaultPath;
            }
            else if (IsCustomMode)
            {
                _ = ReloadPlansAsync();
            }

            if (!_suppressDefaultInfrastPersist)
            {
                _ = PersistDefaultInfrastAsync(normalized);
            }
        }
    }

    public bool IsCustomFileReadOnly => _isCustomFileReadOnly;

    public bool IsCustomMode => Mode == InfrastModeCustom;

    public bool IsRotationMode => Mode == InfrastModeRotation;

    public bool ShowCustomModeSettings => IsCustomMode;

    public bool ShowRotationTip => IsRotationMode;

    public bool ShowThresholdSettings => !IsRotationMode;

    public bool ShowDormFilterSettings => !IsRotationMode;

    public bool CanEditDrones => !IsCustomMode;

    // WPF dev-v2 (c281b7aaca / 59963a90cb / 97d5e8ac38): the Fiammetta recovery and
    // the four cross-facility teams only apply to the default (Normal) mode.
    public bool ShowCrossFacilitySettings => Mode == InfrastModeNormal;

    // Deferred: building these options touches resource/battle_data.json for the
    // localized operator names, so only pay that cost once the panel is shown.
    public IReadOnlyList<StringOption> FiammettaTargetOptions
        => _fiammettaTargetOptions ??= BuildFiammettaTargetOptions();

    public IReadOnlyList<StringOption> OptionalFiammettaTargetOptions
        => _optionalFiammettaTargetOptions ??=
        [
            new StringOption(string.Empty, Texts.GetOrDefault("Infrast.Fiammetta.Target.None", "None")),
            .. FiammettaTargetOptions,
        ];

    private static readonly string[] FiammettaTargetNames =
    [
        "清流", "可露希尔", "但书", "巫恋", "龙舌兰", "歌蕾蒂娅",
        "琴柳", "承曦格雷伊", "温蒂", "槐琥", "至简", "旅骨",
    ];

    private IReadOnlyList<StringOption> BuildFiammettaTargetOptions()
    {
        var operNames = OperNameIndex.Shared;
        return FiammettaTargetNames
            .Select(name => new StringOption(name, operNames.GetLocalizedDisplayName(name, Texts.Language)))
            .ToArray();
    }

    public bool ShowFiammettaTargets => FiammettaRecoveryEnabled;

    public bool FiammettaRecoveryEnabled
    {
        get => _fiammettaRecoveryEnabled;
        set
        {
            if (!SetProperty(ref _fiammettaRecoveryEnabled, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ShowFiammettaTargets));
            QueuePersist();
        }
    }

    public string FiammettaTarget1
    {
        get => _fiammettaTarget1;
        set => SetFiammettaTarget(ref _fiammettaTarget1, value, nameof(FiammettaTarget1));
    }

    public string FiammettaTarget2
    {
        get => _fiammettaTarget2;
        set => SetFiammettaTarget(ref _fiammettaTarget2, value, nameof(FiammettaTarget2));
    }

    public string FiammettaTarget3
    {
        get => _fiammettaTarget3;
        set => SetFiammettaTarget(ref _fiammettaTarget3, value, nameof(FiammettaTarget3));
    }

    public bool UsePinusSylvestris
    {
        get => _usePinusSylvestris;
        set
        {
            if (!SetProperty(ref _usePinusSylvestris, value))
            {
                return;
            }

            QueuePersist();
        }
    }

    public bool UsePerceptionInformation
    {
        get => _usePerceptionInformation;
        set
        {
            if (!SetProperty(ref _usePerceptionInformation, value))
            {
                return;
            }

            QueuePersist();
        }
    }

    public bool UseWorldlyPlight
    {
        get => _useWorldlyPlight;
        set
        {
            if (!SetProperty(ref _useWorldlyPlight, value))
            {
                return;
            }

            QueuePersist();
        }
    }

    public bool UseAbyssalHunter
    {
        get => _useAbyssalHunter;
        set
        {
            if (!SetProperty(ref _useAbyssalHunter, value))
            {
                return;
            }

            QueuePersist();
        }
    }

    public bool UseWeeklySchedule
    {
        get => _useWeeklySchedule;
        set
        {
            if (!SetProperty(ref _useWeeklySchedule, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ShowWeeklyScheduleDays));
            QueuePersist();
        }
    }

    public bool ShowWeeklyScheduleDays => UseWeeklySchedule;

    public bool WeeklyScheduleSunday
    {
        get => _weeklyScheduleSunday;
        set => SetWeeklyScheduleDay(ref _weeklyScheduleSunday, value, nameof(WeeklyScheduleSunday));
    }

    public bool WeeklyScheduleMonday
    {
        get => _weeklyScheduleMonday;
        set => SetWeeklyScheduleDay(ref _weeklyScheduleMonday, value, nameof(WeeklyScheduleMonday));
    }

    public bool WeeklyScheduleTuesday
    {
        get => _weeklyScheduleTuesday;
        set => SetWeeklyScheduleDay(ref _weeklyScheduleTuesday, value, nameof(WeeklyScheduleTuesday));
    }

    public bool WeeklyScheduleWednesday
    {
        get => _weeklyScheduleWednesday;
        set => SetWeeklyScheduleDay(ref _weeklyScheduleWednesday, value, nameof(WeeklyScheduleWednesday));
    }

    public bool WeeklyScheduleThursday
    {
        get => _weeklyScheduleThursday;
        set => SetWeeklyScheduleDay(ref _weeklyScheduleThursday, value, nameof(WeeklyScheduleThursday));
    }

    public bool WeeklyScheduleFriday
    {
        get => _weeklyScheduleFriday;
        set => SetWeeklyScheduleDay(ref _weeklyScheduleFriday, value, nameof(WeeklyScheduleFriday));
    }

    public bool WeeklyScheduleSaturday
    {
        get => _weeklyScheduleSaturday;
        set => SetWeeklyScheduleDay(ref _weeklyScheduleSaturday, value, nameof(WeeklyScheduleSaturday));
    }

    private void SetFiammettaTarget(ref string field, string? value, string propertyName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (!SetProperty(ref field, normalized, propertyName))
        {
            return;
        }

        QueuePersist();
    }

    private void SetWeeklyScheduleDay(ref bool field, bool value, string propertyName)
    {
        if (!SetProperty(ref field, value, propertyName))
        {
            return;
        }

        QueuePersist();
    }

    public bool ContinueTraining
    {
        get => _continueTraining;
        set
        {
            if (!SetProperty(ref _continueTraining, value))
            {
                return;
            }

            QueuePersist();
        }
    }

    public bool DormTrustEnabled
    {
        get => _dormTrustEnabled;
        set
        {
            if (!SetProperty(ref _dormTrustEnabled, value))
            {
                return;
            }

            QueuePersist();
        }
    }

    public bool DormNotStationedEnabled
    {
        get => _dormNotStationedEnabled;
        set
        {
            if (!SetProperty(ref _dormNotStationedEnabled, value))
            {
                return;
            }

            QueuePersist();
        }
    }

    public bool Replenish
    {
        get => _replenish;
        set
        {
            if (!SetProperty(ref _replenish, value))
            {
                return;
            }

            QueuePersist();
        }
    }

    public bool ReceptionMessageBoard
    {
        get => _receptionMessageBoard;
        set
        {
            if (!SetProperty(ref _receptionMessageBoard, value))
            {
                return;
            }

            QueuePersist();
        }
    }

    public bool ReceptionClueExchange
    {
        get => _receptionClueExchange;
        set
        {
            if (!SetProperty(ref _receptionClueExchange, value))
            {
                return;
            }

            QueuePersist();
        }
    }

    public bool ReceptionSendClue
    {
        get => _receptionSendClue;
        set
        {
            if (!SetProperty(ref _receptionSendClue, value))
            {
                return;
            }

            QueuePersist();
        }
    }

    public void SelectAllFacility()
    {
        _suppressFacilitySelectionChanged = true;
        try
        {
            foreach (var option in FacilityOptions)
            {
                option.IsSelected = true;
            }
        }
        finally
        {
            _suppressFacilitySelectionChanged = false;
        }

        QueuePersist();
    }

    public void ClearFacility()
    {
        _suppressFacilitySelectionChanged = true;
        try
        {
            foreach (var option in FacilityOptions)
            {
                option.IsSelected = false;
            }
        }
        finally
        {
            _suppressFacilitySelectionChanged = false;
        }

        QueuePersist();
    }

    public void MoveFacility(int sourceIndex, int targetIndex)
    {
        if (sourceIndex < 0
            || targetIndex < 0
            || sourceIndex >= FacilityOptions.Count
            || targetIndex >= FacilityOptions.Count
            || sourceIndex == targetIndex)
        {
            return;
        }

        FacilityOptions.Move(sourceIndex, targetIndex);
        QueuePersist();
    }

    public void SelectCustomFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        DefaultInfrast = UserDefinedInfrast;
        CustomFilePath = path;
    }

    public Task ReloadPersistentConfigAsync(CancellationToken cancellationToken = default)
    {
        ApplyPersistentDefaultInfrast(ResolveDefaultInfrast());
        return Task.CompletedTask;
    }

    public Task ReloadPlansAsync(CancellationToken cancellationToken = default)
        => ReloadPlansAsync(forceReload: false, resetPlanSelection: false, cancellationToken);

    public Task ReloadPlansAsync(bool forceReload, CancellationToken cancellationToken = default)
        => ReloadPlansAsync(forceReload, resetPlanSelection: false, cancellationToken);

    internal Task WaitForPendingPlanReloadAsync()
        => Volatile.Read(ref _pendingPlanReload);

    private void StartPlanReload(bool resetPlanSelection)
    {
        var reload = ReloadPlansAsync(forceReload: false, resetPlanSelection);
        Volatile.Write(ref _pendingPlanReload, reload);
    }

    private async Task ReloadPlansAsync(
        bool forceReload,
        bool resetPlanSelection,
        CancellationToken cancellationToken = default)
    {
        var reloadVersion = Interlocked.Increment(ref _planReloadVersion);
        if (!IsCustomMode || string.IsNullOrWhiteSpace(CustomFilePath))
        {
            ClearPlansIfCurrent(reloadVersion);
            LastErrorMessage = string.Empty;
            StatusMessage = string.Empty;
            return;
        }

        if (!File.Exists(CustomFilePath))
        {
            ClearPlansIfCurrent(reloadVersion);
            StatusMessage = string.Empty;
            LastErrorMessage = string.Format(
                Texts.GetOrDefault("Infrast.Error.CustomFileNotFound", "Custom file not found: {0}"),
                CustomFilePath);
            await Runtime.DiagnosticsService.RecordFailedResultAsync(
                "Infrast.ParsePlan",
                UiOperationResult.Fail(UiErrorCode.InfrastPlanParseFailed, LastErrorMessage),
                cancellationToken);
            return;
        }

        var fileInfo = new FileInfo(CustomFilePath);
        var cacheKey = new InfrastPlanCacheKey(
            fileInfo.FullName,
            fileInfo.LastWriteTimeUtc,
            fileInfo.Length);
        if (!forceReload && _planCache is { } cache && cache.Key == cacheKey)
        {
            await ApplyParsedPlansAsync(cache.Plans, reloadVersion, resetPlanSelection, cancellationToken);
            return;
        }

        _planCache = null;
        try
        {
            PlanFileReadCount++;
            var json = await File.ReadAllTextAsync(CustomFilePath, cancellationToken);
            var parsedPlans = InfrastPlanParser.Parse(json);
            if (reloadVersion != Volatile.Read(ref _planReloadVersion))
            {
                return;
            }

            _planCache = new InfrastPlanCacheEntry(cacheKey, parsedPlans);
            await ApplyParsedPlansAsync(parsedPlans, reloadVersion, resetPlanSelection, cancellationToken);
        }
        catch (Exception ex)
        {
            if (reloadVersion != Volatile.Read(ref _planReloadVersion))
            {
                return;
            }

            ClearPlansIfCurrent(reloadVersion);
            StatusMessage = string.Empty;
            LastErrorMessage = Texts.GetOrDefault("Infrast.Error.ParseFailed", "Failed to parse custom infrast file.");
            await Runtime.DiagnosticsService.RecordFailedResultAsync(
                "Infrast.ParsePlan",
                UiOperationResult.Fail(UiErrorCode.InfrastPlanParseFailed, LastErrorMessage, ex.Message),
                cancellationToken);
        }
    }

    private Task ApplyParsedPlansAsync(
        IReadOnlyList<InfrastPlanDefinition> parsedPlans,
        int reloadVersion,
        bool resetPlanSelection,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (reloadVersion != Volatile.Read(ref _planReloadVersion))
        {
            return Task.CompletedTask;
        }

        var plans = parsedPlans.ToArray();
        Volatile.Write(ref _parsedPlans, plans);

        var planOptions = new List<PlanOption>();
        if (plans.Any(plan => plan.Periods.Count > 0))
        {
            planOptions.Add(new PlanOption(-1, BuildAutoPlanDisplayName()));
        }

        foreach (var plan in plans)
        {
            planOptions.Add(new PlanOption(plan.Index, plan.Name));
        }

        ReplacePlanOptions(planOptions);

        var normalizedPlanIndex = resetPlanSelection
            ? (plans.Any(plan => plan.Periods.Count > 0) ? -1 : 0)
            : NormalizeCustomPlanIndex(SelectedPlanIndex, plans.Length);
        if (normalizedPlanIndex != SelectedPlanIndex)
        {
            SelectedPlanIndex = normalizedPlanIndex;
            RequestPersistAfterBind();
        }

        SelectedPlan = planOptions.FirstOrDefault(option => option.Index == SelectedPlanIndex);
        LastErrorMessage = string.Empty;

        StatusMessage = string.Format(
            CultureInfo.CurrentCulture,
            Texts.GetOrDefault("Infrast.Status.LoadedPlans", "Loaded {0} plans."),
            plans.Length);
        return Task.CompletedTask;
    }

    private void ClearPlansIfCurrent(int reloadVersion)
    {
        if (reloadVersion != Volatile.Read(ref _planReloadVersion))
        {
            return;
        }

        Volatile.Write(ref _parsedPlans, []);
        ReplacePlanOptions([]);
        SelectedPlan = null;
    }

    private void ReplacePlanOptions(IEnumerable<PlanOption> options)
    {
        Volatile.Write(ref _planOptions, options.ToArray());
        OnPropertyChanged(nameof(PlanOptions));
    }

    private static int NormalizeCustomPlanIndex(int planIndex, int planCount)
        => planIndex < -1 ? -1 : planIndex >= planCount ? 0 : planIndex;

    protected override async Task LoadFromParametersAsync(JsonObject parameters, CancellationToken cancellationToken)
    {
        var model = InfrastParams.FromJson(parameters);
        _loadingParameters = true;
        try
        {
            Mode = model.Mode;
            Drones = model.Drones;
            DormThresholdPercent = Math.Clamp((int)Math.Round(model.Threshold * 100), 0, 100);
            CustomFilePath = model.Filename;
            SelectedPlanIndex = model.PlanIndex;
            ContinueTraining = model.ContinueTraining;
            DormTrustEnabled = model.DormTrustEnabled;
            DormNotStationedEnabled = model.DormNotStationedEnabled;
            Replenish = model.Replenish;
            ReceptionMessageBoard = model.ReceptionMessageBoard;
            ReceptionClueExchange = model.ReceptionClueExchange;
            ReceptionSendClue = model.ReceptionSendClue;
            FiammettaRecoveryEnabled = model.FiammettaRecoveryEnabled;
            FiammettaTarget1 = model.FiammettaTargets.ElementAtOrDefault(0) ?? string.Empty;
            FiammettaTarget2 = model.FiammettaTargets.ElementAtOrDefault(1) ?? string.Empty;
            FiammettaTarget3 = model.FiammettaTargets.ElementAtOrDefault(2) ?? string.Empty;
            UsePinusSylvestris = model.UsePinusSylvestris;
            UsePerceptionInformation = model.UsePerceptionInformation;
            UseWorldlyPlight = model.UseWorldlyPlight;
            UseAbyssalHunter = model.UseAbyssalHunter;
            UseWeeklySchedule = model.UseWeeklySchedule;
            WeeklyScheduleSunday = model.WeeklyScheduleSunday;
            WeeklyScheduleMonday = model.WeeklyScheduleMonday;
            WeeklyScheduleTuesday = model.WeeklyScheduleTuesday;
            WeeklyScheduleWednesday = model.WeeklyScheduleWednesday;
            WeeklyScheduleThursday = model.WeeklyScheduleThursday;
            WeeklyScheduleFriday = model.WeeklyScheduleFriday;
            WeeklyScheduleSaturday = model.WeeklyScheduleSaturday;
            ConfigureFacilityOptions(model.Facility, model.FacilityOrder);
        }
        finally
        {
            _loadingParameters = false;
        }

        await ReloadPlansAsync(cancellationToken);
    }

    protected override JsonObject BuildParameters()
    {
        var facilities = FacilityOptions
            .Where(option => option.IsSelected)
            .Select(option => option.Value)
            .ToList();

        var model = new InfrastParams
        {
            Mode = Mode,
            Facility = facilities,
            FacilityOrder = FacilityOptions.Select(option => option.Value).ToList(),
            Drones = Drones,
            ContinueTraining = ContinueTraining,
            Threshold = DormThresholdPercent / 100.0,
            DormTrustEnabled = DormTrustEnabled,
            DormNotStationedEnabled = DormNotStationedEnabled,
            Replenish = Replenish,
            ReceptionMessageBoard = ReceptionMessageBoard,
            ReceptionClueExchange = ReceptionClueExchange,
            ReceptionSendClue = ReceptionSendClue,
            Filename = CustomFilePath,
            PlanIndex = SelectedPlanIndex,
            FiammettaTargets = [FiammettaTarget1, FiammettaTarget2, FiammettaTarget3],
            FiammettaRecoveryEnabled = FiammettaRecoveryEnabled,
            UsePinusSylvestris = UsePinusSylvestris,
            UsePerceptionInformation = UsePerceptionInformation,
            UseWorldlyPlight = UseWorldlyPlight,
            UseAbyssalHunter = UseAbyssalHunter,
            UseWeeklySchedule = UseWeeklySchedule,
            WeeklyScheduleSunday = WeeklyScheduleSunday,
            WeeklyScheduleMonday = WeeklyScheduleMonday,
            WeeklyScheduleTuesday = WeeklyScheduleTuesday,
            WeeklyScheduleWednesday = WeeklyScheduleWednesday,
            WeeklyScheduleThursday = WeeklyScheduleThursday,
            WeeklyScheduleFriday = WeeklyScheduleFriday,
            WeeklyScheduleSaturday = WeeklyScheduleSaturday,
        };

        return model.ToJson();
    }

    private void OnTextsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(LocalizedTextMap.Language) or "Item[]"))
        {
            return;
        }

        RebuildLocalizedOptions();
        var planOptions = PlanOptions;
        if (planOptions.Count > 0 && planOptions[0].Index == -1)
        {
            var localizedOptions = planOptions.ToArray();
            localizedOptions[0] = new PlanOption(-1, BuildAutoPlanDisplayName());
            ReplacePlanOptions(localizedOptions);
            if (SelectedPlanIndex == -1)
            {
                SelectedPlan = localizedOptions[0];
            }
        }
    }

    private void RebuildLocalizedOptions()
    {
        _modeOptions =
        [
            new IntOption(InfrastModeNormal, Texts.GetOrDefault("Infrast.Mode.Normal", "Default")),
            new IntOption(InfrastModeRotation, Texts.GetOrDefault("Infrast.Mode.Rotation", "Rotation")),
            new IntOption(InfrastModeCustom, Texts.GetOrDefault("Infrast.Mode.Custom", "Custom")),
        ];

        _droneOptions =
        [
            new StringOption("_NotUse", Texts.GetOrDefault("Infrast.Drone.NotUse", "Do not use drones")),
            new StringOption("Money", Texts.GetOrDefault("Infrast.Drone.Money", "LMD")),
            new StringOption("SyntheticJade", Texts.GetOrDefault("Infrast.Drone.SyntheticJade", "Originium shard")),
            new StringOption("CombatRecord", Texts.GetOrDefault("Infrast.Drone.CombatRecord", "Battle record")),
            new StringOption("PureGold", Texts.GetOrDefault("Infrast.Drone.PureGold", "Pure gold")),
            new StringOption("OriginStone", Texts.GetOrDefault("Infrast.Drone.OriginStone", "Originium")),
            new StringOption("Chip", Texts.GetOrDefault("Infrast.Drone.Chip", "Chip")),
        ];

        _defaultInfrastOptions =
        [
            new StringOption(UserDefinedInfrast, Texts.GetOrDefault("Infrast.Default.UserDefined", "User defined")),
            new StringOption("153_layout_3_times_a_day.json", Texts.GetOrDefault("Infrast.Default.153Time3", "153 (3 shifts/day)")),
            new StringOption("153_layout_4_times_a_day.json", Texts.GetOrDefault("Infrast.Default.153Time4", "153 (4 shifts/day)")),
            new StringOption("243_layout_3_times_a_day.json", Texts.GetOrDefault("Infrast.Default.243Time3", "243 (3 shifts/day)")),
            new StringOption("243_layout_4_times_a_day.json", Texts.GetOrDefault("Infrast.Default.243Time4", "243 (4 shifts/day)")),
            new StringOption("333_layout_for_Orundum_3_times_a_day.json", Texts.GetOrDefault("Infrast.Default.333Time3", "333 (orundum, 3 shifts/day)")),
        ];

        _fiammettaTargetOptions = null;
        _optionalFiammettaTargetOptions = null;

        OnPropertyChanged(nameof(ModeOptions));
        OnPropertyChanged(nameof(DroneOptions));
        OnPropertyChanged(nameof(DefaultInfrastOptions));
        OnPropertyChanged(nameof(FiammettaTargetOptions));
        OnPropertyChanged(nameof(OptionalFiammettaTargetOptions));
        OnPropertyChanged(nameof(SelectedModeOption));
        OnPropertyChanged(nameof(SelectedDroneOption));
        OnPropertyChanged(nameof(SelectedDefaultInfrastOption));
        RefreshFacilityDisplayNames();
    }

    private void RefreshFacilityDisplayNames()
    {
        foreach (var option in FacilityOptions)
        {
            option.DisplayName = ResolveFacilityDisplayName(option.Value);
        }
    }

    private string ResolveFacilityDisplayName(string value)
        => Texts.GetOrDefault(value, value);

    private void ConfigureFacilityOptions(
        IEnumerable<string> enabledFacilities,
        IEnumerable<string>? persistedOrder = null)
    {
        var enabled = enabledFacilities
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var enabledSet = enabled.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var orderedNames = (persistedOrder ?? enabled)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var name in enabled)
        {
            if (!orderedNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                orderedNames.Add(name);
            }
        }

        foreach (var room in DefaultFacilityOrder)
        {
            if (!orderedNames.Contains(room, StringComparer.OrdinalIgnoreCase))
            {
                orderedNames.Add(room);
            }
        }

        foreach (var item in FacilityOptions)
        {
            item.PropertyChanged -= OnFacilityOptionChanged;
        }

        _suppressFacilitySelectionChanged = true;
        try
        {
            FacilityOptions.Clear();
            foreach (var name in orderedNames)
            {
                var option = new FacilityOption(name, ResolveFacilityDisplayName(name))
                {
                    IsSelected = enabledSet.Contains(name),
                };
                option.PropertyChanged += OnFacilityOptionChanged;
                FacilityOptions.Add(option);
            }
        }
        finally
        {
            _suppressFacilitySelectionChanged = false;
        }
    }

    private void OnFacilityOptionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressFacilitySelectionChanged || e.PropertyName != nameof(FacilityOption.IsSelected))
        {
            return;
        }

        QueuePersist();
    }

    private string ResolveDefaultInfrast()
    {
        if (Runtime.ConfigurationService.TryGetCurrentProfile(out var profile)
            && profile.Values.TryGetValue(ConfigurationKeys.DefaultInfrast, out var profileNode)
            && TryReadString(profileNode, out var profileValue)
            && !string.IsNullOrWhiteSpace(profileValue))
        {
            return profileValue.Trim();
        }

        var globals = Runtime.ConfigurationService.CurrentConfig.GlobalValues;
        if (!globals.TryGetValue(ConfigurationKeys.DefaultInfrast, out var node) || node is not JsonValue value)
        {
            return UserDefinedInfrast;
        }

        return value.TryGetValue(out string? resolved) && !string.IsNullOrWhiteSpace(resolved)
            ? resolved
            : UserDefinedInfrast;
    }

    private async Task PersistDefaultInfrastAsync(string value)
    {
        _ = await ConfigurationSaveTracker.Instance.RunTrackedAsync(
            "TaskQueue.Infrast.DefaultInfrast",
            Texts.GetOrDefault("Infrast.Title", "基建换班"),
            "Infrast.DefaultInfrast.Save",
            Runtime.DiagnosticsService,
            async cancellationToken =>
            {
                if (Runtime.ConfigurationService.TryGetCurrentProfile(out var profile))
                {
                    profile.Values[ConfigurationKeys.DefaultInfrast] = JsonValue.Create(value);
                }
                else
                {
                    Runtime.ConfigurationService.CurrentConfig.GlobalValues[ConfigurationKeys.DefaultInfrast] = JsonValue.Create(value);
                }

                await Runtime.ConfigurationService.SaveAsync(cancellationToken);
                return true;
            });
    }

    private static bool IsUserDefinedDefault(string value)
        => string.Equals(value, UserDefinedInfrast, StringComparison.OrdinalIgnoreCase);

    private void ApplyPersistentDefaultInfrast(string value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? UserDefinedInfrast : value.Trim();
        var readOnly = !IsUserDefinedDefault(normalized);

        _suppressDefaultInfrastPersist = true;
        try
        {
            SetProperty(ref _defaultInfrast, normalized, nameof(DefaultInfrast));
            SetProperty(ref _isCustomFileReadOnly, readOnly, nameof(IsCustomFileReadOnly));
            OnPropertyChanged(nameof(SelectedDefaultInfrastOption));
        }
        finally
        {
            _suppressDefaultInfrastPersist = false;
        }
    }

    private static bool TryReadString(JsonNode? node, out string value)
    {
        if (node is JsonValue jsonValue
            && jsonValue.TryGetValue(out string? text)
            && !string.IsNullOrWhiteSpace(text))
        {
            value = text;
            return true;
        }

        var raw = node?.ToString();
        if (!string.IsNullOrWhiteSpace(raw))
        {
            value = raw;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private string BuildAutoPlanDisplayName()
    {
        var currentPlanName = ResolveCurrentPlanName() ?? "???";
        return string.Format(
            Texts.GetOrDefault("Infrast.Plan.AutoCurrent", "Auto by time ({0})"),
            currentPlanName);
    }

    private string? ResolveCurrentPlanName()
    {
        var now = TimeOnly.FromDateTime(DateTime.Now.ToLocalTime());
        var plans = Volatile.Read(ref _parsedPlans);
        var current = plans.FirstOrDefault(plan => plan.Periods.Any(period => period.Contains(now)));
        current ??= plans.FirstOrDefault();
        return current?.Name;
    }

    private static IntOption? ResolveSelectedOption(IReadOnlyList<IntOption> options, int value)
    {
        return options.FirstOrDefault(option => option.Value == value)
               ?? options.FirstOrDefault();
    }

    private static StringOption? ResolveSelectedOption(IReadOnlyList<StringOption> options, string value)
    {
        return options.FirstOrDefault(option => string.Equals(option.Value, value, StringComparison.OrdinalIgnoreCase))
               ?? options.FirstOrDefault();
    }

    public sealed record PlanOption(int Index, string Display);

    public sealed record IntOption(int Value, string DisplayName);

    public sealed record StringOption(string Value, string DisplayName);

    public sealed class FacilityOption : ObservableObject
    {
        private bool _isSelected;
        private string _displayName;

        public FacilityOption(string value, string displayName)
        {
            Value = value;
            _displayName = displayName;
        }

        public string Value { get; }

        public string DisplayName
        {
            get => _displayName;
            set => SetProperty(ref _displayName, value);
        }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }
    }

    private sealed record InfrastPlanCacheEntry(InfrastPlanCacheKey Key, IReadOnlyList<InfrastPlanDefinition> Plans);

    private readonly record struct InfrastPlanCacheKey(string FullPath, DateTime LastWriteTimeUtc, long Length);

}
