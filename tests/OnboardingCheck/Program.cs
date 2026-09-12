// DE-D-15 行为检查器:首次运行向导(三选 / 容量预估 / 只读浏览模式)。
//
// 用法:`dotnet run --project desktop/tests/OnboardingCheck -c Release`
//
// 最要紧的两条:
//   - **结构模式绝不冒充可双击的同步目录**:骨架里只能有目录 + 一份说明文件。
//     若实现顺手创建了"与远端同名的 0 字节文件",用户双击会打开空文档、以为文件坏了,
//     而界面上一切正常(这正是 7.6 V2.17 P1-6 要防的"用户以为文件丢了")。
//   - **落骨架不破坏既有内容**:从「全量」切到「仅结构」时本地已有真实文件,落骨架只能建目录。

using NetDisk.SyncEngine.Onboarding;
using NetDisk.SyncEngine.Transfer;

var checks = new List<(string Name, Func<Task> Run)>
{
    ("① 三选各自的范围与默认节流正确", CheckThreeChoicesAsync),
    ("② 全量首次把并发降到 1(7.2 T2-6)", CheckThrottleDefaultAsync),
    ("③ 容量预估按所选范围(限定子树不按全库报)", CheckCapacityScopeAsync),
    ("④ 同步根预检:深度超限当场拦下并给换根建议", CheckDepthLimitAsync),
    ("⑤ 同步根预检:最长路径超 240 字节当场拦下", CheckPathBytesLimitAsync),
    ("⑥ 预检没有样本时**不放行**(可能还没拉到清单)", CheckNoProbeNotOkAsync),
    ("⑦ 结构模式只落目录 + 一份说明文件(无同名 0 字节文件)", CheckStructureOnlySkeletonAsync),
    ("⑧ 说明文件写明「不落地内容、请在客户端下载」", CheckMarkerContentAsync),
    ("⑨ 结构模式是只读浏览(计划标记 IsReadOnlyBrowse)", CheckReadOnlyFlagAsync),
    ("⑩ 落骨架幂等:重复应用不重复写、不重复建", CheckIdempotentAsync),
    ("⑪ 落骨架不碰本地既有真实文件(切模式不是丢数据)", CheckKeepsExistingFilesAsync),
    ("⑫ 计划校验:空根 / 并发 0 / 限定子树却没勾选 都被拒绝", CheckValidationAsync),
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
    Console.WriteLine($"全部通过:{checks.Count} 项 DE-D-15 行为断言(首次运行向导)");
    return 0;
}
Console.Error.WriteLine($"{failed}/{checks.Count} 项失败");
return 1;

// ---------------------------------------------------------------- 检查

static async Task CheckThreeChoicesAsync()
{
    var wizard = new OnboardingWizard((_, _) => Tree());

    var structure = await wizard.BuildAsync(FirstSyncChoice.StructureOnly, @"D:\NetDisk");
    Assert(structure.Choice == FirstSyncChoice.StructureOnly, "选择应被如实记录");
    Assert(structure.IsReadOnlyBrowse, "结构模式必须标记为只读浏览模式");
    Assert(structure.Transfer.MaxConcurrency == 3, "结构模式数据量小,保持默认并发 3");

    var subtrees = await wizard.BuildAsync(FirstSyncChoice.SelectedSubtrees, @"D:\NetDisk",
        new[] { "docs" });
    Assert(!subtrees.IsReadOnlyBrowse, "限定子树不是只读浏览模式");
    Assert(subtrees.Subtrees.Count == 1 && subtrees.Subtrees[0] == "docs", "应记录所勾选的子树");

    var full = await wizard.BuildAsync(FirstSyncChoice.FullWithThrottle, @"D:\NetDisk");
    Assert(full.Choice == FirstSyncChoice.FullWithThrottle, "全量+节流应被如实记录");
    Assert(full.Transfer.MaxConcurrency == 1, "首次全量应降并发到 1");
}

