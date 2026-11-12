using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Security;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.VersionUpdate;
using MAAUnified.Compat.Constants;
using Xunit;

namespace MAAUnified.Tests;

public sealed class VersionUpdateIntegrityTests
{
    // ---------- P0-12: filelist 完整性校验 ----------

    [Fact]
    public void InstallationIntegrityChecker_WhenFileListMissing_PassesWithoutChecking()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "app.txt"), "content");

        var result = InstallationIntegrityChecker.Check(root.Path);

        Assert.False(result.FileListAvailable);
        Assert.True(result.Passed);
        Assert.Equal(0, result.CheckedFileCount);
    }

    [Fact]
    public void InstallationIntegrityChecker_ReportsMissingFilesAndSkipsDllExePy()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "app.txt"), "content");
        Directory.CreateDirectory(Path.Combine(root.Path, "resource"));
        File.WriteAllText(Path.Combine(root.Path, "resource", "tasks.json"), "{}");
        File.WriteAllText(
            Path.Combine(root.Path, InstallationIntegrityChecker.FileListName),
            string.Join(
                '\n',
                "app.txt",
                "resource/tasks.json",
                "resource/missing.json",
                "MAAUnified.dll",
                "MAAUnified.exe",
                "Python/main.py",
                ""));

        var result = InstallationIntegrityChecker.Check(root.Path);

        Assert.True(result.FileListAvailable);
        Assert.Equal(3, result.CheckedFileCount);
        Assert.Equal(["resource/missing.json"], result.MissingFiles);
        Assert.False(result.Passed);
    }

    [Fact]
    public void InstallationIntegrityChecker_IgnoresDirectoryEntriesAbsolutePathsAndTraversal()
    {
        using var root = TestRoot.Create();
        var outsideFile = Path.Combine(Path.GetDirectoryName(root.Path)!, $"outside-{Guid.NewGuid():N}.txt");
        File.WriteAllText(outsideFile, "outside");
        try
        {
            File.WriteAllText(
                Path.Combine(root.Path, InstallationIntegrityChecker.FileListName),
                string.Join(
                    '\n',
                    "resource/",
                    "resource\\",
                    "../escaped.txt",
                    "resource/../../escaped.txt",
                    outsideFile,
                    "ok.txt"));

            var result = InstallationIntegrityChecker.Check(root.Path);

            // 只保留唯一一条合法检查项；目录条目 / 绝对路径 / 路径穿越条目全部跳过，不产生误报。
            Assert.Equal(1, result.CheckedFileCount);
            Assert.Equal(["ok.txt"], result.MissingFiles);
        }
        finally
        {
            File.Delete(outsideFile);
        }
    }

    [Fact]
    public void InstallationIntegrityChecker_WhenAllFilesPresent_Passes()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "app.txt"), "content");
        File.WriteAllText(
            Path.Combine(root.Path, InstallationIntegrityChecker.FileListName),
            "app.txt\n");

        var result = InstallationIntegrityChecker.Check(root.Path);

        Assert.True(result.Passed);
        Assert.Equal(1, result.CheckedFileCount);
    }

    // ---------- P0-12: 失败标记跨启动可见 ----------

    [Fact]
    public void PendingUpdateFailureState_PersistsAcrossStartupsUntilCleared()
    {
        using var root = TestRoot.Create();

        Assert.Null(PendingUpdateFailureState.TryRead(root.Path));
        Assert.False(PendingUpdateFailureState.Exists(root.Path));

        PendingUpdateFailureState.Write(root.Path, "Failed to move file into place: app.txt");

        // 第一次启动读取：存在且有原因，读取后文件仍保留（WPF 217db38484：不再启动即删）。
        Assert.Equal("Failed to move file into place: app.txt", PendingUpdateFailureState.TryRead(root.Path));
        Assert.True(PendingUpdateFailureState.Exists(root.Path));

        // 第二次启动仍然可见。
        Assert.Equal("Failed to move file into place: app.txt", PendingUpdateFailureState.TryRead(root.Path));
        Assert.True(File.Exists(PendingUpdateFailureState.ResolvePath(root.Path)));

        PendingUpdateFailureState.Clear(root.Path);
        Assert.False(PendingUpdateFailureState.Exists(root.Path));
    }

    [Fact]
    public void UpdateStartupHealthInspector_WhenFailureMarkerPresent_RequiresRepairAndKeepsMarker()
    {
        using var root = TestRoot.Create();
        PendingUpdateFailureState.Write(root.Path, "Failed to back up existing entry: app.txt");

        var firstStartup = UpdateStartupHealthInspector.Inspect(root.Path);
        var secondStartup = UpdateStartupHealthInspector.Inspect(root.Path);

        Assert.True(firstStartup.RequiresRepair);
        Assert.True(firstStartup.HasPersistedUpdateFailure);
        Assert.Equal("Failed to back up existing entry: app.txt", firstStartup.UpdateFailureReason);
        Assert.True(secondStartup.RequiresRepair);
        Assert.True(PendingUpdateFailureState.Exists(root.Path));
    }

    [Fact]
    public void UpdateStartupHealthInspector_WhenInstallationFileMissing_RequiresRepair()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(
            Path.Combine(root.Path, InstallationIntegrityChecker.FileListName),
            "app.txt\nresource/missing.json\n");

        var report = UpdateStartupHealthInspector.Inspect(root.Path);

        Assert.True(report.RequiresRepair);
        Assert.False(report.HasPersistedUpdateFailure);
        Assert.True(report.HasMissingInstallationFiles);
        Assert.Equal(["app.txt", "resource/missing.json"], report.MissingFiles);
    }

    [Fact]
    public void UpdateStartupHealthInspector_WhenClean_DoesNotRequireRepair()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "app.txt"), "content");
        File.WriteAllText(Path.Combine(root.Path, InstallationIntegrityChecker.FileListName), "app.txt\n");

        var report = UpdateStartupHealthInspector.Inspect(root.Path);

        Assert.False(report.RequiresRepair);
        Assert.Empty(report.MissingFiles);
    }

    [Fact]
    public async Task TryApplyPendingUpdatePackage_WhenApplied_ClearsPersistedFailureMarker()
    {
        using var root = TestRoot.Create();
        var packageDirectory = Path.Combine(root.Path, "update-packages");
        Directory.CreateDirectory(packageDirectory);
        File.WriteAllText(Path.Combine(root.Path, "app.txt"), "old");
        var packagePath = Path.Combine(packageDirectory, "update.zip");
        await CreateZipAsync(packagePath, ("app.txt", "new"));
        await SavePendingUpdateAsync(root.Path, Path.Combine("update-packages", "update.zip"));
        PendingUpdateFailureState.Write(root.Path, "previous attempt failed");

        var result = PendingAppUpdateService.TryApplyPendingUpdatePackage(root.Path);

        Assert.Equal(PendingAppUpdateStatus.Applied, result.Status);
        Assert.Null(PendingUpdateFailureState.TryRead(root.Path));
    }

    [Fact]
    public async Task TryApplyPendingUpdatePackage_WhenInvalidPackageChangesNothing_DoesNotWriteFailureMarker()
    {
        using var root = TestRoot.Create();
        var packageDirectory = Path.Combine(root.Path, "update-packages");
        Directory.CreateDirectory(packageDirectory);
        var packagePath = Path.Combine(packageDirectory, "update.zip");
        await File.WriteAllBytesAsync(packagePath, [1, 2, 3, 4]);
        await SavePendingUpdateAsync(root.Path, Path.Combine("update-packages", "update.zip"));

        var result = PendingAppUpdateService.TryApplyPendingUpdatePackage(root.Path);

        Assert.Equal(PendingAppUpdateStatus.Failed, result.Status);
        Assert.False(result.RequiresManualRecovery);
        // 安装内容未变动时不落失败标记，避免把「手滑点了个坏包」误报成半更新状态。
        Assert.False(PendingUpdateFailureState.Exists(root.Path));
    }

    // ---------- P0-13: 包名识别 + 同版本完整包修复 ----------

    [Theory]
    [InlineData("MAAUnified-v1.2.3-windows-x64.zip", UpdatePackageKind.FullPackage, null, "v1.2.3", "windows", "x64")]
    [InlineData("MAAUnified-v1.2.3-win-x64.zip", UpdatePackageKind.FullPackage, null, "v1.2.3", "windows", "x64")]
    [InlineData("MAAUnified-v1.2.3-linux-arm64.tar.gz", UpdatePackageKind.FullPackage, null, "v1.2.3", "linux", "arm64")]
    [InlineData("MAAUnified-v1.2.3-macos-x64.dmg", UpdatePackageKind.FullPackage, null, "v1.2.3", "macos", "x64")]
    [InlineData("MAAUnifiedComponent-OTA-v1.2.2_v1.2.3-windows-x64.zip", UpdatePackageKind.OtaPackage, "v1.2.2", "v1.2.3", "windows", "x64")]
    [InlineData("MAA-v1.2.3-win-x64.zip", UpdatePackageKind.FullPackage, null, "v1.2.3", "windows", "x64")]
    [InlineData("MAAComponent-OTA-v1.2.2_v1.2.3-win-x64.zip", UpdatePackageKind.OtaPackage, "v1.2.2", "v1.2.3", "windows", "x64")]
    public void UpdatePackageNameRecognizer_RecognizesKnownLayouts(
        string packageName,
        UpdatePackageKind expectedKind,
        string? expectedSourceVersion,
        string expectedTargetVersion,
        string expectedPlatform,
        string expectedArchitecture)
    {
        Assert.True(UpdatePackageNameRecognizer.TryRecognize(packageName, out var identity));

        Assert.Equal(expectedKind, identity.Kind);
        Assert.Equal(expectedSourceVersion, identity.SourceVersion);
        Assert.Equal(expectedTargetVersion, identity.TargetVersion);
        Assert.Equal(expectedPlatform, identity.Platform);
        Assert.Equal(expectedArchitecture, identity.Architecture);
    }

    [Theory]
    [InlineData("MAAUnified-resource-v1.2.3.zip")]
    [InlineData("archive.zip")]
    [InlineData("")]
    [InlineData("MAAUnified-v1.2.3-windows-x64.7z")]
    public void UpdatePackageNameRecognizer_RejectsUnknownLayouts(string packageName)
    {
        Assert.False(UpdatePackageNameRecognizer.TryRecognize(packageName, out _));
    }

    [Fact]
    public void UpdatePackageNameRecognizer_Validate_RejectsWrongArchitecturePlatformAndDirection()
    {
        Assert.True(UpdatePackageNameRecognizer.TryRecognize("MAAUnified-v2.0.0-linux-x64.zip", out var identity));

        Assert.False(UpdatePackageNameRecognizer.Validate(identity, "v1.0.0", "linux", "arm64").Supported);
        Assert.False(UpdatePackageNameRecognizer.Validate(identity, "v1.0.0", "windows", "x64").Supported);

        Assert.True(UpdatePackageNameRecognizer.TryRecognize("MAAUnified-v0.9.0-linux-x64.zip", out var older));
        Assert.False(UpdatePackageNameRecognizer.Validate(older, "v1.0.0", "linux", "x64").Supported);

        // 同版本重装：默认拒绝，修复场景显式放行。
        Assert.True(UpdatePackageNameRecognizer.TryRecognize("MAAUnified-v1.0.0-linux-x64.zip", out var sameVersion));
        Assert.False(UpdatePackageNameRecognizer.Validate(sameVersion, "v1.0.0", "linux", "x64").Supported);
        Assert.True(UpdatePackageNameRecognizer.Validate(sameVersion, "v1.0.0", "linux", "x64", allowSameVersion: true).Supported);
    }

    [Fact]
    public void UpdatePackageNameRecognizer_Validate_OtaRequiresMatchingSourceVersion()
    {
        Assert.True(UpdatePackageNameRecognizer.TryRecognize(
            "MAAUnifiedComponent-OTA-v1.0.0_v1.1.0-windows-x64.zip",
            out var identity));

        Assert.True(UpdatePackageNameRecognizer.Validate(identity, "v1.0.0", "windows", "x64").Supported);
        Assert.False(UpdatePackageNameRecognizer.Validate(identity, "v0.9.0", "windows", "x64").Supported);
    }

    [Fact]
    public async Task TryRegisterPendingUpdatePackage_WhenSameVersionFullPackage_RegistersRepair()
    {
        using var root = TestRoot.Create();
        await SavePendingUpdateConfigAsync(root.Path);
        var packagePath = Path.Combine(root.Path, $"MAAUnified-v1.0.0-{CurrentPlatform}-{CurrentArchitecture}.zip");
        await File.WriteAllBytesAsync(packagePath, [1, 2, 3]);
        PendingUpdateFailureState.Write(root.Path, "previous attempt failed");

        var result = PendingAppUpdateService.TryRegisterPendingUpdatePackage(
            root.Path,
            packagePath,
            "v1.0.0",
            allowSameVersionReinstall: true);

        Assert.True(result.Registered, result.Message);
        Assert.Equal("v1.0.0", result.TargetVersion);
        Assert.True(result.SameVersionReinstall);
        // 注册即代表用户已着手修复：失败标记要清掉，否则下次启动会连刚注册的包一起清空。
        Assert.False(PendingUpdateFailureState.Exists(root.Path));
        var config = await new AvaloniaJsonConfigStore(root.Path).LoadAsync();
        Assert.NotNull(config);
        Assert.Equal(packagePath, config!.GlobalValues[ConfigurationKeys.VersionUpdatePackage]!.GetValue<string>());
        Assert.Equal("v1.0.0", config.GlobalValues[ConfigurationKeys.VersionName]!.GetValue<string>());
    }

    [Fact]
    public async Task TryConsumeDelegatedUpdateSuccess_ShouldClearPendingAndFailureStateOnce()
    {
        using var root = TestRoot.Create();
        await SavePendingUpdateConfigAsync(root.Path);
        PendingUpdateFailureState.Write(root.Path, "previous attempt failed");
        var successPath = Path.Combine(root.Path, ExternalUpdaterDelegation.SuccessStatusFileName);
        await File.WriteAllTextAsync(successPath, "ok");

        Assert.True(PendingAppUpdateService.TryConsumeDelegatedUpdateSuccess(root.Path));
        Assert.False(File.Exists(successPath));
        Assert.False(PendingUpdateFailureState.Exists(root.Path));
        Assert.False(UpdateRepairState.RequiresFullPackage(root.Path));

        var config = await new AvaloniaJsonConfigStore(root.Path).LoadAsync();
        Assert.NotNull(config);
        Assert.Equal(string.Empty, config!.GlobalValues[ConfigurationKeys.VersionUpdatePackage]!.GetValue<string>());

        // 成功标记只能消费一次，第二次启动不再重复清状态。
        Assert.False(PendingAppUpdateService.TryConsumeDelegatedUpdateSuccess(root.Path));
    }

    [Fact]
    public async Task TryRegisterPendingUpdatePackage_WhenOlderOrUnrecognizedPackage_IsRejected()
    {
        using var root = TestRoot.Create();
        await SavePendingUpdateConfigAsync(root.Path);
        var olderPackagePath = Path.Combine(root.Path, $"MAAUnified-v0.9.0-{CurrentPlatform}-{CurrentArchitecture}.zip");
        await File.WriteAllBytesAsync(olderPackagePath, [1, 2, 3]);
        var unknownPackagePath = Path.Combine(root.Path, "resource-pack.zip");
        await File.WriteAllBytesAsync(unknownPackagePath, [1, 2, 3]);

        var olderResult = PendingAppUpdateService.TryRegisterPendingUpdatePackage(
            root.Path,
            olderPackagePath,
            "v1.0.0",
            allowSameVersionReinstall: true);
        var unknownResult = PendingAppUpdateService.TryRegisterPendingUpdatePackage(
            root.Path,
            unknownPackagePath,
            "v1.0.0",
            allowSameVersionReinstall: true);

        Assert.False(olderResult.Registered);
        Assert.Contains("older", olderResult.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(unknownResult.Registered);
        Assert.Contains("does not match", unknownResult.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- P0-13: 占用重试 / 平台能力降级 ----------

    [Fact]
    public void FileOperationRetry_RetriesOnIoExceptionThenSucceeds()
    {
        var attempts = 0;

        var value = FileOperationRetry.Execute(
            () =>
            {
                attempts++;
                if (attempts < 3)
                {
                    throw new IOException("file is locked");
                }

                return attempts;
            },
            maxAttempts: 5,
            delay: TimeSpan.Zero);

        Assert.Equal(3, value);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public void FileOperationRetry_RethrowsAfterMaxAttempts()
    {
        var attempts = 0;

        var exception = Assert.Throws<IOException>(() => FileOperationRetry.Execute(
            () =>
            {
                attempts++;
                throw new IOException("still locked");
            },
            maxAttempts: 3,
            delay: TimeSpan.Zero));

        Assert.Equal("still locked", exception.Message);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public void UpdateApplyPlatform_OnCurrentPlatform_ProducesExplicitCapabilityProfile()
    {
        using var root = TestRoot.Create();

        var capabilities = UpdateApplyPlatform.Detect(root.Path);

        Assert.True(capabilities.OccupiedFileRetrySupported);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // 未随包发布 MAA.Updater.exe：必须显式降级为进程内应用并给出原因。
            Assert.False(capabilities.ExternalUpdaterSupported);
            Assert.False(capabilities.MutexCoordinationSupported);
            Assert.Contains("in-process", capabilities.DegradationReason, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.False(capabilities.ExternalUpdaterSupported);
            Assert.False(capabilities.MutexCoordinationSupported);
            Assert.Contains("Windows-only", capabilities.DegradationReason, StringComparison.Ordinal);
            Assert.Contains("in-process", capabilities.DegradationReason, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---------- P0-12: filelist 失败标记 + 用户忽略本次/重试更新 ----------

    [Fact]
    public void UpdateRepairState_PersistsAcrossStartupsUntilFullPackageRepairCompletes()
    {
        using var root = TestRoot.Create();

        Assert.Null(UpdateRepairState.TryRead(root.Path));
        Assert.False(UpdateRepairState.RequiresFullPackage(root.Path));

        UpdateRepairState.Write(root.Path, "Installation integrity check failed: 2 file(s) missing.");

        // 读取只读不删：第一次启动提示后标记仍在，第二次启动仍会提示并强制完整包。
        Assert.Equal("Installation integrity check failed: 2 file(s) missing.", UpdateRepairState.TryRead(root.Path));
        Assert.Equal("Installation integrity check failed: 2 file(s) missing.", UpdateRepairState.TryRead(root.Path));
        Assert.True(UpdateRepairState.Exists(root.Path));
        Assert.True(UpdateRepairState.RequiresFullPackage(root.Path));

        UpdateRepairState.Clear(root.Path);
        Assert.False(UpdateRepairState.Exists(root.Path));
        Assert.False(UpdateRepairState.RequiresFullPackage(root.Path));
    }

    [Fact]
    public void UpdateRepairState_RetryRequestIsConsumedOnce()
    {
        using var root = TestRoot.Create();

        Assert.False(UpdateRepairState.ConsumeRetryRequest(root.Path));

        UpdateRepairState.RequestRetry(root.Path);
        Assert.True(File.Exists(UpdateRepairState.ResolveRetryRequestPath(root.Path)));

        Assert.True(UpdateRepairState.ConsumeRetryRequest(root.Path));
        Assert.False(UpdateRepairState.ConsumeRetryRequest(root.Path));
        Assert.False(File.Exists(UpdateRepairState.ResolveRetryRequestPath(root.Path)));
    }

    [Fact]
    public void UpdateStartupHealthInspector_WhenIntegrityCheckFails_PersistsRepairMarkerAcrossStartups()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(
            Path.Combine(root.Path, InstallationIntegrityChecker.FileListName),
            "app.txt\nresource/missing.json\n");

        var firstStartup = UpdateStartupHealthInspector.Inspect(root.Path, persistInstallationRepairMarker: true);

        Assert.True(firstStartup.HasMissingInstallationFiles);
        Assert.True(firstStartup.HasPersistedRepairRequirement);
        Assert.True(UpdateRepairState.Exists(root.Path));

        // 用户忽略提示或缺失文件被手动恢复后，标记仍跨启动保留并继续要求完整包。
        File.Delete(Path.Combine(root.Path, InstallationIntegrityChecker.FileListName));
        var secondStartup = UpdateStartupHealthInspector.Inspect(root.Path);

        Assert.False(secondStartup.HasMissingInstallationFiles);
        Assert.True(secondStartup.HasPersistedRepairRequirement);
        Assert.True(secondStartup.RequiresRepair);
        Assert.True(UpdateRepairState.Exists(root.Path));

        UpdateRepairState.Clear(root.Path);
        Assert.False(UpdateStartupHealthInspector.Inspect(root.Path).RequiresRepair);
    }

    [Fact]
    public async Task TryRegisterPendingUpdatePackage_WhenRepairRequired_RejectsOtaAndAcceptsFullPackage()
    {
        using var root = TestRoot.Create();
        await SavePendingUpdateConfigAsync(root.Path);
        UpdateRepairState.Write(root.Path, "Installation integrity check failed: 1 file(s) missing.");

        var otaPath = Path.Combine(root.Path, $"MAAUnifiedComponent-OTA-v1.0.0_v1.1.0-{CurrentPlatform}-{CurrentArchitecture}.zip");
        await File.WriteAllBytesAsync(otaPath, [1, 2, 3]);

        var otaResult = PendingAppUpdateService.TryRegisterPendingUpdatePackage(
            root.Path,
            otaPath,
            "v1.0.0",
            allowSameVersionReinstall: false);

        Assert.False(otaResult.Registered);
        Assert.Contains("full package", otaResult.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(UpdateRepairState.Exists(root.Path));

        var fullPath = Path.Combine(root.Path, $"MAAUnified-v1.0.0-{CurrentPlatform}-{CurrentArchitecture}.zip");
        await File.WriteAllBytesAsync(fullPath, [1, 2, 3]);

        var fullResult = PendingAppUpdateService.TryRegisterPendingUpdatePackage(
            root.Path,
            fullPath,
            "v1.0.0",
            allowSameVersionReinstall: true);

        Assert.True(fullResult.Registered, fullResult.Message);
        Assert.True(fullResult.SameVersionReinstall);
        Assert.False(UpdateRepairState.Exists(root.Path));
    }

    // ---------- P0-13: 外部更新器委托与显式降级 ----------

    [Theory]
    [InlineData("windows", true, true, true, "")]
    [InlineData("windows", false, false, false, "not packaged")]
    [InlineData("macos", true, false, false, "Windows-only")]
    [InlineData("linux", true, false, false, "Windows-only")]
    public void UpdateApplyPlatform_DetectForPlatform_ProducesExplicitCapabilityProfile(
        string platform,
        bool hasUpdater,
        bool expectedExternalUpdater,
        bool expectedMutex,
        string expectedReasonFragment)
    {
        var capabilities = UpdateApplyPlatform.Detect(platform, hasUpdater);

        Assert.Equal(expectedExternalUpdater, capabilities.ExternalUpdaterSupported);
        Assert.Equal(expectedMutex, capabilities.MutexCoordinationSupported);
        Assert.True(capabilities.OccupiedFileRetrySupported);
        if (expectedReasonFragment.Length == 0)
        {
            Assert.Empty(capabilities.DegradationReason);
        }
        else
        {
            Assert.Contains(expectedReasonFragment, capabilities.DegradationReason, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void UpdateApplyPlatform_OnNonWindows_IgnoresUpdaterExecutableAndDegradesExplicitly()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, UpdateApplyPlatform.ExternalUpdaterExecutableName), "fake updater");

        var capabilities = UpdateApplyPlatform.Detect(root.Path);

        Assert.False(capabilities.ExternalUpdaterSupported);
        Assert.False(capabilities.MutexCoordinationSupported);
        Assert.Contains("Windows-only", capabilities.DegradationReason, StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalUpdaterDelegation_BuildArguments_UsesUpdaterContractOrderAndFlags()
    {
        var spec = new ExternalUpdaterLaunchSpec(
            ParentProcessId: 4321,
            ExecutablePath: "/tmp/MAA.Updater.exe",
            BaseDirectory: "/install",
            ExtractDirectory: "/install/NewVersionExtract",
            BackupDirectory: "/install/.old",
            PackagePath: "/install/update.zip",
            SuccessStatusFilePath: "/install/pending-update-success.txt",
            FailureStatusFilePath: "/install/pending-update-failure.txt",
            RelaunchExecutablePath: "/install/MAAUnified.exe",
            PlanPath: "/install/maa-pending-update-plan.json",
            MutexName: "MAAUnified.Package.Test",
            ShowConsole: true,
            ShowProgress: false);

        var arguments = ExternalUpdaterDelegation.BuildArguments(spec);

        Assert.Equal(
            [
                "4321",
                "/install",
                "/install/NewVersionExtract",
                "/install/.old",
                "/install/update.zip",
                "/install/pending-update-success.txt",
                "/install/pending-update-failure.txt",
                "/install/MAAUnified.exe",
                "/install/maa-pending-update-plan.json",
                "--mutex-name",
                "MAAUnified.Package.Test",
                "--show-console",
                "--no-progress-ui",
            ],
            arguments);
    }

    [Fact]
    public async Task TryApplyPendingUpdatePackage_WhenExternalUpdaterSupported_DelegatesWithoutApplyingInProcess()
    {
        using var root = TestRoot.Create();
        var packageDirectory = Path.Combine(root.Path, "update-packages");
        Directory.CreateDirectory(packageDirectory);
        File.WriteAllText(Path.Combine(root.Path, "app.txt"), "old");
        File.WriteAllText(Path.Combine(root.Path, "old.txt"), "remove me");
        var packagePath = Path.Combine(packageDirectory, "update.zip");
        await CreateZipAsync(
            packagePath,
            ("app.txt", "new"),
            ("removelist.txt", "old.txt"));
        await SavePendingUpdateAsync(root.Path, Path.Combine("update-packages", "update.zip"));
        File.WriteAllText(Path.Combine(root.Path, UpdateApplyPlatform.ExternalUpdaterExecutableName), "fake updater");
        var launcher = new RecordingExternalUpdaterLauncher(succeeds: true);

        var result = PendingAppUpdateService.TryApplyPendingUpdatePackage(
            root.Path,
            new AvaloniaJsonConfigStore(root.Path),
            new UpdateApplyCapabilities(
                ExternalUpdaterSupported: true,
                ExternalUpdaterExecutableName: UpdateApplyPlatform.ExternalUpdaterExecutableName,
                MutexCoordinationSupported: true,
                OccupiedFileRetrySupported: true,
                DegradationReason: string.Empty),
            launcher,
            mutexName: "MAAUnified.Package.Test",
            relaunchExecutablePath: "/install/MAAUnified.exe");

        Assert.Equal(PendingAppUpdateStatus.Delegated, result.Status);
        Assert.Equal(1, launcher.CallCount);
        var spec = Assert.Single(launcher.Specs);
        Assert.Equal("MAAUnified.Package.Test", spec.MutexName);
        Assert.Equal("/install/MAAUnified.exe", spec.RelaunchExecutablePath);
        Assert.True(File.Exists(spec.PlanPath));
        Assert.True(File.Exists(packagePath));
        // 未在进程内应用：安装内容保持原样，由外部更新器在退出后处理。
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(root.Path, "app.txt")));
        Assert.Equal("remove me", await File.ReadAllTextAsync(Path.Combine(root.Path, "old.txt")));

        var plan = JsonNode.Parse(await File.ReadAllTextAsync(spec.PlanPath))!.AsObject();
        Assert.Equal("ota", plan["packageType"]!.GetValue<string>());
        Assert.Contains("old.txt", plan["removeList"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.Contains("app.txt", plan["moveList"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.DoesNotContain("removelist.txt", plan["moveList"]!.AsArray().Select(node => node!.GetValue<string>()));
    }

    [Fact]
    public async Task TryApplyPendingUpdatePackage_WhenExternalUpdaterLaunchFails_FallsBackToInProcessApply()
    {
        using var root = TestRoot.Create();
        var packageDirectory = Path.Combine(root.Path, "update-packages");
        Directory.CreateDirectory(packageDirectory);
        File.WriteAllText(Path.Combine(root.Path, "app.txt"), "old");
        var packagePath = Path.Combine(packageDirectory, "update.zip");
        await CreateZipAsync(packagePath, ("app.txt", "new"));
        await SavePendingUpdateAsync(root.Path, Path.Combine("update-packages", "update.zip"));
        File.WriteAllText(Path.Combine(root.Path, UpdateApplyPlatform.ExternalUpdaterExecutableName), "fake updater");
        var launcher = new RecordingExternalUpdaterLauncher(succeeds: false);

        var result = PendingAppUpdateService.TryApplyPendingUpdatePackage(
            root.Path,
            new AvaloniaJsonConfigStore(root.Path),
            new UpdateApplyCapabilities(true, UpdateApplyPlatform.ExternalUpdaterExecutableName, true, true, string.Empty),
            launcher);

        Assert.Equal(PendingAppUpdateStatus.Applied, result.Status);
        Assert.Equal(1, launcher.CallCount);
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(root.Path, "app.txt")));
        Assert.False(File.Exists(packagePath));
    }

    [Fact]
    public async Task TryApplyPendingUpdatePackage_WhenExternalUpdaterUnsupported_AppliesInProcessWithoutLaunching()
    {
        using var root = TestRoot.Create();
        var packageDirectory = Path.Combine(root.Path, "update-packages");
        Directory.CreateDirectory(packageDirectory);
        File.WriteAllText(Path.Combine(root.Path, "app.txt"), "old");
        var packagePath = Path.Combine(packageDirectory, "update.zip");
        await CreateZipAsync(packagePath, ("app.txt", "new"));
        await SavePendingUpdateAsync(root.Path, Path.Combine("update-packages", "update.zip"));
        var launcher = new RecordingExternalUpdaterLauncher(succeeds: true);

        var result = PendingAppUpdateService.TryApplyPendingUpdatePackage(
            root.Path,
            new AvaloniaJsonConfigStore(root.Path),
            new UpdateApplyCapabilities(false, UpdateApplyPlatform.ExternalUpdaterExecutableName, false, true, "Windows-only; applying in-process."),
            launcher);

        Assert.Equal(PendingAppUpdateStatus.Applied, result.Status);
        Assert.Equal(0, launcher.CallCount);
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(root.Path, "app.txt")));
    }

    // ---------- P1-13: GitHub Token 只对 api.github.com 附带 ----------

    [Theory]
    [InlineData("https://api.github.com/repos/MaaAssistantArknights/MaaAssistantArknights/releases", true)]
    [InlineData("http://api.github.com/repos/x/y", false)]
    [InlineData("https://api.github.com:8443/repos/x/y", false)]
    [InlineData("https://user:pass@api.github.com/repos/x/y", false)]
    [InlineData("https://github.com/repos/x/y", false)]
    [InlineData("https://objects.githubusercontent.com/asset.zip", false)]
    [InlineData("https://mirrorchyan.com/api/resources/MAAUnified/latest", false)]
    public void GitHubApiRequestAuthorizer_OnlyAttachesForHttpsApiGithubDefaultPort(string url, bool expectAttached)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        var outcome = GitHubApiRequestAuthorizer.TryApplyAuthorization(request, "secret-token");

        if (expectAttached)
        {
            Assert.Equal(GitHubAuthorizationOutcome.Attached, outcome);
            Assert.Equal("Bearer secret-token", Assert.Single(request.Headers.GetValues("Authorization")));
        }
        else
        {
            Assert.Equal(GitHubAuthorizationOutcome.NotEligibleUri, outcome);
            Assert.False(request.Headers.Contains("Authorization"));
        }
    }

    [Fact]
    public void GitHubApiRequestAuthorizer_SkipsEmptyExpiredAndAlreadyAuthorizedRequests()
    {
        using var noTokenRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/x/y");
        Assert.Equal(
            GitHubAuthorizationOutcome.NoToken,
            GitHubApiRequestAuthorizer.TryApplyAuthorization(noTokenRequest, "   "));

        using var expiredRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/x/y");
        Assert.Equal(
            GitHubAuthorizationOutcome.TokenExpired,
            GitHubApiRequestAuthorizer.TryApplyAuthorization(
                expiredRequest,
                "secret-token",
                expiresAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                now: new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero)));
        Assert.False(expiredRequest.Headers.Contains("Authorization"));

        using var existingRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/x/y");
        existingRequest.Headers.TryAddWithoutValidation("Authorization", "Bearer caller-provided");
        Assert.Equal(
            GitHubAuthorizationOutcome.AlreadyAuthorized,
            GitHubApiRequestAuthorizer.TryApplyAuthorization(existingRequest, "secret-token"));
        Assert.Equal("Bearer caller-provided", Assert.Single(existingRequest.Headers.GetValues("Authorization")));
    }

    [Theory]
    [InlineData("2026-01-02 03:04:05 UTC", 2026, 1, 2, 3, 4, 5)]
    [InlineData("2026-01-02T03:04:05Z", 2026, 1, 2, 3, 4, 5)]
    public void GitHubApiRequestAuthorizer_ParsesTokenExpiration(
        string raw,
        int year,
        int month,
        int day,
        int hour,
        int minute,
        int second)
    {
        var parsed = GitHubApiRequestAuthorizer.TryParseExpiration(raw);

        Assert.Equal(new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero), parsed);
    }

    [Fact]
    public void GitHubApiRequestAuthorizer_ReturnsNullForUnparsableOrNeverExpiringValues()
    {
        Assert.Null(GitHubApiRequestAuthorizer.TryParseExpiration(null));
        Assert.Null(GitHubApiRequestAuthorizer.TryParseExpiration("none"));
        Assert.Null(GitHubApiRequestAuthorizer.TryParseExpiration("not-a-date"));
    }

    [Fact]
    public async Task GitHubApiAuthorizationHandler_AttachesTokenOnlyForApiGithubHost()
    {
        var capturingHandler = new CapturingHttpMessageHandler();
        var handler = GitHubApiAuthorizationHandler.Wrap(
            capturingHandler,
            VersionUpdatePolicy.Default with { GitHubToken = "secret-token" });
        using var client = new HttpClient(handler, disposeHandler: true);

        using var eligible = await client.GetAsync("https://api.github.com/repos/x/y");
        using var ineligible = await client.GetAsync("https://mirrorchyan.com/api/resources/MAAUnified/latest");

        Assert.Equal("Bearer secret-token", capturingHandler.AuthorizationFor(eligible.RequestMessage!));
        Assert.Null(capturingHandler.AuthorizationFor(ineligible.RequestMessage!));
    }

    [Fact]
    public void SensitiveSettingRegistry_RegistersGitHubTokenAsGlobalCredential()
    {
        Assert.True(SensitiveSettingKeyRegistry.TryGet(ConfigurationKeys.GitHubToken, out var definition));
        Assert.Equal(SensitiveSettingScope.Global, definition.Scope);
        Assert.Equal(ConfigurationKeys.GitHubToken, definition.Key);
    }

    // ---------- P1-14: 更新后启动行为 ----------

    [Fact]
    public void VersionUpdatePolicy_ExposesUpdaterOutputSwitchesFromConfig()
    {
        Assert.True(VersionUpdatePolicy.Default.ShowUpdaterConsole);
        Assert.True(VersionUpdatePolicy.Default.ShowUpdaterProgress);

        var policy = VersionUpdatePolicy.Default with { ShowUpdaterConsole = false, ShowUpdaterProgress = false };
        var global = policy.ToGlobalSettingUpdates();

        Assert.Equal(bool.FalseString, global[ConfigurationKeys.ShowUpdaterConsole]);
        Assert.Equal(bool.FalseString, global[ConfigurationKeys.ShowUpdaterProgress]);
        // 凭据不进明文全局表，只能经 SensitiveStorage 保存。
        Assert.False(global.ContainsKey(ConfigurationKeys.GitHubToken));
    }

    // ---------- helpers ----------

    private static string CurrentPlatform
        => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

    private static string CurrentArchitecture
        => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";

    private static async Task CreateZipAsync(string packagePath, params (string Name, string Content)[] entries)
    {
        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name);
            await using var stream = entry.Open();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(content);
        }
    }

    private static async Task SavePendingUpdateAsync(string baseDirectory, string relativePackagePath)
    {
        var config = new UnifiedConfig();
        config.GlobalValues[ConfigurationKeys.VersionName] = JsonValue.Create("v2.0.0");
        config.GlobalValues[ConfigurationKeys.VersionUpdatePackage] = JsonValue.Create(relativePackagePath);
        await new AvaloniaJsonConfigStore(baseDirectory).SaveAsync(config);
    }

    private static async Task SavePendingUpdateConfigAsync(string baseDirectory)
    {
        var config = new UnifiedConfig();
        config.GlobalValues[ConfigurationKeys.VersionName] = JsonValue.Create("v1.0.0");
        await new AvaloniaJsonConfigStore(baseDirectory).SaveAsync(config);
    }

    private sealed class RecordingExternalUpdaterLauncher(bool succeeds) : IExternalUpdaterProcessLauncher
    {
        public int CallCount { get; private set; }

        public List<ExternalUpdaterLaunchSpec> Specs { get; } = [];

        public bool TryStart(ExternalUpdaterLaunchSpec spec, out string message)
        {
            CallCount++;
            Specs.Add(spec);
            message = succeeds ? "fake launcher" : "fake launcher failure";
            return succeeds;
        }
    }

    private sealed class TestRoot : IDisposable
    {
        private TestRoot(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TestRoot Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"maa-unified-version-update-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TestRoot(path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    private sealed class CapturingHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                RequestMessage = request,
            });
        }

        public string? AuthorizationFor(HttpRequestMessage request)
            => request.Headers.TryGetValues("Authorization", out var values)
                ? values.SingleOrDefault()
                : null;
    }
}
