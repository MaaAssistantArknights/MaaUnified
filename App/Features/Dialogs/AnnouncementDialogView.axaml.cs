using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using MarkdownViewerControl = MarkdownViewer.Core.Controls.MarkdownViewer;
using MAAUnified.App.Controls;
using MAAUnified.App.Infrastructure;
using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.Application.Models;
using MAAUnified.Application.Services.Localization;

namespace MAAUnified.App.Features.Dialogs;

public partial class AnnouncementDialogView : Window, IDialogChromeAware
{
    private const double BottomReachedTolerance = 12d;
    private const double SectionActivationLineY = 18d;
    private const double SectionScrollTargetLineY = 0d;

    private const string NewBadgeKey = "Achievement.NewBadgeText";
    private const string ChineseImageUri = "avares://MAAUnified/Assets/Announcement/NoSkland.jpg";
    private const string EnglishImageUri = "avares://MAAUnified/Assets/Announcement/NoSklandEn.jpg";

    private readonly RootLocalizationTextMap _texts = new("Root.Localization.Dialog.Announcement");
    private readonly List<AnnouncementSectionDisplayItem> _sections = [];
    private readonly List<SectionHeaderAnchor> _sectionHeaderAnchors = [];
    private bool _doNotShowAnnouncement;
    private bool _hasEverScrolledToBottom;
    private int _blockedCloseAttemptCount;
    private string _confirmTextSnapshot = "Confirm";
    private string _confirmLockedText = "Read announcement first";
    private string[] _blockedConfirmMessages =
    [
        "You haven't finished reading. Please confirm after you finish.",
        "Yes, you - still clicking",
        "Still clicking",
    ];
    private string _newBadgeText = "NEW";
    private bool _suppressSectionSelectionChanged;

    public AnnouncementDialogView()
    {
        InitializeComponent();
        WindowVisuals.ApplyDefaultIcon(this);
        Opened += OnOpened;
        KeyDown += OnWindowKeyDown;
    }

    public void ApplyRequest(AnnouncementDialogRequest request)
    {
        Title = request.Title;
        DialogShell.Title = request.Title;
        _confirmTextSnapshot = request.ConfirmText;
        _doNotShowAnnouncement = request.DoNotShowAnnouncement;
        _blockedCloseAttemptCount = 0;
        _hasEverScrolledToBottom = false;
        LoadLocalizedTexts();
        DoNotRemindBox.IsChecked = request.DoNotRemindThisAnnouncementAgain;
        DoNotRemindBox.IsEnabled = request.DoNotRemindThisAnnouncementAgain;
        AnnouncementHeroImage.Source = LoadAnnouncementHeroImage();
        BuildSections(request.Title, request.AnnouncementInfo);
        ConfirmButton.Content = _confirmTextSnapshot;
        UpdateReadDependentUi();
    }

    public AnnouncementDialogPayload BuildPayload()
    {
        return new AnnouncementDialogPayload(
            DoNotRemindThisAnnouncementAgain: DoNotRemindBox.IsChecked ?? false,
            DoNotShowAnnouncement: _doNotShowAnnouncement);
    }

    public void ApplyDialogChrome(DialogChromeSnapshot chrome)
    {
        var title = chrome.GetNamedTextOrDefault(DialogTextCatalog.ChromeKeys.SectionTitle, chrome.Title);
        Title = chrome.Title;
        DialogShell.Title = title;
        _confirmTextSnapshot = chrome.ConfirmText ?? _confirmTextSnapshot;
        LoadLocalizedTexts();
        UpdateReadDependentUi();
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        TryCompleteDialog(DialogReturnSemantic.Confirm);
    }

    private void OnShellCloseRequested(object? sender, EventArgs e)
    {
        TryCompleteDialog(DialogReturnSemantic.Close);
    }

