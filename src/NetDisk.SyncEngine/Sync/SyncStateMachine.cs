// 同步状态机(DE-D-09)。
//
// 这个类的唯一职责:把"本地/远端各自发生了什么"变成**一个状态 + 一个决策**。
// 之所以要单独成类而不是散在同步循环里,是因为这里有两条**错了就会丢用户数据**的规则,
// 而丢数据的路径从来不是崩在明面上,而是"某次分支走错了、把本地文件删了":
//
//   R-19 / 验收②:**403(权限受限)与 401 永不删本地**。空间被管理员冻结、成员被移出、
//   令牌失效,这些都会让服务端返回 403 或 401 —— 而**远端仍然存在那些文件**。
//   此时若把"我读不到了"当成"远端没了"从而删本地,用户就会丢文件;而用户看到的是
//   "同步完成"。正确动作:标 `PermissionLimited`、**保留本地**、按 30s→2min→10min
//   退避探活,权限恢复后**自动补拉**。
//
//   验收③:**404/410 需要二次确认才删本地**。单次 404 可能是"另一个客户端刚删完、
//   事件还没到"或"路径解析抖动"。只凭一次 404 就删本地文件,等于把一次瞬时错误
//   升级成不可恢复的数据删除(网盘没有回收站,4.5)。所以第一次只标
//   `PendingRemoteGone`(本地照留),第二次确认仍然不在才真正删本地。
//
// 状态与决策分开是刻意的:**决策**才是会被执行的动作(上传/下载/删除/探活),
// 状态只是"我现在认为这是什么情况"。这让"什么情况下会删本地文件"成为一个
// 可以被穷举断言的问题(见 WatcherCheck 同级的 SyncStateCheck)。

namespace NetDisk.SyncEngine.Sync;

/// <summary>单条同步条目的状态(7.2)。</summary>
public enum SyncState
{
    /// <summary>本地与远端一致。</summary>
    Synced,

    /// <summary>本地改了,远端没改 → 该上传。</summary>
    LocalModified,

    /// <summary>远端改了,本地没改 → 该下载。</summary>
    RemoteModified,

    /// <summary>两端都改了 → 冲突(裁决见 DE-D-12;本状态不自动决定覆盖哪边)。</summary>
    Conflict,

    /// <summary>正在上传。</summary>
    Uploading,

    /// <summary>正在下载。</summary>
    Downloading,

    /// <summary>远端报告不存在(404/410)**第一次**;本地保留,等二次确认。</summary>
    PendingRemoteGone,

    /// <summary>权限受限(403/401):**本地必须完整保留**,退避探活等恢复。</summary>
    PermissionLimited,
}

/// <summary>状态机给出的决策(会被同步循环执行的动作)。</summary>
public enum SyncDecision
{
    /// <summary>什么都不做。</summary>
    None,

    /// <summary>上传本地版本。</summary>
    Upload,

    /// <summary>下载远端版本。</summary>
    Download,

    /// <summary>把本地内容另存为冲突副本(DE-D-12 的手动策略用)。</summary>
    UploadAsConflictCopy,

    /// <summary>确认远端确实没了 → 删除本地副本。</summary>
    DeleteLocal,

    /// <summary>探活权限(退避到点后)。</summary>
    ProbePermission,

    /// <summary>权限刚恢复 → 重新对齐(补拉/重扫)。</summary>
    Resync,
}

/// <summary>一条同步条目的记录(可持久化到 DE-D-07 的 <c>sync_state</c> 表)。</summary>
public sealed record FileSyncEntry
{
    public required string FileId { get; init; }
    public required string LocalPath { get; init; }

    public SyncState State { get; init; } = SyncState.Synced;

    /// <summary>本地大小/最后写入时间(判定"本地是否改了"的依据)。</summary>
    public long LocalSize { get; init; }

    public long LocalMtimeTicks { get; init; }

    /// <summary>远端版本号(files.version,乐观锁用)。</summary>
    public long RemoteVersion { get; init; }

    /// <summary>上一次成功同步时的本地指纹与远端版本(判定"哪边改了"的基线)。</summary>
    public long SyncedLocalMtimeTicks { get; init; }

    public long SyncedLocalSize { get; init; }

    public long SyncedRemoteVersion { get; init; }

    /// <summary>远端 404/410 已被观察到的次数(**二次确认**用:第一次不动本地)。</summary>
    public int RemoteGoneObservations { get; init; }

    /// <summary>权限受限时探活失败的次数(决定退避到 30s / 2min / 10min)。</summary>
    public int PermissionProbeFailures { get; init; }

    /// <summary>下次允许探活的时刻。</summary>
    public DateTimeOffset NextProbeAt { get; init; }

    /// <summary>本地是否"改过但还没同步"。</summary>
    public bool LocalDirty => LocalMtimeTicks != SyncedLocalMtimeTicks || LocalSize != SyncedLocalSize;

