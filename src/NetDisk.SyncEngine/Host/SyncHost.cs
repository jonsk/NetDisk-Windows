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
public sealed record SyncEntryStatus(string RelativePath, SyncState State, string Message, long Version)
{
    /// <summary>
    /// 当前这个文件传到多少了(0-100;null = 此刻没有正在进行的传输)。
    ///
    /// 为什么要它:状态文字只能说"上传中",而用户真正想知道的是"还要多久" ——
    /// 大文件(几 GB)在 UI 上长时间只显示"上传中",和卡住无法区分。
    /// 进度来自 <see cref="TransferQueue.Progress"/> 事件(按阈值节流,不是每片都上报),
    /// 所以它是"尽力而为"的显示值:丢了某一档不影响正确性,最终一定回到 null(传输结束)。
    /// </summary>
    public double? ProgressPercent { get; init; }
}

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
        _queue.Progress += OnQueueProgress; // 界面上的"传到多少了"来自这里
        _queue.Failed += OnQueueFailed;     // 失败必须有痕迹:状态 + 日志(见 OnQueueFailed)
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

    /// <summary>
    /// **等在飞的传输跑完**(返回 true = 已排空;false = 超时)。
    ///
    /// 为什么要单独暴露它:自动更新必须在"迁移状态库 + 换 exe"之前确认没有传输在飞 ——
    /// 硬杀掉一个正在上传的任务会留下半截暂存文件与"已预留但没结算"的配额(服务端要等回收班车)。
    /// 语义是**等**而不是清:`UpdateOrchestrator` 拿到 false 时**放弃本次升级**(而不是杀任务)。
    /// </summary>
    public async Task<bool> DrainTransfersAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await _queue.DrainAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Notice?.Invoke($"排空传输超时(待处理={_queue.PendingCount} 在跑={_queue.ActiveCount})");
            return false;
        }
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
        // 远端**目录**集合(相对路径)。目录也要参与同步:用户删掉一个文件夹时,
        // 服务端删"目录"就是删整棵子树(一次调用),比逐个文件删少 N 次请求。
        var remoteDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                // 远端目录 → 本地:只有"我们没见过它"或"远端这个目录换了身份"时才落地。
                //
                // 关键区别(实测定下来的):状态行 `dir:<rel>` 就是"这个本地路径**曾经**
                // 与哪个远端目录对应"的凭据。
                //   · 没有状态行 → 远端多出来的新目录(另一端建的、或首次同步)→ 落地 ✓
                //   · 有状态行、id 也没变,但本地目录不在了 → **是用户把它删了**,
                //     绝不能在这里 CreateDirectory 把它当场复活 —— 复活之后本轮扫描
                //     自然"看得见"这个目录,删除传播永远等不到"本地已无",用户删掉的
                //     文件夹就永远传不到远端(真机 ⑫ 实测:远端目录一直留着)。
                //   · 有状态行但 id 变了(远端删了又建同名 = **新目录**)→ 当新目录落地,
                //     与文件侧"复活的一律当新文件"一致;否则会把别人新建的目录当旧目录删掉。
                var knownDirId = KnownFileId("dir:" + rel);
                if (!string.Equals(knownDirId, e.id, StringComparison.Ordinal))
                {
                    Directory.CreateDirectory(LongPath.ToExtended(LocalOf(rel)));
                    if (knownDirId is not null)
                    {
                        // 旧身份的行必须先删:`sync_state` 主键是 file_id、local_path 上没有唯一约束,
                        // 两行同路径时 `KnownFileId` 查出哪一条是不确定的。
                        RemoveStateRow("dir:" + rel);
                    }
                    SaveDirState(rel, e.id);
                }
                remoteDirs.Add(rel);
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

        // 本地扫描**只做一次**:目录与文件两处都要用(两次扫描不只浪费,
        // 还可能出现"两次结果不一致"这种自找的麻烦)。
        var scan = TakeLocalScan();

        // **目录的新建与删除传播**(2026-09-13)。
        // 放在文件删除之前:本地删掉一个目录时,服务端删**目录**就是删整棵子树(一次调用),
        // 比"逐个文件删"少 N 次请求,也不会在中间态留下空目录。
        var dirOps = await SyncDirectoriesAsync(spaceId, remote, remoteDirs, renames, scan, ct)
            .ConfigureAwait(false);
        if (dirOps > 0)
        {
            Notice?.Invoke($"目录同步:本轮处理 {dirOps} 个目录(新建/删除)");
        }

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
        //
        // ⚠ **只读浏览(仅结构)模式下一律不传播删除**(见 ClientConfig.StructureOnly):
        // 那个模式的语义是"看看结构",而删除是**不可逆**的写操作(服务端硬删、无回收站),
        // 让一个"只读浏览"把远端文件删掉,是这个模式最不该发生的事。
        var deletions = _config.StructureOnly
            ? 0
            : await PropagateDeletionsAsync(spaceId, remote, renames, scan, ct).ConfigureAwait(false);
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
            if (_config.StructureOnly)
            {
                // 只读浏览:文件**不落盘**,但要在状态列表里如实出现(名字/大小/远端版本),
                // 否则用户看到的是一个"目录里什么都没有"的假象。
                Upsert(rel, SyncState.StructureOnly, "只读浏览:仅同步目录结构,未下载内容", entry.version);
                continue;
            }
            var local = LocalOf(rel);
            var known = KnownVersion(rel);
            if (!File.Exists(LongPath.ToExtended(local)))
            {
                await DownloadAsync(entry, rel, ct).ConfigureAwait(false);
            }
            else if (entry.version > known)
            {
                // 远端更新:本地没改过就直接覆盖;本地也改过 → 冲突
                if (LocalLooksChanged(rel))
                {
                    // 用户在设置里预先定了"冲突怎么办"就直接办(无人值守);默认"都保留"。
                    switch (ConflictPolicyOf(_config.OnConflict))
                    {
                        case ConflictPolicy.KeepRemote:
                            // 以远端为准:本地改动**被丢弃**(用户在设置里明确选的)
                            Notice?.Invoke($"冲突:{rel} 按设置「以远端为准」覆盖本地(本地改动不再保留副本)");
                            await DownloadAsync(entry, rel, ct,
                                (SyncState.InSync, "冲突:按设置「以远端为准」覆盖了本地")).ConfigureAwait(false);
                            break;
                        case ConflictPolicy.KeepLocal:
                            // 以本地为准:把本地那一版原地覆盖到远端(同一 file_id,版本 +1)
                            Notice?.Invoke($"冲突:{rel} 按设置「以本地为准」覆盖远端(远端那一版不再保留副本)");
                            await UploadAsync(rel, entry, ct).ConfigureAwait(false);
                            break;
                        default:
                            var conflictPath = MakeConflictCopy(rel);
                            var conflictName = Path.GetFileName(conflictPath);
                            // 记住"哪个本地副本对应这条路径":界面上的"以本地/以远端为准"要用它。
                            // 只活在本次运行内 —— 与"冲突徽标不跨重启"这条既有边界一致(见文档)。
                            _conflictCopies[rel] = conflictPath;
                            await DownloadAsync(entry, rel, ct,
                                (SyncState.Conflict, $"两端都改了;本地版本已保留为 {conflictName}")).ConfigureAwait(false);
                            Notice?.Invoke($"冲突:已保留本地副本 {conflictName}");
                            break;
                    }
                }
                else
                {
                    await DownloadAsync(entry, rel, ct).ConfigureAwait(false);
                }
            }
        }

        Notice?.Invoke("对账:进入本地上传阶段");
        // ② 本地 → 远端(新文件 / 本地改动 / **本地改名**)
        //
        // ⚠ **只读浏览(仅结构)模式:一个字节都不上传**(见 ClientConfig.StructureOnly)。
        // 这条规则同样不能只写在界面上:模式的语义是"只看远端结构",而上传会**改远端**。
        // 本地多出来的文件要如实显示成「仅结构:未上传」,不能假装它们已经在远端了。
        foreach (var item in ScanLocal())
        {
            ct.ThrowIfCancellationRequested();
            var rel = item.RelativePath;
            var known = KnownVersion(rel);
            var remoteHas = remote.TryGetValue(rel, out var remoteEntry);

            if (_config.StructureOnly)
            {
                if (!remoteHas)
                {
                    Upsert(rel, SyncState.StructureOnly, "只读浏览:仅同步目录结构,本地文件未上传(仅结构模式)", 0);
                }
                continue;
            }

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
        _jobRel["dl:" + entry.id] = rel; // 进度事件只带 job id,这里先建立映射
        _queue.Enqueue(new TransferJob
        {
            Id = "dl:" + entry.id,
            DisplayName = rel,
            Direction = TransferDirection.Download,
            TotalBytes = entry.size,
            Run = async (progress, token) =>
            {
                // **把进度接上**:下载侧此前没有进度参数,上传侧传的是 null ——
                // 于是界面上"上传中/下载中"永远只是一个词,大文件与卡住无法区分(实测发现)。
                var n = await _files.DownloadAsync(entry.id, local, progress, token).ConfigureAwait(false);
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
        _jobRel["ul:" + rel] = rel; // 进度事件只带 job id,这里先建立映射
        _queue.Enqueue(new TransferJob
        {
            Id = "ul:" + rel,
            DisplayName = rel,
            Direction = TransferDirection.Upload,
            TotalBytes = info.Length,
            Run = async (progress, token) =>
            {
                EntryView after;
                if (remoteEntry is null)
                {
                    // 远端没有同名文件:TUS 建任务(分片 + 断点续传)
                    var res = await _files.UploadAsync(
                        spaceId, parentId, Path.GetFileName(local), local, progress,
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
                        spaceId, parentId, Path.GetFileName(local), local, progress,
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

    // ---------------------------------------------------------------- 冲突解决

    /// <summary>冲突策略(设置里的 `on_conflict`;默认都保留)。</summary>
    private static ConflictPolicy ConflictPolicyOf(string? raw) => raw switch
    {
        "keep_remote" => ConflictPolicy.KeepRemote,
        "keep_local" => ConflictPolicy.KeepLocal,
        _ => ConflictPolicy.KeepBoth, // 未知值一律落到默认,而不是"没有策略"
    };

    /// <summary>
    /// 本次运行里"哪条路径的本地那一版被保留成了哪个副本文件"(`MakeConflictCopy` 时记下)。
    ///
    /// 为什么只在内存里:冲突**徽标**本来就不跨重启(既有边界),而副本文件本身就是用户的数据 ——
    /// 重启后副本还在磁盘上,只是界面不再知道"它对应哪条冲突"。把这条边界写在文档里,
    /// 比"重启后按钮点了没反应"要好。
    /// </summary>
    private readonly Dictionary<string, string> _conflictCopies = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>这条路径当前有没有"可解决的冲突"(即本次运行内还记得它的本地副本)。</summary>
    public bool CanResolveConflict(string rel)
    {
        lock (_conflictCopies)
        {
            return _conflictCopies.ContainsKey(rel) && File.Exists(LongPath.ToExtended(_conflictCopies[rel]));
        }
    }

    /// <summary>
    /// **逐文件解决冲突**(界面上的「以本地为准 / 以远端为准 / 都保留」)。
    ///
    /// 冲突发生后的磁盘状态:原路径 `rel` 上是**远端那一版**,本地那一版被另存成了副本文件。
    /// 三个选择的动作因此是:
    ///   · <see cref="ConflictResolution.KeepLocal"/>:把副本内容写回原路径,再**原地覆盖**上传
    ///     (同一 file_id、版本 +1),然后删掉副本(它已经被应用了);
    ///   · <see cref="ConflictResolution.KeepRemote"/>:远端那一版已经在原路径上,直接**删掉副本**
    ///     (用户明确选了"以远端为准"= 放弃本地改动),并清掉冲突标记;
    ///   · <see cref="ConflictResolution.KeepBoth"/>:副本留着,由对账把它当**新文件**传上去
    ///     (两份都在),清掉冲突标记。
    ///
    /// 返回 false 表示**解决不了**(例如冲突来自上一次运行:这次运行不知道副本是哪一个)——
    /// 调用方应如实告诉用户,而不是把条目显示成"已解决"。
    /// </summary>
    public async Task<bool> ResolveConflictAsync(
        string rel, ConflictResolution choice, CancellationToken ct = default)
    {
        string copyPath;
        lock (_conflictCopies)
        {
            if (!_conflictCopies.TryGetValue(rel, out var recorded) || !File.Exists(LongPath.ToExtended(recorded)))
            {
                Notice?.Invoke($"冲突:{rel} 没有可用的本地副本记录(可能来自上一次运行)——请手动处理这两个文件");
                return false;
            }
            copyPath = recorded;
        }

        try
        {
            var local = LocalOf(rel);
            switch (choice)
            {
                case ConflictResolution.KeepLocal:
                    // 副本 → 原路径,然后**原地覆盖**远端(必须带远端条目,否则会被 409 拒掉)
                    File.Copy(LongPath.ToExtended(copyPath), LongPath.ToExtended(local), overwrite: true);
                    var spaceId = await ResolveSpaceIdAsync(ct).ConfigureAwait(false);
                    var remoteEntry = await _files.FindByRelativePathAsync(spaceId, rel, ct).ConfigureAwait(false);
                    await UploadAsync(rel, remoteEntry, ct).ConfigureAwait(false);
                    File.Delete(LongPath.ToExtended(copyPath));
                    Upsert(rel, SyncState.PendingUpload, "冲突已按「以本地为准」处理:正在覆盖远端", KnownVersion(rel));
                    Notice?.Invoke($"冲突:{rel} 已按「以本地为准」覆盖远端,本地副本已合并删除");
                    break;

                case ConflictResolution.KeepRemote:
                    // 远端那一版已经在 rel 上;删掉副本 = 放弃本地改动(用户明确选的)
                    File.Delete(LongPath.ToExtended(copyPath));
                    Upsert(rel, SyncState.InSync, "冲突已按「以远端为准」处理(本地改动已放弃)", KnownVersion(rel));
                    Notice?.Invoke($"冲突:{rel} 已按「以远端为准」处理,本地副本已删除");
                    break;

                default: // KeepBoth
                    // 副本留着,交给下一轮对账当新文件上传(两份都保留)
                    Upsert(rel, SyncState.InSync, "冲突已按「都保留」处理(本地那一版作为副本上传)", KnownVersion(rel));
                    Notice?.Invoke($"冲突:{rel} 两份都保留:副本 {Path.GetFileName(copyPath)} 会作为新文件上传");
                    break;
            }

            lock (_conflictCopies)
            {
                _conflictCopies.Remove(rel);
            }
            // 解决完立即把结果推给两端(不需要等下一轮 5 分钟的对账)
            await ReconcileAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Upsert(rel, SyncState.Conflict, $"冲突解决失败:{ex.Message}", KnownVersion(rel));
            Notice?.Invoke($"冲突解决失败 {rel}:{ex.Message}");
            return false;
        }
    }

    private void Upsert(string rel, SyncState state, string message, long version, double? progress = null)
    {
        lock (_gate)
        {
            var i = _status.FindIndex(s => string.Equals(s.RelativePath, rel, StringComparison.OrdinalIgnoreCase));
            var item = new SyncEntryStatus(rel, state, message, version) { ProgressPercent = progress };
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

    /// <summary>任务 id → 相对路径(进度事件只带 job id,而界面按路径显示)。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _jobRel =
        new(StringComparer.Ordinal);

    /// <summary>
    /// 把传输进度映射成"这个文件传到多少了"。
    ///
    /// 注意三件事:
    ///   · 进度事件在**后台线程**上触发(队列的工作线程),所以只更新状态列表、不碰 UI;
    ///   · 传输结束时队列会再发一条 `Completed`,那时把进度清成 null(否则大文件传完还挂着 87%);
    ///   · 查不到 rel(任务已被替换/已结束)就忽略 —— 这是节流事件的正常情况,不是错误。
    /// </summary>
    private void OnQueueProgress(TransferProgress p)
    {
        if (!_jobRel.TryGetValue(p.JobId, out var rel))
        {
            return;
        }
        if (p.Completed || p.Failed)
        {
            _jobRel.TryRemove(p.JobId, out _);
            return; // 收尾状态由任务自身的代码写(那里知道最终版本号)
        }
        var percent = p.TotalBytes > 0 ? Math.Min(100.0, p.DoneBytes * 100.0 / p.TotalBytes) : (double?)null;
        Upsert(rel, p.Direction == TransferDirection.Upload ? SyncState.PendingUpload : SyncState.PendingDownload,
            "", KnownVersion(rel), percent);
    }

    /// <summary>
    /// 传输任务**失败**时的收尾。
    ///
    /// 为什么必须有这个处理器(真机踩到的):队列的失败只发 `Failed` 事件、只写它自己的
    /// 完成表,而界面与日志看的是**状态列表与通知**。没人订阅的后果是:
    /// 文件停在「待上传」、日志里一个字都没有、下一轮对账再失败一次 ——
    /// 用户看到的是"网盘不动了",我们拿到的证据是"什么都没有"。实测 SyncHostCheck ⑫
    /// 就是这样:两个文件永远停在 PendingUpload,而日志里连一条失败都没有。
    /// </summary>
    private void OnQueueFailed(TransferJob job, Exception ex)
    {
        var rel = _jobRel.TryGetValue(job.Id, out var r) ? r : job.DisplayName;
        _jobRel.TryRemove(job.Id, out _);
        var state = SyncState.Failed;
        Upsert(rel, state, $"传输失败:{ex.Message}", KnownVersion(rel));
        Notice?.Invoke($"{(job.Direction == TransferDirection.Upload ? "上传" : "下载")}失败 {rel}:{ex.Message}(下一轮对账会重试)");
    }

    /// <summary>
    /// 停止同步并释放资源。**幂等**:重复调用是空操作。
    ///
    /// 为什么必须幂等(真机实测):`SyncHostCheck` ⑬ 里一个"显式 Dispose + await using"
    /// 的双重释放,把进程以**未处理异常**打崩:
    /// `ObjectDisposedException: The CancellationTokenSource has been disposed.`
    /// 而这条路径在真实客户端里也存在(设置页"保存并重启同步"会先释放旧运行时,
    /// 作用域退出时还会再释放一次)—— 崩溃点在**退出/重启**这种最不该崩的时刻。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return; // 已经释放过(第二次调用直接返回,而不是对着已释放的 CTS/连接再操作一遍)
        }
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
        _queue.Progress -= OnQueueProgress;
        _queue.Failed -= OnQueueFailed;
        await _queue.DisposeAsync().ConfigureAwait(false);
        _store.Dispose();
    }

    /// <summary>释放标记(见 <see cref="DisposeAsync"/>:二次释放必须是空操作)。</summary>
    private int _disposed;

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

    /// <summary>
    /// 取**目录**身份(卷序列号 + FileId)。同一卷内改名/移动后 FileId 不变 —— 目录改名
    /// 靠它识别,否则一次"文件夹改个名"会被当成"删掉整棵子树 + 重新上传一遍"。
    ///
    /// 不能复用 <see cref="IdentityOf"/>:那个用 <c>File.Exists</c> 判存在,对目录永远返回 null
    /// (与同步根身份同一个坑,见 <see cref="RootIdentity"/>)。底层 Win32 取句柄时带
    /// `FILE_FLAG_BACKUP_SEMANTICS`,所以对目录同样有效。
    /// </summary>
    private FileIdentity? DirIdentityOf(string local)
    {
        try
        {
            return Directory.Exists(LongPath.ToExtended(local)) ? _identity.TryGet(local) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string FormatIdentity(in FileIdentity id) =>
        $"{id.VolumeSerial}:{id.FileIdHigh}:{id.FileIdLow}";

    // ---------------------------------------------------------------- 删除传播

    /// <summary>本地扫描的一次快照(目录与文件都用它,避免两次扫描结果不一致)。</summary>
    private sealed record LocalScan(HashSet<string> Files, HashSet<string> Dirs, int RawCount, bool Truncated);

    /// <summary>
    /// 扫一次本地树。**截断要如实告诉调用方**:`DirectoryScanner.MaxEntries` 超限时是**静默 break**,
    /// 而"看不见"与"被删了"在扫描结果上一模一样 —— 所以截断时必须放弃一切删除推断。
    /// </summary>
    private LocalScan TakeLocalScan()
    {
        var scanner = new DirectoryScanner();
        var raw = scanner.Scan(_config.SyncRoot, recursive: true);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in raw)
        {
            var rel = Path.GetRelativePath(_config.SyncRoot, e.Path).Replace('\\', '/');
            if (e.IsDirectory)
            {
                dirs.Add(rel);
                continue;
            }
            if (rel.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            {
                continue; // 半成品不参与同步(下载中)
            }
            files.Add(rel);
        }
        return new LocalScan(files, dirs, raw.Count, raw.Count >= scanner.MaxEntries);
    }

    /// <summary>记下目录的远端 id(<c>dir:&lt;rel&gt;</c> 作为状态库里的"路径")。</summary>
    /// <remarks>
    /// 同时记下**目录自己的身份**(卷序列号 + FileId):目录改名识别要用它。
    /// 与 <see cref="SaveState"/> 同一套写法 —— 身份取不到时**不要覆盖**已记的值
    /// (取不到只是这一轮读不到,不代表它变成了别的目录)。
    /// </remarks>
    private void SaveDirState(string rel, string dirId)
    {
        var idn = DirIdentityOf(LocalOf(rel));
        _store.Execute(
            """
            INSERT INTO sync_state(file_id, space_id, local_path, remote_version, local_mtime_ticks, size, hash_sha256, state, updated_at_utc, local_identity)
            VALUES ($id, $space, $p, 0, 0, 0, '', 'InSync', $now, $idn)
            ON CONFLICT(file_id) DO UPDATE SET local_path=$p, updated_at_utc=$now,
                local_identity=CASE WHEN $idn='' THEN local_identity ELSE $idn END
            """,
            ("id", dirId), ("space", _config.SpaceId), ("p", "dir:" + rel),
            ("idn", idn is null ? "" : FormatIdentity(idn.Value)),
            ("now", _clock.GetUtcNow().ToString("O")));
    }

    /// <summary>
    /// 本地**目录改名/移动**识别:新路径 → 旧路径。
    ///
    /// 判据与文件改名同源(DE-D-11):同一卷内改名/移动后**目录自己的 FileId 不变**。
    /// 只有"状态库里记过身份、而那个旧路径本地已经不在了"的目录才有资格作为旧路径 ——
    /// 否则"A 还在、用户又建了一个 A 的副本"会被误判成改名。
    ///
    /// 取不到身份(非 NTFS/网络盘/权限)时**保守返回空**:那时退回"当新目录建 + 不删远端旧目录"
    /// (即本轮之前的行为),不会误删数据,只是远端会留一个旧目录(已记入文档)。
    /// </summary>
    private Dictionary<string, string> DetectDirRenames(LocalScan scan)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var identities = KnownDirIdentities();
        if (identities.Count == 0)
        {
            return result;
        }
        // 身份 → 旧路径;只保留"本地已无"的(本地还在的不可能是被改名带走的)
        var byIdentity = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (oldRel, idn) in identities)
        {
            if (scan.Dirs.Contains(oldRel))
            {
                continue;
            }
            byIdentity[idn] = oldRel;
        }
        if (byIdentity.Count == 0)
        {
            return result;
        }
        foreach (var rel in scan.Dirs)
        {
            if (KnownFileId("dir:" + rel) is not null)
            {
                continue; // 这个路径我们认识 → 不是改名
            }
            var idn = DirIdentityOf(LocalOf(rel));
            if (idn is null)
            {
                continue;
            }
            if (byIdentity.TryGetValue(FormatIdentity(idn.Value), out var oldRel)
                && !string.Equals(oldRel, rel, StringComparison.OrdinalIgnoreCase))
            {
                result[rel] = oldRel;
            }
        }
        return result;
    }

    /// <summary>`dir:&lt;rel&gt;` 对应的目录身份(只返回记过身份的;用于改名识别)。</summary>
    private Dictionary<string, string> KnownDirIdentities() =>
        _store.Query(
                "SELECT local_path, local_identity FROM sync_state WHERE space_id=$s AND local_path LIKE 'dir:%'",
                reader => (Path: reader.GetString(0), Identity: reader.GetString(1)),
                ("$s", _config.SpaceId))
            .Where(r => !string.IsNullOrEmpty(r.Identity))
            .ToDictionary(r => r.Path["dir:".Length..], r => r.Identity, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// **目录的新建与删除传播**(2026-09-13)。
    ///
    /// 三件事:
    ///   ① 本地新目录 → 远端建目录(空目录也要能同步;否则用户建的文件夹在另一端"不存在",
    ///      而一旦往里放文件又会因为父目录不存在而出错);
    ///   ② 本地目录已无 → 远端删该目录(**一次调用删整棵子树**)+ 清掉它下面所有状态行;
    ///   ③ 远端目录已无 → 本地删该目录(连同子目录)。
    ///
    /// 与文件删除同一套**信号完整性**护栏(扫描截断/同步根不可达/根身份变化则整轮不做),
    /// 另加一条针对"目录改名"的保护:若这个目录下面有文件是**本地改名带走的**
    /// (即 `renames.OldPaths` 落在它下面),则**不删** —— 目录改名目前不支持,
    /// 若在这里当成删除,用户改个文件夹名就会把远端一整个子树的文件删掉(硬删、无回收站)。
    /// </summary>
    private async Task<int> SyncDirectoriesAsync(
        string spaceId,
        Dictionary<string, EntryView> remote,
        HashSet<string> remoteDirs,
        (Dictionary<string, string> NewToOld, HashSet<string> OldPaths) renames,
        LocalScan scan,
        CancellationToken ct)
    {
        if (!Directory.Exists(LongPath.ToExtended(_config.SyncRoot)))
        {
            return 0;
        }
        if (scan.Truncated)
        {
            Notice?.Invoke("目录同步:本轮跳过 —— 本机文件数达到扫描上限,扫描被**截断**");
            return 0;
        }
        if (!RootIdentityUnchanged())
        {
            return 0;
        }

        var ops = 0;

        // **先固定"本轮开始时已知的目录行"**,之后再进入新建阶段。
        //
        // 为什么必须在这一步取快照(而不是边循环边读):①新建目录会**立刻**写状态行
        // (`SaveDirState`),而 `remoteDirs` 是本轮对账开头那份远端清单 —— 新建的目录
        // 当然不在里面。于是同一个函数的后半段(③"远端已无 → 本地删目录")会把**刚刚
        // 建好的目录**当成"远端已经没有了",把本地目录连文件一起删掉。
        //
        // 真机实测(2026-09-13,SyncHostCheck ⑫):本地建 `-withfiles/{a.txt,b.txt}` →
        // 远端目录建好了(✓),但**本地那个目录连同两个文件被删了**,文件自然一个都没传上去
        // (下一个断言"目录里的两个文件都上传了 [远端子项=0]"),随后 `Directory.Delete`
        // 直接抛 DirectoryNotFoundException —— 这是**本地数据丢失**,不是显示问题。
        //
        // `EnsureParentDirAsync` 顺带建出的**中间目录**(例如 `p/q/r` 里的 `p`、`p/q`)
        // 同样会写状态行,所以"把①里显式建的目录加进 remoteDirs"这种改法**不够**:
        // 判定的输入必须是**本轮开始前**就已经存在的行(快照取在这里,不是"边循环边读")。
        var knownDirRows = KnownRows()
            .Where(r => r.LocalPath.StartsWith("dir:", StringComparison.Ordinal))
            .ToList();

        // 目录**改名/移动**识别(2026-09-13):先算出来,再决定"新建"还是"移动"。
        // 判据是目录自己的 FileId(同卷内改名/移动不变)——与文件改名同源。
        // 识别不出来的(非 NTFS/网络盘/权限)退回旧行为:当新目录建 + 不删远端旧目录。
        var dirRenames = DetectDirRenames(scan);
        // 本轮**成功移动掉**的旧路径:它们不能再参与②的"本地已无 → 删远端子树",
        // 否则会把我们刚刚移到新位置的**同一个远端目录**删掉(空目录改名时尤其致命:
        // 没有文件落在 OldPaths 里,那道改名保护根本不会触发)。
        var movedOldDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ① 本地新目录 → 远端建目录(只处理状态库里没有记录的目录)
        foreach (var rel in scan.Dirs)
        {
            ct.ThrowIfCancellationRequested();
            if (remoteDirs.Contains(rel) || KnownFileId("dir:" + rel) is not null)
            {
                continue;
            }
            // 只读浏览(仅结构):**不在远端建目录** —— 那是写操作,与"只看结构"矛盾。
            // 本地多出来的目录要如实显示出来(否则用户以为它已经在远端了)。
            if (_config.StructureOnly)
            {
                Upsert(rel, SyncState.StructureOnly, "只读浏览:仅同步目录结构,本地目录未在远端创建(仅结构模式)", 0);
                continue;
            }
            // ①a 先试"这其实是一次目录改名/移动"(远端同一条目换位置,不重传子文件)
            if (dirRenames.TryGetValue(rel, out var renamedFrom)
                && await TryMoveDirAsync(rel, renamedFrom, remote, remoteDirs, renames, ct).ConfigureAwait(false))
            {
                movedOldDirs.Add(renamedFrom);
                ops++;
                continue;
            }
            if (renames.NewToOld.Values.Any(old => old.StartsWith(rel + "/", StringComparison.OrdinalIgnoreCase)))
            {
                continue; // 这是"改名后的新目录",别把它当新目录重复建
            }
            try
            {
                var parentId = await EnsureParentDirAsync(rel, ct).ConfigureAwait(false);
                var created = await _files.CreateDirectoryAsync(
                    spaceId, parentId, Path.GetFileName(rel), ct).ConfigureAwait(false);
                SaveDirState(rel, created.id);
                ops++;
                Notice?.Invoke($"目录同步:已创建远端目录 {rel}");
            }
            catch (Exception ex)
            {
                Notice?.Invoke($"创建远端目录失败 {rel}:{ex.Message}(下一轮再试)");
            }
        }

        // ②③ 删除:只遍历**本轮开始前**已知的目录行(本轮新建的不参与本轮删除判定)
        foreach (var (dirId, dirPath, _) in knownDirRows)
        {
            ct.ThrowIfCancellationRequested();
            var rel = dirPath["dir:".Length..];
            if (movedOldDirs.Contains(rel))
            {
                continue; // 这一轮已经作为"目录改名/移动"处理过了(远端是**同一个**条目,不能删)
            }

            // ② 本地已无 → 远端删子树
            if (!scan.Dirs.Contains(rel))
            {
                if (_config.StructureOnly)
                {
                    continue; // 只读浏览:不删远端任何东西(②是删除,③才是"跟着远端走")
                }
                if (renames.OldPaths.Any(old => old.StartsWith(rel + "/", StringComparison.OrdinalIgnoreCase)))
                {
                    Notice?.Invoke($"目录同步:{rel} 下面是**本地改名**带走的文件 —— 目录改名暂不支持,本轮不删(避免误删整棵子树)");
                    continue;
                }
                if (!ParentEnumerable(Path.Combine(_config.SyncRoot, rel)))
                {
                    continue; // 父目录读不到:可能是权限/网络问题,不是删除
                }
                try
                {
                    await _files.DeleteAsync(dirId, ct).ConfigureAwait(false);
                    RemoveStateRowsUnder(rel);
                    // **快照也要跟着更新**:这一步删掉的是**整棵子树**,而 `remote` 是本轮
                    // 开头列的清单,里面还留着子树里的每个文件。不删干净的话,随后的下载阶段
                    // 会给它们各发一次注定 404 的请求(真机日志里就是三条
                    // 「下载失败 …:文件不存在」),把整个界面刷成失败态 —— 而我们**刚刚**
                    // 亲手删掉了它们,这根本不是"远端删了要同步到本地"。
                    foreach (var gone in remote.Keys
                                 .Where(k => k.StartsWith(rel + "/", StringComparison.OrdinalIgnoreCase))
                                 .ToList())
                    {
                        remote.Remove(gone);
                        RemoveStateRow(gone);
                        RemoveStatus(gone);
                    }
                    Notice?.Invoke($"目录同步:本地已删除 {rel},远端子树已同步删除");
                    ops++;
                }
                catch (ApiException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound)
                {
                    RemoveStateRowsUnder(rel); // 远端已经没有了
                }
                catch (Exception ex)
                {
                    Notice?.Invoke($"删除远端目录失败 {rel}:{ex.Message}(下一轮再试)");
                }
                continue;
            }

            // ③ 远端已无 → 本地删目录(连同子目录)
            if (!remoteDirs.Contains(rel))
            {
                var local = LongPath.ToExtended(LocalOf(rel));
                try
                {
                    if (Directory.Exists(local))
                    {
                        Directory.Delete(local, recursive: true);
                    }
                    RemoveStateRowsUnder(rel);
                    Notice?.Invoke($"目录同步:远端已删除 {rel},本地目录已同步删除");
                    ops++;
                }
                catch (Exception ex)
                {
                    Notice?.Invoke($"删除本地目录失败 {rel}:{ex.Message}(下一轮再试)");
                }
            }
        }


        return ops;
    }

    /// <summary>
    /// 把一次本地**目录改名/移动**推到远端:调用契约 `POST /api/v1/files/{id}/move`,
    /// 让服务端**原地换位置**(同一个 file_id),而不是"建新目录 + 删旧子树 + 重传子文件"。
    ///
    /// 成功后必须把三处一起**重指向**(漏掉任何一处都会产生可见错误):
    ///   ① 状态库:该目录行改为新路径(file_id 不变),**旧前缀下的所有行**(子目录行 `dir:` 前缀、
    ///      子文件行)按前缀改写 —— 否则子文件会被当成"远端有、本地没了"而重新下载回来;
    ///   ② 本轮远端快照 `remote`:键是相对路径,旧路径下的条目要换成新路径,否则上传阶段会
    ///      因为"远端没有这个新路径"而重传一次(服务端 409 名冲突);
    ///   ③ 改名映射 `renames`:被这次目录移动带走的文件不再需要逐个改名(它们已经跟着目录走了),
    ///      必须从 `NewToOld`/`OldPaths` 里摘掉 —— 否则上传阶段会拿一个**已经不存在**的旧路径
    ///      去推改名,失败后降级成"重传"。
    ///
    /// 返回 false 表示"没处理"(调用方按新建目录继续),并且**不修改任何状态**:
    /// move 转异步(202)或失败时,下一轮对账会重新判断。
    /// </summary>
    private async Task<bool> TryMoveDirAsync(
        string rel,
        string oldRel,
        Dictionary<string, EntryView> remote,
        HashSet<string> remoteDirs,
        (Dictionary<string, string> NewToOld, HashSet<string> OldPaths) renames,
        CancellationToken ct)
    {
        var dirId = KnownFileId("dir:" + oldRel);
        if (string.IsNullOrEmpty(dirId))
        {
            return false;
        }
        try
        {
            // 目标父目录可能也是新目录(把 A 移进刚建的 B 里):先确保它在远端存在。
            // **顶层条目不能传 null**:契约的 move 要求具体 `parent_id`,空值会被服务端
            // 拒为「缺少目标目录」(实测第一次跑就是这样);同步起点的远端目录 id 取
            // `_rootId`(配置里给了父目录时就是它本身)。
            var parentId = rel.Contains('/')
                ? await EnsureParentDirAsync(rel, ct).ConfigureAwait(false)
                : MoveTargetParentId();
            var newName = Path.GetFileName(rel);
            Notice?.Invoke($"目录同步:识别为改名/移动 {oldRel} → {rel},按远端 MOVE 处理");
            var res = await _files.MoveAsync(dirId!, parentId, newName, baseVersion: 0, ct).ConfigureAwait(false);
            if (res.async || res.entry is null)
            {
                // 超阈值转异步任务:条目还在原处,**不能**在这里按"已移动"改本地状态
                Notice?.Invoke($"目录同步:{oldRel} → {rel} 超过服务端同步阈值,已转异步任务(下一轮对账看结果)");
                return false;
            }

            // ① 状态库:目录行改到新路径(同一 file_id),旧前缀下的行整体改写
            SaveDirState(rel, dirId!);
            _store.Execute(
                "UPDATE sync_state SET local_path = $new || substr(local_path, length($old) + 1) " +
                "WHERE space_id=$s AND local_path LIKE $oldLike",
                ("new", rel), ("old", oldRel), ("s", _config.SpaceId), ("oldLike", oldRel + "/%"));
            _store.Execute(
                "UPDATE sync_state SET local_path = 'dir:' || $new || substr(local_path, length($oldDir) + 1) " +
                "WHERE space_id=$s AND local_path LIKE $oldDirLike",
                ("new", rel), ("oldDir", "dir:" + oldRel), ("s", _config.SpaceId),
                ("oldDirLike", "dir:" + oldRel + "/%"));

            // ② 远端快照:旧路径 → 新路径(只重指向键;条目的 name/parent_id 没变)
            foreach (var key in remote.Keys
                         .Where(k => k.StartsWith(oldRel + "/", StringComparison.OrdinalIgnoreCase))
                         .ToList())
            {
                var target = remote[key];
                remote.Remove(key);
                remote[rel + key[oldRel.Length..]] = target;
            }
            foreach (var dir in remoteDirs.Where(d =>
                         string.Equals(d, oldRel, StringComparison.OrdinalIgnoreCase)
                         || d.StartsWith(oldRel + "/", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                remoteDirs.Remove(dir);
                remoteDirs.Add(rel + dir[oldRel.Length..]);
            }

            // ③ 改名映射:这一段子树已经跟着目录走了,不再逐个改名
            foreach (var key in renames.NewToOld.Keys
                         .Where(k => k.StartsWith(rel + "/", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                renames.NewToOld.Remove(key);
            }
            renames.OldPaths.RemoveWhere(p => p.StartsWith(oldRel + "/", StringComparison.OrdinalIgnoreCase));

            RemoveStatus(oldRel);
            Upsert(rel, SyncState.InSync, $"已改名/移动(原 {oldRel})", res.entry.version);
            Notice?.Invoke($"目录同步:{oldRel} → {rel} 已在远端原地移动(同一目录,子文件未重传)");
            return true;
        }
        catch (Exception ex)
        {
            // 失败不吞:如实报出,并按"新目录"继续处理(数据安全优先 —— 宁可多一个目录,
            // 也不能让用户以为移动成功了)。旧远端目录由改名保护挡着,不会被误删。
            Notice?.Invoke($"目录改名/移动失败({oldRel} → {rel}),本轮按新建目录处理:{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 移动的**目标父目录 id**(顶层条目用)。
    ///
    /// 为什么不能像上传那样传 null:契约 `POST /files/{id}/move` 的 `parent_id` 是必须的具体 id,
    /// 空值会被服务端拒为「缺少目标目录」(真机第一次跑就是这个报错)。
    /// 同步起点的远端目录 id 记在 `_rootId`(对账开头由"同步起点那一层条目的 parent_id"得到),
    /// 配置里显式给了父目录时就是它本身。
    /// </summary>
    private string? MoveTargetParentId() =>
        !string.IsNullOrEmpty(_config.ParentId) ? _config.ParentId : _rootId;

    /// <summary>删掉某个目录下的所有状态行(含它自己:`dir:&lt;rel&gt;` 与 <c>rel/...</c>)。</summary>
    private void RemoveStateRowsUnder(string rel)
    {
        _store.Execute("DELETE FROM sync_state WHERE local_path=$d", ("d", "dir:" + rel));
        _store.Execute("DELETE FROM sync_state WHERE local_path=$p OR local_path LIKE $prefix",
            ("p", rel), ("prefix", rel + "/%"));
    }

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
        LocalScan scan,
        CancellationToken ct)
    {
        _ = spaceId;
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
        if (scan.Truncated)
        {
            Notice?.Invoke("删除传播:本轮跳过 —— 本机文件数达到扫描上限,扫描被**截断**,不能据此认定删除");
            return 0;
        }
        var localFiles = scan.Files;

        var deleted = 0;

        // ---- ① 远端已无 → 删本地 ----
        // 只看**文件**行:目录行以 `dir:` 开头,由 SyncDirectoriesAsync 处理(它按整棵子树删,更高效)。
        foreach (var (fileId, rel, _) in KnownRows()
                     .Where(r => !r.LocalPath.StartsWith("dir:", StringComparison.Ordinal)))
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
            // **目录行交给 SyncDirectoriesAsync**:它们以 `dir:` 开头(路径前缀会让这里的
            // ParentEnumerable 与文件判断全部走错),而且按整棵子树删更高效。
            if (rel.StartsWith("dir:", StringComparison.Ordinal))
            {
                continue;
            }
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
    private List<(string FileId, string LocalPath, long RemoteVersion)> KnownRows() =>
        // 走 StateStore 的封装(与传输 worker 共用同一把锁):直接自建命令会在
        // "worker 正在写状态"时与它并发用同一条 SQLite 连接(见 StateStore._gate)
        _store.Query(
            "SELECT file_id, local_path, remote_version FROM sync_state WHERE space_id=$s",
            reader => (reader.GetString(0), reader.GetString(1), reader.GetInt64(2)),
            ("$s", _config.SpaceId));

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
