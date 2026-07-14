using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MAAUnified.App.Infrastructure;
using MAAUnified.App.ViewModels.TaskQueue;

namespace MAAUnified.App.Features.TaskQueue;

public partial class PostActionSettingsView : UserControl
{
    public PostActionSettingsView()
    {
        InitializeComponent();
    }

    private PostActionModuleViewModel? VM => DataContext as PostActionModuleViewModel;

    private void OnClearClick(object? sender, RoutedEventArgs e)
    {
        VM?.ClearActions();
    }

    private void OnPostActionTogglePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (VM is null
            || sender is not Control control
            || control.Tag is not string actionName
            || !PointerPressedGestures.IsSecondaryClick(control, e))
        {
            return;
        }

        e.Handled = true;
        VM.ToggleActionOnce(actionName);
    }

    private void OnClearPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (VM is null || sender is not Control control || !PointerPressedGestures.IsSecondaryClick(control, e))
        {
            return;
        }

        e.Handled = true;
        VM.ClearActionsOnce();
    }
}
