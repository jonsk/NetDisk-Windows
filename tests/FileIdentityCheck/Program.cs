// DE-D-11 行为检查器:FileIdInfo 识别改名 + 失效降级。
//
// 用法:`dotnet run --project desktop/tests/FileIdentityCheck -c Release`
//
// 验收①「10GB 文件改名不重传(走 MOVE)」在这里被拆成两条:
//   - 判定结果是 Move(而不是"删了再传");
//   - **哈希没有被算过** —— 这一条才是"不重传"的真正证据:10GB 文件若为了改个名被读一遍,
//     即使用户没重传,磁盘 IO 也已经付出代价了。

using NetDisk.SyncEngine.Sync;

// 10GB:验收①里的那个「大文件」。用它而不真的造一个 10GB 文件是为了跑得快;
// 而「不重传」的真正证据是下面那条「哈希没被算过」的断言 —— 与文件多大无关。
const long TenGigabytes = 10L * 1024 * 1024 * 1024;

var checks = new List<(string Name, Func<Task> Run)>
{
    ("① 身份相同 → MOVE,且**不计算哈希**(10GB 不因改名被读)", CheckSameIdentityNoHashAsync),
    ("② 10GB 改名:判定为 MOVE,绝不出现上传", CheckLargeFileRenameAsync),
    ("③ 跨卷(卷序列号不同)→ 不算改名", CheckCrossVolumeAsync),
    ("④ 身份失效但内容一致 → 降级比哈希后仍按 MOVE", CheckHashFallbackMoveAsync),
    ("⑤ 身份失效且内容不同 → 不是同一个文件", CheckHashFallbackNotRenameAsync),
    ("⑥ 大小不同 → 直接判否(连哈希都不算)", CheckSizeMismatchAsync),
    ("⑦ 身份不可用且没有哈希能力 → 保守判否(宁可重传不可错判)", CheckNoHashAvailableAsync),
    ("⑧ 真实 Win32:改名前后文件身份不变", CheckRealIdentityAcrossRenameAsync),
    ("⑨ 真实 Win32:两个不同文件身份不同;目录身份**跨改名不变**且互不相同", CheckRealIdentityDistinctAsync),
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
    Console.WriteLine($"全部通过:{checks.Count} 项 DE-D-11 行为断言(FileIdInfo 改名识别 + 失效降级)");
    return 0;
}
Console.Error.WriteLine($"{failed}/{checks.Count} 项失败");
return 1;

// ---------------------------------------------------------------- 检查

static Task CheckSameIdentityNoHashAsync()
{
    var id = new FileIdentity(111, 222, 333);
    var hashed = 0;
    var d = new RenameDetector(new FakeIdentity(), _ => { hashed++; return "same"; });

    var decision = d.Detect(
        new FileFingerprint(@"C:\sync\old.bin", TenGigabytes, id),
        new FileFingerprint(@"C:\sync\new.bin", TenGigabytes, id));

    Assert(decision.Verdict == RenameVerdict.Move, $"身份相同应判 MOVE,实际 {decision.Verdict}");
    Assert(hashed == 0, $"身份相同时**不该算哈希**(10GB 文件为了改个名被读一遍是不可接受的),实际算了 {hashed} 次");
    Assert(!decision.HashComputed, "判据里应标明没算哈希");
    return Task.CompletedTask;
}

static Task CheckLargeFileRenameAsync()
{
    // 10GB:若实现把它当"删除 + 新建",用户就要重传 10GB
    var id = new FileIdentity(7, 8, 9);
    var d = new RenameDetector(new FakeIdentity());
    var decision = d.Detect(
        new FileFingerprint(@"C:\sync\big.iso", TenGigabytes, id),
        new FileFingerprint(@"C:\sync\renamed\big.iso", TenGigabytes, id));

    Assert(decision.Verdict == RenameVerdict.Move,
        $"10GB 改名必须是 MOVE(否则要重传 10GB),实际 {decision.Verdict}({decision.Basis})");
    return Task.CompletedTask;
}

