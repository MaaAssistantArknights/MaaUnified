using Avalonia.Controls;
using Avalonia.Interactivity;
using MAAUnified.App.ViewModels.TaskQueue;

namespace MAAUnified.App.Features.Root;

public partial class TaskQueueGuideView : UserControl
{
    private TaskQueuePageViewModel? VM => DataContext as TaskQueuePageViewModel;

    public TaskQueueGuideView()
    {
        InitializeComponent();
    }

    private async void OnPreviousGuideClick(object? sender, RoutedEventArgs e)
    {
        if (VM is { } vm)
        {
            await vm.PreviousGuideStepAsync();
        }
    }

    private async void OnNextGuideClick(object? sender, RoutedEventArgs e)
    {
        if (VM is { } vm)
        {
            await vm.NextGuideStepAsync();
        }
    }

    private async void OnDoneGuideClick(object? sender, RoutedEventArgs e)
    {
        if (VM is { } vm)
        {
            await vm.CompleteGuideAsync();
        }
    }

    private void OnOpenGuideSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (VM is { } vm)
        {
            vm.OpenCurrentGuideStepTarget();
        }
    }
}
