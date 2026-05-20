namespace MAAUnified.Compat.Runtime;

public static class ResourceDirectoryMaintenance
{
    public static int RemoveFlattenedPluginShadowFiles(string resourceDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceDirectory);

        if (!Directory.Exists(resourceDirectory))
        {
            return 0;
        }

        var removedCount = 0;
        foreach (var pluginDirectory in Directory
                     .EnumerateDirectories(resourceDirectory, "plugin", SearchOption.AllDirectories)
                     .OrderByDescending(static path => path.Length)
                     .ToArray())
        {
            var parentDirectory = Directory.GetParent(pluginDirectory)?.FullName;
            if (string.IsNullOrWhiteSpace(parentDirectory))
            {
                continue;
            }

            foreach (var pluginFile in Directory.EnumerateFiles(pluginDirectory, "*", SearchOption.AllDirectories).ToArray())
            {
                var relativePath = Path.GetRelativePath(pluginDirectory, pluginFile);
                var flattenedPath = Path.Combine(parentDirectory, relativePath);
                if (!File.Exists(flattenedPath))
                {
                    continue;
                }

                File.Delete(pluginFile);
                removedCount++;
            }

            DeleteEmptyDirectories(pluginDirectory);
        }

        return removedCount;
    }

    private static void DeleteEmptyDirectories(string rootDirectory)
    {
        if (!Directory.Exists(rootDirectory))
        {
            return;
        }

        foreach (var directory in Directory
                     .EnumerateDirectories(rootDirectory, "*", SearchOption.AllDirectories)
                     .OrderByDescending(static path => path.Length))
        {
            TryDeleteDirectoryIfEmpty(directory);
        }

        TryDeleteDirectoryIfEmpty(rootDirectory);
    }

    private static void TryDeleteDirectoryIfEmpty(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        if (Directory.EnumerateFileSystemEntries(directory).Any())
        {
            return;
        }

        Directory.Delete(directory, recursive: false);
    }
}
