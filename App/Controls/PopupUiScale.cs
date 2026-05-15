using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
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

    private static void ApplyScale(Popup popup)
    {
        if (popup.Child is null)
        {
            return;
        }

        var scale = ResolveTopLevelUiScale(popup);
        if (!double.IsFinite(scale) || scale <= 0d)
        {
            scale = 1d;
        }

        if (popup.Child is LayoutTransformControl { Tag: WrapperMarker } wrapper)
        {
            wrapper.LayoutTransform = CreateTransform(scale);
            return;
        }

        if (Math.Abs(scale - 1d) < 0.001d)
        {
            return;
        }

        var child = popup.Child;
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
