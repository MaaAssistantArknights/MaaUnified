using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace MAAUnified.App.Controls;

[PseudoClasses(":resizable-dialog", ":compact-modal")]
public class AppWindowFrame : ContentControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<AppWindowFrame, string>(nameof(Title), "Window");

    public static readonly StyledProperty<object?> HeaderContentProperty =
        AvaloniaProperty.Register<AppWindowFrame, object?>(nameof(HeaderContent));

    public static readonly StyledProperty<object?> ActionsContentProperty =
        AvaloniaProperty.Register<AppWindowFrame, object?>(nameof(ActionsContent));

    public static readonly StyledProperty<Thickness> ShellMarginProperty =
        AvaloniaProperty.Register<AppWindowFrame, Thickness>(nameof(ShellMargin), new Thickness(12));

    public static readonly StyledProperty<bool> ShowCloseButtonProperty =
        AvaloniaProperty.Register<AppWindowFrame, bool>(nameof(ShowCloseButton), true);

    public static readonly StyledProperty<AppWindowFrameMode> ModeProperty =
        AvaloniaProperty.Register<AppWindowFrame, AppWindowFrameMode>(
            nameof(Mode),
            AppWindowFrameMode.ResizableDialog);

    public static readonly StyledProperty<bool> CapWindowHeightToOwnerProperty =
        AvaloniaProperty.Register<AppWindowFrame, bool>(nameof(CapWindowHeightToOwner));

    public static readonly StyledProperty<double> OwnerHeightCapMarginProperty =
        AvaloniaProperty.Register<AppWindowFrame, double>(nameof(OwnerHeightCapMargin), 24d);

    public static readonly DirectProperty<AppWindowFrame, bool> HasHeaderContentProperty =
        AvaloniaProperty.RegisterDirect<AppWindowFrame, bool>(
            nameof(HasHeaderContent),
            frame => frame.HasHeaderContent);

    public static readonly DirectProperty<AppWindowFrame, bool> HasActionsProperty =
        AvaloniaProperty.RegisterDirect<AppWindowFrame, bool>(
            nameof(HasActions),
            frame => frame.HasActions);

    public static readonly DirectProperty<AppWindowFrame, bool> AllowsHeaderDragProperty =
        AvaloniaProperty.RegisterDirect<AppWindowFrame, bool>(
            nameof(AllowsHeaderDrag),
            frame => frame.AllowsHeaderDrag);

    public static readonly DirectProperty<AppWindowFrame, bool> ShowsResizeGripsProperty =
        AvaloniaProperty.RegisterDirect<AppWindowFrame, bool>(
            nameof(ShowsResizeGrips),
            frame => frame.ShowsResizeGrips);

    private static readonly (string PartName, WindowEdge Edge)[] ResizeGripParts =
    [
        ("PART_ResizeNorth", WindowEdge.North),
        ("PART_ResizeSouth", WindowEdge.South),
        ("PART_ResizeWest", WindowEdge.West),
        ("PART_ResizeEast", WindowEdge.East),
        ("PART_ResizeNorthWest", WindowEdge.NorthWest),
        ("PART_ResizeNorthEast", WindowEdge.NorthEast),
        ("PART_ResizeSouthWest", WindowEdge.SouthWest),
        ("PART_ResizeSouthEast", WindowEdge.SouthEast),
    ];

    private Border? _headerDragArea;
    private Button? _closeButton;
    private readonly List<Control> _resizeGrips = [];
    private Window? _hostWindow;
    private WindowBase? _ownerWindow;
    private bool _hasHeaderContent;
    private bool _hasActions;
    private bool _allowsHeaderDrag;
    private bool _showsResizeGrips;
    private bool _hasCapturedHostMaxHeight;
    private double _initialHostMaxHeight = double.PositiveInfinity;

    public event EventHandler? CloseRequested;

    public AppWindowFrame()
    {
        HasHeaderContent = HeaderContent is not null;
        HasActions = ActionsContent is not null;
        UpdateModeState();
    }

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public object? HeaderContent
    {
        get => GetValue(HeaderContentProperty);
        set => SetValue(HeaderContentProperty, value);
    }

    public object? ActionsContent
    {
        get => GetValue(ActionsContentProperty);
        set => SetValue(ActionsContentProperty, value);
    }

    public Thickness ShellMargin
    {
        get => GetValue(ShellMarginProperty);
        set => SetValue(ShellMarginProperty, value);
    }

    public bool ShowCloseButton
    {
        get => GetValue(ShowCloseButtonProperty);
        set => SetValue(ShowCloseButtonProperty, value);
    }

    public AppWindowFrameMode Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public bool CapWindowHeightToOwner
    {
        get => GetValue(CapWindowHeightToOwnerProperty);
        set => SetValue(CapWindowHeightToOwnerProperty, value);
    }

    public double OwnerHeightCapMargin
    {
        get => GetValue(OwnerHeightCapMarginProperty);
        set => SetValue(OwnerHeightCapMarginProperty, value);
    }

    public bool HasHeaderContent
    {
        get => _hasHeaderContent;
        private set => SetAndRaise(HasHeaderContentProperty, ref _hasHeaderContent, value);
    }

    public bool HasActions
    {
        get => _hasActions;
        private set => SetAndRaise(HasActionsProperty, ref _hasActions, value);
    }

    public bool AllowsHeaderDrag
    {
        get => _allowsHeaderDrag;
        private set => SetAndRaise(AllowsHeaderDragProperty, ref _allowsHeaderDrag, value);
    }

    public bool ShowsResizeGrips
    {
        get => _showsResizeGrips;
        private set => SetAndRaise(ShowsResizeGripsProperty, ref _showsResizeGrips, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        DetachTemplateEvents();
        base.OnApplyTemplate(e);

        _headerDragArea = e.NameScope.Find<Border>("PART_HeaderDragArea");
        _closeButton = e.NameScope.Find<Button>("PART_CloseButton");

        if (_headerDragArea is not null)
        {
            _headerDragArea.PointerPressed += OnHeaderPointerPressed;
        }

        if (_closeButton is not null)
        {
            _closeButton.Click += OnCloseButtonClick;
        }

        foreach (var (partName, edge) in ResizeGripParts)
        {
            if (e.NameScope.Find<Control>(partName) is not { } grip)
            {
                continue;
            }

            grip.Tag = edge.ToString();
            grip.PointerPressed += OnResizeGripPointerPressed;
            _resizeGrips.Add(grip);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AttachToHostWindow();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        DetachTemplateEvents();
        DetachWindowEvents();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == HeaderContentProperty)
        {
            HasHeaderContent = HeaderContent is not null;
            return;
        }

        if (change.Property == ActionsContentProperty)
        {
            HasActions = ActionsContent is not null;
            return;
        }

        if (change.Property == ModeProperty)
        {
            UpdateModeState();
            TryApplyOwnerHeightCap();
            return;
        }

        if (change.Property == CapWindowHeightToOwnerProperty
            || change.Property == OwnerHeightCapMarginProperty)
        {
            TryApplyOwnerHeightCap();
        }
    }

    private void OnCloseButtonClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (CloseRequested is not null)
        {
            CloseRequested(this, EventArgs.Empty);
            return;
        }

        if (TopLevel.GetTopLevel(this) is Window window)
        {
            window.Close();
        }
    }

    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!AllowsHeaderDrag || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (TopLevel.GetTopLevel(this) is not Window window)
        {
            return;
        }

        window.BeginMoveDrag(e);
        e.Handled = true;
    }

    private void OnResizeGripPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ShowsResizeGrips
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || sender is not Control { Tag: string tag }
            || !Enum.TryParse<WindowEdge>(tag, out var edge))
        {
            return;
        }

        if (TopLevel.GetTopLevel(this) is not Window window)
        {
            return;
        }

        window.BeginResizeDrag(edge, e);
        e.Handled = true;
    }

    private void OnHostWindowOpened(object? sender, EventArgs e)
    {
        SubscribeToOwnerWindow(_hostWindow?.Owner);
        TryApplyOwnerHeightCap();
    }

    private void OnHostWindowClosed(object? sender, EventArgs e)
    {
        DetachOwnerWindow();
    }

    private void OnOwnerWindowSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        TryApplyOwnerHeightCap();
    }

    private void AttachToHostWindow()
    {
        if (TopLevel.GetTopLevel(this) is not Window hostWindow)
        {
            return;
        }

        if (ReferenceEquals(_hostWindow, hostWindow))
        {
            TryApplyOwnerHeightCap();
            return;
        }

        DetachWindowEvents();

        _hostWindow = hostWindow;
        CaptureHostWindowMaxHeight(hostWindow);

        _hostWindow.Opened += OnHostWindowOpened;
        _hostWindow.Closed += OnHostWindowClosed;

        SubscribeToOwnerWindow(_hostWindow.Owner);
        TryApplyOwnerHeightCap();
    }

    private void DetachWindowEvents()
    {
        DetachOwnerWindow();

        if (_hostWindow is not null)
        {
            RestoreHostWindowMaxHeight();
            _hostWindow.Opened -= OnHostWindowOpened;
            _hostWindow.Closed -= OnHostWindowClosed;
            _hostWindow = null;
        }

        _hasCapturedHostMaxHeight = false;
        _initialHostMaxHeight = double.PositiveInfinity;
    }

    private void SubscribeToOwnerWindow(WindowBase? ownerWindow)
    {
        if (ReferenceEquals(_ownerWindow, ownerWindow))
        {
            return;
        }

        DetachOwnerWindow();

        _ownerWindow = ownerWindow;
        if (_ownerWindow is not null)
        {
            _ownerWindow.SizeChanged += OnOwnerWindowSizeChanged;
        }
    }

    private void DetachOwnerWindow()
    {
        if (_ownerWindow is not null)
        {
            _ownerWindow.SizeChanged -= OnOwnerWindowSizeChanged;
            _ownerWindow = null;
        }
    }

    private void CaptureHostWindowMaxHeight(Window hostWindow)
    {
        if (_hasCapturedHostMaxHeight)
        {
            return;
        }

        _initialHostMaxHeight = hostWindow.MaxHeight;
        _hasCapturedHostMaxHeight = true;
    }

    private void RestoreHostWindowMaxHeight()
    {
        if (_hostWindow is null || !_hasCapturedHostMaxHeight)
        {
            return;
        }

        _hostWindow.MaxHeight = _initialHostMaxHeight;
    }

    private void TryApplyOwnerHeightCap()
    {
        if (_hostWindow is null)
        {
            return;
        }

        if (Mode != AppWindowFrameMode.ResizableDialog || !CapWindowHeightToOwner)
        {
            RestoreHostWindowMaxHeight();
            return;
        }

        var ownerHeight = _ownerWindow?.Bounds.Height ?? _hostWindow.Owner?.Bounds.Height ?? 0d;
        if (ownerHeight <= 0d)
        {
            return;
        }

        CaptureHostWindowMaxHeight(_hostWindow);

        var cappedHeight = Math.Max(_hostWindow.MinHeight, ownerHeight - OwnerHeightCapMargin);
        var effectiveMaxHeight = double.IsPositiveInfinity(_initialHostMaxHeight)
            ? cappedHeight
            : Math.Min(_initialHostMaxHeight, cappedHeight);

        _hostWindow.MaxHeight = effectiveMaxHeight;
        _hostWindow.Height = Math.Min(
            Math.Max(_hostWindow.Height, _hostWindow.MinHeight),
            effectiveMaxHeight);
    }

    private void UpdateModeState()
    {
        var isResizableDialog = Mode == AppWindowFrameMode.ResizableDialog;

        AllowsHeaderDrag = isResizableDialog;
        ShowsResizeGrips = isResizableDialog;

        PseudoClasses.Set(":resizable-dialog", isResizableDialog);
        PseudoClasses.Set(":compact-modal", !isResizableDialog);
    }

    private void DetachTemplateEvents()
    {
        if (_headerDragArea is not null)
        {
            _headerDragArea.PointerPressed -= OnHeaderPointerPressed;
            _headerDragArea = null;
        }

        if (_closeButton is not null)
        {
            _closeButton.Click -= OnCloseButtonClick;
            _closeButton = null;
        }

        foreach (var grip in _resizeGrips)
        {
            grip.PointerPressed -= OnResizeGripPointerPressed;
        }

        _resizeGrips.Clear();
    }
}
