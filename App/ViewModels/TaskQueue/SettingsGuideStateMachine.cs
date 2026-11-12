using MAAUnified.App.ViewModels.Infrastructure;

namespace MAAUnified.App.ViewModels.TaskQueue;

/// <summary>
/// Simulated actions in the task-settings guide step. The sequence mirrors the WPF dev-v2
/// guide gates (<c>0f4ba4c610</c>, <c>0e940011a9</c>, <c>80bcbd6fbd</c>, <c>b8a65381a6</c>)
/// without touching the real task queue.
/// </summary>
public enum SettingsGuideDemoAction
{
    AddStartUp,
    AddFight,
    RenameFight,
    SelectStage,
    ReorderFight,
    EnableWeekly,
    AddSwitchTheme,
    ReorderTheme,
    AddNightTheme,
}

/// <summary>
/// One guide step. <see cref="SettingsSectionKey"/> is the settings-page section opened by the
/// step, or <see langword="null"/> when the step targets the task queue itself.
/// </summary>
public sealed record SettingsGuideStepDefinition(
    int Index,
    string TitleKey,
    string DescriptionKey,
    string? SettingsSectionKey,
    bool IsAgreementStep,
    bool IsTaskDemoStep);

/// <summary>
/// Observable state of one simulated demo action. Labels are resolved through the delegate so a
/// language change only refreshes text, never the state machine progress.
/// </summary>
public sealed class SettingsGuideDemoStep : ObservableObject
{
    private readonly Func<string, string> _localize;
    private bool _isCompleted;
    private bool _isBadgeVisible;
    private bool _isEnabled;

    public SettingsGuideDemoStep(
        SettingsGuideDemoAction action,
        string labelKey,
        Func<string, string> localize)
    {
        Action = action;
        LabelKey = labelKey;
        _localize = localize;
    }

    public SettingsGuideDemoAction Action { get; }

    public string LabelKey { get; }

    public string Label => _localize(LabelKey);

    public bool IsCompleted
    {
        get => _isCompleted;
        internal set => SetProperty(ref _isCompleted, value);
    }

    public bool IsBadgeVisible
    {
        get => _isBadgeVisible;
        internal set => SetProperty(ref _isBadgeVisible, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        internal set => SetProperty(ref _isEnabled, value);
    }

    internal void RefreshLocalization() => OnPropertyChanged(nameof(Label));
}

/// <summary>
/// Testable settings-guide state machine. It owns the seven-step order, the agreement gate and the
/// simulated task demo; the view only binds derived properties.
/// </summary>
/// <remarks>
/// WPF dev-v2 order (<c>0f4ba4c610</c>) is Ui, User/agreement, Game, Connection, Performance,
/// Task settings, Update. The previous MAAUnified order was Ui, Game, Connection, Task settings,
/// Performance, Update, User, so persisted v6.3.7 step indexes are translated once by
/// <see cref="ResolvePersistedStep"/> and marked with <c>Guide.StepOrderVersion</c>.
/// </remarks>
public sealed class SettingsGuideStateMachine
{
    public const int StepCount = 7;
    public const int CompletedStep = StepCount;
    public const int AgreementStepIndex = 1;
    public const int TaskDemoStepIndex = 5;
    public const int StepOrderVersion = 2;

    private static readonly SettingsGuideStepDefinition[] _steps =
    [
        new(0, "UiSettings", "TaskQueue.Guide.Ui.Description", "GUI", false, false),
        new(1, "UserGuide", "TaskQueue.Guide.User.Description", "About", true, false),
        new(2, "GameSettings", "TaskQueue.Guide.Game.Description", "Game", false, false),
        new(3, "ConnectionSettings", "TaskQueue.Guide.Connection.Description", "Connect", false, false),
        new(4, "PerformanceSettings", "TaskQueue.Guide.Performance.Description", "Performance", false, false),
        new(5, "TaskSettings", "TaskQueue.Guide.Tasks.Description", null, false, true),
        new(6, "UpdateSettings", "TaskQueue.Guide.Update.Description", "VersionUpdate", false, false),
    ];

