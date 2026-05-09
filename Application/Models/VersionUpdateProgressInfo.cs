namespace MAAUnified.Application.Models;

public enum VersionUpdateProgressOperation
{
    SoftwarePackage = 0,
    ResourcePackage = 1,
}

public enum VersionUpdateProgressStage
{
    Started = 0,
    Downloading = 1,
    Preparing = 2,
    Completed = 3,
}

public enum VersionUpdateProgressSource
{
    Unknown = 0,
    GlobalSource = 1,
    MirrorChyan = 2,
}

public sealed record VersionUpdateProgressInfo(
    VersionUpdateProgressOperation Operation,
    VersionUpdateProgressStage Stage,
    VersionUpdateProgressSource Source = VersionUpdateProgressSource.Unknown,
    string? Detail = null,
    long BytesTransferred = 0,
    long TotalBytes = 0,
    double BytesPerSecond = 0d);
