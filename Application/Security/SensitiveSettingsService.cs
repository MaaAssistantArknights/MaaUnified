using System.Text.Json.Nodes;
using MAAUnified.Application.Models;

namespace MAAUnified.Application.Security;

public sealed class SensitiveSettingsService
{
    private readonly IReadOnlyDictionary<SensitiveStorageBackendKind, ISensitiveSettingsBackend> _backends;
    private readonly IPlaintextSensitiveStorageConsent _plaintextConsent;
    private readonly bool _isLinux;
    private SensitiveStorageMetadata? _selectedMetadata;
    private SensitiveSettingsDocument? _lastKnownDocument;
    private Dictionary<string, UnifiedProfile>? _selectedProfiles;
    private bool _canRetryInitialBackendSelection;

    public SensitiveSettingsService(
        IEnumerable<ISensitiveSettingsBackend> backends,
        IPlaintextSensitiveStorageConsent plaintextConsent,
        bool? isLinux = null)
    {
        ArgumentNullException.ThrowIfNull(backends);
        _plaintextConsent = plaintextConsent ?? throw new ArgumentNullException(nameof(plaintextConsent));
        _backends = backends.ToDictionary(backend => backend.Kind);
        _isLinux = isLinux ?? OperatingSystem.IsLinux();
    }

    public static SensitiveSettingsService CreateEphemeralForTests()
        => new([new EphemeralBackend()], new DeniedPlaintextConsent(), isLinux: false);

    public static SensitiveSettingsService CreatePersistentFileForTests(string runtimeBaseDirectory)
        => new(
            [new PlaintextSensitiveSettingsBackend(runtimeBaseDirectory)],
            new ApprovedPlaintextConsent(),
            isLinux: false);

    public SensitiveStorageState State { get; private set; } = SensitiveStorageState.Uninitialized;

    public SensitiveStorageErrorCode? LastErrorCode { get; private set; }

    public bool CanRetryInitialBackendSelection => _canRetryInitialBackendSelection;

    public event Action? StateChanged;

    public void RecordFailure(SensitiveStorageException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        SetState(
            exception.ErrorCode is SensitiveStorageErrorCode.Corrupt or SensitiveStorageErrorCode.InvalidDocument
                or SensitiveStorageErrorCode.RevisionNotFound
                ? SensitiveStorageState.Corrupt
                : SensitiveStorageState.Unavailable,
            exception.ErrorCode);
    }

    public void ConfirmDiscardUnreadableLegacySensitiveValues()
    {
        if (State != SensitiveStorageState.Unavailable || LastErrorCode != SensitiveStorageErrorCode.ActionRequired)
        {
            throw new InvalidOperationException("There are no unreadable legacy sensitive values awaiting confirmation.");
        }

        SetState(SensitiveStorageState.Uninitialized, null);
    }

    internal void BeginInitialBackendSelectionRetry(SensitiveStorageMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (State != SensitiveStorageState.Unavailable
            || metadata.Backend != SensitiveStorageBackendKind.Uninitialized
            || _selectedMetadata is not null
            || !_canRetryInitialBackendSelection)
        {
            throw new SensitiveStorageException(
                SensitiveStorageErrorCode.ActionRequired,
                "Only a failed initial sensitive storage selection can be retried with this operation.");
        }

        _canRetryInitialBackendSelection = false;
        SetState(SensitiveStorageState.Uninitialized, null);
    }

