using Avalonia.Controls;
using Avalonia.Interactivity;
using MAAUnified.App.ViewModels.Toolbox;

namespace MAAUnified.App.Features.Advanced;

public partial class ToolboxPeepView : UserControl
{
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
}
