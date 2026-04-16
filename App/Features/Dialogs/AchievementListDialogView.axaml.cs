using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MAAUnified.App.Infrastructure;
using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.Application.Models;

namespace MAAUnified.App.Features.Dialogs;

public partial class AchievementListDialogView : Window, IDialogChromeAware
{
    private const double PreferredWindowHeight = 820d;
    private const double OwnerHeightThresholdMargin = 24d;
    private const string ProgressFormatKey = "Achievement.ProgressFormat";
    private const string NewBadgeTextKey = "Achievement.NewBadgeText";
    private const string OverviewFormatKey = "Settings.Achievement.Dialog.OverviewFormat";
    private const string FilterAllKey = "Settings.Achievement.Dialog.FilterAll";
    private const string FilterUnlockedKey = "Settings.Achievement.Dialog.FilterUnlocked";
    private const string FilterInProgressKey = "Settings.Achievement.Dialog.FilterInProgress";
    private const string FilterNewKey = "Settings.Achievement.Dialog.FilterNew";
    private const string ResultsFormatKey = "Settings.Achievement.Dialog.ResultsFormat";
    private const string ClearFiltersKey = "Settings.Achievement.Dialog.ClearFilters";
    private const string EmptyTitleKey = "Settings.Achievement.Dialog.EmptyTitle";
    private const string EmptyDescriptionKey = "Settings.Achievement.Dialog.EmptyDescription";

    private readonly RootLocalizationTextMap _texts = new("Root.Localization.Dialog.AchievementList");
    private readonly AchievementListDialogPresenter _presenter = new();

    private string _filterWatermarkSnapshot = "Filter";
    private string _progressFormat = "Progress: {0}";
    private string _newBadgeText = "NEW";
    private string _overviewFormat = "Unlocked {0} / {1} · {2}% complete";
    private string _filterAllText = "All";
    private string _filterUnlockedText = "Unlocked";
    private string _filterInProgressText = "In Progress";
    private string _filterNewText = "New";
    private string _resultsFormat = "Showing {0} achievements";
    private string _clearFiltersText = "Clear filters";
    private string _emptyTitleText = "No matching achievements";
    private string _emptyDescriptionText = "Try a different keyword or filter.";
    private bool _suppressFilterChanged;

    public AchievementListDialogView()
    {
        InitializeComponent();
        WindowVisuals.ApplyDefaultIcon(this);
        Opened += OnOpened;
    }

    public void ApplyRequest(AchievementListDialogRequest request)
    {
        Title = request.Title;
        DialogTitleText.Text = request.Title;
        _filterWatermarkSnapshot = request.FilterWatermark;
        LoadLocalizedText();
        _presenter.ApplyRequest(request, _newBadgeText, _progressFormat);
        FilterInput.Watermark = _filterWatermarkSnapshot;

        _suppressFilterChanged = true;
        FilterInput.Text = request.InitialFilter ?? string.Empty;
        _suppressFilterChanged = false;

        RefreshView();
    }

    public AchievementListDialogPayload BuildPayload()
    {
        return new AchievementListDialogPayload(FilterInput.Text ?? string.Empty, Array.Empty<string>());
    }

    public void ApplyDialogChrome(DialogChromeSnapshot chrome)
    {
        Title = chrome.Title;
        DialogTitleText.Text = chrome.GetNamedTextOrDefault(DialogTextCatalog.ChromeKeys.SectionTitle, chrome.Title);
        FilterInput.Watermark = chrome.GetNamedTextOrDefault(DialogTextCatalog.ChromeKeys.FilterWatermark, _filterWatermarkSnapshot);
        LoadLocalizedText();
        _presenter.UpdateDisplayItems(_newBadgeText, _progressFormat);
        RefreshView();
    }

