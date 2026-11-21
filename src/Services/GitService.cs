using LibGit2Sharp;
using Agate.Models;

namespace Agate.Services;

/// <summary>一次 Commit + Push 的结果。Success=false 时 Message 为可直接展示给用户的错误说明。</summary>
public sealed record CommitPushResult(bool Success, string Message, string? CommitSha = null);

/// <summary>推送前是否先拉取远端。</summary>
public enum PullStrategy
{
    None = 0,
    Rebase = 1,
    Merge = 2
}

/// <summary>提交后的推送策略，由设置页写入。</summary>
public sealed record GitPushPolicy(bool AutoPush, bool Force, PullStrategy Pull)
{
    public static GitPushPolicy Default { get; } = new(true, false, PullStrategy.None);
}

/// <summary>
/// 纯 Git 封装，不依赖 MAUI 类型，可在任意 .NET 目标上运行与单测。
/// 所有公开方法均为异步：LibGit2Sharp 是同步阻塞的，统一用 Task.Run 移出 UI 线程。
/// </summary>
public sealed class GitService
{
    static GitService()
    {
        // libgit2 1.8+ 会校验「仓库目录是否属于当前用户」（CVE-2022-24765 的修复）。
        // Android 的共享存储是 FUSE 挂载，/storage/emulated/0 下所有文件属主都是 media_rw，
        // 应用永远不可能"拥有"它们，于是 init / open 一律报
        // "repository path '...' is not owned by current user"。
        // 在移动端必须关掉这个校验，否则任何手机存储里的仓库都打不开。
        try { GlobalSettings.SetOwnerValidation(false); }
        catch (Exception ex) { AppLog.Error("git-owner-validation", ex); }
    }

    /// <summary>当前生效的推送策略。App 启动时从设置读入。</summary>
    public static GitPushPolicy Policy { get; set; } = GitPushPolicy.Default;

    /// <summary>
    /// 按远端 URL 解析凭据的回调，由 App 层注入（凭据存在 SecureStorage，取值是异步的）。
    /// 保持本类不直接依赖 MAUI。
    /// </summary>
    public static Func<string?, Task<(string Username, string Token)?>>? CredentialResolver { get; set; }

    private readonly string? _token;
    private readonly string _username;

    /// <param name="token">PAT / OAuth Token。为 null 时按匿名访问（仅限公开仓库）。</param>
    /// <param name="username">GitHub 用任意非空值即可（常见 "x-access-token"），GitLab 用 "oauth2"。</param>
    public GitService(string? token = null, string username = "x-access-token")
    {
        _token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        _username = string.IsNullOrWhiteSpace(username) ? "x-access-token" : username;
    }

    // ---------------------------------------------------------------- Clone

