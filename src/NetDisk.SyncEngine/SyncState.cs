// SyncEngine 的骨架(DE-D-01;DE-D-08~13 补齐 watcher/状态机/冲突)。
//
// 这一版定义**同步状态机的最小状态集**,因为它是后续所有同步逻辑的地基:
// 状态给错(例如把"远端已删"当成"本地要删")会造成**用户数据被删**,
// 而这类错误在真机上一次只表现为"某个文件没了",极难复现。
//
// 因此状态集里刻意把两个"必须区分"的情形单列出来:
//   - PendingRemoteGone:远端确实删了(可以删本地);
//   - PermissionLimited / SpaceRevoked:看不到该空间了(**绝不能删本地**,
//     必须保留并等成员关系恢复 —— 7.2 P1-2)。

using NetDisk.ClientCore;

namespace NetDisk.SyncEngine;

/// <summary>一个同步项的本地状态。</summary>
public enum SyncState
{
    /// <summary>本地与远端一致,无待办动作。</summary>
    InSync = 0,

    /// <summary>本地有改动待上传。</summary>
    PendingUpload,

    /// <summary>远端有改动待下载。</summary>
    PendingDownload,

    /// <summary>两端都改了 → 走冲突解决(DE-D-12 的 version 裁决)。</summary>
    Conflict,

    /// <summary>
    /// 远端已不存在且**确认**是删除(不是权限问题)→ 可以删本地。
    /// 与下一项的区别就是"能不能删本地"。
    /// </summary>
    PendingRemoteGone,

    /// <summary>
    /// 空间被移出/解散(服务端 410 space_revoked)。
    /// **保留本地数据**并停止同步该空间 —— 此时删本地等于替服务端执行了
    /// "我不该执行"的动作(用户可能只是被临时移出)。
    /// </summary>
    SpaceRevoked,

    /// <summary>权限被降级到只读:停止上传,保留本地。</summary>
    PermissionLimited,

    /// <summary>
    /// 一次传输**失败**了(网络/服务端拒绝/磁盘问题),原因写在条目的说明里。
    ///
    /// 为什么单列一个状态:失败原先只活在队列内部 —— 界面停在「上传中」、
    /// 日志里一个字都没有,用户看到的是"网盘不动了"。而失败恰恰是最需要
    /// 一行明确原因的时刻(实测:两个文件永远停在 PendingUpload,
    /// 真机上没有任何线索)。失败不是终态:下一轮对账会重试。
    /// </summary>
    Failed,
}

/// <summary>同步项(以空间 + 文件 id 为键;路径是可变的派生信息)。</summary>
public sealed record SyncItem
{
    public required string SpaceId { get; init; }

    /// <summary>服务端文件 id(uuid)。空 = 尚未上传过的新本地文件。</summary>
    public string? FileId { get; init; }

    /// <summary>本地绝对路径(长路径已归一化,DE-D-16)。</summary>
    public required string LocalPath { get; init; }

    /// <summary>服务端版本(乐观锁的 base_version)。</summary>
    public long Version { get; init; }

    public SyncState State { get; init; } = SyncState.InSync;
}

/// <summary>各空间的游标集合(多空间不共用槽位,6.9 T2-1)。</summary>
public sealed class CursorStore
{
    private readonly Dictionary<string, SpaceCursor> _bySpace = new(StringComparer.Ordinal);

    public SpaceCursor Get(string spaceId) =>
        _bySpace.TryGetValue(spaceId, out var c) ? c : new SpaceCursor { SpaceId = spaceId };

    public void Set(SpaceCursor cursor) => _bySpace[cursor.SpaceId] = cursor;

    public IReadOnlyCollection<SpaceCursor> All => _bySpace.Values;
}