    private static readonly (SettingsGuideDemoAction Action, string LabelKey)[] _demoDefinitions =
    [
        (SettingsGuideDemoAction.AddStartUp, "TaskQueue.Guide.Demo.AddStartUp"),
        (SettingsGuideDemoAction.AddFight, "TaskQueue.Guide.Demo.AddFight"),
        (SettingsGuideDemoAction.RenameFight, "TaskQueue.Guide.Demo.RenameFight"),
        (SettingsGuideDemoAction.SelectStage, "TaskQueue.Guide.Demo.SelectStage"),
        (SettingsGuideDemoAction.ReorderFight, "TaskQueue.Guide.Demo.ReorderFight"),
        (SettingsGuideDemoAction.EnableWeekly, "TaskQueue.Guide.Demo.EnableWeekly"),
        (SettingsGuideDemoAction.AddSwitchTheme, "TaskQueue.Guide.Demo.AddSwitchTheme"),
        (SettingsGuideDemoAction.ReorderTheme, "TaskQueue.Guide.Demo.ReorderTheme"),
        (SettingsGuideDemoAction.AddNightTheme, "TaskQueue.Guide.Demo.AddNightTheme"),
    ];

    private readonly Func<string, string> _localize;

    public SettingsGuideStateMachine(
        int currentStep,
        bool agreementConfirmed,
        bool skipped,
        int skippedAtStep,
        Func<string, string>? localize = null)
    {
        _localize = localize ?? (static key => key);
        CurrentStep = Math.Clamp(currentStep, 0, CompletedStep);
        AgreementConfirmed = agreementConfirmed;
        WasSkipped = skipped;
        SkippedAtStep = Math.Clamp(skippedAtStep, 0, StepCount - 1);
        DemoSteps = _demoDefinitions
            .Select(definition => new SettingsGuideDemoStep(definition.Action, definition.LabelKey, _localize))
            .ToArray();
        RefreshDemoState();
    }

    public int CurrentStep { get; private set; }

    public bool AgreementConfirmed { get; private set; }

    public bool WasSkipped { get; private set; }

    public int SkippedAtStep { get; private set; }

    public IReadOnlyList<SettingsGuideDemoStep> DemoSteps { get; }

    public bool IsVisible => CurrentStep < CompletedStep;

    public bool CanGoToPrevious => CurrentStep > 0;

    public bool IsLastStep => CurrentStep == StepCount - 1;

    public bool IsAgreementStep => CurrentStep == AgreementStepIndex;

    public bool IsTaskDemoStep => CurrentStep == TaskDemoStepIndex;

    public bool IsNextBlocked => IsTaskDemoStep && (!AgreementConfirmed || !AllDemoActionsComplete);

    public bool CanGoToNext => IsVisible && !IsNextBlocked;

    public bool AllDemoActionsComplete => DemoSteps.All(static step => step.IsCompleted);

    public int CompletedDemoActionCount => DemoSteps.Count(static step => step.IsCompleted);

    public SettingsGuideDemoStep? NextDemoAction => DemoSteps.FirstOrDefault(static step => !step.IsCompleted);

    public SettingsGuideStepDefinition CurrentStepDefinition => _steps[Math.Clamp(CurrentStep, 0, StepCount - 1)];

    public bool AgreementBadgeVisible =>
        IsVisible && !AgreementConfirmed && (IsAgreementStep || IsTaskDemoStep);

    public bool TryGoNext()
    {
        if (!CanGoToNext || CurrentStep >= StepCount - 1)
        {
            return false;
        }

        CurrentStep++;
        RefreshDemoState();
        return true;
    }

