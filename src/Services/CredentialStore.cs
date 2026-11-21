using System.Text.Json;
using Agate.Models;

namespace Agate.Services;

/// <summary>
/// Git 凭据仓库：元数据（主机 / 用户名 / 平台 / 尾号）放 Preferences，
/// Token 本体放 SecureStorage，按 Id 关联。
///
/// 支持同时保存多家平台和同一平台的多个账号，推送时按远端主机名自动挑一个。
/// </summary>
public static class CredentialStore
{
    private const string ListKey = "git_credentials_v1";
    private const string TokenKeyPrefix = "git_token_";

    /// <summary>旧版单 GitHub Token 的键，首次启动时迁移成一条凭据。</summary>
    private const string LegacyTokenKey = "github_token";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    public static IReadOnlyList<GitCredential> Load()
    {
        try
        {
            var raw = Preferences.Default.Get(ListKey, string.Empty);
            if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<GitCredential>();

            var list = JsonSerializer.Deserialize<List<GitCredential>>(raw, JsonOptions);
            return list ?? new List<GitCredential>();
        }
        catch (Exception ex)
        {
            AppLog.Error("cred-load", ex);
            return Array.Empty<GitCredential>();
        }
    }

    private static void Save(IEnumerable<GitCredential> credentials)
    {
        try
        {
            var json = JsonSerializer.Serialize(credentials.ToList(), JsonOptions);
            Preferences.Default.Set(ListKey, json);
        }
        catch (Exception ex)
        {
            AppLog.Error("cred-save", ex);
        }
    }

    /// <summary>新增或更新一条凭据。token 为 null 表示不改动已存的 Token。</summary>
    public static async Task UpsertAsync(GitCredential credential, string? token)
    {
        var list = Load().ToList();
        var index = list.FindIndex(c => c.Id == credential.Id);
        if (index >= 0) list[index] = credential;
        else list.Add(credential);

        Save(list);

        if (!string.IsNullOrWhiteSpace(token))
        {
            await WriteTokenAsync(credential.Id, token);
            credential.TokenTail = Tail(token);
            // 尾号变了要再存一次
            index = list.FindIndex(c => c.Id == credential.Id);
            if (index >= 0) list[index] = credential;
            Save(list);
        }
    }

    public static async Task RemoveAsync(string id)
    {
        var list = Load().Where(c => c.Id != id).ToList();
        Save(list);

        try { SecureStorage.Default.Remove(TokenKeyPrefix + id); }
        catch (Exception ex) { AppLog.Error("cred-remove-token", ex); }

        await Task.CompletedTask;
    }

    public static async Task<string?> GetTokenAsync(string id)
    {
        try { return await SecureStorage.Default.GetAsync(TokenKeyPrefix + id); }
        catch (Exception ex) { AppLog.Error("cred-read-token", ex); return null; }
    }

    private static async Task WriteTokenAsync(string id, string token)
    {
        try { await SecureStorage.Default.SetAsync(TokenKeyPrefix + id, token.Trim()); }
        catch (Exception ex) { AppLog.Error("cred-write-token", ex); }
    }

    /// <summary>
    /// 按远端 URL 找匹配的凭据。主机名精确匹配优先；
    /// 只有一个凭据时直接用它（自建实例常和配置里写的主机不完全一致）。
    /// </summary>
    public static async Task<(string Username, string Token)?> ResolveAsync(string? url)
    {
        var all = Load();
        if (all.Count == 0) return null;

        var host = GitPlatform.HostFromUrl(url);
        GitCredential? match = null;

        if (!string.IsNullOrEmpty(host))
        {
            match = all.FirstOrDefault(c =>
                string.Equals(c.Host, host, StringComparison.OrdinalIgnoreCase));

            match ??= all.FirstOrDefault(c =>
                !string.IsNullOrEmpty(c.Host) &&
                host.EndsWith("." + c.Host, StringComparison.OrdinalIgnoreCase));
        }

        match ??= all.Count == 1 ? all[0] : null;
        if (match is null) return null;

        var token = await GetTokenAsync(match.Id);
        if (string.IsNullOrWhiteSpace(token)) return null;

        var username = string.IsNullOrWhiteSpace(match.Username)
            ? GitPlatform.DetectByHost(match.Host).Username
            : match.Username;

        return (string.IsNullOrWhiteSpace(username) ? "x-access-token" : username, token);
    }

    /// <summary>
    /// 把旧版单条 GitHub Token 迁移成一条正常的凭据。
    /// 只在还没有任何凭据、且旧键有值时才动，迁移后删掉旧键避免重复。
    /// </summary>
    public static async Task MigrateLegacyAsync()
    {
        try
        {
            if (Load().Count > 0) return;

            var legacy = await SecureStorage.Default.GetAsync(LegacyTokenKey);
            if (string.IsNullOrWhiteSpace(legacy)) return;

            var credential = new GitCredential
            {
                Label = "GitHub",
                Host = GitPlatform.GitHub.Host,
                Username = GitPlatform.GitHub.Username,
                Platform = GitPlatform.GitHub.Name
            };
            await UpsertAsync(credential, legacy);
            SecureStorage.Default.Remove(LegacyTokenKey);
        }
        catch (Exception ex)
        {
            AppLog.Error("cred-migrate", ex);
        }
    }

    private static string Tail(string token)
        => token.Length <= 4 ? token : token[^4..];
}
