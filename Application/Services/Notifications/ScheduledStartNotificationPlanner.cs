using MAAUnified.Platform;

namespace MAAUnified.Application.Services.Notifications;

/// <summary>
/// Selects the timer slots whose pre-start notification is due now. WPF (ba602828e6)
/// compares <c>now + leadMinutes</c> with the slot hour/minute, which also covers timers that
/// wrap past midnight; the lead time is clamped to 1..1439 minutes on read and write.
/// </summary>
public static class ScheduledStartNotificationPlanner
{
    public const int MinLeadMinutes = 1;
    public const int MaxLeadMinutes = 1439;

    public static int ClampLeadMinutes(int minutes) => Math.Clamp(minutes, MinLeadMinutes, MaxLeadMinutes);

    /// <param name="now">Current local time.</param>
    /// <param name="leadMinutes">Configured lead time in minutes.</param>
    /// <param name="slots">Enabled slots (disabled slots must be filtered by the caller).</param>
    /// <returns>The slots whose notification is due at <paramref name="now"/>.</returns>
    public static IReadOnlyList<ScheduledWakeUpSlot> SelectDue(
        DateTimeOffset now,
        int leadMinutes,
        IEnumerable<ScheduledWakeUpSlot> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        var start = now.AddMinutes(ClampLeadMinutes(leadMinutes));
        return slots
            .Where(slot => slot.Hour == start.Hour && slot.Minute == start.Minute)
            .OrderBy(static slot => slot.Index)
            .ToArray();
    }
}
