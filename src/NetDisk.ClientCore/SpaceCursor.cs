// 增量同步游标(6.9"双态同一游标"的客户端侧模型)。
//
// 关键点:在线(SSE)与离线(/changes 补拉)读写的是**同一个** last_seq。
// 因此这个类型必须能同时表达两件事:
//   1. 每个空间各自的游标(多空间场景下不能共用一个槽位 —— 6.9 的 T2-1);
//   2. "服务端说我的游标超窗了"(409 cursor_expired)→ 客户端需要**全量重扫**
//      这一个空间,而不是整站重扫。
//
// 这两件事决定了它不能只是一个 long。

namespace NetDisk.ClientCore;

/// <summary>一个空间的同步游标状态。</summary>
public sealed record SpaceCursor
{
    /// <summary>空间 id。</summary>
    public required string SpaceId { get; init; }

    /// <summary>已确认处理到的 change_seq(0 = 还没同步过)。</summary>
    public long LastSeq { get; init; }

    /// <summary>
    /// 是否需要全量重扫该空间(超窗/首次/服务端换了保留水位)。
    ///
    /// **按空间**而不是全局:一个空间超窗不该让其它空间的增量同步也跟着全量重来。
    /// </summary>
    public bool NeedsFullRescan { get; init; }

    /// <summary>服务端返回 409 cursor_expired 时构造新状态。</summary>
    public SpaceCursor OnCursorExpired() => this with { NeedsFullRescan = true };

    /// <summary>全量清单拉完后,把游标重置到服务端给出的头指针。</summary>
    public SpaceCursor OnFullRescanDone(long headSeq) =>
        this with { NeedsFullRescan = false, LastSeq = headSeq };
}
