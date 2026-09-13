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
        BuildContextMenu();
        // 双击图标 = 打开主界面(用户对托盘图标的默认期待;没有它,窗口隐藏后就"找不回来"了)
        _icon.DoubleClick += (_, _) => ShowRequested?.Invoke();
        _center.Raised += OnNotification;
    }

    // ---------------------------------------------------------------- 右键菜单

    private ToolStripMenuItem _pauseItem = null!;

    /// <summary>用户要求打开主界面(双击图标或菜单)。</summary>
    public event Action? ShowRequested;

    /// <summary>用户要求立即同步。</summary>
    public event Action? SyncNowRequested;

    /// <summary>用户要求暂停/继续同步(参数 = 目标状态:true = 暂停)。</summary>
    public event Action<bool>? TogglePauseRequested;

    /// <summary>用户要求打开同步目录。</summary>
    public event Action? OpenFolderRequested;

    /// <summary>用户要求打开日志文件。</summary>
    public event Action? OpenLogRequested;

    /// <summary>用户要求**真正退出**(不是关到托盘)。</summary>
    public event Action? ExitRequested;

    /// <summary>
    /// 建右键菜单。为什么必须有:窗口"关闭到托盘"之后,托盘是用户唯一的入口 ——
    /// 没有菜单就只能双击,而"怎么退出"会变成猜谜(用户会去任务管理器杀进程,
    /// 那会让同步半途中断)。
    /// </summary>
    private void BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("打开主界面", null, (_, _) => ShowRequested?.Invoke()));
        menu.Items.Add(new ToolStripMenuItem("立即同步", null, (_, _) => SyncNowRequested?.Invoke()));
        _pauseItem = new ToolStripMenuItem("暂停同步", null,
            (_, _) => TogglePauseRequested?.Invoke(!_paused));
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("打开同步目录", null, (_, _) => OpenFolderRequested?.Invoke()));
        menu.Items.Add(new ToolStripMenuItem("打开日志", null, (_, _) => OpenLogRequested?.Invoke()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => ExitRequested?.Invoke()));
        _icon.ContextMenuStrip = menu;
    }

    private bool _paused;

    /// <summary>同步当前是否是暂停态(菜单文字据此显示"暂停同步/继续同步")。</summary>
    public void SetPaused(bool paused)
    {
        _paused = paused;
        if (_pauseItem is not null)
        {
            _pauseItem.Text = paused ? "继续同步" : "暂停同步";
        }
    }

    /// <summary>菜单项文字(供检查器/诊断读取;菜单本身是 UI 对象,不该被测试直接摸)。</summary>
    public string PauseItemText => _pauseItem?.Text ?? "";

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
