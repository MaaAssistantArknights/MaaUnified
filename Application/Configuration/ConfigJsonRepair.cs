using System.Text.Json.Nodes;

namespace MAAUnified.Application.Configuration;

/// <summary>
/// Removes the offending node from a configuration document so deserialization can be retried.
/// Mirrors the recovery loop of WPF dev-v2 `Configuration/Converter/FaultTolerantRootConverter.cs`
/// (`8c72cac105`), which deletes the property named by <see cref="System.Text.Json.JsonException.Path"/>.
/// </summary>
internal static class ConfigJsonRepair
{
    /// <summary>
    /// Removes the value addressed by a <see cref="System.Text.Json.JsonException.Path"/> style path.
    /// </summary>
    /// <param name="root">Parsed configuration document.</param>
    /// <param name="jsonPath">Path such as <c>$.Profiles.Default.TaskQueue[3]</c>.</param>
    /// <returns><see langword="true"/> when a node was removed.</returns>
    public static bool TryRemoveNode(JsonObject root, string? jsonPath)
    {
        if (root is null || string.IsNullOrWhiteSpace(jsonPath))
        {
            return false;
        }

        if (!TryParsePath(jsonPath, out var segments) || segments.Count == 0)
        {
            return false;
        }

        JsonNode? parent = root;
        for (var i = 0; i < segments.Count - 1; i++)
        {
            parent = Navigate(parent, segments[i]);
            if (parent is null)
            {
                return false;
            }
        }

        var leaf = segments[^1];
        return parent switch
        {
            JsonObject obj => obj.Remove(leaf),
            // Polymorphic arrays (TaskQueue) fail per element: `$.Profiles.Default.TaskQueue[7]`.
            JsonArray array when int.TryParse(leaf, out var index) && index >= 0 && index < array.Count
                => RemoveAt(array, index),
            _ => false,
        };
    }

    private static bool RemoveAt(JsonArray array, int index)
    {
        array.RemoveAt(index);
        return true;
    }

    private static JsonNode? Navigate(JsonNode? current, string segment)
    {
        return current switch
        {
            JsonObject obj => obj[segment],
            JsonArray array when int.TryParse(segment, out var index) && index >= 0 && index < array.Count
                => array[index],
            _ => null,
        };
    }

    private static bool TryParsePath(string path, out List<string> segments)
    {
        segments = [];
        var i = path[0] == '$' ? 1 : 0;
        while (i < path.Length)
        {
            var current = path[i];
            if (current is '.' or '$')
            {
                i++;
                continue;
            }

            if (current == '[')
            {
                i++;
                if (i >= path.Length)
                {
                    return false;
                }

                if (path[i] is '\'' or '"')
                {
                    var quote = path[i++];
                    var start = i;
                    while (i < path.Length && path[i] != quote)
                    {
                        i++;
                    }

                    if (i >= path.Length)
                    {
                        return false;
                    }

                    segments.Add(path[start..i]);
                    i++;
                }
                else
                {
                    var start = i;
                    while (i < path.Length && path[i] != ']')
                    {
                        i++;
                    }

                    if (i >= path.Length)
                    {
                        return false;
                    }

                    segments.Add(path[start..i].Trim());
                }

                if (i < path.Length && path[i] == ']')
                {
                    i++;
                }

                continue;
            }

            var nameStart = i;
            while (i < path.Length && path[i] is not ('.' or '['))
            {
                i++;
            }

            if (i > nameStart)
            {
                segments.Add(path[nameStart..i]);
            }
        }

        return segments.Count > 0;
    }
}
