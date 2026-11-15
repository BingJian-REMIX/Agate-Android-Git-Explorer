using Microsoft.Maui.Storage;

namespace Pebble.Services;

/// <summary>一个网络存储连接。密码不随此对象序列化，单独存 SecureStorage。</summary>
public sealed class NetworkConnection
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;   // 显示名，如「我家 NAS」
    public string Type { get; set; } = "WebDAV";        // WebDAV / SMB（SMB 暂未实现）
    public string Url { get; set; } = string.Empty;     // 根地址，建议末尾带 '/'，如 https://nas.example.com/dav/
    public string Username { get; set; } = string.Empty;
}

/// <summary>网络存储连接的持久化：连接清单（不含密码）存 Preferences，密码存 SecureStorage。</summary>
public static class NetworkStore
{
    private const string Key = "network_connections";

    public static IReadOnlyList<NetworkConnection> Connections
    {
        get
        {
            var raw = Preferences.Default.Get(Key, string.Empty);
            if (string.IsNullOrWhiteSpace(raw)) return new List<NetworkConnection>();

            try
            {
                return System.Text.Json.JsonSerializer
                    .Deserialize<List<NetworkConnection>>(raw) ?? new List<NetworkConnection>();
            }
            catch (Exception ex)
            {
                AppLog.Error("net-load", ex);
                return new List<NetworkConnection>();
            }
        }
    }

    public static void Upsert(NetworkConnection c)
    {
        var list = Connections.ToList();
        var i = list.FindIndex(x => x.Id == c.Id);
        if (i >= 0) list[i] = c; else list.Add(c);
        Save(list);
    }

    public static void Remove(NetworkConnection c)
    {
        var list = Connections.ToList();
        list.RemoveAll(x => x.Id == c.Id);
        Save(list);

        try { SecureStorage.Default.Remove(PwdKey(c.Id)); }
        catch (Exception ex) { AppLog.Error("net-pwd-remove", ex); }
    }

    private static void Save(IReadOnlyList<NetworkConnection> list)
    {
        try
        {
            Preferences.Default.Set(Key, System.Text.Json.JsonSerializer.Serialize(list));
        }
        catch (Exception ex) { AppLog.Error("net-save", ex); }
    }

    public static async Task<string?> GetPasswordAsync(string id)
    {
        try { return await SecureStorage.Default.GetAsync(PwdKey(id)); }
        catch (Exception ex) { AppLog.Error("net-pwd-read", ex); return null; }
    }

    public static async Task SetPasswordAsync(string id, string? password)
    {
        try
        {
            if (string.IsNullOrEmpty(password)) SecureStorage.Default.Remove(PwdKey(id));
            else await SecureStorage.Default.SetAsync(PwdKey(id), password);
        }
        catch (Exception ex) { AppLog.Error("net-pwd-write", ex); }
    }

    private static string PwdKey(string id) => "netpwd_" + id;
}
