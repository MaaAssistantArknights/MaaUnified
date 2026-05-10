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

            if (removeList.Length > 0)
            {
                foreach (var relativePath in removeList)
                {
                    var currentPath = Path.Combine(baseDirectory, relativePath);
                    if (!File.Exists(currentPath))
                    {
                        continue;
                    }

                    var backupPath = Path.Combine(backupDirectory, relativePath);
                    MoveExistingEntryToBackup(currentPath, backupPath);
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

                    var currentDirectory = Path.Combine(baseDirectory, directoryName);
                    if (!Directory.Exists(currentDirectory))
                    {
                        continue;
                    }

                    var currentBackupDirectory = Path.Combine(backupDirectory, directoryName);
                    MoveDirectoryWithBackup(currentDirectory, currentBackupDirectory);
                }
            }

            Directory.CreateDirectory(backupDirectory);
            foreach (var directory in Directory.GetDirectories(extractDirectory, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(directory.Replace(extractDirectory, baseDirectory, StringComparison.Ordinal));
                Directory.CreateDirectory(directory.Replace(extractDirectory, backupDirectory, StringComparison.Ordinal));
            }

            foreach (var file in Directory.GetFiles(extractDirectory, "*", SearchOption.AllDirectories))
            {
                var fileName = Path.GetFileName(file);
                if (string.Equals(fileName, "removelist.txt", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(fileName, "changes.json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var currentFilePath = file.Replace(extractDirectory, baseDirectory, StringComparison.Ordinal);
                if (File.Exists(currentFilePath))
                {
                    var backupPath = file.Replace(extractDirectory, backupDirectory, StringComparison.Ordinal);
                    DeleteFileWithBackup(backupPath);
                    EnsureParentDirectoryExists(backupPath);
                    File.Move(currentFilePath, backupPath);
                }
                else
                {
                    EnsureParentDirectoryExists(currentFilePath);
                }

                File.Move(file, currentFilePath);
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
            return File.ReadAllLines(removeListFile);
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
}
