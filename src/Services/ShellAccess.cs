using System.Text;

#if ANDROID
using Android.Runtime;
#endif

namespace Agate.Services;

/// <summary>Shizuku 通道状态。</summary>
public enum ShellState
{
    /// <summary>没装 Shizuku，或服务没在跑（重启后需要重新在 Shizuku 里点「启动」）。</summary>
    Unavailable = 0,

    /// <summary>Shizuku 在跑，但本应用还没拿到授权。</summary>
    NoPermission = 1,

    /// <summary>可以执行命令。</summary>
    Ready = 2
}

/// <summary>shell 列目录得到的一行。</summary>
public sealed record ShellEntry(string Name, string FullPath, bool IsDirectory, long Size);

/// <summary>
/// 受限目录（Android/data、Android/obb）的访问通道。
///
/// 这两个目录从 Android 11 起对第三方应用封锁，MANAGE_EXTERNAL_STORAGE 也不覆盖。
/// 有 Shizuku 时借它的 shell 身份执行命令绕开；没有时所有方法都安全降级。
/// </summary>
public static class ShellAccess
{
    private const int PermissionRequestCode = 4521;

    public static bool IsAndroid => OperatingSystem.IsAndroid();

    /// <summary>当前状态。非 Android 平台永远 Unavailable。</summary>
    public static async Task<ShellState> GetStateAsync()
    {
        if (!IsAndroid) return ShellState.Unavailable;
        var value = await MainThread.InvokeOnMainThreadAsync(() => CallInt("state"));

        // 桥接层调不通时 CallInt 返回 -1，这里统一归为「不可用」，
        // 免得被上层当成「装了但没授权」而弹错提示。
        return value is 0 or 1 or 2 ? (ShellState)value : ShellState.Unavailable;
    }

    /// <summary>Shizuku 服务版本，未连接为 -1。</summary>
    public static async Task<int> GetServiceVersionAsync()
    {
        if (!IsAndroid) return -1;
        return await MainThread.InvokeOnMainThreadAsync(() => CallInt("version"));
    }

    /// <summary>执行命令用的 uid（正常是 2000 shell），不可用时 -1。</summary>
    public static async Task<int> GetShellUidAsync()
    {
        if (!IsAndroid) return -1;
        return await MainThread.InvokeOnMainThreadAsync(() => CallInt("shellUid"));
    }

    /// <summary>路径是否落在系统封锁的 Android/data、Android/obb 里。</summary>
    public static bool IsRestricted(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        foreach (var segment in new[] { "/Android/data", "/Android/obb" })
        {
            if (normalized.EndsWith(segment, StringComparison.OrdinalIgnoreCase)) return true;
            if (normalized.Contains(segment + "/", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// 请求 Shizuku 授权。用户需要在弹出的 Shizuku 对话框里点允许。
    /// 返回是否最终拿到权限。
    /// </summary>
    public static async Task<bool> RequestPermissionAsync(int timeoutMs = 90_000)
    {
        if (!IsAndroid) return false;

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            CallVoid("resetPermissionResult");
            CallVoidInt("requestPermission", PermissionRequestCode);
        });

        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(300);
            var result = await MainThread.InvokeOnMainThreadAsync(() => CallInt("lastPermissionResult"));
            if (result == int.MinValue) continue;           // 还没回应
            return result == 0;                              // 0 = PERMISSION_GRANTED
        }
        return false;
    }

    /// <summary>执行命令，返回 (退出码, 标准输出)。不可用时码为 -1。</summary>
    public static async Task<(int Code, string Output)> RunAsync(string command)
    {
        if (!IsAndroid) return (-1, "Shizuku 仅在 Android 上可用");

        var raw = await MainThread.InvokeOnMainThreadAsync(() => CallString("exec", command));
        return SplitResult(raw);
    }

    /// <summary>列目录。失败时返回空列表，错误信息通过 error 传出。</summary>
    public static async Task<(List<ShellEntry> Items, string Error)> ListAsync(string directory)
    {
        var (code, output) = await RunAsync(ListCommand(directory));
        if (code != 0) return (new List<ShellEntry>(), output.Trim());

        var items = new List<ShellEntry>();
        foreach (var line in output.Split('\n'))
        {
            var text = line.TrimEnd('\r');
            if (text.Length == 0) continue;

            // 格式：大小|类型|绝对路径。类型里有空格（regular file），所以按前两个分隔符切。
            var first = text.IndexOf('|');
            if (first <= 0) continue;
            var second = text.IndexOf('|', first + 1);
            if (second < 0) continue;

            var sizeText = text[..first];
            var typeText = text[(first + 1)..second];
            var fullPath = text[(second + 1)..];
            if (fullPath.Length == 0) continue;

            _ = long.TryParse(sizeText, out var size);
            items.Add(new ShellEntry(
                Path.GetFileName(fullPath),
                fullPath,
                typeText.Equals("directory", StringComparison.OrdinalIgnoreCase),
                size));
        }
        return (items, string.Empty);
    }

    /// <summary>
    /// 把受限目录里的文件复制到应用自己的外部目录，返回本地可读路径。
    /// shell 无法写进应用私有目录（SELinux），所以落在 Android/data/&lt;本应用&gt;/files 下。
    /// </summary>
    public static async Task<(string? LocalPath, string Error)> CopyToLocalAsync(string remotePath)
    {
        var tempDir = GetTempDirectory();
        if (tempDir is null) return (null, "取不到应用缓存目录");

        var name = Path.GetFileName(remotePath);
        var localPath = Path.Combine(tempDir, $"{DateTime.Now:HHmmss}_{name}");

        var (code, output) = await RunAsync($"cp -f {Quote(remotePath)} {Quote(localPath)}");
        if (code != 0) return (null, output.Trim());
        if (!File.Exists(localPath)) return (null, "复制完成但目标文件不存在");
        return (localPath, string.Empty);
    }

    public static async Task<(bool Ok, string Error)> DeleteAsync(string path, bool recursive)
    {
        var (code, output) = await RunAsync($"rm {(recursive ? "-rf" : "-f")} {Quote(path)}");
        return (code == 0, output.Trim());
    }

    public static async Task<(bool Ok, string Error)> RenameAsync(string from, string to)
    {
        var (code, output) = await RunAsync($"mv -f {Quote(from)} {Quote(to)}");
        return (code == 0, output.Trim());
    }

    /// <summary>清掉复制过来的临时文件。</summary>
    public static void CleanTemp()
    {
        var dir = GetTempDirectory();
        if (dir is null) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                try { File.Delete(file); } catch { /* 忽略单个文件失败 */ }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("shell-clean", ex);
        }
    }

