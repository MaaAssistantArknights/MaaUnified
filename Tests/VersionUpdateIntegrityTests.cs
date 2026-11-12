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
