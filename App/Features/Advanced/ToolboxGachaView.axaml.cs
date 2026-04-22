using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using MAAUnified.App.ViewModels.Toolbox;

namespace MAAUnified.App.Features.Advanced;

public partial class ToolboxGachaView : UserControl
{
    private readonly DispatcherTimer _gachaEmphasisTimer;
    private double _gachaEmphasisPhase;
    private TranslateTransform? _gachaGradientTransform;

    public ToolboxGachaView()
    {
        InitializeComponent();

        _gachaEmphasisTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(90),
        };
        _gachaEmphasisTimer.Tick += (_, _) => AnimateGachaDisclaimerText();

        AttachedToVisualTree += (_, _) => _gachaEmphasisTimer.Start();
        DetachedFromVisualTree += (_, _) => _gachaEmphasisTimer.Stop();
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
