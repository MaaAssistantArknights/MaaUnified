using Avalonia.Threading;
using MAAUnified.App.ViewModels.Infrastructure;

namespace MAAUnified.App.ViewModels;

public sealed class AchievementToastItemViewModel : ObservableObject, IDisposable
{
    private const double CloseCountdownSeconds = 7d;
    private static readonly TimeSpan InitialPointerPauseSuppressionWindow = TimeSpan.FromMilliseconds(500);
    private readonly Action<string>? _dismissCallback;
    private readonly object _closeCountdownGate = new();
    private CancellationTokenSource? _closeCountdownCts;
    private DateTimeOffset? _shownAtUtc;
    private DateTimeOffset _lastCloseCountdownTickUtc;
    private double _remainingCloseCountdownSeconds = CloseCountdownSeconds;
    private double _closeCountdownProgress;
    private bool _isCloseCountdownPaused;
    private bool _isCloseCountdownStarted;
    private bool _isAnimationHidden = true;
    private bool _isDismissAnimationActive;
    private bool _isDisposed;

    public AchievementToastItemViewModel(
        string id,
        string celebrateText,
        string title,
        string description,
        string medalColor,
        bool autoClose,
        DateTimeOffset unlockedAtUtc,
        Action<string>? dismissCallback = null)
    {
        Id = id;
        CelebrateText = celebrateText;
        Title = title;
        Description = description;
        MedalColor = medalColor;
        AutoClose = autoClose;
        UnlockedAtUtc = unlockedAtUtc;
        _dismissCallback = dismissCallback;
        _closeCountdownProgress = AutoClose ? 1d : 0d;
    }

    public string Id { get; }

    public string CelebrateText { get; }

    public string Title { get; }

    public string Description { get; }

    public string MedalColor { get; }

    public bool AutoClose { get; }

    public DateTimeOffset UnlockedAtUtc { get; }

    public bool IsCloseCountdownVisible => AutoClose;

    internal bool IsCloseCountdownPaused
    {
        get
        {
            lock (_closeCountdownGate)
            {
                return _isCloseCountdownPaused;
            }
        }
    }

    public bool IsAnimationHidden
    {
        get => _isAnimationHidden;
        private set => SetProperty(ref _isAnimationHidden, value);
    }

    public bool IsDismissAnimationActive
    {
        get => _isDismissAnimationActive;
        private set => SetProperty(ref _isDismissAnimationActive, value);
    }

    public double CloseCountdownProgress
    {
        get => _closeCountdownProgress;
        private set => SetProperty(ref _closeCountdownProgress, value);
    }

    public void StartPresentation()
    {
        bool shouldShow;
        lock (_closeCountdownGate)
        {
            shouldShow = !IsDismissAnimationActive && !_isDisposed;
            if (shouldShow)
            {
                _shownAtUtc = DateTimeOffset.UtcNow;
            }
        }

        if (!shouldShow)
        {
            return;
        }

        IsAnimationHidden = false;
        StartCloseCountdown();
    }

    public void PauseCloseCountdown()
    {
        DateTimeOffset? shownAt;
        lock (_closeCountdownGate)
        {
            if (!AutoClose || _isDisposed)
            {
                return;
            }

            shownAt = _shownAtUtc;
        }

        if (!shownAt.HasValue ||
            DateTimeOffset.UtcNow - shownAt.Value < InitialPointerPauseSuppressionWindow)
        {
            return;
        }

        lock (_closeCountdownGate)
        {
            if (!_isDisposed)
            {
                _isCloseCountdownPaused = true;
            }
        }
    }

    public void ResumeCloseCountdown()
    {
        lock (_closeCountdownGate)
        {
            if (!AutoClose || _isDisposed)
            {
                return;
            }

            _isCloseCountdownPaused = false;
            _lastCloseCountdownTickUtc = DateTimeOffset.UtcNow;
        }
    }

    public bool BeginDismissAnimation()
    {
        if (IsDismissAnimationActive)
        {
            return false;
        }

        IsDismissAnimationActive = true;
        IsAnimationHidden = true;
        Dispose();
        return true;
    }

    public void Dispose()
    {
        CancellationTokenSource? closeCountdownCts;
        lock (_closeCountdownGate)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            closeCountdownCts = _closeCountdownCts;
            _closeCountdownCts = null;
        }

        closeCountdownCts?.Cancel();
        closeCountdownCts?.Dispose();
    }

    private void StartCloseCountdown()
    {
        CancellationToken cancellationToken;
        lock (_closeCountdownGate)
        {
            if (!AutoClose || _isDisposed || _isCloseCountdownStarted)
            {
                return;
            }

            _isCloseCountdownStarted = true;
            _lastCloseCountdownTickUtc = DateTimeOffset.UtcNow;
            _closeCountdownCts = new CancellationTokenSource();
            cancellationToken = _closeCountdownCts.Token;
        }

        _ = Task.Run(() => RunCloseCountdownLoopAsync(cancellationToken));
    }

    private void UpdateCloseCountdownProgress(double progress)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            if (!_isDisposed)
            {
                CloseCountdownProgress = progress;
            }

            return;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                if (!_isDisposed)
                {
                    CloseCountdownProgress = progress;
                }
            },
            DispatcherPriority.Background);
    }

    private void DismissFromCloseCountdown()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            DismissOnUiThread();
            return;
        }

        Dispatcher.UIThread.Post(DismissOnUiThread, DispatcherPriority.Normal);
    }

    private void DismissOnUiThread()
    {
        lock (_closeCountdownGate)
        {
            if (_isDisposed)
            {
                return;
            }
        }

        if (_dismissCallback is not null)
        {
            _dismissCallback.Invoke(Id);
        }
        else
        {
            Dispose();
        }
    }

    private bool TryAdvanceCloseCountdown(out double progress)
    {
        lock (_closeCountdownGate)
        {
            progress = Math.Clamp(_remainingCloseCountdownSeconds / CloseCountdownSeconds, 0d, 1d);
            if (_isDisposed)
            {
                return false;
            }

            var now = DateTimeOffset.UtcNow;
            if (_isCloseCountdownPaused)
            {
                _lastCloseCountdownTickUtc = now;
                return false;
            }

            var elapsed = now - _lastCloseCountdownTickUtc;
            _lastCloseCountdownTickUtc = now;
            _remainingCloseCountdownSeconds = Math.Max(0d, _remainingCloseCountdownSeconds - elapsed.TotalSeconds);
            progress = Math.Clamp(_remainingCloseCountdownSeconds / CloseCountdownSeconds, 0d, 1d);
            return _remainingCloseCountdownSeconds <= 0d;
        }
    }

    private async Task RunCloseCountdownLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);

                var shouldDismiss = TryAdvanceCloseCountdown(out var progress);
                UpdateCloseCountdownProgress(progress);

                if (!shouldDismiss)
                {
                    continue;
                }

                DismissFromCloseCountdown();
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Toasts cancel the loop when they are dismissed or disposed.
        }
    }
}
