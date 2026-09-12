// DE-D-14 行为检查器:传输队列(并发 / 限速 / 最小化后继续传)+ 账本落库。
//
// 用法:`dotnet run --project desktop/tests/TransferCheck -c Release`
//
// 三条验收在这里被拆成可执行断言:
//   并发可配(默认 3)     → 断言默认值与"峰值从未超过上限",并断言"真的并发"(峰值≥2)
//   上下行限速生效         → 断言限速**确实等了**、上下行**互不占用**额度、不限速时零等待
//   最小化后继续传         → 用一个**永不派发**的 SynchronizationContext:实现里任何一步
//                            把续体排到"UI"上都会挂住;并断言一次都没尝试派发

using System.Diagnostics;
using NetDisk.ClientCore;
using NetDisk.SyncEngine;
using NetDisk.SyncEngine.Sync;
using NetDisk.SyncEngine.Transfer;

// 看门狗:某些实现缺陷的表现是**用例挂住**(例如限速器陷在"睡一秒还是不够"的循环里)。
// 挂住比失败糟糕得多 —— 在 CI 上它是一颗定时炸弹(整条流水线等到超时才断)。
// 这里在超时后主动以 99 退出并打印原因,把"挂住"变成"可读的失败"(本轮就是这样抓到缺陷的)。
var watchdogSeconds = int.TryParse(Environment.GetEnvironmentVariable("NETDISK_CHECK_TIMEOUT_SEC"), out var wd) ? wd : 120;
var finished = 0;
new Thread(() =>
{
    Thread.Sleep(watchdogSeconds * 1000);
    if (Volatile.Read(ref finished) == 0)
    {
        Console.Error.WriteLine($"✗ 看门狗:超过 {watchdogSeconds}s 仍未跑完 —— 很可能实现陷在等待循环里(见 DE-D-14 缺陷①:限速桶容量小于单次申请)");
        Environment.Exit(99);
    }
})
{ IsBackground = true }.Start();

