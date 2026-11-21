using Agate.Services;

namespace Agate;

public partial class ConnectionEditPage : ContentPage
{
    private readonly NetworkConnection? _existing;

    public ConnectionEditPage(NetworkConnection? existing)
    {
        InitializeComponent();
        _existing = existing;

        if (existing is not null)
        {
            NameEntry.Text = existing.Name;
            UrlEntry.Text = existing.Url;
            UserEntry.Text = existing.Username;
            TypePicker.SelectedItem = existing.Type;
        }
        else
        {
            TypePicker.SelectedItem = "WebDAV";
        }
    }

    private NetworkConnection BuildFromForm()
    {
        var url = (UrlEntry.Text ?? "").Trim();
        if (url.Length > 0 && !url.EndsWith("/")) url += "/";

        return new NetworkConnection
        {
            Id = _existing?.Id ?? Guid.NewGuid().ToString("N"),
            Name = (NameEntry.Text ?? "").Trim(),
            Type = TypePicker.SelectedItem as string ?? "WebDAV",
            Url = url,
            Username = (UserEntry.Text ?? "").Trim()
        };
    }

    private async void OnTestClicked(object? sender, EventArgs e)
    {
        var conn = BuildFromForm();
        if (!Validate(conn, out var msg)) { await this.SafeAlertAsync("请检查输入", msg, "好"); return; }

        if (conn.Type == "SMB")
        {
            await this.SafeAlertAsync("暂不支持", "SMB 尚未实现，请选择 WebDAV 进行测试。", "好");
            return;
        }

        var password = PassEntry.Text ?? "";
        // 编辑且未改密码时，沿用已保存的密码测试
        if (_existing is not null && password.Length == 0)
            password = await NetworkStore.GetPasswordAsync(_existing.Id) ?? "";

        IsBusy = true;
        try
        {
            var ok = await WebDavService.TestConnectionAsync(conn, password);
            await this.SafeAlertAsync(ok ? "连接成功" : "连接失败",
                ok ? "已成功连上该 WebDAV 服务器。" : "无法连接，请检查地址、账号或网络。", "好");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        var conn = BuildFromForm();
        if (!Validate(conn, out var msg)) { await this.SafeAlertAsync("请检查输入", msg, "好"); return; }

        if (conn.Type == "SMB")
        {
            await this.SafeAlertAsync("暂不支持", "SMB 尚未实现，请选择 WebDAV 保存。", "好");
            return;
        }

        var password = PassEntry.Text ?? "";

        NetworkStore.Upsert(conn);

        // 仅在用户填写了密码时才更新；编辑且留空则保留原密码
        if (password.Length > 0 || _existing is null)
            await NetworkStore.SetPasswordAsync(conn.Id, password.Length > 0 ? password : null);

        await Navigation.PopAsync();
    }

    private static bool Validate(NetworkConnection conn, out string message)
    {
        if (conn.Name.Length == 0) { message = "请填写连接名称。"; return false; }
        if (conn.Url.Length == 0 || !Uri.IsWellFormedUriString(conn.Url, UriKind.Absolute))
        {
            message = "请填写合法的服务器地址（需以 http:// 或 https:// 开头）。";
            return false;
        }
        message = string.Empty;
        return true;
    }
}