    /// <summary>远端是否"改过但还没同步"。</summary>
    public bool RemoteDirty => RemoteVersion != SyncedRemoteVersion;
}

/// <summary>一次状态迁移的结果。</summary>
public sealed record SyncTransition(FileSyncEntry Entry, SyncDecision Decision, string Reason);

/// <summary>同步状态机(纯函数,便于穷举断言)。</summary>
public static class SyncStateMachine
{
    /// <summary>
    /// 权限受限时的探活退避阶梯(验收②:30s → 2min → 10min,之后封顶 10min)。
    ///
    /// 为什么第一次这么短:权限受限的常见原因是"管理员刚冻结/刚移出",而恢复也常常是
    /// 分钟级的(管理员误操作后立刻改回来)。等 10min 才探第一次,用户会觉得"网盘坏了"。
    /// 为什么封顶 10min:再长的退避会让"权限恢复后自动补拉"变得不可接受地慢。
    /// </summary>
    public static readonly TimeSpan[] ProbeBackoff =
    {
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(10),
    };

    /// <summary>权限受限探活的间隔(按失败次数取阶梯,封顶最后一级)。</summary>
    public static TimeSpan ProbeDelayFor(int failures)
    {
        if (failures < 0)
        {
            failures = 0;
        }
        var idx = Math.Min(failures, ProbeBackoff.Length - 1);
        return ProbeBackoff[idx];
    }

    /// <summary>本地发生变化(新增/修改)。</summary>
    public static SyncTransition OnLocalChanged(FileSyncEntry e, long size, long mtimeTicks)
    {
        var updated = e with { LocalSize = size, LocalMtimeTicks = mtimeTicks };

        if (e.State == SyncState.PermissionLimited)
        {
            // 权限受限期间**不发上传**(必然被拒),但要把本地变化记下来:
            // 权限一恢复,`OnPermissionRestored` 会据此决定"补拉还是上传"。
            return new SyncTransition(updated with { State = SyncState.PermissionLimited },
                SyncDecision.None, "权限受限期间只记录本地变化,不上传(上传必然被 403 拒)");
        }

        if (e.RemoteDirty || e.State == SyncState.RemoteModified)
        {
            // 两端都改了 → 冲突。**不自动决定**覆盖哪边(DE-D-12 的裁决策略负责)。
            return new SyncTransition(updated with { State = SyncState.Conflict },
                SyncDecision.None, "本地与远端都改过 → 冲突,交给裁决策略(DE-D-12)");
        }

        return new SyncTransition(updated with { State = SyncState.LocalModified },
            SyncDecision.Upload, "仅本地改过 → 上传");
    }

    /// <summary>远端发生变化(收到变更事件或对账发现新版本)。</summary>
    public static SyncTransition OnRemoteChanged(FileSyncEntry e, long remoteVersion)
    {
        var updated = e with
        {
            RemoteVersion = remoteVersion,
            // 远端又出现了 → 之前的"待确认消失"作废(它没消失,只是事件乱序)
            RemoteGoneObservations = 0,
        };

        if (e.LocalDirty || e.State == SyncState.LocalModified)
        {
            return new SyncTransition(updated with { State = SyncState.Conflict },
                SyncDecision.None, "本地与远端都改过 → 冲突");
        }
        return new SyncTransition(updated with { State = SyncState.RemoteModified },
            SyncDecision.Download, "仅远端改过 → 下载");
    }

    public static SyncTransition OnUploadStarted(FileSyncEntry e)
        => new(e with { State = SyncState.Uploading }, SyncDecision.None, "上传中");

    /// <summary>上传成功:把基线推进到"刚刚同步过"。</summary>
    public static SyncTransition OnUploadFinished(FileSyncEntry e, long newRemoteVersion)
        => new(e with
        {
            State = SyncState.Synced,
            RemoteVersion = newRemoteVersion,
            SyncedRemoteVersion = newRemoteVersion,
            SyncedLocalMtimeTicks = e.LocalMtimeTicks,
            SyncedLocalSize = e.LocalSize,
        }, SyncDecision.None, "上传完成,基线推进");

    public static SyncTransition OnDownloadStarted(FileSyncEntry e)
        => new(e with { State = SyncState.Downloading }, SyncDecision.None, "下载中");

    /// <summary>下载成功:本地指纹按**落盘后**的实际值更新(而不是下载前的值)。</summary>
    public static SyncTransition OnDownloadFinished(FileSyncEntry e, long localSize, long localMtimeTicks)
        => new(e with
        {
            State = SyncState.Synced,
            LocalSize = localSize,
            LocalMtimeTicks = localMtimeTicks,
            SyncedLocalSize = localSize,
            SyncedLocalMtimeTicks = localMtimeTicks,
            SyncedRemoteVersion = e.RemoteVersion,
            RemoteGoneObservations = 0,
        }, SyncDecision.None, "下载完成,基线推进");

