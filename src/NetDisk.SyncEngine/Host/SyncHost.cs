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
//   - 本地变更检测用 **大小 + mtime**(不做内容哈希),改名靠 FileId 识别;
//   - 远端变更用 SSE 事件触发**重新对账**(不做按 seq 的增量裁剪);
//   - 冲突策略:保留本地版本为**冲突副本**并拉回远端版本,冲突副本会在下一轮
//     作为新文件上传(即"两端都有,不丢任何一边")。
// 不在 MVP 内(后续):目录级移动/删除的远端回传、按 seq 增量、限速 UI、
//   只读浏览模式、多空间。

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
    private readonly Func<ITokenProvider, string, SseChangeStream>? _sseFactory;
    private readonly TimeProvider _clock;

    private readonly StateStore _store;
    private readonly ExpectedChangeLedger _ledger;
    private readonly TransferQueue _queue;
    private readonly FileWatcher? _watcher;
    private readonly List<SyncEntryStatus> _status = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _localLoop;
    private Task? _remoteLoop;
    private volatile bool _running;

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
        Func<ITokenProvider, string, SseChangeStream>? sseFactory = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _clock = clock ?? TimeProvider.System;
        _sseFactory = sseFactory;
        _files = new FileApi(api);

        var statePath = Path.Combine(
            Path.GetDirectoryName(ClientConfig.DefaultPath())!, "state.db");
        _store = StateStore.Open(statePath);
        _ledger = new ExpectedChangeLedger(clock: _clock, sink: new SqliteExpectedChangeSink(_store));
        _queue = new TransferQueue(clock: _clock);
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

    public bool IsRunning => _running;

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
        if (!_config.IsUsable())
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
        await ReconcileAsync(_cts.Token).ConfigureAwait(false);

        if (_watcher is not null)
        {
            _watcher.RescanRequested += OnRescanRequested;
            _watcher.Start();
        }
        if (_sseFactory is not null)
        {
            _remoteLoop = Task.Run(() => RemoteLoopAsync(_cts.Token), _cts.Token);
        }
        _running = true;
        Notice?.Invoke("同步已启动");
        return true;
    }

    // ---------------------------------------------------------------- 对账

    /// <summary>
    /// 全量对账:MVP 的"真相来源"。
    /// 先远端→本地(缺的下载、旧的更新),再本地→远端(新文件/改动上传)。
    /// </summary>
    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        var spaceId = await ResolveSpaceIdAsync(ct).ConfigureAwait(false);
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
            Upsert(rel, SyncState.InSync, "", e.version);
        }

        // ① 远端 → 本地
        foreach (var (rel, entry) in remote)
        {
            ct.ThrowIfCancellationRequested();
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
                    Upsert(rel, SyncState.Conflict, $"两端都改了;本地版本已保留为 {Path.GetFileName(conflictPath)}", entry.version);
                    Notice?.Invoke($"冲突:已保留本地副本 {Path.GetFileName(conflictPath)}");
                }
                await DownloadAsync(entry, rel, ct).ConfigureAwait(false);
            }
        }

        // ② 本地 → 远端(新文件或本地改动)
        foreach (var item in ScanLocal())
        {
            ct.ThrowIfCancellationRequested();
            var rel = item.RelativePath;
            var known = KnownVersion(rel);
            var remoteHas = remote.TryGetValue(rel, out var remoteEntry);
            if (remoteHas && !LocalLooksChanged(rel))
            {
                continue;
            }
            await UploadAsync(rel, ct).ConfigureAwait(false);
            _ = known;
            _ = remoteEntry;
        }

        // 传输统一在这里排空:入队是"计划",排空才是"执行完" ——
        // 排空之后状态才是可信的(否则 UI 会看到一堆 Pending 然后瞬间跳 InSync)。
        await _queue.DrainAsync(ct).ConfigureAwait(false);
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

    private Task DownloadAsync(EntryView entry, string rel, CancellationToken ct)
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
                Upsert(rel, SyncState.InSync, "", entry.version);
                return n;
            },
        });
        // 队列是异步跑的:这里只入队,不阻塞对账;实际完成由 ReconcileAsync 末尾统一 Drain
        return Task.CompletedTask;
    }

    private async Task UploadAsync(string rel, CancellationToken ct)
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
                var res = await _files.UploadAsync(spaceId, parentId, Path.GetFileName(local), local, null, token)
                    .ConfigureAwait(false);
                SaveState(new EntryView
                {
                    id = res.FileId,
                    name = Path.GetFileName(local),
                    is_dir = false,
                    size = info.Length,
                    version = res.Version,
                    etag = "",
                    updated_at = DateTimeOffset.UtcNow.ToString("O"),
                }, rel, local);
                Upsert(rel, SyncState.InSync, "", res.Version);
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
        var pid = e.parent_id;
        var guard = 0;
        while (!string.IsNullOrEmpty(pid) && pid != _config.ParentId && guard++ < 64)
        {
            var p = await _files.GetEntryAsync(pid!, CancellationToken.None).ConfigureAwait(false);
            parts.Insert(0, p.name);
            pid = p.parent_id;
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

    private void SaveState(EntryView e, string rel, string local)
    {
        long mtime = 0;
        if (File.Exists(LongPath.ToExtended(local)))
        {
            mtime = new FileInfo(LongPath.ToExtended(local)).LastWriteTimeUtc.Ticks;
        }
        _store.Execute(
            """
            INSERT INTO sync_state(file_id, space_id, local_path, remote_version, local_mtime_ticks, size, hash_sha256, state, updated_at_utc)
            VALUES ($id, $space, $p, $ver, $mtime, $size, $hash, 'InSync', $now)
            ON CONFLICT(file_id) DO UPDATE SET
              local_path=$p, remote_version=$ver, local_mtime_ticks=$mtime, size=$size,
              hash_sha256=$hash, state='InSync', updated_at_utc=$now
            """,
            ("id", e.id), ("space", _config.SpaceId), ("p", rel), ("ver", e.version),
            ("mtime", mtime), ("size", e.size), ("hash", e.hash_sha256 ?? ""),
            ("now", _clock.GetUtcNow().ToString("O")));
    }
}
