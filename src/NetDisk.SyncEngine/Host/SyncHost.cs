// 同步宿主(SyncHost)—— MVP 接线的核心:把"已经写好但一直没接线"的引擎跑起来。
//
// 在此之前:登录/监听/状态机/账本/冲突裁决/传输队列**全部只是一个库 + 一堆检查器**,
// App 里连一个都没实例化(用户装完 MSI 只能看到团队空间页)。这个类就是那条线。
//
// 编排放在 SyncEngine 而不是 App,原因有三:
//   1. **可单测**:App 层依赖 WPF,一旦把编排写进 UI,就只能靠人点着看;
//   2. **不碰 UI 线程**:后台循环在 SyncEngine 里跑,App 只订阅事件 ——
//      这也正好落进 depsguard 的"线程纪律"(引擎侧不得出现 Dispatcher/WPF);
//   3. 依赖方向不变:SyncEngine → Transport。
//
// MVP 的范围(明确写下来,免得被误读成"完整同步引擎"):
//   - 一个空间、一棵目录树、文件级同步(目录只按需创建);
//   - 本地变更检测用 **大小 + mtime**(不做内容哈希);
//   - 远端变更用 SSE 事件触发**重新对账**(不做按 seq 的增量裁剪);
//   - 冲突策略:保留本地版本为**冲突副本**并拉回远端版本,冲突副本会在下一轮
//     作为新文件上传(即"两端都有,不丢任何一边")。
// 不在 MVP 内(后续):目录级移动/删除的远端回传、按 seq 增量、限速 UI、
//   只读浏览模式、多空间。
//
// **本地改名不重传**(2026-09-12 实现并真机验证):本机扫描本身只按相对路径记账,
//   单靠它"改名"就长成"旧路径消失 + 新路径出现" = 一个新文件。所以要靠**本机文件身份**
//   (DE-D-11 的 Win32FileIdentityProvider:卷序列号 + FileId)把它认回来:
//   身份记在 sync_state.local_identity 里(改名后旧路径就没了,没法再回溯查,必须提前存),
//   命中已知条目且旧路径已消失 → 调契约的 `PATCH /api/v1/files/{id}`(改名,带 base_version
//   乐观锁)原地改,远端 **file_id 不变、版本 +1、旧名字消失**。
//   任何不确定的情形都退回"当新文件上传"(宁可多传一次,也不能因为猜错把文件搬走);
//   身份拿不到(NTFS 之外的盘/权限)时同样退化为"没有改名识别",不影响正确性。
//   实测证据:SyncHostCheck 场景 ⑥(同一 file id / 旧名消失 / 版本递增三条断言)。

using NetDisk.ClientCore;
using NetDisk.SyncEngine.Files;
using NetDisk.SyncEngine.Paths;
using NetDisk.SyncEngine.Sync;
using NetDisk.SyncEngine.Transfer;
using NetDisk.SyncEngine.Watch;
using NetDisk.Transport;

namespace NetDisk.SyncEngine.Host;

/// <summary>界面上展示的单条状态。</summary>
/// <param name="RelativePath">相对同步根的路径。</param>
/// <param name="State">状态。</param>
/// <param name="Message">补充说明(错误原因/冲突副本名等)。</param>
/// <param name="Version">已知的远端版本(0 = 未知)。</param>
public sealed record SyncEntryStatus(string RelativePath, SyncState State, string Message, long Version);

/// <summary>同步宿主:登录 + 对账 + 本地监听→上传 + 远端事件→下载 + 冲突副本。</summary>
public sealed class SyncHost : IAsyncDisposable
{
    private readonly ClientConfig _config;
    private readonly ITokenProvider _tokens;
    private readonly FileApi _files;
    private readonly IFileIdentityProvider _identity;
    private readonly Func<ITokenProvider, string, SseChangeStream>? _sseFactory;
    private readonly TimeProvider _clock;

