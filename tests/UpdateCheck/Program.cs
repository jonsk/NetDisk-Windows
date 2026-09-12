// DE-D-19 行为检查器:自动升级序列(顺序 / 排空 / 失败回滚 / 更新器命令行)。
//
// 用法:`dotnet run --project desktop/tests/UpdateCheck -c Release`
//
// 升级流程是**一锤子买卖**:不可能靠人工反复演练来验证"顺序对不对、失败时会不会回滚"。
// 所以这里把每一步做成可注入的假宿主,把顺序、超时、失败路径全部跑一遍。

using NetDisk.SyncEngine.Update;

var checks = new List<(string Name, Func<Task> Run)>
{
    ("① 顺序:暂停 → 排空 → 迁移 → 重启", CheckOrderAsync),
    ("② 排空是「等」:在飞任务跑完才迁移", CheckDrainWaitsAsync),
    ("③ 排空超时 → 放弃升级,且**不搬迁**(不硬杀任务)", CheckDrainTimeoutAsync),
    ("④ 迁移失败 → 回滚 + 恢复同步 + 不重启", CheckMigrateFailureAsync),
    ("⑤ 重启(启动更新器)失败 → 回滚 + 恢复同步", CheckRestartFailureAsync),
    ("⑥ 暂停失败 → 什么都不做(尤其不迁移)", CheckPauseFailureAsync),
    ("⑦ 失败路径**永远**恢复同步(不留卡住的客户端)", CheckAlwaysResumesAsync),
    ("⑧ 回滚也失败时,恢复同步仍然要做", CheckResumeEvenIfRollbackFailsAsync),
    ("⑨ 更新器命令行:MSI 路径带引号 + /qn 静默 + --relaunch", CheckUpdaterCommandAsync),
    ("⑩ 升级失败不抛异常给调用方(UI 要能如实提示)", CheckNoThrowAsync),
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
    Console.WriteLine($"全部通过:{checks.Count} 项 DE-D-19 行为断言(升级序列)");
    return 0;
}
Console.Error.WriteLine($"{failed}/{checks.Count} 项失败");
return 1;

// ---------------------------------------------------------------- 检查

static async Task CheckOrderAsync()
{
    var host = new FakeHost();
    var orch = new UpdateOrchestrator(host);
    var outcome = await orch.RunAsync();

    Assert(outcome.Upgraded, "顺利路径应当升级成功,实际 " + outcome.Reason);
    Assert(orch.Executed.SequenceEqual(new[]
    {
        UpdateStep.PauseSync, UpdateStep.DrainQueue, UpdateStep.MigrateState, UpdateStep.Restart,
    }), "升级顺序必须是 暂停 → 排空 → 迁移 → 重启,实际 " + string.Join("→", orch.Executed));

    // 交给更新器之后由新进程接管,本进程不该再"恢复同步"(那样会与更新器打架)
    Assert(!host.Resumed, "成功交给更新器后不该在本进程恢复同步");
}

static async Task CheckDrainWaitsAsync()
{
    // 在飞任务:排空要**等**它跑完(而不是清空队列就往下走)
    var transferDone = false;
    var host = new FakeHost
    {
        DrainImpl = async (timeout, ct) =>
        {
            await Task.Delay(120, ct); // 模拟一个还在跑的分片
            transferDone = true;
            return true;
        },
    };
    var orch = new UpdateOrchestrator(host);
    await orch.RunAsync();

    Assert(transferDone, "迁移状态库之前,在飞任务必须已经结束(否则会留下半截暂存文件)");
    var drainIdx = orch.Executed.IndexOf(UpdateStep.DrainQueue);
    var migrateIdx = orch.Executed.IndexOf(UpdateStep.MigrateState);
    Assert(drainIdx >= 0 && migrateIdx > drainIdx, "排空必须发生在迁移之前");
}

