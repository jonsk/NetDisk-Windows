// 扫描节拍(自适应卫生扫)+ 只 stat 不读内容的目录扫描(DE-D-08 ②③④)。
//
// **自适应节拍**(7.2 / 10.3):
//   - 有活动时 2min:用户刚在改文件,本地要尽快与远端对齐;
//   - 一直空闲时逐步退避到 30~60min:卫生扫的目的是"兜住监听器漏掉的变更",
//     而空闲机器上每 2 分钟扫一遍全树纯属浪费电与磁盘;
//   - **上限必须在 30~60min 之内**:再长就失去了"兜底"的意义 ——
//     真丢了事件时,用户要等到下一个卫生扫才发现"这个文件一直没同步"。
//
// **只 stat 不读内容**:扫描只取名字/大小/最后写入时间/是否目录。内容哈希是"需要时才算"
// 的东西(冲突判定、秒传预检),卫生扫绝不该顺手把用户磁盘全读一遍。
// 这条纪律由机械规则盯着(见桌面端 WatcherCheck),因为"顺手多加一个 File.ReadAllBytes"
// 在代码审查里极难被发现。
//
// **扫描线程用 VeryLow IO**:Windows 的 THREAD_MODE_BACKGROUND_BEGIN 会把线程的
// CPU 与**磁盘 IO** 优先级一起降到后台档 —— 用户在前台用电脑时,扫描不该抢占 IO。
// 这也是为什么扫描要放在**自己的线程**上:这个模式是**线程级**的,设在主线程上会把
// UI 的 IO 也一起拖慢。

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NetDisk.SyncEngine.Watch;

/// <summary>扫描到的一条元数据(只有 stat 信息,没有内容)。</summary>
public sealed record ScannedEntry(
    string Path,
    bool IsDirectory,
    long Size,
    DateTimeOffset LastWriteUtc,
    FileAttributes Attributes);

/// <summary>目录扫描器(只 stat)。</summary>
public sealed class DirectoryScanner
{
    /// <summary>一次扫描最多返回多少条(防"用户把整个 D 盘拖进来"时把内存吃满)。</summary>
    public int MaxEntries { get; init; } = 200_000;

    /// <summary>扫描一棵子树(或单个目录)的元数据。</summary>
    public IReadOnlyList<ScannedEntry> Scan(string path, bool recursive)
    {
        var list = new List<ScannedEntry>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            // 不跟随符号链接/联接:跟随会让扫描跑出同步根,甚至成环
            AttributesToSkip = FileAttributes.ReparsePoint,
            // 权限不足的目录跳过而不是抛异常(用户目录里总有系统目录)
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
        };

        // 走 \\?\ 前缀(DE-D-16):单名可到 240 字节,叠加目录层级很容易超 MAX_PATH ——
        // 不加前缀的表现是「文件明明在服务端、本地却看不到」,而且报错毫无提示性。
        foreach (var info in new DirectoryInfo(Paths.LongPath.ToExtended(path)).EnumerateFileSystemInfos("*", options))
        {
            if (list.Count >= MaxEntries)
            {
                break;
            }
            // 只读元数据:Length / LastWriteTimeUtc / Attributes 都来自一次 stat,
            // **不打开文件**。这里若出现 FileStream/ReadAllBytes,机械规则会拦住。
            list.Add(new ScannedEntry(
                // 状态库/UI 存的是**可读路径**(不带 \\?\);要调文件 API 时再加前缀
                Paths.LongPath.ForDisplay(info.FullName),
                (info.Attributes & FileAttributes.Directory) != 0,
                info is FileInfo f ? f.Length : 0,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                info.Attributes));
        }
        return list;
    }
}

/// <summary>把当前线程切到 Windows 的后台(VeryLow)IO/CPU 档。</summary>
public static class IoPriority
{
    private const int ThreadModeBackgroundBegin = 0x00010000;
    private const int ThreadModeBackgroundEnd = 0x00020000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetThreadPriority(IntPtr hThread, int nPriority);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    /// <summary>进入后台档(CPU + IO 一起降)。返回是否成功(非 Windows 上为 false)。</summary>
    public static bool EnterBackground()
    {
        try
        {
            return SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundBegin);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>退出后台档(恢复前台优先级)。**必须**成对调用:线程会被复用。</summary>
    public static bool ExitBackground()
    {
        try
        {
            return SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundEnd);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>在后台档里跑一段只读扫描(成对进入/退出,异常也保证复位)。</summary>
    public static T RunInBackground<T>(Func<T> body)
    {
        var entered = EnterBackground();
        try
        {
            return body();
        }
        finally
        {
            if (entered)
            {
                ExitBackground();
            }
        }
    }
}

/// <summary>
/// 自适应卫生扫的节拍器(纯计算,好测)。
///
/// 状态机很短:连续空闲次数每加一,间隔就翻倍,直到封顶 <see cref="IdleInterval"/>;
/// 一旦发生活动,立刻回到 <see cref="BusyInterval"/>。
/// </summary>
public sealed class ScanScheduler
{
    /// <summary>忙时/有活动时的间隔(7.2:2min)。</summary>
    public static readonly TimeSpan BusyInterval = TimeSpan.FromMinutes(2);

    /// <summary>完全空闲时的间隔。默认 45min(验收要求落在 30~60min 区间内)。</summary>
    public static readonly TimeSpan IdleInterval = TimeSpan.FromMinutes(45);

    /// <summary>验收下限/上限(30~60min):越界就是"兜底失效"或"白耗电"。</summary>
    public static readonly TimeSpan MinIdleAllowed = TimeSpan.FromMinutes(30);

    public static readonly TimeSpan MaxIdleAllowed = TimeSpan.FromMinutes(60);

    private TimeSpan _current = BusyInterval;

    /// <summary>当前应当等待的间隔。</summary>
    public TimeSpan CurrentInterval => _current;

    /// <summary>发生了一次需要处理的活动(事件/重扫请求)。</summary>
    public void NoteActivity() => _current = BusyInterval;

    /// <summary>一轮扫描结束,期间没有任何变化 → 间隔翻倍并封顶。</summary>
    public void NoteIdleRound()
    {
        var next = _current + _current;
        if (next > IdleInterval)
        {
            next = IdleInterval;
        }
        if (next < BusyInterval)
        {
            next = BusyInterval;
        }
        _current = next;
    }

    /// <summary>校验常量落在验收区间里(启动自检用;避免有人把 45min 改成 6h)。</summary>
    public static bool ConfigurationIsSane()
        => IdleInterval >= MinIdleAllowed && IdleInterval <= MaxIdleAllowed && BusyInterval == TimeSpan.FromMinutes(2);
}
