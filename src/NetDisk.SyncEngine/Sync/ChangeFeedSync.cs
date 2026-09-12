// 变更通道:SSE 消费 + /changes 游标双态(DE-D-13)。
//
// 6.9 的核心设计是**一个游标两种用法**(在线 SSE / 离线补拉读写同一个 last_seq),
// 而这一层要保证三件事:
//
// ①**帧 id 用来推进"那个空间"的游标**:一条连接上流动的是**所有空间**的变更,
//   所以 `id: {space}:{seq}` 必须按空间拆开各自推进。把 id 当一个不透明字符串
//   (或者只维护一个全局 seq)在多空间下必然漏变更 —— 而且漏的是"某个空间的一段"。
//
// ②**重连必须"先补拉、再重建连接"**:SSE 是**尽力而为**的加速通道(丢了事件服务端不补),
//   断线期间发生的一切都不会重放。因此重连顺序只能是:先对每个已知空间按游标把
//   `/changes` 补到最新,然后才建立新连接。反过来(先连上再补拉)会出现一个窗口:
//   连接已经建立、但游标还是旧的 —— 这个窗口里到达的事件会被当成"重复"丢掉
//   (因为它的 seq 大于旧游标,但用户其实还没拿到那段变更),于是永久漏一段。
//   **也不依赖 Last-Event-ID**:服务端不保证按它补发(它只用于诊断),
//   把正确性押在"服务端一定补发"上,等于把"漏同步"变成一个偶发故障。
//
// ③**事件不直接落定态**:收到 `change` 帧只做一件事 —— 把"这个空间要补到 seq"记下来,
//   真正的文件状态由 `/changes` 的权威条目驱动(6.4:变更流是索引,不是状态)。
//   理由:事件可能乱序、可能重复、也可能因为服务端扇出裁剪而缺字段;拿它直接改本地
//   状态会让"客户端状态"与"服务端事实"分叉,而分叉之后没有任何东西能纠正它。

using NetDisk.ClientCore;
using NetDisk.Transport;

namespace NetDisk.SyncEngine.Sync;

/// <summary>补拉一页的结果(来自 <c>GET /api/v1/changes</c>)。</summary>
public sealed record ChangePage(IReadOnlyList<string> ChangeSeqs, long NextSeq, bool HasMore, string SpaceId);

/// <summary>通道要求调用方做的事。</summary>
public enum FeedActionKind
{
    /// <summary>什么都不用做(重复帧/心跳/无关事件)。</summary>
    None,

    /// <summary>该空间需要按游标补拉(带建议的下界与上界)。</summary>
    PullRequired,

    /// <summary>该空间游标超窗 → 必须全量重扫该空间。</summary>
    FullRescanRequired,
}

public sealed record FeedAction(FeedActionKind Kind, string SpaceId, long FromSeq, long ToSeq, string Reason);

/// <summary>
/// 变更通道的游标状态机(纯逻辑 + 可注入的补拉委托,便于把"重连顺序"跑出来)。
/// </summary>
public sealed class ChangeFeedSync
{
    private readonly Dictionary<string, SpaceCursor> _cursors = new(StringComparer.Ordinal);
    private readonly Func<string, long, CancellationToken, Task<ChangePage>> _pull;
    private readonly Func<CancellationToken, Task>? _reconnect;

    public ChangeFeedSync(
        Func<string, long, CancellationToken, Task<ChangePage>> pull,
        Func<CancellationToken, Task>? reconnect = null)
    {
        _pull = pull ?? throw new ArgumentNullException(nameof(pull));
        _reconnect = reconnect;
    }

    /// <summary>已知空间(测试与日志用)。</summary>
    public IReadOnlyDictionary<string, SpaceCursor> Cursors => _cursors;

    /// <summary>本次会话累计补拉的页数(诊断用)。</summary>
    public int PagesPulled { get; private set; }

    /// <summary>重连次数。</summary>
    public int Reconnects { get; private set; }

    /// <summary>登记/更新一个空间的游标(启动时从状态库读回,或首次全量清单后写入)。</summary>
    public void SetCursor(SpaceCursor cursor) => _cursors[cursor.SpaceId] = cursor;

    public SpaceCursor? CursorOf(string spaceId) => _cursors.TryGetValue(spaceId, out var c) ? c : null;

