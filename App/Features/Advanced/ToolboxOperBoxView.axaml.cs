using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MAAUnified.App.Controls;
using MAAUnified.App.ViewModels.Toolbox;

namespace MAAUnified.App.Features.Advanced;

public partial class ToolboxOperBoxView : UserControl
{
    private readonly AppSlidingSegmentController _modeSlider;

    public ToolboxOperBoxView()
    {
        InitializeComponent();
        _modeSlider = new AppSlidingSegmentController(
            OperBoxModeTrack,
            OperBoxModeSelectionSlider,
            () => VM?.IsOperBoxHaveSelected == true ? OperBoxHaveButton : OperBoxNotHaveButton);
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        DataContextChanged += OnDataContextChanged;
        OperBoxModeTrack.SizeChanged += OnModeSliderLayoutMetricChanged;
        OperBoxNotHaveButton.SizeChanged += OnModeSliderLayoutMetricChanged;
        OperBoxHaveButton.SizeChanged += OnModeSliderLayoutMetricChanged;
    }

    private ToolboxPageViewModel? VM => DataContext as ToolboxPageViewModel;

    private void OnSelectOperBoxNotHaveClick(object? sender, RoutedEventArgs e)
    {
        if (VM is not null)
        {
            VM.OperBoxSelectedIndex = 0;
            _modeSlider.QueueSync(animate: true);
        }
    }

    private void OnSelectOperBoxHaveClick(object? sender, RoutedEventArgs e)
    {
        if (VM is not null)
        {
            VM.OperBoxSelectedIndex = 1;
            _modeSlider.QueueSync(animate: true);
        }
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _modeSlider.QueueSync();
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _modeSlider.Hide();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        _modeSlider.QueueSync();
    }

    private void OnModeSliderLayoutMetricChanged(object? sender, SizeChangedEventArgs e)
    {
        _modeSlider.QueueSync(resetMetrics: false);
    }

    private async void OnOperBoxStartClick(object? sender, RoutedEventArgs e)
    {
        if (VM is not null)
        {
            await VM.StartOperBoxAsync();
        }
    }

    private async void OnOperBoxExportClick(object? sender, RoutedEventArgs e)
    {
        if (VM is null)
        {
            return;
        }

        await CopyTextAsync(VM.OperBoxExportText);
        VM.NotifyOperBoxExportCopied();
    }

    private async Task CopyTextAsync(string text)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard is null || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        await topLevel.Clipboard.SetTextAsync(text);
    }
}
