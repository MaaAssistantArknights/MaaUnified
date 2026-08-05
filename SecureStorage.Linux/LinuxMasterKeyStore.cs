using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace MAAUnified.SecureStorage.Linux;

public sealed class LinuxMasterKeyStore
{
    private const int KeyLength = 32;
    private const string KeyFileName = "sqlcipher.key";

    private const int OpenReadOnly = 0;
    private const int OpenWriteOnly = 1;
    private const int OpenCreate = 0x40;
    private const int OpenExclusive = 0x80;
    private const int OpenNonBlocking = 0x800;
    private const int OpenDirectory = 0x10000;
    private const int OpenNoFollow = 0x20000;
    private const int OpenCloseOnExec = 0x80000;
    private const int AtEmptyPath = 0x1000;
    private const uint StatxType = 0x0001;
    private const uint StatxMode = 0x0002;
    private const uint StatxSize = 0x0200;
    private const ushort FileTypeMask = 0xf000;
    private const ushort RegularFileType = 0x8000;
    private const ushort DirectoryFileType = 0x4000;
    private const ushort GroupOrOtherPermissions = 0x003f;
    private const uint OwnerDirectoryMode = 0x01c0;
    private const uint OwnerFileMode = 0x0180;
    private const int ErrorNoEntry = 2;
    private const int ErrorPermissionDenied = 13;
    private const int ErrorAlreadyExists = 17;
    private const int ErrorNotDirectory = 20;
    private const int ErrorSymbolicLinkLoop = 40;

    private readonly string _keyPath;

    public LinuxMasterKeyStore(string? xdgDataHome = null)
    {
        var dataHome = string.IsNullOrWhiteSpace(xdgDataHome) ? Environment.GetEnvironmentVariable("XDG_DATA_HOME") : xdgDataHome;
        if (string.IsNullOrWhiteSpace(dataHome))
        {
            dataHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }

        _keyPath = Path.GetFullPath(Path.Combine(dataHome, "MAAUnified", "secure", KeyFileName));
    }

    public string KeyPath => _keyPath;

