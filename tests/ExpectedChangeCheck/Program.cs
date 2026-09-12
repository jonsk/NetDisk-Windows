// DE-D-10 行为检查器:自触发销账(期望变更表)。
//
// 用法:`dotnet run --project desktop/tests/ExpectedChangeCheck -c Release`
//
// 这里最要紧的是**反向的那一面**:账本除了"不产生回环",还必须"不吞用户修改"。
// 一个"什么都吞"的实现同样能让 1000 次自写用例全绿 —— 代价是用户改文件永远同步不上去,
// 而且没有任何报错。所以两类断言必须同时存在。

using NetDisk.SyncEngine.Sync;

var checks = new List<(string Name, Func<Task> Run)>
{
    ("① 连续 1000 次自写:零远端误判(无回环)", CheckNoEchoForThousandWritesAsync),
    ("② 没有登记的事件 → 报给状态机(用户修改)", CheckUnknownPathIsUserChangeAsync),
    ("③ 过期条目不得吞掉后续的用户修改(关键)", CheckExpiredEntryDoesNotSwallowAsync),
    ("④ 大小不同 → 不算自己的(写入后又被改过)", CheckSizeMismatchIsUserChangeAsync),
    ("⑤ mtime 容差:粒度导致的秒级差异仍算自己的", CheckMtimeToleranceAsync),
    ("⑥ 销账后账本清空(不残留条目)", CheckConsumedRemovesEntryAsync),
    ("⑦ 账本有上限(连续登记不会无限增长)", CheckBoundedAsync),
    ("⑧ 持久化接缝被调用(Save/Remove/Load)", CheckSinkWiredAsync),
    ("⑨ 连续 1000 轮「自写 + 真实修改交替」都不误判", CheckInterleavedAsync),
};

