namespace MAAUnified.App.Controls;

public sealed record AppStickyTitleState(
    bool IsVisible,
    double Height,
    string CurrentTitle,
    double CurrentTranslateY,
    string? IncomingTitle,
    double IncomingTranslateY,
    bool ShowIncomingTitle)
{
    public static AppStickyTitleState Hidden { get; } = new(
        IsVisible: false,
        Height: 0d,
        CurrentTitle: string.Empty,
        CurrentTranslateY: 0d,
        IncomingTitle: null,
        IncomingTranslateY: 0d,
        ShowIncomingTitle: false);
}
