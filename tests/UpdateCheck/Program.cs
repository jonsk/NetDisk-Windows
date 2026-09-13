// DE-D-19 行为检查器:自动升级序列(顺序 / 排空 / 失败回滚 / 更新器命令行)。
//
// 用法:`dotnet run --project desktop/tests/UpdateCheck -c Release`
//
// 升级流程是**一锤子买卖**:不可能靠人工反复演练来验证"顺序对不对、失败时会不会回滚"。
// 所以这里把每一步做成可注入的假宿主,把顺序、超时、失败路径全部跑一遍。

using NetDisk.SyncEngine.Update;

// 更新器协议断言里用的固定输入(含**空格**的路径:引号问题就是这样暴露的)
const string MsiV2 = @"C:\tmp\有 空格\NetDisk-2.msi";
const string MsiV1 = @"C:\tmp\有 空格\NetDisk-1.msi";
const string ClientExe = @"C:\Users\u\AppData\Local\Programs\NetDisk\NetDisk.App.exe";

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
    ("⑪ 更新器协议:msiexec 参数加引号/静默/不重启,验活用 --self-check", CheckUpdaterInstallArgsAsync),
    ("⑫ 退出码 3010(成功但需重启)也算成功,不许回滚", CheckUpdaterRebootCodeAsync),
    ("⑬ 安装失败(1603)→ InstallFailed,且**仍把客户端拉起来**", CheckUpdaterInstallFailAsync),
    ("⑭ 新版验活失败 → 用**旧包**回滚,结论 = 已回滚", CheckUpdaterHealthFailRollbackAsync),
    ("⑮ 验活失败但**没有旧包** → 如实报 HealthCheckFailed(不谎报成功)", CheckUpdaterHealthFailNoPreviousAsync),
    ("⑯ 回滚也失败 → 结论 = 回滚失败(最坏情况不掩盖)", CheckUpdaterRollbackFailAsync),
    ("⑰ 超时不等待:验活超时按「没活」处理 → 走回滚", CheckUpdaterHealthTimeoutAsync),
    ("⑱ 不重启客户端时一次都不拉进程(--no-relaunch 语义)", CheckUpdaterNoRelaunchAsync),
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
    Assert(!string.IsNullOrEmpty(outcome?.Reason), "结果里必须带可展示的原因(而不是抛异常)");
}

// ---------------------------------------------------------------- 更新器协议(⑪~⑱)

static UpdaterOptions Options(string? previous = MsiV1) =>
    new(MsiV2, ClientExe, PreviousMsiPath: previous);

static async Task CheckUpdaterInstallArgsAsync()
{
    var fake = new FakeRunner();
    var outcome = await new UpdaterRunner(fake).RunAsync(Options());
    Assert(outcome.Kind == UpdaterOutcomeKind.Upgraded, $"应成功,实际 {outcome.Kind}:{outcome.Reason}");
    Assert(fake.Calls.Count == 2, $"应恰好两次等待调用(装 + 验活),实际 {fake.Calls.Count}");
    var (exe, args, _) = fake.Calls[0];
    Assert(exe == UpdaterRunner.MsiexecPath, $"应调用 msiexec,实际 {exe}");
    // 路径含空格必须加引号:不加会被拆成多个参数,安装器收到一个不存在的包路径
    Assert(args.Contains($"\"{MsiV2}\""), "MSI 路径必须加引号,实际 " + args);
    Assert(args.Contains("/qn"), "必须静默(/qn):perUser 包不触发 UAC,不该弹 UI,实际 " + args);
    Assert(args.Contains("/norestart"), "必须 /norestart:升级途中弹「需要重启」会把无人值守变成有人值守,实际 " + args);
    Assert(fake.Calls[1].Exe == ClientExe && fake.Calls[1].Args == "--self-check",
        $"验活必须由客户端自己回答(--self-check),实际 {fake.Calls[1].Exe} {fake.Calls[1].Args}");
    Assert(fake.Started.Count == 1 && fake.Started[0] == ClientExe, "成功后应拉起客户端一次");
}

static async Task CheckUpdaterRebootCodeAsync()
{
    // 3010 = 成功但需要重启。把它当失败会导致"明明装上了却回滚"
    var fake = new FakeRunner();
    fake.ExitCodes.Enqueue(UpdaterRunner.SuccessNeedsReboot);
    var outcome = await new UpdaterRunner(fake).RunAsync(Options());
    Assert(outcome.Kind == UpdaterOutcomeKind.Upgraded, $"3010 必须算成功,实际 {outcome.Kind}:{outcome.Reason}");
    Assert(fake.Calls.Count == 2, "3010 之后仍应验活(而不是直接回滚)");
    Assert(!fake.Calls.Any(c => c.Args.Contains($"\"{MsiV1}\"")), "3010 不该触发回滚(不该装旧包)");
}

static async Task CheckUpdaterInstallFailAsync()
{
    var fake = new FakeRunner { DefaultExitCode = 1603 }; // 1603 = 致命错误(常见于权限/磁盘)
    var outcome = await new UpdaterRunner(fake).RunAsync(Options());
    Assert(outcome.Kind == UpdaterOutcomeKind.InstallFailed, $"装失败应报 InstallFailed,实际 {outcome.Kind}");
    Assert(outcome.InstallerExitCode == 1603, "结论里要带 msiexec 退出码(排障靠它)");
    Assert(fake.Calls.Count == 1, "装都没装上,不该再验活");
    // 关键是这一条:失败也要把客户端拉起来,否则用户升级失败后面对的是"程序不见了"
    Assert(fake.Started.Count == 1, "安装失败后仍应拉起客户端(旧版本还在,用户至少能用)");
}

