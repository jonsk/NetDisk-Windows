// 主窗口:决定"先给用户看登录页还是同步页",并在登录成功后把运行时接到视图上。
//
// 启动路径(两条):
//   ① 已配置过 + 令牌还能用 → 直接进同步页并开始对账(用户不需要每天输口令);
//   ② 其余情况 → 登录页。
// "令牌还能用"这件事由引擎判定(TokenSession/Host.StartAsync 返回 false),
// 这里不自己解析令牌、不看过期时间 —— 那种判断一旦分叉就会出现"界面以为登录着、
// 实际每个请求都 401"的状态。

using System.Windows;
using NetDisk.App.Views;
using NetDisk.SyncEngine;
using NetDisk.SyncEngine.Host;
using NetDisk.SyncEngine.Notify;

namespace NetDisk.App;

public partial class MainWindow : Window
{
    private readonly NotificationCenter _notifications;
    private SyncRuntime? _runtime;
    private bool _conflictNotified;

    public MainWindow(NotificationCenter notifications)
    {
        _notifications = notifications;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>当前跑着的运行时(诊断用;没有则为 null)。</summary>
    public SyncRuntime? Runtime => _runtime;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var cfg = ClientConfig.Load();
        if (!cfg.IsUsable() || !cfg.Onboarded)
        {
            ShowLogin();
            return;
        }

        // 有配置:先按"已保存的令牌"试着直接跑起来,失败再回登录页
        try
        {
            var runtime = SyncRuntime.FromStoredToken(cfg);
            if (await runtime.StartAsync())
            {
                Attach(runtime);
                return;
            }
            await runtime.DisposeAsync();
        }
        catch (Exception)
        {
            // 令牌坏了/服务端不可达:都退回登录页(理由由登录页在用户点按钮后给出)
        }
        ShowLogin();
    }

    private void ShowLogin()
    {
        Tabs.Visibility = Visibility.Collapsed;
        Login.Visibility = Visibility.Visible;
    }

    private void OnSignedIn(SyncRuntime runtime) => Attach(runtime);

    private void Attach(SyncRuntime runtime)
    {
        _runtime = runtime;
        Sync.Attach(runtime);
        // 团队空间页也要用**带令牌**的客户端:否则它在真实使用中只能匿名请求,
        // 表现是"空间列表永远是空的"(而用户会以为是没有空间)。
        Spaces.Attach(runtime.Api);
        runtime.Host.StatusChanged += OnStatusChanged;
        // 先补记历史:头几轮对账的进展发生在订阅之前(引擎在 StartAsync 里就开始了),
        // 不补记的话日志里会出现"什么都没有"的假象 —— 实测第一次就被这个误导过。
        foreach (var (at, message) in runtime.RecentNotices)
        {
            AppLog.Write("sync", $"{at.ToLocalTime():HH:mm:ss} {message}");
        }
        runtime.Notice += OnEngineNotice;
        AppLog.Write("app", $"已接入同步(空间={runtime.Config.SpaceId} 根={runtime.Config.SyncRoot})");

        Login.Visibility = Visibility.Collapsed;
        Tabs.Visibility = Visibility.Visible;
        Tabs.SelectedIndex = 0;
    }

    /// <summary>
    /// 把引擎的进展写进日志文件。
    /// 为什么需要:`%APPDATA%\NetDisk\app.log` 是**唯一**能在用户机器上事后查证的痕迹
    /// (托盘气泡一闪而过,状态列表只在界面上)。出问题时让用户把这个文件发过来即可。
    /// </summary>
    private void OnEngineNotice(string message) => AppLog.Write("sync", message);

    /// <summary>
    /// 冲突要弹托盘气泡(目标 ④:通知保持可用)。
    ///
    /// 这里只做 UI 的"是否已经提示过"记账;冲突**本身**是引擎算出来的
    /// (状态列表里的 Conflict + 消息里的副本名)。同一次冲突不重复弹:
    /// 状态列表每轮对账都会推一次,不记账的话用户会被同一个冲突反复打扰。
    /// </summary>
    private void OnStatusChanged(IReadOnlyList<SyncEntryStatus> status)
    {
        var conflict = status.FirstOrDefault(s => s.State == SyncState.Conflict);
        if (conflict is null)
        {
            _conflictNotified = false;
            return;
        }
        if (_conflictNotified)
        {
            return;
        }
        _conflictNotified = true;
        _notifications.NotifyConflict(
            conflict.RelativePath,
            localVersion: conflict.Version,
            remoteVersion: conflict.Version,
            spaceId: _runtime?.Config.SpaceId);
    }

    protected override async void OnClosed(EventArgs e)
    {
        if (_runtime is not null)
        {
            try
            {
                await _runtime.DisposeAsync();
            }
            catch (Exception)
            {
                // 退出路径不抛异常
            }
            _runtime = null;
        }
        base.OnClosed(e);
    }
}
