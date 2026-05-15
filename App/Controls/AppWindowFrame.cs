using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace MAAUnified.App.Controls;

[PseudoClasses(":resizable-dialog", ":compact-modal", ":controls-left", ":window-maximized")]
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

    public static readonly StyledProperty<double> ChromeScaleFactorProperty =
        AvaloniaProperty.Register<AppWindowFrame, double>(nameof(ChromeScaleFactor), 1d);

    public static readonly StyledProperty<ITransform?> ChromeLayoutTransformProperty =
        AvaloniaProperty.Register<AppWindowFrame, ITransform?>(
            nameof(ChromeLayoutTransform),
            new ScaleTransform(1d, 1d));

    public static readonly StyledProperty<bool> ShowCloseButtonProperty =
        AvaloniaProperty.Register<AppWindowFrame, bool>(nameof(ShowCloseButton), true);

    public static readonly StyledProperty<bool> ShowMinimizeButtonProperty =
        AvaloniaProperty.Register<AppWindowFrame, bool>(nameof(ShowMinimizeButton));

    public static readonly StyledProperty<bool> ShowMaximizeButtonProperty =
        AvaloniaProperty.Register<AppWindowFrame, bool>(nameof(ShowMaximizeButton));

    public static readonly StyledProperty<AppWindowControlsPlacement> WindowControlsPlacementProperty =
        AvaloniaProperty.Register<AppWindowFrame, AppWindowControlsPlacement>(
            nameof(WindowControlsPlacement),
            AppWindowControlsPlacement.PlatformDefault);

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

    public static readonly DirectProperty<AppWindowFrame, AppWindowFrameHorizontalInset> EffectiveHorizontalContentInsetProperty =
        AvaloniaProperty.RegisterDirect<AppWindowFrame, AppWindowFrameHorizontalInset>(
            nameof(EffectiveHorizontalContentInset),
            frame => frame.EffectiveHorizontalContentInset);

    public static readonly DirectProperty<AppWindowFrame, Thickness> EffectiveResizeGripMarginProperty =
        AvaloniaProperty.RegisterDirect<AppWindowFrame, Thickness>(
            nameof(EffectiveResizeGripMargin),
            frame => frame.EffectiveResizeGripMargin);

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
    private Border? _frameSurface;
    private readonly List<Button> _closeButtons = [];
    private readonly List<Button> _minimizeButtons = [];
    private readonly List<Button> _maximizeButtons = [];
    private readonly List<Control> _resizeGrips = [];
    private Window? _hostWindow;
    private WindowBase? _ownerWindow;
    private IPointer? _manualResizePointer;
    private Control? _manualResizeGrip;
    private bool _hasHeaderContent;
    private bool _hasActions;
    private bool _allowsHeaderDrag;
    private bool _showsResizeGrips;
    private bool _hasCapturedHostMaxHeight;
    private double _initialHostMaxHeight = double.PositiveInfinity;
    private AppWindowFrameHorizontalInset _effectiveHorizontalContentInset;
    private readonly bool _preferOuterResizeGrips;
    private Thickness _effectiveResizeGripMargin;
    private ManualResizeDragState? _manualResizeDragState;

    static AppWindowFrame()
    {
        ChromeScaleFactorProperty.Changed.AddClassHandler<AppWindowFrame>((frame, _) => frame.UpdateChromeLayoutTransform());
    }

    public event EventHandler? CloseRequested;

    public AppWindowFrame()
    {
        _preferOuterResizeGrips = OperatingSystem.IsMacOS();
        HasHeaderContent = HeaderContent is not null;
        HasActions = ActionsContent is not null;
        AddHandler(PointerReleasedEvent, OnPointerReleasedForInputFocusDismiss, RoutingStrategies.Bubble, handledEventsToo: true);
        UpdateWindowControlsPlacementState();
        UpdateModeState();
        UpdateEffectiveHorizontalContentInset();
        UpdateEffectiveResizeGripMargin();
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

    private void OnPointerReleasedForInputFocusDismiss(object? sender, PointerReleasedEventArgs e)
    {
        var focusManager = TopLevel.GetTopLevel(this)?.FocusManager;
        var focusedElement = focusManager?.GetFocusedElement();
        if (focusedElement is not Visual focusedVisual || !IsTextEditingFocus(focusedVisual))
        {
            return;
        }

        if (e.Source is Visual sourceVisual && IsWithinActiveInputSurface(sourceVisual, focusedVisual))
        {
            return;
        }

        focusManager?.ClearFocus();
    }

    private static bool IsTextEditingFocus(Visual visual)
    {
        return visual.GetSelfAndVisualAncestors().Any(static ancestor =>
            ancestor is TextBox
                or ComboBox
                or NumericUpDown
                or AppTextInput
                or AppSelect
                or AppNumberInput
                or VerticalSpinNumberBox
                or AppActionInput
                or AppHistoryInput
                or AppSuggestInput
                or AppCopilotPathDropdown
                or AppMultiSelect
                or AppMultiSelectDropdown);
    }

    private static bool IsWithinActiveInputSurface(Visual sourceVisual, Visual focusedVisual)
    {
        var activeInputSurface = GetActiveInputSurface(focusedVisual);
        return activeInputSurface is not null
            && sourceVisual.GetSelfAndVisualAncestors().Contains(activeInputSurface);
    }

    private static Visual? GetActiveInputSurface(Visual focusedVisual)
    {
        var ancestors = focusedVisual.GetSelfAndVisualAncestors().ToArray();
        return ancestors.FirstOrDefault(static ancestor =>
                   ancestor is AppActionInput
                       or AppHistoryInput
                       or AppSuggestInput
                       or AppCopilotPathDropdown
                       or AppMultiSelect
                       or AppMultiSelectDropdown
                       or AppNumberInput
                       or VerticalSpinNumberBox)
               ?? ancestors.FirstOrDefault(static ancestor =>
                   ancestor is TextBox
                       or ComboBox
                       or NumericUpDown
                       or AppTextInput
                       or AppSelect);
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

    public double ChromeScaleFactor
    {
        get => GetValue(ChromeScaleFactorProperty);
        set => SetValue(ChromeScaleFactorProperty, value);
    }

    public ITransform? ChromeLayoutTransform
    {
        get => GetValue(ChromeLayoutTransformProperty);
        set => SetValue(ChromeLayoutTransformProperty, value);
    }

    public bool ShowCloseButton
    {
        get => GetValue(ShowCloseButtonProperty);
        set => SetValue(ShowCloseButtonProperty, value);
    }

    public bool ShowMinimizeButton
    {
        get => GetValue(ShowMinimizeButtonProperty);
        set => SetValue(ShowMinimizeButtonProperty, value);
    }

    public bool ShowMaximizeButton
    {
        get => GetValue(ShowMaximizeButtonProperty);
        set => SetValue(ShowMaximizeButtonProperty, value);
    }

    public AppWindowControlsPlacement WindowControlsPlacement
    {
        get => GetValue(WindowControlsPlacementProperty);
        set => SetValue(WindowControlsPlacementProperty, value);
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

    public AppWindowFrameHorizontalInset EffectiveHorizontalContentInset
    {
        get => _effectiveHorizontalContentInset;
        private set => SetAndRaise(
            EffectiveHorizontalContentInsetProperty,
            ref _effectiveHorizontalContentInset,
            value);
    }

    public Thickness EffectiveResizeGripMargin
    {
        get => _effectiveResizeGripMargin;
        private set => SetAndRaise(
            EffectiveResizeGripMarginProperty,
            ref _effectiveResizeGripMargin,
            value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        DetachTemplateEvents();
        base.OnApplyTemplate(e);

        _frameSurface = e.NameScope.Find<Border>("PART_FrameSurface");
        if (_frameSurface is not null)
        {
            _frameSurface.PropertyChanged += OnFrameSurfacePropertyChanged;
        }

        _headerDragArea = e.NameScope.Find<Border>("PART_HeaderDragArea");
        if (_headerDragArea is not null)
        {
            _headerDragArea.PointerPressed += OnHeaderPointerPressed;
        }

        foreach (var partName in new[]
                 {
                     "PART_LeftCloseButton",
                     "PART_RightCloseButton",
                 })
        {
            if (e.NameScope.Find<Button>(partName) is not { } button)
            {
                continue;
            }

            button.Click += OnCloseButtonClick;
            _closeButtons.Add(button);
        }

        foreach (var partName in new[]
                 {
                     "PART_LeftMinimizeButton",
                     "PART_RightMinimizeButton",
                 })
        {
            if (e.NameScope.Find<Button>(partName) is not { } button)
            {
                continue;
            }

            button.Click += OnMinimizeButtonClick;
            _minimizeButtons.Add(button);
        }

        foreach (var partName in new[]
                 {
                     "PART_LeftMaximizeButton",
                     "PART_RightMaximizeButton",
                 })
        {
            if (e.NameScope.Find<Button>(partName) is not { } button)
            {
                continue;
            }

            button.Click += OnMaximizeRestoreButtonClick;
            _maximizeButtons.Add(button);
        }

        foreach (var (partName, edge) in ResizeGripParts)
        {
            if (e.NameScope.Find<Control>(partName) is not { } grip)
            {
                continue;
            }

            grip.Tag = edge;
            grip.PointerPressed += OnResizeGripPointerPressed;
            grip.PointerMoved += OnResizeGripPointerMoved;
            grip.PointerReleased += OnResizeGripPointerReleased;
            grip.PointerCaptureLost += OnResizeGripPointerCaptureLost;
            _resizeGrips.Add(grip);
        }

        UpdateEffectiveHorizontalContentInset();
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

        if (change.Property == WindowControlsPlacementProperty)
        {
            UpdateWindowControlsPlacementState();
            return;
        }

        if (change.Property == ShellMarginProperty)
        {
            UpdateEffectiveHorizontalContentInset();
            UpdateEffectiveResizeGripMargin();
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

        if (ResolveHostWindow() is Window window)
        {
            window.Close();
        }
    }

    private void OnMinimizeButtonClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ResolveHostWindow() is not Window window)
        {
            return;
        }

        window.WindowState = WindowState.Minimized;
        e.Handled = true;
    }

    private void OnMaximizeRestoreButtonClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ResolveHostWindow() is not Window window)
        {
            return;
        }

        window.WindowState = window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
        e.Handled = true;
    }

    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!AllowsHeaderDrag || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (ResolveHostWindow() is not Window window)
        {
            return;
        }

        if (e.ClickCount >= 2 && ShowMaximizeButton && window.CanResize)
        {
            window.WindowState = window.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            e.Handled = true;
            return;
        }

        window.BeginMoveDrag(e);
        e.Handled = true;
    }

    private void OnResizeGripPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ShowsResizeGrips
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || sender is not Control { Tag: WindowEdge edge })
        {
            return;
        }

        if (ResolveHostWindow() is not Window window)
        {
            return;
        }

        if (!window.CanResize)
        {
            return;
        }

        if (TryBeginManualResizeDrag(sender, e, window, edge))
        {
            e.Handled = true;
            return;
        }

        window.BeginResizeDrag(edge, e);
        e.Handled = true;
    }

    private void OnResizeGripPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!TryUpdateManualResizeDrag(sender, e))
        {
            return;
        }

        e.Handled = true;
    }

    private void OnResizeGripPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(sender, _manualResizeGrip)
            || !ReferenceEquals(e.Pointer, _manualResizePointer))
        {
            return;
        }

        EndManualResizeDrag();
        e.Handled = true;
    }

    private void OnResizeGripPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!ReferenceEquals(sender, _manualResizeGrip)
            || !ReferenceEquals(e.Pointer, _manualResizePointer))
        {
            return;
        }

        EndManualResizeDrag(releasePointer: false);
    }

    private void OnHostWindowOpened(object? sender, EventArgs e)
    {
        SubscribeToOwnerWindow(_hostWindow?.Owner);
        TryApplyOwnerHeightCap();
        UpdateModeState();
    }

    private void OnHostWindowClosed(object? sender, EventArgs e)
    {
        DetachOwnerWindow();
        UpdateModeState();
    }

    private void OnHostWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Window.WindowStateProperty)
        {
            return;
        }

        UpdateModeState();
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
            UpdateModeState();
            return;
        }

        DetachWindowEvents();

        _hostWindow = hostWindow;
        CaptureHostWindowMaxHeight(hostWindow);

        _hostWindow.Opened += OnHostWindowOpened;
        _hostWindow.Closed += OnHostWindowClosed;
        _hostWindow.PropertyChanged += OnHostWindowPropertyChanged;

        SubscribeToOwnerWindow(_hostWindow.Owner);
        TryApplyOwnerHeightCap();
        UpdateModeState();
    }

    private void DetachWindowEvents()
    {
        DetachOwnerWindow();

        if (_hostWindow is not null)
        {
            RestoreHostWindowMaxHeight();
            _hostWindow.Opened -= OnHostWindowOpened;
            _hostWindow.Closed -= OnHostWindowClosed;
            _hostWindow.PropertyChanged -= OnHostWindowPropertyChanged;
            _hostWindow = null;
        }

        _hasCapturedHostMaxHeight = false;
        _initialHostMaxHeight = double.PositiveInfinity;
        UpdateModeState();
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

    private Window? ResolveHostWindow()
    {
        return _hostWindow ?? TopLevel.GetTopLevel(this) as Window;
    }

    private void UpdateWindowControlsPlacementState()
    {
        var resolvedPlacement = WindowControlsPlacement == AppWindowControlsPlacement.PlatformDefault
            ? ResolvePlatformDefaultWindowControlsPlacement()
            : WindowControlsPlacement;

        var useLeftPlacement = resolvedPlacement == AppWindowControlsPlacement.Left;
        PseudoClasses.Set(":controls-left", useLeftPlacement);
    }

    private static AppWindowControlsPlacement ResolvePlatformDefaultWindowControlsPlacement()
    {
        return OperatingSystem.IsMacOS()
            ? AppWindowControlsPlacement.Left
            : AppWindowControlsPlacement.Right;
    }

    private void UpdateModeState()
    {
        UpdateWindowControlsPlacementState();

        var isResizableDialog = Mode == AppWindowFrameMode.ResizableDialog;
        var hostWindow = ResolveHostWindow();
        var hostWindowState = hostWindow?.WindowState ?? WindowState.Normal;
        var isHostNormalState = hostWindowState == WindowState.Normal;
        var isHostMaximizedState = hostWindowState == WindowState.Maximized;

        AllowsHeaderDrag = isResizableDialog;
        ShowsResizeGrips = isResizableDialog && isHostNormalState;

        PseudoClasses.Set(":resizable-dialog", isResizableDialog);
        PseudoClasses.Set(":compact-modal", !isResizableDialog);
        PseudoClasses.Set(":window-maximized", isHostMaximizedState);
        UpdateEffectiveHorizontalContentInset();
    }

    private void OnFrameSurfacePropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Border.PaddingProperty
            || e.Property == Border.BorderThicknessProperty
            || e.Property == Border.MarginProperty)
        {
            UpdateEffectiveHorizontalContentInset();
        }
    }

    private void UpdateEffectiveHorizontalContentInset()
    {
        EffectiveHorizontalContentInset = CalculateEffectiveHorizontalContentInset();
    }

    private void UpdateEffectiveResizeGripMargin()
    {
        EffectiveResizeGripMargin = ResolveResizeGripMargin(ShellMargin, _preferOuterResizeGrips);
    }

    private bool TryBeginManualResizeDrag(object? sender, PointerPressedEventArgs e, Window window, WindowEdge edge)
    {
        if (!OperatingSystem.IsMacOS()
            || sender is not Control grip
            || window.WindowState != WindowState.Normal)
        {
            return false;
        }

        EndManualResizeDrag();

        _manualResizePointer = e.Pointer;
        _manualResizeGrip = grip;
        var renderScaling = Math.Max(0.01d, window.RenderScaling);
        _manualResizeDragState = new ManualResizeDragState(
            edge,
            window.PointToScreen(e.GetPosition(window)),
            window.Position,
            new PixelSize(
                LogicalLengthToPixelSize(window.Bounds.Width, renderScaling),
                LogicalLengthToPixelSize(window.Bounds.Height, renderScaling)),
            LogicalLengthToPixelSize(window.MinWidth, renderScaling),
            LogicalLengthToPixelSize(window.MinHeight, renderScaling),
            LogicalLengthToPixelSize(window.MaxWidth, renderScaling, allowInfinity: true),
            LogicalLengthToPixelSize(window.MaxHeight, renderScaling, allowInfinity: true),
            renderScaling);

        e.Pointer.Capture(grip);
        return true;
    }

    private bool TryUpdateManualResizeDrag(object? sender, PointerEventArgs e)
    {
        if (!ReferenceEquals(sender, _manualResizeGrip)
            || !ReferenceEquals(e.Pointer, _manualResizePointer)
            || _manualResizeDragState is not { } state
            || ResolveHostWindow() is not Window window)
        {
            return false;
        }

        var result = ComputeManualResizeDrag(
            state,
            window.PointToScreen(e.GetPosition(window)));

        if (Math.Abs(window.Width - result.Size.Width) > 0.01d)
        {
            window.Width = result.Size.Width;
        }

        if (Math.Abs(window.Height - result.Size.Height) > 0.01d)
        {
            window.Height = result.Size.Height;
        }

        var actualWindowSize = new PixelSize(
            LogicalLengthToPixelSize(window.Bounds.Width, state.RenderScaling),
            LogicalLengthToPixelSize(window.Bounds.Height, state.RenderScaling));
        var anchoredPosition = ResolveManualResizeAnchoredPosition(state, actualWindowSize, result.Position);
        if (window.Position != anchoredPosition)
        {
            window.Position = anchoredPosition;
        }

        return true;
    }

    private void EndManualResizeDrag(bool releasePointer = true)
    {
        var pointer = _manualResizePointer;
        _manualResizePointer = null;
        _manualResizeGrip = null;
        _manualResizeDragState = null;

        if (releasePointer)
        {
            pointer?.Capture(null);
        }
    }

    private void UpdateChromeLayoutTransform()
    {
        var scale = ChromeScaleFactor;
        if (!double.IsFinite(scale) || scale <= 0d)
        {
            scale = 1d;
        }

        SetCurrentValue(ChromeLayoutTransformProperty, new ScaleTransform(scale, scale));
    }

    private AppWindowFrameHorizontalInset CalculateEffectiveHorizontalContentInset()
    {
        var shellMargin = _frameSurface?.Margin ?? ShellMargin;
        var framePadding = _frameSurface?.Padding ?? default;
        var frameBorder = _frameSurface?.BorderThickness ?? default;

        return new AppWindowFrameHorizontalInset(
            shellMargin.Left + frameBorder.Left + framePadding.Left,
            shellMargin.Right + frameBorder.Right + framePadding.Right);
    }

    internal static Thickness ResolveResizeGripMargin(Thickness shellMargin, bool preferOuterResizeGrips)
    {
        // macOS keeps resize hit targets on the true window edge so they do not
        // cover the traffic-light/title row; Linux keeps the existing visible-frame path.
        return preferOuterResizeGrips
            ? default
            : shellMargin;
    }

    internal static ManualResizeDragResult ComputeManualResizeDrag(ManualResizeDragState state, PixelPoint currentPointerScreenPosition)
    {
        var deltaX = currentPointerScreenPosition.X - state.StartPointerScreenPosition.X;
        var deltaY = currentPointerScreenPosition.Y - state.StartPointerScreenPosition.Y;

        var width = state.StartWindowSize.Width;
        var height = state.StartWindowSize.Height;
        var left = state.StartWindowPosition.X;
        var top = state.StartWindowPosition.Y;

        if (state.Edge is WindowEdge.West or WindowEdge.NorthWest or WindowEdge.SouthWest)
        {
            width = ClampResizePixels(state.StartWindowSize.Width - deltaX, state.MinWidth, state.MaxWidth);
            left = state.StartWindowPosition.X + (state.StartWindowSize.Width - width);
        }
        else if (state.Edge is WindowEdge.East or WindowEdge.NorthEast or WindowEdge.SouthEast)
        {
            width = ClampResizePixels(state.StartWindowSize.Width + deltaX, state.MinWidth, state.MaxWidth);
        }

        if (state.Edge is WindowEdge.North or WindowEdge.NorthWest or WindowEdge.NorthEast)
        {
            height = ClampResizePixels(state.StartWindowSize.Height - deltaY, state.MinHeight, state.MaxHeight);
            top = state.StartWindowPosition.Y + (state.StartWindowSize.Height - height);
        }
        else if (state.Edge is WindowEdge.South or WindowEdge.SouthWest or WindowEdge.SouthEast)
        {
            height = ClampResizePixels(state.StartWindowSize.Height + deltaY, state.MinHeight, state.MaxHeight);
        }

        return new ManualResizeDragResult(
            new Size(
                PixelLengthToLogicalSize(width, state.RenderScaling),
                PixelLengthToLogicalSize(height, state.RenderScaling)),
            new PixelPoint(left, top));
    }

    internal static PixelPoint ResolveManualResizeAnchoredPosition(
        ManualResizeDragState state,
        PixelSize actualWindowSize,
        PixelPoint fallbackPosition)
    {
        var x = fallbackPosition.X;
        var y = fallbackPosition.Y;

        if (state.Edge is WindowEdge.West or WindowEdge.NorthWest or WindowEdge.SouthWest)
        {
            x = state.StartWindowPosition.X + (state.StartWindowSize.Width - actualWindowSize.Width);
        }

        if (state.Edge is WindowEdge.North or WindowEdge.NorthWest or WindowEdge.NorthEast)
        {
            y = state.StartWindowPosition.Y + (state.StartWindowSize.Height - actualWindowSize.Height);
        }

        return new PixelPoint(x, y);
    }

    private static int ClampResizePixels(int value, int minValue, int maxValue)
    {
        var effectiveMin = Math.Max(1, minValue);
        var effectiveMax = maxValue > 0
            ? Math.Max(effectiveMin, maxValue)
            : int.MaxValue;

        return Math.Min(Math.Max(value, effectiveMin), effectiveMax);
    }

    private static int LogicalLengthToPixelSize(double logicalLength, double renderScaling, bool allowInfinity = false)
    {
        if (allowInfinity && !double.IsFinite(logicalLength))
        {
            return 0;
        }

        return (int)Math.Round(logicalLength * Math.Max(0.01d, renderScaling), MidpointRounding.AwayFromZero);
    }

    private static double PixelLengthToLogicalSize(int pixelLength, double renderScaling)
    {
        return pixelLength / Math.Max(0.01d, renderScaling);
    }

    private void DetachTemplateEvents()
    {
        if (_frameSurface is not null)
        {
            _frameSurface.PropertyChanged -= OnFrameSurfacePropertyChanged;
            _frameSurface = null;
        }

        if (_headerDragArea is not null)
        {
            _headerDragArea.PointerPressed -= OnHeaderPointerPressed;
            _headerDragArea = null;
        }

        foreach (var closeButton in _closeButtons)
        {
            closeButton.Click -= OnCloseButtonClick;
        }

        _closeButtons.Clear();

        foreach (var minimizeButton in _minimizeButtons)
        {
            minimizeButton.Click -= OnMinimizeButtonClick;
        }

        _minimizeButtons.Clear();

        foreach (var maximizeButton in _maximizeButtons)
        {
            maximizeButton.Click -= OnMaximizeRestoreButtonClick;
        }

        _maximizeButtons.Clear();

        foreach (var grip in _resizeGrips)
        {
            grip.PointerPressed -= OnResizeGripPointerPressed;
            grip.PointerMoved -= OnResizeGripPointerMoved;
            grip.PointerReleased -= OnResizeGripPointerReleased;
            grip.PointerCaptureLost -= OnResizeGripPointerCaptureLost;
        }

        _resizeGrips.Clear();
        EndManualResizeDrag();
    }

    internal readonly record struct ManualResizeDragState(
        WindowEdge Edge,
        PixelPoint StartPointerScreenPosition,
        PixelPoint StartWindowPosition,
        PixelSize StartWindowSize,
        int MinWidth,
        int MinHeight,
        int MaxWidth,
        int MaxHeight,
        double RenderScaling);

    internal readonly record struct ManualResizeDragResult(Size Size, PixelPoint Position);
}
