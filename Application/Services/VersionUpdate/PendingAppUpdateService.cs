using System.Formats.Tar;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using MAAUnified.Application.Models;
using MAAUnified.Compat.Constants;

namespace MAAUnified.Application.Services.VersionUpdate;

public enum PendingAppUpdateStatus
{
    None = 0,
    Applied = 1,
    Failed = 2,
}

public sealed record PendingAppUpdateInfo(
    string? VersionName,
    string PackagePath);

public sealed record PendingAppUpdateApplyResult(
    PendingAppUpdateStatus Status,
    string Message,
    string? PackagePath = null);

public static class PendingAppUpdateService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static PendingAppUpdateInfo? ResolvePendingUpdate(string baseDirectory)
    {
        var config = TryLoadConfig(baseDirectory);
        if (config is null)
        {
            return null;
        }

        var configuredPackagePath = ReadGlobalString(config, ConfigurationKeys.VersionUpdatePackage);
        if (string.IsNullOrWhiteSpace(configuredPackagePath))
        {
            return null;
        }

        var resolvedPackagePath = Path.IsPathRooted(configuredPackagePath)
            ? configuredPackagePath
            : Path.Combine(baseDirectory, configuredPackagePath);
        return new PendingAppUpdateInfo(
            ReadGlobalString(config, ConfigurationKeys.VersionName),
            resolvedPackagePath);
    }

    public static PendingAppUpdateApplyResult TryApplyPendingUpdatePackage(string baseDirectory)
    {
        var config = TryLoadConfig(baseDirectory);
        if (config is null)
        {
            return new PendingAppUpdateApplyResult(
                PendingAppUpdateStatus.None,
                "No Avalonia config was found.");
        }

        var pendingUpdate = ResolvePendingUpdate(baseDirectory);
        if (pendingUpdate is null)
        {
            return new PendingAppUpdateApplyResult(
                PendingAppUpdateStatus.None,
                "No pending software update package was found.");
        }

        if (!File.Exists(pendingUpdate.PackagePath))
        {
            ClearPendingPackageState(config);
            SaveConfig(baseDirectory, config);
            return new PendingAppUpdateApplyResult(
                PendingAppUpdateStatus.Failed,
                $"Pending software update package is missing: {pendingUpdate.PackagePath}",
                pendingUpdate.PackagePath);
        }

        if (IsManualInstallPackage(pendingUpdate.PackagePath))
        {
            ClearPendingPackageState(config);
            SaveConfig(baseDirectory, config);
            return new PendingAppUpdateApplyResult(
                PendingAppUpdateStatus.Failed,
                $"Pending manual install packages cannot be applied automatically: {pendingUpdate.PackagePath}",
                pendingUpdate.PackagePath);
        }

        var extractDirectory = Path.Combine(baseDirectory, "NewVersionExtract");
        var backupDirectory = Path.Combine(baseDirectory, ".old");

        try
        {
            PrepareExtractionDirectory(extractDirectory);
            ExtractPackageToDirectory(pendingUpdate.PackagePath, extractDirectory);

            var removeListFile = Path.Combine(extractDirectory, "removelist.txt");
            var changesFile = Path.Combine(extractDirectory, "changes.json");
            var isOtaPackage = File.Exists(removeListFile) || File.Exists(changesFile);
            var removeList = LoadRemoveList(removeListFile, changesFile);
            var removeEntries = BuildBackupEntries(baseDirectory, backupDirectory, removeList);
            var extractedDirectories = BuildExtractedDirectoryEntries(extractDirectory, baseDirectory, backupDirectory);
            var payloadFiles = BuildPayloadFileEntries(extractDirectory, baseDirectory, backupDirectory);

            if (removeEntries.Length > 0)
            {
                foreach (var entry in removeEntries)
                {
                    var currentPath = entry.TargetPath;
                    if (!File.Exists(currentPath))
                    {
                        continue;
                    }

                    MoveExistingEntryToBackup(currentPath, entry.BackupPath);
                }
            }
            else if (!isOtaPackage)
            {
                Directory.CreateDirectory(backupDirectory);
                foreach (var extractedSubDirectory in Directory.GetDirectories(extractDirectory))
                {
                    var directoryName = Path.GetFileName(extractedSubDirectory);
                    if (string.IsNullOrWhiteSpace(directoryName))
                    {
                        continue;
                    }

                    var currentDirectory = ResolvePackagePathUnderRoot(baseDirectory, directoryName);
                    if (!Directory.Exists(currentDirectory))
                    {
                        continue;
                    }

                    var currentBackupDirectory = ResolvePackagePathUnderRoot(backupDirectory, directoryName);
                    MoveDirectoryWithBackup(currentDirectory, currentBackupDirectory);
                }
            }

            Directory.CreateDirectory(backupDirectory);
            foreach (var directory in extractedDirectories)
            {
                Directory.CreateDirectory(directory.TargetPath);
                Directory.CreateDirectory(directory.BackupPath);
            }

            foreach (var file in payloadFiles)
            {
                if (File.Exists(file.TargetPath))
                {
                    DeleteFileWithBackup(file.BackupPath);
                    EnsureParentDirectoryExists(file.BackupPath);
                    File.Move(file.TargetPath, file.BackupPath);
                }
                else
                {
                    EnsureParentDirectoryExists(file.TargetPath);
                }

                File.Move(file.SourcePath, file.TargetPath);
            }

            SafeDeleteDirectory(extractDirectory);
            File.Delete(pendingUpdate.PackagePath);

            ClearPendingPackageState(config);
            config.GlobalValues[ConfigurationKeys.VersionUpdateIsFirstBoot] = JsonValue.Create(bool.TrueString);
            SaveConfig(baseDirectory, config);

            return new PendingAppUpdateApplyResult(
                PendingAppUpdateStatus.Applied,
                $"Applied pending software update package `{Path.GetFileName(pendingUpdate.PackagePath)}`.",
                pendingUpdate.PackagePath);
        }
        catch (InvalidDataException)
        {
            TryDeleteFile(pendingUpdate.PackagePath);
            ClearPendingPackageState(config);
            SaveConfig(baseDirectory, config);
            return new PendingAppUpdateApplyResult(
                PendingAppUpdateStatus.Failed,
                $"Pending software update package is invalid: {pendingUpdate.PackagePath}",
                pendingUpdate.PackagePath);
        }
        catch (Exception ex)
        {
            return new PendingAppUpdateApplyResult(
                PendingAppUpdateStatus.Failed,
                $"Failed to apply pending software update package: {ex.Message}",
                pendingUpdate.PackagePath);
        }
        finally
        {
            SafeDeleteDirectory(extractDirectory);
        }
    }

    private static UnifiedConfig? TryLoadConfig(string baseDirectory)
    {
        var configPath = Path.Combine(baseDirectory, "config", "avalonia.json");
        if (!File.Exists(configPath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<UnifiedConfig>(File.ReadAllText(configPath), SerializerOptions);
        }
        catch
        {
            return null;
        }
    }

    private static void SaveConfig(string baseDirectory, UnifiedConfig config)
    {
        var configDirectory = Path.Combine(baseDirectory, "config");
        var configPath = Path.Combine(configDirectory, "avalonia.json");
        Directory.CreateDirectory(configDirectory);
        File.WriteAllText(configPath, JsonSerializer.Serialize(config, SerializerOptions));
    }

    private static string ReadGlobalString(UnifiedConfig config, string key)
    {
        if (!config.GlobalValues.TryGetValue(key, out var node) || node is null)
        {
            return string.Empty;
        }

        if (node is JsonValue value && value.TryGetValue(out string? text) && text is not null)
        {
            return text;
        }

        return node.ToString();
    }

    private static void ClearPendingPackageState(UnifiedConfig config)
    {
        config.GlobalValues[ConfigurationKeys.VersionUpdatePackage] = JsonValue.Create(string.Empty);
    }

    private static bool IsManualInstallPackage(string packagePath)
    {
        return packagePath.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase)
            || (OperatingSystem.IsLinux() && packagePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            || packagePath.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase);
    }

    private static void PrepareExtractionDirectory(string extractDirectory)
    {
        SafeDeleteDirectory(extractDirectory);
        Directory.CreateDirectory(extractDirectory);
    }

    private static void ExtractPackageToDirectory(string packagePath, string extractDirectory)
    {
        if (packagePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(packagePath, extractDirectory);
            return;
        }

        if (packagePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
            || packagePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            using var source = File.OpenRead(packagePath);
            using var gzip = new GZipStream(source, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gzip, extractDirectory, overwriteFiles: true);
            return;
        }

        throw new InvalidDataException($"Unsupported update package format: {packagePath}");
    }

    private static string[] LoadRemoveList(string removeListFile, string changesFile)
    {
        if (File.Exists(removeListFile))
        {
            return File.ReadAllLines(removeListFile)
                .Where(static line => !string.IsNullOrWhiteSpace(line))
                .ToArray();
        }

        if (!File.Exists(changesFile))
        {
            return [];
        }

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(changesFile)) as JsonObject;
            return root?["deleted"] is JsonArray deleted
                ? deleted.Select(static node => node?.GetValue<string>())
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                    .Cast<string>()
                    .ToArray()
                : [];
        }
        catch
        {
            return [];
        }
    }

    private static PendingUpdateBackupEntry[] BuildBackupEntries(
        string targetRoot,
        string backupRoot,
        IEnumerable<string> relativePaths)
    {
        return relativePaths
            .Select(relativePath => new PendingUpdateBackupEntry(
                ResolveManifestPathUnderRoot(targetRoot, relativePath),
                ResolveManifestPathUnderRoot(backupRoot, relativePath)))
            .ToArray();
    }

    private static PendingUpdateDirectoryEntry[] BuildExtractedDirectoryEntries(
        string extractRoot,
        string targetRoot,
        string backupRoot)
    {
        return Directory.GetDirectories(extractRoot, "*", SearchOption.AllDirectories)
            .Select(directory => GetPackageRelativePath(extractRoot, directory))
            .Select(relativePath => new PendingUpdateDirectoryEntry(
                ResolvePackagePathUnderRoot(targetRoot, relativePath),
                ResolvePackagePathUnderRoot(backupRoot, relativePath)))
            .ToArray();
    }

    private static PendingUpdatePayloadFileEntry[] BuildPayloadFileEntries(
        string extractRoot,
        string targetRoot,
        string backupRoot)
    {
        return Directory.GetFiles(extractRoot, "*", SearchOption.AllDirectories)
            .Where(static file => !IsControlFileName(Path.GetFileName(file)))
            .Select(file =>
            {
                var relativePath = GetPackageRelativePath(extractRoot, file);
                return new PendingUpdatePayloadFileEntry(
                    ResolvePackagePathUnderRoot(extractRoot, relativePath),
                    ResolvePackagePathUnderRoot(targetRoot, relativePath),
                    ResolvePackagePathUnderRoot(backupRoot, relativePath));
            })
            .ToArray();
    }

    private static bool IsControlFileName(string fileName)
    {
        return string.Equals(fileName, "removelist.txt", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "changes.json", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetPackageRelativePath(string rootPath, string fullPath)
    {
        return NormalizeRelativePath(Path.GetRelativePath(rootPath, fullPath), trim: false);
    }

    private static string ResolveManifestPathUnderRoot(string rootPath, string relativePath)
    {
        return ResolvePathUnderRoot(rootPath, relativePath, trimRelativePath: true);
    }

    private static string ResolvePackagePathUnderRoot(string rootPath, string relativePath)
    {
        return ResolvePathUnderRoot(rootPath, relativePath, trimRelativePath: false);
    }

    private static string ResolvePathUnderRoot(string rootPath, string relativePath, bool trimRelativePath)
    {
        if (!TryResolvePathUnderRoot(rootPath, relativePath, trimRelativePath, out var resolvedPath))
        {
            throw new InvalidDataException($"Illegal path in update package: {relativePath}");
        }

        return resolvedPath;
    }

    private static bool TryResolvePathUnderRoot(
        string rootPath,
        string relativePath,
        bool trimRelativePath,
        out string resolvedPath)
    {
        resolvedPath = string.Empty;
        if (relativePath is null)
        {
            return false;
        }

        var normalizedRelativePath = NormalizeRelativePath(relativePath, trimRelativePath);
        if (string.IsNullOrEmpty(normalizedRelativePath))
        {
            return false;
        }

        if (Path.IsPathRooted(normalizedRelativePath)
            || (OperatingSystem.IsWindows() && HasWindowsDriveSpecifier(normalizedRelativePath)))
        {
            return false;
        }

        var normalizedRoot = EnsureTrailingSeparator(Path.GetFullPath(rootPath));
        var candidatePath = Path.GetFullPath(Path.Combine(rootPath, normalizedRelativePath));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!candidatePath.StartsWith(normalizedRoot, comparison))
        {
            return false;
        }

        resolvedPath = candidatePath;
        return true;
    }

    private static string NormalizeRelativePath(string relativePath, bool trim)
    {
        var normalizedPath = trim ? relativePath.Trim() : relativePath;
        return normalizedPath
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
    }

    private static bool HasWindowsDriveSpecifier(string relativePath)
    {
        return relativePath.Length >= 2
            && relativePath[1] == ':'
            && ((relativePath[0] >= 'A' && relativePath[0] <= 'Z')
                || (relativePath[0] >= 'a' && relativePath[0] <= 'z'));
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    private static void MoveExistingEntryToBackup(string sourcePath, string backupPath)
    {
        DeleteFileWithBackup(backupPath);
        EnsureParentDirectoryExists(backupPath);
        File.Move(sourcePath, backupPath);
    }

    private static void DeleteFileWithBackup(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        try
        {
            File.Delete(filePath);
        }
        catch
        {
            var index = 0;
            var currentDate = DateTime.Now.ToString("yyyyMMddHHmm");
            var backupPath = $"{filePath}.{currentDate}.{index}";
            while (File.Exists(backupPath))
            {
                index++;
                backupPath = $"{filePath}.{currentDate}.{index}";
            }

            EnsureParentDirectoryExists(backupPath);
            File.Move(filePath, backupPath);
        }
    }

    private static void MoveDirectoryWithBackup(string sourceDirectory, string backupDirectory)
    {
        try
        {
            SafeDeleteDirectory(backupDirectory);
            EnsureParentDirectoryExists(backupDirectory);
            Directory.Move(sourceDirectory, backupDirectory);
        }
        catch
        {
            SafeDeleteDirectory(backupDirectory);
            SafeDeleteDirectory(sourceDirectory);
        }
    }

    private static void EnsureParentDirectoryExists(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }
    }

    private static void SafeDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private sealed record PendingUpdateBackupEntry(
        string TargetPath,
        string BackupPath);

    private sealed record PendingUpdateDirectoryEntry(
        string TargetPath,
        string BackupPath);

    private sealed record PendingUpdatePayloadFileEntry(
        string SourcePath,
        string TargetPath,
        string BackupPath);
}