    public async Task<bool> LoadAsync(UnifiedConfig config, CancellationToken cancellationToken = default)
    {
        var metadata = config.SensitiveStorage;
        _selectedMetadata = metadata.Backend == SensitiveStorageBackendKind.Uninitialized
            ? null
            : CloneMetadata(metadata);
        _lastKnownDocument = null;
        _selectedProfiles = _selectedMetadata is null
            ? null
            : config.Profiles.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        _canRetryInitialBackendSelection = false;
        if (metadata.Backend == SensitiveStorageBackendKind.Uninitialized)
        {
            SensitiveSettingKeyRegistry.InjectDefaults(config);
            SetState(SensitiveStorageState.Uninitialized, null);
            return false;
        }

        try
        {
            ValidateMetadata(metadata);
            var backend = GetSelectedBackend(metadata.Backend);
            var document = await backend.ReadAsync(metadata.StoreId, metadata.Revision, cancellationToken).ConfigureAwait(false);
            ValidateExactMatch(metadata, document);
            SensitiveSettingKeyRegistry.RegisterExternalNotificationInstanceKeys(config);
            var plaintextDocument = SensitiveSettingKeyRegistry.ExtractAndRemove(
                config,
                metadata.StoreId,
                Math.Max(1, metadata.Revision));
            var removedPlaintextValues = SensitiveSettingKeyRegistry.HasValues(plaintextDocument);
            SensitiveSettingKeyRegistry.Inject(config, document);
            _lastKnownDocument = CloneDocument(document);
            SetBackendAvailable(metadata.Backend);
            return removedPlaintextValues;
        }
        catch (SensitiveStorageException ex)
        {
            RecordFailure(ex);
            return false;
        }
    }

    public async Task<SensitiveSettingsDocument?> PrepareSaveAsync(
        UnifiedConfig configToPersist,
        CancellationToken cancellationToken = default)
        => await PrepareSaveAsync(configToPersist, pendingCommit: null, cancellationToken).ConfigureAwait(false);

    internal async Task<SensitiveSettingsDocument?> PrepareSaveAsync(
        UnifiedConfig configToPersist,
        SensitiveSettingsPendingCommit? pendingCommit,
        CancellationToken cancellationToken = default)
    {
        var initialBackendSelectionAttempt = configToPersist.SensitiveStorage.Backend == SensitiveStorageBackendKind.Uninitialized
                                             && _selectedMetadata is null;
        try
        {
            var prepared = await PrepareSaveCoreAsync(configToPersist, pendingCommit, cancellationToken).ConfigureAwait(false);
            _canRetryInitialBackendSelection = false;
            return prepared;
        }
        catch (SensitiveStorageException ex)
        {
            _canRetryInitialBackendSelection = initialBackendSelectionAttempt
                                                && (IsRetriableInitialSelectionFailure(ex.ErrorCode)
                                                    || (_canRetryInitialBackendSelection
                                                        && ex.ErrorCode == SensitiveStorageErrorCode.ActionRequired));
            RecordFailure(ex);
            throw;
        }
    }