    private readonly StateStore _store;
    private readonly ExpectedChangeLedger _ledger;
    private readonly TransferQueue _queue;
    private readonly FileWatcher? _watcher;
    private readonly List<SyncEntryStatus> _status = new();
    // 对账必须**串行**:监听器回调、启动、手动触发三者都可能同时想对账。
    // 实测后果不是"慢一点",而是**挂死**:两个对账同时写 SQLite 状态库并互相
    // 等待队列排空,谁也走不到 DrainAsync 的结束条件。
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);
    private int _reconcilePending;
    // 空间**根目录**的 id:相对路径必须相对它算,否则第一层会多出一个根目录名
    // (实测:远端算出 "根目录/x",本地扫描是 "x" → 两边永不相等 → 每轮都在
    //  "下载一个不存在的新文件" 与 "上传一个远端已存在的文件(409)" 之间打转)。
    private string? _rootId;
    // 目录名缓存:父链上溯是**每个条目一次 GET**,不缓存会让对账变成 N×深度 次请求
    private readonly Dictionary<string, string> _dirNameCache = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _localLoop;
    private Task? _remoteLoop;
    private volatile bool _running;
    /// <summary>暂停中(界面按了"暂停")。对账/监听/事件流/传输都停下,数据不动。</summary>
    private volatile bool _paused;
    /// <summary>监听与事件流是否已接上(暂停时置 false;恢复时重新接上并全量对账补漏)。</summary>
    private bool _loopsStarted;

    /// <summary>状态变化(后台线程触发;App 必须 marshal 回 UI 线程)。</summary>
    public event Action<IReadOnlyList<SyncEntryStatus>>? StatusChanged;

    /// <summary>给用户看的一行进展(托盘提示/状态栏)。</summary>
    public event Action<string>? Notice;

    public SyncHost(
        ClientConfig config,
        ITokenProvider tokens,
        ApiClient api,
        TimeProvider? clock = null,
        bool watchLocal = true,
        Func<ITokenProvider, string, SseChangeStream>? sseFactory = null,
        string? statePath = null,
        IFileIdentityProvider? identityProvider = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _clock = clock ?? TimeProvider.System;
        _sseFactory = sseFactory;
        _files = new FileApi(api);
        // 本机文件身份(DE-D-11):可注入,便于在非 Windows/无文件系统上单测改名逻辑
        _identity = identityProvider ?? new Win32FileIdentityProvider();

        // 状态库路径**可注入**:一是让检查器能在临时目录里跑(不污染用户真实状态),
        // 二是出问题时能把状态库挪到别处做实验,而不是去动用户的程序目录。
        var resolvedState = statePath ?? ClientPaths.StatePath;
        _store = StateStore.Open(resolvedState);
        _ledger = new ExpectedChangeLedger(clock: _clock, sink: new SqliteExpectedChangeSink(_store));
        // 传输参数从**配置**来(并发/限速):配置文件里写了这些键,就必须真的生效 ——
        // 否则那份带 `_说明` 的配置在骗用户("我改了限速却没变化"是最难查的一类问题)。
        QueueOptions = BuildQueueOptions(_config);
        _queue = new TransferQueue(QueueOptions, _clock);
        if (watchLocal && !string.IsNullOrWhiteSpace(_config.SyncRoot))
        {
            _watcher = new FileWatcher(new FileSystemWatcherBackend(_config.SyncRoot), _clock);
        }
    }

    /// <summary>当前状态快照(UI 初次绑定用)。</summary>
    public IReadOnlyList<SyncEntryStatus> Status
    {
        get { lock (_gate) { return _status.ToArray(); } }
    }

    /// <summary>本会话生效的传输参数(并发/限速;界面显示与检查器断言用)。</summary>
    public TransferQueueOptions QueueOptions { get; }

    /// <summary>
    /// 把配置折算成传输参数(公开:检查器要能**不启动同步**就断言这条映射 ——
    /// "配置写了却不生效"属于最难查的一类问题,必须有本地断言钉住)。
    ///
    /// 三处夹紧都必要:
    ///   · 并发夹到 1..16:0/负数会让队列**永不执行**(配置手改坏了不该表现为"同步没反应"),
    ///     上限 16 是"别把用户机器和带宽打满"的经验值;
    ///   · 限速的 KB/s → B/s(配置里用 KB/s 是为了让用户填得直观);
    ///   · 负数一律当 0(不限速),不让一个笔误变成"永远在等令牌"。
    /// </summary>
    public static TransferQueueOptions BuildQueueOptions(ClientConfig cfg)
    {
        var concurrency = cfg.MaxConcurrency;
        if (concurrency < 1)
        {
            concurrency = 3;
        }
        if (concurrency > 16)
        {
            concurrency = 16;
        }
        static long ToBytesPerSecond(int kbps) => kbps > 0 ? kbps * 1024L : 0L;
        return new TransferQueueOptions
        {
            MaxConcurrency = concurrency,
            RateLimit = new RateLimitOptions
            {
                UploadBytesPerSecond = ToBytesPerSecond(cfg.UploadKbps),
                DownloadBytesPerSecond = ToBytesPerSecond(cfg.DownloadKbps),
            },
        };
    }

    public bool IsRunning => _running;

    /// <summary>是否处于暂停状态(界面按钮/状态文字用)。</summary>
    public bool IsPaused => _paused;

    /// <summary>
    /// 启动:对账一次 → 开监听与远端事件循环。
    /// 返回 false 表示**没登录**(由调用方弹登录页),不是错误。
    /// </summary>
    public async Task<bool> StartAsync(CancellationToken ct = default)
    {
        if (_running)
        {
            return true;
        }
        if (_config.IsUsable() is false)
        {
            Notice?.Invoke("未配置服务器地址或同步根");
            return false;
        }
        if (_tokens is TokenSession session && !await session.StartAsync(ct).ConfigureAwait(false))
        {
            return false; // 没登录/被吊销
        }

        Directory.CreateDirectory(LongPath.ToExtended(_config.SyncRoot));

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _paused = false;
        await ReconcileAsync(_cts.Token).ConfigureAwait(false);

        StartLoops();
        _running = true;
        Notice?.Invoke("同步已启动");
        return true;
    }

    /// <summary>
    /// **暂停同步**(用户在界面上按"暂停")。
    ///
    /// 语义(写清楚,免得被理解成别的):
    ///   · **停止产生新的传输**:不再对账(手动"立即同步"也会被忽略)、忽略本地监听事件、
    ///     断开远端事件流,并**取消正在排队/在跑的任务**(见 TransferQueue.CancelActive);
    ///   · **不动任何数据**:不删除、不回滚、不清理状态库 —— 恢复后接着干;
    ///   · 状态列表保留最后一轮的结果(界面显示"已暂停")。
    /// 为什么不"只停对账、让队列跑完":用户按暂停往往正因为"现在别传了"(开会/移动网络),
    /// 让几 GB 继续悄悄传完与按钮上的字不符。
    /// </summary>
    public async Task PauseAsync()
    {
        if (!_running || _paused)
        {
            return;
        }
        _paused = true;
        _running = false;
        // 队列先停:它在飞的请求会以取消收尾(不落半截数据:下载是 .part 原子改名、上传由服务端任务担责)
        _queue.CancelActive();
        await StopLoopsAsync().ConfigureAwait(false);
        Notice?.Invoke("同步已暂停(不再对账与传输;数据未改动)");
    }

    /// <summary>**继续同步**:重新对账一次并把监听/事件流接回去。</summary>
    public async Task ResumeAsync(CancellationToken ct = default)
    {
        if (!_paused)
        {
            return;
        }
        _paused = false;
        _queue.ResumeSession();
        _cts?.Dispose();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Notice?.Invoke("正在恢复同步…");
        await ReconcileAsync(_cts.Token).ConfigureAwait(false);
        StartLoops();
        _running = true;
        Notice?.Invoke("同步已恢复");
    }

    private void StartLoops()
    {
        if (_watcher is not null && !_loopsStarted)
        {
            _watcher.RescanRequested += OnRescanRequested;
            _watcher.Start();
        }
        if (_sseFactory is not null && _remoteLoop is null && _cts is not null)
        {
            _remoteLoop = Task.Run(() => RemoteLoopAsync(_cts.Token), _cts.Token);
        }
        _loopsStarted = true;
    }

    private async Task StopLoopsAsync()
    {
        if (_watcher is not null && _loopsStarted)
        {
            // 不复用/不销毁监听器:只**退订**。监听器本身还在跑(它的去抖队列里可能积着事件),
            // 恢复时重新订阅并做一次**全量对账** —— 漏掉的事件由这一轮补齐,
            // 所以"暂停期间的事件丢了"不会造成不一致(比给 watcher 加 Stop/Start 生命周期更不容易出错)。
            _watcher.RescanRequested -= OnRescanRequested;
            _watcher.Flush();
        }
        _loopsStarted = false;
        var loop = _remoteLoop;
        _remoteLoop = null;
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }
        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 暂停路径:取消是预期结果
            }
            catch (Exception ex)
            {
                Notice?.Invoke($"远端事件循环退出:{ex.Message}");
            }
        }
    }

    // ---------------------------------------------------------------- 对账

    /// <summary>
    /// 全量对账:MVP 的"真相来源"。
    /// 先远端→本地(缺的下载、旧的更新),再本地→远端(新文件/改动上传)。
    /// </summary>
    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        // 暂停中:连"立即同步"也不做 —— 按钮上的字是"暂停",那就什么都不该发生。
        // (恢复时会做一次全量对账,暂停期间攒下的改动不会丢。)
        if (_paused)
        {
            Notice?.Invoke("已暂停:本轮对账被跳过(点「继续同步」后会自动补上)");
            return;
        }
        // 已有对账在跑:记一个"待跑"标记后立刻返回(合并请求),由正在跑的那次收尾时再跑一遍。
        // 不这样做就会堆积 N 个并发对账 —— 它们互相踩状态库与队列。
        if (!await _reconcileGate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            Interlocked.Exchange(ref _reconcilePending, 1);
            return;
        }
        try
        {
            // 上限 3 轮:即便期间不断有新事件(活锁风险),也不会把一次调用变成永动机;
            // 剩下的改动交给下一次事件/下一轮对账处理,而不是在这里空转。
            var rounds = 0;
            do
            {
                Interlocked.Exchange(ref _reconcilePending, 0);
                await ReconcileCoreAsync(ct).ConfigureAwait(false);
                rounds++;
            }
            while (Interlocked.CompareExchange(ref _reconcilePending, 0, 0) == 1
                   && !ct.IsCancellationRequested && rounds < 3);
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    private async Task ReconcileCoreAsync(CancellationToken ct)
    {
        var spaceId = await ResolveSpaceIdAsync(ct).ConfigureAwait(false);
        var syncParent = string.IsNullOrEmpty(_config.ParentId) ? null : _config.ParentId;
        if (_rootId is null)
        {
            // 根目录 id = 同步起点那一层条目的 parent_id。空间为空时拿不到,
            // 那时也没有任何需要在本地建目录的远端条目,不影响正确性。
            var top = await _files.ListAsync(spaceId, syncParent, ct).ConfigureAwait(false);
            _rootId = top.FirstOrDefault()?.parent_id;
        }
        Notice?.Invoke("对账:开始列远端…");
        var remote = new Dictionary<string, EntryView>(StringComparer.OrdinalIgnoreCase);
        var parentOf = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        await foreach (var e in _files.WalkAsync(spaceId, string.IsNullOrEmpty(_config.ParentId) ? null : _config.ParentId, ct))
        {
            var rel = await RelativePathOfAsync(e).ConfigureAwait(false);
            if (rel is null)
            {
                continue; // 根目录自身
            }
            if (e.is_dir)
            {
                Directory.CreateDirectory(LongPath.ToExtended(LocalOf(rel)));
                continue;
            }
            remote[rel] = e;
            // 冲突标记是**粘性**的:每一轮对账开头都会把远端条目无脑写成 InSync,
            // 而冲突副本刚生成、下一轮(自己的 SSE 事件/账本触发的重新对账)就把它抹掉 ——
            // 表现是徽标闪一下就不见了,用户根本不知道发生过冲突。
            // 版本判据用的是 sync_state 表(KnownVersion),不依赖这里的显示状态,所以跳过是安全的。
            if (StateOf(rel) != SyncState.Conflict)
            {
                Upsert(rel, SyncState.InSync, "", e.version);
            }
        }

        Notice?.Invoke($"对账:远端 {remote.Count} 个文件,进入下载阶段");
        // **先把"本地改名"算出来**(纯本地计算,不发请求),原因是一个真实缺陷:
        // 对账顺序是"先远端→本地,再本地→远端",而改名在**推送之前**远端当然还是旧名字 ——
        // 下载阶段若不知道"这个旧名字是被改名带走的",就会把旧名字从服务端**拉回来一份**:
        // 远端是对的(旧名已消失),本地却多出一个旧名字,下一轮又会被当成新文件传上去。
        var renames = DetectLocalRenames();

        // **删除传播(双向,无用户确认)**
        //
        // 产品决策(2026-09-13,用户拍板"删除要双向传播"+"一致优先、无条件",二次确认由我定):
        // **删除无条件自动传播,两端都不弹确认**。安全性不靠"问用户",而靠**信号完整性**:
        // 只有"列表完整成功、扫描未截断、每个待删文件所在目录可枚举"时才认定是删除。
        //
        // 为什么这么严:`DirectoryScanner` 用 `IgnoreInaccessible = true`(读不了的子目录**静默跳过**)
        // 且 `MaxEntries` 超限是**静默截断** —— 这两条会让"看不见"伪装成"已删除"。
        // 若照此传播,一次权限抖动或一次 ACL 变更就会把远端文件删光(服务端是**硬删、无回收站**)。
        // 同理远端侧:列举失败会抛异常(不静默返回短列表),所以"列完了"这件事本身可信。
        var deletions = await PropagateDeletionsAsync(spaceId, remote, renames, ct).ConfigureAwait(false);
        if (deletions > 0)
        {
            Notice?.Invoke($"删除传播:本轮两端共删除 {deletions} 个文件");
        }
        // ① 远端 → 本地
        foreach (var (rel, entry) in remote)
        {
            ct.ThrowIfCancellationRequested();
            if (renames.OldPaths.Contains(rel))
            {
                continue; // 本地已改名带走:内容在新名字那边,改名由下面的阶段推送
            }
            var local = LocalOf(rel);
            var known = KnownVersion(rel);
            if (!File.Exists(LongPath.ToExtended(local)))
            {
                await DownloadAsync(entry, rel, ct).ConfigureAwait(false);
            }
            else if (entry.version > known)
            {
                // 远端更新:本地没改过就直接覆盖;本地也改过 → 冲突副本
                if (LocalLooksChanged(rel))
                {
                    var conflictPath = MakeConflictCopy(rel);
                    var conflictName = Path.GetFileName(conflictPath);
                    await DownloadAsync(entry, rel, ct,
                        (SyncState.Conflict, $"两端都改了;本地版本已保留为 {conflictName}")).ConfigureAwait(false);
                    Notice?.Invoke($"冲突:已保留本地副本 {conflictName}");
                }
                else
                {
                    await DownloadAsync(entry, rel, ct).ConfigureAwait(false);
                }
            }
        }

        Notice?.Invoke("对账:进入本地上传阶段");
        // ② 本地 → 远端(新文件 / 本地改动 / **本地改名**)
        foreach (var item in ScanLocal())
        {
            ct.ThrowIfCancellationRequested();
            var rel = item.RelativePath;
            var known = KnownVersion(rel);
            var remoteHas = remote.TryGetValue(rel, out var remoteEntry);

            // ②a 改名识别(必须在"当成新文件上传"之前做)。判据由 DetectLocalRenames
            // 预先算好:新路径没记过、远端也没有它、而它的本机文件身份对得上某个
            // **旧路径已消失**的已知条目 —— 那就是"用户把 a.txt 改成了 b.txt"。
            if (!remoteHas && renames.NewToOld.TryGetValue(rel, out var renamedFrom)
                && await TryRenameAsync(rel, renamedFrom, ct).ConfigureAwait(false))
            {
                continue;
            }

            if (remoteHas && !LocalLooksChanged(rel))
            {
                continue;
            }
            await UploadAsync(rel, remoteHas ? remoteEntry : null, ct).ConfigureAwait(false);
            _ = known;
            _ = remoteEntry;
        }

        // 传输统一在这里排空:入队是"计划",排空才是"执行完" ——
        // 排空之后状态才是可信的(否则 UI 会看到一堆 Pending 然后瞬间跳 InSync)。
        Notice?.Invoke("对账:排空队列…");
        // 排空**必须有上限**:一条卡住的传输不该让整轮对账(乃至整个同步)永远停摆 ——
        // 现象是用户只看到「上传中」永远不动,而日志里什么都没有。
        // 超时后如实报出「待处理/在跑」条数并继续(下一轮对账会再试)。
        using (var drainCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            drainCts.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                await _queue.DrainAsync(drainCts.Token).ConfigureAwait(false);
                Notice?.Invoke("对账:完成");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Notice?.Invoke($"对账:排空队列超时(待处理={_queue.PendingCount} 在跑={_queue.ActiveCount});下一轮再试");
            }
        }
    }

    // ---------------------------------------------------------------- 本地

    private IEnumerable<(string RelativePath, ScannedEntry Entry)> ScanLocal()
    {
        var scanner = new DirectoryScanner();
        foreach (var e in scanner.Scan(_config.SyncRoot, recursive: true))
        {
            if (e.IsDirectory)
            {
                continue;
            }
            var rel = Path.GetRelativePath(_config.SyncRoot, e.Path).Replace('\\', '/');
            if (rel.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            {
                continue; // 半成品不参与同步(下载中)
            }
            yield return (rel, e);
        }
    }

    private void OnRescanRequested(RescanRequest req)
    {
        // 监听器只告诉我们"哪里可能变了",真正的判定靠一次扫描 + 状态比对。
        // 之所以不直接在事件回调里做 IO:回调在 watcher 线程上,慢 IO 会丢事件。
        _localLoop ??= Task.Run(async () =>
        {
            try
            {
                await ReconcileAsync(_cts!.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 停止中,正常
            }
            catch (Exception ex)
            {
                Notice?.Invoke($"同步出错:{ex.Message}");
            }
            finally
            {
                _localLoop = null;
            }
        });
    }

    // ---------------------------------------------------------------- 远端

    private async Task RemoteLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var spaceId = await ResolveSpaceIdAsync(ct).ConfigureAwait(false);
                var stream = _sseFactory!(_tokens, spaceId);
                await foreach (var _ in stream.ReadAsync(ct).ConfigureAwait(false))
                {
                    // MVP:收到任何变更事件就重新对账(不做 seq 裁剪)
                    await ReconcileAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Notice?.Invoke($"远端事件流断开,稍后重连:{ex.Message}");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    // ---------------------------------------------------------------- 传输

    /// <summary>
    /// 下载一个远端条目到本地。
    ///
    /// <paramref name="finalState"/> 用于"下载完成后该显示成什么状态" —— 默认 InSync,
    /// 但**冲突**场景必须传 Conflict:那一轮里我们先做了冲突副本、再把远端版本拉回来,
    /// 如果这里照旧写 InSync,刚标好的「冲突」会被静默抹掉(实测:副本文件都在了,
    /// UI 上却显示「已同步」,用户完全不知道发生过冲突)。
    /// 注意入队时仍先标 PendingDownload —— 那一刻确实在下载,状态是诚实的。
    /// </summary>
    private Task DownloadAsync(EntryView entry, string rel, CancellationToken ct,
        (SyncState State, string Message)? finalState = null)
    {
        var local = LocalOf(rel);
        Upsert(rel, SyncState.PendingDownload, "", entry.version);
        _queue.Enqueue(new TransferJob
        {
            Id = "dl:" + entry.id,
            DisplayName = rel,
            Direction = TransferDirection.Download,
            TotalBytes = entry.size,
            Run = async (_, token) =>
            {
                var n = await _files.DownloadAsync(entry.id, local, token).ConfigureAwait(false);
                RecordState(entry, rel, local);
                if (finalState is { } f)
                {
                    Upsert(rel, f.State, f.Message, entry.version);
                }
                else
                {
                    Upsert(rel, SyncState.InSync, "", entry.version);
                }
                return n;
            },
        });
        // 队列是异步跑的:这里只入队,不阻塞对账;实际完成由 ReconcileAsync 末尾统一 Drain
        return Task.CompletedTask;
    }

    private async Task UploadAsync(string rel, EntryView? remoteEntry, CancellationToken ct)
    {
        var local = LocalOf(rel);
        var info = new FileInfo(LongPath.ToExtended(local));
        if (!info.Exists)
        {
            return;
        }
        Upsert(rel, SyncState.PendingUpload, "", KnownVersion(rel));
        // 记账:自己写盘导致的事件不该被当成"用户改动"再传一次(DE-D-10)
        _ledger.Record(rel, info.Length, info.LastWriteTimeUtc.Ticks);

        var spaceId = await ResolveSpaceIdAsync(ct).ConfigureAwait(false);
        var parentId = await EnsureParentDirAsync(rel, ct).ConfigureAwait(false);
        _queue.Enqueue(new TransferJob
        {
            Id = "ul:" + rel,
            DisplayName = rel,
            Direction = TransferDirection.Upload,
            TotalBytes = info.Length,
            Run = async (_, token) =>
            {
                EntryView after;
                if (remoteEntry is null)
                {
                    // 远端没有同名文件:TUS 建任务(分片 + 断点续传)
                    var res = await _files.UploadAsync(
                        spaceId, parentId, Path.GetFileName(local), local, null,
                        allowOverwrite: false, token)
                        .ConfigureAwait(false);
                    after = new EntryView
                    {
                        id = res.FileId,
                        name = Path.GetFileName(local),
                        is_dir = false,
                        size = info.Length,
                        version = res.Version,
                        etag = "",
                        updated_at = DateTimeOffset.UtcNow.ToString("O"),
                    };
                }
                else
                {
                    // 远端已有同名文件(本地改动要传回去):必须走**覆盖**。
                    // 用 TUS 建任务会被 409 name_conflict 拒掉(上传任务占名,ADR-5)——
                    // 实测就是这样,导致"改本地已有文件"永远同步不出去。
                    var repl = await _files.UploadAsync(
                        spaceId, parentId, Path.GetFileName(local), local, null,
                        allowOverwrite: true, token).ConfigureAwait(false);
                    after = await _files.GetEntryAsync(repl.FileId, token).ConfigureAwait(false);
                }
                SaveState(after, rel, local);
                Upsert(rel, SyncState.InSync, "", after.version);
                return info.Length;
            },
        });
    }

    /// <summary>确保远端父目录存在(MVP:按路径逐级创建)。</summary>
    private async Task<string?> EnsureParentDirAsync(string rel, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(rel.Replace('/', Path.DirectorySeparatorChar));
        if (string.IsNullOrEmpty(dir))
        {
            return string.IsNullOrEmpty(_config.ParentId) ? null : _config.ParentId;
        }
        var spaceId = await ResolveSpaceIdAsync(ct).ConfigureAwait(false);
        var parent = string.IsNullOrEmpty(_config.ParentId) ? null : _config.ParentId;
        var acc = "";
        foreach (var part in dir.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            acc = acc.Length == 0 ? part : acc + "/" + part;
            var existing = KnownFileId("dir:" + acc);
            if (existing is not null)
            {
                parent = existing;
                continue;
            }
            var created = await _files.CreateDirectoryAsync(spaceId, parent, part, ct).ConfigureAwait(false);
            SaveState(created, "dir:" + acc, LocalOf("dir:" + acc));
            parent = created.id;
        }
        return parent;
    }

    // ---------------------------------------------------------------- 状态

    private string LocalOf(string rel) =>
        Path.Combine(_config.SyncRoot, rel.Replace('/', Path.DirectorySeparatorChar));

    private async Task<string?> RelativePathOfAsync(EntryView e)
    {
        // 远端条目只有 id/name/parent_id;MVP 用"逐级向上拼名字"得到相对路径。
        if (string.IsNullOrEmpty(_config.ParentId) && e.parent_id is null)
        {
            return e.name; // 空间根的直接子项
        }
        var parts = new List<string> { e.name };
        var parent = e.parent_id;
        var guard = 0;
        // 停在**同步起点**就够:再往上就是空间根目录/配置里的父目录,它们的名字
        // 不属于"相对同步根的路径"(多带一层会让本地与远端路径永不相等,见 _rootId 注释)
        while (!string.IsNullOrEmpty(parent)
               && !string.Equals(parent, _rootId, StringComparison.Ordinal)
               && !string.Equals(parent, _config.ParentId, StringComparison.Ordinal)
               && guard++ < 64)
        {
            if (!_dirNameCache.TryGetValue(parent!, out var pname))
            {
                var pe = await _files.GetEntryAsync(parent!, CancellationToken.None).ConfigureAwait(false);
                pname = pe.name;
                _dirNameCache[parent!] = pname;
            }
            parts.Insert(0, pname);
            parent = (await _files.GetEntryAsync(parent!, CancellationToken.None).ConfigureAwait(false)).parent_id;
        }
        return string.Join('/', parts);
    }

    private string MakeConflictCopy(string rel)
    {
        var local = LocalOf(rel);
        // 这里只做"把本地版本改名保留"这一件本地操作:命名规则用 ClientCore 的
        // ConflictNaming(与桌面端冲突检查器、双端夹具同一套实现),避免自己在
        // 这里再编一套命名 —— 两套命名必然漂移,而漂移的表现是"同一冲突在不同
        // 端得到不同名字",排查时极难看出。
        var stamp = ConflictNaming.Timestamp(_clock.GetUtcNow());
        var conflictName = ConflictNaming.Suggest(Path.GetFileName(local), stamp);
        var target = Path.Combine(Path.GetDirectoryName(local)!, conflictName);
        File.Move(LongPath.ToExtended(local), LongPath.ToExtended(target), overwrite: true);
        return target;
    }

    /// <summary>读取某条路径当前的显示状态(没有则 null)。用于"冲突标记要粘住"的判断。</summary>
    private SyncState? StateOf(string rel)
    {
        lock (_gate)
        {
            var i = _status.FindIndex(s => string.Equals(s.RelativePath, rel, StringComparison.OrdinalIgnoreCase));
            return i >= 0 ? _status[i].State : null;
        }
    }

    private void Upsert(string rel, SyncState state, string message, long version)
    {
        lock (_gate)
        {
            var i = _status.FindIndex(s => string.Equals(s.RelativePath, rel, StringComparison.OrdinalIgnoreCase));
            var item = new SyncEntryStatus(rel, state, message, version);
            if (i >= 0)
            {
                _status[i] = item;
            }
            else
            {
                _status.Add(item);
            }
            StatusChanged?.Invoke(_status.ToArray());
        }
    }

    public async ValueTask DisposeAsync()
    {
        _running = false;
        if (_watcher is not null)
        {
            _watcher.RescanRequested -= OnRescanRequested;
            _watcher.Dispose();
        }
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            _cts.Dispose();
        }
        foreach (var t in new[] { _localLoop, _remoteLoop })
        {
            if (t is not null)
            {
                try
                {
                    await t.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 停止路径不抛
                }
            }
        }
        await _queue.DisposeAsync().ConfigureAwait(false);
        _store.Dispose();
    }

    // ---------------------------------------------------------------- SQLite 状态库

    private async Task<string> ResolveSpaceIdAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_config.SpaceId))
        {
            return _config.SpaceId;
        }
        var list = await _files.ListSpacesAsync(ct).ConfigureAwait(false);
        var personal = list.spaces.FirstOrDefault(s => s.kind == "personal") ?? list.spaces.FirstOrDefault()
            ?? throw new InvalidOperationException("账号下没有任何空间");
        _config.SpaceId = personal.id;
        _config.Save();
        return personal.id;
    }

    private long KnownVersion(string rel)
    {
        var v = _store.Scalar(
            "SELECT remote_version FROM sync_state WHERE local_path=$p", ("p", rel));
        return v is null or DBNull ? 0 : Convert.ToInt64(v);
    }

    private string? KnownFileId(string rel)
    {
        var v = _store.Scalar("SELECT file_id FROM sync_state WHERE local_path=$p", ("p", rel));
        return v is null or DBNull ? null : Convert.ToString(v);
    }

    private bool LocalLooksChanged(string rel)
    {
        var local = LocalOf(rel);
        if (!File.Exists(LongPath.ToExtended(local)))
        {
            return false;
        }
        var info = new FileInfo(LongPath.ToExtended(local));

        // ⚠ **先问账本**:对账自己会写盘(下载落盘),watcher 会把这些写也报成"变了"。
        // 不销账的后果不是"多传一次",而是**自激循环**:写盘 → 事件 → 对账 → 又写盘 …
        // 实测:正是这个循环让对账永不结束(整个端到端检查器挂死)。
        // 账本出问题**不能**把整轮对账打死:它的作用是"少传一次",而不是数据安全本身 ——
        // 但它也**不能静默**:吞掉异常会让"账本一直坏着"这件事没人知道。所以降级为
        // "可能多传一次(幂等)"并把原因报到状态列表/通知里。
        try
        {
            if (_ledger.TryConsume(rel, info.Length, info.LastWriteTimeUtc.Ticks) == ExpectedChangeVerdict.Consumed)
            {
                return false;
            }
        }
        catch (Exception ex)
        {
            Notice?.Invoke($"期望变更账本异常(已降级为「可能重复上传一次」):{ex.GetType().Name}: {ex.Message}");
        }

        var size = _store.Scalar("SELECT size FROM sync_state WHERE local_path=$p", ("p", rel));
        var mtime = _store.Scalar("SELECT local_mtime_ticks FROM sync_state WHERE local_path=$p", ("p", rel));
        if (size is null or DBNull)
        {
            return true; // 从没记录过 → 视为本地新文件
        }
        return Convert.ToInt64(size) != info.Length ||
               Convert.ToInt64(mtime) != info.LastWriteTimeUtc.Ticks;
    }

    private void RecordState(EntryView e, string rel, string local)
    {
        var info = new FileInfo(LongPath.ToExtended(local));
        SaveState(e, rel, local);
        _ledger.Record(rel, info.Length, info.LastWriteTimeUtc.Ticks);
    }

    // ---------------------------------------------------------------- 改名

    /// <summary>
    /// 本轮对账里"本地改名"的映射(纯本地计算,不联网)。
    /// <paramref name="NewToOld"/>:新路径 → 旧路径(推送改名用);
    /// <paramref name="OldPaths"/>:被改名带走的旧路径集合(下载阶段要跳过它们)。
    ///
    /// 判据:新路径我们**从没记过**,而它的**本机文件身份**(卷序列号 + FileId,
    /// DE-D-11)对得上某个已知条目,且那个条目的旧路径**已经不存在**了。
    /// 改名后名字已经变了,唯一不变的就是文件身份 —— 这也是 DE-D-11 存在的理由。
    ///
    /// 任何不确定的情形都不进映射(= 走原来的"当新文件上传"):宁可贵一次上传,
    /// 也不能因为猜错把用户的文件搬走。身份拿不到(NTFS 之外的盘/权限)时同理。
    /// </summary>
    private (Dictionary<string, string> NewToOld, HashSet<string> OldPaths) DetectLocalRenames()
    {
        var newToOld = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var oldPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (rel, _) in ScanLocal())
        {
            if (KnownFileId(rel) is not null)
            {
                continue; // 这个路径我们认识 → 不是改名(是常规改动)
            }
            var idn = IdentityOf(LocalOf(rel));
            if (idn is null)
            {
                continue;
            }
            var old = _store.Scalar(
                "SELECT local_path FROM sync_state WHERE local_identity=$id LIMIT 1",
                ("id", FormatIdentity(idn.Value)));
            if (old is null or DBNull)
            {
                continue;
            }
            var oldRel = Convert.ToString(old)!;
            if (string.Equals(oldRel, rel, StringComparison.OrdinalIgnoreCase))
            {
                continue; // 路径没变(第一次见到它)→ 常规上传
            }
            if (File.Exists(LongPath.ToExtended(LocalOf(oldRel))))
            {
                continue; // 旧路径还在:这不是改名(更像复制),让两边各自正常同步
            }
            if (KnownFileId(oldRel) is null)
            {
                continue; // 旧路径本来就没有远端条目 → 没有"改名"可推
            }
            newToOld[rel] = oldRel;
            oldPaths.Add(oldRel);
        }
        return (newToOld, oldPaths);
    }

    /// <summary>
    /// 推送一次改名:把远端已存在的条目按 <paramref name="oldRel"/> 对应的 file_id
    /// 原地改成 <paramref name="rel"/> 的名字。返回 true 表示"已处理,别再上传"。
    /// </summary>
    private async Task<bool> TryRenameAsync(string rel, string oldRel, CancellationToken ct)
    {
        var local = LocalOf(rel);
        var fileId = KnownFileId(oldRel);
        if (string.IsNullOrEmpty(fileId))
        {
            return false;
        }

        try
        {
            Upsert(rel, SyncState.PendingUpload, $"识别为改名({oldRel} → {rel})", KnownVersion(oldRel));
            var after = await _files.RenameAsync(fileId!, Path.GetFileName(local), KnownVersion(oldRel), ct)
                .ConfigureAwait(false);
            // 状态库一行以 file_id 为主键 → 改 local_path 即可,**不产生第二行**,
            // 远端 file_id / 版本号都保持连续(这正是"改名不重传"的意义)。
            SaveState(after, rel, local);
            _store.Execute("DELETE FROM sync_state WHERE local_path=$p AND file_id<>$id",
                ("p", oldRel), ("id", fileId!));
            Upsert(rel, SyncState.InSync, $"已改名(原 {oldRel})", after.version);
            Notice?.Invoke($"改名:{oldRel} → {rel}(未重传)");
            return true;
        }
        catch (Exception ex)
        {
            // 改名失败**不能**吞:吞掉的话本地会一直停在"没有对应远端条目"的状态,
            // 而用户以为改完了。这里如实报出并降级为普通上传(数据安全优先 —— 宁可多传一份,
            // 也不能让文件停在两个地方都不对的状态)。
            Notice?.Invoke($"改名失败({oldRel} → {rel}),将按新文件上传:{ex.Message}");
            return false;
        }
    }

    private FileIdentity? IdentityOf(string local)
    {
        try
        {
            return File.Exists(LongPath.ToExtended(local)) ? _identity.TryGet(local) : null;
        }
        catch (Exception)
        {
            // 身份拿不到(非 NTFS/网络盘/权限)→ 退化成"没有改名识别",不影响正确性
            return null;
        }
    }

    private static string FormatIdentity(in FileIdentity id) =>
        $"{id.VolumeSerial}:{id.FileIdHigh}:{id.FileIdLow}";

    // ---------------------------------------------------------------- 删除传播

    /// <summary>
    /// **双向删除传播**(2026-09-13 产品决策:删除要双向传播、一致优先、**两端都不弹确认**)。
    ///
    /// 返回本轮两端删除的文件数。
    ///
    /// 安全性不靠"问用户",而靠**信号完整性**——凡是"看不见"都可能只是"读不到",
    /// 而服务端删除是**硬删、无回收站**,误判一次就是真删:
    ///   ① 本机扫描**截断**时整轮不删(`DirectoryScanner.MaxEntries` 超限是静默 break);
    ///   ② 同步根不可达(盘符变化/网络盘掉线)时整轮不删;
    ///   ③ 待删文件所在目录**不可枚举**时不删那一个(`IgnoreInaccessible = true` 会静默跳过读不了的目录,
    ///      于是"权限抖动"看起来和"文件被删了"一模一样);
    ///   ④ 删除**一律按 file_id**(`FileApi.DeleteAsync` 的注释说明了为什么不能按名字)。
    /// 远端侧不需要额外放宽:`WalkAsync` 遇到错误会抛异常而不是返回短列表,所以"列完了"本身可信。
    /// </summary>
    private async Task<int> PropagateDeletionsAsync(
        string spaceId,
        Dictionary<string, EntryView> remote,
        (Dictionary<string, string> NewToOld, HashSet<string> OldPaths) renames,
        CancellationToken ct)
    {
        // ---- 信号完整性(本地)----
        if (!Directory.Exists(LongPath.ToExtended(_config.SyncRoot)))
        {
            Notice?.Invoke("删除传播:本轮跳过 —— 同步根不存在(盘符变化/网络盘掉线),不能据此认定删除");
            return 0;
        }
        // **同步根身份保护**(最外层,也是最关键的一道):
        // 同步根被重建(不存在时 StartAsync 会自动建一个空目录)、或盘符/挂载点换了地方时,
        // 本地文件会"全部消失" —— 那与"用户把文件删光了"在扫描结果上一模一样。
        // 身份(卷序列号 + 根目录 FileId)能区分:重建/换盘 ⇒ 新的 FileId。
        // 身份变了就**整轮不传播删除**,并如实告诉用户该怎么办。
        if (!RootIdentityUnchanged())
        {
            return 0;
        }
        var scanner = new DirectoryScanner();
        var raw = scanner.Scan(_config.SyncRoot, recursive: true);
        if (raw.Count >= scanner.MaxEntries)
        {
            Notice?.Invoke($"删除传播:本轮跳过 —— 本机文件数达到扫描上限 {scanner.MaxEntries},扫描被**截断**,不能据此认定删除");
            return 0;
        }
        var localFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in raw)
        {
            if (e.IsDirectory)
            {
                continue;
            }
            var rel = Path.GetRelativePath(_config.SyncRoot, e.Path).Replace('\\', '/');
            if (rel.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            localFiles.Add(rel);
        }

        var deleted = 0;

        // ---- ① 远端已无 → 删本地 ----
        foreach (var (fileId, rel, _) in KnownRows())
        {
            ct.ThrowIfCancellationRequested();
            _ = fileId;
            if (remote.ContainsKey(rel) || renames.OldPaths.Contains(rel))
            {
                continue;
            }
            var local = LocalOf(rel);
            if (!File.Exists(LongPath.ToExtended(local)))
            {
                RemoveStateRow(rel); // 两端都没有:清掉状态行,这不是一次"删除动作"
                continue;
            }
            if (!ParentEnumerable(local))
            {
                Notice?.Invoke($"删除传播:跳过 {rel} —— 所在目录当前不可枚举(权限/网络问题,不是删除)");
                continue;
            }
            try
            {
                File.Delete(LongPath.ToExtended(local));
                RemoveStateRow(rel);
                RemoveStatus(rel);
                deleted++;
                Notice?.Invoke($"删除:远端已删除 {rel},本地副本已同步删除");
            }
            catch (Exception ex)
            {
                Notice?.Invoke($"删除本地副本失败 {rel}:{ex.Message}(下一轮再试)");
            }
        }

        // ---- ② 本地已无 → 删远端 ----
        // 重新取一次状态行:①可能刚删掉若干行。
        //
        // 两条必须排除的情况(都会踩坏别的功能):
        //   · `renames.OldPaths.Contains(rel)`:这一行是**本地改名的旧路径** —— 不排除的话
        //     改名会被我们拆成"删远端 + 传新文件"(改名复用的成果当场作废);
        //   · 远端版本**比我们已知的新**:两端都变了(本地删、远端改),没有 tombstone 时
        //     无法裁决成"用户预期"的那个。这里选**远端更新优先**(下载回来),
        //     因为"删掉别人刚写的数据"是两种错误里更不可逆的那个;用户看到文件回来了可以再删一次。
        var rows = KnownRows();
        var candidates = new List<(string FileId, string Rel)>();
        foreach (var (fileId, rel, knownVersion) in rows)
        {
            ct.ThrowIfCancellationRequested();
            if (localFiles.Contains(rel) || renames.NewToOld.ContainsKey(rel) || renames.OldPaths.Contains(rel))
            {
                continue;
            }
            if (remote.TryGetValue(rel, out var remoteEntry) && remoteEntry.version > knownVersion)
            {
                continue; // 远端也改了:让下载阶段把它拉回来(记在文档里)
            }
            if (!ParentEnumerable(LocalOf(rel)))
            {
                Notice?.Invoke($"删除传播:跳过 {rel} —— 所在目录当前不可枚举(权限/网络问题,不是删除)");
                continue;
            }
            candidates.Add((fileId, rel));
        }

        // 说明:**不**给"一次消失一大片"加延迟确认。原因有二:
        //   ① 产品决策是"一致优先、无条件"——删 100 个文件和删 1 个文件同等对待;
        //   ② 试过之后发现那条路自相矛盾:本轮"只标记不删",下载阶段又会把远端还在的文件
        //      拉回本地(本地文件"回来了"),下一轮便不再有候选 —— 延迟确认永远无法落地。
        // 真正的保护落在**同步根身份**(RootIdentityUnchanged):换盘/根被重建会得到新的 FileId,
        // 那种情况下整轮不删。这是实测出来的结论(见检查器 ⑦ 的"硬保护"一节)。
        foreach (var (fileId, rel) in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await _files.DeleteAsync(fileId, ct).ConfigureAwait(false);
                RemoveStateRow(rel);
                RemoveStatus(rel);
                // **快照也要跟着更新**:`remote` 是本轮开头列的,里面还有这一条;
                // 不删掉的话随后的下载阶段会把我们刚删掉的远端文件**再下载回来**。
                remote.Remove(rel);
                deleted++;
                Notice?.Invoke($"删除:本地已删除 {rel},远端已同步删除");
            }
            catch (Exception ex)
            {
                // 404 = 远端已经没有了(别人先删了、或别人删了又建了同名新文件):
                // 那不是错误,清掉状态行即可。
                // ⚠ **不要动 `remote` 快照**:名字现在可能已经属于**另一端刚建的新文件**
                // (新的 file id)—— 把快照里那条抹掉,轮内就不会再把它当新文件下载下来。
                if (ex is ApiException api && api.Status == System.Net.HttpStatusCode.NotFound)
                {
                    RemoveStateRow(rel);
                    RemoveStatus(rel);
                    continue;
                }
                Notice?.Invoke($"删除远端失败 {rel}:{ex.Message}(下一轮再试)");
            }
        }

        return deleted;
    }

    /// <summary>本空间的同步状态行(file_id + 本地相对路径 + 已知远端版本)。</summary>
    private List<(string FileId, string LocalPath, long RemoteVersion)> KnownRows()
    {
        var list = new List<(string, string, long)>();
        using var cmd = _store.Connection.CreateCommand();
        cmd.CommandText = "SELECT file_id, local_path, remote_version FROM sync_state WHERE space_id=$s";
        cmd.Parameters.AddWithValue("$s", _config.SpaceId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
        }
        return list;
    }

    /// <summary>sync_meta 里记"这个状态库属于哪个同步根"。</summary>
    private const string RootIdentityKey = "root_identity";

    /// <summary>
    /// 取**同步根目录**的身份。
    ///
    /// 注意不能复用 <see cref="IdentityOf"/>:那个用 `File.Exists` 判存在,对**目录**永远返回 null
    /// (实测踩到:根身份保护因此静默失效,空目录照样把远端删了)。
    /// </summary>
    private FileIdentity? RootIdentity()
    {
        try
        {
            var ext = LongPath.ToExtended(_config.SyncRoot);
            return Directory.Exists(ext) ? _identity.TryGet(_config.SyncRoot) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 同步根身份是否**没变**。
    ///
    /// 首次调用(库里没记)会把当前身份写下来并返回 true;之后若不一致就返回 false
    /// (调用方据此放弃删除传播)。身份取不到(网络盘/权限)→ 返回 true 但**说明保护失效** ——
    /// 宁可不挡,也不能因为取不到身份就把正常删除永远堵死(那是"不能判定 ≠ 通过"的镜像:
    /// 这里选择"取不到就不挡",并把这一点如实写在文档里)。
    /// </summary>
    private bool RootIdentityUnchanged()
    {
        var idn = RootIdentity();
        if (idn is null)
        {
            return true; // 取不到身份:这道保护不生效(已记入文档)
        }
        var current = FormatIdentity(idn.Value);
        var recorded = _store.Scalar("SELECT value FROM sync_meta WHERE key=$k", ("k", RootIdentityKey));
        if (recorded is null or DBNull || string.IsNullOrEmpty(Convert.ToString(recorded)))
        {
            _store.Execute("INSERT INTO sync_meta(key, value) VALUES($k, $v) " +
                           "ON CONFLICT(key) DO UPDATE SET value=$v",
                ("k", RootIdentityKey), ("v", current));
            return true;
        }
        if (Convert.ToString(recorded) == current)
        {
            return true;
        }
        Notice?.Invoke("删除传播:本轮跳过 —— **同步根身份变了**(盘符/挂载点变化,或根目录被重建):" +
                       "本地看起来「什么都没有」,但这不等于用户删了文件。请确认同步目录是否还在原位置;" +
                       "若确实换了位置,删掉状态库(%APPDATA%\\NetDisk\\state.db)后重新登录即可继续同步");
        return false;
    }

    private void RemoveStateRow(string rel) =>
        _store.Execute("DELETE FROM sync_state WHERE local_path=$p", ("p", rel));

    /// <summary>从状态列表里去掉一条(文件在两端都不存在了,列表只显示存在的东西)。</summary>
    private void RemoveStatus(string rel)
    {
        lock (_gate)
        {
            var i = _status.FindIndex(s => string.Equals(s.RelativePath, rel, StringComparison.OrdinalIgnoreCase));
            if (i >= 0)
            {
                _status.RemoveAt(i);
                StatusChanged?.Invoke(_status.ToArray());
            }
        }
    }

    /// <summary>
    /// 该路径所在目录**当前是否可枚举**。
    ///
    /// 这是"看不见 ≠ 被删除"的关键防线:扫描器把读不了的目录**静默跳过**
    /// (`IgnoreInaccessible = true`),于是权限抖动、ACL 变更、网络盘卡顿在扫描结果里
    /// 与"用户删了这些文件"完全一样。要删之前先问一句"这个目录现在还读得到吗";
    /// 读不到就不删(下一轮再说)。父目录本身不存在时(用户删了整个目录)也返回 false,
    /// 交给下一轮(那时若同步根仍可达、父目录仍未出现,会走同一条路径,行为一致且不误删)。
    /// </summary>
    private static bool ParentEnumerable(string localPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(localPath);
            if (string.IsNullOrEmpty(dir))
            {
                return true;
            }
            var ext = LongPath.ToExtended(dir);
            if (!Directory.Exists(ext))
            {
                return false; // 目录本身也没了:本轮不删,下一轮再判(宁可慢一轮,不可误删)
            }
            using var e = Directory.EnumerateFileSystemEntries(ext).GetEnumerator();
            return e.MoveNext() || true; // 只要"能开始枚举"就算可读(空目录也算可读)
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void SaveState(EntryView e, string rel, string local)
    {
        long mtime = 0;
        if (File.Exists(LongPath.ToExtended(local)))
        {
            mtime = new FileInfo(LongPath.ToExtended(local)).LastWriteTimeUtc.Ticks;
        }
        // 本机文件身份**必须在这里落库**:改名之后旧路径就没了,没法再回溯查它的身份。
        var idn = IdentityOf(local);
        var identity = idn is null ? "" : FormatIdentity(idn.Value);
        _store.Execute(
            """
            INSERT INTO sync_state(file_id, space_id, local_path, remote_version, local_mtime_ticks, size, hash_sha256, state, updated_at_utc, local_identity)
            VALUES ($id, $space, $p, $ver, $mtime, $size, $hash, 'InSync', $now, $idn)
            ON CONFLICT(file_id) DO UPDATE SET
              local_path=$p, remote_version=$ver, local_mtime_ticks=$mtime, size=$size,
              hash_sha256=$hash, state='InSync', updated_at_utc=$now,
              local_identity=CASE WHEN $idn='' THEN local_identity ELSE $idn END
            """,
            ("id", e.id), ("space", _config.SpaceId), ("p", rel), ("ver", e.version),
            ("mtime", mtime), ("size", e.size), ("hash", e.hash_sha256 ?? ""),
            ("now", _clock.GetUtcNow().ToString("O")), ("idn", identity));
    }
}