static Task CheckThrottleDefaultAsync()
{
    Assert(OnboardingWizard.DefaultTransferFor(FirstSyncChoice.FullWithThrottle).MaxConcurrency == 1,
        "全量首次默认并发必须是 1(7.2 T2-6:新设备一次灌整库会拖死机器)");
    Assert(OnboardingWizard.DefaultTransferFor(FirstSyncChoice.StructureOnly).MaxConcurrency == 3,
        "结构模式默认并发 3");
    Assert(OnboardingWizard.DefaultTransferFor(FirstSyncChoice.SelectedSubtrees).MaxConcurrency == 3,
        "限定子树默认并发 3");
    // 自定义节流必须被采纳(向导里用户可改)
    var custom = new TransferQueueOptions
    {
        MaxConcurrency = 2,
        RateLimit = new RateLimitOptions { UploadBytesPerSecond = 1024 * 1024, DownloadBytesPerSecond = 4 * 1024 * 1024 },
    };
    var plan = new OnboardingPlan { Choice = FirstSyncChoice.FullWithThrottle, RootPath = "D:\\n", Transfer = custom };
    Assert(plan.Transfer.RateLimit.DownloadBytesPerSecond == 4 * 1024 * 1024, "用户改的限速必须进入计划");
    return Task.CompletedTask;
}

static async Task CheckCapacityScopeAsync()
{
    var wizard = new OnboardingWizard((_, _) => Tree());

    var all = await wizard.BuildAsync(FirstSyncChoice.FullWithThrottle, @"D:\NetDisk");
    Assert(all.Estimate!.FileCount == 3, $"全库应有 3 个文件,实际 {all.Estimate.FileCount}");
    Assert(all.Estimate.TotalBytes == 1000 + 2000 + 3000, $"全库字节数应为 6000,实际 {all.Estimate.TotalBytes}");
    Assert(all.Estimate.DirectoryCount == 3, $"全库应有 3 个目录,实际 {all.Estimate.DirectoryCount}");

    var docs = await wizard.BuildAsync(FirstSyncChoice.SelectedSubtrees, @"D:\NetDisk", new[] { "docs" });
    Assert(docs.Estimate!.FileCount == 2,
        $"限定 docs 时只应统计该子树的 2 个文件(按全库报会让用户以为选了 1KB 却要下 6KB),实际 {docs.Estimate.FileCount}");
    Assert(docs.Estimate.TotalBytes == 3000, $"docs 子树字节数应为 3000,实际 {docs.Estimate.TotalBytes}");
    Assert(docs.Estimate.Summary.Contains("GB") || docs.Estimate.Summary.Contains("个文件"),
        "预估要有人话摘要(一串纯数字用户看不懂)");
}

static async Task CheckDepthLimitAsync()
{
    // 32 层(上限 31)→ 必须当场拦下并建议换根
    var deep = new OnboardingWizard((_, _) => Task.FromResult<IReadOnlyList<RemoteEntry>>(new[]
    {
        new RemoteEntry(string.Join("/", Enumerable.Range(1, 32).Select(i => "d" + i)), true, 0, 32),
    }));
    var plan = await deep.BuildAsync(FirstSyncChoice.FullWithThrottle, @"D:\NetDisk");
    Assert(plan.PathBudget is { Ok: false }, "深度超限必须判为不通过");
    var problems = plan.Validate();
    Assert(problems.Any(p => p.Contains("深度")), $"校验应指出深度问题,实际:{string.Join("|", problems)}");
    Assert(plan.PathBudget!.Suggestion is not null && plan.PathBudget.Suggestion.Contains("同步根"),
        $"必须给出「换更浅的根」的建议(否则用户不知道怎么改),实际 {plan.PathBudget.Suggestion}");
}

