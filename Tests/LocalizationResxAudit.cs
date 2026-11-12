using System.Text.Json;
using System.Text.RegularExpressions;

namespace MAAUnified.Tests;

/// <summary>
/// 本地化 resx 审计基线。冻结接入点状 key 契约之前的历史漂移：非规范 key 名、既有缺语言条目
/// 与既有乱序数量；CI 只校验新增 key。由 <c>CI/update-localization-audit-baseline.py</c> 生成。
/// </summary>
internal sealed class LocalizationAuditBaseline
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public List<string> LegacyNonConformingKeys { get; init; } = [];

    public Dictionary<string, List<string>> AllowedMissingKeys { get; init; } = [];

    public Dictionary<string, List<string>> AllowedDuplicateKeys { get; init; } = [];

    public Dictionary<string, int> AllowedInversions { get; init; } = [];

    public static LocalizationAuditBaseline Load(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<LocalizationAuditBaseline>(stream, SerializerOptions)
            ?? throw new InvalidDataException($"Localization audit baseline '{path}' is empty.");
    }
}

/// <summary>
/// resx key 审计：点分命名（<c>Domain.Topic.Name[.Tip]</c>）、五语言集合一致（qps-Ploc 允许子集）
/// 与文件内字典序。已有漂移记录在 <see cref="LocalizationAuditBaseline"/> 中，不参与校验。
/// </summary>
internal static class LocalizationResxAudit
{
    public const string PallasLanguage = "pallas";

    public static readonly string[] FiveLanguages = ["zh-cn", "zh-tw", "en-us", "ja-jp", "ko-kr"];

    private static readonly IReadOnlyDictionary<string, string> ResourceFiles =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["zh-cn"] = "UiTexts.resx",
            ["zh-tw"] = "UiTexts.zh-TW.resx",
            ["en-us"] = "UiTexts.en-US.resx",
            ["ja-jp"] = "UiTexts.ja-JP.resx",
            ["ko-kr"] = "UiTexts.ko-KR.resx",
            [PallasLanguage] = "UiTexts.qps-Ploc.resx",
        };

    private static readonly Regex DottedKeyPattern =
        new(@"^[A-Za-z][A-Za-z0-9]*(\.[A-Za-z0-9]+)+$", RegexOptions.Compiled);

    private static readonly Regex DataEntryPattern =
        new("<data name=\"([^\"]+)\"", RegexOptions.Compiled);

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> LoadDocuments(string localizationDirectory)
    {
        var documents = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (language, fileName) in ResourceFiles)
        {
            var path = Path.Combine(localizationDirectory, fileName);
            documents[language] = ParseKeys(File.ReadAllText(path));
        }

        return documents;
    }

    public static List<string> ParseKeys(string resxContent)
        => DataEntryPattern.Matches(resxContent).Select(match => match.Groups[1].Value).ToList();

    public static List<string> FindViolations(
        IReadOnlyDictionary<string, IReadOnlyList<string>> documents,
        LocalizationAuditBaseline baseline)
    {
        var violations = new List<string>();
        var legacyKeys = new HashSet<string>(baseline.LegacyNonConformingKeys, StringComparer.Ordinal);

        foreach (var (language, keys) in documents)
        {
            var allowedDuplicates = baseline.AllowedDuplicateKeys.TryGetValue(language, out var duplicates)
                ? new HashSet<string>(duplicates, StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            foreach (var duplicate in keys
                .GroupBy(key => key, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key))
            {
                if (!allowedDuplicates.Contains(duplicate))
                {
                    violations.Add($"{language}: duplicate key '{duplicate}'");
                }
            }

            foreach (var key in keys)
            {
                if (!legacyKeys.Contains(key) && !DottedKeyPattern.IsMatch(key))
                {
                    violations.Add($"{language}: key '{key}' does not follow Domain.Topic.Name[.Tip]");
                }
            }
        }

        var referenceKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var language in FiveLanguages)
        {
            if (documents.TryGetValue(language, out var keys))
            {
                referenceKeys.UnionWith(keys);
            }
            else
            {
                violations.Add($"missing resx document for '{language}'");
            }
        }

        foreach (var language in FiveLanguages)
        {
            if (!documents.TryGetValue(language, out var keys))
            {
                continue;
            }

            var presentKeys = new HashSet<string>(keys, StringComparer.Ordinal);
            var allowedMissing = baseline.AllowedMissingKeys.TryGetValue(language, out var missing)
                ? new HashSet<string>(missing, StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in referenceKeys)
            {
                if (!presentKeys.Contains(key) && !allowedMissing.Contains(key))
                {
                    violations.Add($"{language}: missing key '{key}'");
                }
            }
        }

        if (documents.TryGetValue(PallasLanguage, out var pallasKeys)
            && documents.TryGetValue("zh-cn", out var zhCnKeys))
        {
            var zhCnSet = new HashSet<string>(zhCnKeys, StringComparer.Ordinal);
            foreach (var key in pallasKeys)
            {
                if (!zhCnSet.Contains(key))
                {
                    violations.Add($"{PallasLanguage}: key '{key}' is not present in zh-cn");
                }
            }
        }

        foreach (var (language, keys) in documents)
        {
            var inversions = 0;
            for (var i = 1; i < keys.Count; i++)
            {
                if (string.CompareOrdinal(keys[i - 1], keys[i]) > 0)
                {
                    inversions++;
                }
            }

            var budget = baseline.AllowedInversions.TryGetValue(language, out var allowed) ? allowed : 0;
            if (inversions > budget)
            {
                violations.Add($"{language}: {inversions} out-of-order keys exceed the frozen budget of {budget}");
            }
        }

        return violations;
    }
}
