using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json.Nodes;
using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.Application.Models;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using MAAUnified.Application.Services.TaskParams;

namespace MAAUnified.App.ViewModels.TaskQueue;

/// <summary>
/// 库存保持任务编辑器（WPF dev-v2 38b2e9e4f2、948f23f8ae、0e99c81442、be9cc84fd2、
/// a7ee2c2bf7、e7ef27745e、0302381585）。计划列表按顺序下发，每条计划对应一条 Fight
/// 链（目标关卡、材料、目标库存、药剂/源石预算）；上方开关控制整任务跳过、按达成率排序、
/// 仅执行第一个不足计划、AUTO 倍率、双芯片折算与临期药。
/// </summary>
public sealed class DepotMaintainModuleViewModel : TaskModuleSettingsViewModelBase
{
    private readonly object _optionsGate = new();
    private bool _suppressPlanChanges;
    private bool _updateDepot = true;
    private bool _isStageManually;
    private bool _skipDuringActivity;
    private bool _skipDuringResourceCollection;
    private bool _onlyFirstInsufficientPlan;
    private bool _sortByProgress;
    private bool _countDualChipAsChipPack;
    private bool _useAutoSeries;
    private bool _useMedicine = true;
    private bool _useStone = true;
    private bool _useExpiringMedicine;
    private IReadOnlyList<StringChoice> _stageOptions = [];
    private IReadOnlyList<FightTaskModuleViewModel.DropOption> _dropOptions = [];
    private IReadOnlyDictionary<string, int> _inventoryCounts = new Dictionary<string, int>(StringComparer.Ordinal);
    private bool _isBound;

    public DepotMaintainModuleViewModel(MAAUnifiedRuntime runtime, LocalizedTextMap texts)
        : base(runtime, texts, TaskModuleTypes.DepotMaintain)
    {
        Plans.CollectionChanged += OnPlansCollectionChanged;
        Texts.PropertyChanged += OnTextsChanged;
        RebuildOptions();
    }

    public ObservableCollection<DepotMaintainPlanRow> Plans { get; } = [];

    public IReadOnlyList<StringChoice> StageOptions => _stageOptions;

    public IReadOnlyList<FightTaskModuleViewModel.DropOption> DropOptions => _dropOptions;

    public bool UpdateDepot
    {
        get => _updateDepot;
        set => SetFlag(ref _updateDepot, value);
    }

    public bool IsStageManually
    {
        get => _isStageManually;
        set => SetFlag(ref _isStageManually, value);
    }

    public bool SkipDuringActivity
    {
        get => _skipDuringActivity;
        set => SetFlag(ref _skipDuringActivity, value);
    }

    public bool SkipDuringResourceCollection
    {
        get => _skipDuringResourceCollection;
        set => SetFlag(ref _skipDuringResourceCollection, value);
    }

    public bool OnlyFirstInsufficientPlan
    {
        get => _onlyFirstInsufficientPlan;
        set => SetFlag(ref _onlyFirstInsufficientPlan, value);
    }

    public bool SortByProgress
    {
        get => _sortByProgress;
        set => SetFlag(ref _sortByProgress, value);
    }

    public bool CountDualChipAsChipPack
    {
        get => _countDualChipAsChipPack;
        set
        {
            if (SetFlag(ref _countDualChipAsChipPack, value))
            {
                RefreshPlanInventory();
            }
        }
    }

    public bool UseAutoSeries
    {
        get => _useAutoSeries;
        set => SetFlag(ref _useAutoSeries, value);
    }

    public bool UseMedicine
    {
        get => _useMedicine;
        set => SetFlag(ref _useMedicine, value);
    }

    public bool UseStone
    {
        get => _useStone;
        set => SetFlag(ref _useStone, value);
    }

    public bool UseExpiringMedicine
    {
        get => _useExpiringMedicine;
        set => SetFlag(ref _useExpiringMedicine, value);
    }

    public bool ShowEmptyHint => Plans.Count == 0;

    public string TitleText => Texts.GetOrDefault("DepotMaintain.Title", "库存保持");

    public string PlansText => Texts.GetOrDefault("DepotMaintain.Plans", "计划列表");

    public string AddPlanText => Texts.GetOrDefault("DepotMaintain.AddPlan", "添加计划");

    public string EmptyHintText => Texts.GetOrDefault(
        "DepotMaintain.EmptyHint",
        "尚未添加计划；运行时只执行库存识别，不会刷取材料。");

    public string UpdateDepotText => Texts.GetOrDefault("DepotMaintain.UpdateDepot", "任务开始前更新库存数据");

    public string SkipDuringActivityText => Texts.GetOrDefault("DepotMaintain.SkipDuringActivity", "活动期间跳过本任务");

