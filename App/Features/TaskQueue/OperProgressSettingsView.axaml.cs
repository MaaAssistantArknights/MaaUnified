using Avalonia.Controls;
using Avalonia.Interactivity;
using MAAUnified.App.ViewModels.TaskQueue;

namespace MAAUnified.App.Features.TaskQueue;

public partial class OperProgressSettingsView : UserControl
{
    public OperProgressSettingsView()
    {
        InitializeComponent();
    }

    private OperProgressModuleViewModel? VM => DataContext as OperProgressModuleViewModel;

    private void OnAddPlanClick(object? sender, RoutedEventArgs e)
    {
        VM?.AddPlan();
    }

    private void OnRemovePlanClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is OperProgressModuleViewModel.OperProgressPlanRow row)
        {
            VM?.RemovePlan(row);
        }
    }

    private void OnMovePlanUpClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is OperProgressModuleViewModel.OperProgressPlanRow row)
        {
            VM?.MovePlan(row, -1);
        }
    }

    private void OnMovePlanDownClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is OperProgressModuleViewModel.OperProgressPlanRow row)
        {
            VM?.MovePlan(row, 1);
        }
    }
}
