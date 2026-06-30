using System;

namespace MAAUnified.Application.Models.TaskParams;

public sealed class FightTaskParamsDto
{
    public string Stage { get; set; } = FightStageSelection.CurrentOrLast;

    public List<string> StagePlan { get; set; } = [FightStageSelection.CurrentOrLast];

    public bool IsStageManually { get; set; }

    public bool? UseMedicine { get; set; } = false;

    public int Medicine { get; set; }

    public bool? UseStone { get; set; } = false;

    public int Stone { get; set; }

    public bool? EnableTimesLimit { get; set; } = false;

    public int Times { get; set; } = int.MaxValue;

    public int Series { get; set; } = 1;

    public bool IsDrGrandet { get; set; }

    public bool UseExpiringMedicine { get; set; }

    public int ExpiringMedicine { get; set; } = 9999;

    public bool? EnableTargetDrop { get; set; } = false;

    public string DropId { get; set; } = string.Empty;

    public int DropCount { get; set; } = 1;

    public bool UseCustomAnnihilation { get; set; }

    public string AnnihilationStage { get; set; } = "Annihilation";

    public bool UseAlternateStage { get; set; }

    public bool HideUnavailableStage { get; set; } = true;

    public string StageResetMode { get; set; } = "Current";

    public bool HideSeries { get; set; }

    public bool AllowUseStoneSave { get; set; }

    public bool UseWeeklySchedule { get; set; }

    public bool WeeklyScheduleSunday { get; set; } = true;

    public bool WeeklyScheduleMonday { get; set; } = true;

    public bool WeeklyScheduleTuesday { get; set; } = true;

    public bool WeeklyScheduleWednesday { get; set; } = true;

    public bool WeeklyScheduleThursday { get; set; } = true;

    public bool WeeklyScheduleFriday { get; set; } = true;

    public bool WeeklyScheduleSaturday { get; set; } = true;
}

public static class FightStageSelection
{
    public const string CurrentOrLast = "$CurrentOrLast";

    public static bool IsCurrentOrLast(string? stage)
    {
        return string.IsNullOrWhiteSpace(stage)
            || string.Equals(stage.Trim(), CurrentOrLast, StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeStoredValue(string? stage)
    {
        return IsCurrentOrLast(stage)
            ? CurrentOrLast
            : stage!.Trim();
    }

    public static string NormalizePlanEntry(string? stage)
    {
        return NormalizeStoredValue(stage);
    }

    public static List<string> NormalizeStagePlan(IEnumerable<string?>? stagePlan)
    {
        var normalized = (stagePlan ?? [])
            .Select(NormalizePlanEntry)
            .ToList();

        if (normalized.Count == 0)
        {
            normalized.Add(CurrentOrLast);
        }

        return normalized;
    }

    public static string ToCoreStage(string? stage)
    {
        return IsCurrentOrLast(stage)
            ? string.Empty
            : stage!.Trim();
    }
}
