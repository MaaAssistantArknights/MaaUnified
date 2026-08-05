namespace MAAUnified.Application.Services;

internal static class DiagnosticFileSecurity
{
    public static bool IsSafeDirectoryRoot(string directoryPath)
    {
        var root = Path.GetFullPath(directoryPath);
        return IsSafeDirectoryUnderRoot(root, root);
    }

    public static IReadOnlyList<string> EnumerateRegularFiles(string directoryPath)
    {
        var root = Path.GetFullPath(directoryPath);
        if (!IsSafeDirectoryUnderRoot(root, root))
        {
            return [];
        }

        var files = new List<string>();
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(root);
        while (pendingDirectories.TryPop(out var currentDirectory))
        {
            if (!IsSafeDirectoryUnderRoot(root, currentDirectory))
            {
                continue;
            }

            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(currentDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries.OrderBy(static path => path, StringComparer.Ordinal))
            {
                try
                {
                    var attributes = File.GetAttributes(entry);
                    if (IsLinkOrReparsePoint(attributes, new FileInfo(entry).LinkTarget))
                    {
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pendingDirectories.Push(entry);
                    }
                    else
                    {
                        files.Add(entry);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    public static bool TryOpenRegularFile(string trustedRoot, string filePath, out FileStream stream)
    {
        stream = null!;
        var root = Path.GetFullPath(trustedRoot);
        var path = Path.GetFullPath(filePath);
        if (!IsPathWithinRoot(root, path) || !IsSafeFileUnderRoot(root, path))
        {
            return false;
        }

        FileStream? candidate = null;
        try
        {
            candidate = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            if (!IsSafeFileUnderRoot(root, path))
            {
                candidate.Dispose();
                return false;
            }

            _ = candidate.Length;
            stream = candidate;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            candidate?.Dispose();
            return false;
        }
    }

    internal static bool IsLinkOrReparsePoint(FileAttributes attributes, string? linkTarget)
        => (attributes & FileAttributes.ReparsePoint) != 0 || linkTarget is not null;

    private static bool IsSafeDirectoryUnderRoot(string root, string directoryPath)
        => IsSafePathUnderRoot(root, directoryPath, expectDirectory: true);

    private static bool IsSafeFileUnderRoot(string root, string filePath)
        => IsSafePathUnderRoot(root, filePath, expectDirectory: false);

    private static bool IsSafePathUnderRoot(string root, string path, bool expectDirectory)
    {
        if (!IsPathWithinRoot(root, path))
        {
            return false;
        }

        try
        {
            var relativePath = Path.GetRelativePath(root, path);
            var components = relativePath == "."
                ? Array.Empty<string>()
                : relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            var current = root;
            for (var index = -1; index < components.Length; index++)
            {
                if (index >= 0)
                {
                    current = Path.Combine(current, components[index]);
                }

                var attributes = File.GetAttributes(current);
                if (IsLinkOrReparsePoint(attributes, new FileInfo(current).LinkTarget))
                {
                    return false;
                }

                var isLast = index == components.Length - 1;
                var shouldBeDirectory = !isLast || expectDirectory;
                if (((attributes & FileAttributes.Directory) != 0) != shouldBeDirectory)
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsPathWithinRoot(string root, string path)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedPath = Path.GetFullPath(path);
        return string.Equals(normalizedRoot, normalizedPath, comparison)
               || normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison)
               || normalizedPath.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, comparison);
    }
}
