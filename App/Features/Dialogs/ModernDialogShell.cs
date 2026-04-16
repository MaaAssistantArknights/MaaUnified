using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace MAAUnified.App.Features.Dialogs;

public class ModernDialogShell : ContentControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<ModernDialogShell, string>(nameof(Title), "Dialog");

    public static readonly StyledProperty<object?> HeaderContentProperty =
        AvaloniaProperty.Register<ModernDialogShell, object?>(nameof(HeaderContent));

    public static readonly StyledProperty<object?> ActionsContentProperty =
        AvaloniaProperty.Register<ModernDialogShell, object?>(nameof(ActionsContent));

    public static readonly StyledProperty<Thickness> ShellMarginProperty =
        AvaloniaProperty.Register<ModernDialogShell, Thickness>(nameof(ShellMargin), new Thickness(12));

    public static readonly StyledProperty<bool> ShowCloseButtonProperty =
        AvaloniaProperty.Register<ModernDialogShell, bool>(nameof(ShowCloseButton), true);

    public static readonly StyledProperty<bool> EnableHeaderDragProperty =
        AvaloniaProperty.Register<ModernDialogShell, bool>(nameof(EnableHeaderDrag), true);

    public static readonly DirectProperty<ModernDialogShell, bool> HasHeaderContentProperty =
        AvaloniaProperty.RegisterDirect<ModernDialogShell, bool>(
            nameof(HasHeaderContent),
            shell => shell.HasHeaderContent);

    public static readonly DirectProperty<ModernDialogShell, bool> HasActionsProperty =
        AvaloniaProperty.RegisterDirect<ModernDialogShell, bool>(
            nameof(HasActions),
            shell => shell.HasActions);

    private Border? _headerDragArea;
    private Button? _closeButton;
    private bool _hasHeaderContent;
    private bool _hasActions;

    public event EventHandler? CloseRequested;

    public ModernDialogShell()
    {
        HasHeaderContent = HeaderContent is not null;
        HasActions = ActionsContent is not null;
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

    public bool EnableHeaderDrag
    {
        get => GetValue(EnableHeaderDragProperty);
        set => SetValue(EnableHeaderDragProperty, value);
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
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == HeaderContentProperty)
        {
            HasHeaderContent = HeaderContent is not null;
        }
        else if (change.Property == ActionsContentProperty)
        {
            HasActions = ActionsContent is not null;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        DetachTemplateEvents();
        base.OnDetachedFromVisualTree(e);
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
        if (!EnableHeaderDrag || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
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
    }
}
