using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MAAUnified.App.ViewModels.TaskQueue;

namespace MAAUnified.App.Features.Root;

public partial class TaskQueueGuideView : UserControl
{
    // WPF dev-v2 MaaUrls.UserAgreement (0f4ba4c610).
    private const string UserAgreementUrl =
        "https://github.com/MaaAssistantArknights/MaaAssistantArknights/blob/dev-v2/terms-of-service.md";

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

    private async void OnSkipGuideClick(object? sender, RoutedEventArgs e)
    {
        if (VM is { } vm)
        {
            await vm.SkipGuideAsync();
        }
    }

    private async void OnConfirmGuideAgreementClick(object? sender, RoutedEventArgs e)
    {
        if (VM is { } vm)
        {
            await vm.ConfirmGuideAgreementAsync();
        }
    }

    private void OnOpenGuideAgreementClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            _ = Process.Start(new ProcessStartInfo
            {
                FileName = UserAgreementUrl,
                UseShellExecute = true,
            });
        }
        catch
        {
            // Keep the guide responsive if the shell cannot open the agreement link.
        }
    }

    private async void OnGuideDemoActionClick(object? sender, RoutedEventArgs e)
    {
        if (VM is { } vm && sender is Button { Tag: SettingsGuideDemoAction action })
        {
            await vm.ExecuteGuideDemoActionAsync(action);
        }
    }
}