static Task CheckCrossVolumeAsync()
{
    // 跨卷移动 = 复制 + 删除:卷序列号变了 → 只能当「新文件」。
    // **必须提供哈希**:否则这条用例会被"没有哈希能力 → 保守判否"那条路径蒙过去,
    // 于是"不判跨卷"的实现照样绿(第一版就是这样,反向验证把它抓出来了)。
    // 而跨卷移动最常见的形态恰恰是**内容完全相同**(就是把文件搬到了另一个盘),
    // 若只看哈希就会误判成改名 —— 这正是卷序列号判据存在的理由。
    var d = new RenameDetector(new FakeIdentity(), _ => "SAME-CONTENT");
    var decision = d.Detect(
        new FileFingerprint(@"C:\sync\a.bin", 100, new FileIdentity(1, 10, 20)),
        new FileFingerprint(@"D:\sync\a.bin", 100, new FileIdentity(2, 10, 20)));

    Assert(decision.Verdict == RenameVerdict.NotARename,
        $"跨卷不该判成 MOVE(底层是复制+删除),实际 {decision.Verdict}");
    Assert(!decision.HashComputed, "卷序列号不同时连哈希都不用算");
    return Task.CompletedTask;
}

static Task CheckHashFallbackMoveAsync()
{
    // 杀毒软件重写 / 备份还原:同一个路径指向了新的文件 ID,但内容其实没变。
    // 此时若直接判"不是改名",用户会看到"改个名重传了几个 G"。
    var hashed = 0;
    var d = new RenameDetector(new FakeIdentity(), path => { hashed++; return "SAME-CONTENT"; });

    var decision = d.Detect(
        new FileFingerprint(@"C:\sync\old.bin", 5_000_000_000, new FileIdentity(1, 1, 1)),
        new FileFingerprint(@"C:\sync\new.bin", 5_000_000_000, new FileIdentity(1, 9, 9)));

    Assert(decision.Verdict == RenameVerdict.Move,
        $"文件 ID 失效但内容一致 → 仍应按 MOVE(避免重传),实际 {decision.Verdict}");
    Assert(decision.HashComputed && hashed == 2, $"降级路径应当算过两边的哈希,实际 {hashed} 次");
    Assert(d.HashFallbacks == 1, $"降级次数应被记录(诊断这台机器上文件 ID 是否不稳),实际 {d.HashFallbacks}");
    return Task.CompletedTask;
}

static Task CheckHashFallbackNotRenameAsync()
{
    var d = new RenameDetector(new FakeIdentity(), _ => Guid.NewGuid().ToString("N"));
    var decision = d.Detect(
        new FileFingerprint(@"C:\sync\a.bin", 100, new FileIdentity(1, 1, 1)),
        new FileFingerprint(@"C:\sync\b.bin", 100, new FileIdentity(1, 2, 2)));

    Assert(decision.Verdict == RenameVerdict.NotARename,
        $"内容不同 → 不是同一个文件,实际 {decision.Verdict}");
    Assert(decision.HashComputed, "这条路径应当真的比过内容");
    return Task.CompletedTask;
}

static Task CheckSizeMismatchAsync()
{
    var hashed = 0;
    var d = new RenameDetector(new FakeIdentity(), _ => { hashed++; return "x"; });
    var decision = d.Detect(
        new FileFingerprint(@"C:\sync\a.bin", 100, new FileIdentity(1, 1, 1)),
        new FileFingerprint(@"C:\sync\b.bin", 200, new FileIdentity(1, 2, 2)));

    Assert(decision.Verdict == RenameVerdict.NotARename, $"大小不同应判否,实际 {decision.Verdict}");
    Assert(hashed == 0, $"大小不同时不该算哈希(白读一遍内容),实际 {hashed} 次");
    return Task.CompletedTask;
}

static Task CheckNoHashAvailableAsync()
{
    // 没有哈希能力时**必须保守**:宁可多传一次,也不能把两个不同文件错判成同一个改名
    // (错判的后果是"远端被改名成另一个文件的路径",数据就乱了)
    var d = new RenameDetector(new FakeIdentity());
    var decision = d.Detect(
        new FileFingerprint(@"C:\sync\a.bin", 100, new FileIdentity(1, 1, 1)),
        new FileFingerprint(@"C:\sync\b.bin", 100, new FileIdentity(1, 2, 2)));

    Assert(decision.Verdict == RenameVerdict.NotARename,
        $"身份不可靠且无哈希 → 必须保守判否,实际 {decision.Verdict}");
    Assert(decision.Basis.Contains("保守"), $"判据应说明是保守判否,实际「{decision.Basis}」");
    return Task.CompletedTask;
}

