// 通知中心(DE-D-18):四类通知的**触发与节流**。
//
// 托盘气泡本身属于 App 层(Windows UI),但"什么时候该弹、弹什么、弹几次"是**可以单测的
// 逻辑**,而且这里恰好是两类事故的高发区:
//
//  ①**通知风暴**:"同步完成"若按文件弹,一次同步几千个文件就是几千个气泡 —— 用户的第一反应
//    是关掉通知,于是**真正重要的那几条(冲突/被移出空间/配额预警)也一起被关掉了**。
//    所以要**合并窗口**:窗口内的完成事件折成一条汇总。
//
//  ②**阈值抖动反复报警**:配额在 80% 上下反复穿越(上传一点、删一点)时,若每次跨过阈值
//    都报警,用户会被密集提醒淹没。所以要**滞回**:过 80% 报一次,必须**回落到 70% 以下**
//    才重新武装(与 6.11 配额预警阈值同源,但这是端上的"别烦人"策略)。
//
// 另外两条内容纪律:
//   - **被移出空间/空间被冻结**的通知必须明确写"本地文件不会删除"(7.2/R-19:客户端保留本地,
//     用户最担心的是"我的文件是不是没了");
//   - **冲突**通知要说清"两边都改了、需要你选"(冲突不会自动解决,用户必须知道去处理)。

namespace NetDisk.SyncEngine.Notify;

/// <summary>四类通知(验收口径)。</summary>
public enum NotificationKind
{
    /// <summary>同步完成(合并后的汇总)。</summary>
    SyncCompleted,

    /// <summary>冲突(需要用户处理)。</summary>
    Conflict,

    /// <summary>被移出空间 / 空间被冻结(本地保留)。</summary>
    SpaceRevoked,

    /// <summary>配额预警。</summary>
    QuotaWarning,

    /// <summary>通用信息(调用方明确要告诉用户一件事,例如"关闭到托盘后仍在同步")。</summary>
    Info,
}

/// <summary>一条通知。</summary>
public sealed record AppNotification(
    NotificationKind Kind,
    string Title,
    string Message,
    string? SpaceId = null,
    string? Path = null);

/// <summary>通知策略(可配;默认值见字段说明)。</summary>
public sealed record NotifyOptions
{
    /// <summary>"同步完成"的合并窗口:窗口内的完成事件折成一条。</summary>
    public TimeSpan CompletionWindow { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>配额预警阈值(过线报一次)。</summary>
    public int QuotaWarnPercent { get; init; } = 80;

    /// <summary>配额解除阈值(**必须低于**预警阈值,形成滞回)。</summary>
    public int QuotaClearPercent { get; init; } = 70;

    public bool IsSane() => QuotaClearPercent < QuotaWarnPercent && CompletionWindow > TimeSpan.Zero;
}

/// <summary>通知中心:把引擎里发生的事变成"该不该弹、弹什么"。</summary>
public sealed class NotificationCenter
{
    private readonly TimeProvider _clock;
    private readonly NotifyOptions _options;
    private readonly Dictionary<string, bool> _quotaWarned = new(StringComparer.Ordinal);

    private DateTimeOffset _lastCompletionAt = DateTimeOffset.MinValue;
    private int _pendingFiles;
    private long _pendingBytes;

    public NotificationCenter(TimeProvider? clock = null, NotifyOptions? options = null)
    {
        _clock = clock ?? TimeProvider.System;
        _options = options ?? new NotifyOptions();
        if (!_options.IsSane())
        {
            throw new ArgumentOutOfRangeException(nameof(options),
                "通知策略不成立:解除阈值必须低于预警阈值,且合并窗口必须为正");
        }
    }

    /// <summary>通知出口(App 层的托盘/Toast 订阅它)。</summary>
    public event Action<AppNotification>? Raised;

    public NotifyOptions Options => _options;

