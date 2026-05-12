namespace MAAUnified.Compat.Runtime;

public static class RuntimeLayout
{
    public const string RuntimeBinDirectoryName = "bin";
    public const string MacAppSupportDirectoryName = "MAAUnified";

    public static string ResolveRuntimeBaseDirectory(
        string? executableBaseDirectory = null,
        string? macApplicationSupportRootDirectory = null)
    {
        var normalizedExecutableBaseDirectory = NormalizeDirectory(executableBaseDirectory ?? AppContext.BaseDirectory);
        if (IsMacAppContentsMacOsDirectory(normalizedExecutableBaseDirectory))
        {
            return ResolveMacApplicationSupportDirectory(macApplicationSupportRootDirectory);
        }

        if (!IsPackagedBinDirectory(normalizedExecutableBaseDirectory))
        {
            return normalizedExecutableBaseDirectory;
        }

        var parent = Directory.GetParent(normalizedExecutableBaseDirectory);
        return parent is null
            ? normalizedExecutableBaseDirectory
            : NormalizeDirectory(parent.FullName);
    }

    public static string ResolveMacApplicationSupportDirectory(string? rootDirectory = null)
    {
        var root = string.IsNullOrWhiteSpace(rootDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "Application Support")
            : rootDirectory;
        return NormalizeDirectory(Path.Combine(root, MacAppSupportDirectoryName));
    }

    public static string NormalizeDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static bool IsMacAppContentsMacOsDirectory(string path)
    {
        var normalized = NormalizeDirectory(path);
        if (!string.Equals(Path.GetFileName(normalized), "MacOS", StringComparison.Ordinal))
        {
            return false;
        }

        var contents = Directory.GetParent(normalized);
        if (contents is null || !string.Equals(contents.Name, "Contents", StringComparison.Ordinal))
        {
            return false;
        }

        var appBundle = contents.Parent;
        return appBundle is not null
            && appBundle.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsPackagedBinDirectory(string path)
    {
        var normalized = NormalizeDirectory(path);
        return string.Equals(
            Path.GetFileName(normalized),
            RuntimeBinDirectoryName,
            StringComparison.OrdinalIgnoreCase);
    }
}
