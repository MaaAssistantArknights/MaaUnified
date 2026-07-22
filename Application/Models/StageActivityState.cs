namespace MAAUnified.Application.Models;

/// <summary>
/// The stage metadata exposed by gui/StageActivityV2.json together with the
/// permanent farming stages. This is the single source used by the fight UI.
/// </summary>
public sealed record StageActivityState(
    string ClientType,
    IReadOnlyList<StageActivityStage> Stages,
    DateTimeOffset? RefreshedAt,
    bool ResourceTasksUpdated,
    string? CoreVersion = null)
{
    public static StageActivityState Empty(string clientType, string? coreVersion = null) => new(
        clientType,
        StageActivityStage.CreatePermanentStages(),
        RefreshedAt: null,
        ResourceTasksUpdated: false,
        CoreVersion: coreVersion);

    public StageActivityStage? Find(string stageCode)
    {
        return Stages.FirstOrDefault(stage =>
            string.Equals(stage.Value, stageCode, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed record StageActivityStage(
    string Display,
    string Value,
    IReadOnlySet<DayOfWeek>? OpenDaysOfWeek = null,
    StageActivityWindow? Activity = null,
    string? Tip = null,
    string? Drop = null,
    IReadOnlyList<IReadOnlyList<string>>? DropGroups = null,
    bool IsHidden = false,
    string? MinimumRequired = null,
    bool IsCoreVersionSupported = true)
{
    public bool IsOpen(DateTime utcNow, DayOfWeek dayOfWeek)
    {
        if (Activity is not null)
        {
            if (Activity.IsBeingOpen(utcNow))
            {
                return true;
            }

            if (!Activity.IsResourceCollection)
            {
                return false;
            }
        }

        return OpenDaysOfWeek is null
            || OpenDaysOfWeek.Count == 0
            || OpenDaysOfWeek.Contains(dayOfWeek);
    }

    public bool IsOpenOrWillOpen(DateTime utcNow)
    {
        return Activity is null || Activity.IsResourceCollection || !Activity.IsExpired(utcNow);
    }

    public static IReadOnlyList<StageActivityStage> CreatePermanentStages()
    {
        return
        [
            new("Cur/Last", string.Empty),
            new("Pormpt1", "Pormpt1", Days(DayOfWeek.Monday), Tip: "Pormpt1", IsHidden: true),
            new("Pormpt2", "Pormpt2", Days(DayOfWeek.Sunday), Tip: "Pormpt2", IsHidden: true),
            new("1-7", "1-7"),
            new("R8-11", "R8-11"),
            new("12-17-HARD", "12-17-HARD"),
            new("CE-6", "CE-6", Days(DayOfWeek.Tuesday, DayOfWeek.Thursday, DayOfWeek.Saturday, DayOfWeek.Sunday), Tip: "CETip"),
            new("AP-5", "AP-5", Days(DayOfWeek.Monday, DayOfWeek.Thursday, DayOfWeek.Saturday, DayOfWeek.Sunday), Tip: "APTip"),
            new("CA-5", "CA-5", Days(DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Friday, DayOfWeek.Sunday), Tip: "CATip", DropGroups: [["3301", "3302", "3303"]]),
            new("LS-6", "LS-6", Tip: "LSTip"),
            new("SK-5", "SK-5", Days(DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday, DayOfWeek.Saturday), Tip: "SKTip"),
            new("Annihilation", "Annihilation"),
            new("PR-A-1", "PR-A-1", Days(DayOfWeek.Monday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Sunday), Tip: "PR-ATip", DropGroups: [["3261", "3231"], ["3262", "3232"]]),
            new("PR-A-2", "PR-A-2", Days(DayOfWeek.Monday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Sunday)),
            new("PR-B-1", "PR-B-1", Days(DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Friday, DayOfWeek.Saturday), Tip: "PR-BTip", DropGroups: [["3251", "3241"], ["3252", "3242"]]),
            new("PR-B-2", "PR-B-2", Days(DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Friday, DayOfWeek.Saturday)),
            new("PR-C-1", "PR-C-1", Days(DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Saturday, DayOfWeek.Sunday), Tip: "PR-CTip", DropGroups: [["3211", "3271"], ["3212", "3272"]]),
            new("PR-C-2", "PR-C-2", Days(DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Saturday, DayOfWeek.Sunday)),
            new("PR-D-1", "PR-D-1", Days(DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Saturday, DayOfWeek.Sunday), Tip: "PR-DTip", DropGroups: [["3221", "3281"], ["3222", "3282"]]),
            new("PR-D-2", "PR-D-2", Days(DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Saturday, DayOfWeek.Sunday)),
            new("OF-1", "OF-1", IsHidden: true),
            new("OF-F3", "OF-F3", IsHidden: true),
        ];
    }

    private static IReadOnlySet<DayOfWeek> Days(params DayOfWeek[] days) => new HashSet<DayOfWeek>(days);
}

public sealed record StageActivityWindow(
    string Tip,
    string StageName,
    DateTime UtcStartTime,
    DateTime UtcExpireTime,
    bool IsResourceCollection = false)
{
    public bool IsExpired(DateTime utcNow) => utcNow >= UtcExpireTime;

    public bool IsNotOpenYet(DateTime utcNow) => utcNow <= UtcStartTime;

    public bool IsBeingOpen(DateTime utcNow) => !IsNotOpenYet(utcNow) && !IsExpired(utcNow);
}
