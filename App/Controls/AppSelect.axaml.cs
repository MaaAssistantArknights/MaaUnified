using System;
using Avalonia.Controls;

namespace MAAUnified.App.Controls;

public class AppSelect : ComboBox
{
    protected override Type StyleKeyOverride => typeof(ComboBox);

    public AppSelect()
    {
        Classes.Set("settings-select", true);
    }
}
