// 传输队列:并发上限 + 限速 + 进度(DE-D-14)。
//
// 队列本身很短,难的是三条边界条件:
//
// ①**并发上限必须真的封顶**:`SemaphoreSlim` 只保证"进入的许可数",而限速等待发生在
//   许可**之内** —— 若先取限速再取许可,限速等待会占着并发名额睡觉,于是"并发 3"
//   退化成"同时只有 1 个在真正传"(其它两个在等令牌)。所以顺序是:
//   **先进槽位,再等令牌**;而令牌桶允许突发,不会让槽位长时间空转。
//
// ②**一个任务失败不能拖垮队列**:任何一个传输抛异常都必须只影响它自己(记录 + 继续),
//   否则一次 507/网络抖动会让后面几百个文件永远排在队里 —— 用户看到"同步卡住了"。
//
// ③**进度只在跨过阈值时才上报**:每个分片都回调会让 UI 每秒收到上千次通知(而托盘图标
//   重绘比同步本身还贵)。这里按"字节增量 ≥ 阈值(默认 1MB)或任务完成"上报。
//
// 队列**不依赖 UI 线程**(见 RateLimiter 顶部说明):等待与推进全部在线程池上,
// UI 只订阅 Progress 事件。

using System.Collections.Concurrent;

namespace NetDisk.SyncEngine.Transfer;

/// <summary>一个传输任务。</summary>
public sealed record TransferJob
{
    /// <summary>稳定 id(用于进度归并与取消)。</summary>
    public required string Id { get; init; }

    /// <summary>显示名(托盘提示用)。</summary>
    public required string DisplayName { get; init; }

    public required TransferDirection Direction { get; init; }

    /// <summary>预计字节数(限速与进度都要用;0 表示未知)。</summary>
    public long TotalBytes { get; init; }

    /// <summary>实际执行体;返回已传输字节数。</summary>
    public required Func<IProgress<long>, CancellationToken, Task<long>> Run { get; init; }
}

/// <summary>进度快照(托盘/UI 消费)。</summary>
public sealed record TransferProgress(
    string JobId,
    string DisplayName,
    TransferDirection Direction,
    long DoneBytes,
    long TotalBytes,
    bool Completed,
    bool Failed);

/// <summary>队列配置。</summary>
public sealed record TransferQueueOptions
{
    /// <summary>同时进行的传输数(7.2 默认 3)。</summary>
    public int MaxConcurrency { get; init; } = 3;

    public RateLimitOptions RateLimit { get; init; } = new();

    /// <summary>进度上报的字节增量阈值(默认 1MB)。</summary>
    public long ProgressStepBytes { get; init; } = 1024 * 1024;
}

/// <summary>传输队列。</summary>
public sealed class TransferQueue : IAsyncDisposable
{
    private readonly TransferQueueOptions _options;
    private readonly RateLimiter _limiter;
    private readonly ConcurrentQueue<TransferJob> _pending = new();
    private readonly ConcurrentDictionary<string, long> _done = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _slots;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Task> _workers = new();
    private int _active;
    // 存活工作线程数:**必须在工作线程退出时递减**,否则 _workers 里堆的是"已经死了的任务",
    // EnsureWorkers 会以为还有人在干活,于是新入队的任务永远没人取 ——
    // 表现就是"第一批传完之后队列静默失效"(实测:待处理=1 在跑=0,永远不动)。
    private int _liveWorkers;

