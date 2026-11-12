using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace MAAUnified.Application.Services.Notifications;

/// <summary>
/// One retained log line available for external notification packaging.
/// </summary>
/// <param name="Timestamp">Local time the line was appended.</param>
/// <param name="Color">WPF log brush key used by the notification renderer.</param>
/// <param name="Content">Log text.</param>
public sealed record NotificationLogRecord(DateTimeOffset Timestamp, string Color, string Content);

/// <summary>
/// Bounded history used to attach earlier log lines to an external notification. WPF
/// f9c14e0be1 (NotificationHistory) keeps this separate from the displayed log collection, takes
/// the newest lines first and stops on the entry count, the time window or the blacklist filter.
/// </summary>
public sealed class NotificationLogHistory
{
    public const int Capacity = 10000;

    private static readonly TimeSpan FilterBudget = TimeSpan.FromMilliseconds(100);

    private readonly object _gate = new();
    private readonly Queue<NotificationLogRecord> _records = new();
    private readonly NotificationLogFilter _blacklist = new();

    public void Add(NotificationLogRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        lock (_gate)
        {
            _records.Enqueue(record);
            while (_records.Count > Capacity)
            {
                _records.Dequeue();
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _records.Clear();
        }
    }

    /// <summary>
    /// Prepends the retained log lines to <paramref name="body"/>. The notification body stays at
    /// the end, which is what makes tail-preserving truncation (Telegram) keep the useful part.
    /// </summary>
    /// <param name="body">Current notification body.</param>
    /// <param name="policy">Delivery policy controlling the attachment.</param>
    /// <param name="timestamp">Timestamp of the notification being sent.</param>
    /// <param name="skipNewest">
    /// Number of newest records to leave out. A log-triggered delivery already carries the
    /// triggering line as its body, so it must not be attached a second time.
    /// </param>
    /// <returns>Body with the attached log lines, or the unchanged body.</returns>
    public string Bundle(
        string body,
        ExternalNotificationDeliveryPolicy policy,
        DateTimeOffset timestamp,
        int skipNewest = 0)
    {
        ArgumentNullException.ThrowIfNull(policy);
        body ??= string.Empty;

        lock (_gate)
        {
            var maxEntries = Math.Clamp(policy.MaxEntries, 0, Capacity);
            if (!policy.IncludePreviousLogs || maxEntries == 0)
            {
                return body;
            }

            var cutoff = policy.TimeMinutes == 0
                ? DateTimeOffset.MinValue
                : timestamp.AddMinutes(-policy.TimeMinutes);

            // Scan newest to oldest. Only unexpired lines count toward the limit; the current
            // body is always kept (WPF f9c14e0be1).
            var snapshot = _records.ToArray();
            var previous = new List<NotificationLogRecord>(maxEntries);
            var started = Stopwatch.GetTimestamp();
            for (var index = snapshot.Length - 1 - Math.Max(0, skipNewest); index >= 0; index--)
            {
                var record = snapshot[index];
                if (record.Timestamp < cutoff)
                {
                    continue;
                }

                if (policy.FilterPreviousLogs)
                {
                    if (Stopwatch.GetElapsedTime(started) > FilterBudget)
                    {
                        return body;
                    }

                    var matches = _blacklist.Matches(policy.Blacklist, record.Content);
                    if (matches is null)
                    {
                        return body;
                    }

                    if (matches.Value)
                    {
                        continue;
                    }
                }

                previous.Add(record);
                if (previous.Count == maxEntries)
                {
                    break;
                }
            }

            if (previous.Count == 0)
            {
                return body;
            }

            previous.Reverse();
            var builder = new StringBuilder();
            foreach (var record in previous)
            {
                builder.Append('[')
                    .Append(record.Timestamp.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture))
                    .Append("][")
                    .Append(record.Color)
                    .Append(']')
                    .Append(record.Content)
                    .Append('\n');
            }

            builder.Append(body);
            return builder.ToString();
        }
    }
}

/// <summary>
/// Regular-expression matcher for the notification whitelist/blacklist. WPF f9c14e0be1 caches the
/// compiled patterns and treats an invalid or timed-out expression as "unknown" (null) so the
/// caller can fall back to the unfiltered body instead of silently dropping lines.
/// </summary>
public sealed class NotificationLogFilter
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(50);

    private string? _patternText;
    private Regex[] _patterns = [];
    private bool _valid = true;

    public bool? Matches(string patterns, string content)
    {
        if (!UpdatePatterns(patterns))
        {
            return null;
        }

        try
        {
            var started = Stopwatch.GetTimestamp();
            foreach (var pattern in _patterns)
            {
                if (Stopwatch.GetElapsedTime(started) > MatchTimeout)
                {
                    return null;
                }

                if (pattern.IsMatch(content))
                {
                    return true;
                }
            }

            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private bool UpdatePatterns(string patterns)
    {
        if (string.Equals(_patternText, patterns, StringComparison.Ordinal))
        {
            return _valid;
        }

        _patternText = patterns;
        try
        {
            _patterns = Parse(patterns);
            _valid = true;
        }
        catch (ArgumentException)
        {
            _patterns = [];
            _valid = false;
        }

        return _valid;
    }

    private static Regex[] Parse(string patterns) => (patterns ?? string.Empty)
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(static pattern => new Regex(pattern, RegexOptions.CultureInvariant, MatchTimeout))
        .ToArray();
}

/// <summary>
/// Delivery trigger shared by all channels. WPF f9c14e0be1 keeps one counter per delivery and
/// resets it when a delivery is dispatched.
/// </summary>
public sealed class ExternalNotificationTrigger
{
    private readonly NotificationLogFilter _whitelist = new();
    private readonly object _whitelistGate = new();
    private long _newLogs;

    /// <summary>
    /// Decides whether a log line should trigger a delivery.
    /// </summary>
    /// <param name="policy">Delivery policy.</param>
    /// <param name="content">Log line content.</param>
    /// <param name="kind">Notification kind, or null for a plain log line.</param>
    /// <param name="hasChannels">True when at least one channel is configured.</param>
    /// <returns>True when a delivery should be dispatched.</returns>
    public bool ShouldSend(
        ExternalNotificationDeliveryPolicy policy,
        string content,
        ExternalNotificationKind? kind,
        bool hasChannels)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (!policy.Enable || !hasChannels)
        {
            Reset();
            return false;
        }

        var newLogs = Interlocked.Increment(ref _newLogs);
        var kindSelected = kind switch
        {
            ExternalNotificationKind.TaskComplete => policy.SendWhenComplete,
            ExternalNotificationKind.TaskError => policy.SendWhenError,
            ExternalNotificationKind.Stalled => policy.SendWhenStalled,
            _ => false,
        };

        if (kindSelected || (policy.SendAfterLogCount && newLogs >= policy.NewLogCount))
        {
            return true;
        }

        if (!policy.SendWhenContentMatches)
        {
            return false;
        }

        lock (_whitelistGate)
        {
            return _whitelist.Matches(policy.Whitelist, content ?? string.Empty) == true;
        }
    }

    public void Reset() => Interlocked.Exchange(ref _newLogs, 0);
}

public enum ExternalNotificationKind
{
    TaskComplete = 0,
    TaskError = 1,
    Stalled = 2,
}
