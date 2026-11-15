using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Microsoft.Maui.Storage;
using Pebble.Models;
using Pebble.Services;

namespace Pebble;

/// <summary>
/// 不解压预览：打开压缩包后逐层浏览内部文件夹 / 文件（支持多层钻取），不向磁盘解压。
/// 点文件夹 → 进入更深一层；点文本条目 → 抽到缓存用只读编辑器看；点图片 → 缓存后看图。
/// </summary>
public partial class ArchivePreviewPage : ContentPage
{
    private readonly FileItem _archive;
    private string _currentPath = string.Empty;   // 归档内当前文件夹，空串=根

    /// <summary>仅 ZIP 支持包内流式编辑（重命名/删除/添加），其它格式走只读预览。</summary>
    private bool IsZip => string.Equals(Path.GetExtension(_archive.FullPath), ".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>长按条目触发的命令：弹出“重命名 / 删除”菜单。</summary>
    public ICommand LongPressCommand { get; }

    /// <summary>长按时短暂屏蔽 tap，避免长按顺带打开预览页。</summary>
    private bool _suppressTap;

    public ObservableCollection<ArchiveNode> Entries { get; } = new();

    private bool _isLoading = true;
    public bool Loading
    {
        get => _isLoading;
        set { if (_isLoading == value) return; _isLoading = value; OnPropertyChanged(); }
    }

    public string ArchiveType { get; private set; } = "—";
    public string Summary { get; private set; } = "正在读取归档…";

    public ArchivePreviewPage(FileItem archive)
    {
        InitializeComponent();

        _archive = archive;
        Title = archive.Name;
        BindingContext = this;

        LongPressCommand = new Command<ArchiveNode>(async node => await OnEntryLongPressed(node));

        TypeLabel.Text = ArchiveType;
        SummaryLabel.Text = Summary;
        PathLabel.Text = "（根目录）";
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (Entries.Count == 0) await LoadAsync();
    }

    private async Task LoadAsync()
    {
        Loading = true;
        try
        {
            var nodes = await Task.Run(() => ArchiveService.ListChildren(_archive.FullPath, _currentPath));

            Entries.Clear();
            foreach (var n in nodes) Entries.Add(n);

            var files = nodes.Where(n => !n.IsDirectory).ToList();
            var totalSize = files.Sum(n => n.Size);

            ArchiveType = DetectType(_archive.FullPath);
            Summary = $"{nodes.Count} 项 · {files.Count} 个文件 · {FileOperations.FormatSize(totalSize)}";
        }
        catch (Exception ex)
        {
            Summary = "读取失败：" + ex.Message;
        }
        finally
        {
            Loading = false;
            TypeLabel.Text = ArchiveType;
            SummaryLabel.Text = Summary;
            PathLabel.Text = _currentPath.Length == 0 ? "（根目录）" : _currentPath;
            BackButton.IsEnabled = _currentPath.Length > 0;
            BackButton.Opacity = _currentPath.Length > 0 ? 1 : 0.4;
        }
    }

    private async void OnBackClicked(object? sender, EventArgs e)
    {
        if (_currentPath.Length == 0) return;
        var trimmed = _currentPath.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        _currentPath = slash < 0 ? string.Empty : trimmed[..(slash + 1)];
        EntryList.SelectedItem = null;
        await LoadAsync();
    }

    /// <summary>根据扩展名给出归档类型标签（分卷无标准扩展名时归为"分卷/其他"）。</summary>
    private static string DetectType(string path)
    {
        var ext = Path.GetExtension(path).ToUpperInvariant().TrimStart('.');
        return ext switch
        {
            "ZIP" => "ZIP 压缩包",
            "RAR" => "RAR 压缩包",
            "7Z" => "7Z 压缩包",
            "GZ" or "TGZ" => "GZIP 压缩包",
            "TAR" => "TAR 归档",
            "BZ2" => "BZIP2 压缩包",
            "XZ" => "XZ 压缩包",
            "" => "分卷压缩包",
            _ => ext + " 压缩包"
        };
    }

    private async void OnItemTapped(object? sender, TappedEventArgs e)
    {
        if (_suppressTap) return;   // 长按刚触发过，忽略这次 tap
        if (sender is not BindableObject bo || bo.BindingContext is not ArchiveNode node) return;

        if (node.IsDirectory)
        {
            _currentPath = node.Key;   // 已含末尾斜杠
            await LoadAsync();
            return;
        }

        var kind = FileKinds.Classify(node.Name);
        if (kind == FileKind.Text) await PreviewTextEntryAsync(node);
        else if (kind == FileKind.Image) await PreviewImageEntryAsync(node);
        else await this.SafeAlertAsync("暂不支持预览", $"「{node.Name}」暂不支持应用内预览，请解压后再打开。", "好");
    }

    /// <summary>长按条目：弹出“重命名 / 删除”菜单（仅 ZIP 支持）。</summary>
    private async Task OnEntryLongPressed(ArchiveNode node)
    {
        if (node is null) return;
        _suppressTap = true;   // 长按期间屏蔽即将到来的 tap，避免误开预览
        try
        {
            if (!IsZip)
            {
                await this.SafeAlertAsync("不支持", "仅 ZIP 压缩包支持包内编辑（重命名 / 删除 / 添加）。RAR、7Z 等格式请先解压。", "好");
                return;
            }

            var action = await DisplayActionSheetAsync(node.Name, "取消", null, "重命名", "删除");
            if (action == "重命名") await RenameEntryAsync(node);
            else if (action == "删除") await DeleteEntryAsync(node);
        }
        finally
        {
            // 稍后解除屏蔽，给 tap 事件一个“过了”的窗口
            await Task.Delay(350);
            _suppressTap = false;
        }
    }

    /// <summary>父目录前缀：用于把“显示名”拼回完整 key（保持所在目录不变）。</summary>
    private static string ParentPrefix(string key)
    {
        var trimmed = key.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash < 0 ? string.Empty : trimmed.Substring(0, slash + 1);
    }

    private async Task RenameEntryAsync(ArchiveNode node)
    {
        var input = await DisplayPromptAsync("重命名", "输入新的名称（不能包含路径分隔符 / 或 \\）：",
            initialValue: node.Name, maxLength: 120);
        if (string.IsNullOrWhiteSpace(input)) return;
        if (input.Contains('/') || input.Contains('\\'))
        {
            await this.SafeAlertAsync("非法名称", "名称不能包含路径分隔符。", "好");
            return;
        }

        var newKey = ParentPrefix(node.Key) + input + (node.IsDirectory ? "/" : "");
        var renameMap = new Dictionary<string, string> { [node.Key] = newKey };

        try
        {
            Loading = true;
            await Task.Run(() => ArchiveService.RebuildZip(_archive.FullPath, renameMap: renameMap));
            await LoadAsync();
        }
        catch (Exception ex)
        {
            await this.SafeAlertAsync("重命名失败", ex.Message, "好");
        }
        finally
        {
            Loading = false;
        }
    }

    private async Task DeleteEntryAsync(ArchiveNode node)
    {
        var ok = await this.SafeConfirmAsync("删除", $"确定删除「{node.Name}」吗？\n此操作会重写整个压缩包，且不可撤销。", "删除", "取消");
        if (!ok) return;

        try
        {
            Loading = true;
            // 目录按前缀删除（删掉整棵子树）；文件按精确 key 删除
            var deleteKeys = new[] { node.Key };
            await Task.Run(() => ArchiveService.RebuildZip(_archive.FullPath, deleteKeys: deleteKeys));
            await LoadAsync();
        }
        catch (Exception ex)
        {
            await this.SafeAlertAsync("删除失败", ex.Message, "好");
        }
        finally
        {
            Loading = false;
        }
    }

    /// <summary>“添加外部文件”：把手机里选中的文件流式塞进当前压缩包目录（不先解压）。</summary>
    private async void OnAddExternalClicked(object? sender, EventArgs e)
    {
        if (!IsZip)
        {
            await this.SafeAlertAsync("不支持", "仅 ZIP 压缩包支持添加外部文件。", "好");
            return;
        }

        List<string> tempFiles = new();
        try
        {
            var picked = await FilePicker.PickMultipleAsync(new PickOptions { PickerTitle = "选择要塞进压缩包的文件" });
            if (picked is null || !picked.Any()) return;

            // 先把选中的文件拷到可随机读的应用缓存，规避 content:// URI 直接读的问题
            var adds = new List<(string EntryKey, string SourceFilePath)>();
            foreach (var f in picked)
            {
                var safeName = Path.GetFileName(f.FileName);
                var tmp = Path.Combine(FileSystem.CacheDirectory,
                    "add_" + Guid.NewGuid().ToString("N").Substring(0, 8) + "_" + safeName);
                await using (var src = await f.OpenReadAsync())
                await using (var dst = File.Create(tmp))
                    await src.CopyToAsync(dst);
                tempFiles.Add(tmp);
                adds.Add((_currentPath + safeName, tmp));
            }

            Loading = true;
            await Task.Run(() => ArchiveService.RebuildZip(_archive.FullPath, addFiles: adds));
            await LoadAsync();
            await this.SafeAlertAsync("已添加", $"已向压缩包添加 {adds.Count} 个文件。", "好");
        }
        catch (Exception ex)
        {
            await this.SafeAlertAsync("添加失败", ex.Message, "好");
        }
        finally
        {
            Loading = false;
            foreach (var t in tempFiles)
                try { if (File.Exists(t)) File.Delete(t); } catch { /* 清理失败无所谓 */ }
        }
    }

    /// <summary>把单个文本条目抽到缓存目录，用只读编辑器打开（不污染用户目录）。</summary>
    private async Task PreviewTextEntryAsync(ArchiveNode node)
    {
        Loading = true;
        try
        {
            var dest = CachePathFor(node.Key);
            await Task.Run(() => ArchiveService.ExtractEntryToPath(_archive.FullPath, node.Key, dest));

            var tempItem = new FileItem
            {
                Name = Path.GetFileName(node.Key),
                FullPath = dest,
                RelativePath = "archive:" + node.Key,
                IsDirectory = false
            };

            await Navigation.PushAsync(new EditorPage(tempItem, readOnly: true));
        }
        catch (Exception ex)
        {
            await this.SafeAlertAsync("预览失败", ex.Message, "好");
        }
        finally
        {
            Loading = false;
        }
    }

    /// <summary>把单个图片条目抽到缓存目录，用图片预览打开。</summary>
    private async Task PreviewImageEntryAsync(ArchiveNode node)
    {
        Loading = true;
        try
        {
            var dest = CachePathFor(node.Key);
            await Task.Run(() => ArchiveService.ExtractEntryToPath(_archive.FullPath, node.Key, dest));

            var tempItem = new FileItem
            {
                Name = Path.GetFileName(node.Key),
                FullPath = dest,
                RelativePath = "archive:" + node.Key,
                IsDirectory = false
            };

            await Navigation.PushAsync(new ImagePreviewPage(tempItem));
        }
        catch (Exception ex)
        {
            await this.SafeAlertAsync("预览失败", ex.Message, "好");
        }
        finally
        {
            Loading = false;
        }
    }

    /// <summary>为归档内条目生成缓存路径：基础目录 + 哈希防同名冲突。</summary>
    private static string CachePathFor(string key)
    {
        var baseName = Path.GetFileName(key);
        if (string.IsNullOrEmpty(baseName)) baseName = "entry";

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(baseName.Select(c => invalid.Contains(c) ? '_' : c).ToArray());

        var hash = Math.Abs(key.GetHashCode()) % 100000;
        var dir = Path.Combine(FileSystem.CacheDirectory, "archive_preview");
        return Path.Combine(dir, $"{hash}_{cleaned}");
    }
}
