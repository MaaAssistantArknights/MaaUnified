using MAAUnified.Compat.Constants;

namespace MAAUnified.Application.Models;

public sealed record AchievementPolicy(
    bool PopupDisabled,
    bool PopupAutoClose)
{
    // Frozen WPF (AchievementSettingsUserControlModel) defaults both flags to false and
    // preserves an explicit false; keep the same defaults here.
    public static AchievementPolicy Default { get; } = new(
        PopupDisabled: false,
        PopupAutoClose: false);

    public IReadOnlyDictionary<string, string> ToProfileSettingUpdates()
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ConfigurationKeys.AchievementPopupDisabled] = PopupDisabled.ToString(),
            [ConfigurationKeys.AchievementPopupAutoClose] = PopupAutoClose.ToString(),
        };
    }
}
