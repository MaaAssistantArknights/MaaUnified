using MAAUnified.Compat.Constants;

namespace MAAUnified.Application.Models;

public sealed record AnnouncementState(
    string AnnouncementInfo,
    bool DoNotRemindThisAnnouncementAgain,
    bool DoNotShowAnnouncement,
    string LastFetchedSourceUrl = "",
    DateTimeOffset? LastFetchedAtUtc = null,
    IReadOnlyDictionary<string, AnnouncementLocaleState>? LocalizedStates = null,
    string SelectedLanguage = "")
{
    public static AnnouncementState Default { get; } = new(
        AnnouncementInfo: string.Empty,
        DoNotRemindThisAnnouncementAgain: false,
        DoNotShowAnnouncement: false,
        LastFetchedSourceUrl: string.Empty,
        LastFetchedAtUtc: null);

    public AnnouncementState WithFetchedAnnouncement(string announcementInfo, Uri sourceUri, DateTimeOffset fetchedAtUtc)
    {
        return this with
        {
            AnnouncementInfo = announcementInfo,
            DoNotRemindThisAnnouncementAgain = false,
            LastFetchedSourceUrl = sourceUri.ToString(),
            LastFetchedAtUtc = fetchedAtUtc,
        };
    }

    public bool HasAnnouncementInfo => !string.IsNullOrWhiteSpace(AnnouncementInfo);

    public AnnouncementState ForLanguage(string? language)
    {
        var normalizedLanguage = NormalizeAnnouncementLanguage(language);
        if (LocalizedStates is not null && LocalizedStates.TryGetValue(normalizedLanguage, out var localized))
        {
            return this with
            {
                AnnouncementInfo = localized.AnnouncementInfo,
                DoNotRemindThisAnnouncementAgain = localized.DoNotRemindThisAnnouncementAgain,
                LastFetchedSourceUrl = localized.LastFetchedSourceUrl,
                LastFetchedAtUtc = localized.LastFetchedAtUtc,
                SelectedLanguage = normalizedLanguage,
            };
        }

        // Legacy state had one unlabelled cache. Do not reuse it as a fallback
        // after a language-specific cache has been written.
        var hasLocalizedStates = LocalizedStates is { Count: > 0 };
        return this with
        {
            AnnouncementInfo = hasLocalizedStates ? string.Empty : AnnouncementInfo,
            DoNotRemindThisAnnouncementAgain = hasLocalizedStates ? false : DoNotRemindThisAnnouncementAgain,
            LastFetchedSourceUrl = hasLocalizedStates ? string.Empty : LastFetchedSourceUrl,
            LastFetchedAtUtc = hasLocalizedStates ? null : LastFetchedAtUtc,
            SelectedLanguage = normalizedLanguage,
        };
    }

    public AnnouncementLocaleState ToLocaleState() => new(
        AnnouncementInfo,
        DoNotRemindThisAnnouncementAgain,
        LastFetchedSourceUrl,
        LastFetchedAtUtc);

    public static string NormalizeAnnouncementLanguage(string? language) =>
        language?.StartsWith("zh", StringComparison.OrdinalIgnoreCase) == true ? "zh" : "en";

    public AnnouncementState WithDialogPreferences(
        bool doNotRemindThisAnnouncementAgain,
        bool doNotShowAnnouncement)
    {
        return this with
        {
            DoNotRemindThisAnnouncementAgain = doNotRemindThisAnnouncementAgain,
            DoNotShowAnnouncement = doNotShowAnnouncement,
        };
    }

    public IReadOnlyDictionary<string, string> ToGlobalSettingUpdates()
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ConfigurationKeys.AnnouncementInfo] = AnnouncementInfo,
            [ConfigurationKeys.DoNotRemindThisAnnouncementAgain] = DoNotRemindThisAnnouncementAgain.ToString(),
            [ConfigurationKeys.DoNotShowAnnouncement] = DoNotShowAnnouncement.ToString(),
        };
    }
}

public sealed record AnnouncementLocaleState(
    string AnnouncementInfo,
    bool DoNotRemindThisAnnouncementAgain,
    string LastFetchedSourceUrl,
    DateTimeOffset? LastFetchedAtUtc);
