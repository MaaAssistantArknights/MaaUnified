using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MAAUnified.App.Infrastructure;
using MAAUnified.Application.Services.Localization;

namespace MAAUnified.App.Controls;

public static class AppTextEditingMenu
{
    private const string Scope = "App.TextEditingMenu";

    private static readonly object CutCommand = new();
    private static readonly object CopyCommand = new();
    private static readonly object PasteCommand = new();
    private static readonly object SelectAllCommand = new();
    private static bool _registered;

    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>(
            "IsEnabled",
            typeof(AppTextEditingMenu),
            true);

    public static void Register()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        InputElement.PointerPressedEvent.AddClassHandler<TextBox>(
            OnTextBoxPointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        InputElement.PointerReleasedEvent.AddClassHandler<TextBox>(
            OnTextBoxPointerReleased,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        Control.ContextRequestedEvent.AddClassHandler<TextBox>(
            OnTextBoxContextRequested,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
    }

    public static bool GetIsEnabled(TextBox textBox)
    {
        return textBox.GetValue(IsEnabledProperty);
    }

    public static void SetIsEnabled(TextBox textBox, bool value)
    {
        textBox.SetValue(IsEnabledProperty, value);
    }

    private static void OnTextBoxContextRequested(TextBox textBox, ContextRequestedEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        e.Handled = true;

        if (!GetIsEnabled(textBox) || textBox.PasswordChar != default)
        {
            return;
        }

        var placement = e.TryGetPosition(textBox, out _)
            ? PlacementMode.Pointer
            : PlacementMode.BottomEdgeAlignedLeft;
        OpenTextBoxMenu(textBox, placement);
    }

    private static void OnTextBoxPointerPressed(TextBox textBox, PointerPressedEventArgs e)
    {
        if (!PointerPressedGestures.IsSecondaryClick(textBox, e))
        {
            return;
        }

        e.Handled = true;

        if (!GetIsEnabled(textBox) || textBox.PasswordChar != default)
        {
            return;
        }

        OpenTextBoxMenu(textBox, PlacementMode.Pointer);
    }

    private static void OnTextBoxPointerReleased(TextBox textBox, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right)
        {
            return;
        }

        e.Handled = true;
    }

    private static void OpenTextBoxMenu(TextBox textBox, PlacementMode placement)
    {
        var items = BuildMenuItems(textBox);
        if (items.Count == 0)
        {
            return;
        }

        textBox.Focus();
        var verticalOffset = placement == PlacementMode.Pointer ? 0d : 4d;
        AppPopupMenuService.Open(
            textBox,
            items,
            (_, args) => InvokeTextBoxCommand(textBox, args),
            placement,
            verticalOffset);
    }

    private static IReadOnlyList<AppMenuEntry> BuildMenuItems(TextBox textBox)
    {
        var text = CreateTextMap();
        var hasText = !string.IsNullOrEmpty(textBox.Text);
        var canEdit = !textBox.IsReadOnly;
        var items = new List<AppMenuEntry>();

        if (canEdit)
        {
            items.Add(new AppMenuActionItem(
                text.Cut,
                CutCommand,
                IsEnabled: textBox.CanCut));
        }

        items.Add(new AppMenuActionItem(
            text.Copy,
            CopyCommand,
            IsEnabled: textBox.CanCopy));

        if (canEdit)
        {
            items.Add(new AppMenuActionItem(
                text.Paste,
                PasteCommand,
                IsEnabled: textBox.CanPaste));
            items.Add(new AppMenuSeparatorEntry());
        }

        items.Add(new AppMenuActionItem(
            text.SelectAll,
            SelectAllCommand,
            IsEnabled: hasText));

        return items;
    }

    private static void InvokeTextBoxCommand(TextBox textBox, AppMenuItemInvokedEventArgs e)
    {
        if (ReferenceEquals(e.Command, CutCommand))
        {
            textBox.Cut();
            return;
        }

        if (ReferenceEquals(e.Command, CopyCommand))
        {
            textBox.Copy();
            return;
        }

        if (ReferenceEquals(e.Command, PasteCommand))
        {
            textBox.Paste();
            return;
        }

        if (ReferenceEquals(e.Command, SelectAllCommand))
        {
            textBox.SelectAll();
        }
    }

    private static TextEditingMenuText CreateTextMap()
    {
        var runtime = global::MAAUnified.App.App.Runtime;
        var language = runtime is not null
            ? runtime.UiLanguageCoordinator.CurrentLanguage
            : UiLanguageCatalog.FallbackLanguage;
        if (string.IsNullOrWhiteSpace(language))
        {
            language = UiLanguageCatalog.FallbackLanguage;
        }

        var localizer = UiLocalizer.Create(UiLanguageCatalog.Normalize(language));
        return new TextEditingMenuText(
            localizer.GetOrDefault("Common.Edit.Cut", "Cut", Scope),
            localizer.GetOrDefault("Common.Edit.Copy", "Copy", Scope),
            localizer.GetOrDefault("Common.Edit.Paste", "Paste", Scope),
            localizer.GetOrDefault("Common.Edit.SelectAll", "Select all", Scope));
    }

    private sealed record TextEditingMenuText(
        string Cut,
        string Copy,
        string Paste,
        string SelectAll);
}
