using Avalonia.Threading;
using MAAUnified.App.ViewModels.Infrastructure;

namespace MAAUnified.App.ViewModels;

public sealed class AchievementToastItemViewModel : ObservableObject, IDisposable
{
    private const double CloseCountdownSeconds = 5d;
    private readonly Action<string>? _dismissCallback;
    private readonly DispatcherTimer? _closeCountdownTimer;
    private DateTimeOffset _lastCloseCountdownTickUtc;
    private double _remainingCloseCountdownSeconds = CloseCountdownSeconds;
    private double _closeCountdownProgress;
    private bool _isCloseCountdownPaused;
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

        if (AutoClose)
        {
            _closeCountdownTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50),
            };
            _closeCountdownTimer.Tick += OnCloseCountdownTick;
            _lastCloseCountdownTickUtc = DateTimeOffset.UtcNow;
            _closeCountdownTimer.Start();
        }

        Dispatcher.UIThread.Post(BeginEnterAnimation, DispatcherPriority.Loaded);
    }

    public string Id { get; }

    public string CelebrateText { get; }

    public string Title { get; }

    public string Description { get; }

    public string MedalColor { get; }

    public bool AutoClose { get; }

    public DateTimeOffset UnlockedAtUtc { get; }

    public bool IsCloseCountdownVisible => AutoClose;

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

    public void PauseCloseCountdown()
    {
        if (!AutoClose || _isDisposed)
        {
            return;
        }

        _isCloseCountdownPaused = true;
    }

    public void ResumeCloseCountdown()
    {
        if (!AutoClose || _isDisposed)
        {
            return;
        }

        _isCloseCountdownPaused = false;
        _lastCloseCountdownTickUtc = DateTimeOffset.UtcNow;
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
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        if (_closeCountdownTimer is not null)
        {
            _closeCountdownTimer.Stop();
            _closeCountdownTimer.Tick -= OnCloseCountdownTick;
        }
    }

    private void BeginEnterAnimation()
    {
        if (!IsDismissAnimationActive && !_isDisposed)
        {
            IsAnimationHidden = false;
        }
    }

    private void OnCloseCountdownTick(object? sender, EventArgs e)
    {
        if (_isCloseCountdownPaused || _isDisposed)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var elapsed = now - _lastCloseCountdownTickUtc;
        _lastCloseCountdownTickUtc = now;

        _remainingCloseCountdownSeconds = Math.Max(0d, _remainingCloseCountdownSeconds - elapsed.TotalSeconds);
        CloseCountdownProgress = Math.Clamp(_remainingCloseCountdownSeconds / CloseCountdownSeconds, 0d, 1d);

        if (_remainingCloseCountdownSeconds > 0d)
        {
            return;
        }

        if (_closeCountdownTimer is not null)
        {
            _closeCountdownTimer.Stop();
            _closeCountdownTimer.Tick -= OnCloseCountdownTick;
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
}
