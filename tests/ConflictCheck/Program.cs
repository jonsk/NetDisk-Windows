// DE-D-12 行为检查器:冲突解决(version 裁决 + _conflict_ 确定性算法)。
//
// 用法:`dotnet run --project desktop/tests/ConflictCheck -c Release`
//
// 两类断言:
//   ① **命名与服务端逐字节一致** —— 读共享夹具 `desktop/testdata/conflict-cases.json`
//      (夹具由服务端实现生成)。名字不一致的后果是"客户端算出来能过、服务端 400",
//      而且只在超长/组合符/表情跨边界这些形态上出现,靠人肉比对查不全。
//   ② **裁决只认服务端 version**:两份只在 mtime 上不同的条目必须得到**完全相同**的决议
//      (mtime 只用于展示;拿它裁决会在时钟偏差时把新版本覆盖成旧版本)。
//      以及"两边内容都不许丢"(败方必须落成冲突副本)。

using System.Text.Json;
using NetDisk.ClientCore;
using NetDisk.SyncEngine.Sync;

var checks = new List<(string Name, Func<Task> Run)>
{
    ("① 冲突命名与服务端夹具逐字节一致", CheckFixtureMatchesAsync),
    ("② 确定性:同一输入两次得到同一个名字(重试幂等)", CheckDeterministicAsync),
    ("③ 总长 ≤240 字节,且扩展名被保留", CheckByteBudgetAsync),
    ("④ 超长名在 UTF-8 码点边界截断(不产生半个字)", CheckCodePointBoundaryAsync),
    ("⑤ 组合符:截断后 NFC 重归一", CheckNfcNormalizationAsync),
    ("⑥ 极端:后缀本身超限时退化为截断(仍 ≤ 上限)", CheckExtremeBudgetAsync),
    ("⑦ 自动策略:服务端 version 裁决 + 本地留副本", CheckAutoUsesServerVersionAsync),
    ("⑧ mtime 只展示:只有 mtime 不同 → 决议完全相同", CheckMtimeNeverDecidesAsync),
    ("⑨ 手动策略两边都不丢内容", CheckManualKeepsBothSidesAsync),
    ("⑩ 时间戳由调用方传入(不由裁决器现取) → 重试不产生重复副本", CheckCallerSuppliedTimestampAsync),
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
    Console.WriteLine($"全部通过:{checks.Count} 项 DE-D-12 行为断言(冲突裁决 + 冲突命名)");
    return 0;
}
Console.Error.WriteLine($"{failed}/{checks.Count} 项失败");
return 1;

// ---------------------------------------------------------------- 检查

static Task CheckFixtureMatchesAsync()
{
    var file = Locate("desktop/testdata/conflict-cases.json");
    using var doc = JsonDocument.Parse(File.ReadAllText(file));
    var cases = doc.RootElement.GetProperty("cases");
    Assert(cases.GetArrayLength() >= 10, $"夹具用例太少({cases.GetArrayLength()} 条),锁不住算法");

    var mismatched = 0;
    foreach (var c in cases.EnumerateArray())
    {
        var name = c.GetProperty("name").GetString()!;
        var ts = c.GetProperty("timestamp").GetString()!;
        var max = c.GetProperty("max_bytes").GetInt32();
        var expected = c.GetProperty("expected").GetString()!;
        var note = c.TryGetProperty("note", out var n) ? n.GetString() : "";
        var got = ConflictNaming.Suggest(name, ts, max);
        if (got != expected)
        {
            mismatched++;
            Console.Error.WriteLine(
                $"    ✗ note={note} max={max}\n      服务端={expected}\n      客户端={got}");
        }
    }
    Assert(mismatched == 0,
        $"冲突命名与服务端不一致 {mismatched} 条 —— 请改 NetDisk.ClientCore.ConflictNaming 对齐服务端(服务端是权威)");
    return Task.CompletedTask;
}

static Task CheckDeterministicAsync()
{
    var name = new string('报', 80) + ".xlsx";
    var a = ConflictNaming.Suggest(name, "20260912T101500");
    var b = ConflictNaming.Suggest(name, "20260912T101500");
    Assert(a == b, "同一输入必须得到同一个名字(否则每次重试都在用户目录里造一个新副本)");
    Assert(a.Contains("_conflict_20260912T101500"), $"名字里必须含确定的后缀,实际 {a}");
    return Task.CompletedTask;
}

