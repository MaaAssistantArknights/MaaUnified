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
    private string _noCandidatesText = "No detected paths";
    private string _candidateCountFormat = "{0} candidate path(s)";
    private string _selectionInstruction = "Pick a detected emulator executable or paste a custom path below.";
    private string _emptyStateBody = "Paste the emulator executable path below if auto-detection did not find one.";

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
        _noCandidatesText = chrome.GetNamedTextOrDefault(
            DialogTextCatalog.ChromeKeys.SelectionSummary,
            _noCandidatesText);
        _candidateCountFormat = chrome.GetNamedTextOrDefault(
            DialogTextCatalog.ChromeKeys.CandidateCount,
            _candidateCountFormat);
        _selectionInstruction = chrome.GetNamedTextOrDefault(
            DialogTextCatalog.ChromeKeys.SelectionInstruction,
            _selectionInstruction);
        _emptyStateBody = chrome.GetNamedTextOrDefault(
            DialogTextCatalog.ChromeKeys.EmptyStateBody,
            _emptyStateBody);
        DetectedPathsTitleText.Text = chrome.GetNamedTextOrDefault(
            DialogTextCatalog.ChromeKeys.SelectionListTitle,
            "Detected paths");
        EmptyStateTitleText.Text = chrome.GetNamedTextOrDefault(
            DialogTextCatalog.ChromeKeys.EmptyStateTitle,
            _noCandidatesText);
        EmptyStateBodyText.Text = _emptyStateBody;
        CustomPathTitleText.Text = chrome.GetNamedTextOrDefault(
            DialogTextCatalog.ChromeKeys.CustomPathTitle,
            "Custom path");
        PathInput.Watermark = chrome.GetNamedTextOrDefault(
            DialogTextCatalog.ChromeKeys.CustomPathWatermark,
            "/path/to/emulator");
        UpdatePathState();
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
        var state = BuildViewState(
            _paths.Count,
            PathInput.Text,
            _noCandidatesText,
            _candidateCountFormat,
            _selectionInstruction,
            _emptyStateBody);

        ConfirmButton.IsEnabled = state.IsConfirmEnabled;
        PathList.IsVisible = state.HasCandidates;
        EmptyStatePanel.IsVisible = !state.HasCandidates;
        SelectionSummaryText.Text = state.Summary;
        DialogIntroText.Text = state.Intro;
    }

    internal static EmulatorPathSelectionViewState BuildViewState(
        int candidateCount,
        string? currentPath,
        string noCandidatesText,
        string candidateCountFormat,
        string selectionInstruction,
        string emptyStateBody)
    {
        var trimmedPath = (currentPath ?? string.Empty).Trim();
        var hasCandidates = candidateCount > 0;
        return new EmulatorPathSelectionViewState(
            HasCandidates: hasCandidates,
            IsConfirmEnabled: trimmedPath.Length > 0,
            Summary: hasCandidates
                ? string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    candidateCountFormat,
                    candidateCount)
                : noCandidatesText,
            Intro: trimmedPath.Length > 0
                ? trimmedPath
                : hasCandidates
                    ? selectionInstruction
                    : emptyStateBody);
    }
}

internal readonly record struct EmulatorPathSelectionViewState(
    bool HasCandidates,
    bool IsConfirmEnabled,
    string Summary,
    string Intro);
