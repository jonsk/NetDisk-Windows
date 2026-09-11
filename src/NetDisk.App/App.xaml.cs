using System.Windows;

namespace NetDisk.App;

/// <summary>
/// WPF 应用入口(DE-D-01 骨架)。
///
/// 这一版只负责"能起来";真正的启动序列(登录 → 首次运行向导 → watcher →
/// SSE 消费)在 DE-D-03/13/15 接入。刻意不在这里写业务:App 的职责边界是
/// "组合根 + 窗口",一旦往里堆逻辑,它就变成第二个内核(而那个内核没有单测)。
/// </summary>
public partial class App : Application
{
}