var failed = 0;
foreach (var (name, run) in checks)
{
    try
    {
        await run();
        Console.WriteLine($"✓ {name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"✗ {name}: {ex.Message}");
    }
}

Console.WriteLine();
if (failed == 0)
{
    Console.WriteLine($"全部通过:{checks.Count} 项 DE-D-10 行为断言(自触发销账)");
    return 0;
}
Console.Error.WriteLine($"{failed}/{checks.Count} 项失败");
return 1;

// ---------------------------------------------------------------- 检查

static Task CheckNoEchoForThousandWritesAsync()
{
    var clock = new FakeClock();
    var ledger = new ExpectedChangeLedger(clock: clock);
    var userChanges = 0;

    for (var i = 0; i < 1000; i++)
    {
        // 引擎自己的落盘:写入前登记,写入后事件到达
        var path = $@"C:\sync\f{i % 37}.dat"; // 故意复用路径,模拟反复写同几个文件
        var size = 1024 + i;
        var mtime = clock.GetUtcNow().UtcTicks;
        ledger.Record(path, size, mtime);
        if (ledger.TryConsume(path, size, mtime) == ExpectedChangeVerdict.NotOurs)
        {
            userChanges++;
        }
        clock.Advance(TimeSpan.FromMilliseconds(5));
    }

    Assert(userChanges == 0, $"自己的 1000 次落盘不该产生任何一次「用户修改」误判,实际 {userChanges}");
    Assert(ledger.ConsumedCount == 1000, $"应销账 1000 次,实际 {ledger.ConsumedCount}");
    Assert(ledger.PendingCount == 0, $"销账后不该残留条目,实际 {ledger.PendingCount}");
    return Task.CompletedTask;
}

static Task CheckUnknownPathIsUserChangeAsync()
{
    var ledger = new ExpectedChangeLedger();
    Assert(ledger.TryConsume(@"C:\sync\user.txt", 10, 100) == ExpectedChangeVerdict.NotOurs,
        "没有登记过的路径 = 用户在本地新建/修改 → 必须报给状态机");
    return Task.CompletedTask;
}

static Task CheckExpiredEntryDoesNotSwallowAsync()
{
    // 这是本项最关键的一条:登记了但事件一直没来(比如进程在写盘前被打断),
    // 之后用户真的修改了这个文件 —— 账本**不能**把它当成自己的写吞掉。
    var clock = new FakeClock();
    var ledger = new ExpectedChangeLedger(ttl: TimeSpan.FromSeconds(10), clock: clock);

    var ticks = clock.GetUtcNow().UtcTicks;
    ledger.Record(@"C:\sync\a.txt", size: 500, mtimeTicks: ticks);
    clock.Advance(TimeSpan.FromSeconds(11)); // TTL 过期

    // 关键构造:用户的这次修改**大小相同、mtime 也相同**(备份还原、工具复制、
    // 带时间戳的同步都可能保时间戳)。此时"大小 + mtime"两条判据全都会说"是我自己的写",
    // **只有 TTL 能救这一条** —— 所以本用例是 TTL 的专属证据(第一版用例传了"当前时间",
    // 结果被 mtime 容差挡住了,禁用 TTL 也照样绿:那是用例在骗自己)。
    var verdict = ledger.TryConsume(@"C:\sync\a.txt", 500, ticks);
    Assert(verdict == ExpectedChangeVerdict.NotOurs,
        "过期条目必须失效:否则用户之后的真实修改会被「自己人」吞掉(改了却一直不同步,且无报错)");
    Assert(ledger.PendingCount == 0, "过期的条目应被清理");
    return Task.CompletedTask;
}

static Task CheckSizeMismatchIsUserChangeAsync()
{
    var clock = new FakeClock();
    var ledger = new ExpectedChangeLedger(clock: clock);
    ledger.Record(@"C:\sync\a.txt", size: 500, mtimeTicks: clock.GetUtcNow().UtcTicks);

    // 我们写完 500 字节之后,用户又把它改成了 700 字节(事件合并成一条到达)
    var verdict = ledger.TryConsume(@"C:\sync\a.txt", 700, clock.GetUtcNow().UtcTicks);
    Assert(verdict == ExpectedChangeVerdict.NotOurs,
        "大小不同说明写入之后**又有人改过** → 必须报给状态机(否则用户那次修改就丢了)");
    Assert(ledger.PendingCount == 0, "不匹配的条目应被清掉(免得继续吞后续事件)");
    return Task.CompletedTask;
}

static Task CheckMtimeToleranceAsync()
{
    var clock = new FakeClock();
    var ledger = new ExpectedChangeLedger(clock: clock);
    var baseTicks = clock.GetUtcNow().UtcTicks;
    ledger.Record(@"C:\sync\a.txt", size: 100, mtimeTicks: baseTicks);

    // 文件系统把写入时间记成了 1s 之后(卷的时间粒度)→ 仍应认作自己的写
    var verdict = ledger.TryConsume(@"C:\sync\a.txt", 100, baseTicks + TimeSpan.FromSeconds(1).Ticks);
    Assert(verdict == ExpectedChangeVerdict.Consumed,
        "容差内的 mtime 差异应仍算自己的写(精确比较会把「自己的写」误判成用户修改 → 多余上传)");

    // 超出容差 → 不是自己的
    ledger.Record(@"C:\sync\b.txt", size: 100, mtimeTicks: baseTicks);
    var far = ledger.TryConsume(@"C:\sync\b.txt", 100, baseTicks + TimeSpan.FromMinutes(5).Ticks);
    Assert(far == ExpectedChangeVerdict.NotOurs, "超出容差的 mtime 差异应视为用户修改");
    return Task.CompletedTask;
}

static Task CheckConsumedRemovesEntryAsync()
{
    var clock = new FakeClock();
    var ledger = new ExpectedChangeLedger(clock: clock);
    ledger.Record(@"C:\sync\a.txt", 1, clock.GetUtcNow().UtcTicks);
    Assert(ledger.PendingCount == 1, "登记后应有 1 条待销账");

    Assert(ledger.TryConsume(@"C:\sync\a.txt", 1, clock.GetUtcNow().UtcTicks) == ExpectedChangeVerdict.Consumed,
        "应销账");
    Assert(ledger.PendingCount == 0, "销账后条目必须移除(否则下一轮会继续吞事件)");

    // 同一路径的第二次事件(没有重新登记)→ 用户修改
    Assert(ledger.TryConsume(@"C:\sync\a.txt", 1, clock.GetUtcNow().UtcTicks) == ExpectedChangeVerdict.NotOurs,
        "销账只能抵消**一次**自己人事件");
    return Task.CompletedTask;
}

static Task CheckBoundedAsync()
{
    var clock = new FakeClock();
    var ledger = new ExpectedChangeLedger(maxEntries: 64, clock: clock);
    for (var i = 0; i < 500; i++)
    {
        ledger.Record($@"C:\sync\x{i}.dat", 1, clock.GetUtcNow().UtcTicks);
    }
    Assert(ledger.PendingCount <= 64, $"账本必须有上限(否则连续写几万文件会吃满内存),实际 {ledger.PendingCount}");
    // 被淘汰的老条目不该再吞事件
    Assert(ledger.TryConsume(@"C:\sync\x0.dat", 1, clock.GetUtcNow().UtcTicks) == ExpectedChangeVerdict.NotOurs,
        "被淘汰的条目应当已经失效(按 FIFO 淘汰后报为用户修改是安全的)");
    return Task.CompletedTask;
}

static Task CheckSinkWiredAsync()
{
    var clock = new FakeClock();
    var sink = new FakeSink();
    var ledger = new ExpectedChangeLedger(clock: clock, sink: sink);

    ledger.Record(@"C:\sync\a.txt", 10, clock.GetUtcNow().UtcTicks);
    Assert(sink.Saves == 1, $"登记应写穿到持久化接缝,实际 {sink.Saves} 次");
    ledger.TryConsume(@"C:\sync\a.txt", 10, clock.GetUtcNow().UtcTicks);
    Assert(sink.Removes == 1, $"销账应通知持久化层删除,实际 {sink.Removes} 次");

    // 预置记录在构造时被加载(模拟进程重启后 watcher 补发事件)
    var sink2 = new FakeSink();
    sink2.Preload.Add(new ExpectedChange(@"C:\sync\old.txt", 7, clock.GetUtcNow().UtcTicks, clock.GetUtcNow()));
    var ledger2 = new ExpectedChangeLedger(clock: clock, sink: sink2);
    Assert(ledger2.PendingCount == 1, "重启后应把账本读回来");
    Assert(ledger2.TryConsume(@"C:\sync\old.txt", 7, clock.GetUtcNow().UtcTicks) == ExpectedChangeVerdict.Consumed,
        "重启补发的事件应被正确销账(否则会凭空多传一次)");
    return Task.CompletedTask;
}

static Task CheckInterleavedAsync()
{
    // 自写与用户修改交替 1000 轮:两类事件都必须被正确归类。
    var clock = new FakeClock();
    var ledger = new ExpectedChangeLedger(clock: clock);
    var userChanges = 0;
    var echoes = 0;

    for (var i = 0; i < 1000; i++)
    {
        var selfPath = $@"C:\sync\self{i}.dat";
        var userPath = $@"C:\sync\user{i}.dat";
        var ticks = clock.GetUtcNow().UtcTicks;

        ledger.Record(selfPath, 100, ticks);
        if (ledger.TryConsume(selfPath, 100, ticks) == ExpectedChangeVerdict.Consumed) { echoes++; }

        if (ledger.TryConsume(userPath, 100, ticks) == ExpectedChangeVerdict.NotOurs) { userChanges++; }
        clock.Advance(TimeSpan.FromMilliseconds(1));
    }

    Assert(echoes == 1000, $"1000 次自写都应销账,实际 {echoes}");
    Assert(userChanges == 1000, $"1000 次用户修改都应上报,实际 {userChanges}");
    return Task.CompletedTask;
}

// ---------------------------------------------------------------- 工具

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new Exception(message);
    }
}

/// <summary>可控时钟:TTL 与 mtime 容差都要能被确定性地测。</summary>
sealed class FakeClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 9, 12, 10, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now = _now.Add(delta);
}

sealed class FakeSink : IExpectedChangeSink
{
    public List<ExpectedChange> Preload { get; } = new();

    public int Saves { get; private set; }

    public int Removes { get; private set; }

    public void Save(ExpectedChange change) => Saves++;

    public void Remove(string path) => Removes++;

    public IReadOnlyList<ExpectedChange> Load() => Preload;
}
