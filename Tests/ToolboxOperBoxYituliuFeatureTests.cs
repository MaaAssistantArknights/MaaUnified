using System.Text.Json.Nodes;
using MAAUnified.App.ViewModels.Toolbox;
using MAAUnified.Application.Models;
using MAAUnified.Application.Services.ThirdParty;
using LegacyConfigurationKeys = MAAUnified.Compat.Constants.ConfigurationKeys;

namespace MAAUnified.Tests;

/// <summary>
/// P1-10 (WPF 247db567f3): operator recognition can read progression from the yituliu OpenAPI.
/// Failures must never replace or clear local operator-box data.
/// </summary>
public sealed class ToolboxOperBoxYituliuFeatureTests
{
    private const string LocalOperBoxPayload = """
        {
          "done": true,
          "own_opers": [
            { "id": "char_102_texas", "name": "德克萨斯", "rarity": 5, "elite": 2, "level": 80, "own": true, "potential": 2 }
          ]
        }
        """;

    [Fact]
    public async Task StartOperBoxAsync_WithYituliuEnabled_ShouldFetchAndPersistYituliuData()
    {
        var fake = new StubYituliuOpenApiService(
            YituliuTokenValidationResult.Valid,
            [
                new YituliuOperatorInfo(
                    "char_003_kalts",
                    90,
                    2,
                    7,
                    4,
                    [new YituliuOperatorSkill("sk_kalts_1", 3)],
                    [new YituliuOperatorEquip("uniequip_002", "Y", 2)]),
            ]);
        await using var fixture = await ToolboxTestFixture.CreateAsync(
            profileSeeds: BuildYituliuProfileSeeds(token: "my-token"),
            yituliuOpenApiService: fake);
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);
        await vm.InitializeAsync();

        await vm.StartOperBoxAsync();

        Assert.Equal(1, fake.CallCount);
        Assert.Equal("my-token", fake.LastToken);
        Assert.Empty(fixture.Bridge.AppendedTasks);
        Assert.Equal(0, fixture.Bridge.ConnectCallCount);
        Assert.Equal(ToolboxExecutionState.Succeeded, vm.ExecutionState);
        Assert.Null(fixture.Runtime.SessionService.CurrentRunOwner);
        Assert.True(vm.CanExportOperBox);
        Assert.Contains("char_003_kalts", vm.OperBoxExportText, StringComparison.Ordinal);

        await WaitForSettingAsync(fixture, LegacyConfigurationKeys.OperBoxData, "\"source\":\"yituliu\"");
        var payload = ReadGlobalString(fixture, LegacyConfigurationKeys.OperBoxData);
        Assert.Contains("\"elite\":2", payload, StringComparison.Ordinal);
        Assert.Contains("\"level\":90", payload, StringComparison.Ordinal);
        Assert.Contains("\"mainSkillLevel\":7", payload, StringComparison.Ordinal);
        Assert.Contains("\"skills\":[{\"id\":\"sk_kalts_1\",\"level\":3}]", payload, StringComparison.Ordinal);
        Assert.Contains("\"equips\":[{\"id\":\"uniequip_002\",\"type\":\"Y\",\"level\":2}]", payload, StringComparison.Ordinal);

