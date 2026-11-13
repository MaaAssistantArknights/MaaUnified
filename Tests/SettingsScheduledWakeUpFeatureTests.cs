using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Notifications;
using MAAUnified.Platform;

namespace MAAUnified.Tests;

public sealed class SettingsScheduledWakeUpFeatureTests
{
    [Fact]
    public async Task WindowsRegistrar_RegisterAndRemove_ShouldRebuildTask()
    {
        var client = new RecordingScheduledTaskClient();
        var registrar = new WindowsScheduledWakeUpRegistrar(client, "/opt/maa/MAAUnified");
        var plan = new ScheduledWakeUpPlan(
            Enabled: true,
            Slots: [new ScheduledWakeUpSlot(1, 7, 0), new ScheduledWakeUpSlot(2, 23, 58)],
            ExecutablePath: "/opt/maa/MAAUnified",
            Arguments: "--skip-startup-auto-run",
            WorkingDirectory: "/opt/maa");

        var registered = await registrar.SyncAsync(plan);

        Assert.True(registered.Success, registered.Message);
        Assert.Equal(1, client.DeleteCount);
        var created = Assert.Single(client.Created);
        Assert.Equal(ScheduledWakeUpTaskIdentity.BuildTaskName("/opt/maa/MAAUnified"), created.TaskName);
        Assert.Contains("<WakeToRun>true</WakeToRun>", created.Xml, StringComparison.Ordinal);
        Assert.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>", created.Xml, StringComparison.Ordinal);
        Assert.Contains("<StartBoundary>2000-01-01T06:55:00</StartBoundary>", created.Xml, StringComparison.Ordinal);
        Assert.Contains("<StartBoundary>2000-01-01T23:53:00</StartBoundary>", created.Xml, StringComparison.Ordinal);
        Assert.Contains("<Command>/opt/maa/MAAUnified</Command>", created.Xml, StringComparison.Ordinal);
        Assert.Contains("<Arguments>--skip-startup-auto-run</Arguments>", created.Xml, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(created.Xml, "<CalendarTrigger>"));

        var removed = await registrar.SyncAsync(plan with { Enabled = false });

        Assert.True(removed.Success, removed.Message);
        Assert.Equal(2, client.DeleteCount);
        Assert.Single(client.Created);
    }

    [Fact]
    public async Task WindowsRegistrar_EmptySlots_ShouldRemoveWithoutCreate()
    {
        var client = new RecordingScheduledTaskClient();
        var registrar = new WindowsScheduledWakeUpRegistrar(client, "/opt/maa/MAAUnified");

        var result = await registrar.SyncAsync(new ScheduledWakeUpPlan(
            Enabled: true,
            Slots: [],
            ExecutablePath: "/opt/maa/MAAUnified",
            Arguments: string.Empty,
            WorkingDirectory: "/opt/maa"));

        Assert.True(result.Success, result.Message);
        Assert.Equal(1, client.DeleteCount);
        Assert.Empty(client.Created);
    }

    [Fact]
    public async Task WindowsRegistrar_CreateFailure_ShouldSurfaceRegisterError()
    {
        var client = new RecordingScheduledTaskClient { FailCreate = true };
        var registrar = new WindowsScheduledWakeUpRegistrar(client, "/opt/maa/MAAUnified");

        var result = await registrar.SyncAsync(new ScheduledWakeUpPlan(
            Enabled: true,
            Slots: [new ScheduledWakeUpSlot(1, 7, 0)],
            ExecutablePath: "/opt/maa/MAAUnified",
            Arguments: string.Empty,
            WorkingDirectory: "/opt/maa"));

        Assert.False(result.Success);
        Assert.Equal(PlatformErrorCodes.ScheduledWakeUpRegisterFailed, result.ErrorCode);
    }

