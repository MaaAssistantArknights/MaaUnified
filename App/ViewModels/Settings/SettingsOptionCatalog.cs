using MAAUnified.Application.Services.Localization;

namespace MAAUnified.App.ViewModels.Settings;

internal static class SettingsOptionCatalog
{
    private const string Scope = "SettingsOptionCatalog";

    private static readonly IReadOnlyList<LocalizedOptionSpec<string>> ThemeOptionSpecs =
    [
        new("Light", "Settings.Option.Theme.Light", "Light"),
        new("Dark", "Settings.Option.Theme.Dark", "Dark"),
        new("SyncWithOs", "Settings.Option.Theme.SyncWithOs", "Sync with OS"),
    ];

    private static readonly IReadOnlyList<LocalizedOptionSpec<string>> BackgroundStretchOptionSpecs =
    [
        new("None", "Settings.Option.BackgroundStretch.None", "None"),
        new("Fill", "Settings.Option.BackgroundStretch.Fill", "Fill"),
        new("Uniform", "Settings.Option.BackgroundStretch.Uniform", "Uniform"),
        new("UniformToFill", "Settings.Option.BackgroundStretch.UniformToFill", "UniformToFill"),
    ];

    private static readonly IReadOnlyList<LocalizedOptionSpec<string>> VersionTypeOptionSpecs =
    [
        new("Nightly", "Settings.Option.UpdateVersion.Nightly", "Nightly"),
        new("Beta", "Settings.Option.UpdateVersion.Beta", "Beta"),
        new("Stable", "Settings.Option.UpdateVersion.Stable", "Stable"),
    ];

    private static readonly IReadOnlyList<LocalizedOptionSpec<string>> ResourceSourceOptionSpecs =
    [
        new("Github", "Settings.Option.ResourceSource.Github", "Github"),
        new("MirrorChyan", "Settings.Option.ResourceSource.MirrorChyan", "MirrorChyan"),
    ];

    private static readonly IReadOnlyList<LocalizedOptionSpec<string>> OperNameLanguageOptionSpecs =
    [
        new("OperNameLanguageMAA", "Settings.Option.OperNameLanguage.MAA", "Follow MAA"),
        new("OperNameLanguageClient", "Settings.Option.OperNameLanguage.Client", "Follow game client"),
    ];

    private static readonly IReadOnlyList<LocalizedOptionSpec<string>> InverseClearModeOptionSpecs =
    [
        new("Clear", "Settings.Option.InverseClear.Clear", "Clear"),
        new("Inverse", "Settings.Option.InverseClear.Inverse", "Invert"),
        new("ClearInverse", "Settings.Option.InverseClear.Switchable", "Switchable"),
    ];

    private static readonly IReadOnlyList<LocalizedOptionSpec<string>> ConnectConfigOptionSpecs =
    [
        new("General", "StartUp.Option.ConnectConfig.General", "General Mode"),
        new("BlueStacks", "StartUp.Option.ConnectConfig.BlueStacks", "BlueStacks"),
        new("MuMuEmulator12", "StartUp.Option.ConnectConfig.MuMuEmulator12", "MuMu Emulator 12"),
        new("LDPlayer", "StartUp.Option.ConnectConfig.LDPlayer", "LD Player"),
        new("AVD", "StartUp.Option.ConnectConfig.AVD", "AVD"),
        new("Nox", "StartUp.Option.ConnectConfig.Nox", "Nox"),
        new("XYAZ", "StartUp.Option.ConnectConfig.XYAZ", "MEmu"),
        new("PC", "StartUp.Option.ConnectConfig.PC", "PC Client"),
        new("WSA", "StartUp.Option.ConnectConfig.WSA", "Old version of WSA"),
        new("Compatible", "StartUp.Option.ConnectConfig.Compatible", "Compatible Mode"),
        new("SecondResolution", "StartUp.Option.ConnectConfig.SecondResolution", "2nd Resolution"),
        new("GeneralWithoutScreencapErr", "StartUp.Option.ConnectConfig.GeneralWithoutScreencapErr", "General Mode (Blocked exception output)"),
    ];

    private static readonly IReadOnlyList<LocalizedOptionSpec<string>> ClientTypeOptionSpecs =
    [
        new("Official", "StartUp.Option.ClientType.Official", "Official"),
        new("Bilibili", "StartUp.Option.ClientType.Bilibili", "Bilibili"),
        new("YoStarEN", "StartUp.Option.ClientType.YoStarEN", "YostarEN"),
        new("YoStarJP", "StartUp.Option.ClientType.YoStarJP", "YostarJP"),
        new("YoStarKR", "StartUp.Option.ClientType.YoStarKR", "YostarKR"),
        new("txwy", "StartUp.Option.ClientType.Txwy", "txwy"),
    ];

    private static readonly IReadOnlyList<LocalizedOptionSpec<string>> TouchModeOptionSpecs =
    [
        new("minitouch", "StartUp.Option.TouchMode.MiniTouch", "Minitouch"),
        new("maatouch", "StartUp.Option.TouchMode.MaaTouch", "MaaTouch"),
        new("adb", "StartUp.Option.TouchMode.AdbTouch", "ADB Input"),
        new("MaaFwAdb", "StartUp.Option.TouchMode.MaaFwAdbTouch", "MaaFwAdb"),
    ];

