using System.Diagnostics;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using MAAUnified.App.Controls;
using MAAUnified.App.Infrastructure;
using MAAUnified.App.ViewModels;
using MAAUnified.App.ViewModels.Copilot;

namespace MAAUnified.App.Features.Advanced;

public partial class CopilotView : UserControl
{
    private static readonly FilePickerFileType JsonFileType = new("JSON")
    {
        Patterns = ["*.json"],
        MimeTypes = ["application/json"],
    };

    public CopilotView()
    {
        InitializeComponent();
        CopilotListBox.AddHandler(
            InputElement.KeyDownEvent,
            OnCopilotListKeyDown,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        CopilotListBox.AddHandler(
            Control.ContextRequestedEvent,
            OnCopilotListContextRequested,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
    }

    private CopilotPageViewModel? VM => DataContext as CopilotPageViewModel;

    private async void OnFileSelectorEditorCommitted(object? sender, EventArgs e)
    {
        if (VM is null)
        {
            return;
        }

        await VM.LoadCurrentFromDisplayInputAsync();
    }

    private async void OnSelectFileClick(object? sender, RoutedEventArgs e)
    {
        if (VM is null)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is not { CanOpen: true } storageProvider)
        {
            return;
        }

        var files = await storageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = VM.SelectTaskFilePickerTitle,
                AllowMultiple = false,
                FileTypeFilter = [JsonFileType],
            });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            await VM.LoadCurrentFromFileAsync(path);
        }
    }

    private async void OnImportFilesClick(object? sender, RoutedEventArgs e)
    {
        if (VM is null)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is not { CanOpen: true } storageProvider)
        {
            return;
        }

        var files = await storageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = VM.ImportBatchFilePickerTitle,
                AllowMultiple = true,
                FileTypeFilter = [JsonFileType],
            });
        await VM.ImportFilesToListAsync(files.Select(file => file.TryGetLocalPath()).Where(path => !string.IsNullOrWhiteSpace(path)).Cast<string>().ToArray());
    }

    private async void OnPasteClipboardClick(object? sender, RoutedEventArgs e)
    {
        if (VM is null)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        var clipboard = topLevel?.Clipboard;
        if (clipboard is null)
        {
            await VM.LoadCurrentFromClipboardAsync(string.Empty);
            return;
        }

        var file = await Avalonia.Input.Platform.ClipboardExtensions.TryGetFileAsync(clipboard);
        var path = file?.TryGetLocalPath();
        var payload = !string.IsNullOrWhiteSpace(path)
            ? path
            : await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(clipboard) ?? string.Empty;
        await VM.LoadCurrentFromClipboardAsync(payload);
    }

    private async void OnPasteClipboardSetClick(object? sender, RoutedEventArgs e)
    {
        if (VM is null)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        var payload = topLevel?.Clipboard is null
            ? string.Empty
            : await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(topLevel.Clipboard) ?? string.Empty;
        await VM.LoadCurrentFromClipboardSetAsync(payload);
    }

    private void OnCopilotDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = TryGetFirstDroppedJsonFile(e.DataTransfer, out _) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnCopilotDrop(object? sender, DragEventArgs e)
    {
        if (VM is null || !TryGetFirstDroppedJsonFile(e.DataTransfer, out var path))
        {
            return;
        }

        await VM.LoadCurrentFromFileAsync(path);
        e.Handled = true;
    }

    private async void OnFileSelectorSelectionCommitted(object? sender, AppCopilotPathDropdownSelectionCommittedEventArgs e)
    {
        if (VM is null
            || e.SelectedItem is not CopilotPageViewModel.CopilotFileItemViewModel item
            || item.IsFolder)
        {
            return;
        }

        await VM.OnFileSelectedAsync(item);
    }

    private async void OnStartClick(object? sender, RoutedEventArgs e)
    {
        if (VM is not null)
        {
            await VM.StartAsync();
        }
    }

    private async void OnStopClick(object? sender, RoutedEventArgs e)
    {
        if (VM is not null)
        {
            await VM.StopAsync();
        }
    }

    private async void OnAddCurrentToListClick(object? sender, RoutedEventArgs e)
    {
        if (VM is not null)
        {
            await VM.AddCurrentToListAsync(isRaid: false);
        }
    }

    private async void OnAddCurrentToListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (VM is null || sender is not Control control)
        {
            return;
        }

        if (!PointerPressedGestures.IsSecondaryClick(control, e))
        {
            return;
        }

        e.Handled = true;
        await VM.AddCurrentToListAsync(isRaid: true);
    }

    private async void OnClearListClick(object? sender, RoutedEventArgs e)
    {
        if (VM is not null)
        {
            await VM.ConfirmAndClearAllAsync();
        }
    }

    private async void OnClearListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (VM is null || sender is not Control control || !PointerPressedGestures.IsSecondaryClick(control, e))
        {
            return;
        }

        e.Handled = true;
        await VM.CleanInactiveListItemsAsync();
    }

    private async void OnCopilotListItemBodyTapped(object? sender, TappedEventArgs e)
    {
        if (VM is null || sender is not Control { Tag: CopilotItemViewModel item })
        {
            return;
        }

        e.Handled = true;
        await VM.LoadListItemAsync(item, disableListMode: false);
    }

    private void OnCopilotListItemBodyPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (VM is null || sender is not Control { Tag: CopilotItemViewModel item } control)
        {
            return;
        }

        if (!PointerPressedGestures.IsSecondaryClick(control, e))
        {
            return;
        }

        VM.SelectedItem = item;
        OpenCopilotListActionPopup(control, item);
        e.Handled = true;
    }

    private async void OnCopilotListKeyDown(object? sender, KeyEventArgs e)
    {
        if (VM is null
            || CopilotListBox.SelectedItem is not CopilotItemViewModel item)
        {
            return;
        }

        switch (GetCopilotListKeyboardAction(e.Key, e.KeyModifiers, e.Source))
        {
            case CopilotListItemKeyboardAction.Load:
                e.Handled = true;
                await VM.LoadListItemAsync(item, disableListMode: false);
                break;
            case CopilotListItemKeyboardAction.OpenMenu:
                VM.SelectedItem = item;
                OpenCopilotListActionPopup(
                    ResolveCopilotListItemOwner(item),
                    item,
                    PlacementMode.BottomEdgeAlignedLeft);
                e.Handled = true;
                break;
        }
    }

    private void OnCopilotListContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (VM is null
            || CopilotListBox.SelectedItem is not CopilotItemViewModel item)
        {
            return;
        }

        if (!ShouldHandleCopilotListContextRequest(e.Source))
        {
            return;
        }

        var owner = ResolveCopilotListItemOwner(item);
        if (e.TryGetPosition(owner, out _))
        {
            // Pointer context requests are already handled by the item body or delete hotspot.
            return;
        }

        VM.SelectedItem = item;
        OpenCopilotListActionPopup(owner, item, PlacementMode.BottomEdgeAlignedLeft);
        e.Handled = true;
    }

    private async void OnCopilotListItemBodyKeyDown(object? sender, KeyEventArgs e)
    {
        if (VM is null
            || sender is not Control { Tag: CopilotItemViewModel item })
        {
            return;
        }

        switch (GetCopilotListItemKeyboardAction(e.Key, e.KeyModifiers))
        {
            case CopilotListItemKeyboardAction.Load:
                e.Handled = true;
                await VM.LoadListItemAsync(item, disableListMode: false);
                break;
            case CopilotListItemKeyboardAction.OpenMenu:
                VM.SelectedItem = item;
                OpenCopilotListActionPopup((Control)sender, item, PlacementMode.BottomEdgeAlignedLeft);
                e.Handled = true;
                break;
        }
    }

    private void OnCopilotListItemBodyContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (VM is null || sender is not Control { Tag: CopilotItemViewModel item } control)
        {
            return;
        }

        if (e.TryGetPosition(control, out _))
        {
            // Pointer context requests are already handled by OnCopilotListItemBodyPointerPressed.
            return;
        }

        VM.SelectedItem = item;
        OpenCopilotListActionPopup(control, item, PlacementMode.BottomEdgeAlignedLeft);
        e.Handled = true;
    }

    private async void OnCopilotListItemCheckClick(object? sender, RoutedEventArgs e)
    {
        if (VM is null || sender is not CheckBox { DataContext: CopilotItemViewModel item } checkBox)
        {
            return;
        }

        await VM.SetListItemCheckedAsync(item, checkBox.IsChecked == true);
    }

    private async void OnDeleteListItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (VM is null || sender is not Control { Tag: CopilotItemViewModel item } control)
        {
            return;
        }

        if (PointerPressedGestures.IsSecondaryClick(control, e))
        {
            VM.SelectedItem = item;
            OpenCopilotListActionPopup(control, item, PlacementMode.Pointer);
            e.Handled = true;
            return;
        }

        var point = e.GetCurrentPoint(control);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Handled = true;
        await VM.DeleteListItemAsync(item);
    }

    private async void OnDeleteListItemKeyDown(object? sender, KeyEventArgs e)
    {
        if (VM is null || sender is not Control { Tag: CopilotItemViewModel item } control)
        {
            return;
        }

        switch (GetCopilotListItemKeyboardAction(e.Key, e.KeyModifiers))
        {
            case CopilotListItemKeyboardAction.Load:
                e.Handled = true;
                await VM.DeleteListItemAsync(item);
                break;
            case CopilotListItemKeyboardAction.OpenMenu:
                VM.SelectedItem = item;
                OpenCopilotListActionPopup(control, item, PlacementMode.BottomEdgeAlignedLeft);
                e.Handled = true;
                break;
        }
    }

    private void OnDeleteListItemContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (VM is null || sender is not Control { Tag: CopilotItemViewModel item } control)
        {
            return;
        }

        if (e.TryGetPosition(control, out _))
        {
            // Pointer context requests are already handled by OnDeleteListItemPointerPressed.
            return;
        }

        VM.SelectedItem = item;
        OpenCopilotListActionPopup(control, item, PlacementMode.BottomEdgeAlignedLeft);
        e.Handled = true;
    }

    private async void OnCopilotListItemReorderRequested(object? sender, AppSelectionListItemReorderEventArgs e)
    {
        if (VM is null || e.Item is not CopilotItemViewModel item)
        {
            return;
        }

        await VM.MoveListItemToAsync(item, e.TargetIndex);
    }

    private async void OnCopilotListActionPopupItemInvoked(object? sender, AppMenuItemInvokedEventArgs e)
    {
        if (VM is null || e.Parameter is not CopilotListPopupMenuItem item || !item.IsEnabled)
        {
            return;
        }

        switch (item.Action)
        {
            case CopilotListPopupAction.Load:
                await VM.LoadListItemAsync(item.Item, disableListMode: false);
                break;
            case CopilotListPopupAction.LoadSingle:
                await VM.LoadListItemAsync(item.Item, disableListMode: true);
                break;
            case CopilotListPopupAction.ToggleRaid:
                await VM.ToggleListItemRaidAsync(item.Item);
                break;
            case CopilotListPopupAction.Delete:
                await VM.DeleteListItemAsync(item.Item);
                break;
        }
    }

    private void OpenCopilotListActionPopup(
        Control owner,
        CopilotItemViewModel item,
        PlacementMode placement = PlacementMode.Pointer)
    {
        CloseCopilotListActionPopup();

        CopilotListActionPopup.Items = BuildCopilotListMenuItems(item)
            .Where(static menuItem => menuItem.IsVisible)
            .Select(static menuItem => menuItem.ToMenuItem())
            .ToArray();
        CopilotListActionPopup.PlacementTarget = owner;
        CopilotListActionPopup.Placement = placement;
        CopilotListActionPopup.IsOpen = true;
    }

    private Control ResolveCopilotListItemOwner(CopilotItemViewModel item)
    {
        if (CopilotListBox.ContainerFromItem(item) is not Control container)
        {
            return CopilotListBox;
        }

        return container.GetVisualDescendants()
                   .OfType<Border>()
                   .FirstOrDefault(static border => border.Classes.Contains("copilot-list-item-body"))
               ?? container;
    }

    internal static CopilotListItemKeyboardAction GetCopilotListItemKeyboardAction(
        Key key,
        KeyModifiers modifiers)
    {
        return key switch
        {
            Key.Enter or Key.Space => CopilotListItemKeyboardAction.Load,
            Key.Apps => CopilotListItemKeyboardAction.OpenMenu,
            Key.F10 when modifiers.HasFlag(KeyModifiers.Shift) => CopilotListItemKeyboardAction.OpenMenu,
            _ => CopilotListItemKeyboardAction.None,
        };
    }

    internal static CopilotListItemKeyboardAction GetCopilotListKeyboardAction(
        Key key,
        KeyModifiers modifiers,
        object? source)
    {
        return IsCopilotListItemControlSource(source)
            ? CopilotListItemKeyboardAction.None
            : GetCopilotListItemKeyboardAction(key, modifiers);
    }

    internal static bool IsCopilotListItemControlSource(object? source)
    {
        for (var control = source as Control; control is not null; control = control.Parent as Control)
        {
            if (control is Button or ToggleButton || control.Tag is CopilotItemViewModel)
            {
                return true;
            }
        }

        return false;
    }

    internal static bool ShouldHandleCopilotListContextRequest(object? source)
        => !IsCopilotListItemControlSource(source);

    private void CloseCopilotListActionPopup()
    {
        if (CopilotListActionPopup.IsOpen)
        {
            CopilotListActionPopup.IsOpen = false;
        }

        CopilotListActionPopup.Items = null;
    }

    private void OnCopilotListActionPopupClosed(object? sender, EventArgs e)
    {
        CopilotListActionPopup.Items = null;
    }

    private IReadOnlyList<CopilotListPopupMenuItem> BuildCopilotListMenuItems(CopilotItemViewModel item)
    {
        if (VM is null)
        {
            return [];
        }

        return
        [
            new(VM.LoadButtonText, CopilotListPopupAction.Load, item),
            new(VM.CopilotListLoadSingleButtonText, CopilotListPopupAction.LoadSingle, item),
            new(VM.CopilotListToggleRaidButtonText, CopilotListPopupAction.ToggleRaid, item),
            new(VM.DeleteButtonText, CopilotListPopupAction.Delete, item),
        ];
    }

    private void OnOpenUserAdditionalPopupClick(object? sender, RoutedEventArgs e)
    {
        VM?.OpenUserAdditionalPopup();
    }

    private void OnAddUserAdditionalItemClick(object? sender, RoutedEventArgs e)
    {
        VM?.AddUserAdditionalItem();
    }

    private void OnRemoveUserAdditionalItemClick(object? sender, RoutedEventArgs e)
    {
        if (VM is null || sender is not Button button || button.Tag is not CopilotPageViewModel.CopilotUserAdditionalItemViewModel item)
        {
            return;
        }

        VM.RemoveUserAdditionalItem(item);
    }

    private void OnSaveUserAdditionalClick(object? sender, RoutedEventArgs e)
    {
        VM?.SaveUserAdditional();
    }

    private void OnCancelUserAdditionalClick(object? sender, RoutedEventArgs e)
    {
        VM?.CancelUserAdditionalEdit();
    }

    private async void OnLikeLoadedClick(object? sender, RoutedEventArgs e)
    {
        if (VM is not null)
        {
            await VM.SubmitLoadedFeedbackAsync(true);
        }
    }

    private async void OnDislikeLoadedClick(object? sender, RoutedEventArgs e)
    {
        if (VM is not null)
        {
            await VM.SubmitLoadedFeedbackAsync(false);
        }
    }

    private async void OnToggleOverlayClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.DataContext is MainShellViewModel shell)
        {
            await shell.ToggleOverlayFromCopilotAsync();
        }
    }

    private async void OnOverlayButtonPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.DataContext is not MainShellViewModel shell
            || sender is not Control control)
        {
            return;
        }

        if (!PointerPressedGestures.IsSecondaryClick(control, e))
        {
            return;
        }

        e.Handled = true;
        await shell.PickOverlayTargetFromCopilotAsync();
    }

    private void OnOpenCopilotUrlClick(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(VM?.CopilotUrl))
        {
            OpenUrl(VM.CopilotUrl);
        }
    }

    private void OnOpenMapUrlClick(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(VM?.MapUrl))
        {
            OpenUrl(VM.MapUrl);
        }
    }

    private static bool TryGetFirstDroppedJsonFile(IDataTransfer? data, out string filePath)
    {
        filePath = string.Empty;
        var localPath = data?.TryGetFile()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(localPath)
            || !localPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        filePath = localPath;
        return true;
    }

    private static void OpenUrl(string url)
    {
        try
        {
            _ = Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch
        {
            // Keep copilot UI responsive even if shell open fails.
        }
    }

    private sealed record CopilotListPopupMenuItem(
        string Header,
        CopilotListPopupAction Action,
        CopilotItemViewModel Item,
        bool IsEnabled = true,
        bool IsVisible = true)
    {
        public AppMenuActionItem ToMenuItem()
        {
            return new AppMenuActionItem(Header, Action, this, IsEnabled, IsVisible);
        }
    }

    private enum CopilotListPopupAction
    {
        Load,
        LoadSingle,
        ToggleRaid,
        Delete,
    }

    internal enum CopilotListItemKeyboardAction
    {
        None,
        Load,
        OpenMenu,
    }
}
