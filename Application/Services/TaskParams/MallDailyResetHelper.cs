using System.Globalization;

namespace MAAUnified.Application.Services.TaskParams;

public static class MallDailyResetHelper
{
    private const int YjDayStartHour = 4;

    private static readonly IReadOnlyDictionary<string, int> ClientTypeTimezoneOffsets =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [string.Empty] = 8,
            ["Official"] = 8,
            ["Bilibili"] = 8,
            ["txwy"] = 8,
            ["YoStarEN"] = -7,
            ["YoStarJP"] = 9,
            ["YoStarKR"] = 9,
        };

    public static bool IsTaskAvailableToday(string? lastExecutionYjDateText, string clientType, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(lastExecutionYjDateText))
        {
            return true;
        }

        if (!TryParseYjDate(lastExecutionYjDateText, out var parsedDate))
        {
            return true;
        }

        return GetYjDate(utcNow, clientType) > parsedDate.Date;
    }

    public static DateTime GetYjDate(DateTime utcNow, string clientType)
    {
        return GetYjDateTime(utcNow, clientType).Date;
    }

    public static DateTime GetYjDateTime(DateTime utcNow, string clientType)
    {
        var offset = ResolveTimezoneOffset(clientType);
        return utcNow.AddHours(offset - YjDayStartHour);
    }

    public static string GetCurrentYjDateString(DateTime utcNow, string clientType)
    {
        return GetYjDate(utcNow, clientType).ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    public static string GetPreviousYjDateString(DateTime utcNow, string clientType)
    {
        return GetYjDate(utcNow, clientType).AddDays(-1).ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static int ResolveTimezoneOffset(string? clientType)
    {
        if (string.IsNullOrWhiteSpace(clientType))
        {
            return ClientTypeTimezoneOffsets[string.Empty];
        }

        return ClientTypeTimezoneOffsets.TryGetValue(clientType, out var offset)
            ? offset
            : ClientTypeTimezoneOffsets[string.Empty];
    }

    private static bool TryParseYjDate(string text, out DateTime value)
    {
        // Frozen WPF parity (MallTask.IsCreditFightAvailable/IsVisitFriendsAvailable):
        // only the exact "yyyy/MM/dd HH:mm:ss" marker format (with '-' normalized to '/')
        // counts as a valid marker. Every other string is a parse failure and therefore
        // means "available today"; loosely parseable values (date-only strings, other
        // culture layouts, surrounding whitespace) must not be treated as markers.
        return DateTime.TryParseExact(
            text.Replace('-', '/'),
            "yyyy/MM/dd HH:mm:ss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out value);
    }
}
