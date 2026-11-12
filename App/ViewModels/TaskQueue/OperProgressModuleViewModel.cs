using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json.Nodes;
using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.Application.Models;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.TaskParams;

namespace MAAUnified.App.ViewModels.TaskQueue;

/// <summary>
/// 干员培养任务（WPF dev-v2 a412e6a294）。计划条目按顺序执行，每条持有干员名、
/// 可选职业以及精英化/技能等级/三技能专精目标；目标为 0 时不下发（Core 语义为
/// ｢不要求｣）。干员名经 battle_data 别名索引归一化，多语言输入落为 Core 认得的
/// 规范名。
/// </summary>
public sealed class OperProgressModuleViewModel : TaskModuleSettingsViewModelBase
{
    private readonly OperNameIndex _operNames;
    private string _newPlanName = string.Empty;
    private string _newPlanRole = OperProgressRoles.Unknown;
    private bool _suppressPlanChanges;
    private IReadOnlyList<RoleOption> _roleOptions = [];
    private IReadOnlyList<IntOption> _eliteOptions = [];
    private IReadOnlyList<IntOption> _skillLevelOptions = [];
    private IReadOnlyList<IntOption> _masteryOptions = [];

    public OperProgressModuleViewModel(MAAUnifiedRuntime runtime, LocalizedTextMap texts)
        : this(runtime, texts, null)
    {
    }

    internal OperProgressModuleViewModel(MAAUnifiedRuntime runtime, LocalizedTextMap texts, OperNameIndex? operNames)
        : base(runtime, texts, TaskModuleTypes.OperProgress)
    {
        _operNames = operNames ?? OperNameIndex.Shared;
        RebuildLocalizedOptions();
        Plans.CollectionChanged += OnPlansCollectionChanged;
        Texts.PropertyChanged += OnTextsChanged;
    }

    public ObservableCollection<OperProgressPlanRow> Plans { get; } = [];

    public IReadOnlyList<RoleOption> RoleOptions => _roleOptions;

    public IReadOnlyList<IntOption> EliteOptions => _eliteOptions;

    public IReadOnlyList<IntOption> SkillLevelOptions => _skillLevelOptions;

    public IReadOnlyList<IntOption> MasteryOptions => _masteryOptions;

    public string NewPlanName
    {
        get => _newPlanName;
        set => SetProperty(ref _newPlanName, value ?? string.Empty);
    }

    public string NewPlanRole
    {
        get => _newPlanRole;
        set => SetProperty(ref _newPlanRole, OperProgressRoles.Normalize(value));
    }

    public bool ShowEmptyHint => Plans.Count == 0;

    public string AddPlanText => Texts.GetOrDefault("OperProgress.AddPlan", "Add operator");

    public string OperatorNameText => Texts.GetOrDefault("OperProgress.OperatorName", "Operator");

    public string OperatorNameWatermark => Texts.GetOrDefault("OperProgress.OperatorNameWatermark", "Operator name");

    public string OperatorNameTip => Texts.GetOrDefault(
        "OperProgress.OperatorNameTip",
        "Any language variant is accepted and normalized to the name Core recognizes.");

    public bool AddPlan()
    {
        var rawName = (NewPlanName ?? string.Empty).Trim();
        if (rawName.Length == 0)
        {
            StatusMessage = Texts.GetOrDefault("OperProgress.NameRequired", "Enter an operator name first.");
            return false;
        }

        var name = _operNames.NormalizeName(rawName);
        var role = OperProgressRoles.Normalize(NewPlanRole);
        if (role == OperProgressRoles.Unknown)
        {
            role = _operNames.ResolveRole(name);
        }

        // WPF AddOperator defaults to E2 / skill 7 / all three masteries at 3.
        var row = new OperProgressPlanRow(this, name, role, 2, 7, [3, 3, 3]);
        Plans.Add(row);
        NewPlanName = string.Empty;
        NewPlanRole = OperProgressRoles.Unknown;
        StatusMessage = string.Empty;
        return true;
    }

    public void RemovePlan(OperProgressPlanRow? row)
    {
        if (row is null)
        {
            return;
        }

        Plans.Remove(row);
    }

    /// <summary>
    /// Moves one plan by <paramref name="offset"/> positions; WPF offers drag
    /// reordering, this exposes the same ordering through explicit moves.
    /// </summary>
    public void MovePlan(OperProgressPlanRow? row, int offset)
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