static Task CheckByteBudgetAsync()
{
    foreach (var (name, max) in new (string, int)[]
             {
                 ("report.xlsx", 240),
                 (new string('a', 300) + ".txt", 240),
                 (new string('报', 100) + ".xlsx", 240),
                 ("季度报告.xlsx", 240),
                 ("名字.txt", 40),
             })
    {
        var got = ConflictNaming.Suggest(name, "20260912T101500", max);
        var bytes = ConflictNaming.ByteCount(got);
        Assert(bytes <= max, $"「{name[..Math.Min(12, name.Length)]}…」的冲突名 {bytes} 字节超过上限 {max}");
        if (name.Contains('.') && !name.StartsWith('.'))
        {
            var ext = name[name.LastIndexOf('.')..];
            if (ConflictNaming.ByteCount(ext) + 12 < max)
            {
                Assert(got.EndsWith(ext, StringComparison.Ordinal),
                    $"扩展名 {ext} 必须被保留(截掉扩展名会让文件「打不开」),实际 {got}");
            }
        }
    }
    return Task.CompletedTask;
}

static Task CheckCodePointBoundaryAsync()
{
    // 230 个 'a' + 4 字节表情 + .bin:预算恰好卡在表情中间
    var name = new string('a', 230) + "😀" + ".bin";
    var got = ConflictNaming.Suggest(name, "20260912T101500");

    Assert(ConflictNaming.ByteCount(got) <= 240, "不得超上限");
    Assert(!got.Contains('\uFFFD'), "截断必须落在码点边界(出现替换字符说明切了半个字)");
    // 结果必须能无损往返 UTF-8(半个码点做不到)
    var roundTrip = System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(got));
    Assert(roundTrip == got, "冲突名必须是合法 UTF-8");
    return Task.CompletedTask;
}

static Task CheckNfcNormalizationAsync()
{
    // 100 个分解形式的 é(e + U+0301):NFC 会合并,从而改变字节数
    var decomposed = string.Concat(Enumerable.Repeat("e\u0301", 100)) + ".txt";
    var got = ConflictNaming.Suggest(decomposed, "20260912T101500");

    Assert(!got.Contains('\u0301'), $"结果应当是 NFC 归一后的(不应残留组合符),实际 {got[..Math.Min(20, got.Length)]}…");
    Assert(ConflictNaming.ByteCount(got) <= 240, "NFC 归一后仍须 ≤240 字节");
    // 归一后再归一不变(幂等)
    Assert(ConflictNaming.Normalize(got) == got, "NFC 结果必须已经归一(否则每次拼接都会再变一次)");
    return Task.CompletedTask;
}

static Task CheckExtremeBudgetAsync()
{
    // maxBytes=10 而后缀本身 >10:退化为截断,但仍必须 ≤ 上限
    var got = ConflictNaming.Suggest("名字.txt", "20260101T000000", maxBytes: 10);
    Assert(ConflictNaming.ByteCount(got) <= 10, $"极端预算下也必须 ≤10 字节,实际 {ConflictNaming.ByteCount(got)}");
    var got2 = ConflictNaming.Suggest(new string('报', 50), "20260101T000000", maxBytes: 5);
    Assert(ConflictNaming.ByteCount(got2) <= 5, "极端预算(5 字节)也必须守住");
    return Task.CompletedTask;
}

static Task CheckAutoUsesServerVersionAsync()
{
    var entry = ConflictEntry(remoteVersion: 42, localMtime: 100, localSize: 10);
    var r = ConflictResolver.ResolveAuto(entry, "20260912T101500");

    Assert(r.Canonical == ConflictSide.Server, $"自动策略必须保留服务端版本,实际 {r.Canonical}");
    Assert(r.DecidedByServerVersion, "自动策略必须标注「由服务端 version 裁决」");
    Assert(r.Actions.Any(a => a.Kind == ConflictActionKind.Download), "应当把服务端版本拉下来");
    Assert(r.Actions.Any(a => a.Kind == ConflictActionKind.RenameLocalToConflictCopy),
        "本地那份必须先改名为冲突副本(**不能直接丢掉**:4.5 没有回收站)");
    Assert(!r.Actions.Any(a => a.Kind == ConflictActionKind.Upload),
        "自动策略不该上传本地版本(那是「保留本地」才做的事)");
    Assert(r.Actions[0].Kind == ConflictActionKind.RenameLocalToConflictCopy,
        "顺序很重要:必须先保住本地副本再下载(反过来会先覆盖本地)");
    return Task.CompletedTask;
}