    private async Task<SensitiveSettingsDocument?> PrepareSaveCoreAsync(
        UnifiedConfig configToPersist,
        SensitiveSettingsPendingCommit? pendingCommit,
        CancellationToken cancellationToken)
    {
        var metadata = configToPersist.SensitiveStorage;
        if (SensitiveSettingKeyRegistry.HasMisplacedProfileValues(configToPersist)
            && !configToPersist.Profiles.ContainsKey(configToPersist.CurrentProfile))
        {
            throw new SensitiveStorageException(
                SensitiveStorageErrorCode.ActionRequired,
                "Profile-scoped sensitive settings cannot be migrated because the current profile is missing.");
        }

        var nextRevision = metadata.Revision + 1;
        var candidateStoreId = string.IsNullOrWhiteSpace(metadata.StoreId) ? Guid.NewGuid().ToString("D") : metadata.StoreId;
        var document = SensitiveSettingKeyRegistry.ExtractAndRemove(configToPersist, candidateStoreId, nextRevision);

        if (State is SensitiveStorageState.Unavailable or SensitiveStorageState.Corrupt)
        {
            if (CanPersistRedactedSnapshot(metadata, document, configToPersist.Profiles.Keys))
            {
                return null;
            }

            throw new SensitiveStorageException(
                SensitiveStorageErrorCode.ActionRequired,
                "Sensitive settings cannot be saved while sensitive storage requires user action.");
        }

        if (metadata.Backend == SensitiveStorageBackendKind.Uninitialized)
        {
            if (!SensitiveSettingKeyRegistry.HasNonDefaultValues(document))
            {
                return null;
            }

            metadata.Backend = await SelectInitialBackendAsync(cancellationToken).ConfigureAwait(false);
            metadata.StoreId = candidateStoreId;
        }

        ValidateMetadataForWrite(metadata);
        var backend = GetSelectedBackend(metadata.Backend);
        var writeCompleted = false;
        if (metadata.Revision > 0)
        {
            var configured = await backend.ReadAsync(metadata.StoreId, metadata.Revision, cancellationToken).ConfigureAwait(false);
            ValidateExactMatch(metadata, configured);
            var backendCurrent = await backend.ReadCurrentAsync(document.StoreId, cancellationToken).ConfigureAwait(false);
            SensitiveSettingKeyRegistry.ValidateDocument(backendCurrent);
            if (!string.Equals(backendCurrent.StoreId, metadata.StoreId, StringComparison.Ordinal))
            {
                throw ConcurrentSensitiveSettingsChange();
            }

            if (backendCurrent.Revision == metadata.Revision)
            {
                if (!DocumentsMatchExactly(configured, backendCurrent))
                {
                    throw ConcurrentSensitiveSettingsChange();
                }

                if (DocumentsHaveSameValues(configured, document))
                {
                    RememberSelectedDocument(metadata, configured);
                    return null;
                }
            }
            else if (backendCurrent.Revision == document.Revision)
            {
                if (DocumentsHaveSameValues(document, backendCurrent))
                {
                    metadata.FormatVersion = SensitiveStorageMetadata.LatestFormatVersion;
                    metadata.Revision = backendCurrent.Revision;
                    RememberSelectedDocument(metadata, backendCurrent);
                    SetBackendAvailable(metadata.Backend);
                    return backendCurrent;
                }

                if (PendingCommitMatches(pendingCommit, metadata, backendCurrent))
                {
                    // Only a path-bound pending record proves that R+1 belongs to this config.
                    AssignCommitId(document);
                    await backend.ReplaceCurrentAsync(
                        document,
                        backendCurrent,
                        configured,
                        cancellationToken).ConfigureAwait(false);
                    writeCompleted = true;
                }
                else
                {
                    // A copied config can legitimately share R with another portable install.
                    // Preserve that install's R+1 and fork this config into an independent store.
                    document.StoreId = Guid.NewGuid().ToString("D");
                    document.Revision = 1;
                    AssignCommitId(document);
                    await backend.WriteAsync(document, expectedRevision: 0, cancellationToken).ConfigureAwait(false);
                    writeCompleted = true;
                }
            }
            else
            {
                throw ConcurrentSensitiveSettingsChange();
            }
        }

        if (!writeCompleted)
        {
            AssignCommitId(document);
            var expectedRevision = document.Revision - 1;
            await backend.WriteAsync(document, expectedRevision, cancellationToken).ConfigureAwait(false);
        }
        var verified = await backend.ReadAsync(document.StoreId, document.Revision, cancellationToken).ConfigureAwait(false);
        ValidateReadBack(document, verified);
        metadata.FormatVersion = SensitiveStorageMetadata.LatestFormatVersion;
        metadata.StoreId = document.StoreId;
        metadata.Revision = document.Revision;
        RememberSelectedDocument(metadata, document);
        SetBackendAvailable(metadata.Backend);
        return document;
    }

    private static SensitiveStorageException ConcurrentSensitiveSettingsChange()
        => new(
            SensitiveStorageErrorCode.ActionRequired,
            "Sensitive settings changed concurrently. Reload before saving again.");

    private static bool IsRetriableInitialSelectionFailure(SensitiveStorageErrorCode errorCode)
        => errorCode is SensitiveStorageErrorCode.StructurallyUnavailable
            or SensitiveStorageErrorCode.Unavailable
            or SensitiveStorageErrorCode.PermissionDenied
            or SensitiveStorageErrorCode.UserCanceled
            or SensitiveStorageErrorCode.PersistenceVerificationFailed;

