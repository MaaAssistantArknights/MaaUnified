using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace MAAUnified.App.Controls;

public sealed class AppWindowTitleText : TextBlock
{
    public static readonly StyledProperty<string> TitleTextProperty =
        AvaloniaProperty.Register<AppWindowTitleText, string>(nameof(TitleText), string.Empty);

    static AppWindowTitleText()
    {
        TitleTextProperty.Changed.AddClassHandler<AppWindowTitleText>((text, _) => text.RefreshInlines());
    }

    public AppWindowTitleText()
    {
        RefreshInlines();
    }

    public string TitleText
    {
        get => GetValue(TitleTextProperty);
        set => SetValue(TitleTextProperty, value);
    }

    private void RefreshInlines()
    {
        Inlines?.Clear();

        var title = TitleText ?? string.Empty;
        if (title.Length == 0)
        {
            return;
        }

        var separatorIndex = title.IndexOf(" - ", StringComparison.Ordinal);
        if (separatorIndex <= 0)
        {
            AddRun(title, FontWeight.Bold);
            return;
        }

        AddRun(title[..separatorIndex], FontWeight.Bold);
        AddRun(title[separatorIndex..], FontWeight.Medium);
    }

    private void AddRun(string text, FontWeight fontWeight)
    {
        Inlines?.Add(new Run(text)
        {
            FontWeight = fontWeight,
        });
    }
}
