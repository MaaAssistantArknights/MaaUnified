using System.Formats.Tar;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Compat.Constants;
using MAAUnified.Compat.Runtime;

namespace MAAUnified.Application.Services.VersionUpdate;

public enum PendingAppUpdateStatus
{
    None = 0,
    Applied = 1,
    Failed = 2,
    Delegated = 3,
}

public sealed record PendingAppUpdateInfo(
    string? VersionName,
    string PackagePath);

public sealed record PendingAppUpdateApplyResult(
    PendingAppUpdateStatus Status,
    string Message,
    string? PackagePath = null,
    bool RequiresManualRecovery = false);

/// <summary>
/// 本地更新包注册结果。
/// </summary>
/// <param name="Registered">已写入待安装更新包状态。</param>
/// <param name="Message">诊断信息（英文，进日志）。</param>
/// <param name="TargetVersion">识别出的目标版本。</param>
/// <param name="SameVersionReinstall">本次注册为同版本重装（完整性修复）。</param>
public sealed record PendingUpdateRegistrationResult(
    bool Registered,
    string Message,
    string? TargetVersion = null,
    bool SameVersionReinstall = false);

public static class PendingAppUpdateService
{
    /// <summary>
    /// 消费外部更新器成功标记（WPF <c>PendingUpdateApplier.TryConsumeDelegatedUpdateSuccess</c>，
    /// <c>217db38484</c>）：成功文件存在说明更新器已在重启前完成安装。清除待安装包状态、
    /// 失败标记与修复标记并删除成功文件，避免下一次启动重复应用或误报失败。
    /// </summary>
    /// <param name="baseDirectory">安装根目录。</param>
    /// <returns>消费到成功标记时为 <see langword="true"/>。</returns>
    public static bool TryConsumeDelegatedUpdateSuccess(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        var successPath = Path.Combine(baseDirectory, ExternalUpdaterDelegation.SuccessStatusFileName);
        if (!File.Exists(successPath))
        {
            return false;
        }

        var configStore = MAAUnifiedRuntimeFactory.CreateConfigStore(baseDirectory);
        var commitLock = configStore.AcquireCommitLockAsync().AsTask().GetAwaiter().GetResult();
        try
        {
            var config = TryLoadConfig(configStore);
            if (config is not null)
            {
                ClearPendingPackageState(config);
                SaveConfig(configStore, config);
            }
        }
        finally
        {
            commitLock.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        PendingUpdateFailureState.Clear(baseDirectory);
        UpdateRepairState.Clear(baseDirectory);
        TryDeleteFile(successPath);
        return true;
    }

    public static PendingAppUpdateInfo? ResolvePendingUpdate(string baseDirectory)
    {
        var config = TryLoadConfig(MAAUnifiedRuntimeFactory.CreateConfigStore(baseDirectory));
        if (config is null)
        {
            return null;
        }

        return ResolvePendingUpdate(baseDirectory, config);
    }

    private static PendingAppUpdateInfo? ResolvePendingUpdate(string baseDirectory, UnifiedConfig config)
    {
        var versionName = ReadGlobalString(config, ConfigurationKeys.VersionName);
        var configuredPackagePath = ReadGlobalString(config, ConfigurationKeys.VersionUpdatePackage);
        if (string.IsNullOrWhiteSpace(versionName)
            || string.IsNullOrWhiteSpace(configuredPackagePath))
        {
            return null;
        }

        var resolvedPackagePath = Path.IsPathRooted(configuredPackagePath)
            ? configuredPackagePath
            : Path.Combine(baseDirectory, configuredPackagePath);
        return new PendingAppUpdateInfo(
            versionName,
            resolvedPackagePath);
    }

    /// <summary>
    /// 完整包修复入口：校验本地更新包文件名（平台 / 架构 / 版本方向）后注册为待安装更新包，
    /// 并清掉跨启动保留的失败标记（注册即代表用户已着手修复，WPF <c>217db38484</c> 的
    /// <c>ClearDelegatedUpdateFailureState</c> 语义）。
    /// </summary>
    /// <param name="baseDirectory">安装根目录。</param>
    /// <param name="packagePath">本地完整包 / OTA 包路径。</param>
    /// <param name="currentVersion">当前版本（用于版本方向校验）。</param>
    /// <param name="allowSameVersionReinstall">允许同版本重装（资源损坏 / 更新失败后的完整包修复）。</param>
    public static PendingUpdateRegistrationResult TryRegisterPendingUpdatePackage(
        string baseDirectory,
        string packagePath,
        string currentVersion,
        bool allowSameVersionReinstall = false)
        => TryRegisterPendingUpdatePackage(
            baseDirectory,
            MAAUnifiedRuntimeFactory.CreateConfigStore(baseDirectory),
            packagePath,
            currentVersion,
            allowSameVersionReinstall);

    internal static PendingUpdateRegistrationResult TryRegisterPendingUpdatePackage(
        string baseDirectory,
        IUnifiedConfigStore configStore,
        string packagePath,
        string currentVersion,
        bool allowSameVersionReinstall = false)
    {
        if (!File.Exists(packagePath))
        {
            return new PendingUpdateRegistrationResult(false, $"Update package does not exist: {packagePath}");
        }

        if (!UpdatePackageNameRecognizer.TryRecognize(packagePath, out var identity))
        {
            // 包名不符合任何已知更新包布局：显式拒绝，避免把资源包或无关压缩包注册成待安装更新。
            return new PendingUpdateRegistrationResult(
                false,
                $"Package name does not match a known update package layout: {Path.GetFileName(packagePath)}");
        }

        var validation = UpdatePackageNameRecognizer.Validate(
            identity,
            currentVersion,
            ResolveCurrentPlatform(),
            ResolveCurrentArchitecture(),
            allowSameVersionReinstall);
        if (!validation.Supported)
        {
            return new PendingUpdateRegistrationResult(false, validation.Reason, identity.TargetVersion);
        }

        if (UpdateRepairState.RequiresFullPackage(baseDirectory)
            && identity.Kind != UpdatePackageKind.FullPackage)
        {
            // 安装文件缺失或上次更新失败期间 OTA 只含版本间差异，无法修复残留的不一致文件
            // （WPF da79ff88ad / VersionUpdate.OtaRejectedByBrokenInstall）。
            return new PendingUpdateRegistrationResult(
                false,
                "The installation requires a full package update; OTA packages cannot repair missing or inconsistent files.",
                identity.TargetVersion);
        }

        var fullPackagePath = Path.GetFullPath(packagePath);
        var commitLock = configStore.AcquireCommitLockAsync().AsTask().GetAwaiter().GetResult();
        try
        {
            var config = TryLoadConfig(configStore);
            if (config is null)
            {
                return new PendingUpdateRegistrationResult(false, "No Avalonia config was found.");
            }

            config.GlobalValues[ConfigurationKeys.VersionName] = JsonValue.Create(identity.TargetVersion);
            config.GlobalValues[ConfigurationKeys.VersionUpdatePackage] = JsonValue.Create(fullPackagePath);
            config.GlobalValues[ConfigurationKeys.VersionUpdateIsFirstBoot] = JsonValue.Create(bool.FalseString);
            SaveConfig(configStore, config);
        }
        finally
        {
            commitLock.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        PendingUpdateFailureState.Clear(baseDirectory);
        if (identity.Kind == UpdatePackageKind.FullPackage)
        {
            UpdateRepairState.Clear(baseDirectory);
        }

        var sameVersion = UpdatePackageNameRecognizer.CompareVersions(identity.TargetVersion, currentVersion) == 0;
        return new PendingUpdateRegistrationResult(
            true,
            $"Registered pending update package `{Path.GetFileName(fullPackagePath)}` (target {identity.TargetVersion}).",
            identity.TargetVersion,
            SameVersionReinstall: sameVersion);
    }

    private static string ResolveCurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return "windows";
        }

        return OperatingSystem.IsMacOS() ? "macos" : "linux";
    }