    public bool CanMoveUp(OperProgressPlanRow? row) => row is not null && Plans.IndexOf(row) > 0;

    public bool CanMoveDown(OperProgressPlanRow? row)
    {
        var index = row is null ? -1 : Plans.IndexOf(row);
        return index >= 0 && index < Plans.Count - 1;
    }

    public string MoveUpText => Texts.GetOrDefault("OperProgress.MoveUp", "Move up");

    public string MoveDownText => Texts.GetOrDefault("OperProgress.MoveDown", "Move down");

    public string RemoveText => Texts.GetOrDefault("OperProgress.Remove", "Remove");

    public string EmptyHintText => Texts.GetOrDefault(
        "OperProgress.EmptyHint",
        "No operator plan yet; this task is skipped at run time.");

    internal string NormalizeOperatorName(string? name) => _operNames.NormalizeName(name);

    internal string ResolveOperatorRole(string? name) => _operNames.ResolveRole(name);

    internal string GetLocalizedOperatorName(string? name) => _operNames.GetLocalizedDisplayName(name, Texts.Language);

    internal bool CanValidateOperatorNames => !_operNames.IsEmpty;

    internal bool IsKnownOperator(string? name) => _operNames.TryResolve(name, out _);

    protected override Task LoadFromParametersAsync(JsonObject parameters, CancellationToken cancellationToken)
    {
        var model = OperProgressParams.FromJson(parameters);
        _suppressPlanChanges = true;
        try
        {
            Plans.Clear();
            foreach (var plan in model.Plans)
            {
                Plans.Add(new OperProgressPlanRow(
                    this,
                    _operNames.NormalizeName(plan.Name),
                    OperProgressRoles.Normalize(plan.Role),
                    plan.Elite,
                    plan.SkillLevel,
                    plan.SkillMastery));
            }
        }
        finally
        {
            _suppressPlanChanges = false;
        }

        ReindexPlans();
        OnPropertyChanged(nameof(ShowEmptyHint));
        return Task.CompletedTask;
    }

    protected override JsonObject BuildParameters()
    {
        var plans = new List<OperProgressPlan>();
        foreach (var row in Plans)
        {
            var plan = row.ToPlan();
            if (string.IsNullOrWhiteSpace(plan.Name))
            {
                continue;
            }

            plans.Add(plan);
        }

        return new OperProgressParams
        {
            Plans = plans,
        }.ToJson();
    }

    private void OnPlansCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        ReindexPlans();
        OnPropertyChanged(nameof(ShowEmptyHint));
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

        RebuildLocalizedOptions();
        foreach (var row in Plans)
        {
            row.RefreshLocalizedText();
        }

