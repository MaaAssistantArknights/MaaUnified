using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MAAUnified.App.ViewModels;

namespace MAAUnified.App.Features.Dialogs;

internal static class DialogWindowScaling
{
    private const string WrapperMarker = "MAAUnified.DialogWindowScaling.Wrapper";

    public static void ApplyOwnerUiScale(Window dialog, Window owner)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        ArgumentNullException.ThrowIfNull(owner);

        var scale = ResolveOwnerUiScale(owner);
        if (!double.IsFinite(scale) || scale <= 0d)
        {
            scale = 1d;
        }

        ScaleWindowBounds(dialog, scale);
        ScaleRootContent(dialog, scale);
    }

    private static double ResolveOwnerUiScale(Window owner)
    {
        return owner.DataContext is MainShellViewModel shell
            ? shell.EffectiveUiScaleFactor
            : 1d;
    }

    private static void ScaleWindowBounds(Window dialog, double scale)
    {
        dialog.Width = ScaleDimension(dialog.Width, scale);
        dialog.Height = ScaleDimension(dialog.Height, scale);
        dialog.MinWidth = ScaleDimension(dialog.MinWidth, scale);
        dialog.MinHeight = ScaleDimension(dialog.MinHeight, scale);
        dialog.MaxWidth = ScaleDimension(dialog.MaxWidth, scale);
        dialog.MaxHeight = ScaleDimension(dialog.MaxHeight, scale);
    }

    private static double ScaleDimension(double value, double scale)
    {
        return double.IsFinite(value) && value > 0d ? value * scale : value;
    }

    private static void ScaleRootContent(Window dialog, double scale)
    {
        if (dialog.Content is LayoutTransformControl { Tag: WrapperMarker } wrapper)
        {
            wrapper.LayoutTransform = CreateTransform(scale);
            return;
        }

        if (Math.Abs(scale - 1d) < 0.001d || dialog.Content is not Control root)
        {
            return;
        }

        dialog.Content = null;
        dialog.Content = new LayoutTransformControl
        {
            Tag = WrapperMarker,
            LayoutTransform = CreateTransform(scale),
            Child = root,
        };
    }

    private static ITransform CreateTransform(double scale)
    {
        return new ScaleTransform(scale, scale);
    }
}
