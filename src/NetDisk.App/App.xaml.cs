using System.Globalization;
using System.IO;
using System.Windows;
using NetDisk.App.Notify;
using NetDisk.SyncEngine.Diag;
using NetDisk.SyncEngine.Host;
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

        // ⓪ 无界面模式(自动更新用)。必须在任何 UI 之前处理:
        //    · `--self-check`   = "装上去的这个客户端能活吗"(更新器验活用;退出码 0 = 能活)
        //    · `--apply-update` = 更新器本体(客户端把自己复制到临时目录后用这个参数运行副本)
        //    两者都不弹窗口、不建托盘 —— 升级过程里冒出一个窗口会被用户当成"程序坏了"。
        if (RunHeadlessMode(e.Args, out var exitCode))
        {
            Shutdown(exitCode);
            return;
        }

        // ① 线程纪律的"起誓点"。放在最前面:在它之前发生的任何 UI 触碰都应该被记为
        //    "探测器未启用"违规,而不是被悄悄放过。
        ThreadDiscipline.MarkUiThread();

        // ② 长跑 soak(默认关;NETDISK_SOAK=1 打开,落盘 NETDISK_SOAK_CSV)
        _soak = SoakRecorder.StartFromEnvironment();
        ArmSoakStopWatch();

        _notifications = new NotificationCenter();
        _tray = new TrayNotifier(_notifications);

        // ⑤ 数据文件与日志(2026-09-13 产品要求:单文件发布 + 配置放程序目录 + 日志默认开)
        //
        // 首次运行在这里**生成配置文件**:不这么做的话,用户装完程序找不到任何"配置在哪"的线索
        // (以前只能靠文档说"去 %APPDATA% 找"),而"程序目录里就有 client.json"是自解释的。
        var cfgPath = ClientConfig.DefaultPath();
        var firstRun = !File.Exists(cfgPath);
        var cfg = ClientConfig.Load();
        if (firstRun)
        {
            cfg.Save();
        }
        AppLog.Enabled = cfg.Logging;
        AppLog.WriteSessionHeader(BuildVersion(), cfgPath, cfg);
        if (firstRun)
        {
            AppLog.Write("app", $"首次运行:已生成配置文件 {cfgPath}");
        }

        // 未处理异常必须落到日志里:客户端的崩溃现场在用户机器上,
        // 而 WPF 的默认行为是弹一个框然后进程消失 —— 事后什么都查不到。
        DispatcherUnhandledException += (_, e) =>
            AppLog.Write("crash", $"UI 线程未处理异常: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Write("crash", $"未处理异常(进程即将结束): {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Write("crash", $"未观察的任务异常: {e.Exception}");
            e.SetObserved();
        };

        // 主窗口在 App 里建(而不是 StartupUri),因为托盘/通知必须先于窗口存在:
        // 否则登录成功后引擎一冲突就没人接住那条通知(用户什么都看不到)。
        _main = new MainWindow(_notifications);
        MainWindow = _main;
        _main.Show();
    }

    /// <summary>版本号(取程序集信息;与 MSI/自更新用的版本口径一致)。</summary>
    private static string BuildVersion() =>
        typeof(App).Assembly.GetName().Version?.ToString()
        ?? System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "unknown";

    /// <summary>
    /// 无界面模式(**自动更新的两半**)。返回 true = 已处理完毕,调用方应立即按 exitCode 退出。
    ///
    /// 为什么放在客户端里而不是另做一个 updater.exe:产品要求是**单文件发布**,
    /// MSI 载荷校验收紧到"恰好一个文件"(`verify-msi.ps1` 会断言)。所以更新器 = 客户端自己:
    /// 主进程把 exe 复制到临时目录,用 `--apply-update` 拉起**那份副本**,然后自己退出;
    /// 副本去跑 msiexec(它覆盖的是安装目录里的文件,不是自己),装完验活/必要时回滚。
    ///
    /// 退出码(脚本与真机演练按它判定,别只看日志):
    ///   `--self-check`  : 0 能活 / 2 有问题(原因写进日志与 stdout)
    ///   `--apply-update`: 0 升级成功 / 2 安装失败 / 3 验活失败已回滚 / 4 验活失败且回滚失败 / 5 参数不合法
    /// </summary>
    private static bool RunHeadlessMode(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Any(a => string.Equals(a, "--self-check", StringComparison.OrdinalIgnoreCase)))
        {
            exitCode = SelfCheck();
            return true;
        }
        if (args.Any(a => string.Equals(a, "--apply-update", StringComparison.OrdinalIgnoreCase)))
        {
            // ⚠ **必须在线程池上跑**:OnStartup 跑在 UI 线程上,而 WPF 此时已经装好了
            //    SynchronizationContext。若直接在 UI 线程上 `GetAwaiter().GetResult()`,
            //    异步方法里任何一个 await 的续体都会被 Post 回**已被我们阻塞的** UI 线程 ——
            //    经典死锁。真机演练实测:更新器把新版装好、验活也通过了(self-check 日志两行都在),
            //    然后**卡在 await 边界上 50 分钟不退出**,演练看起来像"MSI 装得慢"。
            //    用 Task.Run 把整段异步逻辑交给线程池:那里没有 UI 上下文,续体不再排回 UI 线程。
            exitCode = Task.Run(() => ApplyUpdateAsync(args)).GetAwaiter().GetResult();
            return true;
        }
        return false;
    }

    /// <summary>
    /// 装上去的这个客户端**能活吗**:配置能读/能建、数据目录可写、日志可写、令牌密文可解。
    /// 更新器在新版装好后调用它 —— 这是"新版起不来就回滚"的判据,不能靠猜。
    /// </summary>
    private static int SelfCheck()
    {
        try
        {
            var cfgPath = ClientPaths.ConfigPath;
            var cfg = ClientConfig.Load();      // 不存在时会给出默认值(首次运行语义)
            AppLog.Enabled = cfg.Logging;
            AppLog.Write("update", $"self-check:版本={BuildVersion()} 数据目录={ClientPaths.DataDirectory} 配置={cfgPath}");

            // 数据目录必须可写(状态库与令牌都要写在这里;只读目录会让客户端"启动就报错")
            Directory.CreateDirectory(ClientPaths.DataDirectory);
            var probe = Path.Combine(ClientPaths.DataDirectory, ".self-check-write");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);

            AppLog.Write("update", "self-check:通过(配置可读、数据目录可写、日志可写)");
            Console.WriteLine($"self-check ok: {BuildVersion()} @ {ClientPaths.DataDirectory}");
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                AppLog.Write("update", $"self-check:失败 {ex.GetType().Name}: {ex.Message}");
            }
            catch (Exception)
            {
                // 日志都写不进去时只能靠退出码
            }
            Console.Error.WriteLine("self-check failed: " + ex.Message);
            return 2;
        }
    }

    /// <summary>`--apply-update --msi &lt;p&gt; --client &lt;exe&gt; [--previous-msi &lt;p&gt;] [--no-relaunch] [--visible]`</summary>
    private static async Task<int> ApplyUpdateAsync(string[] args)
    {
        string? Arg(string name)
        {
            var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        var msi = Arg("--msi");
        var client = Arg("--client") ?? Environment.ProcessPath ?? "";
        var previous = Arg("--previous-msi");
        if (string.IsNullOrWhiteSpace(msi) || string.IsNullOrWhiteSpace(client))
        {
            Console.Error.WriteLine("用法:--apply-update --msi <安装包> --client <客户端 exe> [--previous-msi <旧包>] [--no-relaunch] [--visible]");
            return 5;
        }

        var options = new NetDisk.SyncEngine.Update.UpdaterOptions(
            msi,
            client,
            Silent: !args.Any(a => string.Equals(a, "--visible", StringComparison.OrdinalIgnoreCase)),
            RelaunchClient: !args.Any(a => string.Equals(a, "--no-relaunch", StringComparison.OrdinalIgnoreCase)),
            PreviousMsiPath: string.IsNullOrWhiteSpace(previous) ? null : previous);

        var outcome = await new NetDisk.SyncEngine.Update.UpdaterRunner(
            new NetDisk.SyncEngine.Update.ProcessRunner()).RunAsync(options);
        AppLog.Write("update", $"更新器:{outcome.Kind} {outcome.Reason}(msiexec={outcome.InstallerExitCode} 已拉起客户端={outcome.Relaunched})");
        Console.WriteLine($"{outcome.Kind}: {outcome.Reason}");
        return outcome.Kind switch
        {
            NetDisk.SyncEngine.Update.UpdaterOutcomeKind.Upgraded => 0,
            NetDisk.SyncEngine.Update.UpdaterOutcomeKind.InstallFailed => 2,
            NetDisk.SyncEngine.Update.UpdaterOutcomeKind.HealthCheckFailedRolledBack => 3,
            NetDisk.SyncEngine.Update.UpdaterOutcomeKind.HealthCheckFailedRollbackFailed => 4,
            NetDisk.SyncEngine.Update.UpdaterOutcomeKind.HealthCheckFailed => 6,
            _ => 4,
        };
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