        // WPF 247db567f3 writes the same payload to DataDir/OperBoxData.json; Copilot's
        // operbox_data_path points at this file.
        Assert.True(File.Exists(vm.OperBoxDataFilePath));
        var filePayload = await File.ReadAllTextAsync(vm.OperBoxDataFilePath);
        Assert.Contains("\"source\":\"yituliu\"", filePayload, StringComparison.Ordinal);
        Assert.Contains("char_003_kalts", filePayload, StringComparison.Ordinal);
        Assert.Equal(Path.Combine(fixture.Root, "data", "OperBoxData.json"), vm.OperBoxDataFilePath);
    }

    [Fact]
    public async Task StartOperBoxAsync_WhenYituliuFails_ShouldKeepLocalDataAndCache()
    {
        var fake = new StubYituliuOpenApiService(
            YituliuTokenValidationResult.NetworkError,
            []);
        await using var fixture = await ToolboxTestFixture.CreateAsync(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [LegacyConfigurationKeys.OperBoxData] = LocalOperBoxPayload,
            },
            BuildYituliuProfileSeeds(token: "my-token"),
            yituliuOpenApiService: fake);
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);
        await vm.InitializeAsync();
        Assert.True(vm.CanExportOperBox);

        await vm.StartOperBoxAsync();

        Assert.Equal(1, fake.CallCount);
        Assert.Empty(fixture.Bridge.AppendedTasks);
        Assert.Equal(ToolboxExecutionState.Failed, vm.ExecutionState);
        Assert.True(vm.CanExportOperBox);
        Assert.Contains("char_102_texas", vm.OperBoxExportText, StringComparison.Ordinal);
        Assert.Equal(UiErrorCode.ToolboxExecutionFailed, vm.LastExecutionErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(vm.OperBoxInfo));

        await Task.Delay(120);
        var payload = ReadGlobalString(fixture, LegacyConfigurationKeys.OperBoxData);
        Assert.Contains("char_102_texas", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("\"source\":\"yituliu\"", payload, StringComparison.Ordinal);
        Assert.False(File.Exists(vm.OperBoxDataFilePath));
    }

    [Fact]
    public async Task StartOperBoxAsync_WhenYituliuReturnsEmpty_ShouldKeepLocalData()
    {
        var fake = new StubYituliuOpenApiService(YituliuTokenValidationResult.Valid, []);
        await using var fixture = await ToolboxTestFixture.CreateAsync(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [LegacyConfigurationKeys.OperBoxData] = LocalOperBoxPayload,
            },
            BuildYituliuProfileSeeds(token: "my-token"),
            yituliuOpenApiService: fake);
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);
        await vm.InitializeAsync();

        await vm.StartOperBoxAsync();

        Assert.Equal(ToolboxExecutionState.Failed, vm.ExecutionState);
        Assert.Contains("char_102_texas", vm.OperBoxExportText, StringComparison.Ordinal);
        await Task.Delay(120);
        var payload = ReadGlobalString(fixture, LegacyConfigurationKeys.OperBoxData);
        Assert.Contains("char_102_texas", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("\"source\":\"yituliu\"", payload, StringComparison.Ordinal);
        Assert.False(File.Exists(vm.OperBoxDataFilePath));
    }

    [Fact]
    public async Task StartOperBoxAsync_WithYituliuEnabledWithoutToken_ShouldNotFetchOrFallBackToCore()
    {
        var fake = new StubYituliuOpenApiService(YituliuTokenValidationResult.Valid, []);
        await using var fixture = await ToolboxTestFixture.CreateAsync(
            profileSeeds: BuildYituliuProfileSeeds(token: string.Empty),
            yituliuOpenApiService: fake);
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);
        await vm.InitializeAsync();

        await vm.StartOperBoxAsync();

        // WPF 247db567f3: an enabled switch without a token stops with a hint instead of silently
        // falling back to emulator recognition.
        Assert.Equal(0, fake.CallCount);
        Assert.Empty(fixture.Bridge.AppendedTasks);
        Assert.Equal(0, fixture.Bridge.ConnectCallCount);
        Assert.False(string.IsNullOrWhiteSpace(vm.OperBoxInfo));
        Assert.Equal(ToolboxExecutionState.Idle, vm.ExecutionState);
    }

    [Fact]
    public async Task StartOperBoxAsync_WithYituliuDisabled_ShouldUseCoreRecognition()
    {
        var fake = new StubYituliuOpenApiService(YituliuTokenValidationResult.Valid, []);
        await using var fixture = await ToolboxTestFixture.CreateAsync(
            profileSeeds: BuildYituliuProfileSeeds(token: "my-token", enabled: false),
            yituliuOpenApiService: fake);
        var vm = new ToolboxPageViewModel(fixture.Runtime, fixture.ConnectionState);
        await vm.InitializeAsync();

        await vm.StartOperBoxAsync();

        Assert.Equal(0, fake.CallCount);
        Assert.Equal("OperBox", Assert.Single(fixture.Bridge.AppendedTasks).Type);
        Assert.Equal(1, fixture.Bridge.ConnectCallCount);
    }

    private static Dictionary<string, JsonNode?> BuildYituliuProfileSeeds(string token, bool enabled = true)
    {
        return new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            [ThirdPartySettingKeys.OperBoxUseYituliuApi] = JsonValue.Create(enabled),
            [ThirdPartySettingKeys.YituliuOpenApiToken] = JsonValue.Create(token),
        };
    }

    private static async Task WaitForSettingAsync(ToolboxTestFixture fixture, string key, string expectedSubstring)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (ReadGlobalString(fixture, key).Contains(expectedSubstring, StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Contains(expectedSubstring, ReadGlobalString(fixture, key), StringComparison.Ordinal);
    }

    private static string ReadGlobalString(ToolboxTestFixture fixture, string key)
    {
        if (!fixture.Config.CurrentConfig.GlobalValues.TryGetValue(key, out var node) || node is null)
        {
            return string.Empty;
        }

        if (node is JsonValue value && value.TryGetValue(out string? text))
        {
            return text ?? string.Empty;
        }

        return node.ToJsonString();
    }

    private sealed class StubYituliuOpenApiService(
        YituliuTokenValidationResult result,
        IReadOnlyList<YituliuOperatorInfo> operators) : IYituliuOpenApiService
    {
        public int CallCount { get; private set; }

        public string? LastToken { get; private set; }

        public Task<YituliuOperatorInfoResult> GetOperatorInfoAsync(
            string token,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastToken = token;
            return Task.FromResult(new YituliuOperatorInfoResult(result, operators, "stub"));
        }
    }
}
