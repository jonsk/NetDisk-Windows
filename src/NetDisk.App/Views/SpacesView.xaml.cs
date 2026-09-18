// 团队空间协作 + 分享创建的视图(DE-D-17)。
//
// 这一层**只做三件事**:把服务端给的数据画出来、把用户动作转成 `SpaceCollabClient` 调用、
// 把失败原因显示出来。它**不做**判权(4.3):
//   - 按钮不做"我有没有权限"的门禁,也不根据 `is_owner` 禁用按钮 —— 服务端拒绝时
//     显示服务端给的文案即可(端上另写一套判权必然与主实现漂移,而漂移的表现是
//     "服务端明明允许、端上却不让做");
//   - `is_owner` 只用于显示一行角色说明。
//
// 唯一在端上做的"确认"是**解散前的二次确认**(硬删,4.5 无回收站):要求把空间名原样
// 输入。它不是判权,而是防误触 —— 与 H5 的同款确认一致。

using System.Windows;
using System.Windows.Controls;
using NetDisk.ClientCore;
using NetDisk.Transport;
using NetDisk.App.Localization;

namespace NetDisk.App.Views;

/// <summary>团队空间协作视图(与 H5 同一组接口)。</summary>
public partial class SpacesView : System.Windows.Controls.UserControl
{
    // 不是 readonly:登录成功后由主窗口把**带令牌的**客户端注入进来(见 Attach)。
    // 在此之前它只能靠配置里的基址建一个匿名客户端 —— 那正是"列表永远是空的"的成因。
    private SpaceCollabClient? _client;
    private SpaceView? _selected;

    public SpacesView()
    {
        InitializeComponent();
        _client = TryCreateClient();
        if (_client is null)
        {
            Status(Loc.T("Spaces.NotLoggedIn"));
            return;
        }
        _ = ReloadAsync();
    }

    /// <summary>注入一个**已带令牌**的客户端(登录成功后由主窗口调用),并立即重新加载。</summary>
    public void Attach(ApiClient api)
    {
        ArgumentNullException.ThrowIfNull(api);
        _client = new SpaceCollabClient(api);
        _ = ReloadAsync();
    }

