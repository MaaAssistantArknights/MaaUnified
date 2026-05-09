using System.Collections;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Interactivity;

namespace MAAUnified.App.Controls;

public sealed class AppSplitButtonItemInvokedEventArgs(object? item) : EventArgs
{
    public object? Item { get; } = item;
}

public partial class AppSplitButton : UserControl
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<AppSplitButton, string>(nameof(Text), string.Empty);

    public static readonly StyledProperty<ICommand?> CommandProperty =
        AvaloniaProperty.Register<AppSplitButton, ICommand?>(nameof(Command));

    public static readonly StyledProperty<object?> CommandParameterProperty =
        AvaloniaProperty.Register<AppSplitButton, object?>(nameof(CommandParameter));

    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<AppSplitButton, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<IDataTemplate?> ItemTemplateProperty =
        AvaloniaProperty.Register<AppSplitButton, IDataTemplate?>(nameof(ItemTemplate));

    public static readonly StyledProperty<string> FlyoutHeaderProperty =
        AvaloniaProperty.Register<AppSplitButton, string>(nameof(FlyoutHeader), string.Empty);

    public static readonly StyledProperty<bool> IsDropDownOpenProperty =
        AvaloniaProperty.Register<AppSplitButton, bool>(
            nameof(IsDropDownOpen),
            defaultValue: false,
            defaultBindingMode: BindingMode.TwoWay);

    public AppSplitButton()
    {
        InitializeComponent();
    }

    public event EventHandler<EventArgs>? PrimaryClick;

    public event EventHandler<AppSplitButtonItemInvokedEventArgs>? SecondaryItemInvoked;

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public ICommand? Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public IDataTemplate? ItemTemplate
    {
        get => GetValue(ItemTemplateProperty);
        set => SetValue(ItemTemplateProperty, value);
    }

    public string FlyoutHeader
    {
        get => GetValue(FlyoutHeaderProperty);
        set => SetValue(FlyoutHeaderProperty, value);
    }

    public bool IsDropDownOpen
    {
        get => GetValue(IsDropDownOpenProperty);
        set => SetValue(IsDropDownOpenProperty, value);
    }

    private void OnPrimaryButtonClick(object? sender, RoutedEventArgs e)
    {
        PrimaryClick?.Invoke(this, EventArgs.Empty);
        if (Command?.CanExecute(CommandParameter) == true)
        {
            Command.Execute(CommandParameter);
        }
    }

    private void OnToggleButtonClick(object? sender, RoutedEventArgs e)
    {
        IsDropDownOpen = !IsDropDownOpen;
    }

    private void OnPopupClosed(object? sender, EventArgs e)
    {
        IsDropDownOpen = false;
    }

    private void OnSecondaryItemsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SecondaryItemsList.SelectedItem is not { } item)
        {
            return;
        }

        SecondaryItemInvoked?.Invoke(this, new AppSplitButtonItemInvokedEventArgs(item));
        IsDropDownOpen = false;
        SecondaryItemsList.SelectedItem = null;
    }
}