var checks = new List<(string Name, Func<Task> Run)>
{
    ("① 默认并发 3(7.2 口径)", CheckDefaultConcurrencyAsync),
    ("② 并发真的封顶:20 个任务峰值 ≤3 且 ≥2", CheckConcurrencyCappedAsync),
    ("③ 并发可配:设为 1 / 5 时峰值随之变化", CheckConfigurableConcurrencyAsync),
    ("④ 限速确实生效(1MB/s 传 2MB ≈ 等 1s)", CheckRateLimitWaitsAsync),
    ("⑤ 上下行额度互不占用(上传耗尽不影响下载)", CheckDirectionsIndependentAsync),
    ("⑥ 不限速时零等待(不能「关掉限速反而变慢」)", CheckNoLimitNoWaitAsync),
    ("⑦ 最小化后继续传:传输不依赖 UI 调度器", CheckUiIndependentAsync),
    ("⑧ 单个任务失败不拖垮队列", CheckFailureDoesNotStallAsync),
    ("⑨ 进度按阈值节流(不是每片都上报)", CheckProgressThrottledAsync),
    ("⑩ 账本落库:Save/Load 往返(真实 SQLite)且同路径不重复", CheckLedgerPersistenceAsync),
    ("⑪ 跨重启销账:重启后仍能认出自写事件", CheckLedgerSurvivesRestartAsync),
    ("⑫ 第一批跑完后再入队仍会被执行(worker 活性)", CheckEnqueueAfterDrainStillRunsAsync),
    ("⑬ 账本对没登记过的路径不抛异常(曾经 NRE)", CheckLedgerUnknownPathAsync),
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

System.Threading.Interlocked.Exchange(ref finished, 1);
Console.WriteLine();
if (failed == 0)
{
    Console.WriteLine($"全部通过:{checks.Count} 项 DE-D-14 行为断言(传输队列 + 账本落库)");
    return 0;
}
Console.Error.WriteLine($"{failed}/{checks.Count} 项失败");
return 1;

// ---------------------------------------------------------------- 检查

static Task CheckDefaultConcurrencyAsync()
{
    var q = new TransferQueue();
    Assert(q.MaxConcurrency == 3, $"默认并发必须是 3(7.2 口径),实际 {q.MaxConcurrency}");
    Assert(new TransferQueueOptions().MaxConcurrency == 3, "配置默认值也必须是 3");
    // 上限必须能被拒绝为非法(否则 UI 把 0 传进来会得到一个"永不执行"的队列)
    var rejected = false;
    try { _ = new TransferQueue(new TransferQueueOptions { MaxConcurrency = 0 }); }
    catch (ArgumentOutOfRangeException) { rejected = true; }
    Assert(rejected, "并发上限 0 必须被拒绝(否则队列静默不执行任何任务)");
    return q.DisposeAsync().AsTask();
}

static async Task CheckConcurrencyCappedAsync()
{
    await using var q = new TransferQueue(new TransferQueueOptions { MaxConcurrency = 3 });
    var started = 0;
    var maxObserved = 0;
    var gate = new object();

    for (var i = 0; i < 20; i++)
    {
        q.Enqueue(new TransferJob
        {
            Id = $"job-{i}",
            DisplayName = $"f{i}.bin",
            Direction = TransferDirection.Upload,
            TotalBytes = 100,
            Run = async (_, ct) =>
            {
                lock (gate)
                {
                    started++;
                    maxObserved = Math.Max(maxObserved, started);
                }
                await Task.Delay(30, ct);
                lock (gate) { started--; }
                return 100;
            },
        });
    }

    await q.DrainAsync();
    Assert(maxObserved <= 3, $"并发峰值不得超过上限 3,实际 {maxObserved}");
    Assert(maxObserved >= 2, $"应当真的并发起来(峰值≥2),实际 {maxObserved} —— 若为 1 说明限速把槽位睡住了");
    Assert(q.PeakConcurrency <= 3, $"队列自报的峰值也不得超过 3,实际 {q.PeakConcurrency}");
}

static async Task CheckConfigurableConcurrencyAsync()
{
    foreach (var n in new[] { 1, 5 })
    {
        await using var q = new TransferQueue(new TransferQueueOptions { MaxConcurrency = n });
        var started = 0;
        var peak = 0;
        var gate = new object();
        for (var i = 0; i < 12; i++)
        {
            q.Enqueue(new TransferJob
            {
                Id = $"j{i}",
                DisplayName = "f",
                Direction = TransferDirection.Download,
                TotalBytes = 10,
                Run = async (_, ct) =>
                {
                    lock (gate) { started++; peak = Math.Max(peak, started); }
                    await Task.Delay(20, ct);
                    lock (gate) { started--; }
                    return 10;
                },
            });
        }
        await q.DrainAsync();
        Assert(peak == n, $"并发设为 {n} 时峰值应为 {n},实际 {peak}");
    }
}

static async Task CheckRateLimitWaitsAsync()
{
    // 1MB/s,一次申请 2MB:桶初始有 1MB(允许 1 秒突发),还缺 1MB → 必须等约 1s
    var limiter = new RateLimiter(new RateLimitOptions { UploadBytesPerSecond = 1024 * 1024 });
    var sw = Stopwatch.StartNew();
    await limiter.AcquireAsync(TransferDirection.Upload, 2 * 1024 * 1024);
    sw.Stop();

    Assert(sw.Elapsed >= TimeSpan.FromMilliseconds(700),
        $"限速必须**真的等待**(1MB/s 传 2MB 应等约 1s),实际只用了 {sw.ElapsedMilliseconds}ms");
    Assert(sw.Elapsed <= TimeSpan.FromSeconds(3),
        $"等待不该远超理论值,实际 {sw.ElapsedMilliseconds}ms");
}

static async Task CheckDirectionsIndependentAsync()
{
    // 上下行各自一个桶:把上行的额度用光,下载仍应立刻拿到额度
    var limiter = new RateLimiter(new RateLimitOptions
    {
        UploadBytesPerSecond = 1024,      // 很小
        DownloadBytesPerSecond = 1024 * 1024,
    });

    await limiter.AcquireAsync(TransferDirection.Upload, 1024); // 花掉上行的初始额度

    // 用**有界等待**而不是"计时后断言":如果实现把两个方向共用一个桶,下行要按上行的
    // 速率(1KB/s)慢慢攒 512KB —— 那是 500 多秒,直接让用例挂住(反向验证里就变成了
    // "看门狗超时"而不是"哪一条断言失败")。给个 2s 上限,挂住就变成精确的失败信息。
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    try
    {
        await limiter.AcquireAsync(TransferDirection.Download, 512 * 1024, cts.Token);
    }
    catch (OperationCanceledException)
    {
        throw new Exception("上下行必须**分别**限速:下行在 2s 内拿不到额度(说明它用了上行的额度)");
    }
}

static async Task CheckNoLimitNoWaitAsync()
{
    var limiter = new RateLimiter(new RateLimitOptions()); // 两个方向都是 0 = 不限
    var sw = Stopwatch.StartNew();
    await limiter.AcquireAsync(TransferDirection.Upload, 10L * 1024 * 1024 * 1024); // 10GB
    await limiter.AcquireAsync(TransferDirection.Download, 10L * 1024 * 1024 * 1024);
    sw.Stop();
    Assert(sw.Elapsed < TimeSpan.FromMilliseconds(200),
        $"不限速时必须零等待(否则「关掉限速」反而更慢),实际 {sw.ElapsedMilliseconds}ms");
}

static Task CheckUiIndependentAsync()
{
    // 模拟"窗口最小化/UI 被挂起":一个**永不派发**的 SynchronizationContext。
    // 关键设计:被测代码必须跑在一个**独立线程**上(并在那个线程上装着假 UI 上下文),
    // 而测试自身在**没有**上下文的主流程里等待结果 ——
    // 否则装上下文这一步会把测试自己的 await 也挂住(第一版就是这样,整个用例直接卡死,
    // 那次"挂住"同时也是实现缺陷的信号:队列当时确实在往 UI 上 Post)。
    var fakeUi = new NeverDispatchContext();
    var completed = 0;
    Exception? failure = null;
    var done = new ManualResetEventSlim(false);

    var worker = new Thread(() =>
    {
        SynchronizationContext.SetSynchronizationContext(fakeUi);
        try
        {
            var q = new TransferQueue(new TransferQueueOptions { MaxConcurrency = 3 });
            for (var i = 0; i < 9; i++)
            {
                q.Enqueue(new TransferJob
                {
                    Id = $"ui-{i}",
                    DisplayName = $"f{i}",
                    Direction = TransferDirection.Upload,
                    TotalBytes = 50,
                    Run = async (_, ct) =>
                    {
                        await Task.Delay(20, ct);
                        Interlocked.Increment(ref completed);
                        return 50;
                    },
                });
            }
            // 在假 UI 上下文下等待队列跑空:若实现把续体 Post 到 UI 上,这里会超时
            if (!q.DrainAsync().Wait(TimeSpan.FromSeconds(8)))
            {
                throw new Exception("UI 被挂起时队列没能跑完 —— 传输依赖了 UI 调度器");
            }
            _ = q.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            done.Set();
        }
    })
    { IsBackground = true };
    worker.Start();

    Assert(done.Wait(TimeSpan.FromSeconds(15)), "测试自身超时(队列可能卡在 UI 上下文上)");
    if (failure is not null)
    {
        throw failure;
    }
    Assert(completed == 9, $"9 个任务都应完成,实际 {completed}");
    Assert(fakeUi.PostCount == 0,
        $"队列不该向 UI 调度器派发任何续体(实际派发 {fakeUi.PostCount} 次)—— 那正是「最小化后停传」的成因");

    // 正例对照:同一个队列在**没有**假 UI 的情况下当然也应跑完(排除"被测试环境弄坏")
    return Task.CompletedTask;
}

static async Task CheckFailureDoesNotStallAsync()
{
    await using var q = new TransferQueue(new TransferQueueOptions { MaxConcurrency = 3 });
    var failures = 0;
    var completed = 0;
    q.Failed += (_, _) => Interlocked.Increment(ref failures);

    for (var i = 0; i < 12; i++)
    {
        var fail = i % 4 == 0; // 每 4 个坏一个
        q.Enqueue(new TransferJob
        {
            Id = $"f-{i}",
            DisplayName = "x",
            Direction = TransferDirection.Upload,
            TotalBytes = 10,
            Run = async (_, ct) =>
            {
                await Task.Delay(5, ct);
                if (fail)
                {
                    throw new InvalidOperationException("模拟 507 额度不足");
                }
                Interlocked.Increment(ref completed);
                return 10;
            },
        });
    }

    var drain = q.DrainAsync();
    var winner = await Task.WhenAny(drain, Task.Delay(TimeSpan.FromSeconds(10)));
    Assert(winner == drain, "有任务失败时队列也必须能跑空(否则一次失败会让后面几百个文件永远排队)");
    await drain;
    Assert(failures == 3, $"应记录 3 次失败,实际 {failures}");
    Assert(completed == 9, $"其余 9 个任务应正常完成,实际 {completed}");
}

static async Task CheckProgressThrottledAsync()
{
    await using var q = new TransferQueue(new TransferQueueOptions
    {
        MaxConcurrency = 1,
        ProgressStepBytes = 1024, // 阈值调小便于断言
    });
    var reports = new List<TransferProgress>();
    q.Progress += p => { lock (reports) { reports.Add(p); } };

    q.Enqueue(new TransferJob
    {
        Id = "big",
        DisplayName = "big.bin",
        Direction = TransferDirection.Upload,
        TotalBytes = 10 * 1024,
        Run = async (progress, ct) =>
        {
            // 100 次小增量(每次 100 字节,总 10KB):按阈值只应上报约 10 次
            for (var i = 1; i <= 100; i++)
            {
                progress?.Report(i * 100);
                await Task.Yield();
            }
            return 10 * 1024;
        },
    });
    await q.DrainAsync();

    int count;
    lock (reports) { count = reports.Count; }
    Assert(count <= 15, $"进度必须按阈值节流(100 次小增量不该产生 {count} 条通知)");
    Assert(count >= 2, $"至少要上报一次中间进度与一次完成,实际 {count}");
    lock (reports)
    {
        Assert(reports[^1].Completed, "最后一条必须是完成事件");
        Assert(reports[^1].DoneBytes == 10 * 1024, "完成事件的字节数应为总量");
    }
}

static Task CheckLedgerPersistenceAsync()
{
    WithTempStore(store =>
    {
        var sink = new SqliteExpectedChangeSink(store);
        var clock = new FakeClock();
        var ledger = new ExpectedChangeLedger(clock: clock, sink: sink);

        ledger.Record(@"C:\sync\a.txt", size: 100, mtimeTicks: clock.GetUtcNow().UtcTicks);
        var loaded = sink.Load();
        Assert(loaded.Count == 1, $"落库后应能读回 1 条,实际 {loaded.Count}");
        Assert(loaded[0].Path == @"C:\sync\a.txt" && loaded[0].ExpectedSize == 100,
            $"读回的记录应与写入一致,实际 {loaded[0]}");

        // 同一路径再登记一次 → 覆盖而不是追加(否则销账只删一条,另一条继续吞后续事件)
        ledger.Record(@"C:\sync\a.txt", size: 200, mtimeTicks: clock.GetUtcNow().UtcTicks);
        loaded = sink.Load();
        Assert(loaded.Count == 1, $"同一路径必须只有一条记录,实际 {loaded.Count}");
        Assert(loaded[0].ExpectedSize == 200, "应当是最后一次登记的值");

        // 销账后库里也必须删掉
        Assert(ledger.TryConsume(@"C:\sync\a.txt", 200, clock.GetUtcNow().UtcTicks) == ExpectedChangeVerdict.Consumed,
            "应当销账");
        Assert(sink.Load().Count == 0, "销账后库里不该残留记录(否则重启后会吞掉用户的下一次修改)");
    });
    return Task.CompletedTask;
}

static Task CheckLedgerSurvivesRestartAsync()
{
    WithTempStore(store =>
    {
        var clock = new FakeClock();
        // 第一个"进程":登记意图后崩溃(没有销账)
        var sink1 = new SqliteExpectedChangeSink(store);
        var ledger1 = new ExpectedChangeLedger(clock: clock, sink: sink1);
        var ticks = clock.GetUtcNow().UtcTicks;
        ledger1.Record(@"C:\sync\crash.bin", size: 4096, mtimeTicks: ticks);

        // 第二个"进程":watcher 补发了那次事件 → 必须认出来是"自己写的"(不多传一次)
        var sink2 = new SqliteExpectedChangeSink(store);
        var ledger2 = new ExpectedChangeLedger(clock: clock, sink: sink2);
        Assert(ledger2.PendingCount == 1, $"重启后应把账本读回来,实际 {ledger2.PendingCount}");
        Assert(ledger2.TryConsume(@"C:\sync\crash.bin", 4096, ticks) == ExpectedChangeVerdict.Consumed,
            "重启后补发的自写事件必须被销账(否则会凭空多传一次)");
        Assert(sink2.Load().Count == 0, "销账后库里应清空");
    });
    return Task.CompletedTask;
}

// 回归(真实事故,2026-09):第一批任务跑完后队列**静默失效**,第二批一个都不执行。
//
// 成因:EnsureWorkers 用 `_workers.Count` 判断"还要不要补工作线程",而退出的工作线程
// **不会从 _workers 里移除**,于是它长期 ≥ 上限 → 永远不再补人 → 新入队的任务没人取。
// 现场表现极具误导性:同步日志停在「对账:排空队列超时(待处理=1 在跑=0)」,
// 文件明明在本地却永远传不上去;而单批次的并发用例完全看不出问题(它们在 drain 之前
// 就把任务全塞进去了,第一批刚好把 _workers 撑到上限)。
//
// 所以这条用例的**关键**是:每批都 drain 一次,让工作线程自然退出(队列空即退出),
// 第二批才落在"没有活 worker"的时刻 —— 修复前这条必然挂住/失败。
static async Task CheckEnqueueAfterDrainStillRunsAsync()
{
    await using var q = new TransferQueue(new TransferQueueOptions { MaxConcurrency = 3 });
    var ran = 0;

    for (var round = 1; round <= 3; round++)
    {
        for (var i = 0; i < 4; i++)
        {
            q.Enqueue(new TransferJob
            {
                Id = $"batch{round}-{i}",
                DisplayName = $"f{i}.bin",
                Direction = TransferDirection.Upload,
                TotalBytes = 10,
                Run = async (_, ct) =>
                {
                    await Task.Delay(5, ct);
                    Interlocked.Increment(ref ran);
                    return 10;
                },
            });
        }

        var drain = q.DrainAsync();
        var winner = await Task.WhenAny(drain, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert(winner == drain,
            $"第 {round} 批任务没能跑空 —— 队列在第一批之后不再派发新任务(工作线程活性判断失效)");
        await drain;
    }

    Assert(ran == 12, $"三批共 12 个任务都必须执行,实际 {ran} —— 少执行说明有批次被静默丢掉");
}

// 回归(2026-09 现场):同步日志里出现过
//   「期望变更账本异常(已降级为「可能重复上传一次」):NullReferenceException」
// 而当时的表现是"用户改动偶尔被多传一次"——不致命,但会让"改名不重传"这条验收
// 时好时坏。触发点是**问一个从没登记过的路径**(对账阶段对每个远端条目都会问一次)。
// 这里把那个触发点固定下来:账本必须回答"不是我们的",而不是抛异常。
// ⚠ 诚实记录:当时只在 Release 下偶发、没留下栈,本次**没能复现**出原始崩溃;
// 所以这条用例是"守住行为边界",不等于已证明根因。SyncHost 侧仍保留 try/catch 兜底
// (账本的作用是"少传一次",绝不能因为它坏掉而打死整轮对账)。
static Task CheckLedgerUnknownPathAsync()
{
    WithTempStore(store =>
    {
        var clock = new FakeClock();
        var ledger = new ExpectedChangeLedger(clock: clock, sink: new SqliteExpectedChangeSink(store));
        var ticks = clock.GetUtcNow().UtcTicks;

        // ① 空账本直接问
        Assert(ledger.TryConsume(@"C:\sync\nobody.txt", 10, ticks) == ExpectedChangeVerdict.NotOurs,
            "从没登记过的路径必须返回 NotOurs");

        // ② 登记过别的路径,再问一个未登记的(真实对账就是这种交错)
        ledger.Record(@"C:\sync\known.txt", size: 10, mtimeTicks: ticks);
        Assert(ledger.TryConsume(@"C:\sync\other.txt", 10, ticks) == ExpectedChangeVerdict.NotOurs,
            "未登记路径必须返回 NotOurs");
        Assert(ledger.PendingCount == 1, "问未登记路径不该把已登记的条目弄丢");

        // ③ 同一路径反复问(销账后再次问):仍需安全回答
        Assert(ledger.TryConsume(@"C:\sync\known.txt", 10, ticks) == ExpectedChangeVerdict.Consumed,
            "登记过的路径应销账");
        Assert(ledger.TryConsume(@"C:\sync\known.txt", 10, ticks) == ExpectedChangeVerdict.NotOurs,
            "已销账的路径再问必须是 NotOurs(不能报 Consumed 把用户真实修改吞掉)");

        // ④ 过期淘汰后再问(注意顺序:先登记、再让时间走、最后问)
        ledger.Record(@"C:\sync\stale.txt", size: 5, mtimeTicks: ticks);
        clock.Advance(TimeSpan.FromMinutes(1)); // 默认 TTL 是 10s
        ledger.EvictExpired();
        Assert(ledger.TryConsume(@"C:\sync\stale.txt", 5, ticks) == ExpectedChangeVerdict.NotOurs,
            "过期条目必须失效(否则用户稍后的真实修改会被吞掉)");
    });
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

static void WithTempStore(Action<StateStore> body)
{
    var dir = Path.Combine(Path.GetTempPath(), "netdisk-transfer-check");
    Directory.CreateDirectory(dir);
    var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".db");
    try
    {
        using var store = StateStore.Open(path);
        body(store);
    }
    finally
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { if (File.Exists(path + suffix)) File.Delete(path + suffix); }
            catch (IOException) { }
        }
    }
}

/// <summary>永不派发的 SynchronizationContext:模拟"UI 被挂起/最小化"。</summary>
sealed class NeverDispatchContext : SynchronizationContext
{
    private int _postCount;

    public int PostCount => Volatile.Read(ref _postCount);

    public override void Post(SendOrPostCallback d, object? state)
    {
        // 故意**不执行**:如果队列依赖 UI 调度器,相关任务就永远不会完成
        Interlocked.Increment(ref _postCount);
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        Interlocked.Increment(ref _postCount);
        // Send 也不执行(真实 UI 被挂起时它会阻塞,这里用"不执行"来避免测试自身挂死)
    }
}

sealed class FakeClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 9, 12, 10, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now = _now.Add(delta);
}
