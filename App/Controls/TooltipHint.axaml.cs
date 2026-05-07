using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace MAAUnified.App.Controls;

public partial class TooltipHint : UserControl
{
    private static readonly TimeSpan HoverOpenDelay = TimeSpan.FromMilliseconds(500);
    private int _openRequestVersion;
    private bool _hoverOpenPending;
    private bool _isTooltipOpen;
    private Cursor? _handCursor;

    public static readonly StyledProperty<string?> TipProperty =
        AvaloniaProperty.Register<TooltipHint, string?>(nameof(Tip));

    public static readonly StyledProperty<string> GlyphTextProperty =
        AvaloniaProperty.Register<TooltipHint, string>(nameof(GlyphText), "?");

    public static readonly StyledProperty<Thickness> GlyphMarginProperty =
        AvaloniaProperty.Register<TooltipHint, Thickness>(nameof(GlyphMargin), new Thickness(0));

    public TooltipHint()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => UpdateTooltipAvailability();
        AddHandler(
            InputElement.PointerEnteredEvent,
            OnGlyphPointerEntered,
            RoutingStrategies.Direct,
            handledEventsToo: true);
        AddHandler(
            InputElement.PointerMovedEvent,
            OnGlyphPointerMoved,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        AddHandler(
            InputElement.PointerPressedEvent,
            OnGlyphPointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        UpdateTooltipAvailability();
    }

    public string? Tip
    {
        get => GetValue(TipProperty);
        set => SetValue(TipProperty, value);
    }

    public string GlyphText
    {
        get => GetValue(GlyphTextProperty);
        set => SetValue(GlyphTextProperty, value);
    }

    public Thickness GlyphMargin
    {
        get => GetValue(GlyphMarginProperty);
        set => SetValue(GlyphMarginProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TipProperty)
        {
            UpdateTooltipAvailability();
        }
        else if (change.Property == IsEnabledProperty)
        {
            if (change.NewValue is false)
            {
                _openRequestVersion++;
                CloseTooltip();
            }

            UpdateTooltipAvailability();
        }
    }

    private bool HasTip => !string.IsNullOrWhiteSpace(Tip);

    private void OnGlyphPointerEntered(object? sender, PointerEventArgs e)
    {
        ScheduleHoverOpen();
    }

    private void OnGlyphPointerMoved(object? sender, PointerEventArgs e)
    {
        ScheduleHoverOpen();
    }

    private async void ScheduleHoverOpen()
    {
        if (_hoverOpenPending || _isTooltipOpen || !IsPointerOver || !IsEnabled || !HasTip)
        {
            return;
        }

        _hoverOpenPending = true;
        var version = ++_openRequestVersion;
        await Task.Delay(HoverOpenDelay);
        Dispatcher.UIThread.Post(() => OpenTooltipIfCurrent(version), DispatcherPriority.Background);
    }

    private void OnGlyphPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !IsEnabled || !HasTip)
        {
            return;
        }

        var version = ++_openRequestVersion;
        Dispatcher.UIThread.Post(() => OpenTooltipIfCurrent(version), DispatcherPriority.Background);
        e.Handled = true;
    }

    private void OpenTooltipIfCurrent(int version)
    {
        if (version != _openRequestVersion || !IsPointerOver || !IsEnabled || !HasTip)
        {
            if (version == _openRequestVersion)
            {
                _hoverOpenPending = false;
            }

            return;
        }

        _hoverOpenPending = false;
        _isTooltipOpen = true;
        ToolTip.SetServiceEnabled(this, true);
        ToolTip.SetIsOpen(this, true);
    }

    private void OnGlyphPointerExited(object? sender, PointerEventArgs e)
    {
        _openRequestVersion++;
        CloseTooltip();
    }

    private void UpdateTooltipAvailability()
    {
        if (!HasTip)
        {
            _openRequestVersion++;
            CloseTooltip();
        }

        ToolTip.SetServiceEnabled(this, IsEnabled && HasTip);
        UpdateGlyphCursor();
    }

    private void CloseTooltip()
    {
        _hoverOpenPending = false;
        _isTooltipOpen = false;
        ToolTip.SetIsOpen(this, false);
    }

    private void UpdateGlyphCursor()
    {
        if (GlyphHost is null)
        {
            return;
        }

        if (!IsEnabled || !HasTip)
        {
            GlyphHost.Cursor = null;
            return;
        }

        _handCursor ??= TryCreateCursor(StandardCursorType.Hand);
        GlyphHost.Cursor = _handCursor;
    }

    private static Cursor? TryCreateCursor(StandardCursorType cursorType)
    {
        try
        {
            return new Cursor(cursorType);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
