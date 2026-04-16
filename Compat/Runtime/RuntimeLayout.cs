namespace MAAUnified.Compat.Runtime;

public static class RuntimeLayout
{
    public const string RuntimeBinDirectoryName = "bin";

    public static string ResolveRuntimeBaseDirectory(string? executableBaseDirectory = null)
    {
        var normalizedExecutableBaseDirectory = NormalizeDirectory(executableBaseDirectory ?? AppContext.BaseDirectory);
        if (!IsPackagedBinDirectory(normalizedExecutableBaseDirectory))
        {
            return normalizedExecutableBaseDirectory;
        }

        var parent = Directory.GetParent(normalizedExecutableBaseDirectory);
        return parent is null
            ? normalizedExecutableBaseDirectory
            : NormalizeDirectory(parent.FullName);
    }

    public static string NormalizeDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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