static async Task CheckPathBytesLimitAsync()
{
    var longName = new string('报', 100) + ".txt"; // 304 字节 > 240
    var wizard = new OnboardingWizard((_, _) => Task.FromResult<IReadOnlyList<RemoteEntry>>(new[]
    {
        new RemoteEntry("docs/" + longName, false, 10, 1),
    }));
    var plan = await wizard.BuildAsync(FirstSyncChoice.FullWithThrottle, @"D:\NetDisk");
    Assert(plan.PathBudget is { Ok: false }, "最长路径超 240 字节必须判为不通过");
    Assert(plan.Validate().Any(p => p.Contains("超过上限")), "校验应指出路径超限");
}

static async Task CheckNoProbeNotOkAsync()
{
    // 还没拉到清单时**不能放行**:放行等于"跑到一半才发现路径超限"
    var empty = new OnboardingWizard((_, _) => Task.FromResult<IReadOnlyList<RemoteEntry>>(Array.Empty<RemoteEntry>()));
    var plan = await empty.BuildAsync(FirstSyncChoice.FullWithThrottle, @"D:\NetDisk");
    Assert(plan.PathBudget is { Ok: false }, "没有样本时必须判为不通过(而不是默认放行)");
    Assert(plan.PathBudget!.Problems.Any(p => p.Contains("没有可检查的路径")), "应说明原因");
}

static Task CheckStructureOnlySkeletonAsync()
{
    WithTempRoot(root =>
    {
        var plan = new OnboardingPlan { Choice = FirstSyncChoice.StructureOnly, RootPath = root };
        var tree = Tree().GetAwaiter().GetResult();
        var result = new SkeletonBuilder().Apply(plan, tree);

        Assert(result.CreatedDirectories == 3, $"应落 3 个目录骨架,实际 {result.CreatedDirectories}");
        Assert(result.WroteMarker, "应写入说明文件");

        // **关键断言**:骨架里只能有目录 + 那一份说明文件
        var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();
        Assert(files.Length == 1 && files[0] == SkeletonBuilder.MarkerFileName,
            $"结构模式只允许有说明文件,实际:{string.Join(",", files)} —— " +
            "出现与远端同名的文件就是「冒充可双击的同步目录」(用户双击会得到空文档)");

        var dirs = Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();
        Assert(dirs.SequenceEqual(new[] { "docs", "docs/sub", "photos" }),
            $"目录骨架应与远端一致,实际:{string.Join(",", dirs)}");

        // 远端文件一个都不该落地
        Assert(!File.Exists(Path.Combine(root, "docs", "a.txt")), "远端文件不得被落地(只读浏览模式)");
        Assert(!File.Exists(Path.Combine(root, "photos", "p.jpg")), "远端文件不得被落地");
    });
    return Task.CompletedTask;
}

static Task CheckMarkerContentAsync()
{
    WithTempRoot(root =>
    {
        var plan = new OnboardingPlan { Choice = FirstSyncChoice.StructureOnly, RootPath = root };
        new SkeletonBuilder().Apply(plan, Tree().GetAwaiter().GetResult());

        var text = File.ReadAllText(Path.Combine(root, SkeletonBuilder.MarkerFileName));
        Assert(text.Contains("仅同步目录结构"), "说明文件必须写明当前模式");
        Assert(text.Contains("不落地文件内容"), "必须写明「不落地文件内容」(防「用户以为文件丢了」)");
        Assert(text.Contains("下载"), "必须告诉用户怎么拿到文件(客户端内点下载)");
        Assert(text.Contains("空文件"), "应解释「为什么这里没有同名空文件」(否则用户会疑惑)");
    });
    return Task.CompletedTask;
}

static async Task CheckReadOnlyFlagAsync()
{
    var wizard = new OnboardingWizard((_, _) => Tree());
    var structure = await wizard.BuildAsync(FirstSyncChoice.StructureOnly, @"D:\NetDisk");
    Assert(structure.IsReadOnlyBrowse, "结构模式 = 只读浏览模式(客户端据此显示云朵图标与托盘提示)");
    var full = await wizard.BuildAsync(FirstSyncChoice.FullWithThrottle, @"D:\NetDisk");
    Assert(!full.IsReadOnlyBrowse, "全量模式不是只读浏览");
}

