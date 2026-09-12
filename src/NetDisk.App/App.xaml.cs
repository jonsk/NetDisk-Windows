using System.Windows;
using NetDisk.App.Notify;
using NetDisk.SyncEngine.Notify;

namespace NetDisk.App;

/// <summary>
/// WPF 应用入口。
///
/// 组合根只做三件事:建**通知中心**(DE-D-18)、把它接到**托盘气泡**、退出时释放。
/// 业务逻辑(该不该通知、合并窗口、配额滞回)都在 SyncEngine 的 NotificationCenter 里 ——
/// 那不是 UI 决策,而是可单测的策略。
///
/// 同步引擎/传输队列/登录会话在 DE-D-14/15 已就绪,接入它们属于后续的接线工作;
/// 这里先把"通知 → 托盘"这条链路立起来(验收:四类通知可触发)。
/// </summary>
public partial class App : System.Windows.Application
{
    private NotificationCenter? _notifications;
    private TrayNotifier? _tray;

    public NotificationCenter Notifications =>
        _notifications ?? throw new InvalidOperationException("通知中心尚未初始化");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _notifications = new NotificationCenter();
        _tray = new TrayNotifier(_notifications);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }
}