using System.Text.Json.Nodes;
using System.Text.Json;
using System.IO.Compression;
using System.Text;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using MAAUnified.Application.Models;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Security;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using MAAUnified.Compat.Constants;
using MAAUnified.Compat.Runtime;
using MAAUnified.SecureStorage.Linux;
using MAAUnified.SecureStorage.System;
using Microsoft.Data.Sqlite;

namespace MAAUnified.Tests;

public sealed class SensitiveSettingsSecurityTests
{
    [Fact]
    public void UiLogSnapshotSanitizesSecretsAndPrivateUserPathsAtBufferBoundary()
    {
        const string secret = "UI/LOG?SECRET+SENTINEL-bf91";
        const string unixUserSentinel = "UI_LOG_UNIX_USER_SENTINEL";
        const string macUserSentinel = "UI_LOG_MAC_USER_SENTINEL";
        const string windowsUserSentinel = "UI_LOG_WINDOWS_USER_SENTINEL";
        var privateRoot = Path.Combine(Path.GetTempPath(), "UI_LOG_PRIVATE_ROOT_SENTINEL", Guid.NewGuid().ToString("N"));
        var log = new UiLogService([privateRoot]);
        log.SetSensitiveValueProvider(() => [secret]);
        UiLogMessage? received = null;
        log.LogReceived += value => received = value;
        var uriEncodedSecret = Uri.EscapeDataString(secret);
        var formEncodedSecret = System.Net.WebUtility.UrlEncode(secret);

        log.Info(
            $"secret={secret}; uri={uriEncodedSecret}; form={formEncodedSecret}; private={Path.Combine(privateRoot, "adb")}; unix=/home/{unixUserSentinel}/adb; mac=/Users/{macUserSentinel}/adb; windows=C:\\Users\\{windowsUserSentinel}\\adb; relative=cache/adb; system=/usr/bin/adb");

        var message = Assert.Single(log.Snapshot).Message;
        Assert.Equal(message, Assert.IsType<UiLogMessage>(received).Message);
        Assert.DoesNotContain(secret, message, StringComparison.Ordinal);
        Assert.DoesNotContain(uriEncodedSecret, message, StringComparison.Ordinal);
        Assert.DoesNotContain(formEncodedSecret, message, StringComparison.Ordinal);
        Assert.DoesNotContain(privateRoot, message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(unixUserSentinel, message, StringComparison.Ordinal);
        Assert.DoesNotContain(macUserSentinel, message, StringComparison.Ordinal);
        Assert.DoesNotContain(windowsUserSentinel, message, StringComparison.Ordinal);
        Assert.Contains(SensitiveDataRedactor.Replacement, message, StringComparison.Ordinal);
        Assert.Contains("relative=cache/adb", message, StringComparison.Ordinal);
        Assert.Contains("system=/usr/bin/adb", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SystemBackendStorageResourcesCanBeCreated()
    {
        using var directory = TestDirectory.Create();
        var dataDirectory = Path.Combine(directory.Path, "data");
        var testNamespace = $"t{Guid.NewGuid():N}";

        var backend = new MsalSystemStorageBackend(directory.Path, testNamespace);
        backend.InitializeStorage();

        Assert.Equal(SensitiveStorageBackendKind.SystemProtected, backend.Kind);
        Assert.True(Directory.Exists(dataDirectory));

        var storeId = Guid.NewGuid().ToString("D");
        const string sentinel = "SYSTEM-STORAGE-ROUND-TRIP-SENTINEL";
        ISensitiveSettingsBackend persistenceBackend = backend;
        TestDirectory? xdg = null;
        var systemStorageVerified = false;
        var systemStorageCleared = false;
        try
        {
            try
            {
                await backend.VerifyPersistenceAsync();
                systemStorageVerified = true;
            }
            catch (SensitiveStorageException ex) when (
                OperatingSystem.IsLinux()
                && ex.ErrorCode == SensitiveStorageErrorCode.StructurallyUnavailable)
            {
                xdg = TestDirectory.Create();
                persistenceBackend = new SqlCipherStorageBackend(
                    directory.Path,
                    new LinuxMasterKeyStore(xdg.Path));
                await persistenceBackend.VerifyPersistenceAsync();
            }

            await persistenceBackend.WriteAsync(Document(storeId, 1, sentinel), expectedRevision: 0);
            var readBack = await persistenceBackend.ReadAsync(storeId, 1);

            Assert.Equal(
                sentinel,
                readBack.Profiles["Default"][ConfigurationKeys.RemoteControlUserIdentity]!.ToString());

            if (ReferenceEquals(persistenceBackend, backend))
            {
                await backend.ClearIsolatedTestStorageAsync();
                systemStorageCleared = true;
                var missing = await Assert.ThrowsAsync<SensitiveStorageException>(
                    () => backend.ReadAsync(storeId, 1));
                Assert.Equal(SensitiveStorageErrorCode.RevisionNotFound, missing.ErrorCode);
            }
        }
        finally
        {
            if (systemStorageVerified && !systemStorageCleared)
            {
                await backend.ClearIsolatedTestStorageAsync();
            }

            xdg?.Dispose();
        }
    }

    [Fact]
    public async Task PlaintextBackendCallerCancellationIsNotReclassifiedAsStorageFailure()
    {
        using var directory = TestDirectory.Create();
        var backend = new PlaintextSensitiveSettingsBackend(directory.Path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => backend.VerifyPersistenceAsync(cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "data")));
    }

    [Fact]
    public async Task SqlCipherBackendDefersNativeInitializationAndRetriesAfterFailure()
    {
        using var runtime = TestDirectory.Create();
        using var xdg = TestDirectory.Create();
        var attempts = 0;
        var initializer = new SqlCipherProviderInitializer(() =>
        {
            attempts++;
            throw new DllNotFoundException("simulated missing SQLCipher provider");
        });

        var backend = new SqlCipherStorageBackend(
            runtime.Path,
            new LinuxMasterKeyStore(xdg.Path),
            initializer);

        Assert.Equal(0, attempts);
        Assert.False(Directory.Exists(Path.Combine(runtime.Path, "data")));
        var first = await Assert.ThrowsAsync<SensitiveStorageException>(() => backend.VerifyPersistenceAsync());
        var second = await Assert.ThrowsAsync<SensitiveStorageException>(() => backend.VerifyPersistenceAsync());

        Assert.Equal(SensitiveStorageErrorCode.StructurallyUnavailable, first.ErrorCode);
        Assert.Equal(SensitiveStorageErrorCode.StructurallyUnavailable, second.ErrorCode);
        Assert.Equal(2, attempts);
        Assert.False(Directory.Exists(Path.Combine(runtime.Path, "data")));
        Assert.False(File.Exists(new LinuxMasterKeyStore(xdg.Path).KeyPath));
    }

    [Fact]
    public async Task SqlCipherProviderInitializationIsThreadSafe()
    {
        var calls = 0;
        var initializer = new SqlCipherProviderInitializer(() => Interlocked.Increment(ref calls));

        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(initializer.EnsureInitialized)));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SqlCipherBackendCallerCancellationSkipsNativeInitialization()
    {
        using var runtime = TestDirectory.Create();
        var calls = 0;
        var backend = new SqlCipherStorageBackend(
            runtime.Path,
            keyStore: null,
            providerInitializer: new SqlCipherProviderInitializer(() => calls++));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => backend.VerifyPersistenceAsync(cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(0, calls);
        Assert.False(Directory.Exists(Path.Combine(runtime.Path, "data")));
    }

    [Fact]
    public void SqlCipherNativeLoadFailuresAreStructurallyUnavailableThroughWrappers()
    {
        Exception[] failures =
        [
            new DllNotFoundException("missing"),
            new BadImageFormatException("wrong ABI"),
            new EntryPointNotFoundException("missing export"),
            new TypeInitializationException(
                "SQLitePCL.raw",
                new System.Reflection.TargetInvocationException(new DllNotFoundException("wrapped missing"))),
            new AggregateException(
                new IOException("unrelated branch"),
                new TypeInitializationException("SQLitePCL.provider", new EntryPointNotFoundException("wrapped export"))),
        ];

        foreach (var failure in failures)
        {
            var classified = SqlCipherStorageBackend.Classify(failure, "provider init failed");

            Assert.Equal(SensitiveStorageErrorCode.StructurallyUnavailable, classified.ErrorCode);
            Assert.Same(failure, classified.InnerException);
        }
    }

    [Fact]
    public void SqlCipherDataPermissionAndTransientFailuresRemainNonStructural()
    {
        Assert.Equal(
            SensitiveStorageErrorCode.PermissionDenied,
            SqlCipherStorageBackend.Classify(new UnauthorizedAccessException(), "failed").ErrorCode);
        Assert.Equal(
            SensitiveStorageErrorCode.Corrupt,
            SqlCipherStorageBackend.Classify(new InvalidDataException(), "failed").ErrorCode);
        Assert.Equal(
            SensitiveStorageErrorCode.Unavailable,
            SqlCipherStorageBackend.Classify(new IOException(), "failed").ErrorCode);
    }

    [Fact]
    public async Task CaseVariantSensitiveKeysAreRedactedFromIssueReportAndMigratedToSecureStorage()
    {
        using var directory = TestDirectory.Create();
        const string remoteSentinel = "CASE-VARIANT-REMOTE-SENTINEL-7bc2";
        const string globalSentinel = "CASE-VARIANT-CDK-SENTINEL-a115";
        var configDirectory = Path.Combine(directory.Path, "config");
        Directory.CreateDirectory(configDirectory);
        var caseVariantRemoteKey = ConfigurationKeys.RemoteControlGetTaskEndpointUri.ToLowerInvariant();
        var caseVariantGlobalKey = ConfigurationKeys.MirrorChyanCdk.ToLowerInvariant();
        var legacyConfig = new JsonObject
        {
            [nameof(UnifiedConfig.SchemaVersion)] = 2,
            [nameof(UnifiedConfig.CurrentProfile)] = "Default",
            [nameof(UnifiedConfig.Profiles)] = new JsonObject
            {
                ["Default"] = new JsonObject
                {
                    [nameof(UnifiedProfile.Values)] = new JsonObject(),
                    [nameof(UnifiedProfile.TaskQueue)] = new JsonArray(),
                },
            },
            [nameof(UnifiedConfig.GlobalValues)] = new JsonObject
            {
                [caseVariantRemoteKey] = remoteSentinel,
                [caseVariantGlobalKey] = globalSentinel,
            },
        };
        var configPath = Path.Combine(configDirectory, "avalonia.json");
        await File.WriteAllTextAsync(configPath, legacyConfig.ToJsonString());

        var diagnostics = new UiDiagnosticsService(directory.Path, new UiLogService());
        var bundlePath = await diagnostics.BuildIssueReportBundleAsync(directory.Path);
        using (var archive = ZipFile.OpenRead(bundlePath))
        {
            var configEntry = Assert.Single(archive.Entries, entry => entry.FullName == "config/avalonia.json");
            using var reader = new StreamReader(configEntry.Open());
            var bundledConfig = await reader.ReadToEndAsync();
            Assert.DoesNotContain(remoteSentinel, bundledConfig, StringComparison.Ordinal);
            Assert.DoesNotContain(globalSentinel, bundledConfig, StringComparison.Ordinal);
            Assert.DoesNotContain(caseVariantRemoteKey, bundledConfig, StringComparison.Ordinal);
            Assert.DoesNotContain(caseVariantGlobalKey, bundledConfig, StringComparison.Ordinal);
        }

        var backend = new FakeBackend(SensitiveStorageBackendKind.SystemProtected);
        var service = CreateConfigurationService(directory.Path, backend);
        await service.LoadOrBootstrapAsync();

        var persistedText = await File.ReadAllTextAsync(configPath);
        Assert.DoesNotContain(remoteSentinel, persistedText, StringComparison.Ordinal);
        Assert.DoesNotContain(globalSentinel, persistedText, StringComparison.Ordinal);
        Assert.DoesNotContain(caseVariantRemoteKey, persistedText, StringComparison.Ordinal);
        Assert.DoesNotContain(caseVariantGlobalKey, persistedText, StringComparison.Ordinal);
        Assert.Equal(
            remoteSentinel,
            service.CurrentConfig.Profiles["default"].Values[ConfigurationKeys.RemoteControlGetTaskEndpointUri]!.ToString());
        Assert.Equal(globalSentinel, service.CurrentConfig.GlobalValues[ConfigurationKeys.MirrorChyanCdk]!.ToString());

        var metadata = service.CurrentConfig.SensitiveStorage;
        var document = await backend.ReadAsync(metadata.StoreId, metadata.Revision);
        Assert.Equal(
            remoteSentinel,
            document.Profiles["Default"][ConfigurationKeys.RemoteControlGetTaskEndpointUri]!.ToString());
        Assert.Equal(globalSentinel, document.GlobalValues[ConfigurationKeys.MirrorChyanCdk]!.ToString());
    }

    [Fact]
    public async Task SystemBackendPersistenceProbeUsesIsolatedIdentityAndPreservesExistingStores()
    {
        using var directory = TestDirectory.Create();
        var testNamespace = $"t{Guid.NewGuid():N}";
        var primaryProperties = MsalSystemStorageBackend.CreateStorageProperties(directory.Path, persistenceProbe: false);
        var probeProperties = MsalSystemStorageBackend.CreateStorageProperties(directory.Path, persistenceProbe: true);
        var testProperties = MsalSystemStorageBackend.CreateStorageProperties(
            directory.Path,
            persistenceProbe: false,
            testNamespace);
        var testProbeProperties = MsalSystemStorageBackend.CreateStorageProperties(
            directory.Path,
            persistenceProbe: true,
            testNamespace);

        Assert.Equal("system-sensitive-settings.bin", primaryProperties.CacheFileName);
        Assert.Equal("io.maa.MAAUnified.sensitive-settings", primaryProperties.MacKeyChainServiceName);
        Assert.Equal("MAAUnified.SensitiveSettings.v1", primaryProperties.MacKeyChainAccountName);
        Assert.Equal("io.maa.MAAUnified.SensitiveSettings", primaryProperties.KeyringSchemaName);
        Assert.Equal(new KeyValuePair<string, string>("application", "MAAUnified"), primaryProperties.KeyringAttribute1);
        Assert.Equal(new KeyValuePair<string, string>("format", "1"), primaryProperties.KeyringAttribute2);
        Assert.NotEqual(primaryProperties.CacheFileName, probeProperties.CacheFileName);
        Assert.NotEqual(primaryProperties.MacKeyChainServiceName, probeProperties.MacKeyChainServiceName);
        Assert.NotEqual(primaryProperties.MacKeyChainAccountName, probeProperties.MacKeyChainAccountName);
        Assert.NotEqual(primaryProperties.KeyringSchemaName, probeProperties.KeyringSchemaName);
        Assert.NotEqual(primaryProperties.KeyringAttribute2, probeProperties.KeyringAttribute2);
        Assert.NotEqual(primaryProperties.CacheFileName, testProperties.CacheFileName);
        Assert.NotEqual(primaryProperties.MacKeyChainServiceName, testProperties.MacKeyChainServiceName);
        Assert.NotEqual(primaryProperties.MacKeyChainAccountName, testProperties.MacKeyChainAccountName);
        Assert.NotEqual(primaryProperties.KeyringSchemaName, testProperties.KeyringSchemaName);
        Assert.NotEqual(primaryProperties.KeyringAttribute1, testProperties.KeyringAttribute1);
        Assert.NotEqual(testProperties.CacheFileName, testProbeProperties.CacheFileName);
        Assert.NotEqual(testProperties.MacKeyChainServiceName, testProbeProperties.MacKeyChainServiceName);
        Assert.NotEqual(testProperties.MacKeyChainAccountName, testProbeProperties.MacKeyChainAccountName);
        Assert.NotEqual(testProperties.KeyringSchemaName, testProbeProperties.KeyringSchemaName);
        Assert.All(
            new[] { primaryProperties.KeyringSchemaName, probeProperties.KeyringSchemaName, testProperties.KeyringSchemaName, testProbeProperties.KeyringSchemaName },
            AssertValidLinuxKeyringSchema);

        var envelope = MsalSystemStorageBackend.MergeDocument(null, Document("portable-a", 1, "a-one"));
        envelope = MsalSystemStorageBackend.MergeDocument(envelope, Document("portable-b", 1, "b-one"));
        var primaryBytes = JsonSerializer.SerializeToUtf8Bytes(envelope);
        byte[]? probeBytes = null;

        await MsalSystemStorageBackend.VerifyPersistenceAndCleanupAsync(
            verifyPersistence: () =>
            {
                probeBytes = "probe"u8.ToArray();
                Assert.Equal("probe"u8.ToArray(), probeBytes);
            },
            clear: () => probeBytes = null,
            cancellationToken: CancellationToken.None);

        var preserved = JsonSerializer.Deserialize<SensitiveSettingsEnvelope>(primaryBytes)!;
        Assert.Equal(
            "a-one",
            MsalSystemStorageBackend.FindStoredRevision(preserved, "portable-a", 1)
                .Profiles["Default"][ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
        Assert.Equal(
            "b-one",
            MsalSystemStorageBackend.FindStoredRevision(preserved, "portable-b", 1)
                .Profiles["Default"][ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
        Assert.Null(probeBytes);

        var productionBackend = new MsalSystemStorageBackend(directory.Path);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => productionBackend.ClearIsolatedTestStorageAsync());
    }

    [Fact]
    public async Task SystemBackendPersistenceProbeAlwaysRunsExplicitCleanup()
    {
        var cleanupCalls = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => MsalSystemStorageBackend.VerifyPersistenceAndCleanupAsync(
                verifyPersistence: () => throw new InvalidOperationException("simulated persistence failure"),
                clear: () => cleanupCalls++,
                cancellationToken: CancellationToken.None));

        Assert.Equal(1, cleanupCalls);
        Assert.Throws<InvalidOperationException>(
            () => MsalSystemStorageBackend.VerifyPersistenceAndCleanupAsync(
                    verifyPersistence: () => { },
                    clear: () => throw new InvalidOperationException("simulated cleanup failure"),
                    cancellationToken: CancellationToken.None)
                .GetAwaiter()
                .GetResult());
    }

    private static void AssertValidLinuxKeyringSchema(string schema)
    {
        var components = schema.Split('.');
        Assert.True(components.Length >= 2);
        Assert.All(components, component =>
        {
            Assert.NotEmpty(component);
            Assert.True(component[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_');
            Assert.All(
                component,
                character => Assert.True(
                    character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_'));
        });
    }

    [Fact]
    public void SystemBackendClassificationTraversesWrappedExceptionsAndPrioritizesCancellation()
    {
        var error = MsalSystemStorageBackend.Classify(
            new Exception(
                "outer org.freedesktop.secrets DBUS_SESSION_BUS_ADDRESS",
                new AggregateException(
                    new InvalidOperationException("temporary D-Bus failure"),
                    new OperationCanceledException("prompt was canceled"))),
            "verify failed",
            isLinux: true);

        Assert.Equal(SensitiveStorageErrorCode.UserCanceled, error.ErrorCode);
    }

    [Fact]
    public void SystemBackendClassificationTreatsMalformedOrUndecryptablePayloadAsCorrupt()
    {
        var malformed = MsalSystemStorageBackend.Classify(
            new JsonException("malformed envelope"),
            "read failed",
            isLinux: false);
        var undecryptable = MsalSystemStorageBackend.Classify(
            new Exception("storage wrapper", new CryptographicException("invalid protected payload")),
            "read failed",
            isLinux: false);
        var probeFailure = MsalSystemStorageBackend.Classify(
            new CryptographicException("cryptographic provider unavailable"),
            "verify failed",
            isLinux: false,
            classifyPayloadCorruption: false);

        Assert.Equal(SensitiveStorageErrorCode.Corrupt, malformed.ErrorCode);
        Assert.Equal(SensitiveStorageErrorCode.Corrupt, undecryptable.ErrorCode);
        Assert.Equal(SensitiveStorageErrorCode.Unavailable, probeFailure.ErrorCode);
    }

    [Fact]
    public void SystemBackendClassificationKeepsPermissionAndLockedStateAheadOfCorruption()
    {
        var permission = MsalSystemStorageBackend.Classify(
            new UnauthorizedAccessException("denied", new CryptographicException("decode failed")),
            "read failed",
            isLinux: false);
        var locked = MsalSystemStorageBackend.Classify(
            new CryptographicException("org.freedesktop.Secret.Error.IsLocked: collection is locked"),
            "read failed",
            isLinux: true);

        Assert.Equal(SensitiveStorageErrorCode.PermissionDenied, permission.ErrorCode);
        Assert.Equal(SensitiveStorageErrorCode.Unavailable, locked.ErrorCode);
    }

    [Fact]
    public void SystemBackendClassificationPrioritizesWrappedPermissionAndLockedFailuresOverStructuralText()
    {
        var permission = MsalSystemStorageBackend.Classify(
            new AggregateException(
                new DllNotFoundException("libsecret-1.so.0"),
                new Exception("wrapper", new UnauthorizedAccessException("denied"))),
            "verify failed",
            isLinux: true);
        var locked = MsalSystemStorageBackend.Classify(
            new Exception("org.freedesktop.secrets collection is locked; DBUS_SESSION_BUS_ADDRESS is not set"),
            "verify failed",
            isLinux: true);

        Assert.Equal(SensitiveStorageErrorCode.PermissionDenied, permission.ErrorCode);
        Assert.Equal(SensitiveStorageErrorCode.Unavailable, locked.ErrorCode);
    }

    [Theory]
    [InlineData("Unable to load shared library 'libsecret-1.so.0'")]
    [InlineData("Cannot autolaunch D-Bus without X11 $DISPLAY")]
    [InlineData("org.freedesktop.DBus.Error.ServiceUnknown: The name org.freedesktop.secrets was not provided by any .service files")]
    public void SystemBackendClassificationAllowsOnlyKnownStructuralLinuxFailures(string detail)
    {
        var error = MsalSystemStorageBackend.Classify(new InvalidOperationException(detail), "verify failed", isLinux: true);

        Assert.Equal(SensitiveStorageErrorCode.StructurallyUnavailable, error.ErrorCode);
    }

    [Theory]
    [InlineData("Temporary failure calling org.freedesktop.secrets")]
    [InlineData("Connection at DBUS_SESSION_BUS_ADDRESS closed unexpectedly")]
    [InlineData("org.freedesktop.DBus.Error.NoReply")]
    public void SystemBackendClassificationDoesNotDowngradeGenericSecretServiceFailures(string detail)
    {
        var error = MsalSystemStorageBackend.Classify(new InvalidOperationException(detail), "verify failed", isLinux: true);

        Assert.Equal(SensitiveStorageErrorCode.Unavailable, error.ErrorCode);
    }

    [Fact]
    public void SystemBackendRetainsTwoRevisionsForEachPortableStore()
    {
        var envelope = MsalSystemStorageBackend.MergeDocument(null, Document("portable-a", 1, "a-one"));
        envelope = JsonSerializer.Deserialize<SensitiveSettingsEnvelope>(JsonSerializer.Serialize(envelope))!;
        envelope = MsalSystemStorageBackend.MergeDocument(envelope, Document("portable-b", 1, "b-one"));
        envelope = JsonSerializer.Deserialize<SensitiveSettingsEnvelope>(JsonSerializer.Serialize(envelope))!;
        envelope = MsalSystemStorageBackend.MergeDocument(envelope, Document("portable-a", 2, "a-two"));

        Assert.Equal(
            "a-one",
            MsalSystemStorageBackend.FindStoredRevision(envelope, "portable-a", 1)
                .Profiles["Default"][ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
        Assert.Equal(
            "a-two",
            MsalSystemStorageBackend.FindStoredRevision(envelope, "portable-a", 2)
                .Profiles["Default"][ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
        Assert.Equal(
            "b-one",
            MsalSystemStorageBackend.FindStoredRevision(envelope, "portable-b", 1)
                .Profiles["Default"][ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
    }

    [Fact]
    public void RegistryContainsExactlyWpfSensitiveSet()
    {
        Assert.Equal(32, SensitiveSettingKeyRegistry.Definitions.Count);
        Assert.Equal(30, SensitiveSettingKeyRegistry.Definitions.Count(value => value.Scope == SensitiveSettingScope.Profile));
        // P1-13（WPF 53870a7a52）：GitHub Token 与 Mirror 酱 CDK 同为全局凭据。
        Assert.Equal(2, SensitiveSettingKeyRegistry.Definitions.Count(value => value.Scope == SensitiveSettingScope.Global));
        Assert.Equal(32, SensitiveSettingKeyRegistry.Definitions.Select(value => value.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(SensitiveSettingKeyRegistry.Definitions, definition =>
        {
            Assert.False(string.IsNullOrWhiteSpace(definition.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(definition.LocalizationKey));
        });
    }

    [Fact]
    public void ExtractRemoveInjectRoundTripsGlobalAndMultipleProfiles()
    {
        var config = new UnifiedConfig
        {
            CurrentProfile = "Second",
            GlobalValues = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase)
            {
                [ConfigurationKeys.MirrorChyanCdk] = "global-secret",
                ["ordinary-global"] = "keep-global",
            },
            Profiles = new Dictionary<string, UnifiedProfile>(StringComparer.OrdinalIgnoreCase)
            {
                ["Default"] = new()
                {
                    Values = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase)
                    {
                        [ConfigurationKeys.RemoteControlUserIdentity] = "default-user",
                        ["ordinary-profile"] = "keep-default",
                    },
                },
                ["Second"] = new()
                {
                    Values = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase)
                    {
                        [ConfigurationKeys.RemoteControlUserIdentity] = "second-user",
                        [ConfigurationKeys.ExternalNotificationSmtpPassword] = "smtp-secret",
                    },
                },
            },
        };

        var document = SensitiveSettingKeyRegistry.ExtractAndRemove(config, "store", 4);

        Assert.False(config.GlobalValues.ContainsKey(ConfigurationKeys.MirrorChyanCdk));
        Assert.False(config.Profiles["Default"].Values.ContainsKey(ConfigurationKeys.RemoteControlUserIdentity));
        Assert.Equal("keep-global", config.GlobalValues["ordinary-global"]!.ToString());
        Assert.Equal("keep-default", config.Profiles["Default"].Values["ordinary-profile"]!.ToString());

        SensitiveSettingKeyRegistry.Inject(config, document);

        Assert.Equal("global-secret", config.GlobalValues[ConfigurationKeys.MirrorChyanCdk]!.ToString());
        Assert.Equal("default-user", config.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
        Assert.Equal("second-user", config.Profiles["Second"].Values[ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
        Assert.Equal("smtp-secret", config.Profiles["Second"].Values[ConfigurationKeys.ExternalNotificationSmtpPassword]!.ToString());
    }

    [Fact]
    public async Task DefaultOnlySensitiveValueRemainsAvailableAfterUninitializedRestart()
    {
        using var directory = TestDirectory.Create();
        var first = CreateConfigurationService(directory.Path, new FakeBackend(SensitiveStorageBackendKind.SystemProtected));
        await first.LoadOrBootstrapAsync();
        first.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.ExternalNotificationBarkServer] =
            JsonValue.Create("https://api.day.app");

        await first.SaveAsync();

        Assert.Equal(SensitiveStorageBackendKind.Uninitialized, first.CurrentConfig.SensitiveStorage.Backend);
        var persisted = await File.ReadAllTextAsync(Path.Combine(directory.Path, "config", "avalonia.json"));
        Assert.DoesNotContain(ConfigurationKeys.ExternalNotificationBarkServer, persisted, StringComparison.Ordinal);

        var restarted = CreateConfigurationService(directory.Path, new FakeBackend(SensitiveStorageBackendKind.SystemProtected));
        await restarted.LoadOrBootstrapAsync();

        Assert.Equal(SensitiveStorageBackendKind.Uninitialized, restarted.CurrentConfig.SensitiveStorage.Backend);
        Assert.Equal(
            "https://api.day.app",
            restarted.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.ExternalNotificationBarkServer]!.ToString());
    }

    [Fact]
    public void RegistryRejectsSensitiveKeysInTheWrongScope()
    {
        var profileKeyInGlobalScope = Document("store", 1, "secret");
        profileKeyInGlobalScope.GlobalValues[ConfigurationKeys.RemoteControlUserIdentity] = "invalid";
        var globalKeyInProfileScope = Document("store", 1, "secret");
        globalKeyInProfileScope.Profiles["Default"][ConfigurationKeys.MirrorChyanCdk] = "invalid";

        var globalError = Assert.Throws<SensitiveStorageException>(
            () => SensitiveSettingKeyRegistry.ValidateDocument(profileKeyInGlobalScope));
        var profileError = Assert.Throws<SensitiveStorageException>(
            () => SensitiveSettingKeyRegistry.ValidateDocument(globalKeyInProfileScope));

        Assert.Equal(SensitiveStorageErrorCode.InvalidDocument, globalError.ErrorCode);
        Assert.Equal(SensitiveStorageErrorCode.InvalidDocument, profileError.ErrorCode);
    }

    [Fact]
    public void RegistryMigratesMisplacedGlobalProfileSecretWithoutOverwritingProfileValue()
    {
        var config = new UnifiedConfig();
        config.GlobalValues[ConfigurationKeys.RemoteControlGetTaskEndpointUri] = "https://global.example/task";
        config.GlobalValues[ConfigurationKeys.RemoteControlReportStatusUri] = "https://global.example/report";
        config.Profiles["Default"].Values[ConfigurationKeys.RemoteControlGetTaskEndpointUri] = "https://profile.example/task";

        var document = SensitiveSettingKeyRegistry.ExtractAndRemove(config, "store", 1);

        Assert.False(config.GlobalValues.ContainsKey(ConfigurationKeys.RemoteControlGetTaskEndpointUri));
        Assert.False(config.GlobalValues.ContainsKey(ConfigurationKeys.RemoteControlReportStatusUri));
        Assert.Equal(
            "https://profile.example/task",
            document.Profiles["Default"][ConfigurationKeys.RemoteControlGetTaskEndpointUri]!.ToString());
        Assert.Equal(
            "https://global.example/report",
            document.Profiles["Default"][ConfigurationKeys.RemoteControlReportStatusUri]!.ToString());
    }

    [Fact]
    public void ShortSensitiveValuesAreRedactedOnlyAtCompleteTokenBoundaries()
    {
        var config = new UnifiedConfig();
        config.Profiles["Default"].Values[ConfigurationKeys.ExternalNotificationTelegramTopicId] = "1";
        config.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity] = "q7";
        var values = SensitiveSettingKeyRegistry.GetPlaintextValues(config);
        var log = new UiLogService();
        log.SetSensitiveValueProvider(() => values);

        log.Info("topicId=1 status=401 count=11 token=[1] identity=q7 suffix=maq7x");

        Assert.Contains("1", values);
        Assert.Contains("q7", values);
        var message = Assert.Single(log.Snapshot).Message;
        Assert.Equal(
            "topicId=[REDACTED] status=401 count=11 token=[[REDACTED]] identity=[REDACTED] suffix=maq7x",
            message);

        var json = SensitiveDataRedactor.Redact(
            "{\"topicId\":\"1\",\"status\":\"401\",\"revision\":11}",
            values);
        Assert.Equal(
            "{\"topicId\":\"[REDACTED]\",\"status\":\"401\",\"revision\":11}",
            json);
    }

    [Fact]
    public async Task InitialLinuxStructuralSystemFailureSelectsSqlCipher()
    {
        var system = new FakeBackend(SensitiveStorageBackendKind.SystemProtected)
        {
            VerifyFailure = new SensitiveStorageException(SensitiveStorageErrorCode.StructurallyUnavailable, "no secret service"),
        };
        var sqlCipher = new FakeBackend(SensitiveStorageBackendKind.LinuxLocalSqlCipher);
        var service = new SensitiveSettingsService([system, sqlCipher], new FakeConsent(false), isLinux: true);
        var config = ConfigWithSecret();

        await service.PrepareSaveAsync(config);

        Assert.Equal(SensitiveStorageBackendKind.LinuxLocalSqlCipher, config.SensitiveStorage.Backend);
        Assert.Equal(1, sqlCipher.WriteCount);
    }

    [Fact]
    public async Task InitialLinuxTransientSystemFailureDoesNotDowngrade()
    {
        var system = new FakeBackend(SensitiveStorageBackendKind.SystemProtected)
        {
            VerifyFailure = new SensitiveStorageException(SensitiveStorageErrorCode.Unavailable, "locked"),
        };
        var sqlCipher = new FakeBackend(SensitiveStorageBackendKind.LinuxLocalSqlCipher);
        var service = new SensitiveSettingsService([system, sqlCipher], new FakeConsent(false), isLinux: true);

        var error = await Assert.ThrowsAsync<SensitiveStorageException>(() => service.PrepareSaveAsync(ConfigWithSecret()));

        Assert.Equal(SensitiveStorageErrorCode.Unavailable, error.ErrorCode);
        Assert.Equal(0, sqlCipher.VerifyCount);
    }

    [Fact]
    public async Task SelectedBackendFailureDoesNotTryAnotherBackend()
    {
        var system = new FakeBackend(SensitiveStorageBackendKind.SystemProtected)
        {
            ReadFailure = new SensitiveStorageException(SensitiveStorageErrorCode.UserCanceled, "unlock canceled"),
        };
        var sqlCipher = new FakeBackend(SensitiveStorageBackendKind.LinuxLocalSqlCipher);
        var config = new UnifiedConfig
        {
            SensitiveStorage = new SensitiveStorageMetadata
            {
                Backend = SensitiveStorageBackendKind.SystemProtected,
                StoreId = "selected-store",
                Revision = 8,
            },
        };
        var service = new SensitiveSettingsService([system, sqlCipher], new FakeConsent(true), isLinux: true);

        await service.LoadAsync(config);

        Assert.Equal(SensitiveStorageState.Unavailable, service.State);
        Assert.Equal(SensitiveStorageErrorCode.UserCanceled, service.LastErrorCode);
        Assert.Equal(0, system.VerifyCount);
        Assert.Equal(0, sqlCipher.ReadCount);
        Assert.Equal(0, sqlCipher.WriteCount);
    }

    [Fact]
    public async Task SelectedBackendLoadDoesNotRunDestructivePersistenceProbe()
    {
        var system = new FakeBackend(SensitiveStorageBackendKind.SystemProtected)
        {
            ClearDocumentsOnVerify = true,
        };
        system.Seed(Document("selected-store", 8, "survives-restart"));
        var config = ConfigForSelectedBackend(SensitiveStorageBackendKind.SystemProtected, "selected-store", 8);
        var service = new SensitiveSettingsService([system], new FakeConsent(false), isLinux: false);

        await service.LoadAsync(config);

        Assert.Equal(SensitiveStorageState.Available, service.State);
        Assert.Equal(0, system.VerifyCount);
        Assert.Equal(1, system.ReadCount);
        Assert.Equal(
            "survives-restart",
            config.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
    }

    [Fact]
    public async Task SelectedBackendLoadRemovesAllPersistedPlaintextAndUsesBackendAsAuthority()
    {
        using var directory = TestDirectory.Create();
        const string storeId = "selected-store";
        const string backendSecret = "BACKEND-AUTHORITATIVE-SECRET";
        const string conflictingPlaintext = "STALE-CONFLICTING-PLAINTEXT";
        const string missingFromBackendPlaintext = "STALE-MISSING-PLAINTEXT";
        var backend = new FakeBackend(SensitiveStorageBackendKind.SystemProtected);
        backend.Seed(Document(storeId, 4, backendSecret));
        var persisted = ConfigForSelectedBackend(SensitiveStorageBackendKind.SystemProtected, storeId, 4);
        persisted.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity] = conflictingPlaintext;
        persisted.GlobalValues[ConfigurationKeys.MirrorChyanCdk] = missingFromBackendPlaintext;
        var store = new AvaloniaJsonConfigStore(directory.Path);
        await store.SaveAsync(persisted);
        var service = CreateConfigurationService(directory.Path, backend, store);

        await service.LoadOrBootstrapAsync();

        Assert.Equal(
            backendSecret,
            service.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
        Assert.False(service.CurrentConfig.GlobalValues.ContainsKey(ConfigurationKeys.MirrorChyanCdk));
        var onDisk = await File.ReadAllTextAsync(store.ConfigPath);
        Assert.DoesNotContain(conflictingPlaintext, onDisk, StringComparison.Ordinal);
        Assert.DoesNotContain(missingFromBackendPlaintext, onDisk, StringComparison.Ordinal);
        Assert.DoesNotContain(backendSecret, onDisk, StringComparison.Ordinal);
        Assert.Equal(4, service.CurrentConfig.SensitiveStorage.Revision);
        Assert.Equal(0, backend.WriteCount);
    }

    [Fact]
    public async Task ProfileCopyIsRejectedWhenInitialBackendReadFailsAndSourceSecretSurvivesReload()
    {
        using var directory = TestDirectory.Create();
        const string storeId = "selected-store";
        const string sourceSecret = "SOURCE-PROFILE-SECRET";
        var backend = new FakeBackend(SensitiveStorageBackendKind.SystemProtected);
        backend.Seed(Document(storeId, 3, sourceSecret));
        var store = new AvaloniaJsonConfigStore(directory.Path);
        await store.SaveAsync(ConfigForSelectedBackend(SensitiveStorageBackendKind.SystemProtected, storeId, 3));
        backend.ReadFailure = new SensitiveStorageException(SensitiveStorageErrorCode.Unavailable, "first read unavailable");
        var unavailable = CreateConfigurationService(directory.Path, backend, store);
        await unavailable.LoadOrBootstrapAsync();
        var profiles = new ConfigurationProfileFeatureService(unavailable);

        var result = await profiles.AddProfileAsync("Copy", "Default");

        Assert.False(result.Success);
        Assert.Equal(UiErrorCode.ConfigurationProfileSaveFailed, result.Error?.Code);
        Assert.False(unavailable.CurrentConfig.Profiles.ContainsKey("Copy"));
        backend.ReadFailure = null;
        var recovered = CreateConfigurationService(directory.Path, backend, store);
        await recovered.LoadOrBootstrapAsync();
        Assert.Equal(
            sourceSecret,
            recovered.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
        Assert.False(recovered.CurrentConfig.Profiles.ContainsKey("Copy"));
    }

    [Fact]
    public async Task WriteReadBackRejectsChangedOrMissingSensitivePayload()
    {
        Func<SensitiveSettingsDocument, SensitiveSettingsDocument>[] corruptions =
        [
            stored => Document(stored.StoreId, stored.Revision, "changed"),
            stored => new SensitiveSettingsDocument { StoreId = stored.StoreId, Revision = stored.Revision },
            stored => new SensitiveSettingsDocument
            {
                StoreId = stored.StoreId,
                Revision = stored.Revision,
                Profiles = new Dictionary<string, Dictionary<string, JsonNode?>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Default"] = new(StringComparer.OrdinalIgnoreCase),
                },
            },
        ];

        foreach (var corrupt in corruptions)
        {
            var system = new FakeBackend(SensitiveStorageBackendKind.SystemProtected) { ReadTransform = corrupt };
            var service = new SensitiveSettingsService([system], new FakeConsent(false), isLinux: false);
            var config = ConfigWithSecret();

            var error = await Assert.ThrowsAsync<SensitiveStorageException>(() => service.PrepareSaveAsync(config));

            Assert.Equal(SensitiveStorageErrorCode.PersistenceVerificationFailed, error.ErrorCode);
            Assert.Equal(0, config.SensitiveStorage.Revision);
        }
    }

    [Fact]
    public async Task StructurallyMissingSqlCipherRequiresExplicitPlaintextConsent()
    {
        var system = new FakeBackend(SensitiveStorageBackendKind.SystemProtected)
        {
            VerifyFailure = new SensitiveStorageException(SensitiveStorageErrorCode.StructurallyUnavailable, "missing"),
        };
        var sqlCipher = new FakeBackend(SensitiveStorageBackendKind.LinuxLocalSqlCipher)
        {
            VerifyFailure = new SensitiveStorageException(SensitiveStorageErrorCode.StructurallyUnavailable, "provider missing"),
        };
        var plaintext = new FakeBackend(SensitiveStorageBackendKind.Plaintext);
        var service = new SensitiveSettingsService([system, sqlCipher, plaintext], new FakeConsent(false), isLinux: true);
        var config = ConfigWithSecret();

        var error = await Assert.ThrowsAsync<SensitiveStorageException>(() => service.PrepareSaveAsync(config));

        Assert.Equal(SensitiveStorageErrorCode.ActionRequired, error.ErrorCode);
        Assert.Equal(SensitiveStorageBackendKind.Uninitialized, config.SensitiveStorage.Backend);
        Assert.Equal(0, plaintext.WriteCount);
    }

    [Fact]
    public async Task InitialNativeSqlCipherLoadFailureCanUseApprovedPlaintext()
    {
        using var runtime = TestDirectory.Create();
        var system = new FakeBackend(SensitiveStorageBackendKind.SystemProtected)
        {
            VerifyFailure = new SensitiveStorageException(SensitiveStorageErrorCode.StructurallyUnavailable, "missing"),
        };
        var initializer = new SqlCipherProviderInitializer(
            () => throw new BadImageFormatException("simulated SQLCipher ABI mismatch"));
        var sqlCipher = new SqlCipherStorageBackend(
            runtime.Path,
            keyStore: null,
            providerInitializer: initializer);
        var plaintext = new FakeBackend(SensitiveStorageBackendKind.Plaintext);
        var consent = new FakeConsent(true);
        var service = new SensitiveSettingsService([system, sqlCipher, plaintext], consent, isLinux: true);
        var config = ConfigWithSecret();

        await service.PrepareSaveAsync(config);

        Assert.Equal(SensitiveStorageBackendKind.Plaintext, config.SensitiveStorage.Backend);
        Assert.Equal(SensitiveStorageState.PlaintextWarning, service.State);
        Assert.Equal(1, consent.RequestCount);
        Assert.Equal(1, plaintext.VerifyCount);
        Assert.Equal(1, plaintext.WriteCount);
        Assert.False(Directory.Exists(Path.Combine(runtime.Path, "data")));
    }

    [Fact]
    public async Task SelectedSqlCipherNativeLoadFailureDoesNotDowngrade()
    {
        using var runtime = TestDirectory.Create();
        var initializer = new SqlCipherProviderInitializer(
            () => throw new EntryPointNotFoundException("simulated SQLCipher ABI mismatch"));
        var sqlCipher = new SqlCipherStorageBackend(
            runtime.Path,
            keyStore: null,
            providerInitializer: initializer);
        var plaintext = new FakeBackend(SensitiveStorageBackendKind.Plaintext);
        var consent = new FakeConsent(true);
        var service = new SensitiveSettingsService([sqlCipher, plaintext], consent, isLinux: true);
        var config = ConfigForSelectedBackend(
            SensitiveStorageBackendKind.LinuxLocalSqlCipher,
            "selected-store",
            1);

        await service.LoadAsync(config);

        Assert.Equal(SensitiveStorageState.Unavailable, service.State);
        Assert.Equal(SensitiveStorageErrorCode.StructurallyUnavailable, service.LastErrorCode);
        Assert.Equal(SensitiveStorageBackendKind.LinuxLocalSqlCipher, config.SensitiveStorage.Backend);
        Assert.Equal("selected-store", config.SensitiveStorage.StoreId);
        Assert.Equal(1, config.SensitiveStorage.Revision);
        Assert.Equal(0, consent.RequestCount);
        Assert.Equal(0, plaintext.VerifyCount);
        Assert.Equal(0, plaintext.ReadCount);
        Assert.Equal(0, plaintext.WriteCount);
        Assert.False(Directory.Exists(Path.Combine(runtime.Path, "data")));
    }

    [Theory]
    [InlineData(SensitiveStorageErrorCode.Corrupt)]
    [InlineData(SensitiveStorageErrorCode.PermissionDenied)]
    [InlineData(SensitiveStorageErrorCode.Unavailable)]
    public async Task InitialSqlCipherDataOrIoFailureDoesNotOfferPlaintext(SensitiveStorageErrorCode errorCode)
    {
        var system = new FakeBackend(SensitiveStorageBackendKind.SystemProtected)
        {
            VerifyFailure = new SensitiveStorageException(SensitiveStorageErrorCode.StructurallyUnavailable, "missing"),
        };
        var sqlCipher = new FakeBackend(SensitiveStorageBackendKind.LinuxLocalSqlCipher)
        {
            VerifyFailure = new SensitiveStorageException(errorCode, "SQLCipher failure"),
        };
        var plaintext = new FakeBackend(SensitiveStorageBackendKind.Plaintext);
        var consent = new FakeConsent(true);
        var service = new SensitiveSettingsService([system, sqlCipher, plaintext], consent, isLinux: true);
        var config = ConfigWithSecret();

        var error = await Assert.ThrowsAsync<SensitiveStorageException>(() => service.PrepareSaveAsync(config));

        Assert.Equal(errorCode, error.ErrorCode);
        Assert.Equal(0, consent.RequestCount);
        Assert.Equal(0, plaintext.VerifyCount);
        Assert.Equal(0, plaintext.WriteCount);
        Assert.Equal(SensitiveStorageBackendKind.Uninitialized, config.SensitiveStorage.Backend);
    }

    [Fact]
    public async Task InitialSqlCipherMissingKeyIsCorruptAndDoesNotOfferPlaintext()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var runtime = TestDirectory.Create();
        using var xdg = TestDirectory.Create();
        var databaseDirectory = Path.Combine(runtime.Path, "data");
        Directory.CreateDirectory(databaseDirectory);
        await File.WriteAllBytesAsync(Path.Combine(databaseDirectory, "sensitive-settings.db"), RandomNumberGenerator.GetBytes(64));
        var system = new FakeBackend(SensitiveStorageBackendKind.SystemProtected)
        {
            VerifyFailure = new SensitiveStorageException(SensitiveStorageErrorCode.StructurallyUnavailable, "missing"),
        };
        var sqlCipher = new SqlCipherStorageBackend(runtime.Path, new LinuxMasterKeyStore(xdg.Path));
        var plaintext = new FakeBackend(SensitiveStorageBackendKind.Plaintext);
        var consent = new FakeConsent(true);
        var service = new SensitiveSettingsService([system, sqlCipher, plaintext], consent, isLinux: true);

        var error = await Assert.ThrowsAsync<SensitiveStorageException>(() => service.PrepareSaveAsync(ConfigWithSecret()));

        Assert.Equal(SensitiveStorageErrorCode.Corrupt, error.ErrorCode);
        Assert.Equal(0, consent.RequestCount);
        Assert.False(File.Exists(new LinuxMasterKeyStore(xdg.Path).KeyPath));
        Assert.Equal(0, plaintext.WriteCount);
    }

    [Fact]
    public async Task PlaintextBackendRetainsCurrentAndPreviousRevision()
    {
        using var directory = TestDirectory.Create();
        var backend = new PlaintextSensitiveSettingsBackend(directory.Path);
        await backend.WriteAsync(Document("store", 1, "first"), expectedRevision: 0);
        await backend.WriteAsync(Document("store", 2, "second"), expectedRevision: 1);

        Assert.Equal("first", (await backend.ReadAsync("store", 1)).Profiles["Default"][ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
        Assert.Equal("second", (await backend.ReadAsync("store", 2)).Profiles["Default"][ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(Path.Combine(directory.Path, "data", "sensitive-settings.json")));
        }

        await backend.DeleteAsync();
        Assert.False(File.Exists(Path.Combine(directory.Path, "data", "sensitive-settings.json")));
    }

    [Fact]
    public async Task PlaintextBackendRetainsOldCurrentWhenStoreIdChanges()
    {
        using var directory = TestDirectory.Create();
        var backend = new PlaintextSensitiveSettingsBackend(directory.Path);
        await backend.WriteAsync(Document("old-store", 4, "old-authoritative"), expectedRevision: 0);

        await backend.WriteAsync(Document("new-store", 1, "new-pending"), expectedRevision: 0);

        Assert.Equal(
            "old-authoritative",
            (await backend.ReadAsync("old-store", 4)).Profiles["Default"][ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
        Assert.Equal(
            "new-pending",
            (await backend.ReadAsync("new-store", 1)).Profiles["Default"][ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
    }

    [Fact]
    public async Task PlaintextBackendRejectsGroupReadableFileAsPermissionDenied()
    {
        if (OperatingSystem.IsWindows()) return;
        using var directory = TestDirectory.Create();
        var dataDirectory = Path.Combine(directory.Path, "data");
        var filePath = Path.Combine(dataDirectory, "sensitive-settings.json");
        Directory.CreateDirectory(dataDirectory);
        await File.WriteAllTextAsync(filePath, "{}");
        File.SetUnixFileMode(
            filePath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        var backend = new PlaintextSensitiveSettingsBackend(directory.Path);

        var error = await Assert.ThrowsAsync<SensitiveStorageException>(() => backend.VerifyPersistenceAsync());

        Assert.Equal(SensitiveStorageErrorCode.PermissionDenied, error.ErrorCode);
    }

    [Fact]
    public async Task PlaintextBackendRejectsSymbolicLinkAsPermissionDenied()
    {
        if (OperatingSystem.IsWindows()) return;
        using var directory = TestDirectory.Create();
        var dataDirectory = Path.Combine(directory.Path, "data");
        var targetPath = Path.Combine(directory.Path, "plaintext-target.json");
        var filePath = Path.Combine(dataDirectory, "sensitive-settings.json");
        Directory.CreateDirectory(dataDirectory);
        await File.WriteAllTextAsync(targetPath, "{}");
        File.SetUnixFileMode(targetPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.CreateSymbolicLink(filePath, targetPath);
        var backend = new PlaintextSensitiveSettingsBackend(directory.Path);

        var error = await Assert.ThrowsAsync<SensitiveStorageException>(() => backend.VerifyPersistenceAsync());

        Assert.Equal(SensitiveStorageErrorCode.PermissionDenied, error.ErrorCode);
    }

    [Fact]
    public async Task PlaintextBackendRejectsDirectoryAtFilePathAsPermissionDenied()
    {
        using var directory = TestDirectory.Create();
        Directory.CreateDirectory(Path.Combine(directory.Path, "data", "sensitive-settings.json"));
        var backend = new PlaintextSensitiveSettingsBackend(directory.Path);

        var error = await Assert.ThrowsAsync<SensitiveStorageException>(() => backend.VerifyPersistenceAsync());

        Assert.Equal(SensitiveStorageErrorCode.PermissionDenied, error.ErrorCode);
    }

    [Fact]
    public async Task PlaintextBackendClassifiesInvalidJsonAsCorrupt()
    {
        using var directory = TestDirectory.Create();
        var dataDirectory = Path.Combine(directory.Path, "data");
        var filePath = Path.Combine(dataDirectory, "sensitive-settings.json");
        Directory.CreateDirectory(dataDirectory);
        await File.WriteAllTextAsync(filePath, "{ invalid-json");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var backend = new PlaintextSensitiveSettingsBackend(directory.Path);

        var readError = await Assert.ThrowsAsync<SensitiveStorageException>(() => backend.ReadAsync("store", 1));
        var writeError = await Assert.ThrowsAsync<SensitiveStorageException>(() => backend.WriteAsync(Document("store", 1, "secret"), expectedRevision: 0));

        Assert.Equal(SensitiveStorageErrorCode.Corrupt, readError.ErrorCode);
        Assert.Equal(SensitiveStorageErrorCode.Corrupt, writeError.ErrorCode);
    }

    [Fact]
    public async Task LinuxMasterKeyStoreDoesNotRegenerateMissingDatabaseKey()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var directory = TestDirectory.Create();
        var keyStore = new LinuxMasterKeyStore(directory.Path);

        await Assert.ThrowsAsync<FileNotFoundException>(() => keyStore.GetOrCreateAsync(databaseExists: true));
        Assert.False(File.Exists(keyStore.KeyPath));
    }

    [Fact]
    public async Task PortableAppImageUpdateReopensExistingSqlCipherDocumentAndConfig()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var directory = TestDirectory.Create();
        const string sentinel = "APPIMAGE-UPDATE-SQLCIPHER-SENTINEL-8e43";
        const string storeId = "appimage-update-store";
        var portableRoot = Path.Combine(directory.Path, "portable");
        var appImagePath = Path.Combine(portableRoot, RuntimeLayout.LinuxPortableLauncherFileName);
        var firstMount = Path.Combine(directory.Path, ".mount_MAAUnified-v1");
        var secondMount = Path.Combine(directory.Path, ".mount_MAAUnified-v2");
        var firstExecutableDirectory = Path.Combine(firstMount, "usr", "lib", "maaunified", "bin");
        var secondExecutableDirectory = Path.Combine(secondMount, "usr", "lib", "maaunified", "bin");
        var xdgDataHome = Path.Combine(directory.Path, "xdg-data");
        var originalAppDir = Environment.GetEnvironmentVariable("APPDIR");

        try
        {
            Directory.CreateDirectory(Path.Combine(portableRoot, "bin"));
            Directory.CreateDirectory(Path.Combine(portableRoot, "resource"));
            Directory.CreateDirectory(firstExecutableDirectory);
            File.WriteAllText(appImagePath, "v1");
            File.WriteAllText(Path.Combine(portableRoot, "bin", "MAAUnified"), "app");
            File.WriteAllText(Path.Combine(portableRoot, "libMaaCore.so"), "core");
            Environment.SetEnvironmentVariable("APPDIR", firstMount);
            var firstRuntime = RuntimeLayout.ResolveRuntimeBaseDirectory(
                firstExecutableDirectory,
                linuxAppImagePath: appImagePath);
            Assert.Equal(Path.GetFullPath(portableRoot), firstRuntime);

            var keyStore = new LinuxMasterKeyStore(xdgDataHome);
            var firstBackend = new SqlCipherStorageBackend(firstRuntime, keyStore);
            await firstBackend.WriteAsync(Document(storeId, 1, sentinel), expectedRevision: 0);
            await new AvaloniaJsonConfigStore(firstRuntime).SaveAsync(
                ConfigForSelectedBackend(SensitiveStorageBackendKind.LinuxLocalSqlCipher, storeId, 1));

            Directory.CreateDirectory(secondExecutableDirectory);
            await File.WriteAllTextAsync(appImagePath, "v2");
            Environment.SetEnvironmentVariable("APPDIR", secondMount);
            var updatedRuntime = RuntimeLayout.ResolveRuntimeBaseDirectory(
                secondExecutableDirectory,
                linuxAppImagePath: appImagePath);
            Assert.Equal(firstRuntime, updatedRuntime);

            var reopenedBackend = new SqlCipherStorageBackend(updatedRuntime, new LinuxMasterKeyStore(xdgDataHome));
            var restarted = new UnifiedConfigurationService(
                new AvaloniaJsonConfigStore(updatedRuntime),
                new GuiNewJsonConfigImporter(),
                new GuiJsonConfigImporter(),
                new UiLogService(),
                updatedRuntime,
                new SensitiveSettingsService([reopenedBackend], new FakeConsent(false), isLinux: true));
            await restarted.LoadOrBootstrapAsync();

            Assert.Equal(
                sentinel,
                restarted.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
            Assert.True(File.Exists(Path.Combine(updatedRuntime, "data", "sensitive-settings.db")));
            Assert.True(File.Exists(keyStore.KeyPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable("APPDIR", originalAppDir);
        }
    }

    [Theory]
    [InlineData(488)] // 0750
    [InlineData(511)] // 0777
    public async Task LinuxMasterKeyStoreRejectsInsecureParentDirectoryMode(int mode)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var directory = TestDirectory.Create();
        var keyStore = new LinuxMasterKeyStore(directory.Path);
        var secureDirectory = Path.GetDirectoryName(keyStore.KeyPath)!;
        Directory.CreateDirectory(secureDirectory);
        File.SetUnixFileMode(secureDirectory, (UnixFileMode)mode);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => keyStore.GetOrCreateAsync(databaseExists: false));
        Assert.False(File.Exists(keyStore.KeyPath));
    }

    [Fact]
    public async Task LinuxMasterKeyStoreRejectsSymbolicLinkParentDirectory()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var directory = TestDirectory.Create();
        var keyStore = new LinuxMasterKeyStore(directory.Path);
        var secureDirectory = Path.GetDirectoryName(keyStore.KeyPath)!;
        var actualDirectory = Path.Combine(directory.Path, "actual-secure");
        Directory.CreateDirectory(Path.GetDirectoryName(secureDirectory)!);
        Directory.CreateDirectory(actualDirectory);
        File.SetUnixFileMode(actualDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.CreateSymbolicLink(secureDirectory, actualDirectory);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => keyStore.GetOrCreateAsync(databaseExists: false));
        Assert.False(File.Exists(keyStore.KeyPath));
    }

    [Fact]
    public async Task LinuxMasterKeyStoreRejectsSymbolicLinkInAncestorDirectory()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var directory = TestDirectory.Create();
        var actualDataHome = Path.Combine(directory.Path, "actual-data-home");
        var linkedDataHome = Path.Combine(directory.Path, "linked-data-home");
        Directory.CreateDirectory(actualDataHome);
        Directory.CreateSymbolicLink(linkedDataHome, actualDataHome);
        var keyStore = new LinuxMasterKeyStore(linkedDataHome);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => keyStore.GetOrCreateAsync(databaseExists: false));

        Assert.False(File.Exists(Path.Combine(actualDataHome, "MAAUnified", "secure", "sqlcipher.key")));
    }

    [Fact]
    public async Task LinuxMasterKeyStoreRejectsDirectoryAtKeyPath()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var directory = TestDirectory.Create();
        var keyStore = new LinuxMasterKeyStore(directory.Path);
        var secureDirectory = Path.GetDirectoryName(keyStore.KeyPath)!;
        Directory.CreateDirectory(secureDirectory);
        File.SetUnixFileMode(secureDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.CreateDirectory(keyStore.KeyPath);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => keyStore.GetOrCreateAsync(databaseExists: false));
    }

    [Fact]
    public async Task SqlCipherBackendReportsCipherAndDoesNotContainPlaintextSentinel()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var runtime = TestDirectory.Create();
        using var xdg = TestDirectory.Create();
        var backend = new SqlCipherStorageBackend(runtime.Path, new LinuxMasterKeyStore(xdg.Path));
        const string sentinel = "SENSITIVE-SQLCIPHER-SENTINEL-9eb79a";

        await backend.WriteAsync(Document("store", 1, sentinel), expectedRevision: 0);

        Assert.False(string.IsNullOrWhiteSpace(await backend.GetCipherVersionAsync()));
        Assert.Equal(sentinel, (await backend.ReadAsync("store", 1)).Profiles["Default"][ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
        var databasePath = Path.Combine(runtime.Path, "data", "sensitive-settings.db");
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(databasePath));
        var bytes = await File.ReadAllBytesAsync(databasePath);
        Assert.DoesNotContain(sentinel, System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);

        var reopened = new SqlCipherStorageBackend(runtime.Path, new LinuxMasterKeyStore(xdg.Path));
        Assert.Equal(
            sentinel,
            (await reopened.ReadAsync("store", 1)).Profiles["Default"][ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
    }

    [Theory]
    [InlineData((int)(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead))]
    [InlineData((int)(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead))]
    [InlineData((int)(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherExecute))]
    public async Task SqlCipherBackendRejectsExistingDatabaseWithGroupOrOtherPermissions(int rawMode)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var runtime = TestDirectory.Create();
        using var xdg = TestDirectory.Create();
        var backend = new SqlCipherStorageBackend(runtime.Path, new LinuxMasterKeyStore(xdg.Path));
        await backend.WriteAsync(Document("store", 1, "private"), expectedRevision: 0);
        var databasePath = Path.Combine(runtime.Path, "data", "sensitive-settings.db");
        File.SetUnixFileMode(databasePath, (UnixFileMode)rawMode);

        var error = await Assert.ThrowsAsync<SensitiveStorageException>(() => backend.ReadAsync("store", 1));

        Assert.Equal(SensitiveStorageErrorCode.PermissionDenied, error.ErrorCode);
    }

    [Fact]
    public async Task SqlCipherBackendClassifiesMalformedPayloadAsCorrupt()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var runtime = TestDirectory.Create();
        using var xdg = TestDirectory.Create();
        var keyStore = new LinuxMasterKeyStore(xdg.Path);
        var backend = new SqlCipherStorageBackend(runtime.Path, keyStore);
        await backend.WriteAsync(Document("store", 1, "private"), expectedRevision: 0);

        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(runtime.Path, "data", "sensitive-settings.db"),
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString()))
        {
            await connection.OpenAsync();
            var key = await File.ReadAllBytesAsync(keyStore.KeyPath);
            try
            {
                await using var keyCommand = connection.CreateCommand();
                keyCommand.CommandText = $"PRAGMA key = \"x'{Convert.ToHexString(key)}'\";";
                await keyCommand.ExecuteNonQueryAsync();
                await using var corruptCommand = connection.CreateCommand();
                corruptCommand.CommandText = "UPDATE payload SET data = X'7B' WHERE id = 1;";
                await corruptCommand.ExecuteNonQueryAsync();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }

        var error = await Assert.ThrowsAsync<SensitiveStorageException>(() => backend.ReadAsync("store", 1));

        Assert.Equal(SensitiveStorageErrorCode.Corrupt, error.ErrorCode);
    }

    [Fact]
    public async Task SqlCipherBackendRejectsStaleExpectedRevision()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var runtime = TestDirectory.Create();
        using var xdg = TestDirectory.Create();
        var backend = new SqlCipherStorageBackend(runtime.Path, new LinuxMasterKeyStore(xdg.Path));
        await backend.WriteAsync(Document("store", 1, "initial"), expectedRevision: 0);
        await backend.WriteAsync(Document("store", 2, "winner"), expectedRevision: 1);

        var error = await Assert.ThrowsAsync<SensitiveStorageException>(() =>
            backend.WriteAsync(Document("store", 2, "stale"), expectedRevision: 1));

        Assert.Equal(SensitiveStorageErrorCode.ActionRequired, error.ErrorCode);
        Assert.Equal(
            "winner",
            (await backend.ReadAsync("store", 2))
            .Profiles["Default"][ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
    }

    [Fact]
    public async Task SqlCipherBackendRetainsOldCurrentWhenStoreIdChanges()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var runtime = TestDirectory.Create();
        using var xdg = TestDirectory.Create();
        var backend = new SqlCipherStorageBackend(runtime.Path, new LinuxMasterKeyStore(xdg.Path));
        await backend.WriteAsync(Document("old-store", 4, "old-authoritative"), expectedRevision: 0);

        await backend.WriteAsync(Document("new-store", 1, "new-pending"), expectedRevision: 0);

        Assert.Equal(
            "old-authoritative",
            (await backend.ReadAsync("old-store", 4)).Profiles["Default"][ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
        Assert.Equal(
            "new-pending",
            (await backend.ReadAsync("new-store", 1)).Profiles["Default"][ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
    }

    [Fact]
    public async Task SqlCipherBackendRejectsDatabaseSymbolicLinkWithoutChangingExternalDatabase()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var externalRuntime = TestDirectory.Create();
        using var victimRuntime = TestDirectory.Create();
        using var xdg = TestDirectory.Create();
        var keyStore = new LinuxMasterKeyStore(xdg.Path);
        var external = new SqlCipherStorageBackend(externalRuntime.Path, keyStore);
        await external.WriteAsync(Document("store", 1, "external-sentinel"), expectedRevision: 0);
        var victimDataDirectory = Path.Combine(victimRuntime.Path, "data");
        Directory.CreateDirectory(victimDataDirectory);
        File.CreateSymbolicLink(
            Path.Combine(victimDataDirectory, "sensitive-settings.db"),
            Path.Combine(externalRuntime.Path, "data", "sensitive-settings.db"));
        var victim = new SqlCipherStorageBackend(victimRuntime.Path, keyStore);

        var error = await Assert.ThrowsAsync<SensitiveStorageException>(() =>
            victim.WriteAsync(Document("store", 2, "attacker-write"), expectedRevision: 1));

        Assert.Equal(SensitiveStorageErrorCode.PermissionDenied, error.ErrorCode);
        Assert.Equal(
            "external-sentinel",
            (await external.ReadAsync("store", 1)).Profiles["Default"][ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
    }

    [Fact]
    public async Task SqlCipherBackendRejectsDanglingDatabaseSymbolicLinkWithoutCreatingTarget()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var runtime = TestDirectory.Create();
        using var external = TestDirectory.Create();
        using var xdg = TestDirectory.Create();
        var dataDirectory = Path.Combine(runtime.Path, "data");
        var externalTarget = Path.Combine(external.Path, "outside.db");
        Directory.CreateDirectory(dataDirectory);
        File.CreateSymbolicLink(Path.Combine(dataDirectory, "sensitive-settings.db"), externalTarget);
        var keyStore = new LinuxMasterKeyStore(xdg.Path);
        var backend = new SqlCipherStorageBackend(runtime.Path, keyStore);

        var error = await Assert.ThrowsAsync<SensitiveStorageException>(() =>
            backend.WriteAsync(Document("store", 1, "must-not-escape"), expectedRevision: 0));

        Assert.Equal(SensitiveStorageErrorCode.PermissionDenied, error.ErrorCode);
        Assert.False(File.Exists(externalTarget));
        Assert.False(File.Exists(keyStore.KeyPath));
    }

    [Fact]
    public async Task SqlCipherBackendRejectsLinkedDataDirectoryWithoutChangingExternalDatabase()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var externalRuntime = TestDirectory.Create();
        using var victimRuntime = TestDirectory.Create();
        using var xdg = TestDirectory.Create();
        var keyStore = new LinuxMasterKeyStore(xdg.Path);
        var external = new SqlCipherStorageBackend(externalRuntime.Path, keyStore);
        await external.WriteAsync(Document("store", 1, "external-data-sentinel"), expectedRevision: 0);
        File.CreateSymbolicLink(
            Path.Combine(victimRuntime.Path, "data"),
            Path.Combine(externalRuntime.Path, "data"));
        var victim = new SqlCipherStorageBackend(victimRuntime.Path, keyStore);

        var error = await Assert.ThrowsAsync<SensitiveStorageException>(() =>
            victim.WriteAsync(Document("store", 2, "attacker-write"), expectedRevision: 1));

        Assert.Equal(SensitiveStorageErrorCode.PermissionDenied, error.ErrorCode);
        Assert.Equal(
            "external-data-sentinel",
            (await external.ReadAsync("store", 1)).Profiles["Default"][ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
    }

    [Fact]
    public async Task SqlCipherBackendRejectsFifoDatabasePath()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var runtime = TestDirectory.Create();
        using var xdg = TestDirectory.Create();
        var dataDirectory = Path.Combine(runtime.Path, "data");
        var databasePath = Path.Combine(dataDirectory, "sensitive-settings.db");
        Directory.CreateDirectory(dataDirectory);
        Assert.Equal(0, CreateFifo(databasePath, 0x180));
        var keyStore = new LinuxMasterKeyStore(xdg.Path);
        var backend = new SqlCipherStorageBackend(runtime.Path, keyStore);

        var error = await Assert.ThrowsAsync<SensitiveStorageException>(() => backend.VerifyPersistenceAsync());

        Assert.Equal(SensitiveStorageErrorCode.PermissionDenied, error.ErrorCode);
        Assert.False(File.Exists(keyStore.KeyPath));
    }

    [Fact]
    public async Task ConfigurationSaveRedactsJsonAndReloadInjectsSecrets()
    {
        using var directory = TestDirectory.Create();
        var backend = new FakeBackend(SensitiveStorageBackendKind.SystemProtected);
        var service = CreateConfigurationService(directory.Path, backend);
        const string sentinel = "CONFIG-SECRET-SENTINEL-7ef2";
        service.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity] = sentinel;
        service.CurrentConfig.Profiles["Default"].Values["ordinary"] = "visible";

        await service.SaveAsync();

        var json = await File.ReadAllTextAsync(Path.Combine(directory.Path, "config", "avalonia.json"));
        Assert.DoesNotContain(sentinel, json, StringComparison.Ordinal);
        Assert.Contains("visible", json, StringComparison.Ordinal);
        Assert.Equal(1, service.CurrentConfig.SensitiveStorage.Revision);

        var reloaded = CreateConfigurationService(directory.Path, backend);
        await reloaded.LoadOrBootstrapAsync();
        Assert.Equal(sentinel, reloaded.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
    }

    [Fact]
    public async Task FailedConfigCommitReloadsPreviousSensitiveRevision()
    {
        using var directory = TestDirectory.Create();
        var backend = new FakeBackend(SensitiveStorageBackendKind.SystemProtected);
        var innerStore = new AvaloniaJsonConfigStore(directory.Path);
        var failingStore = new FailNextSaveStore(innerStore);
        var service = CreateConfigurationService(directory.Path, backend, failingStore);
        service.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity] = "revision-one";
        await service.SaveAsync();

        service.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity] = "revision-two";
        failingStore.FailNextSave = true;
        await Assert.ThrowsAsync<IOException>(() => service.SaveAsync());

        var reloaded = CreateConfigurationService(directory.Path, backend);
        await reloaded.LoadOrBootstrapAsync();
        Assert.Equal(1, reloaded.CurrentConfig.SensitiveStorage.Revision);
        Assert.Equal("revision-one", reloaded.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
    }

    [Fact]
    public async Task FailedConfigCommitRetryRecoversMatchingPendingSensitiveRevision()
    {
        using var directory = TestDirectory.Create();
        var backend = new FakeBackend(SensitiveStorageBackendKind.SystemProtected);
        var innerStore = new AvaloniaJsonConfigStore(directory.Path);
        var failingStore = new FailNextSaveStore(innerStore);
        var service = CreateConfigurationService(directory.Path, backend, failingStore);
        service.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity] = "revision-one";
        await service.SaveAsync();
        service.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity] = "revision-two";
        failingStore.FailNextSave = true;
        await Assert.ThrowsAsync<IOException>(() => service.SaveAsync());
        Assert.Equal(2, backend.WriteCount);

        await service.SaveAsync();

        Assert.Equal(2, backend.WriteCount);
        Assert.Equal(2, service.CurrentConfig.SensitiveStorage.Revision);
        Assert.Equal(2, (await innerStore.LoadAsync())!.SensitiveStorage.Revision);
        var reloaded = CreateConfigurationService(directory.Path, backend);
        await reloaded.LoadOrBootstrapAsync();
        Assert.Equal("revision-two", reloaded.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
    }

    [Fact]
    public async Task OrdinarySettingsSaveWithUnchangedSecretsDoesNotWriteBackend()
    {
        using var directory = TestDirectory.Create();
        var backend = new FakeBackend(SensitiveStorageBackendKind.SystemProtected);
        var service = CreateConfigurationService(directory.Path, backend);
        service.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity] = "unchanged-secret";
        await service.SaveAsync();
        Assert.Equal(1, backend.WriteCount);
        service.CurrentConfig.GlobalValues["ordinary-setting"] = "changed";

        await service.SaveAsync();

        Assert.Equal(1, backend.WriteCount);
        var persisted = await new AvaloniaJsonConfigStore(directory.Path).LoadAsync();
        Assert.Equal("changed", persisted!.GlobalValues["ordinary-setting"]!.ToString());
        Assert.Equal(1, persisted.SensitiveStorage.Revision);
    }

    [Fact]
    public async Task RepeatedFailedManualImportsKeepOldSecretsAuthoritativeWithoutAdvancingRevision()
    {
        using var directory = TestDirectory.Create();
        var innerStore = new AvaloniaJsonConfigStore(directory.Path);
        var failingStore = new FailNextSaveStore(innerStore);
        var backend = new PlaintextSensitiveSettingsBackend(directory.Path);
        var sensitiveService = new SensitiveSettingsService([backend], new FakeConsent(true), isLinux: false);
        var service = new UnifiedConfigurationService(
            failingStore,
            new GuiNewJsonConfigImporter(),
            new GuiJsonConfigImporter(),
            new UiLogService(),
            directory.Path,
            sensitiveService);
        service.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity] = "old-authoritative";
        await service.SaveAsync();
        var originalStoreId = service.CurrentConfig.SensitiveStorage.StoreId;
        Assert.Equal(1, service.CurrentConfig.SensitiveStorage.Revision);

        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, "config", "gui.json"),
            $$"""
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "{{ConfigurationKeys.RemoteControlUserIdentity}}": "new-imported"
                }
              }
            }
            """);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            failingStore.FailNextSave = true;
            var report = await service.ImportLegacyAsync(ImportSource.GuiOnly, manualImport: true);
            Assert.False(report.AppliedConfig);
        }

        var reloaded = new UnifiedConfigurationService(
            innerStore,
            new GuiNewJsonConfigImporter(),
            new GuiJsonConfigImporter(),
            new UiLogService(),
            directory.Path,
            new SensitiveSettingsService([backend], new FakeConsent(true), isLinux: false));
        await reloaded.LoadOrBootstrapAsync();

        Assert.Equal(originalStoreId, reloaded.CurrentConfig.SensitiveStorage.StoreId);
        Assert.Equal(1, reloaded.CurrentConfig.SensitiveStorage.Revision);
        Assert.Equal(
            "old-authoritative",
            reloaded.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity]!.ToString());

        var successfulReport = await reloaded.ImportLegacyAsync(ImportSource.GuiOnly, manualImport: true);
        Assert.True(successfulReport.AppliedConfig);
        Assert.Equal(originalStoreId, reloaded.CurrentConfig.SensitiveStorage.StoreId);
        Assert.Equal(2, reloaded.CurrentConfig.SensitiveStorage.Revision);
        Assert.Equal(
            "new-imported",
            reloaded.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
    }

    [Fact]
    public async Task LegacyMigrationSanitizesConfigAndSchemaBackup()
    {
        using var directory = TestDirectory.Create();
        Directory.CreateDirectory(Path.Combine(directory.Path, "config"));
        const string sentinel = "LEGACY-SECRET-SENTINEL-bec8";
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, "config", "avalonia.json"),
            $$"""
            {
              "SchemaVersion": 2,
              "CurrentProfile": "Default",
              "Profiles": {
                "Default": {
                  "Values": {
                    "{{ConfigurationKeys.RemoteControlUserIdentity}}": "{{sentinel}}"
                  },
                  "TaskQueue": []
                }
              },
              "GlobalValues": {}
            }
            """);
        var service = CreateConfigurationService(directory.Path, new FakeBackend(SensitiveStorageBackendKind.SystemProtected));

        await service.LoadOrBootstrapAsync();

        Assert.Equal(sentinel, service.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
        Assert.DoesNotContain(sentinel, await File.ReadAllTextAsync(Path.Combine(directory.Path, "config", "avalonia.json")), StringComparison.Ordinal);
        var backup = Assert.Single(Directory.EnumerateFiles(Path.Combine(directory.Path, "config"), "avalonia.json.schema-v2.bak.*"));
        Assert.DoesNotContain(sentinel, await File.ReadAllTextAsync(backup), StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(backup));
        }
    }

    [Theory]
    [InlineData(".bak.20240102030405")]
    [InlineData(".schema-v2.bak.20240102030405")]
    public async Task SanitizedBackupDoesNotFollowExistingFileLink(string suffix)
    {
        if (OperatingSystem.IsWindows()) return;
        using var directory = TestDirectory.Create();
        const string sentinel = "BACKUP-LINK-TARGET-SENTINEL-8c61";
        var store = new AvaloniaJsonConfigStore(directory.Path);
        Directory.CreateDirectory(store.ConfigDirectory);
        var targetPath = Path.Combine(directory.Path, "outside-target.json");
        var requestedBackupPath = store.ConfigPath + suffix;
        await File.WriteAllTextAsync(targetPath, sentinel);
        File.CreateSymbolicLink(requestedBackupPath, "../outside-target.json");
        var config = new UnifiedConfig();
        config.GlobalValues["ordinary"] = "safe-backup-value";

        await store.BackupSanitizedAsync(config, suffix);

        Assert.Equal(sentinel, await File.ReadAllTextAsync(targetPath));
        Assert.NotNull(new FileInfo(requestedBackupPath).LinkTarget);
        var publishedBackup = Assert.Single(
            Directory.EnumerateFiles(store.ConfigDirectory, $"{Path.GetFileName(requestedBackupPath)}.*"),
            path => new FileInfo(path).LinkTarget is null);
        Assert.Contains("safe-backup-value", await File.ReadAllTextAsync(publishedBackup), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SanitizedBackupDoesNotReplaceExistingDirectoryLink()
    {
        if (OperatingSystem.IsWindows()) return;
        using var directory = TestDirectory.Create();
        const string suffix = ".bak.20240102030405";
        const string sentinel = "BACKUP-DIRECTORY-TARGET-SENTINEL-c3f7";
        var store = new AvaloniaJsonConfigStore(directory.Path);
        Directory.CreateDirectory(store.ConfigDirectory);
        var targetDirectory = Path.Combine(directory.Path, "outside-target-directory");
        Directory.CreateDirectory(targetDirectory);
        var sentinelPath = Path.Combine(targetDirectory, "sentinel.txt");
        await File.WriteAllTextAsync(sentinelPath, sentinel);
        var requestedBackupPath = store.ConfigPath + suffix;
        Directory.CreateSymbolicLink(requestedBackupPath, "../outside-target-directory");

        await store.BackupSanitizedAsync(new UnifiedConfig(), suffix);

        Assert.Equal(sentinel, await File.ReadAllTextAsync(sentinelPath));
        Assert.NotNull(new DirectoryInfo(requestedBackupPath).LinkTarget);
        Assert.Single(Directory.EnumerateFiles(targetDirectory));
        Assert.Single(Directory.EnumerateFiles(store.ConfigDirectory, $"{Path.GetFileName(requestedBackupPath)}.*"));
    }

    [Fact]
    public async Task BackupApisCannotCopyRegisteredSensitivePlaintext()
    {
        using var directory = TestDirectory.Create();
        const string profileSecret = "DIRECT-BACKUP-PROFILE-SECRET";
        const string globalSecret = "DIRECT-BACKUP-GLOBAL-SECRET";
        const string suffix = ".bak.20240102030405";
        var store = new AvaloniaJsonConfigStore(directory.Path);
        var config = new UnifiedConfig();
        config.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity] = profileSecret;
        config.GlobalValues[ConfigurationKeys.MirrorChyanCdk] = globalSecret;
        await store.SaveAsync(config);

        await Assert.ThrowsAsync<NotSupportedException>(() => store.BackupAsync(suffix));
        Assert.False(File.Exists(store.ConfigPath + suffix));

        await store.BackupSanitizedAsync(config, suffix);

        var backup = await File.ReadAllTextAsync(store.ConfigPath + suffix);
        Assert.DoesNotContain(profileSecret, backup, StringComparison.Ordinal);
        Assert.DoesNotContain(globalSecret, backup, StringComparison.Ordinal);

        IUnifiedConfigStore legacyStore = new RawBackupOnlyStore(store.ConfigPath);
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            legacyStore.BackupSanitizedAsync(config, ".bak.20240102030406"));
        Assert.False(File.Exists(store.ConfigPath + ".bak.20240102030406"));
    }

    [Fact]
    public async Task SanitizedBackupRejectsLinkedConfigDirectoryWithoutTouchingExternalFiles()
    {
        if (OperatingSystem.IsWindows()) return;
        using var directory = TestDirectory.Create();
        const string sentinel = "BACKUP-DIRECTORY-LINK-SENTINEL-a974";
        var linkedTarget = Path.Combine(directory.Path, "outside-config");
        Directory.CreateDirectory(linkedTarget);
        var sentinelPath = Path.Combine(linkedTarget, "sentinel.txt");
        await File.WriteAllTextAsync(sentinelPath, sentinel);
        File.CreateSymbolicLink(Path.Combine(directory.Path, "config"), "outside-config");
        var store = new AvaloniaJsonConfigStore(directory.Path);

        await Assert.ThrowsAsync<IOException>(() =>
            store.BackupSanitizedAsync(new UnifiedConfig(), ".bak.20240102030405"));

        Assert.Equal(sentinel, await File.ReadAllTextAsync(sentinelPath));
        Assert.Single(Directory.EnumerateFiles(linkedTarget));
    }

    [Fact]
    public async Task SanitizedBackupPreservesRegularCollisionAndPublishesConcurrentSameSecondBackups()
    {
        using var directory = TestDirectory.Create();
        const string suffix = ".bak.20240102030405";
        const string existingContents = "EXISTING-BACKUP-COLLISION-SENTINEL-491e";
        var store = new AvaloniaJsonConfigStore(directory.Path);
        Directory.CreateDirectory(store.ConfigDirectory);
        var requestedBackupPath = store.ConfigPath + suffix;
        await File.WriteAllTextAsync(requestedBackupPath, existingContents);
        var configs = Enumerable.Range(0, 8)
            .Select(index =>
            {
                var config = new UnifiedConfig();
                config.GlobalValues["ordinary"] = $"same-second-{index}";
                return config;
            })
            .ToArray();

        await Task.WhenAll(configs.Select(config => store.BackupSanitizedAsync(config, suffix)));

        Assert.Equal(existingContents, await File.ReadAllTextAsync(requestedBackupPath));
        var backups = Directory
            .EnumerateFiles(store.ConfigDirectory, $"{Path.GetFileName(requestedBackupPath)}*")
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(configs.Length + 1, backups.Length);
        var publishedContents = await Task.WhenAll(backups
            .Where(path => !string.Equals(path, requestedBackupPath, StringComparison.Ordinal))
            .Select(path => File.ReadAllTextAsync(path)));
        foreach (var index in Enumerable.Range(0, configs.Length))
        {
            Assert.Contains(publishedContents, contents => contents.Contains($"same-second-{index}", StringComparison.Ordinal));
        }

        Assert.DoesNotContain(Directory.EnumerateFiles(store.ConfigDirectory), path => path.EndsWith(".tmp", StringComparison.Ordinal));
        if (!OperatingSystem.IsWindows())
        {
            foreach (var backup in backups.Where(path => !string.Equals(path, requestedBackupPath, StringComparison.Ordinal)))
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(backup));
            }
        }
    }

    [Fact]
    public async Task ExplicitPlaintextRetryMigratesToSystemBackend()
    {
        using var directory = TestDirectory.Create();
        var system = new FakeBackend(SensitiveStorageBackendKind.SystemProtected);
        var plaintext = new FakeBackend(SensitiveStorageBackendKind.Plaintext);
        plaintext.Seed(Document("plaintext-store", 3, "retry-secret"));
        var sensitiveService = new SensitiveSettingsService([system, plaintext], new FakeConsent(true), isLinux: false);
        var configService = new UnifiedConfigurationService(
            new AvaloniaJsonConfigStore(directory.Path),
            new GuiNewJsonConfigImporter(),
            new GuiJsonConfigImporter(),
            new UiLogService(),
            directory.Path,
            sensitiveService);
        configService.CurrentConfig.SensitiveStorage = new SensitiveStorageMetadata
        {
            Backend = SensitiveStorageBackendKind.Plaintext,
            StoreId = "plaintext-store",
            Revision = 3,
        };
        configService.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity] = "retry-secret";

        await configService.RetrySecureStorageAsync();

        Assert.Equal(SensitiveStorageBackendKind.SystemProtected, configService.CurrentConfig.SensitiveStorage.Backend);
        Assert.Equal(4, configService.CurrentConfig.SensitiveStorage.Revision);
        Assert.Equal(1, system.VerifyCount);
        Assert.Equal(1, system.WriteCount);
        Assert.Equal(1, plaintext.DeleteCount);
        var json = await File.ReadAllTextAsync(Path.Combine(directory.Path, "config", "avalonia.json"));
        Assert.DoesNotContain("retry-secret", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, SensitiveStorageErrorCode.Unavailable, SensitiveStorageState.Unavailable)]
    [InlineData(false, SensitiveStorageErrorCode.Corrupt, SensitiveStorageState.Corrupt)]
    [InlineData(true, SensitiveStorageErrorCode.Unavailable, SensitiveStorageState.Unavailable)]
    [InlineData(true, SensitiveStorageErrorCode.Corrupt, SensitiveStorageState.Corrupt)]
    public async Task MigratedBackendFailureStillAllowsOnlyRedactedOrdinarySettingsSave(
        bool targetRevisionAlreadyExists,
        SensitiveStorageErrorCode errorCode,
        SensitiveStorageState expectedState)
    {
        using var directory = TestDirectory.Create();
        const string storeId = "plaintext-store";
        const string secret = "POST-MIGRATION-SECRET-SENTINEL";
        var store = new AvaloniaJsonConfigStore(directory.Path);
        await store.SaveAsync(ConfigForSelectedBackend(SensitiveStorageBackendKind.Plaintext, storeId, 3));
        var plaintext = new FakeBackend(SensitiveStorageBackendKind.Plaintext);
        plaintext.Seed(Document(storeId, 3, secret));
        var system = new FakeBackend(SensitiveStorageBackendKind.SystemProtected);
        if (targetRevisionAlreadyExists)
        {
            system.Seed(Document(storeId, 4, secret));
        }

        var sensitive = new SensitiveSettingsService([system, plaintext], new FakeConsent(true), isLinux: false);
        var configService = new UnifiedConfigurationService(
            store,
            new GuiNewJsonConfigImporter(),
            new GuiJsonConfigImporter(),
            new UiLogService(),
            directory.Path,
            sensitive);
        await configService.LoadOrBootstrapAsync();
        await configService.RetrySecureStorageAsync();
        Assert.Equal(targetRevisionAlreadyExists ? 0 : 1, system.WriteCount);

        sensitive.RecordFailure(new SensitiveStorageException(errorCode, "migrated backend failed"));
        var readCount = system.ReadCount;
        var writeCount = system.WriteCount;
        configService.CurrentConfig.GlobalValues["ordinary-after-migration"] = "saved";

        await configService.SaveAsync();

        Assert.Equal(expectedState, configService.SensitiveStorageState);
        Assert.Equal(errorCode, configService.SensitiveStorageErrorCode);
        Assert.Equal(readCount, system.ReadCount);
        Assert.Equal(writeCount, system.WriteCount);
        Assert.Equal(SensitiveStorageBackendKind.SystemProtected, configService.CurrentConfig.SensitiveStorage.Backend);
        Assert.Equal(storeId, configService.CurrentConfig.SensitiveStorage.StoreId);
        Assert.Equal(4, configService.CurrentConfig.SensitiveStorage.Revision);
        Assert.Equal(
            secret,
            configService.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
        var persisted = await store.LoadAsync();
        Assert.NotNull(persisted);
        Assert.Equal("saved", persisted!.GlobalValues["ordinary-after-migration"]!.ToString());
        Assert.Equal(SensitiveStorageBackendKind.SystemProtected, persisted.SensitiveStorage.Backend);
        Assert.Equal(storeId, persisted.SensitiveStorage.StoreId);
        Assert.Equal(4, persisted.SensitiveStorage.Revision);
        Assert.False(persisted.Profiles["Default"].Values.ContainsKey(ConfigurationKeys.RemoteControlUserIdentity));
        var persistedJson = await File.ReadAllTextAsync(store.ConfigPath);
        Assert.DoesNotContain(secret, persistedJson, StringComparison.Ordinal);

        configService.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity] = "changed-secret";
        var error = await Assert.ThrowsAsync<SensitiveStorageException>(() => configService.SaveAsync());

        Assert.Equal(SensitiveStorageErrorCode.ActionRequired, error.ErrorCode);
        Assert.Equal(persistedJson, await File.ReadAllTextAsync(store.ConfigPath));
        Assert.Equal(readCount, system.ReadCount);
        Assert.Equal(writeCount, system.WriteCount);
        Assert.Equal(
            secret,
            (await system.ReadAsync(storeId, 4)).Profiles["Default"][ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
    }

    [Fact]
    public async Task ExplicitPlaintextRetryOnLinuxUsesSqlCipherAfterStructuralSystemFailure()
    {
        using var directory = TestDirectory.Create();
        var system = new FakeBackend(SensitiveStorageBackendKind.SystemProtected)
        {
            VerifyFailure = new SensitiveStorageException(SensitiveStorageErrorCode.StructurallyUnavailable, "no secret service"),
        };
        var sqlCipher = new FakeBackend(SensitiveStorageBackendKind.LinuxLocalSqlCipher);
        var plaintext = new FakeBackend(SensitiveStorageBackendKind.Plaintext);
        plaintext.Seed(Document("plaintext-store", 3, "retry-secret"));
        var sensitiveService = new SensitiveSettingsService([system, sqlCipher, plaintext], new FakeConsent(true), isLinux: true);
        var configService = new UnifiedConfigurationService(
            new AvaloniaJsonConfigStore(directory.Path),
            new GuiNewJsonConfigImporter(),
            new GuiJsonConfigImporter(),
            new UiLogService(),
            directory.Path,
            sensitiveService);
        configService.CurrentConfig.SensitiveStorage = new SensitiveStorageMetadata
        {
            Backend = SensitiveStorageBackendKind.Plaintext,
            StoreId = "plaintext-store",
            Revision = 3,
        };
        configService.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity] = "retry-secret";

        await configService.RetrySecureStorageAsync();

        Assert.Equal(SensitiveStorageBackendKind.LinuxLocalSqlCipher, configService.CurrentConfig.SensitiveStorage.Backend);
        Assert.Equal(1, system.VerifyCount);
        Assert.Equal(1, sqlCipher.VerifyCount);
        Assert.Equal(1, sqlCipher.WriteCount);
        Assert.Equal(1, plaintext.DeleteCount);
    }

    [Fact]
    public async Task ExplicitPlaintextRetryOnLinuxDoesNotDowngradeAfterTransientSystemFailure()
    {
        var system = new FakeBackend(SensitiveStorageBackendKind.SystemProtected)
        {
            VerifyFailure = new SensitiveStorageException(SensitiveStorageErrorCode.Unavailable, "keyring locked"),
        };
        var sqlCipher = new FakeBackend(SensitiveStorageBackendKind.LinuxLocalSqlCipher);
        var plaintext = new FakeBackend(SensitiveStorageBackendKind.Plaintext);
        var service = new SensitiveSettingsService([system, sqlCipher, plaintext], new FakeConsent(true), isLinux: true);
        var config = ConfigForSelectedBackend(SensitiveStorageBackendKind.Plaintext, "plaintext-store", 3);
        config.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity] = "retry-secret";

        var error = await Assert.ThrowsAsync<SensitiveStorageException>(
            () => service.PrepareSecureStorageMigrationAsync(config));

        Assert.Equal(SensitiveStorageErrorCode.Unavailable, error.ErrorCode);
        Assert.Equal(SensitiveStorageBackendKind.Plaintext, config.SensitiveStorage.Backend);
        Assert.Equal(0, sqlCipher.VerifyCount);
        Assert.Equal(0, sqlCipher.WriteCount);
    }

    [Fact]
    public async Task IssueReportExcludesSensitiveFilesAndRedactsConfigAndLogs()
    {
        using var directory = TestDirectory.Create();
        var configDirectory = Path.Combine(directory.Path, "config");
        Directory.CreateDirectory(configDirectory);
        const string sentinel = "ISSUE/REPORT?SECRET+SENTINEL-52ca";
        const string encodedSentinel = "ISSUE%2FREPORT%3FSECRET%2BSENTINEL-52ca";
        const string shortSentinel = "q7";
        const string numericLikeSecret = "25";
        const string exifMetadataSentinel = "EXIF_UTF16_METADATA_SENTINEL";
        const string xmpMetadataSentinel = "XMP_METADATA_SENTINEL";
        const string pngMetadataSentinel = "PNG_ZTXT_METADATA_SENTINEL";
        const string entryNameSecret = "ENTRY NAME SECRET SENTINEL";
        const string unixEntryUserSentinel = "ENTRY_UNIX_USER_SENTINEL";
        const string macEntryUserSentinel = "ENTRY_MAC_USER_SENTINEL";
        const string windowsEntryUserSentinel = "ENTRY_WINDOWS_USER_SENTINEL";
        const string jsonKeySecret = "JSON/KEY?SECRET+SENTINEL";
        const string jsonKeyUserSentinel = "JSON_KEY_USER_SENTINEL";
        var encodedEntryNameSecret = Uri.EscapeDataString(entryNameSecret);
        var encodedJsonKeySecret = Uri.EscapeDataString(jsonKeySecret);
        var config = ConfigWithSecret();
        config.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity] = sentinel;
        await new AvaloniaJsonConfigStore(directory.Path).SaveAsync(config);
        await File.WriteAllTextAsync(Path.Combine(configDirectory, "gui.json"), sentinel);
        await File.WriteAllTextAsync(Path.Combine(configDirectory, "avalonia.json.bak.old"), sentinel);
        await File.WriteAllTextAsync(Path.Combine(configDirectory, "sensitive-settings.json"), sentinel);
        await File.WriteAllTextAsync(Path.Combine(configDirectory, "sensitive-settings.db"), sentinel);
        Directory.CreateDirectory(Path.Combine(directory.Path, "debug", "nested"));
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "debug", "nested", "trace.txt"), sentinel);
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, "debug", "config-import-report.json"),
            "{\"port\":25,\"password\":\"25\",\"enabled\":true,\"optional\":null}");
        var keyedJson = new JsonObject
        {
            [jsonKeySecret] = true,
            [encodedJsonKeySecret] = 42,
            [SensitiveDataRedactor.Replacement] = false,
            ["nested"] = new JsonObject
            {
                [jsonKeySecret] = null,
            },
            ["items"] = new JsonArray
            {
                new JsonObject
                {
                    [jsonKeySecret] = "ordinary string",
                },
            },
            [$"/Users/{jsonKeyUserSentinel}/file"] = "ordinary path-key value",
        };
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, "debug", "nested", "keyed.json"),
            keyedJson.ToJsonString());
        var cacheDirectory = Path.Combine(directory.Path, "cache");
        Directory.CreateDirectory(cacheDirectory);
        await File.WriteAllTextAsync(Path.Combine(cacheDirectory, "cached-response.txt"), $"{sentinel}:{encodedSentinel}:{shortSentinel}");
        await File.WriteAllTextAsync(Path.Combine(cacheDirectory, "safe-diagnostic.txt"), "safe diagnostic text");
        await File.WriteAllTextAsync(
            Path.Combine(cacheDirectory, "utf16-diagnostic.txt"),
            $"secret={sentinel}; path={directory.Path}/private/file.txt",
            Encoding.Unicode);
        await File.WriteAllTextAsync(Path.Combine(cacheDirectory, $"{entryNameSecret}.txt"), "raw secret file name");
        await File.WriteAllTextAsync(Path.Combine(cacheDirectory, $"{encodedEntryNameSecret}.txt"), "encoded secret file name");
        await File.WriteAllTextAsync(Path.Combine(cacheDirectory, $"{SensitiveDataRedactor.Replacement}.txt"), "collision file name");
        var unixEntryDirectory = Path.Combine(cacheDirectory, "home", unixEntryUserSentinel);
        var macEntryDirectory = Path.Combine(cacheDirectory, "Users", macEntryUserSentinel);
        var windowsEntryDirectory = Path.Combine(cacheDirectory, "C", "Users", windowsEntryUserSentinel);
        Directory.CreateDirectory(unixEntryDirectory);
        Directory.CreateDirectory(macEntryDirectory);
        Directory.CreateDirectory(windowsEntryDirectory);
        await File.WriteAllTextAsync(Path.Combine(unixEntryDirectory, "report.txt"), "unix user path");
        await File.WriteAllTextAsync(Path.Combine(macEntryDirectory, "report.txt"), "mac user path");
        await File.WriteAllTextAsync(Path.Combine(windowsEntryDirectory, "report.txt"), "windows user path");
        Directory.CreateDirectory(Path.Combine(cacheDirectory, "images"));
        await File.WriteAllBytesAsync(
            Path.Combine(cacheDirectory, "images", "private-exif.jpg"),
            BuildJpegWithExifUserComment(exifMetadataSentinel));
        await File.WriteAllBytesAsync(
            Path.Combine(cacheDirectory, "images", "private-xmp.webp"),
            BuildWebpWithXmp(xmpMetadataSentinel));
        await File.WriteAllBytesAsync(
            Path.Combine(cacheDirectory, "images", "private-ztxt.png"),
            BuildPngWithCompressedText(pngMetadataSentinel));
        var log = new UiLogService();
        var diagnostics = new UiDiagnosticsService(directory.Path, log);
        diagnostics.SetSensitiveValueProvider(() =>
            [sentinel, shortSentinel, numericLikeSecret, entryNameSecret, jsonKeySecret]);
        await diagnostics.RecordErrorAsync("Sentinel", $"{sentinel}:{encodedSentinel}:{shortSentinel} path={directory.Path}/users/LOCAL_USER_SENTINEL/file.txt");

        var bundle = await diagnostics.BuildIssueReportBundleAsync(directory.Path);

        using var archive = ZipFile.OpenRead(bundle);
        var entryNames = archive.Entries.Select(static entry => entry.FullName).ToArray();
        Assert.Equal(entryNames.Length, entryNames.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("cache/[REDACTED].txt", entryNames);
        Assert.Contains("cache/[REDACTED]~2.txt", entryNames);
        Assert.Contains("cache/[REDACTED]~3.txt", entryNames);
        foreach (var entryName in entryNames)
        {
            Assert.DoesNotContain(entryNameSecret, entryName, StringComparison.Ordinal);
            Assert.DoesNotContain(encodedEntryNameSecret, entryName, StringComparison.Ordinal);
            Assert.DoesNotContain(unixEntryUserSentinel, entryName, StringComparison.Ordinal);
            Assert.DoesNotContain(macEntryUserSentinel, entryName, StringComparison.Ordinal);
            Assert.DoesNotContain(windowsEntryUserSentinel, entryName, StringComparison.Ordinal);
            Assert.DoesNotContain("\\", entryName, StringComparison.Ordinal);
            Assert.False(entryName.StartsWith("/", StringComparison.Ordinal));
            Assert.DoesNotContain(entryName, static value => char.IsControl(value) || value is '<' or '>' or ':' or '"' or '|' or '?' or '*');
            Assert.DoesNotContain(entryName.Split('/'), static segment => string.IsNullOrEmpty(segment) || segment is "." or "..");
        }

        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Contains("gui.json", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Contains("sensitive-settings", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Contains(".bak", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName == "cache/images/private-exif.jpg");
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName == "cache/images/private-xmp.webp");
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName == "cache/images/private-ztxt.png");
        Assert.Contains(archive.Entries, entry => entry.FullName == "cache/cached-response.txt");
        Assert.Contains(archive.Entries, entry => entry.FullName == "cache/safe-diagnostic.txt");
        Assert.Contains(archive.Entries, entry => entry.FullName == "cache/utf16-diagnostic.txt");
        foreach (var entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            var content = await reader.ReadToEndAsync();
            Assert.DoesNotContain(sentinel, content, StringComparison.Ordinal);
            Assert.DoesNotContain(encodedSentinel, content, StringComparison.Ordinal);
            Assert.DoesNotContain(shortSentinel, content, StringComparison.Ordinal);
            Assert.DoesNotContain("LOCAL_USER_SENTINEL", content, StringComparison.Ordinal);
            Assert.DoesNotContain(directory.Path, content, StringComparison.Ordinal);
            Assert.DoesNotContain(exifMetadataSentinel, content, StringComparison.Ordinal);
            Assert.DoesNotContain(xmpMetadataSentinel, content, StringComparison.Ordinal);
            Assert.DoesNotContain(pngMetadataSentinel, content, StringComparison.Ordinal);
            Assert.DoesNotContain(jsonKeySecret, content, StringComparison.Ordinal);
            Assert.DoesNotContain(encodedJsonKeySecret, content, StringComparison.Ordinal);
            Assert.DoesNotContain(jsonKeyUserSentinel, content, StringComparison.Ordinal);
            if (entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                _ = JsonNode.Parse(content) ?? throw new Xunit.Sdk.XunitException($"JSON entry `{entry.FullName}` was null.");
            }
        }

        var importEntry = Assert.Single(archive.Entries, entry => entry.FullName == "debug/config-import-report.json");
        using var importReader = new StreamReader(importEntry.Open());
        var importJson = JsonNode.Parse(await importReader.ReadToEndAsync())!.AsObject();
        Assert.Equal(25, importJson["port"]!.GetValue<int>());
        Assert.Equal(SensitiveDataRedactor.Replacement, importJson["password"]!.GetValue<string>());
        Assert.True(importJson["enabled"]!.GetValue<bool>());
        Assert.Null(importJson["optional"]);
        var safeEntry = Assert.Single(archive.Entries, entry => entry.FullName == "cache/safe-diagnostic.txt");
        using var safeReader = new StreamReader(safeEntry.Open());
        Assert.Equal("safe diagnostic text", await safeReader.ReadToEndAsync());
        var utf16Entry = Assert.Single(archive.Entries, entry => entry.FullName == "cache/utf16-diagnostic.txt");
        using var utf16Reader = new StreamReader(utf16Entry.Open());
        Assert.Equal(
            $"secret={SensitiveDataRedactor.Replacement}; path={DiagnosticDataSanitizer.RuntimeRootReplacement}/private/file.txt",
            await utf16Reader.ReadToEndAsync());
        var keyedEntry = Assert.Single(archive.Entries, entry => entry.FullName == "debug/nested/keyed.json");
        using var keyedReader = new StreamReader(keyedEntry.Open());
        var sanitizedKeyedJson = JsonNode.Parse(await keyedReader.ReadToEndAsync())!.AsObject();
        Assert.True(sanitizedKeyedJson[SensitiveDataRedactor.Replacement]!.GetValue<bool>());
        Assert.Equal(42, sanitizedKeyedJson[$"{SensitiveDataRedactor.Replacement}~2"]!.GetValue<int>());
        Assert.False(sanitizedKeyedJson[$"{SensitiveDataRedactor.Replacement}~3"]!.GetValue<bool>());
        Assert.Null(sanitizedKeyedJson["nested"]![SensitiveDataRedactor.Replacement]);
        Assert.Equal(
            "ordinary string",
            sanitizedKeyedJson["items"]![0]![SensitiveDataRedactor.Replacement]!.GetValue<string>());
        Assert.Equal("ordinary path-key value", sanitizedKeyedJson["$HOME/file"]!.GetValue<string>());
    }

    [Fact]
    public async Task IssueReportStructurallyRedactsHistoricalUrlAndAuthorizationCredentials()
    {
        using var directory = TestDirectory.Create();
        var configDirectory = Path.Combine(directory.Path, "config");
        var debugDirectory = Path.Combine(directory.Path, "debug");
        var cacheDirectory = Path.Combine(directory.Path, "cache");
        Directory.CreateDirectory(configDirectory);
        Directory.CreateDirectory(debugDirectory);
        Directory.CreateDirectory(cacheDirectory);

        const string historicalCdk = "HISTORICAL/CDK+SENTINEL-A";
        const string currentCdk = "CURRENT-CDK-SENTINEL-B";
        const string historicalAuthorization = "HISTORICAL-AUTHORIZATION-SENTINEL";
        const string fragmentCredential = "HISTORICAL-FRAGMENT-SENTINEL";
        var encodedHistoricalCdk = Uri.EscapeDataString(historicalCdk);
        var config = new UnifiedConfig();
        config.GlobalValues[ConfigurationKeys.MirrorChyanCdk] = currentCdk;
        await new AvaloniaJsonConfigStore(directory.Path).SaveAsync(config);

        var diagnostics = new UiDiagnosticsService(directory.Path, new UiLogService());
        diagnostics.SetSensitiveValueProvider(() => [currentCdk]);
        await File.WriteAllTextAsync(
            diagnostics.StartupLogPath,
            $"old request URL: https://mirror.example.test/resource?C%44K={encodedHistoricalCdk}&channel=stable#access_token={fragmentCredential}\n");
        await File.WriteAllTextAsync(
            diagnostics.ErrorLogPath,
            $"Authorization: Bearer {historicalAuthorization}\n");
        await File.WriteAllTextAsync(
            Path.Combine(cacheDirectory, "historical-response.txt"),
            $"embedded=(https://user:password@example.test/api?cdk={historicalCdk}&version=1#route?token={fragmentCredential}) normal=https://example.test/status#healthy");

        var bundle = await diagnostics.BuildIssueReportBundleAsync(directory.Path);

        using var archive = ZipFile.OpenRead(bundle);
        var contents = new StringBuilder();
        foreach (var entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            contents.Append(await reader.ReadToEndAsync());
        }

        var reportText = contents.ToString();
        Assert.DoesNotContain(historicalCdk, reportText, StringComparison.Ordinal);
        Assert.DoesNotContain(encodedHistoricalCdk, reportText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(currentCdk, reportText, StringComparison.Ordinal);
        Assert.DoesNotContain(historicalAuthorization, reportText, StringComparison.Ordinal);
        Assert.DoesNotContain(fragmentCredential, reportText, StringComparison.Ordinal);
        Assert.DoesNotContain("user:password", reportText, StringComparison.Ordinal);
        Assert.Contains("https://mirror.example.test/resource?C%44K=[REDACTED]&channel=[REDACTED]#access_token=[REDACTED]", reportText, StringComparison.Ordinal);
        Assert.Contains("Authorization: Bearer [REDACTED]", reportText, StringComparison.Ordinal);
        Assert.Contains("normal=https://example.test/status#healthy", reportText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IssueReportDoesNotFollowFileOrDirectoryLinks()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = TestDirectory.Create();
        const string externalSentinel = "EXTERNAL_LINK_TARGET_SENTINEL_7f31";
        var configDirectory = Path.Combine(directory.Path, "config");
        var debugDirectory = Path.Combine(directory.Path, "debug");
        var cacheDirectory = Path.Combine(directory.Path, "cache");
        var outsideDirectory = Path.Combine(directory.Path, "outside");
        var linkedDirectory = Path.Combine(outsideDirectory, "linked-directory");
        Directory.CreateDirectory(configDirectory);
        Directory.CreateDirectory(debugDirectory);
        Directory.CreateDirectory(cacheDirectory);
        Directory.CreateDirectory(linkedDirectory);

        var outsideFile = Path.Combine(outsideDirectory, "credentials.txt");
        var outsideConfig = Path.Combine(outsideDirectory, "avalonia.json");
        await File.WriteAllTextAsync(outsideFile, externalSentinel);
        await File.WriteAllTextAsync(outsideConfig, $"{{\"sentinel\":\"{externalSentinel}\"}}");
        await File.WriteAllTextAsync(Path.Combine(linkedDirectory, "external.txt"), externalSentinel);
        await File.WriteAllTextAsync(Path.Combine(cacheDirectory, "normal.txt"), "normal diagnostic");

        File.CreateSymbolicLink(Path.Combine(configDirectory, "avalonia.json"), "../outside/avalonia.json");
        File.CreateSymbolicLink(Path.Combine(debugDirectory, "config-import-report.json"), "../outside/avalonia.json");
        File.CreateSymbolicLink(Path.Combine(cacheDirectory, "credentials.txt"), outsideFile);
        File.CreateSymbolicLink(Path.Combine(cacheDirectory, "relative-link.txt"), "../outside/credentials.txt");
        File.CreateSymbolicLink(Path.Combine(cacheDirectory, "chained-link.txt"), "relative-link.txt");
        Directory.CreateSymbolicLink(Path.Combine(cacheDirectory, "linked-directory"), "../outside/linked-directory");
        Directory.CreateSymbolicLink(Path.Combine(directory.Path, "resource"), "outside/linked-directory");

        var diagnostics = new UiDiagnosticsService(directory.Path, new UiLogService());
        var bundle = await diagnostics.BuildIssueReportBundleAsync(directory.Path);

        using var archive = ZipFile.OpenRead(bundle);
        var entryNames = archive.Entries.Select(static entry => entry.FullName).ToArray();
        Assert.Contains("cache/normal.txt", entryNames);
        Assert.DoesNotContain("config/avalonia.json", entryNames);
        Assert.DoesNotContain("debug/config-import-report.json", entryNames);
        Assert.DoesNotContain("cache/credentials.txt", entryNames);
        Assert.DoesNotContain("cache/relative-link.txt", entryNames);
        Assert.DoesNotContain("cache/chained-link.txt", entryNames);
        Assert.DoesNotContain(entryNames, name => name.Contains("linked-directory", StringComparison.Ordinal));
        Assert.DoesNotContain(entryNames, name => name.StartsWith("resource/", StringComparison.Ordinal));
        foreach (var entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            Assert.DoesNotContain(externalSentinel, await reader.ReadToEndAsync(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DiagnosticFileSecurityRejectsLinkTargetsAndWindowsReparsePointAttributes()
    {
        Assert.True(DiagnosticFileSecurity.IsLinkOrReparsePoint(FileAttributes.ReparsePoint, null));
        Assert.True(DiagnosticFileSecurity.IsLinkOrReparsePoint(FileAttributes.Normal, "relative-target"));
        Assert.False(DiagnosticFileSecurity.IsLinkOrReparsePoint(FileAttributes.Normal, null));
    }

    [Theory]
    [InlineData("CON.foo.txt", "_CON.foo.txt")]
    [InlineData("COM1.backup.log", "_COM1.backup.log")]
    [InlineData("lpt9 .trace.txt", "_lpt9 .trace.txt")]
    [InlineData("COM10.backup.log", "COM10.backup.log")]
    [InlineData("CONSOLE.txt", "CONSOLE.txt")]
    public void DiagnosticArchivePathSegmentSanitizesWindowsDeviceBaseNames(string segment, string expected)
    {
        Assert.Equal(expected, UiDiagnosticsService.SanitizeArchivePathSegment(segment));
    }

    [Fact]
    public async Task SensitiveStorageMetadataSerializesWithExactContractShape()
    {
        using var directory = TestDirectory.Create();
        var config = new UnifiedConfig
        {
            SensitiveStorage = new SensitiveStorageMetadata
            {
                FormatVersion = 1,
                Backend = SensitiveStorageBackendKind.SystemProtected,
                StoreId = "b854d699-0508-4f6a-a17a-2a4e18bff2fb",
                Revision = 12,
            },
        };

        await new AvaloniaJsonConfigStore(directory.Path).SaveAsync(config);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(directory.Path, "config", "avalonia.json")))!.AsObject();
        var metadata = root["sensitiveStorage"]!.AsObject();
        Assert.Equal(["backend", "formatVersion", "revision", "storeId"], metadata.Select(pair => pair.Key).Order());
        Assert.Equal(1, metadata["formatVersion"]!.GetValue<int>());
        Assert.Equal("SystemProtected", metadata["backend"]!.GetValue<string>());
        Assert.Equal("b854d699-0508-4f6a-a17a-2a4e18bff2fb", metadata["storeId"]!.GetValue<string>());
        Assert.Equal(12, metadata["revision"]!.GetValue<long>());
        Assert.False(root.ContainsKey("SensitiveStorage"));
    }

    [Fact]
    public async Task ProfileCopyDeleteAndSwitchKeepSensitiveValuesIsolated()
    {
        using var directory = TestDirectory.Create();
        var backend = new FakeBackend(SensitiveStorageBackendKind.SystemProtected);
        var service = CreateConfigurationService(directory.Path, backend);
        service.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity] = "default-secret";
        service.CurrentConfig.Profiles["Second"] = new UnifiedProfile
        {
            Values = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase)
            {
                [ConfigurationKeys.RemoteControlUserIdentity] = "second-secret",
            },
        };
        service.CurrentConfig.CurrentProfile = "Second";
        await service.SaveAsync();

        service.CurrentConfig.Profiles.Remove("Default");
        service.CurrentConfig.Profiles["Copy"] = new UnifiedProfile
        {
            Values = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase)
            {
                [ConfigurationKeys.RemoteControlUserIdentity] = "copy-secret",
            },
        };
        service.CurrentConfig.CurrentProfile = "Copy";
        await service.SaveAsync();

        var reloaded = CreateConfigurationService(directory.Path, backend);
        await reloaded.LoadOrBootstrapAsync();
        Assert.Equal("Copy", reloaded.CurrentConfig.CurrentProfile);
        Assert.False(reloaded.CurrentConfig.Profiles.ContainsKey("Default"));
        Assert.Equal("second-secret", reloaded.CurrentConfig.Profiles["Second"].Values[ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
        Assert.Equal("copy-secret", reloaded.CurrentConfig.Profiles["Copy"].Values[ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
    }

    [Theory]
    [InlineData(SensitiveStorageErrorCode.PermissionDenied)]
    [InlineData(SensitiveStorageErrorCode.Unavailable)]
    public async Task SelectedSqlCipherWriteFailureDoesNotDowngrade(SensitiveStorageErrorCode errorCode)
    {
        using var directory = TestDirectory.Create();
        var sqlCipher = new FakeBackend(SensitiveStorageBackendKind.LinuxLocalSqlCipher)
        {
            WriteFailure = new SensitiveStorageException(errorCode, "simulated disk failure"),
        };
        sqlCipher.Seed(Document("selected-store", 1, "old-secret"));
        var plaintext = new FakeBackend(SensitiveStorageBackendKind.Plaintext);
        var config = new UnifiedConfig
        {
            SensitiveStorage = new SensitiveStorageMetadata
            {
                Backend = SensitiveStorageBackendKind.LinuxLocalSqlCipher,
                StoreId = "selected-store",
                Revision = 1,
            },
        };
        var store = new AvaloniaJsonConfigStore(directory.Path);
        await store.SaveAsync(config);
        var sensitiveService = new SensitiveSettingsService([sqlCipher, plaintext], new FakeConsent(true), isLinux: true);
        var configService = new UnifiedConfigurationService(
            store,
            new GuiNewJsonConfigImporter(),
            new GuiJsonConfigImporter(),
            new UiLogService(),
            directory.Path,
            sensitiveService);
        await configService.LoadOrBootstrapAsync();
        configService.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity] = "new-secret";

        var error = await Assert.ThrowsAsync<SensitiveStorageException>(() => configService.SaveAsync());

        Assert.Equal(errorCode, error.ErrorCode);
        Assert.Equal(SensitiveStorageState.Unavailable, sensitiveService.State);
        Assert.Equal(SensitiveStorageBackendKind.LinuxLocalSqlCipher, configService.CurrentConfig.SensitiveStorage.Backend);
        Assert.Equal(1, configService.CurrentConfig.SensitiveStorage.Revision);
        Assert.Equal("new-secret", configService.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
        Assert.Equal(0, plaintext.VerifyCount);
        Assert.Equal(0, plaintext.WriteCount);
    }

    [Fact]
    public async Task SelectedSqlCipherMissingKeyIsCorruptAndDoesNotRegenerate()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var runtime = TestDirectory.Create();
        using var xdg = TestDirectory.Create();
        var keyStore = new LinuxMasterKeyStore(xdg.Path);
        var backend = new SqlCipherStorageBackend(runtime.Path, keyStore);
        await backend.WriteAsync(Document("selected-store", 1, "missing-key-secret"), expectedRevision: 0);
        File.Delete(keyStore.KeyPath);
        var config = ConfigForSelectedBackend(SensitiveStorageBackendKind.LinuxLocalSqlCipher, "selected-store", 1);
        var service = new SensitiveSettingsService([backend], new FakeConsent(false), isLinux: true);

        await service.LoadAsync(config);

        Assert.Equal(SensitiveStorageState.Corrupt, service.State);
        Assert.Equal(SensitiveStorageErrorCode.Corrupt, service.LastErrorCode);
        Assert.False(File.Exists(keyStore.KeyPath));
        Assert.False(config.Profiles["Default"].Values.ContainsKey(ConfigurationKeys.RemoteControlUserIdentity));
    }

    [Fact]
    public async Task SelectedSqlCipherWrongKeyIsCorrupt()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var runtime = TestDirectory.Create();
        using var xdg = TestDirectory.Create();
        var keyStore = new LinuxMasterKeyStore(xdg.Path);
        var backend = new SqlCipherStorageBackend(runtime.Path, keyStore);
        await backend.WriteAsync(Document("selected-store", 1, "wrong-key-secret"), expectedRevision: 0);
        await File.WriteAllBytesAsync(keyStore.KeyPath, RandomNumberGenerator.GetBytes(32));
        var service = new SensitiveSettingsService([backend], new FakeConsent(false), isLinux: true);

        await service.LoadAsync(ConfigForSelectedBackend(SensitiveStorageBackendKind.LinuxLocalSqlCipher, "selected-store", 1));

        Assert.Equal(SensitiveStorageState.Corrupt, service.State);
        Assert.Equal(SensitiveStorageErrorCode.Corrupt, service.LastErrorCode);
    }

    [Fact]
    public async Task SelectedSqlCipherCorruptDatabaseIsCorrupt()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var runtime = TestDirectory.Create();
        using var xdg = TestDirectory.Create();
        var keyStore = new LinuxMasterKeyStore(xdg.Path);
        var backend = new SqlCipherStorageBackend(runtime.Path, keyStore);
        await backend.WriteAsync(Document("selected-store", 1, "corrupt-database-secret"), expectedRevision: 0);
        await File.WriteAllBytesAsync(
            Path.Combine(runtime.Path, "data", "sensitive-settings.db"),
            RandomNumberGenerator.GetBytes(512));
        var service = new SensitiveSettingsService([backend], new FakeConsent(false), isLinux: true);

        await service.LoadAsync(ConfigForSelectedBackend(SensitiveStorageBackendKind.LinuxLocalSqlCipher, "selected-store", 1));

        Assert.Equal(SensitiveStorageState.Corrupt, service.State);
        Assert.Equal(SensitiveStorageErrorCode.Corrupt, service.LastErrorCode);
    }

    [Fact]
    public async Task SensitiveBackendFailureDisablesRemoteControlOperations()
    {
        using var directory = TestDirectory.Create();
        var sensitiveService = new SensitiveSettingsService(
            [new FakeBackend(SensitiveStorageBackendKind.SystemProtected)],
            new FakeConsent(false),
            isLinux: false);
        sensitiveService.RecordFailure(new SensitiveStorageException(SensitiveStorageErrorCode.Unavailable, "locked"));
        await using var runtime = MAAUnifiedRuntimeFactory.Create(directory.Path, sensitiveService);

        var result = await runtime.RemoteControlFeatureService.TestConnectivityAsync(
            new RemoteControlConnectivityRequest("https://example.com/get", "https://example.com/report", 1000));

        Assert.False(result.Success);
        Assert.Equal(UiErrorCode.RemoteControlUnsupported, result.Error?.Code);
    }

    [Fact]
    public async Task SensitiveBackendFailurePreventsNotificationSenderInvocation()
    {
        using var directory = TestDirectory.Create();
        var sensitiveService = new SensitiveSettingsService(
            [new FakeBackend(SensitiveStorageBackendKind.SystemProtected)],
            new FakeConsent(false),
            isLinux: false);
        sensitiveService.RecordFailure(new SensitiveStorageException(SensitiveStorageErrorCode.Unavailable, "locked"));
        var configService = new UnifiedConfigurationService(
            new AvaloniaJsonConfigStore(directory.Path),
            new GuiNewJsonConfigImporter(),
            new GuiJsonConfigImporter(),
            new UiLogService(),
            directory.Path,
            sensitiveService);
        var sendCount = 0;
        var service = new NotificationProviderFeatureService(
            configService,
            supported: true,
            sendAsync: (_, _, _, _, _) =>
            {
                sendCount++;
                return Task.FromResult(UiOperationResult.Ok("sent"));
            },
            sendHttpAsync: null);

        var validate = await service.ValidateProviderParametersAsync(
            new NotificationProviderRequest("ServerChan", "sendKey=cached-secret"));
        var send = await service.SendTestAsync(
            new NotificationProviderTestRequest("ServerChan", "sendKey=cached-secret", "title", "message"));

        Assert.False(validate.Success);
        Assert.False(send.Success);
        Assert.Equal(UiErrorCode.NotificationProviderUnsupported, validate.Error?.Code);
        Assert.Equal(UiErrorCode.NotificationProviderUnsupported, send.Error?.Code);
        Assert.Equal(0, sendCount);
    }

    [Fact]
    public async Task SensitiveBackendFailureDisablesMirrorChyanSettingsAndNetworkOperations()
    {
        using var directory = TestDirectory.Create();
        var sensitiveService = new SensitiveSettingsService(
            [new FakeBackend(SensitiveStorageBackendKind.SystemProtected)],
            new FakeConsent(false),
            isLinux: false);
        sensitiveService.RecordFailure(new SensitiveStorageException(SensitiveStorageErrorCode.Unavailable, "locked"));
        var configService = new UnifiedConfigurationService(
            new AvaloniaJsonConfigStore(directory.Path),
            new GuiNewJsonConfigImporter(),
            new GuiJsonConfigImporter(),
            new UiLogService(),
            directory.Path,
            sensitiveService);
        var service = new MAAUnified.Application.Services.Features.VersionUpdateFeatureService(
            configService,
            runtimeBaseDirectory: directory.Path);
        var policy = VersionUpdatePolicy.Default with
        {
            ResourceUpdateSource = "MirrorChyan",
            MirrorChyanCdk = "must-not-be-used",
        };

        var save = await service.SavePolicyAsync(policy);
        var update = await service.UpdateResourceAsync(policy, "Official");
        var check = await service.CheckResourceUpdateAsync(policy, "Official");

        Assert.False(save.Success);
        Assert.False(update.Success);
        Assert.False(check.Success);
        Assert.Equal(UiErrorCode.VersionUpdateServiceUnavailable, save.Error?.Code);
        Assert.Equal(UiErrorCode.VersionUpdateServiceUnavailable, update.Error?.Code);
        Assert.Equal(UiErrorCode.VersionUpdateServiceUnavailable, check.Error?.Code);
    }

    [Fact]
    public async Task PlaintextRefusalLeavesLegacyConfigAuthoritative()
    {
        using var directory = TestDirectory.Create();
        const string sentinel = "PLAINTEXT-REFUSAL-SENTINEL-71f4";
        var source = ConfigWithSecret();
        source.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity] = sentinel;
        var store = new AvaloniaJsonConfigStore(directory.Path);
        await store.SaveAsync(source);
        var system = new FakeBackend(SensitiveStorageBackendKind.SystemProtected)
        {
            VerifyFailure = new SensitiveStorageException(SensitiveStorageErrorCode.StructurallyUnavailable, "no secret service"),
        };
        var sqlCipher = new FakeBackend(SensitiveStorageBackendKind.LinuxLocalSqlCipher)
        {
            VerifyFailure = new SensitiveStorageException(SensitiveStorageErrorCode.StructurallyUnavailable, "provider missing"),
        };
        var plaintext = new FakeBackend(SensitiveStorageBackendKind.Plaintext);
        var sensitiveService = new SensitiveSettingsService([system, sqlCipher, plaintext], new FakeConsent(false), isLinux: true);
        var configService = new UnifiedConfigurationService(
            store,
            new GuiNewJsonConfigImporter(),
            new GuiJsonConfigImporter(),
            new UiLogService(),
            directory.Path,
            sensitiveService);

        await configService.LoadOrBootstrapAsync();

        Assert.Equal(
            sentinel,
            configService.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
        configService.CurrentConfig.Profiles["Default"].Values["ordinary-after-refusal"] = "must-not-overwrite-secrets";
        var saveError = await Assert.ThrowsAsync<SensitiveStorageException>(() => configService.SaveAsync());

        var persisted = await File.ReadAllTextAsync(store.ConfigPath);
        Assert.Contains(sentinel, persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("ordinary-after-refusal", persisted, StringComparison.Ordinal);
        Assert.Equal(SensitiveStorageErrorCode.ActionRequired, saveError.ErrorCode);
        Assert.Equal(SensitiveStorageBackendKind.Uninitialized, (await store.LoadAsync())!.SensitiveStorage.Backend);
        Assert.Equal(SensitiveStorageState.Unavailable, sensitiveService.State);
        Assert.Equal(0, plaintext.WriteCount);

        var reloaded = new UnifiedConfigurationService(
            store,
            new GuiNewJsonConfigImporter(),
            new GuiJsonConfigImporter(),
            new UiLogService(),
            directory.Path,
            sensitiveService);
        await reloaded.LoadOrBootstrapAsync();
        Assert.Equal(
            sentinel,
            reloaded.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
    }

    [Fact]
    public async Task FailedSystemMigrationConfigCommitLeavesPlaintextAuthoritative()
    {
        using var directory = TestDirectory.Create();
        var innerStore = new AvaloniaJsonConfigStore(directory.Path);
        var plaintext = new FakeBackend(SensitiveStorageBackendKind.Plaintext);
        plaintext.Seed(Document("plaintext-store", 3, "plaintext-authoritative"));
        await innerStore.SaveAsync(ConfigForSelectedBackend(SensitiveStorageBackendKind.Plaintext, "plaintext-store", 3));
        var system = new FakeBackend(SensitiveStorageBackendKind.SystemProtected);
        var sensitiveService = new SensitiveSettingsService([system, plaintext], new FakeConsent(true), isLinux: false);
        var failingStore = new FailNextSaveStore(innerStore);
        var configService = new UnifiedConfigurationService(
            failingStore,
            new GuiNewJsonConfigImporter(),
            new GuiJsonConfigImporter(),
            new UiLogService(),
            directory.Path,
            sensitiveService);
        await configService.LoadOrBootstrapAsync();
        failingStore.FailNextSave = true;

        await Assert.ThrowsAsync<IOException>(() => configService.RetrySecureStorageAsync());

        Assert.Equal(SensitiveStorageBackendKind.Plaintext, configService.CurrentConfig.SensitiveStorage.Backend);
        Assert.Equal(3, configService.CurrentConfig.SensitiveStorage.Revision);
        Assert.Equal("plaintext-authoritative", configService.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
        var persisted = await innerStore.LoadAsync();
        Assert.Equal(SensitiveStorageBackendKind.Plaintext, persisted!.SensitiveStorage.Backend);
        Assert.Equal(3, persisted.SensitiveStorage.Revision);
        Assert.Equal(SensitiveStorageState.PlaintextWarning, sensitiveService.State);
        Assert.Equal(1, system.WriteCount);
        Assert.Equal(0, plaintext.DeleteCount);

        await configService.RetrySecureStorageAsync();

        Assert.Equal(SensitiveStorageBackendKind.SystemProtected, configService.CurrentConfig.SensitiveStorage.Backend);
        Assert.Equal(4, configService.CurrentConfig.SensitiveStorage.Revision);
        Assert.Equal("plaintext-authoritative", configService.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
        persisted = await innerStore.LoadAsync();
        Assert.Equal(SensitiveStorageBackendKind.SystemProtected, persisted!.SensitiveStorage.Backend);
        Assert.Equal(4, persisted.SensitiveStorage.Revision);
        Assert.Equal(SensitiveStorageState.Available, sensitiveService.State);
        Assert.Equal(1, system.WriteCount);
        Assert.Equal(1, plaintext.DeleteCount);
    }

    [Fact]
    public async Task FailedMigrationThenModifiedPlaintextCanCasAdvanceOldPendingTarget()
    {
        using var directory = TestDirectory.Create();
        var innerStore = new AvaloniaJsonConfigStore(directory.Path);
        var plaintext = new FakeBackend(SensitiveStorageBackendKind.Plaintext);
        plaintext.Seed(Document("plaintext-store", 3, "before-failed-migration"));
        await innerStore.SaveAsync(ConfigForSelectedBackend(SensitiveStorageBackendKind.Plaintext, "plaintext-store", 3));
        var system = new FakeBackend(SensitiveStorageBackendKind.SystemProtected);
        var sensitiveService = new SensitiveSettingsService([system, plaintext], new FakeConsent(true), isLinux: false);
        var failingStore = new FailNextSaveStore(innerStore);
        var configService = new UnifiedConfigurationService(
            failingStore,
            new GuiNewJsonConfigImporter(),
            new GuiJsonConfigImporter(),
            new UiLogService(),
            directory.Path,
            sensitiveService);
        await configService.LoadOrBootstrapAsync();
        failingStore.FailNextSave = true;
        await Assert.ThrowsAsync<IOException>(() => configService.RetrySecureStorageAsync());

        configService.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity] = "modified-plaintext";
        await configService.SaveAsync();
        Assert.Equal(4, configService.CurrentConfig.SensitiveStorage.Revision);
        Assert.Equal(SensitiveStorageBackendKind.Plaintext, configService.CurrentConfig.SensitiveStorage.Backend);

        await configService.RetrySecureStorageAsync();

        Assert.Equal(SensitiveStorageBackendKind.SystemProtected, configService.CurrentConfig.SensitiveStorage.Backend);
        Assert.Equal(5, configService.CurrentConfig.SensitiveStorage.Revision);
        Assert.Equal("modified-plaintext", (await system.ReadAsync("plaintext-store", 5))
            .Profiles["Default"][ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
        Assert.Equal(2, system.WriteCount);
        Assert.Equal(1, plaintext.DeleteCount);
    }

    [Fact]
    public async Task SecureStorageMigrationRejectsConcurrentlyAdvancedPlaintextSource()
    {
        using var directory = TestDirectory.Create();
        var store = new AvaloniaJsonConfigStore(directory.Path);
        var plaintext = new FakeBackend(SensitiveStorageBackendKind.Plaintext);
        plaintext.Seed(Document("plaintext-store", 3, "loaded"));
        await store.SaveAsync(ConfigForSelectedBackend(SensitiveStorageBackendKind.Plaintext, "plaintext-store", 3));
        var system = new FakeBackend(SensitiveStorageBackendKind.SystemProtected);
        var sensitiveService = new SensitiveSettingsService([system, plaintext], new FakeConsent(true), isLinux: false);
        var configService = new UnifiedConfigurationService(
            store,
            new GuiNewJsonConfigImporter(),
            new GuiJsonConfigImporter(),
            new UiLogService(),
            directory.Path,
            sensitiveService);
        await configService.LoadOrBootstrapAsync();
        await plaintext.WriteAsync(Document("plaintext-store", 4, "concurrent"), expectedRevision: 3);

        var error = await Assert.ThrowsAsync<SensitiveStorageException>(() => configService.RetrySecureStorageAsync());

        Assert.Equal(SensitiveStorageErrorCode.ActionRequired, error.ErrorCode);
        Assert.Equal(0, system.WriteCount);
        Assert.Equal(SensitiveStorageBackendKind.Plaintext, (await store.LoadAsync())!.SensitiveStorage.Backend);
    }

    [Theory]
    [InlineData(4, 0)]
    [InlineData(5, 0)]
    public async Task SecureStorageMigrationRejectsDifferentOrStalePendingDocument(long targetRevision, int expectedWriteAttempts)
    {
        using var directory = TestDirectory.Create();
        var store = new AvaloniaJsonConfigStore(directory.Path);
        var plaintext = new FakeBackend(SensitiveStorageBackendKind.Plaintext);
        plaintext.Seed(Document("plaintext-store", 3, "authoritative"));
        await store.SaveAsync(ConfigForSelectedBackend(SensitiveStorageBackendKind.Plaintext, "plaintext-store", 3));
        var system = new FakeBackend(SensitiveStorageBackendKind.SystemProtected);
        if (targetRevision > 4)
        {
            system.Seed(Document("plaintext-store", 4, "authoritative"));
        }

        system.Seed(Document("plaintext-store", targetRevision, "concurrent"));
        var sensitiveService = new SensitiveSettingsService([system, plaintext], new FakeConsent(true), isLinux: false);
        var configService = new UnifiedConfigurationService(
            store,
            new GuiNewJsonConfigImporter(),
            new GuiJsonConfigImporter(),
            new UiLogService(),
            directory.Path,
            sensitiveService);
        await configService.LoadOrBootstrapAsync();

        var error = await Assert.ThrowsAsync<SensitiveStorageException>(() => configService.RetrySecureStorageAsync());

        Assert.Equal(SensitiveStorageErrorCode.ActionRequired, error.ErrorCode);
        Assert.Equal(SensitiveStorageBackendKind.Plaintext, configService.CurrentConfig.SensitiveStorage.Backend);
        Assert.Equal(3, configService.CurrentConfig.SensitiveStorage.Revision);
        Assert.Equal(expectedWriteAttempts, system.WriteCount);
        Assert.Equal(0, plaintext.DeleteCount);
    }

    [Fact]
    public async Task SuccessfulSaveSanitizesOrdinaryAndSchemaBackupsOnly()
    {
        using var directory = TestDirectory.Create();
        const string sentinel = "HISTORICAL-BACKUP-SENTINEL-f384";
        const string preservedMarker = "HISTORICAL-UNKNOWN-FIELD-f6a1";
        var configDirectory = Path.Combine(directory.Path, "config");
        Directory.CreateDirectory(configDirectory);
        var backupJson = HistoricalBackupWithAllSensitiveValues(sentinel, preservedMarker).ToJsonString();
        var ordinaryBackupPath = Path.Combine(configDirectory, "avalonia.json.bak.20240102030405");
        var schemaBackupPath = Path.Combine(configDirectory, "avalonia.json.schema-v2.bak.20240102030406");
        var ordinaryCollisionBackupPath = Path.Combine(
            configDirectory,
            "avalonia.json.bak.20240102030405.0123456789abcdef0123456789abcdef");
        var schemaCollisionBackupPath = Path.Combine(
            configDirectory,
            "avalonia.json.schema-v2.bak.20240102030406.ABCDEF0123456789abcdef0123456789");
        var unknownSimilarPaths = new[]
        {
            Path.Combine(configDirectory, "avalonia.json.schema-v2.bak.20240102030406.extra"),
            Path.Combine(configDirectory, "avalonia.json.bak.20240102030405.0123456789abcdef0123456789abcde"),
            Path.Combine(configDirectory, "avalonia.json.bak.20240102030405.0123456789abcdef0123456789abcdeg"),
            Path.Combine(configDirectory, "avalonia.json.bak.20240102030405.0123456789abcdef0123456789abcdef.extra"),
            Path.Combine(configDirectory, "avalonia.json.bak.20240102030405.old"),
        };
        await new AvaloniaJsonConfigStore(directory.Path).SaveAsync(new UnifiedConfig());
        foreach (var backupPath in new[] { ordinaryBackupPath, schemaBackupPath, ordinaryCollisionBackupPath, schemaCollisionBackupPath }
                     .Concat(unknownSimilarPaths))
        {
            await File.WriteAllTextAsync(backupPath, backupJson);
        }

        UnixFileMode? expectedUnixMode = null;
        if (!OperatingSystem.IsWindows())
        {
            expectedUnixMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
            File.SetUnixFileMode(schemaBackupPath, expectedUnixMode.Value);
        }

        var service = CreateConfigurationService(directory.Path, new FakeBackend(SensitiveStorageBackendKind.SystemProtected));
        await service.LoadOrBootstrapAsync();
        service.CurrentConfig.GlobalValues["ordinary"] = "changed";

        await service.SaveAsync();

        foreach (var sanitizedPath in new[]
                 {
                     ordinaryBackupPath,
                     schemaBackupPath,
                     ordinaryCollisionBackupPath,
                     schemaCollisionBackupPath,
                 })
        {
            var sanitized = await File.ReadAllTextAsync(sanitizedPath);
            Assert.DoesNotContain(sentinel, sanitized, StringComparison.Ordinal);
            Assert.Contains(preservedMarker, sanitized, StringComparison.Ordinal);
            foreach (var definition in SensitiveSettingKeyRegistry.Definitions)
            {
                Assert.DoesNotContain(definition.Key, sanitized, StringComparison.OrdinalIgnoreCase);
            }
        }

        foreach (var unknownSimilarPath in unknownSimilarPaths)
        {
            Assert.Contains(sentinel, await File.ReadAllTextAsync(unknownSimilarPath), StringComparison.Ordinal);
        }

        Assert.DoesNotContain(
            sentinel,
            await File.ReadAllTextAsync(Path.Combine(configDirectory, "avalonia.json")),
            StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows() && expectedUnixMode is { } unixMode)
        {
            Assert.Equal(unixMode, File.GetUnixFileMode(schemaBackupPath));
        }
    }

    [Theory]
    [InlineData("avalonia.json.schema-v2.bak.20240102030405")]
    [InlineData("avalonia.json.schema-v2.bak.20240102030405.0123456789abcdef0123456789abcdef")]
    public async Task SuccessfulSaveDoesNotFollowKnownBackupSymbolicLink(string backupFileName)
    {
        if (OperatingSystem.IsWindows()) return;
        using var directory = TestDirectory.Create();
        const string sentinel = "LINKED-HISTORICAL-BACKUP-SENTINEL-7f32";
        var configDirectory = Path.Combine(directory.Path, "config");
        Directory.CreateDirectory(configDirectory);
        var targetPath = Path.Combine(directory.Path, "linked-backup-target.json");
        var backupPath = Path.Combine(configDirectory, backupFileName);
        await new AvaloniaJsonConfigStore(directory.Path).SaveAsync(new UnifiedConfig());
        await File.WriteAllTextAsync(targetPath, HistoricalBackupWithAllSensitiveValues(sentinel, "target").ToJsonString());
        File.CreateSymbolicLink(backupPath, "../linked-backup-target.json");
        var service = CreateConfigurationService(directory.Path, new FakeBackend(SensitiveStorageBackendKind.SystemProtected));
        await service.LoadOrBootstrapAsync();
        service.CurrentConfig.GlobalValues["ordinary"] = "changed";

        await service.SaveAsync();

        Assert.NotNull(new FileInfo(backupPath).LinkTarget);
        Assert.Contains(sentinel, await File.ReadAllTextAsync(targetPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuccessfulSavePreservesCorruptKnownBackupAndLogsFixedError()
    {
        using var directory = TestDirectory.Create();
        const string corruptContents = "{ invalid-json CORRUPT-BACKUP-SENTINEL-9d42";
        var configDirectory = Path.Combine(directory.Path, "config");
        Directory.CreateDirectory(configDirectory);
        var backupPath = Path.Combine(configDirectory, "avalonia.json.bak.20240102030405");
        await new AvaloniaJsonConfigStore(directory.Path).SaveAsync(new UnifiedConfig());
        await File.WriteAllTextAsync(backupPath, corruptContents);
        var log = new UiLogService();
        var service = new UnifiedConfigurationService(
            new AvaloniaJsonConfigStore(directory.Path),
            new GuiNewJsonConfigImporter(),
            new GuiJsonConfigImporter(),
            log,
            directory.Path,
            new SensitiveSettingsService(
                [new FakeBackend(SensitiveStorageBackendKind.SystemProtected)],
                new FakeConsent(false),
                isLinux: false));
        await service.LoadOrBootstrapAsync();
        service.CurrentConfig.GlobalValues["ordinary"] = "changed";

        await service.SaveAsync();

        Assert.Equal(corruptContents, await File.ReadAllTextAsync(backupPath));
        Assert.Contains(
            log.Snapshot,
            entry => entry.Message == "Known config backup sanitization skipped: errorCode=InvalidJson.");
        Assert.DoesNotContain(log.Snapshot, entry => entry.Message.Contains(backupPath, StringComparison.Ordinal));
        Assert.DoesNotContain(log.Snapshot, entry => entry.Message.Contains("CORRUPT-BACKUP-SENTINEL", StringComparison.Ordinal));
    }

    [Fact]
    public void EnvelopeCompareAndSwapRejectsStaleWriterWithoutOverwritingCurrentRevision()
    {
        var envelope = MsalSystemStorageBackend.MergeDocument(null, Document("store-a", 1, "initial"), expectedRevision: 0);
        envelope = MsalSystemStorageBackend.MergeDocument(envelope, Document("store-b", 7, "other-store"), expectedRevision: 0);
        envelope = MsalSystemStorageBackend.MergeDocument(envelope, Document("store-a", 2, "winner"), expectedRevision: 1);

        var error = Assert.Throws<SensitiveStorageException>(() =>
            MsalSystemStorageBackend.MergeDocument(envelope, Document("store-a", 2, "stale"), expectedRevision: 1));

        Assert.Equal(SensitiveStorageErrorCode.ActionRequired, error.ErrorCode);
        Assert.Equal(
            "winner",
            SensitiveSettingsEnvelopeStore.FindRevision(envelope, "store-a", 2)
                .Profiles["Default"][ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
        Assert.Equal(
            "other-store",
            SensitiveSettingsEnvelopeStore.FindRevision(envelope, "store-b", 7)
                .Profiles["Default"][ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
    }

    [Fact]
    public void EnvelopeCurrentReplacementPreservesPreviousAndRejectsStaleExpectedContent()
    {
        var authoritative = Document("store", 1, "authoritative");
        var orphan = Document("store", 2, "orphan");
        var envelope = SensitiveSettingsEnvelopeStore.Merge(null, authoritative, expectedRevision: 0);
        envelope = SensitiveSettingsEnvelopeStore.Merge(envelope, orphan, expectedRevision: 1);

        envelope = SensitiveSettingsEnvelopeStore.ReplaceCurrent(
            envelope,
            Document("store", 2, "replacement"),
            orphan,
            authoritative);

        Assert.Equal(
            "replacement",
            SensitiveSettingsEnvelopeStore.FindRevision(envelope, "store", 2)
                .Profiles["Default"][ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
        Assert.Equal(
            "authoritative",
            SensitiveSettingsEnvelopeStore.FindRevision(envelope, "store", 1)
                .Profiles["Default"][ConfigurationKeys.RemoteControlUserIdentity]!.ToString());

        var error = Assert.Throws<SensitiveStorageException>(() =>
            SensitiveSettingsEnvelopeStore.ReplaceCurrent(
                envelope,
                Document("store", 2, "stale-writer"),
                orphan,
                authoritative));

        Assert.Equal(SensitiveStorageErrorCode.ActionRequired, error.ErrorCode);
        Assert.Equal(
            "replacement",
            SensitiveSettingsEnvelopeStore.FindCurrent(envelope, "store")
                .Profiles["Default"][ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
    }

    [Fact]
    public void EnvelopeCurrentProbeDoesNotAcceptMatchingPreviousRevision()
    {
        var envelope = SensitiveSettingsEnvelopeStore.Merge(
            null,
            Document("store", 4, "matching-pending"),
            expectedRevision: 0);
        envelope = SensitiveSettingsEnvelopeStore.Merge(
            envelope,
            Document("store", 5, "newer-current"),
            expectedRevision: 4);

        Assert.Equal(
            "matching-pending",
            SensitiveSettingsEnvelopeStore.FindRevision(envelope, "store", 4)
                .Profiles["Default"][ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
        var current = SensitiveSettingsEnvelopeStore.FindCurrent(envelope, "store");
        Assert.Equal(5, current.Revision);
        Assert.Equal(
            "newer-current",
            current.Profiles["Default"][ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
    }

    [Fact]
    public async Task PlaintextBackendConcurrentWritersAllowOnlyOneRevisionWinner()
    {
        using var directory = TestDirectory.Create();
        var first = new PlaintextSensitiveSettingsBackend(directory.Path);
        var second = new PlaintextSensitiveSettingsBackend(directory.Path);
        await first.WriteAsync(Document("store", 1, "initial"), expectedRevision: 0);

        var attempts = await Task.WhenAll(
            CaptureExceptionAsync(() => first.WriteAsync(Document("store", 2, "first-writer"), expectedRevision: 1)),
            CaptureExceptionAsync(() => second.WriteAsync(Document("store", 2, "second-writer"), expectedRevision: 1)));

        Assert.Single(attempts, exception => exception is null);
        var conflict = Assert.Single(attempts, exception => exception is SensitiveStorageException);
        Assert.Equal(SensitiveStorageErrorCode.ActionRequired, ((SensitiveStorageException)conflict!).ErrorCode);
        var winner = await first.ReadAsync("store", 2);
        Assert.Contains(
            winner.Profiles["Default"][ConfigurationKeys.RemoteControlUserIdentity]!.ToString(),
            new[] { "first-writer", "second-writer" });
    }

    [Fact]
    public async Task ConfigurationServicesRejectStaleSensitiveWriter()
    {
        using var directory = TestDirectory.Create();
        var backend = new FakeBackend(SensitiveStorageBackendKind.SystemProtected);
        var first = CreateConfigurationService(directory.Path, backend);
        await first.LoadOrBootstrapAsync();
        first.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity] = "initial";
        await first.SaveAsync();

        var second = CreateConfigurationService(directory.Path, backend);
        await second.LoadOrBootstrapAsync();
        first.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity] = "winner";
        second.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity] = "stale";
        await first.SaveAsync();

        var error = await Assert.ThrowsAsync<SensitiveStorageException>(() => second.SaveAsync());

        Assert.Equal(SensitiveStorageErrorCode.ActionRequired, error.ErrorCode);
        Assert.Equal(SensitiveStorageState.Unavailable, second.SensitiveStorageState);
        var reloaded = CreateConfigurationService(directory.Path, backend);
        await reloaded.LoadOrBootstrapAsync();
        Assert.Equal(
            "winner",
            reloaded.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
    }

    [Fact]
    public async Task ConfigurationCommitLockPreventsPendingRevisionFromBeingReplacedByConcurrentWriter()
    {
        using var directory = TestDirectory.Create();
        var backend = new FakeBackend(SensitiveStorageBackendKind.SystemProtected);
        var seed = CreateConfigurationService(directory.Path, backend);
        seed.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity] = "initial";
        await seed.SaveAsync();

        var pausingStore = new PauseNextSaveStore(new AvaloniaJsonConfigStore(directory.Path));
        var first = CreateConfigurationService(directory.Path, backend, pausingStore);
        var second = CreateConfigurationService(directory.Path, backend);
        await first.LoadOrBootstrapAsync();
        await second.LoadOrBootstrapAsync();
        first.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity] = "winner";
        second.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity] = "stale";

        var firstSave = first.SaveAsync();
        await pausingStore.SaveEntered.WaitAsync(TimeSpan.FromSeconds(5));
        var writesAfterFirstPrepared = backend.WriteCount;
        var secondSave = second.SaveAsync();
        try
        {
            Assert.False(secondSave.IsCompleted);
            Assert.Equal(writesAfterFirstPrepared, backend.WriteCount);
        }
        finally
        {
            pausingStore.ReleaseSave();
        }

        await firstSave;
        var error = await Assert.ThrowsAsync<SensitiveStorageException>(() => secondSave);

        Assert.Equal(SensitiveStorageErrorCode.ActionRequired, error.ErrorCode);
        Assert.Equal(writesAfterFirstPrepared, backend.WriteCount);
        var current = await backend.ReadCurrentAsync(first.CurrentConfig.SensitiveStorage.StoreId);
        Assert.Equal(
            "winner",
            current.Profiles["Default"][ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
        Assert.False(File.Exists(Path.Combine(directory.Path, "config", ".sensitive-settings.pending.json")));
        var lockPath = Path.Combine(directory.Path, "config", ".avalonia.json.commit.lock");
        Assert.Equal(0, new FileInfo(lockPath).Length);
        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode groupOrOtherAccess = UnixFileMode.GroupRead
                                                    | UnixFileMode.GroupWrite
                                                    | UnixFileMode.GroupExecute
                                                    | UnixFileMode.OtherRead
                                                    | UnixFileMode.OtherWrite
                                                    | UnixFileMode.OtherExecute;
            Assert.Equal((UnixFileMode)0, File.GetUnixFileMode(lockPath) & groupOrOtherAccess);
        }
    }

    [Fact]
    public async Task ConfigurationCommitLockWaitCanBeCanceledAndReleasedLockCanBeReacquired()
    {
        using var directory = TestDirectory.Create();
        IUnifiedConfigStore store = new AvaloniaJsonConfigStore(directory.Path);
        var firstLease = await store.AcquireCommitLockAsync();
        try
        {
            using var cancellation = new CancellationTokenSource();
            var waiting = store.AcquireCommitLockAsync(cancellation.Token).AsTask();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        }
        finally
        {
            await firstLease.DisposeAsync();
        }

        using var reacquireTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var reacquired = await store.AcquireCommitLockAsync(reacquireTimeout.Token);
    }

    [Fact]
    public async Task ConfigurationServicesRejectStaleOrdinarySettingsWriter()
    {
        using var directory = TestDirectory.Create();
        var backend = new FakeBackend(SensitiveStorageBackendKind.SystemProtected);
        var first = CreateConfigurationService(directory.Path, backend);
        first.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity] = "initial";
        first.CurrentConfig.GlobalValues["ordinary-setting"] = "initial-ordinary";
        await first.SaveAsync();
        var second = CreateConfigurationService(directory.Path, backend);
        await second.LoadOrBootstrapAsync();

        first.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity] = "winner";
        await first.SaveAsync();
        second.CurrentConfig.GlobalValues["ordinary-setting"] = "stale-ordinary";

        var error = await Assert.ThrowsAsync<SensitiveStorageException>(() => second.SaveAsync());

        Assert.Equal(SensitiveStorageErrorCode.ActionRequired, error.ErrorCode);
        var persisted = await new AvaloniaJsonConfigStore(directory.Path).LoadAsync();
        Assert.Equal(2, persisted!.SensitiveStorage.Revision);
        Assert.Equal("initial-ordinary", persisted.GlobalValues["ordinary-setting"]!.ToString());
        var reloaded = CreateConfigurationService(directory.Path, backend);
        await reloaded.LoadOrBootstrapAsync();
        Assert.Equal("winner", reloaded.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
    }

    [Fact]
    public async Task CopiedConfigsForkSharedSystemStoreWithoutOverwritingEachOthersSecrets()
    {
        using var directory = TestDirectory.Create();
        var firstRoot = Path.Combine(directory.Path, "portable-a");
        var secondRoot = Path.Combine(directory.Path, "portable-b");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        var backend = new FakeBackend(SensitiveStorageBackendKind.SystemProtected);

        var first = CreateConfigurationService(firstRoot, backend);
        first.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity] = "shared-r1";
        await first.SaveAsync();

        var firstConfigPath = new AvaloniaJsonConfigStore(firstRoot).ConfigPath;
        var secondConfigPath = new AvaloniaJsonConfigStore(secondRoot).ConfigPath;
        Directory.CreateDirectory(Path.GetDirectoryName(secondConfigPath)!);
        File.Copy(firstConfigPath, secondConfigPath);

        first.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity] = "secret-a";
        await first.SaveAsync();

        var second = CreateConfigurationService(secondRoot, backend);
        await second.LoadOrBootstrapAsync();
        Assert.Equal("shared-r1", second.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
        second.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity] = "secret-b";
        await second.SaveAsync();

        Assert.NotEqual(first.CurrentConfig.SensitiveStorage.StoreId, second.CurrentConfig.SensitiveStorage.StoreId);
        Assert.Equal(2, first.CurrentConfig.SensitiveStorage.Revision);
        Assert.Equal(1, second.CurrentConfig.SensitiveStorage.Revision);

        var restartedFirst = CreateConfigurationService(firstRoot, backend);
        var restartedSecond = CreateConfigurationService(secondRoot, backend);
        await restartedFirst.LoadOrBootstrapAsync();
        await restartedSecond.LoadOrBootstrapAsync();

        Assert.Equal("secret-a", restartedFirst.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
        Assert.Equal("secret-b", restartedSecond.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
    }

    [Fact]
    public async Task FailedConfigCommitThenRestartAllowsOrdinarySaveByReplacingOrphan()
    {
        using var directory = TestDirectory.Create();
        var innerStore = new AvaloniaJsonConfigStore(directory.Path);
        var backend = new FakeBackend(SensitiveStorageBackendKind.SystemProtected);
        var failingStore = new FailNextSaveStore(innerStore);
        var first = CreateConfigurationService(directory.Path, backend, failingStore);
        first.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity] = "authoritative-a";
        await first.SaveAsync();
        first.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity] = "orphan-b";
        failingStore.FailNextSave = true;
        await Assert.ThrowsAsync<IOException>(() => first.SaveAsync());

        var restarted = CreateConfigurationService(directory.Path, backend);
        await restarted.LoadOrBootstrapAsync();
        Assert.Equal("authoritative-a", restarted.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
        restarted.CurrentConfig.GlobalValues["ordinary-setting"] = "saved-after-restart";

        await restarted.SaveAsync();

        Assert.Equal(2, restarted.CurrentConfig.SensitiveStorage.Revision);
        Assert.Equal(
            "authoritative-a",
            (await backend.ReadAsync(restarted.CurrentConfig.SensitiveStorage.StoreId, 2))
                .Profiles["Default"][ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
        var persisted = await innerStore.LoadAsync();
        Assert.Equal(2, persisted!.SensitiveStorage.Revision);
        Assert.Equal("saved-after-restart", persisted.GlobalValues["ordinary-setting"]!.ToString());
    }

    [Fact]
    public async Task FailedConfigCommitThenRestartAllowsNewSecretByReplacingOrphan()
    {
        using var directory = TestDirectory.Create();
        var innerStore = new AvaloniaJsonConfigStore(directory.Path);
        var backend = new FakeBackend(SensitiveStorageBackendKind.SystemProtected);
        var failingStore = new FailNextSaveStore(innerStore);
        var first = CreateConfigurationService(directory.Path, backend, failingStore);
        first.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity] = "authoritative-a";
        await first.SaveAsync();
        first.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity] = "orphan-b";
        failingStore.FailNextSave = true;
        await Assert.ThrowsAsync<IOException>(() => first.SaveAsync());

        var restarted = CreateConfigurationService(directory.Path, backend);
        await restarted.LoadOrBootstrapAsync();
        restarted.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity] = "replacement-c";

        await restarted.SaveAsync();

        Assert.Equal(2, restarted.CurrentConfig.SensitiveStorage.Revision);
        Assert.Equal(
            "replacement-c",
            (await backend.ReadAsync(restarted.CurrentConfig.SensitiveStorage.StoreId, 2))
                .Profiles["Default"][ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
        Assert.Equal(2, (await innerStore.LoadAsync())!.SensitiveStorage.Revision);
    }

    [Fact]
    public async Task TwoFailedConfigCommitsKeepAuthoritativeRevisionInPlaintextEnvelope()
    {
        using var directory = TestDirectory.Create();
        var innerStore = new AvaloniaJsonConfigStore(directory.Path);
        var failingStore = new FailNextSaveStore(innerStore);
        var backend = new PlaintextSensitiveSettingsBackend(directory.Path);
        UnifiedConfigurationService CreateService() => new(
            failingStore,
            new GuiNewJsonConfigImporter(),
            new GuiJsonConfigImporter(),
            new UiLogService(),
            directory.Path,
            new SensitiveSettingsService([backend], new FakeConsent(true), isLinux: false));

        var first = CreateService();
        first.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity] = "authoritative-a";
        await first.SaveAsync();
        var storeId = first.CurrentConfig.SensitiveStorage.StoreId;
        first.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity] = "orphan-b";
        failingStore.FailNextSave = true;
        await Assert.ThrowsAsync<IOException>(() => first.SaveAsync());

        var second = CreateService();
        await second.LoadOrBootstrapAsync();
        Assert.Equal("authoritative-a", second.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
        second.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity] = "replacement-c";
        failingStore.FailNextSave = true;
        await Assert.ThrowsAsync<IOException>(() => second.SaveAsync());

        var envelopePath = Path.Combine(directory.Path, "data", "sensitive-settings.json");
        var envelope = JsonSerializer.Deserialize<SensitiveSettingsEnvelope>(await File.ReadAllTextAsync(envelopePath));
        Assert.NotNull(envelope);
        Assert.Contains(storeId, envelope!.Stores);
        var revisions = envelope.Stores[storeId];
        Assert.Equal(2, revisions.Current.Revision);
        Assert.Equal("replacement-c", revisions.Current.Profiles["Default"][ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
        Assert.NotNull(revisions.Previous);
        Assert.Equal(1, revisions.Previous!.Revision);
        Assert.Equal("authoritative-a", revisions.Previous.Profiles["Default"][ConfigurationKeys.RemoteControlUserIdentity]!.ToString());

        var third = CreateService();
        await third.LoadOrBootstrapAsync();
        Assert.Equal(1, third.CurrentConfig.SensitiveStorage.Revision);
        Assert.Equal("authoritative-a", third.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
        third.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity] = "successful-d";
        await third.SaveAsync();

        Assert.Equal(2, third.CurrentConfig.SensitiveStorage.Revision);
        Assert.Equal(2, (await innerStore.LoadAsync())!.SensitiveStorage.Revision);
        Assert.Equal(
            "successful-d",
            (await backend.ReadAsync(storeId, 2)).Profiles["Default"][ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
        Assert.Equal(
            "authoritative-a",
            (await backend.ReadAsync(storeId, 1)).Profiles["Default"][ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
    }

    [Fact]
    public async Task MissingCurrentProfilePreservesMisplacedSensitiveValueAndBlocksSave()
    {
        using var directory = TestDirectory.Create();
        const string sentinel = "MISSING-PROFILE-SENSITIVE-SENTINEL";
        var config = new UnifiedConfig
        {
            SchemaVersion = 2,
            CurrentProfile = "Missing",
            Profiles = new Dictionary<string, UnifiedProfile>(StringComparer.OrdinalIgnoreCase)
            {
                ["Alt"] = new(),
            },
        };
        config.GlobalValues[ConfigurationKeys.RemoteControlUserIdentity] = sentinel;
        var store = new AvaloniaJsonConfigStore(directory.Path);
        await store.SaveAsync(config);
        var original = await File.ReadAllTextAsync(store.ConfigPath);
        var service = CreateConfigurationService(directory.Path, new FakeBackend(SensitiveStorageBackendKind.SystemProtected), store);

        var result = await service.LoadOrBootstrapAsync();

        Assert.Equal(original, await File.ReadAllTextAsync(store.ConfigPath));
        Assert.Equal(sentinel, service.CurrentConfig.GlobalValues[ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
        Assert.Equal(SensitiveStorageState.Unavailable, result.SensitiveStorageState);
        Assert.Equal(SensitiveStorageErrorCode.ActionRequired, result.SensitiveStorageErrorCode);
        Assert.Contains(result.ValidationIssues, issue => issue.Code == "CurrentProfileMissing" && issue.Blocking);
        await Assert.ThrowsAsync<SensitiveStorageException>(() => service.SaveAsync());
        Assert.Equal(original, await File.ReadAllTextAsync(store.ConfigPath));
    }

    [Fact]
    public async Task UnreadableLegacyDpapiValueIsReportedAndOriginalConfigRemainsAuthoritative()
    {
        using var directory = TestDirectory.Create();
        var bytes = new byte[96];
        byte[] header =
        [
            0x01, 0x00, 0x00, 0x00,
            0xd0, 0x8c, 0x9d, 0xdf, 0x01, 0x15, 0xd1, 0x11,
            0x8c, 0x7a, 0x00, 0xc0, 0x4f, 0xc2, 0x97, 0xeb,
        ];
        header.CopyTo(bytes, 0);
        var blob = Convert.ToBase64String(bytes);
        var config = new UnifiedConfig();
        config.GlobalValues[ConfigurationKeys.MirrorChyanCdk] = blob;
        var store = new AvaloniaJsonConfigStore(directory.Path);
        await store.SaveAsync(config);
        var original = await File.ReadAllTextAsync(store.ConfigPath);
        var service = CreateConfigurationService(directory.Path, new FakeBackend(SensitiveStorageBackendKind.SystemProtected), store);

        var result = await service.LoadOrBootstrapAsync();

        Assert.Equal(original, await File.ReadAllTextAsync(store.ConfigPath));
        Assert.Equal(string.Empty, service.CurrentConfig.GlobalValues[ConfigurationKeys.MirrorChyanCdk]!.ToString());
        Assert.NotNull(result.ImportReport);
        Assert.Contains(result.ImportReport!.UnreadableValues, value => value.Key == ConfigurationKeys.MirrorChyanCdk);
        Assert.Equal(SensitiveStorageState.Unavailable, result.SensitiveStorageState);
        Assert.Equal(SensitiveStorageErrorCode.ActionRequired, result.SensitiveStorageErrorCode);
        Assert.True(service.HasUnreadableLegacySensitiveValuesPending);
        await Assert.ThrowsAsync<SensitiveStorageException>(() => service.SaveAsync());
        Assert.Equal(original, await File.ReadAllTextAsync(store.ConfigPath));

        await service.ConfirmDiscardUnreadableLegacySensitiveValuesAsync();

        Assert.False(service.HasUnreadableLegacySensitiveValuesPending);
        Assert.Equal(SensitiveStorageState.Uninitialized, service.SensitiveStorageState);
        Assert.Equal(original, await File.ReadAllTextAsync(store.ConfigPath));
        service.CurrentConfig.GlobalValues[ConfigurationKeys.MirrorChyanCdk] = "replacement-cdk";
        await service.SaveAsync();

        var persisted = await File.ReadAllTextAsync(store.ConfigPath);
        Assert.DoesNotContain(blob, persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("replacement-cdk", persisted, StringComparison.Ordinal);
        Assert.Equal(SensitiveStorageBackendKind.SystemProtected, service.CurrentConfig.SensitiveStorage.Backend);
    }

    [Fact]
    public async Task ManualImportRequiresConfirmationBeforeDiscardingUnreadableLegacySecret()
    {
        using var directory = TestDirectory.Create();
        var backend = new FakeBackend(SensitiveStorageBackendKind.SystemProtected);
        var service = CreateConfigurationService(directory.Path, backend);
        service.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity] = "old-authoritative";
        await service.SaveAsync();
        var originalStoreId = service.CurrentConfig.SensitiveStorage.StoreId;
        var originalRevision = service.CurrentConfig.SensitiveStorage.Revision;

        var bytes = new byte[96];
        byte[] header =
        [
            0x01, 0x00, 0x00, 0x00,
            0xd0, 0x8c, 0x9d, 0xdf, 0x01, 0x15, 0xd1, 0x11,
            0x8c, 0x7a, 0x00, 0xc0, 0x4f, 0xc2, 0x97, 0xeb,
        ];
        header.CopyTo(bytes, 0);
        var blob = Convert.ToBase64String(bytes);
        var guiPath = Path.Combine(directory.Path, "config", "gui.json");
        await File.WriteAllTextAsync(
            guiPath,
            $$"""
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "{{ConfigurationKeys.RemoteControlUserIdentity}}": "{{blob}}"
                }
              }
            }
            """);
        var request = new LegacyImportRequest(
            LegacyConfigSnapshot.FromPaths(null, guiPath),
            ImportSource.GuiOnly,
            ManualImport: true);

        var blocked = await service.ImportLegacyAsync(request);

        Assert.False(blocked.AppliedConfig);
        Assert.False(blocked.Success);
        Assert.Single(blocked.UnreadableValues);
        Assert.Equal("old-authoritative", service.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
        Assert.Equal(originalStoreId, service.CurrentConfig.SensitiveStorage.StoreId);
        Assert.Equal(originalRevision, service.CurrentConfig.SensitiveStorage.Revision);
        Assert.Equal(1, backend.WriteCount);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(directory.Path, "config"), "avalonia.json.bak.*"));

        var applied = await service.ImportLegacyAsync(
            request with { AllowDiscardUnreadableSensitiveValues = true });

        Assert.True(applied.AppliedConfig);
        Assert.True(applied.Success);
        Assert.Equal(string.Empty, service.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
        Assert.Equal(originalStoreId, service.CurrentConfig.SensitiveStorage.StoreId);
        Assert.Equal(originalRevision + 1, service.CurrentConfig.SensitiveStorage.Revision);
        Assert.Equal(2, backend.WriteCount);
    }

    [Fact]
    public async Task SecureBackendLoadRetriesObsoletePlaintextCleanupAfterPriorFailure()
    {
        using var directory = TestDirectory.Create();
        const string storeId = "migrated-store";
        var secure = new FakeBackend(SensitiveStorageBackendKind.SystemProtected);
        secure.Seed(Document(storeId, 4, "secure-authoritative"));
        var plaintext = new FakeBackend(SensitiveStorageBackendKind.Plaintext)
        {
            DeleteFailure = new SensitiveStorageException(SensitiveStorageErrorCode.PermissionDenied, "simulated cleanup failure"),
        };
        plaintext.Seed(Document(storeId, 3, "obsolete-plaintext"));
        var store = new AvaloniaJsonConfigStore(directory.Path);
        await store.SaveAsync(ConfigForSelectedBackend(SensitiveStorageBackendKind.SystemProtected, storeId, 4));

        var firstSensitiveService = new SensitiveSettingsService([secure, plaintext], new FakeConsent(false), isLinux: false);
        var first = new UnifiedConfigurationService(
            store,
            new GuiNewJsonConfigImporter(),
            new GuiJsonConfigImporter(),
            new UiLogService(),
            directory.Path,
            firstSensitiveService);
        await first.LoadOrBootstrapAsync();

        Assert.Equal(SensitiveStorageState.Available, firstSensitiveService.State);
        Assert.Equal(1, plaintext.DeleteCount);
        Assert.Equal("obsolete-plaintext", (await plaintext.ReadAsync(storeId, 3)).Profiles["Default"][ConfigurationKeys.RemoteControlUserIdentity]!.ToString());

        plaintext.DeleteFailure = null;
        var restarted = new UnifiedConfigurationService(
            store,
            new GuiNewJsonConfigImporter(),
            new GuiJsonConfigImporter(),
            new UiLogService(),
            directory.Path,
            new SensitiveSettingsService([secure, plaintext], new FakeConsent(false), isLinux: false));
        await restarted.LoadOrBootstrapAsync();

        Assert.Equal(2, plaintext.DeleteCount);
        await Assert.ThrowsAsync<SensitiveStorageException>(() => plaintext.ReadAsync(storeId, 3));
        Assert.Equal("secure-authoritative", restarted.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
    }

    [Theory]
    [InlineData(SensitiveStorageErrorCode.Unavailable, SensitiveStorageState.Unavailable)]
    [InlineData(SensitiveStorageErrorCode.Corrupt, SensitiveStorageState.Corrupt)]
    public async Task SelectedBackendFailureStillAllowsRedactedOrdinarySettingsSave(
        SensitiveStorageErrorCode errorCode,
        SensitiveStorageState expectedState)
    {
        using var directory = TestDirectory.Create();
        const string storeId = "selected-store";
        const string secret = "IN-MEMORY-SECRET-SENTINEL";
        var backend = new FakeBackend(SensitiveStorageBackendKind.SystemProtected);
        backend.Seed(Document(storeId, 7, secret));
        var persistedConfig = ConfigForSelectedBackend(SensitiveStorageBackendKind.SystemProtected, storeId, 7);
        persistedConfig.GlobalValues["ordinary-setting"] = "before";
        var store = new AvaloniaJsonConfigStore(directory.Path);
        await store.SaveAsync(persistedConfig);
        var sensitive = new SensitiveSettingsService([backend], new FakeConsent(false), isLinux: false);
        var service = new UnifiedConfigurationService(
            store,
            new GuiNewJsonConfigImporter(),
            new GuiJsonConfigImporter(),
            new UiLogService(),
            directory.Path,
            sensitive);
        await service.LoadOrBootstrapAsync();
        Assert.Equal(
            secret,
            service.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity]!.ToString());

        sensitive.RecordFailure(new SensitiveStorageException(errorCode, "selected backend unavailable"));
        var readCount = backend.ReadCount;
        var writeCount = backend.WriteCount;
        service.CurrentConfig.GlobalValues["ordinary-setting"] = "after";

        await service.SaveAsync();

        Assert.Equal(expectedState, service.SensitiveStorageState);
        Assert.Equal(errorCode, service.SensitiveStorageErrorCode);
        Assert.Equal(readCount, backend.ReadCount);
        Assert.Equal(writeCount, backend.WriteCount);
        Assert.Equal(secret, service.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
        var onDisk = await store.LoadAsync();
        Assert.NotNull(onDisk);
        Assert.Equal("after", onDisk!.GlobalValues["ordinary-setting"]!.ToString());
        Assert.Equal(SensitiveStorageBackendKind.SystemProtected, onDisk.SensitiveStorage.Backend);
        Assert.Equal(storeId, onDisk.SensitiveStorage.StoreId);
        Assert.Equal(7, onDisk.SensitiveStorage.Revision);
        Assert.False(onDisk.Profiles["Default"].Values.ContainsKey(ConfigurationKeys.RemoteControlUserIdentity));
        Assert.DoesNotContain(secret, await File.ReadAllTextAsync(store.ConfigPath), StringComparison.Ordinal);

        var restarted = CreateConfigurationService(directory.Path, backend, store);
        await restarted.LoadOrBootstrapAsync();
        Assert.Equal(
            secret,
            restarted.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
    }

    [Theory]
    [InlineData(SensitiveStorageErrorCode.Unavailable, SensitiveStorageState.Unavailable)]
    [InlineData(SensitiveStorageErrorCode.Corrupt, SensitiveStorageState.Corrupt)]
    public async Task SelectedBackendLoadFailureAllowsSecretFreeOrdinarySettingsSave(
        SensitiveStorageErrorCode errorCode,
        SensitiveStorageState expectedState)
    {
        using var directory = TestDirectory.Create();
        const string storeId = "selected-store";
        const string secret = "BACKEND-ONLY-SECRET-SENTINEL";
        var backend = new FakeBackend(SensitiveStorageBackendKind.SystemProtected)
        {
            ReadFailure = new SensitiveStorageException(errorCode, "selected backend read failed"),
        };
        backend.Seed(Document(storeId, 9, secret));
        var store = new AvaloniaJsonConfigStore(directory.Path);
        await store.SaveAsync(ConfigForSelectedBackend(SensitiveStorageBackendKind.SystemProtected, storeId, 9));
        var service = CreateConfigurationService(directory.Path, backend, store);

        var result = await service.LoadOrBootstrapAsync();

        Assert.Equal(expectedState, result.SensitiveStorageState);
        Assert.False(service.CurrentConfig.Profiles["Default"].Values.ContainsKey(ConfigurationKeys.RemoteControlUserIdentity));
        var readCount = backend.ReadCount;
        service.CurrentConfig.GlobalValues["ordinary-setting"] = "saved-during-failure";
        await service.SaveAsync();

        Assert.Equal(readCount, backend.ReadCount);
        var onDisk = await store.LoadAsync();
        Assert.NotNull(onDisk);
        Assert.Equal("saved-during-failure", onDisk!.GlobalValues["ordinary-setting"]!.ToString());
        Assert.Equal(SensitiveStorageBackendKind.SystemProtected, onDisk.SensitiveStorage.Backend);
        Assert.Equal(storeId, onDisk.SensitiveStorage.StoreId);
        Assert.Equal(9, onDisk.SensitiveStorage.Revision);
        Assert.DoesNotContain(secret, await File.ReadAllTextAsync(store.ConfigPath), StringComparison.Ordinal);

        backend.ReadFailure = null;
        var restarted = CreateConfigurationService(directory.Path, backend, store);
        await restarted.LoadOrBootstrapAsync();
        Assert.Equal(
            secret,
            restarted.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("add")]
    [InlineData("case-rename")]
    [InlineData("same-name-replace")]
    public async Task SelectedBackendLoadFailureRejectsProfileTopologyChanges(string change)
    {
        using var directory = TestDirectory.Create();
        const string storeId = "selected-store";
        const string secret = "BACKEND-ONLY-TOPOLOGY-SECRET";
        var backend = new FakeBackend(SensitiveStorageBackendKind.SystemProtected)
        {
            ReadFailure = new SensitiveStorageException(
                SensitiveStorageErrorCode.Unavailable,
                "selected backend read failed"),
        };
        backend.Seed(Document(storeId, 9, secret));
        var store = new AvaloniaJsonConfigStore(directory.Path);
        await store.SaveAsync(ConfigForSelectedBackend(SensitiveStorageBackendKind.SystemProtected, storeId, 9));
        var service = CreateConfigurationService(directory.Path, backend, store);
        await service.LoadOrBootstrapAsync();
        var originalConfigFile = await File.ReadAllTextAsync(store.ConfigPath);

        switch (change)
        {
            case "delete":
                service.CurrentConfig.Profiles.Remove("Default");
                break;
            case "add":
                service.CurrentConfig.Profiles["Imported"] = new UnifiedProfile();
                break;
            case "case-rename":
                service.CurrentConfig.Profiles.Remove("Default");
                service.CurrentConfig.Profiles["default"] = new UnifiedProfile();
                service.CurrentConfig.CurrentProfile = "default";
                break;
            case "same-name-replace":
                service.CurrentConfig.Profiles["Default"] = new UnifiedProfile();
                break;
        }

        var error = await Assert.ThrowsAsync<SensitiveStorageException>(() => service.SaveAsync());

        Assert.Equal(SensitiveStorageErrorCode.ActionRequired, error.ErrorCode);
        Assert.Equal(originalConfigFile, await File.ReadAllTextAsync(store.ConfigPath));
        backend.ReadFailure = null;
        Assert.Equal(9, (await backend.ReadCurrentAsync(storeId)).Revision);
    }

    [Fact]
    public async Task DeleteLastProfileAfterSelectedBackendLoadFailureRollsBackAndCannotReviveSecret()
    {
        using var directory = TestDirectory.Create();
        const string storeId = "selected-store";
        const string secret = "DELETED-PROFILE-SECRET-SENTINEL";
        var backend = new FakeBackend(SensitiveStorageBackendKind.SystemProtected)
        {
            ReadFailure = new SensitiveStorageException(
                SensitiveStorageErrorCode.Unavailable,
                "selected backend read failed"),
        };
        backend.Seed(Document(storeId, 9, secret));
        var store = new AvaloniaJsonConfigStore(directory.Path);
        await store.SaveAsync(ConfigForSelectedBackend(SensitiveStorageBackendKind.SystemProtected, storeId, 9));
        var service = CreateConfigurationService(directory.Path, backend, store);
        await service.LoadOrBootstrapAsync();
        var profiles = new ConfigurationProfileFeatureService(service);

        var result = await profiles.DeleteProfileAsync("Default");

        Assert.False(result.Success);
        Assert.Equal(UiErrorCode.ConfigurationProfileSaveFailed, result.Error?.Code);
        Assert.True(service.CurrentConfig.Profiles.ContainsKey("Default"));
        Assert.Equal("Default", service.CurrentConfig.Profiles.Keys.Single());
        service.CurrentConfig.GlobalValues["ordinary-after-rollback"] = "saved";
        await service.SaveAsync();
        Assert.Equal("saved", (await store.LoadAsync())!.GlobalValues["ordinary-after-rollback"]!.ToString());

        backend.ReadFailure = null;
        var restarted = CreateConfigurationService(directory.Path, backend, store);
        await restarted.LoadOrBootstrapAsync();
        Assert.Equal("Default", restarted.CurrentConfig.Profiles.Keys.Single());
        Assert.Equal(
            secret,
            restarted.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
    }

    [Fact]
    public async Task SelectedBackendLoadFailureStillAllowsProfileSwitchWithoutLosingSecrets()
    {
        using var directory = TestDirectory.Create();
        const string storeId = "selected-store";
        var document = Document(storeId, 9, "default-secret");
        document.Profiles["Second"] = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase)
        {
            [ConfigurationKeys.RemoteControlUserIdentity] = "second-secret",
        };
        var backend = new FakeBackend(SensitiveStorageBackendKind.SystemProtected)
        {
            ReadFailure = new SensitiveStorageException(
                SensitiveStorageErrorCode.Unavailable,
                "selected backend read failed"),
        };
        backend.Seed(document);
        var persistedConfig = ConfigForSelectedBackend(SensitiveStorageBackendKind.SystemProtected, storeId, 9);
        persistedConfig.Profiles["Second"] = new UnifiedProfile();
        var store = new AvaloniaJsonConfigStore(directory.Path);
        await store.SaveAsync(persistedConfig);
        var service = CreateConfigurationService(directory.Path, backend, store);
        await service.LoadOrBootstrapAsync();
        var profiles = new ConfigurationProfileFeatureService(service);

        var result = await profiles.SwitchProfileAsync("Second");

        Assert.True(result.Success);
        Assert.Equal("Second", (await store.LoadAsync())!.CurrentProfile);

        backend.ReadFailure = null;
        var restarted = CreateConfigurationService(directory.Path, backend, store);
        await restarted.LoadOrBootstrapAsync();
        Assert.Equal("Second", restarted.CurrentConfig.CurrentProfile);
        Assert.Equal(
            "default-secret",
            restarted.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
        Assert.Equal(
            "second-secret",
            restarted.CurrentConfig.Profiles["Second"].Values[ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
    }

    [Theory]
    [InlineData(SensitiveStorageErrorCode.Unavailable)]
    [InlineData(SensitiveStorageErrorCode.Corrupt)]
    public async Task SelectedBackendFailureStillRejectsSensitiveSettingsChange(SensitiveStorageErrorCode errorCode)
    {
        using var directory = TestDirectory.Create();
        const string storeId = "selected-store";
        const string originalSecret = "ORIGINAL-SECRET-SENTINEL";
        const string changedSecret = "CHANGED-SECRET-SENTINEL";
        var backend = new FakeBackend(SensitiveStorageBackendKind.SystemProtected);
        backend.Seed(Document(storeId, 4, originalSecret));
        var store = new AvaloniaJsonConfigStore(directory.Path);
        await store.SaveAsync(ConfigForSelectedBackend(SensitiveStorageBackendKind.SystemProtected, storeId, 4));
        var sensitive = new SensitiveSettingsService([backend], new FakeConsent(false), isLinux: false);
        var service = new UnifiedConfigurationService(
            store,
            new GuiNewJsonConfigImporter(),
            new GuiJsonConfigImporter(),
            new UiLogService(),
            directory.Path,
            sensitive);
        await service.LoadOrBootstrapAsync();
        sensitive.RecordFailure(new SensitiveStorageException(errorCode, "selected backend unavailable"));
        var originalConfigFile = await File.ReadAllTextAsync(store.ConfigPath);
        var readCount = backend.ReadCount;
        var writeCount = backend.WriteCount;
        service.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity] = changedSecret;

        var error = await Assert.ThrowsAsync<SensitiveStorageException>(() => service.SaveAsync());

        Assert.Equal(SensitiveStorageErrorCode.ActionRequired, error.ErrorCode);
        Assert.Equal(originalConfigFile, await File.ReadAllTextAsync(store.ConfigPath));
        Assert.Equal(readCount, backend.ReadCount);
        Assert.Equal(writeCount, backend.WriteCount);
        Assert.Equal(
            originalSecret,
            (await backend.ReadAsync(storeId, 4)).Profiles["Default"][ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
    }

    [Theory]
    [InlineData(SensitiveStorageErrorCode.PermissionDenied)]
    [InlineData(SensitiveStorageErrorCode.UserCanceled)]
    public async Task FirstSensitiveWriteFailureRecordsUnavailableState(SensitiveStorageErrorCode errorCode)
    {
        var backend = new FakeBackend(SensitiveStorageBackendKind.SystemProtected)
        {
            VerifyFailure = new SensitiveStorageException(errorCode, "first write unavailable"),
        };
        var service = new SensitiveSettingsService([backend], new FakeConsent(false), isLinux: false);
        var stateChangeCount = 0;
        service.StateChanged += () => stateChangeCount++;

        var error = await Assert.ThrowsAsync<SensitiveStorageException>(() => service.PrepareSaveAsync(ConfigWithSecret()));

        Assert.Equal(errorCode, error.ErrorCode);
        Assert.Equal(SensitiveStorageState.Unavailable, service.State);
        Assert.Equal(errorCode, service.LastErrorCode);
        Assert.Equal(1, stateChangeCount);
    }

    [Fact]
    public async Task CallerCancellationDoesNotChangeAvailableSelectedBackendState()
    {
        var backend = new FakeBackend(SensitiveStorageBackendKind.SystemProtected);
        backend.Seed(Document("selected-store", 1, "secret"));
        var service = new SensitiveSettingsService([backend], new FakeConsent(false), isLinux: false);
        var config = ConfigForSelectedBackend(
            SensitiveStorageBackendKind.SystemProtected,
            "selected-store",
            revision: 1);
        await service.LoadAsync(config);
        Assert.Equal(SensitiveStorageState.Available, service.State);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        backend.ReadFailure = new OperationCanceledException(cancellation.Token);

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.LoadAsync(config, cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(SensitiveStorageState.Available, service.State);
        Assert.Null(service.LastErrorCode);
    }

    [Theory]
    [InlineData(SensitiveStorageErrorCode.UserCanceled)]
    [InlineData(SensitiveStorageErrorCode.Unavailable)]
    public async Task FailedInitialBackendSelectionCanBeExplicitlyRetried(SensitiveStorageErrorCode errorCode)
    {
        using var directory = TestDirectory.Create();
        var backend = new FakeBackend(SensitiveStorageBackendKind.SystemProtected)
        {
            VerifyFailure = new SensitiveStorageException(errorCode, "first selection failed"),
        };
        var sensitiveService = new SensitiveSettingsService([backend], new FakeConsent(false), isLinux: false);
        var configService = new UnifiedConfigurationService(
            new AvaloniaJsonConfigStore(directory.Path),
            new GuiNewJsonConfigImporter(),
            new GuiJsonConfigImporter(),
            new UiLogService(),
            directory.Path,
            sensitiveService);
        configService.CurrentConfig.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity] = "retry-secret";

        var firstError = await Assert.ThrowsAsync<SensitiveStorageException>(() => configService.SaveAsync());
        Assert.Equal(errorCode, firstError.ErrorCode);
        Assert.Equal(SensitiveStorageBackendKind.Uninitialized, configService.CurrentConfig.SensitiveStorage.Backend);
        Assert.Equal(SensitiveStorageState.Unavailable, sensitiveService.State);
        Assert.True(sensitiveService.CanRetryInitialBackendSelection);

        backend.VerifyFailure = null;
        await configService.RetrySecureStorageAsync();

        Assert.Equal(SensitiveStorageBackendKind.SystemProtected, configService.CurrentConfig.SensitiveStorage.Backend);
        Assert.Equal(SensitiveStorageState.Available, sensitiveService.State);
        Assert.False(sensitiveService.CanRetryInitialBackendSelection);
        Assert.Equal(2, backend.VerifyCount);
        Assert.Equal(1, backend.WriteCount);
        var metadata = configService.CurrentConfig.SensitiveStorage;
        Assert.Equal(
            "retry-secret",
            (await backend.ReadAsync(metadata.StoreId, metadata.Revision))
                .Profiles["Default"][ConfigurationKeys.RemoteControlUserIdentity]!.ToString());
    }

    private static async Task<Exception?> CaptureExceptionAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int CreateFifo(string path, uint mode);

    private static UnifiedConfig ConfigWithSecret()
    {
        var config = new UnifiedConfig();
        config.Profiles["Default"].Values[ConfigurationKeys.RemoteControlUserIdentity] = "sentinel";
        return config;
    }

    private static JsonObject HistoricalBackupWithAllSensitiveValues(string sentinel, string preservedMarker)
    {
        var globalValues = new JsonObject();
        var profileValues = new JsonObject();
        foreach (var definition in SensitiveSettingKeyRegistry.Definitions)
        {
            globalValues[definition.Key] = sentinel;
            profileValues[definition.Key] = sentinel;
        }

        return new JsonObject
        {
            [nameof(UnifiedConfig.SchemaVersion)] = 2,
            [nameof(UnifiedConfig.CurrentProfile)] = "Default",
            [nameof(UnifiedConfig.GlobalValues)] = globalValues,
            [nameof(UnifiedConfig.Profiles)] = new JsonObject
            {
                ["Default"] = new JsonObject
                {
                    [nameof(UnifiedProfile.Values)] = profileValues,
                    [nameof(UnifiedProfile.TaskQueue)] = new JsonArray(),
                },
            },
            ["UnknownHistoricalPayload"] = new JsonObject
            {
                ["RawMarker"] = preservedMarker,
            },
        };
    }

    private static SensitiveSettingsDocument Document(string storeId, long revision, string value)
        => new()
        {
            StoreId = storeId,
            Revision = revision,
            Profiles = new Dictionary<string, Dictionary<string, JsonNode?>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Default"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    [ConfigurationKeys.RemoteControlUserIdentity] = value,
                },
            },
        };

    private static UnifiedConfig ConfigForSelectedBackend(
        SensitiveStorageBackendKind backend,
        string storeId,
        long revision)
        => new()
        {
            SensitiveStorage = new SensitiveStorageMetadata
            {
                Backend = backend,
                StoreId = storeId,
                Revision = revision,
            },
        };

    private static UnifiedConfigurationService CreateConfigurationService(
        string root,
        ISensitiveSettingsBackend backend,
        IUnifiedConfigStore? store = null)
    {
        var log = new UiLogService();
        return new UnifiedConfigurationService(
            store ?? new AvaloniaJsonConfigStore(root),
            new GuiNewJsonConfigImporter(),
            new GuiJsonConfigImporter(),
            log,
            root,
            new SensitiveSettingsService([backend], new FakeConsent(false), isLinux: false));
    }

    private static byte[] BuildJpegWithExifUserComment(string comment)
    {
        var commentBytes = Encoding.BigEndianUnicode.GetBytes(comment);
        var userComment = Encoding.ASCII.GetBytes("UNICODE\0").Concat(commentBytes).ToArray();
        using var tiffStream = new MemoryStream();
        using (var tiff = new BinaryWriter(tiffStream, Encoding.UTF8, leaveOpen: true))
        {
            tiff.Write(Encoding.ASCII.GetBytes("II"));
            tiff.Write((ushort)42);
            tiff.Write((uint)8);
            tiff.Write((ushort)1);
            tiff.Write((ushort)0x8769);
            tiff.Write((ushort)4);
            tiff.Write((uint)1);
            tiff.Write((uint)26);
            tiff.Write((uint)0);
            tiff.Write((ushort)1);
            tiff.Write((ushort)0x9286);
            tiff.Write((ushort)7);
            tiff.Write((uint)userComment.Length);
            tiff.Write((uint)44);
            tiff.Write((uint)0);
            tiff.Write(userComment);
        }

        var exifPayload = Encoding.ASCII.GetBytes("Exif\0\0").Concat(tiffStream.ToArray()).ToArray();
        using var jpegStream = new MemoryStream();
        using (var jpeg = new BinaryWriter(jpegStream, Encoding.UTF8, leaveOpen: true))
        {
            jpeg.Write((byte)0xFF);
            jpeg.Write((byte)0xD8);
            jpeg.Write((byte)0xFF);
            jpeg.Write((byte)0xE1);
            WriteBigEndian(jpeg, checked((ushort)(exifPayload.Length + 2)));
            jpeg.Write(exifPayload);
            jpeg.Write((byte)0xFF);
            jpeg.Write((byte)0xD9);
        }

        return jpegStream.ToArray();
    }

    private static byte[] BuildWebpWithXmp(string sentinel)
    {
        var imageWebp = Convert.FromBase64String("UklGRiIAAABXRUJQVlA4IBYAAAAwAQCdASoBAAEALmk0mk0iIiIiIgBoSygABc6zbAAA");
        var xmp = Encoding.UTF8.GetBytes($"<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:Description source=\"/Users/{sentinel}/capture.webp\" /></x:xmpmeta>");
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write((uint)0);
            writer.Write(Encoding.ASCII.GetBytes("WEBP"));
            writer.Write(Encoding.ASCII.GetBytes("VP8X"));
            writer.Write((uint)10);
            writer.Write((byte)0x04);
            writer.Write(new byte[9]);
            writer.Write(imageWebp, 12, imageWebp.Length - 12);
            writer.Write(Encoding.ASCII.GetBytes("XMP "));
            writer.Write((uint)xmp.Length);
            writer.Write(xmp);
            if ((xmp.Length & 1) != 0)
            {
                writer.Write((byte)0);
            }
        }

        var result = stream.ToArray();
        BitConverter.GetBytes(checked((uint)(result.Length - 8))).CopyTo(result, 4);
        return result;
    }

    private static byte[] BuildPngWithCompressedText(string sentinel)
    {
        var ihdr = new byte[13];
        ihdr[3] = 1;
        ihdr[7] = 1;
        ihdr[8] = 8;
        ihdr[9] = 6;
        var compressedText = CompressZlib(Encoding.UTF8.GetBytes($"source=/Users/{sentinel}/capture.png;gps=31.2304,121.4737"));
        var ztxt = Encoding.ASCII.GetBytes("Comment\0")
            .Concat(new byte[] { 0 })
            .Concat(compressedText)
            .ToArray();
        var imageData = CompressZlib([0, 0, 0, 0, 0]);

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            WritePngChunk(writer, "IHDR", ihdr);
            WritePngChunk(writer, "zTXt", ztxt);
            WritePngChunk(writer, "IDAT", imageData);
            WritePngChunk(writer, "IEND", []);
        }

        return stream.ToArray();
    }

    private static byte[] CompressZlib(byte[] value)
    {
        using var output = new MemoryStream();
        using (var compressor = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            compressor.Write(value);
        }

        return output.ToArray();
    }

    private static void WritePngChunk(BinaryWriter writer, string type, byte[] data)
    {
        var typeBytes = Encoding.ASCII.GetBytes(type);
        WriteBigEndian(writer, checked((uint)data.Length));
        writer.Write(typeBytes);
        writer.Write(data);
        var crc = UpdateCrc32(UpdateCrc32(uint.MaxValue, typeBytes), data) ^ uint.MaxValue;
        WriteBigEndian(writer, crc);
    }

    private static uint UpdateCrc32(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
            }
        }

        return crc;
    }

    private static void WriteBigEndian(BinaryWriter writer, ushort value)
    {
        writer.Write((byte)(value >> 8));
        writer.Write((byte)value);
    }

    private static void WriteBigEndian(BinaryWriter writer, uint value)
    {
        writer.Write((byte)(value >> 24));
        writer.Write((byte)(value >> 16));
        writer.Write((byte)(value >> 8));
        writer.Write((byte)value);
    }

    private sealed class TestDirectory : IDisposable
    {
        private TestDirectory(string path) => Path = path;

        public string Path { get; }

        public static TestDirectory Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"maa-sensitive-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TestDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class FailNextSaveStore(IUnifiedConfigStore inner) : IUnifiedConfigStore
    {
        public bool FailNextSave { get; set; }

        public string ConfigPath => inner.ConfigPath;

        public bool Exists() => inner.Exists();

        public Task<UnifiedConfig?> LoadAsync(CancellationToken cancellationToken = default)
            => inner.LoadAsync(cancellationToken);

        public Task SaveAsync(UnifiedConfig config, CancellationToken cancellationToken = default)
        {
            if (FailNextSave)
            {
                FailNextSave = false;
                return Task.FromException(new IOException("simulated config commit failure"));
            }

            return inner.SaveAsync(config, cancellationToken);
        }

        public Task BackupAsync(string suffix, CancellationToken cancellationToken = default)
            => inner.BackupAsync(suffix, cancellationToken);

        public Task BackupSanitizedAsync(UnifiedConfig config, string suffix, CancellationToken cancellationToken = default)
            => inner.BackupSanitizedAsync(config, suffix, cancellationToken);
    }

    private sealed class PauseNextSaveStore(IUnifiedConfigStore inner) : IUnifiedConfigStore
    {
        private readonly TaskCompletionSource _saveEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseSave = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SaveEntered => _saveEntered.Task;

        public string ConfigPath => inner.ConfigPath;

        public bool Exists() => inner.Exists();

        public Task<UnifiedConfig?> LoadAsync(CancellationToken cancellationToken = default)
            => inner.LoadAsync(cancellationToken);

        public async Task SaveAsync(UnifiedConfig config, CancellationToken cancellationToken = default)
        {
            _saveEntered.TrySetResult();
            await _releaseSave.Task.WaitAsync(cancellationToken);
            await inner.SaveAsync(config, cancellationToken);
        }

        public void ReleaseSave() => _releaseSave.TrySetResult();

        public Task BackupAsync(string suffix, CancellationToken cancellationToken = default)
            => inner.BackupAsync(suffix, cancellationToken);

        public Task BackupSanitizedAsync(UnifiedConfig config, string suffix, CancellationToken cancellationToken = default)
            => inner.BackupSanitizedAsync(config, suffix, cancellationToken);
    }

    private sealed class FakeConsent(bool result) : IPlaintextSensitiveStorageConsent
    {
        public int RequestCount { get; private set; }

        public Task<bool> ConfirmAsync(CancellationToken cancellationToken = default)
        {
            RequestCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeBackend(SensitiveStorageBackendKind kind) : ISensitiveSettingsBackend, IDeletableSensitiveSettingsBackend
    {
        private readonly Dictionary<(string StoreId, long Revision), SensitiveSettingsDocument> _documents = [];

        public SensitiveStorageBackendKind Kind { get; } = kind;

        public Exception? VerifyFailure { get; set; }

        public Exception? ReadFailure { get; set; }

        public Exception? WriteFailure { get; init; }

        public Exception? DeleteFailure { get; set; }

        public bool ClearDocumentsOnVerify { get; init; }

        public Func<SensitiveSettingsDocument, SensitiveSettingsDocument>? ReadTransform { get; init; }

        public int VerifyCount { get; private set; }

        public int ReadCount { get; private set; }

        public int WriteCount { get; private set; }

        public int DeleteCount { get; private set; }

        public Task VerifyPersistenceAsync(CancellationToken cancellationToken = default)
        {
            VerifyCount++;
            if (ClearDocumentsOnVerify)
            {
                _documents.Clear();
            }

            return VerifyFailure is null ? Task.CompletedTask : Task.FromException(VerifyFailure);
        }

        public Task<SensitiveSettingsDocument> ReadAsync(string storeId, long revision, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            if (ReadFailure is not null)
            {
                return Task.FromException<SensitiveSettingsDocument>(ReadFailure);
            }

            if (!_documents.TryGetValue((storeId, revision), out var document))
            {
                return Task.FromException<SensitiveSettingsDocument>(new SensitiveStorageException(
                    SensitiveStorageErrorCode.RevisionNotFound,
                    "simulated revision not found"));
            }

            return Task.FromResult(ReadTransform?.Invoke(document) ?? document);
        }

        public Task<SensitiveSettingsDocument> ReadCurrentAsync(
            string storeId,
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            if (ReadFailure is not null)
            {
                return Task.FromException<SensitiveSettingsDocument>(ReadFailure);
            }

            var document = _documents
                .Where(pair => string.Equals(pair.Key.StoreId, storeId, StringComparison.Ordinal))
                .OrderByDescending(pair => pair.Key.Revision)
                .Select(pair => pair.Value)
                .FirstOrDefault();
            if (document is null)
            {
                return Task.FromException<SensitiveSettingsDocument>(new SensitiveStorageException(
                    SensitiveStorageErrorCode.RevisionNotFound,
                    "simulated store not found"));
            }

            return Task.FromResult(ReadTransform?.Invoke(document) ?? document);
        }

        public Task WriteAsync(
            SensitiveSettingsDocument document,
            long expectedRevision,
            CancellationToken cancellationToken = default)
        {
            WriteCount++;
            if (WriteFailure is not null)
            {
                return Task.FromException(WriteFailure);
            }

            var currentRevision = _documents.Keys
                .Where(key => string.Equals(key.StoreId, document.StoreId, StringComparison.Ordinal))
                .Select(key => key.Revision)
                .DefaultIfEmpty(0)
                .Max();
            if (currentRevision != expectedRevision)
            {
                return Task.FromException(new SensitiveStorageException(
                    SensitiveStorageErrorCode.ActionRequired,
                    "simulated stale writer"));
            }

            _documents[(document.StoreId, document.Revision)] = document;
            return Task.CompletedTask;
        }

        public Task ReplaceCurrentAsync(
            SensitiveSettingsDocument document,
            SensitiveSettingsDocument expectedCurrent,
            SensitiveSettingsDocument expectedPrevious,
            CancellationToken cancellationToken = default)
        {
            WriteCount++;
            if (WriteFailure is not null)
            {
                return Task.FromException(WriteFailure);
            }

            var current = _documents
                .Where(pair => string.Equals(pair.Key.StoreId, document.StoreId, StringComparison.Ordinal))
                .OrderByDescending(pair => pair.Key.Revision)
                .Select(pair => pair.Value)
                .FirstOrDefault();
            if (current is null
                || !_documents.TryGetValue((document.StoreId, expectedPrevious.Revision), out var previous)
                || !DocumentMatches(current, expectedCurrent)
                || !DocumentMatches(previous, expectedPrevious))
            {
                return Task.FromException(new SensitiveStorageException(
                    SensitiveStorageErrorCode.ActionRequired,
                    "simulated stale writer"));
            }

            _documents[(document.StoreId, document.Revision)] = document;
            return Task.CompletedTask;
        }

        private static bool DocumentMatches(
            SensitiveSettingsDocument left,
            SensitiveSettingsDocument right)
            => JsonNode.DeepEquals(
                JsonSerializer.SerializeToNode(left),
                JsonSerializer.SerializeToNode(right));

        public void Seed(SensitiveSettingsDocument document)
        {
            _documents[(document.StoreId, document.Revision)] = document;
        }

        public Task DeleteAsync(CancellationToken cancellationToken = default)
        {
            DeleteCount++;
            if (DeleteFailure is not null)
            {
                return Task.FromException(DeleteFailure);
            }

            _documents.Clear();
            return Task.CompletedTask;
        }
    }

    private sealed class RawBackupOnlyStore(string configPath) : IUnifiedConfigStore
    {
        public string ConfigPath { get; } = configPath;

        public bool Exists() => File.Exists(ConfigPath);

        public Task<UnifiedConfig?> LoadAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task SaveAsync(UnifiedConfig config, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task BackupAsync(string suffix, CancellationToken cancellationToken = default)
        {
            File.Copy(ConfigPath, ConfigPath + suffix, overwrite: true);
            return Task.CompletedTask;
        }
    }
}