    public string SkipDuringResourceCollectionText => Texts.GetOrDefault(
        "DepotMaintain.SkipDuringResourceCollection",
        "资源全开放期间跳过本任务");

    public string OnlyFirstInsufficientPlanText => Texts.GetOrDefault(
        "DepotMaintain.OnlyFirstInsufficientPlan",
        "仅执行第一个库存不足的计划");

    public string SortByProgressText => Texts.GetOrDefault(
        "DepotMaintain.SortByProgress",
        "按库存达成率排序执行");

    public string CountDualChipAsChipPackText => Texts.GetOrDefault(
        "DepotMaintain.CountDualChipAsChipPack",
        "双芯片折算计入芯片组");

    public string UseAutoSeriesText => Texts.GetOrDefault("DepotMaintain.UseAutoSeries", "AUTO 代理倍率");

    public string UseMedicineText => Texts.GetOrDefault("DepotMaintain.EnableUseMedicine", "启用 ｢使用药剂｣");

    public string UseStoneText => Texts.GetOrDefault("DepotMaintain.EnableUseStone", "启用 ｢使用源石｣");

    public string UseExpiringMedicineText => Texts.GetOrDefault(
        "DepotMaintain.UseExpiringMedicine",
        "使用 48 小时内过期的理智药");

    public string StageText => Texts.GetOrDefault("DepotMaintain.Stage", "关卡");

    public string DropText => Texts.GetOrDefault("DepotMaintain.Drop", "材料");

    public string DropCountText => Texts.GetOrDefault("DepotMaintain.DropCount", "目标数量");

    public string MedicineCountText => Texts.GetOrDefault("DepotMaintain.MedicineCount", "药剂数量");

    public string StoneCountText => Texts.GetOrDefault("DepotMaintain.StoneCount", "源石数量");

    public string PlanUseMedicineText => Texts.GetOrDefault("DepotMaintain.PlanUseMedicine", "使用药剂");

    public string PlanUseStoneText => Texts.GetOrDefault("DepotMaintain.PlanUseStone", "使用源石");

    public string RemoveText => Texts.GetOrDefault("DepotMaintain.Remove", "删除");

    public string MoveUpText => Texts.GetOrDefault("DepotMaintain.MoveUp", "上移");

    public string MoveDownText => Texts.GetOrDefault("DepotMaintain.MoveDown", "下移");

    public string NotSelectedText => Texts.GetOrDefault("DepotMaintain.NotSelected", "不选择");

    public string InventoryText => Texts.GetOrDefault("DepotMaintain.Inventory", "现有库存");

    public void AddPlan()
    {
        Plans.Add(new DepotMaintainPlanRow(this)
        {
            Index = Plans.Count + 1,
        });
    }

    public void RemovePlan(DepotMaintainPlanRow? row)
    {
        if (row is null)
        {
            return;
        }

        Plans.Remove(row);
    }

    public void MovePlan(DepotMaintainPlanRow? row, int offset)
    {
        if (row is null || offset == 0)
        {
            return;
        }

        var index = Plans.IndexOf(row);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= Plans.Count)
        {
            return;
        }

        _suppressPlanChanges = true;
        try
        {
            Plans.Move(index, target);
        }
        finally
        {
            _suppressPlanChanges = false;
        }

