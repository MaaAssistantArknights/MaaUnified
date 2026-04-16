using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MAAUnified.App.Infrastructure;
using MAAUnified.Application.Models;
using MAAUnified.App.ViewModels.Infrastructure;

namespace MAAUnified.App.Features.Dialogs;

public partial class EmulatorPathSelectionDialogView : Window, IDialogChromeAware
{
    private readonly ObservableCollection<string> _paths = [];
    private bool _suppressPathSynchronization;

    public EmulatorPathSelectionDialogView()
    {
        InitializeComponent();
        WindowVisuals.ApplyDefaultIcon(this);
        PathList.ItemsSource = _paths;
        PathList.SelectionChanged += OnPathSelectionChanged;
        Opened += OnOpened;
    }

    public void ApplyRequest(EmulatorPathDialogRequest request)
    {
        Title = request.Title;
        DialogShell.Title = request.Title;
        ConfirmButton.Content = request.ConfirmText;
        CancelButton.Content = request.CancelText;
        _paths.Clear();
        foreach (var path in request.CandidatePaths
                     .Where(static path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            _paths.Add(path);
        }

        var selected = request.SelectedPath;
        _suppressPathSynchronization = true;
        PathList.SelectedItem = string.IsNullOrWhiteSpace(selected)
            ? _paths.FirstOrDefault()
            : _paths.FirstOrDefault(p => string.Equals(p, selected, StringComparison.OrdinalIgnoreCase));
        PathInput.Text = selected ?? (PathList.SelectedItem as string) ?? string.Empty;
        _suppressPathSynchronization = false;
        UpdatePathState();
    }

    public EmulatorPathDialogPayload? BuildPayload()
    {
        var selected = (PathInput.Text ?? string.Empty).Trim();
        if (selected.Length == 0)
        {
            return null;
        }

        return new EmulatorPathDialogPayload(selected);
    }

    private void OnPathSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressPathSynchronization)
        {
            return;
        }

        if (PathList.SelectedItem is string selected)
        {
            _suppressPathSynchronization = true;
            PathInput.Text = selected;
            _suppressPathSynchronization = false;
        }

        UpdatePathState();
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

    public void ApplyDialogChrome(DialogChromeSnapshot chrome)
    {
        Title = chrome.Title;
        DialogShell.Title = chrome.GetNamedTextOrDefault(DialogTextCatalog.ChromeKeys.SectionTitle, chrome.Title);
        ConfirmButton.Content = chrome.ConfirmText ?? ConfirmButton.Content;
        CancelButton.Content = chrome.CancelText ?? CancelButton.Content;
    }

    private void OnPathInputTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressPathSynchronization)
        {
            UpdatePathState();
            return;
        }

        var input = (PathInput.Text ?? string.Empty).Trim();
        var matchedPath = _paths.FirstOrDefault(path => string.Equals(path, input, StringComparison.OrdinalIgnoreCase));

        _suppressPathSynchronization = true;
        PathList.SelectedItem = matchedPath;
        _suppressPathSynchronization = false;
        UpdatePathState();
    }

    private void OnPathListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (BuildPayload() is not null)
        {
            Close(DialogReturnSemantic.Confirm);
        }
    }

    private void OnPathListKeyDown(object? sender, KeyEventArgs e)
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

    private void OnPathInputKeyDown(object? sender, KeyEventArgs e)
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
        PathInput.Focus();
        PathInput.CaretIndex = PathInput.Text?.Length ?? 0;
    }

    private void UpdatePathState()
    {
        var currentPath = (PathInput.Text ?? string.Empty).Trim();
        var hasCandidates = _paths.Count > 0;

        ConfirmButton.IsEnabled = currentPath.Length > 0;
        PathList.IsVisible = hasCandidates;
        EmptyStatePanel.IsVisible = !hasCandidates;
        SelectionSummaryText.Text = hasCandidates
            ? $"{_paths.Count} candidate path{(_paths.Count == 1 ? string.Empty : "s")}"
            : "No detected paths";
        DialogIntroText.Text = currentPath.Length > 0
            ? currentPath
            : (hasCandidates
                ? "Pick a detected emulator executable or paste a custom path below."
                : "Paste the emulator executable path below if auto-detection did not find one.");
    }
}