    private static bool PendingCommitMatches(
        SensitiveSettingsPendingCommit? pendingCommit,
        SensitiveStorageMetadata metadata,
        SensitiveSettingsDocument backendCurrent)
        => pendingCommit is not null
           && pendingCommit.FormatVersion == SensitiveSettingsPendingCommit.LatestFormatVersion
           && pendingCommit.Backend == metadata.Backend
           && string.Equals(pendingCommit.BaseStoreId, metadata.StoreId, StringComparison.Ordinal)
           && pendingCommit.BaseRevision == metadata.Revision
           && string.Equals(pendingCommit.TargetStoreId, backendCurrent.StoreId, StringComparison.Ordinal)
           && pendingCommit.TargetRevision == backendCurrent.Revision
           && !string.IsNullOrWhiteSpace(pendingCommit.TargetCommitId)
           && string.Equals(pendingCommit.TargetCommitId, backendCurrent.CommitId, StringComparison.Ordinal);

    private static void AssignCommitId(SensitiveSettingsDocument document)
        => document.CommitId = Guid.NewGuid().ToString("D");

    private bool CanPersistRedactedSnapshot(
        SensitiveStorageMetadata metadata,
        SensitiveSettingsDocument candidate,
        IEnumerable<string> candidateProfileNames)
    {
        if (_selectedMetadata is null
            || metadata.Backend == SensitiveStorageBackendKind.Uninitialized
            || !MetadataMatches(metadata, _selectedMetadata))
        {
            return false;
        }

        return _lastKnownDocument is null
            ? !SensitiveSettingKeyRegistry.HasValues(candidate)
              && _selectedProfiles is not null
              && _selectedProfiles.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(candidateProfileNames)
            : DocumentsHaveSameValues(_lastKnownDocument, candidate);
    }

    internal void ValidateRedactedSaveSource(UnifiedConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (State is not (SensitiveStorageState.Unavailable or SensitiveStorageState.Corrupt)
            || _lastKnownDocument is not null
            || _selectedMetadata is null
            || !MetadataMatches(config.SensitiveStorage, _selectedMetadata)
            || ProfileInstancesMatch(config.Profiles))
        {
            return;
        }

        throw new SensitiveStorageException(
            SensitiveStorageErrorCode.ActionRequired,
            "Configuration profiles cannot be replaced while the selected sensitive storage document is unavailable.");
    }

    private bool ProfileInstancesMatch(IReadOnlyDictionary<string, UnifiedProfile> candidateProfiles)
    {
        if (_selectedProfiles is null || _selectedProfiles.Count != candidateProfiles.Count)
        {
            return false;
        }

        foreach (var (name, profile) in _selectedProfiles)
        {
            if (!candidateProfiles.TryGetValue(name, out var candidate)
                || !candidateProfiles.Keys.Contains(name, StringComparer.Ordinal)
                || !ReferenceEquals(profile, candidate))
            {
                return false;
            }
        }

        return true;
    }

    private void RememberSelectedDocument(
        SensitiveStorageMetadata metadata,
        SensitiveSettingsDocument document)
    {
        _selectedMetadata = CloneMetadata(metadata);
        _lastKnownDocument = CloneDocument(document);
    }

