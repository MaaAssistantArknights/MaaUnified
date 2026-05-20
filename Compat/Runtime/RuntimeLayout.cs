namespace MAAUnified.Compat.Runtime;

public static class RuntimeLayout
{
    public const string RuntimeBinDirectoryName = "bin";
    public const string MacAppSupportDirectoryName = "MAAUnified";
    public const string LinuxAppSupportDirectoryName = "MAAUnified";
    public const string LinuxPortableLauncherFileName = "MAAUnified.AppImage";
    public const string LinuxPortableResourceDirectoryName = "resource";
    public const string LinuxPortableCoreLibraryPrefix = "libMaaCore.so";

    public static string ResolveRuntimeBaseDirectory(
        string? executableBaseDirectory = null,
        string? macApplicationSupportRootDirectory = null,
        string? linuxApplicationSupportRootDirectory = null,
        string? linuxAppImagePath = null,
        string? linuxAppDirPath = null)
    {
        var normalizedExecutableBaseDirectory = NormalizeDirectory(executableBaseDirectory ?? AppContext.BaseDirectory);
        if (IsMacAppContentsMacOsDirectory(normalizedExecutableBaseDirectory))
        {
            return ResolveMacApplicationSupportDirectory(macApplicationSupportRootDirectory);
        }

        if (TryResolveLinuxPortablePackageRoot(normalizedExecutableBaseDirectory, linuxAppImagePath, out var linuxPortablePackageRoot))
        {
            return linuxPortablePackageRoot;
        }

        if (ShouldUseLinuxApplicationSupportDirectory(
                normalizedExecutableBaseDirectory,
                linuxAppImagePath,
                linuxAppDirPath))
        {
            return ResolveLinuxApplicationSupportDirectory(linuxApplicationSupportRootDirectory);
        }

        if (!IsPackagedBinDirectory(normalizedExecutableBaseDirectory))
        {
            return normalizedExecutableBaseDirectory;
        }

        return ResolvePackagedRuntimeBaseDirectory(normalizedExecutableBaseDirectory);
    }

    public static string ResolvePackageRootDirectory(
        string? executableBaseDirectory = null,
        string? linuxAppImagePath = null)
    {
        var normalizedExecutableBaseDirectory = NormalizeDirectory(executableBaseDirectory ?? AppContext.BaseDirectory);
        if (IsMacAppContentsMacOsDirectory(normalizedExecutableBaseDirectory))
        {
            return ResolveMacBundleRootDirectory(normalizedExecutableBaseDirectory);
        }

        if (TryResolveLinuxPortablePackageRoot(normalizedExecutableBaseDirectory, linuxAppImagePath, out var linuxPortablePackageRoot))
        {
            return linuxPortablePackageRoot;
        }

        if (IsPackagedBinDirectory(normalizedExecutableBaseDirectory))
        {
            return ResolvePackagedRuntimeBaseDirectory(normalizedExecutableBaseDirectory);
        }

        var appImageDirectory = ResolveLinuxAppImageDirectory(linuxAppImagePath);
        if (OperatingSystem.IsLinux() && appImageDirectory is not null && IsRunningFromLinuxAppImageMount(normalizedExecutableBaseDirectory))
        {
            return appImageDirectory;
        }

        return normalizedExecutableBaseDirectory;
    }

    public static string ResolveSingleInstanceIdentityPath(
        string? executableBaseDirectory = null,
        string? linuxAppImagePath = null)
    {
        var normalizedExecutableBaseDirectory = NormalizeDirectory(executableBaseDirectory ?? AppContext.BaseDirectory);
        if (IsMacAppContentsMacOsDirectory(normalizedExecutableBaseDirectory))
        {
            return ResolveMacBundleRootDirectory(normalizedExecutableBaseDirectory);
        }

        if (TryResolveLinuxPortablePackageRoot(normalizedExecutableBaseDirectory, linuxAppImagePath, out var linuxPortablePackageRoot))
        {
            return linuxPortablePackageRoot;
        }

        var appImagePath = ResolveLinuxAppImagePath(linuxAppImagePath);
        if (OperatingSystem.IsLinux()
            && !string.IsNullOrWhiteSpace(appImagePath)
            && IsRunningFromLinuxAppImageMount(normalizedExecutableBaseDirectory))
        {
            return NormalizePath(appImagePath);
        }

        return IsPackagedBinDirectory(normalizedExecutableBaseDirectory)
            ? ResolvePackagedRuntimeBaseDirectory(normalizedExecutableBaseDirectory)
            : normalizedExecutableBaseDirectory;
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

    public static string ResolveLinuxApplicationSupportDirectory(string? rootDirectory = null)
    {
        var root = string.IsNullOrWhiteSpace(rootDirectory)
            ? Environment.GetEnvironmentVariable("XDG_DATA_HOME")
            : rootDirectory;

        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local",
                "share");
        }