    /// <summary>
    /// 远端报告不存在(404 / <c>space_gone</c>)。
    ///
    /// **第一次绝不删本地**(验收③):只标 `PendingRemoteGone` 并记一次观察。
    /// 第二次确认仍然不在,才给出 <see cref="SyncDecision.DeleteLocal"/>。
    ///
    /// **计数器只在 `PendingRemoteGone` 状态里有意义**(穷举断言逼出来的规则):
    /// 若不管状态就累加,下面这条路径会删掉用户刚编辑的内容 ——
    /// "远端消失(第 1 次)→ 用户在本地改了文件(状态转 LocalModified,计数被带过去)
    /// → 又收到一次远端消失(计数凑到 2)→ 删本地"。而第二次消息与第一次说的是
    /// **同一个旧事实**,并不能证明"用户改完之后远端仍然没有这个文件"。
    /// 所以从别的状态进来一律算**首次观察**。
    /// </summary>
    public static SyncTransition OnRemoteGone(FileSyncEntry e)
    {
        var previous = e.State == SyncState.PendingRemoteGone ? e.RemoteGoneObservations : 0;
        var observations = previous + 1;
        if (observations >= 2)
        {
            return new SyncTransition(e with
            {
                State = SyncState.PendingRemoteGone,
                RemoteGoneObservations = observations,
            }, SyncDecision.DeleteLocal, "二次确认远端确实不存在 → 允许删除本地副本");
        }
        return new SyncTransition(e with
        {
            State = SyncState.PendingRemoteGone,
            RemoteGoneObservations = observations,
        }, SyncDecision.None, "首次观察远端不存在 → 保留本地,等二次确认");
    }

    /// <summary>
    /// 权限受限(403 <c>space_revoked</c> / 被冻结;401 由会话层给出同一处置)。
    ///
    /// **永远不产生删除本地的决策** —— 这正是 R-19 的落点。
    /// </summary>
    public static SyncTransition OnPermissionLimited(FileSyncEntry e, DateTimeOffset now)
    {
        var failures = e.PermissionProbeFailures;
        return new SyncTransition(e with
        {
            State = SyncState.PermissionLimited,
            // 注意:**不动** RemoteGoneObservations,也不动本地指纹 —— 本地文件一个字节都不碰
            NextProbeAt = now + ProbeDelayFor(failures),
        }, SyncDecision.ProbePermission,
            $"权限受限(403/401):保留本地,{ProbeDelayFor(failures).TotalSeconds:0}s 后探活");
    }

    /// <summary>探活结果:仍受限 → 退避升级;已恢复 → 重新对齐并补拉。</summary>
    public static SyncTransition OnPermissionProbe(FileSyncEntry e, bool restored, DateTimeOffset now)
    {
        if (!restored)
        {
            var failures = e.PermissionProbeFailures + 1;
            return new SyncTransition(e with
            {
                State = SyncState.PermissionLimited,
                PermissionProbeFailures = failures,
                NextProbeAt = now + ProbeDelayFor(failures),
            }, SyncDecision.ProbePermission,
                $"仍然受限 → 下次 {ProbeDelayFor(failures).TotalSeconds:0}s 后再探");
        }

        // 权限恢复:**自动补拉**。若本地在受限期间也改过,则两端都变了 → 冲突(交给裁决),
        // 否则按"远端是否变过"决定下载还是回到 Synced。
        var restoredEntry = e with
        {
            State = SyncState.Synced,
            PermissionProbeFailures = 0,
            NextProbeAt = default,
        };
        if (e.LocalDirty && e.RemoteDirty)
        {
            return new SyncTransition(restoredEntry with { State = SyncState.Conflict },
                SyncDecision.Resync, "权限恢复:两端在受限期间都改过 → 冲突(先重扫对齐)");
        }
        if (e.RemoteDirty)
        {
            return new SyncTransition(restoredEntry with { State = SyncState.RemoteModified },
                SyncDecision.Resync, "权限恢复:远端有变更 → 自动补拉");
        }
        if (e.LocalDirty)
        {
            return new SyncTransition(restoredEntry with { State = SyncState.LocalModified },
                SyncDecision.Resync, "权限恢复:本地有变更 → 重新对齐后上传");
        }
        return new SyncTransition(restoredEntry, SyncDecision.Resync, "权限恢复:重新对齐");
    }

    /// <summary>到点该不该探活(调用方按 NextProbeAt 判断,避免忙等探活)。</summary>
    public static bool ShouldProbePermission(FileSyncEntry e, DateTimeOffset now)
        => e.State == SyncState.PermissionLimited && now >= e.NextProbeAt;
}
