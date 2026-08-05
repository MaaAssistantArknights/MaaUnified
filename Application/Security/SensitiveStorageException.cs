namespace MAAUnified.Application.Security;

public enum SensitiveStorageErrorCode
{
    Unknown = 0,
    StructurallyUnavailable = 1,
    Unavailable = 2,
    PermissionDenied = 3,
    UserCanceled = 4,
    Corrupt = 5,
    InvalidDocument = 6,
    RevisionNotFound = 7,
    PersistenceVerificationFailed = 8,
    ActionRequired = 9,
}

public sealed class SensitiveStorageException : Exception
{
    public SensitiveStorageException(SensitiveStorageErrorCode errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public SensitiveStorageException(SensitiveStorageErrorCode errorCode, string message, Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    public SensitiveStorageErrorCode ErrorCode { get; }
}
