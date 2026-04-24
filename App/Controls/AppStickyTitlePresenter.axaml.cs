using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MAAUnified.App.Controls;

public partial class AppStickyTitlePresenter : UserControl
{
    public static readonly StyledProperty<AppStickyTitleState> StateProperty =
        AvaloniaProperty.Register<AppStickyTitlePresenter, AppStickyTitleState>(
            nameof(State),
            defaultValue: AppStickyTitleState.Hidden);

    public AppStickyTitlePresenter()
    {
        InitializeComponent();
        this.GetObservable(StateProperty).Subscribe(ApplyState);
        ApplyState(State);
    }

    public AppStickyTitleState State
    {
        get => GetValue(StateProperty) ?? AppStickyTitleState.Hidden;
        set => SetValue(StateProperty, value ?? AppStickyTitleState.Hidden);
    }

    private void ApplyState(AppStickyTitleState? state)
    {
        var resolvedState = state ?? AppStickyTitleState.Hidden;
        if (!resolvedState.IsVisible)
        {
            IsVisible = false;
            Height = double.NaN;
            CurrentTitleText.Text = string.Empty;
            IncomingHost.IsVisible = false;
            IncomingHost.Height = double.NaN;
            IncomingTitleText.Text = string.Empty;
            CurrentHost.RenderTransform = CreateVerticalTransform(0d);
            IncomingHost.RenderTransform = CreateVerticalTransform(0d);
            return;
        }

        IsVisible = true;
        Height = resolvedState.Height;
        CurrentTitleText.Text = resolvedState.CurrentTitle;

        // Sticky-title handoff motion is a design contract.
        // Keep these transform changes transition-driven instead of snapping them off.
        CurrentHost.RenderTransform = CreateVerticalTransform(resolvedState.CurrentTranslateY);

        if (!resolvedState.ShowIncomingTitle || string.IsNullOrWhiteSpace(resolvedState.IncomingTitle))
        {
            IncomingHost.IsVisible = false;
            IncomingHost.Height = resolvedState.Height;
            IncomingTitleText.Text = string.Empty;
            IncomingHost.RenderTransform = CreateVerticalTransform(resolvedState.Height);
            return;
        }

        IncomingHost.IsVisible = true;
        IncomingHost.Height = resolvedState.Height;
        IncomingTitleText.Text = resolvedState.IncomingTitle;
        IncomingHost.RenderTransform = CreateVerticalTransform(resolvedState.IncomingTranslateY);
    }

    private static TranslateTransform CreateVerticalTransform(double y)
    {
        return new TranslateTransform
        {
            Y = y,
        };
    }
}
