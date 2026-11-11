using System.Globalization;
using MAAUnified.App.Services;

namespace MAAUnified.Tests;

// Mirrors WPF LocalizationHelper.FormatVersion (LocalizationHelper.cs) used by the WPF
// window title for both the UI version (build date) and the resource version (resource date).
// The string argument is the GUI.CustomCulture config value: WPF branches on
// CustomCultureInfo = GUI.CustomCulture when valid, otherwise CultureInfo.CurrentCulture,
// never on the UI language.
public sealed class VersionDisplayFormatterTests
{
    private static readonly DateTime SampleLocal = new(2025, 9, 1, 12, 0, 0, DateTimeKind.Local);

    [Theory]
    [InlineData("zh-cn")]
    [InlineData("zh-tw")]
    public void FormatVersion_ShouldAppendHashMonthDay_ForChineseCultures(string customCulture)
    {
        Assert.Equal("v5.0.0#0901", VersionDisplayFormatter.FormatVersion("v5.0.0", SampleLocal, customCulture));
    }

    [Fact]
    public void FormatVersion_ShouldPrefixDayMonth_ForEnglishUsCulture()
    {
        Assert.Equal("01/09 v5.0.0", VersionDisplayFormatter.FormatVersion("v5.0.0", SampleLocal, "en-us"));
    }

    [Theory]
    [InlineData("ja-jp")]
    [InlineData("ko-kr")]
    public void FormatVersion_ShouldPrefixShortDateWithoutYear_ForOtherCultures(string customCulture)
    {
        var formatted = VersionDisplayFormatter.FormatVersion("v5.0.0", SampleLocal, customCulture);

        Assert.EndsWith(" v5.0.0", formatted, StringComparison.Ordinal);
        Assert.True(formatted.Length > " v5.0.0".Length);
        Assert.DoesNotContain("2025", formatted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not-a-culture")]
    [InlineData("")]
    [InlineData(null)]
    public void FormatVersion_ShouldFallBackToCurrentCulture_ForMissingOrInvalidCustomCulture(string? customCulture)
    {
        // WPF CustomCultureInfo: empty or unknown GUI.CustomCulture falls back to the OS culture.
        var formatted = VersionDisplayFormatter.FormatVersion("v5.0.0", SampleLocal, customCulture);
        var expected = VersionDisplayFormatter.FormatVersion(
            "v5.0.0", SampleLocal, CultureInfo.CurrentCulture.Name);

        Assert.Equal(expected, formatted);
    }

    [Fact]
    public void FormatVersion_ShouldConvertUtcToLocalTime()
    {
        var localNoon = new DateTime(2025, 3, 15, 12, 0, 0, DateTimeKind.Local);
        var utcEquivalent = localNoon.ToUniversalTime();

        var expected = VersionDisplayFormatter.FormatVersion("v1.0.0", localNoon, "zh-cn");

        Assert.Equal(expected, VersionDisplayFormatter.FormatVersion("v1.0.0", utcEquivalent, "zh-cn"));
    }
}
