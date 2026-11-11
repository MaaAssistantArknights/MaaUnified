using MAAUnified.Platform;

namespace MAAUnified.App.Views;

/// <summary>
/// Holds in-app fallback notifications while their owner window cannot present them.
/// </summary>
internal sealed class InAppNotificationPresentationQueue
{
    private const int MaximumPendingNotifications = 32;
    private readonly Queue<SystemNotificationRequest> _pending = [];

    public int PendingCount => _pending.Count;

    public void PresentOrQueue(
        SystemNotificationRequest notification,
        bool canPresent,
        Action<SystemNotificationRequest> present)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentNullException.ThrowIfNull(present);

        if (canPresent)
        {
            present(notification);
            return;
        }

        if (_pending.Count == MaximumPendingNotifications)
        {
            _pending.Dequeue();
        }

        _pending.Enqueue(notification);
    }

    public void FlushIfPossible(bool canPresent, Action<SystemNotificationRequest> present)
    {
        ArgumentNullException.ThrowIfNull(present);
        if (!canPresent)
        {
            return;
        }

        while (_pending.TryDequeue(out var notification))
        {
            present(notification);
        }
    }

    public void Clear() => _pending.Clear();
}