    public async Task<byte[]> GetOrCreateAsync(bool databaseExists, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("The Linux SQLCipher master key store is only available on Linux.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var parentHandle = OpenDirectoryTree(Path.GetDirectoryName(_keyPath)!);
        ValidateDirectoryHandle(parentHandle);

        var existingHandle = TryOpenExistingKey(parentHandle);
        if (existingHandle is not null)
        {
            await using var stream = new FileStream(existingHandle, FileAccess.Read, KeyLength + 1, isAsync: true);
            return await ReadAndValidateKeyAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        if (databaseExists)
        {
            throw new FileNotFoundException("The SQLCipher database exists but its master key is missing.", _keyPath);
        }

        var key = RandomNumberGenerator.GetBytes(KeyLength);
        try
        {
            var fileDescriptor = OpenAt(
                GetFileDescriptor(parentHandle),
                KeyFileName,
                OpenWriteOnly | OpenCreate | OpenExclusive | OpenNoFollow | OpenCloseOnExec,
                OwnerFileMode);
            if (fileDescriptor < 0)
            {
                ThrowOpenException("create the SQLCipher master key", Marshal.GetLastPInvokeError());
            }

            await using var stream = new FileStream(
                new SafeFileHandle((nint)fileDescriptor, ownsHandle: true),
                FileAccess.Write,
                4096,
                isAsync: true);
            ValidateKeyHandle(stream.SafeFileHandle, expectedLength: 0);
            await stream.WriteAsync(key, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
            ValidateKeyHandle(stream.SafeFileHandle, expectedLength: KeyLength);
            return key;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(key);
            throw;
        }
    }

    private static async Task<byte[]> ReadAndValidateKeyAsync(FileStream stream, CancellationToken cancellationToken)
    {
        ValidateKeyHandle(stream.SafeFileHandle, expectedLength: KeyLength);
        var buffer = new byte[KeyLength + 1];
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        ValidateKeyHandle(stream.SafeFileHandle, expectedLength: KeyLength);
        if (totalRead != KeyLength)
        {
            CryptographicOperations.ZeroMemory(buffer);
            throw new InvalidDataException("The SQLCipher master key has an invalid length.");
        }

        var key = buffer.AsSpan(0, KeyLength).ToArray();
        CryptographicOperations.ZeroMemory(buffer);
        return key;
    }

    private static SafeFileHandle OpenDirectoryTree(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var rootDescriptor = Open("/", OpenReadOnly | OpenDirectory | OpenNoFollow | OpenCloseOnExec);
        if (rootDescriptor < 0)
        {
            ThrowOpenException("open the filesystem root", Marshal.GetLastPInvokeError());
        }

        SafeFileHandle? current = new((nint)rootDescriptor, ownsHandle: true);
        try
        {
            foreach (var component in fullPath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                var nextDescriptor = OpenAt(
                    GetFileDescriptor(current),
                    component,
                    OpenReadOnly | OpenDirectory | OpenNoFollow | OpenCloseOnExec,
                    0);
                if (nextDescriptor < 0 && Marshal.GetLastPInvokeError() == ErrorNoEntry)
                {
                    if (MkdirAt(GetFileDescriptor(current), component, OwnerDirectoryMode) != 0
                        && Marshal.GetLastPInvokeError() != ErrorAlreadyExists)
                    {
                        ThrowOpenException($"create SQLCipher key directory component '{component}'", Marshal.GetLastPInvokeError());
                    }

                    nextDescriptor = OpenAt(
                        GetFileDescriptor(current),
                        component,
                        OpenReadOnly | OpenDirectory | OpenNoFollow | OpenCloseOnExec,
                        0);
                }

                if (nextDescriptor < 0)
                {
                    ThrowOpenException($"open SQLCipher key directory component '{component}'", Marshal.GetLastPInvokeError());
                }

                var next = new SafeFileHandle((nint)nextDescriptor, ownsHandle: true);
                current.Dispose();
                current = next;
            }

            var result = current;
            current = null;
            return result;
        }
        finally
        {
            current?.Dispose();
        }
    }

    private static SafeFileHandle? TryOpenExistingKey(SafeFileHandle parentHandle)
    {
        var fileDescriptor = OpenAt(
            GetFileDescriptor(parentHandle),
            KeyFileName,
            OpenReadOnly | OpenNonBlocking | OpenNoFollow | OpenCloseOnExec,
            0);
        if (fileDescriptor >= 0)
        {
            var handle = new SafeFileHandle((nint)fileDescriptor, ownsHandle: true);
            try
            {
                ValidateKeyHandle(handle, expectedLength: KeyLength);
                return handle;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        var error = Marshal.GetLastPInvokeError();
        if (error == ErrorNoEntry)
        {
            return null;
        }

        ThrowOpenException("open the SQLCipher master key", error);
        return null;
    }

    private static void ValidateDirectoryHandle(SafeFileHandle handle)
    {
        var status = GetStatus(handle);
        if ((status.Mode & FileTypeMask) != DirectoryFileType)
        {
            throw new UnauthorizedAccessException("The SQLCipher master key parent must be a regular directory.");
        }

        if ((status.Mode & GroupOrOtherPermissions) != 0)
        {
            throw new UnauthorizedAccessException("The SQLCipher master key directory is accessible to group or other users.");
        }
    }

    private static void ValidateKeyHandle(SafeFileHandle handle, long expectedLength)
    {
        var status = GetStatus(handle);
        if ((status.Mode & FileTypeMask) != RegularFileType)
        {
            throw new UnauthorizedAccessException("The SQLCipher master key must be a regular file.");
        }

        if ((status.Mode & GroupOrOtherPermissions) != 0)
        {
            throw new UnauthorizedAccessException("The SQLCipher master key is accessible to group or other users.");
        }

        if ((long)status.Size != expectedLength)
        {
            throw new InvalidDataException("The SQLCipher master key has an invalid length.");
        }
    }

    private static LinuxFileStatus GetStatus(SafeFileHandle handle)
    {
        if (Statx(
                GetFileDescriptor(handle),
                string.Empty,
                AtEmptyPath,
                StatxType | StatxMode | StatxSize,
                out var status) != 0)
        {
            throw new IOException(
                "The SQLCipher master key metadata could not be read.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        if ((status.Mask & (StatxType | StatxMode | StatxSize)) != (StatxType | StatxMode | StatxSize))
        {
            throw new IOException("The SQLCipher master key metadata is incomplete.");
        }

        return status;
    }

    private static int GetFileDescriptor(SafeFileHandle handle)
        => checked((int)handle.DangerousGetHandle());

    private static void ThrowOpenException(string operation, int error)
    {
        var inner = new Win32Exception(error);
        if (error is ErrorPermissionDenied or ErrorNotDirectory or ErrorSymbolicLinkLoop)
        {
            throw new UnauthorizedAccessException($"Unable to {operation} without following symbolic links.", inner);
        }

        throw new IOException($"Unable to {operation}.", inner);
    }

    [StructLayout(LayoutKind.Sequential, Size = 256)]
    private struct LinuxFileStatus
    {
        public uint Mask;
        public uint BlockSize;
        public ulong Attributes;
        public uint LinkCount;
        public uint UserId;
        public uint GroupId;
        public ushort Mode;
        public ushort Spare;
        public ulong Inode;
        public ulong Size;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAt(int directoryFileDescriptor, string path, int flags, uint mode);

    [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true)]
    private static extern int MkdirAt(int directoryFileDescriptor, string path, uint mode);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(
        int directoryFileDescriptor,
        string path,
        int flags,
        uint mask,
        out LinuxFileStatus status);
}
