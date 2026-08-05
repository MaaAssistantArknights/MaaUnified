using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MAAUnified.Application.Security;

public static class SensitiveDataRedactor
{
    public const string Replacement = "[REDACTED]";

    public static string Redact(string text, IEnumerable<string> sensitiveValues)
    {
        var redacted = text;
        foreach (var value in GetRedactionValues(sensitiveValues))
        {
            redacted = ReplaceCompleteValue(redacted, value);
        }

        return redacted;
    }

    internal static IReadOnlyList<string> GetRedactionValues(IEnumerable<string> sensitiveValues)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in sensitiveValues.Where(static value => !string.IsNullOrEmpty(value)))
        {
            values.Add(value);
            TryAddEncoded(values, value, static text => Uri.EscapeDataString(text));
            TryAddEncoded(values, value, WebUtility.UrlEncode);
        }

        return values.OrderByDescending(static value => value.Length).ToArray();
    }

    private static void TryAddEncoded(ISet<string> values, string value, Func<string, string?> encode)
    {
        try
        {
            var encoded = encode(value);
            if (!string.IsNullOrEmpty(encoded) && !string.Equals(encoded, value, StringComparison.Ordinal))
            {
                values.Add(encoded);
            }
        }
        catch (UriFormatException)
        {
        }
    }

    private static string ReplaceCompleteValue(string text, string value)
    {
        var searchIndex = 0;
        var copyIndex = 0;
        StringBuilder? result = null;
        while (searchIndex <= text.Length - value.Length)
        {
            var matchIndex = text.IndexOf(value, searchIndex, StringComparison.Ordinal);
            if (matchIndex < 0)
            {
                break;
            }

            var hasLeftBoundary = !IsTokenCharacter(value[0])
                                  || matchIndex == 0
                                  || !IsTokenCharacter(text[matchIndex - 1]);
            var matchEnd = matchIndex + value.Length;
            var hasRightBoundary = !IsTokenCharacter(value[^1])
                                   || matchEnd == text.Length
                                   || !IsTokenCharacter(text[matchEnd]);
            if (hasLeftBoundary && hasRightBoundary)
            {
                result ??= new StringBuilder(text.Length);
                result.Append(text, copyIndex, matchIndex - copyIndex);
                result.Append(Replacement);
                copyIndex = matchEnd;
                searchIndex = matchEnd;
                continue;
            }

            searchIndex = matchIndex + 1;
        }

        if (result is null)
        {
            return text;
        }

        result.Append(text, copyIndex, text.Length - copyIndex);
        return result.ToString();
    }

    private static bool IsTokenCharacter(char value)
        => char.IsLetterOrDigit(value) || value == '_';
}

public static partial class DiagnosticDataSanitizer
{
    public const string RuntimeRootReplacement = "<runtime>";
    public const string UserHomeReplacement = "$HOME";