static Task CheckMtimeNeverDecidesAsync()
{
    // mtime 取一个有辨识度的值:第一版用 1,而 `1` 在时间戳里出现过,断言形同虚设
    var a = ConflictEntry(remoteVersion: 7, localMtime: 1_234_567_890_123, localSize: 10);
    var b = ConflictEntry(remoteVersion: 7, localMtime: 999_999_999_999, localSize: 10);

    var ra = ConflictResolver.ResolveAuto(a, "20260912T101500");
    var rb = ConflictResolver.ResolveAuto(b, "20260912T101500");

    Assert(ra.Canonical == rb.Canonical && ra.Actions.Count == rb.Actions.Count,
        "只有 mtime 不同时,决议必须完全相同(mtime 只用于展示)");
    for (var i = 0; i < ra.Actions.Count; i++)
    {
        Assert(ra.Actions[i].Kind == rb.Actions[i].Kind && ra.Actions[i].TargetName == rb.Actions[i].TargetName,
            $"第 {i + 1} 个动作不能因为 mtime 而改变");
    }
    Assert(!ra.Reason.Contains(a.LocalMtimeTicks.ToString(), StringComparison.Ordinal),
        $"判据说明里不该出现 mtime(它不是裁决依据),实际「{ra.Reason}」");
    return Task.CompletedTask;
}

static Task CheckManualKeepsBothSidesAsync()
{
    var entry = ConflictEntry(remoteVersion: 5, localMtime: 1, localSize: 10);

    var keepLocal = ConflictResolver.ResolveManual(entry, ConflictSide.Local, "20260912T101500");
    Assert(keepLocal.Canonical == ConflictSide.Local, "手动保留本地 → 规范名上是本地版本");
    Assert(!keepLocal.DecidedByServerVersion, "手动策略不算「由服务端裁决」");
    Assert(keepLocal.Actions.Any(a => a.Kind == ConflictActionKind.RenameRemoteToConflictCopy),
        "远端那份必须**先改名保留**(否则接下来上传会把它覆盖掉,内容永久丢失)");
    Assert(keepLocal.Actions.Any(a => a.Kind == ConflictActionKind.Upload), "然后才上传本地版本");
    Assert(keepLocal.Actions[0].Kind == ConflictActionKind.RenameRemoteToConflictCopy,
        "顺序:先保住远端副本,再上传覆盖");
    Assert(keepLocal.Actions.Any(a => a.Note.Contains("base_version")),
        "上传必须带 base_version(乐观锁),否则并发写会静默覆盖");

    var keepServer = ConflictResolver.ResolveManual(entry, ConflictSide.Server, "20260912T101500");
    Assert(keepServer.Canonical == ConflictSide.Server, "手动保留服务端 → 规范名上是服务端版本");
    Assert(keepServer.Actions.Any(a => a.Kind == ConflictActionKind.RenameLocalToConflictCopy),
        "本地那份同样要保留成副本");
    return Task.CompletedTask;
}

static Task CheckCallerSuppliedTimestampAsync()
{
    var entry = ConflictEntry(remoteVersion: 3, localMtime: 1, localSize: 10);
    // 同一个冲突、两次重试:调用方传**同一个**已持久化的时间戳 → 同一个副本名
    var first = ConflictResolver.ResolveAuto(entry, "20260912T101500");
    var retry = ConflictResolver.ResolveAuto(entry, "20260912T101500");
    Assert(first.Actions[0].TargetName == retry.Actions[0].TargetName,
        "重试必须得到同一个副本名(否则用户目录里会堆积重复副本)");

    // 时间戳是**参数**而不是内部现取:传不同时间戳才会得到不同名字(证明它确实由调用方决定)
    var later = ConflictResolver.ResolveAuto(entry, "20260912T101600");
    Assert(later.Actions[0].TargetName != first.Actions[0].TargetName,
        "时间戳应当是调用方给的(它的持久化是「重试幂等」的前提)");
    return Task.CompletedTask;
}

// ---------------------------------------------------------------- 工具

static FileSyncEntry ConflictEntry(long remoteVersion, long localMtime, long localSize) => new()
{
    FileId = "file-1",
    LocalPath = @"C:\sync\季度报告.xlsx",
    State = SyncState.Conflict,
    LocalSize = localSize,
    LocalMtimeTicks = localMtime,
    RemoteVersion = remoteVersion,
    SyncedLocalSize = 1,
    SyncedLocalMtimeTicks = 1,
    SyncedRemoteVersion = 1,
};

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new Exception(message);
    }
}

static string Locate(string relative)
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        var candidate = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(candidate))
        {
            return candidate;
        }
        dir = dir.Parent;
    }
    throw new Exception($"找不到 {relative}(从 {AppContext.BaseDirectory} 向上找)");
}
