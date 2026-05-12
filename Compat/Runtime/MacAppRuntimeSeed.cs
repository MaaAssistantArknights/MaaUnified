namespace MAAUnified.Compat.Runtime;

public enum MacAppRuntimeSeedStatus
{
    NotMacApp = 0,
    Ready = 1,
}

public sealed record MacAppRuntimeSeedResult(
    MacAppRuntimeSeedStatus Status,
    string RuntimeBaseDirectory,
    string? BundleResourceDirectory = null,
    int NativeLibraryCount = 0,
    bool ResourceSeeded = false,
    string ResourceSeedReason = "");

public static class MacAppRuntimeSeed
{
    public static MacAppRuntimeSeedResult EnsureSeeded(
        string? executableBaseDirectory = null,
        string? runtimeBaseDirectory = null)
    {
        var executableDirectory = RuntimeLayout.NormalizeDirectory(executableBaseDirectory ?? AppContext.BaseDirectory);
        var resolvedRuntimeBaseDirectory = RuntimeLayout.NormalizeDirectory(
            runtimeBaseDirectory ?? RuntimeLayout.ResolveRuntimeBaseDirectory(executableDirectory));

        if (!RuntimeLayout.IsMacAppContentsMacOsDirectory(executableDirectory))
        {
            return new MacAppRuntimeSeedResult(
                MacAppRuntimeSeedStatus.NotMacApp,
                resolvedRuntimeBaseDirectory);
        }

        Directory.CreateDirectory(resolvedRuntimeBaseDirectory);

        var nativeLibraryCount = CopyNativeLibraries(executableDirectory, resolvedRuntimeBaseDirectory);
        var bundleResourceDirectory = ResolveBundleResourceDirectory(executableDirectory);
        var resourceSeedResult = SeedResourceDirectoryIfNeeded(
            bundleResourceDirectory,
            Path.Combine(resolvedRuntimeBaseDirectory, "resource"));

        return new MacAppRuntimeSeedResult(
            MacAppRuntimeSeedStatus.Ready,
            resolvedRuntimeBaseDirectory,
            bundleResourceDirectory,
            nativeLibraryCount,
            resourceSeedResult.Seeded,
            resourceSeedResult.Reason);
    }

    private static int CopyNativeLibraries(string executableDirectory, string runtimeBaseDirectory)
    {
        var count = 0;
        foreach (var sourcePath in Directory.EnumerateFiles(executableDirectory, "*.dylib", SearchOption.TopDirectoryOnly))
        {
            var destinationPath = Path.Combine(runtimeBaseDirectory, Path.GetFileName(sourcePath));
            CopyFileIfChanged(sourcePath, destinationPath);
            count++;
        }

        return count;
    }

    private static string ResolveBundleResourceDirectory(string executableDirectory)
    {
        var contentsDirectory = Directory.GetParent(executableDirectory)
            ?? throw new DirectoryNotFoundException($"Cannot resolve .app Contents directory from {executableDirectory}.");
        return Path.Combine(contentsDirectory.FullName, "Resources", "resource");
    }

    private static ResourceSeedDecision SeedResourceDirectoryIfNeeded(string sourceDirectory, string destinationDirectory)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            return new ResourceSeedDecision(false, "bundle-resource-missing");
        }

        if (Directory.Exists(destinationDirectory)
            && Directory.EnumerateFileSystemEntries(destinationDirectory).Any())
        {
            if (!ShouldRefreshFromBundleResource(sourceDirectory, destinationDirectory))
            {
                return new ResourceSeedDecision(false, "runtime-resource-current");
            }

            CopyDirectory(sourceDirectory, destinationDirectory);
            return new ResourceSeedDecision(true, "bundle-resource-newer");
        }

        CopyDirectory(sourceDirectory, destinationDirectory);
        return new ResourceSeedDecision(true, "runtime-resource-missing");
    }

    private static bool ShouldRefreshFromBundleResource(string sourceDirectory, string destinationDirectory)
    {
        var sourceLastUpdated = TryReadResourceLastUpdated(sourceDirectory);
        var destinationLastUpdated = TryReadResourceLastUpdated(destinationDirectory);
        return sourceLastUpdated.HasValue
            && (!destinationLastUpdated.HasValue || sourceLastUpdated.Value > destinationLastUpdated.Value);
    }

    private static DateTime? TryReadResourceLastUpdated(string resourceDirectory)
    {
        var versionFile = Path.Combine(resourceDirectory, "version.json");
        if (!File.Exists(versionFile))
        {
            return null;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(versionFile));
            if (!document.RootElement.TryGetProperty("last_updated", out var node)
                || node.ValueKind != System.Text.Json.JsonValueKind.String)
            {
                return null;
            }

            var raw = node.GetString();
            return DateTime.TryParse(
                raw,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed)
                ? parsed
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(directory.Replace(sourceDirectory, destinationDirectory, StringComparison.Ordinal));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var destinationPath = file.Replace(sourceDirectory, destinationDirectory, StringComparison.Ordinal);
            CopyFileIfChanged(file, destinationPath);
        }
    }

    private static void CopyFileIfChanged(string sourcePath, string destinationPath)
    {
        EnsureParentDirectoryExists(destinationPath);
        if (File.Exists(destinationPath))
        {
            var sourceInfo = new FileInfo(sourcePath);
            var destinationInfo = new FileInfo(destinationPath);
            if (sourceInfo.Length == destinationInfo.Length
                && sourceInfo.LastWriteTimeUtc <= destinationInfo.LastWriteTimeUtc)
            {
                return;
            }
        }

        File.Copy(sourcePath, destinationPath, overwrite: true);
    }

    private static void EnsureParentDirectoryExists(string filePath)
    {
        var parent = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }
    }

    private sealed record ResourceSeedDecision(bool Seeded, string Reason);
}
