using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Writers;
using SharpCompress.Writers.Zip;
using System.Linq;

namespace Pebble.Services;

/// <summary>归档内单条目信息（仅列举，不解压到磁盘）。</summary>
public sealed class ArchiveEntryInfo
{
    public string Name { get; }
    public long Size { get; }
    public long? CompressedSize { get; }
    public bool IsDirectory { get; }

    public ArchiveEntryInfo(string name, long size, long? compressedSize, bool isDirectory)
    {
        Name = name;
        Size = size;
        CompressedSize = compressedSize;
        IsDirectory = isDirectory;
    }

    /// <summary>右侧展示的体积文本（文件夹不显示）。</summary>
    public string SizeText => IsDirectory ? string.Empty : FileOperations.FormatSize(Size);
}

/// <summary>归档内"当前文件夹"下的可见节点（文件夹或文件），用于多层导航。</summary>
public sealed class ArchiveNode
{
    public string Name { get; init; } = "";   // 显示名（路径最后一段）
    public string Key { get; init; } = "";     // 文件：归档内完整 key；文件夹：含末尾斜杠的路径
    public bool IsDirectory { get; init; }
    public long Size { get; init; }

    public string SizeText => IsDirectory ? string.Empty : FileOperations.FormatSize(Size);
}

/// <summary>
/// 应用内归档处理：基于 SharpCompress，统一支持 zip / rar / 7z / gz / tar 及它们的分卷
/// （.z01、.r00、.001 等多卷）。ArchiveFactory 会自动探测格式与多卷关系。
/// </summary>
public static class ArchiveService
{
    /// <summary>
    /// 解压归档到 targetDirectory。同名时回调 resolve 决定策略（Skip 返回 null）。
    /// 自动防护 Zip Slip 路径穿越。
    /// </summary>
    public static List<string> Extract(string archivePath, string targetDirectory, Func<string, string, ConflictAction> resolve)
    {
        var extracted = new List<string>();

        Directory.CreateDirectory(targetDirectory);
        var prefix = Path.GetFullPath(targetDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        using var archive = ArchiveFactory.Open(archivePath);
        foreach (var entry in archive.Entries)
        {
            if (entry.IsDirectory) continue;

            // gz 这类单文件压缩可能没有给出内部文件名
            var key = entry.Key;
            if (string.IsNullOrEmpty(key)) key = Path.GetFileNameWithoutExtension(archivePath);

            var destination = Path.GetFullPath(Path.Combine(targetDirectory, key));

            // 防路径穿越：解出的路径必须仍在目标目录内
            if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var dir = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            if (File.Exists(destination))
            {
                var action = resolve(key, destination);
                if (action == ConflictAction.Skip) continue;
                if (action == ConflictAction.KeepBoth) destination = FileOperations.UniquePath(destination);
                else if (File.Exists(destination)) File.Delete(destination); // Replace 覆盖
            }

            entry.WriteToFile(destination);
            extracted.Add(destination);
        }

        return extracted;
    }

    /// <summary>
    /// 不解压预览：只读列举归档内条目（名称、体积、是否目录），不向磁盘写入任何内容。
    /// gz 等单文件压缩若内部无文件名，则用归档名（去扩展名）作为展示名。
    /// </summary>
    public static List<ArchiveEntryInfo> ListEntries(string archivePath)
    {
        var list = new List<ArchiveEntryInfo>();

        using var archive = ArchiveFactory.Open(archivePath);
        foreach (var entry in archive.Entries)
        {
            var key = entry.Key;
            var name = string.IsNullOrEmpty(key)
                ? Path.GetFileNameWithoutExtension(archivePath)
                : key;

            list.Add(new ArchiveEntryInfo(
                name,
                entry.Size,
                entry.CompressedSize,
                entry.IsDirectory));
        }

        return list;
    }

    /// <summary>
    /// 多层导航：只返回归档内「当前文件夹」下的直接子项（文件夹在前、文件在后）。
    /// folderPath 为空串或 "/" 表示根；否则形如 "docs/img/"。点文件夹进入更深一层，
    /// 不再把所有条目拍平到一个列表里。
    /// </summary>
    public static List<ArchiveNode> ListChildren(string archivePath, string folderPath)
    {
        folderPath = (folderPath ?? string.Empty).Trim().TrimEnd('/');
        var prefix = folderPath.Length == 0 ? string.Empty : folderPath + "/";

        var dirs = new Dictionary<string, ArchiveNode>(StringComparer.OrdinalIgnoreCase);
        var files = new List<ArchiveNode>();

        using var archive = ArchiveFactory.Open(archivePath);
        foreach (var entry in archive.Entries)
        {
            var key = entry.Key;
            if (string.IsNullOrEmpty(key)) key = Path.GetFileNameWithoutExtension(archivePath);
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            var rem = key.Substring(prefix.Length).TrimEnd('/');
            if (rem.Length == 0) continue;   // 当前文件夹自身

            var slash = rem.IndexOf('/');
            if (slash >= 0)
            {
                // 更深一层：合成一个虚拟文件夹节点
                var d = rem.Substring(0, slash);
                if (!dirs.ContainsKey(d))
                    dirs[d] = new ArchiveNode { Name = d, Key = prefix + d + "/", IsDirectory = true };
            }
            else if (entry.IsDirectory)
            {
                if (!dirs.ContainsKey(rem))
                    dirs[rem] = new ArchiveNode { Name = rem, Key = prefix + rem + "/", IsDirectory = true };
            }
            else
            {
                files.Add(new ArchiveNode { Name = rem, Key = key, IsDirectory = false, Size = entry.Size });
            }
        }

        var result = new List<ArchiveNode>();
        result.AddRange(dirs.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase));
        result.AddRange(files.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>
    /// 仅抽出单个条目到指定路径（用于"不解压预览"内部文本/图片）：写到应用缓存目录，
    /// 不污染用户目录。key 为空时（gz 单文件）取归档内第一个文件条目。
    /// </summary>
    public static void ExtractEntryToPath(string archivePath, string key, string destination)
    {
        using var archive = ArchiveFactory.Open(archivePath);

        IArchiveEntry? found = null;
        foreach (var entry in archive.Entries)
        {
            if (entry.IsDirectory) continue;

            if (string.IsNullOrEmpty(key))
            {
                found = entry; // gz 单文件：取唯一文件条目
                break;
            }

            if (string.Equals(entry.Key, key, StringComparison.Ordinal))
            {
                found = entry;
                break;
            }
        }

        if (found is null)
            throw new FileNotFoundException($"归档内未找到：{key}");

        var dir = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(destination)) File.Delete(destination);

        found.WriteToFile(destination);
    }

    /// <summary>
    /// 流式重写 ZIP（MT 管理器式“包内免解压修改”）：
    /// 用 SharpCompress 读取原 Zip，逐条目流式写出到临时 Zip，过程中：
    ///  · 命中 deleteKeys 的条目被跳过（目录按前缀匹配 = 删除整棵子树）；
    ///  · 命中 renameMap 的条目改用新 key 写出（目录按前缀匹配 = 重命名整棵子树）；
    ///  · addFiles 在末尾追加到指定目录。
    /// 全程逐条目流式拷贝（reader.Open() → writer.Write()，内部 CopyTo 分块传输），
    /// 不会把整个压缩包解到内存或磁盘，内存占用仅单条目一块缓冲。
    /// 注意：ZipWriter 会按 Deflate 重新压缩，因此不保证与原字节一致，但归档内容不变。
    /// 写完后用 File.Move 原子替换原文件（同卷 rename 即原子），临时文件在 finally 中清理。
    /// </summary>
    public static void RebuildZip(
        string sourcePath,
        IEnumerable<string>? deleteKeys = null,
        IReadOnlyDictionary<string, string>? renameMap = null,
        IEnumerable<(string EntryKey, string SourceFilePath)>? addFiles = null)
    {
        deleteKeys ??= Enumerable.Empty<string>();
        renameMap ??= new Dictionary<string, string>();
        addFiles ??= Enumerable.Empty<(string, string)>();

        var deleteSet = new HashSet<string>(deleteKeys, StringComparer.Ordinal);

        // 临时文件与原文件同目录（保证同卷，Move 才原子）
        var tmp = Path.Combine(
            Path.GetDirectoryName(sourcePath) ?? ".",
            Path.GetFileName(sourcePath) + ".pebble-tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8));

        try
        {
            // 读：SharpCompress 的 ZipWriter 是只写流式写入器，“读”由 ArchiveFactory 完成。
            // reader 必须包在显式 using 块里，确保 Move 之前已释放对原文件的句柄。
            using (var reader = ArchiveFactory.Open(sourcePath))
            using (var writer = new ZipWriter(File.Create(tmp), new ZipWriterOptions(CompressionType.Deflate) { LeaveStreamOpen = false }))
            {
                foreach (var entry in reader.Entries)
                {
                    if (entry.IsDirectory) continue;   // 目录由文件路径重建，无需单独写

                    var key = NormalizeKey(entry.Key);
                    if (string.IsNullOrEmpty(key))
                        key = NormalizeKey(Path.GetFileNameWithoutExtension(sourcePath));

                    if (IsDeleted(deleteSet, key)) continue;                 // 跳过被删条目
                    var outKey = NormalizeKey(MapKey(renameMap, key));        // 改名条目换 key

                    using var src = entry.OpenEntryStream();
                    writer.Write(outKey, src, entry.LastModifiedTime);        // 流式拷贝单条目
                }

                // 追加外部文件（已拷到可随机读的路径）
                foreach (var (entryKey, sourceFilePath) in addFiles)
                {
                    var addKey = NormalizeKey(entryKey);
                    using var fs = File.OpenRead(sourceFilePath);
                    writer.Write(addKey, fs, File.GetLastWriteTime(sourceFilePath));
                }
            }

            // 全部写完后原子替换原文件（reader/writer 已释放句柄）
            File.Move(tmp, sourcePath, overwrite: true);
        }
        finally
        {
            // 无论如何清理临时文件（正常路径已被 Move 移走，这里是失败兜底）
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    private static string NormalizeKey(string? key) => (key ?? string.Empty).Replace('\\', '/');

    private static bool IsDeleted(HashSet<string> deleteSet, string key)
    {
        if (deleteSet.Contains(key)) return true;
        foreach (var d in deleteSet)
            if (d.EndsWith('/') && key.StartsWith(d, StringComparison.Ordinal)) return true;
        return false;
    }

    private static string MapKey(IReadOnlyDictionary<string, string> renameMap, string key)
    {
        if (renameMap.TryGetValue(key, out var rk)) return rk;
        // 目录重命名：整棵子树前缀替换
        foreach (var kv in renameMap)
            if (kv.Key.EndsWith('/') && key.StartsWith(kv.Key, StringComparison.Ordinal))
                return kv.Value + key.Substring(kv.Key.Length);
        return key;
    }
}
