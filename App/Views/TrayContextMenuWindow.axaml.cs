using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MAAUnified.App.Infrastructure;
using MAAUnified.Platform;

namespace MAAUnified.App.Views;

public abstract record TrayContextMenuEntry;

public sealed record TrayContextMenuItemEntry(
    string Header,
    TrayCommandId Command,
    bool IsEnabled) : TrayContextMenuEntry;

public sealed record TrayContextMenuSeparatorEntry() : TrayContextMenuEntry;

public sealed class TrayContextMenuCommandInvokedEventArgs(TrayCommandId command) : EventArgs
{
    public TrayCommandId Command { get; } = command;
}

public partial class TrayContextMenuWindow : Window
{
    private const int PopupMargin = 8;
    private PixelPoint _anchorPosition;

    public TrayContextMenuWindow()
    {
        InitializeComponent();
        WindowVisuals.ApplyDefaultIcon(this);
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        SystemDecorations = SystemDecorations.None;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome;
        WindowStartupLocation = WindowStartupLocation.Manual;
        TransparencyLevelHint =
        [
            WindowTransparencyLevel.Transparent,
            WindowTransparencyLevel.AcrylicBlur,
        ];

        Opened += OnOpened;
        Closed += OnClosed;
        Deactivated += OnDeactivated;
        KeyDown += OnKeyDown;
    }

    public TrayContextMenuWindow(IReadOnlyList<TrayContextMenuEntry> entries)
        : this()
    {
        SetEntries(entries);
    }

    public event EventHandler<TrayContextMenuCommandInvokedEventArgs>? CommandInvoked;

    public string CommandSource { get; set; } = "tray-popup";

    public void SetEntries(IReadOnlyList<TrayContextMenuEntry> entries)
    {
        MenuItemsHost.ItemsSource = entries;
    }

    public void OpenAt(PixelPoint anchorPosition)
    {
        _anchorPosition = anchorPosition;
        Position = anchorPosition;
        Show();
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        PositionWithinWorkingArea();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        Closed -= OnClosed;
        Deactivated -= OnDeactivated;
        KeyDown -= OnKeyDown;
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        Close();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        Close();
    }

    private void OnMenuItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { Tag: TrayContextMenuItemEntry item } control || !item.IsEnabled)
        {
            return;
        }

        var point = e.GetCurrentPoint(control);
        if (!point.Properties.IsLeftButtonPressed
            && point.Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
        {
            return;
        }

        e.Handled = true;
        CommandInvoked?.Invoke(this, new TrayContextMenuCommandInvokedEventArgs(item.Command));
    }

    private void PositionWithinWorkingArea()
    {
        var screen = Screens.ScreenFromPoint(_anchorPosition) ?? Screens.Primary;
        if (screen is null)
        {
            return;
        }

        var scale = Math.Max(0.01d, RenderScaling);
        var widthPx = Math.Max(1, (int)Math.Ceiling(Bounds.Width * scale));
        var heightPx = Math.Max(1, (int)Math.Ceiling(Bounds.Height * scale));
        var workingArea = screen.WorkingArea;
        var preferredX = _anchorPosition.X - (widthPx / 2);
        var minX = workingArea.X + PopupMargin;
        var maxX = workingArea.X + Math.Max(PopupMargin, workingArea.Width - widthPx - PopupMargin);
        var x = Math.Clamp(preferredX, minX, maxX);

        var aboveY = _anchorPosition.Y - heightPx - PopupMargin;
        var minY = workingArea.Y + PopupMargin;
        var maxY = workingArea.Y + Math.Max(PopupMargin, workingArea.Height - heightPx - PopupMargin);
        var y = aboveY >= minY
            ? aboveY
            : Math.Clamp(_anchorPosition.Y + PopupMargin, minY, maxY);

        Position = new PixelPoint(x, y);
    }
}
