using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.Maui.Storage;
using Agate.Models;
using Agate.Services;

namespace Agate;

public partial class NetworkBrowserPage : ContentPage, INotifyPropertyChanged
{
    private readonly NetworkConnection _conn;
    private string _password = string.Empty;
    private string _relativePath = string.Empty;

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set { if (_isLoading == value) return; _isLoading = value; OnPropertyChanged(nameof(IsLoading)); }
    }

    public ObservableCollection<RemoteItem> Items { get; } = new();

    public string CurrentPath => "/" + _relativePath;

    public NetworkBrowserPage(NetworkConnection conn)
    {
        InitializeComponent();
        _conn = conn;
        Title = conn.Name;
        BindingContext = this;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _password = await NetworkStore.GetPasswordAsync(_conn.Id) ?? string.Empty;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var list = await WebDavService.ListAsync(_conn, _password, _relativePath);
            Items.Clear();
            foreach (var item in list) Items.Add(item);
            OnPropertyChanged(nameof(CurrentPath));
            PathLabel.Text = CurrentPath;
        }
        catch (Exception ex)
        {
            AppLog.Error("dav-list", ex);
            await this.SafeAlertAsync("读取失败", ex.Message, "好");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async void OnItemTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is not RemoteItem item) return;

        try
        {
            if (item.IsDirectory)
            {
                _relativePath = item.RelativePath;
                await LoadAsync();
                return;
            }

            var action = await DisplayActionSheet(item.Name, "取消", null, "下载到本地", "下载并预览");
            if (action == "下载到本地") await DownloadToLocalAsync(item, preview: false);
            else if (action == "下载并预览") await DownloadToLocalAsync(item, preview: true);
        }
        catch (Exception ex)
        {
            AppLog.Error("dav-tap", ex);
            await this.SafeAlertAsync("操作失败", ex.Message, "好");
        }
    }

    private async void OnUpTapped(object? sender, TappedEventArgs e)
    {
        if (_relativePath.Length == 0) return;
        var idx = _relativePath.LastIndexOf('/');
        _relativePath = idx < 0 ? string.Empty : _relativePath[..idx];
        await LoadAsync();
    }

    private async void OnUploadTapped(object? sender, TappedEventArgs e)
    {
        try
        {
            var picked = await FilePicker.Default.PickMultipleAsync();
            if (picked is null || !picked.Any()) return;

            IsLoading = true;
            var names = new List<string>();
            foreach (var file in picked)
            {
                await WebDavService.UploadAsync(_conn, _password, file.FullPath, _relativePath);
                names.Add(file.FileName);
            }

            await LoadAsync();
            await this.SafeAlertAsync("上传完成", $"已上传 {names.Count} 个文件到当前目录。", "好");
        }
        catch (Exception ex)
        {
            AppLog.Error("dav-upload", ex);
            await this.SafeAlertAsync("上传失败", ex.Message, "好");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async void OnNewFolderTapped(object? sender, TappedEventArgs e)
    {
        var name = await DisplayPromptAsync("新建文件夹", "文件夹名称", "创建", "取消");
        if (string.IsNullOrWhiteSpace(name)) return;

        var rel = string.IsNullOrEmpty(_relativePath) ? name.Trim() : _relativePath + "/" + name.Trim();
        IsLoading = true;
        try
        {
            await WebDavService.CreateDirectoryAsync(_conn, _password, rel);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("dav-mkdir", ex);
            await this.SafeAlertAsync("创建失败", ex.Message, "好");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task DownloadToLocalAsync(RemoteItem item, bool preview)
    {
        var localPath = Path.Combine(GetLocalBase(), item.RelativePath);
        IsLoading = true;
        try
        {
            await WebDavService.DownloadAsync(_conn, _password, item.RelativePath, localPath);

            if (preview) await OpenLocalFileAsync(localPath);
            else await this.SafeAlertAsync("已下载", $"文件已保存到：\n{localPath}", "好");
        }
        catch (Exception ex)
        {
            AppLog.Error("dav-download", ex);
            await this.SafeAlertAsync("下载失败", ex.Message, "好");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>下载后用与本地浏览器一致的路由打开预览（文本/图片/压缩包/字体/媒体）。</summary>
    private async Task OpenLocalFileAsync(string localPath)
    {
        var item = new FileItem
        {
            Name = Path.GetFileName(localPath),
            FullPath = localPath,
            IsDirectory = false,
            RelativePath = Path.GetFileName(localPath)
        };

        switch (FileKinds.Classify(localPath))
        {
            case FileKind.Text:
                await Navigation.PushAsync(new EditorPage(item));
                break;
            case FileKind.Image:
                if (FileKinds.IsSvg(localPath)) await Navigation.PushAsync(new SvgPreviewPage(item));
                else await Navigation.PushAsync(new ImagePreviewPage(item, new List<FileItem>()));
                break;
            case FileKind.Archive:
                await Navigation.PushAsync(new ArchivePreviewPage(item));
                break;
            case FileKind.Font:
                await Navigation.PushAsync(new FontPreviewPage(item));
                break;
            case FileKind.Media:
                await Navigation.PushAsync(new MediaPage(item));
                break;
            default:
                await this.SafeAlertAsync("已下载", $"文件已保存到：\n{localPath}", "好");
                break;
        }
    }

    /// <summary>本地落盘根目录：优先手机 Download/Agate，不可写则回退到应用缓存。</summary>
    private static string GetLocalBase()
    {
        var root = StorageAccess.ExternalRootExists()
            ? Path.Combine(StorageAccess.ExternalRoot, "Download", "Agate")
            : Path.Combine(FileSystem.CacheDirectory, "webdav");
        Directory.CreateDirectory(root);
        return root;
    }

}