    public Task CloneAsync(
        string url,
        string localPath,
        IProgress<TransferProgress>? progress = null,
        CancellationToken ct = default)
        => Task.Run(async () =>
        {
            ct.ThrowIfCancellationRequested();

            var target = RepoPath.NormalizeAbsolute(localPath);
            if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
                throw new InvalidOperationException($"目标目录非空，无法 Clone：{target}");

            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);

            var credential = await ResolveCredentialForUrlAsync(url);
            var options = new CloneOptions { Checkout = true };
            options.FetchOptions.CredentialsProvider = CreateCredentialProvider(credential);
            if (progress is not null)
                options.FetchOptions.OnTransferProgress = p => { progress.Report(p); return true; };

            Repository.Clone(url, target, options);
        }, ct);

    // ------------------------------------------------------------- 文件列表

    /// <summary>读取仓库中某一层目录的内容，并附上 Git 状态。relativePath 为空表示仓库根。</summary>
    public Task<IReadOnlyList<FileItem>> GetChildrenAsync(
        string repoRoot,
        string relativePath = "",
        CancellationToken ct = default)
        => Task.Run(() => GetChildren(repoRoot, relativePath), ct);

    public IReadOnlyList<FileItem> GetFilesWithStatus(string repoPath)
        => GetChildren(repoPath, string.Empty);

    public IReadOnlyList<FileItem> GetChildren(string repoRoot, string relativePath = "")
    {
        var root = RepoPath.NormalizeAbsolute(repoRoot);
        if (!IsRepository(root))
            throw new InvalidOperationException($"不是有效的 Git 仓库：{root}");

        var rel = RepoPath.NormalizeRelative(relativePath);
        var abs = RepoPath.Combine(root, rel);

        using var repo = new Repository(root);
        var statusMap = BuildStatusMap(repo);

        var items = new List<FileItem>();

        foreach (var dir in Directory.EnumerateDirectories(abs).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(dir);
            if (name == ".git") continue;

            var relDir = RepoPath.JoinRelative(rel, name);
            items.Add(new FileItem
            {
                Name = name,
                FullPath = dir,
                RelativePath = relDir,
                IsDirectory = true,
                GitStatus = AggregateDirectoryStatus(statusMap, relDir)
            });
        }

        foreach (var file in Directory.EnumerateFiles(abs).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(file);
            var relFile = RepoPath.JoinRelative(rel, name);
            statusMap.TryGetValue(relFile, out var status);

            items.Add(new FileItem
            {
                Name = name,
                FullPath = file,
                RelativePath = relFile,
                IsDirectory = false,
                GitStatus = status
            });
        }

        // 已删除但仍在索引里的文件在磁盘上不存在，需要从状态表补回来，否则永远看不到 "D"。
        foreach (var (path, status) in statusMap)
        {
            if (status != GitFileStatus.Deleted) continue;
            if (!TryGetDirectChildName(rel, path, out var childName)) continue;

            items.Add(new FileItem
            {
                Name = childName,
                FullPath = RepoPath.Combine(root, path),
                RelativePath = path,
                IsDirectory = false,
                GitStatus = GitFileStatus.Deleted
            });
        }

        return items;
    }

    // ------------------------------------------------ 工作区变更（Git 抽屉数据源）

    /// <summary>整个工作区的未提交变更（M / U / D），供 Git 抽屉展示。</summary>
    public Task<IReadOnlyList<FileItem>> GetWorkingChangesAsync(string repoRoot, CancellationToken ct = default)
        => Task.Run(() => GetWorkingChanges(repoRoot), ct);

    public IReadOnlyList<FileItem> GetWorkingChanges(string repoRoot)
    {
        var root = RepoPath.NormalizeAbsolute(repoRoot);
        if (!IsRepository(root)) return Array.Empty<FileItem>();

        using var repo = new Repository(root);
        // RecurseUntrackedDirs 必须开，否则未跟踪目录只会返回目录本身而不是里面的文件。
        var status = repo.RetrieveStatus(new StatusOptions { IncludeUntracked = true, RecurseUntrackedDirs = true });

        var items = new List<FileItem>();
        foreach (var entry in status)
        {
            var state = MapStatus(entry.State);
            if (state == GitFileStatus.Unmodified) continue;

            var rel = RepoPath.NormalizeRelative(entry.FilePath);
            items.Add(new FileItem
            {
                Name = Path.GetFileName(rel),
                FullPath = RepoPath.Combine(root, rel),
                RelativePath = rel,
                IsDirectory = false,
                GitStatus = state
            });
        }

        return items.OrderBy(i => i.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ------------------------------------------------------ Stage / 单文件操作

    /// <summary>暂存单个文件。relativePath 相对仓库根、'/' 分隔。</summary>
    public void StageFile(string repoRoot, string relativePath)
    {
        using var repo = new Repository(RepoPath.NormalizeAbsolute(repoRoot));
        Commands.Stage(repo, RepoPath.NormalizeRelative(relativePath));
    }

    /// <summary>批量暂存。返回成功暂存的数量。</summary>
    public Task<int> StageFilesAsync(string repoRoot, IReadOnlyList<string> relativePaths, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var root = RepoPath.NormalizeAbsolute(repoRoot);
            using var repo = new Repository(root);

            var staged = 0;
            foreach (var rel in relativePaths)
            {
                Commands.Stage(repo, RepoPath.NormalizeRelative(rel));
                staged++;
            }

            return staged;
        }, ct);

    /// <summary>
    /// 获取工作区某个文件的 diff 文本（相对 HEAD）。
    /// 用 repo.Diff.Compare&lt;Patch&gt;(null, DiffTargets.WorkingDirectory, pathspec) 拿 Patch 后拼 text。
    /// 没有改动时返回 null。
    /// </summary>
    public Task<string?> GetFileDiffAsync(string repoRoot, string relativePath, CancellationToken ct = default)
        => Task.Run(() => GetFileDiff(repoRoot, relativePath), ct);

    public string? GetFileDiff(string repoRoot, string relativePath)
    {
        var root = RepoPath.NormalizeAbsolute(repoRoot);
        using var repo = new Repository(root);

        var rel = RepoPath.NormalizeRelative(relativePath);

        var patch = repo.Diff.Compare<Patch>(null, DiffTargets.WorkingDirectory, new[] { rel });
        if (patch is null) return null;

        var text = patch.Content;
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    public void UnstageFile(string repoRoot, string relativePath)
    {
        using var repo = new Repository(RepoPath.NormalizeAbsolute(repoRoot));
        Commands.Unstage(repo, RepoPath.NormalizeRelative(relativePath));
    }

    /// <summary>绝对路径重载：自动向上找仓库根。注意目标必须存在，已删除的文件请用 (repoRoot, relativePath) 版本。</summary>
    public void StageFile(string absolutePath)
    {
        var (repo, rel) = OpenByPath(absolutePath);
        using (repo) Commands.Stage(repo, rel);
    }

    public void UnstageFile(string absolutePath)
    {
        var (repo, rel) = OpenByPath(absolutePath);
        using (repo) Commands.Unstage(repo, rel);
    }

    /// <summary>删除文件或目录。删除后不自动提交，作为工作区变更留给用户 staged。</summary>
    public void DeleteFile(string path)
    {
        var full = RepoPath.NormalizeAbsolute(path);

        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        else if (File.Exists(full)) File.Delete(full);
    }

    private static (Repository Repo, string RelativePath) OpenByPath(string absolutePath)
    {
        var full = RepoPath.NormalizeAbsolute(absolutePath);

        var discovered = Repository.Discover(full)
            ?? throw new InvalidOperationException($"不在 Git 仓库中：{full}");

        var repo = new Repository(discovered);
        var workDir = repo.Info.WorkingDirectory
            ?? throw new InvalidOperationException("裸仓库没有工作区");

        return (repo, Path.GetRelativePath(workDir, full).Replace('\\', '/'));
    }

    // ---------------------------------------------------------- Commit + Push

    public Task<CommitPushResult> CommitAndPushAsync(
        string repoRoot,
        string commitMessage,
        string? token = null,
        CancellationToken ct = default)
        => Task.Run(async () =>
        {
            var credential = await ResolveCredentialAsync(repoRoot, token);
            return CommitAndPushCore(repoRoot, commitMessage, credential, null);
        }, ct);

    /// <summary>同步入口，签名即 (repoPath, message, token)。</summary>
    public CommitPushResult CommitAndPush(string repoRoot, string commitMessage, string? token = null)
        => CommitAndPushCore(repoRoot, commitMessage, FromToken(token), null);

    /// <summary>只提交指定文件（相对仓库根）。空集合等价于提交全部。</summary>
    public CommitPushResult CommitPaths(string repoRoot, string commitMessage, IReadOnlyList<string> relativePaths, string? token = null)
        => CommitAndPushCore(repoRoot, commitMessage, FromToken(token), relativePaths);

    private (string Username, string Token)? FromToken(string? token)
        => string.IsNullOrWhiteSpace(token) ? null : (_username, token.Trim());

    /// <summary>
    /// 决定这次推送用哪个凭据：显式传入的 token 优先，否则按仓库 origin 的
    /// 主机名去凭据库里找。
    /// </summary>
    private static async Task<(string Username, string Token)?> ResolveCredentialAsync(string repoRoot, string? token)
    {
        if (!string.IsNullOrWhiteSpace(token)) return ("x-access-token", token.Trim());

        var resolver = CredentialResolver;
        if (resolver is null) return null;

        try
        {
            using var repo = new Repository(RepoPath.NormalizeAbsolute(repoRoot));
            var url = repo.Network.Remotes["origin"]?.Url;
            if (string.IsNullOrWhiteSpace(url)) return null;
            return await resolver(url);
        }
        catch (Exception ex)
        {
            AppLog.Error("cred-resolve", ex);
            return null;
        }
    }

    /// <summary>按 URL 直接解析凭据（Clone 尚无本地仓库时用）。</summary>
    private static async Task<(string Username, string Token)?> ResolveCredentialForUrlAsync(string url)
    {
        var resolver = CredentialResolver;
        if (resolver is null) return null;

        try { return await resolver(url); }
        catch (Exception ex) { AppLog.Error("cred-resolve-url", ex); return null; }
    }

    private CommitPushResult CommitAndPushCore(        string repoRoot,
        string commitMessage,
        (string Username, string Token)? credential,
        IReadOnlyList<string>? paths)
    {
        if (string.IsNullOrWhiteSpace(commitMessage))
            return new CommitPushResult(false, "提交信息不能为空");

        var root = RepoPath.NormalizeAbsolute(repoRoot);
        if (!IsRepository(root))
            return new CommitPushResult(false, $"不是有效的 Git 仓库：{root}");

        using var repo = new Repository(root);

        if (!repo.RetrieveStatus(new StatusOptions()).IsDirty)
            return new CommitPushResult(false, "工作区没有改动");

        if (paths is { Count: > 0 })
        {
            // 先清空暂存区再只加勾选项，避免未勾选的变更被顺带提交。
            Commands.Unstage(repo, "*");
            foreach (var path in paths)
                Commands.Stage(repo, path);
        }
        else
        {
            // pathspec 形式可以同时暂存新增、修改与删除（'*' 覆盖工作区内全部变更）。
            Commands.Stage(repo, "*");
        }

        Commit commit;
        try
        {
            var signature = EnsureSignature(repo);
            commit = repo.Commit(commitMessage.Trim(), signature, signature);
        }
        catch (EmptyCommitException)
        {
            return new CommitPushResult(false, "暂存区为空，没有可提交内容");
        }

        var policy = Policy;
        if (!policy.AutoPush)
            return new CommitPushResult(true, $"已提交 {commit.Sha[..7]}（设置为手动推送，未推送）", commit.Sha);

        var credentials = CreateCredentialProvider(credential);

        try
        {
            if (policy.Pull != PullStrategy.None) PullBeforePush(repo, credentials, policy.Pull);
            PushHead(repo, credentials, policy.Force);
        }
        catch (Exception ex) when (ex is LibGit2SharpException or InvalidOperationException or CheckoutConflictException)
        {
            // 提交已经落盘，推送失败不能当成 "什么都没发生"。
            return new CommitPushResult(false, $"已提交但推送失败：{ExplainPushError(ex.Message)}", commit.Sha);
        }

        var mode = policy.Force ? "已强制提交并推送" : "已提交并推送";
        return new CommitPushResult(true, $"{mode} {commit.Sha[..7]}", commit.Sha);
    }

    /// <summary>把 libgit2 的英文错误翻成能直接照做的中文提示。</summary>
    private static string ExplainPushError(string message)
    {
        if (message.Contains("authentication", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("401", StringComparison.Ordinal))
            return "认证失败。请到「设置 → Token 管理」确认该平台的 Token 有效，且勾了仓库读写权限。";

        if (message.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("rejected", StringComparison.OrdinalIgnoreCase))
            return "远端有你本地没有的提交。可把推送策略改成「先拉取（rebase）」再推，或手动强制推送。";

        return message;
    }

    /// <summary>推送前先把远端变化并进来，减少 non-fast-forward 拒绝。</summary>
    private static void PullBeforePush(
        Repository repo,
        LibGit2Sharp.Handlers.CredentialsHandler credentials,
        PullStrategy strategy)
    {
        var head = repo.Head;
        if (head.TrackedBranch is null) return;

        var remoteName = head.TrackedBranch.RemoteName;
        var remote = string.IsNullOrWhiteSpace(remoteName) ? null : repo.Network.Remotes[remoteName];
        if (remote is null) return;

        Commands.Fetch(
            repo,
            remote.Name,
            remote.FetchRefSpecs.Select(spec => spec.Specification),
            new FetchOptions { CredentialsProvider = credentials },
            "agate: fetch before push");

        var signature = EnsureSignature(repo);

        if (strategy == PullStrategy.Merge)
        {
            var result = repo.Merge(head.TrackedBranch, signature, new MergeOptions());
            if (result.Status == MergeStatus.Conflicts)
            {
                repo.Reset(ResetMode.Hard, head.Tip);
                throw new InvalidOperationException("远端改动与本地冲突，已放弃拉取，请手动处理后再推。");
            }
            return;
        }

        var identity = new Identity(signature.Name, signature.Email);
        var rebase = repo.Rebase.Start(head, head.TrackedBranch, null, identity, new RebaseOptions());
        if (rebase.Status == RebaseStatus.Conflicts)
        {
            repo.Rebase.Abort();
            throw new InvalidOperationException("远端改动与本地冲突，已中止 rebase，请手动处理后再推。");
        }
        if (rebase.Status == RebaseStatus.Stop)
        {
            repo.Rebase.Abort();
            throw new InvalidOperationException("rebase 未能自动完成，已中止，请手动处理后再推。");
        }
    }

    private void PushHead(
        Repository repo,
        LibGit2Sharp.Handlers.CredentialsHandler credentials,
        bool force = false)
    {
        var options = new PushOptions { CredentialsProvider = credentials };
        var head = repo.Head;

        if (force)
        {
            // 强制推送：refspec 前缀 '+' 即 --force，覆盖远端分支。
            var forceRemote = repo.Network.Remotes[head.TrackedBranch?.RemoteName ?? "origin"]
                              ?? throw new InvalidOperationException("仓库没有 origin 远程");
            repo.Network.Push(
                forceRemote,
                $"+refs/heads/{head.FriendlyName}:refs/heads/{head.TrackedBranch?.FriendlyName ?? head.FriendlyName}",
                options);
            return;
        }

        // 有 upstream 直接推当前分支；否则显式指定 origin + refspec，避免 "no tracking information"。
        if (head.TrackedBranch is not null)
        {
            repo.Network.Push(head, options);
            return;
        }

        var remote = repo.Network.Remotes["origin"]
                     ?? throw new InvalidOperationException("仓库没有 origin 远程");

        repo.Network.Push(remote, $"refs/heads/{head.FriendlyName}:refs/heads/{head.FriendlyName}", options);
    }

    // ---------------------------------------------------------------- Helpers

    public static bool IsRepository(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return Repository.IsValid(path); }
        catch { return false; }
    }

    /// <summary>在指定目录初始化一个新的 Git 仓库（已存在则拒绝，避免覆盖原有 .git）。</summary>
    public static void InitRepository(string path)
    {
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"目录不存在：{path}");
        if (IsRepository(path))
            throw new InvalidOperationException("该目录已经是 Git 仓库");

        // 先探测目录可写。Android 上最常见的失败其实是「没有存储写入权限」，
        // 直接调 libgit2 只会给一句 failed to make directory，看不出原因。
        ProbeWritable(path);

        try
        {
            Repository.Init(path);
        }
        catch (LibGit2SharpException ex)
        {
            TryCleanupBrokenGitDir(path);
            throw new InvalidOperationException(ExplainInitError(ex.Message, path), ex);
        }

        if (!IsRepository(path))
            throw new InvalidOperationException($"初始化没有生效：{path} 下没有生成有效的 .git");
    }

    /// <summary>
    /// 初始化中途失败时，libgit2 可能留下半个 .git。它既不是有效仓库，
    /// 又会让「该目录已经是 Git 仓库」的判重逻辑挡路，这里清掉以便重试。
    /// </summary>
    private static void TryCleanupBrokenGitDir(string path)
    {
        try
        {
            var gitDir = Path.Combine(path, ".git");
            if (!Directory.Exists(gitDir)) return;
            if (IsRepository(path)) return;

            Directory.Delete(gitDir, recursive: true);
        }
        catch (Exception ex)
        {
            AppLog.Error("git-init-cleanup", ex);
        }
    }

    private static void ProbeWritable(string path)
    {
        var probe = Path.Combine(path, ".agate-write-test");
        try
        {
            File.WriteAllText(probe, "agate");
            File.Delete(probe);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UnauthorizedAccessException(
                "该目录没有写入权限，无法创建 .git。\n\n" +
                "如果是手机存储里的普通目录，请到系统设置给 Agate 开启「所有文件访问」；" +
                "如果是 Android/data、Android/obb，需要先配置 Shizuku。", ex);
        }
        catch (IOException ex)
        {
            throw new IOException($"目录不可写：{ex.Message}", ex);
        }
    }

    private static string ExplainInitError(string message, string path)
    {
        if (message.Contains("not owned", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("ownership", StringComparison.OrdinalIgnoreCase))
            return "Git 判定该目录不属于本应用。Android 共享存储的所有文件属主都是系统媒体用户，" +
                   "本应用已主动关闭这项校验；若仍出现，请把仓库放到应用自己的目录后再试。";

        if (message.Contains("exists", StringComparison.OrdinalIgnoreCase))
            return $"{path} 下已经存在 .git，但当前不是一个有效仓库。可以先把它改名备份，再重新初始化。";

        if (message.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("denied", StringComparison.OrdinalIgnoreCase))
            return "系统拒绝了写入操作。请检查「所有文件访问」权限，或换一个可写目录。";

        if (message.Contains("space", StringComparison.OrdinalIgnoreCase))
            return "存储空间不足，无法创建 .git。";

        return $"初始化失败：{message}";
    }

    /// <summary>
    /// 从给定路径向上逐层查找 .git，返回仓库根；找不到返回 null。
    /// 用于「在手机存储的某个子目录里也能识别出它属于某个 Git 仓库」。
    /// </summary>
    public static string? DiscoverRepository(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            var current = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (current is null) return null;

            while (true)
            {
                if (IsRepository(current)) return current;

                var parent = Path.GetDirectoryName(current);
                if (parent is null || parent == current) return null;

                current = parent;
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string GetBranchName(string repoRoot)
    {
        using var repo = new Repository(RepoPath.NormalizeAbsolute(repoRoot));
        return repo.Head.FriendlyName;
    }

    /// <summary>LibGit2Sharp 0.32.0 期望的原生库文件名（LibGit2Sharp 按 libgit2-&lt;sha&gt; 硬编码查找）。</summary>
    public const string ExpectedNativeLibrary = "libgit2-5853918.so";

    /// <summary>
    /// 探测原生库是否可用。首次访问 GlobalSettings.Version 会真正 dlopen，
    /// 因此这是"还没写业务代码就能判断环境对不对"的最早检测点。
    /// </summary>
    public static string ProbeNativeLibrary()
    {
        try
        {
            var v = GlobalSettings.Version;
            return $"libgit2 {v.InformationalVersion} (sha {v.LibGit2CommitSha})";
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    public static bool IsNativeLibraryAvailable() => ProbeNativeLibrary().StartsWith("libgit2 ", StringComparison.Ordinal);

    /// <summary>
    /// 生成凭据回调。tokenOverride 非空时优先于构造函数里的 token，
    /// 用于"这次 push 用某个临时 PAT"的场景。
    /// </summary>
    private LibGit2Sharp.Handlers.CredentialsHandler CreateCredentialProvider(
        (string Username, string Token)? credential = null)
    {
        var resolved = credential
                       ?? (string.IsNullOrWhiteSpace(_token) ? null : (_username, _token));

        return (url, usernameFromUrl, types) =>
        {
            if (resolved is null) return null;
            var (user, secret) = resolved.Value;
            return new UsernamePasswordCredentials
            {
                Username = string.IsNullOrWhiteSpace(user) ? _username : user,
                Password = secret
            };
        };
    }

    /// <summary>设备上通常没有全局 gitconfig，缺少 user.name/email 时 Commit 会直接抛异常。</summary>
    private static Signature EnsureSignature(Repository repo)
    {
        var name = repo.Config.Get<string>("user.name")?.Value;
        var email = repo.Config.Get<string>("user.email")?.Value;

        if (string.IsNullOrWhiteSpace(name))
        {
            name = "Agate";
            repo.Config.Set("user.name", name, ConfigurationLevel.Local);
        }

        if (string.IsNullOrWhiteSpace(email))
        {
            email = "agate@localhost";
            repo.Config.Set("user.email", email, ConfigurationLevel.Local);
        }

        return new Signature(name, email, DateTimeOffset.Now);
    }

    private static Dictionary<string, GitFileStatus> BuildStatusMap(Repository repo)
    {
        var map = new Dictionary<string, GitFileStatus>(StringComparer.Ordinal);

        foreach (var entry in repo.RetrieveStatus(new StatusOptions { IncludeUntracked = true }))
        {
            var status = MapStatus(entry.State);
            if (status == GitFileStatus.Unmodified) continue;
            map[RepoPath.NormalizeRelative(entry.FilePath)] = status;
        }

        return map;
    }

    private static GitFileStatus MapStatus(FileStatus state)
    {
        if ((state & FileStatus.Conflicted) != 0) return GitFileStatus.Modified;
        if ((state & (FileStatus.NewInIndex | FileStatus.NewInWorkdir)) != 0) return GitFileStatus.New;
        if ((state & (FileStatus.DeletedFromIndex | FileStatus.DeletedFromWorkdir)) != 0) return GitFileStatus.Deleted;
        if ((state & (FileStatus.ModifiedInIndex | FileStatus.ModifiedInWorkdir
                      | FileStatus.RenamedInIndex | FileStatus.RenamedInWorkdir
                      | FileStatus.TypeChangeInIndex | FileStatus.TypeChangeInWorkdir)) != 0)
            return GitFileStatus.Modified;

        return GitFileStatus.Unmodified;
    }

    /// <summary>目录状态取其子树内优先级最高的状态，让折叠目录也能看出有改动。</summary>
    private static GitFileStatus AggregateDirectoryStatus(
        Dictionary<string, GitFileStatus> statusMap, string directoryRel)
    {
        if (directoryRel.Length == 0) return GitFileStatus.Unmodified;

        var prefix = directoryRel + "/";
        var best = GitFileStatus.Unmodified;

        foreach (var (path, status) in statusMap)
        {
            if (!path.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (StatusPriority(status) > StatusPriority(best)) best = status;
        }

        return best;
    }

    private static int StatusPriority(GitFileStatus status) => status switch
    {
        GitFileStatus.Modified => 3,
        GitFileStatus.New => 2,
        GitFileStatus.Deleted => 1,
        _ => 0
    };

    /// <summary>判断 childRel 是否是 parentRel 的直接子项（不含层级）。</summary>
    private static bool TryGetDirectChildName(string parentRel, string childRel, out string name)
    {
        name = string.Empty;

        var prefix = parentRel.Length == 0 ? string.Empty : parentRel + "/";
        if (!childRel.StartsWith(prefix, StringComparison.Ordinal)) return false;

        var rest = childRel[prefix.Length..];
        if (rest.Length == 0 || rest.Contains('/')) return false;

        name = rest;
        return true;
    }
}

/// <summary>
/// 路径工具：对外一律以 '/' 表示仓库内相对路径，落盘时再转平台分隔符，
/// 并拒绝 "../" 这类越界路径。
/// </summary>
internal static class RepoPath
{
    public static string NormalizeRelative(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;

        var p = path.Replace('\\', '/').Trim();
        while (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];

        return p.Trim('/');
    }

    public static string NormalizeAbsolute(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("路径不能为空", nameof(path));

        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);

        if (root is not null && full.Length > root.Length)
            full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return full;
    }

    public static string JoinRelative(string left, string right)
    {
        var l = NormalizeRelative(left);
        var r = NormalizeRelative(right);

        if (l.Length == 0) return r;
        if (r.Length == 0) return l;
        return l + "/" + r;
    }

    /// <summary>把相对路径解析为绝对路径，越出 root 时抛异常。</summary>
    public static string Combine(string root, string relative)
    {
        var rootFull = NormalizeAbsolute(root);
        var rel = NormalizeRelative(relative);

        if (rel.Length == 0) return rootFull;

        var combined = Path.GetFullPath(Path.Combine(rootFull, rel.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;

        if (!combined.StartsWith(prefix, StringComparison.Ordinal))
            throw new ArgumentException($"路径越界：{relative}", nameof(relative));

        return combined;
    }
}
