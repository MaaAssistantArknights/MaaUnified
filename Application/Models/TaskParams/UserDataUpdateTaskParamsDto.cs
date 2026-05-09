namespace MAAUnified.Application.Models.TaskParams;

public sealed class UserDataUpdateTaskParamsDto
{
    public const string TriggerEveryTime = "EveryTime";
    public const string TriggerDaily = "Daily";
    public const string TriggerWeekly = "Weekly";

    public bool UpdateOperBox { get; set; } = true;

    public bool UpdateDepot { get; set; } = true;

    public string TriggerInterval { get; set; } = TriggerEveryTime;
}
