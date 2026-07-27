using System.Collections.Concurrent;
using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.Platform;

namespace MAAUnified.Tests;

public sealed class DesktopNotificationServiceTests
{
    [Fact]
    public async Task NotifyAsync_ForwardsActionsToNativePoster()
    {
        using var poster = new FakeNotificationPoster(isAvailable: true);
        using var service = new DesktopNotificationService(poster);
        var request = new SystemNotificationRequest(
            "title",
            "message",
            [new SystemNotificationAction("Open", "open-url")]);

        var result = await service.NotifyAsync(request);

        Assert.True(result.Success);
        Assert.False(result.UsedFallback);
        Assert.Same(request, poster.LastNotification);
    }

    [Fact]
    public async Task NotifyAsync_WhenWindowsPosterIsUnavailable_RequestsInAppFallback()
    {
        using var poster = new FakeNotificationPoster(isAvailable: false);
        using var service = new DesktopNotificationService(poster);
        InAppNotificationRequestedEventArgs? fallback = null;
        service.InAppNotificationRequested += (_, args) => fallback = args;

        var result = await service.NotifyAsync("title", "message");

        Assert.True(result.Success);
        Assert.True(result.UsedFallback);
        Assert.Equal(PlatformErrorCodes.NotificationFallback, result.ErrorCode);
        Assert.Equal(0, poster.ShowCallCount);
        Assert.NotNull(fallback);
        Assert.Equal("title", fallback.Notification.Title);
        Assert.Equal("message", fallback.Notification.Message);
        Assert.Equal(InAppNotificationSeverity.Information, fallback.Notification.InAppSeverity);
        Assert.Contains("disabled", fallback.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NotifyAsync_WhenSystemNotificationsAreDisabled_RequestsInAppFallback()
    {
        using var poster = new FakeNotificationPoster(isAvailable: true);
        using var service = new DesktopNotificationService(poster);
        InAppNotificationRequestedEventArgs? fallback = null;
        service.InAppNotificationRequested += (_, args) => fallback = args;
        var request = new SystemNotificationRequest("title", "message", [], UseSystemNotification: false);

        var result = await service.NotifyAsync(request);

        Assert.True(result.Success);
        Assert.True(result.UsedFallback);
        Assert.Equal(0, poster.ShowCallCount);
        Assert.Same(request, fallback?.Notification);
        Assert.Equal(InAppNotificationSeverity.Information, fallback?.Notification.InAppSeverity);
    }

    [Fact]
    public async Task NotifyAsync_WhenDiagnosticRequestsError_PreservesSeverityForInAppFallback()
    {
        using var poster = new FakeNotificationPoster(isAvailable: true);
        using var service = new DesktopNotificationService(poster);
        InAppNotificationRequestedEventArgs? fallback = null;
        service.InAppNotificationRequested += (_, args) => fallback = args;
        var request = new SystemNotificationRequest(
            "title",
            "message",
            [],
            UseSystemNotification: false,
            InAppSeverity: InAppNotificationSeverity.Error);

        var result = await service.NotifyAsync(request);

        Assert.True(result.Success);
        Assert.True(result.UsedFallback);
        Assert.Same(request, fallback?.Notification);
        Assert.Equal(InAppNotificationSeverity.Error, fallback?.Notification.InAppSeverity);
    }

    [Fact]
    public void NotificationActionActivation_DecodesSessionTaggedArgument()
    {
        var action = SystemNotificationAction.ForOpenUrl("Open", "https://maa.plus/path?q=value");
        var args = new NotificationActionActivatedEventArgs(System.Net.WebUtility.UrlEncode(action.Tag));

        Assert.Equal(action.Tag, args.Argument);
        Assert.True(SystemNotificationAction.TryGetOpenUrl(args.Argument, out var url));
        Assert.Equal("https://maa.plus/path?q=value", url);
    }

    [Fact]
    public void OpenUrlAction_RoundTripsWithinCurrentSession()
    {
        var action = SystemNotificationAction.ForOpenUrl("Open", "HTTPS://MAA.PLUS/path with space");

        Assert.True(SystemNotificationAction.TryGetOpenUrl(action.Tag, out var url));
        Assert.Equal("https://maa.plus/path%20with%20space", url);
    }

    [Fact]
    public void OpenUrlAction_RejectsForeignSession()
    {
        var currentAction = SystemNotificationAction.ForOpenUrl("Open", "https://maa.plus/");
        string foreignSessionId;
        do
        {
            foreignSessionId = Guid.NewGuid().ToString("N");
        }
        while (currentAction.Tag.StartsWith($"OpenUrl:{foreignSessionId}:", StringComparison.Ordinal));

        Assert.False(SystemNotificationAction.TryGetOpenUrl(
            $"OpenUrl:{foreignSessionId}:https://maa.plus/",
            out var url));
        Assert.Null(url);
        Assert.False(SystemNotificationAction.TryGetOpenUrl("OpenUrl:https://maa.plus/", out url));
        Assert.Null(url);
    }

    [Fact]
    public void OpenUrlAction_OnlyAcceptsHttpSchemes()
    {
        var currentAction = SystemNotificationAction.ForOpenUrl("Open", "https://maa.plus/");
        var currentSessionPrefix = currentAction.Tag[..^"https://maa.plus/".Length];

        Assert.Throws<ArgumentException>(() => SystemNotificationAction.ForOpenUrl("Open", "file:///tmp/test"));
        Assert.False(SystemNotificationAction.TryGetOpenUrl(
            currentSessionPrefix + "file:///tmp/test",
            out var url));
        Assert.Null(url);
    }

    [Fact]
    public void NativeActionActivation_IsForwardedByService()
    {
        using var poster = new FakeNotificationPoster(isAvailable: true);
        using var service = new DesktopNotificationService(poster);
        string? activatedArgument = null;
        service.ActionActivated += (_, args) => activatedArgument = args.Argument;

        poster.Activate("open-url");

        Assert.Equal("open-url", activatedArgument);
    }

    [Fact]
    public void NativeActionActivation_BeforeSubscriber_IsReplayedOnce()
    {
        using var poster = new FakeNotificationPoster(isAvailable: true);
        using var service = new DesktopNotificationService(poster);
        var firstSubscriberArguments = new List<string>();
        var secondSubscriberArguments = new List<string>();

        poster.Activate("cold-start");
        service.ActionActivated += (_, args) => firstSubscriberArguments.Add(args.Argument);
        service.ActionActivated += (_, args) => secondSubscriberArguments.Add(args.Argument);

        Assert.Equal(["cold-start"], firstSubscriberArguments);
        Assert.Empty(secondSubscriberArguments);
    }

    [Fact]
    public async Task NativeActionActivation_BeforeServiceConstruction_ReachesFirstServiceSubscriberOnceInOrder()
    {
        using var poster = new BufferedNotificationPoster();
        poster.Activate("first-cold-start");

        using var service = new DesktopNotificationService(poster);
        var activatedArguments = new ConcurrentQueue<string>();
        using var firstActivationEntered = new ManualResetEventSlim();
        using var releaseFirstActivation = new ManualResetEventSlim();
        EventHandler<NotificationActionActivatedEventArgs> handler = (_, args) =>
        {
            activatedArguments.Enqueue(args.Argument);
            if (args.Argument == "first-cold-start")
            {
                firstActivationEntered.Set();
                Assert.True(releaseFirstActivation.Wait(TimeSpan.FromSeconds(10)));
            }
        };

        var subscribeTask = Task.Run(() => service.ActionActivated += handler);
        Assert.True(firstActivationEntered.Wait(TimeSpan.FromSeconds(10)));

        poster.Activate("second-cold-start");
        releaseFirstActivation.Set();
        await subscribeTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["first-cold-start", "second-cold-start"], activatedArguments);
    }

    [Fact]
    public void BufferedNativeActionActivation_IsBoundedAndReplayedInFifoOrder()
    {
        using var source = new BufferedNotificationActivationSource(2);
        var sender = new object();
        var replayedArguments = new List<string>();
        object? replayedSender = null;

        source.Publish(sender, new NotificationActionActivatedEventArgs("discarded"));
        source.Publish(sender, new NotificationActionActivatedEventArgs("first-retained"));
        source.Publish(sender, new NotificationActionActivatedEventArgs("second-retained"));
        source.ActionActivated += (actualSender, args) =>
        {
            replayedSender = actualSender;
            replayedArguments.Add(args.Argument);
        };

        Assert.Same(sender, replayedSender);
        Assert.Equal(["first-retained", "second-retained"], replayedArguments);
    }

    [Fact]
    public void BufferedNativeActionActivation_AfterDispose_IsNotReplayed()
    {
        var source = new BufferedNotificationActivationSource(2);
        var replayedArguments = new List<string>();
        source.Publish(this, new NotificationActionActivatedEventArgs("before-dispose"));

        source.Dispose();
        source.Publish(this, new NotificationActionActivatedEventArgs("after-dispose"));
        source.ActionActivated += (_, args) => replayedArguments.Add(args.Argument);

        Assert.Empty(replayedArguments);
    }

    [Fact]
    public void BufferedNativeActionActivation_FailingHandlerDoesNotBlockOtherHandlers()
    {
        using var source = new BufferedNotificationActivationSource(2);
        var replayedArguments = new List<string>();
        source.ActionActivated += (_, _) => throw new InvalidOperationException("consumer failure");
        source.ActionActivated += (_, args) => replayedArguments.Add(args.Argument);

        source.Publish(this, new NotificationActionActivatedEventArgs("still-delivered"));

        Assert.Equal(["still-delivered"], replayedArguments);
    }

    [Fact]
    public async Task BufferedNativeActionActivation_ReplayAndConcurrentPublishRemainFifoWithSubscriberSnapshots()
    {
        using var source = new BufferedNotificationActivationSource(2);
        var firstSubscriberArguments = new ConcurrentQueue<string>();
        var secondSubscriberArguments = new ConcurrentQueue<string>();
        using var firstActivationEntered = new ManualResetEventSlim();
        using var releaseFirstActivation = new ManualResetEventSlim();
        EventHandler<NotificationActionActivatedEventArgs> firstSubscriber = (_, args) =>
        {
            firstSubscriberArguments.Enqueue(args.Argument);
            if (args.Argument == "first")
            {
                firstActivationEntered.Set();
                Assert.True(releaseFirstActivation.Wait(TimeSpan.FromSeconds(10)));
            }
        };

        source.Publish(this, new NotificationActionActivatedEventArgs("first"));
        var subscribeTask = Task.Run(() => source.ActionActivated += firstSubscriber);
        Assert.True(firstActivationEntered.Wait(TimeSpan.FromSeconds(10)));

        source.Publish(this, new NotificationActionActivatedEventArgs("second"));
        source.ActionActivated += (_, args) => secondSubscriberArguments.Enqueue(args.Argument);
        releaseFirstActivation.Set();
        await subscribeTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["first", "second"], firstSubscriberArguments);
        Assert.Empty(secondSubscriberArguments);
    }

