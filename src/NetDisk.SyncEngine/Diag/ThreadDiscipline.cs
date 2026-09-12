// 线程纪律探测器(DE-D-21)。
//
// 验收原文:"Dispatcher 违规检测为 0;同步引擎与 UI 线程严格分离"。
//
// 这条验收有一个**很容易被做假**的地方:如果探测器从来不响,那"违规检测为 0"
// 只说明"没有探测器",不说明"没有违规"。所以这里有三条自我防卫:
//
//	① **必须标记 UI 线程**(`MarkUiThread`)。没有标记时 `AssertOffUiThread`
//	   记一条"未启用"违规 —— **不能判定 ≠ 通过**。整个工程里只有 App 能知道
//	   谁是 UI 线程,所以标记点只可能在 App(这一点由 depsguard 的静态规则钉住)。
//	② **违规计数只增不减**,且**只记录不抛出**。探测器跑在同步引擎的后台线程上,
//	   抛异常会把同步循环打死 —— 那是"为了报告问题而制造更大问题"。
//	   代价是不可中断:所以计数必须能被**排空一次**(drain)以免重复上报,
//	   且 `SoakAnalyzer` 会把"计数变小"本身当成违规(否则重置计数即可掩盖违规)。
//	③ 这个类型**不依赖 WPF**(不引 `System.Windows.Threading`)。它只接受一个
//	   `Func<bool>`/线程 id,由 App 注入。原因:探测点大量位于 SyncEngine,
//	   而 SyncEngine 一旦 `using System.Windows.Threading` 就把内核绑在了 WPF 上
//	   (DE-D-01 的纪律),从此无法单测。

using System.Runtime.CompilerServices;

namespace NetDisk.SyncEngine.Diag;

/// <summary>一条线程纪律违规。</summary>
/// <param name="Operation">发生违规的操作名(定位用)。</param>
/// <param name="ThreadId">实际线程 id。</param>
/// <param name="UiThreadId">被标记的 UI 线程 id(0 = 从未标记)。</param>
/// <param name="Detail">人读的说明。</param>
public sealed record ThreadViolation(string Operation, int ThreadId, int UiThreadId, string Detail);

/// <summary>UI 线程纪律探测器(静态,进程内唯一)。</summary>
public static class ThreadDiscipline
{
    private static readonly object Gate = new();
    private static readonly List<ThreadViolation> Violations = new();
    private static int _uiThreadId; // 0 = 尚未标记
    private static int _checks;

    /// <summary>标记当前线程为 UI 线程。只有 App 的组合根该调用它。</summary>
    public static void MarkUiThread()
    {
        Volatile.Write(ref _uiThreadId, Environment.CurrentManagedThreadId);
    }

    /// <summary>是否已标记 UI 线程。未标记时一切判定都是"不可知"。</summary>
    public static bool IsArmed => Volatile.Read(ref _uiThreadId) != 0;

    /// <summary>被标记的 UI 线程 id(0 = 未标记)。</summary>
    public static int UiThreadId => Volatile.Read(ref _uiThreadId);

    /// <summary>已完成的判定次数(用于证明探测器真的在跑)。</summary>
    public static int CheckedCount => Volatile.Read(ref _checks);

    /// <summary>违规条数(只增,除非 <see cref="DrainViolations"/>)。</summary>
    public static int ViolationCount
    {
        get { lock (Gate) { return Violations.Count; } }
    }

    /// <summary>当前线程是否是 UI 线程(未标记时为 false)。</summary>
    public static bool IsUiThread =>
        IsArmed && Environment.CurrentManagedThreadId == UiThreadId;

    /// <summary>
    /// 断言**不在** UI 线程上(同步引擎/传输/IO 的调用点用这个)。
    ///
    /// 在 UI 线程上跑同步逻辑的后果不是"报错",而是**界面卡死**:大文件哈希/网络
    /// 重试/冲突裁决一旦落在 UI 线程,窗口就不再响应重绘与输入,用户看到"未响应"。
    /// 所以这类调用点必须被逐一声明,而不是靠约定。
    /// </summary>
    public static void AssertOffUiThread([CallerMemberName] string operation = "")
    {
        Interlocked.Increment(ref _checks);
        if (!IsArmed)
        {
            Record(operation, "探测器未启用:未调用 ThreadDiscipline.MarkUiThread,线程纪律无法判定(不能判定不等于通过)");
            return;
        }
        if (Environment.CurrentManagedThreadId == UiThreadId)
        {
            Record(operation, $"在 UI 线程(#{UiThreadId})上执行同步引擎操作 —— 会让界面卡死(未响应)");
        }
    }

    /// <summary>
    /// 断言**在** UI 线程上(碰 UI 对象之前用这个)。
    ///
    /// 反向的纪律:UI 对象(托盘/窗口/控件)只能被 UI 线程碰。跨线程触碰的表现是
    /// 随机的 —— 有时只是气泡不显示,有时是句柄泄漏或进程崩,所以必须显式声明。
    /// </summary>
    public static void AssertOnUiThread([CallerMemberName] string operation = "")
    {
        Interlocked.Increment(ref _checks);
        if (!IsArmed)
        {
            Record(operation, "探测器未启用:未调用 ThreadDiscipline.MarkUiThread,线程纪律无法判定(不能判定不等于通过)");
            return;
        }
        if (Environment.CurrentManagedThreadId != UiThreadId)
        {
            Record(operation, $"在后台线程(#{Environment.CurrentManagedThreadId})上触碰 UI 对象 —— UI 对象只能由 UI 线程(#{UiThreadId})操作,必须先 marshal 回 UI 线程");
        }
    }

    /// <summary>取走(并清空)违规列表 —— 上报一次即可,不要重复上报。</summary>
    public static IReadOnlyList<ThreadViolation> DrainViolations()
    {
        lock (Gate)
        {
            if (Violations.Count == 0)
            {
                return Array.Empty<ThreadViolation>();
            }
            var copy = Violations.ToArray();
            Violations.Clear();
            return copy;
        }
    }

    /// <summary>只读快照(不清空)。</summary>
    public static IReadOnlyList<ThreadViolation> Snapshot()
    {
        lock (Gate)
        {
            return Violations.ToArray();
        }
    }

    /// <summary>清空计数与标记(仅供检查器复位进程内状态)。</summary>
    public static void ResetForTests()
    {
        lock (Gate)
        {
            Violations.Clear();
        }
        Volatile.Write(ref _uiThreadId, 0);
        Volatile.Write(ref _checks, 0);
    }

    private static void Record(string operation, string detail)
    {
        lock (Gate)
        {
            // 上限保护:违规一旦成规模(例如把同步循环整个搬到 UI 线程),
            // 无上限记录会自己变成内存泄漏 —— 那就成了"为了发现问题而制造问题"。
            if (Violations.Count >= 512)
            {
                return;
            }
            Violations.Add(new ThreadViolation(
                string.IsNullOrEmpty(operation) ? "(未命名)" : operation,
                Environment.CurrentManagedThreadId,
                Volatile.Read(ref _uiThreadId),
                detail));
        }
    }
}
