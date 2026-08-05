using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MAAUnified.SecureStorage.Linux;

internal sealed class LinuxSecureDatabaseFile : IDisposable
{
    private const string DatabaseFileName = "sensitive-settings.db";

    private const int AtEmptyPath = 0x1000;
    private const int ErrnoAccessDenied = 13;
    private const int ErrnoAlreadyExists = 17;
    private const int ErrnoInvalidCrossDeviceLink = 18;
    private const int ErrnoIsDirectory = 21;
    private const int ErrnoLoop = 40;
    private const int ErrnoNoEntry = 2;
    private const int ErrnoNotDirectory = 20;
    private const int ErrnoOperationNotPermitted = 1;

    private const int OpenCreate = 0x40;
    private const int OpenExclusive = 0x80;
    private const int OpenCloseOnExec = 0x80000;
    private const int OpenDirectoryFlag = 0x10000;
    private const int OpenNoFollow = 0x20000;
    private const int OpenNonBlocking = 0x800;
    private const int OpenPath = 0x200000;
    private const int OpenReadWrite = 0x2;

    private const uint StatxBasicStats = 0x7ff;
    private const uint StatxRequiredIdentity = 0x10f;
    private const ushort FileTypeMask = 0xf000;
    private const ushort FileTypeDirectory = 0x4000;
    private const ushort FileTypeRegular = 0x8000;
    private const ushort GroupWrite = 0x10;
    private const ushort OtherWrite = 0x2;
    private const ushort GroupAndOtherPermissions = 0x3f;
    private const ushort PrivateFilePermissions = 0x180;

    private SafeFileHandle? _databaseFile;
    private readonly SafeFileHandle _dataDirectory;
    private readonly SafeFileHandle _runtimeDirectory;
    private LinuxStatx _databaseIdentity;

    private LinuxSecureDatabaseFile(SafeFileHandle runtimeDirectory, SafeFileHandle dataDirectory)
    {
        _runtimeDirectory = runtimeDirectory;
        _dataDirectory = dataDirectory;
    }

    public string SqliteDataSource
        => $"/proc/self/fd/{_dataDirectory.DangerousGetHandle().ToInt64()}/{DatabaseFileName}";

