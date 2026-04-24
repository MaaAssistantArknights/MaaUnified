using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MAAUnified.App.Controls;
using settingsViews = MAAUnified.App.Features.Settings;
using MAAUnified.App.ViewModels.Settings;

namespace MAAUnified.App.Features.Root;

public partial class SettingsView : UserControl, INotifyPropertyChanged
{
    private readonly record struct SectionScrollPosition(string SectionKey, double OffsetWithinSection);
    private readonly record struct SectionHeaderLayout(
        SettingsSectionViewModel Section,
        double ContentTop,
        double ViewportTop,
        double HeaderHeight);
    private readonly record struct StickyTitlePresentationState(
        string? CurrentSectionKey,
        AppStickyTitleState PresenterState);

    private const int BackgroundSectionWarmupIntervalMs = 45;
    private const int ProgressiveMaterializationLeadCount = 2;
    private const double StickyActivationPadding = 8d;
    private const double StickyTitleTopInset = 0d;
    private const double StickyTitleBottomInset = 0d;
    private const double StickyTitleRevealLineY = 0d;
    private static readonly string[] SectionOrder =
    [
        "ConfigurationManager",
        "Timer",
        "Performance",
        "Game",
        "Connect",
        "Start",
        "RemoteControl",
        "GUI",
        "Background",
        "ExternalNotification",
        "HotKey",
        "Achievement",
        "VersionUpdate",
        "IssueReport",
        "About",
    ];
    private static readonly object BackgroundSectionWarmupGate = new();
    private static readonly Dictionary<string, Control> PrewarmedSectionContentCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> PrewarmedSectionKeys = new(StringComparer.OrdinalIgnoreCase);
    private static DispatcherTimer? _backgroundSectionWarmupTimer;
    private static int _backgroundSectionWarmupIndex;

    private readonly Dictionary<string, Border> _sectionAnchors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextBlock> _sectionTitleAnchors = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _materializedSections = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _sectionTopCache = new(StringComparer.OrdinalIgnoreCase);
    private ScrollViewer? _sectionScrollViewer;
    private StackPanel? _sectionContentPanel;
    private bool _suppressSectionSelectionChanged;
    private bool _suppressSectionScrollChanged;
    private bool _sectionLayoutRefreshQueued;
    private bool _sectionTopCacheDirty = true;
    private double _lastKnownExtentHeight = -1d;
    private double _lastKnownViewportHeight = -1d;
    private CancellationTokenSource? _progressiveMaterializationCts;
    private readonly HashSet<string> _pendingProgressiveSections = new(StringComparer.OrdinalIgnoreCase);
    private bool _sectionMaterializationInitialized;
    private SettingsPageViewModel? _observedViewModel;
    private bool _viewCompositionActive;
    private SettingsPageViewModel? _viewCompositionOwner;
    private event PropertyChangedEventHandler? ViewPropertyChanged;

    public SettingsView()
    {
        InitializeComponent();
        // Legacy local transforms (_stickyCurrentTitleTransform / _stickyIncomingTitleTransform)
        // are now owned by AppStickyTitlePresenter to keep the animation contract centralized.
        StickyTitlePresenter.State = AppStickyTitleState.Hidden;
        AttachedToVisualTree += (_, _) =>
        {
            BindViewModelNotifications();
            RefreshSectionLayoutReferences();

            BeginViewComposition();
            RebuildSectionAnchors();
            EnsureSectionMaterializationInitialized();
            EnsureCurrentSectionMaterialized();
            Dispatcher.UIThread.Post(ScrollToSelectedSection, DispatcherPriority.Loaded);
            StartProgressiveSectionMaterialization();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            CancelProgressiveSectionMaterialization();
            CompleteViewComposition();
            if (_sectionContentPanel is not null)
            {
                _sectionContentPanel.SizeChanged -= OnSectionContentPanelSizeChanged;
                _sectionContentPanel = null;
            }

            _sectionScrollViewer = null;
            if (_observedViewModel is not null)
            {
                _observedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
                _observedViewModel = null;
            }
        };
        DataContextChanged += (_, _) =>
        {
            CancelProgressiveSectionMaterialization();
            CompleteViewComposition();
            BindViewModelNotifications();
            _sectionMaterializationInitialized = false;
            RaiseSectionChromePropertyChanged();
            Dispatcher.UIThread.Post(() =>
            {
                if (VisualRoot is null)
                {
                    return;
                }

                RefreshSectionLayoutReferences();
                BeginViewComposition();
                RebuildSectionAnchors();
                EnsureSectionMaterializationInitialized(forceReset: true);
                EnsureCurrentSectionMaterialized();
                ScrollToSelectedSection();
                StartProgressiveSectionMaterialization();
            }, DispatcherPriority.Loaded);
        };
    }

