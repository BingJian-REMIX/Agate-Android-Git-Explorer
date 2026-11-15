using System.Text.RegularExpressions;

namespace Pebble.Services;

public enum RenameMode
{
    Sequence,   // 按序号 + 模板
    Regex       // 正则替换
}

/// <summary>一条重命名计划：原名 → 新名，以及校验错误（非空则该条不可执行）。</summary>
public sealed class RenamePlanItem
{
    public required string SourcePath { get; init; }
    public required string OriginalName { get; init; }
    public string NewName { get; set; } = "";
    public string? Error { get; set; }

    public bool CanApply => Error is null && NewName.Length > 0 && NewName != OriginalName;
}

/// <summary>
/// 批量重命名：序号模板 / 正则替换两种模式，先生成计划并校验冲突，再两步改名落地。
/// 纯 System.IO + 正则，不依赖平台 API，便于离线测试。
/// </summary>
public static class BatchRenamer
{
    /// <summary>序号占位符：`{n}` 或 `{n:000}`（补零位数由冒号后的 0 个数决定）。</summary>
    private static readonly Regex SequenceToken = new(@"\{n(?::(0+))?\}", RegexOptions.Compiled);

    /// <summary>非法文件名字符（两种分隔符都算）。</summary>
    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    /// <summary>
    /// 序号模式。模板支持 `{name}`（原名去扩展名）、`{ext}`（扩展名，含点）、`{n}`/`{n:000}`（序号）。
    /// 例：`{name}_{n:000}{ext}` → photo_001.jpg。
    /// </summary>
    public static List<RenamePlanItem> PlanSequence(
        IReadOnlyList<string> paths, string template, int start = 1, int step = 1)
    {
        if (step <= 0) step = 1;
        var plan = new List<RenamePlanItem>(paths.Count);
        var index = start;

        foreach (var path in paths)
        {
            var original = Path.GetFileName(path);
            var item = new RenamePlanItem { SourcePath = path, OriginalName = original };
            try
            {
                item.NewName = RenderTemplate(template, original, index);
            }
            catch (Exception ex)
            {
                item.Error = ex.Message;
            }
            plan.Add(item);
            index += step;
        }

        Validate(plan);
        return plan;
    }

    /// <summary>
    /// 正则模式。默认只对主名（不含扩展名）替换、保留原扩展名；
    /// includeExtension=true 时对整个文件名替换（扩展名由用户自己处理）。
    /// </summary>
    public static List<RenamePlanItem> PlanRegex(
        IReadOnlyList<string> paths, string pattern, string replacement, bool includeExtension = false)
    {
        var plan = new List<RenamePlanItem>(paths.Count);
        if (string.IsNullOrEmpty(pattern))
        {
            foreach (var path in paths)
            {
                var original = Path.GetFileName(path);
                plan.Add(new RenamePlanItem { SourcePath = path, OriginalName = original, Error = "正则不能为空" });
            }
            return plan;
        }

        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.Compiled);
        }
        catch (ArgumentException ex)
        {
            foreach (var path in paths)
            {
                var original = Path.GetFileName(path);
                plan.Add(new RenamePlanItem { SourcePath = path, OriginalName = original, Error = "正则无效：" + ex.Message });
            }
            return plan;
        }

        foreach (var path in paths)
        {
            var original = Path.GetFileName(path);
            var item = new RenamePlanItem { SourcePath = path, OriginalName = original };
            try
            {
                if (includeExtension)
                {
                    item.NewName = regex.Replace(original, replacement);
                }
                else
                {
                    var stem = Path.GetFileNameWithoutExtension(original);
                    var ext = Path.GetExtension(original);
                    item.NewName = regex.Replace(stem, replacement) + ext;
                }
            }
            catch (Exception ex)
            {
                item.Error = ex.Message;
            }
            plan.Add(item);
        }

        Validate(plan);
        return plan;
    }

    private static string RenderTemplate(string template, string originalName, int index)
    {
        if (string.IsNullOrWhiteSpace(template))
            throw new InvalidOperationException("模板不能为空");

        var stem = Path.GetFileNameWithoutExtension(originalName);
        var ext = Path.GetExtension(originalName);

        var result = template.Replace("{name}", stem).Replace("{ext}", ext);
        result = SequenceToken.Replace(result, match =>
        {
            var zeros = match.Groups[1].Value;
            return zeros.Length > 0 ? index.ToString("D" + zeros.Length) : index.ToString();
        });
        return result.Trim();
    }

    /// <summary>
    /// 统一校验：非法字符、空名、组内重名、与磁盘已有文件冲突。
    /// 磁盘冲突判定会排除「本批次自己的源文件」，这样 a→b、b→a 的互换是合法的。
    /// </summary>
    private static void Validate(List<RenamePlanItem> plan)
    {
        // 本批次所有源路径（不区分大小写，Windows/Android 均按文件名语义处理）
        var sources = new HashSet<string>(
            plan.Select(i => i.SourcePath),
            StringComparer.OrdinalIgnoreCase);

        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in plan)
        {
            if (item.Error is not null) continue;

            var name = item.NewName;
            if (string.IsNullOrWhiteSpace(name))
            {
                item.Error = "新名为空";
                continue;
            }
            if (name.IndexOfAny(InvalidChars) >= 0)
            {
                item.Error = "含非法字符";
                continue;
            }

            var directory = Path.GetDirectoryName(item.SourcePath) ?? "";
            var target = Path.Combine(directory, name);

            // 组内重名：同一目标被两个源指向
            var key = target.Replace('\\', '/');
            if (seen.TryGetValue(key, out var firstSource))
            {
                item.Error = $"与 {Path.GetFileName(firstSource)} 的目标重名";
                continue;
            }
            seen[key] = item.SourcePath;

            // 磁盘已有同名项，且不在本批次源文件里（本批次源会被移走）
            if ((File.Exists(target) || Directory.Exists(target)) && !sources.Contains(target))
                item.Error = "目标已存在";
        }
    }

    /// <summary>
    /// 执行计划。采用两步改名（先全部改成临时名，再改成目标名），
    /// 这样 a→b、b→a 这类链式/互换重命名不会互相覆盖。返回实际改名的条数。
    /// </summary>
    public static int Apply(IReadOnlyList<RenamePlanItem> plan)
    {
        var staged = new List<(string Temp, string Target)>();
        foreach (var item in plan)
        {
            if (!item.CanApply) continue;

            var directory = Path.GetDirectoryName(item.SourcePath)!;
            var temp = Path.Combine(directory, ".pebble-rename-" + Guid.NewGuid().ToString("N"));

            if (Directory.Exists(item.SourcePath)) Directory.Move(item.SourcePath, temp);
            else File.Move(item.SourcePath, temp);

            staged.Add((temp, Path.Combine(directory, item.NewName)));
        }

        var moved = 0;
        foreach (var (temp, target) in staged)
        {
            if (Directory.Exists(temp)) Directory.Move(temp, target);
            else File.Move(temp, target);
            moved++;
        }

        return moved;
    }
}
