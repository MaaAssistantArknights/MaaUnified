using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Models.TaskParams;
using MAAUnified.Application.Security;
using MAAUnified.Application.Services.TaskParams;
using MAAUnified.Compat.Constants;

namespace MAAUnified.Application.Services;

public enum ConfigValidationMode
{
    Minimal = 0,
    Full = 1,
}

public sealed class UnifiedConfigurationService
{
    private static readonly JsonSerializerOptions _reportOptions = new()
    {
        WriteIndented = true,
    };
    private static readonly JsonSerializerOptions _configSnapshotOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };
    private const string GuiNewFileName = "gui.new.json";
    private const string GuiFileName = "gui.json";
    private const string ParseNullWarningCode = "ConfigRepair.DeserializeNull";
    private const string ParseExceptionWarningCode = "ConfigRepair.DeserializeException";
    private const string SensitivePendingCommitFileName = ".sensitive-settings.pending.json";
    private const string ConfigBackupFilePrefix = "avalonia.json.bak.";
    private const string SchemaConfigBackupFilePrefix = "avalonia.json.schema-v";
    private const string ConfigBackupTimestampFormat = "yyyyMMddHHmmss";
    private const int ConfigBackupTimestampLength = 14;
    private const int ConfigBackupCollisionIdLength = 32;

    private readonly IUnifiedConfigStore _store;
    private readonly IConfigImporter _guiNewImporter;
    private readonly IConfigImporter _guiImporter;
    private readonly string _baseDirectory;
    private readonly SensitiveSettingsService _sensitiveSettingsService;
    private readonly SemaphoreSlim _saveSemaphore = new(1, 1);
    private List<ConfigValidationIssue> _currentValidationIssues = [];
    private string? _lastPersistedConfigSnapshot;
    private IReadOnlyList<ImportUnreadableConfigValue> _pendingUnreadableLegacySensitiveValues = [];

    public UnifiedConfigurationService(
        IUnifiedConfigStore store,
        IConfigImporter guiNewImporter,
        IConfigImporter guiImporter,
        UiLogService logService,
        string baseDirectory,
        SensitiveSettingsService? sensitiveSettingsService = null)
    {
        _store = store;
        _guiNewImporter = guiNewImporter;
        _guiImporter = guiImporter;
        _baseDirectory = baseDirectory;
        _sensitiveSettingsService = sensitiveSettingsService
            ?? SensitiveSettingsService.CreatePersistentFileForTests(baseDirectory);
        _sensitiveSettingsService.StateChanged += () => SensitiveStorageStateChanged?.Invoke();
        LogService = logService;
    }

    public UiLogService LogService { get; }

    public UnifiedConfig CurrentConfig { get; private set; } = new();

    public IReadOnlyList<ConfigValidationIssue> CurrentValidationIssues => _currentValidationIssues;

    public bool HasBlockingValidationIssues => _currentValidationIssues.Any(i => i.Blocking);

    public SensitiveStorageState SensitiveStorageState => _sensitiveSettingsService.State;

    public SensitiveStorageErrorCode? SensitiveStorageErrorCode => _sensitiveSettingsService.LastErrorCode;

    public bool CanRetryInitialSensitiveStorageSelection => _sensitiveSettingsService.CanRetryInitialBackendSelection;

    public bool HasUnreadableLegacySensitiveValuesPending => _pendingUnreadableLegacySensitiveValues.Count > 0;

    public event Action<UnifiedConfig>? ConfigChanged;

    public event Action? SensitiveStorageStateChanged;

    public async Task ConfirmDiscardUnreadableLegacySensitiveValuesAsync(CancellationToken cancellationToken = default)
    {
        await _saveSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (_pendingUnreadableLegacySensitiveValues.Count == 0)
            {
                throw new InvalidOperationException("There are no unreadable legacy sensitive values awaiting confirmation.");
            }

            _sensitiveSettingsService.ConfirmDiscardUnreadableLegacySensitiveValues();
            _pendingUnreadableLegacySensitiveValues = [];
            LogService.Warn("The user confirmed discarding unreadable legacy sensitive values; re-entry is now enabled.");
            RaiseConfigChanged();
        }
        finally
        {
            _saveSemaphore.Release();
        }
    }

    public IReadOnlyList<ConfigValidationIssue> RevalidateCurrentConfig(bool logIssues = false)
    {
        return RevalidateCurrentConfig(ConfigValidationMode.Full, logIssues);
    }

    public IReadOnlyList<ConfigValidationIssue> RevalidateCurrentConfig(
        ConfigValidationMode validationMode,
        bool logIssues = false)
    {
        return RefreshValidationState(validationMode, logIssues);
    }

    public bool TryGetCurrentProfile(out UnifiedProfile profile)
    {
        return CurrentConfig.Profiles.TryGetValue(CurrentConfig.CurrentProfile, out profile!);
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await SaveCoreAsync(CurrentConfig, logSavedConfig: true, cancellationToken);
    }

    public async Task SaveReplacementAsync(
        UnifiedConfig replacement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        await SaveCoreAsync(replacement, logSavedConfig: true, cancellationToken);
    }

    public async Task RetrySecureStorageAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentConfig.SensitiveStorage.Backend == SensitiveStorageBackendKind.Uninitialized)
        {
            await SaveCoreAsync(
                CurrentConfig,
                logSavedConfig: true,
                cancellationToken,
                allowInitialBackendSelectionRetry: true);
            return;
        }

        await _saveSemaphore.WaitAsync(cancellationToken);
        try
        {
            await using var commitLock = await _store.AcquireCommitLockAsync(cancellationToken);
            await EnsurePersistedPlaintextMetadataIsCurrentAsync(cancellationToken);
            var configToPersist = CreatePersistentConfigSnapshot(CurrentConfig);
            await _sensitiveSettingsService.PrepareSecureStorageMigrationAsync(configToPersist, cancellationToken);
            await EnsurePersistedPlaintextMetadataIsCurrentAsync(cancellationToken);
            await _store.SaveAsync(configToPersist, cancellationToken);
            CurrentConfig.SensitiveStorage = CloneSensitiveStorageMetadata(configToPersist.SensitiveStorage);
            _lastPersistedConfigSnapshot = SerializeConfigSnapshot(configToPersist);
            _sensitiveSettingsService.MarkBackendActive(configToPersist.SensitiveStorage.Backend);
            try
            {
                await _sensitiveSettingsService.DeletePlaintextStoreAsync(CancellationToken.None);
            }
            catch (SensitiveStorageException ex)
            {
                LogService.Warn($"Obsolete plaintext sensitive storage cleanup failed: errorCode={ex.ErrorCode}.");
            }

            RaiseConfigChanged();
            LogService.Info(
                $"Sensitive storage migration completed: backend={configToPersist.SensitiveStorage.Backend}, revision={configToPersist.SensitiveStorage.Revision}.");
        }
        finally
        {
            _saveSemaphore.Release();
        }
    }

    private async Task EnsurePersistedPlaintextMetadataIsCurrentAsync(CancellationToken cancellationToken)
    {
        if (!_store.Exists())
        {
            return;
        }

        var persisted = await _store.LoadAsync(cancellationToken);
        var expected = CurrentConfig.SensitiveStorage;
        var actual = persisted?.SensitiveStorage;
        if (expected.Backend != SensitiveStorageBackendKind.Plaintext
            || actual is null
            || actual.Backend != SensitiveStorageBackendKind.Plaintext
            || !string.Equals(actual.StoreId, expected.StoreId, StringComparison.Ordinal)
            || actual.Revision != expected.Revision)
        {
            throw new SensitiveStorageException(
                MAAUnified.Application.Security.SensitiveStorageErrorCode.ActionRequired,
                "Plaintext sensitive settings metadata changed concurrently. Reload before retrying migration.");
        }
    }

    public Task<ConfigLoadResult> LoadOrBootstrapAsync(CancellationToken cancellationToken = default)
        => LoadOrBootstrapAsync(ConfigValidationMode.Full, cancellationToken);

    public async Task<ConfigLoadResult> LoadOrBootstrapAsync(
        ConfigValidationMode validationMode,
        CancellationToken cancellationToken = default)
    {
        if (_store.Exists())
        {
            try
            {
                var loaded = await _store.LoadAsync(cancellationToken);
                if (loaded is not null)
                {
                    CurrentConfig = loaded;
                    var repairedNullEntries = CurrentConfig.HasPersistedNullEntries;
                    // Repairing the selection before checking for misplaced profile-scoped sensitive
                    // values would silently migrate those secrets into the fallback profile. Keep the
                    // blocking ActionRequired behavior for that case and leave the file authoritative.
                    var hasUnmigratableMisplacedValues =
                        !CurrentConfig.Profiles.ContainsKey(CurrentConfig.CurrentProfile)
                        && SensitiveSettingKeyRegistry.HasMisplacedProfileValues(CurrentConfig);
                    (string Missing, string Fallback)? repairedProfileSelection = null;
                    if (!hasUnmigratableMisplacedValues)
                    {
                        repairedProfileSelection = RepairMissingCurrentProfileSelection(CurrentConfig);
                        if (repairedProfileSelection is not null)
                        {
                            LogService.Warn(
                                $"Current profile `{repairedProfileSelection.Value.Missing}` was not found in config/avalonia.json; selected `{repairedProfileSelection.Value.Fallback}` instead.");
                        }
                    }
                    var removedPersistedSensitiveValues = await _sensitiveSettingsService.LoadAsync(CurrentConfig, cancellationToken);
                    await RetryObsoletePlaintextCleanupAsync(cancellationToken);
                    if (_sensitiveSettingsService.State is SensitiveStorageState.Unavailable or SensitiveStorageState.Corrupt)
                    {
                        LogService.Warn(
                            $"Sensitive storage unavailable: backend={CurrentConfig.SensitiveStorage.Backend}, errorCode={_sensitiveSettingsService.LastErrorCode}, revision={CurrentConfig.SensitiveStorage.Revision}.");
                    }

                    var normalizedFightStageCount = NormalizeFightStageSelections(CurrentConfig);
                    var normalizedTaskParamCount = NormalizeTaskQueueParams(CurrentConfig);
                    var clearedRecruitExpeditedCount = ClearOneShotRecruitExpedited(CurrentConfig);
                    var legacyConversionReport = new ImportReport
                    {
                        Source = ImportSource.Auto,
                        StartedAt = DateTimeOffset.UtcNow,
                        OutputConfigPath = "config/avalonia.json",
                    };
                    var decryptedLegacySensitiveValueCount = CurrentConfig.SensitiveStorage.Backend == SensitiveStorageBackendKind.Uninitialized
                        ? ConvertLegacyEncryptedSensitiveValues(CurrentConfig, legacyConversionReport)
                        : 0;
                    var normalizedSensitiveScopeCount = SensitiveSettingKeyRegistry.NormalizeMisplacedProfileValues(CurrentConfig);
                    var hasUnreadableLegacySensitiveValues = legacyConversionReport.UnreadableValues.Count > 0;
                    _pendingUnreadableLegacySensitiveValues = hasUnreadableLegacySensitiveValues
                        ? legacyConversionReport.UnreadableValues.ToArray()
                        : [];
                    if (hasUnreadableLegacySensitiveValues || hasUnmigratableMisplacedValues)
                    {
                        _sensitiveSettingsService.RecordFailure(new SensitiveStorageException(
                            MAAUnified.Application.Security.SensitiveStorageErrorCode.ActionRequired,
                            hasUnreadableLegacySensitiveValues
                                ? "Legacy encrypted sensitive settings must be re-entered before configuration can be saved."
                                : "Profile-scoped sensitive settings cannot be migrated because the current profile is missing."));
                    }

                    var requiresSensitiveMigration = CurrentConfig.SensitiveStorage.Backend == SensitiveStorageBackendKind.Uninitialized
                                                     && HasLegacySensitiveValues(CurrentConfig);
                    var requiresSchemaMigration = CurrentConfig.SchemaVersion < UnifiedConfig.LatestSchemaVersion;
                    var normalizedConfigChanged = normalizedFightStageCount > 0
                        || normalizedTaskParamCount > 0
                        || clearedRecruitExpeditedCount > 0
                        || decryptedLegacySensitiveValueCount > 0
                        || normalizedSensitiveScopeCount > 0
                        || repairedNullEntries
                        || repairedProfileSelection is not null
                        || removedPersistedSensitiveValues
                        || requiresSensitiveMigration
                        || requiresSchemaMigration;
                    var persistedNormalizedConfig = false;
                    if (normalizedFightStageCount > 0)
                    {
                        LogService.Info(
                            $"Normalized {normalizedFightStageCount} legacy Fight stage selector(s) to `{FightStageSelection.CurrentOrLast}`.");
                    }

                    if (normalizedTaskParamCount > 0)
                    {
                        LogService.Info($"Normalized {normalizedTaskParamCount} task parameter set(s).");
                    }

                    if (repairedNullEntries)
                    {
                        LogService.Info("Removed null profile or task queue entries from config/avalonia.json.");
                    }

                    if (clearedRecruitExpeditedCount > 0)
                    {
                        LogService.Info(
                            $"Cleared {clearedRecruitExpeditedCount} one-shot Recruit expedited setting(s).");
                    }

                    if (decryptedLegacySensitiveValueCount > 0)
                    {
                        LogService.Info($"Converted {decryptedLegacySensitiveValueCount} legacy encrypted sensitive value(s) before secure storage migration.");
                    }

                    if (normalizedSensitiveScopeCount > 0)
                    {
                        LogService.Info($"Moved {normalizedSensitiveScopeCount} misplaced profile-scoped sensitive value(s) out of global configuration.");
                    }

                    if (normalizedConfigChanged && !hasUnreadableLegacySensitiveValues && !hasUnmigratableMisplacedValues)
                    {
                        try
                        {
                            await SaveCoreAsync(CurrentConfig, logSavedConfig: false, cancellationToken, validationMode);
                            persistedNormalizedConfig = true;
                            LogService.Info("Persisted normalized and redacted config values to config/avalonia.json");
                        }
                        catch (SensitiveStorageException ex)
                        {
                            _sensitiveSettingsService.RecordFailure(ex);
                            LogService.Warn(
                                $"Sensitive settings migration requires action: backend={CurrentConfig.SensitiveStorage.Backend}, errorCode={ex.ErrorCode}, revision={CurrentConfig.SensitiveStorage.Revision}.");
                        }
                        catch (Exception ex)
                        {
                            LogService.Warn($"Failed to persist normalized config values: errorType={ex.GetType().Name}.");
                        }
                    }
                    else if (normalizedConfigChanged)
                    {
                        LogService.Warn(
                            "Skipped automatic config persistence because sensitive settings require user action; the original config remains authoritative.");
                    }

                    if (clearedRecruitExpeditedCount > 0)
                    {
                        ClearOneShotRecruitExpedited(CurrentConfig);
                    }

                    if (!normalizedConfigChanged || persistedNormalizedConfig)
                    {
                        _lastPersistedConfigSnapshot = SerializeConfigSnapshot(CreateRedactedConfigSnapshot(CurrentConfig));
                    }
                    else
                    {
                        _lastPersistedConfigSnapshot = null;
                    }

                    var schemaMigrationNotice = BuildSchemaMigrationNotice();
                    if (schemaMigrationNotice is not null)
                    {
                        LogService.Warn(
                            $"config/avalonia.json schema is {CurrentConfig.SchemaVersion}, latest is {UnifiedConfig.LatestSchemaVersion}. " +
                            "No automatic migration is applied.");
                    }

                    var validationIssues = RefreshValidationState(validationMode, logIssues: true);
                    LogService.Info("Loaded config/avalonia.json and skipped legacy auto import");
                    RaiseConfigChanged();

                    legacyConversionReport.FinishedAt = DateTimeOffset.UtcNow;
                    legacyConversionReport.Success = !hasUnreadableLegacySensitiveValues;
                    legacyConversionReport.AppliedConfig = !hasUnreadableLegacySensitiveValues;
                    return new ConfigLoadResult {
                        Config = CurrentConfig,
                        LoadedFromExistingConfig = true,
                        ImportReport = hasUnreadableLegacySensitiveValues ? legacyConversionReport : null,
                        ValidationIssues = validationIssues,
                        SchemaMigrationNotice = schemaMigrationNotice,
                        SensitiveStorageState = _sensitiveSettingsService.State,
                        SensitiveStorageErrorCode = _sensitiveSettingsService.LastErrorCode,
                    };
                }

                LogService.Warn(
                    $"[{ParseNullWarningCode}] config/avalonia.json parse returned null; rebuilding defaults and skipping legacy import");
                var rebuildIssues = await SaveCoreAsync(
                    new UnifiedConfig(),
                    logSavedConfig: false,
                    cancellationToken,
                    validationMode,
                    replaceInvalidPersistedConfig: true);
                return new ConfigLoadResult {
                    Config = CurrentConfig,
                    LoadedFromExistingConfig = true,
                    ValidationIssues = rebuildIssues,
                    SchemaMigrationNotice = BuildSchemaMigrationNotice(),
                    SensitiveStorageState = _sensitiveSettingsService.State,
                    SensitiveStorageErrorCode = _sensitiveSettingsService.LastErrorCode,
                };
            }
            catch (Exception ex) when (IsConfigParseFailure(ex))
            {
                LogService.Warn(
                    $"[{ParseExceptionWarningCode}] failed to parse config/avalonia.json (errorType={ex.GetType().Name}); rebuilding defaults and skipping legacy import");
                var fallbackIssues = await SaveCoreAsync(
                    new UnifiedConfig(),
                    logSavedConfig: false,
                    cancellationToken,
                    validationMode,
                    replaceInvalidPersistedConfig: true);
                return new ConfigLoadResult {
                    Config = CurrentConfig,
                    LoadedFromExistingConfig = true,
                    ValidationIssues = fallbackIssues,
                    SchemaMigrationNotice = BuildSchemaMigrationNotice(),
                    SensitiveStorageState = _sensitiveSettingsService.State,
                    SensitiveStorageErrorCode = _sensitiveSettingsService.LastErrorCode,
                };
            }
        }

        var report = await ImportLegacyAsync(ImportSource.Auto, manualImport: false, cancellationToken: cancellationToken);
        var importValidationIssues = RefreshValidationState(validationMode, logIssues: true);

        return new ConfigLoadResult {
            Config = CurrentConfig,
            LoadedFromExistingConfig = false,
            ImportReport = report,
            ValidationIssues = importValidationIssues,
            SchemaMigrationNotice = BuildSchemaMigrationNotice(),
            SensitiveStorageState = _sensitiveSettingsService.State,
            SensitiveStorageErrorCode = _sensitiveSettingsService.LastErrorCode,
        };
    }

    public async Task<ImportReport> ImportLegacyAsync(
        ImportSource source,
        bool manualImport,
        CancellationToken cancellationToken = default)
    {
        var snapshot = LegacyConfigSnapshot.FromBaseDirectory(_baseDirectory);
        var request = new LegacyImportRequest(
            snapshot,
            source,
            manualImport,
            AllowPartialImport: true);
        return await ImportLegacyAsync(request, cancellationToken);
    }

    public async Task<ImportReport> ImportLegacyAsync(
        LegacyImportRequest request,
        CancellationToken cancellationToken = default)
    {
        var report = new ImportReport {
            Source = request.Source,
            StartedAt = DateTimeOffset.UtcNow,
            OutputConfigPath = "config/avalonia.json",
            ReportPath = "debug/config-import-report.json",
        };

        try
        {
            var config = new UnifiedConfig();
            if (request.ManualImport && _store.Exists())
            {
                config.SensitiveStorage = CloneSensitiveStorageMetadata(CurrentConfig.SensitiveStorage);
            }

            AppendUnselectedMissingFiles(request, report);

            var importPlan = BuildImportPlan(request.Source);

            foreach (var step in importPlan)
            {
                await step.Importer.ImportAsync(request.Snapshot, config, report, step.FillMissingOnly, cancellationToken);
            }

            var importedAny = report.ImportedFiles.Count > 0;
            if (!importedAny && !request.ManualImport)
            {
                report.CreatedDefaultConfig = true;
                report.DefaultFallbackCount += 1;
                report.Warnings.Add("No legacy config file found, generated default avalonia.json");
            }

            if (report.DamagedFiles.Count > 0 && request.ManualImport && !request.AllowPartialImport)
            {
                report.Success = false;
                report.AppliedConfig = false;
                LogImportReport(report, request.ManualImport);
                return report;
            }

            if (!importedAny && !report.CreatedDefaultConfig)
            {
                report.Success = false;
                report.AppliedConfig = false;
                LogImportReport(report, request.ManualImport);
                return report;
            }

            ApplyFallbackGlobalValues(config, request, report);
            PostActionLegacyMigration.MaterializeImportedProfiles(config, report);

            config.SchemaVersion = UnifiedConfig.LatestSchemaVersion;
            NormalizeFightStageSelections(config);
            config.Migration = new UnifiedMigrationMetadata {
                ImportedAt = DateTimeOffset.UtcNow,
                ImportedBy = "MAAUnified",
                ImportedFromGuiNew = report.ImportedGuiNew,
                ImportedFromGui = report.ImportedGui,
                Warnings = [.. report.Warnings],
            };
            EnsureCurrentProfileExists(config, report);

            if (request.ManualImport
                && report.UnreadableValues.Count > 0
                && !request.AllowDiscardUnreadableSensitiveValues)
            {
                report.Warnings.Add(
                    "Legacy import requires confirmation before unreadable encrypted sensitive values can be discarded.");
                report.Success = false;
                report.AppliedConfig = false;
                LogImportReport(report, request.ManualImport);
                return report;
            }

            if (request.ManualImport && _store.Exists())
            {
                var suffix = $".bak.{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
                var sanitizedBackup = CreateRedactedConfigSnapshot(CurrentConfig);
                await _store.BackupSanitizedAsync(sanitizedBackup, suffix, cancellationToken);
                LogService.Info("Backed up the current config using a redacted snapshot.");
            }

            await SaveCoreAsync(config, logSavedConfig: false, cancellationToken);

            report.AppliedConfig = true;
            report.Success = report.Errors.Count == 0;
            LogImportReport(report, request.ManualImport);
        }
        catch (Exception ex)
        {
            report.Errors.Add($"Legacy import failed (errorType={ex.GetType().Name}).");
            report.Success = false;
            report.AppliedConfig = false;
            LogService.Error($"Legacy import failed: errorType={ex.GetType().Name}.");
        }
        finally
        {
            report.FinishedAt = DateTimeOffset.UtcNow;
            await WriteReportAsync(report, cancellationToken);
        }

        return report;
    }

    private async Task RetryObsoletePlaintextCleanupAsync(CancellationToken cancellationToken)
    {
        if (CurrentConfig.SensitiveStorage.Backend is SensitiveStorageBackendKind.Uninitialized
            or SensitiveStorageBackendKind.Plaintext
            || _sensitiveSettingsService.State != SensitiveStorageState.Available)
        {
            return;
        }

        try
        {
            await _sensitiveSettingsService.DeletePlaintextStoreAsync(cancellationToken);
        }
        catch (SensitiveStorageException ex)
        {
            LogService.Warn(
                $"Obsolete plaintext sensitive storage remains after cleanup retry: errorCode={ex.ErrorCode}.");
        }
    }

    private IReadOnlyList<ConfigValidationIssue> ValidateCurrentConfig(ConfigValidationMode validationMode)
    {
        var issues = ValidateMinimumConfig();
        if (validationMode == ConfigValidationMode.Minimal || CurrentConfig.Profiles.Count == 0)
        {
            return issues;
        }

        foreach (var (profileName, profile) in CurrentConfig.Profiles)
        {
            for (var index = 0; index < profile.TaskQueue.Count; index++)
            {
                var task = profile.TaskQueue[index];
                var compiled = TaskParamCompiler.CompileTask(task, profile, CurrentConfig, strict: true);
                foreach (var issue in compiled.Issues)
                {
                    issues.Add(new ConfigValidationIssue
                    {
                        Scope = "TaskValidation",
                        Code = issue.Code,
                        Field = issue.Field,
                        Message = issue.Message,
                        Blocking = issue.Blocking,
                        ProfileName = profileName,
                        TaskIndex = index,
                        TaskName = task.Name,
                        SuggestedAction = issue.Blocking
                            ? $"Open task `{task.Name}` ({TaskParamCompiler.NormalizeTaskType(task.Type)}) and save again."
                            : null,
                    });
                }
            }
        }

        return issues;
    }

    private List<ConfigValidationIssue> ValidateMinimumConfig()
    {
        var issues = new List<ConfigValidationIssue>();
        var schemaMigrationNotice = BuildSchemaMigrationNotice();
        if (schemaMigrationNotice is not null)
        {
            issues.Add(new ConfigValidationIssue
            {
                Scope = "ConfigMigration",
                Code = "SchemaOutdated",
                Field = "schema_version",
                Message = schemaMigrationNotice.Message,
                Blocking = false,
                SuggestedAction = schemaMigrationNotice.SuggestedAction,
            });
        }

        if (CurrentConfig.Profiles.Count == 0)
        {
            issues.Add(new ConfigValidationIssue
            {
                Scope = "ConfigLoad",
                Code = "ProfileMissing",
                Field = "profiles",
                Message = "No profile was found in config.",
                Blocking = true,
                SuggestedAction = "Create a new profile and reconfigure task queue.",
            });
            return issues;
        }

        if (!CurrentConfig.Profiles.ContainsKey(CurrentConfig.CurrentProfile))
        {
            issues.Add(new ConfigValidationIssue
            {
                Scope = "ConfigLoad",
                Code = "CurrentProfileMissing",
                Field = "current_profile",
                Message = $"Current profile `{CurrentConfig.CurrentProfile}` is missing.",
                Blocking = true,
                SuggestedAction = "Switch to an existing profile or recreate the current profile.",
            });
        }

        return issues;
    }

    private SchemaMigrationNotice? BuildSchemaMigrationNotice()
    {
        if (CurrentConfig.SchemaVersion == UnifiedConfig.LatestSchemaVersion)
        {
            return null;
        }

        var current = CurrentConfig.SchemaVersion;
        var latest = UnifiedConfig.LatestSchemaVersion;
        return new SchemaMigrationNotice(
            CurrentSchemaVersion: current,
            LatestSchemaVersion: latest,
            Message: $"Detected outdated schema version v{current}. Configuration will stay on compatible read mode until an explicit save.",
            SuggestedAction: "Review settings and save configuration to migrate to the latest schema. A schema backup will be created before overwrite.");
    }

    private void LogValidationIssues(IReadOnlyList<ConfigValidationIssue> issues)
    {
        if (issues.Count == 0)
        {
            return;
        }

        LogService.Warn($"Detected {issues.Count} config validation issue(s).");
        foreach (var issue in issues)
        {
            var location = issue.TaskIndex is int taskIndex
                ? $"profile={issue.ProfileName},taskIndex={taskIndex},taskName={issue.TaskName}"
                : $"profile={issue.ProfileName ?? "-"}";
            LogService.Warn(
                $"[{issue.Scope}] {issue.Code} field={issue.Field} blocking={issue.Blocking} {location} message={issue.Message}");
        }
    }

    private void UpdateValidationIssues(IReadOnlyList<ConfigValidationIssue> issues)
    {
        _currentValidationIssues = [.. issues];
    }

    private IReadOnlyList<ConfigValidationIssue> RefreshValidationState(ConfigValidationMode validationMode, bool logIssues)
    {
        var issues = ValidateCurrentConfig(validationMode);
        UpdateValidationIssues(issues);
        if (logIssues)
        {
            LogValidationIssues(issues);
        }

        return CurrentValidationIssues;
    }

    private void RaiseConfigChanged()
    {
        var handlers = ConfigChanged?.GetInvocationList();
        if (handlers is null)
        {
            return;
        }

        // A subscriber failure must not escape an already-durable save/load transaction or
        // starve the remaining subscribers; isolate and disclose each failure instead.
        foreach (Action<UnifiedConfig> handler in handlers)
        {
            try
            {
                handler(CurrentConfig);
            }
            catch (Exception ex)
            {
                LogService.Warn($"ConfigChanged subscriber failed and was isolated: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private async Task<IReadOnlyList<ConfigValidationIssue>> SaveCoreAsync(
        UnifiedConfig config,
        bool logSavedConfig,
        CancellationToken cancellationToken,
        ConfigValidationMode validationMode = ConfigValidationMode.Full,
        bool replaceInvalidPersistedConfig = false,
        bool allowInitialBackendSelectionRetry = false)
    {
        await _saveSemaphore.WaitAsync(cancellationToken);
        try
        {
            await using var commitLock = await _store.AcquireCommitLockAsync(cancellationToken);
            // Save-time repairs must remain detached until the config store accepts the write.
            // Callers may recover their own pending edits after a failed save, but they cannot
            // recover mutations made here unless this transaction keeps them off the live model.
            var candidate = CloneConfigForSave(config);
            var sourceSchemaVersion = candidate.SchemaVersion;
            if (sourceSchemaVersion != UnifiedConfig.LatestSchemaVersion && _store.Exists())
            {
                var suffix = $".schema-v{sourceSchemaVersion}.bak.{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
                var sanitizedBackup = CreateRedactedConfigSnapshot(candidate);
                await _store.BackupSanitizedAsync(sanitizedBackup, suffix, cancellationToken);
                LogService.Warn(
                    $"Schema migration write detected: v{sourceSchemaVersion} -> v{UnifiedConfig.LatestSchemaVersion}. Redacted backup created.");
            }

            candidate.SchemaVersion = UnifiedConfig.LatestSchemaVersion;
            var normalizedTaskParamCount = NormalizeTaskQueueParams(candidate);
            var persistedMetadataBaseline = CloneSensitiveStorageMetadata(candidate.SensitiveStorage);
            if (!replaceInvalidPersistedConfig)
            {
                await EnsurePersistedSensitiveMetadataIsCurrentAsync(persistedMetadataBaseline, cancellationToken);
            }

            // Validate the live input, not the detached candidate: the guard relies on profile
            // instance identity captured at load time, and the clone always fails that check.
            _sensitiveSettingsService.ValidateRedactedSaveSource(config);
            var configToPersist = CreatePersistentConfigSnapshot(candidate);
            if (allowInitialBackendSelectionRetry)
            {
                _sensitiveSettingsService.BeginInitialBackendSelectionRetry(configToPersist.SensitiveStorage);
            }

            var pendingCommit = await ReadPendingSensitiveCommitAsync(cancellationToken);
            var preparedDocument = await _sensitiveSettingsService
                .PrepareSaveAsync(configToPersist, pendingCommit, cancellationToken)
                .ConfigureAwait(false);
            if (preparedDocument is not null)
            {
                await WritePendingSensitiveCommitAsync(
                    persistedMetadataBaseline,
                    configToPersist.SensitiveStorage.Backend,
                    preparedDocument,
                    cancellationToken);
            }
            var configSnapshot = SerializeConfigSnapshot(configToPersist);
            if (!config.HasPersistedNullEntries
                && string.Equals(_lastPersistedConfigSnapshot, configSnapshot, StringComparison.Ordinal))
            {
                ApplySuccessfulSaveNormalization(config, candidate);
                CurrentConfig = config;
                return RefreshValidationState(validationMode, logIssues: true);
            }

            if (normalizedTaskParamCount > 0)
            {
                LogService.Info($"Normalized {normalizedTaskParamCount} task parameter set(s) before saving config/avalonia.json");
            }

            if (!replaceInvalidPersistedConfig)
            {
                await EnsurePersistedSensitiveMetadataIsCurrentAsync(persistedMetadataBaseline, cancellationToken);
            }
            await _store.SaveAsync(configToPersist, cancellationToken);
            TryDeletePendingSensitiveCommit();
            // The durable write already succeeded: post-write cleanup must not surface
            // cancellation as a save failure. Callers roll back live edits on any error,
            // which would diverge the live model from the persisted file.
            await SanitizeKnownConfigBackupsAsync(CancellationToken.None);
            config.MarkNullEntryRepairsPersisted();
            _lastPersistedConfigSnapshot = configSnapshot;
            ApplySuccessfulSaveNormalization(config, candidate);
            SensitiveSettingKeyRegistry.NormalizeMisplacedProfileValues(config);
            config.SensitiveStorage = CloneSensitiveStorageMetadata(configToPersist.SensitiveStorage);
            CurrentConfig = config;
            var issues = RefreshValidationState(validationMode, logIssues: true);
            RaiseConfigChanged();

            if (logSavedConfig)
            {
                LogService.Info("Saved config/avalonia.json");
            }

            return issues;
        }
        finally
        {
            _saveSemaphore.Release();
        }
    }

    private static string SerializeConfigSnapshot(UnifiedConfig config)
    {
        return JsonSerializer.Serialize(config, _configSnapshotOptions);
    }

    private static UnifiedConfig CreatePersistentConfigSnapshot(UnifiedConfig config)
    {
        var copy = CloneConfigForSave(config);
        ClearOneShotRecruitExpedited(copy);
        return copy;
    }

    private static UnifiedConfig CloneConfigForSave(UnifiedConfig config)
    {
        var json = JsonSerializer.Serialize(config, _configSnapshotOptions);
        return JsonSerializer.Deserialize<UnifiedConfig>(json, _configSnapshotOptions) ?? new UnifiedConfig();
    }

    private static void ApplySuccessfulSaveNormalization(UnifiedConfig config, UnifiedConfig candidate)
    {
        config.SchemaVersion = candidate.SchemaVersion;
        _ = NormalizeTaskQueueParams(config);
    }

    private static UnifiedConfig CreateRedactedConfigSnapshot(UnifiedConfig config)
    {
        var copy = CreatePersistentConfigSnapshot(config);
        _ = SensitiveSettingKeyRegistry.ExtractAndRemove(
            copy,
            string.IsNullOrWhiteSpace(copy.SensitiveStorage.StoreId) ? "redacted" : copy.SensitiveStorage.StoreId,
            Math.Max(1, copy.SensitiveStorage.Revision));
        return copy;
    }

    private static bool HasLegacySensitiveValues(UnifiedConfig config)
    {
        var copy = CreatePersistentConfigSnapshot(config);
        var document = SensitiveSettingKeyRegistry.ExtractAndRemove(copy, "migration-probe", 1);
        return SensitiveSettingKeyRegistry.HasNonDefaultValues(document);
    }

    private static int ConvertLegacyEncryptedSensitiveValues(UnifiedConfig config, ImportReport report)
    {
        var converted = 0;
        foreach (var definition in SensitiveSettingKeyRegistry.Definitions)
        {
            if (definition.Scope == SensitiveSettingScope.Global)
            {
                if (!config.GlobalValues.TryGetValue(definition.Key, out var value))
                {
                    continue;
                }

                var replacement = LegacyEncryptedConfigValueConverter.ConvertGlobalValue(definition.Key, value, report);
                if (!JsonNode.DeepEquals(value, replacement))
                {
                    config.GlobalValues[definition.Key] = replacement;
                    converted++;
                }

                continue;
            }

            foreach (var (profileName, profile) in config.Profiles)
            {
                if (!profile.Values.TryGetValue(definition.Key, out var value))
                {
                    continue;
                }

                var replacement = LegacyEncryptedConfigValueConverter.ConvertProfileValue(profileName, definition.Key, value, report);
                if (!JsonNode.DeepEquals(value, replacement))
                {
                    profile.Values[definition.Key] = replacement;
                    converted++;
                }
            }
        }

        return converted;
    }

    private static void RemoveSensitiveValues(UnifiedConfig config)
    {
        _ = SensitiveSettingKeyRegistry.ExtractAndRemove(
            config,
            string.IsNullOrWhiteSpace(config.SensitiveStorage.StoreId) ? "redacted" : config.SensitiveStorage.StoreId,
            Math.Max(1, config.SensitiveStorage.Revision));
    }

    private static bool IsConfigParseFailure(Exception exception)
        => exception is JsonException or NotSupportedException;

    private async Task EnsurePersistedSensitiveMetadataIsCurrentAsync(
        SensitiveStorageMetadata expected,
        CancellationToken cancellationToken)
    {
        if (!_store.Exists())
        {
            return;
        }

        var persisted = await _store.LoadAsync(cancellationToken);
        var actual = persisted?.SensitiveStorage;
        if (actual is null
            || actual.FormatVersion != expected.FormatVersion
            || actual.Backend != expected.Backend
            || !string.Equals(actual.StoreId, expected.StoreId, StringComparison.Ordinal)
            || actual.Revision != expected.Revision)
        {
            var exception = new SensitiveStorageException(
                MAAUnified.Application.Security.SensitiveStorageErrorCode.ActionRequired,
                "Sensitive settings metadata changed concurrently. Reload before saving again.");
            _sensitiveSettingsService.RecordFailure(exception);
            throw exception;
        }
    }

    private async Task<SensitiveSettingsPendingCommit?> ReadPendingSensitiveCommitAsync(
        CancellationToken cancellationToken)
    {
        var path = GetPendingSensitiveCommitPath();
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous);
            var pending = await JsonSerializer.DeserializeAsync<SensitiveSettingsPendingCommit>(
                stream,
                _configSnapshotOptions,
                cancellationToken);
            return pending is not null
                   && string.Equals(pending.ConfigPathId, GetConfigPathId(), StringComparison.Ordinal)
                ? pending
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            LogService.Warn($"Ignored invalid sensitive settings pending state: errorType={ex.GetType().Name}.");
            return null;
        }
    }

    private async Task WritePendingSensitiveCommitAsync(
        SensitiveStorageMetadata baseline,
        SensitiveStorageBackendKind backend,
        SensitiveSettingsDocument document,
        CancellationToken cancellationToken)
    {
        var path = GetPendingSensitiveCommitPath();
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        var pending = new SensitiveSettingsPendingCommit
        {
            ConfigPathId = GetConfigPathId(),
            Backend = backend,
            BaseStoreId = baseline.StoreId,
            BaseRevision = baseline.Revision,
            TargetStoreId = document.StoreId,
            TargetRevision = document.Revision,
            TargetCommitId = document.CommitId,
        };

        try
        {
            var options = FileOptions.Asynchronous | FileOptions.WriteThrough;
            await using (var stream = OperatingSystem.IsWindows()
                             ? new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, options)
                             : new FileStream(tempPath, new FileStreamOptions
                             {
                                 Mode = FileMode.CreateNew,
                                 Access = FileAccess.Write,
                                 Share = FileShare.None,
                                 BufferSize = 4096,
                                 Options = options,
                                 UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                             }))
            {
                await JsonSerializer.SerializeAsync(stream, pending, _configSnapshotOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private void TryDeletePendingSensitiveCommit()
    {
        try
        {
            File.Delete(GetPendingSensitiveCommitPath());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Warn($"Sensitive settings pending state cleanup failed: errorType={ex.GetType().Name}.");
        }
    }

    private string GetPendingSensitiveCommitPath()
        => Path.Combine(Path.GetDirectoryName(_store.ConfigPath)!, SensitivePendingCommitFileName);

    private string GetConfigPathId()
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(_store.ConfigPath))));

    private static SensitiveStorageMetadata CloneSensitiveStorageMetadata(SensitiveStorageMetadata metadata)
        => new()
        {
            FormatVersion = metadata.FormatVersion,
            Backend = metadata.Backend,
            StoreId = metadata.StoreId,
            Revision = metadata.Revision,
            ExtensionData = CloneExtensionData(metadata.ExtensionData),
        };

    private static Dictionary<string, JsonElement>? CloneExtensionData(
        Dictionary<string, JsonElement>? extensionData)
    {
        if (extensionData is null)
        {
            return null;
        }

        var copy = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, value) in extensionData)
        {
            copy[key] = value.Clone();
        }

        return copy;
    }

    private async Task SanitizeKnownConfigBackupsAsync(CancellationToken cancellationToken)
    {
        var configDirectory = Path.GetDirectoryName(_store.ConfigPath);
        if (string.IsNullOrWhiteSpace(configDirectory) || !Directory.Exists(configDirectory))
        {
            return;
        }

        if (!DiagnosticFileSecurity.IsSafeDirectoryRoot(configDirectory))
        {
            LogConfigBackupSanitizationFailure("UnsafeDirectory");
            return;
        }

        string[] backupPaths;
        try
        {
            backupPaths = Directory
                .EnumerateFiles(configDirectory, "avalonia.json*", SearchOption.TopDirectoryOnly)
                .Where(path => IsKnownConfigBackupFileName(Path.GetFileName(path)))
                .OrderBy(static path => path, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogConfigBackupSanitizationFailure("EnumerationFailed");
            return;
        }

        foreach (var backupPath in backupPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SanitizeKnownConfigBackupAsync(configDirectory, backupPath, cancellationToken);
        }
    }

    private async Task SanitizeKnownConfigBackupAsync(
        string configDirectory,
        string backupPath,
        CancellationToken cancellationToken)
    {
        if (!DiagnosticFileSecurity.TryOpenRegularFile(configDirectory, backupPath, out var input))
        {
            LogConfigBackupSanitizationFailure("UnsafeFile");
            return;
        }

        JsonObject? backup;
        UnixFileMode? originalUnixMode = null;
        try
        {
            await using (input)
            {
                if (!OperatingSystem.IsWindows())
                {
                    originalUnixMode = File.GetUnixFileMode(backupPath);
                }

                backup = await JsonSerializer.DeserializeAsync<JsonObject>(
                    input,
                    _configSnapshotOptions,
                    cancellationToken);
            }
        }
        catch (JsonException)
        {
            LogConfigBackupSanitizationFailure("InvalidJson");
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            LogConfigBackupSanitizationFailure("ReadFailed");
            return;
        }

        if (backup is null)
        {
            LogConfigBackupSanitizationFailure("InvalidJson");
            return;
        }

        if (RemoveAllRegisteredSensitiveValues(backup) == 0)
        {
            return;
        }

        var tempPath = Path.Combine(
            configDirectory,
            $".{Path.GetFileName(backupPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = CreateConfigBackupTempFile(tempPath, originalUnixMode))
            {
                await JsonSerializer.SerializeAsync(output, backup, _configSnapshotOptions, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }

            if (!OperatingSystem.IsWindows() && originalUnixMode is { } unixMode)
            {
                File.SetUnixFileMode(tempPath, unixMode);
            }

            if (!DiagnosticFileSecurity.TryOpenRegularFile(configDirectory, backupPath, out var verificationStream))
            {
                LogConfigBackupSanitizationFailure("UnsafeFile");
                return;
            }

            verificationStream.Dispose();
            ReplaceConfigBackupAtomically(tempPath, backupPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            LogConfigBackupSanitizationFailure("WriteFailed");
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static FileStream CreateConfigBackupTempFile(string tempPath, UnixFileMode? unixMode)
    {
        const FileOptions options = FileOptions.Asynchronous | FileOptions.WriteThrough;
        if (OperatingSystem.IsWindows())
        {
            return new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, options);
        }

        return new FileStream(tempPath, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 4096,
            Options = options,
            UnixCreateMode = unixMode ?? (UnixFileMode.UserRead | UnixFileMode.UserWrite),
        });
    }

    private static void ReplaceConfigBackupAtomically(string tempPath, string backupPath)
    {
        try
        {
            File.Replace(tempPath, backupPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        catch (PlatformNotSupportedException)
        {
            File.Move(tempPath, backupPath, overwrite: true);
        }
        catch (UnauthorizedAccessException) when (!OperatingSystem.IsWindows())
        {
            File.Move(tempPath, backupPath, overwrite: true);
        }
    }

    private static int RemoveAllRegisteredSensitiveValues(JsonObject backup)
    {
        var removed = 0;
        foreach (var (propertyName, value) in backup.ToArray())
        {
            if (string.Equals(propertyName, nameof(UnifiedConfig.GlobalValues), StringComparison.OrdinalIgnoreCase)
                && value is JsonObject globalValues)
            {
                removed += RemoveRegisteredSensitiveKeys(globalValues);
                continue;
            }

            if (!string.Equals(propertyName, nameof(UnifiedConfig.Profiles), StringComparison.OrdinalIgnoreCase)
                || value is not JsonObject profiles)
            {
                continue;
            }

            foreach (var (_, profileValue) in profiles)
            {
                if (profileValue is not JsonObject profile)
                {
                    continue;
                }

                foreach (var (profilePropertyName, profilePropertyValue) in profile.ToArray())
                {
                    if (string.Equals(profilePropertyName, nameof(UnifiedProfile.Values), StringComparison.OrdinalIgnoreCase)
                        && profilePropertyValue is JsonObject profileValues)
                    {
                        removed += RemoveRegisteredSensitiveKeys(profileValues);
                    }
                }
            }
        }

        return removed;
    }

    private static int RemoveRegisteredSensitiveKeys(JsonObject values)
    {
        var sensitiveKeys = values
            .Select(static property => property.Key)
            .Where(SensitiveSettingKeyRegistry.IsSensitive)
            .ToArray();
        foreach (var key in sensitiveKeys)
        {
            values.Remove(key);
        }

        return sensitiveKeys.Length;
    }

    private static bool IsKnownConfigBackupFileName(string fileName)
    {
        if (fileName.StartsWith(ConfigBackupFilePrefix, StringComparison.Ordinal))
        {
            return IsCanonicalConfigBackupTimestamp(fileName.AsSpan(ConfigBackupFilePrefix.Length));
        }

        if (!fileName.StartsWith(SchemaConfigBackupFilePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var schemaAndTimestamp = fileName.AsSpan(SchemaConfigBackupFilePrefix.Length);
        var backupMarkerIndex = schemaAndTimestamp.IndexOf(".bak.", StringComparison.Ordinal);
        if (backupMarkerIndex <= 0)
        {
            return false;
        }

        var schemaVersionText = schemaAndTimestamp[..backupMarkerIndex];
        if (!int.TryParse(schemaVersionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var schemaVersion)
            || !schemaVersionText.SequenceEqual(schemaVersion.ToString(CultureInfo.InvariantCulture)))
        {
            return false;
        }

        return IsCanonicalConfigBackupTimestamp(schemaAndTimestamp[(backupMarkerIndex + ".bak.".Length)..]);
    }

    private static bool IsCanonicalConfigBackupTimestamp(ReadOnlySpan<char> value)
    {
        if (value.Length is not ConfigBackupTimestampLength
            && (value.Length != ConfigBackupTimestampLength + 1 + ConfigBackupCollisionIdLength
                || value[ConfigBackupTimestampLength] != '.'))
        {
            return false;
        }

        if (!DateTime.TryParseExact(
                value[..ConfigBackupTimestampLength],
                ConfigBackupTimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _))
        {
            return false;
        }

        return value.Length == ConfigBackupTimestampLength
               || Guid.TryParseExact(value[(ConfigBackupTimestampLength + 1)..], "N", out _);
    }

    private void LogConfigBackupSanitizationFailure(string errorCode)
        => LogService.Warn($"Known config backup sanitization skipped: errorCode={errorCode}.");

    private static int ClearOneShotRecruitExpedited(UnifiedConfig config)
    {
        var cleared = 0;
        foreach (var profile in config.Profiles.Values)
        {
            foreach (var task in profile.TaskQueue)
            {
                if (!string.Equals(TaskParamCompiler.NormalizeTaskType(task.Type), TaskModuleTypes.Recruit, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (task.Params.Remove("expedite"))
                {
                    cleared += 1;
                }

                if (task.Params.Remove("expedite_times"))
                {
                    cleared += 1;
                }
            }
        }

        return cleared;
    }

    private List<(IConfigImporter Importer, bool FillMissingOnly)> BuildImportPlan(ImportSource source)
    {
        return source switch
        {
            ImportSource.GuiNewOnly => [(_guiNewImporter, false)],
            ImportSource.GuiOnly => [(_guiImporter, false)],
            _ => [(_guiNewImporter, false), (_guiImporter, true)],
        };
    }

    private static void ApplyFallbackGlobalValues(
        UnifiedConfig config,
        LegacyImportRequest request,
        ImportReport report)
    {
        if (request.FallbackGlobalValues is null || request.FallbackGlobalValues.Count == 0)
        {
            return;
        }

        foreach (var (key, value) in request.FallbackGlobalValues)
        {
            if (string.IsNullOrWhiteSpace(key) || config.GlobalValues.ContainsKey(key))
            {
                continue;
            }

            config.GlobalValues[key] = value?.DeepClone();
            report.MappedFieldCount += 1;
        }
    }

    private async Task WriteReportAsync(ImportReport report, CancellationToken cancellationToken)
    {
        var reportPath = Path.IsPathRooted(report.ReportPath)
            ? report.ReportPath
            : Path.Combine(_baseDirectory, report.ReportPath);
        var reportDir = Path.GetDirectoryName(reportPath);
        if (!string.IsNullOrEmpty(reportDir))
        {
            Directory.CreateDirectory(reportDir);
        }

        await using var stream = File.Create(reportPath);
        await JsonSerializer.SerializeAsync(stream, report, _reportOptions, cancellationToken);
    }

    private void LogImportReport(ImportReport report, bool manualImport)
    {
        foreach (var line in ImportReportTextFormatter.BuildLogLines(report, manualImport))
        {
            switch (line.Level.ToUpperInvariant())
            {
                case "ERROR":
                    LogService.Error(line.Message);
                    break;
                case "WARN":
                case "WARNING":
                    LogService.Warn(line.Message);
                    break;
                default:
                    LogService.Info(line.Message);
                    break;
            }
        }

        LogService.Info($"Legacy import report: {report.Summary}");
    }

    private static void AppendUnselectedMissingFiles(LegacyImportRequest request, ImportReport report)
    {
        if (request.Source == ImportSource.GuiOnly && !request.Snapshot.GuiNewExists)
        {
            AddReportFile(report.MissingFiles, GuiNewFileName);
        }

        if (request.Source == ImportSource.GuiNewOnly && !request.Snapshot.GuiExists)
        {
            AddReportFile(report.MissingFiles, GuiFileName);
        }
    }

    private static void AddReportFile(ICollection<string> collection, string fileName)
    {
        foreach (var existing in collection)
        {
            if (string.Equals(existing, fileName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        collection.Add(fileName);
    }

    private static void EnsureCurrentProfileExists(UnifiedConfig config, ImportReport report)
    {
        if (config.Profiles.ContainsKey(config.CurrentProfile))
        {
            return;
        }

        var missing = config.CurrentProfile;
        var fallback = SelectFallbackProfileName(config);
        report.Warnings.Add(
            $"Current profile `{missing}` was not imported; selected `{fallback}` instead.");
        config.CurrentProfile = fallback;
    }

    // A persisted configuration must always retain a usable selected profile. The fallback is
    // deterministic: the first non-"Default" profile, then the first profile, then a recreated
    // "Default" profile. Callers must disclose the repair and persist it.
    private static (string Missing, string Fallback)? RepairMissingCurrentProfileSelection(UnifiedConfig config)
    {
        if (config.Profiles.ContainsKey(config.CurrentProfile))
        {
            return null;
        }

        var missing = config.CurrentProfile;
        var fallback = SelectFallbackProfileName(config);
        config.CurrentProfile = fallback;
        return (missing, fallback);
    }

    private static bool TryReadBool(JsonNode? node, out bool value)
    {
        value = false;
        if (node is null)
        {
            return false;
        }

        var text = node.ToString();
        if (bool.TryParse(text, out value))
        {
            return true;
        }

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            value = number != 0;
            return true;
        }

        return false;
    }

    private static string SelectFallbackProfileName(UnifiedConfig config)
    {
        var fallback = config.Profiles.Keys.FirstOrDefault(name =>
            !string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase));
        fallback ??= config.Profiles.Keys.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(fallback))
        {
            fallback = "Default";
            config.Profiles[fallback] = new UnifiedProfile();
        }

        return fallback;
    }

    private static int NormalizeTaskQueueParams(UnifiedConfig config)
    {
        var normalizedCount = 0;
        foreach (var profile in config.Profiles.Values)
        {
            foreach (var task in profile.TaskQueue)
            {
                var compiled = TaskParamCompiler.NormalizeTaskForPersistence(task, profile, config, strict: true);
                if (compiled.HasBlockingIssues)
                {
                    continue;
                }

                var normalizedParams = compiled.Params.DeepClone() as JsonObject ?? new JsonObject();
                var typeChanged = !string.Equals(task.Type, compiled.NormalizedType, StringComparison.Ordinal);
                var paramsChanged = !JsonNode.DeepEquals(task.Params, normalizedParams);
                if (!typeChanged && !paramsChanged)
                {
                    continue;
                }

                task.Type = compiled.NormalizedType;
                task.Params = normalizedParams;
                normalizedCount += 1;
            }
        }

        return normalizedCount;
    }

    private static int NormalizeFightStageSelections(UnifiedConfig config)
    {
        var normalizedCount = 0;
        foreach (var profile in config.Profiles.Values)
        {
            foreach (var task in profile.TaskQueue)
            {
                if (!string.Equals(TaskParamCompiler.NormalizeTaskType(task.Type), TaskModuleTypes.Fight, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!task.Params.TryGetPropertyValue("stage", out var stageNode)
                    || stageNode is not JsonValue stageValue)
                {
                    continue;
                }

                if (!stageValue.TryGetValue(out string? stage))
                {
                    continue;
                }

                var normalizedStage = FightStageSelection.NormalizeStoredValue(stage);
                task.Params.TryGetPropertyValue("_ui_stage_plan", out var stagePlanNode);
                if (string.Equals(stage, normalizedStage, StringComparison.Ordinal)
                    && stagePlanNode is null)
                {
                    continue;
                }

                if (!string.Equals(stage, normalizedStage, StringComparison.Ordinal))
                {
                    task.Params["stage"] = normalizedStage;
                    normalizedCount += 1;
                }

                if (stagePlanNode is JsonArray stagePlanArray)
                {
                    var updated = false;
                    for (var index = 0; index < stagePlanArray.Count; index++)
                    {
                        if (stagePlanArray[index] is not JsonValue planValue
                            || !planValue.TryGetValue(out string? planStage))
                        {
                            continue;
                        }

                        var normalizedPlanStage = FightStageSelection.NormalizeStoredValue(planStage);
                        if (string.Equals(planStage, normalizedPlanStage, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        stagePlanArray[index] = normalizedPlanStage;
                        updated = true;
                    }

                    if (updated)
                    {
                        normalizedCount += 1;
                    }
                }
            }
        }

        return normalizedCount;
    }
}
