// 托盘通知适配器(DE-D-18;App 层,允许用 Windows UI)。
//
// 这一层只做一件事:**把 <see cref="NotificationCenter"/> 的通知画成托盘气泡**。
// 它刻意不含任何"要不要通知"的判断 —— 那些(合并窗口、配额滞回)都在 SyncEngine 里,
// 因为**它们是可以单测的逻辑**,而"什么时候该弹"一旦落到 UI 层,就只能靠人肉点着看了。
//
// DE-D-21 补上了这一层最容易漏掉的一件事:**marshal 回 UI 线程**。
// `NotificationCenter.Raised` 是在**同步引擎的后台线程**上触发的(同步完成/冲突/配额
// 都是后台算出来的),而 `NotifyIcon` 是 UI 对象。跨线程碰 UI 对象的表现是随机的:
// 有时只是气泡不显示,有时是句柄泄漏(托盘图标所在的隐藏窗口被反复创建),
// 有时在退出时崩 —— 都属于"偶发、难查、被当成玄学"的那类问题。
// 所以这里显式 `CheckAccess` + `BeginInvoke`,并且在真正碰 UI 对象的那一行
// 让 `ThreadDiscipline` 记账:一旦有人删掉 marshal,soak 里的"违规数"就不再是 0。
//
// 两个实现细节:
//   - 用 `NotifyIcon` 显示气泡(Windows 托盘原生能力,不需要额外依赖);
//   - 图标用 `SystemIcons.Application`(系统自带),这样骨架阶段不必先塞一个 .ico 资源;
//     DE-D-20 打包时会换成产品图标。

using System.Drawing;
using System.Windows.Forms;
using System.Windows.Threading;
using NetDisk.SyncEngine.Diag;
using NetDisk.SyncEngine.Notify;

namespace NetDisk.App.Notify;

/// <summary>托盘图标 + 气泡通知(把后台通知 marshal 回 UI 线程后显示)。</summary>
public sealed class TrayNotifier : IDisposable
{
    private readonly NotificationCenter _center;
    private readonly NotifyIcon _icon;
    private readonly Dispatcher _dispatcher;
    private bool _disposed;

    public TrayNotifier(NotificationCenter center, string tooltip = "NetDisk")
    {
        _center = center ?? throw new ArgumentNullException(nameof(center));

        // 构造必须发生在 UI 线程:构造里就创建了托盘图标(UI 对象)。
        ThreadDiscipline.AssertOnUiThread(nameof(TrayNotifier));
        _dispatcher = Dispatcher.CurrentDispatcher;

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = tooltip.Length > 63 ? tooltip[..63] : tooltip, // NotifyIcon.Text 上限 63 字符
        };
        _center.Raised += OnNotification;
    }

    /// <summary>已经显示过的气泡条数(诊断/检查用)。</summary>
    public int ShownCount { get; private set; }

    /// <summary>因为跨线程而被 marshal 回 UI 线程的通知条数(诊断/检查用)。</summary>
    public int MarshalledCount { get; private set; }

    private void OnNotification(AppNotification n)
    {
        if (_disposed)
        {
            return;
        }

        if (!_dispatcher.CheckAccess())
        {
            // 后台线程 → 回 UI 线程。用 BeginInvoke(而不是 Invoke):
            // 同步引擎线程不该在 UI 线程繁忙时被阻塞(那会把同步卡住)。
            MarshalledCount++;
            try
            {
                if (!_dispatcher.HasShutdownStarted && !_dispatcher.HasShutdownFinished)
                {
                    _dispatcher.BeginInvoke(new Action(() => OnNotification(n)));
                }
            }
            catch (Exception)
            {
                // 进程正在退出时 Dispatcher 可能已经停了;丢一条气泡不影响正确性
            }
            return;
        }

        if (_disposed)
        {
            return;
        }

        // 到这里必须已经是 UI 线程 —— 这一行就是纪律的记账点。
        ThreadDiscipline.AssertOnUiThread(nameof(ShowBalloonTip));
        ShowBalloonTip(n);
    }

    private void ShowBalloonTip(AppNotification n)
    {
        // 按通知类型选图标:冲突/配额用警告,其余用信息
        var icon = n.Kind is NotificationKind.Conflict or NotificationKind.QuotaWarning
            ? ToolTipIcon.Warning
            : ToolTipIcon.Info;
        _icon.ShowBalloonTip(5000, n.Title, n.Message, icon);
        ShownCount++;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _center.Raised -= OnNotification;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
