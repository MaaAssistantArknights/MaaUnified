using System;
using System.Globalization;
using System.Net.Http;

namespace MAAUnified.Application.Services.VersionUpdate;

public enum GitHubAuthorizationOutcome
{
    /// <summary>已附带 <c>Authorization</c> 头。</summary>
    Attached = 0,

    /// <summary>未配置 Token。</summary>
    NoToken = 1,

    /// <summary>请求目标不是 https + <c>api.github.com</c> + 默认端口 + 无 userinfo，按安全规则不附带凭据。</summary>
    NotEligibleUri = 2,

    /// <summary>Token 已过期（有效期由 GitHub 响应头写回），不附带并提示重新输入。</summary>
    TokenExpired = 3,

    /// <summary>请求已自带 <c>Authorization</c> 头，不覆盖。</summary>
    AlreadyAuthorized = 4,
}

/// <summary>
/// GitHub API 凭据附带规则。
/// 对应 WPF <c>53870a7a52</c>（<c>Helper/GitHubUpdateHelper.cs</c>）：只有 https + <c>api.github.com</c>
/// + 默认端口 + 无 userinfo 的目标才附带 <c>Authorization: Bearer</c>，
/// 避免重定向或恶意 ResourceApi 把凭据带去第三方主机；下载链接（<c>objects.githubusercontent.com</c>）
/// 与镜像站不在白名单内，保持匿名请求。
/// </summary>
public static class GitHubApiRequestAuthorizer
{
    public const string Host = "api.github.com";

    public const string AuthenticationHeaderName = "Authorization";

    public const string ExpirationHeaderName = "GitHub-Authentication-Token-Expiration";

    public static bool IsEligibleUri(Uri? uri)
    {
        return uri is not null
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase)
            && uri.IsDefaultPort
            && string.IsNullOrEmpty(uri.UserInfo);
    }

    public static GitHubAuthorizationOutcome TryApplyAuthorization(
        HttpRequestMessage request,
        string? token,
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedToken = token?.Trim();
        if (string.IsNullOrEmpty(normalizedToken))
        {
            return GitHubAuthorizationOutcome.NoToken;
        }

        if (!IsEligibleUri(request.RequestUri))
        {
            return GitHubAuthorizationOutcome.NotEligibleUri;
        }

        if (expiresAt.HasValue && expiresAt.Value <= (now ?? DateTimeOffset.UtcNow))
        {
            return GitHubAuthorizationOutcome.TokenExpired;
        }

        if (request.Headers.Contains(AuthenticationHeaderName))
        {
            return GitHubAuthorizationOutcome.AlreadyAuthorized;
        }

        request.Headers.TryAddWithoutValidation(AuthenticationHeaderName, $"Bearer {normalizedToken}");
        return GitHubAuthorizationOutcome.Attached;
    }

    /// <summary>
    /// 解析 GitHub 返回的 <c>GitHub-Authentication-Token-Expiration</c> 头。
    /// 值形如 <c>2026-01-02 03:04:05 UTC</c>，也接受 ISO 8601；无法解析时返回 <c>null</c>。
    /// </summary>
    public static DateTimeOffset? TryParseExpiration(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        var normalized = rawValue.Trim();
        if (string.Equals(normalized, "none", StringComparison.OrdinalIgnoreCase))
        {
            // GitHub 对永不过期的令牌返回 none。
            return null;
        }

        if (DateTimeOffset.TryParse(
                normalized,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return parsed;
        }

        if (DateTime.TryParse(
                normalized.Replace(" UTC", "Z", StringComparison.OrdinalIgnoreCase),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsedUtc))
        {
            return new DateTimeOffset(parsedUtc, TimeSpan.Zero);
        }

        return null;
    }
}
