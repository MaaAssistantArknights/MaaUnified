using System.Collections.Concurrent;

namespace MAAUnified.Application.Configuration;

internal static class UnifiedConfigCommitLock
{
    private const int RetryDelayMilliseconds = 50;
    private const UnixFileMode PrivateUnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProcessGates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static async ValueTask<IAsyncDisposable> AcquireAsync(
        string configPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        var normalizedConfigPath = Path.GetFullPath(configPath);
        var processGate = ProcessGates.GetOrAdd(normalizedConfigPath, static _ => new SemaphoreSlim(1, 1));
        await processGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var lockPath = Path.Combine(
                Path.GetDirectoryName(normalizedConfigPath)!,
                $".{Path.GetFileName(normalizedConfigPath)}.commit.lock");
            Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
            var stream = await AcquireFileLockAsync(lockPath, cancellationToken).ConfigureAwait(false);
            try
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(stream.SafeFileHandle, PrivateUnixFileMode);
                }

                return new Lease(stream, processGate);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
        catch
        {
            processGate.Release();
            throw;
        }
    }

    private static async Task<FileStream> AcquireFileLockAsync(
        string lockPath,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return CreateNewLockFile(lockPath);
            }
            catch (IOException) when (File.Exists(lockPath))
            {
                try
                {
                    ValidateExistingLockFile(lockPath);
                }
                catch (FileNotFoundException)
                {
                    continue;
                }
            }

            try
            {
                return OpenExistingLockFile(lockPath);
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            catch (IOException) when (File.Exists(lockPath))
            {
                await Task.Delay(RetryDelayMilliseconds, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static FileStream CreateNewLockFile(string lockPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        }

        return new FileStream(lockPath, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            UnixCreateMode = PrivateUnixFileMode,
        });
    }

    private static FileStream OpenExistingLockFile(string lockPath)
        => new(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

    private static void ValidateExistingLockFile(string lockPath)
    {
        var attributes = File.GetAttributes(lockPath);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
            || new FileInfo(lockPath).LinkTarget is not null)
        {
            throw new IOException("The unified configuration commit lock must be a regular file.");
        }
    }

    private sealed class Lease(FileStream stream, SemaphoreSlim processGate) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                stream.Dispose();
                processGate.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}
