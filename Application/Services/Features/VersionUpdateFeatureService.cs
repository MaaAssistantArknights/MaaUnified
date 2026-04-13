using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.VersionUpdate;
using MAAUnified.Application.Services.Localization;
using MAAUnified.Compat.Constants;
using MAAUnified.CoreBridge;
using MAAUnified.Platform;

namespace MAAUnified.Application.Services.Features;

public sealed class VersionUpdateFeatureService : IVersionUpdateFeatureService
{
    private const string WindowsManualUpdateMessageKey = "Settings.VersionUpdate.Status.WindowsManualUpdateRequired";
    private const string PackageUnavailableMessageKey = "Settings.VersionUpdate.Status.PackageUnavailable";
    private const string PackageDownloadFailedMessageKey = "Settings.VersionUpdate.Status.PackageDownloadFailed";
    private const string GithubResourceArchiveUrl = "https://github.com/MaaAssistantArknights/MaaResource/archive/refs/heads/main.zip";
    private const string MirrorChyanResourceApiUrl = "https://mirrorchyan.com/api/resources/MaaResource/latest";
    private static readonly HashSet<string> AllowedVersionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Stable",
        "Beta",
        "Nightly",
    };
    private static readonly HashSet<string> AllowedProxyTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http",
        "https",
        "socks5",
        "system",
    };
    private static readonly HashSet<string> DefaultClientTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        string.Empty,
        "Official",
        "Bilibili",
    };
    private static readonly HttpClient ResourceHttpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(15),
    };

    private readonly UnifiedConfigurationService? _configService;
    private readonly UiDiagnosticsService? _diagnosticsService;
    private readonly IAchievementTrackerService? _achievementTrackerService;
    private readonly UiLogService? _uiLogService;
    private readonly AppUpdateWorkflowService _appUpdateWorkflowService;
    private readonly string? _runtimeBaseDirectory;

    public VersionUpdateFeatureService()
    {
        _appUpdateWorkflowService = new AppUpdateWorkflowService(new NoOpAppLifecycleService());
    }

    public VersionUpdateFeatureService(
        UnifiedConfigurationService configService,
        UiDiagnosticsService? diagnosticsService = null,
        IAchievementTrackerService? achievementTrackerService = null,
        UiLogService? uiLogService = null,
        AppUpdateWorkflowService? appUpdateWorkflowService = null,
        string? runtimeBaseDirectory = null)
    {
        _configService = configService;
        _diagnosticsService = diagnosticsService;
        _achievementTrackerService = achievementTrackerService;
        _uiLogService = uiLogService;
        _appUpdateWorkflowService = appUpdateWorkflowService ?? new AppUpdateWorkflowService(new NoOpAppLifecycleService());
        _runtimeBaseDirectory = NormalizeRuntimeBaseDirectory(runtimeBaseDirectory);
    }

    public Task<UiOperationResult<VersionUpdatePolicy>> LoadPolicyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetConfig(out var config, out var failure))
        {
            return Task.FromResult(failure);
        }

        var policy = NormalizePolicy(new VersionUpdatePolicy(
            Proxy: ReadString(config, ConfigurationKeys.UpdateProxy, string.Empty),
            ProxyType: ReadString(config, ConfigurationKeys.ProxyType, "http"),
            VersionType: ReadString(config, ConfigurationKeys.VersionType, "Stable"),
            ResourceUpdateSource: ReadString(config, ConfigurationKeys.UpdateSource, "Github"),
            ForceGithubGlobalSource: ReadBool(config, ConfigurationKeys.ForceGithubGlobalSource, false),
            MirrorChyanCdk: DecryptMirrorChyanCdk(ReadString(config, ConfigurationKeys.MirrorChyanCdk, string.Empty)),
            MirrorChyanCdkExpired: ReadString(config, ConfigurationKeys.MirrorChyanCdkExpiredTime, string.Empty),
            StartupUpdateCheck: ReadBool(config, ConfigurationKeys.StartupUpdateCheck, true),
            ScheduledUpdateCheck: ReadBool(config, ConfigurationKeys.UpdateAutoCheck, false),
            ResourceApi: ReadString(config, ConfigurationKeys.ResourceApi, string.Empty),
            AllowNightlyUpdates: ReadBool(config, ConfigurationKeys.AllowNightlyUpdates, false),
            HasAcknowledgedNightlyWarning: ReadBool(config, ConfigurationKeys.HasAcknowledgedNightlyWarning, false),
            UseAria2: ReadBool(config, ConfigurationKeys.UseAria2, false),
            AutoDownloadUpdatePackage: ReadBool(config, ConfigurationKeys.AutoDownloadUpdatePackage, true),
            AutoInstallUpdatePackage: ReadBool(config, ConfigurationKeys.AutoInstallUpdatePackage, false),
            VersionName: ReadString(config, ConfigurationKeys.VersionName, string.Empty),
            VersionBody: ReadString(config, ConfigurationKeys.VersionUpdateBody, string.Empty),
            IsFirstBoot: ReadBool(config, ConfigurationKeys.VersionUpdateIsFirstBoot, false),
            VersionPackage: ReadString(config, ConfigurationKeys.VersionUpdatePackage, string.Empty),
            DoNotShowUpdate: ReadBool(config, ConfigurationKeys.VersionUpdateDoNotShowUpdate, false)));
        return Task.FromResult(UiOperationResult<VersionUpdatePolicy>.Ok(policy, "Loaded version update policy."));
    }

    public Task<UiOperationResult<ResourceVersionInfo>> LoadResourceVersionInfoAsync(
        string? clientType,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var resourceInfo = LoadResourceVersionInfo(ResolveRuntimeBaseDirectory(), clientType);
            return Task.FromResult(UiOperationResult<ResourceVersionInfo>.Ok(resourceInfo, "Loaded resource version info."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(UiOperationResult<ResourceVersionInfo>.Fail(
                UiErrorCode.UiOperationFailed,
                $"Failed to load resource version info: {ex.Message}",
                ex.Message));
        }
    }

    public async Task<UiOperationResult> SaveChannelAsync(VersionUpdatePolicy policy, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedPolicy = NormalizePolicy(policy);
        var validation = ValidateChannelPolicy(normalizedPolicy);
        if (!validation.Success)
        {
            return validation;
        }

        return await PersistGlobalSettingsWithRollbackAsync(
            normalizedPolicy.ToChannelSettingUpdates(),
            "Version update channel settings saved.",
            cancellationToken);
    }

    public async Task<UiOperationResult> SaveProxyAsync(VersionUpdatePolicy policy, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedPolicy = NormalizePolicy(policy);
        var validation = ValidateProxyPolicy(normalizedPolicy);
        if (!validation.Success)
        {
            return validation;
        }

        return await PersistGlobalSettingsWithRollbackAsync(
            normalizedPolicy.ToProxySettingUpdates(),
            "Version update proxy settings saved.",
            cancellationToken);
    }

    public async Task<UiOperationResult> SavePolicyAsync(VersionUpdatePolicy policy, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedPolicy = NormalizePolicy(policy);
        var validation = ValidatePolicy(normalizedPolicy);
        if (!validation.Success)
        {
            return validation;
        }

        return await PersistGlobalSettingsWithRollbackAsync(
            normalizedPolicy.ToGlobalSettingUpdates(),
            "Version update policy saved.",
            cancellationToken);
    }

    public async Task<UiOperationResult<string>> UpdateResourceAsync(
        VersionUpdatePolicy policy,
        string? clientType,
        IProgress<VersionUpdateProgressInfo>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedPolicy = NormalizePolicy(policy);
        var validation = ValidatePolicy(normalizedPolicy);
        if (!validation.Success)
        {
            return UiOperationResult<string>.Fail(
                validation.Error?.Code ?? UiErrorCode.VersionUpdateInvalidParameters,
                validation.Message,
                validation.Error?.Details);
        }

        var source = normalizedPolicy.ResourceUpdateSource;
        if (string.Equals(source, "MirrorChyan", StringComparison.OrdinalIgnoreCase))
        {
            return await UpdateResourceFromMirrorChyanAsync(normalizedPolicy, clientType, progress, cancellationToken);
        }

        return await UpdateResourceFromGithubAsync(progress, cancellationToken);
    }

    public async Task<UiOperationResult<ResourceUpdateCheckResult>> CheckResourceUpdateAsync(
        VersionUpdatePolicy policy,
        string? clientType,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedPolicy = NormalizePolicy(policy);
        var validation = ValidatePolicy(normalizedPolicy);
        if (!validation.Success)
        {
            return UiOperationResult<ResourceUpdateCheckResult>.Fail(
                validation.Error?.Code ?? UiErrorCode.VersionUpdateInvalidParameters,
                validation.Message,
                validation.Error?.Details);
        }

        var scope = "VersionUpdate.Resource.Check";
        var cdk = normalizedPolicy.MirrorChyanCdk.Trim();
        var runtimeBaseDirectory = ResolveRuntimeBaseDirectory();
        var localVersion = LoadResourceVersionInfo(runtimeBaseDirectory, clientType);
        var requestUrl =
            $"{MirrorChyanResourceApiUrl}?current_version={Uri.EscapeDataString(BuildCurrentVersionQueryToken(localVersion))}&cdk={Uri.EscapeDataString(cdk)}&user_agent=MAAUnified&sp_id={Uri.EscapeDataString(BuildMirrorChyanSpId())}";

        try
        {
            PublishUpdateLog($"Checking resource updates. source={normalizedPolicy.ResourceUpdateSource}, client={clientType ?? "<default>"}");
            await TraceVersionUpdateAsync(scope, $"Query begin url={requestUrl}", cancellationToken).ConfigureAwait(false);
            using var response = await ResourceHttpClient.GetAsync(requestUrl, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            await TraceVersionUpdateAsync(
                scope,
                $"Query end status={(int)response.StatusCode}; bodyLength={body.Length}",
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                PublishUpdateLog($"Resource update check failed with HTTP {(int)response.StatusCode}.", "WARN");
                return UiOperationResult<ResourceUpdateCheckResult>.Fail(
                    UiErrorCode.UiOperationFailed,
                    $"MirrorChyan request failed with status {(int)response.StatusCode}.",
                    body);
            }

            var payload = ParseMirrorChyanPayload(body);
            await TraceVersionUpdateAsync(
                scope,
                $"Payload parsed code={payload.Code}; hasUrl={!string.IsNullOrWhiteSpace(payload.DownloadUrl)}; versionTimestamp={payload.VersionTimestamp:O}",
                cancellationToken).ConfigureAwait(false);

            if (payload.CdkExpiredEpoch.HasValue)
            {
                await PersistMirrorChyanExpiryAsync(payload.CdkExpiredEpoch.Value, cancellationToken).ConfigureAwait(false);
            }

            if (payload.Code != 0)
            {
                PublishUpdateLog(
                    string.IsNullOrWhiteSpace(payload.Message)
                        ? "Resource update check failed."
                        : $"Resource update check failed: {payload.Message}",
                    "WARN");
                return UiOperationResult<ResourceUpdateCheckResult>.Fail(
                    UiErrorCode.VersionUpdateInvalidParameters,
                    string.IsNullOrWhiteSpace(payload.Message)
                        ? "MirrorChyan request failed."
                        : payload.Message);
            }

            if (payload.VersionTimestamp.HasValue
                && localVersion.LastUpdatedAt != DateTime.MinValue
                && payload.VersionTimestamp.Value <= localVersion.LastUpdatedAt)
            {
                PublishUpdateLog("Resources are already up to date.");
                return UiOperationResult<ResourceUpdateCheckResult>.Ok(
                    new ResourceUpdateCheckResult(
                        IsUpdateAvailable: false,
                        DisplayVersion: string.Empty,
                        ReleaseNote: string.Empty,
                        RequiresMirrorChyanCdk: false,
                        DownloadUrl: payload.DownloadUrl),
                    "资源已是最新版本。");
            }

            var displayVersion = payload.VersionTimestamp?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                ?? string.Empty;
            var requiresMirrorChyanCdk = string.IsNullOrWhiteSpace(payload.DownloadUrl);
            PublishUpdateLog(
                requiresMirrorChyanCdk
                    ? $"Resource update available: {displayVersion}. MirrorChyan CDK required for direct download."
                    : $"Resource update available: {displayVersion}.");
            return UiOperationResult<ResourceUpdateCheckResult>.Ok(
                new ResourceUpdateCheckResult(
                    IsUpdateAvailable: true,
                    DisplayVersion: displayVersion,
                    ReleaseNote: payload.ReleaseNote,
                    RequiresMirrorChyanCdk: requiresMirrorChyanCdk,
                    DownloadUrl: payload.DownloadUrl),
                requiresMirrorChyanCdk
                    ? "检测到资源更新，可手动更新。"
                    : "检测到资源更新。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            PublishUpdateLog($"Resource update check failed: {ex.Message}", "ERROR");
            await TraceVersionUpdateErrorAsync(scope, "Resource update check failed.", ex, cancellationToken).ConfigureAwait(false);
            return UiOperationResult<ResourceUpdateCheckResult>.Fail(
                UiErrorCode.UiOperationFailed,
                $"Failed to check resource updates: {ex.Message}",
                ex.Message);
        }
    }

    public async Task<UiOperationResult<VersionUpdateCheckResult>> CheckForUpdatesAsync(
        VersionUpdatePolicy policy,
        string currentVersion,
        IProgress<VersionUpdateProgressInfo>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedPolicy = NormalizePolicy(policy);
        var validation = ValidatePolicy(normalizedPolicy);
        if (!validation.Success)
        {
            return UiOperationResult<VersionUpdateCheckResult>.Fail(
                validation.Error?.Code ?? UiErrorCode.VersionUpdateInvalidParameters,
                validation.Message,
                validation.Error?.Details);
        }

        try
        {
            PublishUpdateLog($"Checking software updates. channel={normalizedPolicy.VersionType}, current={currentVersion}");
            var workflowResult = await _appUpdateWorkflowService.CheckForUpdatesAsync(
                normalizedPolicy,
                string.IsNullOrWhiteSpace(currentVersion) ? "unknown" : currentVersion.Trim(),
                cancellationToken).ConfigureAwait(false);
            var effectiveResult = workflowResult;

            if (workflowResult.IsNewVersion
                && workflowResult.HasPackage
                && normalizedPolicy.AutoDownloadUpdatePackage)
            {
                PublishUpdateLog(
                    workflowResult.PackageName is null
                        ? "Preparing software update package download."
                        : $"Preparing software update package `{workflowResult.PackageName}`.");
                progress?.Report(new VersionUpdateProgressInfo(
                    VersionUpdateProgressOperation.SoftwarePackage,
                    VersionUpdateProgressStage.Started));
                var downloadResult = await _appUpdateWorkflowService.DownloadPackageAsync(
                    workflowResult,
                    ResolveRuntimeBaseDirectory(),
                    forceDownload: false,
                    normalizedPolicy,
                    progress,
                    cancellationToken).ConfigureAwait(false);
                if (downloadResult.Success && !string.IsNullOrWhiteSpace(downloadResult.Value))
                {
                    effectiveResult = workflowResult with
                    {
                        PreparedPackagePath = downloadResult.Value,
                    };
                    progress?.Report(new VersionUpdateProgressInfo(
                        VersionUpdateProgressOperation.SoftwarePackage,
                        VersionUpdateProgressStage.Completed));
                    PublishUpdateLog(
                        workflowResult.PackageName is null
                            ? "Software update package is ready to apply after restart."
                            : $"Software update package `{workflowResult.PackageName}` is ready to apply after restart.");
                }
                else
                {
                    PublishUpdateLog(
                        downloadResult.Message.Length == 0
                            ? "Software update package preparation failed."
                            : $"Software update package preparation failed: {downloadResult.Message}",
                        "ERROR");
                    effectiveResult = ApplyPackageDownloadFailure(workflowResult);
                }
            }

            var message = BuildVersionUpdateMessage(effectiveResult);
            PublishUpdateLog(
                effectiveResult.IsNewVersion
                    ? $"Version update available: {effectiveResult.TargetVersion}"
                    : $"Software is already up to date on `{effectiveResult.Channel}`.");
            return UiOperationResult<VersionUpdateCheckResult>.Ok(effectiveResult, message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            PublishUpdateLog($"Software update check failed: {ex.Message}", "ERROR");
            return UiOperationResult<VersionUpdateCheckResult>.Fail(
                UiErrorCode.UiOperationFailed,
                $"Failed to check for updates: {ex.Message}",
                ex.Message);
        }
    }

    private static VersionUpdateCheckResult ApplyPackageDownloadFailure(VersionUpdateCheckResult workflowResult)
    {
        if (IsWindowsPackageResolution(workflowResult))
        {
            return workflowResult with
            {
                PreparedPackagePath = null,
                PackageResolutionStatus = PackageResolutionStatus.WindowsManualUpdateRequired,
                PackageFailureMessageKey = WindowsManualUpdateMessageKey,
            };
        }

        return workflowResult with
        {
            PreparedPackagePath = null,
            PackageResolutionStatus = PackageResolutionStatus.DownloadFailed,
            PackageFailureMessageKey = PackageDownloadFailedMessageKey,
        };
    }

    private static bool IsWindowsPackageResolution(VersionUpdateCheckResult workflowResult)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return true;
        }

        if (workflowResult.PackageSourceKind == PackageSourceKind.WindowsRelayManifest)
        {
            return true;
        }

        var packageName = workflowResult.PackageName ?? string.Empty;
        if (packageName.Contains("windows", StringComparison.OrdinalIgnoreCase)
            || packageName.Contains("win-", StringComparison.OrdinalIgnoreCase)
            || packageName.Contains("-win", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return packageName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildVersionUpdateMessage(VersionUpdateCheckResult result)
    {
        if (!result.IsNewVersion)
        {
            return $"已检查 `{result.Channel}` 通道，当前已是最新。";
        }

        if (!string.IsNullOrWhiteSpace(result.PreparedPackagePath))
        {
            return $"发现新版本：{result.TargetVersion}。已准备更新包，重启后即可应用。";
        }

        return result.PackageResolutionStatus switch
        {
            PackageResolutionStatus.WindowsManualUpdateRequired
                => $"发现新版本：{result.TargetVersion}。Windows 版目前暂未在 release 发布，请手动更新。",
            PackageResolutionStatus.Unavailable
                => $"发现新版本：{result.TargetVersion}。更新失败。",
            PackageResolutionStatus.DownloadFailed
                => $"发现新版本：{result.TargetVersion}。更新失败。",
            _ => $"发现新版本：{result.TargetVersion}",
        };
    }

    private UiOperationResult ValidatePolicy(VersionUpdatePolicy policy)
    {
        var channelValidation = ValidateChannelPolicy(policy);
        if (!channelValidation.Success)
        {
            return channelValidation;
        }

        return ValidateProxyPolicy(policy);
    }

    private UiOperationResult ValidateChannelPolicy(VersionUpdatePolicy policy)
    {
        if (!AllowedVersionTypes.Contains(policy.VersionType))
        {
            return UiOperationResult.Fail(
                UiErrorCode.VersionUpdateInvalidParameters,
                $"Version type `{policy.VersionType}` is unsupported.");
        }

        if (!string.Equals(policy.ResourceUpdateSource, "Github", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(policy.ResourceUpdateSource, "MirrorChyan", StringComparison.OrdinalIgnoreCase))
        {
            return UiOperationResult.Fail(
                UiErrorCode.VersionUpdateInvalidParameters,
                $"Resource update source `{policy.ResourceUpdateSource}` is unsupported.");
        }

        return UiOperationResult.Ok("Version update channel validation passed.");
    }

    private static UiOperationResult ValidateProxyPolicy(VersionUpdatePolicy policy)
    {
        if (!AllowedProxyTypes.Contains(policy.ProxyType))
        {
            return UiOperationResult.Fail(
                UiErrorCode.VersionUpdateInvalidParameters,
                $"Proxy type `{policy.ProxyType}` is unsupported.");
        }

        var proxy = policy.Proxy.Trim();
        if (proxy.Length > 0)
        {
            if (Uri.TryCreate(proxy, UriKind.Absolute, out _))
            {
                return UiOperationResult.Ok("Version update proxy validation passed.");
            }

            if (TryParseHostPortProxy(proxy))
            {
                return UiOperationResult.Ok("Version update proxy validation passed.");
            }

            return UiOperationResult.Fail(
                UiErrorCode.VersionUpdateInvalidParameters,
                $"Proxy `{policy.Proxy}` must be in `<host>:<port>` or absolute URI format.");
        }

        return UiOperationResult.Ok("Version update proxy validation passed.");
    }

    private async Task<UiOperationResult<string>> UpdateResourceFromGithubAsync(
        IProgress<VersionUpdateProgressInfo>? progress,
        CancellationToken cancellationToken)
    {
        const string scope = "VersionUpdate.Resource.Github";
        var runtimeBaseDirectory = ResolveRuntimeBaseDirectory();
        var resourceDirectory = Path.Combine(runtimeBaseDirectory, "resource");
        PublishUpdateLog("Starting resource update from Github.");
        progress?.Report(new VersionUpdateProgressInfo(
            VersionUpdateProgressOperation.ResourcePackage,
            VersionUpdateProgressStage.Started,
            VersionUpdateProgressSource.GlobalSource));
        await TraceVersionUpdateAsync(
            scope,
            $"Begin runtimeBaseDirectory={runtimeBaseDirectory}; resourceDirectory={resourceDirectory}",
            cancellationToken).ConfigureAwait(false);
        if (!Directory.Exists(resourceDirectory))
        {
            return UiOperationResult<string>.Fail(
                UiErrorCode.UiOperationFailed,
                $"Resource directory was not found: {resourceDirectory}");
        }

        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "maa-unified-resource-update",
            Guid.NewGuid().ToString("N"));
        var zipPath = Path.Combine(tempRoot, "MaaResourceGithub.zip");
        var extractDirectory = Path.Combine(tempRoot, "extract");
        Directory.CreateDirectory(tempRoot);
        await TraceVersionUpdateAsync(
            scope,
            $"Prepared tempRoot={tempRoot}; zipPath={zipPath}; extractDirectory={extractDirectory}",
            cancellationToken).ConfigureAwait(false);

        try
        {
            await TraceVersionUpdateAsync(
                scope,
                $"Download begin url={GithubResourceArchiveUrl}",
                cancellationToken).ConfigureAwait(false);
            await DownloadToFileAsync(
                GithubResourceArchiveUrl,
                zipPath,
                VersionUpdateProgressOperation.ResourcePackage,
                VersionUpdateProgressSource.GlobalSource,
                progress,
                cancellationToken).ConfigureAwait(false);
            await TraceVersionUpdateAsync(
                scope,
                $"Download end zipSize={TryGetFileLength(zipPath)}",
                cancellationToken).ConfigureAwait(false);

            progress?.Report(new VersionUpdateProgressInfo(
                VersionUpdateProgressOperation.ResourcePackage,
                VersionUpdateProgressStage.Preparing,
                VersionUpdateProgressSource.GlobalSource));
            await TraceVersionUpdateAsync(scope, "Extract begin", cancellationToken).ConfigureAwait(false);
            await Task.Run(
                () => ZipFile.ExtractToDirectory(zipPath, extractDirectory, overwriteFiles: true),
                cancellationToken).ConfigureAwait(false);
            await TraceVersionUpdateAsync(scope, "Extract end", cancellationToken).ConfigureAwait(false);

            await TraceVersionUpdateAsync(scope, "Resolve extracted resource directory begin", cancellationToken).ConfigureAwait(false);
            var extractedResourceDirectory = ResolveExtractedResourceDirectory(extractDirectory);
            await TraceVersionUpdateAsync(
                scope,
                $"Resolve extracted resource directory end path={extractedResourceDirectory}",
                cancellationToken).ConfigureAwait(false);
            if (!Directory.Exists(extractedResourceDirectory))
            {
                return UiOperationResult<string>.Fail(
                    UiErrorCode.UiOperationFailed,
                    "Downloaded package does not contain `resource` directory.");
            }

            await TraceVersionUpdateAsync(
                scope,
                $"Merge begin source={extractedResourceDirectory}; destination={resourceDirectory}",
                cancellationToken).ConfigureAwait(false);
            await Task.Run(
                () => MergeDirectory(extractedResourceDirectory, resourceDirectory),
                cancellationToken).ConfigureAwait(false);
            await TraceVersionUpdateAsync(scope, "Merge end", cancellationToken).ConfigureAwait(false);
            progress?.Report(new VersionUpdateProgressInfo(
                VersionUpdateProgressOperation.ResourcePackage,
                VersionUpdateProgressStage.Completed,
                VersionUpdateProgressSource.GlobalSource));
            PublishUpdateLog("Resource update completed from Github.");
            return UiOperationResult<string>.Ok(
                "资源更新完成（Github）。",
                "Resource update completed.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            PublishUpdateLog($"Resource update from Github failed: {ex.Message}", "ERROR");
            await TraceVersionUpdateErrorAsync(scope, "Github resource update failed.", ex, cancellationToken).ConfigureAwait(false);
            return UiOperationResult<string>.Fail(
                UiErrorCode.UiOperationFailed,
                $"Failed to update resources from Github: {ex.Message}",
                ex.Message);
        }
        finally
        {
            await TraceVersionUpdateAsync(scope, $"Cleanup begin tempRoot={tempRoot}", cancellationToken).ConfigureAwait(false);
            TryDeleteDirectory(tempRoot);
            await TraceVersionUpdateAsync(
                scope,
                $"Cleanup end tempRootExists={Directory.Exists(tempRoot)}",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<UiOperationResult<string>> UpdateResourceFromMirrorChyanAsync(
        VersionUpdatePolicy policy,
        string? clientType,
        IProgress<VersionUpdateProgressInfo>? progress,
        CancellationToken cancellationToken)
    {
        const string scope = "VersionUpdate.Resource.MirrorChyan";
        var cdk = policy.MirrorChyanCdk.Trim();
        PublishUpdateLog("Starting resource update from MirrorChyan.");
        await TraceVersionUpdateAsync(
            scope,
            $"Begin clientType={clientType ?? "<null>"}; cdkLength={cdk.Length}",
            cancellationToken).ConfigureAwait(false);
        if (cdk.Length == 0)
        {
            return UiOperationResult<string>.Fail(
                UiErrorCode.VersionUpdateInvalidParameters,
                "MirrorChyan source requires a CDK.");
        }

        var localVersion = LoadResourceVersionInfo(ResolveRuntimeBaseDirectory(), clientType);
        var requestUrl =
            $"{MirrorChyanResourceApiUrl}?current_version={Uri.EscapeDataString(BuildCurrentVersionQueryToken(localVersion))}&cdk={Uri.EscapeDataString(cdk)}&user_agent=MAAUnified&sp_id={Uri.EscapeDataString(BuildMirrorChyanSpId())}";

        MirrorChyanUpdateResponse payload;
        try
        {
            await TraceVersionUpdateAsync(scope, $"Query begin url={requestUrl}", cancellationToken).ConfigureAwait(false);
            using var response = await ResourceHttpClient.GetAsync(requestUrl, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            await TraceVersionUpdateAsync(
                scope,
                $"Query end status={(int)response.StatusCode}; bodyLength={body.Length}",
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return UiOperationResult<string>.Fail(
                    UiErrorCode.UiOperationFailed,
                    $"MirrorChyan request failed with status {(int)response.StatusCode}.",
                    body);
            }

            payload = ParseMirrorChyanPayload(body);
            await TraceVersionUpdateAsync(
                scope,
                $"Payload parsed code={payload.Code}; hasUrl={!string.IsNullOrWhiteSpace(payload.DownloadUrl)}; versionTimestamp={payload.VersionTimestamp:O}",
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await TraceVersionUpdateErrorAsync(scope, "MirrorChyan resource query failed.", ex, cancellationToken).ConfigureAwait(false);
            return UiOperationResult<string>.Fail(
                UiErrorCode.UiOperationFailed,
                $"Failed to query MirrorChyan update endpoint: {ex.Message}",
                ex.Message);
        }

        if (payload.CdkExpiredEpoch.HasValue)
        {
            await PersistMirrorChyanExpiryAsync(payload.CdkExpiredEpoch.Value, cancellationToken).ConfigureAwait(false);
        }

        if (payload.Code != 0)
        {
            return UiOperationResult<string>.Fail(
                UiErrorCode.VersionUpdateInvalidParameters,
                string.IsNullOrWhiteSpace(payload.Message)
                    ? "MirrorChyan request failed."
                    : payload.Message);
        }

        if (payload.VersionTimestamp.HasValue
            && localVersion.LastUpdatedAt != DateTime.MinValue
            && payload.VersionTimestamp.Value <= localVersion.LastUpdatedAt)
        {
            return UiOperationResult<string>.Ok("资源已是最新版本。", "Resource is already up to date.");
        }

        if (string.IsNullOrWhiteSpace(payload.DownloadUrl))
        {
            return UiOperationResult<string>.Fail(
                UiErrorCode.UiOperationFailed,
                "MirrorChyan response does not contain a downloadable package URL.");
        }

        progress?.Report(new VersionUpdateProgressInfo(
            VersionUpdateProgressOperation.ResourcePackage,
            VersionUpdateProgressStage.Started,
            VersionUpdateProgressSource.MirrorChyan,
            payload.ReleaseNote));

        var runtimeBaseDirectory = ResolveRuntimeBaseDirectory();
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "maa-unified-resource-update",
            Guid.NewGuid().ToString("N"));
        var zipPath = Path.Combine(tempRoot, "MaaResourceMirrorChyan.zip");
        var extractDirectory = Path.Combine(tempRoot, "extract");
        Directory.CreateDirectory(tempRoot);
        await TraceVersionUpdateAsync(
            scope,
            $"Prepared tempRoot={tempRoot}; zipPath={zipPath}; extractDirectory={extractDirectory}",
            cancellationToken).ConfigureAwait(false);

        try
        {
            await TraceVersionUpdateAsync(
                scope,
                $"Download begin url={payload.DownloadUrl}",
                cancellationToken).ConfigureAwait(false);
            await DownloadToFileAsync(
                payload.DownloadUrl,
                zipPath,
                VersionUpdateProgressOperation.ResourcePackage,
                VersionUpdateProgressSource.MirrorChyan,
                progress,
                cancellationToken).ConfigureAwait(false);
            await TraceVersionUpdateAsync(
                scope,
                $"Download end zipSize={TryGetFileLength(zipPath)}",
                cancellationToken).ConfigureAwait(false);

            progress?.Report(new VersionUpdateProgressInfo(
                VersionUpdateProgressOperation.ResourcePackage,
                VersionUpdateProgressStage.Preparing,
                VersionUpdateProgressSource.MirrorChyan,
                payload.ReleaseNote));
            await TraceVersionUpdateAsync(scope, "Extract begin", cancellationToken).ConfigureAwait(false);
            await Task.Run(
                () => ZipFile.ExtractToDirectory(zipPath, extractDirectory, overwriteFiles: true),
                cancellationToken).ConfigureAwait(false);
            await TraceVersionUpdateAsync(scope, "Extract end", cancellationToken).ConfigureAwait(false);

            await TraceVersionUpdateAsync(scope, "Resolve patch merge directory begin", cancellationToken).ConfigureAwait(false);
            var mergeSource = ResolvePatchMergeDirectory(extractDirectory);
            await TraceVersionUpdateAsync(
                scope,
                $"Resolve patch merge directory end path={mergeSource}",
                cancellationToken).ConfigureAwait(false);

            await TraceVersionUpdateAsync(
                scope,
                $"Merge begin source={mergeSource}; destination={runtimeBaseDirectory}",
                cancellationToken).ConfigureAwait(false);
            await Task.Run(
                () => MergeDirectory(mergeSource, runtimeBaseDirectory),
                cancellationToken).ConfigureAwait(false);
            await TraceVersionUpdateAsync(scope, "Merge end", cancellationToken).ConfigureAwait(false);
            _achievementTrackerService?.Unlock("MirrorChyanFirstUse");
            progress?.Report(new VersionUpdateProgressInfo(
                VersionUpdateProgressOperation.ResourcePackage,
                VersionUpdateProgressStage.Completed,
                VersionUpdateProgressSource.MirrorChyan,
                payload.ReleaseNote));
            var message = string.IsNullOrWhiteSpace(payload.ReleaseNote)
                ? "资源更新完成（MirrorChyan）。"
                : $"资源更新完成（MirrorChyan）：{payload.ReleaseNote}";
            PublishUpdateLog("Resource update completed from MirrorChyan.");
            return UiOperationResult<string>.Ok(message, "Resource update completed.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            PublishUpdateLog($"Resource update from MirrorChyan failed: {ex.Message}", "ERROR");
            await TraceVersionUpdateErrorAsync(scope, "MirrorChyan resource update failed.", ex, cancellationToken).ConfigureAwait(false);
            return UiOperationResult<string>.Fail(
                UiErrorCode.UiOperationFailed,
                $"Failed to update resources from MirrorChyan: {ex.Message}",
                ex.Message);
        }
        finally
        {
            await TraceVersionUpdateAsync(scope, $"Cleanup begin tempRoot={tempRoot}", cancellationToken).ConfigureAwait(false);
            TryDeleteDirectory(tempRoot);
            await TraceVersionUpdateAsync(
                scope,
                $"Cleanup end tempRootExists={Directory.Exists(tempRoot)}",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PersistMirrorChyanExpiryAsync(long unixSeconds, CancellationToken cancellationToken)
    {
        if (_configService is null)
        {
            return;
        }

        try
        {
            _configService.CurrentConfig.GlobalValues[ConfigurationKeys.MirrorChyanCdkExpiredTime] =
                JsonValue.Create(unixSeconds.ToString(CultureInfo.InvariantCulture));
            await _configService.SaveAsync(cancellationToken);
        }
        catch
        {
            // Ignore expiry persistence failures to avoid blocking update flow.
        }
    }

    private static VersionUpdatePolicy NormalizePolicy(VersionUpdatePolicy policy)
    {
        return policy with
        {
            ProxyType = NormalizeProxyType(policy.ProxyType),
            ResourceUpdateSource = NormalizeResourceSource(policy.ResourceUpdateSource),
        };
    }

    private static string NormalizeProxyType(string? proxyType)
    {
        var normalized = (proxyType ?? string.Empty).Trim().ToLowerInvariant();
        return normalized.Length == 0 ? "http" : normalized;
    }

    private static string NormalizeResourceSource(string? source)
    {
        var normalized = (source ?? string.Empty).Trim();
        if (normalized.Length == 0
            || string.Equals(normalized, "Official", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "Github", StringComparison.OrdinalIgnoreCase))
        {
            return "Github";
        }

        if (string.Equals(normalized, "Mirror", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "MirrorChyan", StringComparison.OrdinalIgnoreCase))
        {
            return "MirrorChyan";
        }

        return normalized;
    }

    private static bool TryParseHostPortProxy(string proxy)
    {
        if (proxy.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        if (!Uri.TryCreate($"tcp://{proxy}", UriKind.Absolute, out var uri))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(uri.Host) && uri.Port > 0;
    }

    private string ResolveRuntimeBaseDirectory()
    {
        return _runtimeBaseDirectory
            ?? AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string? NormalizeRuntimeBaseDirectory(string? runtimeBaseDirectory)
    {
        return string.IsNullOrWhiteSpace(runtimeBaseDirectory)
            ? null
            : runtimeBaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string ResolveExtractedResourceDirectory(string extractDirectory)
    {
        var preferred = Path.Combine(extractDirectory, "MaaResource-main", "resource");
        if (Directory.Exists(preferred))
        {
            return preferred;
        }

        var discovered = Directory
            .EnumerateDirectories(extractDirectory, "resource", SearchOption.AllDirectories)
            .FirstOrDefault(path => File.Exists(Path.Combine(path, "version.json")));
        return discovered ?? preferred;
    }

    private static string ResolvePatchMergeDirectory(string extractDirectory)
    {
        var files = Directory.GetFiles(extractDirectory, "*", SearchOption.TopDirectoryOnly);
        var directories = Directory.GetDirectories(extractDirectory, "*", SearchOption.TopDirectoryOnly);
        if (files.Length == 0 && directories.Length == 1)
        {
            return directories[0];
        }

        return extractDirectory;
    }

    private static async Task DownloadToFileAsync(
        string url,
        string destinationPath,
        VersionUpdateProgressOperation operation,
        VersionUpdateProgressSource source,
        IProgress<VersionUpdateProgressInfo>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await ResourceHttpClient.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var file = File.Create(destinationPath);
        if (progress is null)
        {
            await stream.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            return;
        }

        byte[] buffer = new byte[81920];
        long bytesTransferred = 0;
        long bytesSinceLastReport = 0;
        var totalBytes = response.Content.Headers.ContentLength ?? 0;
        var lastReportAt = DateTime.UtcNow;

        while (true)
        {
            var bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead <= 0)
            {
                break;
            }

            await file.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
            bytesTransferred += bytesRead;
            bytesSinceLastReport += bytesRead;

            var now = DateTime.UtcNow;
            var elapsed = (now - lastReportAt).TotalSeconds;
            if (elapsed < 1 && (totalBytes <= 0 || bytesTransferred < totalBytes))
            {
                continue;
            }

            progress.Report(new VersionUpdateProgressInfo(
                operation,
                VersionUpdateProgressStage.Downloading,
                source,
                BytesTransferred: bytesTransferred,
                TotalBytes: totalBytes,
                BytesPerSecond: bytesSinceLastReport / Math.Max(elapsed, 0.001d)));
            lastReportAt = now;
            bytesSinceLastReport = 0;
        }

        if (bytesTransferred > 0 && bytesSinceLastReport > 0)
        {
            var now = DateTime.UtcNow;
            var elapsed = (now - lastReportAt).TotalSeconds;
            progress.Report(new VersionUpdateProgressInfo(
                operation,
                VersionUpdateProgressStage.Downloading,
                source,
                BytesTransferred: bytesTransferred,
                TotalBytes: totalBytes,
                BytesPerSecond: bytesSinceLastReport / Math.Max(elapsed, 0.001d)));
        }
    }

    private static void MergeDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.GetFiles(source))
        {
            var destinationFile = Path.Combine(destination, Path.GetFileName(file));
            File.Copy(file, destinationFile, overwrite: true);
        }

        foreach (var directory in Directory.GetDirectories(source))
        {
            var destinationDirectory = Path.Combine(destination, Path.GetFileName(directory));
            MergeDirectory(directory, destinationDirectory);
        }
    }

    private static void TryDeleteDirectory(string path)
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
            // Ignore temporary cleanup failures.
        }
    }

    private void PublishUpdateLog(string message, string level = "INFO")
    {
        if (_uiLogService is null || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var payload = $"[update] {message}";
        if (string.Equals(level, "ERROR", StringComparison.OrdinalIgnoreCase))
        {
            _uiLogService.Error(payload);
            return;
        }

        if (string.Equals(level, "WARN", StringComparison.OrdinalIgnoreCase)
            || string.Equals(level, "WARNING", StringComparison.OrdinalIgnoreCase))
        {
            _uiLogService.Warn(payload);
            return;
        }

        _uiLogService.Info(payload);
    }

    private async Task TraceVersionUpdateAsync(string scope, string message, CancellationToken cancellationToken = default)
    {
        if (_diagnosticsService is null)
        {
            return;
        }

        try
        {
            await _diagnosticsService.RecordEventAsync(
                scope,
                $"{message} | thread={Environment.CurrentManagedThreadId}",
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort diagnostics must not block resource updates.
        }
    }

    private async Task TraceVersionUpdateErrorAsync(
        string scope,
        string message,
        Exception exception,
        CancellationToken cancellationToken = default)
    {
        if (_diagnosticsService is null)
        {
            return;
        }

        try
        {
            await _diagnosticsService.RecordErrorAsync(
                scope,
                $"{message} | thread={Environment.CurrentManagedThreadId}",
                exception,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort diagnostics must not block resource updates.
        }
    }

    private static long TryGetFileLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static string BuildCurrentVersionQueryToken(ResourceVersionInfo info)
    {
        var effectiveTime = info.LastUpdatedAt == DateTime.MinValue
            ? DateTime.UnixEpoch
            : info.LastUpdatedAt;
        return effectiveTime.ToString("yyyy-MM-dd+HH:mm:ss.fff", CultureInfo.InvariantCulture);
    }

    private static string BuildMirrorChyanSpId()
    {
        var material = string.Join(
            "|",
            Environment.MachineName,
            Environment.UserName,
            Environment.OSVersion.VersionString);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(bytes[..8]).ToLowerInvariant();
    }

    private static MirrorChyanUpdateResponse ParseMirrorChyanPayload(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidDataException("MirrorChyan response is not a JSON object.");

        var code = root["code"]?.GetValue<int?>() ?? -1;
        var message = root["msg"]?.GetValue<string>() ?? string.Empty;
        var data = root["data"] as JsonObject;
        var releaseNote = data?["release_note"]?.GetValue<string>();
        var downloadUrl = data?["url"]?.GetValue<string>();
        var versionName = data?["version_name"]?.GetValue<string>();
        var cdkExpired = data?["cdk_expired_time"]?.GetValue<long?>();
        DateTime? versionTimestamp = DateTime.TryParse(versionName, out var parsedVersion)
            ? parsedVersion
            : null;

        return new MirrorChyanUpdateResponse(
            Code: code,
            Message: message,
            DownloadUrl: downloadUrl ?? string.Empty,
            ReleaseNote: releaseNote ?? string.Empty,
            VersionTimestamp: versionTimestamp,
            CdkExpiredEpoch: cdkExpired);
    }

    private static ResourceVersionInfo LoadResourceVersionInfo(string baseDirectory, string? clientType)
    {
        var normalizedClientType = (clientType ?? string.Empty).Trim();
        var defaultVersionFilePath = Path.Combine(baseDirectory, "resource", "version.json");
        var selectedVersionFilePath = DefaultClientTypes.Contains(normalizedClientType)
            ? defaultVersionFilePath
            : Path.Combine(baseDirectory, "resource", "global", normalizedClientType, "resource", "version.json");

        if (!File.Exists(defaultVersionFilePath) || !File.Exists(selectedVersionFilePath))
        {
            return ResourceVersionInfo.Empty;
        }

        var selectedVersionJson = LoadJsonObject(selectedVersionFilePath);
        if (selectedVersionJson is null)
        {
            return ResourceVersionInfo.Empty;
        }

        var defaultVersionJson = string.Equals(selectedVersionFilePath, defaultVersionFilePath, StringComparison.OrdinalIgnoreCase)
            ? selectedVersionJson
            : LoadJsonObject(defaultVersionFilePath);

        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var poolTime = ReadUnixTime(selectedVersionJson, "gacha", "time");
        var activityTime = ReadUnixTime(selectedVersionJson, "activity", "time");
        var poolName = ReadNestedString(selectedVersionJson, "gacha", "pool");
        var activityName = ReadNestedString(selectedVersionJson, "activity", "name");

        var poolStarted = poolTime.HasValue && nowUnix >= poolTime.Value;
        var activityStarted = activityTime.HasValue && nowUnix >= activityTime.Value;

        var versionName = (poolStarted, activityStarted) switch
        {
            (false, false) => string.Empty,
            (true, false) => poolName,
            (false, true) => activityName,
            _ => (poolTime ?? long.MinValue) > (activityTime ?? long.MinValue)
                ? poolName
                : activityName,
        };

        var lastUpdatedRaw = ReadNestedString(defaultVersionJson, "last_updated");
        var parsedLastUpdated = TryParseResourceTimestamp(lastUpdatedRaw, out var lastUpdated)
            ? lastUpdated
            : DateTime.MinValue;

        return new ResourceVersionInfo(versionName, parsedLastUpdated);
    }

    private static JsonObject? LoadJsonObject(string path)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    private static long? ReadUnixTime(JsonObject root, params string[] segments)
    {
        JsonNode? current = root;
        foreach (var segment in segments)
        {
            current = current?[segment];
            if (current is null)
            {
                return null;
            }
        }

        if (current is JsonValue value)
        {
            if (value.TryGetValue<long>(out var longValue))
            {
                return longValue;
            }

            if (value.TryGetValue<string>(out var text)
                && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out longValue))
            {
                return longValue;
            }
        }

        return null;
    }

    private static string ReadNestedString(JsonObject? root, params string[] segments)
    {
        if (root is null)
        {
            return string.Empty;
        }

        JsonNode? current = root;
        foreach (var segment in segments)
        {
            current = current?[segment];
            if (current is null)
            {
                return string.Empty;
            }
        }

        if (current is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text))
            {
                return text?.Trim() ?? string.Empty;
            }
        }

        return current.ToString().Trim();
    }

    private static bool TryParseResourceTimestamp(string value, out DateTime parsed)
    {
        if (DateTime.TryParseExact(
                value,
                "yyyy-MM-dd HH:mm:ss.fff",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out parsed))
        {
            return true;
        }

        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed);
    }

    private async Task<UiOperationResult> PersistGlobalSettingsWithRollbackAsync(
        IReadOnlyDictionary<string, string> updates,
        string successMessage,
        CancellationToken cancellationToken)
    {
        if (_configService is null)
        {
            return UiOperationResult.Fail(UiErrorCode.VersionUpdateServiceUnavailable, "Version update service is not initialized.");
        }

        var config = _configService.CurrentConfig;
        var snapshot = CloneGlobalSettings(config);
        var persistedUpdates = PrepareSettingsForPersistence(updates);
        try
        {
            foreach (var (key, value) in persistedUpdates)
            {
                config.GlobalValues[key] = JsonValue.Create(value);
            }

            await _configService.SaveAsync(cancellationToken);
            return UiOperationResult.Ok(successMessage);
        }
        catch (Exception ex)
        {
            config.GlobalValues = snapshot;
            _configService.RevalidateCurrentConfig(logIssues: false);
            return UiOperationResult.Fail(
                UiErrorCode.VersionUpdateSaveFailed,
                $"Failed to save version update settings: {ex.Message}",
                ex.Message);
        }
    }

    private static IReadOnlyDictionary<string, string> PrepareSettingsForPersistence(
        IReadOnlyDictionary<string, string> updates)
    {
        var persisted = new Dictionary<string, string>(updates.Count, StringComparer.Ordinal);
        foreach (var (key, value) in updates)
        {
            persisted[key] = string.Equals(key, ConfigurationKeys.MirrorChyanCdk, StringComparison.Ordinal)
                ? EncryptMirrorChyanCdk(value)
                : value;
        }

        return persisted;
    }

    private static Dictionary<string, JsonNode?> CloneGlobalSettings(UnifiedConfig config)
    {
        var clone = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in config.GlobalValues)
        {
            clone[key] = value?.DeepClone();
        }

        return clone;
    }

    private bool TryGetConfig(
        out UnifiedConfig config,
        out UiOperationResult<VersionUpdatePolicy> failure)
    {
        if (_configService is null)
        {
            config = null!;
            failure = UiOperationResult<VersionUpdatePolicy>.Fail(
                UiErrorCode.VersionUpdateServiceUnavailable,
                "Version update service is not initialized.");
            return false;
        }

        config = _configService.CurrentConfig;
        failure = default!;
        return true;
    }

    private static string ReadString(UnifiedConfig config, string key, string fallback)
    {
        if (config.GlobalValues.TryGetValue(key, out var node) && node is not null)
        {
            var text = node.ToString().Trim();
            if (text.Length > 0)
            {
                return text;
            }
        }

        return fallback;
    }

    private static string EncryptMirrorChyanCdk(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !OperatingSystem.IsWindows())
        {
            return value;
        }

        try
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            var encrypted = InvokeWindowsProtectedData("Protect", bytes);
            return encrypted is null ? value : Convert.ToBase64String(encrypted);
        }
        catch
        {
            return value;
        }
    }

    private static string DecryptMirrorChyanCdk(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !OperatingSystem.IsWindows())
        {
            return value;
        }

        try
        {
            var bytes = Convert.FromBase64String(value);
            var decrypted = InvokeWindowsProtectedData("Unprotect", bytes);
            if (decrypted is null)
            {
                return value;
            }

            return Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            return value;
        }
    }

    private static byte[]? InvokeWindowsProtectedData(string methodName, byte[] data)
    {
        var protectedDataType = Type.GetType(
            "System.Security.Cryptography.ProtectedData, System.Security.Cryptography.ProtectedData");
        var dataProtectionScopeType = Type.GetType(
            "System.Security.Cryptography.DataProtectionScope, System.Security.Cryptography.ProtectedData");
        if (protectedDataType is null || dataProtectionScopeType is null)
        {
            return null;
        }

        var method = protectedDataType.GetMethod(
            methodName,
            [typeof(byte[]), typeof(byte[]), dataProtectionScopeType]);
        if (method is null)
        {
            return null;
        }

        var currentUserScope = Enum.Parse(dataProtectionScopeType, "CurrentUser");
        return method.Invoke(null, [data, null, currentUserScope]) as byte[];
    }

    private static bool ReadBool(UnifiedConfig config, string key, bool fallback)
    {
        if (!config.GlobalValues.TryGetValue(key, out var node) || node is null)
        {
            return fallback;
        }

        if (bool.TryParse(node.ToString(), out var parsed))
        {
            return parsed;
        }

        if (int.TryParse(node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt))
        {
            return parsedInt != 0;
        }

        return fallback;
    }

    private sealed record MirrorChyanUpdateResponse(
        int Code,
        string Message,
        string DownloadUrl,
        string ReleaseNote,
        DateTime? VersionTimestamp,
        long? CdkExpiredEpoch);
}
