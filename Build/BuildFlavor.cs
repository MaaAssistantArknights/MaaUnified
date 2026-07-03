namespace MAAUnified.Platform;

public static class MaaUnifiedBuildFlavor
{
    public const string BetaTitleSuffix = "Beta";
    public const string AlphaTitleSuffix = "Alpha";
    public const string DebugTitleSuffix = "Debug";

#if MAAUNIFIED_FORMAL_RELEASE
    public static bool IsFormalRelease => true;
#else
    public static bool IsFormalRelease => false;
#endif

#if MAAUNIFIED_MINIMAL_DIAGNOSTICS
    public static bool UsesMinimalDiagnostics => true;
#else
    public static bool UsesMinimalDiagnostics => false;
#endif

    public static bool CapturesVerboseDiagnostics => !UsesMinimalDiagnostics;
    public static bool ExposesDeveloperTools => !IsFormalRelease;
    public static bool ExposesIssueReportMaintenanceTools => !IsFormalRelease;
    public static bool ExposesAchievementDebugTools => !IsFormalRelease;

    public static string BuildTitle(string baseTitle, string? informationalVersion = null)
    {
        var suffix = ResolveTitleSuffix(informationalVersion, IsFormalRelease);
        return string.IsNullOrEmpty(suffix)
            ? baseTitle
            : $"{baseTitle} ({suffix})";
    }

    public static string ResolveTitleSuffix(string? informationalVersion, bool isFormalRelease)
    {
        if (!isFormalRelease)
        {
            return DebugTitleSuffix;
        }

        if (ContainsVersionLabel(informationalVersion, "beta"))
        {
            return BetaTitleSuffix;
        }

        if (ContainsVersionLabel(informationalVersion, "alpha"))
        {
            return AlphaTitleSuffix;
        }

        return string.Empty;
    }

    private static bool ContainsVersionLabel(string? informationalVersion, string label)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return false;
        }

        var version = informationalVersion.Split('+')[0];
        return version.Contains($"-{label}", StringComparison.OrdinalIgnoreCase)
            || version.Contains($".{label}", StringComparison.OrdinalIgnoreCase);
    }
}