    public static string Sanitize(
        string text,
        IEnumerable<string>? sensitiveValues = null,
        IEnumerable<string>? privateRoots = null)
    {
        var sanitized = text;
        foreach (var root in (privateRoots ?? [])
                     .Where(static root => !string.IsNullOrWhiteSpace(root))
                     .Select(Path.GetFullPath)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(static root => root.Length))
        {
            sanitized = ReplacePathPrefix(sanitized, root, RuntimeRootReplacement);
        }

        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userHome))
        {
            sanitized = ReplacePathPrefix(sanitized, Path.GetFullPath(userHome), UserHomeReplacement);
        }

        sanitized = UnixUserHomePathRegex().Replace(sanitized, UserHomeReplacement);
        sanitized = WindowsUserHomePathRegex().Replace(sanitized, "%USERPROFILE%");
        sanitized = AuthorizationCredentialRegex().Replace(
            sanitized,
            static match => match.Groups["prefix"].Value + SensitiveDataRedactor.Replacement);
        sanitized = AbsoluteUrlRegex().Replace(
            sanitized,
            static match => SanitizeAbsoluteUrl(match.Value));
        return SensitiveDataRedactor.Redact(sanitized, sensitiveValues ?? []);
    }

    private static string SanitizeAbsoluteUrl(string matchedUrl)
    {
        var suffixLength = 0;
        while (suffixLength < matchedUrl.Length
               && matchedUrl[^(suffixLength + 1)] is '.' or ',' or '!' or ')' or ']' or '}')
        {
            suffixLength++;
        }

        var url = suffixLength == 0 ? matchedUrl : matchedUrl[..^suffixLength];
        var suffix = suffixLength == 0 ? string.Empty : matchedUrl[^suffixLength..];
        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
        {
            return matchedUrl;
        }

        var authorityStart = schemeEnd + 3;
        var authorityEnd = url.IndexOfAny(['/', '?', '#'], authorityStart);
        if (authorityEnd < 0)
        {
            authorityEnd = url.Length;
        }

        var userInfoEnd = url.LastIndexOf('@', authorityEnd - 1, authorityEnd - authorityStart);
        if (userInfoEnd >= authorityStart)
        {
            url = string.Concat(
                url.AsSpan(0, authorityStart),
                SensitiveDataRedactor.Replacement,
                url.AsSpan(userInfoEnd));
        }

        var fragmentStart = url.IndexOf('#', authorityStart);
        var queryStart = url.IndexOf('?', authorityStart);
        if (queryStart >= 0 && (fragmentStart < 0 || queryStart < fragmentStart))
        {
            url = RedactParameterValues(url, queryStart + 1, fragmentStart < 0 ? url.Length : fragmentStart);
        }

        fragmentStart = url.IndexOf('#', authorityStart);
        if (fragmentStart >= 0)
        {
            var fragmentQueryStart = url.IndexOf('?', fragmentStart + 1);
            var parameterStart = fragmentQueryStart >= 0 ? fragmentQueryStart + 1 : fragmentStart + 1;
            if (url.IndexOf('=', parameterStart) >= 0)
            {
                url = RedactParameterValues(url, parameterStart, url.Length);
            }
        }

        return url + suffix;
    }

    private static string RedactParameterValues(string text, int start, int end)
    {
        var builder = new StringBuilder(text.Length);
        builder.Append(text, 0, start);
        var segmentStart = start;
        while (segmentStart < end)
        {
            var separator = text.IndexOfAny(['&', ';'], segmentStart);
            if (separator < 0 || separator >= end)
            {
                separator = end;
            }

            var equals = text.IndexOf('=', segmentStart, separator - segmentStart);
            if (equals < 0)
            {
                builder.Append(text, segmentStart, separator - segmentStart);
            }
            else
            {
                builder.Append(text, segmentStart, equals - segmentStart + 1);
                if (equals + 1 < separator)
                {
                    builder.Append(SensitiveDataRedactor.Replacement);
                }
            }

            if (separator < end)
            {
                builder.Append(text[separator]);
            }

            segmentStart = separator + 1;
        }

        builder.Append(text, end, text.Length - end);
        return builder.ToString();
    }

    private static string ReplacePathPrefix(string text, string path, string replacement)
    {
        var sanitized = text.Replace(path, replacement, StringComparison.OrdinalIgnoreCase);
        var alternate = path.Contains('\\', StringComparison.Ordinal)
            ? path.Replace('\\', '/')
            : path.Replace('/', '\\');
        if (!string.Equals(alternate, path, StringComparison.Ordinal))
        {
            sanitized = sanitized.Replace(alternate, replacement, StringComparison.OrdinalIgnoreCase);
        }

        return sanitized;
    }

    [GeneratedRegex("""(?<![A-Za-z0-9:])/(?:Users|home)/[^/\\\s"'<>]+""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex UnixUserHomePathRegex();

    [GeneratedRegex("""(?<![A-Za-z0-9:])[A-Za-z]:[\\/]+Users[\\/]+[^/\\\s"'<>]+""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex WindowsUserHomePathRegex();

    [GeneratedRegex("""(?<prefix>\b(?:Proxy-)?Authorization\b["']?\s*[:=]\s*["']?(?:[A-Za-z][A-Za-z0-9+.-]*\s+)?)(?<credential>[^\s,;"'}]+)""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex AuthorizationCredentialRegex();

    [GeneratedRegex("""\b(?:https?|wss?)://[^\s<>"']+""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex AbsoluteUrlRegex();
}
