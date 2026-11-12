using Avalonia.Controls;
using Avalonia.Interactivity;
using MAAUnified.App.ViewModels.TaskQueue;

namespace MAAUnified.App.Features.TaskQueue;

public partial class DepotMaintainSettingsView : UserControl
{
    public DepotMaintainSettingsView()
    {
        InitializeComponent();
    }

    private DepotMaintainModuleViewModel? VM => DataContext as DepotMaintainModuleViewModel;

    private void OnAddPlanClick(object? sender, RoutedEventArgs e)
    {
        VM?.AddPlan();
    }

    private void OnRemovePlanClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is DepotMaintainModuleViewModel.DepotMaintainPlanRow row)
        {
            VM?.RemovePlan(row);
        }
    }

    private void OnMovePlanUpClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is DepotMaintainModuleViewModel.DepotMaintainPlanRow row)
        {
            VM?.MovePlan(row, -1);
        }
    }

    private void OnMovePlanDownClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is DepotMaintainModuleViewModel.DepotMaintainPlanRow row)
        {
            VM?.MovePlan(row, 1);
        }
    }
}
