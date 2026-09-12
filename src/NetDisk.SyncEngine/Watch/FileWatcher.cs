// 目录监听 + 事件触发定向重扫(DE-D-08)。
//
// 三条纪律(都是"监听器看起来在工作、实际已经漏事件"的入口):
//
// ①**溢出必须变成"重扫那棵子树",而不是丢事件**:FileSystemWatcher 的内部缓冲区
//   在事件风暴(解压一个 10 万文件的压缩包、git checkout)时会溢出,此时它只发一个
//   Error 事件 —— 缓冲区里的事件**已经丢了**。若只记一条日志,本地与远端就会静默长期
//   不一致(而且是"某些文件一直没同步",用户根本说不清是哪一步错了)。
//   所以溢出 → 请求重扫**这棵子树**(不是全盘,也不是不管)。
//
// ②**事件必须去抖 + 定向**:Windows 上一次保存会连发若干条(创建/修改/重命名),
//   而一次重扫可能扫上万条。做法:同一路径在去抖窗口内合并,并且只重扫**受影响的目录**。
//
// ③**扫描只 stat、不读内容**(DE-D-08 ④):读内容既没必要(变更判定靠 size/mtime/hash
//   三件套,而 hash 只在需要时算)又昂贵(用户机器上几万个文件,一次卫生扫读一遍内容
//   相当于把磁盘全读一次)。这条纪律由**机械规则**盯着(见 WatcherCheck 的源码断言),
//   不靠自觉。
//
// 监听后端被抽象成 <see cref="IWatcherBackend"/>,因此"溢出 → 只重扫该子树"这条逻辑
// 可以被真的跑出来(假后端直接触发 Failed),而不必真的去撑爆系统缓冲区。

using System.Collections.Concurrent;

namespace NetDisk.SyncEngine.Watch;

/// <summary>监听后端(真实实现包 <see cref="FileSystemWatcher"/>;测试可注入假的)。</summary>
public interface IWatcherBackend : IDisposable
{
    /// <summary>某个路径发生了变化(文件或目录)。</summary>
    event Action<string>? Changed;

    /// <summary>后端出错(**缓冲区溢出**、目录被删等)。</summary>
    event Action<Exception>? Failed;

    /// <summary>开始监听(路径由实现决定)。</summary>
    void Start();

    /// <summary>被监听的根路径(溢出时按它兜底)。</summary>
    string RootPath { get; }
}

/// <summary>重扫请求(由溢出或去抖后的变更触发)。</summary>
public sealed record RescanRequest(string Path, RescanReason Reason, bool WholeRoot = false);

public enum RescanReason
{
    /// <summary>普通变更(去抖后按目录重扫)。</summary>
    Changed,

    /// <summary>缓冲区溢出 / 后端故障:必须重扫受影响的子树。</summary>
    Overflow,
}

/// <summary>
/// 去抖 + 溢出转重扫的调度器。它**不认识** FileSystemWatcher,只认识后端接口 ——
/// 这样"溢出后到底扫了哪棵子树"就是一个可断言的输出,而不是一句日志。
/// </summary>
public sealed class FileWatcher : IDisposable
{
    /// <summary>去抖窗口:一次保存产生的多条事件在这里合并成一条重扫请求。</summary>
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(500);

    private readonly IWatcherBackend _backend;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _debounce;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    private ITimer? _timer;
    private bool _disposed;

    public FileWatcher(IWatcherBackend backend, TimeProvider? clock = null, TimeSpan? debounce = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _clock = clock ?? TimeProvider.System;
        _debounce = debounce ?? DefaultDebounce;
        _backend.Changed += OnChanged;
        _backend.Failed += OnFailed;
    }

    /// <summary>重扫请求(去抖之后按目录聚合)。</summary>
    public event Action<RescanRequest>? RescanRequested;

    public string RootPath => _backend.RootPath;

    public void Start()
    {
        _backend.Start();
        lock (_gate)
        {
            _timer ??= _clock.CreateTimer(_ => Flush(), null, _debounce, _debounce);
        }
    }

    private void OnChanged(string path)
    {
        // 目录事件也收:新建/删除目录本身就要重扫它**所在**的目录
        var parent = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(parent))
        {
            parent = _backend.RootPath;
        }
        // 去抖:同一目录在窗口内出现多次只保留一条(值用"最近一次时间",只用于判断是否有待处理)
        var now = _clock.GetUtcNow();
        _pending.AddOrUpdate(parent, now, (_, _) => now);
    }

    private void OnFailed(Exception ex)
    {
        // 纪律①:溢出 = **事件已丢**。这里不尝试"从缓冲区里补"——缓冲区的内容已经被系统
        // 丢弃了,唯一正确的动作是把这棵子树重扫一遍(整根,因为它可能影响任意子目录)。
        RescanRequested?.Invoke(new RescanRequest(_backend.RootPath, RescanReason.Overflow, WholeRoot: true));
        _ = ex; // 由上层记录(这里不引日志依赖,便于内核单测)
    }

    /// <summary>把去抖窗口内累积的目录各发一条重扫请求(由计时器或测试直接调用)。</summary>
    public void Flush()
    {
        if (_disposed)
        {
            return;
        }
        foreach (var dir in _pending.Keys.ToArray())
        {
            if (_pending.TryRemove(dir, out _))
            {
                RescanRequested?.Invoke(new RescanRequest(dir, RescanReason.Changed));
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _backend.Changed -= OnChanged;
        _backend.Failed -= OnFailed;
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
        _backend.Dispose();
    }
}

/// <summary>真实后端:<see cref="FileSystemWatcher"/> + 足够大的缓冲区。</summary>
public sealed class FileSystemWatcherBackend : IWatcherBackend
{
    /// <summary>
    /// 64KB 缓冲区。默认 8KB 在解压/checkout 这类风暴下几乎必然溢出 ——
    /// 而溢出一次就要全根重扫,代价远大于多占 56KB 内存。
    /// </summary>
    public const int BufferBytes = 64 * 1024;

    private readonly FileSystemWatcher _watcher;

    public FileSystemWatcherBackend(string root)
    {
        RootPath = root;
        _watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = BufferBytes,
            // 只要"名字/大小/时间"级别的通知:不需要安全属性等无关变更(少一类噪声事件)
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                           | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _watcher.Created += (_, e) => Changed?.Invoke(e.FullPath);
        _watcher.Changed += (_, e) => Changed?.Invoke(e.FullPath);
        _watcher.Deleted += (_, e) => Changed?.Invoke(e.FullPath);
        _watcher.Renamed += (_, e) =>
        {
            Changed?.Invoke(e.OldFullPath);
            Changed?.Invoke(e.FullPath);
        };
        _watcher.Error += (_, e) => Failed?.Invoke(e.GetException());
    }

    public event Action<string>? Changed;

    public event Action<Exception>? Failed;

    public string RootPath { get; }

    public void Start() => _watcher.EnableRaisingEvents = true;

    public void Dispose() => _watcher.Dispose();
}
