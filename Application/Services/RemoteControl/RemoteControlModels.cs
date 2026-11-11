using System.Text.Json.Nodes;

namespace MAAUnified.Application.Services.RemoteControl;

internal sealed record RemoteControlPollingSnapshot(
    Uri? GetTaskEndpoint,
    Uri? ReportEndpoint,
    string UserIdentity,
    string DeviceIdentity,
    int PollIntervalMs)
{
    // WPF parity: a valid GetTask endpoint alone is enough to start polling;
    // the report endpoint is optional and only gates status reports.
    public bool IsConfigured => GetTaskEndpoint is not null;
}

internal sealed record RemoteControlCommandRequest(
    string RawCommand,
    JsonNode? Payload,
    string UserIdentity,
    string DeviceIdentity,
    string? TaskId = null);

// WPF parity: the frozen WPF RemoteControlService routes each fetched task to
// either the sequential execution queue or the instant execution queue, and
// silently consumes unknown task types without dispatching or reporting them.
internal enum RemoteControlCommandKind
{
    Unknown = 0,
    Sequential = 1,
    Instant = 2,
}

internal sealed record RemoteControlCommandResult(
    string RawCommand,
    string NormalizedCommand,
    bool Success,
    string Message,
    string? ErrorCode = null,
    string? Details = null,
    int? CoreTaskId = null,
    string? TaskType = null,
    byte[]? ImageBytes = null,
    string? ImageContentType = null,
    string? LegacyReportPayload = null);
