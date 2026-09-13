// 客户端日志(纯追加写文件 + 界面开关)。
//
// 为什么必须有它:桌面客户端的故障现场在**用户机器上**,而托盘气泡一闪而过、状态列表只活在界面上 ——
// 没有日志就只能靠"你再点一次我看看"。有了它,用户把文件发过来即可(或评测时自己看)。
//
// 2026-09-13 按产品要求增强:
//   · **界面上有开关,默认开启**(开关状态存进 client.json);
//   · 位置跟着程序走:`<程序目录>\logs\client.log`(见 ClientPaths);
//   · 除了引擎进展,还记**每个文件的状态变化**(排查"为什么这个文件没同步"时最有用)、
//     启动会话头(版本/OS/路径/生效配置)与**所有未处理异常**(带堆栈)。
//
// 三条纪律:
//   ① **绝不写口令/令牌**:只写人类可读的进展与错误(令牌走 DPapiTokenStore 加密落盘);
//   ② **绝不因为日志失败而影响同步**:磁盘满/权限不足时静默放弃(日志是辅助,不是功能);
//   ③ 单文件超过阈值就切换一次(`.1`),避免无限增长把用户磁盘写满。

using System.IO;
using System.Text;
using NetDisk.SyncEngine.Host;

namespace NetDisk.App;

/// <summary>极简文件日志(线程安全;可在运行时开关;失败静默)。</summary>
public static class AppLog
{
    private const long MaxBytes = 4 * 1024 * 1024;
    private static readonly object Gate = new();
    private static bool _enabled = true;
    private static bool _warned;          // 写失败只提示一次,避免刷屏
    private static int _disabledWrites;   // 关掉期间丢掉的条数(重新打开时补一行说明)

    /// <summary>日志是否启用(界面开关;默认开)。</summary>
    public static bool Enabled
    {
        get => _enabled;
        set
        {
            lock (Gate)
            {
                if (_enabled == value)
                {
                    return;
                }
                _enabled = value;
            }
            if (value)
            {
                Write("app", $"日志已启用(此前关闭期间丢弃 {_disabledWrites} 条)");
                _disabledWrites = 0;
            }
            else
            {
                Write("app", "日志已关闭(此后的进展不再记录)");
            }
        }
    }

    /// <summary>日志文件路径(界面上显示给用户,便于自查与反馈)。</summary>
    public static string DefaultPath() => ClientPaths.LogPath;

    /// <summary>写一行(thread id 记下来:线程纪律类问题只能靠它定位)。</summary>
    public static void Write(string tag, string message)
    {
        lock (Gate)
        {
            if (!_enabled)
            {
                _disabledWrites++;
                return;
            }
            try
            {
                var path = DefaultPath();
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Move(path, path + ".1", overwrite: true);
                }
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [t{Environment.CurrentManagedThreadId:D2}] [{tag}] {message}";
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
                _warned = false;
            }
            catch (Exception)
            {
                // 见纪律 ②:日志写不下去不能影响同步(只提示一次,避免自己变成刷屏源)
                if (!_warned)
                {
                    _warned = true;
                }
            }
        }
    }

    /// <summary>
    /// 会话头:每条日志文件开头都应能回答"这是哪台机器、哪个版本、哪些路径、哪个配置"。
    /// 少了它,拿到一份日志往往连"是不是同一台机器"都判断不了。
    /// </summary>
    public static void WriteSessionHeader(string version, string configPath, ClientConfig cfg)
    {
        Write("session", "================ NetDisk 客户端启动 ================");
        Write("session", $"版本: {version}   OS: {Environment.OSVersion}   .NET: {Environment.Version}");
        Write("session", $"程序目录: {AppContext.BaseDirectory}");
        Write("session", $"数据目录: {ClientPaths.DataDirectory}" +
                       (ClientPaths.FallbackReason is { } why ? $"  (回退原因: {why})" : "  (程序所在目录)"));
        Write("session", $"配置文件: {configPath}");
        Write("session", $"服务器: {(string.IsNullOrWhiteSpace(cfg.BaseUrl) ? "(未配置)" : cfg.BaseUrl)}" +
                       $"   同步目录: {(string.IsNullOrWhiteSpace(cfg.SyncRoot) ? "(未配置)" : cfg.SyncRoot)}" +
                       $"   已完成首次运行: {cfg.Onboarded}");
        Write("session", $"日志: {(Enabled ? "启用" : "关闭")}(可在「同步」页切换)");
        Write("session", $"命令行: {Environment.CommandLine}");
    }
}
