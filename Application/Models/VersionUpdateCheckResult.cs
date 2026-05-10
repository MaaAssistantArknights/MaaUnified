using System;

namespace MAAUnified.Application.Models;

public enum PackageResolutionStatus
{
    NotChecked = 0,
    Available = 1,
    Unavailable = 2,
    WindowsManualUpdateRequired = 3,
    DownloadFailed = 4,
    MacOSManualInstallRequired = 5,
    AppImageManualInstallRequired = 6,
}

public enum PackageSourceKind
{
    None = 0,
    ReleaseAsset = 1,
    WindowsRelayManifest = 2,
}

public sealed record VersionUpdateCheckResult(
    string Channel,
    string CurrentVersion,
    string TargetVersion,
    string ReleaseName,
    string Summary,
    string Body,
    string? PackageName,
    Uri? PackageDownloadUrl,
    long? PackageSize,
    bool IsNewVersion,
    bool HasPackage,
    string? PreparedPackagePath = null,
    PackageResolutionStatus PackageResolutionStatus = PackageResolutionStatus.NotChecked,
    PackageSourceKind PackageSourceKind = PackageSourceKind.None,
    string? PackageFailureMessageKey = null,
    IReadOnlyList<Uri>? PackageMirrorUrls = null);
