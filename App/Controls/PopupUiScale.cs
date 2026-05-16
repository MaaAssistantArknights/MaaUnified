using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.VisualTree;
using MAAUnified.App.ViewModels;

namespace MAAUnified.App.Controls;

public static class PopupUiScale
{
    public static readonly AttachedProperty<bool> UseTopLevelUiScaleProperty =
        AvaloniaProperty.RegisterAttached<Popup, bool>(
            "UseTopLevelUiScale",
            typeof(PopupUiScale));

    private const string WrapperMarker = "MAAUnified.PopupUiScale.Wrapper";

    static PopupUiScale()
    {
        UseTopLevelUiScaleProperty.Changed.AddClassHandler<Popup>(OnUseTopLevelUiScaleChanged);
        Popup.ChildProperty.Changed.AddClassHandler<Popup>(OnPopupChildChanged);
    }

    public static bool GetUseTopLevelUiScale(Popup popup)
    {
        return popup.GetValue(UseTopLevelUiScaleProperty);
    }

    public static void SetUseTopLevelUiScale(Popup popup, bool value)
    {
        popup.SetValue(UseTopLevelUiScaleProperty, value);
    }

    private static void OnUseTopLevelUiScaleChanged(Popup popup, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.GetNewValue<bool>())
        {
            popup.Opened += OnPopupOpened;
            ApplyScale(popup);
            return;
        }

        popup.Opened -= OnPopupOpened;
    }

    private static void OnPopupOpened(object? sender, EventArgs e)
    {
        if (sender is Popup popup)
        {
            ApplyScale(popup);
        }
    }

    private static void OnPopupChildChanged(Popup popup, AvaloniaPropertyChangedEventArgs e)
    {
        if (!GetUseTopLevelUiScale(popup))
        {
            return;
        }

        ApplyScale(popup);
    }

    private static void ApplyScale(Popup popup)
    {
        ApplyScale(popup, ResolveTopLevelUiScale(popup));
    }

    internal static void ApplyScale(Popup popup, double scale)
    {
        if (popup.Child is null)
        {
            return;
        }

        if (!double.IsFinite(scale) || scale <= 0d)
        {
            scale = 1d;
        }

        if (popup.Child is LayoutTransformControl { Tag: WrapperMarker } wrapper)
        {
            wrapper.LayoutTransform = CreateTransform(scale);
            return;
        }

        var child = popup.Child;
        if (child.GetVisualParent() is not null)
        {
            return;
        }

        popup.Child = null;
        popup.Child = new LayoutTransformControl
        {
            Tag = WrapperMarker,
            LayoutTransform = CreateTransform(scale),
            Child = child,
        };
    }

    private static ITransform CreateTransform(double scale)
    {
        return new ScaleTransform(scale, scale);
    }

    private static double ResolveTopLevelUiScale(Popup popup)
    {
        var topLevel = popup.PlacementTarget is not null
            ? TopLevel.GetTopLevel(popup.PlacementTarget)
            : null;

        return topLevel is Window { DataContext: MainShellViewModel shell }
            ? shell.EffectiveUiScaleFactor
            : 1d;
    }
}
