using System.Text.Json.Nodes;
using MAAUnified.Application.Models;
using MAAUnified.Compat.Runtime;

namespace MAAUnified.Application.Services.TaskParams;

public sealed record OperNameInfo(
    string Name,
    string? NameTw,
    string? NameEn,
    string? NameJp,
    string? NameKr,
    string Role);

/// <summary>
/// Operator name alias index over <c>resource/battle_data.json</c>. Mirrors the
/// WPF operator-name normalization used by Roguelike CoreChar and OperProgress
/// import: any localized name or alias resolves to the canonical (zh-cn) name
/// that Core matches against, so multilingual input never reaches Core verbatim
/// when a canonical name is known.
/// </summary>
public sealed class OperNameIndex
{
    public static readonly OperNameIndex Empty = new(new Dictionary<string, OperNameInfo>(StringComparer.OrdinalIgnoreCase));

    private static readonly object SharedLock = new();
    private static OperNameIndex? _shared;

    private readonly IReadOnlyDictionary<string, OperNameInfo> _aliasIndex;

    private OperNameIndex(IReadOnlyDictionary<string, OperNameInfo> aliasIndex)
    {
        _aliasIndex = aliasIndex;
    }

    /// <summary>
    /// Gets the process-wide index loaded from the runtime resource directory.
    /// Empty when <c>battle_data.json</c> is unavailable.
    /// </summary>
    public static OperNameIndex Shared
    {
        get
        {
            lock (SharedLock)
            {
                return _shared ??= LoadFromRuntimeLayout();
            }
        }
    }

    public bool IsEmpty => _aliasIndex.Count == 0;

    public static OperNameIndex FromBattleDataJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty;
        }

        try
        {
            return JsonNode.Parse(json) is JsonObject root && root["chars"] is JsonObject chars
                ? FromChars(chars)
                : Empty;
        }
        catch
        {
            return Empty;
        }
    }

    public static OperNameIndex FromFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return Empty;
        }

        try
        {
            return FromBattleDataJson(File.ReadAllText(path));
        }
        catch
        {
            return Empty;
        }
    }

    /// <summary>
    /// Resolves one name (canonical or any localized alias) to the canonical
    /// name; unknown input is returned trimmed so hand-edited values survive.
    /// </summary>
    public string NormalizeName(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        return !string.IsNullOrEmpty(trimmed) && _aliasIndex.TryGetValue(trimmed, out var info)
            ? info.Name
            : trimmed;
    }

    public bool TryResolve(string? name, out OperNameInfo info)
    {
        info = null!;
        var trimmed = name?.Trim() ?? string.Empty;
        return !string.IsNullOrEmpty(trimmed) && _aliasIndex.TryGetValue(trimmed, out info!);
    }

    /// <summary>
    /// Resolves the Core role name (see <see cref="OperProgressRoles"/>) for a
    /// known operator; <see cref="OperProgressRoles.Unknown"/> when unknown.
    /// </summary>
    public string ResolveRole(string? name)
        => TryResolve(name, out var info) ? info.Role : OperProgressRoles.Unknown;

    /// <summary>
    /// Localized display name for the given UI language, falling back to the
    /// canonical name when the localized variant is missing.
    /// </summary>
    public string GetLocalizedDisplayName(string? name, string? language)
    {
        if (!TryResolve(name, out var info))
        {
            return name?.Trim() ?? string.Empty;
        }

        var localized = (language ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "zh-tw" => info.NameTw,
            "en-us" => info.NameEn,
            "ja-jp" => info.NameJp,
            "ko-kr" => info.NameKr,
            _ => null,
        };

        return string.IsNullOrWhiteSpace(localized) ? info.Name : localized;
    }

    private static OperNameIndex LoadFromRuntimeLayout()
    {
        string[] candidates;
        try
        {
            var runtimeBaseDirectory = RuntimeLayout.ResolveRuntimeBaseDirectory();
            candidates =
            [
                Path.Combine(runtimeBaseDirectory, "resource", "battle_data.json"),
                Path.Combine(Environment.CurrentDirectory, "resource", "battle_data.json"),
            ];
        }
        catch
        {
            candidates = [Path.Combine(Environment.CurrentDirectory, "resource", "battle_data.json")];
        }

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return FromFile(candidate);
            }
        }

        return Empty;
    }

    private static OperNameIndex FromChars(JsonObject chars)
    {
        var aliasIndex = new Dictionary<string, OperNameInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in chars)
        {
            if (pair.Value is not JsonObject character
                || !TryReadString(character["name"], out var name))
            {
                continue;
            }

            _ = TryReadString(character["name_tw"], out var nameTw);
            _ = TryReadString(character["name_en"], out var nameEn);
            _ = TryReadString(character["name_jp"], out var nameJp);
            _ = TryReadString(character["name_kr"], out var nameKr);

            var info = new OperNameInfo(
                name.Trim(),
                Normalize(nameTw),
                Normalize(nameEn),
                Normalize(nameJp),
                Normalize(nameKr),
                NormalizeRole(ReadString(character["profession"])));

            AddAlias(aliasIndex, info.Name, info);
            AddAlias(aliasIndex, info.NameTw, info);
            AddAlias(aliasIndex, info.NameEn, info);
            AddAlias(aliasIndex, info.NameJp, info);
            AddAlias(aliasIndex, info.NameKr, info);
        }

        return new OperNameIndex(aliasIndex);
    }

    private static string NormalizeRole(string? profession)
    {
        var normalized = (profession ?? string.Empty).Trim();
        return normalized.ToUpperInvariant() switch
        {
            "PIONEER" => "Pioneer",
            "WARRIOR" => "Warrior",
            "TANK" => "Tank",
            "SNIPER" => "Sniper",
            "CASTER" => "Caster",
            "MEDIC" => "Medic",
            "SUPPORT" => "Support",
            "SPECIAL" => "Special",
            _ => OperProgressRoles.Unknown,
        };
    }

    private static void AddAlias(
        IDictionary<string, OperNameInfo> aliasIndex,
        string? alias,
        OperNameInfo info)
    {
        var normalized = Normalize(alias);
        if (normalized is not null && !aliasIndex.ContainsKey(normalized))
        {
            aliasIndex[normalized] = info;
        }
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool TryReadString(JsonNode? node, out string value)
    {
        value = string.Empty;
        if (node is not JsonValue jsonValue
            || !jsonValue.TryGetValue(out string? parsed)
            || string.IsNullOrWhiteSpace(parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static string ReadString(JsonNode? node)
        => TryReadString(node, out var value) ? value : string.Empty;
}
