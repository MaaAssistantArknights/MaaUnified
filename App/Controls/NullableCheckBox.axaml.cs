using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using MAAUnified.App.Infrastructure;

namespace MAAUnified.App.Controls;

public partial class NullableCheckBox : UserControl
{
    private bool _hasTip;

    public static readonly StyledProperty<bool?> IsCheckedProperty =
        AvaloniaProperty.Register<NullableCheckBox, bool?>(
            nameof(IsChecked),
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<NullableCheckBox, string?>(nameof(Text));

    public static readonly StyledProperty<string?> TipProperty =
        AvaloniaProperty.Register<NullableCheckBox, string?>(nameof(Tip));

    public static readonly DirectProperty<NullableCheckBox, bool> HasTipProperty =
        AvaloniaProperty.RegisterDirect<NullableCheckBox, bool>(
            nameof(HasTip),
            checkBox => checkBox.HasTip);

    public NullableCheckBox()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => SettingsLabelWidthCoordinator.InvalidateNearestGroup(this);
    }

    public bool? IsChecked
    {
        get => GetValue(IsCheckedProperty);
        set => SetValue(IsCheckedProperty, value);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string? Tip
    {
        get => GetValue(TipProperty);
        set => SetValue(TipProperty, value);
    }

    public bool HasTip
    {
        get => _hasTip;
        private set => SetAndRaise(HasTipProperty, ref _hasTip, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TipProperty)
        {
            HasTip = !string.IsNullOrWhiteSpace(Tip);
            SettingsLabelWidthCoordinator.InvalidateNearestGroup(this);
        }
        else if (change.Property == TextProperty)
        {
            SettingsLabelWidthCoordinator.InvalidateNearestGroup(this);
        }
    }

    private void OnCheckBoxPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control || !PointerPressedGestures.IsSecondaryClick(control, e))
        {
            return;
        }

        e.Handled = true;
        IsChecked = IsChecked is null ? false : null;
    }
}
