using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MAAUnified.Application.Services.TaskParams;

public sealed record InfrastPlanPeriod(TimeOnly Start, TimeOnly End)
{
    public bool Contains(TimeOnly value) => Start <= value && value <= End;
}

public sealed record InfrastPlanDefinition(int Index, string Name, IReadOnlyList<InfrastPlanPeriod> Periods);

public static class InfrastPlanParser
{
    public static IReadOnlyList<InfrastPlanDefinition> ParseFile(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename) || !File.Exists(filename))
        {
            throw new FileNotFoundException("Custom infrast file does not exist.", filename);
        }

        return Parse(File.ReadAllText(filename));
    }

    public static IReadOnlyList<InfrastPlanDefinition> Parse(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject
            ?? throw new JsonException("Custom infrast root must be an object.");
        var plans = root["plans"] as JsonArray
            ?? throw new JsonException("`plans` must be an array.");

        var result = new List<InfrastPlanDefinition>(plans.Count);
        for (var index = 0; index < plans.Count; index++)
        {
            var plan = plans[index] as JsonObject
                ?? throw new JsonException($"Plan {index} must be an object.");
            var name = ReadOptionalString(plan["name"])
                ?? "Plan " + (char)('A' + index);
            var periods = ParsePeriods(plan["period"], index);
            result.Add(new InfrastPlanDefinition(index, name, periods));
        }

        return result;
    }

    public static int ResolvePlanIndex(
        IReadOnlyList<InfrastPlanDefinition> plans,
        int selectedIndex,
        TimeOnly now)
    {
        if (plans.Count == 0)
        {
            throw new InvalidOperationException("Custom infrast file contains no plans.");
        }

        if (selectedIndex >= 0)
        {
            if (selectedIndex >= plans.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(selectedIndex), "Custom infrast plan index is out of range.");
            }

            return selectedIndex;
        }

        return plans.FirstOrDefault(plan => plan.Periods.Any(period => period.Contains(now)))?.Index ?? 0;
    }

    private static IReadOnlyList<InfrastPlanPeriod> ParsePeriods(JsonNode? node, int planIndex)
    {
        if (node is null)
        {
            return [];
        }

        var periods = node as JsonArray
            ?? throw new JsonException($"Plan {planIndex} `period` must be an array.");
        var result = new List<InfrastPlanPeriod>(periods.Count);
        for (var periodIndex = 0; periodIndex < periods.Count; periodIndex++)
        {
            var range = periods[periodIndex] as JsonArray;
            if (range is null || range.Count != 2)
            {
                throw new JsonException($"Plan {planIndex} period {periodIndex} must contain exactly two time strings.");
            }

            var startText = ReadRequiredString(range[0], planIndex, periodIndex);
            var endText = ReadRequiredString(range[1], planIndex, periodIndex);
            result.Add(new InfrastPlanPeriod(
                TimeOnly.Parse(startText, CultureInfo.InvariantCulture),
                TimeOnly.Parse(endText, CultureInfo.InvariantCulture)));
        }

        return result;
    }

    private static string ReadRequiredString(JsonNode? node, int planIndex, int periodIndex)
        => ReadOptionalString(node)
            ?? throw new JsonException($"Plan {planIndex} period {periodIndex} must contain time strings.");

    private static string? ReadOptionalString(JsonNode? node)
        => node is JsonValue value && value.TryGetValue(out string? text) ? text : null;
}
