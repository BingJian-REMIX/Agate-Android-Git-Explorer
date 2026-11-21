using System.IO;
using System.Net;
using System.Text;
using System.Xml.Linq;
using Agate.Models;

namespace Agate.Services;

/// <summary>
/// WebDAV 客户端：基于 System.Net.Http 直接发 PROPFIND / GET / PUT / MKCOL，不依赖平台 API。
/// 设计要点：
/// · 用单个静态 HttpClient，所有请求都传绝对 URL + 每请求 Basic 认证头，避免 BaseAddress 串味。
/// · 目录列表走 PROPFIND（Depth:1），解析 207 Multi-Status 的 multistatus XML。
/// · 下载用 ResponseHeadersRead 流式拷贝，支持进度回调；上传用 PUT，整文件流式推。
/// · 相对路径一律以 '/' 分隔，相对连接根（conn.Url）计算。
/// 注：SMB 暂未实现；本类仅服务 WebDAV 类型连接。
/// </summary>
public static class WebDavService
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private static readonly XNamespace D = "DAV:";

    // ----------------------------------------------------------------- 基础

    private static Uri BuildBaseUri(NetworkConnection conn)
    {
        var url = conn.Url.Trim();
        if (!url.EndsWith("/")) url += "/";
        return new Uri(url, UriKind.Absolute);
    }

    /// <summary>把相对路径拼到连接根，得到绝对 URL。relativePath 为空表示根本身。</summary>
    private static string BuildUrl(NetworkConnection conn, string relativePath)
    {
        var baseUri = BuildBaseUri(conn);
        if (string.IsNullOrWhiteSpace(relativePath)) return baseUri.ToString();
        return new Uri(baseUri, relativePath.TrimStart('/')).ToString();
    }

    private static HttpRequestMessage PropFind(string url, int depth)
    {
        var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), url);
        req.Headers.TryAddWithoutValidation("Depth", depth.ToString());
        req.Headers.TryAddWithoutValidation("Accept", "application/xml");

        const string body =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<D:propfind xmlns:D=\"DAV:\"><D:prop>" +
            "<D:displayname/><D:resourcetype/><D:getcontentlength/><D:getlastmodified/>" +
            "</D:prop></D:propfind>";

        req.Content = new StringContent(body, Encoding.UTF8, "application/xml");
        return req;
    }

    private static void ApplyAuth(HttpRequestMessage req, NetworkConnection conn, string? password)
    {
        if (string.IsNullOrEmpty(conn.Username)) return;
        var raw = conn.Username + ":" + (password ?? string.Empty);
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", token);
    }

    // ----------------------------------------------------------------- 对外 API

    /// <summary>测试连接：对根做一次 PROPFIND（Depth:0），期望 207/200/204。</summary>
    public static async Task<bool> TestConnectionAsync(NetworkConnection conn, string? password, CancellationToken ct = default)
    {
        try
        {
            var url = BuildBaseUri(conn).ToString();
            using var req = PropFind(url, 0);
            ApplyAuth(req, conn, password);

            using var resp = await Client.SendAsync(req, ct).ConfigureAwait(false);
            return resp.StatusCode is HttpStatusCode.MultiStatus or HttpStatusCode.OK or HttpStatusCode.NoContent;
        }
        catch (Exception ex)
        {
            AppLog.Error("dav-test", ex);
            return false;
        }
    }

    /// <summary>列出某远端目录下的条目（不含目录自身）。</summary>
    public static async Task<List<RemoteItem>> ListAsync(
        NetworkConnection conn, string? password, string relativePath, CancellationToken ct = default)
    {
        var url = BuildUrl(conn, relativePath);
        using var req = PropFind(url, 1);
        ApplyAuth(req, conn, password);

        using var resp = await Client.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode != HttpStatusCode.MultiStatus && resp.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException($"PROPFIND 失败：{(int)resp.StatusCode} {resp.ReasonPhrase}");

        var xml = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseMultistatus(xml, BuildBaseUri(conn));
    }

    /// <summary>下载远端文件到本地 destPath，支持进度（已下载字节数）。</summary>
    public static async Task DownloadAsync(
        NetworkConnection conn, string? password, string remoteRelativePath, string destPath,
        IProgress<long>? progress = null, CancellationToken ct = default)
    {
        var url = BuildUrl(conn, remoteRelativePath);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyAuth(req, conn, password);

        using var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = File.Create(destPath);

        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            total += read;
            progress?.Report(total);
        }
    }

    /// <summary>上传本地文件到远端目录（remoteRelativeDir 相对根，可为空串表示根）。</summary>
    public static async Task UploadAsync(
        NetworkConnection conn, string? password, string localFilePath, string remoteRelativeDir,
        IProgress<long>? progress = null, CancellationToken ct = default)
    {
        var fileName = Path.GetFileName(localFilePath);
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("本地文件路径无效", nameof(localFilePath));

        var rel = string.IsNullOrWhiteSpace(remoteRelativeDir)
            ? fileName
            : remoteRelativeDir.Trim('/') + "/" + fileName;

        var url = new Uri(BuildBaseUri(conn), rel).ToString();
        using var req = new HttpRequestMessage(HttpMethod.Put, url);
        ApplyAuth(req, conn, password);

        var content = new StreamContent(File.OpenRead(localFilePath));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        req.Content = content;

        using var resp = await Client.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        progress?.Report(new FileInfo(localFilePath).Length);
    }

    /// <summary>在远端创建目录（MKCOL）。</summary>
    public static async Task CreateDirectoryAsync(
        NetworkConnection conn, string? password, string remoteRelativePath, CancellationToken ct = default)
    {
        var rel = remoteRelativePath.Trim('/');
        if (rel.Length == 0) return;

        var url = new Uri(BuildBaseUri(conn), rel + "/").ToString();
        using var req = new HttpRequestMessage(new HttpMethod("MKCOL"), url);
        ApplyAuth(req, conn, password);

        using var resp = await Client.SendAsync(req, ct).ConfigureAwait(false);
        // 405 Method Not Allowed 通常意味着目录已存在，视为成功
        if (resp.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.OK or HttpStatusCode.MethodNotAllowed))
            throw new HttpRequestException($"MKCOL 失败：{(int)resp.StatusCode} {resp.ReasonPhrase}");
    }

    // ----------------------------------------------------------------- 解析

    private static List<RemoteItem> ParseMultistatus(string xml, Uri baseUri)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (Exception ex) { throw new FormatException("WebDAV 响应不是合法 XML", ex); }

        var basePath = Uri.UnescapeDataString(baseUri.AbsolutePath);
        if (!basePath.EndsWith("/")) basePath += "/";
        var basePathNoSlash = basePath.TrimEnd('/');

        var items = new List<RemoteItem>();

        foreach (var resp in doc.Descendants(D + "response"))
        {
            var hrefEl = resp.Element(D + "href");
            if (hrefEl is null) continue;

            var rawHref = hrefEl.Value.Trim();

            // 服务器返回的 href 可能是绝对 URL，也可能是相对路径：都解析成绝对。
            // 注意 Uri.AbsolutePath 会重新编码（空格变 %20），所以取值后再解码一次，
            // 否则文件名里的空格/中文会残留百分号、且下载 URL 构建会出错。
            if (!Uri.TryCreate(rawHref, UriKind.Absolute, out var itemUri))
            {
                if (!Uri.TryCreate(baseUri, rawHref, out itemUri)) continue;
            }

            var path = Uri.UnescapeDataString(itemUri.AbsolutePath);

            // 计算相对根的路径
            string rel;
            if (path.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
                rel = path.Substring(basePath.Length);
            else if (path.StartsWith(basePathNoSlash + "/", StringComparison.OrdinalIgnoreCase) || path == basePathNoSlash)
                rel = path.Substring(basePathNoSlash.Length).TrimStart('/');
            else
                continue; // 不属于本根，跳过（理论上不该出现）

            rel = rel.Trim('/');
            if (rel.Length == 0) continue;   // 目录自身，跳过

            var name = rel.Contains('/') ? rel[(rel.LastIndexOf('/') + 1)..] : rel;

            var prop = resp.Elements(D + "propstat")
                .Select(p => p.Element(D + "prop"))
                .FirstOrDefault(p => p is not null);

            var isDir = prop?.Element(D + "resourcetype")?.Element(D + "collection") is not null;

            long size = 0;
            var lenEl = prop?.Element(D + "getcontentlength");
            if (lenEl is not null) long.TryParse(lenEl.Value, out size);

            DateTime modified = DateTime.MinValue;
            var modEl = prop?.Element(D + "getlastmodified");
            if (modEl is not null) DateTime.TryParse(modEl.Value, out modified);

            items.Add(new RemoteItem
            {
                Name = name,
                RelativePath = rel,
                IsDirectory = isDir,
                Size = size,
                LastModified = modified
            });
        }

        return items
            .OrderBy(i => !i.IsDirectory)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
