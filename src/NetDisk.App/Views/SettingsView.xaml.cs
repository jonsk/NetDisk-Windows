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
using NetDisk.App.Localization;
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
        PathText.Text = Loc.F("Settings.PathFormat", runtime.Config.Path, ClientPaths.DataDirectory) +
                        (ClientPaths.FallbackReason is { } why ? Loc.F("Settings.PathFallback", why) : "");
        _runtime = runtime;
        ReloadSpaceList();
        LanguageCombo.ItemsSource = Locale.Languages.Select(l => l.SelfName).ToList();
        LanguageCombo.SelectedIndex = GetLanguageIndex(Loc.Current);
        StatusText.Text = "";
    }

    private static int GetLanguageIndex(string code) =>
        code switch
        {
            "en" => 1,
            _ => 0,
        };

    /// <summary>语言切换:立即生效 + 写回 client.json 持久化。</summary>
    private void OnLanguageChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_config is null || LanguageCombo.SelectedIndex < 0)
        {
            return;
        }
        var code = LanguageCombo.SelectedIndex switch
        {
            1 => "en",
            _ => "zh",
        };
        if (string.Equals(code, Loc.Current, StringComparison.Ordinal))
        {
            return;
        }
        Loc.Instance.Code = code;              // 即时全局刷新(免重启)
        _config.Language = code;               // 持久化
        _config.Save();
        AppLog.Write("app", $"界面语言已切换:{code}");
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
            Fail(Loc.F("Error.AddSpaceList", ex.Message));
            return;
        }
        if (spaces.Count == 0)
        {
            Fail(Loc.T("Error.NoSpaces"));
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
            Fail(Loc.F("Error.DirBound", clash));
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
        StatusText.Text = Loc.T("Settings.StatusAdded");
    }

    /// <summary>移除选中的绑定(只解除绑定,不删本地文件)。</summary>
    private void OnRemoveSpace(object sender, RoutedEventArgs e)
    {
        if (_config is null || SpaceList.SelectedIndex < 0)
        {
            StatusText.Foreground = System.Windows.Media.Brushes.DimGray;
            StatusText.Text = Loc.T("Settings.StatusPickBinding");
            return;
        }
        var bindings = _config.EffectiveBindings.ToList();
        // 主空间不允许在这里移除:它承载 legacy 三件套(space_id/sync_root),
        // 允许多空间的同时不破坏老配置的语义 —— 想换主空间就改上面的"同步目录"。
        if (SpaceList.SelectedIndex == 0)
        {
            Fail(Loc.T("Error.CannotRemovePrimary"));
            return;
        }
        var victim = bindings[SpaceList.SelectedIndex];
        bindings.RemoveAt(SpaceList.SelectedIndex);
        _config.Spaces = bindings;
        _config.Save();
        AppLog.Write("app", $"已移除空间绑定:{victim.SpaceId} → {victim.SyncRoot}(本地文件未删除)");
        ReloadSpaceList();
        StatusText.Foreground = System.Windows.Media.Brushes.DimGray;
        StatusText.Text = Loc.T("Settings.StatusRemoved");
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
            Fail(Loc.T("Error.BadBaseUrl"));
            return;
        }
        if (root.Length == 0)
        {
            Fail(Loc.T("Error.EmptyRoot"));
            return;
        }
        if (!int.TryParse(ConcurrencyBox.Text.Trim(), out var concurrency))
        {
            Fail(Loc.T("Error.BadConcurrency"));
            return;
        }
        if (!int.TryParse(UploadBox.Text.Trim(), out var upKbps) ||
            !int.TryParse(DownloadBox.Text.Trim(), out var downKbps))
        {
            Fail(Loc.T("Error.BadRateLimit"));
            return;
        }
        if (concurrency < 1 || concurrency > 16)
        {
            Fail(Loc.T("Error.ConcurrencyRange"));
            return;
        }
        if (upKbps < 0 || downKbps < 0)
        {
            Fail(Loc.T("Error.NegativeRate"));
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
        StatusText.Text = Loc.T("Settings.StatusRestarting");
        await RestartRequested(_config);
        StatusText.Text = Loc.T("Settings.StatusRestarted");
            AppLog.Write("app", "已按新配置重启同步");
        }
        catch (Exception ex)
        {
            Fail(Loc.F("Error.RestartFail", ex.Message));
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
            Loc.T("Error.LogoutConfirmMsg"),
            Loc.T("Error.LogoutTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
        if (!ok)
        {
            return;
        }
        LogoutButton.IsEnabled = false;
        try
        {
            StatusText.Foreground = System.Windows.Media.Brushes.DimGray;
            StatusText.Text = Loc.T("Settings.StatusRestarting"); // 复用"正在…"提示文案(登出也用进度语义)
            await LogoutRequested();
        }
        catch (Exception ex)
        {
            Fail(Loc.F("Error.LogoutFail", ex.Message));
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
