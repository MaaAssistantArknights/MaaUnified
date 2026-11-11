using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Orchestration;
using MAAUnified.Application.Security;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using MAAUnified.CoreBridge;
using Xunit;

namespace MAAUnified.Tests;

public sealed class WebApiHttpContractTests
{
    [Fact]
    public async Task VersionPrefixedRoutes_KeepLegacyTaskContracts()
    {
        var root = Path.Combine(Path.GetTempPath(), "maa-unified-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var port = ReserveLoopbackPort();
        var log = new UiLogService();
        var config = new UnifiedConfigurationService(
            new AvaloniaJsonConfigStore(root),
            new GuiNewJsonConfigImporter(),
            new GuiJsonConfigImporter(),
            log,
            root,
            SensitiveSettingsService.CreateEphemeralForTests());
        await config.LoadOrBootstrapAsync();
        config.CurrentConfig.GlobalValues["Advanced.WebApi.Enabled"] = true;
        config.CurrentConfig.GlobalValues["Advanced.WebApi.Host"] = "127.0.0.1";
        config.CurrentConfig.GlobalValues["Advanced.WebApi.Port"] = port;

        await using var bridge = new MaaCoreBridgeStub();
        Assert.True((await bridge.InitializeAsync(new CoreInitializeRequest(root, "Official"))).Success);
        Assert.True((await bridge.ConnectAsync(new CoreConnectionInfo("127.0.0.1:5555", "General", null))).Success);
        var session = new UnifiedSessionService(bridge, config, log, new SessionStateMachine());
        var service = new WebApiFeatureService(config, session, null, null, bridge);
        try
        {
            Assert.True((await service.StartAsync()).Success);
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };

            var append = await PostAsync(client, "v9.9.9/task/append", new JsonObject
            {
                ["type"] = "Fight",
                ["params"] = new JsonObject { ["stage"] = "1-7" },
            });
            Assert.Equal(10000, append["code"]!.GetValue<int>());
            var taskId = append["data"]!["task_id"]!.GetValue<int>();
            Assert.True(taskId > 0);

            var duplicate = await PostAsync(client, "v9.9.9/task/append", new JsonObject
            {
                ["type"] = "Fight",
                ["params"] = new JsonObject(),
            });
            Assert.Equal(10001, duplicate["code"]!.GetValue<int>());

            var modify = await PostAsync(client, "v9.9.9/task/modify", new JsonObject
            {
                ["task_id"] = taskId,
                ["params"] = new JsonObject { ["stage"] = "CE-6" },
            });
            Assert.Equal(10000, modify["code"]!.GetValue<int>());

            var list = await GetAsync(client, "v9.9.9/task/list");
            Assert.Equal(10000, list["code"]!.GetValue<int>());
            var task = list["data"]!["tasks"]!.AsArray().Single()!.AsObject();
            Assert.Equal(taskId, task["task_id"]!.GetValue<int>());
            Assert.Equal("Fight", task["task_type"]!.GetValue<string>());
            Assert.Contains("CE-6", task["task_params"]!.GetValue<string>(), StringComparison.Ordinal);

            Assert.Equal(10000, (await PostAsync(client, "v9.9.9/task/start", new JsonObject()))["code"]!.GetValue<int>());
            Assert.True((await GetAsync(client, "v9.9.9/task/running"))["data"]!["status"]!.GetValue<bool>());
            Assert.Equal(10000, (await PostAsync(client, "v9.9.9/task/stop", new JsonObject()))["code"]!.GetValue<int>());

            Assert.Equal(10000, (await PostAsync(client, "v9.9.9/destroy", new JsonObject()))["code"]!.GetValue<int>());
            Assert.Equal(10000, (await GetAsync(client, "v9.9.9/version"))["code"]!.GetValue<int>());
        }
        finally
        {
            await service.StopAsync();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Temporary test directory cleanup is best effort.
            }
        }
    }

    private static int ReserveLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<JsonObject> PostAsync(HttpClient client, string path, JsonObject payload)
    {
        using var response = await client.PostAsJsonAsync(path, payload);
        response.EnsureSuccessStatusCode();
        return (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!.AsObject();
    }

    private static async Task<JsonObject> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return (await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync()))!.AsObject();
    }
}
