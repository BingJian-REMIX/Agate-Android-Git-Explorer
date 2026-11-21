using Agate.Models;
using Agate.Services;

namespace Agate;

/// <summary>凭据列表：一行一个 Token，点一下可以编辑或删除。</summary>
public partial class TokenManagerPage : ContentPage
{
    public TokenManagerPage()
    {
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await Task.Delay(50);   // 等编辑页返回后再刷新，避免和导航动画抢
        Render();
    }

    private void Render()
    {
        var credentials = CredentialStore.Load();

        CredentialList.Clear();
        EmptyHint.IsVisible = credentials.Count == 0;

        foreach (var credential in credentials)
        {
            var row = BuildRow(credential);
            CredentialList.Add(row);
        }
    }

    private View BuildRow(GitCredential credential)
    {
        var title = new Label
        {
            Text = credential.DisplayName,
            FontSize = 14,
            FontAttributes = FontAttributes.Bold,
            TextColor = Application.Current?.RequestedTheme == AppTheme.Dark
                ? Color.FromArgb("#E9ECF5")
                : Color.FromArgb("#14161F")
        };

        var detail = new Label
        {
            Text = $"{credential.Platform} · {credential.Host} · {credential.MaskedToken}",
            FontSize = 11,
            TextColor = Application.Current?.RequestedTheme == AppTheme.Dark
                ? Color.FromArgb("#8B94A8")
                : Color.FromArgb("#5F6779")
        };

        var stack = new VerticalStackLayout { Spacing = 3 };
        stack.Add(title);
        stack.Add(detail);

        var border = new Border
        {
            Padding = new Thickness(14, 12),
            StrokeThickness = 1,
            BackgroundColor = Application.Current?.RequestedTheme == AppTheme.Dark
                ? Color.FromArgb("#10131C")
                : Colors.White,
            Stroke = Application.Current?.RequestedTheme == AppTheme.Dark
                ? Color.FromArgb("#1E2434")
                : Color.FromArgb("#DCE1EC"),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
            Content = stack
        };

        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => await ShowActionsAsync(credential);
        border.GestureRecognizers.Add(tap);

        return border;
    }

    private async Task ShowActionsAsync(GitCredential credential)
    {
        var action = await DisplayActionSheetAsync(credential.DisplayName, "取消", null, "编辑", "删除");

        switch (action)
        {
            case "编辑":
                await Navigation.PushAsync(new TokenEditPage(credential));
                break;

            case "删除":
                var confirmed = await this.SafeConfirmAsync("删除 Token",
                    $"确定删除「{credential.DisplayName}」的凭据？删除后推送该平台需要重新配置。", "删除", "取消");
                if (!confirmed) break;

                await CredentialStore.RemoveAsync(credential.Id);
                Render();
                break;
        }
    }

    private async void OnAddClicked(object? sender, EventArgs e)
    {
        await Navigation.PushAsync(new TokenEditPage(null));
    }
}