    public static LinuxSecureDatabaseFile Open(string runtimeBaseDirectory)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("The SQLCipher filesystem guard requires Linux.");
        }

        var runtimeDirectory = OpenAbsoluteDirectoryPath(Path.GetFullPath(runtimeBaseDirectory));
        SafeFileHandle? dataDirectory = null;
        try
        {
            dataDirectory = OpenOrCreateDataDirectory(runtimeDirectory);
            ValidateOwnedDirectory(dataDirectory);
            return new LinuxSecureDatabaseFile(runtimeDirectory, dataDirectory);
        }
        catch
        {
            dataDirectory?.Dispose();
            runtimeDirectory.Dispose();
            throw;
        }
    }

    public bool TryOpenExistingDatabase()
    {
        ObjectDisposedException.ThrowIf(_databaseFile is not null, this);
        var descriptor = NativeMethods.OpenAt(
            _dataDirectory,
            DatabaseFileName,
            OpenReadWrite | OpenCloseOnExec | OpenNoFollow | OpenNonBlocking,
            0);
        if (descriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == ErrnoNoEntry)
            {
                return false;
            }

            throw CreateOpenException(error, "The SQLCipher database path could not be opened securely.");
        }

        _databaseFile = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        try
        {
            _databaseIdentity = ReadStat(_databaseFile);
            ValidateOwnedRegularDatabase(_databaseIdentity, requirePrivatePermissions: false);
            return true;
        }
        catch
        {
            _databaseFile.Dispose();
            _databaseFile = null;
            throw;
        }
    }

    public void CreateDatabase()
    {
        ObjectDisposedException.ThrowIf(_databaseFile is not null, this);
        var descriptor = NativeMethods.OpenAt(
            _dataDirectory,
            DatabaseFileName,
            OpenReadWrite | OpenCreate | OpenExclusive | OpenCloseOnExec | OpenNoFollow | OpenNonBlocking,
            PrivateFilePermissions);
        if (descriptor < 0)
        {
            throw CreateOpenException(
                Marshal.GetLastPInvokeError(),
                "The SQLCipher database placeholder could not be created securely.");
        }

        _databaseFile = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        try
        {
            _databaseIdentity = ReadStat(_databaseFile);
            ValidateOwnedRegularDatabase(_databaseIdentity, requirePrivatePermissions: true);
        }
        catch
        {
            _databaseFile.Dispose();
            _databaseFile = null;
            throw;
        }
    }

    public void VerifyDatabasePathUnchanged()
    {
        ObjectDisposedException.ThrowIf(_databaseFile is null, this);
        var descriptor = NativeMethods.OpenAt(
            _dataDirectory,
            DatabaseFileName,
            OpenReadWrite | OpenCloseOnExec | OpenNoFollow | OpenNonBlocking,
            0);
        if (descriptor < 0)
        {
            throw CreateOpenException(
                Marshal.GetLastPInvokeError(),
                "The SQLCipher database path changed while it was being opened.");
        }

        using var currentFile = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        var currentIdentity = ReadStat(currentFile);
        ValidateOwnedRegularDatabase(currentIdentity, requirePrivatePermissions: false);
        if (currentIdentity.Inode != _databaseIdentity.Inode
            || currentIdentity.DeviceMajor != _databaseIdentity.DeviceMajor
            || currentIdentity.DeviceMinor != _databaseIdentity.DeviceMinor)
        {
            throw new UnauthorizedAccessException("The SQLCipher database path changed while it was being opened.");
        }
    }

    public void Dispose()
    {
        _databaseFile?.Dispose();
        _dataDirectory.Dispose();
        _runtimeDirectory.Dispose();
    }

    private static SafeFileHandle OpenAbsoluteDirectoryPath(string absolutePath)
    {
        if (!Path.IsPathFullyQualified(absolutePath))
        {
            throw new ArgumentException("The SQLCipher runtime directory must be absolute.", nameof(absolutePath));
        }

        var current = OpenDirectory("/");
        try
        {
            foreach (var component in absolutePath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                var next = OpenDirectoryAt(current, component, "The SQLCipher runtime directory path must not contain symbolic links.");
                current.Dispose();
                current = next;
            }

            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenOrCreateDataDirectory(SafeFileHandle runtimeDirectory)
    {
        var descriptor = NativeMethods.OpenAt(
            runtimeDirectory,
            "data",
            OpenPath | OpenDirectoryFlag | OpenCloseOnExec | OpenNoFollow,
            0);
        if (descriptor >= 0)
        {
            return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        }

        var error = Marshal.GetLastPInvokeError();
        if (error != ErrnoNoEntry)
        {
            throw CreateOpenException(error, "The SQLCipher data directory must not be a symbolic link.");
        }

        if (NativeMethods.MakeDirectoryAt(runtimeDirectory, "data", 0x1c0) != 0)
        {
            error = Marshal.GetLastPInvokeError();
            if (error != ErrnoAlreadyExists)
            {
                throw CreateOpenException(error, "The SQLCipher data directory could not be created securely.");
            }
        }

        return OpenDirectoryAt(
            runtimeDirectory,
            "data",
            "The SQLCipher data directory must not be a symbolic link.");
    }

    private static SafeFileHandle OpenDirectory(string path)
    {
        var descriptor = NativeMethods.Open(
            path,
            OpenPath | OpenDirectoryFlag | OpenCloseOnExec | OpenNoFollow,
            0);
        if (descriptor < 0)
        {
            throw CreateOpenException(Marshal.GetLastPInvokeError(), "The SQLCipher runtime directory could not be opened securely.");
        }

        return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
    }

    private static SafeFileHandle OpenDirectoryAt(SafeFileHandle parent, string name, string message)
    {
        var descriptor = NativeMethods.OpenAt(
            parent,
            name,
            OpenPath | OpenDirectoryFlag | OpenCloseOnExec | OpenNoFollow,
            0);
        if (descriptor < 0)
        {
            throw CreateOpenException(Marshal.GetLastPInvokeError(), message);
        }

        return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
    }

    private static void ValidateOwnedDirectory(SafeFileHandle directory)
    {
        var stat = ReadStat(directory);
        if ((stat.Mode & FileTypeMask) != FileTypeDirectory)
        {
            throw new UnauthorizedAccessException("The SQLCipher data path must be a directory.");
        }

        if (stat.UserId != NativeMethods.GetEffectiveUserId())
        {
            throw new UnauthorizedAccessException("The SQLCipher data directory must be owned by the current user.");
        }

        if ((stat.Mode & (GroupWrite | OtherWrite)) != 0)
        {
            throw new UnauthorizedAccessException("The SQLCipher data directory must not be writable by group or other users.");
        }
    }

    private static void ValidateOwnedRegularDatabase(LinuxStatx stat, bool requirePrivatePermissions)
    {
        if ((stat.Mode & FileTypeMask) != FileTypeRegular || stat.LinkCount != 1)
        {
            throw new UnauthorizedAccessException("The SQLCipher database path must be a single-link regular file.");
        }

        if (stat.UserId != NativeMethods.GetEffectiveUserId())
        {
            throw new UnauthorizedAccessException("The SQLCipher database must be owned by the current user.");
        }

        if ((stat.Mode & GroupAndOtherPermissions) != 0)
        {
            throw new UnauthorizedAccessException("The SQLCipher database must not grant permissions to group or other users.");
        }

        if (requirePrivatePermissions && (stat.Mode & 0x1ff) != PrivateFilePermissions)
        {
            throw new UnauthorizedAccessException("A new SQLCipher database must have mode 0600.");
        }
    }

    private static LinuxStatx ReadStat(SafeFileHandle handle)
    {
        if (NativeMethods.Statx(handle, string.Empty, AtEmptyPath, StatxBasicStats, out var stat) != 0)
        {
            throw new IOException(
                "The SQLCipher filesystem entry could not be inspected securely.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        if ((stat.Mask & StatxRequiredIdentity) != StatxRequiredIdentity)
        {
            throw new IOException("The SQLCipher filesystem did not return a complete file identity.");
        }

        return stat;
    }

    private static Exception CreateOpenException(int error, string message)
        => error is ErrnoAccessDenied
            or ErrnoAlreadyExists
            or ErrnoInvalidCrossDeviceLink
            or ErrnoIsDirectory
            or ErrnoLoop
            or ErrnoNotDirectory
            or ErrnoOperationNotPermitted
            ? new UnauthorizedAccessException(message, new Win32Exception(error))
            : new IOException(message, new Win32Exception(error));

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxStatxTimestamp
    {
        internal long Seconds;
        internal uint Nanoseconds;
        internal int Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxStatx
    {
        internal uint Mask;
        internal uint BlockSize;
        internal ulong Attributes;
        internal uint LinkCount;
        internal uint UserId;
        internal uint GroupId;
        internal ushort Mode;
        internal ushort Padding;
        internal ulong Inode;
        internal ulong Size;
        internal ulong Blocks;
        internal ulong AttributesMask;
        internal LinuxStatxTimestamp BirthTime;
        internal LinuxStatxTimestamp ChangeTime;
        internal LinuxStatxTimestamp ModificationTime;
        internal LinuxStatxTimestamp AccessTime;
        internal uint DeviceIdMajor;
        internal uint DeviceIdMinor;
        internal uint DeviceMajor;
        internal uint DeviceMinor;
        internal ulong MountId;
        internal uint DirectIoMemoryAlignment;
        internal uint DirectIoOffsetAlignment;
        internal ulong Spare0;
        internal ulong Spare1;
        internal ulong Spare2;
        internal ulong Spare3;
        internal ulong Spare4;
        internal ulong Spare5;
        internal ulong Spare6;
        internal ulong Spare7;
        internal ulong Spare8;
        internal ulong Spare9;
        internal ulong Spare10;
        internal ulong Spare11;
    }

    private static class NativeMethods
    {
        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        internal static extern int Open(string path, int flags, uint mode);

        [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
        internal static extern int OpenAt(SafeFileHandle directory, string path, int flags, uint mode);

        [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true)]
        internal static extern int MakeDirectoryAt(SafeFileHandle directory, string path, uint mode);

        [DllImport("libc", EntryPoint = "geteuid")]
        internal static extern uint GetEffectiveUserId();

        [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
        internal static extern int Statx(
            SafeFileHandle directory,
            string path,
            int flags,
            uint mask,
            out LinuxStatx stat);
    }
}