    /// <summary>
    /// 处理一条 SSE 事件。
    ///
    /// **只推进"待补拉"的意图,不改文件状态**(验收③)。
    /// </summary>
    public FeedAction OnEvent(SseEvent evt)
    {
        if (evt.IsReady || !evt.IsChange)
        {
            return new FeedAction(FeedActionKind.None, "", 0, 0, "非变更帧(ready/心跳)");
        }
        if (!ChangeFrameId.TryParse(evt.Id, out var frame))
        {
            // 畸形帧**丢弃**而不是崩掉:它不是权威路径(权威是 /changes),
            // 丢掉它,下一次补拉仍会把这段变更带回来。
            return new FeedAction(FeedActionKind.None, "", 0, 0, "帧 id 无法解析(已丢弃,靠补拉兜底)");
        }

        _cursors.TryGetValue(frame.SpaceId, out var cursor);
        var last = cursor?.LastSeq ?? 0;
        if (cursor is { NeedsFullRescan: true })
        {
            return new FeedAction(FeedActionKind.FullRescanRequired, frame.SpaceId, last, frame.Seq,
                "该空间游标已超窗,需要全量重扫(事件不改变这个结论)");
        }
        if (frame.Seq <= last)
        {
            // 重复/乱序帧:**幂等忽略**。不这么做的话,乱序帧会让游标倒退,
            // 于是同一段变更被反复补拉(而且看起来"一直在同步")。
            return new FeedAction(FeedActionKind.None, frame.SpaceId, last, frame.Seq,
                $"帧 seq={frame.Seq} 不大于游标 {last}(重复或乱序,忽略)");
        }

        return new FeedAction(FeedActionKind.PullRequired, frame.SpaceId, last, frame.Seq,
            $"事件提示 {frame.SpaceId} 有到 seq={frame.Seq} 的变更 → 按游标补拉权威条目");
    }

    /// <summary>
    /// 按游标补拉一个空间直到追平(供重连前调用,也可被事件驱动调用)。
    /// </summary>
    public async Task<FeedAction> CatchUpAsync(string spaceId, CancellationToken ct = default)
    {
        if (!_cursors.TryGetValue(spaceId, out var cursor))
        {
            return new FeedAction(FeedActionKind.None, spaceId, 0, 0, "未知空间(尚未建立游标)");
        }
        if (cursor.NeedsFullRescan)
        {
            return new FeedAction(FeedActionKind.FullRescanRequired, spaceId, cursor.LastSeq, 0, "游标超窗");
        }

        var from = cursor.LastSeq;
        var target = cursor.LastSeq;
        while (true)
        {
            ChangePage page;
            try
            {
                page = await _pull(spaceId, _cursors[spaceId].LastSeq, ct).ConfigureAwait(false);
            }
            catch (ApiException ex) when (ex.Code == "cursor_expired")
            {
                // 服务端明确说"你的游标早于已清理水位" → 这一空间全量重扫。
                // **绝不能**当成空结果:那会让客户端以为自己已经最新,永久漏一段(6.4)。
                _cursors[spaceId] = _cursors[spaceId].OnCursorExpired();
                return new FeedAction(FeedActionKind.FullRescanRequired, spaceId, from, 0,
                    "服务端 409 cursor_expired → 该空间走全量清单");
            }

            PagesPulled++;
            target = Math.Max(target, page.NextSeq);
            _cursors[spaceId] = _cursors[spaceId] with { LastSeq = page.NextSeq };
            if (!page.HasMore)
            {
                break;
            }
        }
        return new FeedAction(FeedActionKind.PullRequired, spaceId, from, target, "已补拉到最新");
    }

    /// <summary>
    /// 重连:**先逐空间补拉,再重建连接**(验收②;顺序由检查器断言)。
    /// </summary>
    /// <returns>补拉结果(每空间一条);连接在全部补拉完成之后才重建。</returns>
    public async Task<IReadOnlyList<FeedAction>> ReconnectAsync(CancellationToken ct = default)
    {
        var results = new List<FeedAction>();
        foreach (var spaceId in _cursors.Keys.ToArray())
        {
            results.Add(await CatchUpAsync(spaceId, ct).ConfigureAwait(false));
        }

        // 只有全部空间补齐之后才建新连接 —— 顺序反了会留下"连接已建立但游标还是旧的"
        // 窗口,窗口内到达的事件会被当成重复丢掉(永久漏一段)。
        if (_reconnect is not null)
        {
            await _reconnect(ct).ConfigureAwait(false);
            Reconnects++;
        }
        return results;
    }
}
