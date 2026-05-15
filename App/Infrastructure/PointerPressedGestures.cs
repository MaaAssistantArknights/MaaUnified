using Avalonia.Controls;
using Avalonia.Input;

namespace MAAUnified.App.Infrastructure;

internal static class PointerPressedGestures
{
    public static bool IsSecondaryClick(Control control, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(control);
        if (point.Properties.IsRightButtonPressed
            || point.Properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed)
        {
            return true;
        }

        return OperatingSystem.IsMacOS()
            && point.Properties.IsLeftButtonPressed
            && e.KeyModifiers.HasFlag(KeyModifiers.Control);
    }
}
