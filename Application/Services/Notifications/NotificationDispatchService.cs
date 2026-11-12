using MAAUnified.Application.Models;
using MAAUnified.Application.Services.Features;
using MAAUnified.Compat.Constants;
using MAAUnified.Platform;

namespace MAAUnified.Application.Services.Notifications;

/// <summary>
/// Producer of a notification event. WPF parity (<c>f9c14e0be1</c>, <c>NotificationSource</c>):
/// the task queue and Copilot are separate sources even though they share the delivery pipeline.
/// </summary>
public enum NotificationSource
{
    TaskQueue,
    Copilot,
}

/// <summary>
/// Notification classification. WPF parity (<c>f9c14e0be1</c>, <c>NotificationKind</c>): the kind
/// selects the external delivery switch and the toast behaviour.
/// </summary>
public enum NotificationKind
{
    TaskComplete,
    TaskError,
    Stalled,
    ScheduledStart,
}

/// <summary>
/// Payload of one notification event, independent from its log presentation.
/// </summary>
/// <param name="Kind">Notification classification.</param>
/// <param name="Title">Title used by the system notification and the external delivery.</param>
/// <param name="Content">Body shown by the system notification and sent externally.</param>
public sealed record NotificationMessage(NotificationKind Kind, string Title, string Content);

/// <summary>
/// Presentation and delivery options for one notification event.
/// </summary>
public sealed record NotificationDispatchOptions
{
    /// <summary>Gets the diagnostics scope used when a delivery fails.</summary>
    public required string Scope { get; init; }

    /// <summary>Gets the human readable reason, used in the system-notification failure message.</summary>
    public required string Reason { get; init; }

    /// <summary>Gets the log line content used by the delivery trigger; defaults to the message content.</summary>
    public string? LogContent { get; init; }

    /// <summary>Gets the WPF log brush key recorded with the log line.</summary>
    public string Color { get; init; } = "TraceLogBrush";

    /// <summary>Gets the final external body. When null the message content is used verbatim.</summary>
    public string? ExternalMessage { get; init; }

    /// <summary>Gets a value indicating whether the external notification pipeline is consulted.</summary>
    public bool SendExternal { get; init; } = true;

    /// <summary>Gets a value indicating whether the event line is recorded in the delivery history.</summary>
    public bool RecordHistory { get; init; }

    /// <summary>Gets a value indicating whether the system notification is dispatched.</summary>
    public bool ShowSystemNotification { get; init; } = true;

    /// <summary>Gets a per-source delivery key; a second event with the same key is dropped.</summary>
    public string? DedupeKey { get; init; }

    /// <summary>Gets the presentation callback. It must only render; publishing is done by this service.</summary>
    public Action? Display { get; init; }

    /// <summary>Gets the event timestamp.</summary>
    public DateTimeOffset? Timestamp { get; init; }
}

/// <summary>
/// The single routing entry point for task notifications. WPF <c>f9c14e0be1</c> collapses every
/// producer (task queue, Copilot, stall timer) into <c>NotificationService.PublishLog/Notify</c>;
/// transports only deliver already selected payloads.
/// </summary>
public interface INotificationDispatchService
{
    /// <summary>
    /// Publishes one notification event. The display callback renders the log line, the service
    /// owns history, system notification and external delivery.
    /// </summary>
    /// <param name="source">Event source.</param>
    /// <param name="message">Notification payload.</param>
    /// <param name="options">Presentation and delivery options.</param>
    void Notify(NotificationSource source, NotificationMessage message, NotificationDispatchOptions options);

    /// <summary>
    /// Publishes one plain log line (WPF <c>NotificationService.PublishLog</c>): history, log-count
    /// trigger and content match. The display callback only renders.
    /// </summary>
    /// <param name="source">Log source.</param>
    /// <param name="content">Log line.</param>
    /// <param name="color">WPF log brush key.</param>
    /// <param name="timestamp">Log timestamp.</param>
    /// <param name="display">Optional presentation callback.</param>
    void PublishLog(
        NotificationSource source,
        string? content,
        string color,
        DateTimeOffset timestamp,
        Action? display = null);

