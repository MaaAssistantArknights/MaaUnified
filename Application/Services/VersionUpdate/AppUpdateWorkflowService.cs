using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using MAAUnified.Application.Models;
using MAAUnified.Application.Services;

namespace MAAUnified.Application.Services.VersionUpdate;

public sealed class AppUpdateWorkflowService
{
    private const string MaaApiBaseUrl = "https://api.maa.plus/MaaAssistantArknights/api/";
    private const string MaaApiFallbackBaseUrl = "https://api2.maa.plus/MaaAssistantArknights/api/";
    private const string MirrorChyanAppUpdateUrl = "https://mirrorchyan.com/api/resources/MAAUnified/latest";
    private const string WindowsRelayManifestFileName = "windows-relay.json";
    private const string WindowsManualUpdateMessageKey = "Settings.VersionUpdate.Status.WindowsManualUpdateRequired";
    private const string PackageUnavailableMessageKey = "Settings.VersionUpdate.Status.PackageUnavailable";
    private const string PackageDownloadFailedMessageKey = "Settings.VersionUpdate.Status.PackageDownloadFailed";
    private static readonly HttpClient DefaultHttpClient = BuildDefaultHttpClient();

    private readonly IAppLifecycleService _appLifecycleService;
    private readonly HttpClient? _httpClient;
    private readonly PackageSelectionPlatform _platform;

    public AppUpdateWorkflowService(
        IAppLifecycleService appLifecycleService,
        HttpClient? httpClient = null,
        OSPlatform? operatingSystem = null,
        Architecture? architecture = null)
    {
        _appLifecycleService = appLifecycleService;
        _httpClient = httpClient;
        _platform = ResolvePlatform(operatingSystem, architecture);
    }

    /// <summary>
    /// 检查软件更新并解析适用于当前平台的更新包。
    /// </summary>
    /// <param name="policy">更新策略。</param>
    /// <param name="currentVersion">当前版本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="forceFullPackage">
    /// 安装文件缺失或上次更新失败时强制只解析完整包（WPF <c>da79ff88ad</c>）。
    /// OTA 增量只含版本间差异，无法修复残留的不一致文件。
    /// </param>
    public async Task<VersionUpdateCheckResult> CheckForUpdatesAsync(
        VersionUpdatePolicy policy,
        string currentVersion,
        CancellationToken cancellationToken,
        bool forceFullPackage = false)
    {
        var (httpClient, disposeClient) = ResolveHttpClient(policy);
        try
        {
            currentVersion = string.IsNullOrWhiteSpace(currentVersion) ? "unknown" : currentVersion.Trim();
            if (ShouldSkipUpdateCheckForDebugVersion(currentVersion))
            {
                return new VersionUpdateCheckResult(
                    Channel: policy.VersionType,
                    CurrentVersion: currentVersion,
                    TargetVersion: currentVersion,
                    ReleaseName: currentVersion,
                    Summary: string.Empty,
                    Body: string.Empty,
                    PackageName: null,
                    PackageDownloadUrl: null,
                    PackageSize: null,
                    IsNewVersion: false,
                    HasPackage: false);
            }

            var mirrorChyanResolution = await TryResolveReleaseFromMirrorChyanAsync(
                policy,
                currentVersion,
                httpClient,
                cancellationToken,
                forceFullPackage).ConfigureAwait(false);
            if (mirrorChyanResolution.Release is not null)
            {
                return mirrorChyanResolution.Release;
            }

            var releaseResolution = await ResolveReleaseAsync(
                policy.ResourceApi,
                policy.VersionType,
                httpClient,
                cancellationToken).ConfigureAwait(false);
            if (releaseResolution.Release is null)
            {
                return new VersionUpdateCheckResult(
                    Channel: policy.VersionType,
                    CurrentVersion: currentVersion,
                    TargetVersion: currentVersion,
                    ReleaseName: currentVersion,
                    Summary: string.Empty,
                    Body: string.Empty,
                    PackageName: null,
                    PackageDownloadUrl: null,
                    PackageSize: null,
                    IsNewVersion: false,
                    HasPackage: false,
                    CheckSucceeded: releaseResolution.HasValidResponse,
                    CheckFailureMessage: releaseResolution.HasValidResponse
                        ? null
                        : "Failed to get release information from Maa API.",
                    MirrorChyanCdkExpiryUnixSeconds: mirrorChyanResolution.CdkExpiryUnixSeconds);
            }

            var releaseValue = releaseResolution.Release!.Release;
            var tag = TryGetString(releaseValue, "tag_name") ?? string.Empty;
            var releaseName = TryGetString(releaseValue, "name") ?? tag;
            var body = TryGetString(releaseValue, "body") ?? string.Empty;
            var summary = string.IsNullOrWhiteSpace(body) ? releaseName : body;
            var targetVersion = tag;
            var isNew = IsNewerVersion(targetVersion, currentVersion);

            ResolvedPackage? resolvedPackage = null;
            if (isNew || forceFullPackage)
            {
                // 修复场景不做版本新旧判断：已是最新版本时也解析同版本完整包用于重装修复。

                resolvedPackage = await ResolvePackageAsync(
                    policy.ResourceApi,
                    policy.VersionType,
                    allowMirrorUrls: string.Equals(policy.ResourceUpdateSource, "Github", StringComparison.OrdinalIgnoreCase)
                        && !policy.ForceGithubGlobalSource,
                    allowWindowsRelay: true,
                    targetVersion,
                    releaseValue,
                    httpClient,
                    cancellationToken,
                    currentVersion,
                    forceFullPackage).ConfigureAwait(false);
            }

            var hasUsablePackage = resolvedPackage?.Status == PackageResolutionStatus.Available
                && resolvedPackage.DownloadUrl is not null;

            return new VersionUpdateCheckResult(
                Channel: policy.VersionType,
                CurrentVersion: currentVersion,
                TargetVersion: targetVersion,
                ReleaseName: releaseName,
                Summary: summary,
                Body: body,
                PackageName: resolvedPackage?.Name,
                PackageDownloadUrl: resolvedPackage?.DownloadUrl,
                PackageSize: resolvedPackage?.Size,
                // A release can be newer even when no package is suitable for this
                // installation. Keep that fact visible so the caller can surface the
                // release notes/manual-update path instead of reporting "latest".
                IsNewVersion: isNew,
                HasPackage: hasUsablePackage,
                PreparedPackagePath: null,
                PackageResolutionStatus: resolvedPackage?.Status ?? PackageResolutionStatus.NotChecked,
                PackageSourceKind: resolvedPackage?.SourceKind ?? PackageSourceKind.None,
                PackageFailureMessageKey: resolvedPackage?.FailureMessageKey,
                PackageMirrorUrls: resolvedPackage?.MirrorUrls,
                MirrorChyanCdkExpiryUnixSeconds: mirrorChyanResolution.CdkExpiryUnixSeconds);
        }
        finally
        {
            if (disposeClient)
            {
                httpClient.Dispose();
            }
        }
    }

