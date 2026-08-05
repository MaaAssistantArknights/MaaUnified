using System.Text.Json;
using MAAUnified.Application.Models;
using MAAUnified.Application.Security;
using MAAUnified.Application.Services;

namespace MAAUnified.Application.Configuration;

public sealed class AvaloniaJsonConfigStore : IUnifiedConfigStore
{
    private static readonly JsonSerializerOptions _serializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };
    private const FileShare SharedConfigReadShare = FileShare.ReadWrite | FileShare.Delete;
    private const int MaxBackupPublishAttempts = 16;
    private const UnixFileMode PrivateUnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public AvaloniaJsonConfigStore(string baseDirectory)
    {
        BaseDirectory = baseDirectory;
        ConfigDirectory = Path.Combine(baseDirectory, "config");
        ConfigPath = Path.Combine(ConfigDirectory, "avalonia.json");
    }

    public string BaseDirectory { get; }

    public string ConfigDirectory { get; }

    public string ConfigPath { get; }

    public bool Exists() => File.Exists(ConfigPath);

    public async Task<UnifiedConfig?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!Exists())
        {
            return null;
        }

        await using var stream = new FileStream(
            ConfigPath,
            FileMode.Open,
            FileAccess.Read,
            SharedConfigReadShare,
            bufferSize: 4096,
            FileOptions.Asynchronous);
        return await JsonSerializer.DeserializeAsync<UnifiedConfig>(stream, _serializerOptions, cancellationToken);
    }

    public async Task SaveAsync(UnifiedConfig config, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(ConfigDirectory);
        var tempPath = Path.Combine(
            ConfigDirectory,
            $"{Path.GetFileName(ConfigPath)}.tmp.{Environment.ProcessId}.{Guid.NewGuid():N}");

        try
        {
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, config, _serializerOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            if (Exists())
            {
                ReplaceExistingConfig(tempPath);
            }
            else
            {
                File.Move(tempPath, ConfigPath);
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

    public Task BackupAsync(string suffix, CancellationToken cancellationToken = default)
        => Task.FromException(new NotSupportedException(
            "Raw config backups are disabled. Use BackupSanitizedAsync with a structured config."));

    public async Task BackupSanitizedAsync(
        UnifiedConfig config,
        string suffix,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        var sanitizedConfig = CloneAndRedact(config);
        Directory.CreateDirectory(ConfigDirectory);
        EnsureSafeBackupDirectory();
        var backupPath = ResolveBackupPath(suffix);
        var tempPath = Path.Combine(
            ConfigDirectory,
            $".{Path.GetFileName(backupPath)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = CreatePrivateBackupTempFile(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, sanitizedConfig, _serializerOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(tempPath, PrivateUnixFileMode);
            }

            PublishBackupWithoutOverwrite(tempPath, backupPath);
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

    private static UnifiedConfig CloneAndRedact(UnifiedConfig config)
    {
        var clone = JsonSerializer.Deserialize<UnifiedConfig>(
            JsonSerializer.Serialize(config, _serializerOptions),
            _serializerOptions) ?? throw new InvalidOperationException("Could not clone config for sanitized backup.");
        _ = SensitiveSettingKeyRegistry.ExtractAndRemove(
            clone,
            string.IsNullOrWhiteSpace(clone.SensitiveStorage.StoreId) ? "redacted" : clone.SensitiveStorage.StoreId,
            Math.Max(1, clone.SensitiveStorage.Revision));
        return clone;
    }

    private string ResolveBackupPath(string suffix)
    {
        ArgumentException.ThrowIfNullOrEmpty(suffix);
        if (suffix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || suffix.Contains(Path.DirectorySeparatorChar)
            || suffix.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException("The config backup suffix must be a file-name suffix.", nameof(suffix));
        }

        var configDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ConfigDirectory));
        var backupPath = Path.GetFullPath(ConfigPath + suffix);
        var backupDirectory = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(backupPath)!);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(configDirectory, backupDirectory, comparison))
        {
            throw new ArgumentException("The config backup must remain inside the config directory.", nameof(suffix));
        }

        return backupPath;
    }

    private void EnsureSafeBackupDirectory()
    {
        if (!DiagnosticFileSecurity.IsSafeDirectoryRoot(ConfigDirectory))
        {
            throw new IOException("The config backup directory is not a regular directory.");
        }
    }

    private static FileStream CreatePrivateBackupTempFile(string tempPath)
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
            UnixCreateMode = PrivateUnixFileMode,
        });
    }

    private void PublishBackupWithoutOverwrite(string tempPath, string requestedBackupPath)
    {
        IOException? lastCollision = null;
        for (var attempt = 0; attempt < MaxBackupPublishAttempts; attempt++)
        {
            EnsureSafeBackupDirectory();
            var candidatePath = attempt == 0
                ? requestedBackupPath
                : $"{requestedBackupPath}.{Guid.NewGuid():N}";
            try
            {
                File.Move(tempPath, candidatePath);
                return;
            }
            catch (IOException ex)
            {
                lastCollision = ex;
            }
        }

        throw new IOException("Could not publish a unique config backup.", lastCollision);
    }

    private void ReplaceExistingConfig(string tempPath)
    {
        try
        {
            File.Replace(tempPath, ConfigPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        catch (PlatformNotSupportedException)
        {
            File.Move(tempPath, ConfigPath, overwrite: true);
        }
        catch (UnauthorizedAccessException) when (!OperatingSystem.IsWindows())
        {
            File.Move(tempPath, ConfigPath, overwrite: true);
        }
    }
}