    [Fact]
    public async Task WindowsRegistrar_DeleteFailure_ShouldStillOverwriteWhenEnabled()
    {
        var client = new RecordingScheduledTaskClient { FailDelete = true };
        var registrar = new WindowsScheduledWakeUpRegistrar(client, "/opt/maa/MAAUnified");

        var registered = await registrar.SyncAsync(new ScheduledWakeUpPlan(
            Enabled: true,
            Slots: [new ScheduledWakeUpSlot(1, 7, 0)],
            ExecutablePath: "/opt/maa/MAAUnified",
            Arguments: string.Empty,
            WorkingDirectory: "/opt/maa"));

        Assert.True(registered.Success, registered.Message);
        Assert.Single(client.Created);
        Assert.Contains("removal reported", registered.Message, StringComparison.Ordinal);

        var removed = await registrar.SyncAsync(ScheduledWakeUpPlan.Disabled);

        Assert.False(removed.Success);
        Assert.Equal(PlatformErrorCodes.ScheduledWakeUpRemoveFailed, removed.ErrorCode);
    }

    [Fact]
    public void TaskIdentity_ShouldBeStableAndPathScoped()
    {
        var first = ScheduledWakeUpTaskIdentity.BuildTaskName("/opt/maa/MAAUnified");
        var second = ScheduledWakeUpTaskIdentity.BuildTaskName("/opt/maa/MAAUnified");
        var other = ScheduledWakeUpTaskIdentity.BuildTaskName("/home/other/MAAUnified");

        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
        Assert.StartsWith(ScheduledWakeUpTaskIdentity.TaskNamePrefix, first, StringComparison.Ordinal);
        Assert.DoesNotContain(' ', first);
    }

    [Fact]
    public async Task UnsupportedRegistrar_ShouldDeclareCapabilityAndRejectSync()
    {
        var registrar = new UnsupportedScheduledWakeUpRegistrar();
        using var coordinator = new ScheduledWakeUpCoordinator(registrar);

        Assert.False(coordinator.Capability.Supported);
        var result = await coordinator.SyncNowAsync(ScheduledWakeUpPlan.Disabled);

        Assert.False(result.Success);
        Assert.Equal(PlatformErrorCodes.ScheduledWakeUpUnsupported, result.ErrorCode);
    }

    [Fact]
    public async Task Coordinator_Debounce_ShouldCollapseRequestsAndUseLatestPlan()
    {
        var registrar = new RecordingScheduledWakeUpRegistrar();
        using var coordinator = new ScheduledWakeUpCoordinator(registrar, TimeSpan.FromMilliseconds(50));
        var first = CreatePlan(1, 7, 0);
        var latest = new ScheduledWakeUpPlan(
            Enabled: true,
            Slots: [new ScheduledWakeUpSlot(1, 7, 0), new ScheduledWakeUpSlot(2, 9, 30)],
            ExecutablePath: "/opt/maa/MAAUnified",
            Arguments: "--skip-startup-auto-run",
            WorkingDirectory: "/opt/maa");

        coordinator.RequestSync(first);
        coordinator.RequestSync(latest);
        await coordinator.PendingSync.WaitAsync(TimeSpan.FromSeconds(2));

        var call = Assert.Single(registrar.Calls);
        Assert.Equal(2, call.Slots.Count);
        Assert.Equal(9, call.Slots[1].Hour);
    }

