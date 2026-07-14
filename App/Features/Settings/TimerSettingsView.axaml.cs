using Avalonia.Controls;
using Avalonia.Input;
using MAAUnified.App.Infrastructure;
using MAAUnified.App.ViewModels.Settings;

namespace MAAUnified.App.Features.Settings;

public partial class TimerSettingsView : UserControl
{
    public TimerSettingsView()
    {
        InitializeComponent();
    }

    private void OnTimerSlotEnabledPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control
            || control.DataContext is not TimerSlotViewModel slot
            || !PointerPressedGestures.IsSecondaryClick(control, e))
        {
            return;
        }

        e.Handled = true;
        slot.Enabled = slot.Enabled is null ? false : null;
    }
}