    /// <summary>
    /// 下载更新包。
    /// </summary>
    /// <param name="checkResult">更新检查结果。</param>
    /// <param name="runtimeBaseDirectory">安装根目录。</param>
    /// <param name="forceDownload">覆盖已存在的同名包。</param>
    /// <param name="policy">更新策略。</param>
    /// <param name="progress">下载进度。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="forceFullPackage">只允许完整包（安装修复场景）。</param>
    public async Task<UiOperationResult<string>> DownloadPackageAsync(
        VersionUpdateCheckResult checkResult,
        string runtimeBaseDirectory,
        bool forceDownload,
        VersionUpdatePolicy? policy,
        IProgress<VersionUpdateProgressInfo>? progress,
        CancellationToken cancellationToken,
        bool forceFullPackage = false)
    {
        if (!checkResult.HasPackage
            || checkResult.PackageDownloadUrl is null
            || checkResult.PackageResolutionStatus != PackageResolutionStatus.Available)
        {
            return UiOperationResult<string>.Fail(
                UiErrorCode.UiOperationFailed,
                "No update package is available for download.");
        }

        var targetDirectory = Path.Combine(runtimeBaseDirectory, "update-packages");
        Directory.CreateDirectory(targetDirectory);
        var packageName = string.IsNullOrWhiteSpace(checkResult.PackageName)
            ? BuildFallbackPackageName(checkResult.TargetVersion)
            : checkResult.PackageName;
        if (forceFullPackage && !IsFullPackageForRepair(packageName))
        {
            return UiOperationResult<string>.Fail(
                UiErrorCode.UiOperationFailed,
                $"The installation requires a full package update; refusing to download `{packageName}`. OTA packages cannot repair missing or inconsistent installation files.");
        }

        var destinationPath = Path.Combine(targetDirectory, packageName);
        var temporaryPath = $"{destinationPath}.temp";

        if (File.Exists(destinationPath) && !forceDownload)
        {
            return UiOperationResult<string>.Ok(destinationPath, "Reused existing update package.");
        }

        var (httpClient, disposeClient) = ResolveHttpClient(policy);
        try
        {
            var candidates = BuildPackageDownloadCandidates(checkResult);
            UiOperationResult<string>? lastFailure = null;
            foreach (var candidate in candidates)
            {
                TryDeleteFile(temporaryPath);
                var downloadResult = await TryDownloadPackageFromCandidateAsync(
                    httpClient,
                    candidate,
                    temporaryPath,
                    progress,
                    cancellationToken).ConfigureAwait(false);
                if (downloadResult.Success)
                {
                    File.Move(temporaryPath, destinationPath, overwrite: true);
                    return UiOperationResult<string>.Ok(destinationPath, "Update package downloaded.");
                }

                lastFailure = downloadResult;
            }

            return lastFailure ?? UiOperationResult<string>.Fail(
                UiErrorCode.UiOperationFailed,
                "Failed to download update package.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return UiOperationResult<string>.Fail(
                UiErrorCode.UiOperationFailed,
                $"Failed to download update package: {ex.Message}");
        }
        finally
        {
            TryDeleteFile(temporaryPath);
            if (disposeClient)
            {
                httpClient.Dispose();
            }
        }
    }

