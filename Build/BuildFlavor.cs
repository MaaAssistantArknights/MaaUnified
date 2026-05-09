namespace MAAUnified.Platform;

public static class MaaUnifiedBuildFlavor
{
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
}