static async Task CheckDrainTimeoutAsync()
{
    var host = new FakeHost { DrainImpl = (_, _) => Task.FromResult(false) }; // 超时
    var orch = new UpdateOrchestrator(host, new UpdatePolicy { DrainTimeout = TimeSpan.FromSeconds(1) });
    var outcome = await orch.RunAsync();

    Assert(!outcome.Upgraded, "排空超时必须放弃本次升级");
    Assert(outcome.FailedAt == UpdateStep.DrainQueue, "失败点应当是排空,实际 " + outcome.FailedAt);
    Assert(!orch.Executed.Contains(UpdateStep.MigrateState),
        "没排空就迁移状态库是危险的(在飞任务仍在写,迁移可能与它并发)");
    Assert(!orch.Executed.Contains(UpdateStep.Restart), "没排空不该重启");
    Assert(host.Resumed, "放弃升级后必须恢复同步");
    Assert(outcome.Reason.Contains("不硬杀"), "原因里要说明为什么不硬杀任务,实际 " + outcome.Reason);
}

static async Task CheckMigrateFailureAsync()
{
    var host = new FakeHost { MigrateShouldFail = true };
    var orch = new UpdateOrchestrator(host);
    var outcome = await orch.RunAsync();

    Assert(!outcome.Upgraded, "迁移失败必须放弃升级(这正是「先迁移再重启」的意义)");
    Assert(outcome.FailedAt == UpdateStep.MigrateState, "失败点应当是迁移,实际 " + outcome.FailedAt);
    Assert(host.RolledBack, "迁移失败必须回滚(此时新版本还没装,回滚=保持旧版本可用)");
    Assert(host.Resumed, "迁移失败后必须恢复同步");
    Assert(!orch.Executed.Contains(UpdateStep.Restart), "迁移失败绝不重启(否则进入「新版本+旧 schema」)");
}

static async Task CheckRestartFailureAsync()
{
    var host = new FakeHost { RestartShouldFail = true };
    var orch = new UpdateOrchestrator(host);
    var outcome = await orch.RunAsync();

    Assert(!outcome.Upgraded, "启动更新器失败 = 没升级成功");
    Assert(outcome.FailedAt == UpdateStep.Restart, "失败点应当是重启,实际 " + outcome.FailedAt);
    Assert(host.RolledBack && host.Resumed, "启动更新器失败也要回滚 + 恢复同步");
}

static async Task CheckPauseFailureAsync()
{
    var host = new FakeHost { PauseShouldFail = true };
    var orch = new UpdateOrchestrator(host);
    var outcome = await orch.RunAsync();

    Assert(!outcome.Upgraded, "暂停失败就该放弃");
    Assert(outcome.FailedAt == UpdateStep.PauseSync, "失败点应当是暂停,实际 " + outcome.FailedAt);
    Assert(orch.Executed.SequenceEqual(new[] { UpdateStep.PauseSync }),
        "暂停失败后不该执行任何后续步骤,实际 " + string.Join("→", orch.Executed));
    // 暂停失败的语义是"同步还在跑":此时**不该**回滚(没有半成品可回滚),
    // 但也不该假装恢复成功 —— 关键是不要迁移/重启
    Assert(!host.RolledBack, "什么都没做时不必回滚");
}

static async Task CheckAlwaysResumesAsync()
{
    // 三种失败路径都必须恢复同步
    foreach (var (mutate, _) in new (Action<FakeHost>, string)[]
             {
                 (h => h.DrainImpl = (_, _) => Task.FromResult(false), "排空超时"),
                 (h => h.MigrateShouldFail = true, "迁移失败"),
                 (h => h.RestartShouldFail = true, "启动更新器失败"),
             })
    {
        var host = new FakeHost();
        mutate(host);
        var orch = new UpdateOrchestrator(host, new UpdatePolicy { DrainTimeout = TimeSpan.FromSeconds(1) });
        var outcome = await orch.RunAsync();
        Assert(host.Resumed, "失败路径(" + outcome.FailedAt + ")必须恢复同步,否则用户面对一个卡住的客户端");
        Assert(outcome.SyncResumed, "结果里应如实报告「已恢复同步」");
    }
}

static async Task CheckResumeEvenIfRollbackFailsAsync()
{
    var host = new FakeHost { MigrateShouldFail = true, RollbackShouldFail = true };
    var orch = new UpdateOrchestrator(host);
    var outcome = await orch.RunAsync();

    Assert(!outcome.Upgraded && !outcome.RolledBack, "回滚失败应如实报告");
    Assert(host.Resumed && outcome.SyncResumed,
        "**回滚失败也必须恢复同步**:用户宁可要「旧版本 + 能同步」,也不要「暂停着、什么都不干」");
    Assert(outcome.Reason.Contains("回滚也失败"), "原因里要说清回滚失败,实际 " + outcome.Reason);
}

