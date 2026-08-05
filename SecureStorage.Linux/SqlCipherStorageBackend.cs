using System.Security.Cryptography;
using System.Text.Json;
using MAAUnified.Application.Security;
using Microsoft.Data.Sqlite;

namespace MAAUnified.SecureStorage.Linux;

public sealed class SqlCipherStorageBackend : ISensitiveSettingsBackend
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly SqlCipherProviderInitializer SharedProviderInitializer = new(
        static () => SQLitePCL.Batteries_V2.Init());
    private readonly string _runtimeBaseDirectory;
    private readonly LinuxMasterKeyStore _keyStore;
    private readonly SqlCipherProviderInitializer _providerInitializer;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqlCipherStorageBackend(string runtimeBaseDirectory, LinuxMasterKeyStore? keyStore = null)
        : this(runtimeBaseDirectory, keyStore, SharedProviderInitializer)
    {
    }

    internal SqlCipherStorageBackend(
        string runtimeBaseDirectory,
        LinuxMasterKeyStore? keyStore,
        SqlCipherProviderInitializer providerInitializer)
    {
        _runtimeBaseDirectory = Path.GetFullPath(runtimeBaseDirectory);
        _keyStore = keyStore ?? new LinuxMasterKeyStore();
        _providerInitializer = providerInitializer ?? throw new ArgumentNullException(nameof(providerInitializer));
    }

    public SensitiveStorageBackendKind Kind => SensitiveStorageBackendKind.LinuxLocalSqlCipher;

    public async Task VerifyPersistenceAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var openedConnection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var connection = openedConnection.Connection;
            await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (SensitiveStorageException) { throw; }
        catch (Exception ex) { throw Classify(ex, "SQLCipher persistence verification failed."); }
        finally { _gate.Release(); }
    }

    public async Task<SensitiveSettingsDocument> ReadAsync(string storeId, long revision, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var openedConnection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var connection = openedConnection.Connection;
            await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            var envelope = await ReadEnvelopeAsync(connection, cancellationToken).ConfigureAwait(false)
                ?? throw new SensitiveStorageException(SensitiveStorageErrorCode.RevisionNotFound, "SQLCipher storage does not contain the requested revision.");
            return SensitiveSettingsEnvelopeStore.FindRevision(envelope, storeId, revision);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (SensitiveStorageException) { throw; }
        catch (Exception ex) { throw Classify(ex, "SQLCipher storage could not be read."); }
        finally { _gate.Release(); }
    }

    public async Task<SensitiveSettingsDocument> ReadCurrentAsync(
        string storeId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var openedConnection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var connection = openedConnection.Connection;
            await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            var envelope = await ReadEnvelopeAsync(connection, cancellationToken).ConfigureAwait(false)
                ?? throw new SensitiveStorageException(SensitiveStorageErrorCode.RevisionNotFound, "SQLCipher storage does not contain the requested store.");
            return SensitiveSettingsEnvelopeStore.FindCurrent(envelope, storeId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (SensitiveStorageException) { throw; }
        catch (Exception ex) { throw Classify(ex, "SQLCipher storage could not be read."); }
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
            existing => SensitiveSettingsEnvelopeStore.Merge(existing, document, expectedRevision),
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
            await using var openedConnection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var connection = openedConnection.Connection;
            await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var existing = await ReadEnvelopeAsync(connection, cancellationToken, transaction).ConfigureAwait(false);
            var envelope = updateEnvelope(existing);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO payload(id, data) VALUES(1, $data) ON CONFLICT(id) DO UPDATE SET data = excluded.data;";
            command.Parameters.AddWithValue("$data", JsonSerializer.SerializeToUtf8Bytes(envelope, SerializerOptions));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            var verified = await ReadEnvelopeAsync(connection, cancellationToken).ConfigureAwait(false)
                ?? throw new SensitiveStorageException(SensitiveStorageErrorCode.PersistenceVerificationFailed, "SQLCipher storage read-back failed.");
            _ = SensitiveSettingsEnvelopeStore.FindRevision(verified, document.StoreId, document.Revision);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (SensitiveStorageException) { throw; }
        catch (Exception ex) { throw Classify(ex, "SQLCipher storage could not be written."); }
        finally { _gate.Release(); }
    }

    public async Task<string> GetCipherVersionAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var openedConnection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var connection = openedConnection.Connection;
            return await ReadCipherVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (SensitiveStorageException) { throw; }
        catch (Exception ex) { throw Classify(ex, "SQLCipher version could not be read."); }
        finally { _gate.Release(); }
    }

    private async Task<OpenedConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        _providerInitializer.EnsureInitialized();
        var secureFile = LinuxSecureDatabaseFile.Open(_runtimeBaseDirectory);
        byte[]? key = null;
        SqliteConnection? connection = null;
        try
        {
            var databaseExists = secureFile.TryOpenExistingDatabase();
            key = await _keyStore.GetOrCreateAsync(databaseExists, cancellationToken).ConfigureAwait(false);
            if (!databaseExists)
            {
                secureFile.CreateDatabase();
            }

            connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = secureFile.SqliteDataSource,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
            }.ToString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            secureFile.VerifyDatabasePathUnchanged();
            await using (var keyCommand = connection.CreateCommand())
            {
                keyCommand.CommandText = $"PRAGMA key = \"x'{Convert.ToHexString(key)}'\";";
                await keyCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(await ReadCipherVersionAsync(connection, cancellationToken).ConfigureAwait(false)))
            {
                throw new SensitiveStorageException(SensitiveStorageErrorCode.StructurallyUnavailable, "The loaded SQLite provider does not support SQLCipher.");
            }

            return new OpenedConnection(connection, secureFile);
        }
        catch
        {
            try
            {
                if (connection is not null)
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                secureFile.Dispose();
            }

            throw;
        }
        finally
        {
            if (key is not null)
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
    }

    private static async Task<string> ReadCipherVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA cipher_version;";
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) ?? string.Empty;
    }

    private static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS payload(id INTEGER PRIMARY KEY CHECK(id = 1), data BLOB NOT NULL);";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<SensitiveSettingsEnvelope?> ReadEnvelopeAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken,
        System.Data.Common.DbTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction?)transaction;
        command.CommandText = "SELECT data FROM payload WHERE id = 1;";
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is byte[] payload
            ? JsonSerializer.Deserialize<SensitiveSettingsEnvelope>(payload, SerializerOptions)
            : null;
    }

    internal static SensitiveStorageException Classify(Exception exception, string message)
    {
        if (ContainsNativeProviderLoadFailure(exception))
        {
            return new(SensitiveStorageErrorCode.StructurallyUnavailable, message, exception);
        }

        if (exception is UnauthorizedAccessException) return new(SensitiveStorageErrorCode.PermissionDenied, message, exception);
        if (exception is FileNotFoundException or InvalidDataException or JsonException
            || exception is SqliteException sqlite && sqlite.SqliteErrorCode is 11 or 26)
        {
            return new(SensitiveStorageErrorCode.Corrupt, message, exception);
        }

        return new(SensitiveStorageErrorCode.Unavailable, message, exception);
    }

    private static bool ContainsNativeProviderLoadFailure(Exception exception)
    {
        var pending = new Stack<Exception>();
        pending.Push(exception);
        while (pending.TryPop(out var current))
        {
            if (current is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
            {
                return true;
            }

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Push(inner);
                }
            }
            else if (current.InnerException is { } inner)
            {
                pending.Push(inner);
            }
        }

        return false;
    }

    private sealed class OpenedConnection(SqliteConnection connection, LinuxSecureDatabaseFile secureFile) : IAsyncDisposable
    {
        public SqliteConnection Connection { get; } = connection;

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Connection.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                secureFile.Dispose();
            }
        }
    }
}

internal sealed class SqlCipherProviderInitializer
{
    private readonly object _gate = new();
    private readonly Action _initialize;
    private bool _initialized;

    public SqlCipherProviderInitializer(Action initialize)
    {
        _initialize = initialize ?? throw new ArgumentNullException(nameof(initialize));
    }

    public void EnsureInitialized()
    {
        if (Volatile.Read(ref _initialized))
        {
            return;
        }

        lock (_gate)
        {
            if (_initialized)
            {
                return;
            }

            _initialize();
            Volatile.Write(ref _initialized, true);
        }
    }
}