    /// <summary>一次同步完成(<paramref name="files"/> 个文件 / <paramref name="bytes"/> 字节)。</summary>
    public void NotifySyncCompleted(int files, long bytes)
    {
        if (files <= 0 && bytes <= 0)
        {
            return;
        }
        var now = _clock.GetUtcNow();
        if (now - _lastCompletionAt >= _options.CompletionWindow)
        {
            _lastCompletionAt = now;
            Raise(new AppNotification(NotificationKind.SyncCompleted, "同步完成",
                $"已同步 {files} 个文件({Human(bytes)})"));
            return;
        }
        // 窗口内:只累计,不弹(等 FlushPending 汇总)
        _pendingFiles += files;
        _pendingBytes += bytes;
    }

    /// <summary>
    /// 把合并窗口内累计的完成事件汇总成一条(App 的计时器周期性调用;也可在退出前调用)。
    /// </summary>
    public void FlushPending()
    {
        if (_pendingFiles == 0 && _pendingBytes == 0)
        {
            return;
        }
        var files = _pendingFiles;
        var bytes = _pendingBytes;
        _pendingFiles = 0;
        _pendingBytes = 0;
        _lastCompletionAt = _clock.GetUtcNow();
        Raise(new AppNotification(NotificationKind.SyncCompleted, "同步完成",
            $"已同步 {files} 个文件({Human(bytes)})"));
    }

    /// <summary>冲突:必须让用户知道"两边都改了,需要选择保留哪边"。</summary>
    public void NotifyConflict(string path, long localVersion, long remoteVersion, string? spaceId = null)
        => Raise(new AppNotification(NotificationKind.Conflict, "发现冲突",
            $"「{path}」本地(v{localVersion})与远端(v{remoteVersion})都改过,请在客户端中选择保留哪一边",
            spaceId, path));

    /// <summary>
    /// 被移出空间 / 空间被冻结。**必须写明本地文件保留** —— 用户最担心"我的文件是不是没了"。
    /// </summary>
    public void NotifySpaceRevoked(string spaceId, string reason)
        => Raise(new AppNotification(NotificationKind.SpaceRevoked, "空间访问已变更",
            $"{reason}。本地文件**不会**被删除,恢复访问后会自动继续同步。", spaceId));

    /// <summary>配额预警(带滞回:过线报一次,回落到解除线以下才重新武装)。</summary>
    public void NotifyQuota(string spaceId, int usedPercent)
    {
        _quotaWarned.TryGetValue(spaceId, out var warned);

        if (usedPercent >= _options.QuotaWarnPercent && !warned)
        {
            _quotaWarned[spaceId] = true;
            Raise(new AppNotification(NotificationKind.QuotaWarning, "空间容量预警",
                $"空间已用 {usedPercent}%(预警线 {_options.QuotaWarnPercent}%),接近上限时上传会被拒", spaceId));
            return;
        }
        if (usedPercent <= _options.QuotaClearPercent && warned)
        {
            // 只有**回落到解除线以下**才重新武装;否则会在阈值上下反复报警(通知风暴的另一半)
            _quotaWarned[spaceId] = false;
        }
    }

    /// <summary>
    /// 一条**通用信息**通知。与其它通知不同,它不做合并、不做阈值判断 ——
    /// 调用方(界面)是明确要告诉用户一件事的,被策略悄悄吞掉才是错的。
    /// </summary>
    public void NotifyInfo(string title, string message) =>
        Raise(new AppNotification(NotificationKind.Info, title, message));

    private void Raise(AppNotification n) => Raised?.Invoke(n);

    private static string Human(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return $"{bytes / 1024.0 / 1024 / 1024:0.##} GB";
        }
        if (bytes >= 1024 * 1024)
        {
            return $"{bytes / 1024.0 / 1024:0.##} MB";
        }
        if (bytes >= 1024)
        {
            return $"{bytes / 1024.0:0.##} KB";
        }
        return $"{bytes} B";
    }
}
