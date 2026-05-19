using System.Text.Json;
using MAAUnified.Application.Models;

namespace MAAUnified.Application.Configuration;

public sealed class AvaloniaJsonConfigStore : IUnifiedConfigStore
{
    private static readonly JsonSerializerOptions _serializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };
    private const FileShare SharedConfigReadShare = FileShare.ReadWrite | FileShare.Delete;

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
    {
        if (!Exists())
        {
            return Task.CompletedTask;
        }

        var backupPath = ConfigPath + suffix;
        File.Copy(ConfigPath, backupPath, true);
        return Task.CompletedTask;
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
