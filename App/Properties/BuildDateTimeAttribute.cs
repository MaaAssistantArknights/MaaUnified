using System.Globalization;

namespace MAAUnified.App.Properties;

// Mirrors the frozen WPF `MaaWpfGui.Properties.BuildDateTimeAttribute`: the compile time is
// stamped into the app assembly so the build age stays available even for single-file
// publishes where `Assembly.Location` is empty.
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class BuildDateTimeAttribute(string date) : Attribute
{
    public DateTimeOffset BuildTime { get; } = DateTimeOffset.ParseExact(
        date,
        "O",
        CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
