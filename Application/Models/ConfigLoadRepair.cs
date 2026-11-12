namespace MAAUnified.Application.Models;

/// <summary>
/// Describes how a damaged <c>config/avalonia.json</c> was recovered during load.
/// </summary>
/// <param name="RemovedJsonPaths">JSON paths removed before the document could be deserialized.</param>
/// <param name="BrokenConfigBackupPath">Copy of the unreadable file, created before defaults replace it.</param>
public sealed record ConfigLoadRepair(
    IReadOnlyList<string> RemovedJsonPaths,
    string? BrokenConfigBackupPath);

/// <summary>
/// Startup-visible summary of a config load repair, surfaced through the shell notices.
/// </summary>
/// <param name="RemovedJsonPaths">JSON paths removed before the document could be deserialized.</param>
/// <param name="BrokenConfigBackupPath">Copy of the unreadable file, created before defaults replace it.</param>
/// <param name="RebuiltDefaults">Whether the unreadable file was replaced with a default configuration.</param>
/// <param name="Message">Operator-facing description of the recovery.</param>
/// <param name="SuggestedAction">What the user should check next.</param>
public sealed record ConfigRepairNotice(
    IReadOnlyList<string> RemovedJsonPaths,
    string? BrokenConfigBackupPath,
    bool RebuiltDefaults,
    string Message,
    string SuggestedAction);
