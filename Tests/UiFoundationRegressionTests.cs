using System.Globalization;
using Avalonia.Input;
using MAAUnified.App.Controls;
using MAAUnified.App.Features.Dialogs;
using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.Application.Services.Localization;

namespace MAAUnified.Tests;

public sealed class UiFoundationRegressionTests
{
    public static TheoryData<string> UiLanguages => new()
    {
        "zh-cn",
        "zh-tw",
        "en-us",
        "ja-jp",
        "ko-kr",
        "pallas",
    };

    [Fact]
    public void PopupMenuKeyboardInput_MapsNavigationActivationAndDismissal()
    {
        Assert.Equal(AppPopupMenuKeyboardAction.MovePrevious, AppPopupMenuPresenter.GetKeyboardAction(true, Key.Up));
        Assert.Equal(AppPopupMenuKeyboardAction.MoveNext, AppPopupMenuPresenter.GetKeyboardAction(true, Key.Down));
        Assert.Equal(AppPopupMenuKeyboardAction.MoveToFirst, AppPopupMenuPresenter.GetKeyboardAction(true, Key.Home));
        Assert.Equal(AppPopupMenuKeyboardAction.MoveToLast, AppPopupMenuPresenter.GetKeyboardAction(true, Key.End));
        Assert.Equal(AppPopupMenuKeyboardAction.Invoke, AppPopupMenuPresenter.GetKeyboardAction(true, Key.Enter));
        Assert.Equal(AppPopupMenuKeyboardAction.Invoke, AppPopupMenuPresenter.GetKeyboardAction(true, Key.Space));
        Assert.Equal(AppPopupMenuKeyboardAction.Dismiss, AppPopupMenuPresenter.GetKeyboardAction(true, Key.Escape));
        Assert.Equal(AppPopupMenuKeyboardAction.None, AppPopupMenuPresenter.GetKeyboardAction(false, Key.Enter));

        Assert.Equal(1, AppPopupMenuPresenter.GetNextActionIndex(0, 3, 1));
        Assert.Equal(2, AppPopupMenuPresenter.GetNextActionIndex(0, 3, -1));
        Assert.Equal(0, AppPopupMenuPresenter.GetNextActionIndex(2, 3, 1));
        Assert.Equal(0, AppPopupMenuPresenter.GetNextActionIndex(-1, 3, 1));
        Assert.Equal(2, AppPopupMenuPresenter.GetNextActionIndex(-1, 3, -1));
        Assert.Equal(-1, AppPopupMenuPresenter.GetNextActionIndex(0, 0, 1));
    }

