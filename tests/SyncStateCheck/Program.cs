// DE-D-09 行为检查器:同步状态机。
//
// 用法:`dotnet run --project desktop/tests/SyncStateCheck -c Release`
//
// 断言的重点不是"状态转得对",而是**什么情况下会删本地文件**:
//   - R-19:403/401(权限受限)**永远不产生 DeleteLocal**,且本地指纹一个字节都不动;
//   - 404/410 必须**二次确认**才允许删本地(第一次只标待确认);
//   - 穷举:所有状态 × 所有事件里,只有"二次确认的远端消失"才会给出 DeleteLocal。
// 另外把七种状态都跑一遍(验收①要求"全可测"),把退避阶梯钉成 30s→2min→10min。

using NetDisk.SyncEngine.Sync;

var now = new DateTimeOffset(2026, 9, 12, 10, 0, 0, TimeSpan.Zero);

var checks = new List<(string Name, Func<Task> Run)>
{
    ("① 七种状态全部可达且可断言", CheckAllStatesReachableAsync),
    ("② 上传/下载的完整往返(基线推进正确)", CheckRoundTripsAsync),
    ("③ 两端都改 → Conflict(不自动覆盖任何一边)", CheckConflictAsync),
    ("④ 403/401 永不删本地,且本地指纹不变", CheckForbiddenNeverDeletesAsync),
    ("⑤ 权限受限退避 30s → 2min → 10min 并封顶", CheckProbeBackoffAsync),
    ("⑥ 权限恢复 → 自动补拉(Resync)", CheckPermissionRestoredAsync),
    ("⑦ 404/410 首次不删、二次才删", CheckRemoteGoneNeedsTwoObservationsAsync),
    ("⑧ 远端又出现 → 待确认消失作废(事件乱序不误删)", CheckRemoteReappearsClearsGoneAsync),
    ("⑨ 权限受限期间本地改动不上传、但被记住", CheckLocalChangeWhileLimitedAsync),
    ("⑩ 穷举:只有二次确认的远端消失会产生 DeleteLocal", CheckDeleteLocalIsExhaustivelyGuardedAsync),
    ("⑪ 中间发生过本地改动 → 残留计数不得凑成二次确认", CheckStaleGoneCounterAsync),
};

