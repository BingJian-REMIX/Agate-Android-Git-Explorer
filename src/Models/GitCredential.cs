using System.Text.Json.Serialization;

namespace Agate.Models;

/// <summary>
/// 一条 Git 托管平台的访问凭据。
///
/// Token 本身不落在这份模型里（存 SecureStorage），这里只留末四位用于展示。
/// 一个应用可以同时存多家平台、同一平台的多个账号，按仓库主机名匹配。
/// </summary>
public sealed class GitCredential
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>展示名，例如「GitHub 工作号」。留空时用主机名。</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>主机名，用于和远端 URL 匹配，例如 github.com / gitlab.com / cnb.cool。</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Basic 认证的用户名。各平台约定不同（GitHub 任意、GitLab 用 oauth2）。</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>平台标识，仅用于界面展示。</summary>
    public string Platform { get; set; } = string.Empty;

    /// <summary>Token 末四位，用于在列表里区分多个凭据。完整值在 SecureStorage。</summary>
    public string TokenTail { get; set; } = string.Empty;

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Label) ? Host : Label;

    [JsonIgnore]
    public string MaskedToken => string.IsNullOrEmpty(TokenTail) ? "••••" : "••••" + TokenTail;

    [JsonIgnore]
    public string Summary => $"{DisplayName} · {MaskedToken}";
}

/// <summary>
/// 主流代码托管平台的约定：默认主机、默认用户名、Token 前缀特征、申请地址。
/// 用来在用户粘贴 Token 时自动推断平台，并把用户名填对。
/// </summary>
public sealed record GitPlatform(
    string Name,
    string Host,
    string Username,
    string[] TokenPrefixes,
    string TokenUrl)
{
    /// <summary>GitHub：PAT 任意非空用户名即可；细粒度 token 有 github_pat_ 前缀。</summary>
    public static readonly GitPlatform GitHub = new(
        "GitHub", "github.com", "x-access-token",
        new[] { "ghp_", "gho_", "ghu_", "ghs_", "github_pat_" },
        "https://github.com/settings/tokens");

    /// <summary>GitLab：用户名固定 oauth2，token 以 glpat- 开头。</summary>
    public static readonly GitPlatform GitLab = new(
        "GitLab", "gitlab.com", "oauth2",
        new[] { "glpat-" },
        "https://gitlab.com/-/user_settings/personal_access_tokens");

    /// <summary>Gitee：用户名填账号名即可，token 没有统一前缀。</summary>
    public static readonly GitPlatform Gitee = new(
        "Gitee", "gitee.com", "oauth2",
        Array.Empty<string>(),
        "https://gitee.com/profile/personal_access_tokens");

    /// <summary>Bitbucket：用户名固定 x-token-auth，App Password 常以 ATBB 开头。</summary>
    public static readonly GitPlatform Bitbucket = new(
        "Bitbucket", "bitbucket.org", "x-token-auth",
        new[] { "ATBB" },
        "https://bitbucket.org/account/settings/app-passwords/");

    /// <summary>CNB（cnb.cool）：用户名固定 cnb，token 无固定前缀。</summary>
    public static readonly GitPlatform Cnb = new(
        "CNB", "cnb.cool", "cnb",
        Array.Empty<string>(),
        "https://cnb.cool/profile/token");

    /// <summary>Gitea / Forgejo 自建实例：用户名填账号，主机需自己填。</summary>
    public static readonly GitPlatform Gitea = new(
        "Gitea / Forgejo", "", "",
        Array.Empty<string>(),
        "");

    /// <summary>自定义主机（自建 GitLab、Coding、Gitee 企业版等）。</summary>
    public static readonly GitPlatform Custom = new(
        "自定义", "", "",
        Array.Empty<string>(),
        "");

    public static IReadOnlyList<GitPlatform> All { get; } = new[]
    {
        GitHub, GitLab, Gitee, Bitbucket, Cnb, Gitea, Custom
    };

    /// <summary>按 Token 前缀推断平台；无法判断时返回 null（交由用户选）。</summary>
    public static GitPlatform? DetectByToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var value = token.Trim();

        foreach (var platform in All)
        {
            foreach (var prefix in platform.TokenPrefixes)
            {
                if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return platform;
            }
        }
        return null;
    }

    /// <summary>按主机名推断平台（含常见子域，如 gitlab.example.com）。</summary>
    public static GitPlatform DetectByHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return Custom;
        var value = host.Trim().ToLowerInvariant();

        if (value == "github.com" || value.EndsWith(".github.com")) return GitHub;
        if (value == "gitlab.com") return GitLab;
        if (value == "gitee.com") return Gitee;
        if (value == "bitbucket.org") return Bitbucket;
        if (value == "cnb.cool") return Cnb;
        if (value.Contains("gitlab")) return GitLab;
        if (value.Contains("gitea") || value.Contains("forgejo")) return Gitea;
        return Custom;
    }

    /// <summary>从远端 URL 里取出主机名，支持 https 与 scp 风格的 git@host:path。</summary>
    public static string? HostFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var value = url.Trim();

        // git@github.com:owner/repo.git
        var at = value.IndexOf('@');
        if (at >= 0 && value.Contains(':') && !value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            var afterAt = value[(at + 1)..];
            var colon = afterAt.IndexOf(':');
            return colon > 0 ? afterAt[..colon].ToLowerInvariant() : afterAt.ToLowerInvariant();
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
            return uri.Host.ToLowerInvariant();

        return null;
    }
}
