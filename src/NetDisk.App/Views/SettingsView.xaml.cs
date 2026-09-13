// 设置页的代码后置:把界面上的值写进配置,并把"重启同步/退出登录"转成对运行时的一次替换。
//
// 这一层刻意很薄:真正的编排(重建 SyncHost、吊销令牌)都在引擎侧 —
// 这里只负责取值、校验、把失败原因显示出来。
//
// 两个设计点:
//   ① **改配置必然重启同步**:换同步目录/改并发/改限速都要新的 SyncHost 才生效 ——
//      不如实说明的话,用户会以为"保存了却没动"(最典型的是改了同步目录却发现还在传旧目录);
//   ② **退出登录要吊销服务端令牌**:只删本地密文的话,服务端那条 refresh 仍然有效
//      (等于"登出"没登出),所以走 TokenSession.SignOutAsync。

using System.IO;
using System.Windows;
using Microsoft.Win32;
using NetDisk.SyncEngine.Host;

namespace NetDisk.App.Views;

public partial class SettingsView : System.Windows.Controls.UserControl
{
    private ClientConfig? _config;

    /// <summary>用户要求"保存并重启同步"(由主窗口执行:它持有运行时)。参数是新配置。</summary>
    public event Func<ClientConfig, Task>? RestartRequested;

    /// <summary>用户要求退出登录(由主窗口执行:它持有运行时)。</summary>
    public event Func<Task>? LogoutRequested;

    public SettingsView()
    {
        InitializeComponent();
    }

    /// <summary>接上运行时:把当前生效的配置灌进界面。</summary>
    public void Attach(SyncRuntime runtime)
    {
        _config = runtime.Config;
        ServerBox.Text = runtime.Config.BaseUrl;
        RootBox.Text = runtime.Config.SyncRoot;
        ConcurrencyBox.Text = runtime.Config.MaxConcurrency.ToString();
        UploadBox.Text = runtime.Config.UploadKbps.ToString();
        DownloadBox.Text = runtime.Config.DownloadKbps.ToString();
        LogCheck.IsChecked = runtime.Config.Logging;
        StructureOnlyCheck.IsChecked = runtime.Config.StructureOnly;
        PathText.Text = $"配置文件:{runtime.Config.Path}\n" +
                        $"数据目录:{ClientPaths.DataDirectory}" +
                        (ClientPaths.FallbackReason is { } why ? $" (回退:{why})" : "");
        StatusText.Text = "";
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "选择同步目录",
            InitialDirectory = Directory.Exists(RootBox.Text) ? RootBox.Text : null,
        };
        if (dlg.ShowDialog() == true)
        {
            RootBox.Text = dlg.FolderName;
        }
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (_config is null || RestartRequested is null)
        {
            return;
        }
        var baseUrl = ServerBox.Text.Trim().TrimEnd('/');
        var root = RootBox.Text.Trim();
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out _))
        {
            Fail("服务器地址要写成完整地址,例如 http://10.14.37.187");
            return;
        }
        if (root.Length == 0)
        {
            Fail("同步目录不能为空");
            return;
        }
        if (!int.TryParse(ConcurrencyBox.Text.Trim(), out var concurrency))
        {
            Fail("并发数要填数字(1-16)");
            return;
        }
        if (!int.TryParse(UploadBox.Text.Trim(), out var upKbps) ||
            !int.TryParse(DownloadBox.Text.Trim(), out var downKbps))
        {
            Fail("限速要填数字(KB/s,0 = 不限速)");
            return;
        }
        if (concurrency < 1 || concurrency > 16)
        {
            Fail("并发数必须在 1..16(0 会让队列永不执行;过大把机器与带宽打满)");
            return;
        }
        if (upKbps < 0 || downKbps < 0)
        {
            Fail("限速不能为负(0 = 不限速)");
            return;
        }

        SaveButton.IsEnabled = false;
        try
        {
            _config.BaseUrl = baseUrl;
            _config.SyncRoot = root;
            _config.MaxConcurrency = concurrency;
            _config.UploadKbps = upKbps;
            _config.DownloadKbps = downKbps;
            _config.Logging = LogCheck.IsChecked == true;
            _config.StructureOnly = StructureOnlyCheck.IsChecked == true;
            _config.Save();
            AppLog.Enabled = _config.Logging;
            AppLog.Write("app",
                $"设置已保存:服务器={baseUrl} 同步目录={root} 并发={concurrency} " +
                $"上行限速={upKbps}KB/s 下行限速={downKbps}KB/s 日志={_config.Logging} " +
                $"只读浏览(仅结构)={_config.StructureOnly}");

            StatusText.Foreground = System.Windows.Media.Brushes.DimGray;
            StatusText.Text = "正在按新配置重启同步…";
            await RestartRequested(_config);
            StatusText.Text = "已按新配置重启同步。";
            AppLog.Write("app", "已按新配置重启同步");
        }
        catch (Exception ex)
        {
            Fail("重启同步失败: " + ex.Message);
            AppLog.Write("app", $"重启同步失败: {ex}");
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private async void OnLogout(object sender, RoutedEventArgs e)
    {
        if (LogoutRequested is null)
        {
            return;
        }
        var ok = System.Windows.MessageBox.Show(
            "退出登录会吊销服务器上的登录状态并删除本机令牌(文件不会被删除)。继续吗?",
            "退出登录", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
        if (!ok)
        {
            return;
        }
        LogoutButton.IsEnabled = false;
        try
        {
            StatusText.Foreground = System.Windows.Media.Brushes.DimGray;
            StatusText.Text = "正在退出登录…";
            await LogoutRequested();
        }
        catch (Exception ex)
        {
            Fail("退出登录失败: " + ex.Message);
            AppLog.Write("app", $"退出登录失败: {ex}");
        }
        finally
        {
            LogoutButton.IsEnabled = true;
        }
    }

    private void Fail(string message)
    {
        StatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
        StatusText.Text = message;
    }
}
