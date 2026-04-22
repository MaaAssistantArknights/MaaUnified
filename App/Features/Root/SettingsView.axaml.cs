using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using settingsViews = MAAUnified.App.Features.Settings;
using MAAUnified.App.ViewModels.Settings;

namespace MAAUnified.App.Features.Root;

public partial class SettingsView : UserControl, INotifyPropertyChanged
{
    private readonly record struct SectionScrollPosition(string SectionKey, double OffsetWithinSection);

    private const int BackgroundSectionWarmupIntervalMs = 45;
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
    private readonly HashSet<string> _materializedSections = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _sectionTopCache = new(StringComparer.OrdinalIgnoreCase);
    private ScrollViewer? _sectionScrollViewer;
    private StackPanel? _sectionContentPanel;
    private bool _suppressSectionSelectionChanged;
    private bool _suppressSectionScrollChanged;
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

    private void OnRailItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: SettingsSectionViewModel section } || VM is null)
        {
            return;
        }

        if (!ReferenceEquals(VM.SelectedSection, section))
        {
            VM.SelectedSection = section;
        }

        RaiseSectionChromePropertyChanged();
    }

    private async void OnSectionActionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: SettingsSectionActionItem action } || VM is null)
        {
            return;
        }

        await VM.ExecuteSectionActionAsync(action);
        RaiseSectionChromePropertyChanged();
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
    }

    private void OnSectionContentPanelSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        InvalidateSectionTopCache();
        RefreshSectionTopCacheIfNeeded();
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
                || !_sectionAnchors.TryGetValue(section.Key, out var anchor))
            {
                continue;
            }

            var point = anchor.TranslatePoint(default, _sectionContentPanel);
            if (point.HasValue)
            {
                _sectionTopCache[section.Key] = point.Value.Y;
            }
        }

        _sectionTopCacheDirty = false;
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

        var pendingSections = SectionOrder
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
            && (_sectionAnchors.TryGetValue(vm.SelectedSection.Key, out var anchor)
                && anchor.TranslatePoint(default, contentPanel) is { } point))
        {
            top = point.Y;
            _sectionTopCache[vm.SelectedSection.Key] = top;
        }

        if (!_sectionTopCache.TryGetValue(vm.SelectedSection.Key, out top))
        {
            return;
        }

        SuppressSectionScrollChangedOnce();
        scrollViewer.Offset = new Vector(
            scrollViewer.Offset.X,
            Math.Max(top, 0d));
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

        return new SectionScrollPosition(
            vm.SelectedSection.Key,
            Math.Max(scrollViewer.Offset.Y - top, 0d));
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
            && (_sectionAnchors.TryGetValue(scrollPosition.Value.SectionKey, out var anchor)
                && anchor.TranslatePoint(default, contentPanel) is { } point))
        {
            top = point.Y;
            _sectionTopCache[scrollPosition.Value.SectionKey] = top;
        }

        if (!_sectionTopCache.TryGetValue(scrollPosition.Value.SectionKey, out top))
        {
            return false;
        }

        var maxOffset = Math.Max(scrollViewer.Extent.Height - scrollViewer.Viewport.Height, 0d);
        var targetOffset = Math.Clamp(top + scrollPosition.Value.OffsetWithinSection, 0d, maxOffset);
        SuppressSectionScrollChangedOnce();
        scrollViewer.Offset = new Vector(scrollViewer.Offset.X, targetOffset);
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

        RefreshSectionTopCacheIfNeeded();
        if (_sectionTopCache.Count == 0)
        {
            return;
        }

        var threshold = scrollViewer.Offset.Y + Math.Max(24d, scrollViewer.Viewport.Height * 0.25d);
        SettingsSectionViewModel? candidate = null;
        var candidateTop = double.MinValue;

        foreach (var section in vm.Sections)
        {
            if (!_sectionTopCache.TryGetValue(section.Key, out var top))
            {
                continue;
            }

            if (top <= threshold && top >= candidateTop)
            {
                candidate = section;
                candidateTop = top;
            }
        }

        candidate ??= vm.Sections.Count > 0 ? vm.Sections[0] : null;
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

            return selectedKey switch
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
        }
    }

    public bool HasCurrentSectionStatusMessage => !string.IsNullOrWhiteSpace(CurrentSectionStatusMessage);

    private void RaiseSectionChromePropertyChanged()
    {
        OnPropertyChanged(nameof(CurrentSectionIntroText));
        OnPropertyChanged(nameof(HasCurrentSectionIntroText));
        OnPropertyChanged(nameof(CurrentSectionStatusTitle));
        OnPropertyChanged(nameof(CurrentSectionStatusMessage));
        OnPropertyChanged(nameof(HasCurrentSectionStatusMessage));
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
