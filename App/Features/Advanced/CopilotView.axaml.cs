using System.Diagnostics;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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
        var payload = topLevel?.Clipboard is null
            ? string.Empty
            : await topLevel.Clipboard.GetTextAsync() ?? string.Empty;
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
            : await topLevel.Clipboard.GetTextAsync() ?? string.Empty;
        await VM.LoadCurrentFromClipboardSetAsync(payload);
    }

    private void OnCopilotDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = TryGetFirstDroppedJsonFile(e.Data, out _) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnCopilotDrop(object? sender, DragEventArgs e)
    {
        if (VM is null || !TryGetFirstDroppedJsonFile(e.Data, out var path))
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

        var point = e.GetCurrentPoint(control);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Handled = true;
        await VM.DeleteListItemAsync(item);
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

    private void OpenCopilotListActionPopup(Control owner, CopilotItemViewModel item)
    {
        CloseCopilotListActionPopup();

        CopilotListActionPopup.Items = BuildCopilotListMenuItems(item)
            .Where(static menuItem => menuItem.IsVisible)
            .Select(static menuItem => menuItem.ToMenuItem())
            .ToArray();
        CopilotListActionPopup.PlacementTarget = owner;
        CopilotListActionPopup.IsOpen = true;
    }

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

    private static bool TryGetFirstDroppedJsonFile(IDataObject data, out string filePath)
    {
        filePath = string.Empty;
        var first = data.GetFiles()?.FirstOrDefault();
        var localPath = first?.TryGetLocalPath();
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
}
