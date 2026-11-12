using System.Text.Json.Nodes;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Security;
using MAAUnified.Application.Services;
using MAAUnified.Compat.Constants;

namespace MAAUnified.Tests;

/// <summary>
/// P1-12: configuration persistence chain — serialized saves, per-key change events with
/// old/new payloads, sensitive values masked as “updated”, demo mode never writes to disk and
/// the GitHub token is part of the sensitive-setting registry (WPF dev-v2 <c>382009b97b</c>,
/// <c>53870a7a52</c>).
/// </summary>
public sealed class SettingsPersistenceChainTests
{
    [Fact]
    public async Task SaveAsync_ShouldRaisePerKeyChangeEventsWithOldAndNewValues()
    {
        var root = CreateTempRoot();
        var service = CreateService(root);
        await service.LoadOrBootstrapAsync();

        var changes = new List<ConfigValueChange>();
        service.ConfigValueChanged += changes.Add;
        service.CurrentConfig.GlobalValues[ConfigurationKeys.Localization] = JsonValue.Create("en-us");

        await service.SaveAsync();

        var change = Assert.Single(changes, item => item.Path == "GlobalValues.GUI.Localization");
        Assert.False(change.IsSensitive);
        Assert.NotEqual("en-us", change.OldValue);
        Assert.Equal("en-us", change.NewValue);

        var logs = SnapshotLogs(service);
        Assert.Contains(
            "Configuration GlobalValues.GUI.Localization has been set:",
            logs,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveAsync_UnchangedConfig_ShouldNotRaiseChangeEvents()
    {
        var root = CreateTempRoot();
        var service = CreateService(root);
        await service.LoadOrBootstrapAsync();

        var changes = new List<ConfigValueChange>();
        service.ConfigValueChanged += changes.Add;

        await service.SaveAsync();

        Assert.Empty(changes);
    }

    [Fact]
    public async Task SaveAsync_ShouldReportGranularPathsForTaskQueueParameters()
    {
        var root = CreateTempRoot();
        var service = CreateService(root);
        await service.LoadOrBootstrapAsync();

        var changes = new List<ConfigValueChange>();
        service.ConfigValueChanged += changes.Add;
        var profile = service.CurrentConfig.Profiles[service.CurrentConfig.CurrentProfile];
        profile.TaskQueue.Add(new UnifiedTaskItem
        {
            Type = "Fight",
            Name = "Fight",
            Params = new JsonObject { ["stage"] = "1-7" },
        });

        await service.SaveAsync();

        var change = Assert.Single(
            changes,
            item => item.Path.StartsWith("Profiles.Default.TaskQueue[0].", StringComparison.Ordinal)
                    && item.Path.EndsWith("Params.stage", StringComparison.Ordinal));
        Assert.Equal("1-7", change.NewValue);
    }

    [Fact]
    public async Task SaveAsync_ShouldNeverExposeSensitiveValuesInEventsOrLogs()
    {
        const string githubToken = "ghp_parity_secret_token";
        const string mirrorChyanCdk = "mirror-chyan-secret-cdk";
        var root = CreateTempRoot();
        var service = CreateService(root);
        await service.LoadOrBootstrapAsync();

        var changes = new List<ConfigValueChange>();
        service.ConfigValueChanged += changes.Add;
        service.CurrentConfig.GlobalValues[ConfigurationKeys.GitHubToken] = JsonValue.Create(githubToken);
        service.CurrentConfig.GlobalValues[ConfigurationKeys.MirrorChyanCdk] = JsonValue.Create(mirrorChyanCdk);

        await service.SaveAsync();

        var githubChange = Assert.Single(
            changes,
            item => item.Path == $"GlobalValues.{ConfigurationKeys.GitHubToken}");
        Assert.True(githubChange.IsSensitive);
        Assert.Null(githubChange.OldValue);
        Assert.Null(githubChange.NewValue);

        var cdkChange = Assert.Single(
            changes,
            item => item.Path == $"GlobalValues.{ConfigurationKeys.MirrorChyanCdk}");
        Assert.True(cdkChange.IsSensitive);
        Assert.Null(cdkChange.OldValue);
        Assert.Null(cdkChange.NewValue);

        var logs = SnapshotLogs(service);
        Assert.DoesNotContain(githubToken, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(mirrorChyanCdk, logs, StringComparison.Ordinal);
        Assert.Contains(
            $"Configuration GlobalValues.{ConfigurationKeys.GitHubToken} has been updated",
            logs,
            StringComparison.Ordinal);
        Assert.DoesNotContain("has been set", FindLog(logs, ConfigurationKeys.GitHubToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveAsync_AfterSensitiveValueChanges_ShouldDetectTheSecondUpdate()
    {
        var root = CreateTempRoot();
        var service = CreateService(root);
        await service.LoadOrBootstrapAsync();

        var changes = new List<ConfigValueChange>();
        service.ConfigValueChanged += changes.Add;

        service.CurrentConfig.GlobalValues[ConfigurationKeys.GitHubToken] = JsonValue.Create("first-token");
        await service.SaveAsync();
        Assert.Single(changes, item => item.Path == $"GlobalValues.{ConfigurationKeys.GitHubToken}");

        changes.Clear();
        service.CurrentConfig.GlobalValues[ConfigurationKeys.GitHubToken] = JsonValue.Create("second-token");
        await service.SaveAsync();

        var second = Assert.Single(changes, item => item.Path == $"GlobalValues.{ConfigurationKeys.GitHubToken}");
        Assert.True(second.IsSensitive);
        Assert.Null(second.OldValue);
        Assert.Null(second.NewValue);
    }

    [Fact]
    public async Task SaveAsync_InDemoMode_ShouldKeepChangesInMemoryWithoutTouchingDisk()
    {
        var root = CreateTempRoot();
        var configPath = Path.Combine(root, "config", "avalonia.json");
        var log = new UiLogService();
        var service = new UnifiedConfigurationService(
            new AvaloniaJsonConfigStore(root),
            new GuiNewJsonConfigImporter(),
            new GuiJsonConfigImporter(),
            log,
            root,
            SensitiveSettingsService.CreatePersistentFileForTests(root),
            demoMode: true);

        await service.LoadOrBootstrapAsync();
        Assert.True(service.IsDemoMode);
        Assert.False(File.Exists(configPath));

        service.CurrentConfig.GlobalValues[ConfigurationKeys.Localization] = JsonValue.Create("ja-jp");
        await service.SaveAsync();

        Assert.Equal("ja-jp", service.CurrentConfig.GlobalValues[ConfigurationKeys.Localization]?.GetValue<string>());
        Assert.False(File.Exists(configPath));
        Assert.Contains(
            log.Snapshot,
            entry => entry.Message.Contains("Demo mode enabled: skipped persisting", StringComparison.Ordinal));
    }

    [Fact]
    public void SensitiveSettingRegistry_ShouldRegisterGitHubTokenAndMirrorChyanCdkAsGlobalSecrets()
    {
        // WPF dev-v2 53870a7a52 adds GitHubToken next to the existing MirrorChyan CDK credential.
        Assert.True(SensitiveSettingKeyRegistry.TryGet(ConfigurationKeys.GitHubToken, out var githubToken));
        Assert.Equal(SensitiveSettingScope.Global, githubToken.Scope);
        Assert.True(SensitiveSettingKeyRegistry.IsSensitive(ConfigurationKeys.GitHubToken));

        Assert.True(SensitiveSettingKeyRegistry.TryGet(ConfigurationKeys.MirrorChyanCdk, out var mirrorChyan));
        Assert.Equal(SensitiveSettingScope.Global, mirrorChyan.Scope);

        Assert.Equal(2, SensitiveSettingKeyRegistry.Definitions.Count(value => value.Scope == SensitiveSettingScope.Global));
        Assert.Equal(32, SensitiveSettingKeyRegistry.Definitions.Count);
        Assert.Equal(
            32,
            SensitiveSettingKeyRegistry.Definitions.Select(value => value.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task SaveAsync_ConcurrentCalls_ShouldSerializeBehindTheSaveSemaphore()
    {
        var root = CreateTempRoot();
        var service = CreateService(root);
        await service.LoadOrBootstrapAsync();

        service.CurrentConfig.GlobalValues["Tests.Concurrent"] = JsonValue.Create(42);

        var saves = Enumerable.Range(0, 4)
            .Select(_ => Task.Run(() => service.SaveAsync()))
            .ToArray();

        await Task.WhenAll(saves);

        var persisted = Assert.IsType<JsonObject>(
            JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "config", "avalonia.json"))));
        var globalValues = Assert.IsType<JsonObject>(persisted["GlobalValues"]);
        Assert.Equal(42, globalValues["Tests.Concurrent"]?.GetValue<int>());
    }

    private static string SnapshotLogs(UnifiedConfigurationService service)
        => string.Join('\n', service.LogService.Snapshot.Select(entry => entry.Message));

    private static string FindLog(string logs, string fragment)
        => logs.Split('\n').FirstOrDefault(line => line.Contains(fragment, StringComparison.Ordinal)) ?? string.Empty;

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "maa-unified-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        return root;
    }

    private static UnifiedConfigurationService CreateService(string root)
    {
        var log = new UiLogService();
        return new UnifiedConfigurationService(
            new AvaloniaJsonConfigStore(root),
            new GuiNewJsonConfigImporter(),
            new GuiJsonConfigImporter(),
            log,
            root,
            SensitiveSettingsService.CreatePersistentFileForTests(root));
    }
}
