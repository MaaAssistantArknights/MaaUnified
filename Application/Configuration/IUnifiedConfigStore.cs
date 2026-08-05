using MAAUnified.Application.Models;

namespace MAAUnified.Application.Configuration;

public interface IUnifiedConfigStore
{
    string ConfigPath { get; }

    bool Exists();

    Task<UnifiedConfig?> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(UnifiedConfig config, CancellationToken cancellationToken = default);

    ValueTask<IAsyncDisposable> AcquireCommitLockAsync(CancellationToken cancellationToken = default)
        => UnifiedConfigCommitLock.AcquireAsync(ConfigPath, cancellationToken);

    Task BackupAsync(string suffix, CancellationToken cancellationToken = default);

    Task BackupSanitizedAsync(UnifiedConfig config, string suffix, CancellationToken cancellationToken = default)
        => Task.FromException(new NotSupportedException(
            "This config store must implement structured sanitized backups."));
}