    public TransferQueue(TransferQueueOptions? options = null, TimeProvider? clock = null)
    {
        _options = options ?? new TransferQueueOptions();
        if (_options.MaxConcurrency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "并发上限必须 ≥1");
        }
        _limiter = new RateLimiter(_options.RateLimit, clock);
        _slots = new SemaphoreSlim(_options.MaxConcurrency, _options.MaxConcurrency);
    }

    /// <summary>进度事件(托盘订阅;按阈值节流)。</summary>
    public event Action<TransferProgress>? Progress;

    /// <summary>失败事件(UI 提示;不影响队列继续跑)。</summary>
    public event Action<TransferJob, Exception>? Failed;

    public int MaxConcurrency => _options.MaxConcurrency;

    /// <summary>当前正在传输的数量(检查器用它断言"并发从未超过上限")。</summary>
    public int ActiveCount => Volatile.Read(ref _active);

    /// <summary>历史最高并发(诊断:如果它 < 上限,说明限速把槽位睡住了)。</summary>
    public int PeakConcurrency { get; private set; }

    /// <summary>队列里还有多少待处理。</summary>
    public int PendingCount => _pending.Count;

    /// <summary>入队(不阻塞;由工作线程取走)。</summary>
    public void Enqueue(TransferJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        _pending.Enqueue(job);
        EnsureWorkers();
    }

    /// <summary>等所有任务结束(测试与"退出前等待"用)。</summary>
    public async Task DrainAsync(CancellationToken ct = default)
    {
        while (!_pending.IsEmpty || ActiveCount > 0)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(5, ct).ConfigureAwait(false);
        }
    }

    /// <summary>确保有足够的后台工作线程(线程池调度,**不经 UI 调度器**)。</summary>
    private void EnsureWorkers()
    {
        lock (_workers)
        {
            var wanted = Math.Min(_options.MaxConcurrency, Math.Max(1, _pending.Count));
            while (Volatile.Read(ref _liveWorkers) < wanted)
            {
                // 先加计数再启线程:线程体内 finally 会递减,保证计数与实际存活一致
                Interlocked.Increment(ref _liveWorkers);
                // Task.Run:工作线程跑在线程池上 —— 即使 UI 线程被挂起/最小化,
                // 传输也照常推进(验收"最小化后继续传")
                _workers.Add(Task.Run(() => WorkerLoopAsync(_shutdown.Token)));
            }
        }
    }

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        try
        {
        while (!ct.IsCancellationRequested)
        {
            if (!_pending.TryDequeue(out var job))
            {
                return; // 没活了就退出;下一次 Enqueue 会再拉起工作线程(靠 _liveWorkers 判断)
            }

            await _slots.WaitAsync(ct).ConfigureAwait(false);
            var active = Interlocked.Increment(ref _active);
            if (active > PeakConcurrency)
            {
                PeakConcurrency = active;
            }
            try
            {
                await RunJobAsync(job, ct).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
                _slots.Release();
            }
        }
        }
        finally
        {
            Interlocked.Decrement(ref _liveWorkers);
        }
    }

    private async Task RunJobAsync(TransferJob job, CancellationToken ct)
    {
        // 进度节流:只有跨过阈值(或结束)才上报
        var lastReportedBy = 0L;
        // 用内联回调而不是 BCL 的 `Progress<T>`:`Progress<T>` 会为**每一条**进度上报
        // 往捕获到的上下文 Post 一次(线程池往返),而进度上报是高频动作(每个阈值一次),
        // 那些往返纯属浪费。这里直接在本工作线程上回调;需要跨线程更新 UI 的订阅者
        // 自己 marshal(那是 UI 的职责)。
        //
        // 注意(诚实记录):`Progress<T>` **不会**在这里造成"UI 绑定" —— 它是在**工作线程**
        // 上构造的,捕获到的是 null 上下文。真正保证"最小化后继续传"的是下面工作循环走
        // `Task.Run` + 全程 `ConfigureAwait(false)`:后者让续体留在**线程池**上,
        // 而不是排回 UI 线程。检查器里的"假 UI 上下文"用例正是对着这一点断言的。
        IProgress<long> progress = new InlineProgress(done =>
        {
            var reported = Interlocked.Read(ref lastReportedBy);
            if (done - reported < _options.ProgressStepBytes && done < job.TotalBytes)
            {
                return;
            }
            Interlocked.Exchange(ref lastReportedBy, done);
            _done[job.Id] = done;
            Progress?.Invoke(new TransferProgress(
                job.Id, job.DisplayName, job.Direction, done, job.TotalBytes,
                Completed: false, Failed: false));
        });

        try
        {
            // 限速(**在槽位之内**):按方向申请额度,等待期间不影响其它槽位
            await _limiter.AcquireAsync(job.Direction, job.TotalBytes, ct).ConfigureAwait(false);
            var done = await job.Run(progress, ct).ConfigureAwait(false);
            _done[job.Id] = done;
            Progress?.Invoke(new TransferProgress(
                job.Id, job.DisplayName, job.Direction, done, job.TotalBytes,
                Completed: true, Failed: false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 关闭中:静默结束
        }
        catch (Exception ex)
        {
            // 单个任务失败**不影响队列**:记录 + 继续
            _done[job.Id] = -1;
            Progress?.Invoke(new TransferProgress(
                job.Id, job.DisplayName, job.Direction, 0, job.TotalBytes,
                Completed: false, Failed: true));
            Failed?.Invoke(job, ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        List<Task> workers;
        lock (_workers)
        {
            workers = _workers.ToList();
        }
        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or AggregateException)
        {
            // 关闭路径:取消是预期结果
        }
        _slots.Dispose();
        _shutdown.Dispose();
    }
}

/// <summary>
/// 内联进度实现:**不捕获 SynchronizationContext**,回调直接在当前线程执行。
///
/// 为什么不用 BCL 的 <c>Progress&lt;T&gt;</c>:它会把回调 Post 到创建时的上下文,
/// 于是"队列 → UI"这条反向依赖就建立起来了 —— UI 被挂起(最小化/锁屏)时传输跟着停。
/// 传输的进度是"尽力而为"的通知,丢一两条无所谓;卡住整个传输才是真问题。
/// </summary>
internal sealed class InlineProgress : IProgress<long>
{
    private readonly Action<long> _callback;

    public InlineProgress(Action<long> callback) => _callback = callback;

    public void Report(long value) => _callback(value);
}
