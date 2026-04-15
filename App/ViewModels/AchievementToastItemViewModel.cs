using MAAUnified.App.ViewModels.Infrastructure;

namespace MAAUnified.App.ViewModels;

public sealed class AchievementToastItemViewModel : ObservableObject
{
    public AchievementToastItemViewModel(
        string id,
        string celebrateText,
        string title,
        string description,
        string medalColor,
        bool autoClose,
        DateTimeOffset unlockedAtUtc)
    {
        Id = id;
        CelebrateText = celebrateText;
        Title = title;
        Description = description;
        MedalColor = medalColor;
        AutoClose = autoClose;
        UnlockedAtUtc = unlockedAtUtc;
    }

    public string Id { get; }

    public string CelebrateText { get; }

    public string Title { get; }

    public string Description { get; }

    public string MedalColor { get; }

    public bool AutoClose { get; }

    public DateTimeOffset UnlockedAtUtc { get; }
}