    private static string ResolveCurrentArchitecture()
        => RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            _ => "x64",
        };

    private static string ResolveMutexName(string baseDirectory)
    {
        try
        {
            return PackageInstanceGuard.BuildMutexName(RuntimeLayout.ResolveSingleInstanceIdentityPath(baseDirectory));
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string? ResolveRelaunchExecutable(string baseDirectory)
    {
        var packagedExecutable = Path.Combine(
            baseDirectory,
            OperatingSystem.IsWindows() ? "MAAUnified.exe" : "MAAUnified");
        return File.Exists(packagedExecutable) ? packagedExecutable : Environment.ProcessPath;
    }

    /// <summary>
    /// 委托外部更新器应用待安装更新包。能力不支持、更新器缺失、解压/计划构造失败或进程启动失败时
    /// 返回 <c>null</c>，调用方继续走进程内应用（非 Windows 的显式降级路径）。
    /// 启动成功时返回 <see cref="PendingAppUpdateStatus.Delegated"/>，保留更新包与解压目录供更新器使用。
    /// </summary>
    private static PendingAppUpdateApplyResult? TryDelegatePendingUpdateToExternalUpdater(
        string baseDirectory,
        UnifiedConfig config,
        PendingAppUpdateInfo pendingUpdate,
        UpdateApplyCapabilities capabilities,
        IExternalUpdaterProcessLauncher externalUpdaterLauncher,
        string? mutexName,
        string? relaunchExecutablePath,
        string extractDirectory,
        string backupDirectory)
    {
        if (!capabilities.ExternalUpdaterSupported)
        {
            return null;
        }

        var updaterPath = Path.Combine(baseDirectory, capabilities.ExternalUpdaterExecutableName);
        if (!File.Exists(updaterPath))
        {
            return null;
        }

        string? planPath = null;
        try
        {
            PrepareExtractionDirectory(extractDirectory);
            ExtractPackageToDirectory(pendingUpdate.PackagePath, extractDirectory);

            var removeListFile = Path.Combine(extractDirectory, "removelist.txt");
            var changesFile = Path.Combine(extractDirectory, "changes.json");
            var isOtaPackage = File.Exists(removeListFile) || File.Exists(changesFile);
            var removeEntries = isOtaPackage
                ? BuildDelegatedRemoveEntries(baseDirectory, LoadRemoveList(removeListFile, changesFile))
                : BuildDelegatedFullPackageRemoveEntries(baseDirectory, extractDirectory);
            var moveEntries = BuildDelegatedMoveEntries(extractDirectory);

            planPath = Path.Combine(baseDirectory, $"maa-pending-update-{Guid.NewGuid():N}.json");
            ExternalUpdaterDelegation.WritePlan(
                planPath,
                isOtaPackage ? "ota" : "full",
                removeEntries,
                moveEntries);

            var spec = new ExternalUpdaterLaunchSpec(
                ParentProcessId: Environment.ProcessId,
                ExecutablePath: updaterPath,
                BaseDirectory: baseDirectory,
                ExtractDirectory: extractDirectory,
                BackupDirectory: backupDirectory,
                PackagePath: pendingUpdate.PackagePath,
                SuccessStatusFilePath: Path.Combine(baseDirectory, ExternalUpdaterDelegation.SuccessStatusFileName),
                FailureStatusFilePath: PendingUpdateFailureState.ResolvePath(baseDirectory),
                RelaunchExecutablePath: relaunchExecutablePath ?? string.Empty,
                PlanPath: planPath,
                MutexName: mutexName ?? string.Empty,
                ShowConsole: ReadGlobalBool(config, ConfigurationKeys.ShowUpdaterConsole, true),
                ShowProgress: ReadGlobalBool(config, ConfigurationKeys.ShowUpdaterProgress, true));

            if (!externalUpdaterLauncher.TryStart(spec, out var launchMessage))
            {
                SafeDeleteDirectory(extractDirectory);
                TryDeleteFile(planPath);
                return null;
            }

            return new PendingAppUpdateApplyResult(
                PendingAppUpdateStatus.Delegated,
                $"Delegated pending software update package `{Path.GetFileName(pendingUpdate.PackagePath)}` to external updater. {launchMessage}",
                pendingUpdate.PackagePath);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // 解压或计划构造失败：回退进程内路径，由既有异常处理落失败标记/回滚。
            SafeDeleteDirectory(extractDirectory);
            if (planPath is not null)
            {
                TryDeleteFile(planPath);
            }

            return null;
        }
    }

    private static string[] BuildDelegatedRemoveEntries(string baseDirectory, IEnumerable<string> removeList)
    {
        var entries = new List<string>();
        foreach (var rawEntry in removeList)
        {
            // 复用进程内路径校验，非法条目交给进程内路径抛 InvalidDataException 走统一失败处理。
            _ = ResolveManifestPathUnderRoot(baseDirectory, rawEntry);
            if (ExternalUpdaterDelegation.TryNormalizePlanEntry(rawEntry, out var normalized))
            {
                entries.Add(normalized);
            }
        }

        return entries.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string[] BuildDelegatedFullPackageRemoveEntries(string baseDirectory, string extractDirectory)
    {
        return Directory.GetDirectories(extractDirectory)
            .Select(Path.GetFileName)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Where(name => Directory.Exists(Path.Combine(baseDirectory, name!)))
            .Select(name => name!)
            .Select(name => ExternalUpdaterDelegation.TryNormalizePlanEntry(name, out var normalized) ? normalized : throw new InvalidDataException($"Illegal path in update package: {name}"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] BuildDelegatedMoveEntries(string extractDirectory)
    {
        return Directory.GetFiles(extractDirectory, "*", SearchOption.AllDirectories)
            .Where(static file => !IsControlFileName(Path.GetFileName(file)))
            .Select(file => Path.GetRelativePath(extractDirectory, file))
            .Select(path => ExternalUpdaterDelegation.TryNormalizePlanEntry(path, out var normalized)
                ? normalized
                : throw new InvalidDataException($"Illegal path in update package: {path}"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool ReadGlobalBool(UnifiedConfig config, string key, bool fallback)
    {
        if (!config.GlobalValues.TryGetValue(key, out var node) || node is null)
        {
            return fallback;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue(out bool booleanValue))
            {
                return booleanValue;
            }

            if (value.TryGetValue(out string? text) && bool.TryParse(text, out var parsedText))
            {
                return parsedText;
            }
        }

        return bool.TryParse(node.ToString(), out var parsed) ? parsed : fallback;
    }

    public static PendingAppUpdateApplyResult TryApplyPendingUpdatePackage(string baseDirectory)
        => TryApplyPendingUpdatePackage(
            baseDirectory,
            MAAUnifiedRuntimeFactory.CreateConfigStore(baseDirectory),
            UpdateApplyPlatform.Detect(baseDirectory),
            new ProcessExternalUpdaterProcessLauncher(),
            ResolveMutexName(baseDirectory),
            ResolveRelaunchExecutable(baseDirectory));

    internal static PendingAppUpdateApplyResult TryApplyPendingUpdatePackage(
        string baseDirectory,
        IUnifiedConfigStore configStore)
        => TryApplyPendingUpdatePackage(
            baseDirectory,
            configStore,
            UpdateApplyPlatform.Detect(baseDirectory),
            new ProcessExternalUpdaterProcessLauncher(),
            ResolveMutexName(baseDirectory),
            ResolveRelaunchExecutable(baseDirectory));

    internal static PendingAppUpdateApplyResult TryApplyPendingUpdatePackage(
        string baseDirectory,
        IUnifiedConfigStore configStore,
        UpdateApplyCapabilities capabilities,
        IExternalUpdaterProcessLauncher externalUpdaterLauncher,
        string? mutexName = null,
        string? relaunchExecutablePath = null)
    {
        var commitLock = configStore.AcquireCommitLockAsync().AsTask().GetAwaiter().GetResult();
        try
        {
            return TryApplyPendingUpdatePackageCore(
                baseDirectory,
                configStore,
                capabilities,
                externalUpdaterLauncher,
                mutexName,
                relaunchExecutablePath);
        }
        finally
        {
            commitLock.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static PendingAppUpdateApplyResult TryApplyPendingUpdatePackageCore(
        string baseDirectory,
        IUnifiedConfigStore configStore,
        UpdateApplyCapabilities capabilities,
        IExternalUpdaterProcessLauncher externalUpdaterLauncher,
        string? mutexName,
        string? relaunchExecutablePath)
    {
        var config = TryLoadConfig(configStore);
        if (config is null)
        {
            return new PendingAppUpdateApplyResult(
                PendingAppUpdateStatus.None,
                "No Avalonia config was found.");
        }

        var pendingUpdate = ResolvePendingUpdate(baseDirectory, config);
        if (pendingUpdate is null)
        {
            return new PendingAppUpdateApplyResult(
                PendingAppUpdateStatus.None,
                "No pending software update package was found.");
        }

        if (!File.Exists(pendingUpdate.PackagePath))
        {
            ClearPendingPackageState(config);
            SaveConfig(configStore, config);
            return new PendingAppUpdateApplyResult(
                PendingAppUpdateStatus.Failed,
                $"Pending software update package is missing: {pendingUpdate.PackagePath}",
                pendingUpdate.PackagePath);
        }

        if (IsManualInstallPackage(pendingUpdate.PackagePath))
        {
            ClearPendingPackageState(config);
            SaveConfig(configStore, config);
            return new PendingAppUpdateApplyResult(
                PendingAppUpdateStatus.Failed,
                $"Pending manual install packages cannot be applied automatically: {pendingUpdate.PackagePath}",
                pendingUpdate.PackagePath);
        }

        var extractDirectory = Path.Combine(baseDirectory, "NewVersionExtract");
        var backupDirectory = Path.Combine(baseDirectory, ".old");

        // Windows 且随包发布了 MAA.Updater.exe 时委托外部更新器：它会在本进程退出后处理
        // dll/exe 占用、互斥量与回滚。启动失败或非 Windows 显式降级回进程内应用。
        var delegatedResult = TryDelegatePendingUpdateToExternalUpdater(
            baseDirectory,
            config,
            pendingUpdate,
            capabilities,
            externalUpdaterLauncher,
            mutexName,
            relaunchExecutablePath,
            extractDirectory,
            backupDirectory);
        if (delegatedResult is not null)
        {
            return delegatedResult;
        }

        var rollbackActions = new List<PendingUpdateRollbackAction>();

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
                    rollbackActions.Add(new PendingUpdateRollbackAction(entry.BackupPath, currentPath));
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
                    rollbackActions.Add(new PendingUpdateRollbackAction(currentBackupDirectory, currentDirectory));
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
                    MoveFileWithRetry(file.TargetPath, file.BackupPath);
                    rollbackActions.Add(new PendingUpdateRollbackAction(file.BackupPath, file.TargetPath));
                }
                else
                {
                    EnsureParentDirectoryExists(file.TargetPath);
                }

                MoveFileWithRetry(file.SourcePath, file.TargetPath);
                rollbackActions.Add(new PendingUpdateRollbackAction(file.TargetPath, file.SourcePath));
            }

            SafeDeleteDirectory(extractDirectory);
            File.Delete(pendingUpdate.PackagePath);

            ClearPendingPackageState(config);
            config.GlobalValues[ConfigurationKeys.VersionUpdateIsFirstBoot] = JsonValue.Create(bool.TrueString);
            SaveConfig(configStore, config);

            // 成功应用后清掉跨启动保留的失败标记，避免下次启动继续提示修复。
            PendingUpdateFailureState.Clear(baseDirectory);
            var postApplyIntegrity = InstallationIntegrityChecker.Check(baseDirectory);
            if (postApplyIntegrity.Passed)
            {
                UpdateRepairState.Clear(baseDirectory);
            }
            else
            {
                UpdateRepairState.Write(
                    baseDirectory,
                    $"Installation integrity check still reports {postApplyIntegrity.MissingFiles.Count} missing file(s) after applying the update.");
            }

            return new PendingAppUpdateApplyResult(
                PendingAppUpdateStatus.Applied,
                $"Applied pending software update package `{Path.GetFileName(pendingUpdate.PackagePath)}`.",
                pendingUpdate.PackagePath);
        }
        catch (InvalidDataException ex)
        {
            // 清单/路径校验在搬移过程中也可能抛出（例如已删若干文件后才遇到非法路径），
            // 因此这里同样先回滚；回滚失败才落跨启动保留的失败标记（WPF 217db38484）。
            var rollbackError = TryRollback(rollbackActions);
            TryDeleteFile(pendingUpdate.PackagePath);
            ClearPendingPackageState(config);
            SaveConfig(configStore, config);
            if (rollbackError is not null)
            {
                var recoveryReason = $"Invalid update package `{Path.GetFileName(pendingUpdate.PackagePath)}`: {ex.Message} Rollback also failed: {rollbackError.Message}";
                PendingUpdateFailureState.Write(baseDirectory, recoveryReason);
                return new PendingAppUpdateApplyResult(
                    PendingAppUpdateStatus.Failed,
                    $"Pending software update package is invalid and the installation could not be fully restored: {pendingUpdate.PackagePath}",
                    pendingUpdate.PackagePath,
                    RequiresManualRecovery: true);
            }

            return new PendingAppUpdateApplyResult(
                PendingAppUpdateStatus.Failed,
                $"Pending software update package is invalid: {pendingUpdate.PackagePath}",
                pendingUpdate.PackagePath);
        }
        catch (Exception ex)
        {
            var rollbackError = TryRollback(rollbackActions);
            var message = rollbackError is null
                ? $"Failed to apply pending software update package: {ex.Message}"
                : $"Failed to apply pending software update package: {ex.Message} Rollback also failed: {rollbackError.Message}";
            if (rollbackError is not null)
            {
                // 安装内容已变动且无法回滚：落盘跨启动保留的失败标记，下次启动提示并走完整包修复（P0-12）。
                PendingUpdateFailureState.Write(
                    baseDirectory,
                    $"Failed to apply pending software update package `{Path.GetFileName(pendingUpdate.PackagePath)}`: {ex.Message} Rollback also failed: {rollbackError.Message}");
            }

            return new PendingAppUpdateApplyResult(
                PendingAppUpdateStatus.Failed,
                message,
                pendingUpdate.PackagePath,
                RequiresManualRecovery: rollbackError is not null);
        }
        finally
        {
            SafeDeleteDirectory(extractDirectory);
        }
    }

    private static UnifiedConfig? TryLoadConfig(IUnifiedConfigStore configStore)
    {
        try
        {
            return configStore.LoadAsync().GetAwaiter().GetResult();
        }
        catch
        {
            return null;
        }
    }

    private static void SaveConfig(IUnifiedConfigStore configStore, UnifiedConfig config)
        => configStore.SaveAsync(config).GetAwaiter().GetResult();

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
        MoveFileWithRetry(sourcePath, backupPath);
    }

    /// <summary>
    /// 搬移文件时按占用重试：Windows 上杀软扫描新解压文件或句柄短暂占用会让 <see cref="File.Move(string, string)"/>
    /// 抛 <see cref="IOException"/>（WPF <c>208d28d6fa</c> 的占用重试语义）；macOS/Linux 等价于一次成功。
    /// </summary>
    private static void MoveFileWithRetry(string sourcePath, string destinationPath)
        => FileOperationRetry.Execute(() => File.Move(sourcePath, destinationPath));

    private static void MoveDirectoryWithRetry(string sourceDirectory, string destinationDirectory)
        => FileOperationRetry.Execute(() => Directory.Move(sourceDirectory, destinationDirectory));

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
        SafeDeleteDirectory(backupDirectory);
        EnsureParentDirectoryExists(backupDirectory);
        MoveDirectoryWithRetry(sourceDirectory, backupDirectory);
    }

    private static Exception? TryRollback(IReadOnlyList<PendingUpdateRollbackAction> rollbackActions)
    {
        Exception? firstError = null;
        for (var index = rollbackActions.Count - 1; index >= 0; index--)
        {
            var action = rollbackActions[index];
            try
            {
                MovePathWithOverwrite(action.SourcePath, action.DestinationPath);
            }
            catch (Exception ex)
            {
                firstError ??= ex;
            }
        }

        return firstError;
    }

    private static void MovePathWithOverwrite(string sourcePath, string destinationPath)
    {
        if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
        {
            return;
        }

        DeleteEntryIfExists(destinationPath);
        EnsureParentDirectoryExists(destinationPath);
        if (Directory.Exists(sourcePath))
        {
            MoveDirectoryWithRetry(sourcePath, destinationPath);
        }
        else
        {
            MoveFileWithRetry(sourcePath, destinationPath);
        }
    }

    private static void DeleteEntryIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        else if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
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

    private sealed record PendingUpdateRollbackAction(
        string SourcePath,
        string DestinationPath);

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