    public bool TryGoPrevious()
    {
        if (!CanGoToPrevious)
        {
            return false;
        }

        CurrentStep--;
        RefreshDemoState();
        return true;
    }

    public bool ConfirmAgreement()
    {
        if (!IsVisible || !IsAgreementStep || AgreementConfirmed)
        {
            return false;
        }

        AgreementConfirmed = true;
        RefreshDemoState();
        return true;
    }

    public bool CompleteDemoAction(SettingsGuideDemoAction action)
    {
        if (!IsVisible || !IsTaskDemoStep || !AgreementConfirmed)
        {
            return false;
        }

        var step = DemoSteps.FirstOrDefault(candidate => candidate.Action == action);
        if (step is null || step.IsCompleted || !ReferenceEquals(step, NextDemoAction))
        {
            return false;
        }

        step.IsCompleted = true;
        RefreshDemoState();
        return true;
    }

    public bool Complete()
    {
        if (!IsVisible)
        {
            return false;
        }

        CurrentStep = CompletedStep;
        RefreshDemoState();
        return true;
    }

    public bool Skip()
    {
        if (!IsVisible)
        {
            return false;
        }

        WasSkipped = true;
        SkippedAtStep = CurrentStep;
        CurrentStep = CompletedStep;
        RefreshDemoState();
        return true;
    }

    public bool Restart()
    {
        CurrentStep = 0;
        WasSkipped = false;
        SkippedAtStep = 0;
        ResetDemoProgressCore();
        RefreshDemoState();
        return true;
    }

    public void ResetDemoProgress()
    {
        ResetDemoProgressCore();
        RefreshDemoState();
    }

    public void ApplyPersisted(int currentStep, bool agreementConfirmed, bool skipped, int skippedAtStep)
    {
        CurrentStep = Math.Clamp(currentStep, 0, CompletedStep);
        AgreementConfirmed = agreementConfirmed;
        WasSkipped = skipped;
        SkippedAtStep = Math.Clamp(skippedAtStep, 0, StepCount - 1);
        RefreshDemoState();
    }

    public bool Matches(int currentStep, bool agreementConfirmed, bool skipped, int skippedAtStep)
        => CurrentStep == currentStep
            && AgreementConfirmed == agreementConfirmed
            && WasSkipped == skipped
            && SkippedAtStep == skippedAtStep;

    public void RefreshLocalization()
    {
        foreach (var step in DemoSteps)
        {
            step.RefreshLocalization();
        }
    }

    public static string? GetSettingsSectionKey(int stepIndex)
        => stepIndex is >= 0 and < StepCount ? _steps[stepIndex].SettingsSectionKey : null;

    public static int ResolvePersistedStep(int persistedStep, int persistedStepOrderVersion)
    {
        var normalized = Math.Clamp(persistedStep, 0, CompletedStep);
        return persistedStepOrderVersion >= StepOrderVersion
            ? normalized
            : MapLegacyStepIndex(normalized);
    }

    /// <summary>
    /// Translates the MAAUnified v6.3.7 order (Ui, Game, Connect, Tasks, Performance, Update, User)
    /// onto the dev-v2 order.
    /// </summary>
    public static int MapLegacyStepIndex(int legacyStep) => legacyStep switch
    {
        0 => 0,
        1 => 2,
        2 => 3,
        3 => 5,
        4 => 4,
        5 => 6,
        6 => 1,
        _ => Math.Clamp(legacyStep, 0, CompletedStep),
    };

    private void RefreshDemoState()
    {
        var next = AgreementConfirmed ? NextDemoAction : null;
        foreach (var step in DemoSteps)
        {
            var isCurrent = IsTaskDemoStep && ReferenceEquals(step, next);
            step.IsEnabled = isCurrent;
            step.IsBadgeVisible = isCurrent;
        }
    }

    private void ResetDemoProgressCore()
    {
        foreach (var step in DemoSteps)
        {
            step.IsCompleted = false;
        }
    }
}