static Task CheckIdempotentAsync()
{
    WithTempRoot(root =>
    {
        var plan = new OnboardingPlan { Choice = FirstSyncChoice.StructureOnly, RootPath = root };
        var tree = Tree().GetAwaiter().GetResult();
        var builder = new SkeletonBuilder();

        var first = builder.Apply(plan, tree);
        Assert(first.WroteMarker, "首次应写说明文件");
        var second = builder.Apply(plan, tree);
        Assert(second.CreatedDirectories == 0, $"第二次不该再建目录,实际 {second.CreatedDirectories}");
        Assert(!second.WroteMarker, "第二次不该重写说明文件(内容一致就没必要动它)");
        Assert(Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Length == 3, "目录数量不变");
    });
    return Task.CompletedTask;
}

static Task CheckKeepsExistingFilesAsync()
{
    WithTempRoot(root =>
    {
        // 用户原本在「全量」模式下同步过,本地已有真实文件
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        var existing = Path.Combine(root, "docs", "a.txt");
        File.WriteAllText(existing, "用户已有的真实内容");

        var plan = new OnboardingPlan { Choice = FirstSyncChoice.StructureOnly, RootPath = root };
        new SkeletonBuilder().Apply(plan, Tree().GetAwaiter().GetResult());

        Assert(File.Exists(existing), "切到结构模式**绝不能**删掉本地既有文件");
        Assert(File.ReadAllText(existing) == "用户已有的真实内容", "既有文件内容不得被覆盖");
    });
    return Task.CompletedTask;
}

static Task CheckValidationAsync()
{
    var emptyRoot = new OnboardingPlan { Choice = FirstSyncChoice.FullWithThrottle, RootPath = "  " };
    Assert(emptyRoot.Validate().Any(p => p.Contains("同步根")), "空同步根必须被拒绝");

    var zeroConcurrency = new OnboardingPlan
    {
        Choice = FirstSyncChoice.FullWithThrottle,
        RootPath = "D:\\n",
        Transfer = new TransferQueueOptions { MaxConcurrency = 0 },
    };
    Assert(zeroConcurrency.Validate().Any(p => p.Contains("并发")), "并发 0 必须被拒绝");

    var noSubtree = new OnboardingPlan
    {
        Choice = FirstSyncChoice.SelectedSubtrees,
        RootPath = "D:\\n",
        Subtrees = Array.Empty<string>(),
    };
    Assert(noSubtree.Validate().Any(p => p.Contains("没有勾选")),
        "选了限定子树却一个都没勾,必须当场拦下(否则用户得到一个「什么都不同步」的客户端)");

    var ok = new OnboardingPlan { Choice = FirstSyncChoice.StructureOnly, RootPath = "D:\\n" };
    Assert(ok.Validate().Count == 0, $"合法计划不该报错,实际:{string.Join("|", ok.Validate())}");
    return Task.CompletedTask;
}

// ---------------------------------------------------------------- 工具

/// <summary>远端树样本:3 个目录 + 3 个文件(其中 docs 子树 2 个文件 3000 字节)。</summary>
static Task<IReadOnlyList<RemoteEntry>> Tree() => Task.FromResult<IReadOnlyList<RemoteEntry>>(new[]
{
    new RemoteEntry("docs", true, 0, 1),
    new RemoteEntry("docs/a.txt", false, 1000, 2),
    new RemoteEntry("docs/sub", true, 0, 2),
    new RemoteEntry("docs/sub/b.bin", false, 2000, 3),
    new RemoteEntry("photos", true, 0, 1),
    new RemoteEntry("photos/p.jpg", false, 3000, 2),
});

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new Exception(message);
    }
}

static void WithTempRoot(Action<string> body)
{
    var root = Path.Combine(Path.GetTempPath(), "netdisk-onboarding-check", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        body(root);
    }
    finally
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }
}
