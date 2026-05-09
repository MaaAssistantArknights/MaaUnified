using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MAAUnified.Application.Services.Localization;

public static class AchievementTextCatalog
{
    private static readonly Regex NestedKeyPattern = new(@"\{key=(\w+)\}", RegexOptions.Compiled);
    private static readonly string[] PallasChars = ["💃", "🕺", "🍷", "🍸", "🍺", "🍻", "🥃", "🍶"];
    private static readonly object CatalogGate = new();
    private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> CatalogByLanguage = new(StringComparer.OrdinalIgnoreCase);

    public static string GetString(string key, string? language, string? fallback = null)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return fallback ?? string.Empty;
        }

        var normalizedLanguage = UiLanguageCatalog.Normalize(language);
        if (string.Equals(normalizedLanguage, "pallas", StringComparison.OrdinalIgnoreCase))
        {
            return GetPallasString();
        }

        foreach (var candidate in BuildLookupOrder(normalizedLanguage))
        {
            var catalog = LoadCatalog(candidate);
            if (catalog.TryGetValue(key, out var value))
            {
                return ResolveNestedKeys(key, value, normalizedLanguage, new Stack<string>());
            }
        }

        return fallback ?? $"{{{{ {key} }}}}";
    }

    public static string GetPallasString(int low = 3, int high = 6)
    {
        var normalizedLow = Math.Max(1, low);
        var normalizedHigh = Math.Max(normalizedLow + 1, high);
        var length = Random.Shared.Next(normalizedLow, normalizedHigh);
        var builder = new StringBuilder(length * 2);
        for (var index = 0; index < length; index++)
        {
            builder.Append(PallasChars[Random.Shared.Next(0, PallasChars.Length)]);
        }

        return builder.ToString();
    }

    public static IReadOnlyDictionary<string, string> GetAllStrings(string? language)
    {
        var normalizedLanguage = UiLanguageCatalog.Normalize(language);
        var lookupLanguage = string.Equals(normalizedLanguage, "pallas", StringComparison.OrdinalIgnoreCase)
            ? "zh-cn"
            : normalizedLanguage;
        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in BuildLookupOrder(lookupLanguage).Reverse())
        {
            foreach (var (key, value) in LoadCatalog(candidate))
            {
                entries[key] = ResolveNestedKeys(key, value, lookupLanguage, new Stack<string>());
            }
        }

        return entries;
    }

    private static string ResolveNestedKeys(
        string currentKey,
        string input,
        string language,
        Stack<string> visited)
    {
        if (visited.Contains(currentKey))
        {
            return input;
        }

        visited.Push(currentKey);
        try
        {
            return NestedKeyPattern.Replace(input, match =>
            {
                var innerKey = match.Groups[1].Value;
                var replacement = GetString(innerKey, language, innerKey);
                return ResolveNestedKeys(innerKey, replacement, language, visited);
            });
        }
        finally
        {
            _ = visited.Pop();
        }
    }

    private static IReadOnlyList<string> BuildLookupOrder(string language)
    {
        return language switch
        {
            "zh-cn" => ["zh-cn"],
            "zh-tw" => ["zh-tw", "zh-cn", "en-us"],
            "en-us" => ["en-us", "zh-cn"],
            _ => [language, "en-us", "zh-cn"],
        };
    }

    private static IReadOnlyDictionary<string, string> LoadCatalog(string language)
    {
        lock (CatalogGate)
        {
            if (CatalogByLanguage.TryGetValue(language, out var cached))
            {
                return cached;
            }

            var loaded = LoadCatalogCore(language);
            CatalogByLanguage[language] = loaded;
            return loaded;
        }
    }

    private static IReadOnlyDictionary<string, string> LoadCatalogCore(string language)
    {
        var assembly = typeof(AchievementTextCatalog).Assembly;
        var resourceName = assembly
            .GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith($".AchievementLocalizations.{language}.xaml", StringComparison.OrdinalIgnoreCase));
        if (resourceName is null)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var document = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in document.Descendants().Where(node => string.Equals(node.Name.LocalName, "String", StringComparison.OrdinalIgnoreCase)))
        {
            var key = element.Attributes().FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, "Key", StringComparison.OrdinalIgnoreCase))?.Value;
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            entries[key] = element.Value;
        }

        return entries;
    }
}
