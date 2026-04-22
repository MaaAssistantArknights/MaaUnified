using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;

namespace MAAUnified.App.Controls;

public partial class DelimitedTextListEditor : UserControl
{
    public static readonly StyledProperty<string> HeaderTextProperty =
        AvaloniaProperty.Register<DelimitedTextListEditor, string>(nameof(HeaderText), string.Empty);

    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<DelimitedTextListEditor, string>(
            nameof(Text),
            string.Empty,
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string> DelimiterProperty =
        AvaloniaProperty.Register<DelimitedTextListEditor, string>(nameof(Delimiter), ",");

    public static readonly StyledProperty<string> WatermarkProperty =
        AvaloniaProperty.Register<DelimitedTextListEditor, string>(nameof(Watermark), string.Empty);

    public static readonly StyledProperty<bool> IsReadOnlyProperty =
        AvaloniaProperty.Register<DelimitedTextListEditor, bool>(nameof(IsReadOnly), false);

    public DelimitedTextListEditor()
    {
        InitializeComponent();
        this.GetObservable(TextProperty).Subscribe(_ => RefreshParsedItems());
        this.GetObservable(DelimiterProperty).Subscribe(_ => RefreshParsedItems());
        RefreshParsedItems();
    }

    public ObservableCollection<string> ParsedItems { get; } = new();

    public string HeaderText
    {
        get => GetValue(HeaderTextProperty);
        set => SetValue(HeaderTextProperty, value);
    }

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string Delimiter
    {
        get => GetValue(DelimiterProperty);
        set => SetValue(DelimiterProperty, value);
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

    public void SetItems(IEnumerable<string>? items)
    {
        var normalized = items?
            .Select(static item => item?.Trim() ?? string.Empty)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .ToArray()
            ?? [];
        SetCurrentValue(TextProperty, string.Join(GetEffectiveDelimiter(), normalized));
    }

    private void RefreshParsedItems()
    {
        ParsedItems.Clear();
        foreach (var item in SplitItems(Text, GetEffectiveDelimiter()))
        {
            ParsedItems.Add(item);
        }
    }

    private string GetEffectiveDelimiter()
    {
        return string.IsNullOrWhiteSpace(Delimiter) ? "," : Delimiter;
    }

    private static IEnumerable<string> SplitItems(string? text, string delimiter)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return text.Split([delimiter], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static item => !string.IsNullOrWhiteSpace(item));
    }
}