static Task CheckUpdaterCommandAsync()
{
    var launcher = new UpdaterLauncher(@"C:\Program Files\NetDisk\NetDisk.Updater.exe");
    var cmd = launcher.BuildCommand(
        @"C:\Users\u\Downloads\NetDisk 1.0.1.msi",
        @"C:\Users\u\AppData\Local\NetDisk\NetDisk.App.exe");

    var args = cmd.ToArguments();
    Assert(args.Contains("--msi \"C:\\Users\\u\\Downloads\\NetDisk 1.0.1.msi\""),
        "MSI 路径必须加引号(含空格是常态,不加会被拆成多个参数),实际 " + args);
    Assert(args.Contains(" /qn"), "必须静默安装(/qn):升级途中弹 UI 就不是「自动」更新了,实际 " + args);
    Assert(args.Contains("--relaunch"), "装完要把客户端拉起来(否则用户以为程序没了),实际 " + args);
    Assert(args.Contains("--client \"C:\\Users\\u\\AppData\\Local\\NetDisk\\NetDisk.App.exe\""),
        "要告诉更新器装完拉起哪个 exe,实际 " + args);
    Assert(cmd.ToString().StartsWith("\"C:\\Program Files\\NetDisk\\NetDisk.Updater.exe\"", StringComparison.Ordinal),
        "更新器自身路径也要带引号(它在 Program Files 里),实际 " + cmd);

    // 路径为空必须被拒(否则会拼出一个"装空气"的命令行)
    var rejected = false;
    try { _ = launcher.BuildCommand("", "x.exe"); }
    catch (ArgumentException) { rejected = true; }
    Assert(rejected, "空 MSI 路径必须被拒绝");
    return Task.CompletedTask;
}

static async Task CheckNoThrowAsync()
{
    // 任何一步炸掉都不该把异常抛给调用方:UI 要能显示"这次没升级成,已回到原样"
    var host = new FakeHost { MigrateShouldFail = true, RollbackShouldFail = true };
    var orch = new UpdateOrchestrator(host);
    var outcome = await orch.RunAsync();
    Assert(outcome is not null, "升级失败也要返回结果(而不是抛异常)");
    Assert(!string.IsNullOrEmpty(outcome.Reason), "结果里必须带可展示的原因");
}

// ---------------------------------------------------------------- 工具

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new Exception(message);
    }
}

/// <summary>假宿主:记录调用顺序,并可按场景注入失败。</summary>
sealed class FakeHost : IUpdateHost
{
    public bool PauseShouldFail { get; set; }
    public bool MigrateShouldFail { get; set; }
    public bool RestartShouldFail { get; set; }
    public bool RollbackShouldFail { get; set; }
    public Func<TimeSpan, CancellationToken, Task<bool>>? DrainImpl { get; set; }

    public bool Resumed { get; private set; }
    public bool RolledBack { get; private set; }
    public bool Restarted { get; private set; }

    public Task PauseSyncAsync(CancellationToken ct)
        => PauseShouldFail ? Task.FromException(new InvalidOperationException("暂停失败(注入)")) : Task.CompletedTask;

    public Task ResumeSyncAsync(CancellationToken ct)
    {
        Resumed = true;
        return Task.CompletedTask;
    }

    public Task<bool> DrainQueueAsync(TimeSpan timeout, CancellationToken ct)
        => DrainImpl is null ? Task.FromResult(true) : DrainImpl(timeout, ct);

    public Task MigrateStateAsync(CancellationToken ct)
        => MigrateShouldFail ? Task.FromException(new InvalidOperationException("迁移失败(注入)")) : Task.CompletedTask;

    public Task RestartIntoUpdaterAsync(CancellationToken ct)
    {
        if (RestartShouldFail)
        {
            return Task.FromException(new InvalidOperationException("启动更新器失败(注入)"));
        }
        Restarted = true;
        return Task.CompletedTask;
    }

    public Task RollbackAsync(CancellationToken ct)
    {
        if (RollbackShouldFail)
        {
            return Task.FromException(new InvalidOperationException("回滚失败(注入)"));
        }
        RolledBack = true;
        return Task.CompletedTask;
    }
}
