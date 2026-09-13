// 客户端数据文件的位置(配置 / 令牌 / 状态库 / 日志)—— **一处定义**,别处只许引用。
//
// 为什么要有这个类(2026-09-13 产品要求):打包改成**单文件发布**后,客户端应该像绿色软件一样
// "程序在哪、配置就在哪":首次运行在**程序所在目录**生成配置文件,之后都从那里读。
// 在此之前四处各自拼 `%APPDATA%\NetDisk`(配置、令牌、状态库、日志),想改位置得改四个地方 ——
// 而"改漏一处"的表现是"配置读到了、令牌没读到"这种半截状态,极难查。
//
// 三条纪律:
//   ① "程序所在目录"取 `AppContext.BaseDirectory`(**exe 所在目录**),不取 `Environment.CurrentDirectory`:
//      当前目录取决于用户从哪启动(双击=exe 目录、快捷方式=其"起始位置"、终端=终端目录、
//      计划任务=system32)—— 同一个程序会读到不同的配置,这种"配置漂移"用户根本无法理解。
//   ② 该目录**不可写**时(例如被装到 Program Files)回退到 `%APPDATA%\NetDisk`,并把
//      "实际用了哪个目录"写进日志 —— 静默回退会让人以为"配置没生效"。
//   ③ 从旧的 %APPDATA% 位置做**一次性迁移**(只搬不删源,失败也只是重来一次)。

using System.Diagnostics.CodeAnalysis;

namespace NetDisk.SyncEngine.Host;

/// <summary>客户端数据文件位置的唯一来源。</summary>
public static class ClientPaths
{
    private static readonly object Gate = new();
    private static string? _dataDir;
    private static string? _fallbackReason;

    /// <summary>放配置/令牌/状态库/日志的目录(程序所在目录;不可写则回退 %APPDATA%）。</summary>
    public static string DataDirectory
    {
        get
        {
            lock (Gate)
            {
                if (_dataDir is not null)
                {
                    return _dataDir;
                }

                var exeDir = (ExeDirectoryOverride ?? AppContext.BaseDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar);
                if (!string.IsNullOrEmpty(exeDir) && IsWritable(exeDir))
                {
                    _dataDir = exeDir;
                }
                else
                {
                    var legacy = LegacyDirectory;
                    Directory.CreateDirectory(legacy);
                    _fallbackReason = $"程序目录不可写({exeDir}),已回退到 {legacy}";
                    _dataDir = legacy;
                }
                MigrateIfNeeded(_dataDir);
                return _dataDir;
            }
        }
    }

    /// <summary>旧的 %APPDATA% 位置(迁移来源;也为"回退"复用）。</summary>
    public static string LegacyDirectory =>
        LegacyDirectoryOverride
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetDisk");

    /// <summary>
    /// **测试注入**:覆盖"程序所在目录"(默认 <see cref="AppContext.BaseDirectory"/>)。
    ///
    /// 为什么需要它:回退与一次性迁移这两个分支只在特定环境里才走到 ——
    /// "装到 Program Files 导致不可写"和"用户从旧版本升级上来、旧位置还有数据"都不是随手能造的现场。
    /// 不给缝的话,① 的核心逻辑就只能靠**间接**证据(MSI 装完看文件落在哪),那证明不了回退与迁移。
    /// 与项目里既有的注入点同一做法(如 <c>ApiClientOptions.DelayAsync</c>)。
    /// </summary>
    public static string? ExeDirectoryOverride { get; set; }

    /// <summary>**测试注入**:覆盖旧的 %APPDATA%\NetDisk(迁移来源/回退落点)。</summary>
    public static string? LegacyDirectoryOverride { get; set; }

    public static string ConfigPath => Path.Combine(DataDirectory, "client.json");

    public static string TokenPath => Path.Combine(DataDirectory, "tokens.bin");

    public static string StatePath => Path.Combine(DataDirectory, "state.db");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    public static string LogPath => Path.Combine(LogDirectory, "client.log");

    /// <summary>回退原因(为空 = 用的就是程序所在目录);启动时写进日志,别让人猜。</summary>
    public static string? FallbackReason
    {
        get
        {
            _ = DataDirectory; // 触发一次探测
            return _fallbackReason;
        }
    }

    /// <summary>目录是否可写(用一次真实写入探测:只判 ACL/只读位会漏掉很多实际情况)。</summary>
    private static bool IsWritable(string dir)
    {
        try
        {
            if (!Directory.Exists(dir))
            {
                return false;
            }
            var probe = Path.Combine(dir, ".netdisk-write-test");
            using (var fs = new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.WriteByte(0);
            }
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 把旧位置(%APPDATA%\NetDisk)的数据**搬到**程序所在目录(仅在目标不存在时)。
    ///
    /// 用 Move 而不是 Copy:两份配置同时存在必然漂移,而漂移的表现是"我改的设置没生效"
    /// (程序读的是另一份)。搬完源目录里只剩旧日志,不影响。
    /// 任何一步失败都不抛:最坏结果是用户重新登录一次,而不是程序起不来。
    /// </summary>
    private static void MigrateIfNeeded(string dataDir)
    {
        try
        {
            var legacy = LegacyDirectory;
            if (string.Equals(
                    Path.GetFullPath(legacy).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(dataDir).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                return; // 回退场景:目标本身就是旧目录,没什么可搬
            }
            MoveIfAbsent(Path.Combine(legacy, "client.json"), Path.Combine(dataDir, "client.json"));
            MoveIfAbsent(Path.Combine(legacy, "tokens.bin"), Path.Combine(dataDir, "tokens.bin"));
            MoveIfAbsent(Path.Combine(legacy, "state.db"), Path.Combine(dataDir, "state.db"));
        }
        catch (Exception)
        {
            // 迁移失败不阻塞启动(见上)
        }
    }

    [SuppressMessage("Design", "CA1031", Justification = "迁移失败不阻塞启动")]
    private static void MoveIfAbsent(string from, string to)
    {
        try
        {
            if (!File.Exists(from) || File.Exists(to))
            {
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Move(from, to, overwrite: false);
        }
        catch (Exception)
        {
            // 单个文件搬不动就算了(其余继续)
        }
    }

    /// <summary>测试/诊断用:清掉缓存,让下一次访问重新探测(检查器会在临时目录里跑)。</summary>
    public static void ResetCacheForTests()
    {
        lock (Gate)
        {
            _dataDir = null;
            _fallbackReason = null;
        }
    }
}