    [Fact]
    public void PopupMenuKeyboardInput_IsWiredToFirstItemFocusAndOwnerFocusRestoration()
    {
        var root = TestRepoLayout.GetMaaUnifiedRoot();
        var popupCode = File.ReadAllText(Path.Combine(root, "App", "Controls", "AppPopupMenu.cs"));
        var presenterXaml = File.ReadAllText(Path.Combine(root, "App", "Controls", "AppPopupMenuPresenter.axaml"));
        var presenterCode = File.ReadAllText(Path.Combine(root, "App", "Controls", "AppPopupMenuPresenter.axaml.cs"));

        Assert.Contains("Opened += OnOpened;", popupCode, StringComparison.Ordinal);
        Assert.Contains("_presenter.DismissRequested += OnPresenterDismissRequested;", popupCode, StringComparison.Ordinal);
        Assert.Contains("_presenter.FocusFirstEnabledItem();", popupCode, StringComparison.Ordinal);
        Assert.Contains("var owner = PlacementTarget as Control;", popupCode, StringComparison.Ordinal);
        Assert.Contains(
            "owner.Focus(NavigationMethod.Directional, KeyModifiers.None)",
            popupCode,
            StringComparison.Ordinal);
        Assert.Contains("Focusable=\"{Binding IsEnabled}\"", presenterXaml, StringComparison.Ordinal);
        Assert.Contains("IsTabStop=\"{Binding IsEnabled}\"", presenterXaml, StringComparison.Ordinal);
        Assert.Contains("KeyDown=\"OnMenuItemKeyDown\"", presenterXaml, StringComparison.Ordinal);
        Assert.Contains("DismissRequested?.Invoke(this, EventArgs.Empty);", presenterCode, StringComparison.Ordinal);
        Assert.Contains("case AppPopupMenuKeyboardAction.Invoke:", presenterCode, StringComparison.Ordinal);
        Assert.Contains("InvokeItem(item);", presenterCode, StringComparison.Ordinal);
        Assert.Contains("_activeItemIndex = 0;", presenterCode, StringComparison.Ordinal);
        Assert.Contains("return controls[0].Focus(NavigationMethod.Directional, KeyModifiers.None);", presenterCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsAndAnnouncementSectionRails_RemainFocusableAndKeepSelectionWiring()
    {
        var root = TestRepoLayout.GetMaaUnifiedRoot();
        var settingsXaml = File.ReadAllText(Path.Combine(root, "App", "Features", "Root", "SettingsView.axaml"));
        var settingsCode = File.ReadAllText(Path.Combine(root, "App", "Features", "Root", "SettingsView.axaml.cs"));
        var announcementXaml = File.ReadAllText(Path.Combine(root, "App", "Features", "Dialogs", "AnnouncementDialogView.axaml"));
        var announcementCode = File.ReadAllText(Path.Combine(root, "App", "Features", "Dialogs", "AnnouncementDialogView.axaml.cs"));
        var selectionStyles = File.ReadAllText(Path.Combine(root, "App", "Styles", "AppSelectionListStyles.axaml"));

        Assert.Contains("ItemsSource=\"{Binding Sections}\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding SelectedSection}\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("SelectionChanged=\"OnSectionSelectionChanged\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("Focusable=\"True\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("IsTabStop=\"True\"", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SectionList\"", announcementXaml, StringComparison.Ordinal);
        Assert.Contains("SelectionChanged=\"OnSectionSelectionChanged\"", announcementXaml, StringComparison.Ordinal);
        Assert.Contains("Focusable=\"True\"", announcementXaml, StringComparison.Ordinal);
        Assert.Contains("IsTabStop=\"True\"", announcementXaml, StringComparison.Ordinal);
        Assert.Contains("Dispatcher.UIThread.Post(ScrollToSelectedSection", settingsCode, StringComparison.Ordinal);
        Assert.Contains("NavigateToSection(selected);", announcementCode, StringComparison.Ordinal);
        Assert.Contains(
            "controls|AppSelectionList ListBoxItem:focus-visible Border.app-selection-list-item-shell",
            selectionStyles,
            StringComparison.Ordinal);

        var baseItemStyleStart = selectionStyles.IndexOf(
            "<Style Selector=\"controls|AppSelectionList ListBoxItem\">",
            StringComparison.Ordinal);
        Assert.True(baseItemStyleStart >= 0);
        var baseItemStyleEnd = selectionStyles.IndexOf("</Style>", baseItemStyleStart, StringComparison.Ordinal);
        Assert.True(baseItemStyleEnd > baseItemStyleStart);
        var baseItemStyle = selectionStyles[baseItemStyleStart..baseItemStyleEnd];
        Assert.Contains("Focusable\" Value=\"True\"", baseItemStyle, StringComparison.Ordinal);
        Assert.Contains("IsTabStop\" Value=\"False\"", baseItemStyle, StringComparison.Ordinal);
        Assert.Contains(
            "<Setter Property=\"Focusable\" Value=\"True\" />",
            selectionStyles,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Setter Property=\"IsTabStop\" Value=\"True\" />",
            selectionStyles,
            StringComparison.Ordinal);

        var selectionChanges = 0;
        var list = new AppSelectionList
        {
            ItemsSource = new[] { "first", "second" },
            SelectedIndex = 0,
        };
        list.SelectionChanged += (_, _) => selectionChanges++;

        list.SelectedIndex = 1;

        Assert.Equal("second", list.SelectedItem);
        Assert.Equal(1, selectionChanges);
    }

    [Theory]
    [MemberData(nameof(UiLanguages))]
    public void EmulatorPathDialogText_ResolvesEveryFunctionalLabelWithoutFallback(string language)
    {
        var fallbacks = new List<LocalizationFallbackInfo>();
        var texts = new RootLocalizationTextMap("M28.EmulatorPath")
        {
            Language = language,
        };
        texts.FallbackReported += fallbacks.Add;

        var chrome = DialogTextCatalog.CreateEmulatorPathChrome(texts);
        var namedKeys = new[]
        {
            DialogTextCatalog.ChromeKeys.SelectionSummary,
            DialogTextCatalog.ChromeKeys.CandidateCount,
            DialogTextCatalog.ChromeKeys.SelectionListTitle,
            DialogTextCatalog.ChromeKeys.SelectionInstruction,
            DialogTextCatalog.ChromeKeys.EmptyStateTitle,
            DialogTextCatalog.ChromeKeys.EmptyStateBody,
            DialogTextCatalog.ChromeKeys.CustomPathTitle,
            DialogTextCatalog.ChromeKeys.CustomPathWatermark,
        };

        Assert.Empty(fallbacks);
        Assert.False(string.IsNullOrWhiteSpace(chrome.Title));
        Assert.False(string.IsNullOrWhiteSpace(chrome.ConfirmText));
        Assert.False(string.IsNullOrWhiteSpace(chrome.CancelText));
        Assert.Contains("{0}", chrome.GetNamedTextOrDefault(DialogTextCatalog.ChromeKeys.CandidateCount), StringComparison.Ordinal);
        foreach (var key in namedKeys)
        {
            Assert.False(string.IsNullOrWhiteSpace(chrome.GetNamedTextOrDefault(key)));
        }

        if (string.Equals(language, "pallas", StringComparison.Ordinal))
        {
            Assert.StartsWith("[!! ", chrome.Title, StringComparison.Ordinal);
            Assert.StartsWith("[!! ", chrome.ConfirmText!, StringComparison.Ordinal);
            Assert.StartsWith("[!! ", chrome.CancelText!, StringComparison.Ordinal);
            Assert.All(
                namedKeys,
                key => Assert.StartsWith(
                    "[!! ",
                    chrome.GetNamedTextOrDefault(key),
                    StringComparison.Ordinal));
            return;
        }

        if (string.Equals(language, "en-us", StringComparison.Ordinal))
        {
            return;
        }

        var englishTexts = new RootLocalizationTextMap("M28.EmulatorPath")
        {
            Language = "en-us",
        };
        var englishChrome = DialogTextCatalog.CreateEmulatorPathChrome(englishTexts);
        Assert.NotEqual(englishChrome.Title, chrome.Title);
        Assert.NotEqual(englishChrome.ConfirmText, chrome.ConfirmText);
        Assert.NotEqual(englishChrome.CancelText, chrome.CancelText);
        foreach (var key in namedKeys.Where(static key => key != DialogTextCatalog.ChromeKeys.CustomPathWatermark))
        {
            Assert.NotEqual(
                englishChrome.GetNamedTextOrDefault(key),
                chrome.GetNamedTextOrDefault(key));
        }
    }

    [Theory]
    [MemberData(nameof(UiLanguages))]
    public void EmulatorPathDialogState_UsesLocalizedEmptyAndPopulatedCopy(string language)
    {
        var texts = new RootLocalizationTextMap("M28.EmulatorPath")
        {
            Language = language,
        };
        var chrome = DialogTextCatalog.CreateEmulatorPathChrome(texts);
        var noCandidates = chrome.GetNamedTextOrDefault(DialogTextCatalog.ChromeKeys.SelectionSummary);
        var candidateCount = chrome.GetNamedTextOrDefault(DialogTextCatalog.ChromeKeys.CandidateCount);
        var selectionInstruction = chrome.GetNamedTextOrDefault(DialogTextCatalog.ChromeKeys.SelectionInstruction);
        var emptyStateBody = chrome.GetNamedTextOrDefault(DialogTextCatalog.ChromeKeys.EmptyStateBody);

        var empty = EmulatorPathSelectionDialogView.BuildViewState(
            0,
            string.Empty,
            noCandidates,
            candidateCount,
            selectionInstruction,
            emptyStateBody);
        var populated = EmulatorPathSelectionDialogView.BuildViewState(
            2,
            string.Empty,
            noCandidates,
            candidateCount,
            selectionInstruction,
            emptyStateBody);
        var customPath = EmulatorPathSelectionDialogView.BuildViewState(
            2,
            "  C:\\custom\\emulator.exe  ",
            noCandidates,
            candidateCount,
            selectionInstruction,
            emptyStateBody);

        Assert.False(empty.HasCandidates);
        Assert.False(empty.IsConfirmEnabled);
        Assert.Equal(noCandidates, empty.Summary);
        Assert.Equal(emptyStateBody, empty.Intro);

        Assert.True(populated.HasCandidates);
        Assert.False(populated.IsConfirmEnabled);
        Assert.Equal(
            string.Format(CultureInfo.CurrentCulture, candidateCount, 2),
            populated.Summary);
        Assert.Equal(selectionInstruction, populated.Intro);

        Assert.True(customPath.IsConfirmEnabled);
        Assert.Equal("C:\\custom\\emulator.exe", customPath.Intro);
    }

    [Fact]
    public void EmulatorPathDialogView_ContainsNoLegacyEnglishStateLiterals()
    {
        var root = TestRepoLayout.GetMaaUnifiedRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "App", "Features", "Dialogs", "EmulatorPathSelectionDialogView.axaml"));
        var code = File.ReadAllText(Path.Combine(root, "App", "Features", "Dialogs", "EmulatorPathSelectionDialogView.axaml.cs"));

        Assert.DoesNotContain("No detected paths", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Detected paths", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Pick a detected emulator executable", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Paste the emulator executable path", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Custom path", xaml, StringComparison.Ordinal);
        Assert.Contains("DialogTextCatalog.ChromeKeys.SelectionSummary", code, StringComparison.Ordinal);
        Assert.Contains("DialogTextCatalog.ChromeKeys.CandidateCount", code, StringComparison.Ordinal);
        Assert.Contains("DialogTextCatalog.ChromeKeys.SelectionListTitle", code, StringComparison.Ordinal);
        Assert.Contains("DialogTextCatalog.ChromeKeys.SelectionInstruction", code, StringComparison.Ordinal);
        Assert.Contains("DialogTextCatalog.ChromeKeys.EmptyStateBody", code, StringComparison.Ordinal);
        Assert.Contains("DialogTextCatalog.ChromeKeys.CustomPathTitle", code, StringComparison.Ordinal);
        Assert.Contains("DialogTextCatalog.ChromeKeys.CustomPathWatermark", code, StringComparison.Ordinal);
    }
}
