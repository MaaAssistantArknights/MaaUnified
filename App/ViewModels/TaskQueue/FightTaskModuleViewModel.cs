using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using MAAUnified.App.Services;
using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.Application.Models;
using MAAUnified.Application.Models.TaskParams;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Localization;
using MAAUnified.Application.Services.TaskParams;
using MAAUnified.Compat.Runtime;
using LegacyConfigurationKeys = MAAUnified.Compat.Constants.ConfigurationKeys;

namespace MAAUnified.App.ViewModels.TaskQueue;

public sealed class FightTaskModuleViewModel : TypedTaskModuleViewModelBase<FightTaskParamsDto>
{
    private static readonly HashSet<string> ExcludedDropIds =
    [
        "3213", "3223", "3233", "3243",
        "3253", "3263", "3273", "3283",
        "7001", "7002", "7003", "7004",
        "4004", "4005",
        "3105", "3131", "3132", "3133",
        "6001",
        "3141", "4002",
        "32001",
        "30115",
        "30125",
        "30135",
        "30145",
        "30155",
        "30165",
    ];

    private static readonly IReadOnlyDictionary<string, string> DisplayLanguageClientDirectoryMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["zh-tw"] = "txwy",
            ["en-us"] = "YoStarEN",
            ["ja-jp"] = "YoStarJP",
            ["ko-kr"] = "YoStarKR",
        };

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<DayOfWeek>> WeeklyOpenStageCodes =
        new Dictionary<string, IReadOnlySet<DayOfWeek>>(StringComparer.OrdinalIgnoreCase)
        {
            ["CE-6"] = new HashSet<DayOfWeek> { DayOfWeek.Tuesday, DayOfWeek.Thursday, DayOfWeek.Saturday, DayOfWeek.Sunday },
            ["AP-5"] = new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Thursday, DayOfWeek.Saturday, DayOfWeek.Sunday },
            ["CA-5"] = new HashSet<DayOfWeek> { DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Friday, DayOfWeek.Sunday },
            ["SK-5"] = new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday, DayOfWeek.Saturday },
            ["PR-A-1"] = new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Sunday },
            ["PR-A-2"] = new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Sunday },
            ["PR-B-1"] = new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Friday, DayOfWeek.Saturday },
            ["PR-B-2"] = new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Friday, DayOfWeek.Saturday },
            ["PR-C-1"] = new HashSet<DayOfWeek> { DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Saturday, DayOfWeek.Sunday },
            ["PR-C-2"] = new HashSet<DayOfWeek> { DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Saturday, DayOfWeek.Sunday },
            ["PR-D-1"] = new HashSet<DayOfWeek> { DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Saturday, DayOfWeek.Sunday },
            ["PR-D-2"] = new HashSet<DayOfWeek> { DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Saturday, DayOfWeek.Sunday },
        };

    private static readonly IReadOnlyList<string> FallbackStageCodes =
    [
        "1-7",
        "R8-11",
        "12-17-HARD",
        "CE-6",
        "AP-5",
        "CA-5",
        "LS-6",
        "SK-5",
        "Annihilation",
        "PR-A-1",
        "PR-A-2",
        "PR-B-1",
        "PR-B-2",
        "PR-C-1",
        "PR-C-2",
        "PR-D-1",
        "PR-D-2",
    ];

    private static readonly IReadOnlyList<string> StageActivityCandidateRelativePaths =
    [
        Path.Combine("cache", "gui", "StageActivityV2.json"),
        Path.Combine("gui", "StageActivityV2.json"),
        Path.Combine("resource", "gui", "StageActivityV2.json"),
    ];

    private static readonly IReadOnlyList<string> StageJsonCandidateRelativePaths =
    [
        Path.Combine("resource", "stages.json"),
    ];

    private static readonly IReadOnlyList<(string StageCode, string ZhTip, string EnTip, IReadOnlyList<string[]>? InventoryGroups)> DailyHintSpecs =
    [
        ("CE-6", "CE-6: 龙门币", "CE-6: LMD", null),
        ("AP-5", "AP-5: 红票", "AP-5: Purchase Certificate", null),
        ("CA-5", "CA-5: 技能", "CA-5: Skill Summary", null),
        ("LS-6", "LS-6: 经验", "LS-6: Battle Record", null),
        ("SK-5", "SK-5: 碳", "SK-5: Carbon", null),
        ("PR-A-1", "PR-A-1/2: 奶&盾芯片", "PR-A-1/2: Med&Def Chip", [["3231", "3261"], ["3232", "3262"]]),
        ("PR-B-1", "PR-B-1/2: 术&狙芯片", "PR-B-1/2: Cst&Sni Chip", [["3251", "3241"], ["3252", "3242"]]),
        ("PR-C-1", "PR-C-1/2: 先&辅芯片", "PR-C-1/2: Pio&Sup Chip", [["3211", "3271"], ["3212", "3272"]]),
        ("PR-D-1", "PR-D-1/2: 近&特芯片", "PR-D-1/2: Grd&Spc Chip", [["3221", "3281"], ["3222", "3282"]]),
    ];

    private static readonly IReadOnlyDictionary<string, string> ManualStageAliasMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AN"] = "Annihilation",
            ["剿灭"] = "Annihilation",
            ["CE"] = "CE-6",
            ["龙门币"] = "CE-6",
            ["LS"] = "LS-6",
            ["经验"] = "LS-6",
            ["狗粮"] = "LS-6",
            ["CA"] = "CA-5",
            ["技能"] = "CA-5",
            ["AP"] = "AP-5",
            ["红票"] = "AP-5",
            ["SK"] = "SK-5",
            ["碳"] = "SK-5",
            ["炭"] = "SK-5",
        };

    private readonly ObservableCollection<StagePlanEntry> _stagePlan = [];
    private bool _suppressStagePlanSync;
    private string _stage = FightStageSelection.CurrentOrLast;
    private bool _isStageManually;
    private bool? _useMedicine = false;
    private int _medicine;
    private bool? _useStone = false;
    private int _stone;
    private bool? _enableTimesLimit = false;
    private int _times = int.MaxValue;
    private int _series = 1;
    private bool _isDrGrandet;
    private bool _useExpiringMedicine;
    private int _expiringMedicine = 9999;
    private bool? _enableTargetDrop = false;
    private string _dropId = string.Empty;
    private int _dropCount = 1;
    private bool _useCustomAnnihilation;
    private string _annihilationStage = "Annihilation";
    private bool _useWeeklySchedule;
    private bool _weeklyScheduleSunday = true;
    private bool _weeklyScheduleMonday = true;
    private bool _weeklyScheduleTuesday = true;
    private bool _weeklyScheduleWednesday = true;
    private bool _weeklyScheduleThursday = true;
    private bool _weeklyScheduleFriday = true;
    private bool _weeklyScheduleSaturday = true;
    private bool _useAlternateStage;
    private bool _hideUnavailableStage = true;
    private string _stageResetMode = "Current";
    private bool _hideSeries;
    private bool _allowUseStoneSave;
    private bool _autoRestartOnDrop = true;
    private IReadOnlyList<IntOption> _seriesOptions = [];
    private IReadOnlyList<StringOption> _stageResetModeOptions = [];
    private IReadOnlyList<StringOption> _annihilationStageOptions = [];
    private IReadOnlyList<DropOption> _dropOptions = [];
    private readonly ObservableCollection<StageOption> _stageOptions = [];

    public FightTaskModuleViewModel(MAAUnifiedRuntime runtime, LocalizedTextMap texts)
        : base(runtime, texts, "TaskQueue.Fight")
    {
        _stagePlan.CollectionChanged += (_, e) => OnStagePlanCollectionChanged(e);
        Texts.PropertyChanged += OnTextsChanged;
        ApplyPersistentAutoRestartOnDrop(ResolveAutoRestartOnDrop());
        EnsureStagePlanInitialized();
        RebuildLocalizedOptions();
        RebuildDropOptions();
        RebuildStageOptions();
    }

    public IReadOnlyList<IntOption> SeriesOptions => _seriesOptions;

    public IntOption? SelectedSeriesOption
    {
        get => SeriesOptions.FirstOrDefault(option => option.Value == Series);
        set => Series = value?.Value ?? 1;
    }

    public IReadOnlyList<StringOption> StageResetModeOptions => _stageResetModeOptions;

    public StringOption? SelectedStageResetModeOption
    {
        get => StageResetModeOptions.FirstOrDefault(
            option => string.Equals(option.Value, StageResetMode, StringComparison.OrdinalIgnoreCase));
        set => StageResetMode = value?.Value ?? "Current";
    }

    public IReadOnlyList<StringOption> AnnihilationStageOptions => _annihilationStageOptions;

    public StringOption? SelectedAnnihilationStageOption
    {
        get => FindAnnihilationStageOption(AnnihilationStage);
        set => AnnihilationStage = value?.Value ?? "Annihilation";
    }

    public string SelectedAnnihilationStageValue
    {
        get => AnnihilationStage;
        set
        {
            if (value is null)
            {
                return;
            }

            AnnihilationStage = value;
        }
    }

    public IReadOnlyList<StageOption> StageOptions => _stageOptions;

    public ObservableCollection<StagePlanEntry> StagePlan => _stagePlan;

    public StageOption? SelectedStageOption
    {
        get => StageOptions.FirstOrDefault(
            option => string.Equals(option.Value, Stage, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is null)
            {
                OnPropertyChanged(nameof(SelectedStageOption));
                OnPropertyChanged(nameof(SelectedStageValue));
                return;
            }

            Stage = value.Value;
        }
    }

    public string SelectedStageValue
    {
        get => SelectedStageOption?.Value ?? Stage;
        set
        {
            if (IsTransientEmptySelectionValue(value))
            {
                RequestSelectedStageValueRefresh();
                return;
            }

            Stage = value;
        }
    }

    private void RequestSelectedStageValueRefresh()
    {
        Dispatcher.UIThread.Post(
            () =>
            {
                OnPropertyChanged(nameof(SelectedStageOption));
                OnPropertyChanged(nameof(SelectedStageValue));
            },
            DispatcherPriority.Background);
    }

    public IReadOnlyList<DropOption> DropOptions => _dropOptions;

    public DropOption? SelectedDropOption
    {
        get => DropOptions.FirstOrDefault(option => string.Equals(option.Value, DropId, StringComparison.Ordinal));
        set => DropId = value?.Value ?? string.Empty;
    }

    public bool UseStoneDisplay
    {
        get => UseStone != false;
        set => UseStone = value;
    }

    public bool IsMedicineToggleEnabled => !UseStoneDisplay;

    public bool IsMedicineInputEnabled => !UseStoneDisplay;

    public bool ShowSeriesSetting => !HideSeries;

    public bool IsDropControlsEnabled => EnableTargetDrop != false;

    public bool IsHideUnavailableStageEnabled => !UseWeeklySchedule;

    public bool CanRemoveStagePlanEntry => StagePlan.Count > 1;

    public bool ShowStagePlanSelector => !UseAlternateStage;

    public bool ShowStagePlanList => UseAlternateStage;

    public bool ShowStagePlanComboBox => !IsStageManually;

    public bool ShowStagePlanTextBox => IsStageManually;

    public string UseStoneDisplayName =>
        $"{Texts.GetOrDefault("Fight.UseStoneDisplay", Texts.GetOrDefault("Fight.UseStone", "Use stone"))}{(AllowUseStoneSave ? string.Empty : "*")}";

    public string StageSelectDisplayText => UseAlternateStage
        ? Texts.GetOrDefault("Fight.StageSelect2", Texts.GetOrDefault("StageSelect2", "Candidates"))
        : Texts.GetOrDefault("Fight.StageSelect", "Stage");

    public string AddStageText => Texts.GetOrDefault("Fight.AddStage", Texts.GetOrDefault("AddStage", "Add candidate"));

    public string CustomStageCodeText => Texts.GetOrDefault("Fight.CustomStageCode", Texts.GetOrDefault("CustomStageCode", "Manual entry of stage names"));

    public string CustomStageCodeTipText => Texts.GetOrDefault(
        "Fight.CustomStageCodeTip",
        Texts.GetOrDefault("CustomStageCodeTip", "Support most main stage names and stage names from the original list."));

    public string MultiTasksShareTipText => Texts.GetOrDefault(
        "Fight.MultiTasksShareTip",
        Texts.GetOrDefault("MultiTasksShareTip", "The following options are shared across multiple tasks."));

    public string AutoRestartOnDropText => Texts.GetOrDefault(
        "Fight.AutoRestartOption",
        Texts.GetOrDefault("AutoRestartOption", "Restart when game disconnects"));

    public string UseWeeklyScheduleText => Texts.GetOrDefault("Fight.UseWeeklySchedule", "Enable weekly schedule");

    public string UseWeeklyScheduleTip => Texts.GetOrDefault(
        "Fight.UseWeeklyScheduleTip",
        "The day of week here follows in-game reset time rather than local clock time.");

    public string WeeklyScheduleSundayText => Texts.GetOrDefault("Fight.WeeklySchedule.Sunday", "Sun");

    public string WeeklyScheduleMondayText => Texts.GetOrDefault("Fight.WeeklySchedule.Monday", "Mon");

    public string WeeklyScheduleTuesdayText => Texts.GetOrDefault("Fight.WeeklySchedule.Tuesday", "Tue");

    public string WeeklyScheduleWednesdayText => Texts.GetOrDefault("Fight.WeeklySchedule.Wednesday", "Wed");

    public string WeeklyScheduleThursdayText => Texts.GetOrDefault("Fight.WeeklySchedule.Thursday", "Thu");

    public string WeeklyScheduleFridayText => Texts.GetOrDefault("Fight.WeeklySchedule.Friday", "Fri");

    public string WeeklyScheduleSaturdayText => Texts.GetOrDefault("Fight.WeeklySchedule.Saturday", "Sat");

    public string MoveStagePlanEntryUpText => Texts.GetOrDefault("TaskQueue.Root.MoveUp", "Move up");

    public string MoveStagePlanEntryDownText => Texts.GetOrDefault("TaskQueue.Root.MoveDown", "Move down");

    public string RemoveStagePlanEntryText => Texts.GetOrDefault("TaskQueue.Root.Delete", "Delete");

    public string Stage
    {
        get => _stage;
        set
        {
            var normalized = NormalizeStagePlanEntryValue(value);
            if (!SetTrackedProperty(ref _stage, normalized))
            {
                return;
            }

            if (!_suppressStagePlanSync)
            {
                SetFirstStagePlanEntry(normalized);
            }

            OnPropertyChanged(nameof(SelectedStageOption));
            OnPropertyChanged(nameof(SelectedStageValue));
        }
    }

    public bool IsStageManually
    {
        get => _isStageManually;
        set
        {
            if (!SetTrackedProperty(ref _isStageManually, value))
            {
                return;
            }

            if (!value)
            {
                NormalizeStagePlanAgainstKnownStages();
            }

            OnPropertyChanged(nameof(ShowStagePlanComboBox));
            OnPropertyChanged(nameof(ShowStagePlanTextBox));
            RefreshStagePlanPresentation();
        }
    }

    public bool? UseMedicine
    {
        get => _useMedicine;
        set
        {
            if (!SetTrackedProperty(ref _useMedicine, value))
            {
                return;
            }

            if (value == false && UseStone != false)
            {
                UseStoneDisplay = false;
            }

            OnPropertyChanged(nameof(IsMedicineToggleEnabled));
            OnPropertyChanged(nameof(IsMedicineInputEnabled));
        }
    }

    public int Medicine
    {
        get => _medicine;
        set => SetTrackedProperty(ref _medicine, Math.Max(0, value));
    }

    public bool? UseStone
    {
        get => _useStone;
        set
        {
            var requestedValue = value;
            if (!AllowUseStoneSave && value == true)
            {
                value = null;
            }

            if (!SetTrackedProperty(ref _useStone, value))
            {
                if (requestedValue != value)
                {
                    OnPropertyChanged(nameof(UseStone));
                }

                return;
            }

            if (value != false)
            {
                if (Medicine < 999)
                {
                    Medicine = 999;
                }

                if (UseMedicine == false)
                {
                    UseMedicine = value;
                }
            }

            OnPropertyChanged(nameof(UseStoneDisplay));
            OnPropertyChanged(nameof(IsMedicineToggleEnabled));
            OnPropertyChanged(nameof(IsMedicineInputEnabled));
        }
    }

    public int Stone
    {
        get => _stone;
        set => SetTrackedProperty(ref _stone, Math.Max(0, value));
    }

    public bool? EnableTimesLimit
    {
        get => _enableTimesLimit;
        set => SetTrackedProperty(ref _enableTimesLimit, value);
    }

    public int Times
    {
        get => _times;
        set => SetTrackedProperty(ref _times, Math.Max(0, value));
    }

    public int Series
    {
        get => _series;
        set
        {
            var normalized = Math.Clamp(value, -1, 6);
            if (!SetTrackedProperty(ref _series, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedSeriesOption));
        }
    }

    public bool IsDrGrandet
    {
        get => _isDrGrandet;
        set => SetTrackedProperty(ref _isDrGrandet, value);
    }

    public bool UseExpiringMedicine
    {
        get => _useExpiringMedicine;
        set => SetTrackedProperty(ref _useExpiringMedicine, value);
    }

    public bool? EnableTargetDrop
    {
        get => _enableTargetDrop;
        set
        {
            if (!SetTrackedProperty(ref _enableTargetDrop, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsDropControlsEnabled));
        }
    }

    public bool AutoRestartOnDrop
    {
        get => _autoRestartOnDrop;
        set
        {
            if (!SetProperty(ref _autoRestartOnDrop, value))
            {
                return;
            }

            _ = PersistAutoRestartOnDropAsync(value);
        }
    }

    public string DropId
    {
        get => _dropId;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (!SetTrackedProperty(ref _dropId, normalized))
            {
                return;
            }

            NormalizeDropSelectionToKnownOption();
            OnPropertyChanged(nameof(SelectedDropOption));
        }
    }

    public int DropCount
    {
        get => _dropCount;
        set => SetTrackedProperty(ref _dropCount, Math.Max(1, value));
    }

    public bool UseCustomAnnihilation
    {
        get => _useCustomAnnihilation;
        set
        {
            if (!SetTrackedProperty(ref _useCustomAnnihilation, value))
            {
                return;
            }

            RebuildStageOptions();
        }
    }

    public string AnnihilationStage
    {
        get => _annihilationStage;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "Annihilation" : value.Trim();
            if (!SetTrackedProperty(ref _annihilationStage, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedAnnihilationStageOption));
            OnPropertyChanged(nameof(SelectedAnnihilationStageValue));
            RebuildStageOptions();
        }
    }

    public bool UseWeeklySchedule
    {
        get => _useWeeklySchedule;
        set
        {
            if (!SetTrackedProperty(ref _useWeeklySchedule, value))
            {
                return;
            }

            if (value && HideUnavailableStage)
            {
                HideUnavailableStage = false;
            }

            OnPropertyChanged(nameof(IsHideUnavailableStageEnabled));
        }
    }

    public bool WeeklyScheduleSunday
    {
        get => _weeklyScheduleSunday;
        set => SetTrackedProperty(ref _weeklyScheduleSunday, value);
    }

    public bool WeeklyScheduleMonday
    {
        get => _weeklyScheduleMonday;
        set => SetTrackedProperty(ref _weeklyScheduleMonday, value);
    }

    public bool WeeklyScheduleTuesday
    {
        get => _weeklyScheduleTuesday;
        set => SetTrackedProperty(ref _weeklyScheduleTuesday, value);
    }

    public bool WeeklyScheduleWednesday
    {
        get => _weeklyScheduleWednesday;
        set => SetTrackedProperty(ref _weeklyScheduleWednesday, value);
    }

    public bool WeeklyScheduleThursday
    {
        get => _weeklyScheduleThursday;
        set => SetTrackedProperty(ref _weeklyScheduleThursday, value);
    }

    public bool WeeklyScheduleFriday
    {
        get => _weeklyScheduleFriday;
        set => SetTrackedProperty(ref _weeklyScheduleFriday, value);
    }

    public bool WeeklyScheduleSaturday
    {
        get => _weeklyScheduleSaturday;
        set => SetTrackedProperty(ref _weeklyScheduleSaturday, value);
    }

    public bool UseAlternateStage
    {
        get => _useAlternateStage;
        set
        {
            if (!SetTrackedProperty(ref _useAlternateStage, value))
            {
                return;
            }

            if (value)
            {
                if (HideUnavailableStage)
                {
                    HideUnavailableStage = false;
                }

                if (!string.Equals(StageResetMode, "Ignore", StringComparison.OrdinalIgnoreCase))
                {
                    StageResetMode = "Ignore";
                }
            }
            else
            {
                CollapseStagePlanToPrimaryEntry();
            }

            OnPropertyChanged(nameof(StageSelectDisplayText));
            OnPropertyChanged(nameof(ShowStagePlanSelector));
            OnPropertyChanged(nameof(ShowStagePlanList));
        }
    }

    public bool HideUnavailableStage
    {
        get => _hideUnavailableStage;
        set
        {
            if (!SetTrackedProperty(ref _hideUnavailableStage, value))
            {
                return;
            }

            if (value)
            {
                if (UseAlternateStage)
                {
                    UseAlternateStage = false;
                }

                if (!string.Equals(StageResetMode, "Current", StringComparison.OrdinalIgnoreCase))
                {
                    StageResetMode = "Current";
                }
            }

            RebuildStageOptions();
            RefreshStagePlanPresentation();
        }
    }

    public string StageResetMode
    {
        get => _stageResetMode;
        set
        {
            var normalized = NormalizeStageResetMode(value);
            if (!SetTrackedProperty(ref _stageResetMode, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedStageResetModeOption));
        }
    }

    public bool HideSeries
    {
        get => _hideSeries;
        set
        {
            if (!SetTrackedProperty(ref _hideSeries, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ShowSeriesSetting));
        }
    }

    public bool AllowUseStoneSave
    {
        get => _allowUseStoneSave;
        set
        {
            if (!SetTrackedProperty(ref _allowUseStoneSave, value))
            {
                return;
            }

            OnPropertyChanged(nameof(UseStoneDisplayName));

            if (!value && UseStone == true)
            {
                UseStone = null;
            }
        }
    }

    public Task ReloadPersistentConfigAsync(CancellationToken cancellationToken = default)
    {
        ApplyPersistentAutoRestartOnDrop(ResolveAutoRestartOnDrop());
        return Task.CompletedTask;
    }

    protected override Task<UiOperationResult<FightTaskParamsDto>> LoadDtoAsync(int index, CancellationToken cancellationToken)
    {
        return Runtime.TaskQueueFeatureService.GetFightParamsAsync(index, cancellationToken);
    }

    protected override Task<UiOperationResult> SaveDtoAsync(int index, FightTaskParamsDto dto, CancellationToken cancellationToken)
    {
        return Runtime.TaskQueueFeatureService.SaveFightParamsAsync(index, dto, cancellationToken);
    }

    protected override TaskCompileOutput CompileDto(FightTaskParamsDto dto, UnifiedProfile profile, UnifiedConfig config)
    {
        return TaskParamCompiler.CompileFight(dto, profile, config);
    }

    protected override void ApplyDto(FightTaskParamsDto dto)
    {
        IsStageManually = dto.IsStageManually;
        ReplaceStagePlan(dto.StagePlan.Count > 0 ? dto.StagePlan : [dto.Stage]);
        SetProperty(ref _stage, FightStageSelection.NormalizeStoredValue(dto.Stage), nameof(Stage));
        OnPropertyChanged(nameof(SelectedStageOption));
        AllowUseStoneSave = dto.AllowUseStoneSave;
        UseMedicine = dto.UseMedicine;
        Medicine = dto.Medicine;
        UseStone = dto.UseStone;
        Stone = dto.Stone;
        EnableTimesLimit = dto.EnableTimesLimit;
        Times = dto.Times;
        Series = dto.Series;
        IsDrGrandet = dto.IsDrGrandet;
        UseExpiringMedicine = dto.UseExpiringMedicine;
        _expiringMedicine = Math.Max(1, dto.ExpiringMedicine);
        EnableTargetDrop = dto.EnableTargetDrop;
        DropId = dto.DropId;
        DropCount = dto.DropCount;
        UseCustomAnnihilation = dto.UseCustomAnnihilation;
        AnnihilationStage = dto.AnnihilationStage;
        UseWeeklySchedule = dto.UseWeeklySchedule;
        WeeklyScheduleSunday = dto.WeeklyScheduleSunday;
        WeeklyScheduleMonday = dto.WeeklyScheduleMonday;
        WeeklyScheduleTuesday = dto.WeeklyScheduleTuesday;
        WeeklyScheduleWednesday = dto.WeeklyScheduleWednesday;
        WeeklyScheduleThursday = dto.WeeklyScheduleThursday;
        WeeklyScheduleFriday = dto.WeeklyScheduleFriday;
        WeeklyScheduleSaturday = dto.WeeklyScheduleSaturday;
        UseAlternateStage = dto.UseAlternateStage;
        HideUnavailableStage = dto.HideUnavailableStage;
        StageResetMode = dto.StageResetMode;
        HideSeries = dto.HideSeries;
        NormalizeDropSelectionToKnownOption();
        RebuildStageOptions();
        RefreshStagePlanPresentation();
    }

    protected override FightTaskParamsDto BuildDto()
    {
        var stagePlan = StagePlan.Select(entry => entry.Stage).ToList();
        return new FightTaskParamsDto
        {
            Stage = FightStageSelection.NormalizeStoredValue(Stage),
            StagePlan = FightStageSelection.NormalizeStagePlan(stagePlan),
            IsStageManually = IsStageManually,
            UseMedicine = UseMedicine,
            Medicine = Math.Max(0, Medicine),
            UseStone = UseStone,
            Stone = Math.Max(0, Stone),
            EnableTimesLimit = EnableTimesLimit,
            Times = EnableTimesLimit != false ? Math.Max(0, Times) : int.MaxValue,
            Series = Math.Clamp(Series, -1, 6),
            IsDrGrandet = IsDrGrandet,
            UseExpiringMedicine = UseExpiringMedicine,
            ExpiringMedicine = UseExpiringMedicine ? Math.Max(1, _expiringMedicine) : 0,
            EnableTargetDrop = EnableTargetDrop,
            DropId = DropId.Trim(),
            DropCount = Math.Max(1, DropCount),
            UseCustomAnnihilation = UseCustomAnnihilation,
            AnnihilationStage = string.IsNullOrWhiteSpace(AnnihilationStage) ? "Annihilation" : AnnihilationStage.Trim(),
            UseWeeklySchedule = UseWeeklySchedule,
            WeeklyScheduleSunday = WeeklyScheduleSunday,
            WeeklyScheduleMonday = WeeklyScheduleMonday,
            WeeklyScheduleTuesday = WeeklyScheduleTuesday,
            WeeklyScheduleWednesday = WeeklyScheduleWednesday,
            WeeklyScheduleThursday = WeeklyScheduleThursday,
            WeeklyScheduleFriday = WeeklyScheduleFriday,
            WeeklyScheduleSaturday = WeeklyScheduleSaturday,
            UseAlternateStage = UseAlternateStage,
            HideUnavailableStage = HideUnavailableStage,
            StageResetMode = NormalizeStageResetMode(StageResetMode),
            HideSeries = HideSeries,
            AllowUseStoneSave = AllowUseStoneSave,
        };
    }

    private void OnTextsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(LocalizedTextMap.Language) or "Item[]"))
        {
            return;
        }

        RebuildLocalizedOptions();
        RebuildDropOptions();
        RebuildStageOptions();
    }

    private void RebuildLocalizedOptions()
    {
        _seriesOptions =
        [
            new IntOption(0, "AUTO"),
            new IntOption(6, "6"),
            new IntOption(5, "5"),
            new IntOption(4, "4"),
            new IntOption(3, "3"),
            new IntOption(2, "2"),
            new IntOption(1, "1"),
            new IntOption(-1, Texts.GetOrDefault("Fight.NotSwitch", "Don't Switch")),
        ];
        _stageResetModeOptions =
        [
            new StringOption("Current", Texts.GetOrDefault("Fight.StageReset.Current", Texts.GetOrDefault("Fight.DefaultStage", "Cur/Last"))),
            new StringOption("Ignore", Texts.GetOrDefault("Fight.StageReset.Ignore", Texts.GetOrDefault("Fight.NotSwitch", "Don't Switch"))),
        ];
        _annihilationStageOptions =
        [
            new StringOption("Annihilation", Texts.GetOrDefault("Fight.Annihilation.Current", "Current Annihilation")),
            new StringOption("Chernobog@Annihilation", Texts.GetOrDefault("Fight.Annihilation.Chernobog", "Chernobog")),
            new StringOption("LungmenOutskirts@Annihilation", Texts.GetOrDefault("Fight.Annihilation.LungmenOutskirts", "Lungmen Outskirts")),
            new StringOption("LungmenDowntown@Annihilation", Texts.GetOrDefault("Fight.Annihilation.LungmenDowntown", "Lungmen Downtown")),
        ];

        OnPropertyChanged(nameof(SeriesOptions));
        OnPropertyChanged(nameof(SelectedSeriesOption));
        OnPropertyChanged(nameof(StageResetModeOptions));
        OnPropertyChanged(nameof(SelectedStageResetModeOption));
        OnPropertyChanged(nameof(AnnihilationStageOptions));
        OnPropertyChanged(nameof(SelectedAnnihilationStageOption));
        OnPropertyChanged(nameof(SelectedAnnihilationStageValue));
        OnPropertyChanged(nameof(UseStoneDisplayName));
        OnPropertyChanged(nameof(StageSelectDisplayText));
        OnPropertyChanged(nameof(AddStageText));
        OnPropertyChanged(nameof(CustomStageCodeText));
        OnPropertyChanged(nameof(CustomStageCodeTipText));
        OnPropertyChanged(nameof(MultiTasksShareTipText));
        OnPropertyChanged(nameof(AutoRestartOnDropText));
        OnPropertyChanged(nameof(UseWeeklyScheduleText));
        OnPropertyChanged(nameof(UseWeeklyScheduleTip));
        OnPropertyChanged(nameof(WeeklyScheduleSundayText));
        OnPropertyChanged(nameof(WeeklyScheduleMondayText));
        OnPropertyChanged(nameof(WeeklyScheduleTuesdayText));
        OnPropertyChanged(nameof(WeeklyScheduleWednesdayText));
        OnPropertyChanged(nameof(WeeklyScheduleThursdayText));
        OnPropertyChanged(nameof(WeeklyScheduleFridayText));
        OnPropertyChanged(nameof(WeeklyScheduleSaturdayText));
        OnPropertyChanged(nameof(MoveStagePlanEntryUpText));
        OnPropertyChanged(nameof(MoveStagePlanEntryDownText));
        OnPropertyChanged(nameof(RemoveStagePlanEntryText));
        RefreshStagePlanPresentation();
    }

    private void RebuildDropOptions()
    {
        _dropOptions = BuildDropOptionsForLanguage(
            UiLanguageCatalog.Normalize(Texts.Language),
            Texts.GetOrDefault("Fight.Drop.NotSelected", "Not selected"));
        NormalizeDropSelectionToKnownOption();
        OnPropertyChanged(nameof(DropOptions));
        OnPropertyChanged(nameof(SelectedDropOption));
    }

    private void NormalizeDropSelectionToKnownOption()
    {
        if (string.IsNullOrWhiteSpace(DropId))
        {
            return;
        }

        if (_dropOptions.Any(option => string.Equals(option.Value, DropId, StringComparison.Ordinal)))
        {
            return;
        }

        SetTrackedProperty(ref _dropId, string.Empty, nameof(DropId));
    }

    internal static IReadOnlyList<DropOption> BuildDropOptionsForLanguage(
        string language,
        string notSelectedText,
        string? baseDirectory = null)
    {
        var list = new List<DropOption>
        {
            new(notSelectedText, string.Empty),
        };

        foreach (var path in ResolveItemIndexCandidatePathsByDisplayLanguage(language, baseDirectory))
        {
            if (TryAppendDropOptionsFromItemIndex(path, list))
            {
                break;
            }
        }

        return list
            .GroupBy(option => option.Value, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(option => option.Value, StringComparer.Ordinal)
            .ToList();
    }

    internal static IReadOnlyList<string> ResolveItemIndexCandidatePathsByDisplayLanguage(
        string language,
        string? baseDirectory = null)
    {
        var root = baseDirectory ?? RuntimeLayout.ResolveRuntimeBaseDirectory();
        var candidates = new List<string>();

        if (DisplayLanguageClientDirectoryMap.TryGetValue(language, out var clientDirectory))
        {
            candidates.Add(Path.Combine(root, "resource", "global", clientDirectory, "resource", "item_index.json"));
        }

        candidates.Add(Path.Combine(root, "resource", "item_index.json"));
        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool TryAppendDropOptionsFromItemIndex(string path, List<DropOption> list)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var originalCount = list.Count;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (root is null)
            {
                return false;
            }

            foreach (var pair in root)
            {
                if (!int.TryParse(pair.Key, out _))
                {
                    continue;
                }

                if (ExcludedDropIds.Contains(pair.Key))
                {
                    continue;
                }

                if (pair.Value is not JsonObject itemObj
                    || !TryReadString(itemObj["name"], out var name)
                    || string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                list.Add(new DropOption(name, pair.Key));
            }
        }
        catch
        {
            return false;
        }

        return list.Count > originalCount;
    }

    private static bool TryReadString(JsonNode? node, out string value)
    {
        value = string.Empty;
        if (node is not JsonValue jsonValue || !jsonValue.TryGetValue(out string? parsed) || string.IsNullOrWhiteSpace(parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    public void RefreshStageOptions(string? clientType = null, bool forceReload = false)
    {
        RebuildStageOptions(clientType, forceReload);
    }

    public static string BuildDailyResourceHint(
        string language,
        string? clientType,
        UnifiedConfig? config = null,
        DateTime? nowUtc = null)
    {
        var zhCn = IsChineseLanguage(language);
        var normalizedClientType = NormalizeClientType(clientType);
        var timestamp = nowUtc ?? DateTime.UtcNow;
        var dayOfWeek = MallDailyResetHelper.GetYjDate(timestamp, normalizedClientType).DayOfWeek;
        var depotCounts = ReadDepotCounts(config);
        var lines = new List<string>();
        foreach (var spec in DailyHintSpecs)
        {
            if (!IsStageOpen(spec.StageCode, dayOfWeek))
            {
                continue;
            }

            lines.Add(zhCn ? spec.ZhTip : spec.EnTip);
            if (spec.InventoryGroups is null)
            {
                continue;
            }

            var inventoryHint = BuildInventoryHint(spec.InventoryGroups, depotCounts, zhCn);
            if (!string.IsNullOrWhiteSpace(inventoryHint))
            {
                lines.Add(inventoryHint);
            }
        }

        if (lines.Count == 0)
        {
            lines.Add(zhCn
                ? "今日关卡信息将在资源加载后展示。"
                : "Daily stage hints will be shown after resources are loaded.");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string? BuildInventoryHint(
        IReadOnlyList<string[]> inventoryGroups,
        IReadOnlyDictionary<string, int> depotCounts,
        bool zhCn)
    {
        var groupTexts = new List<string>(inventoryGroups.Count);
        foreach (var group in inventoryGroups)
        {
            var itemCounts = new List<string>(group.Length);
            foreach (var itemId in group)
            {
                if (!depotCounts.TryGetValue(itemId, out var count) || count < 0)
                {
                    return null;
                }

                itemCounts.Add(count.ToString());
            }

            groupTexts.Add(string.Join(" & ", itemCounts));
        }

        var prefix = zhCn ? "(库存 " : "(Inventory ";
        return $"{prefix}{string.Join(" / ", groupTexts)})";
    }

    private static IReadOnlyDictionary<string, int> ReadDepotCounts(UnifiedConfig? config)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (config is null
            || !config.GlobalValues.TryGetValue(LegacyConfigurationKeys.DepotResult, out JsonNode? node)
            || node is null)
        {
            return result;
        }

        var raw = ExtractRawDepotPayload(node);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return result;
        }

        TryReadDepotCountsFromPayload(raw, result);
        return result;
    }

    private static string ExtractRawDepotPayload(JsonNode node)
    {
        if (node is JsonValue value && value.TryGetValue(out string? text))
        {
            return text ?? string.Empty;
        }

        return node.ToJsonString();
    }

    private static void TryReadDepotCountsFromPayload(string payload, IDictionary<string, int> counts)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(payload);
        }
        catch
        {
            return;
        }

        if (parsed is not JsonObject root)
        {
            return;
        }

        ReadFlatDepotCountMap(root, counts);

        if (root["data"] is JsonObject dataObject)
        {
            ReadFlatDepotCountMap(dataObject, counts);
        }
        else if (root["data"] is JsonValue dataValue && dataValue.TryGetValue(out string? dataText))
        {
            TryReadDepotCountsFromPayload(dataText ?? string.Empty, counts);
        }

        if (root["items"] is JsonArray itemsArray)
        {
            ReadDepotItemsArray(itemsArray, counts);
        }

        if (root["arkplanner"]?["object"]?["items"] is JsonArray arkPlannerItems)
        {
            ReadDepotItemsArray(arkPlannerItems, counts);
        }
    }

    private static void ReadFlatDepotCountMap(JsonObject source, IDictionary<string, int> counts)
    {
        foreach (var pair in source)
        {
            if (!int.TryParse(pair.Key, out _))
            {
                continue;
            }

            if (TryReadDepotCount(pair.Value, out var count))
            {
                counts[pair.Key] = count;
            }
        }
    }

    private static void ReadDepotItemsArray(JsonArray items, IDictionary<string, int> counts)
    {
        foreach (var node in items)
        {
            if (node is not JsonObject item
                || !TryReadString(item["id"], out var id)
                || string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            if (TryReadDepotCount(item["have"], out var have))
            {
                counts[id] = have;
                continue;
            }

            if (TryReadDepotCount(item["count"], out var count))
            {
                counts[id] = count;
            }
        }
    }

    private static bool TryReadDepotCount(JsonNode? node, out int count)
    {
        count = 0;
        if (node is JsonValue value)
        {
            if (value.TryGetValue(out int intValue))
            {
                count = intValue;
                return true;
            }

            if (value.TryGetValue(out string? text) && int.TryParse(text, out intValue))
            {
                count = intValue;
                return true;
            }
        }

        return false;
    }

    private void RebuildStageOptions(string? clientTypeOverride = null, bool forceReload = false)
    {
        var normalizedClientType = NormalizeClientType(clientTypeOverride ?? ResolveClientTypeFromConfig());
        var dayOfWeek = MallDailyResetHelper.GetYjDate(DateTime.UtcNow, normalizedClientType).DayOfWeek;
        var stageCodes = ResolveStageSelectionCodes(normalizedClientType, forceReload);
        var knownStageCodes = stageCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var defaultStageDisplay = Texts.GetOrDefault("Fight.DefaultStage", "Cur/Last");
        var annihilationDisplay = ResolveAnnihilationStageDisplay();
        var previousOptions = _stageOptions;
        var list = new List<StageOption>
        {
            ReuseStageOption(
                previousOptions,
                defaultStageDisplay,
                FightStageSelection.CurrentOrLast,
                isOpen: true,
                isOutdated: false),
        };

        foreach (var stageCode in stageCodes)
        {
            var normalizedStage = stageCode.Trim();
            if (string.IsNullOrWhiteSpace(normalizedStage))
            {
                continue;
            }

            var isOpen = IsStageOpen(normalizedStage, dayOfWeek);
            if (HideUnavailableStage && !isOpen)
            {
                continue;
            }

            var display = string.Equals(normalizedStage, "Annihilation", StringComparison.OrdinalIgnoreCase)
                ? annihilationDisplay
                : ResolveStageDisplayName(normalizedStage);
            list.Add(ReuseStageOption(previousOptions, display, normalizedStage, isOpen, isOutdated: false));
        }

        AppendMissingStagePlanOptions(list, knownStageCodes, previousOptions, dayOfWeek);

        ApplyStageOptions(list);

        OnPropertyChanged(nameof(StageOptions));
        RefreshStagePlanPresentation();
        RequestSelectedStageValueRefresh();
        RequestStagePlanSelectedStageValueRefresh();
    }

    private void ApplyStageOptions(IReadOnlyList<StageOption> options)
    {
        for (var targetIndex = 0; targetIndex < options.Count; targetIndex++)
        {
            var option = options[targetIndex];
            if (targetIndex < _stageOptions.Count && EqualityComparer<StageOption>.Default.Equals(_stageOptions[targetIndex], option))
            {
                continue;
            }

            var existingIndex = IndexOfStageOption(option, targetIndex + 1);
            if (existingIndex >= 0)
            {
                _stageOptions.Move(existingIndex, targetIndex);
                continue;
            }

            if (targetIndex < _stageOptions.Count)
            {
                _stageOptions[targetIndex] = option;
            }
            else
            {
                _stageOptions.Add(option);
            }
        }

        while (_stageOptions.Count > options.Count)
        {
            _stageOptions.RemoveAt(_stageOptions.Count - 1);
        }
    }

    private int IndexOfStageOption(StageOption option, int startIndex)
    {
        for (var index = Math.Max(0, startIndex); index < _stageOptions.Count; index++)
        {
            if (EqualityComparer<StageOption>.Default.Equals(_stageOptions[index], option))
            {
                return index;
            }
        }

        return -1;
    }

    private IReadOnlyList<string> ResolveStageSelectionCodes(string clientType, bool forceReload = false)
    {
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        _ = forceReload;
        if (TryLoadStageActivityStageCodes(clientType, out var activityCodes))
        {
            AddStageCodes(activityCodes, ordered, seen);
        }

        if (TryLoadActiveTaskStageCodes(clientType, out var taskStageCodes))
        {
            AddStageCodes(taskStageCodes, ordered, seen);
        }

        AddStageCodes(FallbackStageCodes, ordered, seen);

        return ordered;
    }

    private string ResolveAnnihilationStageDisplay()
    {
        var defaultDisplay = Texts.GetOrDefault("Fight.Annihilation.Current", "Current Annihilation");
        return UseCustomAnnihilation
            ? FindAnnihilationStageOption(AnnihilationStage)?.DisplayName ?? defaultDisplay
            : defaultDisplay;
    }

    private StringOption? FindAnnihilationStageOption(string? value)
    {
        var normalized = NormalizeAnnihilationStageOptionValue(value);
        return AnnihilationStageOptions.FirstOrDefault(
            option => string.Equals(
                NormalizeAnnihilationStageOptionValue(option.Value),
                normalized,
                StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeAnnihilationStageOptionValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || string.Equals(value.Trim(), "Annihilation", StringComparison.OrdinalIgnoreCase))
        {
            return "Annihilation";
        }

        var trimmed = value.Trim();
        return trimmed.EndsWith("@Annihilation", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"{trimmed}@Annihilation";
    }

    private string ResolveStageDisplayName(string stageCode)
    {
        return AchievementTextCatalog.GetString(stageCode, Texts.Language, stageCode);
    }

    private void AppendMissingStagePlanOptions(
        ICollection<StageOption> target,
        ISet<string> knownStageCodes,
        IReadOnlyList<StageOption> previousOptions,
        DayOfWeek dayOfWeek)
    {
        var seen = target
            .Select(option => option.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in StagePlan)
        {
            var stage = FightStageSelection.NormalizeStoredValue(entry.Stage);
            if (FightStageSelection.IsCurrentOrLast(stage) || !seen.Add(stage))
            {
                continue;
            }

            var isKnown = knownStageCodes.Contains(stage);
            target.Add(ReuseStageOption(
                previousOptions,
                isKnown ? ResolveStageDisplayName(stage) : stage,
                stage,
                isOpen: IsStageManually || (isKnown && IsStageOpen(stage, dayOfWeek)),
                isOutdated: !isKnown && !IsStageManually));
        }
    }

    private static StageOption ReuseStageOption(
        IReadOnlyList<StageOption> previousOptions,
        string displayName,
        string value,
        bool isOpen,
        bool isOutdated)
    {
        var existing = previousOptions.FirstOrDefault(option =>
            string.Equals(option.Value, value, StringComparison.OrdinalIgnoreCase)
            && string.Equals(option.DisplayName, displayName, StringComparison.Ordinal)
            && option.IsOpen == isOpen
            && option.IsOutdated == isOutdated);

        return existing ?? new StageOption(displayName, value, isOpen, isOutdated);
    }

    private static void AddStageCodes(IEnumerable<string> source, ICollection<string> target, ISet<string> seen)
    {
        foreach (var code in source)
        {
            var normalized = code.Trim();
            if (!string.IsNullOrWhiteSpace(normalized) && seen.Add(normalized))
            {
                target.Add(normalized);
            }
        }
    }

    private bool TryLoadStageActivityStageCodes(string clientType, out IReadOnlyList<string> stageCodes)
    {
        stageCodes = Array.Empty<string>();
        var path = ResolveStageActivityPath();
        if (path is null)
        {
            return false;
        }

        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root
                || !TryResolveStageActivityClientNode(root, clientType, out var clientNode))
            {
                return false;
            }

            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AppendSideStoryStageCodes(clientNode["sideStoryStage"], result, seen);
            stageCodes = result;
            return result.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private bool TryLoadActiveTaskStageCodes(string clientType, out IReadOnlyList<string> stageCodes)
    {
        stageCodes = Array.Empty<string>();
        if (!TryLoadActiveStagePrefixes(clientType, out var activePrefixes) || activePrefixes.Count == 0)
        {
            return false;
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prefix in activePrefixes)
        {
            foreach (var path in ResolveStageTaskFileCandidatePaths(clientType, prefix))
            {
                if (TryAppendStageTaskCodes(path, prefix, result, seen))
                {
                    break;
                }
            }
        }

        stageCodes = result;
        return result.Count > 0;
    }

    private bool TryLoadActiveStagePrefixes(string clientType, out IReadOnlyList<string> prefixes)
    {
        prefixes = Array.Empty<string>();
        foreach (var path in ResolveStageJsonCandidatePaths(clientType))
        {
            if (!File.Exists(path))
            {
                continue;
            }

            if (!TryReadActiveStagePrefixes(path, out prefixes))
            {
                continue;
            }

            return prefixes.Count > 0;
        }

        return false;
    }

    private IEnumerable<string> ResolveStageJsonCandidatePaths(string clientType)
    {
        var clientDirectory = NormalizeClientDirectory(clientType);
        foreach (var root in EnumerateRuntimeRoots())
        {
            if (!string.Equals(clientDirectory, "Official", StringComparison.OrdinalIgnoreCase))
            {
                yield return Path.Combine(root, "resource", "global", clientDirectory, "resource", "stages.json");
            }

            foreach (var relativePath in StageJsonCandidateRelativePaths)
            {
                yield return Path.Combine(root, relativePath);
            }
        }
    }

    private IEnumerable<string> ResolveStageTaskFileCandidatePaths(string clientType, string prefix)
    {
        var clientDirectory = NormalizeClientDirectory(clientType);
        foreach (var root in EnumerateRuntimeRoots())
        {
            if (!string.Equals(clientDirectory, "Official", StringComparison.OrdinalIgnoreCase))
            {
                yield return Path.Combine(root, "resource", "global", clientDirectory, "resource", "tasks", "Stages", $"{prefix}.json");
            }

            yield return Path.Combine(root, "resource", "tasks", "Stages", $"{prefix}.json");
        }
    }

    private static bool TryReadActiveStagePrefixes(string path, out IReadOnlyList<string> prefixes)
    {
        prefixes = Array.Empty<string>();
        try
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var root = JsonNode.Parse(File.ReadAllText(path));
            if (root is JsonArray array)
            {
                foreach (var node in array)
                {
                    AppendActiveStagePrefix(node, result, seen);
                }
            }
            else if (root is JsonObject objectRoot)
            {
                foreach (var pair in objectRoot)
                {
                    AppendActiveStagePrefix(pair.Value, result, seen);
                }
            }

            prefixes = result;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void AppendActiveStagePrefix(JsonNode? node, ICollection<string> target, ISet<string> seen)
    {
        if (node is not JsonObject stage
            || !TryReadString(stage["code"], out var code)
            || !TryReadString(stage["stageId"], out var stageId)
            || !stageId.EndsWith("_rep", StringComparison.OrdinalIgnoreCase)
            || !TryReadStagePrefix(code, out var prefix)
            || !seen.Add(prefix))
        {
            return;
        }

        target.Add(prefix);
    }

    private static bool TryAppendStageTaskCodes(
        string path,
        string prefix,
        ICollection<string> target,
        ISet<string> seen)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root)
            {
                return false;
            }

            var originalCount = target.Count;
            foreach (var pair in root)
            {
                if (IsTaskStageCodeForPrefix(pair.Key, prefix))
                {
                    AddStageCodes([pair.Key], target, seen);
                }
            }

            return target.Count > originalCount;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadStagePrefix(string code, out string prefix)
    {
        prefix = string.Empty;
        var separatorIndex = code.IndexOf('-');
        if (separatorIndex <= 0)
        {
            return false;
        }

        prefix = code[..separatorIndex];
        return prefix.All(static c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
    }

    private static bool IsTaskStageCodeForPrefix(string key, string prefix)
    {
        if (string.IsNullOrWhiteSpace(key) || key.IndexOf('@', StringComparison.Ordinal) >= 0)
        {
            return false;
        }

        var prefixWithSeparator = $"{prefix}-";
        if (!key.StartsWith(prefixWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var nextIndex = prefixWithSeparator.Length;
        return nextIndex < key.Length && char.IsDigit(key[nextIndex]);
    }

    private static string NormalizeClientDirectory(string clientType)
    {
        return string.IsNullOrWhiteSpace(clientType)
            ? "Official"
            : clientType.Trim();
    }

    private string? ResolveStageActivityPath()
    {
        foreach (var root in EnumerateRuntimeRoots())
        {
            foreach (var relativePath in StageActivityCandidateRelativePaths)
            {
                var candidate = Path.Combine(root, relativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private IEnumerable<string> EnumerateRuntimeRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new[]
        {
            ResolveConfigurationBaseDirectory(),
            RuntimeLayout.ResolveRuntimeBaseDirectory(),
            Environment.CurrentDirectory,
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")),
        }
        .Where(static candidate => !string.IsNullOrWhiteSpace(candidate))
        .Select(static candidate => candidate!);

        foreach (var candidate in candidates)
        {
            var current = new DirectoryInfo(Path.GetFullPath(candidate));
            while (current is not null)
            {
                if (seen.Add(current.FullName))
                {
                    yield return current.FullName;
                }

                current = current.Parent;
            }
        }
    }

    private string? ResolveConfigurationBaseDirectory()
    {
        try
        {
            var field = typeof(UnifiedConfigurationService).GetField(
                "_baseDirectory",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            return field?.GetValue(Runtime.ConfigurationService) as string;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryResolveStageActivityClientNode(JsonObject root, string clientType, out JsonObject clientNode)
    {
        if (TryGetStageActivityClientNode(root, clientType, out clientNode))
        {
            return clientNode["sideStoryStage"] is not null;
        }

        if (!string.Equals(clientType, "Official", StringComparison.OrdinalIgnoreCase)
            && TryGetStageActivityClientNode(root, "Official", out clientNode))
        {
            return clientNode["sideStoryStage"] is not null;
        }

        clientNode = null!;
        return false;
    }

    private static bool TryGetStageActivityClientNode(JsonObject root, string clientType, out JsonObject clientNode)
    {
        clientNode = null!;
        foreach (var pair in root)
        {
            if (!string.Equals(NormalizeClientType(pair.Key), clientType, StringComparison.OrdinalIgnoreCase)
                || pair.Value is not JsonObject node)
            {
                continue;
            }

            clientNode = node;
            return true;
        }

        return false;
    }

    private static void AppendSideStoryStageCodes(JsonNode? sideStoryNode, ICollection<string> target, ISet<string> seen)
    {
        if (sideStoryNode is not JsonObject sideStoryObject)
        {
            return;
        }

        foreach (var group in sideStoryObject)
        {
            if (group.Value is not JsonObject groupObject)
            {
                continue;
            }

            var groupActivity = groupObject["Activity"] ?? groupObject["activity"];
            var stages = groupObject["Stages"] ?? groupObject["stages"];
            if (stages is not JsonArray stageArray)
            {
                continue;
            }

            foreach (var stageNode in stageArray)
            {
                if (stageNode is not JsonObject stageObject)
                {
                    continue;
                }

                var activity = stageObject["Activity"] ?? stageObject["activity"] ?? groupActivity;
                if (!IsActivityStageOpenOrWillOpen(activity)
                    || !TryReadString(stageObject["Value"] ?? stageObject["value"], out var value)
                    || string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                AddStageCodes([value], target, seen);
            }
        }
    }

    private static bool IsActivityStageOpenOrWillOpen(JsonNode? activityNode)
    {
        if (activityNode is not JsonObject activity)
        {
            return false;
        }

        return !TryReadActivityTime(activity, "UtcExpireTime", out var expireTime)
            || DateTime.UtcNow < expireTime;
    }

    private static bool TryReadActivityTime(JsonObject activity, string key, out DateTime utcTime)
    {
        utcTime = default;
        if (!TryReadString(activity[key], out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var timezone = 0;
        if (activity["TimeZone"] is JsonValue timezoneValue)
        {
            _ = timezoneValue.TryGetValue(out timezone);
        }

        if (DateTime.TryParseExact(
                raw,
                "yyyy/MM/dd HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            utcTime = parsed.AddHours(-timezone);
            return true;
        }

        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out parsed))
        {
            utcTime = parsed.ToUniversalTime();
            return true;
        }

        return false;
    }

    private string ResolveClientTypeFromConfig()
    {
        if (Runtime.ConfigurationService.TryGetCurrentProfile(out var profile)
            && profile.Values.TryGetValue("ClientType", out var node)
            && node is JsonValue jsonValue
            && jsonValue.TryGetValue(out string? configuredClientType)
            && !string.IsNullOrWhiteSpace(configuredClientType))
        {
            return configuredClientType;
        }

        return "Official";
    }

    private static string NormalizeClientType(string? clientType)
    {
        if (string.IsNullOrWhiteSpace(clientType)
            || string.Equals(clientType, "Official", StringComparison.OrdinalIgnoreCase)
            || string.Equals(clientType, "Bilibili", StringComparison.OrdinalIgnoreCase))
        {
            return "Official";
        }

        return clientType.Trim();
    }

    private static bool IsStageOpen(string stageCode, DayOfWeek dayOfWeek)
    {
        if (!WeeklyOpenStageCodes.TryGetValue(stageCode, out var openDays))
        {
            return true;
        }

        return openDays.Contains(dayOfWeek);
    }

    private string BuildClosedSuffix()
    {
        return IsChineseLanguage(Texts.Language)
            ? "(未开放)"
            : "(Closed)";
    }

    private string BuildOutdatedSuffix()
    {
        return IsChineseLanguage(Texts.Language)
            ? "(已过期)"
            : "(Outdated)";
    }

    private static IReadOnlyList<string> GetResourceStageCodesForDay(DayOfWeek dayOfWeek)
    {
        var list = new List<string> { "LS-6" };
        foreach (var pair in WeeklyOpenStageCodes)
        {
            if (!pair.Value.Contains(dayOfWeek))
            {
                continue;
            }

            list.Add(pair.Key);
        }

        return list.OrderBy(stageCode => stageCode, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IsChineseLanguage(string? language)
    {
        return !string.IsNullOrWhiteSpace(language)
            && language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeStageResetMode(string? value)
    {
        return string.Equals(value, "Ignore", StringComparison.OrdinalIgnoreCase)
            ? "Ignore"
            : "Current";
    }

    public void AddStagePlanEntry()
    {
        StagePlan.Add(new StagePlanEntry(this, FightStageSelection.CurrentOrLast));
    }

    public void RemoveStagePlanEntry(StagePlanEntry? entry)
    {
        if (entry is null || StagePlan.Count <= 1)
        {
            return;
        }

        StagePlan.Remove(entry);
    }

    public void MoveStagePlanEntryUp(StagePlanEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        var index = StagePlan.IndexOf(entry);
        MoveStagePlanEntry(index, index - 1);
    }

    public void MoveStagePlanEntryDown(StagePlanEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        var index = StagePlan.IndexOf(entry);
        MoveStagePlanEntry(index, index + 1);
    }

    public void MoveStagePlanEntry(int sourceIndex, int targetIndex)
    {
        if (sourceIndex == targetIndex
            || sourceIndex < 0
            || targetIndex < 0
            || sourceIndex >= StagePlan.Count
            || targetIndex >= StagePlan.Count)
        {
            return;
        }

        StagePlan.Move(sourceIndex, targetIndex);
    }

    private void OnStagePlanCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (_suppressStagePlanSync)
        {
            return;
        }

        if (StagePlan.Count == 0)
        {
            _suppressStagePlanSync = true;
            try
            {
                StagePlan.Add(new StagePlanEntry(this, FightStageSelection.CurrentOrLast));
            }
            finally
            {
                _suppressStagePlanSync = false;
            }
        }

        SyncPrimaryStageFromPlan(markDirty: true);
        OnPropertyChanged(nameof(CanRemoveStagePlanEntry));
        OnPropertyChanged(nameof(StageSelectDisplayText));
        RefreshStageOptionsForStagePlanChange(e.Action);
        MarkDirty();
    }

    private void EnsureStagePlanInitialized()
    {
        if (StagePlan.Count == 0)
        {
            StagePlan.Add(new StagePlanEntry(this, FightStageSelection.CurrentOrLast));
        }
    }

    private void ReplaceStagePlan(IEnumerable<string?> stages)
    {
        var normalized = FightStageSelection.NormalizeStagePlan(stages);

        _suppressStagePlanSync = true;
        try
        {
            StagePlan.Clear();
            foreach (var stage in normalized)
            {
                StagePlan.Add(new StagePlanEntry(this, stage));
            }
        }
        finally
        {
            _suppressStagePlanSync = false;
        }

        SyncPrimaryStageFromPlan(markDirty: false);
        OnPropertyChanged(nameof(CanRemoveStagePlanEntry));
        RebuildStageOptions();
    }

    private void SetFirstStagePlanEntry(string stage)
    {
        EnsureStagePlanInitialized();
        if (string.Equals(StagePlan[0].Stage, stage, StringComparison.Ordinal))
        {
            return;
        }

        _suppressStagePlanSync = true;
        try
        {
            StagePlan[0].Stage = stage;
        }
        finally
        {
            _suppressStagePlanSync = false;
        }

        RefreshStagePlanPresentation();
    }

    private void SyncPrimaryStageFromPlan(bool markDirty)
    {
        EnsureStagePlanInitialized();
        var primary = FightStageSelection.NormalizeStoredValue(StagePlan[0].Stage);

        _suppressStagePlanSync = true;
        try
        {
            if (markDirty)
            {
                Stage = primary;
            }
            else
            {
                SetProperty(ref _stage, primary, nameof(Stage));
                OnPropertyChanged(nameof(SelectedStageOption));
                OnPropertyChanged(nameof(SelectedStageValue));
            }
        }
        finally
        {
            _suppressStagePlanSync = false;
        }
    }

    private void CollapseStagePlanToPrimaryEntry()
    {
        if (StagePlan.Count <= 1)
        {
            return;
        }

        ReplaceStagePlan([StagePlan[0].Stage]);
        MarkDirty();
    }

    private void NormalizeStagePlanAgainstKnownStages()
    {
        var knownStageCodes = ResolveStageSelectionCodes(ResolveClientTypeFromConfig())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var updated = false;
        foreach (var entry in StagePlan)
        {
            if (FightStageSelection.IsCurrentOrLast(entry.Stage)
                || knownStageCodes.Contains(entry.Stage))
            {
                continue;
            }

            entry.Stage = FightStageSelection.CurrentOrLast;
            updated = true;
        }

        if (updated)
        {
            MarkDirty();
        }
    }

    internal string NormalizeStagePlanEntryValue(string? value)
    {
        if (IsStageManually)
        {
            return NormalizeManualStageInput(value);
        }

        return FightStageSelection.NormalizeStoredValue(value);
    }

    private static bool IsTransientEmptySelectionValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value);
    }

    private string NormalizeManualStageInput(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return FightStageSelection.CurrentOrLast;
        }

        var trimmed = value.Trim();
        if (FightStageSelection.IsCurrentOrLast(trimmed))
        {
            return FightStageSelection.CurrentOrLast;
        }

        var upper = trimmed.ToUpperInvariant();
        if (ManualStageAliasMap.TryGetValue(upper, out var alias))
        {
            return alias;
        }

        var matchedStage = Runtime.StageManagerFeatureService
            .GetStageCodes(ResolveClientTypeFromConfig())
            .FirstOrDefault(stageCode => string.Equals(stageCode, trimmed, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(matchedStage))
        {
            return matchedStage;
        }

        return upper;
    }

    internal void OnStagePlanEntryChanged(StagePlanEntry entry)
    {
        if (_suppressStagePlanSync)
        {
            return;
        }

        if (StagePlan.IndexOf(entry) == 0)
        {
            SyncPrimaryStageFromPlan(markDirty: true);
        }

        RefreshStageOptionsForStagePlanChange(NotifyCollectionChangedAction.Add);
        MarkDirty();
    }

    private void RefreshStageOptionsForStagePlanChange(NotifyCollectionChangedAction action)
    {
        if (action is NotifyCollectionChangedAction.Remove
            or NotifyCollectionChangedAction.Replace
            or NotifyCollectionChangedAction.Reset
            || StagePlanContainsMissingStageOption())
        {
            RebuildStageOptions();
            return;
        }

        RefreshStagePlanPresentation();
    }

    private bool StagePlanContainsMissingStageOption()
    {
        var selectedValues = _stageOptions
            .Select(option => option.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return StagePlan
            .Select(entry => FightStageSelection.NormalizeStoredValue(entry.Stage))
            .Any(stage => !FightStageSelection.IsCurrentOrLast(stage) && !selectedValues.Contains(stage));
    }

    private void RequestStagePlanSelectedStageValueRefresh()
    {
        foreach (var entry in StagePlan)
        {
            entry.RequestSelectedStageValueRefresh();
        }
    }

    private void RefreshStagePlanPresentation()
    {
        var normalizedClientType = NormalizeClientType(ResolveClientTypeFromConfig());
        var dayOfWeek = MallDailyResetHelper.GetYjDate(DateTime.UtcNow, normalizedClientType).DayOfWeek;
        var knownStageCodes = ResolveStageSelectionCodes(normalizedClientType)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in StagePlan)
        {
            entry.RefreshSelectedStageOption();
            var stage = FightStageSelection.NormalizeStoredValue(entry.Stage);
            if (FightStageSelection.IsCurrentOrLast(stage))
            {
                entry.UpdateAvailability(isOpen: true, isOutdated: false, statusText: string.Empty);
                continue;
            }

            var option = _stageOptions.FirstOrDefault(candidate => string.Equals(candidate.Value, stage, StringComparison.OrdinalIgnoreCase));
            if (option is not null)
            {
                entry.UpdateAvailability(option.IsOpen, option.IsOutdated, BuildStageStatusText(option.IsOpen, option.IsOutdated));
                continue;
            }

            if (knownStageCodes.Contains(stage))
            {
                var knownStageIsOpen = IsStageManually || IsStageOpen(stage, dayOfWeek);
                entry.UpdateAvailability(knownStageIsOpen, isOutdated: false, BuildStageStatusText(knownStageIsOpen, isOutdated: false));
                continue;
            }

            var fallbackIsOpen = IsStageManually || IsStageOpen(stage, dayOfWeek);
            entry.UpdateAvailability(fallbackIsOpen, isOutdated: !IsStageManually, BuildStageStatusText(fallbackIsOpen, isOutdated: !IsStageManually));
        }
    }

    private string BuildStageStatusText(bool isOpen, bool isOutdated)
    {
        if (isOutdated)
        {
            return BuildOutdatedSuffix();
        }

        return string.Empty;
    }

    private bool ResolveAutoRestartOnDrop()
    {
        if (Runtime.ConfigurationService.TryGetCurrentProfile(out var profile)
            && profile.Values.TryGetValue(LegacyConfigurationKeys.AutoRestartOnDrop, out var profileNode)
            && profileNode is JsonValue profileValue)
        {
            if (profileValue.TryGetValue(out bool profileFlag))
            {
                return profileFlag;
            }

            if (profileValue.TryGetValue(out string? profileText) && bool.TryParse(profileText, out profileFlag))
            {
                return profileFlag;
            }
        }

        if (Runtime.ConfigurationService.CurrentConfig.GlobalValues.TryGetValue(LegacyConfigurationKeys.AutoRestartOnDrop, out var globalNode)
            && globalNode is JsonValue globalValue)
        {
            if (globalValue.TryGetValue(out bool globalFlag))
            {
                return globalFlag;
            }

            if (globalValue.TryGetValue(out string? globalText) && bool.TryParse(globalText, out globalFlag))
            {
                return globalFlag;
            }
        }

        return true;
    }

    private void ApplyPersistentAutoRestartOnDrop(bool value)
    {
        SetProperty(ref _autoRestartOnDrop, value, nameof(AutoRestartOnDrop));
    }

    private async Task PersistAutoRestartOnDropAsync(bool value)
    {
        _ = await ConfigurationSaveTracker.Instance.RunTrackedAsync(
            "TaskQueue.Fight.AutoRestartOnDrop",
            Texts.GetOrDefault("Fight.Title", "理智作战"),
            "Fight.AutoRestartOnDrop.Save",
            Runtime.DiagnosticsService,
            async cancellationToken =>
            {
                if (Runtime.ConfigurationService.TryGetCurrentProfile(out var profile))
                {
                    profile.Values[LegacyConfigurationKeys.AutoRestartOnDrop] = JsonValue.Create(value);
                }
                else
                {
                    Runtime.ConfigurationService.CurrentConfig.GlobalValues[LegacyConfigurationKeys.AutoRestartOnDrop] = JsonValue.Create(value);
                }

                await Runtime.ConfigurationService.SaveAsync(cancellationToken);
                return true;
            });
        if (ConfigurationSaveTracker.Instance.IsPendingOrFailed("TaskQueue.Fight.AutoRestartOnDrop"))
        {
            LastErrorMessage = string.Empty;
        }
    }

    public sealed record IntOption(int Value, string DisplayName);

    public sealed record StringOption(string Value, string DisplayName);

    public sealed record DropOption(string DisplayName, string Value);

    public sealed class StagePlanEntry : ObservableObject
    {
        private readonly FightTaskModuleViewModel _owner;
        private string _stage;
        private bool _isOpen = true;
        private bool _isOutdated;
        private string _statusText = string.Empty;

        public StagePlanEntry(FightTaskModuleViewModel owner, string stage)
        {
            _owner = owner;
            _stage = FightStageSelection.NormalizeStoredValue(stage);
        }

        public string Stage
        {
            get => _stage;
            set
            {
                var normalized = _owner.NormalizeStagePlanEntryValue(value);
                if (!SetProperty(ref _stage, normalized))
                {
                    return;
                }

                OnPropertyChanged(nameof(EditableStageText));
                OnPropertyChanged(nameof(SelectedStageOption));
                OnPropertyChanged(nameof(SelectedStageValue));
                OnPropertyChanged(nameof(PreviewStageText));
                _owner.OnStagePlanEntryChanged(this);
            }
        }

        public StageOption? SelectedStageOption
        {
            get => _owner.StageOptions.FirstOrDefault(
                option => string.Equals(option.Value, Stage, StringComparison.OrdinalIgnoreCase));
            set
            {
                if (value is null)
                {
                    OnPropertyChanged(nameof(SelectedStageOption));
                    OnPropertyChanged(nameof(SelectedStageValue));
                    OnPropertyChanged(nameof(PreviewStageText));
                    return;
                }

                Stage = value.Value;
            }
        }

        public string SelectedStageValue
        {
            get => SelectedStageOption?.Value ?? Stage;
            set
            {
                if (IsTransientEmptySelectionValue(value))
                {
                    RequestSelectedStageValueRefresh();
                    return;
                }

                Stage = value;
            }
        }

        public string EditableStageText
        {
            get => FightStageSelection.IsCurrentOrLast(Stage) ? string.Empty : Stage;
            set => Stage = value;
        }

        public string PreviewStageText =>
            SelectedStageOption?.DisplayName
            ?? (FightStageSelection.IsCurrentOrLast(Stage)
                ? _owner.Texts.GetOrDefault("Fight.DefaultStage", "Cur/Last")
                : Stage);

        public bool IsOpen
        {
            get => _isOpen;
            private set
            {
                if (SetProperty(ref _isOpen, value))
                {
                    OnPropertyChanged(nameof(IsClosed));
                }
            }
        }

        public bool IsClosed => !IsOpen;

        public bool IsOutdated
        {
            get => _isOutdated;
            private set => SetProperty(ref _isOutdated, value);
        }

        public string StatusText
        {
            get => _statusText;
            private set => SetProperty(ref _statusText, value);
        }

        internal void UpdateAvailability(bool isOpen, bool isOutdated, string statusText)
        {
            IsOpen = isOpen;
            IsOutdated = isOutdated;
            StatusText = statusText;
        }

        internal void RefreshSelectedStageOption()
        {
            OnPropertyChanged(nameof(SelectedStageOption));
            OnPropertyChanged(nameof(SelectedStageValue));
            OnPropertyChanged(nameof(PreviewStageText));
        }

        internal void RequestSelectedStageValueRefresh()
        {
            Dispatcher.UIThread.Post(RefreshSelectedStageOption, DispatcherPriority.Background);
        }

        public override string ToString() => FightStageSelection.IsCurrentOrLast(Stage) ? string.Empty : Stage;
    }

    public sealed record StageOption(string DisplayName, string Value, bool IsOpen, bool IsOutdated)
    {
        public bool IsClosed => !IsOpen;
    }
}
