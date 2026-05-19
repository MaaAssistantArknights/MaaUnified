using MAAUnified.App;

namespace MAAUnified.Tests;

public sealed class AppUnhandledExceptionFilterTests
{
    [Fact]
    public void ShouldIgnoreUnhandledException_WhenOperationCanceled_ReturnsTrue()
    {
        Assert.True(MAAUnified.App.App.ShouldIgnoreUnhandledException(new OperationCanceledException("cancelled")));
    }

    [Fact]
    public void ShouldIgnoreUnhandledException_WhenCanonicalAppMenuRegistrarIsMissing_ReturnsTrue()
    {
        var exception = new AggregateException(
            new InvalidOperationException(
                "org.freedesktop.DBus.Error.ServiceUnknown: The name com.canonical.AppMenu.Registrar was not provided by any .service files"));

        Assert.Equal(OperatingSystem.IsLinux(), MAAUnified.App.App.ShouldIgnoreUnhandledException(exception));
    }

    [Fact]
    public void ShouldIgnoreUnhandledException_WhenUnexpectedFailureOccurs_ReturnsFalse()
    {
        var exception = new AggregateException(
            new InvalidOperationException("boom"),
            new InvalidOperationException("org.freedesktop.DBus.Error.ServiceUnknown: Another service is missing"));

        Assert.False(MAAUnified.App.App.ShouldIgnoreUnhandledException(exception));
    }
}