    private static readonly IReadOnlyList<LocalizedOptionSpec<string>> AttachWindowScreencapOptionSpecs =
    [
        new("2", "StartUp.Option.AttachScreencap.FramePool", "FramePool"),
        new("16", "StartUp.Option.AttachScreencap.PrintWindow", "PrintWindow"),
        new("32", "StartUp.Option.AttachScreencap.ScreenDC", "ScreenDC"),
        new("8", "StartUp.Option.AttachScreencap.DesktopWindow", "DesktopWindow"),
    ];

    private static readonly IReadOnlyList<LocalizedOptionSpec<string>> AttachWindowInputOptionSpecs =
    [
        new("1", "StartUp.Option.AttachInput.Seize", "Seize"),
        new("64", "StartUp.Option.AttachInput.PostWithCursor", "PostMessageWithCursor"),
        new("32", "StartUp.Option.AttachInput.SendWithCursor", "SendMessageWithCursor"),
        new("256", "StartUp.Option.AttachInput.PostWithWindowPos", "PostMessageWithWindowPos"),
        new("128", "StartUp.Option.AttachInput.SendWithWindowPos", "SendMessageWithWindowPos"),
    ];

    private static readonly IReadOnlyList<string> LogItemDateFormatOptions =
    [
        "HH:mm:ss",
        "MM-dd  HH:mm:ss",
        "MM/dd  HH:mm:ss",
        "MM.dd  HH:mm:ss",
        "dd-MM  HH:mm:ss",
        "dd/MM  HH:mm:ss",
        "dd.MM  HH:mm:ss",
    ];

    public static IReadOnlyList<DisplayValueOption> BuildThemeOptions(string language)
        => BuildDisplayOptions(language, ThemeOptionSpecs);

    public static IReadOnlyList<DisplayValueOption> BuildLanguageOptions()
    {
        return UiLanguageCatalog.Ordered
            .Select(
                language => new DisplayValueOption(
                    GetLanguageDisplayName(language),
                    language))
            .ToArray();
    }

    public static IReadOnlyList<DisplayValueOption> BuildBackgroundStretchOptions(string language)
        => BuildDisplayOptions(language, BackgroundStretchOptionSpecs);

    public static IReadOnlyList<ConnectionGameOptionItem> BuildConnectConfigOptions(string language)
        => BuildConnectionOptions(language, ConnectConfigOptionSpecs);

    public static IReadOnlyList<ConnectionGameOptionItem> BuildClientTypeOptions(string language)
        => BuildConnectionOptions(language, ClientTypeOptionSpecs);

    public static IReadOnlyList<ConnectionGameOptionItem> BuildTouchModeOptions(string language)
        => BuildConnectionOptions(language, TouchModeOptionSpecs);

    public static IReadOnlyList<ConnectionGameOptionItem> BuildAttachWindowScreencapOptions(string language)
        => BuildConnectionOptions(language, AttachWindowScreencapOptionSpecs);

    public static IReadOnlyList<ConnectionGameOptionItem> BuildAttachWindowInputOptions(string language)
        => BuildConnectionOptions(language, AttachWindowInputOptionSpecs);

    public static IReadOnlyList<DisplayValueOption> BuildVersionTypeOptions(string language, bool allowNightly)
    {
        var specs = allowNightly ? VersionTypeOptionSpecs : VersionTypeOptionSpecs.Skip(1).ToArray();
        return BuildDisplayOptions(language, specs);
    }

    public static IReadOnlyList<DisplayValueOption> BuildVersionResourceSourceOptions(string language)
        => BuildDisplayOptions(language, ResourceSourceOptionSpecs);

    public static IReadOnlyList<DisplayValueOption> BuildOperNameLanguageOptions(string language)
        => BuildDisplayOptions(language, OperNameLanguageOptionSpecs);

    public static IReadOnlyList<DisplayValueOption> BuildInverseClearModeOptions(string language)
        => BuildDisplayOptions(language, InverseClearModeOptionSpecs);

    public static IReadOnlyList<string> GetLogItemDateFormatOptions()
    {
        return LogItemDateFormatOptions;
    }

    public static string GetLanguageDisplayName(string language)
    {
        var normalized = UiLanguageCatalog.Normalize(language);
        return UiLocalizer.Create(normalized).GetOrDefault(
            $"Settings.Option.Language.{normalized}",
            normalized,
            Scope);
    }

    private static IReadOnlyList<DisplayValueOption> BuildDisplayOptions(
        string language,
        IEnumerable<LocalizedOptionSpec<string>> specs)
    {
        var localizer = UiLocalizer.Create(language);
        return specs
            .Select(spec => new DisplayValueOption(localizer.GetOrDefault(spec.TextKey, spec.Fallback, Scope), spec.Value))
            .ToArray();
    }

    private static IReadOnlyList<ConnectionGameOptionItem> BuildConnectionOptions(
        string language,
        IEnumerable<LocalizedOptionSpec<string>> specs)
    {
        var localizer = UiLocalizer.Create(language);
        return specs
            .Select(spec => new ConnectionGameOptionItem(spec.Value, localizer.GetOrDefault(spec.TextKey, spec.Fallback, Scope)))
            .ToArray();
    }
}
