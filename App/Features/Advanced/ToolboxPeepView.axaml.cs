using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using MAAUnified.App.ViewModels.Toolbox;

namespace MAAUnified.App.Features.Advanced;

public partial class ToolboxPeepView : UserControl
{
    private bool? _peepControlsDockedRight;

    public ToolboxPeepView()
    {
        InitializeComponent();
    }

    private ToolboxPageViewModel? VM => DataContext as ToolboxPageViewModel;

    private async void OnPeepCommandClick(object? sender, RoutedEventArgs e)
    {
        if (VM is null)
        {
            return;
        }

        if (VM.IsGachaInProgress)
        {
            await VM.StopActiveToolAsync();
        }
        else
        {
            await VM.TogglePeepAsync();
        }
    }

    private void OnPeepSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var size = e.NewSize;
        var dockRight = size.Width >= 760 && size.Width > size.Height * 1.28;
        if (_peepControlsDockedRight == dockRight)
        {
            return;
        }

        _peepControlsDockedRight = dockRight;
        ApplyPeepControlDock(dockRight);
    }

    private void ApplyPeepControlDock(bool dockRight)
    {
        if (dockRight)
        {
            Grid.SetColumnSpan(PeepDisplayArea, 1);
            Grid.SetRow(PeepControlPanel, 0);
            Grid.SetColumn(PeepControlPanel, 1);
            Grid.SetColumnSpan(PeepControlPanel, 1);
            PeepControlPanel.Orientation = Orientation.Vertical;
            PeepControlPanel.HorizontalAlignment = HorizontalAlignment.Right;
            PeepControlPanel.VerticalAlignment = VerticalAlignment.Bottom;
            PeepControlPanel.Margin = new Thickness(16, 0, 0, 0);
            return;
        }

        Grid.SetColumnSpan(PeepDisplayArea, 2);
        Grid.SetRow(PeepControlPanel, 1);
        Grid.SetColumn(PeepControlPanel, 0);
        Grid.SetColumnSpan(PeepControlPanel, 2);
        PeepControlPanel.Orientation = Orientation.Horizontal;
        PeepControlPanel.HorizontalAlignment = HorizontalAlignment.Center;
        PeepControlPanel.VerticalAlignment = VerticalAlignment.Bottom;
        PeepControlPanel.Margin = new Thickness(0, 14, 0, 0);
    }
}