    [Fact]
    public void NativeActionActivation_UnsubscribedHandlerIsNotCalled_AndPendingActivationRemainsReplayable()
    {
        using var poster = new FakeNotificationPoster(isAvailable: true);
        using var service = new DesktopNotificationService(poster);
        var removedCallCount = 0;
        var replayedArguments = new List<string>();
        EventHandler<NotificationActionActivatedEventArgs> removed = (_, _) => removedCallCount++;

        service.ActionActivated += removed;
        service.ActionActivated -= removed;
        poster.Activate("after-unsubscribe");
        service.ActionActivated += (_, args) => replayedArguments.Add(args.Argument);

        Assert.Equal(0, removedCallCount);
        Assert.Equal(["after-unsubscribe"], replayedArguments);
    }

    [Theory]
    [InlineData(NotificationAvailabilityReason.DisabledForApplication, "The user has disabled notifications for this app in Windows Settings.")]
    [InlineData(NotificationAvailabilityReason.DisabledForUser, "The user has disabled all app notifications in Windows Settings.")]
    [InlineData(NotificationAvailabilityReason.DisabledByGroupPolicy, "Disabled by Windows Group Policy")]
    [InlineData(NotificationAvailabilityReason.DisabledByManifest, "Disabled by app manifest (Package.appxmanifest)")]
    public void NotificationAvailabilityReason_MapsToLocalizedUiText(
        NotificationAvailabilityReason reason,
        string expected)
    {
        var availability = new NotificationAvailabilityStatus(false, "diagnostic detail", reason);

        var actual = PlatformCapabilityTextMap.GetNotificationAvailabilityDetail("en-us", availability);

        Assert.Equal(expected, actual);
    }