var failed = 0;
foreach (var (name, run) in checks)
{
    try
    {
        await run();
        Console.WriteLine($"✓ {name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"✗ {name}: {ex.Message}");
    }
}

Console.WriteLine();
if (failed == 0)
{
    Console.WriteLine($"全部通过:{checks.Count} 项 DE-D-09 行为断言(同步状态机)");
    return 0;
}
Console.Error.WriteLine($"{failed}/{checks.Count} 项失败");
return 1;

// ---------------------------------------------------------------- 检查

Task CheckAllStatesReachableAsync()
{
    var e = Fresh();
    var seen = new HashSet<SyncState> { e.State };

    seen.Add(SyncStateMachine.OnLocalChanged(e, 20, 200).Entry.State);
    seen.Add(SyncStateMachine.OnRemoteChanged(e, 5).Entry.State);
    seen.Add(SyncStateMachine.OnLocalChanged(e, 20, 200).Entry.State); // 冲突的前置
    var conflictCandidate = e with { SyncedRemoteVersion = 1, RemoteVersion = 2 };
    seen.Add(SyncStateMachine.OnLocalChanged(conflictCandidate, 20, 200).Entry.State);
    seen.Add(SyncStateMachine.OnUploadStarted(e).Entry.State);
    seen.Add(SyncStateMachine.OnDownloadStarted(e).Entry.State);
    seen.Add(SyncStateMachine.OnRemoteGone(e).Entry.State);
    seen.Add(SyncStateMachine.OnPermissionLimited(e, now).Entry.State);

    foreach (var s in Enum.GetValues<SyncState>())
    {
        Assert(seen.Contains(s), $"状态 {s} 在用例里不可达 —— 验收①要求七种状态全可测");
    }
    Assert(seen.Count == 8, $"应当覆盖 8 种状态,实际 {seen.Count}");
    return Task.CompletedTask;
}

static Task CheckRoundTripsAsync()
{
    var e = Fresh();

    // 本地改 → 上传 → 同步完成
    var t1 = SyncStateMachine.OnLocalChanged(e, 20, 200);
    Assert(t1.Entry.State == SyncState.LocalModified && t1.Decision == SyncDecision.Upload,
        $"本地改应转 LocalModified/Upload,实际 {t1.Entry.State}/{t1.Decision}");
    var t2 = SyncStateMachine.OnUploadStarted(t1.Entry);
    Assert(t2.Entry.State == SyncState.Uploading, $"应转 Uploading,实际 {t2.Entry.State}");
    var t3 = SyncStateMachine.OnUploadFinished(t2.Entry, newRemoteVersion: 9);
    Assert(t3.Entry.State == SyncState.Synced, $"上传完成应回到 Synced,实际 {t3.Entry.State}");
    Assert(t3.Entry.SyncedRemoteVersion == 9 && t3.Entry.RemoteVersion == 9, "基线应推进到新版本");
    Assert(!t3.Entry.LocalDirty, "上传完成后本地不该仍然「脏」(否则会反复上传同一个文件)");

    // 远端改 → 下载 → 同步完成(本地指纹取**落盘后**的实际值)
    var t4 = SyncStateMachine.OnRemoteChanged(t3.Entry, 10);
    Assert(t4.Entry.State == SyncState.RemoteModified && t4.Decision == SyncDecision.Download,
        $"远端改应转 RemoteModified/Download,实际 {t4.Entry.State}/{t4.Decision}");
    var t5 = SyncStateMachine.OnDownloadStarted(t4.Entry);
    Assert(t5.Entry.State == SyncState.Downloading, $"应转 Downloading,实际 {t5.Entry.State}");
    var t6 = SyncStateMachine.OnDownloadFinished(t5.Entry, localSize: 33, localMtimeTicks: 333);
    Assert(t6.Entry.State == SyncState.Synced, "下载完成应回到 Synced");
    Assert(!t6.Entry.LocalDirty && !t6.Entry.RemoteDirty, "下载完成后两端都不该仍脏");
    return Task.CompletedTask;
}

static Task CheckConflictAsync()
{
    var e = Fresh();
    // 远端先变,本地再变 → 冲突
    var remoteFirst = SyncStateMachine.OnRemoteChanged(e, 5);
    var t = SyncStateMachine.OnLocalChanged(remoteFirst.Entry, 20, 200);
    Assert(t.Entry.State == SyncState.Conflict, $"两端都改应转 Conflict,实际 {t.Entry.State}");
    Assert(t.Decision == SyncDecision.None, $"冲突**不能**自动决定覆盖哪边,实际给了 {t.Decision}");

    // 本地先变,远端再变 → 同样冲突
    var localFirst = SyncStateMachine.OnLocalChanged(e, 20, 200);
    var t2 = SyncStateMachine.OnRemoteChanged(localFirst.Entry, 5);
    Assert(t2.Entry.State == SyncState.Conflict, "顺序反过来也必须是冲突");
    Assert(t2.Decision == SyncDecision.None, "冲突不自动决策");
    return Task.CompletedTask;
}

Task CheckForbiddenNeverDeletesAsync()
{
    var e = Fresh();
    var t = SyncStateMachine.OnPermissionLimited(e, now);
    Assert(t.Entry.State == SyncState.PermissionLimited, $"应转 PermissionLimited,实际 {t.Entry.State}");
    Assert(t.Decision == SyncDecision.ProbePermission, $"应安排探活,实际 {t.Decision}");
    Assert(t.Entry.LocalSize == e.LocalSize && t.Entry.LocalMtimeTicks == e.LocalMtimeTicks,
        "权限受限**不能改动**本地指纹(R-19:本地文件一个字节都不碰)");
    Assert(t.Entry.RemoteGoneObservations == e.RemoteGoneObservations,
        "权限受限不能被当成「远端消失」的观察(否则两次 403 会凑成「二次确认」从而删本地)");

    // 反复受限也不许出现删除倾向
    var cur = t.Entry;
    for (var i = 0; i < 5; i++)
    {
        var next = SyncStateMachine.OnPermissionProbe(cur, restored: false, now);
        Assert(next.Decision != SyncDecision.DeleteLocal, "反复受限绝不能演变成删除本地");
        cur = next.Entry;
    }
    Assert(cur.State == SyncState.PermissionLimited, "一直受限就一直是 PermissionLimited");
    return Task.CompletedTask;
}

Task CheckProbeBackoffAsync()
{
    Assert(SyncStateMachine.ProbeBackoff[0] == TimeSpan.FromSeconds(30), "第一次探活退避应为 30s");
    Assert(SyncStateMachine.ProbeBackoff[1] == TimeSpan.FromMinutes(2), "第二次应为 2min");
    Assert(SyncStateMachine.ProbeBackoff[2] == TimeSpan.FromMinutes(10), "第三次及以后应为 10min");
    Assert(SyncStateMachine.ProbeDelayFor(3) == TimeSpan.FromMinutes(10), "封顶 10min");
    Assert(SyncStateMachine.ProbeDelayFor(99) == TimeSpan.FromMinutes(10), "再多次也是 10min");
    Assert(SyncStateMachine.ProbeDelayFor(0) == TimeSpan.FromSeconds(30), "首次 30s");

    var e = Fresh();
    var t = SyncStateMachine.OnPermissionLimited(e, now);
    Assert(t.Entry.NextProbeAt == now + TimeSpan.FromSeconds(30), "首次受限应安排在 30s 后探活");
    Assert(!SyncStateMachine.ShouldProbePermission(t.Entry, now), "刚受限时不该立刻探活(要退避)");
    Assert(SyncStateMachine.ShouldProbePermission(t.Entry, now + TimeSpan.FromSeconds(31)), "到点该探活");

    var fail1 = SyncStateMachine.OnPermissionProbe(t.Entry, false, now + TimeSpan.FromSeconds(30));
    Assert(fail1.Entry.NextProbeAt == now + TimeSpan.FromSeconds(30) + TimeSpan.FromMinutes(2),
        "第二次失败应退到 2min");
    var fail2 = SyncStateMachine.OnPermissionProbe(fail1.Entry, false, fail1.Entry.NextProbeAt);
    Assert(fail2.Entry.NextProbeAt == fail1.Entry.NextProbeAt + TimeSpan.FromMinutes(10),
        "第三次失败应退到 10min");
    var fail3 = SyncStateMachine.OnPermissionProbe(fail2.Entry, false, fail2.Entry.NextProbeAt);
    Assert(fail3.Entry.NextProbeAt == fail2.Entry.NextProbeAt + TimeSpan.FromMinutes(10),
        "之后一直 10min(封顶)");
    return Task.CompletedTask;
}

Task CheckPermissionRestoredAsync()
{
    // 受限期间远端改了 → 恢复后自动补拉
    var limited = SyncStateMachine.OnPermissionLimited(Fresh(), now).Entry;
    var remoteChanged = limited with { RemoteVersion = limited.SyncedRemoteVersion + 1 };
    var t = SyncStateMachine.OnPermissionProbe(remoteChanged, restored: true, now);
    Assert(t.Decision == SyncDecision.Resync, $"恢复后应重新对齐,实际 {t.Decision}");
    Assert(t.Entry.State == SyncState.RemoteModified, $"远端有变更 → 应转 RemoteModified,实际 {t.Entry.State}");
    Assert(t.Entry.PermissionProbeFailures == 0 && t.Entry.NextProbeAt == default,
        "恢复后应清掉退避状态");

    // 受限期间本地改了 → 恢复后上传
    var localChanged = limited with { LocalMtimeTicks = limited.LocalMtimeTicks + 1 };
    var t2 = SyncStateMachine.OnPermissionProbe(localChanged, restored: true, now);
    Assert(t2.Entry.State == SyncState.LocalModified, $"本地有变更 → 应转 LocalModified,实际 {t2.Entry.State}");
    Assert(t2.Decision == SyncDecision.Resync, "恢复后先重新对齐再上传");

    // 两端都改 → 冲突
    var both = limited with
    {
        LocalMtimeTicks = limited.LocalMtimeTicks + 1,
        RemoteVersion = limited.SyncedRemoteVersion + 1,
    };
    var t3 = SyncStateMachine.OnPermissionProbe(both, restored: true, now);
    Assert(t3.Entry.State == SyncState.Conflict, "受限期间两端都改 → 冲突");
    return Task.CompletedTask;
}

static Task CheckRemoteGoneNeedsTwoObservationsAsync()
{
    var e = Fresh();

    var first = SyncStateMachine.OnRemoteGone(e);
    Assert(first.Entry.State == SyncState.PendingRemoteGone,
        $"首次远端消失应转 PendingRemoteGone,实际 {first.Entry.State}");
    Assert(first.Decision == SyncDecision.None,
        $"**首次 404/410 绝不能删本地**(单次 404 可能只是事件乱序/路径抖动),实际 {first.Decision}");
    Assert(first.Entry.RemoteGoneObservations == 1, "应记一次观察");
    Assert(first.Entry.LocalSize == e.LocalSize, "首次观察不得改动本地指纹");

    var second = SyncStateMachine.OnRemoteGone(first.Entry);
    Assert(second.Decision == SyncDecision.DeleteLocal,
        $"二次确认后仍不存在 → 才允许删本地,实际 {second.Decision}");
    Assert(second.Entry.RemoteGoneObservations == 2, "观察计数应为 2");
    return Task.CompletedTask;
}

static Task CheckRemoteReappearsClearsGoneAsync()
{
    var first = SyncStateMachine.OnRemoteGone(Fresh());
    // 事件乱序:先收到"删除",随后又收到"远端有新版本"(其实没删)
    var back = SyncStateMachine.OnRemoteChanged(first.Entry, 7);
    Assert(back.Entry.RemoteGoneObservations == 0,
        "远端又出现 → 必须清掉「待确认消失」的计数(否则下一个 404 会被凑成二次确认而误删本地)");
    Assert(back.Entry.State != SyncState.PendingRemoteGone, "不应仍停在待确认消失");
    Assert(back.Decision == SyncDecision.Download, "远端有内容 → 应下载");
    return Task.CompletedTask;
}

Task CheckLocalChangeWhileLimitedAsync()
{
    var limited = SyncStateMachine.OnPermissionLimited(Fresh(), now).Entry;
    var t = SyncStateMachine.OnLocalChanged(limited, 999, 9999);
    Assert(t.Decision == SyncDecision.None,
        $"权限受限期间不该尝试上传(必然被 403 拒),实际 {t.Decision}");
    Assert(t.Entry.LocalSize == 999 && t.Entry.LocalMtimeTicks == 9999,
        "但必须把本地变化记下来(恢复后才能决定补拉还是上传)");
    Assert(t.Entry.LocalDirty, "受限期间的本地改动应当被标记为脏");
    Assert(t.Entry.State == SyncState.PermissionLimited, "状态仍应是权限受限");
    return Task.CompletedTask;
}

Task CheckStaleGoneCounterAsync()
{
    // 这条路径是穷举断言逼出来的真实缺陷:计数被带过状态边界后,
    // "远端消失(1)→ 用户本地改了文件(2)→ 又收到一次远端消失(凑成 2)"
    // 会把用户**刚编辑的内容**删掉 —— 而第二条消息说的还是第一次那个旧事实。
    var first = SyncStateMachine.OnRemoteGone(Fresh());
    Assert(first.Decision == SyncDecision.None, "前置:首次不删");

    var localEdit = SyncStateMachine.OnLocalChanged(first.Entry, 999, 9999);
    Assert(localEdit.Entry.State == SyncState.LocalModified,
        $"本地改动应转 LocalModified,实际 {localEdit.Entry.State}");

    var second = SyncStateMachine.OnRemoteGone(localEdit.Entry);
    Assert(second.Decision != SyncDecision.DeleteLocal,
        "本地改动之后的一次远端消失**只能算首次观察**,不能因为残留计数就删掉用户刚改的内容");
    Assert(second.Entry.RemoteGoneObservations == 1,
        $"跨状态后计数必须重新从 1 开始,实际 {second.Entry.RemoteGoneObservations}");

    // 反过来:连续两次"真的没有别的事发生"的观察,仍然要能删(不能把保护做成永不删)
    var again = SyncStateMachine.OnRemoteGone(second.Entry);
    Assert(again.Decision == SyncDecision.DeleteLocal, "同一状态下的第二次观察才允许删本地");
    return Task.CompletedTask;
}

Task CheckDeleteLocalIsExhaustivelyGuardedAsync()
{
    // 穷举:所有状态 × 所有事件,"能删本地"的只允许是 OnRemoteGone 的第二次。
    // 这条断言是 R-19 的**结构性**保证:以后新增一个事件/状态,如果它顺手产生了
    // DeleteLocal,这里立刻会红 —— 而不是等到用户发现文件没了。
    var states = Enum.GetValues<SyncState>();
    var deleteSources = new List<string>();

    foreach (var state in states)
    {
        var e = Fresh() with
        {
            State = state,
            RemoteGoneObservations = 1, // 最危险的前置:已经观察过一次"消失"
        };

        var outcomes = new (string Name, SyncTransition T)[]
        {
            ("OnLocalChanged", SyncStateMachine.OnLocalChanged(e, 5, 5)),
            ("OnRemoteChanged", SyncStateMachine.OnRemoteChanged(e, 42)),
            ("OnUploadStarted", SyncStateMachine.OnUploadStarted(e)),
            ("OnUploadFinished", SyncStateMachine.OnUploadFinished(e, 42)),
            ("OnDownloadStarted", SyncStateMachine.OnDownloadStarted(e)),
            ("OnDownloadFinished", SyncStateMachine.OnDownloadFinished(e, 5, 5)),
            ("OnRemoteGone", SyncStateMachine.OnRemoteGone(e)),
            ("OnPermissionLimited", SyncStateMachine.OnPermissionLimited(e, now)),
            ("ProbeOk", SyncStateMachine.OnPermissionProbe(e, true, now)),
            ("ProbeFail", SyncStateMachine.OnPermissionProbe(e, false, now)),
        };

        foreach (var (name, t) in outcomes)
        {
            if (t.Decision == SyncDecision.DeleteLocal)
            {
                deleteSources.Add($"{state}.{name}");
            }
        }
    }

    Assert(deleteSources.Count == 1 && deleteSources[0] == "PendingRemoteGone.OnRemoteGone",
        "只有「已观察过一次消失」的条目再次收到远端消失才允许删本地,实际:" +
        string.Join(", ", deleteSources));
    return Task.CompletedTask;
}

// ---------------------------------------------------------------- 工具

/// <summary>一条已同步的条目(基线:本地 10 字节 / mtime=100 / 远端 version=3)。</summary>
static FileSyncEntry Fresh() => new()
{
    FileId = "file-1",
    LocalPath = @"C:\sync\a.txt",
    State = SyncState.Synced,
    LocalSize = 10,
    LocalMtimeTicks = 100,
    RemoteVersion = 3,
    SyncedLocalSize = 10,
    SyncedLocalMtimeTicks = 100,
    SyncedRemoteVersion = 3,
};

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new Exception(message);
    }
}