    private void LoadLocalizedText()
    {
        _progressFormat = Text(ProgressFormatKey, "Progress: {0}");
        _newBadgeText = Text(NewBadgeTextKey, "NEW");
        _overviewFormat = Text(OverviewFormatKey, "Unlocked {0} / {1} · {2}% complete");
        _filterAllText = Text(FilterAllKey, "All");
        _filterUnlockedText = Text(FilterUnlockedKey, "Unlocked");
        _filterInProgressText = Text(FilterInProgressKey, "In Progress");
        _filterNewText = Text(FilterNewKey, "New");
        _resultsFormat = Text(ResultsFormatKey, "Showing {0} achievements");
        _clearFiltersText = Text(ClearFiltersKey, "Clear filters");
        _emptyTitleText = Text(EmptyTitleKey, "No matching achievements");
        _emptyDescriptionText = Text(EmptyDescriptionKey, "Try a different keyword or filter.");

        FilterAllButton.Content = _filterAllText;
        FilterUnlockedButton.Content = _filterUnlockedText;
        FilterInProgressButton.Content = _filterInProgressText;
        FilterNewButton.Content = _filterNewText;
        ClearFiltersButton.Content = _clearFiltersText;
        EmptyStateTitleText.Text = _emptyTitleText;
        EmptyStateDescriptionText.Text = _emptyDescriptionText;
    }

    private void RefreshView()
    {
        var state = _presenter.BuildState(
            _overviewFormat,
            _resultsFormat);

        AchievementItems.ItemsSource = state.Items;
        OverviewText.Text = state.OverviewText;
        ResultCountText.Text = state.ResultsText;
        ClearFiltersButton.IsVisible = state.HasActiveFilters;
        EmptyStatePanel.IsVisible = !state.HasResults;
        AchievementItems.IsVisible = state.HasResults;

        FilterAllButton.Classes.Set("selected", _presenter.ActiveFilter == AchievementQuickFilter.All);
        FilterUnlockedButton.Classes.Set("selected", _presenter.ActiveFilter == AchievementQuickFilter.Unlocked);
        FilterInProgressButton.Classes.Set("selected", _presenter.ActiveFilter == AchievementQuickFilter.InProgress);
        FilterNewButton.Classes.Set("selected", _presenter.ActiveFilter == AchievementQuickFilter.NewUnlock);
    }

    private void OnFilterChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressFilterChanged)
        {
            return;
        }

        _presenter.SetSearchText(FilterInput.Text);
        RefreshView();
    }

    private void OnFilterChipClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }
            || !Enum.TryParse<AchievementQuickFilter>(tag, out var filter))
        {
            return;
        }

        _presenter.SetFilter(filter);
        RefreshView();
    }

    private void OnClearFiltersClick(object? sender, RoutedEventArgs e)
    {
        _presenter.ClearFilters();
        _suppressFilterChanged = true;
        FilterInput.Text = string.Empty;
        _suppressFilterChanged = false;
        RefreshView();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close(DialogReturnSemantic.Close);
    }

    private void OnDragHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        BeginMoveDrag(e);
        e.Handled = true;
    }

    private void OnResizeGripPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || sender is not Control { Tag: string tag }
            || !Enum.TryParse<WindowEdge>(tag, out var edge))
        {
            return;
        }

        BeginResizeDrag(edge, e);
        e.Handled = true;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        ApplyOwnerHeightThreshold();
        FilterInput.Focus();
    }

    private void ApplyOwnerHeightThreshold()
    {
        var ownerHeight = Owner?.Bounds.Height ?? 0d;
        if (ownerHeight <= 0d)
        {
            return;
        }

        var cappedHeight = Math.Max(MinHeight, ownerHeight - OwnerHeightThresholdMargin);
        MaxHeight = cappedHeight;
        Height = Math.Min(Math.Max(Height, PreferredWindowHeight), cappedHeight);
    }

    private string Text(string key, string fallback)
    {
        _texts.Language = App.Runtime.UiLanguageCoordinator.CurrentLanguage;
        return _texts.GetOrDefault(key, fallback);
    }
}

internal enum AchievementQuickFilter
{
    All,
    Unlocked,
    InProgress,
    NewUnlock,
}

internal sealed class AchievementListDialogPresenter
{
    private IReadOnlyList<AchievementListItem> _sourceItems = [];
    private IReadOnlyList<AchievementListDisplayItem> _displayItems = [];
    private int _unlockedCount;
    private int _totalCount;

    public AchievementQuickFilter ActiveFilter { get; private set; } = AchievementQuickFilter.All;

    public string SearchText { get; private set; } = string.Empty;