static Task CheckRealIdentityAcrossRenameAsync()
{
    var root = Path.Combine(Path.GetTempPath(), "netdisk-id-check", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var provider = new Win32FileIdentityProvider();
        var before = Path.Combine(root, "before.bin");
        File.WriteAllBytes(before, new byte[1024]);

        var idBefore = provider.TryGet(before);
        Assert(idBefore is not null, "真实 Win32 实现应当能取到文件身份(说明 P/Invoke 是通的)");

        var after = Path.Combine(root, "renamed.bin");
        File.Move(before, after);
        var idAfter = provider.TryGet(after);
        Assert(idAfter is not null, "改名后仍应能取到身份");
        Assert(idBefore!.Value.Equals(idAfter!.Value),
            "同卷改名后文件身份必须不变 —— 这正是「10GB 改名不重传」的依据");

        // 端到端:用**真实**身份读取器做一次判定
        var d = new RenameDetector(provider);
        var decision = d.Detect(
            new FileFingerprint(before, 1024, idBefore),
            new FileFingerprint(after, 1024, idAfter));
        Assert(decision.Verdict == RenameVerdict.Move, $"真实身份判定应为 MOVE,实际 {decision.Verdict}");
        Assert(!decision.HashComputed, "真实路径也不该算哈希");
    }
    finally
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }
    return Task.CompletedTask;
}

static Task CheckRealIdentityDistinctAsync()
{
    var root = Path.Combine(Path.GetTempPath(), "netdisk-id-check2", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var provider = new Win32FileIdentityProvider();
        var a = Path.Combine(root, "a.bin");
        var b = Path.Combine(root, "b.bin");
        File.WriteAllBytes(a, new byte[10]);
        File.WriteAllBytes(b, new byte[10]);

        var ia = provider.TryGet(a);
        var ib = provider.TryGet(b);
        Assert(ia is not null && ib is not null, "两个新文件都应当能取到身份");
        Assert(!ia!.Value.Equals(ib!.Value), "不同文件的身份必须不同(否则会把 A 的改名认成 B 的)");
        Assert(ia.Value.SameVolume(ib.Value), "同一卷上的两个文件卷序列号应当相同");

        // 目录也要能取到身份:重命名目录同样不能触发整棵子树重传
        var dir = Path.Combine(root, "d1");
        Directory.CreateDirectory(dir);
        var idDir = provider.TryGet(dir);
        Assert(idDir is not null,
            "目录也必须能取到身份(重命名目录不该让整棵子树重传)");

        // 目录身份必须**跨改名/移动不变** —— 这是"目录级改名/移动传播"(SyncHost.DetectDirRenames)
        // 的唯一判据。少了这条,那个功能就只能靠"看起来对"来交付:
        // 一旦身份在改名后变了,引擎会把一次文件夹改名当成"删掉整棵子树 + 重新上传一堆文件"。
        var dirMoved = Path.Combine(root, "d2");
        Directory.Move(dir, dirMoved);
        var idDirMoved = provider.TryGet(dirMoved);
        Assert(idDirMoved is not null, "目录改名后仍应能取到身份");
        Assert(idDir!.Value.Equals(idDirMoved!.Value),
            "同卷内目录改名后身份必须不变 —— 这正是「文件夹改名不重传整棵子树」的依据");

        // 两个不同目录的身份必须不同(否则会把 A 的改名认成 B 的)
        var dirOther = Path.Combine(root, "d3");
        Directory.CreateDirectory(dirOther);
        var idDirOther = provider.TryGet(dirOther);
        Assert(idDirOther is not null && !idDirOther.Value.Equals(idDirMoved.Value),
            "不同目录的身份必须不同(否则会把 A 的改名认到 B 头上)");

        // 不存在的路径 → null(而不是抛异常)
        Assert(provider.TryGet(Path.Combine(root, "missing.bin")) is null, "不存在的路径应返回 null");
    }
    finally
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }
    return Task.CompletedTask;
}

// ---------------------------------------------------------------- 工具

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new Exception(message);
    }
}

/// <summary>身份提供者桩(真值由指纹自己带,这里只满足接口)。</summary>
sealed class FakeIdentity : IFileIdentityProvider
{
    public FileIdentity? TryGet(string path) => null;
}
