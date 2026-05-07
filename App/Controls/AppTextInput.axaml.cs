using System;
using Avalonia.Controls;

namespace MAAUnified.App.Controls;

public class AppTextInput : TextBox
{
    protected override Type StyleKeyOverride => typeof(TextBox);

    public AppTextInput()
    {
        Classes.Set("settings-input", true);
    }
}
