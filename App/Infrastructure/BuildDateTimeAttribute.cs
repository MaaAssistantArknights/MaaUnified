using System.Globalization;

namespace MAAUnified.App.Infrastructure;

// Mirrors the WPF MaaWpfGui.Properties.BuildDateTimeAttribute so the shell window title
// can render the localized build-date suffix of the UI version.
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class BuildDateTimeAttribute(string date) : Attribute
{
    public DateTime BuildDateTime { get; } = DateTime.ParseExact(
        date,
        "O",
        null,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
