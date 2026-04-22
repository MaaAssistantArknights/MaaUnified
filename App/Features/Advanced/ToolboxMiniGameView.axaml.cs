using Avalonia.Controls;
using Avalonia.Interactivity;
using MAAUnified.App.ViewModels.Toolbox;

namespace MAAUnified.App.Features.Advanced;

public partial class ToolboxMiniGameView : UserControl
{
    public ToolboxMiniGameView()
    {
        InitializeComponent();
    }

    private ToolboxPageViewModel? VM => DataContext as ToolboxPageViewModel;

    private async void OnMiniGameCommandClick(object? sender, RoutedEventArgs e)
    {
        if (VM is null)
        {
            return;
        }

        if (VM.IsMiniGameRunning)
        {
            await VM.StopActiveToolAsync();
            return;
        }

        await VM.StartMiniGameAsync();
    }
}