    /// <summary>Prepends the retained log lines to an external notification body.</summary>
    /// <param name="body">Current notification body.</param>
    /// <param name="timestamp">Timestamp of the notification being sent.</param>
    /// <param name="skipNewest">Number of newest records to leave out.</param>
    /// <returns>Body with the attached log lines, or the unchanged body.</returns>
    string BundleHistory(string body, DateTimeOffset timestamp, int skipNewest = 0);

    /// <summary>Drops the retained delivery history.</summary>
    void ClearHistory();

    /// <summary>Drops every per-source dedupe key.</summary>
    void ClearDedupe();
}

/// <summary>
/// Default notification routing. The external request shape matches the pre-wave
/// <c>BuildAutomaticExternalNotificationRequests</c> contract, so provider payloads do not change.
/// </summary>
public sealed class NotificationDispatchService : INotificationDispatchService
{
    private const int MaxDedupeKeys = 256;

    private readonly UnifiedConfigurationService _configService;
    private readonly IPlatformCapabilityService _platformCapabilityService;
    private readonly INotificationProviderFeatureService _notificationProviderService;
    private readonly NotificationLogHistory _history;
    private readonly ExternalNotificationTrigger _trigger;
    private readonly Func<string, string, Exception?, Task>? _failureHandler;
    private readonly Func<string, string, Exception?, Task>? _systemFailureHandler;
    private readonly object _dedupeGate = new();
    private readonly HashSet<string> _dedupeKeys = new(StringComparer.Ordinal);
    private readonly Queue<string> _dedupeOrder = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="NotificationDispatchService"/> class.
    /// </summary>
    /// <param name="configService">Configuration source for delivery switches and channels.</param>
    /// <param name="platformCapabilityService">System-notification transport.</param>
    /// <param name="notificationProviderService">External-notification transport.</param>
    /// <param name="history">History shared with the log publication path.</param>
    /// <param name="trigger">Delivery trigger shared with the log publication path.</param>
    /// <param name="failureHandler">Optional diagnostics hook invoked when an external delivery fails.</param>
    /// <param name="systemFailureHandler">
    /// Optional diagnostics hook invoked when a system notification throws; defaults to
    /// <paramref name="failureHandler"/>.
    /// </param>
    public NotificationDispatchService(
        UnifiedConfigurationService configService,
        IPlatformCapabilityService platformCapabilityService,
        INotificationProviderFeatureService notificationProviderService,
        NotificationLogHistory history,
        ExternalNotificationTrigger trigger,
        Func<string, string, Exception?, Task>? failureHandler = null,
        Func<string, string, Exception?, Task>? systemFailureHandler = null)
    {
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _platformCapabilityService = platformCapabilityService ?? throw new ArgumentNullException(nameof(platformCapabilityService));
        _notificationProviderService = notificationProviderService ?? throw new ArgumentNullException(nameof(notificationProviderService));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _trigger = trigger ?? throw new ArgumentNullException(nameof(trigger));
        _failureHandler = failureHandler;
        _systemFailureHandler = systemFailureHandler ?? failureHandler;
    }

    /// <summary>
    /// Maps a Core callback name to its notification kind. WPF <c>f9c14e0be1</c> keys the
    /// delivery switches by callback in the legacy shape; the kind is the durable form.
    /// </summary>
    /// <param name="callbackName">Core callback message name.</param>
    /// <returns>The mapped kind, or null when the callback never notifies.</returns>
    public static NotificationKind? KindFromCallbackName(string? callbackName) => callbackName switch
    {
        "AllTasksCompleted" => NotificationKind.TaskComplete,
        "TaskChainError" => NotificationKind.TaskError,
        "TaskStalled" => NotificationKind.Stalled,
        "ScheduledStart" => NotificationKind.ScheduledStart,
        _ => null,
    };