    [Fact]
    public async Task Coordinator_SyncNow_ShouldReportFailureToSubscribers()
    {
        var registrar = new RecordingScheduledWakeUpRegistrar
        {
            Result = PlatformOperation.Failed(
            "test",
            "boom",
            PlatformErrorCodes.ScheduledWakeUpRegisterFailed,
            "scheduledWakeUp.sync")
        };
        using var coordinator = new ScheduledWakeUpCoordinator(registrar, TimeSpan.FromMilliseconds(10));
        var reported = new TaskCompletionSource<ScheduledWakeUpSyncResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.SyncCompleted += (_, result) => reported.TrySetResult(result);

        coordinator.RequestSync(CreatePlan(1, 7, 0), userInitiated: true);

        var eventResult = await reported.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(eventResult.Result.Success);
        Assert.True(eventResult.UserInitiated);
        Assert.Contains("boom", eventResult.Result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Planner_SelectDue_ShouldMatchLeadAndWrapMidnight()
    {
        var slots = new[]
        {
            new ScheduledWakeUpSlot(1, 10, 0),
            new ScheduledWakeUpSlot(2, 0, 3),
            new ScheduledWakeUpSlot(3, 9, 59),
        };

        var regular = ScheduledStartNotificationPlanner.SelectDue(
            new DateTimeOffset(2026, 1, 1, 9, 55, 0, TimeSpan.FromHours(8)),
            5,
            slots);
        Assert.Equal([1], regular.Select(static slot => slot.Index));

        // 23:58 + 5 minutes is 00:03 on the next day.
        var wrapped = ScheduledStartNotificationPlanner.SelectDue(
            new DateTimeOffset(2026, 1, 1, 23, 58, 0, TimeSpan.FromHours(8)),
            5,
            slots);
        Assert.Equal([2], wrapped.Select(static slot => slot.Index));

        var none = ScheduledStartNotificationPlanner.SelectDue(
            new DateTimeOffset(2026, 1, 1, 9, 50, 0, TimeSpan.FromHours(8)),
            5,
            slots);
        Assert.Empty(none);
    }

    [Fact]
    public void Planner_LeadMinutes_ShouldClampToWpfRange()
    {
        Assert.Equal(1, ScheduledStartNotificationPlanner.ClampLeadMinutes(0));
        Assert.Equal(1, ScheduledStartNotificationPlanner.ClampLeadMinutes(-10));
        Assert.Equal(1439, ScheduledStartNotificationPlanner.ClampLeadMinutes(1440));
        Assert.Equal(1439, ScheduledStartNotificationPlanner.ClampLeadMinutes(5000));
        Assert.Equal(5, ScheduledStartNotificationPlanner.ClampLeadMinutes(5));
    }

    private static ScheduledWakeUpPlan CreatePlan(int index, int hour, int minute)
    {
        return new ScheduledWakeUpPlan(
            Enabled: true,
            Slots: [new ScheduledWakeUpSlot(index, hour, minute)],
            ExecutablePath: "/opt/maa/MAAUnified",
            Arguments: "--skip-startup-auto-run",
            WorkingDirectory: "/opt/maa");
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private sealed class RecordingScheduledTaskClient : IScheduledTaskClient
    {
        public List<(string TaskName, string Xml)> Created { get; } = [];

        public int DeleteCount { get; private set; }

        public bool FailCreate { get; init; }

        public bool FailDelete { get; init; }

        public Task<ScheduledTaskCommandResult> CreateAsync(
            string taskName,
            string taskXml,
            CancellationToken cancellationToken = default)
        {
            if (FailCreate)
            {
                return Task.FromResult(new ScheduledTaskCommandResult(false, false, "create failed"));
            }

            Created.Add((taskName, taskXml));
            return Task.FromResult(new ScheduledTaskCommandResult(true, false, "created"));
        }

        public Task<ScheduledTaskCommandResult> DeleteAsync(
            string taskName,
            CancellationToken cancellationToken = default)
        {
            DeleteCount++;
            return Task.FromResult(FailDelete
                ? new ScheduledTaskCommandResult(false, false, "delete failed")
                : new ScheduledTaskCommandResult(true, false, "deleted"));
        }
    }

    private sealed class RecordingScheduledWakeUpRegistrar : IScheduledWakeUpRegistrar
    {
        public PlatformCapabilityStatus Capability { get; } = new(
            Supported: true,
            Message: "test registrar",
            Provider: "test");

        public List<ScheduledWakeUpPlan> Calls { get; } = [];

        public PlatformOperationResult Result { get; init; } = PlatformOperation.NativeSuccess(
            "test",
            "synced",
            "scheduledWakeUp.sync");

        public Task<PlatformOperationResult> SyncAsync(
            ScheduledWakeUpPlan plan,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(plan);
            return Task.FromResult(Result);
        }
    }
}