        ReindexPlans();
        QueuePersist();
    }

    /// <summary>
    /// 计划行变更后同步持久化；由 <see cref="DepotMaintainPlanRow"/> 调用。
    /// </summary>
    internal void NotifyPlanEdited()
    {
        if (_suppressPlanChanges)
        {
            return;
        }

        RefreshPlanInventory();
        QueuePersist();
    }

    internal string ResolveDropDisplayName(string? dropId)
    {
        if (string.IsNullOrWhiteSpace(dropId))
        {
            return NotSelectedText;
        }

        return _dropOptions.FirstOrDefault(option => string.Equals(option.Value, dropId, StringComparison.Ordinal))?.DisplayName
            ?? dropId;
    }

    /// <summary>
    /// 计划行的现有库存显示：开启双芯片折算且存在折算量时为 ｢原始 (+折算)｣。
    /// 无库存数据时显示 ｢--｣（WPF GetInventoryCountDisplay）。
    /// </summary>
    internal string ResolveInventoryDisplay(string? dropId)
    {
        if (string.IsNullOrWhiteSpace(dropId)
            || !_inventoryCounts.TryGetValue(dropId, out var count))
        {
            return "--";
        }

        if (!_countDualChipAsChipPack)
        {
            return count.ToString("N0");
        }

        var formulaIndex = ItemFormulaIndex.Load();
        var conversion = ItemFormulaIndex.GetDualChipConversion(dropId, _inventoryCounts, formulaIndex);
        return conversion > 0 ? $"{count:N0} (+{conversion:N0})" : count.ToString("N0");
    }

    protected override Task LoadFromParametersAsync(JsonObject parameters, CancellationToken cancellationToken)
    {
        var model = DepotMaintainParams.FromJson(parameters);
        _isBound = true;
        _suppressPlanChanges = true;
        try
        {
            _updateDepot = model.UpdateDepot;
            _isStageManually = model.IsStageManually;
            _skipDuringActivity = model.SkipDuringActivity;
            _skipDuringResourceCollection = model.SkipDuringResourceCollection;
            _onlyFirstInsufficientPlan = model.OnlyFirstInsufficientPlan;
            _sortByProgress = model.SortByProgress;
            _countDualChipAsChipPack = model.CountDualChipAsChipPack;
            _useAutoSeries = model.UseAutoSeries;
            _useMedicine = model.UseMedicine;
            _useStone = model.UseStone;
            _useExpiringMedicine = model.UseExpiringMedicine;

            Plans.Clear();
            foreach (var plan in model.Plans)
            {
                Plans.Add(DepotMaintainPlanRow.FromPlan(this, plan));
            }
        }
        finally
        {
            _suppressPlanChanges = false;
        }

        ReindexPlans();
        RaiseOptionDependents();
        OnPropertyChanged(string.Empty);
        RefreshPlanInventory();
        return Task.CompletedTask;
    }

    protected override JsonObject BuildParameters()
    {
        var model = new DepotMaintainParams
        {
            UpdateDepot = _updateDepot,
            IsStageManually = _isStageManually,
            SkipDuringActivity = _skipDuringActivity,
            SkipDuringResourceCollection = _skipDuringResourceCollection,
            OnlyFirstInsufficientPlan = _onlyFirstInsufficientPlan,
            SortByProgress = _sortByProgress,
            CountDualChipAsChipPack = _countDualChipAsChipPack,
            UseAutoSeries = _useAutoSeries,
            UseMedicine = _useMedicine,
            UseStone = _useStone,
            UseExpiringMedicine = _useExpiringMedicine,
            Plans = [.. Plans.Select(row => row.ToPlan())],
        };

        return model.ToJson();
    }

    public override void ClearBinding()
    {
        base.ClearBinding();
        _isBound = false;
    }

    private bool SetFlag(ref bool field, bool value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (field == value)
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        QueuePersist();
        return true;
    }

    private void OnPlansCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        ReindexPlans();
        OnPropertyChanged(nameof(ShowEmptyHint));
        RefreshPlanInventory();
        if (!_suppressPlanChanges)
        {
            QueuePersist();
        }
    }

    private void ReindexPlans()
    {
        for (var index = 0; index < Plans.Count; index++)
        {
            Plans[index].Index = index + 1;
        }
    }

    private void OnTextsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(LocalizedTextMap.Language) or "Item[]"))
        {
            return;
        }

        RebuildOptions();
        foreach (var row in Plans)
        {
            row.RefreshLocalizedText();
        }

        RaiseOptionDependents();
        OnPropertyChanged(string.Empty);
    }

    private void RaiseOptionDependents()
    {
        OnPropertyChanged(nameof(StageOptions));
        OnPropertyChanged(nameof(DropOptions));
        OnPropertyChanged(nameof(ShowEmptyHint));
    }

    private void RebuildOptions()
    {
        lock (_optionsGate)
        {
            var clientType = ResolveClientType();
            IReadOnlyList<string> stageCodes;
            try
            {
                stageCodes = Runtime.StageManagerFeatureService.GetStageCodes(clientType);
            }
            catch (Exception)
            {
                stageCodes = [];
            }

            var stageChoices = new List<StringChoice>();
            foreach (var stageCode in stageCodes)
            {
                if (string.IsNullOrWhiteSpace(stageCode))
                {
                    continue;
                }

                stageChoices.Add(new StringChoice(stageCode.Trim(), stageCode.Trim()));
            }

            _stageOptions = stageChoices;

            try
            {
                _dropOptions = FightTaskModuleViewModel.BuildDropOptionsForLanguage(
                    Texts.Language,
                    NotSelectedText);
            }
            catch (Exception)
            {
                _dropOptions = [new FightTaskModuleViewModel.DropOption(NotSelectedText, string.Empty)];
            }

            RefreshInventoryCounts();
        }
    }

    private void RefreshInventoryCounts()
    {
        try
        {
            _inventoryCounts = TaskParamCompiler.ReadDepotCounts(Runtime.ConfigurationService.CurrentConfig);
        }
        catch (Exception)
        {
            _inventoryCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        }
    }

    private void RefreshPlanInventory()
    {
        RefreshInventoryCounts();
        foreach (var row in Plans)
        {
            row.RefreshInventory();
        }
    }

    private string ResolveClientType()
    {
        if (Runtime.ConfigurationService.TryGetCurrentProfile(out var profile)
            && profile.Values.TryGetValue("ClientType", out var node)
            && node is JsonValue value
            && value.TryGetValue(out string? clientType)
            && !string.IsNullOrWhiteSpace(clientType))
        {
            return clientType;
        }

        return "Official";
    }

    internal bool IsBoundToTask => _isBound;

    public sealed record StringChoice(string Value, string DisplayName);

    public sealed class DepotMaintainPlanRow : ObservableObject
    {
        private readonly DepotMaintainModuleViewModel _owner;
        private int _index;
        private string _stage = string.Empty;
        private string _dropId = string.Empty;
        private int _dropCount;
        private bool _useMedicine;
        private int _medicineCount;
        private bool _useStone;
        private int _stoneCount;
        private string _dropDisplayName = string.Empty;
        private string _inventoryDisplay = "--";

        internal DepotMaintainPlanRow(DepotMaintainModuleViewModel owner)
        {
            _owner = owner;
            _dropDisplayName = owner.NotSelectedText;
        }

        public int Index
        {
            get => _index;
            set => SetProperty(ref _index, value);
        }

        public string Stage
        {
            get => _stage;
            set
            {
                var normalized = (value ?? string.Empty).Trim();
                if (!SetProperty(ref _stage, normalized))
                {
                    return;
                }

                _owner.NotifyPlanEdited();
            }
        }

        public string DropId
        {
            get => _dropId;
            set
            {
                var normalized = (value ?? string.Empty).Trim();
                if (!SetProperty(ref _dropId, normalized))
                {
                    return;
                }

                DropDisplayName = _owner.ResolveDropDisplayName(normalized);
                _owner.NotifyPlanEdited();
            }
        }

        public int DropCount
        {
            get => _dropCount;
            set
            {
                if (!SetProperty(ref _dropCount, Math.Max(0, value)))
                {
                    return;
                }

                _owner.NotifyPlanEdited();
            }
        }

        public bool UseMedicine
        {
            get => _useMedicine;
            set
            {
                if (!SetProperty(ref _useMedicine, value))
                {
                    return;
                }

                _owner.NotifyPlanEdited();
            }
        }

        public int MedicineCount
        {
            get => _medicineCount;
            set
            {
                if (!SetProperty(ref _medicineCount, Math.Max(0, value)))
                {
                    return;
                }

                _owner.NotifyPlanEdited();
            }
        }

        public bool UseStone
        {
            get => _useStone;
            set
            {
                if (!SetProperty(ref _useStone, value))
                {
                    return;
                }

                _owner.NotifyPlanEdited();
            }
        }

        public int StoneCount
        {
            get => _stoneCount;
            set
            {
                if (!SetProperty(ref _stoneCount, Math.Max(0, value)))
                {
                    return;
                }

                _owner.NotifyPlanEdited();
            }
        }

        public string DropDisplayName
        {
            get => _dropDisplayName;
            private set => SetProperty(ref _dropDisplayName, value);
        }

        public string InventoryDisplay
        {
            get => _inventoryDisplay;
            private set => SetProperty(ref _inventoryDisplay, value);
        }

        public string InventoryLabel => _owner.InventoryText;

        internal static DepotMaintainPlanRow FromPlan(DepotMaintainModuleViewModel owner, DepotMaintainPlan plan)
        {
            var row = new DepotMaintainPlanRow(owner)
            {
                _stage = plan.Stage,
                _dropId = plan.DropId,
                _dropCount = plan.DropCount,
                _useMedicine = plan.UseMedicine,
                _medicineCount = plan.MedicineCount,
                _useStone = plan.UseStone,
                _stoneCount = plan.StoneCount,
            };
            row._dropDisplayName = owner.ResolveDropDisplayName(plan.DropId);
            return row;
        }

        internal DepotMaintainPlan ToPlan()
        {
            return new DepotMaintainPlan
            {
                Stage = Stage,
                DropId = DropId,
                DropCount = DropCount,
                UseMedicine = UseMedicine,
                MedicineCount = MedicineCount,
                UseStone = UseStone,
                StoneCount = StoneCount,
            };
        }

        internal void RefreshInventory()
        {
            InventoryDisplay = _owner.ResolveInventoryDisplay(_dropId);
        }

        internal void RefreshLocalizedText()
        {
            DropDisplayName = _owner.ResolveDropDisplayName(_dropId);
            OnPropertyChanged(nameof(InventoryLabel));
        }
    }
}
