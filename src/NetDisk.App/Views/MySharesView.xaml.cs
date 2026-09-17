// 我的分享视图(DE-D-17 衍生):列出我创建的分享、吊销、复制/打开落地页链接。
//
// 与 SpacesView 同源的职责边界:只做"画数据 / 转动作 / 显示服务端原因",
// 不端上判权(4.3)。吊销前的二次确认是防误触,不是判权。

using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using NetDisk.ClientCore;
using NetDisk.Transport;

namespace NetDisk.App.Views;

public partial class MySharesView : System.Windows.Controls.UserControl
{
    // 登录成功后由主窗口注入带令牌的客户端(见 Attach);之前仅能按已存配置建匿名客户端。
    private SpaceCollabClient? _client;
    private readonly ObservableCollection<ShareItem> _items = new();

    public MySharesView()
    {
        InitializeComponent();
        SharesList.ItemsSource = _items;
        _client = TryCreateClient();
        if (_client is not null)
        {
            _ = ReloadAsync();
        }
        else
        {
            Status("尚未登录:请先在「同步」页填写服务器地址并登录。");
        }
    }

    /// <summary>注入带令牌的客户端(登录成功后由主窗口调用)并立即加载。</summary>
    public void Attach(ApiClient api)
    {
        ArgumentNullException.ThrowIfNull(api);
        _client = new SpaceCollabClient(api);
        _ = ReloadAsync();
    }

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

    private static string CurrentBaseUrl()
    {
        var baseUrl = NetDisk.SyncEngine.Host.ClientConfig.Load().BaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = Environment.GetEnvironmentVariable("NETDISK_BASE_URL");
        }
        return baseUrl ?? "";
    }

    private void Status(string text) => StatusText.Text = text;

    private async Task ReloadAsync()
    {
        if (_client is null)
        {
            Status("尚未登录:请先在「同步」页登录。");
            return;
        }
        try
        {
            var shares = await _client.ListSharesAsync();
            _items.Clear();
            foreach (var s in shares)
            {
                _items.Add(s);
            }
            Status(_items.Count == 0 ? "你还没有创建任何分享链接。" : $"共 {_items.Count} 条分享");
        }
        catch (ApiException ex)
        {
            Status(SpaceCollabClient.DescribeFailure(ex));
        }
        catch (Exception ex)
        {
            Status($"加载失败:{ex.Message}");
        }
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await ReloadAsync();

    private async void OnRevoke(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: ShareItem item } || _client is null)
        {
            return;
        }
        var name = item.name ?? item.token;
        if (System.Windows.MessageBox.Show(
                $"确定吊销分享「{name}」?\n吊销后该链接立即失效且不可恢复。",
                "吊销确认", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning)
            != System.Windows.MessageBoxResult.OK)
        {
            return;
        }
        try
        {
            await _client.RevokeShareAsync(item.id);
            _items.Remove(item);
            Status($"已吊销「{name}」");
        }
        catch (ApiException ex)
        {
            Status(SpaceCollabClient.DescribeFailure(ex));
        }
        catch (Exception ex)
        {
            Status($"吊销失败:{ex.Message}");
        }
    }

    private void OnCopyLink(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: ShareItem item })
        {
            return;
        }
        var link = ShareLinks.LinkForToken(item.token, CurrentBaseUrl());
        System.Windows.Clipboard.SetText(link);
        Status("链接已复制到剪贴板");
    }

    private void OnOpenLink(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: ShareItem item })
        {
            return;
        }
        var link = ShareLinks.LinkForToken(item.token, CurrentBaseUrl());
        try
        {
            Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Status($"无法打开链接:{ex.Message}");
        }
    }
}
