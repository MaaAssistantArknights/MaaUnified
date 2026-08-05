using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Identity.Client.Extensions.Msal;
using MAAUnified.Application.Security;

namespace MAAUnified.SecureStorage.System;

public sealed class MsalSystemStorageBackend : ISensitiveSettingsBackend
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly string _dataDirectory;
    private readonly string _lockDirectory;
    private readonly string _lockPath;
    private readonly string? _testNamespace;
    private readonly object _initializationGate = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private StorageResources? _resources;

    public MsalSystemStorageBackend(string runtimeBaseDirectory)
        : this(runtimeBaseDirectory, testNamespace: null, isolatedTestStorage: false)
    {
    }

    internal MsalSystemStorageBackend(string runtimeBaseDirectory, string testNamespace)
        : this(runtimeBaseDirectory, ValidateTestNamespace(testNamespace), isolatedTestStorage: true)
    {
    }

    private MsalSystemStorageBackend(
        string runtimeBaseDirectory,
        string? testNamespace,
        bool isolatedTestStorage)
    {
        if (isolatedTestStorage != (testNamespace is not null))
        {
            throw new ArgumentException("An isolated system storage instance requires a test namespace.");
        }

        _dataDirectory = Path.Combine(Path.GetFullPath(runtimeBaseDirectory), "data");
        _testNamespace = testNamespace;
        _lockDirectory = testNamespace is not null || OperatingSystem.IsWindows()
            ? _dataDirectory
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MAAUnified",
                "secure");
        _lockPath = Path.Combine(
            _lockDirectory,
            testNamespace is null
                ? "system-sensitive-settings.lock"
                : $"system-sensitive-settings.test-{testNamespace}.lock");
    }

    public SensitiveStorageBackendKind Kind => SensitiveStorageBackendKind.SystemProtected;

    internal void InitializeStorage() => _ = GetOrCreateResources();

    public async Task VerifyPersistenceAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var resources = GetOrCreateResources();
            using var crossProcessLock = new CrossPlatLock(_lockPath);
            await VerifyPersistenceAndCleanupAsync(resources.PersistenceProbeStorage, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            throw Classify(
                ex,
                "System-protected storage persistence verification failed.",
                classifyPayloadCorruption: false);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static StorageCreationProperties CreateStorageProperties(
        string dataDirectory,
        bool persistenceProbe,
        string? testNamespace = null)
    {
        if (testNamespace is not null)
        {
            ValidateTestNamespace(testNamespace);
        }

        var fileSuffix = testNamespace is null ? string.Empty : $".test-{testNamespace}";
        var macSuffix = testNamespace is null ? string.Empty : $".test.{testNamespace}";
        var schemaSuffix = testNamespace is null ? string.Empty : $".Test.{testNamespace}";
        var accountSuffix = testNamespace is null ? string.Empty : $".Test.{testNamespace}";
        var application = testNamespace is null ? "MAAUnified" : $"MAAUnified.Tests.{testNamespace}";
        if (persistenceProbe)
        {
            fileSuffix += ".persistence-probe";
            macSuffix += ".persistence-probe";
            schemaSuffix += ".PersistenceProbe";
        }

        return new StorageCreationPropertiesBuilder($"system-sensitive-settings{fileSuffix}.bin", dataDirectory)
            .WithMacKeyChain(
                $"io.maa.MAAUnified.sensitive-settings{macSuffix}",
                persistenceProbe
                    ? $"MAAUnified.SensitiveSettings{accountSuffix}.PersistenceProbe.v1"
                    : $"MAAUnified.SensitiveSettings{accountSuffix}.v1")
            .WithLinuxKeyring(
                $"io.maa.MAAUnified.SensitiveSettings{schemaSuffix}",
                MsalCacheHelper.LinuxKeyRingDefaultCollection,
                persistenceProbe
                    ? $"MAAUnified sensitive settings persistence probe{FormatTestLabelSuffix(testNamespace)}"
                    : $"MAAUnified sensitive settings{FormatTestLabelSuffix(testNamespace)}",
                new KeyValuePair<string, string>("application", application),
                new KeyValuePair<string, string>(persistenceProbe ? "probe-format" : "format", "1"))
            .Build();
    }

    internal static async Task VerifyPersistenceAndCleanupAsync(
        Storage persistenceProbeStorage,
        CancellationToken cancellationToken)
        => await VerifyPersistenceAndCleanupAsync(
            persistenceProbeStorage.VerifyPersistence,
            () => persistenceProbeStorage.Clear(ignoreExceptions: false),
            cancellationToken).ConfigureAwait(false);

    internal static async Task VerifyPersistenceAndCleanupAsync(
        Action verifyPersistence,
        Action clear,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Run(verifyPersistence, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            clear();
        }
    }

    internal async Task ClearIsolatedTestStorageAsync(CancellationToken cancellationToken = default)
    {
        if (_testNamespace is null)
        {
            throw new InvalidOperationException("Only isolated test storage can be cleared through this API.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var resources = GetOrCreateResources();
            using var crossProcessLock = new CrossPlatLock(_lockPath);
            var failures = new List<Exception>();
            try
            {
                await Task.Run(() => resources.Storage.Clear(ignoreExceptions: false), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }

            try
            {
                await Task.Run(() => resources.PersistenceProbeStorage.Clear(ignoreExceptions: false), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }

            if (failures.Count > 0)
            {
                throw new SensitiveStorageException(
                    SensitiveStorageErrorCode.Unavailable,
                    $"Isolated system storage cleanup failed for test namespace '{_testNamespace}'.",
                    new AggregateException(failures));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SensitiveSettingsDocument> ReadAsync(string storeId, long revision, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var resources = GetOrCreateResources();
            using var crossProcessLock = new CrossPlatLock(_lockPath);
            var bytes = await Task.Run(resources.Storage.ReadData, cancellationToken).ConfigureAwait(false);
            if (bytes is null || bytes.Length == 0)
            {
                throw new SensitiveStorageException(SensitiveStorageErrorCode.RevisionNotFound, "System-protected storage does not contain the requested revision.");
            }

            var envelope = JsonSerializer.Deserialize<SensitiveSettingsEnvelope>(bytes, SerializerOptions)
                ?? throw new SensitiveStorageException(SensitiveStorageErrorCode.Corrupt, "System-protected storage contains an invalid envelope.");
            return SensitiveSettingsEnvelopeStore.FindRevision(envelope, storeId, revision);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (SensitiveStorageException) { throw; }
        catch (Exception ex) { throw Classify(ex, "System-protected storage could not be read."); }
        finally { _gate.Release(); }
    }

    public async Task<SensitiveSettingsDocument> ReadCurrentAsync(
        string storeId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var resources = GetOrCreateResources();
            using var crossProcessLock = new CrossPlatLock(_lockPath);
            var bytes = await Task.Run(resources.Storage.ReadData, cancellationToken).ConfigureAwait(false);
            if (bytes is null || bytes.Length == 0)
            {
                throw new SensitiveStorageException(SensitiveStorageErrorCode.RevisionNotFound, "System-protected storage does not contain the requested store.");
            }

            var envelope = JsonSerializer.Deserialize<SensitiveSettingsEnvelope>(bytes, SerializerOptions)
                ?? throw new SensitiveStorageException(SensitiveStorageErrorCode.Corrupt, "System-protected storage contains an invalid envelope.");
            return SensitiveSettingsEnvelopeStore.FindCurrent(envelope, storeId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (SensitiveStorageException) { throw; }
        catch (Exception ex) { throw Classify(ex, "System-protected storage could not be read."); }
        finally { _gate.Release(); }
    }

    public async Task WriteAsync(
        SensitiveSettingsDocument document,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        SensitiveSettingKeyRegistry.ValidateDocument(document);
        await UpdateAsync(
            document,
            existing => MergeDocument(existing, document, expectedRevision),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task ReplaceCurrentAsync(
        SensitiveSettingsDocument document,
        SensitiveSettingsDocument expectedCurrent,
        SensitiveSettingsDocument expectedPrevious,
        CancellationToken cancellationToken = default)
    {
        SensitiveSettingKeyRegistry.ValidateDocument(document);
        SensitiveSettingKeyRegistry.ValidateDocument(expectedCurrent);
        SensitiveSettingKeyRegistry.ValidateDocument(expectedPrevious);
        await UpdateAsync(
            document,
            existing => SensitiveSettingsEnvelopeStore.ReplaceCurrent(
                existing,
                document,
                expectedCurrent,
                expectedPrevious),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task UpdateAsync(
        SensitiveSettingsDocument document,
        Func<SensitiveSettingsEnvelope?, SensitiveSettingsEnvelope> updateEnvelope,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var resources = GetOrCreateResources();
            using var crossProcessLock = new CrossPlatLock(_lockPath);
            SensitiveSettingsEnvelope? existing = null;
            var existingBytes = await Task.Run(resources.Storage.ReadData, cancellationToken).ConfigureAwait(false);
            if (existingBytes is { Length: > 0 })
            {
                existing = JsonSerializer.Deserialize<SensitiveSettingsEnvelope>(existingBytes, SerializerOptions)
                    ?? throw new SensitiveStorageException(SensitiveStorageErrorCode.Corrupt, "System-protected storage contains an invalid envelope.");
            }

            var envelope = updateEnvelope(existing);
            await Task.Run(() => resources.Storage.WriteData(JsonSerializer.SerializeToUtf8Bytes(envelope, SerializerOptions)), cancellationToken).ConfigureAwait(false);
            var readBack = await Task.Run(resources.Storage.ReadData, cancellationToken).ConfigureAwait(false);
            var verified = JsonSerializer.Deserialize<SensitiveSettingsEnvelope>(readBack, SerializerOptions)
                ?? throw new SensitiveStorageException(SensitiveStorageErrorCode.PersistenceVerificationFailed, "System-protected storage read-back failed.");
            _ = SensitiveSettingsEnvelopeStore.FindRevision(verified, document.StoreId, document.Revision);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (SensitiveStorageException) { throw; }
        catch (Exception ex) { throw Classify(ex, "System-protected storage could not be written."); }
        finally { _gate.Release(); }
    }

    internal static SensitiveSettingsEnvelope MergeDocument(
        SensitiveSettingsEnvelope? envelope,
        SensitiveSettingsDocument document,
        long expectedRevision)
        => SensitiveSettingsEnvelopeStore.Merge(envelope, document, expectedRevision);

    internal static SensitiveSettingsEnvelope MergeDocument(
        SensitiveSettingsEnvelope? envelope,
        SensitiveSettingsDocument document)
    {
        var expectedRevision = 0L;
        if (envelope is not null)
        {
            try
            {
                expectedRevision = SensitiveSettingsEnvelopeStore
                    .FindRevision(envelope, document.StoreId, document.Revision - 1)
                    .Revision;
            }
            catch (SensitiveStorageException ex) when (ex.ErrorCode == SensitiveStorageErrorCode.RevisionNotFound)
            {
                // Compatibility helper for envelope-focused tests.
            }
        }

        return MergeDocument(envelope, document, expectedRevision);
    }

    internal static SensitiveSettingsDocument FindStoredRevision(
        SensitiveSettingsEnvelope envelope,
        string storeId,
        long revision)
        => SensitiveSettingsEnvelopeStore.FindRevision(envelope, storeId, revision);

    private StorageResources GetOrCreateResources()
    {
        lock (_initializationGate)
        {
            if (_resources is not null)
            {
                return _resources;
            }

            Directory.CreateDirectory(_dataDirectory);
            Directory.CreateDirectory(_lockDirectory);
            var resources = new StorageResources(
                Storage.Create(CreateStorageProperties(_dataDirectory, persistenceProbe: false, _testNamespace)),
                Storage.Create(CreateStorageProperties(_dataDirectory, persistenceProbe: true, _testNamespace)));
            _resources = resources;
            return resources;
        }
    }

    internal static SensitiveStorageException Classify(
        Exception exception,
        string message,
        bool? isLinux = null,
        bool classifyPayloadCorruption = true)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var chain = EnumerateExceptionChain(exception).ToArray();
        var classifiedCause = chain.FirstOrDefault(candidate => candidate is OperationCanceledException)
                              ?? chain.FirstOrDefault(candidate => candidate is UnauthorizedAccessException)
                              ?? chain.FirstOrDefault(candidate => candidate is JsonException or CryptographicException)
                              ?? exception;
        var detail = string.Join(Environment.NewLine, chain.Select(candidate => candidate.ToString()));

        if (chain.Any(candidate => candidate is OperationCanceledException)
            || ContainsAny(detail, "prompt was dismissed", "prompt dismissed", "user canceled", "user cancelled"))
        {
            return new(SensitiveStorageErrorCode.UserCanceled, message, classifiedCause);
        }

        if (chain.Any(candidate => candidate is UnauthorizedAccessException)
            || ContainsAny(detail, "org.freedesktop.DBus.Error.AccessDenied", "permission denied", "access denied"))
        {
            return new(SensitiveStorageErrorCode.PermissionDenied, message, classifiedCause);
        }

        // A locked collection is recoverable user/session state, never evidence that Secret Service is absent.
        if (ContainsAny(detail, "org.freedesktop.Secret.Error.IsLocked", "collection is locked", "secret service is locked"))
        {
            return new(SensitiveStorageErrorCode.Unavailable, message, exception);
        }

        if (classifyPayloadCorruption
            && chain.Any(candidate => candidate is JsonException or CryptographicException))
        {
            return new(SensitiveStorageErrorCode.Corrupt, message, classifiedCause);
        }

        var structuralLinuxFailure = (isLinux ?? OperatingSystem.IsLinux())
            && (chain.Any(candidate => candidate is DllNotFoundException)
                || ContainsAny(
                    detail,
                    "libsecret-1.so.0: cannot open shared object file",
                    "Unable to load shared library 'libsecret-1.so.0'",
                    "Cannot autolaunch D-Bus",
                    "Unable to autolaunch a dbus-daemon",
                    "DBUS_SESSION_BUS_ADDRESS is not set",
                    "org.freedesktop.DBus.Error.ServiceUnknown: The name org.freedesktop.secrets",
                    "org.freedesktop.DBus.Error.NameHasNoOwner: Could not get owner of name 'org.freedesktop.secrets'",
                    "The name org.freedesktop.secrets was not provided by any .service files"));
        return new(
            structuralLinuxFailure ? SensitiveStorageErrorCode.StructurallyUnavailable : SensitiveStorageErrorCode.Unavailable,
            message,
            exception);
    }

    private static IEnumerable<Exception> EnumerateExceptionChain(Exception exception)
    {
        var pending = new Stack<Exception>();
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push(exception);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!visited.Add(current))
            {
                continue;
            }

            yield return current;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.Reverse())
                {
                    pending.Push(inner);
                }
            }
            else if (current.InnerException is not null)
            {
                pending.Push(current.InnerException);
            }
        }
    }

    private static bool ContainsAny(string value, params string[] candidates)
        => candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    private static string ValidateTestNamespace(string testNamespace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(testNamespace);
        if (testNamespace.Length > 64
            || !IsAsciiLetter(testNamespace[0])
            || testNamespace.Any(character => !IsAsciiLetter(character) && !char.IsAsciiDigit(character)))
        {
            throw new ArgumentException(
                "The system storage test namespace must start with an ASCII letter and contain only ASCII letters or digits.",
                nameof(testNamespace));
        }

        return testNamespace;
    }

    private static bool IsAsciiLetter(char character)
        => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static string FormatTestLabelSuffix(string? testNamespace)
        => testNamespace is null ? string.Empty : $" test {testNamespace}";

    private sealed record StorageResources(Storage Storage, Storage PersistenceProbeStorage);
}
