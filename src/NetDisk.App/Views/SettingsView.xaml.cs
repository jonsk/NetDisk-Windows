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
        ConflictCombo.SelectedIndex = runtime.Config.OnConflict switch
        {
            "keep_local" => 1,
            "keep_remote" => 2,
            _ => 0, // 未知值/默认 = 都保留(与引擎的归一化一致:未知一律落到"不丢数据"那条)
        };
        PathText.Text = $"配置文件:{runtime.Config.Path}\n" +
                        $"数据目录:{ClientPaths.DataDirectory}" +
                        (ClientPaths.FallbackReason is { } why ? $" (回退:{why})" : "");
        _runtime = runtime;
        ReloadSpaceList();
        StatusText.Text = "";
    }

    // ---------------------------------------------------------------- 多空间(绑定管理)

    private SyncRuntime? _runtime;
    /// <summary>界面上的空间名(id → 名字),来自服务端的空间列表;取不到就只显示短 id。</summary>
    private readonly Dictionary<string, string> _spaceNames = new(StringComparer.Ordinal);
    private readonly System.Collections.ObjectModel.ObservableCollection<string> _spaceRows = new();

    private void ReloadSpaceList()
    {
        _spaceRows.Clear();
        foreach (var b in _config?.EffectiveBindings ?? Array.Empty<SpaceBinding>())
        {
            var name = _spaceNames.TryGetValue(b.SpaceId, out var n) ? n : b.SpaceId[..Math.Min(8, b.SpaceId.Length)];
            var kind = b.StructureOnly == true ? "(仅结构)" : "";
            _spaceRows.Add($"{name}{kind}  →  {b.SyncRoot}");
        }
        SpaceList.ItemsSource = _spaceRows;
        // 顺带把空间名拉回来(只为了显示;失败不影响绑定管理)
        _ = LoadSpaceNamesAsync();
    }

    private async Task LoadSpaceNamesAsync()
    {
        if (_runtime is null)
        {
            return;
        }
        try
        {
            var list = await new NetDisk.Transport.SpaceCollabClient(_runtime.Api).ListMineAsync();
            foreach (var s in list.spaces ?? new List<SpaceView>())
            {
                _spaceNames[s.id] = string.IsNullOrWhiteSpace(s.name) ? s.id[..Math.Min(8, s.id.Length)] : s.name;
            }
            ReloadSpaceList();
        }
        catch (Exception)
        {
            // 名字只是显示;取不到就用短 id(不弹框打扰用户)
        }
    }

    /// <summary>添加一个空间绑定:选空间 → 选本地目录。</summary>
    private async void OnAddSpace(object sender, RoutedEventArgs e)
    {
        if (_config is null || _runtime is null)
        {
            return;
        }
        List<SpaceView> spaces;
        try
        {
            var list = await new NetDisk.Transport.SpaceCollabClient(_runtime.Api).ListMineAsync();
            spaces = (list.spaces ?? new List<SpaceView>()).ToList();
        }
        catch (Exception ex)
        {
            Fail("取空间列表失败:" + ex.Message);
            return;
        }
        if (spaces.Count == 0)
        {
            Fail("这个账号没有可见空间。");
            return;
        }

        var pick = new SpacePickWindow(spaces, _config.EffectiveBindings.Select(b => b.SpaceId).ToHashSet())
        {
            Owner = Window.GetWindow(this),
        };
        if (pick.ShowDialog() != true || pick.Selected is null)
        {
            return;
        }
        var folder = new OpenFolderDialog { Title = "选择这个空间的本地同步目录" };
        if (folder.ShowDialog() != true)
        {
            return;
        }
        if (IsRootAlreadyBound(folder.FolderName, pick.Selected.id, out var clash))
        {
            Fail($"这个目录已经绑给另一个空间了:{clash}。请为每个空间选不同的目录。");
            return;
        }

        // 写回配置:spaces 列表才是权威(旧的单空间三件套保留作主空间)
        var bindings = _config.EffectiveBindings
            .Select(b => new SpaceBinding { SpaceId = b.SpaceId, SyncRoot = b.SyncRoot, ParentId = b.ParentId, StructureOnly = b.StructureOnly })
            .ToList();
        bindings.Add(new SpaceBinding { SpaceId = pick.Selected.id, SyncRoot = folder.FolderName });
        _config.Spaces = bindings;
        _config.Save();
        AppLog.Write("app", $"已添加空间绑定:{pick.Selected.id} → {folder.FolderName}(重启同步后生效)");
        ReloadSpaceList();
        StatusText.Foreground = System.Windows.Media.Brushes.DimGray;
        StatusText.Text = "已添加。点「保存并重启同步」后按新配置生效。";
    }

    /// <summary>移除选中的绑定(只解除绑定,不删本地文件)。</summary>
    private void OnRemoveSpace(object sender, RoutedEventArgs e)
    {
        if (_config is null || SpaceList.SelectedIndex < 0)
        {
            StatusText.Foreground = System.Windows.Media.Brushes.DimGray;
            StatusText.Text = "请先在上面选中一条绑定。";
            return;
        }
        var bindings = _config.EffectiveBindings.ToList();
        // 主空间不允许在这里移除:它承载 legacy 三件套(space_id/sync_root),
        // 允许多空间的同时不破坏老配置的语义 —— 想换主空间就改上面的"同步目录"。
        if (SpaceList.SelectedIndex == 0)
        {
            Fail("第一条是**主空间**(对应上面的服务器/同步目录设置),不能在这里移除;请先移除其它空间。");
            return;
        }
        var victim = bindings[SpaceList.SelectedIndex];
        bindings.RemoveAt(SpaceList.SelectedIndex);
        _config.Spaces = bindings;
        _config.Save();
        AppLog.Write("app", $"已移除空间绑定:{victim.SpaceId} → {victim.SyncRoot}(本地文件未删除)");
        ReloadSpaceList();
        StatusText.Foreground = System.Windows.Media.Brushes.DimGray;
        StatusText.Text = "已移除(本地文件没有删除)。点「保存并重启同步」后生效。";
    }

    private bool IsRootAlreadyBound(string root, string spaceId, out string clash)
    {
        foreach (var b in _config?.EffectiveBindings ?? Array.Empty<SpaceBinding>())
        {
            if (!string.Equals(b.SpaceId, spaceId, StringComparison.Ordinal)
                && string.Equals(Path.GetFullPath(b.SyncRoot), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
            {
                clash = b.SpaceId;
                return true;
            }
        }
        clash = "";
        return false;
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
            _config.OnConflict = ConflictCombo.SelectedIndex switch
            {
                1 => "keep_local",
                2 => "keep_remote",
                _ => "keep_both",
            };
            _config.Save();
            AppLog.Enabled = _config.Logging;
            AppLog.Write("app",
                $"设置已保存:服务器={baseUrl} 同步目录={root} 并发={concurrency} " +
                $"上行限速={upKbps}KB/s 下行限速={downKbps}KB/s 日志={_config.Logging} " +
                $"只读浏览(仅结构)={_config.StructureOnly} 冲突策略={_config.OnConflict}");

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
