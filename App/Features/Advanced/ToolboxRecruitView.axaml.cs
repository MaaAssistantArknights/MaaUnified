using Avalonia.Controls;
using Avalonia.Interactivity;
using MAAUnified.App.ViewModels.Toolbox;

namespace MAAUnified.App.Features.Advanced;

public partial class ToolboxRecruitView : UserControl
{
    public ToolboxRecruitView()
    {
        InitializeComponent();
    }

    private ToolboxPageViewModel? VM => DataContext as ToolboxPageViewModel;

    private async void OnRecruitStartClick(object? sender, RoutedEventArgs e)
    {
        if (VM is not null)
        {
            await VM.StartRecruitAsync();
        }
    }
}
