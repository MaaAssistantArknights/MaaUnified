using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace MAAUnified.App.Controls;

public class AppTextInput : TextBox
{
    public static readonly StyledProperty<bool> DismissFocusOnEnterProperty =
        AvaloniaProperty.Register<AppTextInput, bool>(nameof(DismissFocusOnEnter), true);

    protected override Type StyleKeyOverride => typeof(TextBox);

    public AppTextInput()
    {
        Classes.Set("settings-input", true);
    }

    public bool DismissFocusOnEnter
    {
        get => GetValue(DismissFocusOnEnterProperty);
        set => SetValue(DismissFocusOnEnterProperty, value);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DismissFocusOnEnter && !AcceptsReturn)
        {
            TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus();
        }

        base.OnKeyDown(e);
    }
}