    public void ApplyRequest(AchievementListDialogRequest request, string newBadgeText, string progressFormat)
    {
        _sourceItems = request.Items;
        _unlockedCount = request.UnlockedCount;
        _totalCount = request.TotalCount;
        SearchText = (request.InitialFilter ?? string.Empty).Trim();
        ActiveFilter = AchievementQuickFilter.All;
        UpdateDisplayItems(newBadgeText, progressFormat);
    }

    public void UpdateDisplayItems(string newBadgeText, string progressFormat)
    {
        _displayItems = _sourceItems
            .Select(item => new AchievementListDisplayItem(item, newBadgeText, progressFormat))
            .ToArray();
    }

    public void SetSearchText(string? filter)
    {
        SearchText = (filter ?? string.Empty).Trim();
    }

    public void SetFilter(AchievementQuickFilter filter)
    {
        ActiveFilter = filter;
    }

    public void ClearFilters()
    {
        SearchText = string.Empty;
        ActiveFilter = AchievementQuickFilter.All;
    }

    public AchievementListDialogViewState BuildState(
        string overviewFormat,
        string resultsFormat)
    {
        var items = _displayItems
            .Where(MatchesFilter)
            .Where(MatchesSearch)
            .ToArray();

        var percent = _totalCount <= 0
            ? 0
            : (int)Math.Round((_unlockedCount * 100d) / _totalCount, MidpointRounding.AwayFromZero);
        var overviewText = string.Format(
            CultureInfo.CurrentCulture,
            overviewFormat,
            _unlockedCount,
            _totalCount,
            percent);
        var resultsText = string.Format(CultureInfo.CurrentCulture, resultsFormat, items.Length);

        return new AchievementListDialogViewState(
            items,
            overviewText,
            resultsText,
            items.Length > 0,
            ActiveFilter != AchievementQuickFilter.All || SearchText.Length > 0);
    }

    private bool MatchesFilter(AchievementListDisplayItem item)
    {
        return ActiveFilter switch
        {
            AchievementQuickFilter.All => true,
            AchievementQuickFilter.Unlocked => item.IsUnlocked,
            AchievementQuickFilter.InProgress => item.ShowProgress,
            AchievementQuickFilter.NewUnlock => item.IsNewUnlock,
            _ => true,
        };
    }

    private bool MatchesSearch(AchievementListDisplayItem item)
    {
        if (SearchText.Length == 0)
        {
            return true;
        }

        return item.Title.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || item.Description.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || item.Status.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || item.Conditions.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed record AchievementListDialogViewState(
    IReadOnlyList<AchievementListDisplayItem> Items,
    string OverviewText,
    string ResultsText,
    bool HasResults,
    bool HasActiveFilters);

internal sealed class AchievementListDisplayItem
{
    public AchievementListDisplayItem(AchievementListItem source, string newBadgeText, string progressFormat)
    {
        Source = source;
        NewBadgeText = newBadgeText;
        var progressToken = source.ShowProgress
            ? string.Format(CultureInfo.CurrentCulture, "{0} / {1}", source.Progress, source.Target)
            : source.Progress.ToString(CultureInfo.CurrentCulture);
        ProgressText = string.Format(CultureInfo.CurrentCulture, progressFormat, progressToken);
    }

    public AchievementListItem Source { get; }

    public string Id => Source.Id;

    public string Title => Source.Title;

    public string Description => Source.Description;

    public string Status => Source.Status;

    public string Conditions => Source.Conditions;

    public bool IsUnlocked => Source.IsUnlocked;

    public bool ShowProgress => Source.ShowProgress;

    public int Target => Math.Max(Source.Target, 1);

    public int ProgressValue => Math.Clamp(Source.Progress, 0, Target);

    public string MedalColor => Source.MedalColor;

    public string UnlockedAtText => Source.UnlockedAtText;

    public bool IsNewUnlock => Source.IsNewUnlock;

    public string NewBadgeText { get; }

    public string ProgressText { get; }

    public bool HasConditions => !string.IsNullOrWhiteSpace(Source.Conditions);

    public bool HasConditionDetails => HasConditions || ShowProgress;

    public bool HasUnlockedAtText => !string.IsNullOrWhiteSpace(Source.UnlockedAtText);
}