    // ---------------------------------------------------------------- 内部

    private static string ListCommand(string directory)
        => $"find {Quote(directory)} -mindepth 1 -maxdepth 1 -exec stat -c '%s|%F|%n' {{}} +";

    private static (int Code, string Output) SplitResult(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return (-1, "空响应");
        var index = raw.IndexOf('\n');
        if (index < 0) return (-1, raw);
        _ = int.TryParse(raw[..index], out var code);
        return (code, raw[(index + 1)..]);
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private static string? GetTempDirectory()
    {
        try
        {
            var context = Android.App.Application.Context;
            var root = context.GetExternalFilesDir("shelltmp");
            if (root is null) return null;
            var dir = root.AbsolutePath;
            Directory.CreateDirectory(dir);
            return dir;
        }
        catch (Exception ex)
        {
            AppLog.Error("shell-tmp", ex);
            return null;
        }
    }

#if ANDROID
    private const string BridgeClass = "com/agate/bridge/ShizukuShell";

    private static IntPtr BridgeType() => JNIEnv.FindClass(BridgeClass);

    private static int CallInt(string method)
    {
        try
        {
            var type = BridgeType();
            var id = JNIEnv.GetStaticMethodID(type, method, "()I");
            return JNIEnv.CallStaticIntMethod(type, id);
        }
        catch (Exception ex)
        {
            AppLog.Error("shizuku-" + method, ex);
            return -1;
        }
    }

    private static void CallVoid(string method)
    {
        try
        {
            var type = BridgeType();
            var id = JNIEnv.GetStaticMethodID(type, method, "()V");
            JNIEnv.CallStaticVoidMethod(type, id);
        }
        catch (Exception ex)
        {
            AppLog.Error("shizuku-" + method, ex);
        }
    }

    private static void CallVoidInt(string method, int value)
    {
        try
        {
            var type = BridgeType();
            var id = JNIEnv.GetStaticMethodID(type, method, "(I)V");
            JNIEnv.CallStaticVoidMethod(type, id, new JValue(value));
        }
        catch (Exception ex)
        {
            AppLog.Error("shizuku-" + method, ex);
        }
    }

    private static string CallString(string method, string argument)
    {
        try
        {
            var type = BridgeType();
            var id = JNIEnv.GetStaticMethodID(type, method, "(Ljava/lang/String;)Ljava/lang/String;");
            var javaArgument = JNIEnv.NewString(argument);
            try
            {
                var result = JNIEnv.CallStaticObjectMethod(type, id, new JValue(javaArgument));
                return JNIEnv.GetString(result, JniHandleOwnership.TransferLocalRef) ?? string.Empty;
            }
            finally
            {
                JNIEnv.DeleteLocalRef(javaArgument);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("shizuku-" + method, ex);
            return "-1\n" + ex.Message;
        }
    }
#else
    private static int CallInt(string method) => -1;

    private static void CallVoid(string method) { }

    private static void CallVoidInt(string method, int value) { }

    private static string CallString(string method, string argument) => "-1\n仅 Android 可用";
#endif
}
