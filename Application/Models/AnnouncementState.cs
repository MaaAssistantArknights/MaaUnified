using MAAUnified.Compat.Constants;

namespace MAAUnified.Application.Models;

public sealed record AnnouncementState(
    string AnnouncementInfo,
    bool DoNotRemindThisAnnouncementAgain,
    bool DoNotShowAnnouncement,
    string LastFetchedSourceUrl = "",
    DateTimeOffset? LastFetchedAtUtc = null)
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