    public async Task PrepareSecureStorageMigrationAsync(
        UnifiedConfig configToPersist,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await PrepareSecureStorageMigrationCoreAsync(configToPersist, cancellationToken).ConfigureAwait(false);
        }
        catch (SensitiveStorageException ex)
        {
            SetState(
                configToPersist.SensitiveStorage.Backend == SensitiveStorageBackendKind.Plaintext
                    ? SensitiveStorageState.PlaintextWarning
                    : State,
                ex.ErrorCode);
            throw;
        }
    }

    private async Task PrepareSecureStorageMigrationCoreAsync(
        UnifiedConfig configToPersist,
        CancellationToken cancellationToken)
    {
        var metadata = configToPersist.SensitiveStorage;
        if (metadata.Backend != SensitiveStorageBackendKind.Plaintext)
        {
            throw new SensitiveStorageException(SensitiveStorageErrorCode.ActionRequired, "Only plaintext sensitive storage can be migrated with this operation.");
        }

        var backend = await SelectSecureRetryBackendAsync(cancellationToken).ConfigureAwait(false);
        var sourceBackend = GetSelectedBackend(SensitiveStorageBackendKind.Plaintext);
        var sourceCurrent = await sourceBackend.ReadCurrentAsync(metadata.StoreId, cancellationToken).ConfigureAwait(false);
        SensitiveSettingKeyRegistry.ValidateDocument(sourceCurrent);
        if (!string.Equals(sourceCurrent.StoreId, metadata.StoreId, StringComparison.Ordinal)
            || sourceCurrent.Revision != metadata.Revision)
        {
            throw new SensitiveStorageException(
                SensitiveStorageErrorCode.ActionRequired,
                "Plaintext sensitive settings changed concurrently. Reload before retrying migration.");
        }

        var document = SensitiveSettingKeyRegistry.ExtractAndRemove(
            configToPersist,
            metadata.StoreId,
            metadata.Revision + 1);
        try
        {
            var backendCurrent = await backend.ReadCurrentAsync(document.StoreId, cancellationToken).ConfigureAwait(false);
            if (backendCurrent.Revision == document.Revision)
            {
                if (DocumentsMatchExactly(document, backendCurrent))
                {
                    metadata.Backend = backend.Kind;
                    metadata.Revision = document.Revision;
                    RememberSelectedDocument(metadata, document);
                    return;
                }

                throw new SensitiveStorageException(
                    SensitiveStorageErrorCode.ActionRequired,
                    "Sensitive settings migration changed concurrently. Reload before retrying migration.");
            }

            if (backendCurrent.Revision == metadata.Revision
                && await IsPreviousPlaintextRevisionAsync(sourceBackend, metadata, backendCurrent, cancellationToken).ConfigureAwait(false))
            {
                await backend.WriteAsync(document, metadata.Revision, cancellationToken).ConfigureAwait(false);
            }
            else if (backendCurrent.Revision < metadata.Revision)
            {
                document.StoreId = Guid.NewGuid().ToString("D");
                document.Revision = 1;
                await backend.WriteAsync(document, expectedRevision: 0, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                throw new SensitiveStorageException(
                    SensitiveStorageErrorCode.ActionRequired,
                    "Sensitive settings migration changed concurrently. Reload before retrying migration.");
            }
        }
        catch (SensitiveStorageException ex) when (ex.ErrorCode == SensitiveStorageErrorCode.RevisionNotFound)
        {
            await backend.WriteAsync(document, expectedRevision: 0, cancellationToken).ConfigureAwait(false);
        }

        var verified = await backend.ReadAsync(document.StoreId, document.Revision, cancellationToken).ConfigureAwait(false);
        ValidateReadBack(document, verified);
        metadata.Backend = backend.Kind;
        metadata.StoreId = document.StoreId;
        metadata.Revision = document.Revision;
        RememberSelectedDocument(metadata, document);
    }

    private static async Task<bool> IsPreviousPlaintextRevisionAsync(
        ISensitiveSettingsBackend plaintextBackend,
        SensitiveStorageMetadata metadata,
        SensitiveSettingsDocument targetCurrent,
        CancellationToken cancellationToken)
    {
        if (metadata.Revision <= 1)
        {
            return false;
        }

        try
        {
            var previous = await plaintextBackend
                .ReadAsync(metadata.StoreId, metadata.Revision - 1, cancellationToken)
                .ConfigureAwait(false);
            return previous.Revision == metadata.Revision - 1
                   && string.Equals(previous.StoreId, metadata.StoreId, StringComparison.Ordinal)
                   && DocumentsHaveSameValues(previous, targetCurrent);
        }
        catch (SensitiveStorageException ex) when (ex.ErrorCode == SensitiveStorageErrorCode.RevisionNotFound)
        {
            return false;
        }
    }

    public void MarkBackendActive(SensitiveStorageBackendKind backend)
    {
        SetBackendAvailable(backend);
    }

    public Task DeletePlaintextStoreAsync(CancellationToken cancellationToken = default)
        => _backends.TryGetValue(SensitiveStorageBackendKind.Plaintext, out var plaintext)
           && plaintext is IDeletableSensitiveSettingsBackend deletable
            ? deletable.DeleteAsync(cancellationToken)
            : Task.CompletedTask;

    private async Task<ISensitiveSettingsBackend> SelectSecureRetryBackendAsync(CancellationToken cancellationToken)
    {
        if (_backends.TryGetValue(SensitiveStorageBackendKind.SystemProtected, out var system))
        {
            try
            {
                await system.VerifyPersistenceAsync(cancellationToken).ConfigureAwait(false);
                return system;
            }
            catch (SensitiveStorageException ex) when (
                _isLinux && ex.ErrorCode == SensitiveStorageErrorCode.StructurallyUnavailable)
            {
                // A structural Linux Secret Service failure permits an explicit retry through SQLCipher.
            }
        }

        if (_isLinux && _backends.TryGetValue(SensitiveStorageBackendKind.LinuxLocalSqlCipher, out var sqlCipher))
        {
            await sqlCipher.VerifyPersistenceAsync(cancellationToken).ConfigureAwait(false);
            return sqlCipher;
        }

        throw new SensitiveStorageException(SensitiveStorageErrorCode.Unavailable, "No secure sensitive storage backend is available.");
    }

    private async Task<SensitiveStorageBackendKind> SelectInitialBackendAsync(CancellationToken cancellationToken)
    {
        if (_backends.TryGetValue(SensitiveStorageBackendKind.SystemProtected, out var system))
        {
            try
            {
                await system.VerifyPersistenceAsync(cancellationToken).ConfigureAwait(false);
                return SensitiveStorageBackendKind.SystemProtected;
            }
            catch (SensitiveStorageException ex) when (
                _isLinux && ex.ErrorCode == SensitiveStorageErrorCode.StructurallyUnavailable)
            {
                // Only a structural Linux Secret Service failure permits trying SQLCipher.
            }
        }

        if (_isLinux && _backends.TryGetValue(SensitiveStorageBackendKind.LinuxLocalSqlCipher, out var sqlCipher))
        {
            try
            {
                await sqlCipher.VerifyPersistenceAsync(cancellationToken).ConfigureAwait(false);
                return SensitiveStorageBackendKind.LinuxLocalSqlCipher;
            }
            catch (SensitiveStorageException ex) when (
                ex.ErrorCode == SensitiveStorageErrorCode.StructurallyUnavailable)
            {
                // A structurally missing SQLCipher provider permits explicit plaintext consent.
            }
        }

        if (_backends.TryGetValue(SensitiveStorageBackendKind.Plaintext, out var plaintext)
            && await _plaintextConsent.ConfirmAsync(cancellationToken).ConfigureAwait(false))
        {
            await plaintext.VerifyPersistenceAsync(cancellationToken).ConfigureAwait(false);
            return SensitiveStorageBackendKind.Plaintext;
        }

        throw new SensitiveStorageException(SensitiveStorageErrorCode.ActionRequired, "Secure storage is unavailable and plaintext storage was not approved.");
    }

    private ISensitiveSettingsBackend GetSelectedBackend(SensitiveStorageBackendKind kind)
    {
        if (!_backends.TryGetValue(kind, out var backend))
        {
            throw new SensitiveStorageException(SensitiveStorageErrorCode.Unavailable, $"The selected sensitive storage backend '{kind}' is unavailable.");
        }

        return backend;
    }

    private void SetBackendAvailable(SensitiveStorageBackendKind backend)
    {
        _canRetryInitialBackendSelection = false;
        SetState(
            backend == SensitiveStorageBackendKind.Plaintext
                ? SensitiveStorageState.PlaintextWarning
                : SensitiveStorageState.Available,
            null);
    }

    private void SetState(SensitiveStorageState state, SensitiveStorageErrorCode? errorCode)
    {
        if (State == state && LastErrorCode == errorCode)
        {
            return;
        }

        State = state;
        LastErrorCode = errorCode;
        StateChanged?.Invoke();
    }

    private static void ValidateMetadata(SensitiveStorageMetadata metadata)
    {
        ValidateMetadataForWrite(metadata);
        if (metadata.Revision <= 0)
        {
            throw new SensitiveStorageException(SensitiveStorageErrorCode.InvalidDocument, "Sensitive storage revision is invalid.");
        }
    }

    private static void ValidateMetadataForWrite(SensitiveStorageMetadata metadata)
    {
        if (metadata.FormatVersion != SensitiveStorageMetadata.LatestFormatVersion
            || metadata.Backend == SensitiveStorageBackendKind.Uninitialized
            || string.IsNullOrWhiteSpace(metadata.StoreId))
        {
            throw new SensitiveStorageException(SensitiveStorageErrorCode.InvalidDocument, "Sensitive storage metadata is invalid.");
        }
    }

    private static void ValidateExactMatch(SensitiveStorageMetadata metadata, SensitiveSettingsDocument document)
    {
        SensitiveSettingKeyRegistry.ValidateDocument(document);
        if (!string.Equals(metadata.StoreId, document.StoreId, StringComparison.Ordinal)
            || metadata.Revision != document.Revision)
        {
            throw new SensitiveStorageException(SensitiveStorageErrorCode.InvalidDocument, "Sensitive storage document does not match the configured store and revision.");
        }
    }

    private static void ValidateReadBack(SensitiveSettingsDocument expected, SensitiveSettingsDocument actual)
    {
        SensitiveSettingKeyRegistry.ValidateDocument(expected);
        SensitiveSettingKeyRegistry.ValidateDocument(actual);
        if (!DocumentsMatchExactly(expected, actual))
        {
            throw new SensitiveStorageException(
                SensitiveStorageErrorCode.PersistenceVerificationFailed,
                "Sensitive storage read-back did not match the document that was written.");
        }
    }

    private static bool MetadataMatches(SensitiveStorageMetadata left, SensitiveStorageMetadata right)
        => left.FormatVersion == right.FormatVersion
           && left.Backend == right.Backend
           && string.Equals(left.StoreId, right.StoreId, StringComparison.Ordinal)
           && left.Revision == right.Revision;

    private static SensitiveStorageMetadata CloneMetadata(SensitiveStorageMetadata metadata)
        => new()
        {
            FormatVersion = metadata.FormatVersion,
            Backend = metadata.Backend,
            StoreId = metadata.StoreId,
            Revision = metadata.Revision,
        };

    private static SensitiveSettingsDocument CloneDocument(SensitiveSettingsDocument document)
        => new()
        {
            FormatVersion = document.FormatVersion,
            StoreId = document.StoreId,
            Revision = document.Revision,
            CommitId = document.CommitId,
            GlobalValues = document.GlobalValues.ToDictionary(
                pair => pair.Key,
                pair => pair.Value?.DeepClone(),
                StringComparer.OrdinalIgnoreCase),
            Profiles = document.Profiles.ToDictionary(
                profile => profile.Key,
                profile => profile.Value.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value?.DeepClone(),
                    StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase),
        };

    private static bool DocumentsHaveSameValues(SensitiveSettingsDocument left, SensitiveSettingsDocument right)
        => NodeDictionariesEqual(left.GlobalValues, right.GlobalValues)
           && ProfileDictionariesEqual(left.Profiles, right.Profiles);

    private static bool DocumentsMatchExactly(SensitiveSettingsDocument left, SensitiveSettingsDocument right)
        => left.FormatVersion == right.FormatVersion
           && string.Equals(left.StoreId, right.StoreId, StringComparison.Ordinal)
           && left.Revision == right.Revision
           && string.Equals(left.CommitId, right.CommitId, StringComparison.Ordinal)
           && DocumentsHaveSameValues(left, right);

    private static bool ProfileDictionariesEqual(
        IReadOnlyDictionary<string, Dictionary<string, JsonNode?>> left,
        IReadOnlyDictionary<string, Dictionary<string, JsonNode?>> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var (profileName, values) in left)
        {
            if (!TryGetValue(right, profileName, out var otherValues)
                || !NodeDictionariesEqual(values, otherValues))
            {
                return false;
            }
        }

        return true;
    }

    private static bool NodeDictionariesEqual(
        IReadOnlyDictionary<string, JsonNode?> left,
        IReadOnlyDictionary<string, JsonNode?> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var (key, value) in left)
        {
            if (!TryGetValue(right, key, out var otherValue) || !JsonNode.DeepEquals(value, otherValue))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryGetValue<T>(IReadOnlyDictionary<string, T> source, string key, out T value)
    {
        foreach (var pair in source)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = default!;
        return false;
    }

    private sealed class DeniedPlaintextConsent : IPlaintextSensitiveStorageConsent
    {
        public Task<bool> ConfirmAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class ApprovedPlaintextConsent : IPlaintextSensitiveStorageConsent
    {
        public Task<bool> ConfirmAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class EphemeralBackend : ISensitiveSettingsBackend
    {
        private readonly Dictionary<(string StoreId, long Revision), SensitiveSettingsDocument> _documents = [];

        public SensitiveStorageBackendKind Kind => SensitiveStorageBackendKind.SystemProtected;

        public Task VerifyPersistenceAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<SensitiveSettingsDocument> ReadAsync(string storeId, long revision, CancellationToken cancellationToken = default)
        {
            if (_documents.TryGetValue((storeId, revision), out var document))
            {
                return Task.FromResult(document);
            }

            return Task.FromException<SensitiveSettingsDocument>(
                new SensitiveStorageException(SensitiveStorageErrorCode.RevisionNotFound, "Ephemeral test storage does not contain the requested revision."));
        }

        public Task<SensitiveSettingsDocument> ReadCurrentAsync(
            string storeId,
            CancellationToken cancellationToken = default)
        {
            var current = _documents
                .Where(pair => string.Equals(pair.Key.StoreId, storeId, StringComparison.Ordinal))
                .OrderByDescending(pair => pair.Key.Revision)
                .Select(pair => pair.Value)
                .FirstOrDefault();
            return current is not null
                ? Task.FromResult(current)
                : Task.FromException<SensitiveSettingsDocument>(new SensitiveStorageException(
                    SensitiveStorageErrorCode.RevisionNotFound,
                    "Ephemeral test storage does not contain the requested store."));
        }

        public Task WriteAsync(
            SensitiveSettingsDocument document,
            long expectedRevision,
            CancellationToken cancellationToken = default)
        {
            var currentRevision = _documents.Keys
                .Where(key => string.Equals(key.StoreId, document.StoreId, StringComparison.Ordinal))
                .Select(key => key.Revision)
                .DefaultIfEmpty(0)
                .Max();
            if (currentRevision != expectedRevision)
            {
                return Task.FromException(new SensitiveStorageException(
                    SensitiveStorageErrorCode.ActionRequired,
                    "Ephemeral sensitive settings changed concurrently."));
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
            var current = _documents
                .Where(pair => string.Equals(pair.Key.StoreId, document.StoreId, StringComparison.Ordinal))
                .OrderByDescending(pair => pair.Key.Revision)
                .Select(pair => pair.Value)
                .FirstOrDefault();
            if (current is null
                || !_documents.TryGetValue((document.StoreId, expectedPrevious.Revision), out var previous)
                || !DocumentsMatchExactly(current, expectedCurrent)
                || !DocumentsMatchExactly(previous, expectedPrevious))
            {
                return Task.FromException(ConcurrentSensitiveSettingsChange());
            }

            _documents[(document.StoreId, document.Revision)] = document;
            return Task.CompletedTask;
        }
    }
}

public enum SensitiveStorageState
{
    Uninitialized = 0,
    Available = 1,
    PlaintextWarning = 2,
    Unavailable = 3,
    Corrupt = 4,
}