        return NormalizeDirectory(Path.Combine(root, LinuxAppSupportDirectoryName));
    }

    public static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(path);
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

    public static string ResolveMacBundleRootDirectory(string path)
    {
        var normalized = NormalizeDirectory(path);
        if (!IsMacAppContentsMacOsDirectory(normalized))
        {
            return normalized;
        }

        return NormalizeDirectory(Directory.GetParent(normalized)!.Parent!.FullName);
    }

    public static bool IsPackagedBinDirectory(string path)
    {
        var normalized = NormalizeDirectory(path);
        return string.Equals(
            Path.GetFileName(normalized),
            RuntimeBinDirectoryName,
            StringComparison.OrdinalIgnoreCase);
    }

    public static string ResolvePackagedRuntimeBaseDirectory(string path)
    {
        var normalized = NormalizeDirectory(path);
        if (!IsPackagedBinDirectory(normalized))
        {
            return normalized;
        }

        var parent = Directory.GetParent(normalized);
        return parent is null
            ? normalized
            : NormalizeDirectory(parent.FullName);
    }

    public static bool ShouldUseLinuxApplicationSupportDirectory(
        string path,
        string? linuxAppImagePath = null,
        string? linuxAppDirPath = null)
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        var normalized = NormalizeDirectory(path);
        if (!IsPackagedBinDirectory(normalized))
        {
            return false;
        }

        if (TryResolveLinuxPortablePackageRoot(normalized, linuxAppImagePath, out _))
        {
            return false;
        }

        return IsRunningFromLinuxAppImageMount(normalized, linuxAppDirPath);
    }

    public static bool IsRunningFromLinuxAppImageMount(string path, string? linuxAppDirPath = null)
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        var normalized = NormalizeDirectory(path);
        var appDir = ResolveLinuxAppDir(linuxAppDirPath);
        if (string.IsNullOrWhiteSpace(appDir))
        {
            return false;
        }

        return IsInsideDirectory(normalized, appDir);
    }

    public static bool TryResolveLinuxPortablePackageRoot(
        string path,
        string? linuxAppImagePath,
        out string packageRoot)
    {
        packageRoot = string.Empty;
        if (!TryResolveLinuxPortablePackageRootCandidate(path, linuxAppImagePath, out var candidateRoot))
        {
            return false;
        }

        if (!IsLinuxPortablePackageRoot(candidateRoot))
        {
            return false;
        }

        packageRoot = candidateRoot;
        return true;
    }

    public static bool IsLinuxPortablePackageLaunchInvalid(
        string path,
        string? linuxAppImagePath,
        out string packageRoot)
    {
        packageRoot = string.Empty;
        if (!TryResolveLinuxPortablePackageRootCandidate(path, linuxAppImagePath, out var candidateRoot))
        {
            return false;
        }

        if (IsLinuxPortablePackageRoot(candidateRoot))
        {
            return false;
        }

        packageRoot = candidateRoot;
        return true;
    }

    public static bool IsLinuxPortablePackageRoot(string path)
    {
        var normalized = NormalizeDirectory(path);
        if (!Directory.Exists(normalized))
        {
            return false;
        }

        return File.Exists(Path.Combine(normalized, LinuxPortableLauncherFileName))
            && File.Exists(Path.Combine(normalized, RuntimeBinDirectoryName, "MAAUnified"))
            && Directory.Exists(Path.Combine(normalized, LinuxPortableResourceDirectoryName))
            && Directory.EnumerateFiles(normalized, $"{LinuxPortableCoreLibraryPrefix}*", SearchOption.TopDirectoryOnly).Any();
    }

    public static string? ResolveLinuxAppImagePath(string? linuxAppImagePath = null)
    {
        var path = string.IsNullOrWhiteSpace(linuxAppImagePath)
            ? Environment.GetEnvironmentVariable("APPIMAGE")
            : linuxAppImagePath;
        return string.IsNullOrWhiteSpace(path) ? null : NormalizePath(path);
    }

    public static string? ResolveLinuxAppImageDirectory(string? linuxAppImagePath = null)
    {
        var path = ResolveLinuxAppImagePath(linuxAppImagePath);
        var directory = path is null ? null : Path.GetDirectoryName(path);
        return string.IsNullOrWhiteSpace(directory) ? null : NormalizeDirectory(directory);
    }

    private static string? ResolveLinuxAppDir(string? linuxAppDirPath = null)
    {
        var path = string.IsNullOrWhiteSpace(linuxAppDirPath)
            ? Environment.GetEnvironmentVariable("APPDIR")
            : linuxAppDirPath;
        return string.IsNullOrWhiteSpace(path) ? null : NormalizeDirectory(path);
    }

    private static bool TryResolveLinuxPortablePackageRootCandidate(
        string path,
        string? linuxAppImagePath,
        out string packageRoot)
    {
        packageRoot = string.Empty;
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        var normalized = NormalizeDirectory(path);
        if (!IsPackagedBinDirectory(normalized))
        {
            return false;
        }

        var appImageDirectory = ResolveLinuxAppImageDirectory(linuxAppImagePath);
        if (string.IsNullOrWhiteSpace(appImageDirectory))
        {
            return false;
        }

        var packagedRuntimeBaseDirectory = ResolvePackagedRuntimeBaseDirectory(normalized);
        if (!PathsEqual(packagedRuntimeBaseDirectory, appImageDirectory))
        {
            return false;
        }

        packageRoot = appImageDirectory;
        return true;
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            NormalizeDirectory(left),
            NormalizeDirectory(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static bool IsInsideDirectory(string path, string rootDirectory)
    {
        var normalizedPath = NormalizeDirectory(path);
        var normalizedRoot = NormalizeDirectory(rootDirectory);
        if (PathsEqual(normalizedPath, normalizedRoot))
        {
            return true;
        }

        var rootWithSeparator = normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(
            rootWithSeparator,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
