using Avalonia.Controls;
using Avalonia.Interactivity;
using MAAUnified.App.ViewModels.TaskQueue;

namespace MAAUnified.App.Features.TaskQueue;

public partial class FightSettingsView : UserControl
{
    public FightSettingsView()
    {
        InitializeComponent();
    }

    private FightTaskModuleViewModel? ViewModel => DataContext as FightTaskModuleViewModel;

    private void OnAddStagePlanEntryClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.AddStagePlanEntry();
    }

    private void OnRemoveStagePlanEntryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: FightTaskModuleViewModel.StagePlanEntry entry })
        {
            ViewModel?.RemoveStagePlanEntry(entry);
        }
    }

    private void OnMoveStagePlanEntryUpClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: FightTaskModuleViewModel.StagePlanEntry entry })
        {
            ViewModel?.MoveStagePlanEntryUp(entry);
        }
    }

    private void OnMoveStagePlanEntryDownClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: FightTaskModuleViewModel.StagePlanEntry entry })
        {
            ViewModel?.MoveStagePlanEntryDown(entry);
        }
    }
}
