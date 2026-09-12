// 托盘通知适配器(DE-D-18;App 层,允许用 Windows UI)。
//
// 这一层只做一件事:**把 <see cref="NotificationCenter"/> 的通知画成托盘气泡**。
// 它刻意不含任何"要不要通知"的判断 —— 那些(合并窗口、配额滞回)都在 SyncEngine 里,
// 因为**它们是可以单测的逻辑**,而"什么时候该弹"一旦落到 UI 层,就只能靠人肉点着看了。
//
// 两个实现细节:
//   - 用 `NotifyIcon` 显示气泡(Windows 托盘原生能力,不需要额外依赖);
//   - 图标用 `SystemIcons.Application`(系统自带),这样骨架阶段不必先塞一个 .ico 资源;
//     DE-D-20 打包时会换成产品图标。

using System.Drawing;
using System.Windows.Forms;
using NetDisk.SyncEngine.Notify;

namespace NetDisk.App.Notify;

/// <summary>托盘图标 + 气泡通知。</summary>
public sealed class TrayNotifier : IDisposable
{
    private readonly NotificationCenter _center;
    private readonly NotifyIcon _icon;
    private bool _disposed;

    public TrayNotifier(NotificationCenter center, string tooltip = "NetDisk")
    {
        _center = center ?? throw new ArgumentNullException(nameof(center));
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

    private void OnNotification(AppNotification n)
    {
        if (_disposed)
        {
            return;
        }
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
