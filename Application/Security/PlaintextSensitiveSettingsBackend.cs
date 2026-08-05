using System.Text.Json;

namespace MAAUnified.Application.Security;

public sealed class PlaintextSensitiveSettingsBackend : ISensitiveSettingsBackend, IDeletableSensitiveSettingsBackend
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private readonly string _filePath;
    private readonly string _lockPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PlaintextSensitiveSettingsBackend(string runtimeBaseDirectory)
    {
        _filePath = Path.Combine(runtimeBaseDirectory, "data", "sensitive-settings.json");
        _lockPath = Path.Combine(runtimeBaseDirectory, "data", "sensitive-settings.lock");
    }

    public SensitiveStorageBackendKind Kind => SensitiveStorageBackendKind.Plaintext;

    public Task VerifyPersistenceAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.GetDirectoryName(_filePath)!;
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            if (FileSystemEntryExists(_filePath))
            {
                ValidateExistingFile();
            }

            return Task.CompletedTask;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SensitiveStorageException)
        {
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new SensitiveStorageException(SensitiveStorageErrorCode.PermissionDenied, "Plaintext sensitive storage permissions are invalid.", ex);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException)
        {
            throw new SensitiveStorageException(SensitiveStorageErrorCode.Unavailable, "Plaintext sensitive storage persistence verification failed.", ex);
        }
    }

    public async Task<SensitiveSettingsDocument> ReadAsync(string storeId, long revision, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await VerifyPersistenceAsync(cancellationToken).ConfigureAwait(false);
            var envelope = await ReadEnvelopeAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new SensitiveStorageException(SensitiveStorageErrorCode.RevisionNotFound, "Plaintext sensitive storage does not contain the requested revision.");
            return FindRevision(envelope, storeId, revision);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SensitiveStorageException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new SensitiveStorageException(SensitiveStorageErrorCode.Corrupt, "Plaintext sensitive storage is invalid.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new SensitiveStorageException(SensitiveStorageErrorCode.PermissionDenied, "Plaintext sensitive storage permission was denied.", ex);
        }
        catch (Exception ex)
        {
            throw new SensitiveStorageException(SensitiveStorageErrorCode.Unavailable, "Plaintext sensitive storage could not be read.", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SensitiveSettingsDocument> ReadCurrentAsync(
        string storeId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await VerifyPersistenceAsync(cancellationToken).ConfigureAwait(false);
            await using var crossProcessLock = await AcquireCrossProcessLockAsync(cancellationToken).ConfigureAwait(false);
            var envelope = await ReadEnvelopeAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new SensitiveStorageException(SensitiveStorageErrorCode.RevisionNotFound, "Plaintext sensitive storage does not contain the requested store.");
            return SensitiveSettingsEnvelopeStore.FindCurrent(envelope, storeId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (SensitiveStorageException) { throw; }
        catch (JsonException ex) { throw new SensitiveStorageException(SensitiveStorageErrorCode.Corrupt, "Plaintext sensitive storage is invalid.", ex); }
        catch (UnauthorizedAccessException ex) { throw new SensitiveStorageException(SensitiveStorageErrorCode.PermissionDenied, "Plaintext sensitive storage permission was denied.", ex); }
        catch (Exception ex) { throw new SensitiveStorageException(SensitiveStorageErrorCode.Unavailable, "Plaintext sensitive storage could not be read.", ex); }
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
            await VerifyPersistenceAsync(cancellationToken).ConfigureAwait(false);
            await using var crossProcessLock = await AcquireCrossProcessLockAsync(cancellationToken).ConfigureAwait(false);
            var existing = await ReadEnvelopeAsync(cancellationToken).ConfigureAwait(false);
            var envelope = updateEnvelope(existing);

            var directory = Path.GetDirectoryName(_filePath)!;
            var tempPath = Path.Combine(directory, $".{Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var stream = CreateRestrictedFile(tempPath))
                {
                    await JsonSerializer.SerializeAsync(stream, envelope, SerializerOptions, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(tempPath, _filePath, overwrite: true);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(_filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SensitiveStorageException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new SensitiveStorageException(SensitiveStorageErrorCode.Corrupt, "Plaintext sensitive storage is invalid.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new SensitiveStorageException(SensitiveStorageErrorCode.PermissionDenied, "Plaintext sensitive storage permission was denied.", ex);
        }
        catch (Exception ex)
        {
            throw new SensitiveStorageException(SensitiveStorageErrorCode.Unavailable, "Plaintext sensitive storage could not be written.", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!FileSystemEntryExists(_filePath))
            {
                return;
            }

            ValidateExistingFile();
            File.Delete(_filePath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new SensitiveStorageException(SensitiveStorageErrorCode.PermissionDenied, "Plaintext sensitive storage could not be deleted.", ex);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException)
        {
            throw new SensitiveStorageException(SensitiveStorageErrorCode.Unavailable, "Plaintext sensitive storage could not be deleted.", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SensitiveSettingsEnvelope?> ReadEnvelopeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return null;
        }

        await using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        return await JsonSerializer.DeserializeAsync<SensitiveSettingsEnvelope>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new SensitiveStorageException(SensitiveStorageErrorCode.Corrupt, "Plaintext sensitive storage is invalid.");
    }

    private static SensitiveSettingsDocument FindRevision(SensitiveSettingsEnvelope envelope, string storeId, long revision)
    {
        return SensitiveSettingsEnvelopeStore.FindRevision(envelope, storeId, revision);
    }

    private async Task<FileStream> AcquireCrossProcessLockAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return CreateRestrictedLockFile(_lockPath);
            }
            catch (IOException) when (attempt < 199)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new SensitiveStorageException(
            SensitiveStorageErrorCode.Unavailable,
            "Timed out waiting for the plaintext sensitive storage lock.");
    }

    private static FileStream CreateRestrictedLockFile(string path)
        => OperatingSystem.IsWindows()
            ? new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
            : new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });

    private static FileStream CreateRestrictedFile(string path)
    {
        const FileOptions options = FileOptions.Asynchronous | FileOptions.WriteThrough;
        return OperatingSystem.IsWindows()
            ? new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, options)
            : new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 4096,
                Options = options,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
    }

    private void ValidateExistingFile()
    {
        if (File.ResolveLinkTarget(_filePath, returnFinalTarget: false) is not null)
        {
            throw new UnauthorizedAccessException("The plaintext sensitive settings file must not be a symbolic link.");
        }

        var attributes = File.GetAttributes(_filePath);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new UnauthorizedAccessException("The plaintext sensitive settings path must be a regular file.");
        }

        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var mode = File.GetUnixFileMode(_filePath);
        const UnixFileMode forbidden = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                                       | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        if ((mode & forbidden) != 0)
        {
            throw new UnauthorizedAccessException("The plaintext sensitive settings file is accessible to group or other users.");
        }
    }

    private static bool FileSystemEntryExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }
}
