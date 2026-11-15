using System.Collections.ObjectModel;
using Pebble.Services;

namespace Pebble;

public partial class NetworkStoragePage : ContentPage
{
    public ObservableCollection<NetworkConnection> Conns { get; } = new();

    public NetworkStoragePage()
    {
        InitializeComponent();
        BindingContext = this;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Refresh();
    }

    private void Refresh()
    {
        Conns.Clear();
        foreach (var c in NetworkStore.Connections) Conns.Add(c);
    }

    private async void OnAddClicked(object? sender, EventArgs e)
    {
        await Navigation.PushAsync(new ConnectionEditPage(null));
    }

    private async void OnConnTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is not NetworkConnection conn) return;

        var action = await DisplayActionSheet(conn.Name, "取消", null,
            "浏览", "编辑", "测试连接", "删除");

        switch (action)
        {
            case "浏览":
                await Navigation.PushAsync(new NetworkBrowserPage(conn));
                break;

            case "编辑":
                await Navigation.PushAsync(new ConnectionEditPage(conn));
                break;

            case "测试连接":
                await TestAsync(conn);
                break;

            case "删除":
                var ok = await DisplayAlert("删除连接",
                    $"确定删除「{conn.Name}」？此操作不可撤销。", "删除", "取消");
                if (ok)
                {
                    NetworkStore.Remove(conn);
                    Refresh();
                }
                break;
        }
    }

    private async Task TestAsync(NetworkConnection conn)
    {
        var password = await NetworkStore.GetPasswordAsync(conn.Id);
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
}
