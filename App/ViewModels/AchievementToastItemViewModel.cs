using System.Globalization;
using Avalonia.Threading;
using MAAUnified.App.ViewModels.Infrastructure;

namespace MAAUnified.App.ViewModels;

public sealed class AchievementToastItemViewModel : ObservableObject, IDisposable
{
    private const double CloseCountdownSeconds = 5d;
    private const double CloseCountdownCircumference = 106.814d;
    private readonly Action<string>? _dismissCallback;
    private readonly DispatcherTimer? _closeCountdownTimer;
    private DateTimeOffset _lastCloseCountdownTickUtc;
    private double _remainingCloseCountdownSeconds = CloseCountdownSeconds;
    private string _closeCountdownStrokeDashArray = BuildCloseCountdownStrokeDashArray(1d);
    private bool _isCloseCountdownPaused;
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
    }

    public string Id { get; }

    public string CelebrateText { get; }

    public string Title { get; }

    public string Description { get; }

    public string MedalColor { get; }

    public bool AutoClose { get; }

    public DateTimeOffset UnlockedAtUtc { get; }

    public bool IsCloseCountdownVisible => AutoClose;

    public string CloseCountdownStrokeDashArray
    {
        get => _closeCountdownStrokeDashArray;
        private set => SetProperty(ref _closeCountdownStrokeDashArray, value);
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
        CloseCountdownStrokeDashArray = BuildCloseCountdownStrokeDashArray(_remainingCloseCountdownSeconds / CloseCountdownSeconds);

        if (_remainingCloseCountdownSeconds > 0d)
        {
            return;
        }

        Dispose();
        _dismissCallback?.Invoke(Id);
    }

    private static string BuildCloseCountdownStrokeDashArray(double progress)
    {
        var normalizedProgress = Math.Clamp(progress, 0d, 1d);
        var dashLength = CloseCountdownCircumference * normalizedProgress;
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:0.###} {1:0.###}",
            dashLength,
            CloseCountdownCircumference);
    }
}