        OnPropertyChanged(nameof(AddPlanText));
        OnPropertyChanged(nameof(OperatorNameText));
        OnPropertyChanged(nameof(OperatorNameWatermark));
        OnPropertyChanged(nameof(OperatorNameTip));
        OnPropertyChanged(nameof(MoveUpText));
        OnPropertyChanged(nameof(MoveDownText));
        OnPropertyChanged(nameof(RemoveText));
        OnPropertyChanged(nameof(EmptyHintText));
    }

    private void RebuildLocalizedOptions()
    {
        var notSet = Texts.GetOrDefault("OperProgress.NotSet", "---");
        _roleOptions =
        [
            new RoleOption(OperProgressRoles.Unknown, notSet),
            .. OperProgressRoles.Selectable.Select(role => new RoleOption(
                role,
                Texts.GetOrDefault($"Game.Role.{role}", role))),
        ];

        _eliteOptions =
        [
            new IntOption(0, notSet),
            new IntOption(1, "1"),
            new IntOption(2, "2"),
        ];

        _skillLevelOptions =
        [
            new IntOption(0, notSet),
            .. new[] { 2, 3, 4, 5, 6, 7 }.Select(level => new IntOption(level, $"Lv. {level}")),
        ];

        _masteryOptions =
        [
            new IntOption(0, notSet),
            new IntOption(1, Texts.GetOrDefault("OperProgress.Mastery1", "Mastery 1")),
            new IntOption(2, Texts.GetOrDefault("OperProgress.Mastery2", "Mastery 2")),
            new IntOption(3, Texts.GetOrDefault("OperProgress.Mastery3", "Mastery 3")),
        ];

        OnPropertyChanged(nameof(RoleOptions));
        OnPropertyChanged(nameof(EliteOptions));
        OnPropertyChanged(nameof(SkillLevelOptions));
        OnPropertyChanged(nameof(MasteryOptions));
    }

    internal void NotifyPlanEdited()
    {
        if (_suppressPlanChanges)
        {
            return;
        }

        QueuePersist();
    }

    public sealed record RoleOption(string Value, string DisplayName);

    public sealed record IntOption(int Value, string DisplayName);

    public sealed class OperProgressPlanRow : ObservableObject
    {
        private readonly OperProgressModuleViewModel _owner;
        private string _name;
        private string _role;
        private int _elite;
        private int _skillLevel;
        private readonly int[] _skillMastery = new int[3];
        private int _index;

        internal OperProgressPlanRow(
            OperProgressModuleViewModel owner,
            string name,
            string role,
            int elite,
            int skillLevel,
            IReadOnlyList<int> skillMastery)
        {
            _owner = owner;
            _name = name ?? string.Empty;
            _role = OperProgressRoles.Normalize(role);
            _elite = NormalizeElite(elite);
            _skillLevel = NormalizeSkillLevel(skillLevel);
            for (var index = 0; index < _skillMastery.Length; index++)
            {
                _skillMastery[index] = index < skillMastery.Count ? Math.Clamp(skillMastery[index], 0, 3) : 0;
            }
        }

        public int Index
        {
            get => _index;
            set => SetProperty(ref _index, value);
        }

        public string Name
        {
            get => _name;
            set
            {
                var normalized = _owner.NormalizeOperatorName(value);
                if (!SetProperty(ref _name, normalized))
                {
                    return;
                }

                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(NameWarningVisible));
                if (OperProgressRoles.Normalize(_role) == OperProgressRoles.Unknown)
                {
                    Role = _owner.ResolveOperatorRole(normalized);
                }

                _owner.NotifyPlanEdited();
            }
        }

        public string Role
        {
            get => _role;
            set
            {
                if (!SetProperty(ref _role, OperProgressRoles.Normalize(value)))
                {
                    return;
                }

                _owner.NotifyPlanEdited();
            }
        }

        public int Elite
        {
            get => _elite;
            set
            {
                if (!SetProperty(ref _elite, NormalizeElite(value)))
                {
                    return;
                }

                _owner.NotifyPlanEdited();
            }
        }

        public int SkillLevel
        {
            get => _skillLevel;
            set
            {
                if (!SetProperty(ref _skillLevel, NormalizeSkillLevel(value)))
                {
                    return;
                }

                _owner.NotifyPlanEdited();
            }
        }

        public int Mastery1
        {
            get => _skillMastery[0];
            set => SetMastery(0, value);
        }

        public int Mastery2
        {
            get => _skillMastery[1];
            set => SetMastery(1, value);
        }

        public int Mastery3
        {
            get => _skillMastery[2];
            set => SetMastery(2, value);
        }

        public string DisplayName => _owner.GetLocalizedOperatorName(_name);

        public bool NameWarningVisible
            => !string.IsNullOrWhiteSpace(_name)
               && _owner.CanValidateOperatorNames
               && !_owner.IsKnownOperator(_name);

        public string NameWarningText => _owner.Texts.GetOrDefault(
            "OperProgress.UnknownOperator",
            "Unknown operator name; MaaCore will reject this plan.");

        internal OperProgressPlan ToPlan()
        {
            return new OperProgressPlan
            {
                Role = OperProgressRoles.Normalize(_role),
                Name = (_name ?? string.Empty).Trim(),
                Elite = NormalizeElite(_elite),
                SkillLevel = NormalizeSkillLevel(_skillLevel),
                SkillMastery = [.. _skillMastery],
            };
        }

        internal void RefreshLocalizedText()
        {
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(NameWarningText));
            OnPropertyChanged(nameof(NameWarningVisible));
        }

        private void SetMastery(int index, int value)
        {
            var normalized = Math.Clamp(value, 0, 3);
            if (_skillMastery[index] == normalized)
            {
                return;
            }

            _skillMastery[index] = normalized;
            OnPropertyChanged(index switch
            {
                0 => nameof(Mastery1),
                1 => nameof(Mastery2),
                _ => nameof(Mastery3),
            });
            _owner.NotifyPlanEdited();
        }

        private static int NormalizeElite(int value) => value is >= 1 and <= 2 ? value : 0;

        private static int NormalizeSkillLevel(int value) => value is >= 2 and <= 7 ? value : 0;
    }
}
