using System.Text.Json.Nodes;

namespace MAAUnified.Application.Configuration;

/// <summary>
/// One settings-page section. <see cref="Key"/> is the MAAUnified section key used by the
/// navigation and views; <see cref="WpfKey"/> is the frozen WPF <c>SettingKey</c> enum name
/// (dev-v2, <c>f464016813</c>) persisted in <c>Gui.SettingOrders</c> / <c>Gui.CollapesStates</c>.
/// </summary>
public sealed record SettingsSectionDescriptor(string Key, string WpfKey);

/// <summary>
/// Settings-page section order and collapse persistence.
/// </summary>
/// <remarks>
/// WPF dev-v2 (<c>0bc04350c0</c>, <c>f7f23f0386</c>, <c>ff2d6cd2e7</c>, <c>b5412be3a1</c>,
/// <c>f464016813</c>) stores the section order as a <c>SettingKey</c> list and the collapsed
/// sections as another list under the global <c>Gui</c> object. MAAUnified keeps the same JSON
/// property names and WPF enum values so a shared configuration is readable by both frontends,
/// while the runtime section keys stay MAAUnified-native.
/// </remarks>
public static class SettingsSectionLayout
{
    /// <summary>Global key holding the persisted section order (WPF <c>Gui.SettingOrders</c>).</summary>
    public const string SettingOrdersKey = "GUI.SettingOrders";

    /// <summary>Global key holding the collapsed sections (WPF <c>Gui.CollapesStates</c>).</summary>
    public const string CollapsedSectionsKey = "GUI.CollapesStates";

    /// <summary>Prefix of the v6.3.7 per-section expander booleans (true = expanded).</summary>
    public const string LegacyExpanderKeyPrefix = "GUI.Expander";

    /// <summary>WPF key of the background section merged into <c>UiSettings</c> (dev-v2 <c>f464016813</c>).</summary>
    public const string LegacyBackgroundWpfKey = "BackgroundSettings";

    /// <summary>WPF key of the external-notification section merged into <c>NotificationSettings</c>.</summary>
    public const string LegacyExternalNotificationWpfKey = "ExternalNotificationSettings";

    /// <summary>WPF key of the merged notification section.</summary>
    public const string NotificationWpfKey = "NotificationSettings";

    private const string LegacyExpanderPrefix = "Expander";

    private static readonly SettingsSectionDescriptor[] _sections =
    [
        new("ConfigurationManager", "SwitchConfiguration"),
        new("Timer", "ScheduleSettings"),
        new("Performance", "PerformanceSettings"),
        new("Game", "GameSettings"),
        new("Connect", "ConnectionSettings"),
        new("Start", "StartupSettings"),
        new("RemoteControl", "RemoteControlSettings"),
        new("GUI", "UiSettings"),
        new("Notification", NotificationWpfKey),
        new("ThirdParty", "ThirdPartyServiceSettings"),
        new("HotKey", "HotKeySettings"),
        new("Achievement", "AchievementSettings"),
        new("VersionUpdate", "UpdateSettings"),
        new("IssueReport", "IssueReport"),
        new("About", "AboutUs"),
    ];

    public static IReadOnlyList<SettingsSectionDescriptor> Sections => _sections;

    /// <summary>Gets the default section order, which matches the order used before this feature.</summary>
    public static IReadOnlyList<string> DefaultOrder { get; } = _sections.Select(static section => section.Key).ToArray();

    public static string DefaultSectionKey => _sections[0].Key;

