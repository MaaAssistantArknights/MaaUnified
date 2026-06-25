using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using MAAUnified.App.Controls;
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
    private const int PopupGap = 4;
    private PixelPoint _anchorPosition;
    private PixelRect? _anchorBounds;

    public TrayContextMenuWindow()
    {
        InitializeComponent();
        WindowVisuals.ApplyDefaultIcon(this);
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
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
        MenuPresenter.Items = entries.Select<TrayContextMenuEntry, AppMenuEntry>(static entry => entry switch
        {
            TrayContextMenuItemEntry item => new AppMenuActionItem(item.Header, item.Command, IsEnabled: item.IsEnabled),
            TrayContextMenuSeparatorEntry => new AppMenuSeparatorEntry(),
            _ => throw new InvalidOperationException($"Unsupported tray context menu entry: {entry.GetType().FullName}"),
        }).ToArray();
    }

    public void OpenAt(PixelPoint anchorPosition)
    {
        _anchorPosition = anchorPosition;
        _anchorBounds = new PixelRect(anchorPosition.X, anchorPosition.Y, 1, 1);
        Position = anchorPosition;
        Show();
    }

    public void OpenAt(TrayMenuRequestEvent request)
    {
        ArgumentNullException.ThrowIfNull(request);

        _anchorPosition = new PixelPoint(request.ScreenX, request.ScreenY);
        if (TryCreateAnchorBounds(request, out var anchorBounds))
        {
            _anchorBounds = anchorBounds;
        }
        else
        {
            _anchorBounds = new PixelRect(request.ScreenX, request.ScreenY, 1, 1);
        }

        Position = _anchorPosition;
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

    private void OnMenuItemInvoked(object? sender, AppMenuItemInvokedEventArgs e)
    {
        if (e.Command is not TrayCommandId command)
        {
            return;
        }

        CommandInvoked?.Invoke(this, new TrayContextMenuCommandInvokedEventArgs(command));
    }

    private void PositionWithinWorkingArea()
    {
        var screen = Screens.ScreenFromPoint(_anchorPosition) ?? Screens.Primary;
        if (screen is null)
        {
            return;
        }

        var scale = Math.Max(0.01d, DesktopScaling);
        var widthPx = Math.Max(1, (int)Math.Ceiling(Bounds.Width * scale));
        var heightPx = Math.Max(1, (int)Math.Ceiling(Bounds.Height * scale));
        var workingArea = screen.WorkingArea;
        var minX = workingArea.X + PopupMargin;
        var maxX = workingArea.X + Math.Max(PopupMargin, workingArea.Width - widthPx - PopupMargin);
        var minY = workingArea.Y + PopupMargin;
        var maxY = workingArea.Y + Math.Max(PopupMargin, workingArea.Height - heightPx - PopupMargin);
        var taskbarEdge = DetectTaskbarEdge(screen);

        int preferredX;
        int preferredY;
        switch (taskbarEdge)
        {
            case ScreenEdge.Top:
                preferredX = _anchorPosition.X - (widthPx / 2);
                preferredY = _anchorPosition.Y + PopupGap;
                break;
            case ScreenEdge.Left:
                preferredX = _anchorPosition.X + PopupGap;
                preferredY = _anchorPosition.Y - (heightPx / 2);
                break;
            case ScreenEdge.Right:
                preferredX = _anchorPosition.X - widthPx - PopupGap;
                preferredY = _anchorPosition.Y - (heightPx / 2);
                break;
            case ScreenEdge.Bottom:
            default:
                preferredX = _anchorPosition.X - (widthPx / 2);
                preferredY = _anchorPosition.Y - heightPx - PopupGap;
                break;
        }

        var x = Math.Clamp(preferredX, minX, maxX);
        var y = Math.Clamp(preferredY, minY, maxY);

        Position = new PixelPoint(x, y);
    }

    private static bool TryCreateAnchorBounds(TrayMenuRequestEvent request, out PixelRect anchorBounds)
    {
        anchorBounds = default;
        if (request.AnchorLeft is null
            || request.AnchorTop is null
            || request.AnchorRight is null
            || request.AnchorBottom is null)
        {
            return false;
        }

        var width = Math.Max(1, request.AnchorRight.Value - request.AnchorLeft.Value);
        var height = Math.Max(1, request.AnchorBottom.Value - request.AnchorTop.Value);
        anchorBounds = new PixelRect(request.AnchorLeft.Value, request.AnchorTop.Value, width, height);
        return true;
    }

    private static ScreenEdge DetectTaskbarEdge(Screen screen)
    {
        var bounds = screen.Bounds;
        var workingArea = screen.WorkingArea;

        if (workingArea.Y > bounds.Y)
        {
            return ScreenEdge.Top;
        }

        if (workingArea.X > bounds.X)
        {
            return ScreenEdge.Left;
        }

        if (workingArea.Right < bounds.Right)
        {
            return ScreenEdge.Right;
        }

        return ScreenEdge.Bottom;
    }

    private enum ScreenEdge
    {
        Top,
        Right,
        Bottom,
        Left,
    }
}
