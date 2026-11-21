using Agate.Services;

namespace Agate;

public partial class SettingsPage : ContentPage
{
    public SettingsPage()
    {
        InitializeComponent();

        ThemePicker.SelectedIndex = AppSettings.Theme switch
        {
            AppTheme.Light => 1,
            AppTheme.Dark => 2,
            _ => 0
        };

        PortEntry.Text = AppSettings.ServerPort.ToString();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        var token = await AppSettings.GetGitHubTokenAsync();
        if (!string.IsNullOrEmpty(token)) TokenEntry.Text = token;

        await RefreshShizukuStatusAsync();
    }

    private async void OnSaveTokenClicked(object? sender, EventArgs e)
    {
        var token = TokenEntry.Text?.Trim();
        if (string.IsNullOrEmpty(token))
        {
            await AppSettings.SetGitHubTokenAsync(null);
            await this.SafeAlertAsync("已清除", "GitHub Token 已清除。", "好");
            return;
        }

        await AppSettings.SetGitHubTokenAsync(token);
        await this.SafeAlertAsync("已保存", "Token 已写入系统安全存储。", "好");
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (ThemePicker.SelectedIndex < 0) return;

        AppSettings.Theme = ThemePicker.SelectedIndex switch
        {
            1 => AppTheme.Light,
            2 => AppTheme.Dark,
            _ => AppTheme.Unspecified
        };
    }

    private async void OnSavePortClicked(object? sender, EventArgs e)
    {
        if (!int.TryParse(PortEntry.Text?.Trim(), out var port) || port is < 1024 or > 65535)
        {
            await this.SafeAlertAsync("端口无效", "请输入 1024–65535 之间的端口号。", "好");
            return;
        }

        AppSettings.ServerPort = port;
        await this.SafeAlertAsync("已保存", $"服务器端口设为 {port}。", "好");
    }

    private async void OnNetworkStorageClicked(object? sender, EventArgs e)
    {
        await Navigation.PushAsync(new NetworkStoragePage());
    }

    private async void OnRefreshShizukuClicked(object? sender, EventArgs e) => await RefreshShizukuStatusAsync();

    private async void OnRequestShizukuClicked(object? sender, EventArgs e)
    {
        try
        {
            var state = await ShellAccess.GetStateAsync();

            if (state == ShellState.Unavailable)
            {
                var go = await this.SafeConfirmAsync("需要 Shizuku",
                    "Shizuku 是一个用 adb / root 启动的常驻服务，装好并启动后本应用才能申请 shell 权限。\n\n" +
                    "要到官网下载页看看吗？",
                    "了解 Shizuku", "取消");
                if (go) await Launcher.Default.OpenAsync("https://shizuku.rikka.app/");
                return;
            }

            if (state == ShellState.Ready)
            {
                await this.SafeAlertAsync("已就绪", "Agate 已获得 Shizuku 的 shell 权限，可以直接浏览 Android/data 了。", "好");
                await RefreshShizukuStatusAsync();
                return;
            }

            if (await ShellAccess.RequestPermissionAsync())
            {
                await RefreshShizukuStatusAsync();
                await this.SafeAlertAsync("已授权", "现在可以浏览 Android/data 与 Android/obb 了。", "好");
            }
            else
            {
                await this.SafeAlertAsync("未获得授权", "没有拿到 Shizuku 权限，可以稍后在 Shizuku 应用里为 Agate 手动打开。", "好");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("shizuku-settings", ex);
            await this.SafeAlertAsync("操作失败", ex.Message, "好");
        }
    }

    /// <summary>把 Shizuku 状态写进设置页那一行；非 Android 直接说明不可用。</summary>
    private async Task RefreshShizukuStatusAsync()
    {
        try
        {
            if (!ShellAccess.IsAndroid)
            {
                ShizukuStatusLabel.Text = "当前平台不需要（仅 Android 受限目录才用得到）";
                return;
            }

            var state = await ShellAccess.GetStateAsync();
            ShizukuStatusLabel.Text = state switch
            {
                ShellState.Ready => $"可用 · 服务版本 {await ShellAccess.GetServiceVersionAsync()} · uid {await ShellAccess.GetShellUidAsync()}",
                ShellState.NoPermission => "已安装 Shizuku，但 Agate 还没拿到授权",
                _ => "未检测到 Shizuku（未安装，或服务没启动）"
            };
        }
        catch (Exception ex)
        {
            AppLog.Error("shizuku-status", ex);
            ShizukuStatusLabel.Text = "检测失败：" + ex.Message;
        }
    }

    private async void OnClearTrashClicked(object? sender, EventArgs e)
    {
        var trash = Path.Combine(FileSystem.AppDataDirectory, ".trash");
        if (!Directory.Exists(trash))
        {
            await this.SafeAlertAsync("回收站为空", "没有需要清理的内容。", "好");
            return;
        }

        var confirmed = await this.SafeConfirmAsync("清理回收站",
            "将删除沙盒回收站里的所有文件，此操作不可撤销。", "清理", "取消");
        if (!confirmed) return;

        try
        {
            await Task.Run(() => Directory.Delete(trash, recursive: true));
            await this.SafeAlertAsync("完成", "回收站已清空。", "好");
        }
        catch (Exception ex)
        {
            await this.SafeAlertAsync("清理失败", ex.Message, "好");
        }
    }
}
