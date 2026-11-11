using MAAUnified.Application.Models;

namespace MAAUnified.Application.Services;

/// <summary>
/// Runs an operation while <see cref="UnifiedTaskItem.IsEnabled"/> values on the live
/// profile are temporarily forced (run-once, account switch, remote subset start) and
/// restores the original in-memory values afterwards. The session append transaction
/// persists the profile when it consumes a one-shot Recruit expedite; such a mid-window
/// save would otherwise carry the temporarily forced enable states to disk until the
/// next save (and durably on a crash inside the window). When any save landed inside
/// the forced window, the profile is saved once more after the in-memory restore so the
/// persisted file keeps the original enable states.
/// </summary>
public static class TaskQueueEnableStateOverride
{
    public static async Task<T> RunAsync<T>(
        UnifiedConfigurationService configService,
        UnifiedProfile profile,
        Action applyForcedEnableStates,
        Func<Task<T>> operation,
        UiLogService? logService = null,
        string scope = "Task queue enable override")
    {
        ArgumentNullException.ThrowIfNull(configService);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(applyForcedEnableStates);
        ArgumentNullException.ThrowIfNull(operation);

        var snapshot = profile.TaskQueue.Select(static task => task.IsEnabled).ToArray();
        var savedWhileEnableStatesForced = false;
        void OnConfigChangedWhileEnableStatesForced(UnifiedConfig _) => savedWhileEnableStatesForced = true;
        configService.ConfigChanged += OnConfigChangedWhileEnableStatesForced;
        try
        {
            applyForcedEnableStates();
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            configService.ConfigChanged -= OnConfigChangedWhileEnableStatesForced;
            for (var index = 0; index < profile.TaskQueue.Count && index < snapshot.Length; index++)
            {
                profile.TaskQueue[index].IsEnabled = snapshot[index];
            }

            if (savedWhileEnableStatesForced)
            {
                try
                {
                    await configService.SaveAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logService?.Warn($"{scope} could not re-save restored task enable states: {ex.Message}");
                }
            }
        }
    }
}