    /// <summary>
    /// Builds the external request list for one notification kind. Parity with the legacy
    /// <c>BuildAutomaticExternalNotificationRequests</c> helper: same channels, same parameter
    /// text, same completion/error/stall switches (including the stalled legacy key fallback).
    /// </summary>
    /// <param name="kind">Notification kind.</param>
    /// <param name="title">Notification title.</param>
    /// <param name="message">External body.</param>
    /// <param name="config">Configuration snapshot.</param>
    /// <returns>Requests to send; empty when the kind is disabled or no channel is configured.</returns>
    public static IReadOnlyList<NotificationProviderTestRequest> BuildExternalRequests(
        NotificationKind kind,
        string title,
        string message,
        UnifiedConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var policy = ExternalNotificationDeliveryPolicy.FromConfig(config);
        if (!IsKindEnabled(policy, kind))
        {
            return [];
        }

        var channels = ExternalNotificationChannels.ResolveEnabled(config);
        if (channels.Count == 0)
        {
            return [];
        }

        return channels
            .Select(channel => new NotificationProviderTestRequest(
                channel.Provider,
                ExternalNotificationChannels.BuildParameterText(channel),
                title,
                message))
            .ToList();
    }

    /// <inheritdoc />
    public void Notify(NotificationSource source, NotificationMessage message, NotificationDispatchOptions options)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(options);

        var title = string.IsNullOrWhiteSpace(message.Title) ? message.Content : message.Title;
        var content = message.Content ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        if (!TryRegisterDedupe(source, options.DedupeKey))
        {
            return;
        }

        var timestamp = options.Timestamp ?? DateTimeOffset.Now;
        var logContent = string.IsNullOrWhiteSpace(options.LogContent) ? content : options.LogContent!;

        if (options.RecordHistory && !string.IsNullOrWhiteSpace(logContent))
        {
            _history.Add(new NotificationLogRecord(timestamp, options.Color, logContent));
        }

        if (options.ShowSystemNotification)
        {
            QueueSystemNotification(title, content, options.Scope, options.Reason);
        }

        if (options.SendExternal)
        {
            DispatchExternal(source, message.Kind, title, options.ExternalMessage ?? content, logContent);
        }

