using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using MAAUnified.Application.Services.VersionUpdate;
using MAAUnified.Compat.Constants;
using Xunit;

namespace MAAUnified.Tests;

public sealed class VersionUpdateFeatureServiceTests
{
    [Fact]
    public async Task CheckForUpdatesAsync_ReadsLocalReleaseFeed()
    {
        var service = new VersionUpdateFeatureService();
        var feedPath = Path.Combine(Path.GetTempPath(), $"maa-unified-release-feed-{Guid.NewGuid():N}.json");

        try
        {
            await File.WriteAllTextAsync(feedPath, JsonSerializer.Serialize(new[]
            {
                new
                {
                    tag_name = "v2.0.0",
                    name = "Release v2.0.0",
                    body = "Line one.\nLine two.",
                    prerelease = false,
                    assets = new[]
                    {
                        new
                        {
                            name = "MAAUnified-v2.0.0-linux-x64.tar.gz",
                            browser_download_url = "https://example.com/MAAUnified-v2.0.0-linux-x64.tar.gz",
                            size = 1234,
                        },
                    },
                },
            }));

            var policy = VersionUpdatePolicy.Default with
            {
                ResourceApi = feedPath,
                VersionType = "Stable",
                AutoDownloadUpdatePackage = false,
            };

            var result = await service.CheckForUpdatesAsync(policy, "v1.0.0");

            Assert.True(result.Success);
            Assert.NotNull(result.Value);
            Assert.Equal("v2.0.0", result.Value!.TargetVersion);
            Assert.Equal("Release v2.0.0", result.Value.ReleaseName);
            Assert.Equal("Line one.\nLine two.", result.Value.Body);
            Assert.Equal("MAAUnified-v2.0.0-linux-x64.tar.gz", result.Value.PackageName);
            Assert.True(result.Value.IsNewVersion);
            Assert.True(result.Value.HasPackage);
            Assert.Equal(PackageResolutionStatus.Available, result.Value.PackageResolutionStatus);
            Assert.Equal(PackageSourceKind.ReleaseAsset, result.Value.PackageSourceKind);
        }
        finally
        {
            try
            {
                if (File.Exists(feedPath))
                {
                    File.Delete(feedPath);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    [Fact]
    public async Task CheckForUpdatesAsync_WhenAutoDownloadEnabled_PreparesPackagePath()
    {
        var root = Path.Combine(Path.GetTempPath(), $"maa-unified-version-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var feedPath = Path.Combine(root, "release-feed.json");
        try
        {
            await File.WriteAllTextAsync(feedPath, JsonSerializer.Serialize(new[]
            {
                new
                {
                    tag_name = "v2.0.0",
                    name = "Release v2.0.0",
                    body = "Line one.\nLine two.",
                    prerelease = false,
                    assets = new[]
                    {
                        new
                        {
                            name = "MAAUnified-v2.0.0-linux-x64.tar.gz",
                            browser_download_url = "https://example.com/MAAUnified-v2.0.0-linux-x64.tar.gz",
                            size = 1234,
                        },
                    },
                },
            }));

            using var httpClient = new HttpClient(new StubHttpMessageHandler(static request =>
            {
                if (request.RequestUri?.AbsoluteUri == "https://example.com/MAAUnified-v2.0.0-linux-x64.tar.gz")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent([0x1F, 0x8B, 0x08, 0x00, 0x00]),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }));
            var workflow = new AppUpdateWorkflowService(new NoOpAppLifecycleService(), httpClient);
            var service = new VersionUpdateFeatureService(
                CreateConfigurationService(root),
                appUpdateWorkflowService: workflow,
                runtimeBaseDirectory: root);

            var policy = VersionUpdatePolicy.Default with
            {
                ResourceApi = feedPath,
                VersionType = "Stable",
                AutoDownloadUpdatePackage = true,
            };

            var result = await service.CheckForUpdatesAsync(policy, "v1.0.0");

            Assert.True(result.Success);
            Assert.NotNull(result.Value);
            Assert.False(string.IsNullOrWhiteSpace(result.Value!.PreparedPackagePath));
            Assert.True(File.Exists(result.Value.PreparedPackagePath));
            Assert.Contains("已准备更新包", result.Message, StringComparison.Ordinal);
            Assert.Equal(PackageResolutionStatus.Available, result.Value.PackageResolutionStatus);
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    [Fact]
    public async Task CheckForUpdatesAsync_WhenLegacyResourceApiTlsFails_FallsBackToOfficialMaaApi()
    {
        const string legacyBaseUrl = "https://maa-ota.annangela.cn/MaaAssistantArknights/MaaAssistantArknights/";
        const string officialBaseUrl = "https://api.maa.plus/MaaAssistantArknights/api/";
        var root = Path.Combine(Path.GetTempPath(), $"maa-unified-version-update-fallback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            using var httpClient = new HttpClient(new StubHttpMessageHandler(static request =>
            {
                var url = request.RequestUri?.AbsoluteUri ?? string.Empty;
                if (string.Equals(url, $"{legacyBaseUrl}version/summary.json", StringComparison.OrdinalIgnoreCase))
                {
                    throw new HttpRequestException("The SSL connection could not be established, see inner exception.");
                }

                if (string.Equals(url, $"{officialBaseUrl}version/summary.json", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""
                            {
                              "stable": {
                                "version": "v6.7.0",
                                "detail": "https://api.maa.plus/MaaAssistantArknights/api/version/stable.json"
                              }
                            }
                            """),
                    };
                }

                if (string.Equals(url, $"{officialBaseUrl}version/stable.json", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""
                            {
                              "version": "v6.7.0",
                              "details": {
                                "tag_name": "v6.7.0",
                                "name": "v6.7.0",
                                "body": "Fallback body.",
                                "prerelease": false,
                                "assets": [
                                  {
                                    "name": "MAAUnified-v6.7.0-linux-x64.tar.gz",
                                    "browser_download_url": "https://example.com/MAAUnified-v6.7.0-linux-x64.tar.gz",
                                    "size": 4321
                                  }
                                ]
                              }
                            }
                            """),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }));
            var workflow = new AppUpdateWorkflowService(new NoOpAppLifecycleService(), httpClient);
            var service = new VersionUpdateFeatureService(
                CreateConfigurationService(root),
                appUpdateWorkflowService: workflow,
                runtimeBaseDirectory: root);

            var policy = VersionUpdatePolicy.Default with
            {
                ResourceApi = legacyBaseUrl,
                VersionType = "Stable",
            };

            var result = await service.CheckForUpdatesAsync(policy, "v6.6.0");

            Assert.True(result.Success);
            Assert.NotNull(result.Value);
            Assert.Equal("v6.7.0", result.Value!.TargetVersion);
            Assert.Equal("v6.7.0", result.Value.ReleaseName);
            Assert.Equal("MAAUnified-v6.7.0-linux-x64.tar.gz", result.Value.PackageName);
            Assert.True(result.Value.IsNewVersion);
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    [Fact]
    public async Task CheckForUpdatesAsync_OnWindowsRelayManifest_UsesExactArchitecturePackage()
    {
        var root = Path.Combine(Path.GetTempPath(), $"maa-unified-windows-relay-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var feedPath = Path.Combine(root, "release-feed.json");
        var relayManifestPath = Path.Combine(root, "windows-relay.json");

        try
        {
            await File.WriteAllTextAsync(feedPath, JsonSerializer.Serialize(new[]
            {
                new
                {
                    tag_name = "v2.0.0",
                    name = "Release v2.0.0",
                    body = "Body",
                    prerelease = false,
                    assets = Array.Empty<object>(),
                },
            }));
            await File.WriteAllTextAsync(relayManifestPath, """
                {
                  "version": "v2.0.0",
                  "channel": "Stable",
                  "packages": [
                    {
                      "os": "windows",
                      "arch": "x64",
                      "url": "https://example.com/MAAUnified-v2.0.0-win-x64.zip",
                      "sha256": "abc",
                      "size": 2048,
                      "name": "MAAUnified-v2.0.0-win-x64.zip"
                    }
                  ]
                }
                """);

            var workflow = new AppUpdateWorkflowService(
                new NoOpAppLifecycleService(),
                operatingSystem: OSPlatform.Windows,
                architecture: Architecture.X64);
            var service = new VersionUpdateFeatureService(
                CreateConfigurationService(root),
                appUpdateWorkflowService: workflow,
                runtimeBaseDirectory: root);

            var result = await service.CheckForUpdatesAsync(
                VersionUpdatePolicy.Default with
                {
                    ResourceApi = feedPath,
                    AutoDownloadUpdatePackage = false,
                },
                "v1.0.0");

            Assert.True(result.Success);
            Assert.NotNull(result.Value);
            Assert.True(result.Value!.HasPackage);
            Assert.Equal(PackageResolutionStatus.Available, result.Value.PackageResolutionStatus);
            Assert.Equal(PackageSourceKind.WindowsRelayManifest, result.Value.PackageSourceKind);
            Assert.Equal("MAAUnified-v2.0.0-win-x64.zip", result.Value.PackageName);
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task CheckForUpdatesAsync_OnWindowsRelayManifestMiss_ReturnsManualUpdateRequired()
    {
        var root = Path.Combine(Path.GetTempPath(), $"maa-unified-windows-relay-miss-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var feedPath = Path.Combine(root, "release-feed.json");
        var relayManifestPath = Path.Combine(root, "windows-relay.json");

        try
        {
            await File.WriteAllTextAsync(feedPath, JsonSerializer.Serialize(new[]
            {
                new
                {
                    tag_name = "v2.0.0",
                    name = "Release v2.0.0",
                    body = "Body",
                    prerelease = false,
                    assets = Array.Empty<object>(),
                },
            }));
            await File.WriteAllTextAsync(relayManifestPath, """
                {
                  "version": "v2.0.0",
                  "channel": "Stable",
                  "packages": [
                    {
                      "os": "windows",
                      "arch": "x64",
                      "url": "https://example.com/MAAUnified-v2.0.0-win-x64.zip"
                    }
                  ]
                }
                """);

            var workflow = new AppUpdateWorkflowService(
                new NoOpAppLifecycleService(),
                operatingSystem: OSPlatform.Windows,
                architecture: Architecture.Arm64);
            var service = new VersionUpdateFeatureService(
                CreateConfigurationService(root),
                appUpdateWorkflowService: workflow,
                runtimeBaseDirectory: root);

            var result = await service.CheckForUpdatesAsync(
                VersionUpdatePolicy.Default with
                {
                    ResourceApi = feedPath,
                    AutoDownloadUpdatePackage = false,
                },
                "v1.0.0");

            Assert.True(result.Success);
            Assert.NotNull(result.Value);
            Assert.False(result.Value!.HasPackage);
            Assert.Equal(PackageResolutionStatus.WindowsManualUpdateRequired, result.Value.PackageResolutionStatus);
            Assert.Contains("手动更新", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task CheckForUpdatesAsync_OnWindowsDownload404_ReturnsManualUpdateRequired()
    {
        var root = Path.Combine(Path.GetTempPath(), $"maa-unified-windows-download-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var feedPath = Path.Combine(root, "release-feed.json");
        var relayManifestPath = Path.Combine(root, "windows-relay.json");

        try
        {
            await File.WriteAllTextAsync(feedPath, JsonSerializer.Serialize(new[]
            {
                new
                {
                    tag_name = "v2.0.0",
                    name = "Release v2.0.0",
                    body = "Body",
                    prerelease = false,
                    assets = Array.Empty<object>(),
                },
            }));
            await File.WriteAllTextAsync(relayManifestPath, """
                {
                  "version": "v2.0.0",
                  "channel": "Stable",
                  "packages": [
                    {
                      "os": "windows",
                      "arch": "x64",
                      "url": "https://example.com/MAAUnified-v2.0.0-win-x64.zip",
                      "name": "MAAUnified-v2.0.0-win-x64.zip"
                    }
                  ]
                }
                """);

            using var httpClient = new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
            var workflow = new AppUpdateWorkflowService(
                new NoOpAppLifecycleService(),
                httpClient,
                OSPlatform.Windows,
                Architecture.X64);
            var service = new VersionUpdateFeatureService(
                CreateConfigurationService(root),
                appUpdateWorkflowService: workflow,
                runtimeBaseDirectory: root);

            var result = await service.CheckForUpdatesAsync(
                VersionUpdatePolicy.Default with
                {
                    ResourceApi = feedPath,
                    AutoDownloadUpdatePackage = true,
                },
                "v1.0.0");

            Assert.True(result.Success);
            Assert.NotNull(result.Value);
            Assert.Equal(PackageResolutionStatus.WindowsManualUpdateRequired, result.Value!.PackageResolutionStatus);
            Assert.Contains("手动更新", result.Message, StringComparison.Ordinal);
            Assert.True(string.IsNullOrWhiteSpace(result.Value.PreparedPackagePath));
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task CheckForUpdatesAsync_OnLinuxWithoutMatchingPackage_ReturnsUnavailable()
    {
        var root = Path.Combine(Path.GetTempPath(), $"maa-unified-linux-unavailable-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var feedPath = Path.Combine(root, "release-feed.json");

        try
        {
            await File.WriteAllTextAsync(feedPath, JsonSerializer.Serialize(new[]
            {
                new
                {
                    tag_name = "v2.0.0",
                    name = "Release v2.0.0",
                    body = "Body",
                    prerelease = false,
                    assets = new[]
                    {
                        new
                        {
                            name = "MAAUnified-v2.0.0-win-x64.zip",
                            browser_download_url = "https://example.com/MAAUnified-v2.0.0-win-x64.zip",
                            size = 1024,
                        },
                    },
                },
            }));

            var workflow = new AppUpdateWorkflowService(
                new NoOpAppLifecycleService(),
                operatingSystem: OSPlatform.Linux,
                architecture: Architecture.X64);
            var service = new VersionUpdateFeatureService(
                CreateConfigurationService(root),
                appUpdateWorkflowService: workflow,
                runtimeBaseDirectory: root);

            var result = await service.CheckForUpdatesAsync(
                VersionUpdatePolicy.Default with
                {
                    ResourceApi = feedPath,
                    AutoDownloadUpdatePackage = false,
                },
                "v1.0.0");

            Assert.True(result.Success);
            Assert.NotNull(result.Value);
            Assert.Equal(PackageResolutionStatus.Unavailable, result.Value!.PackageResolutionStatus);
            Assert.Contains("更新失败", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task CheckForUpdatesAsync_OnMacOSReleaseAssets_SelectsCurrentArchitectureDmg()
    {
        var root = Path.Combine(Path.GetTempPath(), $"maa-unified-macos-release-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var feedPath = Path.Combine(root, "release-feed.json");

        try
        {
            await File.WriteAllTextAsync(feedPath, JsonSerializer.Serialize(new[]
            {
                new
                {
                    tag_name = "v2.0.0",
                    name = "Release v2.0.0",
                    body = "Body",
                    prerelease = false,
                    assets = new[]
                    {
                        new
                        {
                            name = "MAAUnified-v2.0.0-macos-x64.dmg",
                            browser_download_url = "https://example.com/MAAUnified-v2.0.0-macos-x64.dmg",
                            size = 100,
                        },
                        new
                        {
                            name = "MAAUnified-v2.0.0-macos-arm64.dmg",
                            browser_download_url = "https://example.com/MAAUnified-v2.0.0-macos-arm64.dmg",
                            size = 200,
                        },
                        new
                        {
                            name = "MAAUnified-v2.0.0-macos-arm64.tar.gz",
                            browser_download_url = "https://example.com/MAAUnified-v2.0.0-macos-arm64.tar.gz",
                            size = 300,
                        },
                    },
                },
            }));

            var workflow = new AppUpdateWorkflowService(
                new NoOpAppLifecycleService(),
                operatingSystem: OSPlatform.OSX,
                architecture: Architecture.Arm64);

            var result = await workflow.CheckForUpdatesAsync(
                VersionUpdatePolicy.Default with
                {
                    ResourceApi = feedPath,
                    AutoDownloadUpdatePackage = false,
                },
                "v1.0.0",
                CancellationToken.None);

            Assert.True(result.HasPackage);
            Assert.Equal("MAAUnified-v2.0.0-macos-arm64.dmg", result.PackageName);
            Assert.Equal(new Uri("https://example.com/MAAUnified-v2.0.0-macos-arm64.dmg"), result.PackageDownloadUrl);
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task CheckForUpdatesAsync_OnMacOSAutoDownload_DownloadsDmgButRequiresManualInstall()
    {
        var root = Path.Combine(Path.GetTempPath(), $"maa-unified-macos-dmg-download-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var feedPath = Path.Combine(root, "release-feed.json");

        try
        {
            await File.WriteAllTextAsync(feedPath, JsonSerializer.Serialize(new[]
            {
                new
                {
                    tag_name = "v2.0.0",
                    name = "Release v2.0.0",
                    body = "Body",
                    prerelease = false,
                    assets = new[]
                    {
                        new
                        {
                            name = "MAAUnified-v2.0.0-macos-x64.dmg",
                            browser_download_url = "https://example.com/MAAUnified-v2.0.0-macos-x64.dmg",
                            size = 3,
                        },
                    },
                },
            }));

            using var httpClient = new HttpClient(new StubHttpMessageHandler(static request =>
            {
                if (request.RequestUri?.AbsoluteUri == "https://example.com/MAAUnified-v2.0.0-macos-x64.dmg")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent([1, 2, 3]),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }));
            var workflow = new AppUpdateWorkflowService(
                new NoOpAppLifecycleService(),
                httpClient,
                OSPlatform.OSX,
                Architecture.X64);
            var service = new VersionUpdateFeatureService(
                CreateConfigurationService(root),
                appUpdateWorkflowService: workflow,
                runtimeBaseDirectory: root);

            var result = await service.CheckForUpdatesAsync(
                VersionUpdatePolicy.Default with
                {
                    ResourceApi = feedPath,
                    AutoDownloadUpdatePackage = true,
                },
                "v1.0.0");

            Assert.True(result.Success);
            Assert.NotNull(result.Value);
            Assert.Equal(PackageResolutionStatus.MacOSManualInstallRequired, result.Value!.PackageResolutionStatus);
            Assert.EndsWith(".dmg", result.Value.PreparedPackagePath, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(result.Value.PreparedPackagePath));
            Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(result.Value.PreparedPackagePath!));
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task CheckForUpdatesAsync_WhenCurrentVersionIsNewer_DoesNotReportUpdate()
    {
        var root = Path.Combine(Path.GetTempPath(), $"maa-unified-version-compare-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var feedPath = Path.Combine(root, "release-feed.json");

        try
        {
            await File.WriteAllTextAsync(feedPath, JsonSerializer.Serialize(new[]
            {
                new
                {
                    tag_name = "v2.0.0",
                    name = "Release v2.0.0",
                    body = "Body",
                    prerelease = false,
                    assets = new[]
                    {
                        new
                        {
                            name = "MAAUnified-v2.0.0-linux-x64.tar.gz",
                            browser_download_url = "https://example.com/MAAUnified-v2.0.0-linux-x64.tar.gz",
                            size = 1024,
                        },
                    },
                },
            }));

            var workflow = new AppUpdateWorkflowService(
                new NoOpAppLifecycleService(),
                operatingSystem: OSPlatform.Linux,
                architecture: Architecture.X64);
            var service = new VersionUpdateFeatureService(
                CreateConfigurationService(root),
                appUpdateWorkflowService: workflow,
                runtimeBaseDirectory: root);

            var result = await service.CheckForUpdatesAsync(
                VersionUpdatePolicy.Default with
                {
                    ResourceApi = feedPath,
                    AutoDownloadUpdatePackage = false,
                },
                "v9.9.9");

            Assert.True(result.Success);
            Assert.NotNull(result.Value);
            Assert.False(result.Value!.IsNewVersion);
            Assert.Equal("v2.0.0", result.Value.TargetVersion);
            Assert.Contains("当前已是最新", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task CheckForUpdatesAsync_WithMirrorChyanSource_UsesMirrorChyanEndpoint()
    {
        var root = Path.Combine(Path.GetTempPath(), $"maa-unified-mirrorchyan-app-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            using var httpClient = new HttpClient(new StubHttpMessageHandler(static request =>
            {
                var url = request.RequestUri?.AbsoluteUri ?? string.Empty;
                if (url.StartsWith("https://mirrorchyan.com/api/resources/MAA/latest", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""
                            {
                              "code": 0,
                              "data": {
                                "version_name": "v2.0.0",
                                "release_note": "MirrorChyan note",
                                "url": "https://mirror.example.com/MAAUnified-v2.0.0-linux-x64.tar.gz"
                              }
                            }
                            """),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }));
            var workflow = new AppUpdateWorkflowService(
                new NoOpAppLifecycleService(),
                httpClient,
                OSPlatform.Linux,
                Architecture.X64);
            var service = new VersionUpdateFeatureService(
                CreateConfigurationService(root),
                appUpdateWorkflowService: workflow,
                runtimeBaseDirectory: root);

            var result = await service.CheckForUpdatesAsync(
                VersionUpdatePolicy.Default with
                {
                    ResourceUpdateSource = "MirrorChyan",
                    MirrorChyanCdk = "test-cdk",
                    AutoDownloadUpdatePackage = false,
                },
                "v1.0.0");

            Assert.True(result.Success);
            Assert.NotNull(result.Value);
            Assert.True(result.Value!.IsNewVersion);
            Assert.True(result.Value.HasPackage);
            Assert.Equal("v2.0.0", result.Value.TargetVersion);
            Assert.Equal(new Uri("https://mirror.example.com/MAAUnified-v2.0.0-linux-x64.tar.gz"), result.Value.PackageDownloadUrl);
            Assert.Equal(PackageResolutionStatus.Available, result.Value.PackageResolutionStatus);
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task CheckForUpdatesAsync_WithGithubMirrorsAndGlobalSourceDisabled_DownloadsFromMirror()
    {
        var root = Path.Combine(Path.GetTempPath(), $"maa-unified-github-mirror-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var feedPath = Path.Combine(root, "release-feed.json");

        try
        {
            await File.WriteAllTextAsync(feedPath, """
                [
                  {
                    "tag_name": "v2.0.0",
                    "name": "Release v2.0.0",
                    "body": "Body",
                    "prerelease": false,
                    "assets": [
                      {
                        "name": "MAAUnified-v2.0.0-linux-x64.tar.gz",
                        "browser_download_url": "https://global.example.com/MAAUnified-v2.0.0-linux-x64.tar.gz",
                        "mirrors": [
                          "https://mirror.example.com/MAAUnified-v2.0.0-linux-x64.tar.gz"
                        ],
                        "size": 5
                      }
                    ]
                  }
                ]
                """);

            using var httpClient = new HttpClient(new StubHttpMessageHandler(static request =>
            {
                var url = request.RequestUri?.AbsoluteUri ?? string.Empty;
                return url switch
                {
                    "https://global.example.com/MAAUnified-v2.0.0-linux-x64.tar.gz" => new HttpResponseMessage(HttpStatusCode.NotFound),
                    "https://mirror.example.com/MAAUnified-v2.0.0-linux-x64.tar.gz" => new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent([1, 2, 3, 4, 5]),
                    },
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound),
                };
            }));
            var workflow = new AppUpdateWorkflowService(
                new NoOpAppLifecycleService(),
                httpClient,
                OSPlatform.Linux,
                Architecture.X64);
            var service = new VersionUpdateFeatureService(
                CreateConfigurationService(root),
                appUpdateWorkflowService: workflow,
                runtimeBaseDirectory: root);

            var result = await service.CheckForUpdatesAsync(
                VersionUpdatePolicy.Default with
                {
                    ResourceApi = feedPath,
                    ResourceUpdateSource = "Github",
                    ForceGithubGlobalSource = false,
                    AutoDownloadUpdatePackage = true,
                },
                "v1.0.0");

            Assert.True(result.Success);
            Assert.NotNull(result.Value);
            Assert.False(string.IsNullOrWhiteSpace(result.Value!.PreparedPackagePath));
            Assert.True(File.Exists(result.Value.PreparedPackagePath));
            Assert.Equal([1, 2, 3, 4, 5], await File.ReadAllBytesAsync(result.Value.PreparedPackagePath));
            Assert.Contains("已准备更新包", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task CheckForUpdatesAsync_WithGithubMirrorsAndGlobalSourceForced_IgnoresMirrorUrls()
    {
        var root = Path.Combine(Path.GetTempPath(), $"maa-unified-github-global-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var feedPath = Path.Combine(root, "release-feed.json");

        try
        {
            await File.WriteAllTextAsync(feedPath, """
                [
                  {
                    "tag_name": "v2.0.0",
                    "name": "Release v2.0.0",
                    "body": "Body",
                    "prerelease": false,
                    "assets": [
                      {
                        "name": "MAAUnified-v2.0.0-linux-x64.tar.gz",
                        "browser_download_url": "https://global.example.com/MAAUnified-v2.0.0-linux-x64.tar.gz",
                        "mirrors": [
                          "https://mirror.example.com/MAAUnified-v2.0.0-linux-x64.tar.gz"
                        ],
                        "size": 5
                      }
                    ]
                  }
                ]
                """);

            using var httpClient = new HttpClient(new StubHttpMessageHandler(static request =>
            {
                var url = request.RequestUri?.AbsoluteUri ?? string.Empty;
                return url switch
                {
                    "https://global.example.com/MAAUnified-v2.0.0-linux-x64.tar.gz" => new HttpResponseMessage(HttpStatusCode.NotFound),
                    "https://mirror.example.com/MAAUnified-v2.0.0-linux-x64.tar.gz" => new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent([1, 2, 3, 4, 5]),
                    },
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound),
                };
            }));
            var workflow = new AppUpdateWorkflowService(
                new NoOpAppLifecycleService(),
                httpClient,
                OSPlatform.Linux,
                Architecture.X64);
            var service = new VersionUpdateFeatureService(
                CreateConfigurationService(root),
                appUpdateWorkflowService: workflow,
                runtimeBaseDirectory: root);

            var result = await service.CheckForUpdatesAsync(
                VersionUpdatePolicy.Default with
                {
                    ResourceApi = feedPath,
                    ResourceUpdateSource = "Github",
                    ForceGithubGlobalSource = true,
                    AutoDownloadUpdatePackage = true,
                },
                "v1.0.0");

            Assert.True(result.Success);
            Assert.NotNull(result.Value);
            Assert.True(string.IsNullOrWhiteSpace(result.Value!.PreparedPackagePath));
            Assert.Equal(PackageResolutionStatus.DownloadFailed, result.Value.PackageResolutionStatus);
            Assert.Contains("更新失败", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task TryApplyPendingUpdatePackage_AppliesZipAndMarksFirstBoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"maa-unified-pending-update-{Guid.NewGuid():N}");
        var configDir = Path.Combine(root, "config");
        var packageDir = Path.Combine(root, "update-packages");
        Directory.CreateDirectory(configDir);
        Directory.CreateDirectory(packageDir);

        var currentFile = Path.Combine(root, "app.txt");
        await File.WriteAllTextAsync(currentFile, "old");

        var packagePath = Path.Combine(packageDir, "update.zip");
        using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("app.txt");
            await using var stream = entry.Open();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync("new");
        }

        var config = new UnifiedConfig();
        config.GlobalValues[ConfigurationKeys.VersionName] = JsonValue.Create("v2.0.0");
        config.GlobalValues[ConfigurationKeys.VersionUpdatePackage] = JsonValue.Create(Path.Combine("update-packages", "update.zip"));
        var store = new AvaloniaJsonConfigStore(root);
        await store.SaveAsync(config);

        var result = PendingAppUpdateService.TryApplyPendingUpdatePackage(root);
        var reloaded = await store.LoadAsync();

        Assert.Equal(PendingAppUpdateStatus.Applied, result.Status);
        Assert.Equal("new", await File.ReadAllTextAsync(currentFile));
        Assert.False(File.Exists(packagePath));
        Assert.NotNull(reloaded);
        Assert.Equal(string.Empty, ReadGlobalString(reloaded!, ConfigurationKeys.VersionUpdatePackage));
        Assert.Equal(bool.TrueString, ReadGlobalString(reloaded!, ConfigurationKeys.VersionUpdateIsFirstBoot));

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public async Task TryApplyPendingUpdatePackage_AppliesTarGzAndMarksFirstBoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"maa-unified-pending-update-targz-{Guid.NewGuid():N}");
        var configDir = Path.Combine(root, "config");
        var packageDir = Path.Combine(root, "update-packages");
        Directory.CreateDirectory(configDir);
        Directory.CreateDirectory(packageDir);

        var currentFile = Path.Combine(root, "app.txt");
        await File.WriteAllTextAsync(currentFile, "old");

        var packagePath = Path.Combine(packageDir, "update.tar.gz");
        await CreateTarGzPackageAsync(packagePath, ("app.txt", "new"));

        var config = new UnifiedConfig();
        config.GlobalValues[ConfigurationKeys.VersionName] = JsonValue.Create("v2.0.0");
        config.GlobalValues[ConfigurationKeys.VersionUpdatePackage] = JsonValue.Create(Path.Combine("update-packages", "update.tar.gz"));
        var store = new AvaloniaJsonConfigStore(root);
        await store.SaveAsync(config);

        var result = PendingAppUpdateService.TryApplyPendingUpdatePackage(root);
        var reloaded = await store.LoadAsync();

        Assert.Equal(PendingAppUpdateStatus.Applied, result.Status);
        Assert.Equal("new", await File.ReadAllTextAsync(currentFile));
        Assert.False(File.Exists(packagePath));
        Assert.NotNull(reloaded);
        Assert.Equal(string.Empty, ReadGlobalString(reloaded!, ConfigurationKeys.VersionUpdatePackage));
        Assert.Equal(bool.TrueString, ReadGlobalString(reloaded!, ConfigurationKeys.VersionUpdateIsFirstBoot));

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task TryApplyPendingUpdatePackage_WhenPackageIsDmg_ClearsPendingStateWithoutDeletingPackage()
    {
        var root = Path.Combine(Path.GetTempPath(), $"maa-unified-pending-update-dmg-{Guid.NewGuid():N}");
        var configDir = Path.Combine(root, "config");
        var packageDir = Path.Combine(root, "update-packages");
        Directory.CreateDirectory(configDir);
        Directory.CreateDirectory(packageDir);

        var packagePath = Path.Combine(packageDir, "update.dmg");
        await File.WriteAllBytesAsync(packagePath, [1, 2, 3]);

        var config = new UnifiedConfig();
        config.GlobalValues[ConfigurationKeys.VersionName] = JsonValue.Create("v2.0.0");
        config.GlobalValues[ConfigurationKeys.VersionUpdatePackage] = JsonValue.Create(Path.Combine("update-packages", "update.dmg"));
        var store = new AvaloniaJsonConfigStore(root);
        await store.SaveAsync(config);

        var result = PendingAppUpdateService.TryApplyPendingUpdatePackage(root);
        var reloaded = await store.LoadAsync();

        Assert.Equal(PendingAppUpdateStatus.Failed, result.Status);
        Assert.True(File.Exists(packagePath));
        Assert.NotNull(reloaded);
        Assert.Equal(string.Empty, ReadGlobalString(reloaded!, ConfigurationKeys.VersionUpdatePackage));

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch
        {
        }
    }

    private static UnifiedConfigurationService CreateConfigurationService(string root)
    {
        return new UnifiedConfigurationService(
            new AvaloniaJsonConfigStore(root),
            new GuiNewJsonConfigImporter(),
            new GuiJsonConfigImporter(),
            new UiLogService(),
            root);
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

    private static async Task CreateTarGzPackageAsync(string packagePath, params (string RelativePath, string Content)[] files)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"maa-unified-targz-source-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            foreach (var (relativePath, content) in files)
            {
                var fullPath = Path.Combine(tempRoot, relativePath);
                var directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                await File.WriteAllTextAsync(fullPath, content);
            }

            await using var output = File.Create(packagePath);
            await using var gzip = new GZipStream(output, CompressionLevel.SmallestSize);
            TarFile.CreateFromDirectory(tempRoot, gzip, includeBaseDirectory: false);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_handler(request));
        }
    }
}
