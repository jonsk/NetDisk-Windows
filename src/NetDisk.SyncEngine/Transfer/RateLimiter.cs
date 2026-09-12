// 传输队列的限速与并发(DE-D-14)。
//
// 三件事各有各的坑:
//
// ①**并发可配(默认 3)**:不是"越多越快"。桌面端跑在用户的机器上,并发拉满会同时
//   挤占磁盘 IO、网络与 CPU,用户的心智模型是"网盘别把我的电脑拖慢"。默认 3 是
//   7.2 的口径,并且**必须可配**(有人愿意为速度让出更多资源)。
//
// ②**上下行分别限速**:家用宽带的上下行**极不对称**(下载 500Mbps / 上传 30Mbps 很常见),
//   用一个总限速会得到一个荒谬的结果 —— 限了 30Mbps 下载也跟着变慢,或者按下载限速
//   上传根本传不动。所以两个方向各自一个令牌桶。
//
// ③**最小化后继续传(即:传输不得依赖 UI 线程)**:这是最容易"实现正确但行为错误"的一条。
//   如果队列用 UI 调度器(DispatcherTimer / SynchronizationContext.Post)驱动,
//   窗口最小化、被挂起或用户拖窗口时,传输就会跟着停 —— 而"最小化后继续传"是网盘客户端的
//   基本预期。所以这一层的所有等待与推进都必须发生在**线程池**上,UI 只订阅进度事件。
//   检查器用一个"永不派发"的 SynchronizationContext 来复现这一点:如果实现里任何一步
//   把续体排到 UI 上,那个用例会直接挂住(超时失败)。

namespace NetDisk.SyncEngine.Transfer;

/// <summary>传输方向(上下行分别限速)。</summary>
public enum TransferDirection
{
    Upload,
    Download,
}

/// <summary>限速配置(0 = 不限速)。</summary>
public sealed record RateLimitOptions
{
    /// <summary>上行字节/秒(0 = 不限)。</summary>
    public long UploadBytesPerSecond { get; init; }

    /// <summary>下行字节/秒(0 = 不限)。</summary>
    public long DownloadBytesPerSecond { get; init; }

    public long LimitFor(TransferDirection direction) => direction == TransferDirection.Upload
        ? UploadBytesPerSecond
        : DownloadBytesPerSecond;
}

/// <summary>
/// 令牌桶限速器(每方向一个桶)。
///
/// 用**字节**而不是"每次传输"计数:限速的意义是"别把用户的带宽占满",
/// 而大文件与小文件的代价差三个数量级 —— 按次数限速对 10GB 文件毫无作用。
/// </summary>
public sealed class RateLimiter
{
    /// <summary>桶容量 = 1 秒的额度(允许短时突发到 1 秒的量,再平滑回填)。</summary>
    private const double BurstSeconds = 1.0;

    private readonly RateLimitOptions _options;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly Dictionary<TransferDirection, Bucket> _buckets = new();

    public RateLimiter(RateLimitOptions options, TimeProvider? clock = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>当前配置(UI 展示/热更新用)。</summary>
    public RateLimitOptions Options => _options;

    /// <summary>
    /// 申请 <paramref name="bytes"/> 字节的额度,必要时等待。
    /// 不限速(0)时立即返回 —— **不引入任何等待**,否则"关掉限速"反而变慢。
    /// </summary>
    public async Task AcquireAsync(TransferDirection direction, long bytes, CancellationToken ct = default)
    {
        if (bytes <= 0)
        {
            return;
        }
        var limit = _options.LimitFor(direction);
        if (limit <= 0)
        {
            return;
        }

        while (true)
        {
            TimeSpan wait;
            lock (_gate)
            {
                var bucket = GetBucket(direction, limit);
                var now = _clock.GetUtcNow();
                // **单次申请可能大于桶容量**(例如 10GB 文件按整文件申请):桶容量取
                // `max(1 秒额度, 本次申请量)`,否则永远攒不够 —— 实现会陷在
                // "睡 1 秒 → 还是不够 → 再睡 1 秒"的死循环里(检查器 ④ 就是这么抓出来的:
                // 用例直接挂住,而不是报错,因为循环本身"看起来在正常工作")。
                var capacity = Math.Max(limit * BurstSeconds, bytes);
                bucket.Refill(now, limit, capacity);
                if (bucket.Tokens >= bytes)
                {
                    bucket.Tokens -= bytes;
                    return;
                }
                // 需要等多久才能攒够(用**缺多少**算,而不是固定等一秒)
                var missing = bytes - bucket.Tokens;
                wait = TimeSpan.FromSeconds(missing / limit);
            }
            await Task.Delay(wait, _clock, ct).ConfigureAwait(false);
        }
    }

    private Bucket GetBucket(TransferDirection direction, long limit)
    {
        if (!_buckets.TryGetValue(direction, out var bucket) || Math.Abs(bucket.Limit - limit) > 0.5)
        {
            // 配置热更新(用户在托盘里改了限速)时重建桶,避免用旧速率回填
            bucket = new Bucket
            {
                // 初始给满 1 秒额度(允许突发);若单次申请更大,循环里会按容量补足
                Tokens = limit * BurstSeconds,
                Limit = limit,
                LastRefill = _clock.GetUtcNow(),
            };
            _buckets[direction] = bucket;
        }
        return bucket;
    }

    private sealed class Bucket
    {
        public double Tokens;
        public double Limit;
        public DateTimeOffset LastRefill;

        public void Refill(DateTimeOffset now, long limit, double capacity)
        {
            var elapsed = (now - LastRefill).TotalSeconds;
            if (elapsed <= 0)
            {
                return;
            }
            Tokens = Math.Min(capacity, Tokens + elapsed * limit);
            LastRefill = now;
        }
    }
}
