namespace MAAUnified.Application.Security;

public interface ISensitiveSettingsBackend
{
    SensitiveStorageBackendKind Kind { get; }

    Task VerifyPersistenceAsync(CancellationToken cancellationToken = default);

    Task<SensitiveSettingsDocument> ReadAsync(
        string storeId,
        long revision,
        CancellationToken cancellationToken = default);

    Task<SensitiveSettingsDocument> ReadCurrentAsync(
        string storeId,
        CancellationToken cancellationToken = default);

    Task WriteAsync(
        SensitiveSettingsDocument document,
        long expectedRevision,
        CancellationToken cancellationToken = default);

    Task ReplaceCurrentAsync(
        SensitiveSettingsDocument document,
        SensitiveSettingsDocument expectedCurrent,
        SensitiveSettingsDocument expectedPrevious,
        CancellationToken cancellationToken = default);
}

public interface IDeletableSensitiveSettingsBackend
{
    Task DeleteAsync(CancellationToken cancellationToken = default);
}

public interface IPlaintextSensitiveStorageConsent
{
    Task<bool> ConfirmAsync(CancellationToken cancellationToken = default);
}
