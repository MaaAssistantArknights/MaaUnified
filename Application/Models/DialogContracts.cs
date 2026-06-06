namespace MAAUnified.Application.Models;

public enum DialogType
{
    Announcement = 0,
    VersionUpdate = 1,
    ProcessPicker = 2,
    EmulatorPath = 3,
    Error = 4,
    AchievementList = 5,
    Text = 6,
    WarningConfirm = 7,
}

public enum DialogReturnSemantic
{
    Confirm = 0,
    Cancel = 1,
    Close = 2,
    Details = 3,
}

public sealed record DialogTraceToken(
    string TraceId,
    DialogType DialogType,
    string SourceScope,
    DateTimeOffset OpenedAtUtc);

public sealed record DialogCompletion<TPayload>(
    DialogReturnSemantic Return,
    TPayload? Payload,
    string Summary);

public sealed record DialogErrorRaisedEvent(
    string Context,
    UiOperationResult Result,
    DateTimeOffset TimestampUtc);

public sealed record DialogChromeSnapshot
{
    private static readonly IReadOnlyDictionary<string, string> EmptyNamedTexts =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public DialogChromeSnapshot(
        string title,
        string? confirmText = null,
        string? cancelText = null,
        IReadOnlyDictionary<string, string>? namedTexts = null)
    {
        Title = title;
        ConfirmText = confirmText;
        CancelText = cancelText;
        NamedTexts = namedTexts ?? EmptyNamedTexts;
    }

    public string Title { get; init; }

    public string? ConfirmText { get; init; }

    public string? CancelText { get; init; }

    public IReadOnlyDictionary<string, string> NamedTexts { get; init; }

    public string GetNamedTextOrDefault(string key, string fallback = "")
    {
        if (!string.IsNullOrWhiteSpace(key)
            && NamedTexts.TryGetValue(key, out var value)
            && !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return fallback;
    }
}

public sealed class DialogChromeCatalog
{
    private readonly Func<string, DialogChromeSnapshot> _snapshotFactory;

    public DialogChromeCatalog(string initialLanguage, Func<string, DialogChromeSnapshot> snapshotFactory)
    {
        InitialLanguage = string.IsNullOrWhiteSpace(initialLanguage) ? "en-us" : initialLanguage.Trim();
        _snapshotFactory = snapshotFactory ?? throw new ArgumentNullException(nameof(snapshotFactory));
    }

    public string InitialLanguage { get; }

    public DialogChromeSnapshot GetSnapshot(string? language = null)
    {
        var resolvedLanguage = string.IsNullOrWhiteSpace(language) ? InitialLanguage : language.Trim();
        return _snapshotFactory(resolvedLanguage);
    }
}

public interface IDialogChromeAware
{
    void ApplyDialogChrome(DialogChromeSnapshot chrome);
}

public sealed record AnnouncementDialogRequest(
    string Title,
    string AnnouncementInfo,
    bool DoNotRemindThisAnnouncementAgain,
    bool DoNotShowAnnouncement,
    string ConfirmText = "Confirm",
    string CancelText = "Cancel",
    DialogChromeCatalog? Chrome = null);

public sealed record AnnouncementDialogPayload(
    bool DoNotRemindThisAnnouncementAgain,
    bool DoNotShowAnnouncement);

public sealed record VersionUpdateDialogRequest(
    string Title,
    string CurrentVersion,
    string TargetVersion,
    string Summary,
    string Body,
    string ConfirmText = "Confirm",
    string CancelText = "Later",
    DialogChromeCatalog? Chrome = null);

public sealed record VersionUpdateDialogPayload(
    string Action,
    string CurrentVersion,
    string TargetVersion,
    string Summary);

public sealed record ProcessPickerItem(
    string Id,
    string DisplayName,
    bool IsPrimary);

public sealed record ProcessPickerDialogRequest(
    string Title,
    IReadOnlyList<ProcessPickerItem> Items,
    string? SelectedId,
    string ConfirmText = "Select",
    string CancelText = "Cancel",
    Func<CancellationToken, Task<IReadOnlyList<ProcessPickerItem>>>? RefreshItemsAsync = null,
    DialogChromeCatalog? Chrome = null);

public sealed record ProcessPickerDialogPayload(
    string SelectedId,
    string SelectedDisplayName);

public sealed record EmulatorPathDialogRequest(
    string Title,
    IReadOnlyList<string> CandidatePaths,
    string? SelectedPath,
    string ConfirmText = "Confirm",
    string CancelText = "Cancel",
    DialogChromeCatalog? Chrome = null);

public sealed record EmulatorPathDialogPayload(string SelectedPath);

public sealed record ErrorDialogRequest(
    string Title,
    string Context,
    UiOperationResult Result,
    string? Suggestion = null,
    string ConfirmText = "Close",
    string CancelText = "Ignore",
    string Language = "en-us",
    DialogChromeCatalog? Chrome = null);

public sealed record ErrorDialogPayload(
    string FormattedErrorText,
    bool Copied,
    bool IssueReportOpened,
    bool SettingsOpened);

public sealed record AchievementListItem(
    string Id,
    string Title,
    string Description,
    string Status,
    string Conditions = "",
    bool IsUnlocked = false,
    bool IsHidden = false,
    bool IsProgressive = false,
    bool ShowProgress = false,
    int Progress = 0,
    int Target = 0,
    string MedalColor = "#B0B0B0",
    string UnlockedAtText = "",
    bool IsNewUnlock = false,
    bool CanShow = true,
    int SortCategory = 0,
    string SortGroup = "",
    int SortGroupIndex = int.MaxValue);

public sealed record AchievementListDialogRequest(
    string Title,
    IReadOnlyList<AchievementListItem> Items,
    string? InitialFilter,
    string ConfirmText = "Confirm",
    string CancelText = "Cancel",
    string FilterWatermark = "Filter",
    int UnlockedCount = 0,
    int TotalCount = 0,
    DialogChromeCatalog? Chrome = null);

public sealed record AchievementListDialogPayload(
    string FilterText,
    IReadOnlyList<string> SelectedIds);

public sealed record TextDialogRequest(
    string Title,
    string Prompt,
    string DefaultText,
    bool MultiLine = false,
    bool ReadOnlyContent = false,
    string ConfirmText = "Confirm",
    string CancelText = "Cancel",
    DialogChromeCatalog? Chrome = null);

public sealed record TextDialogPayload(string Text);

public sealed record DialogLinkItem(
    string Title,
    string Url);

public sealed record WarningConfirmDialogRequest(
    string Title,
    string Message,
    string ConfirmText = "Confirm",
    string CancelText = "Cancel",
    string Language = "en-us",
    int CountdownSeconds = 0,
    DialogChromeCatalog? Chrome = null,
    IReadOnlyList<DialogLinkItem>? Links = null);

public sealed record WarningConfirmDialogPayload(bool Confirmed);