        options.Display?.Invoke();
    }

    /// <inheritdoc />
    public void PublishLog(
        NotificationSource source,
        string? content,
        string color,
        DateTimeOffset timestamp,
        Action? display = null)
    {
        if (!string.IsNullOrWhiteSpace(content))
        {
            _history.Add(new NotificationLogRecord(timestamp, color, content));

            var policy = ExternalNotificationDeliveryPolicy.FromConfig(_configService.CurrentConfig);
            if (!policy.SendAfterLogCount && !policy.SendWhenContentMatches)
            {
                // Neither log trigger is enabled: keep the counter clean, exactly like the
                // pre-unification path.
                _trigger.Reset();
            }
            else
            {
                var channels = ExternalNotificationChannels.ResolveEnabled(_configService.CurrentConfig);
                if (_trigger.ShouldSend(policy, content, null, channels.Count > 0))
                {
                    _trigger.Reset();
                    var body = _history.Bundle(content, policy, timestamp, skipNewest: 1);
                    var requests = channels
                        .Select(channel => new NotificationProviderTestRequest(
                            channel.Provider,
                            ExternalNotificationChannels.BuildParameterText(channel),
                            content,
                            body))
                        .ToList();
                    Dispatch(requests, "TaskQueue.Notification.LogTrigger");
                }
            }
        }

        display?.Invoke();
    }

    /// <inheritdoc />
    public string BundleHistory(string body, DateTimeOffset timestamp, int skipNewest = 0)
    {
        var policy = ExternalNotificationDeliveryPolicy.FromConfig(_configService.CurrentConfig);
        return _history.Bundle(body ?? string.Empty, policy, timestamp, skipNewest);
    }

    /// <inheritdoc />
    public void ClearHistory() => _history.Clear();

    /// <inheritdoc />
    public void ClearDedupe()
    {
        lock (_dedupeGate)
        {
            _dedupeKeys.Clear();
            _dedupeOrder.Clear();
        }
    }

    private static bool IsKindEnabled(ExternalNotificationDeliveryPolicy policy, NotificationKind kind) => kind switch
    {
        NotificationKind.TaskComplete => policy.SendWhenComplete,
        NotificationKind.TaskError => policy.SendWhenError,
        NotificationKind.Stalled => policy.SendWhenStalled,
        NotificationKind.ScheduledStart => policy.SendBeforeScheduledStart,
        _ => false,
    };

    private static ExternalNotificationKind ToExternalKind(NotificationKind kind) => kind switch
    {
        NotificationKind.TaskComplete => ExternalNotificationKind.TaskComplete,
        NotificationKind.TaskError => ExternalNotificationKind.TaskError,
        NotificationKind.Stalled => ExternalNotificationKind.Stalled,
        NotificationKind.ScheduledStart => ExternalNotificationKind.TaskComplete,
        _ => ExternalNotificationKind.TaskComplete,
    };

    private bool TryRegisterDedupe(NotificationSource source, string? dedupeKey)
    {
        if (string.IsNullOrWhiteSpace(dedupeKey))
        {
            return true;
        }

        var key = $"{source}:{dedupeKey}";
        lock (_dedupeGate)
        {
            if (!_dedupeKeys.Add(key))
            {
                return false;
            }

            _dedupeOrder.Enqueue(key);
            while (_dedupeOrder.Count > MaxDedupeKeys)
            {
                _dedupeKeys.Remove(_dedupeOrder.Dequeue());
            }

            return true;
        }
    }

    private void DispatchExternal(
        NotificationSource source,
        NotificationKind kind,
        string title,
        string body,
        string logContent)
    {
        var config = _configService.CurrentConfig;
        var policy = ExternalNotificationDeliveryPolicy.FromConfig(config);
        var channels = ExternalNotificationChannels.ResolveEnabled(config);

        // One publication is one trigger evaluation: a kind-selected delivery and a log-count
        // delivery for the same event must not produce two messages (WPF NotificationService
        // ProcessLog sends once, then the display callback does not republish).
        if (!_trigger.ShouldSend(policy, logContent, ToExternalKind(kind), channels.Count > 0))
        {
            return;
        }

        _trigger.Reset();
        var requests = channels
            .Select(channel => new NotificationProviderTestRequest(
                channel.Provider,
                ExternalNotificationChannels.BuildParameterText(channel),
                title,
                body))
            .ToList();
        var scope = source == NotificationSource.Copilot ? "Copilot.Notification" : "TaskQueue.Notification";
        Dispatch(requests, scope);
    }

    private void QueueSystemNotification(string title, string message, string scope, string reason)
    {
        var useSystemNotification = NotificationConfigReader.ReadBool(
            _configService.CurrentConfig,
            ConfigurationKeys.UseNotify,
            true);
        _ = Task.Run(async () =>
        {
            try
            {
                await _platformCapabilityService.SendSystemNotificationAsync(
                    new SystemNotificationRequest(
                        title,
                        message,
                        [],
                        UseSystemNotification: useSystemNotification));
            }
            catch (Exception ex)
            {
                await ReportFailureAsync(
                    _systemFailureHandler,
                    scope,
                    $"Failed to dispatch automatic system notification for {reason}.",
                    ex);
            }
        });
    }

    private void Dispatch(IReadOnlyList<NotificationProviderTestRequest> requests, string scope)
    {
        if (requests.Count == 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            foreach (var request in requests)
            {
                try
                {
                    var result = await _notificationProviderService.SendTestAsync(request);
                    if (!result.Success)
                    {
                        await ReportFailureAsync(
                            _failureHandler,
                            scope,
                            $"External notification via {request.Provider} failed: {result.Message}",
                            null);
                    }
                }
                catch (Exception ex)
                {
                    await ReportFailureAsync(
                        _failureHandler,
                        scope,
                        $"External notification via {request.Provider} failed.",
                        ex);
                }
            }
        });
    }

    private static async Task ReportFailureAsync(
        Func<string, string, Exception?, Task>? handler,
        string scope,
        string message,
        Exception? exception)
    {
        if (handler is null)
        {
            return;
        }

        try
        {
            await handler(scope, message, exception);
        }
        catch
        {
            // A diagnostics failure must never take down the notification pump.
        }
    }
}