    private void OnSectionSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSectionSelectionChanged)
        {
            return;
        }

        if (SectionList.SelectedItem is not AnnouncementSectionDisplayItem selected)
        {
            return;
        }

        NavigateToSection(selected);
    }

    private void OnMarkdownScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        UpdateScrollSynchronizedUi();
        if (HasReachedBottom(MarkdownHost.Extent.Height, MarkdownHost.Viewport.Height, MarkdownHost.Offset.Y))
        {
            _hasEverScrolledToBottom = true;
            UpdateReadDependentUi();
        }
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        MarkdownHost.Focus();
        Dispatcher.UIThread.Post(EvaluateReadStateFromCurrentViewport, DispatcherPriority.Background);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        if (!_hasEverScrolledToBottom)
        {
            return;
        }

        TryCompleteDialog(DialogReturnSemantic.Close);
    }

    private void TryCompleteDialog(DialogReturnSemantic semantic)
    {
        if (_hasEverScrolledToBottom || (DoNotRemindBox.IsChecked ?? false))
        {
            Close(semantic);
            return;
        }

        HandleUnreadCloseAttempt();
    }

    private void HandleUnreadCloseAttempt()
    {
        _blockedCloseAttemptCount++;
        if (_blockedCloseAttemptCount <= _blockedConfirmMessages.Length)
        {
            ConfirmButton.Content = _blockedConfirmMessages[_blockedCloseAttemptCount - 1];
            return;
        }

        var content = ConfirmButton.Content as string;
        if (string.IsNullOrEmpty(content))
        {
            content = _blockedConfirmMessages[^1];
        }

        ConfirmButton.Content = string.Concat(content, "?\u200B");
        if (_blockedCloseAttemptCount > 20)
        {
            _ = App.Runtime.AchievementTrackerService.Unlock("AnnouncementStubbornClick", forceStayOpen: true);
            Close(DialogReturnSemantic.Close);
        }
    }

    private void BuildSections(string fallbackTitle, string markdown)
    {
        _sections.Clear();
        foreach (var section in BuildSectionDefinitions(fallbackTitle, markdown))
        {
            _sections.Add(new AnnouncementSectionDisplayItem(
                Title: section.Title,
                MarkdownContent: section.MarkdownContent,
                IsNew: section.IsNew,
                NewBadgeText: _newBadgeText));
        }

        SectionList.ItemsSource = _sections;
        RenderSectionContent();
        SelectSection(_sections.FirstOrDefault());
        Dispatcher.UIThread.Post(ResetMarkdownViewport, DispatcherPriority.Background);
    }

    private void LoadLocalizedTexts()
    {
        _newBadgeText = Text(NewBadgeKey, "NEW");
        _confirmLockedText = Text(
            "Settings.About.Dialog.ConfirmLocked",
            "Read announcement first");
        _blockedConfirmMessages =
        [
            Text(
                "Settings.About.Dialog.ConfirmBlocked1",
                "You haven't finished reading. Please confirm after you finish."),
            Text(
                "Settings.About.Dialog.ConfirmBlocked2",
                "Yes, you - still clicking"),
            Text(
                "Settings.About.Dialog.ConfirmBlocked3",
                "Still clicking"),
        ];
        DoNotRemindBox.Content = Text(
            "Settings.About.Dialog.DoNotRemindThisAnnouncementAgain",
            "Do not show this announcement again");
    }

    private void UpdateReadDependentUi()
    {
        var canToggleDoNotRemind = _hasEverScrolledToBottom || (DoNotRemindBox.IsChecked ?? false);
        DoNotRemindBox.IsEnabled = canToggleDoNotRemind;
        ConfirmButton.IsEnabled = true;
        if (_hasEverScrolledToBottom)
        {
            ConfirmButton.Content = _confirmTextSnapshot;
        }
        else if (_blockedCloseAttemptCount == 0)
        {
            ConfirmButton.Content = _confirmLockedText;
        }

        SetClass(ActionPanel, "ready", _hasEverScrolledToBottom);
        SetClass(ReadProgressStatePanel, "ready", _hasEverScrolledToBottom);
        SetClass(DoNotRemindBox, "ready", _hasEverScrolledToBottom);
        SetClass(DoNotRemindBox, "locked", !canToggleDoNotRemind);
        SetClass(ConfirmButton, "locked", !_hasEverScrolledToBottom);
    }

    private void EvaluateReadStateFromCurrentViewport()
    {
        _hasEverScrolledToBottom |= HasReachedBottom(
            MarkdownHost.Extent.Height,
            MarkdownHost.Viewport.Height,
            MarkdownHost.Offset.Y);
        UpdateScrollSynchronizedUi();
        UpdateReadDependentUi();
    }

    private void ResetMarkdownViewport()
    {
        MarkdownHost.Offset = new Vector(MarkdownHost.Offset.X, 0d);
        EvaluateReadStateFromCurrentViewport();
    }

    private void UpdateScrollSynchronizedUi()
    {
        SelectSection(CalculateActiveSection());
    }

    private AnnouncementSectionDisplayItem? CalculateActiveSection()
    {
        if (_sections.Count == 0)
        {
            return null;
        }

        var offsetY = MarkdownHost.Offset.Y;
        var headerLayouts = MeasureSectionHeaderLayouts();
        return ResolveActiveSection(headerLayouts, offsetY);
    }

    private IReadOnlyList<SectionHeaderLayout> MeasureSectionHeaderLayouts()
    {
        if (_sectionHeaderAnchors.Count == 0)
        {
            return [];
        }

        var layouts = new List<SectionHeaderLayout>(_sectionHeaderAnchors.Count);
        for (var i = 0; i < _sectionHeaderAnchors.Count; i++)
        {
            var anchor = _sectionHeaderAnchors[i];
            var point = anchor.Header.TranslatePoint(new Point(0d, 0d), MarkdownContentPanel);
            if (point is null)
            {
                continue;
            }

            layouts.Add(new SectionHeaderLayout(
                Anchor: anchor,
                Index: i,
                ContentTop: point.Value.Y));
        }

        return layouts;
    }

    private AnnouncementSectionDisplayItem? ResolveActiveSection(IReadOnlyList<SectionHeaderLayout> headerLayouts, double offsetY)
    {
        if (headerLayouts.Count == 0)
        {
            return _sections.FirstOrDefault();
        }

        var index = ResolveActiveSectionIndex(
            offsetY,
            GetSectionActivationLineY(),
            headerLayouts.Select(static layout => layout.ContentTop).ToArray());

        return index >= 0
            ? headerLayouts[index].Anchor.Section
            : _sections.FirstOrDefault();
    }

    private void RenderSectionContent()
    {
        _sectionHeaderAnchors.Clear();

        var contentStack = new StackPanel
        {
            Spacing = 28,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        foreach (var item in _sections)
        {
            var sectionStack = new StackPanel
            {
                Spacing = 14,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            var header = CreateSectionHeader(item.Title);

            var viewer = CreateMarkdownViewer(item.MarkdownContent);

            sectionStack.Children.Add(header);
            sectionStack.Children.Add(viewer);
            contentStack.Children.Add(sectionStack);
            _sectionHeaderAnchors.Add(new SectionHeaderAnchor(item, header));
        }

        MarkdownViewerHost.Content = contentStack;
    }

    private void NavigateToSection(AnnouncementSectionDisplayItem section)
    {
        var targetOffset = GetScrollTargetOffset(section);
        if (targetOffset is null)
        {
            return;
        }

        SelectSection(section);
        ScrollToOffset(targetOffset.Value);
    }

    private void ScrollToOffset(double offsetY)
    {
        MarkdownHost.Offset = new Vector(MarkdownHost.Offset.X, ClampScrollOffset(offsetY));
        EvaluateReadStateFromCurrentViewport();
    }

    private double? GetScrollTargetOffset(AnnouncementSectionDisplayItem section)
    {
        var anchor = _sectionHeaderAnchors.FirstOrDefault(existing => ReferenceEquals(existing.Section, section));
        if (anchor is null)
        {
            return null;
        }

        var point = anchor.Header.TranslatePoint(new Point(0d, 0d), MarkdownContentPanel);
        if (point is null)
        {
            return null;
        }

        return ComputeSectionTargetOffset(point.Value.Y, GetSectionScrollTargetLineY());
    }

    private static TextBlock CreateSectionHeader(string title)
    {
        var header = new TextBlock
        {
            Text = title,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            TextWrapping = TextWrapping.Wrap,
        };
        header.Classes.Add("app-window-title");
        header.Classes.Add("announcement-dialog-content-title");
        header.Classes.Add("announcement-dialog-section-heading");
        return header;
    }

    private static MarkdownViewerControl CreateMarkdownViewer(string markdownText)
    {
        var viewer = new MarkdownViewerControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Focusable = false,
            MarkdownText = markdownText,
        };
        viewer.Classes.Add("announcement-dialog-markdown-viewer");
        void NormalizeAndStopWhenReady(Control control)
        {
            if (!TryNormalizeMarkdownInlineLayout(control))
            {
                return;
            }

            viewer.LayoutUpdated -= OnViewerLayoutUpdated;
        }

        viewer.AttachedToVisualTree += (sender, _) =>
        {
            if (sender is Control control)
            {
                NormalizeAndStopWhenReady(control);
            }
        };
        viewer.LayoutUpdated += OnViewerLayoutUpdated;
        return viewer;

        void OnViewerLayoutUpdated(object? sender, EventArgs e)
        {
            if (sender is Control control)
            {
                NormalizeAndStopWhenReady(control);
            }
        }
    }

    internal static void NormalizeMarkdownInlineLayout(Control root)
    {
        _ = TryNormalizeMarkdownInlineLayout(root);
    }

    internal static bool TryNormalizeMarkdownInlineLayout(Control root)
    {
        var foundMarkdownContent = false;
        foreach (var control in EnumerateLogicalControls(root))
        {
            switch (control)
            {
                case TextBlock textBlock:
                    foundMarkdownContent = true;
                    NormalizeMarkdownTextBlock(textBlock);
                    break;
                case Button button:
                    foundMarkdownContent = true;
                    NormalizeMarkdownLinkButton(button);
                    break;
            }
        }

        return foundMarkdownContent;
    }

    private static void NormalizeMarkdownTextBlock(TextBlock textBlock)
    {
        textBlock.TextWrapping = TextWrapping.Wrap;
        if (textBlock.Inlines is null)
        {
            return;
        }

        foreach (var inline in textBlock.Inlines)
        {
            NormalizeMarkdownInline(inline);
        }
    }

    private static void NormalizeMarkdownInline(Inline inline)
    {
        if (inline is Span span)
        {
            foreach (var childInline in span.Inlines)
            {
                NormalizeMarkdownInline(childInline);
            }
        }

        if (inline is not InlineUIContainer container)
        {
            return;
        }

        container.BaselineAlignment = BaselineAlignment.Center;
        if (container.Child is not Control child)
        {
            return;
        }

        child.VerticalAlignment = VerticalAlignment.Center;
        if (child is Button button)
        {
            NormalizeMarkdownLinkButton(button);
        }
        else if (child is Border border)
        {
            border.VerticalAlignment = VerticalAlignment.Center;
        }
    }

    private static void NormalizeMarkdownLinkButton(Button button)
    {
        button.MinHeight = 0d;
        button.MinWidth = 0d;
        button.Padding = new Thickness(0d);
        button.BorderThickness = new Thickness(0d);
        button.VerticalAlignment = VerticalAlignment.Center;
        button.VerticalContentAlignment = VerticalAlignment.Center;
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        button.Background = Brushes.Transparent;
    }

    private static IEnumerable<Control> EnumerateLogicalControls(Control root)
    {
        yield return root;
        foreach (var child in root.GetLogicalChildren().OfType<Control>())
        {
            foreach (var descendant in EnumerateLogicalControls(child))
            {
                yield return descendant;
            }
        }
    }

    private void SelectSection(AnnouncementSectionDisplayItem? section)
    {
        if (section is null || ReferenceEquals(SectionList.SelectedItem, section))
        {
            return;
        }

        _suppressSectionSelectionChanged = true;
        SectionList.SelectedItem = section;
        _suppressSectionSelectionChanged = false;
    }

    private static bool HasReachedBottom(double extentHeight, double viewportHeight, double offsetY)
    {
        if (extentHeight <= 0d || viewportHeight <= 0d)
        {
            return false;
        }

        if (extentHeight <= viewportHeight + BottomReachedTolerance)
        {
            return true;
        }

        return offsetY >= Math.Max(0d, extentHeight - viewportHeight - BottomReachedTolerance);
    }

    private Bitmap LoadAnnouncementHeroImage()
    {
        var language = App.Runtime.UiLanguageCoordinator.CurrentLanguage;
        var uri = language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? new Uri(ChineseImageUri)
            : new Uri(EnglishImageUri);
        return new Bitmap(AssetLoader.Open(uri));
    }

    internal static IReadOnlyList<AnnouncementSectionDefinition> BuildSectionDefinitions(string fallbackTitle, string markdown)
    {
        var resolvedFallbackTitle = string.IsNullOrWhiteSpace(fallbackTitle)
            ? "Announcement"
            : fallbackTitle.Trim();
        var trimmedMarkdown = string.IsNullOrWhiteSpace(markdown) ? string.Empty : markdown.Trim();
        if (trimmedMarkdown.Length == 0)
        {
            return
            [
                new AnnouncementSectionDefinition(
                    Title: resolvedFallbackTitle,
                    MarkdownContent: string.Empty,
                    IsNew: false),
            ];
        }

        if (!HasExplicitSectionHeadings(trimmedMarkdown))
        {
            return
            [
                new AnnouncementSectionDefinition(
                    Title: resolvedFallbackTitle,
                    MarkdownContent: trimmedMarkdown,
                    IsNew: false),
            ];
        }

        const string newMarker = "(NEW!!!)";
        var sections = new List<AnnouncementSectionDefinition>();
        var rawSections = trimmedMarkdown.Split(["### "], StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawSection in rawSections)
        {
            var normalized = rawSection.Trim();
            if (normalized.Length == 0)
            {
                continue;
            }

            var lines = normalized.Split('\n');
            var rawTitle = lines[0].Trim();
            var isNew = rawTitle.Contains(newMarker, StringComparison.OrdinalIgnoreCase);
            var title = rawTitle.Replace(newMarker, string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
            if (title.Length == 0)
            {
                title = resolvedFallbackTitle;
            }

            var content = string.Join('\n', lines.Skip(1)).Trim();
            sections.Add(new AnnouncementSectionDefinition(
                Title: title,
                MarkdownContent: content,
                IsNew: isNew));
        }

        return sections.Count > 0
            ? sections
            :
            [
                new AnnouncementSectionDefinition(
                    Title: resolvedFallbackTitle,
                    MarkdownContent: trimmedMarkdown,
                    IsNew: false),
            ];
    }

    private string Text(string key, string fallback = "")
    {
        _texts.Language = App.Runtime.UiLanguageCoordinator.CurrentLanguage;
        return _texts.GetOrDefault(key, fallback.Length == 0 ? key : fallback);
    }

    private static void SetClass(StyledElement element, string className, bool enabled)
    {
        if (enabled)
        {
            if (!element.Classes.Contains(className))
            {
                element.Classes.Add(className);
            }

            return;
        }

        element.Classes.Remove(className);
    }

    private static bool HasExplicitSectionHeadings(string markdown)
    {
        return markdown.StartsWith("### ", StringComparison.Ordinal)
            || markdown.Contains("\n### ", StringComparison.Ordinal);
    }

    internal sealed record AnnouncementSectionDefinition(
        string Title,
        string MarkdownContent,
        bool IsNew);

    private sealed record AnnouncementSectionDisplayItem(
        string Title,
        string MarkdownContent,
        bool IsNew,
        string NewBadgeText);

    private double GetSectionActivationLineY()
    {
        return SectionActivationLineY;
    }

    private double GetSectionScrollTargetLineY()
    {
        return SectionScrollTargetLineY;
    }

    private double ClampScrollOffset(double offsetY)
    {
        var maxOffset = Math.Max(0d, MarkdownHost.Extent.Height - MarkdownHost.Viewport.Height);
        return Math.Clamp(offsetY, 0d, maxOffset);
    }

    private sealed record SectionHeaderAnchor(AnnouncementSectionDisplayItem Section, TextBlock Header)
    {
        public string Title => Section.Title;
    }

    private sealed record SectionHeaderLayout(
        SectionHeaderAnchor Anchor,
        int Index,
        double ContentTop);

    internal static double ComputeSectionTargetOffset(double headerContentTop, double activationLineY)
    {
        return StickyTitleMath.ComputeSectionTargetOffset(headerContentTop, activationLineY);
    }

    internal static int ResolveActiveSectionIndex(double offsetY, double activationLineY, IReadOnlyList<double> headerContentTops)
    {
        return StickyTitleMath.ResolveActiveSectionIndex(offsetY, activationLineY, headerContentTops);
    }

}
