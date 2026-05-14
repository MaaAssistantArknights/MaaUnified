using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MAAUnified.App.ViewModels.Toolbox;
using MAAUnified.Application.Models;

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
            if (VM.IsRecruitExecuting)
            {
                await VM.StopActiveToolAsync();
                return;
            }

            await VM.StartRecruitAsync();
        }
    }

    private void OnRecruitStartPointerEntered(object? sender, PointerEventArgs e)
    {
        VM?.SetToolActionHover(ToolboxToolKind.Recruit, hovering: true);
    }

    private void OnRecruitStartPointerExited(object? sender, PointerEventArgs e)
    {
        VM?.SetToolActionHover(ToolboxToolKind.Recruit, hovering: false);
    }
}
