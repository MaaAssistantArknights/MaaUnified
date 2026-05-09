using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using MAAUnified.App.ViewModels.Toolbox;

namespace MAAUnified.App.Features.Advanced;

public partial class ToolboxPeepView : UserControl
{
    private const double PeepPreviewAspectRatio = 16d / 9d;

    private bool? _peepControlsDockedRight;

    public ToolboxPeepView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => SyncPeepControlDock();
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

    private void OnPeepLayoutSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdatePeepControlDock(e.NewSize);
    }

    private void SyncPeepControlDock()
    {
        UpdatePeepControlDock(Bounds.Size);
    }

    private void UpdatePeepControlDock(Size size)
    {
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        var dockRight = ShouldDockPeepControlsRight(size);
        if (_peepControlsDockedRight == dockRight)
        {
            return;
        }

        _peepControlsDockedRight = dockRight;
        ApplyPeepControlDock(dockRight);
    }

    private static bool ShouldDockPeepControlsRight(Size size)
    {
        return size.Width / size.Height >= PeepPreviewAspectRatio;
    }

    private void ApplyPeepControlDock(bool dockRight)
    {
        if (dockRight)
        {
            Grid.SetColumnSpan(PeepDisplayArea, 1);
            Grid.SetRow(PeepControlPanel, 0);
            Grid.SetColumn(PeepControlPanel, 1);
            Grid.SetColumnSpan(PeepControlPanel, 1);
            PeepControlPanel.RowDefinitions = new RowDefinitions("Auto,Auto");
            PeepControlPanel.ColumnDefinitions = new ColumnDefinitions("Auto");
            Grid.SetRow(PeepCommandControlGroup, 0);
            Grid.SetColumn(PeepCommandControlGroup, 0);
            Grid.SetRow(PeepFpsControlGroup, 1);
            Grid.SetColumn(PeepFpsControlGroup, 0);
            PeepFpsControlGroup.Orientation = Orientation.Vertical;
            PeepControlPanel.HorizontalAlignment = HorizontalAlignment.Right;
            PeepControlPanel.VerticalAlignment = VerticalAlignment.Center;
            PeepControlPanel.Margin = new Thickness(16, 0, 0, 0);
            PeepFpsControlGroup.Margin = new Thickness(0, 12, 0, 0);
            PeepFpsControlGroup.HorizontalAlignment = HorizontalAlignment.Center;
            return;
        }

        Grid.SetColumnSpan(PeepDisplayArea, 2);
        Grid.SetRow(PeepControlPanel, 1);
        Grid.SetColumn(PeepControlPanel, 0);
        Grid.SetColumnSpan(PeepControlPanel, 2);
        PeepControlPanel.RowDefinitions = new RowDefinitions("Auto");
        PeepControlPanel.ColumnDefinitions = new ColumnDefinitions("*,Auto,*");
        Grid.SetRow(PeepCommandControlGroup, 0);
        Grid.SetColumn(PeepCommandControlGroup, 1);
        Grid.SetRow(PeepFpsControlGroup, 0);
        Grid.SetColumn(PeepFpsControlGroup, 2);
        PeepFpsControlGroup.Orientation = Orientation.Horizontal;
        PeepControlPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
        PeepControlPanel.VerticalAlignment = VerticalAlignment.Center;
        PeepControlPanel.Margin = new Thickness(0, 14, 0, 0);
        PeepFpsControlGroup.Margin = new Thickness(24, 0, 0, 0);
        PeepFpsControlGroup.HorizontalAlignment = HorizontalAlignment.Left;
    }
}
