namespace MAAUnified.Application.Models;

public sealed record ResourceUpdateCheckResult(
    bool IsUpdateAvailable,
    string DisplayVersion,
    string ReleaseNote,
    DateTimeOffset? VersionTimestamp,
    bool RequiresMirrorChyanCdk,
    string? DownloadUrl);