static async Task CheckUpdaterHealthFailRollbackAsync()
{
    var fake = new FakeRunner();
    fake.ExitCodes.Enqueue(0);   // 装新版成功
    fake.ExitCodes.Enqueue(1);   // 验活失败(新版起不来)
    fake.ExitCodes.Enqueue(0);   // 回滚成功
    var outcome = await new UpdaterRunner(fake).RunAsync(Options());
    Assert(outcome.Kind == UpdaterOutcomeKind.HealthCheckFailedRolledBack,
        $"验活失败应回滚,实际 {outcome.Kind}:{outcome.Reason}");
    Assert(fake.Calls.Count == 3, $"应为 装→验活→回滚 三步,实际 {fake.Calls.Count}");
    Assert(fake.Calls[2].Args.Contains($"\"{MsiV1}\""), "回滚必须用**旧包**");
    Assert(fake.Started.Count == 1, "回滚后也要把客户端拉起来");
}

static async Task CheckUpdaterHealthFailNoPreviousAsync()
{
    var fake = new FakeRunner();
    fake.ExitCodes.Enqueue(0);   // 装成功
    fake.ExitCodes.Enqueue(1);   // 验活失败
    var outcome = await new UpdaterRunner(fake).RunAsync(Options(previous: null));
    Assert(outcome.Kind == UpdaterOutcomeKind.HealthCheckFailed,
        $"没有旧包时必须如实报 HealthCheckFailed(不许谎报成功),实际 {outcome.Kind}");
    Assert(!outcome.Success, "这不是成功");
    Assert(fake.Calls.Count == 2, "没有旧包就不该尝试回滚");
}

static async Task CheckUpdaterRollbackFailAsync()
{
    var fake = new FakeRunner();
    fake.ExitCodes.Enqueue(0);    // 装成功
    fake.ExitCodes.Enqueue(1);    // 验活失败
    fake.ExitCodes.Enqueue(1603); // 回滚失败
    var outcome = await new UpdaterRunner(fake).RunAsync(Options());
    Assert(outcome.Kind == UpdaterOutcomeKind.HealthCheckFailedRollbackFailed,
        $"回滚失败必须如实报(最坏情况不掩盖),实际 {outcome.Kind}");
    Assert(fake.Started.Count == 1, "回滚失败也要把客户端拉起来(手动还能用)");
}

static async Task CheckUpdaterHealthTimeoutAsync()
{
    var fake = new FakeRunner();
    fake.ExitCodes.Enqueue(0);   // 装成功
    fake.TimeoutCalls.Add(2);    // **第二次**调用(验活)超时:新版卡住起不来
    fake.ExitCodes.Enqueue(0);   // 回滚成功
    var outcome = await new UpdaterRunner(fake).RunAsync(Options());
    Assert(outcome.Kind == UpdaterOutcomeKind.HealthCheckFailedRolledBack,
        $"验活超时按「没活」处理并回滚,实际 {outcome.Kind}:{outcome.Reason}");
    Assert(fake.Calls.Count == 3, $"应为 装→验活(超时)→回滚 三步,实际 {fake.Calls.Count}");
    Assert(fake.Calls[2].Args.Contains($"\"{MsiV1}\""), "超时路径也要真的回滚");
}

static async Task CheckUpdaterNoRelaunchAsync()
{
    var fake = new FakeRunner();
    var outcome = await new UpdaterRunner(fake).RunAsync(new UpdaterOptions(
        MsiV2, ClientExe, RelaunchClient: false, PreviousMsiPath: MsiV1));
    Assert(outcome.Kind == UpdaterOutcomeKind.Upgraded, "不重启客户端不影响升级本身");
    Assert(fake.Started.Count == 0, "--no-relaunch 时一次都不该拉进程");
    Assert(!outcome.Relaunched, "结论里要如实反映「没有拉起客户端」");
}

// ---------------------------------------------------------------- 工具

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new Exception(message);
    }
}

/// <summary>
/// 假进程执行器:记录每一次调用(可执行文件 + 参数 + 是否等待),并按场景注入退出码/超时。
/// 更新器协议是"一锤子买卖"的现场 —— 真机演练只证明一条路径,这里把所有失败路径都跑出来。
/// </summary>
sealed class FakeRunner : IProcessRunner
{
    public List<(string Exe, string Args, bool Waited)> Calls { get; } = new();
    public List<string> Started { get; } = new();
    /// <summary>按调用次序给出退出码;用完后回落到 DefaultExitCode。</summary>
    public Queue<int> ExitCodes { get; } = new();
    public int DefaultExitCode { get; set; }
    /// <summary>第 N 次等待调用抛超时(1 起数):用来精确模拟"新版起不来"而不是"安装器超时"。</summary>
    public HashSet<int> TimeoutCalls { get; } = new();

    public Task<int> RunAsync(string exe, string arguments, TimeSpan timeout, CancellationToken ct)
    {
        Calls.Add((exe, arguments, true));
        if (TimeoutCalls.Contains(Calls.Count))
        {
            return Task.FromException<int>(new TimeoutException("注入的超时"));
        }
        return Task.FromResult(ExitCodes.Count > 0 ? ExitCodes.Dequeue() : DefaultExitCode);
    }

    public void StartDetached(string exe, string arguments) => Started.Add(exe);
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
