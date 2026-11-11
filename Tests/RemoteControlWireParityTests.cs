using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Security;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using MAAUnified.Application.Services.RemoteControl;
using MAAUnified.Compat.Constants;

namespace MAAUnified.Tests;

public sealed class RemoteControlWireParityTests
{
    private static RemoteControlPollingSnapshot Snapshot(
        Uri? getTask,
        Uri? report,
        int pollIntervalMs = 1000)
        => new(getTask, report, "alice", "pc-1", pollIntervalMs);

    [Fact]
    public async Task FetchTasks_UsesLegacyPostWithUserAndDeviceBody()
    {
        var getTaskUri = new Uri("http://127.0.0.1:1/get");
        HttpRequestMessage? captured = null;
        string? capturedBody = null;
        await using var service = new RemoteControlFeatureService(
            null, null, null, null, null, null, null,
            probeAsync: null,
            sendHttpAsync: async (request, cancellationToken) =>
            {
                captured = request;
                capturedBody = request.Content is null
                    ? null
                    : await request.Content.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"tasks":[]}"""),
                };
            });

        var requests = await service.FetchTasksAsync(Snapshot(getTaskUri, report: null), CancellationToken.None);

        Assert.Empty(requests);
        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Equal(getTaskUri, captured.RequestUri);
        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.Equal("alice", doc.RootElement.GetProperty("user").GetString());
        Assert.Equal("pc-1", doc.RootElement.GetProperty("device").GetString());
    }

    [Fact]
    public async Task FetchTasks_ExecutesEveryTaskInBatchButEachIdOnlyOnce()
    {
        var getTaskUri = new Uri("http://127.0.0.1:1/get");
        await using var service = new RemoteControlFeatureService(
            null, null, null, null, null, null, null,
            probeAsync: null,
            sendHttpAsync: static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"tasks":[{"id":"1","type":"HeartBeat"},{"id":"2","type":"StopTask"},{"id":"1","type":"HeartBeat"}]}"""),
            }));

        var first = await service.FetchTasksAsync(Snapshot(getTaskUri, report: null), CancellationToken.None);
        Assert.Equal(new[] { "1", "2" }, first.Select(request => request.TaskId));
        Assert.Equal(new[] { "HeartBeat", "StopTask" }, first.Select(request => request.RawCommand));

        // Same payload delivered again: every id was already seen, so nothing is re-executed.
        var second = await service.FetchTasksAsync(Snapshot(getTaskUri, report: null), CancellationToken.None);
        Assert.Empty(second);
    }

    [Fact]
    public async Task ReportAsync_PostsLegacyStatusTaskPayloadSchema()
    {
        var reportUri = new Uri("http://127.0.0.1:1/report");
        string? capturedBody = null;
        HttpRequestMessage? captured = null;
        await using var service = new RemoteControlFeatureService(
            null, null, null, null, null, null, null,
            probeAsync: null,
            sendHttpAsync: async (request, cancellationToken) =>
            {
                captured = request;
                capturedBody = request.Content is null
                    ? null
                    : await request.Content.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });

        var snapshot = Snapshot(getTask: null, report: reportUri);
        var command = new RemoteControlCommandRequest("HeartBeat", null, "alice", "pc-1", TaskId: "42");
        var success = new RemoteControlCommandResult("HeartBeat", "HeartBeat", Success: true, "ok", LegacyReportPayload: "41");
        await service.ReportAsync(snapshot, command, success, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Equal(reportUri, captured.RequestUri);
        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.Equal("alice", doc.RootElement.GetProperty("user").GetString());
        Assert.Equal("pc-1", doc.RootElement.GetProperty("device").GetString());
        Assert.Equal("SUCCESS", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("42", doc.RootElement.GetProperty("task").GetString());
        Assert.Equal("41", doc.RootElement.GetProperty("payload").GetString());
        Assert.False(doc.RootElement.TryGetProperty("normalizedCommand", out _));
        Assert.False(doc.RootElement.TryGetProperty("success", out _));

        var failed = new RemoteControlCommandResult("StopTask", "StopTask", Success: false, "boom");
        await service.ReportAsync(snapshot, command, failed, CancellationToken.None);
        using var failedDoc = JsonDocument.Parse(capturedBody!);
        Assert.Equal("FAILED", failedDoc.RootElement.GetProperty("status").GetString());
        Assert.Equal(string.Empty, failedDoc.RootElement.GetProperty("payload").GetString());
    }

    [Fact]
    public void Snapshot_IsConfigured_WithGetTaskEndpointOnly()
    {
        var snapshot = Snapshot(new Uri("http://127.0.0.1:1/get"), report: null);
        Assert.True(snapshot.IsConfigured);
    }

    [Fact]
    public async Task ReadSnapshot_UsesWpfDefaultPollInterval_WhenKeyIsMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "maa-unified-rc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var log = new UiLogService();
        var config = new UnifiedConfigurationService(
            new AvaloniaJsonConfigStore(root),
            new GuiNewJsonConfigImporter(),
            new GuiJsonConfigImporter(),
            log,
            root,
            SensitiveSettingsService.CreateEphemeralForTests());
        await config.LoadOrBootstrapAsync();
        var profile = config.CurrentConfig.Profiles[config.CurrentConfig.CurrentProfile];
        profile.Values[ConfigurationKeys.RemoteControlGetTaskEndpointUri] = JsonValue.Create("http://127.0.0.1:1/get");

        await using var service = new RemoteControlFeatureService(config, null, null, null, null, null, null);
        var snapshot = service.ReadSnapshot();

        Assert.Equal(1000, snapshot.PollIntervalMs);
        Assert.True(snapshot.IsConfigured);
    }
}
