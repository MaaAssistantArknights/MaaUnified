using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using MAAUnified.App.ViewModels.Toolbox;

namespace MAAUnified.App.Features.Advanced;

public partial class ToolboxGachaView : UserControl
{
    private const double GachaFpsBadgeInset = 16d;

    private readonly DispatcherTimer _gachaEmphasisTimer;
    private double _gachaEmphasisPhase;
    private TranslateTransform? _gachaGradientTransform;
    private ToolboxPageViewModel? _observedViewModel;

    public ToolboxGachaView()
    {
        InitializeComponent();

        _gachaEmphasisTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(90),
        };
        _gachaEmphasisTimer.Tick += (_, _) => AnimateGachaDisclaimerText();

        AttachedToVisualTree += (_, _) =>
        {
            _gachaEmphasisTimer.Start();
            BindViewModelNotifications();
            QueueGachaFpsBadgePlacement();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _gachaEmphasisTimer.Stop();
            UnbindViewModelNotifications();
        };
        DataContextChanged += (_, _) =>
        {
            BindViewModelNotifications();
            QueueGachaFpsBadgePlacement();
        };
        GachaPreviewArea.SizeChanged += (_, _) => QueueGachaFpsBadgePlacement();
        GachaPreviewImage.SizeChanged += (_, _) => QueueGachaFpsBadgePlacement();
        GachaFpsBadge.SizeChanged += (_, _) => QueueGachaFpsBadgePlacement();
    }

    private ToolboxPageViewModel? VM => DataContext as ToolboxPageViewModel;

    private async void OnGachaAgreeDisclaimerClick(object? sender, RoutedEventArgs e)
    {
        if (VM is null)
        {
            return;
        }

        await VM.ConfirmGachaDisclaimerAsync();
    }

    private async void OnGachaOnceClick(object? sender, RoutedEventArgs e)
    {
        if (VM is not null)
        {
            await VM.StartGachaAsync(once: true);
        }
    }

    private async void OnGachaTenTimesClick(object? sender, RoutedEventArgs e)
    {
        if (VM is not null)
        {
            await VM.StartGachaAsync(once: false);
        }
    }

    private async void OnGachaPeepClick(object? sender, RoutedEventArgs e)
    {
        if (VM is null)
        {
            return;
        }

        if (VM.IsGachaInProgress)
        {
            await VM.StopActiveToolAsync();
            return;
        }

        await VM.TogglePeepAsync();
    }

    private void BindViewModelNotifications()
    {
        if (ReferenceEquals(_observedViewModel, VM))
        {
            return;
        }

        UnbindViewModelNotifications();
        _observedViewModel = VM;
        if (_observedViewModel is not null)
        {
            _observedViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void UnbindViewModelNotifications()
    {
        if (_observedViewModel is null)
        {
            return;
        }

        _observedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _observedViewModel = null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName)
            || string.Equals(e.PropertyName, nameof(ToolboxPageViewModel.PeepImage), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(ToolboxPageViewModel.ShowGachaPreview), StringComparison.Ordinal))
        {
            QueueGachaFpsBadgePlacement();
        }
    }

    private void QueueGachaFpsBadgePlacement()
    {
        Dispatcher.UIThread.Post(UpdateGachaFpsBadgePlacement, DispatcherPriority.Loaded);
    }

    private void UpdateGachaFpsBadgePlacement()
    {
        var image = VM?.PeepImage;
        var sourceWidth = image?.PixelSize.Width ?? 0;
        var sourceHeight = image?.PixelSize.Height ?? 0;
        var slot = GachaPreviewImage.Bounds.Size;
        if (sourceWidth <= 0 || sourceHeight <= 0 || slot.Width <= 0 || slot.Height <= 0)
        {
            GachaFpsBadge.Margin = new Thickness(GachaFpsBadgeInset);
            return;
        }

        var scale = Math.Min(slot.Width / sourceWidth, slot.Height / sourceHeight);
        var renderedWidth = sourceWidth * scale;
        var renderedHeight = sourceHeight * scale;
        var renderedLeft = Math.Max(0d, (slot.Width - renderedWidth) / 2d);
        var renderedTop = Math.Max(0d, (slot.Height - renderedHeight) / 2d);

        GachaFpsBadge.Margin = new Thickness(
            renderedLeft + GachaFpsBadgeInset,
            renderedTop + GachaFpsBadgeInset,
            0,
            0);
    }

    private void AnimateGachaDisclaimerText()
    {
        if (GachaDisclaimerEmphasisText is null)
        {
            return;
        }

        _gachaEmphasisPhase += 0.16d;
        if (GachaDisclaimerEmphasisText.Foreground is LinearGradientBrush gradientBrush)
        {
            gradientBrush.SpreadMethod = GradientSpreadMethod.Repeat;
            _gachaGradientTransform ??= gradientBrush.Transform as TranslateTransform ?? new TranslateTransform();
            if (!ReferenceEquals(gradientBrush.Transform, _gachaGradientTransform))
            {
                gradientBrush.Transform = _gachaGradientTransform;
            }

            _gachaGradientTransform.X = (_gachaEmphasisPhase * 24d) % 280d;
        }

        GachaDisclaimerEmphasisText.Opacity = 0.96d + (Math.Sin(_gachaEmphasisPhase * 0.75d) * 0.04d);
    }
}
