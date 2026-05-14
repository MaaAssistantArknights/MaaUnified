using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace MAAUnified.App.Controls;

public partial class AppActionInput : UserControl
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<AppActionInput, string>(
            nameof(Text),
            string.Empty,
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string> WatermarkProperty =
        AvaloniaProperty.Register<AppActionInput, string>(nameof(Watermark), string.Empty);

    public static readonly StyledProperty<bool> IsReadOnlyProperty =
        AvaloniaProperty.Register<AppActionInput, bool>(nameof(IsReadOnly));

    public static readonly StyledProperty<object?> ActionContentProperty =
        AvaloniaProperty.Register<AppActionInput, object?>(nameof(ActionContent));

    public static readonly StyledProperty<ICommand?> ActionCommandProperty =
        AvaloniaProperty.Register<AppActionInput, ICommand?>(nameof(ActionCommand));

    public static readonly StyledProperty<bool> IsPrimaryActionProperty =
        AvaloniaProperty.Register<AppActionInput, bool>(nameof(IsPrimaryAction));

    public static readonly StyledProperty<object?> SecondaryActionContentProperty =
        AvaloniaProperty.Register<AppActionInput, object?>(nameof(SecondaryActionContent));

    public static readonly StyledProperty<ICommand?> SecondaryActionCommandProperty =
        AvaloniaProperty.Register<AppActionInput, ICommand?>(nameof(SecondaryActionCommand));

    public AppActionInput()
    {
        InitializeComponent();
        this.GetObservable(IsPrimaryActionProperty).Subscribe(value => ActionButton.Classes.Set("primary", value));
        this.GetObservable(SecondaryActionContentProperty).Subscribe(value =>
        {
            var hasSecondaryAction = value is not null;
            SecondaryActionButton.IsVisible = hasSecondaryAction;
            ActionButton.Classes.Set("has-secondary-action", hasSecondaryAction);
        });
    }

    public event EventHandler<RoutedEventArgs>? ActionClick;

    public event EventHandler<RoutedEventArgs>? SecondaryActionClick;

    public event EventHandler<RoutedEventArgs>? EditorLostFocus;

    public event EventHandler<KeyEventArgs>? EditorKeyDown;

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string Watermark
    {
        get => GetValue(WatermarkProperty);
        set => SetValue(WatermarkProperty, value);
    }

    public bool IsReadOnly
    {
        get => GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    public object? ActionContent
    {
        get => GetValue(ActionContentProperty);
        set => SetValue(ActionContentProperty, value);
    }

    public ICommand? ActionCommand
    {
        get => GetValue(ActionCommandProperty);
        set => SetValue(ActionCommandProperty, value);
    }

    public bool IsPrimaryAction
    {
        get => GetValue(IsPrimaryActionProperty);
        set => SetValue(IsPrimaryActionProperty, value);
    }

    public object? SecondaryActionContent
    {
        get => GetValue(SecondaryActionContentProperty);
        set => SetValue(SecondaryActionContentProperty, value);
    }

    public ICommand? SecondaryActionCommand
    {
        get => GetValue(SecondaryActionCommandProperty);
        set => SetValue(SecondaryActionCommandProperty, value);
    }

    private void OnEditorGotFocus(object? sender, GotFocusEventArgs e)
    {
        ShellBorder.Classes.Set("focused", true);
    }

    private void OnEditorLostFocus(object? sender, RoutedEventArgs e)
    {
        ShellBorder.Classes.Set("focused", false);
        EditorLostFocus?.Invoke(this, e);
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        EditorKeyDown?.Invoke(this, e);
    }

    private void OnActionButtonClick(object? sender, RoutedEventArgs e)
    {
        ActionClick?.Invoke(this, e);
    }

    private void OnSecondaryActionButtonClick(object? sender, RoutedEventArgs e)
    {
        SecondaryActionClick?.Invoke(this, e);
    }
}
