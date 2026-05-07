using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;

namespace MAAUnified.App.Controls;

public partial class AppHintedCheckBox : UserControl
{
    private bool _hasTip;

    public static readonly StyledProperty<bool?> IsCheckedProperty =
        AvaloniaProperty.Register<AppHintedCheckBox, bool?>(
            nameof(IsChecked),
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<bool> IsThreeStateProperty =
        AvaloniaProperty.Register<AppHintedCheckBox, bool>(nameof(IsThreeState));

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<AppHintedCheckBox, string?>(nameof(Text));

    public static readonly StyledProperty<string?> TipProperty =
        AvaloniaProperty.Register<AppHintedCheckBox, string?>(nameof(Tip));

    public static readonly StyledProperty<bool> CheckBoxIsEnabledProperty =
        AvaloniaProperty.Register<AppHintedCheckBox, bool>(nameof(CheckBoxIsEnabled), true);

    public static readonly DirectProperty<AppHintedCheckBox, bool> HasTipProperty =
        AvaloniaProperty.RegisterDirect<AppHintedCheckBox, bool>(
            nameof(HasTip),
            checkBox => checkBox.HasTip);

    public AppHintedCheckBox()
    {
        InitializeComponent();
    }

    public bool? IsChecked
    {
        get => GetValue(IsCheckedProperty);
        set => SetValue(IsCheckedProperty, value);
    }

    public bool IsThreeState
    {
        get => GetValue(IsThreeStateProperty);
        set => SetValue(IsThreeStateProperty, value);
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

    public bool CheckBoxIsEnabled
    {
        get => GetValue(CheckBoxIsEnabledProperty);
        set => SetValue(CheckBoxIsEnabledProperty, value);
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
        }
    }
}
