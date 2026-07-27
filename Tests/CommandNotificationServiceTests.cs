using MAAUnified.Platform;

namespace MAAUnified.Tests;

public sealed class CommandNotificationServiceTests
{
    private static readonly PlatformCapabilityStatus WindowsFallbackCapability = new(
        Supported: true,
        Message: "Windows notification fallback uses in-app notifications.",
        Provider: "in-app",
        HasFallback: true,
        FallbackMode: "in-app");

    [Fact]
    public async Task WindowsInAppFallback_ReportsBackendUnavailable_AndStillNotifiesInApp()
    {
        var service = new CommandNotificationService(WindowsFallbackCapability, useInAppOnly: true);
        InAppNotificationRequestedEventArgs? fallback = null;
        service.InAppNotificationRequested += (_, args) => fallback = args;

        var availability = service.GetAvailability();
        var result = await service.NotifyAsync("title", "message");

        Assert.Same(WindowsFallbackCapability, service.Capability);
        Assert.False(availability.IsAvailable);
        Assert.Equal(NotificationAvailabilityReason.BackendUnavailable, availability.Reason);
        Assert.Equal(WindowsFallbackCapability.Message, availability.Detail);
        Assert.True(result.Success);
        Assert.True(result.UsedFallback);
        Assert.Equal(PlatformErrorCodes.NotificationFallback, result.ErrorCode);
        Assert.Equal("title", fallback?.Notification.Title);
        Assert.Equal("message", fallback?.Notification.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CommandBackends_PreserveCapabilityBasedAvailability(bool supported)
    {
        var capability = new PlatformCapabilityStatus(
            Supported: supported,
            Message: "command backend status",
            Provider: "command",
            HasFallback: true,
            FallbackMode: "in-app");
        var service = new CommandNotificationService(capability, useInAppOnly: false);

        var availability = service.GetAvailability();

        Assert.Equal(supported, availability.IsAvailable);
        Assert.Equal(capability.Message, availability.Detail);
        Assert.Equal(NotificationAvailabilityReason.None, availability.Reason);
    }
}
