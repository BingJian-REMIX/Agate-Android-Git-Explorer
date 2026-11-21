namespace Agate.Services;

/// <summary>
/// 大文件分割与合并。全部流式读写（固定缓冲区），不把整个文件读进内存，
/// 因此按大小分割也不受文件体积限制（只受磁盘空间限制）。
/// </summary>
public static class FileSplitter
{
    private const int BufferSize = 1024 * 1024; // 1 MiB

    /// <summary>
    /// 按字节大小分割。分片名为 `原名.partNNN`（沿用原扩展名，NNN 从 001 起）。
    /// 返回生成的分片路径列表（按序号）。chunkSize 必须 &gt; 0。
    /// </summary>
    public static List<string> SplitBySize(string sourcePath, long chunkSize, Action<long>? onProgress = null)
    {
        if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize), "分片大小必须大于 0");

        var total = new FileInfo(sourcePath).Length;
        if (total == 0) throw new IOException("文件为空，无法分割");

        var parts = new List<string>();
        var buffer = new byte[BufferSize];
        long written = 0;
        var index = 1;

        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize);
        while (input.Position < total)
        {
            var partPath = PartPath(sourcePath, index);
            using var output = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize);

            long inPart = 0;
            while (inPart < chunkSize)
            {
                var want = (int)Math.Min(buffer.Length, chunkSize - inPart);
                var read = input.Read(buffer, 0, want);
                if (read <= 0) break;

                output.Write(buffer, 0, read);
                inPart += read;
                written += read;
                onProgress?.Invoke(written);
            }

            parts.Add(partPath);
            index++;
        }

        return parts;
    }

    /// <summary>
    /// 按文本行数分割（保留每行原始字节，不会把换行规范化成别的形式）。
    /// 分片名同上。linesPerPart 必须 &gt; 0。
    /// </summary>
    public static List<string> SplitByLines(string sourcePath, int linesPerPart, Action<long>? onProgress = null)
    {
        if (linesPerPart <= 0) throw new ArgumentOutOfRangeException(nameof(linesPerPart), "每片行数必须大于 0");

        var parts = new List<string>();
        var buffer = new byte[BufferSize];
        long bytesRead = 0;
        var index = 1;
        var linesInPart = 0;

        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize);
        FileStream? output = null;

        try
        {
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                var offset = 0;
                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] != (byte)'\n') continue;

                    linesInPart++;
                    if (linesInPart < linesPerPart) continue;

                    // 当前行是分片的最后一行：把它（含换行）写进当前片，然后开下一片
                    output ??= OpenNext(sourcePath, parts, ref index);
                    output.Write(buffer, offset, i - offset + 1);

                    output.Dispose();
                    output = null;
                    linesInPart = 0;
                    offset = i + 1;
                }

                // 本缓冲区剩余的（未凑满一行的）字节写入当前片
                if (offset < read)
                {
                    output ??= OpenNext(sourcePath, parts, ref index);
                    output.Write(buffer, offset, read - offset);
                }

                bytesRead += read;
                onProgress?.Invoke(bytesRead);
            }
        }
        finally
        {
            output?.Dispose();
        }

        if (parts.Count == 0) throw new IOException("没有可分割的内容");
        return parts;
    }

    private static FileStream OpenNext(string sourcePath, List<string> parts, ref int index)
    {
        var partPath = PartPath(sourcePath, index);
        parts.Add(partPath);
        index++;
        return new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize);
    }

    /// <summary>
    /// 合并：按给定顺序把 parts 流式拼接到 targetPath（覆盖已有文件）。
    /// 返回写入的总字节数。
    /// </summary>
    public static long Merge(IReadOnlyList<string> parts, string targetPath, Action<long>? onProgress = null)
    {
        if (parts.Count == 0) throw new IOException("没有可合并的分片");

        var buffer = new byte[BufferSize];
        long written = 0;

        using var output = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize);
        foreach (var part in parts)
        {
            using var input = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize);

            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
                written += read;
                onProgress?.Invoke(written);
            }
        }

        return written;
    }

    /// <summary>
    /// 猜测分片序号：从 `xxx.part003` 解析 3；解析不出返回 int.MaxValue（排在最后）。
    /// 供合并时按序号排序，避免 part10 排在 part2 前面。
    /// </summary>
    public static int PartIndex(string path)
    {
        var name = Path.GetFileName(path);
        var dot = name.LastIndexOf(".part", StringComparison.OrdinalIgnoreCase);
        if (dot < 0) return int.MaxValue;

        var digits = name[(dot + 5)..];
        return int.TryParse(digits, out var value) ? value : int.MaxValue;
    }

    /// <summary>合并时按分片序号自然排序（同序号或无法解析时保持原相对顺序）。</summary>
    public static List<string> SortParts(IEnumerable<string> parts)
        => parts.Select((path, order) => (path, order))
                .OrderBy(item => PartIndex(item.path))
                .ThenBy(item => item.order)
                .Select(item => item.path)
                .ToList();

    private static string PartPath(string sourcePath, int index)
    {
        var directory = Path.GetDirectoryName(sourcePath) ?? "";
        var name = Path.GetFileName(sourcePath);
        var target = Path.Combine(directory, $"{name}.part{index:000}");

        // 已存在同名分片就往上加序号，避免覆盖用户数据
        var probe = index;
        while (File.Exists(target))
        {
            probe++;
            target = Path.Combine(directory, $"{name}.part{probe:000}");
        }

        return target;
    }
}