    private static async Task CopyToAsyncWithProgress(
        Stream sourceStream,
        Stream destinationStream,
        long totalBytes,
        IProgress<VersionUpdateProgressInfo>? progress,
        CancellationToken cancellationToken)
    {
        if (progress is null)
        {
            await sourceStream.CopyToAsync(destinationStream, cancellationToken).ConfigureAwait(false);
            return;
        }

        byte[] buffer = new byte[81920];
        long bytesTransferred = 0;
        long bytesSinceLastReport = 0;
        var lastReportAt = DateTime.UtcNow;

        while (true)
        {
            var bytesRead = await sourceStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead <= 0)
            {
                break;
            }

            await destinationStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
            bytesTransferred += bytesRead;
            bytesSinceLastReport += bytesRead;

            var now = DateTime.UtcNow;
            var elapsed = (now - lastReportAt).TotalSeconds;
            if (elapsed < 1 && (totalBytes <= 0 || bytesTransferred < totalBytes))
            {
                continue;
            }

            progress.Report(new VersionUpdateProgressInfo(
                VersionUpdateProgressOperation.SoftwarePackage,
                VersionUpdateProgressStage.Downloading,
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
                VersionUpdateProgressOperation.SoftwarePackage,
                VersionUpdateProgressStage.Downloading,
                BytesTransferred: bytesTransferred,
                TotalBytes: totalBytes,
                BytesPerSecond: bytesSinceLastReport / Math.Max(elapsed, 0.001d)));
        }
    }

    public Task<UiOperationResult> InstallPackageAsync(string packagePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath))
        {
            return Task.FromResult(UiOperationResult.Fail(
                UiErrorCode.UiOperationFailed,
                "Update package file is missing."));
        }

        return _appLifecycleService.RestartAsync(cancellationToken);
    }

    private static HttpClient BuildDefaultHttpClient(VersionUpdatePolicy? policy = null)
    {
        return VersionUpdateHttpClientFactory.Create(policy, TimeSpan.FromSeconds(60));
    }

    private (HttpClient Client, bool DisposeClient) ResolveHttpClient(VersionUpdatePolicy? policy)
    {
        if (_httpClient is not null)
        {
            return (_httpClient, false);
        }

        if (policy is null || string.IsNullOrWhiteSpace(policy.Proxy))
        {
            return (DefaultHttpClient, false);
        }

        return (BuildDefaultHttpClient(policy), true);
    }

    private async Task<ReleaseResolution> ResolveReleaseAsync(
        string? resourceApi,
        string channel,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var hasValidResponse = false;
        if (!string.IsNullOrWhiteSpace(resourceApi))
        {
            var trimmed = resourceApi.Trim();
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            {
                if (string.Equals(uri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
                {
                    var release = SelectRelease(LoadReleasesFromFileSync(uri.LocalPath), channel);
                    return new ReleaseResolution(
                        release.HasValue ? new ResolvedRelease(release.Value) : null,
                        HasValidResponse: true);
                }

                var directRelease = await TryResolveReleaseFromDirectFeedAsync(httpClient, uri, channel, cancellationToken).ConfigureAwait(false);
                if (directRelease is not null)
                {
                    return directRelease;
                }

                foreach (var baseUrl in BuildMaaApiBaseUrlCandidates(uri))
                {
                    var apiRelease = await TryResolveReleaseFromMaaApiBaseUrlAsync(httpClient, baseUrl, channel, cancellationToken).ConfigureAwait(false);
                    if (apiRelease.Release is not null)
                    {
                        return apiRelease;
                    }

                    hasValidResponse |= apiRelease.HasValidResponse;
                }
            }

            if (File.Exists(trimmed))
            {
                var release = SelectRelease(LoadReleasesFromFileSync(trimmed), channel);
                return new ReleaseResolution(
                    release.HasValue ? new ResolvedRelease(release.Value) : null,
                    HasValidResponse: true);
            }
        }

        foreach (var baseUrl in GetDefaultMaaApiBaseUrls())
        {
            var apiRelease = await TryResolveReleaseFromMaaApiBaseUrlAsync(httpClient, baseUrl, channel, cancellationToken).ConfigureAwait(false);
            if (apiRelease.Release is not null)
            {
                return apiRelease;
            }

            hasValidResponse |= apiRelease.HasValidResponse;
        }

        return new ReleaseResolution(null, hasValidResponse);
    }

    private async Task<ResolvedPackage> ResolvePackageAsync(
        string? resourceApi,
        string channel,
        bool allowMirrorUrls,
        bool allowWindowsRelay,
        string targetVersion,
        JsonElement release,
        HttpClient httpClient,
        CancellationToken cancellationToken,
        string currentVersion,
        bool forceFullPackage)
    {
        if (_platform.IsWindows)
        {
            if (allowWindowsRelay)
            {
                var relayPackage = await TryResolveWindowsRelayPackageAsync(
                    resourceApi,
                    channel,
                    targetVersion,
                    httpClient,
                    cancellationToken,
                    currentVersion,
                    forceFullPackage).ConfigureAwait(false);
                if (relayPackage is not null)
                {
                    return relayPackage;
                }
            }

            return new ResolvedPackage(
                Status: PackageResolutionStatus.WindowsManualUpdateRequired,
                SourceKind: PackageSourceKind.None,
                FailureMessageKey: WindowsManualUpdateMessageKey);
        }

        var asset = SelectPackageAsset(release, currentVersion, forceFullPackage);
        if (asset is not null)
        {
            return allowMirrorUrls ? asset : asset with { MirrorUrls = null };
        }

        return
            new ResolvedPackage(
                Status: PackageResolutionStatus.Unavailable,
                SourceKind: PackageSourceKind.None,
                FailureMessageKey: PackageUnavailableMessageKey);
    }

    private async Task<ResolvedPackage?> TryResolveWindowsRelayPackageAsync(
        string? resourceApi,
        string channel,
        string targetVersion,
        HttpClient httpClient,
        CancellationToken cancellationToken,
        string currentVersion,
        bool forceFullPackage)
    {
        foreach (var candidate in BuildWindowsRelayManifestCandidates(resourceApi))
        {
            WindowsRelayManifest? manifest;
            try
            {
                manifest = await LoadWindowsRelayManifestAsync(httpClient, candidate, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                continue;
            }

            if (manifest is null)
            {
                continue;
            }

            var matched = manifest.Packages.FirstOrDefault(entry => WindowsRelayEntryMatches(
                entry,
                manifest,
                channel,
                targetVersion,
                currentVersion,
                forceFullPackage));
            if (matched is null)
            {
                continue;
            }

            if (!Uri.TryCreate(matched.Url, UriKind.Absolute, out var downloadUrl))
            {
                continue;
            }

            return new ResolvedPackage(
                Status: PackageResolutionStatus.Available,
                SourceKind: PackageSourceKind.WindowsRelayManifest,
                Name: string.IsNullOrWhiteSpace(matched.Name)
                    ? Path.GetFileName(downloadUrl.AbsolutePath)
                    : matched.Name,
                DownloadUrl: downloadUrl,
                Size: matched.Size);
        }

        return null;
    }

    private IEnumerable<string> BuildWindowsRelayManifestCandidates(string? resourceApi)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(resourceApi))
        {
            var trimmed = resourceApi.Trim();
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            {
                if (string.Equals(uri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
                {
                    var directory = Path.GetDirectoryName(uri.LocalPath);
                    if (!string.IsNullOrWhiteSpace(directory))
                    {
                        candidates.Add(Path.Combine(directory, WindowsRelayManifestFileName));
                    }
                }
                else if (LooksLikeDirectReleaseFeedUri(uri))
                {
                    candidates.Add(new Uri(uri, WindowsRelayManifestFileName).ToString());
                }
                else
                {
                    foreach (var baseUrl in BuildMaaApiBaseUrlCandidates(uri))
                    {
                        candidates.Add(new Uri(new Uri(baseUrl), $"version/{WindowsRelayManifestFileName}").ToString());
                    }
                }
            }
            else if (File.Exists(trimmed))
            {
                var directory = Path.GetDirectoryName(trimmed);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    candidates.Add(Path.Combine(directory, WindowsRelayManifestFileName));
                }
            }
        }

        foreach (var baseUrl in GetDefaultMaaApiBaseUrls())
        {
            candidates.Add(new Uri(new Uri(baseUrl), $"version/{WindowsRelayManifestFileName}").ToString());
        }

        return candidates
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<WindowsRelayManifest?> LoadWindowsRelayManifestAsync(
        HttpClient httpClient,
        string candidate,
        CancellationToken cancellationToken)
    {
        if (File.Exists(candidate))
        {
            using var localDocument = JsonDocument.Parse(await File.ReadAllTextAsync(candidate, cancellationToken).ConfigureAwait(false));
            return ParseWindowsRelayManifest(localDocument.RootElement);
        }

        if (Uri.TryCreate(candidate, UriKind.Absolute, out var candidateUri)
            && string.Equals(candidateUri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
        {
            using var fileDocument = JsonDocument.Parse(await File.ReadAllTextAsync(candidateUri.LocalPath, cancellationToken).ConfigureAwait(false));
            return ParseWindowsRelayManifest(fileDocument.RootElement);
        }

        using var remoteDocument = await FetchJsonDocumentAsync(httpClient, candidate, cancellationToken).ConfigureAwait(false);
        return ParseWindowsRelayManifest(remoteDocument.RootElement);
    }

    private static WindowsRelayManifest ParseWindowsRelayManifest(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return new WindowsRelayManifest(null, null, ParseWindowsRelayEntries(root, null, null));
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return new WindowsRelayManifest(null, null, []);
        }

        var defaultVersion = TryGetString(root, "version");
        var defaultChannel = TryGetString(root, "channel");
        if (root.TryGetProperty("packages", out var packages) && packages.ValueKind == JsonValueKind.Array)
        {
            return new WindowsRelayManifest(
                defaultVersion,
                defaultChannel,
                ParseWindowsRelayEntries(packages, defaultVersion, defaultChannel));
        }

        return new WindowsRelayManifest(
            defaultVersion,
            defaultChannel,
            ParseWindowsRelayEntries(new[] { root }, defaultVersion, defaultChannel));
    }

    private static IReadOnlyList<WindowsRelayPackageEntry> ParseWindowsRelayEntries(
        JsonElement packages,
        string? defaultVersion,
        string? defaultChannel)
    {
        return ParseWindowsRelayEntries(
            packages.EnumerateArray(),
            defaultVersion,
            defaultChannel);
    }

    private static IReadOnlyList<WindowsRelayPackageEntry> ParseWindowsRelayEntries(
        IEnumerable<JsonElement> packages,
        string? defaultVersion,
        string? defaultChannel)
    {
        var entries = new List<WindowsRelayPackageEntry>();
        foreach (var package in packages)
        {
            if (package.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var os = TryGetString(package, "os");
            var arch = TryGetString(package, "arch");
            var url = TryGetString(package, "url");
            if (string.IsNullOrWhiteSpace(os)
                || string.IsNullOrWhiteSpace(arch)
                || string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            entries.Add(new WindowsRelayPackageEntry(
                Version: TryGetString(package, "version") ?? defaultVersion,
                Channel: TryGetString(package, "channel") ?? defaultChannel,
                Os: os,
                Arch: arch,
                Url: url,
                Sha256: TryGetString(package, "sha256"),
                Size: TryGetInt64(package, "size"),
                Name: TryGetString(package, "name")));
        }

        return entries;
    }

    private bool WindowsRelayEntryMatches(
        WindowsRelayPackageEntry entry,
        WindowsRelayManifest manifest,
        string channel,
        string targetVersion,
        string currentVersion,
        bool forceFullPackage)
    {
        if (!IsMaaUnifiedPackageReference(entry.Name, entry.Url))
        {
            return false;
        }

        // 包名识别统一决定资产类型：修复场景只认完整包；识别不了的文件名无法保证可修复安装，
        // 在强制完整包时直接跳过（WPF da79ff88ad 的 OtaRejectedByBrokenInstall 语义）。
        var packageName = entry.Name ?? string.Empty;
        if (string.IsNullOrWhiteSpace(packageName)
            && Uri.TryCreate(entry.Url, UriKind.Absolute, out var packageUri))
        {
            packageName = Path.GetFileName(packageUri.AbsolutePath);
        }
        if (UpdatePackageNameRecognizer.TryRecognize(packageName, out var identity))
        {
            if (forceFullPackage && identity.Kind != UpdatePackageKind.FullPackage)
            {
                return false;
            }

            if (identity.Kind == UpdatePackageKind.OtaPackage)
            {
                var sourceComparison = UpdatePackageNameRecognizer.CompareVersions(identity.SourceVersion, currentVersion);
                if (sourceComparison is null || sourceComparison != 0)
                {
                    return false;
                }
            }
        }
        else if (forceFullPackage)
        {
            return false;
        }

        var entryVersion = entry.Version ?? manifest.Version ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(entryVersion)
            && !string.Equals(entryVersion.Trim(), targetVersion, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var entryChannel = entry.Channel ?? manifest.Channel ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(entryChannel)
            && !MatchesNormalizedChannel(entryChannel, channel))
        {
            return false;
        }

        return string.Equals(NormalizeOs(entry.Os), _platform.OperatingSystem, StringComparison.OrdinalIgnoreCase)
            && string.Equals(NormalizeArchitecture(entry.Arch), _platform.Architecture, StringComparison.OrdinalIgnoreCase);
    }

    private ResolvedPackage? SelectPackageAsset(
        JsonElement release,
        string currentVersion,
        bool forceFullPackage)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        ResolvedPackage? best = null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("browser_download_url", out var browserNode)
                || browserNode.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var rawUrl = browserNode.GetString();
            if (string.IsNullOrWhiteSpace(rawUrl) || !Uri.TryCreate(rawUrl, UriKind.Absolute, out var downloadUrl))
            {
                continue;
            }

            var name = asset.TryGetProperty("name", out var nameNode) && nameNode.ValueKind == JsonValueKind.String
                ? nameNode.GetString() ?? Path.GetFileName(downloadUrl.AbsolutePath)
                : Path.GetFileName(downloadUrl.AbsolutePath);
            if (!IsMaaUnifiedPackageName(name))
            {
                continue;
            }

            var score = ScorePackageName(name);
            if (score <= 0)
            {
                continue;
            }

            // 包名识别用于选择下载资产：平台/架构不匹配直接跳过，OTA 还需源版本正好等于当前版本；
            // 安装修复场景只接受完整包，识别不了的文件名无法保证可修复安装，一并跳过。
            if (UpdatePackageNameRecognizer.TryRecognize(name, out var identity))
            {
                if (!string.Equals(identity.Platform, _platform.OperatingSystem, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(identity.Architecture, _platform.Architecture, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (forceFullPackage && identity.Kind != UpdatePackageKind.FullPackage)
                {
                    continue;
                }

                if (identity.Kind == UpdatePackageKind.OtaPackage)
                {
                    var sourceComparison = UpdatePackageNameRecognizer.CompareVersions(identity.SourceVersion, currentVersion);
                    if (sourceComparison is not null && sourceComparison != 0)
                    {
                        continue;
                    }
                }

                var targetComparison = UpdatePackageNameRecognizer.CompareVersions(identity.TargetVersion, currentVersion);
                if (targetComparison is < 0)
                {
                    continue;
                }

                score += 200;
            }
            else if (forceFullPackage)
            {
                continue;
            }

            long? size = null;
            if (asset.TryGetProperty("size", out var sizeNode) && sizeNode.TryGetInt64(out var sizeValue))
            {
                size = sizeValue;
            }

            var candidate = new ResolvedPackage(
                Status: PackageResolutionStatus.Available,
                SourceKind: PackageSourceKind.ReleaseAsset,
                Name: name,
                DownloadUrl: downloadUrl,
                Size: size,
                MirrorUrls: ReadMirrorUrls(asset),
                Score: score);
            if (best is null || candidate.Score > best.Score)
            {
                best = candidate;
            }
        }

        return best;
    }

    private int ScorePackageName(string packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName))
        {
            return 0;
        }

        var normalized = packageName.Trim().ToLowerInvariant();
        var extensionScore = _platform.IsWindows
            ? 0
            : _platform.IsMacOS
                ? normalized.EndsWith(".dmg", StringComparison.Ordinal) ? 10 : 0
                : normalized.EndsWith(".zip", StringComparison.Ordinal) ? 10 : 0;
        if (extensionScore == 0)
        {
            return 0;
        }

        var exactScore = _platform.ExactTokens.FirstOrDefault(token => normalized.Contains(token, StringComparison.Ordinal));
        if (exactScore is not null)
        {
            return 100 + extensionScore;
        }

        var hasOs = _platform.OsTokens.Any(token => normalized.Contains(token, StringComparison.Ordinal));
        var hasArch = _platform.ArchitectureTokens.Any(token => normalized.Contains(token, StringComparison.Ordinal));
        if (!hasOs || !hasArch)
        {
            return 0;
        }

        return 50 + extensionScore;
    }

    private static bool IsMaaUnifiedPackageReference(string? packageName, string packageUrl)
    {
        return IsMaaUnifiedPackageName(packageName)
            || (Uri.TryCreate(packageUrl, UriKind.Absolute, out var uri)
                && IsMaaUnifiedPackageName(Path.GetFileName(uri.AbsolutePath)));
    }

    private static bool IsMaaUnifiedPackageName(string? packageName)
    {
        return !string.IsNullOrWhiteSpace(packageName)
            && packageName.Trim().StartsWith("MAAUnified-", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFullPackageForRepair(string? packageName)
    {
        return !string.IsNullOrWhiteSpace(packageName)
            && UpdatePackageNameRecognizer.TryRecognize(packageName, out var identity)
            && identity.Kind == UpdatePackageKind.FullPackage;
    }

    private static bool LooksLikeDirectReleaseFeedUri(Uri uri)
    {
        if (string.Equals(uri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.Contains("/releases", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return uri.AbsolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<ReleaseResolution?> TryResolveReleaseFromDirectFeedAsync(
        HttpClient httpClient,
        Uri uri,
        string channel,
        CancellationToken cancellationToken)
    {
        if (!LooksLikeDirectReleaseFeedUri(uri))
        {
            return null;
        }

        try
        {
            var releases = await FetchReleasesFromUrlAsync(httpClient, uri.ToString(), cancellationToken).ConfigureAwait(false);
            var release = SelectRelease(releases, channel);
            return new ReleaseResolution(
                release.HasValue ? new ResolvedRelease(release.Value) : null,
                HasValidResponse: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new ReleaseResolution(null, HasValidResponse: false);
        }
    }

    private static string[] BuildMaaApiBaseUrlCandidates(Uri resourceApiUri)
    {
        var candidates = new List<string>();
        if (TryNormalizeMaaApiBaseUrl(resourceApiUri, out var normalized))
        {
            candidates.Add(normalized);
        }

        foreach (var officialBaseUrl in GetDefaultMaaApiBaseUrls())
        {
            if (!candidates.Contains(officialBaseUrl, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(officialBaseUrl);
            }
        }

        return candidates.ToArray();
    }

    private static string[] GetDefaultMaaApiBaseUrls()
    {
        return [MaaApiBaseUrl, MaaApiFallbackBaseUrl];
    }

    private static bool TryNormalizeMaaApiBaseUrl(Uri resourceApiUri, out string baseUrl)
    {
        baseUrl = string.Empty;
        if (!string.Equals(resourceApiUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(resourceApiUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(resourceApiUri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase)
            || resourceApiUri.AbsolutePath.Contains("/releases", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var absoluteUri = resourceApiUri.AbsoluteUri;
        var versionIndex = absoluteUri.IndexOf("/version/", StringComparison.OrdinalIgnoreCase);
        if (versionIndex >= 0)
        {
            baseUrl = absoluteUri[..(versionIndex + 1)];
            return true;
        }

        if (Path.HasExtension(resourceApiUri.AbsolutePath))
        {
            return false;
        }

        baseUrl = absoluteUri.EndsWith("/", StringComparison.Ordinal) ? absoluteUri : $"{absoluteUri}/";
        return true;
    }

    private async Task<ReleaseResolution> TryResolveReleaseFromMaaApiBaseUrlAsync(
        HttpClient httpClient,
        string baseUrl,
        string channel,
        CancellationToken cancellationToken)
    {
        try
        {
            var summaryUri = new Uri(new Uri(baseUrl), "version/summary.json");
            using var summaryDocument = await FetchJsonDocumentAsync(httpClient, summaryUri.ToString(), cancellationToken).ConfigureAwait(false);
            if (summaryDocument.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new ReleaseResolution(null, HasValidResponse: false);
            }

            var detailUri = ResolveMaaApiDetailUri(summaryDocument.RootElement, baseUrl, channel);
            using var detailDocument = await FetchJsonDocumentAsync(httpClient, detailUri, cancellationToken).ConfigureAwait(false);
            if (detailDocument.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new ReleaseResolution(null, HasValidResponse: false);
            }

            if (detailDocument.RootElement.TryGetProperty("details", out var details)
                && details.ValueKind == JsonValueKind.Object)
            {
                return new ReleaseResolution(new ResolvedRelease(details.Clone()), HasValidResponse: true);
            }

            return detailDocument.RootElement.TryGetProperty("tag_name", out _)
                ? new ReleaseResolution(new ResolvedRelease(detailDocument.RootElement.Clone()), HasValidResponse: true)
                : new ReleaseResolution(null, HasValidResponse: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new ReleaseResolution(null, HasValidResponse: false);
        }
    }

    private static string ResolveMaaApiDetailUri(JsonElement summary, string baseUrl, string channel)
    {
        var channelKey = NormalizeMaaApiChannel(channel);
        if (summary.TryGetProperty(channelKey, out var channelElement)
            && channelElement.ValueKind == JsonValueKind.Object
            && channelElement.TryGetProperty("detail", out var detailElement)
            && detailElement.ValueKind == JsonValueKind.String)
        {
            var detail = detailElement.GetString();
            if (!string.IsNullOrWhiteSpace(detail))
            {
                if (Uri.TryCreate(detail, UriKind.Absolute, out var absoluteDetail))
                {
                    return absoluteDetail.ToString();
                }

                return new Uri(new Uri(baseUrl), detail).ToString();
            }
        }

        return new Uri(new Uri(baseUrl), $"version/{channelKey}.json").ToString();
    }

    private static string NormalizeMaaApiChannel(string channel)
    {
        if (string.Equals(channel, "Beta", StringComparison.OrdinalIgnoreCase))
        {
            return "beta";
        }

        if (string.Equals(channel, "Nightly", StringComparison.OrdinalIgnoreCase))
        {
            return "alpha";
        }

        return "stable";
    }

    private static JsonElement[] LoadReleasesFromFileSync(string path)
    {
        var text = File.ReadAllText(path);
        using var document = JsonDocument.Parse(text);
        return document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().Select(static element => element.Clone()).ToArray()
            : Array.Empty<JsonElement>();
    }

    private static async Task<JsonDocument> FetchJsonDocumentAsync(
        HttpClient httpClient,
        string url,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonElement[]> FetchReleasesFromUrlAsync(
        HttpClient httpClient,
        string url,
        CancellationToken cancellationToken)
    {
        using var document = await FetchJsonDocumentAsync(httpClient, url, cancellationToken).ConfigureAwait(false);
        return document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().Select(static element => element.Clone()).ToArray()
            : Array.Empty<JsonElement>();
    }

    private static JsonElement? SelectRelease(JsonElement[] releases, string channel)
    {
        JsonElement? fallback = null;
        var normalizedChannel = (channel ?? string.Empty).Trim();
        var requiresChannelMatch =
            string.Equals(normalizedChannel, "Stable", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedChannel, "Beta", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedChannel, "Nightly", StringComparison.OrdinalIgnoreCase);
        foreach (var release in releases)
        {
            if (fallback is null)
            {
                fallback = release;
            }

            if (MatchesChannel(release, normalizedChannel))
            {
                return release;
            }
        }

        return requiresChannelMatch ? null : fallback;
    }

    private static bool MatchesChannel(JsonElement release, string channel)
    {
        var tag = TryGetString(release, "tag_name") ?? string.Empty;
        var name = TryGetString(release, "name") ?? string.Empty;
        var prerelease = release.TryGetProperty("prerelease", out var prereleaseNode)
            && prereleaseNode.ValueKind is JsonValueKind.True or JsonValueKind.False
            && prereleaseNode.GetBoolean();

        if (string.Equals(channel, "Beta", StringComparison.OrdinalIgnoreCase))
        {
            return IsBetaReleaseTag(tag)
                || name.Contains("beta", StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(channel, "Nightly", StringComparison.OrdinalIgnoreCase))
        {
            return tag.Contains("nightly", StringComparison.OrdinalIgnoreCase)
                || tag.Contains("alpha", StringComparison.OrdinalIgnoreCase)
                || name.Contains("nightly", StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(channel, "Stable", StringComparison.OrdinalIgnoreCase))
        {
            return !prerelease
                && !HasPrereleaseTagSuffix(tag);
        }

        return true;
    }

    private static bool IsBetaReleaseTag(string tag)
    {
        var normalized = NormalizeVersionTag(tag);
        var dashIndex = normalized.IndexOf('-', StringComparison.Ordinal);
        if (dashIndex < 0 || dashIndex == normalized.Length - 1)
        {
            return false;
        }

        var prerelease = normalized[(dashIndex + 1)..];
        return prerelease.StartsWith("beta", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasPrereleaseTagSuffix(string tag)
    {
        return NormalizeVersionTag(tag).Contains('-', StringComparison.Ordinal);
    }

    private static string NormalizeVersionTag(string tag)
    {
        var normalized = (tag ?? string.Empty).Trim();
        return normalized.StartsWith('v') || normalized.StartsWith('V')
            ? normalized[1..]
            : normalized;
    }

    private static bool MatchesNormalizedChannel(string manifestChannel, string requestedChannel)
    {
        return string.Equals(
            NormalizeMaaApiChannel(manifestChannel),
            NormalizeMaaApiChannel(requestedChannel),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()?.Trim()
                : null;
    }

    private static long? TryGetInt64(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
            && property.TryGetInt64(out var value)
                ? value
                : null;
    }

    private async Task<MirrorChyanReleaseResolution> TryResolveReleaseFromMirrorChyanAsync(
        VersionUpdatePolicy policy,
        string currentVersion,
        HttpClient httpClient,
        CancellationToken cancellationToken,
        bool forceFullPackage)
    {
        if (!string.Equals(policy.ResourceUpdateSource, "MirrorChyan", StringComparison.OrdinalIgnoreCase))
        {
            return new MirrorChyanReleaseResolution(null, null);
        }

        var cdk = policy.MirrorChyanCdk.Trim();
        if (cdk.Length == 0)
        {
            return new MirrorChyanReleaseResolution(null, null);
        }

        var requestUrl =
            $"{MirrorChyanAppUpdateUrl}?current_version={Uri.EscapeDataString(currentVersion)}&cdk={Uri.EscapeDataString(cdk)}&user_agent=MAAUnified&os={Uri.EscapeDataString(BuildMirrorChyanOs())}&arch={Uri.EscapeDataString(BuildMirrorChyanArchitecture())}&channel={Uri.EscapeDataString(NormalizeMaaApiChannel(policy.VersionType))}&sp_id={Uri.EscapeDataString(BuildMirrorChyanSpId())}";

        try
        {
            using var document = await FetchJsonDocumentAsync(httpClient, requestUrl, cancellationToken).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new MirrorChyanReleaseResolution(null, null);
            }

            var root = document.RootElement;
            var code = root.TryGetProperty("code", out var codeNode) && codeNode.TryGetInt32(out var parsedCode)
                ? parsedCode
                : -1;
            var cdkExpiryUnixSeconds = root.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object
                ? TryGetInt64(data, "cdk_expired_time")
                : null;
            if (code != 0)
            {
                return new MirrorChyanReleaseResolution(null, cdkExpiryUnixSeconds);
            }

            if (!root.TryGetProperty("data", out data) || data.ValueKind != JsonValueKind.Object)
            {
                return new MirrorChyanReleaseResolution(null, cdkExpiryUnixSeconds);
            }

            var targetVersion = TryGetString(data, "version_name") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(targetVersion))
            {
                return new MirrorChyanReleaseResolution(null, cdkExpiryUnixSeconds);
            }

            if (!IsNewerVersion(targetVersion, currentVersion))
            {
                return new MirrorChyanReleaseResolution(
                    new VersionUpdateCheckResult(
                        Channel: policy.VersionType,
                        CurrentVersion: currentVersion,
                        TargetVersion: targetVersion,
                        ReleaseName: targetVersion,
                        Summary: string.Empty,
                        Body: string.Empty,
                        PackageName: null,
                        PackageDownloadUrl: null,
                        PackageSize: null,
                        IsNewVersion: false,
                        HasPackage: false,
                        MirrorChyanCdkExpiryUnixSeconds: cdkExpiryUnixSeconds),
                    cdkExpiryUnixSeconds);
            }

            var releaseNote = TryGetString(data, "release_note") ?? string.Empty;
            if (!Uri.TryCreate(TryGetString(data, "url"), UriKind.Absolute, out var downloadUrl))
            {
                return new MirrorChyanReleaseResolution(null, cdkExpiryUnixSeconds);
            }

            var packageName = Path.GetFileName(downloadUrl.AbsolutePath);
            if (string.IsNullOrWhiteSpace(packageName))
            {
                packageName = BuildFallbackPackageName(targetVersion);
            }

            if (!IsMaaUnifiedPackageName(packageName))
            {
                return new MirrorChyanReleaseResolution(null, cdkExpiryUnixSeconds);
            }

            if (forceFullPackage && !IsFullPackageForRepair(packageName))
            {
                // 修复场景拒绝 MirrorChyan 返回的 OTA，交由后续完整包来源兜底。
                return new MirrorChyanReleaseResolution(null, cdkExpiryUnixSeconds);
            }

            if (ScorePackageName(packageName) <= 0)
            {
                return new MirrorChyanReleaseResolution(null, cdkExpiryUnixSeconds);
            }

            return new MirrorChyanReleaseResolution(
                new VersionUpdateCheckResult(
                    Channel: policy.VersionType,
                    CurrentVersion: currentVersion,
                    TargetVersion: targetVersion,
                    ReleaseName: targetVersion,
                    Summary: string.IsNullOrWhiteSpace(releaseNote) ? targetVersion : releaseNote,
                    Body: releaseNote,
                    PackageName: packageName,
                    PackageDownloadUrl: downloadUrl,
                    PackageSize: null,
                    IsNewVersion: true,
                    HasPackage: true,
                    PreparedPackagePath: null,
                    PackageResolutionStatus: PackageResolutionStatus.Available,
                    PackageSourceKind: PackageSourceKind.ReleaseAsset,
                    MirrorChyanCdkExpiryUnixSeconds: cdkExpiryUnixSeconds),
                cdkExpiryUnixSeconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new MirrorChyanReleaseResolution(null, null);
        }
    }

    private static IReadOnlyList<Uri>? ReadMirrorUrls(JsonElement asset)
    {
        if (!asset.TryGetProperty("mirrors", out var mirrorsNode) || mirrorsNode.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var mirrors = new List<Uri>();
        foreach (var mirror in mirrorsNode.EnumerateArray())
        {
            if (mirror.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var raw = mirror.GetString();
            if (Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            {
                mirrors.Add(uri);
            }
        }

        return mirrors.Count == 0 ? null : mirrors;
    }

    private static IReadOnlyList<Uri> BuildPackageDownloadCandidates(VersionUpdateCheckResult checkResult)
    {
        var candidates = new List<Uri>();
        if (checkResult.PackageMirrorUrls is not null)
        {
            foreach (var mirror in checkResult.PackageMirrorUrls)
            {
                if (!candidates.Contains(mirror))
                {
                    candidates.Add(mirror);
                }
            }
        }

        if (!candidates.Contains(checkResult.PackageDownloadUrl!))
        {
            candidates.Add(checkResult.PackageDownloadUrl!);
        }

        return candidates;
    }

    private static async Task<UiOperationResult<string>> TryDownloadPackageFromCandidateAsync(
        HttpClient httpClient,
        Uri candidate,
        string destinationPath,
        IProgress<VersionUpdateProgressInfo>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, candidate);
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return UiOperationResult<string>.Fail(
                    UiErrorCode.UiOperationFailed,
                    $"Package download failed with HTTP {(int)response.StatusCode}.");
            }

            await using var sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var destinationStream = File.Create(destinationPath);
            await CopyToAsyncWithProgress(
                sourceStream,
                destinationStream,
                response.Content.Headers.ContentLength ?? 0,
                progress,
                cancellationToken).ConfigureAwait(false);
            return UiOperationResult<string>.Ok(destinationPath, "Update package downloaded.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return UiOperationResult<string>.Fail(
                UiErrorCode.UiOperationFailed,
                $"Failed to download update package: {ex.Message}");
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
            // Best-effort cleanup before retrying the next candidate.
        }
    }

    private static bool IsNewerVersion(string targetVersion, string currentVersion)
    {
        targetVersion = (targetVersion ?? string.Empty).Trim();
        currentVersion = (currentVersion ?? string.Empty).Trim();
        if (targetVersion.Length == 0 || currentVersion.Length == 0)
        {
            return targetVersion.Length > 0 && !string.Equals(targetVersion, currentVersion, StringComparison.OrdinalIgnoreCase);
        }

        if (TryParseComparableVersion(targetVersion, out var target)
            && TryParseComparableVersion(currentVersion, out var current))
        {
            return target.CompareTo(current) > 0;
        }

        return string.CompareOrdinal(currentVersion, targetVersion) < 0;
    }

    private static bool TryParseComparableVersion(string version, out ComparableVersion parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        var normalized = version.Trim();
        var plusIndex = normalized.IndexOf('+', StringComparison.Ordinal);
        if (plusIndex >= 0)
        {
            normalized = normalized[..plusIndex];
        }

        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[1..];
        }

        var dashIndex = normalized.IndexOf('-', StringComparison.Ordinal);
        var coreText = dashIndex >= 0 ? normalized[..dashIndex] : normalized;
        var prereleaseText = dashIndex >= 0 ? normalized[(dashIndex + 1)..] : string.Empty;
        var coreParts = coreText.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (coreParts.Length is < 2 or > 4)
        {
            return false;
        }

        Span<int> numbers = stackalloc int[4];
        for (var index = 0; index < coreParts.Length; index++)
        {
            if (!int.TryParse(coreParts[index], out numbers[index]))
            {
                return false;
            }
        }

        var prerelease = new List<ComparableVersionIdentifier>();
        if (prereleaseText.Length > 0)
        {
            foreach (var identifier in prereleaseText.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (long.TryParse(identifier, out var numeric))
                {
                    prerelease.Add(new ComparableVersionIdentifier(identifier, numeric, true));
                }
                else
                {
                    prerelease.Add(new ComparableVersionIdentifier(identifier, null, false));
                }
            }
        }

        parsed = new ComparableVersion(
            numbers[0],
            numbers[1],
            numbers[2],
            numbers[3],
            prerelease);
        return true;
    }

    private static bool ShouldSkipUpdateCheckForDebugVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        return System.Text.RegularExpressions.Regex.IsMatch(
            version.Trim(),
            @"^(.*DEBUG.*|v\d+(\.\d+){1,3}-\d+-g[0-9a-f]{6,}|[^v][0-9a-f]{6,})$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static string BuildMirrorChyanOs()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "win";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "mac";
        }

        return "linux";
    }

    private static string BuildMirrorChyanArchitecture()
    {
        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
        };
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

    private static PackageSelectionPlatform ResolvePlatform(OSPlatform? operatingSystem, Architecture? architecture)
    {
        var os = ResolveOperatingSystem(operatingSystem);
        var arch = NormalizeArchitecture((architecture ?? RuntimeInformation.OSArchitecture).ToString());

        return os switch
        {
            "windows" => new PackageSelectionPlatform(
                OperatingSystem: os,
                Architecture: arch,
                IsWindows: true,
                IsMacOS: false,
                ExactTokens: BuildExactTokens(os, "win", arch),
                OsTokens: ["windows", "win"],
                ArchitectureTokens: BuildArchitectureTokens(arch)),
            "linux" => new PackageSelectionPlatform(
                OperatingSystem: os,
                Architecture: arch,
                IsWindows: false,
                IsMacOS: false,
                ExactTokens: BuildExactTokens(os, os, arch),
                OsTokens: ["linux"],
                ArchitectureTokens: BuildArchitectureTokens(arch)),
            "macos" => new PackageSelectionPlatform(
                OperatingSystem: os,
                Architecture: arch,
                IsWindows: false,
                IsMacOS: true,
                ExactTokens: BuildExactTokens("macos", "osx", arch, "darwin", "mac"),
                OsTokens: ["macos", "osx", "darwin", "mac"],
                ArchitectureTokens: BuildArchitectureTokens(arch)),
            _ => new PackageSelectionPlatform(
                OperatingSystem: os,
                Architecture: arch,
                IsWindows: false,
                IsMacOS: false,
                ExactTokens: [],
                OsTokens: [os],
                ArchitectureTokens: BuildArchitectureTokens(arch)),
        };
    }

    private static string ResolveOperatingSystem(OSPlatform? operatingSystem)
    {
        if (operatingSystem.HasValue)
        {
            if (operatingSystem.Value == OSPlatform.Windows)
            {
                return "windows";
            }

            if (operatingSystem.Value == OSPlatform.Linux)
            {
                return "linux";
            }

            if (operatingSystem.Value == OSPlatform.OSX)
            {
                return "macos";
            }
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "windows";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return "linux";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "macos";
        }

        return "unknown";
    }

    private static string NormalizeOs(string os)
    {
        return os.Trim().ToLowerInvariant() switch
        {
            "win" => "windows",
            "windows" => "windows",
            "linux" => "linux",
            "osx" => "macos",
            "macos" => "macos",
            "darwin" => "macos",
            "mac" => "macos",
            var other => other,
        };
    }

    private static string NormalizeArchitecture(string architecture)
    {
        return architecture.Trim().ToLowerInvariant() switch
        {
            "amd64" => "x64",
            "x86_64" => "x64",
            "x64" => "x64",
            "arm64" => "arm64",
            "aarch64" => "arm64",
            var other => other,
        };
    }

    private static string[] BuildExactTokens(string os, string secondaryOs, string arch, params string[] extraOsTokens)
    {
        var osTokens = new List<string> { os, secondaryOs };
        osTokens.AddRange(extraOsTokens);
        var archTokens = BuildArchitectureTokens(arch);
        return osTokens
            .Where(static token => !string.IsNullOrWhiteSpace(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(token => archTokens.Select(archToken => $"{token}-{archToken}"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] BuildArchitectureTokens(string arch)
    {
        return arch switch
        {
            "x64" => ["x64", "amd64", "x86_64"],
            "arm64" => ["arm64", "aarch64"],
            _ => [arch],
        };
    }

    private string BuildFallbackPackageName(string targetVersion)
    {
        var extension = _platform.IsWindows ? ".zip" : _platform.IsMacOS ? ".dmg" : ".zip";
        return $"MAAUnified-{targetVersion}-{_platform.OperatingSystem}-{_platform.Architecture}{extension}";
    }

    private sealed record ResolvedPackage(
        PackageResolutionStatus Status,
        PackageSourceKind SourceKind,
        string? Name = null,
        Uri? DownloadUrl = null,
        long? Size = null,
        string? FailureMessageKey = null,
        IReadOnlyList<Uri>? MirrorUrls = null,
        int Score = 0);

    private sealed record ResolvedRelease(JsonElement Release);

    private sealed record ReleaseResolution(
        ResolvedRelease? Release,
        bool HasValidResponse);

    private sealed record MirrorChyanReleaseResolution(
        VersionUpdateCheckResult? Release,
        long? CdkExpiryUnixSeconds);

    private readonly record struct ComparableVersionIdentifier(
        string RawText,
        long? NumericValue,
        bool IsNumeric);

    private readonly record struct ComparableVersion(
        int Major,
        int Minor,
        int Patch,
        int Revision,
        IReadOnlyList<ComparableVersionIdentifier> PreRelease) : IComparable<ComparableVersion>
    {
        public int CompareTo(ComparableVersion other)
        {
            var majorCompare = Major.CompareTo(other.Major);
            if (majorCompare != 0)
            {
                return majorCompare;
            }

            var minorCompare = Minor.CompareTo(other.Minor);
            if (minorCompare != 0)
            {
                return minorCompare;
            }

            var patchCompare = Patch.CompareTo(other.Patch);
            if (patchCompare != 0)
            {
                return patchCompare;
            }

            var revisionCompare = Revision.CompareTo(other.Revision);
            if (revisionCompare != 0)
            {
                return revisionCompare;
            }

            var hasPreRelease = PreRelease.Count > 0;
            var otherHasPreRelease = other.PreRelease.Count > 0;
            if (hasPreRelease != otherHasPreRelease)
            {
                return hasPreRelease ? -1 : 1;
            }

            if (!hasPreRelease)
            {
                return 0;
            }

            var count = Math.Min(PreRelease.Count, other.PreRelease.Count);
            for (var index = 0; index < count; index++)
            {
                var identifierCompare = CompareIdentifier(PreRelease[index], other.PreRelease[index]);
                if (identifierCompare != 0)
                {
                    return identifierCompare;
                }
            }

            return PreRelease.Count.CompareTo(other.PreRelease.Count);
        }

        private static int CompareIdentifier(
            ComparableVersionIdentifier left,
            ComparableVersionIdentifier right)
        {
            if (left.IsNumeric && right.IsNumeric)
            {
                return Nullable.Compare(left.NumericValue, right.NumericValue);
            }

            if (left.IsNumeric != right.IsNumeric)
            {
                return left.IsNumeric ? -1 : 1;
            }

            return string.CompareOrdinal(left.RawText, right.RawText);
        }
    }

    private readonly record struct PackageSelectionPlatform(
        string OperatingSystem,
        string Architecture,
        bool IsWindows,
        bool IsMacOS,
        IReadOnlyList<string> ExactTokens,
        IReadOnlyList<string> OsTokens,
        IReadOnlyList<string> ArchitectureTokens);
}

public static class VersionUpdateHttpClientFactory
{
    public static HttpClient Create(VersionUpdatePolicy? policy, TimeSpan timeout)
    {
        HttpMessageHandler handler = new HttpClientHandler();
        if (policy is not null && TryBuildProxy(policy, out var proxy) && handler is HttpClientHandler proxyHandler)
        {
            proxyHandler.UseProxy = true;
            proxyHandler.Proxy = proxy;
        }

        handler = GitHubApiAuthorizationHandler.Wrap(handler, policy);
        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = timeout,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MAAUnified-VersionUpdate/1.0");
        return client;
    }

    private static bool TryBuildProxy(VersionUpdatePolicy policy, out IWebProxy? proxy)
    {
        proxy = null;
        var rawProxy = policy.Proxy?.Trim();
        if (string.IsNullOrWhiteSpace(rawProxy))
        {
            return false;
        }

        if (string.Equals(policy.ProxyType, "system", StringComparison.OrdinalIgnoreCase))
        {
            proxy = WebRequest.DefaultWebProxy;
            return proxy is not null;
        }

        var candidate = rawProxy.Contains("://", StringComparison.Ordinal)
            ? rawProxy
            : $"{policy.ProxyType}://{rawProxy}";
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var proxyUri))
        {
            return false;
        }

        proxy = new WebProxy(proxyUri);
        return true;
    }
}

/// <summary>
/// GitHub API 凭据附带处理器（WPF <c>53870a7a52</c>）。只在 <see cref="GitHubApiRequestAuthorizer.IsEligibleUri"/>
/// 通过时附带 <c>Authorization</c>，其余请求（含下载链接、镜像站、自定义 ResourceApi）保持匿名。
/// </summary>
internal sealed class GitHubApiAuthorizationHandler : DelegatingHandler
{
    private readonly string? _token;
    private readonly DateTimeOffset? _expiresAt;

    internal GitHubApiAuthorizationHandler(HttpMessageHandler innerHandler, string? token, DateTimeOffset? expiresAt)
        : base(innerHandler)
    {
        _token = token;
        _expiresAt = expiresAt;
    }

    internal static HttpMessageHandler Wrap(HttpMessageHandler innerHandler, VersionUpdatePolicy? policy)
    {
        if (policy is null || string.IsNullOrWhiteSpace(policy.GitHubToken))
        {
            return innerHandler;
        }

        return new GitHubApiAuthorizationHandler(
            innerHandler,
            policy.GitHubToken,
            GitHubApiRequestAuthorizer.TryParseExpiration(policy.GitHubTokenExpiresAt));
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        GitHubApiRequestAuthorizer.TryApplyAuthorization(request, _token, _expiresAt);
        return base.SendAsync(request, cancellationToken);
    }
}
