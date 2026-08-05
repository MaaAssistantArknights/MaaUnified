namespace MAAUnified.Application.Security;

internal sealed class SensitiveSettingsPendingCommit
{
    public const int LatestFormatVersion = 1;

    public int FormatVersion { get; set; } = LatestFormatVersion;

    public string ConfigPathId { get; set; } = string.Empty;

    public SensitiveStorageBackendKind Backend { get; set; }

    public string BaseStoreId { get; set; } = string.Empty;

    public long BaseRevision { get; set; }

    public string TargetStoreId { get; set; } = string.Empty;

    public long TargetRevision { get; set; }

    public string? TargetCommitId { get; set; }
}
