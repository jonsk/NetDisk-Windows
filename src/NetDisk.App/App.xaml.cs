using System.Globalization;
using System.IO;
using System.Windows;
using NetDisk.App.Notify;
using NetDisk.SyncEngine.Diag;
using NetDisk.SyncEngine.Notify;

namespace NetDisk.App;

/// <summary>
/// WPF 应用入口。
///
/// 组合根做五件事:①**起誓 UI 线程**(DE-D-21:只有 App 知道谁是 UI 线程)、
/// ②按环境变量开 soak 采样、③建**通知中心**(DE-D-18)、④把它接到**托盘气泡**、
/// ⑤建主窗口(登录页 → 同步页);退出时释放。
/// 业务逻辑(该不该通知、合并窗口、配额滞回)都在 SyncEngine 的 NotificationCenter 里 ——
/// 那不是 UI 决策,而是可单测的策略;同步本身在 SyncHost 里,App 只订阅事件。
/// </summary>
public partial class App : System.Windows.Application
{
    private NotificationCenter? _notifications;
    private TrayNotifier? _tray;
    private SoakRecorder? _soak;
    private System.Windows.Threading.DispatcherTimer? _soakStopWatch;
    private MainWindow? _main;

    public NotificationCenter Notifications =>
        _notifications ?? throw new InvalidOperationException("通知中心尚未初始化");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ① 线程纪律的"起誓点"。放在最前面:在它之前发生的任何 UI 触碰都应该被记为
        //    "探测器未启用"违规,而不是被悄悄放过。
        ThreadDiscipline.MarkUiThread();

        // ② 长跑 soak(默认关;NETDISK_SOAK=1 打开,落盘 NETDISK_SOAK_CSV)
        _soak = SoakRecorder.StartFromEnvironment();
        ArmSoakStopWatch();

        _notifications = new NotificationCenter();
        _tray = new TrayNotifier(_notifications);

        // 主窗口在 App 里建(而不是 StartupUri),因为托盘/通知必须先于窗口存在:
        // 否则登录成功后引擎一冲突就没人接住那条通知(用户什么都看不到)。
        _main = new MainWindow(_notifications);
        MainWindow = _main;
        _main.Show();
    }

    /// <summary>
    /// soak 的**优雅停止通道**(NETDISK_SOAK_STOP_FILE)。
    ///
    /// 为什么需要它:soak 的结论写在客户端的退出路径里(OnExit 里判定并落盘),
    /// 而"让一个 GUI 程序退出"这件事本身并不可靠 —— 外部脚本请求关闭窗口
    /// (`Process.CloseMainWindow`)在无交互桌面/句柄缓存过期等情况下会**静默失败**
    /// (实测:返回 false 且进程不动),结果是跑了 24 小时却拿不到结论,只能强杀,
    /// 而强杀恰好丢掉了最后一段最有用的采样。所以给一个由客户端自己轮询的停止文件:
    /// 脚本只负责创建这个文件,剩下的交给客户端。
    ///
    /// 用 DispatcherTimer(而不是后台线程)是刻意的:退出必须由 UI 线程发起,
    /// 而"回调确实在 UI 线程上"这件事正好被 ThreadDiscipline 记账(见下)。
    /// </summary>
    private void ArmSoakStopWatch()
    {
        var stopFile = Environment.GetEnvironmentVariable("NETDISK_SOAK_STOP_FILE");
        if (_soak is null || string.IsNullOrWhiteSpace(stopFile))
        {
            return;
        }

        _soakStopWatch = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _soakStopWatch.Tick += (_, _) =>
        {
            // DispatcherTimer 的回调必须落在 UI 线程上;若哪天它不再如此(或被挪到别处),
            // 这里会留下违规记录,而不是继续"看起来正常"。
            ThreadDiscipline.AssertOnUiThread(nameof(ArmSoakStopWatch));

            if (!File.Exists(stopFile))
            {
                return;
            }
            _soakStopWatch?.Stop();
            Shutdown(); // 走正常退出路径,OnExit 里写 verdict
        };
        _soakStopWatch.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _soakStopWatch?.Stop();
        WriteSoakVerdict();
        _tray?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// 退出时把 soak 结论落盘。
    ///
    /// 为什么不只在脚本里判:soak 跑的是**真实客户端**,它的运行时长只有它自己知道
    /// (用户随时可能关掉窗口)。把判定与原始采样一起留在磁盘上,运维/验收才能看到
    /// "这次到底跑了多久、句柄从多少到多少" —— 而不是只看一个"通过"。
    /// </summary>
    private void WriteSoakVerdict()
    {
        if (_soak is null)
        {
            return;
        }
        try
        {
            var requiredHours = 24.0;
            if (double.TryParse(Environment.GetEnvironmentVariable("NETDISK_SOAK_REQUIRED_HOURS"),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
            {
                requiredHours = parsed;
            }

            var verdict = _soak.Verdict(TimeSpan.FromHours(requiredHours));
            var path = _soak.CsvPath + ".verdict.txt";
            var lines = new List<string>
            {
                verdict.Passed ? "PASS" : "FAIL",
                verdict.Summary,
                "要求跨度: " + (requiredHours >= 0.1 ? requiredHours.ToString("F1", CultureInfo.InvariantCulture) + "h" : (requiredHours * 60).ToString("F1", CultureInfo.InvariantCulture) + "min"),
            };
            lines.AddRange(verdict.Reasons.Select(r => "原因: " + r));
            File.WriteAllLines(path, lines);
        }
        catch (Exception)
        {
            // 退出路径绝不抛异常(抛了就变成"关了程序还报错")
        }
        finally
        {
            _soak.Dispose();
        }
    }
}