    event PropertyChangedEventHandler? INotifyPropertyChanged.PropertyChanged
    {
        add => ViewPropertyChanged += value;
        remove => ViewPropertyChanged -= value;
    }

    private SettingsPageViewModel? VM => DataContext as SettingsPageViewModel;

    public void StartBackgroundWarmup()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(StartBackgroundWarmup, DispatcherPriority.Background);
            return;
        }

        if (VM is null)
        {
            return;
        }

        BindViewModelNotifications();
        RefreshSectionLayoutReferences();
        BeginViewComposition();
        RebuildSectionAnchors();
        EnsureSectionMaterializationInitialized();
        EnsureCurrentSectionMaterialized();
        StartProgressiveSectionMaterialization();
    }

    public static void StartBackgroundSectionWarmup()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(StartBackgroundSectionWarmup, DispatcherPriority.Background);
            return;
        }

        lock (BackgroundSectionWarmupGate)
        {
            if (_backgroundSectionWarmupTimer is not null || PrewarmedSectionKeys.Count >= SectionOrder.Length)
            {
                return;
            }

            _backgroundSectionWarmupIndex = 0;
            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(BackgroundSectionWarmupIntervalMs),
            };
            timer.Tick += OnBackgroundSectionWarmupTick;
            _backgroundSectionWarmupTimer = timer;
            timer.Start();
        }
    }

    private void BindViewModelNotifications()
    {
        if (ReferenceEquals(_observedViewModel, VM))
        {
            return;
        }

        if (_observedViewModel is not null)
        {
            _observedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _observedViewModel = VM;
        if (_observedViewModel is not null)
        {
            _observedViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName)
            || string.Equals(e.PropertyName, nameof(SettingsPageViewModel.SelectedSection), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(SettingsPageViewModel.SelectedSectionTitle), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(SettingsPageViewModel.RootTexts), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(SettingsPageViewModel.Language), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(SettingsPageViewModel.StatusMessage), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(SettingsPageViewModel.RemoteControlStatusMessage), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(SettingsPageViewModel.ExternalNotificationStatusMessage), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(SettingsPageViewModel.HotkeyStatusMessage), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(SettingsPageViewModel.VersionUpdateStatusMessage), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(SettingsPageViewModel.ConfigurationManagerStatusMessage), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(SettingsPageViewModel.AchievementStatusMessage), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(SettingsPageViewModel.IssueReportStatusMessage), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(SettingsPageViewModel.AboutStatusMessage), StringComparison.Ordinal))
        {
            RaiseSectionChromePropertyChanged();
        }

        if (!string.Equals(e.PropertyName, nameof(SettingsPageViewModel.RootTexts), StringComparison.Ordinal)
            && !string.Equals(e.PropertyName, nameof(SettingsPageViewModel.Language), StringComparison.Ordinal))
        {
            return;
        }

        Dispatcher.UIThread.Post(RefreshLocalizedSections, DispatcherPriority.Loaded);
    }

    private void RefreshLocalizedSections()
    {
        if (VM is null || _sectionAnchors.Count == 0)
        {
            return;
        }

        RaiseSectionChromePropertyChanged();
        var scrollPosition = CaptureCurrentSectionScrollPosition();
        CancelProgressiveSectionMaterialization();
        BeginViewComposition();
        EnsureSectionMaterializationInitialized(forceReset: true);
        EnsureCurrentSectionMaterialized(materializeThroughSelection: true);
        Dispatcher.UIThread.Post(
            () =>
            {
                if (!RestoreSectionScrollPosition(scrollPosition))
                {
                    ScrollToSelectedSection();
                }
            },
            DispatcherPriority.Loaded);
        StartProgressiveSectionMaterialization();
    }

    private void RefreshSectionLayoutReferences()
    {
        _sectionScrollViewer = this.FindControl<ScrollViewer>("SectionScrollViewer");
        var contentPanel = this.FindControl<StackPanel>("SectionContentPanel");
        if (ReferenceEquals(_sectionContentPanel, contentPanel))
        {
            return;
        }

        if (_sectionContentPanel is not null)
        {
            _sectionContentPanel.SizeChanged -= OnSectionContentPanelSizeChanged;
        }

        _sectionContentPanel = contentPanel;
        if (_sectionContentPanel is not null)
        {
            _sectionContentPanel.SizeChanged += OnSectionContentPanelSizeChanged;
        }
    }

    private void BeginViewComposition()
    {
        var vm = VM;
        if (_viewCompositionActive && ReferenceEquals(_viewCompositionOwner, vm))
        {
            return;
        }

        CompleteViewComposition();
        _viewCompositionActive = true;
        _viewCompositionOwner = vm;
        vm?.BeginViewComposition();
    }

    private void CompleteViewComposition()
    {
        if (!_viewCompositionActive)
        {
            return;
        }

        var owner = _viewCompositionOwner;
        _viewCompositionActive = false;
        _viewCompositionOwner = null;
        owner?.EndViewComposition();
    }

    private void OnSectionSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSectionSelectionChanged)
        {
            return;
        }

        RaiseSectionChromePropertyChanged();
        Dispatcher.UIThread.Post(ScrollToSelectedSection, DispatcherPriority.Background);
    }

    private void OnSectionScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_suppressSectionScrollChanged)
        {
            return;
        }

        InvalidateSectionTopCacheIfLayoutChanged();
        TryMaterializeNextSectionForScroll();
        UpdateSelectedSectionFromScroll();
        UpdateStickyTitlePresentation();
    }

    private void OnSectionContentPanelSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        InvalidateSectionTopCache();
        QueueSectionLayoutRefresh();
    }

    private void RebuildSectionAnchors()
    {
        _sectionAnchors.Clear();
        RegisterSectionAnchor("ConfigurationManager", "SectionConfigurationManager");
        RegisterSectionAnchor("Timer", "SectionTimer");
        RegisterSectionAnchor("Performance", "SectionPerformance");
        RegisterSectionAnchor("Game", "SectionGame");
        RegisterSectionAnchor("Connect", "SectionConnect");
        RegisterSectionAnchor("Start", "SectionStart");
        RegisterSectionAnchor("RemoteControl", "SectionRemoteControl");
        RegisterSectionAnchor("GUI", "SectionGui");
        RegisterSectionAnchor("Background", "SectionBackground");
        RegisterSectionAnchor("ExternalNotification", "SectionExternalNotification");
        RegisterSectionAnchor("HotKey", "SectionHotKey");
        RegisterSectionAnchor("Achievement", "SectionAchievement");
        RegisterSectionAnchor("VersionUpdate", "SectionVersionUpdate");
        RegisterSectionAnchor("IssueReport", "SectionIssueReport");
        RegisterSectionAnchor("About", "SectionAbout");
        InvalidateSectionTopCache();
    }

    private void RegisterSectionAnchor(string key, string controlName)
    {
        if (this.FindControl<Border>(controlName) is { } anchor)
        {
            _sectionAnchors[key] = anchor;
        }
    }

    private void ResetSectionMaterialization()
    {
        foreach (var anchor in _sectionAnchors.Values)
        {
            anchor.Child = null;
        }

        _sectionTitleAnchors.Clear();
        _materializedSections.Clear();
        InvalidateSectionTopCache();
    }

    private void EnsureSectionMaterializationInitialized(bool forceReset = false)
    {
        if (_sectionMaterializationInitialized && !forceReset)
        {
            return;
        }

        ResetSectionMaterialization();
        _sectionMaterializationInitialized = true;
    }

    private void EnsureCurrentSectionMaterialized(bool materializeThroughSelection = false)
    {
        var selectedKey = VM?.SelectedSection?.Key;
        if (string.IsNullOrWhiteSpace(selectedKey))
        {
            return;
        }

        if (materializeThroughSelection)
        {
            EnsureSectionsThrough(selectedKey);
            return;
        }

        EnsureSectionMaterialized(selectedKey);
    }

    private bool EnsureSectionsThrough(string key)
    {
        var anyMaterialized = false;
        foreach (var sectionKey in SectionOrder)
        {
            if (EnsureSectionMaterialized(sectionKey))
            {
                anyMaterialized = true;
            }

            if (string.Equals(sectionKey, key, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }

        return anyMaterialized;
    }

    private bool EnsureSectionMaterialized(string key)
        => EnsureSectionMaterializedCore(key, loadDeferredData: true);

    private bool EnsureSectionMaterializedCore(string key, bool loadDeferredData)
    {
        if (string.IsNullOrWhiteSpace(key)
            || _materializedSections.Contains(key)
            || !_sectionAnchors.TryGetValue(key, out var anchor))
        {
            return false;
        }

        var content = CreateSectionContent(key);
        if (content is null)
        {
            return false;
        }

        ApplySectionDataContext(key, content);
        MarkSectionWarmupPrepared(key);
        anchor.Child = content;
        UpdateSectionTitleAnchor(key, content);
        _materializedSections.Add(key);
        InvalidateSectionTopCache();
        if (loadDeferredData && VM is { } vm)
        {
            _ = vm.EnsureSectionDataLoadedAsync(key);
        }

        return true;
    }

    private Control? CreateSectionContent(string key)
    {
        if (TryTakePrewarmedSectionContent(key, out var prewarmedContent))
        {
            return prewarmedContent;
        }

        return CreateSectionContentCore(key);
    }

    private static Control? CreateSectionContentCore(string key)
    {
        return key switch
        {
            "ConfigurationManager" => new settingsViews.ConfigurationManagerView(),
            "Timer" => new settingsViews.TimerSettingsView(),
            "Performance" => new settingsViews.PerformanceSettingsView(),
            "Game" => new settingsViews.GameSettingsView(),
            "Connect" => new settingsViews.ConnectSettingsView(),
            "Start" => new settingsViews.StartSettingsView(),
            "RemoteControl" => new settingsViews.RemoteControlSettingsView(),
            "GUI" => new settingsViews.GuiSettingsView(),
            "Background" => new settingsViews.BackgroundSettingsView(),
            "ExternalNotification" => new settingsViews.ExternalNotificationSettingsView(),
            "HotKey" => new settingsViews.HotKeySettingsView(),
            "Achievement" => new settingsViews.AchievementSettingsView(),
            "VersionUpdate" => new settingsViews.VersionUpdateSettingsView(),
            "IssueReport" => new settingsViews.IssueReportView(),
            "About" => new settingsViews.AboutSettingsView(),
            _ => null,
        };
    }

    private void ApplySectionDataContext(string key, Control content)
    {
        if (VM is not { } vm)
        {
            return;
        }

        content.DataContext = string.Equals(key, "Connect", StringComparison.OrdinalIgnoreCase)
            ? vm.ConnectionGameSharedState
            : vm;
    }

    private void UpdateSectionTitleAnchor(string key, Control? content)
    {
        _sectionTitleAnchors.Remove(key);
        if (content is null)
        {
            return;
        }

        if (FindSectionTitleAnchor(content) is { } title)
        {
            _sectionTitleAnchors[key] = title;
        }
    }

    private static TextBlock? FindSectionTitleAnchor(Control content)
    {
        if (content is TextBlock directTitle && directTitle.Classes.Contains("settings-page-title"))
        {
            return directTitle;
        }

        foreach (var title in content.GetVisualDescendants().OfType<TextBlock>())
        {
            if (title.Classes.Contains("settings-page-title"))
            {
                return title;
            }
        }

        return null;
    }

    private void InvalidateSectionTopCache()
    {
        _sectionTopCacheDirty = true;
    }

    private void InvalidateSectionTopCacheIfLayoutChanged()
    {
        if (_sectionScrollViewer is null)
        {
            return;
        }

        var extentHeight = _sectionScrollViewer.Extent.Height;
        var viewportHeight = _sectionScrollViewer.Viewport.Height;
        if (Math.Abs(extentHeight - _lastKnownExtentHeight) > 0.5d
            || Math.Abs(viewportHeight - _lastKnownViewportHeight) > 0.5d)
        {
            _lastKnownExtentHeight = extentHeight;
            _lastKnownViewportHeight = viewportHeight;
            InvalidateSectionTopCache();
        }
    }

    private void RefreshSectionTopCacheIfNeeded()
    {
        if (!_sectionTopCacheDirty || _sectionContentPanel is null || VM is null)
        {
            return;
        }

        _sectionTopCache.Clear();
        foreach (var section in VM.Sections)
        {
            if (!_materializedSections.Contains(section.Key)
                || !TryResolveSectionTop(section.Key, out var top))
            {
                continue;
            }

            _sectionTopCache[section.Key] = top;
        }

        _sectionTopCacheDirty = false;
    }

    private void QueueSectionLayoutRefresh()
    {
        if (_sectionLayoutRefreshQueued)
        {
            return;
        }

        _sectionLayoutRefreshQueued = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _sectionLayoutRefreshQueued = false;
                RefreshSectionTopCacheIfNeeded();
                UpdateStickyTitlePresentation();
            },
            DispatcherPriority.Render);
    }

    private bool TryResolveSectionTop(string key, out double top)
    {
        if (_sectionContentPanel is not null
            && _sectionTitleAnchors.TryGetValue(key, out var titleAnchor)
            && titleAnchor.TranslatePoint(default, _sectionContentPanel) is { } titlePoint)
        {
            top = titlePoint.Y;
            return true;
        }

        if (_sectionContentPanel is not null
            && _sectionAnchors.TryGetValue(key, out var sectionAnchor)
            && sectionAnchor.TranslatePoint(default, _sectionContentPanel) is { } sectionPoint)
        {
            top = sectionPoint.Y;
            return true;
        }

        top = 0d;
        return false;
    }

    private void TryMaterializeNextSectionForScroll()
    {
        var scrollViewer = _sectionScrollViewer;
        if (scrollViewer is null)
        {
            return;
        }

        var threshold = Math.Max(180d, scrollViewer.Viewport.Height * 0.35d);
        var viewportBottom = scrollViewer.Offset.Y + scrollViewer.Viewport.Height;
        if (viewportBottom < scrollViewer.Extent.Height - threshold)
        {
            return;
        }

        TryMaterializeNextSectionInOrder();
    }

    private bool TryMaterializeNextSectionInOrder()
    {
        foreach (var sectionKey in SectionOrder)
        {
            if (_materializedSections.Contains(sectionKey))
            {
                continue;
            }

            return EnsureSectionMaterialized(sectionKey);
        }

        return false;
    }

    private void StartProgressiveSectionMaterialization()
    {
        CancelProgressiveSectionMaterialization();
        if (VM is not { } vm)
        {
            CompleteViewComposition();
            return;
        }

        var pendingSections = ResolveProgressiveMaterializationTargets(vm.SelectedSection?.Key)
            .Where(sectionKey => !_materializedSections.Contains(sectionKey))
            .ToArray();
        if (pendingSections.Length == 0)
        {
            CompleteViewComposition();
            return;
        }

        _progressiveMaterializationCts = new CancellationTokenSource();
        foreach (var sectionKey in pendingSections)
        {
            _pendingProgressiveSections.Add(sectionKey);
            _ = MaterializeSectionWhenReadyAsync(vm, sectionKey, _progressiveMaterializationCts.Token);
        }
    }

    private static IEnumerable<string> ResolveProgressiveMaterializationTargets(string? selectedKey)
    {
        if (SectionOrder.Length == 0)
        {
            yield break;
        }

        var startIndex = 0;
        if (!string.IsNullOrWhiteSpace(selectedKey))
        {
            var selectedIndex = Array.FindIndex(
                SectionOrder,
                sectionKey => string.Equals(sectionKey, selectedKey, StringComparison.OrdinalIgnoreCase));
            if (selectedIndex >= 0)
            {
                startIndex = selectedIndex;
            }
        }

        var endIndex = Math.Min(SectionOrder.Length - 1, startIndex + ProgressiveMaterializationLeadCount);
        for (var i = startIndex; i <= endIndex; i++)
        {
            yield return SectionOrder[i];
        }
    }

    private void CancelProgressiveSectionMaterialization()
    {
        _pendingProgressiveSections.Clear();
        _progressiveMaterializationCts?.Cancel();
        _progressiveMaterializationCts?.Dispose();
        _progressiveMaterializationCts = null;
    }

    private async Task MaterializeSectionWhenReadyAsync(
        SettingsPageViewModel owner,
        string sectionKey,
        CancellationToken cancellationToken)
    {
        try
        {
            await owner.EnsureSectionDataLoadedAsync(sectionKey, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }

        await Dispatcher.UIThread.InvokeAsync(
            () =>
            {
                if (!cancellationToken.IsCancellationRequested
                    && ReferenceEquals(VM, owner))
                {
                    EnsureSectionMaterializedCore(sectionKey, loadDeferredData: false);
                }

                CompleteProgressiveSectionMaterialization(owner, sectionKey);
            },
            DispatcherPriority.Background);
    }

    private void CompleteProgressiveSectionMaterialization(SettingsPageViewModel owner, string sectionKey)
    {
        if (!ReferenceEquals(_viewCompositionOwner, owner))
        {
            return;
        }

        _pendingProgressiveSections.Remove(sectionKey);
        if (_pendingProgressiveSections.Count > 0)
        {
            return;
        }

        _progressiveMaterializationCts?.Dispose();
        _progressiveMaterializationCts = null;
        CompleteViewComposition();
    }

    private static void OnBackgroundSectionWarmupTick(object? sender, EventArgs e)
    {
        if (TryPrewarmNextSectionInOrder())
        {
            return;
        }

        CancelBackgroundSectionWarmup();
    }

    private static bool TryPrewarmNextSectionInOrder()
    {
        while (_backgroundSectionWarmupIndex < SectionOrder.Length)
        {
            var sectionKey = SectionOrder[_backgroundSectionWarmupIndex++];
            if (PrewarmedSectionKeys.Contains(sectionKey))
            {
                continue;
            }

            var content = CreateSectionContentCore(sectionKey);
            if (content is null)
            {
                continue;
            }

            PrewarmedSectionContentCache[sectionKey] = content;
            PrewarmedSectionKeys.Add(sectionKey);
            return true;
        }

        return false;
    }

    private static bool TryTakePrewarmedSectionContent(string key, out Control? content)
    {
        lock (BackgroundSectionWarmupGate)
        {
            if (PrewarmedSectionContentCache.Remove(key, out var cachedContent))
            {
                content = cachedContent;
                return true;
            }
        }

        content = null;
        return false;
    }

    private static void MarkSectionWarmupPrepared(string key)
    {
        lock (BackgroundSectionWarmupGate)
        {
            PrewarmedSectionKeys.Add(key);
        }
    }

    private static void CancelBackgroundSectionWarmup()
    {
        lock (BackgroundSectionWarmupGate)
        {
            var timer = _backgroundSectionWarmupTimer;
            _backgroundSectionWarmupTimer = null;
            if (timer is null)
            {
                return;
            }

            timer.Stop();
            timer.Tick -= OnBackgroundSectionWarmupTick;
        }
    }

    private void ScrollToSelectedSection()
    {
        var vm = VM;
        var scrollViewer = _sectionScrollViewer;
        var contentPanel = _sectionContentPanel;
        if (vm?.SelectedSection is null || scrollViewer is null || contentPanel is null)
        {
            return;
        }

        EnsureSectionsThrough(vm.SelectedSection.Key);
        RefreshSectionTopCacheIfNeeded();

        if (!_sectionTopCache.TryGetValue(vm.SelectedSection.Key, out var top)
            && TryResolveSectionTop(vm.SelectedSection.Key, out var resolvedTop))
        {
            top = resolvedTop;
            _sectionTopCache[vm.SelectedSection.Key] = top;
        }

        if (!_sectionTopCache.TryGetValue(vm.SelectedSection.Key, out top))
        {
            return;
        }

        var targetOffset = ComputeSectionTargetOffset(top, GetSectionScrollTargetLineY());
        SuppressSectionScrollChangedOnce();
        scrollViewer.Offset = new Vector(
            scrollViewer.Offset.X,
            Math.Max(targetOffset, 0d));
        UpdateStickyTitlePresentation();
        StartProgressiveSectionMaterialization();
    }

    private SectionScrollPosition? CaptureCurrentSectionScrollPosition()
    {
        var vm = VM;
        var scrollViewer = _sectionScrollViewer;
        if (vm?.SelectedSection is null || scrollViewer is null)
        {
            return null;
        }

        EnsureSectionsThrough(vm.SelectedSection.Key);
        RefreshSectionTopCacheIfNeeded();
        if (!_sectionTopCache.TryGetValue(vm.SelectedSection.Key, out var top))
        {
            return null;
        }

        var sectionOffset = ComputeSectionTargetOffset(top, GetSectionScrollTargetLineY());
        return new SectionScrollPosition(
            vm.SelectedSection.Key,
            Math.Max(scrollViewer.Offset.Y - sectionOffset, 0d));
    }

    private bool RestoreSectionScrollPosition(SectionScrollPosition? scrollPosition)
    {
        var scrollViewer = _sectionScrollViewer;
        var contentPanel = _sectionContentPanel;
        if (scrollPosition is null || scrollViewer is null || contentPanel is null)
        {
            return false;
        }

        EnsureSectionsThrough(scrollPosition.Value.SectionKey);
        RefreshSectionTopCacheIfNeeded();

        if (!_sectionTopCache.TryGetValue(scrollPosition.Value.SectionKey, out var top)
            && TryResolveSectionTop(scrollPosition.Value.SectionKey, out var resolvedTop))
        {
            top = resolvedTop;
            _sectionTopCache[scrollPosition.Value.SectionKey] = top;
        }

        if (!_sectionTopCache.TryGetValue(scrollPosition.Value.SectionKey, out top))
        {
            return false;
        }

        var maxOffset = Math.Max(scrollViewer.Extent.Height - scrollViewer.Viewport.Height, 0d);
        var sectionOffset = ComputeSectionTargetOffset(top, GetSectionScrollTargetLineY());
        var targetOffset = Math.Clamp(sectionOffset + scrollPosition.Value.OffsetWithinSection, 0d, maxOffset);
        SuppressSectionScrollChangedOnce();
        scrollViewer.Offset = new Vector(scrollViewer.Offset.X, targetOffset);
        UpdateStickyTitlePresentation();
        return true;
    }

    private void UpdateSelectedSectionFromScroll()
    {
        var vm = VM;
        var scrollViewer = _sectionScrollViewer;
        if (vm is null || scrollViewer is null || _sectionAnchors.Count == 0)
        {
            return;
        }

        var headerLayouts = MeasureSectionHeaderLayouts(Math.Max(scrollViewer.Offset.Y, 0d));
        if (headerLayouts.Count == 0)
        {
            return;
        }

        var selectedIndex = ResolveActiveSectionIndex(
            Math.Max(scrollViewer.Offset.Y, 0d),
            GetSectionActivationLineY(),
            headerLayouts.Select(static layout => layout.ContentTop).ToArray());
        SettingsSectionViewModel? candidate = selectedIndex >= 0
            ? headerLayouts[selectedIndex].Section
            : (vm.Sections.Count > 0 ? vm.Sections[0] : null);
        if (candidate is null || ReferenceEquals(candidate, vm.SelectedSection))
        {
            return;
        }

        SuppressSectionSelectionChangedOnce();
        vm.SelectedSection = candidate;
    }

    private void SuppressSectionSelectionChangedOnce()
    {
        _suppressSectionSelectionChanged = true;
        Dispatcher.UIThread.Post(
            () => _suppressSectionSelectionChanged = false,
            DispatcherPriority.Background);
    }

    private void SuppressSectionScrollChangedOnce()
    {
        _suppressSectionScrollChanged = true;
        Dispatcher.UIThread.Post(
            () => _suppressSectionScrollChanged = false,
            DispatcherPriority.Background);
    }

    private void UpdateStickyTitlePresentation()
    {
        ApplyStickyTitlePresentation(CalculateStickyTitlePresentation());
    }

    private StickyTitlePresentationState CalculateStickyTitlePresentation()
    {
        var vm = VM;
        var scrollViewer = _sectionScrollViewer;
        if (vm is null || vm.Sections.Count == 0 || scrollViewer is null)
        {
            return new StickyTitlePresentationState(null, AppStickyTitleState.Hidden);
        }

        var headerLayouts = MeasureSectionHeaderLayouts(Math.Max(scrollViewer.Offset.Y, 0d));
        if (headerLayouts.Count == 0)
        {
            return new StickyTitlePresentationState(null, AppStickyTitleState.Hidden);
        }

        var currentIndex = StickyTitleMath.ResolvePinnedHeaderIndex(
            headerLayouts.Select(static layout => layout.ViewportTop).ToArray(),
            StickyTitleRevealLineY);
        if (currentIndex < 0)
        {
            return new StickyTitlePresentationState(null, AppStickyTitleState.Hidden);
        }

        var currentLayout = headerLayouts[currentIndex];
        var nextLayout = currentIndex + 1 < headerLayouts.Count
            ? headerLayouts[currentIndex + 1]
            : (SectionHeaderLayout?)null;
        var height = GetStickyPresentationHeight(currentLayout, nextLayout);
        var pushOffset = CalculatePushOffset(nextLayout, height);
        if (nextLayout is null)
        {
            return new StickyTitlePresentationState(
                currentLayout.Section.Key,
                new AppStickyTitleState(
                    IsVisible: true,
                    Height: height,
                    CurrentTitle: currentLayout.Section.DisplayName,
                    CurrentTranslateY: 0d,
                    IncomingTitle: null,
                    IncomingTranslateY: height,
                    ShowIncomingTitle: false));
        }

        return new StickyTitlePresentationState(
            currentLayout.Section.Key,
            new AppStickyTitleState(
                IsVisible: true,
                Height: height,
                CurrentTitle: currentLayout.Section.DisplayName,
                CurrentTranslateY: -pushOffset,
                IncomingTitle: pushOffset > 0d ? nextLayout.Value.Section.DisplayName : null,
                IncomingTranslateY: height - pushOffset,
                ShowIncomingTitle: pushOffset > 0d));
    }

    private IReadOnlyList<SectionHeaderLayout> MeasureSectionHeaderLayouts(double offsetY)
    {
        RefreshSectionTopCacheIfNeeded();
        if (VM is null || _sectionTopCache.Count == 0)
        {
            return [];
        }

        var layouts = new List<SectionHeaderLayout>(VM.Sections.Count);
        foreach (var section in VM.Sections)
        {
            if (!_sectionTopCache.TryGetValue(section.Key, out var top))
            {
                continue;
            }

            var headerHeight = 0d;
            if (_sectionTitleAnchors.TryGetValue(section.Key, out var titleAnchor))
            {
                headerHeight = Math.Max(titleAnchor.Bounds.Height, titleAnchor.DesiredSize.Height);
            }

            layouts.Add(new SectionHeaderLayout(
                section,
                top,
                top - offsetY,
                Math.Max(headerHeight, 1d)));
        }

        return layouts;
    }

    private double GetSectionActivationLineY()
    {
        return Math.Max(18d, GetStickyReferenceHeight() + StickyActivationPadding);
    }

    private double GetSectionScrollTargetLineY()
    {
        return StickyTitleRevealLineY;
    }

    private double GetStickyReferenceHeight()
    {
        var tallestSectionHeader = _sectionTitleAnchors.Count == 0
            ? 0d
            : _sectionTitleAnchors.Values.Max(title => Math.Max(title.Bounds.Height, title.DesiredSize.Height));
        return Math.Max(
            1d,
            Math.Max(
                GetSectionHeaderVisualHeight(tallestSectionHeader),
                StickyTitlePresenter.Bounds.Height));
    }

    private double GetStickyPresentationHeight(SectionHeaderLayout currentLayout, SectionHeaderLayout? nextLayout)
    {
        return Math.Max(
            1d,
            Math.Max(
                GetStickyReferenceHeight(),
                Math.Max(
                    GetSectionHeaderVisualHeight(currentLayout.HeaderHeight),
                    GetSectionHeaderVisualHeight(nextLayout?.HeaderHeight ?? 0d))));
    }

    private static double GetSectionHeaderVisualHeight(double headerHeight)
    {
        return Math.Max(1d, headerHeight + StickyTitleTopInset + StickyTitleBottomInset);
    }

    private static double CalculatePushOffset(SectionHeaderLayout? nextLayout, double stickyHeight)
    {
        if (nextLayout is null || stickyHeight <= 0d)
        {
            return 0d;
        }

        return StickyTitleMath.ComputePushOffset(nextLayout.Value.ViewportTop, stickyHeight);
    }

    private static double ComputeSectionTargetOffset(double headerContentTop, double activationLineY)
    {
        return StickyTitleMath.ComputeSectionTargetOffset(headerContentTop, activationLineY);
    }

    private static int ResolveActiveSectionIndex(double offsetY, double activationLineY, IReadOnlyList<double> headerContentTops)
    {
        return StickyTitleMath.ResolveActiveSectionIndex(offsetY, activationLineY, headerContentTops);
    }

    private void ApplyStickyTitlePresentation(StickyTitlePresentationState state)
    {
        UpdateSectionTitleAnchorVisibility(state.CurrentSectionKey);
        StickyTitlePresenter.State = state.PresenterState;
    }

    private void UpdateSectionTitleAnchorVisibility(string? hiddenSectionKey)
    {
        foreach (var (sectionKey, titleAnchor) in _sectionTitleAnchors)
        {
            titleAnchor.Opacity = string.Equals(sectionKey, hiddenSectionKey, StringComparison.OrdinalIgnoreCase)
                ? 0d
                : 1d;
        }
    }

    public string CurrentSectionIntroText
    {
        get
        {
            if (VM?.SelectedSection?.Key is not { Length: > 0 } key || VM.RootTexts is null)
            {
                return string.Empty;
            }

            return VM.RootTexts.GetOrDefault($"Settings.Section.{key}.Intro", string.Empty);
        }
    }

    public bool HasCurrentSectionIntroText => !string.IsNullOrWhiteSpace(CurrentSectionIntroText);

    public string CurrentSectionStatusTitle
    {
        get
        {
            if (VM?.RootTexts is null)
            {
                return string.Empty;
            }

        return VM.RootTexts.GetOrDefault("Settings.Section.Status.Title", "Status");
        }
    }

    public string CurrentSectionStatusMessage
    {
        get
        {
            if (VM is null)
            {
                return string.Empty;
            }

            var selectedKey = VM.SelectedSection?.Key;
            if (string.IsNullOrWhiteSpace(selectedKey))
            {
                return string.Empty;
            }

            var message = selectedKey switch
            {
                "ConfigurationManager" => FirstNonEmpty(VM.ConfigurationManagerStatusMessage, VM.StatusMessage),
                "RemoteControl" => FirstNonEmpty(VM.RemoteControlStatusMessage, VM.StatusMessage),
                "ExternalNotification" => FirstNonEmpty(VM.ExternalNotificationStatusMessage, VM.StatusMessage),
                "HotKey" => FirstNonEmpty(VM.HotkeyStatusMessage, VM.StatusMessage),
                "Achievement" => FirstNonEmpty(VM.AchievementStatusMessage, VM.StatusMessage),
                "VersionUpdate" => FirstNonEmpty(VM.VersionUpdateStatusMessage, VM.StatusMessage),
                "IssueReport" => FirstNonEmpty(VM.IssueReportStatusMessage, VM.StatusMessage),
                "About" => FirstNonEmpty(VM.AboutStatusMessage, VM.StatusMessage),
                _ => VM.StatusMessage,
            };

            return IsTransientSaveStateMessage(message) ? string.Empty : message;
        }
    }

    private bool IsTransientSaveStateMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message) || VM?.RootTexts is null)
        {
            return false;
        }

        var savingText = VM.RootTexts.GetOrDefault("Settings.State.Saving", string.Empty);
        var savedText = VM.RootTexts.GetOrDefault("Settings.State.Saved", string.Empty);
        return string.Equals(message, savingText, StringComparison.Ordinal)
            || string.Equals(message, savedText, StringComparison.Ordinal);
    }

    public bool HasCurrentSectionStatusMessage => !string.IsNullOrWhiteSpace(CurrentSectionStatusMessage);

    private void RaiseSectionChromePropertyChanged()
    {
        OnPropertyChanged(nameof(CurrentSectionIntroText));
        OnPropertyChanged(nameof(HasCurrentSectionIntroText));
        OnPropertyChanged(nameof(CurrentSectionStatusTitle));
        OnPropertyChanged(nameof(CurrentSectionStatusMessage));
        OnPropertyChanged(nameof(HasCurrentSectionStatusMessage));
        Dispatcher.UIThread.Post(UpdateStickyTitlePresentation, DispatcherPriority.Background);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        ViewPropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static string FirstNonEmpty(string primary, string fallback)
    {
        return !string.IsNullOrWhiteSpace(primary) ? primary : fallback;
    }
}
