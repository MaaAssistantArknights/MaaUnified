using System.Text.Json;
using System.Text.Json.Nodes;
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
    private const int MaxDeserializationRepairAttempts = 20;
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

    public ConfigLoadRepair? LastLoadRepair { get; private set; }

    public bool Exists() => File.Exists(ConfigPath);

    public async Task<UnifiedConfig?> LoadAsync(CancellationToken cancellationToken = default)
    {
        LastLoadRepair = null;
        if (!Exists())
        {
            return null;
        }

        string json;
        await using (var stream = new FileStream(
                         ConfigPath,
                         FileMode.Open,
                         FileAccess.Read,
                         SharedConfigReadShare,
                         bufferSize: 4096,
                         FileOptions.Asynchronous))
        using (var reader = new StreamReader(stream))
        {
            json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            // Never rewrite defaults over an unreadable file without keeping a copy of it.
            LastLoadRepair = new ConfigLoadRepair([], PreserveUnreadableConfig());
            throw new JsonException("config/avalonia.json is not valid JSON.", ex);
        }

        if (node is null)
        {
            return null;
        }

        if (node is not JsonObject root)
        {
            LastLoadRepair = new ConfigLoadRepair([], PreserveUnreadableConfig());
            throw new JsonException("The configuration JSON root must be an object.");
        }

        return DeserializeWithRepair(root);
    }

    /// <summary>
    /// Deserializes the document, deleting the node named by each recoverable
    /// <see cref="JsonException.Path"/> and retrying (bounded).
    /// </summary>
    private UnifiedConfig? DeserializeWithRepair(JsonObject root)
    {
        var removedPaths = new List<string>();
        JsonException? lastError = null;
        for (var attempt = 0; attempt <= MaxDeserializationRepairAttempts; attempt++)
        {
            try
            {
                var config = root.Deserialize<UnifiedConfig>(_serializerOptions);
                if (removedPaths.Count > 0)
                {
                    LastLoadRepair = new ConfigLoadRepair(removedPaths.ToArray(), null);
                }

                return config;
            }
            catch (JsonException ex)
            {
                lastError = ex;
                if (attempt == MaxDeserializationRepairAttempts)
                {
                    break;
                }

                if (!ConfigJsonRepair.TryRemoveNode(root, ex.Path))
                {
                    break;
                }

                removedPaths.Add(ex.Path!);
            }
        }

        LastLoadRepair = new ConfigLoadRepair(removedPaths.ToArray(), PreserveUnreadableConfig());
        throw new JsonException(
            "Failed to deserialize config/avalonia.json after removing unreadable values.",
            lastError);
    }

    /// <summary>
    /// Copies the unreadable configuration next to it so the user can still recover it manually.
    /// </summary>
    private string? PreserveUnreadableConfig()
    {
        try
        {
            if (!Exists())
            {
                return null;
            }

            EnsureSafeBackupDirectory();
            var backupPath = ResolveBackupPath(
                $".broken.{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.{Guid.NewGuid():N}");
            File.Copy(ConfigPath, backupPath, overwrite: false);
            return backupPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
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