    public static bool IsKnownSection(string key)
        => _sections.Any(section => string.Equals(section.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Maps a persisted entry (WPF enum name or MAAUnified section key, case-insensitive) to the
    /// MAAUnified section key. Returns <see langword="null"/> for unknown or removed sections.
    /// </summary>
    public static string? TryMapSectionName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var trimmed = name.Trim();
        // Numeric payloads (serialized enum indices) and other non-identifier shapes are stale
        // values produced by older tolerant converters; never strong-cast them onto a section.
        if (!char.IsLetter(trimmed[0]) && trimmed[0] != '_')
        {
            return null;
        }

        foreach (var section in _sections)
        {
            if (string.Equals(section.Key, trimmed, StringComparison.OrdinalIgnoreCase)
                || string.Equals(section.WpfKey, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return section.Key;
            }
        }

        return null;
    }

    public static string? TryGetWpfKey(string sectionKey)
        => _sections.FirstOrDefault(section => string.Equals(section.Key, sectionKey, StringComparison.OrdinalIgnoreCase))?.WpfKey;

    /// <summary>
    /// Normalizes a persisted order list: maps WPF/MAAUnified names, renames the merged
    /// external-notification section, drops removed/unknown entries and appends missing sections
    /// in the default order.
    /// </summary>
    public static IReadOnlyList<string> NormalizeOrder(JsonNode? node)
    {
        var entries = EnumerateStringEntries(node);
        if (entries.Count == 0)
        {
            return DefaultOrder;
        }

        var result = new List<string>(_sections.Length);
        foreach (var entry in entries)
        {
            // dev-v2 merges ExternalNotificationSettings into NotificationSettings; keep the
            // position so the merged section inherits the old external-notification slot.
            var mapped = TryMapSectionName(entry)
                         ?? (string.Equals(entry, LegacyExternalNotificationWpfKey, StringComparison.OrdinalIgnoreCase)
                             ? TryMapSectionName(NotificationWpfKey)
                             : null);
            if (mapped is null || result.Contains(mapped, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            result.Add(mapped);
        }

        foreach (var section in _sections)
        {
            if (!result.Contains(section.Key, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(section.Key);
            }
        }

        return result;
    }

    /// <summary>
    /// Normalizes a persisted collapsed-section list. The merged sections do not inherit collapse
    /// state (<c>f464016813</c>), so legacy Background/ExternalNotification entries are dropped.
    /// </summary>
    public static IReadOnlyList<string> NormalizeCollapsed(JsonNode? node)
    {
        var entries = EnumerateStringEntries(node);
        var result = new List<string>(entries.Count);
        foreach (var entry in entries)
        {
            if (string.Equals(entry, LegacyBackgroundWpfKey, StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry, LegacyExternalNotificationWpfKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var mapped = TryMapSectionName(entry);
            if (mapped is null || result.Contains(mapped, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            result.Add(mapped);
        }

        return result;
    }

    public static JsonArray CreateOrderNode(IEnumerable<string> sectionKeys) => CreateWpfNameArray(sectionKeys);

    public static JsonArray CreateCollapsedNode(IEnumerable<string> sectionKeys) => CreateWpfNameArray(sectionKeys);

    public static IReadOnlyList<string> ReadOrder(IReadOnlyDictionary<string, JsonNode?> globalValues)
        => globalValues.TryGetValue(SettingOrdersKey, out var node) ? NormalizeOrder(node) : DefaultOrder;

    public static IReadOnlyList<string> ReadCollapsed(IReadOnlyDictionary<string, JsonNode?> globalValues)
        => globalValues.TryGetValue(CollapsedSectionsKey, out var node) ? NormalizeCollapsed(node) : [];

    /// <summary>
    /// Migrates the v6.3.7 per-section expander booleans (<c>GUI.ExpanderXxx</c>, true = expanded)
    /// into the collapsed-section list and normalizes both layout lists in place. Idempotent.
    /// </summary>
    /// <returns><see langword="true"/> when the global values were changed.</returns>
    public static bool NormalizeGlobalLayout(IDictionary<string, JsonNode?> globalValues)
    {
        ArgumentNullException.ThrowIfNull(globalValues);
        var changed = false;

        var collapsed = globalValues.TryGetValue(CollapsedSectionsKey, out var collapsedNode)
            ? new List<string>(NormalizeCollapsed(collapsedNode))
            : [];

        foreach (var key in globalValues.Keys
                     .Where(static key => key.StartsWith(LegacyExpanderKeyPrefix, StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            var suffix = key[LegacyExpanderKeyPrefix.Length..];
            // v6.3.7 defaulted every expander to true; a missing/invalid value must not collapse
            // the section, so treat unreadable payloads as expanded.
            var expanded = !TryReadBoolean(globalValues[key], out var parsed) || parsed;
            globalValues.Remove(key);
            changed = true;

            var mapped = TryMapSectionName(suffix);
            if (mapped is null || expanded || collapsed.Contains(mapped, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            collapsed.Add(mapped);
        }

        // Fresh configurations keep the layout keys absent: the default order is applied on read,
        // so a load without any layout key must not rewrite (and thereby dirty) the config file.
        if (globalValues.ContainsKey(SettingOrdersKey))
        {
            globalValues.TryGetValue(SettingOrdersKey, out var orderNode);
            var orderValue = CreateOrderNode(NormalizeOrder(orderNode));
            if (!JsonNode.DeepEquals(orderNode, orderValue))
            {
                globalValues[SettingOrdersKey] = orderValue;
                changed = true;
            }
        }

        var hadCollapsed = globalValues.ContainsKey(CollapsedSectionsKey);
        if (!hadCollapsed && collapsed.Count == 0)
        {
            return changed;
        }

        globalValues.TryGetValue(CollapsedSectionsKey, out var currentCollapsedNode);
        var collapsedValue = CreateCollapsedNode(collapsed);
        if (!JsonNode.DeepEquals(currentCollapsedNode, collapsedValue))
        {
            globalValues[CollapsedSectionsKey] = collapsedValue;
            changed = true;
        }

        return changed;
    }

    private static JsonArray CreateWpfNameArray(IEnumerable<string> sectionKeys)
    {
        var array = new JsonArray();
        foreach (var sectionKey in sectionKeys)
        {
            var wpfKey = TryGetWpfKey(sectionKey);
            if (wpfKey is not null)
            {
                array.Add(wpfKey);
            }
        }

        return array;
    }

    private static List<string> EnumerateStringEntries(JsonNode? node)
    {
        var result = new List<string>();
        switch (node)
        {
            case JsonArray array:
                foreach (var item in array)
                {
                    if (item is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text))
                    {
                        result.Add(text.Trim());
                    }
                }

                break;
            case JsonValue scalar when scalar.TryGetValue(out string? single) && !string.IsNullOrWhiteSpace(single):
                foreach (var item in single.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    result.Add(item);
                }

                break;
        }

        return result;
    }

    private static bool TryReadBoolean(JsonNode? node, out bool value)
    {
        value = false;
        if (node is not JsonValue jsonValue)
        {
            return false;
        }

        if (jsonValue.TryGetValue(out bool parsedBool))
        {
            value = parsedBool;
            return true;
        }

        if (jsonValue.TryGetValue(out int parsedInt))
        {
            value = parsedInt != 0;
            return true;
        }

        if (jsonValue.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text))
        {
            if (bool.TryParse(text.Trim(), out var parsedText))
            {
                value = parsedText;
                return true;
            }

            if (int.TryParse(text.Trim(), out var parsedTextInt))
            {
                value = parsedTextInt != 0;
                return true;
            }
        }

        return false;
    }
}