    private sealed class FakeNotificationPoster(bool isAvailable) : INotificationPoster
    {
        public event EventHandler<NotificationActionActivatedEventArgs>? ActionActivated;

        public PlatformCapabilityStatus Capability => new(
            Supported: true,
            Message: "fake Windows Toast poster",
            Provider: "windows-toast",
            HasFallback: true,
            FallbackMode: "in-app");

        public SystemNotificationRequest? LastNotification { get; private set; }

        public int ShowCallCount { get; private set; }

        public NotificationAvailabilityStatus GetAvailability()
            => new(
                isAvailable,
                isAvailable ? string.Empty : "Notifications are disabled for this application.",
                isAvailable
                    ? NotificationAvailabilityReason.None
                    : NotificationAvailabilityReason.DisabledForApplication);

        public Task ShowAsync(SystemNotificationRequest notification, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastNotification = notification;
            ShowCallCount++;
            return Task.CompletedTask;
        }

        public void Activate(string argument)
            => ActionActivated?.Invoke(this, new NotificationActionActivatedEventArgs(argument));

        public void Dispose()
        {
        }
    }

    private sealed class BufferedNotificationPoster : INotificationPoster
    {
        private readonly BufferedNotificationActivationSource _activationSource = new(8);

        public event EventHandler<NotificationActionActivatedEventArgs>? ActionActivated
        {
            add => _activationSource.ActionActivated += value;
            remove => _activationSource.ActionActivated -= value;
        }

        public PlatformCapabilityStatus Capability => new(
            Supported: true,
            Message: "buffered Windows Toast poster",
            Provider: "windows-toast",
            HasFallback: true,
            FallbackMode: "in-app");

        public NotificationAvailabilityStatus GetAvailability()
            => new(true, string.Empty);

        public Task ShowAsync(SystemNotificationRequest notification, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public void Activate(string argument)
            => _activationSource.Publish(this, new NotificationActionActivatedEventArgs(argument));

        public void Dispose()
            => _activationSource.Dispose();
    }
}
