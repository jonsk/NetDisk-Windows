// 自触发销账(DE-D-10)。
//
// 要防的回环长这样:同步引擎**自己**把远端文件写到本地 → FileSystemWatcher 立刻报
// "这个路径变了" → 若不做销账,引擎会把**自己的落盘**当成"用户在本地改了文件" → 上传回
// 远端 → 另一个客户端再下载 → …… 一个文件在两端之间来回翻倍地传,而每一跳看起来都
// "符合逻辑"。
//
// 做法:落盘**之前**先在账本上记一笔"我准备把 X 写成 size/mtime",事件到达时先问账本:
// 是我自己的写 → 销账吞掉;不是我的 → 交给状态机(DE-D-09)。
//
// 这里最容易写错的**不是**"怎么匹配",而是**匹配不上的时候怎么办**:
//   - 账本条目**必须有过期时间**。若一条记录因为事件没到达而永远留着,那么用户之后
//     对同一个文件的**真实修改**会被它吞掉 —— 表现为"我改了文件但一直没同步",
//     而且完全没有报错。所以 TTL 到点即失效,宁可多报一次(重复上传是幂等的),
//     也绝不吞用户的修改。
//   - 大小不匹配时**不算自己的**(事件到达前用户又改了一次):这种情况必须报给状态机,
//     否则用户那次修改就丢了。同时把这条记录清掉,免得它继续吞后续事件。
//
// 匹配键是 (路径 + 大小 + mtime 容差)。大小是强判据(自己的写入大小是确定的),mtime 用
// 容差是因为文件系统的写入时间粒度(有些卷上只有 1s 甚至 2s)会让精确比较误判。

namespace NetDisk.SyncEngine.Sync;

/// <summary>账本里的一条"我要写的"记录。</summary>
public sealed record ExpectedChange(
    string Path,
    long ExpectedSize,
    long ExpectedMtimeTicks,
    DateTimeOffset RecordedAt);

/// <summary>事件到达时的裁定。</summary>
public enum ExpectedChangeVerdict
{
    /// <summary>不是我们自己的写 → 交给状态机(可能是用户的真实修改)。</summary>
    NotOurs,

    /// <summary>是我们自己的写 → 销账吞掉,不产生任何同步动作。</summary>
    Consumed,
}

/// <summary>
/// 持久化接缝:账本跨进程重启也要有效。
///
/// 为什么需要持久化:进程在"记录意图"和"事件到达"之间崩溃时,重启后 watcher 仍可能补发
/// 那次事件;没有持久化的话它会变成一次**凭空的多余上传**(不丢数据,但会让用户看到
/// "我什么都没改,它却在传")。当前实现先用内存账本 + 这个接缝(见清单里 DE-D-10 的说明),
/// 落库实现随 DE-D-14 的传输队列一起接上(它同时需要 `expected_changes` 表的列扩展)。
/// </summary>
public interface IExpectedChangeSink
{
    void Save(ExpectedChange change);

    void Remove(string path);

    IReadOnlyList<ExpectedChange> Load();
}

/// <summary>内存账本(默认),sink 为 null 时只存活于本进程。</summary>
public sealed class ExpectedChangeLedger
{
    /// <summary>
    /// 条目默认存活 10s。取值理由:自己的落盘事件通常毫秒级到达;10s 足够覆盖
    /// "大文件写完 + 杀毒软件扫一遍"这类延迟,又不至于让一条**没等到事件**的记录
    /// 长时间威胁用户的下一次真实修改。宁可多报一次(重复上传幂等),绝不吞用户修改。
    /// </summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(10);

    /// <summary>mtime 容差:卷的写入时间粒度可能到 1~2s,精确比较会误判成"不是我的"。</summary>
    public static readonly TimeSpan MtimeTolerance = TimeSpan.FromSeconds(2);

    private readonly Dictionary<string, ExpectedChange> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _order = new(); // 粗粒度 FIFO,用于封顶淘汰
    private readonly TimeSpan _ttl;
    private readonly int _maxEntries;
    private readonly TimeProvider _clock;
    private readonly IExpectedChangeSink? _sink;

    public ExpectedChangeLedger(
        TimeSpan? ttl = null,
        int maxEntries = 4096,
        TimeProvider? clock = null,
        IExpectedChangeSink? sink = null)
    {
        _ttl = ttl ?? DefaultTtl;
        _maxEntries = maxEntries > 0 ? maxEntries : 4096;
        _clock = clock ?? TimeProvider.System;
        _sink = sink;
        if (_sink is not null)
        {
            foreach (var c in _sink.Load())
            {
                Add(c);
            }
        }
    }

    /// <summary>待销账条数。</summary>
    public int PendingCount => _pending.Count;

    /// <summary>已销账(吞掉的自己写的事件)次数。</summary>
    public int ConsumedCount { get; private set; }

    /// <summary>登记一次"我即将把 path 写成这个大小/时间"。</summary>
    public void Record(string path, long size, long mtimeTicks)
    {
        var change = new ExpectedChange(path, size, mtimeTicks, _clock.GetUtcNow());
        Add(change);
        _sink?.Save(change);
    }

    /// <summary>事件到达:问账本"这是我自己的写吗"。</summary>
    public ExpectedChangeVerdict TryConsume(string path, long actualSize, long actualMtimeTicks)
    {
        EvictExpired();

        if (!_pending.TryGetValue(path, out var expected))
        {
            // 没有记录(或已过期)→ 用户的真实修改
            return ExpectedChangeVerdict.NotOurs;
        }

        // 大小不一致 = 在我们写入之后**又有人改过**这个文件 → 必须报给状态机。
        // (若在这里吞掉,用户那次修改就永远丢了。)
        if (expected.ExpectedSize != actualSize)
        {
            Remove(path);
            return ExpectedChangeVerdict.NotOurs;
        }

        // mtime 用容差比较:见 MtimeTolerance 的说明
        var delta = Math.Abs(actualMtimeTicks - expected.ExpectedMtimeTicks);
        if (delta > MtimeTolerance.Ticks)
        {
            Remove(path);
            return ExpectedChangeVerdict.NotOurs;
        }

        Remove(path);
        ConsumedCount++;
        return ExpectedChangeVerdict.Consumed;
    }

    /// <summary>清理过期条目(TTL 到点即失效 —— 绝不能长期吞掉用户的下一次修改)。</summary>
    public void EvictExpired()
    {
        var now = _clock.GetUtcNow();
        foreach (var (path, change) in _pending.ToArray())
        {
            if (now - change.RecordedAt > _ttl)
            {
                Remove(path);
            }
        }
    }

    private void Add(ExpectedChange change)
    {
        if (_pending.ContainsKey(change.Path))
        {
            _order.Remove(change.Path);
        }
        _pending[change.Path] = change;
        _order.AddLast(change.Path);

        // 封顶:账本不该无限增长(用户可能连续写几万个文件)
        while (_order.Count > _maxEntries && _order.First is { } first)
        {
            Remove(first.Value);
        }
    }

    private void Remove(string path)
    {
        if (_pending.Remove(path))
        {
            _order.Remove(path);
            _sink?.Remove(path);
        }
    }
}
