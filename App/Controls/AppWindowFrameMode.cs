namespace MAAUnified.App.Controls;

public enum AppWindowFrameMode
{
    ResizableDialog,
    CompactModal,
}

public enum AppWindowControlsPlacement
{
    PlatformDefault,
    Left,
    Right,
}

public readonly record struct AppWindowFrameHorizontalInset(double Left, double Right)
{
    public static AppWindowFrameHorizontalInset Empty { get; } = new(0d, 0d);

    public double Total => Left + Right;
}