    /// <summary>
    /// 建客户端的兜底路径(未登录时):**先读已保存的配置**(用户在登录页填的地址),
    /// 再回落到环境变量 NETDISK_BASE_URL(仅为兼容老用法/CI,普通用户装完 MSI 用不到它)。
    /// </summary>
    private static SpaceCollabClient? TryCreateClient()
    {
        var baseUrl = NetDisk.SyncEngine.Host.ClientConfig.Load().BaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = Environment.GetEnvironmentVariable("NETDISK_BASE_URL");
        }
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }
        var api = new ApiClient(new ClientOptions { BaseAddress = new Uri(baseUrl) });
        return new SpaceCollabClient(api);
    }

    private void Status(string text) => StatusText.Text = text;

    /// <summary>把异常翻译成给用户看的一句话(用服务端给的原因)。</summary>
    private void Report(Exception ex)
    {
        var text = ex is ApiException api ? SpaceCollabClient.DescribeFailure(api) : ex.Message;
        Status(text);
        System.Windows.MessageBox.Show(text, Loc.T("Spaces.OpFail"), MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async Task ReloadAsync()
    {
        if (_client is null)
        {
            return;
        }
        try
        {
            var result = await _client.ListMineAsync();
            SpaceList.ItemsSource = result.spaces;
            SpaceList.DisplayMemberPath = nameof(SpaceView.name);
            Status(Loc.F("Spaces.Count", result.total));
            if (_selected is not null)
            {
                _selected = result.spaces.FirstOrDefault(s => s.id == _selected.id);
                await LoadMembersAsync();
            }
        }
        catch (Exception ex)
        {
            Report(ex);
        }
    }

    private async Task LoadMembersAsync()
    {
        if (_client is null || _selected is null)
        {
            MemberList.ItemsSource = null;
            return;
        }
        var members = await _client.ListMembersAsync(_selected.id);
        MemberList.ItemsSource = members.members;
        MemberList.DisplayMemberPath = nameof(MemberView.username);

        SelectedSpaceTitle.Text = _selected.name;
        // is_owner **只用于展示**:它不参与任何"要不要发请求"的判断(见文件头)
        SelectedSpaceRole.Text = _selected.is_owner == true
            ? Loc.T("Spaces.RoleOwner")
            : Loc.T("Spaces.RoleMember");
    }

    private async void OnSpaceSelected(object sender, SelectionChangedEventArgs e)
    {
        _selected = SpaceList.SelectedItem as SpaceView;
        try
        {
            await LoadMembersAsync();
        }
        catch (Exception ex)
        {
            Report(ex);
        }
    }

    private async void OnCreateSpace(object sender, RoutedEventArgs e)
    {
        if (_client is null)
        {
            return;
        }
        try
        {
            var created = await _client.CreateAsync(NewSpaceName.Text);
            NewSpaceName.Clear();
            Status(Loc.F("Spaces.Created", created.name));
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            Report(ex);
        }
    }

    private async void OnInvite(object sender, RoutedEventArgs e)
    {
        if (_client is null || _selected is null)
        {
            Status(Loc.T("Spaces.PickOne"));
            return;
        }
        try
        {
            var permission = (InvitePermission.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "reader";
            var member = await _client.InviteAsync(_selected.id, InviteUsername.Text, permission);
            InviteUsername.Clear();
            Status(Loc.F("Spaces.Invited", member.username, member.permission));
            await LoadMembersAsync();
        }
        catch (Exception ex)
        {
            Report(ex);
        }
    }

    private async void OnRemoveMember(object sender, RoutedEventArgs e)
    {
        if (_client is null || _selected is null || MemberList.SelectedItem is not MemberView member)
        {
            Status(Loc.T("Spaces.PickMember"));
            return;
        }
        try
        {
            await _client.RemoveMemberAsync(_selected.id, member.user_id);
            Status(Loc.F("Spaces.RemovedMem", member.username));
            await LoadMembersAsync();
        }
        catch (Exception ex)
        {
            Report(ex);
        }
    }

    private async void OnTransfer(object sender, RoutedEventArgs e)
    {
        if (_client is null || _selected is null)
        {
            Status(Loc.T("Spaces.PickOne"));
            return;
        }
        try
        {
            await _client.TransferAsync(_selected.id, TransferUsername.Text);
            Status(Loc.F("Spaces.Transferred", TransferUsername.Text));
            TransferUsername.Clear();
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            Report(ex);
        }
    }

    private async void OnLeave(object sender, RoutedEventArgs e)
    {
        if (_client is null || _selected is null)
        {
            Status(Loc.T("Spaces.PickOne"));
            return;
        }
        try
        {
            await _client.LeaveAsync(_selected.id);
            Status(Loc.T("Spaces.Left"));
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            // owner 未转让时会拿到 409「请先转让空间再退出」—— 这正是服务端的判权
            Report(ex);
        }
    }

    private async void OnDissolve(object sender, RoutedEventArgs e)
    {
        if (_client is null || _selected is null)
        {
            Status(Loc.T("Spaces.PickOne"));
            return;
        }
        // 硬删二次确认:必须手输空间名(H5 同款)。这不是判权,是防误触。
        if (!string.Equals(DissolveConfirmName.Text, _selected.name, StringComparison.Ordinal))
        {
            Status(Loc.T("Spaces.DissolveConfirm"));
            return;
        }
        try
        {
            await _client.DissolveAsync(_selected.id);
            Status(Loc.F("Spaces.Dissolved", _selected.name));
            DissolveConfirmName.Clear();
            _selected = null;
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            Report(ex);
        }
    }

    private async void OnCreateShare(object sender, RoutedEventArgs e)
    {
        if (_client is null)
        {
            return;
        }
        try
        {
            var hours = string.IsNullOrWhiteSpace(ShareExpiresHours.Text)
                ? (int?)null
                : (int.TryParse(ShareExpiresHours.Text, out var h) ? h : (int?)null);
            var maxDownloads = string.IsNullOrWhiteSpace(ShareMaxDownloads.Text)
                ? (int?)null
                : (int.TryParse(ShareMaxDownloads.Text, out var m) ? m : (int?)null);
            var created = await _client.CreateShareAsync(new ShareCreateRequest
            {
                FileId = ShareFileId.Text.Trim(),
                Password = string.IsNullOrWhiteSpace(SharePassword.Text) ? null : SharePassword.Text,
                ExpiresInHours = hours,
                MaxDownloads = maxDownloads,
            });
            var baseUrl = Environment.GetEnvironmentVariable("NETDISK_BASE_URL") ?? "";
            ShareLink.Text = ShareLinks.LinkFor(created, baseUrl);

            // 把服务端回显的约束原样告诉用户(免得"建完不知道限制有没有生效")
            var info = new System.Text.StringBuilder();
            info.Append(Loc.T("Spaces.ShareCreatedPrefix"));
            info.Append(created.need_password ? Loc.T("Spaces.SharePwdSet") : Loc.T("Spaces.ShareNoPwd"));
            info.Append(created.expires_at == null ? Loc.T("Spaces.Share7d") : Loc.F("Spaces.ShareExpiresAt", created.expires_at));
            info.Append(created.max_downloads == null ? Loc.T("Spaces.ShareUnlimited") : Loc.F("Spaces.ShareMaxDownloads", created.max_downloads));
            Status(info.ToString());
        }
        catch (Exception ex)
        {
            Report(ex);
        }
    }
}
