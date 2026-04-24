using Avalonia;
using Avalonia.Controls;

namespace MAAUnified.App.Controls;

public class AppSelectionList : ListBox
{
    public static readonly StyledProperty<AppSelectionListVisualMode> VisualModeProperty =
        AvaloniaProperty.Register<AppSelectionList, AppSelectionListVisualMode>(
            nameof(VisualMode),
            AppSelectionListVisualMode.Surface);

    public static readonly StyledProperty<bool> ReserveTrailingAccessorySpaceProperty =
        AvaloniaProperty.Register<AppSelectionList, bool>(
            nameof(ReserveTrailingAccessorySpace));

    private const string RailClassName = "selection-list-rail";
    private const string SurfaceClassName = "selection-list-surface";
    private const string NoneClassName = "selection-list-none";
    private const string RailTrailingAccessorySpaceClassName = "selection-list-rail-trailing-accessory-space";

    public AppSelectionList()
    {
        UpdateVisualModeClasses(VisualMode);
        UpdateTrailingAccessorySpaceClass(ReserveTrailingAccessorySpace);
    }

    public AppSelectionListVisualMode VisualMode
    {
        get => GetValue(VisualModeProperty);
        set => SetValue(VisualModeProperty, value);
    }

    public bool ReserveTrailingAccessorySpace
    {
        get => GetValue(ReserveTrailingAccessorySpaceProperty);
        set => SetValue(ReserveTrailingAccessorySpaceProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == VisualModeProperty)
        {
            UpdateVisualModeClasses(change.GetNewValue<AppSelectionListVisualMode>());
        }
        else if (change.Property == ReserveTrailingAccessorySpaceProperty)
        {
            UpdateTrailingAccessorySpaceClass(change.GetNewValue<bool>());
        }
    }

    private void UpdateVisualModeClasses(AppSelectionListVisualMode mode)
    {
        SetClass(RailClassName, mode == AppSelectionListVisualMode.Rail);
        SetClass(SurfaceClassName, mode == AppSelectionListVisualMode.Surface);
        SetClass(NoneClassName, mode == AppSelectionListVisualMode.None);
    }

    private void UpdateTrailingAccessorySpaceClass(bool enabled)
    {
        SetClass(RailTrailingAccessorySpaceClassName, enabled);
    }

    private void SetClass(string className, bool enabled)
    {
        if (enabled)
        {
            if (!Classes.Contains(className))
            {
                Classes.Add(className);
            }

            return;
        }

        Classes.Remove(className);
    }
}
