using Agate.Models;
using Agate.Services;

namespace Agate;

/// <summary>
/// 单条凭据的编辑页。新建时按 Token 前缀自动推断平台与用户名；
/// 编辑时 Token 留空表示沿用已存的那条。
/// </summary>
public partial class TokenEditPage : ContentPage
{
    private readonly GitCredential _credential;
    private readonly bool _isNew;
    private readonly List<string> _platformNames;
    private GitPlatform _platform = GitPlatform.Custom;

    public TokenEditPage(GitCredential? credential)
    {
        InitializeComponent();

        _isNew = credential is null;
        _credential = credential ?? new GitCredential { Platform = GitPlatform.Custom.Name };

        Title = _isNew ? "添加 Token" : "编辑 Token";

        _platformNames = new List<string> { "自动识别" }
            .Concat(GitPlatform.All.Select(p => p.Name)).ToList();
        PlatformPicker.ItemsSource = _platformNames;

        if (_isNew)
        {
            PlatformPicker.SelectedIndex = 0;
        }
        else
        {
            HostEntry.Text = _credential.Host;
            UserEntry.Text = _credential.Username;
            LabelEntry.Text = _credential.Label;
            TokenEntry.Placeholder = "留空表示不修改已保存的 Token";

            var index = _platformNames.IndexOf(_credential.Platform);
            PlatformPicker.SelectedIndex = index > 0 ? index : 0;
        }

        UpdatePlatformHint();
    }

    private void OnPlatformChanged(object? sender, EventArgs e)
    {
        if (PlatformPicker.SelectedIndex <= 0)
        {
            // 回到「自动识别」：按 Token 前缀重新推断
            ApplyPlatform(GitPlatform.DetectByToken(TokenEntry.Text) ?? GitPlatform.Custom, false);
            return;
        }

        var platform = GitPlatform.All[PlatformPicker.SelectedIndex - 1];
        ApplyPlatform(platform, true);
    }

    private void ApplyPlatform(GitPlatform platform, bool overwriteFields)
    {
        _platform = platform;

        if (overwriteFields && !string.IsNullOrWhiteSpace(platform.Host))
            HostEntry.Text = platform.Host;

        if (overwriteFields && !string.IsNullOrWhiteSpace(platform.Username))
            UserEntry.Text = platform.Username;

        UpdatePlatformHint();
    }

    private void OnTokenChanged(object? sender, TextChangedEventArgs e)
    {
        if (PlatformPicker.SelectedIndex > 0) return;   // 用户已经手选平台，不再自动改

        var detected = GitPlatform.DetectByToken(e.NewTextValue);
        if (detected is null || detected == _platform) return;

        _platform = detected;
        if (!string.IsNullOrWhiteSpace(detected.Host)) HostEntry.Text = detected.Host;
        if (!string.IsNullOrWhiteSpace(detected.Username)) UserEntry.Text = detected.Username;
        UpdatePlatformHint();
    }

    private void UpdatePlatformHint()
    {
        DetectLabel.Text = _platform == GitPlatform.Custom
            ? string.Empty
            : $"已识别为 {_platform.Name}";
        OpenTokenPageButton.IsVisible = !string.IsNullOrWhiteSpace(_platform.TokenUrl);
    }

    private async void OnOpenTokenPageClicked(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_platform.TokenUrl)) return;

        try { await Launcher.Default.OpenAsync(_platform.TokenUrl); }
        catch (Exception ex)
        {
            AppLog.Error("token-open-url", ex);
            await this.SafeAlertAsync("打不开", _platform.TokenUrl, "好");
        }
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        try
        {
            var token = TokenEntry.Text?.Trim();
            var host = HostEntry.Text?.Trim();

            if (_isNew && string.IsNullOrWhiteSpace(token))
            {
                await this.SafeAlertAsync("缺少 Token", "请先粘贴访问令牌。", "好");
                return;
            }

            if (string.IsNullOrWhiteSpace(host))
            {
                await this.SafeAlertAsync("缺少主机名",
                    "请填写仓库所在主机，例如 github.com 或你自建 GitLab 的域名。", "好");
                return;
            }

            _credential.Host = host.ToLowerInvariant();
            _credential.Username = UserEntry.Text?.Trim() ?? string.Empty;
            _credential.Label = LabelEntry.Text?.Trim() ?? string.Empty;
            _credential.Platform = _platform == GitPlatform.Custom
                ? GitPlatform.DetectByHost(host).Name
                : _platform.Name;

            await CredentialStore.UpsertAsync(_credential, string.IsNullOrWhiteSpace(token) ? null : token);

            await this.SafeAlertAsync("已保存", $"{_credential.Host} 的凭据已写入安全存储。", "好");
            await Navigation.PopAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("token-save", ex);
            await this.SafeAlertAsync("保存失败", ex.Message, "好");
        }
    }
}
