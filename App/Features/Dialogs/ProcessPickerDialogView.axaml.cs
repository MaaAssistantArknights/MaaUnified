using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MAAUnified.App.Infrastructure;
using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.Application.Models;

namespace MAAUnified.App.Features.Dialogs;

public partial class ProcessPickerDialogView : Window, IDialogChromeAware
{
    private readonly ObservableCollection<ProcessPickerItem> _items = [];
    private ProcessPickerDialogRequest? _request;
    private bool _isRefreshing;
    private string _refreshButtonText = "Refresh";
    private string _refreshingButtonText = "Refreshing...";

    public ProcessPickerDialogView()
    {
        InitializeComponent();
        WindowVisuals.ApplyDefaultIcon(this);
        ProcessList.ItemsSource = _items;
        Opened += OnOpened;
    }

    public void ApplyRequest(ProcessPickerDialogRequest request)
    {
        _request = request;
        Title = request.Title;
        DialogShell.Title = request.Title;
        ConfirmButton.Content = request.ConfirmText;
        CancelButton.Content = request.CancelText;
        _refreshButtonText = RefreshButton.Content?.ToString() ?? "Refresh";
        _refreshingButtonText = _refreshButtonText;
        ApplyItems(request.Items, request.SelectedId);
        RefreshButton.IsVisible = request.RefreshItemsAsync is not null;
        RefreshButton.IsEnabled = request.RefreshItemsAsync is not null;
        _isRefreshing = false;
        RefreshButton.Content = _refreshButtonText;
        UpdateSelectionState();
    }

    public ProcessPickerDialogPayload? BuildPayload()
    {
        if (ProcessList.SelectedItem is not ProcessPickerItem selected)
        {
            return null;
        }

        return new ProcessPickerDialogPayload(selected.Id, selected.DisplayName);
    }

    private async void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        if (_request?.RefreshItemsAsync is not { } refreshItemsAsync)
        {
            return;
        }

        var selectedId = (ProcessList.SelectedItem as ProcessPickerItem)?.Id;
        RefreshButton.IsEnabled = false;
        ConfirmButton.IsEnabled = false;
        _isRefreshing = true;
        RefreshButton.Content = _refreshingButtonText;
        try
        {
            var refreshedItems = await refreshItemsAsync(CancellationToken.None);
            ApplyItems(refreshedItems, selectedId);
        }
        catch
        {
            // Keep existing items/selection when refresh fails.
        }
        finally
        {
            _isRefreshing = false;
            RefreshButton.Content = _refreshButtonText;
            RefreshButton.IsEnabled = true;
            UpdateSelectionState();
        }
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        if (BuildPayload() is null)
        {
            return;
        }

        Close(DialogReturnSemantic.Confirm);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(DialogReturnSemantic.Cancel);
    }

    private void OnShellCloseRequested(object? sender, EventArgs e)
    {
        Close(DialogReturnSemantic.Close);
    }

    private void ApplyItems(IReadOnlyList<ProcessPickerItem> items, string? selectedId)
    {
        _items.Clear();
        foreach (var item in items.OrderByDescending(i => i.IsPrimary).ThenBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            _items.Add(item);
        }

        if (!string.IsNullOrWhiteSpace(selectedId))
        {
            ProcessList.SelectedItem = _items.FirstOrDefault(i => string.Equals(i.Id, selectedId, StringComparison.Ordinal));
        }

        ProcessList.SelectedItem ??= _items.FirstOrDefault();
        UpdateSelectionState();
    }

    public void ApplyDialogChrome(DialogChromeSnapshot chrome)
    {
        Title = chrome.Title;
        DialogShell.Title = chrome.GetNamedTextOrDefault(DialogTextCatalog.ChromeKeys.SectionTitle, chrome.Title);
        ConfirmButton.Content = chrome.ConfirmText ?? ConfirmButton.Content;
        CancelButton.Content = chrome.CancelText ?? CancelButton.Content;
        _refreshButtonText = chrome.GetNamedTextOrDefault(DialogTextCatalog.ChromeKeys.RefreshButton, _refreshButtonText);
        _refreshingButtonText = chrome.GetNamedTextOrDefault(DialogTextCatalog.ChromeKeys.RefreshingButton, _refreshButtonText);
        RefreshButton.Content = _isRefreshing ? _refreshingButtonText : _refreshButtonText;
    }

    private void OnProcessSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateSelectionState();
    }

    private void OnProcessListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (BuildPayload() is not null)
        {
            Close(DialogReturnSemantic.Confirm);
        }
    }

    private void OnProcessListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && BuildPayload() is not null)
        {
            e.Handled = true;
            Close(DialogReturnSemantic.Confirm);
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close(DialogReturnSemantic.Cancel);
        }
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (_items.Count > 0)
        {
            ProcessList.Focus();
        }
    }

    private void UpdateSelectionState()
    {
        var hasItems = _items.Count > 0;
        var selected = ProcessList.SelectedItem as ProcessPickerItem;

        ConfirmButton.IsEnabled = !_isRefreshing && selected is not null;
        ProcessList.IsVisible = hasItems;

        if (EmptyStatePanel is not null)
        {
            EmptyStatePanel.IsVisible = !hasItems;
        }

        if (SelectionSummaryText is not null)
        {
            SelectionSummaryText.Text = hasItems
                ? $"{_items.Count} process{(_items.Count == 1 ? string.Empty : "es")} available"
                : "No running process found";
        }

        if (HintText is not null)
        {
            HintText.Text = selected?.DisplayName
                ?? (hasItems
                    ? "Choose the process that should receive the connection."
                    : "Refresh to scan again, or start the target app first.");
        }
    }
}
