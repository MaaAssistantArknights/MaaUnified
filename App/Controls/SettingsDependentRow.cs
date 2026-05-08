using Avalonia;
using Avalonia.Controls;

namespace MAAUnified.App.Controls;

public class SettingsDependentRow : ContentControl
{
    public static readonly StyledProperty<string> GlyphProperty =
        AvaloniaProperty.Register<SettingsDependentRow, string>(nameof(Glyph), "└");

    public SettingsDependentRow()
    {
        Classes.Set("settings-dependent-row", true);
    }

    public string Glyph
    {
        get => GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }
}
