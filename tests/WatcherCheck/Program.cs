// DE-D-08 行为检查器:FileSystemWatcher 监听 + 溢出转定向重扫 + 自适应卫生扫。
//
// 用法:`dotnet run --project desktop/tests/WatcherCheck -c Release`
//
// 四条验收各自对应一类"看起来在工作、其实已经漏了"的实现方式:
//   ① 溢出必须变成"重扫这棵子树"(丢了事件只记日志 = 本地与远端静默长期不一致)
//   ② 空闲退避到 30~60min、忙时 2min(太短白耗电、太长等于没有兜底)
//   ③ 扫描线程 VeryLow IO(用户前台用机时不该被同步抢 IO)
//   ④ 扫描只 stat 不读内容(**机械规则**扫源码,不靠自觉)

using NetDisk.SyncEngine.Watch;

var checks = new List<(string Name, Func<Task> Run)>
{
    ("① 溢出 → 请求重扫整根(而不是只记日志)", CheckOverflowTriggersRescanAsync),
    ("② 普通变更 → 去抖后只重扫受影响的目录", CheckDebouncedTargetedRescanAsync),
    ("③ 空闲翻倍、封顶 45min、有活动回到 2min", CheckAdaptiveIntervalAsync),
    ("④ 区间常量落在验收范围(30~60min / 忙 2min)", CheckIntervalConstantsAsync),
    ("⑤ 扫描只 stat:机械规则拦住内容读取 API", CheckNoContentReadsAsync),
    ("⑥ 真 FileSystemWatcher:写入文件能收到事件", CheckRealWatcherAsync),
    ("⑦ 扫描结果字段正确(大小/时间/目录标志)", CheckScanMetadataAsync),
    ("⑧ VeryLow IO 后台档可进可出", CheckBackgroundIoAsync),
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
    Console.WriteLine($"全部通过:{checks.Count} 项 DE-D-08 行为断言(监听 / 定向重扫 / 自适应卫生扫)");
    return 0;
}
Console.Error.WriteLine($"{failed}/{checks.Count} 项失败");
return 1;

// ---------------------------------------------------------------- 检查

static Task CheckOverflowTriggersRescanAsync()
{
    using var backend = new FakeBackend(@"C:\sync");
    using var watcher = new FileWatcher(backend);
    var requests = new List<RescanRequest>();
    watcher.RescanRequested += requests.Add;
    watcher.Start();

    backend.RaiseFailure(new InternalBufferOverflowException("模拟缓冲区溢出"));

    Assert(requests.Count == 1, $"溢出应当产生 1 条重扫请求,实际 {requests.Count}");
    Assert(requests[0].Reason == RescanReason.Overflow, $"原因应为 Overflow,实际 {requests[0].Reason}");
    Assert(requests[0].WholeRoot, "溢出丢的是**未知范围**的事件 → 必须重扫整根,不能只扫一个子目录");
    Assert(requests[0].Path == @"C:\sync", $"应重扫被监听的根,实际 {requests[0].Path}");
    return Task.CompletedTask;
}

static Task CheckDebouncedTargetedRescanAsync()
{
    using var backend = new FakeBackend(@"C:\sync");
    using var watcher = new FileWatcher(backend);
    var requests = new List<RescanRequest>();
    watcher.RescanRequested += requests.Add;
    watcher.Start();

    // 一次保存往往连发多条事件(创建/修改):去抖窗口内只应产生 1 条重扫
    backend.RaiseChanged(@"C:\sync\docs\a.txt");
    backend.RaiseChanged(@"C:\sync\docs\a.txt");
    backend.RaiseChanged(@"C:\sync\docs\a.txt");
    backend.RaiseChanged(@"C:\sync\other\b.txt");

    Assert(requests.Count == 0, "去抖窗口内不该立即派发");
    watcher.Flush();

    Assert(requests.Count == 2, $"应按目录聚合成 2 条(两个不同目录),实际 {requests.Count}");
    Assert(requests.All(r => r.Reason == RescanReason.Changed), "普通变更的原因应为 Changed");
    Assert(requests.Any(r => r.Path == @"C:\sync\docs"), @"应包含 C:\sync\docs");
    Assert(requests.Any(r => r.Path == @"C:\sync\other"), @"应包含 C:\sync\other");
    Assert(!requests.Any(r => r.WholeRoot), "普通变更**不该**整根重扫(那会让一次保存变成全盘扫描)");
    return Task.CompletedTask;
}

static Task CheckAdaptiveIntervalAsync()
{
    var s = new ScanScheduler();
    Assert(s.CurrentInterval == ScanScheduler.BusyInterval, "初始应为忙时间隔(2min)");

    var seq = new List<TimeSpan>();
    for (var i = 0; i < 10; i++)
    {
        s.NoteIdleRound();
        seq.Add(s.CurrentInterval);
    }
    for (var i = 1; i < seq.Count; i++)
    {
        Assert(seq[i] >= seq[i - 1], $"空闲间隔必须单调不减:{seq[i - 1]} → {seq[i]}");
    }
    Assert(seq[^1] == ScanScheduler.IdleInterval, $"连续空闲应封顶到 45min,实际 {seq[^1]}");
    Assert(seq[1] > seq[0], "第一次空闲后应当翻倍(2 → 4min)");

    // 一旦有活动,立刻回到 2min(用户正在改文件时不该还在慢慢等)
    s.NoteActivity();
    Assert(s.CurrentInterval == ScanScheduler.BusyInterval, "有活动必须回到忙时间隔");
    return Task.CompletedTask;
}

static Task CheckIntervalConstantsAsync()
{
    Assert(ScanScheduler.ConfigurationIsSane(), "节拍常量必须落在验收区间内");
    Assert(ScanScheduler.IdleInterval >= TimeSpan.FromMinutes(30), "空闲上限不得低于 30min");
    Assert(ScanScheduler.IdleInterval <= TimeSpan.FromMinutes(60), "空闲上限不得高于 60min(否则兜底失效)");
    Assert(ScanScheduler.BusyInterval == TimeSpan.FromMinutes(2), "忙时间隔必须是 2min");
    return Task.CompletedTask;
}

static Task CheckNoContentReadsAsync()
{
    // 机械规则(④):扫描器源码里不得出现任何"读内容"的 API。
    // 为什么是机械规则而不是用例:读内容这件事**不影响扫描结果的正确性** ——
    // 用例全绿而磁盘被全读一遍是完全可能的,靠代码审查又极难发现。
    var file = Locate("desktop/src/NetDisk.SyncEngine/Watch/DirectoryScanner.cs");
    // **必须先去掉注释再查**:第一版直接扫全文,结果被文件里那句
    // "这里若出现 FileStream/ReadAllBytes,机械规则会拦住"的**注释**骗过 ——
    // 检查器报了一个假警报。这与第 1 卷红线核对里 R-14 的假门禁是同一类问题:
    // 扫全文的检查既会漏(注释里写了真实现)也会误报(注释里提到被禁词)。
    var text = StripComments(File.ReadAllText(file));
    string[] forbidden =
    {
        "ReadAllBytes", "ReadAllText", "ReadAllLines", "OpenRead", "FileStream",
        "StreamReader", "ReadAsByteArrayAsync", "SHA256", "ComputeHash",
    };
    foreach (var f in forbidden)
    {
        Assert(!text.Contains(f, StringComparison.Ordinal),
            $"DirectoryScanner 里出现 {f}:卫生扫只允许 stat,不得读内容(DE-D-08 ④)");
    }
    // 正例:必须真的用了只读元数据
    Assert(text.Contains("LastWriteTimeUtc") && text.Contains("Length"),
        "扫描器应当只依赖元数据(LastWriteTimeUtc / Length)");
    return Task.CompletedTask;
}

static async Task CheckRealWatcherAsync()
{
    // 真后端冒烟:证明"包 FileSystemWatcher"这一段是通的(假后端测不到它)
    var root = Path.Combine(Path.GetTempPath(), "netdisk-watch-check", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        using var backend = new FileSystemWatcherBackend(root);
        using var watcher = new FileWatcher(backend, debounce: TimeSpan.FromMilliseconds(50));
        var hits = new List<RescanRequest>();
        watcher.RescanRequested += r => { lock (hits) { hits.Add(r); } };
        watcher.Start();

        var sub = Path.Combine(root, "docs");
        Directory.CreateDirectory(sub);
        await File.WriteAllTextAsync(Path.Combine(sub, "a.txt"), "hello");

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            watcher.Flush();
            lock (hits)
            {
                if (hits.Count > 0)
                {
                    break;
                }
            }
            await Task.Delay(50);
        }
        lock (hits)
        {
            Assert(hits.Count > 0, "真实 FileSystemWatcher 应当在 5s 内捕获到写入事件");
            Assert(hits.Any(h => h.Path.Equals(sub, StringComparison.OrdinalIgnoreCase)),
                $"事件应指向受影响的目录 {sub},实际 {string.Join(",", hits.Select(h => h.Path))}");
        }
    }
    finally
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }
}

static Task CheckScanMetadataAsync()
{
    var root = Path.Combine(Path.GetTempPath(), "netdisk-scan-check", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var sub = Path.Combine(root, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(root, "a.txt"), "12345");
        File.WriteAllText(Path.Combine(sub, "b.txt"), "1234567890");

        var scanner = new DirectoryScanner();
        var flat = scanner.Scan(root, recursive: false);
        Assert(flat.Count == 2, $"非递归应只看到 sub 与 a.txt,实际 {flat.Count}");
        var a = flat.Single(e => e.Path.EndsWith("a.txt", StringComparison.Ordinal));
        Assert(!a.IsDirectory && a.Size == 5, $"a.txt 应为 5 字节的普通文件,实际 dir={a.IsDirectory} size={a.Size}");
        Assert(a.LastWriteUtc > DateTimeOffset.UtcNow.AddMinutes(-5), "LastWriteUtc 应当接近刚写入的时间");
        var dir = flat.Single(e => e.IsDirectory);
        Assert(dir.Path.EndsWith("sub", StringComparison.Ordinal), "应当能看到子目录");

        var deep = scanner.Scan(root, recursive: true);
        Assert(deep.Count == 3, $"递归应看到 3 条(sub、a.txt、b.txt),实际 {deep.Count}");
        Assert(deep.Single(e => e.Path.EndsWith("b.txt", StringComparison.Ordinal)).Size == 10,
            "b.txt 的大小应被正确读出");
        return Task.CompletedTask;
    }
    finally
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }
}

static Task CheckBackgroundIoAsync()
{
    var entered = IoPriority.EnterBackground();
    try
    {
        Assert(entered, "Windows 上应当能把线程切到后台(THREAD_MODE_BACKGROUND_BEGIN)");
        // 后台档里跑一次真实扫描:证明"扫描在后台档"这条路径可用(而不是只有开关没有用)
        var root = Path.Combine(Path.GetTempPath(), "netdisk-bg-scan", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "x.bin"), "x");
            var entries = IoPriority.RunInBackground(() => new DirectoryScanner().Scan(root, recursive: true));
            Assert(entries.Count == 1, $"后台档扫描应当照常返回结果,实际 {entries.Count}");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
    finally
    {
        if (entered)
        {
            IoPriority.ExitBackground();
        }
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

/// <summary>去掉 C# 注释(逐字符状态机;字符串字面量里的斜杠不当注释)。</summary>
static string StripComments(string source)
{
    var sb = new System.Text.StringBuilder(source.Length);
    var inLine = false;
    var inBlock = false;
    var inString = false;
    var escaped = false;
    for (var i = 0; i < source.Length; i++)
    {
        var c = source[i];
        var next = i + 1 < source.Length ? source[i + 1] : '\0';
        if (inLine)
        {
            if (c == '\n')
            {
                inLine = false;
                sb.Append(c);
            }
            continue;
        }
        if (inBlock)
        {
            if (c == '*' && next == '/')
            {
                inBlock = false;
                i++;
            }
            else if (c == '\n')
            {
                sb.Append(c);
            }
            continue;
        }
        if (inString)
        {
            sb.Append(c);
            if (escaped) { escaped = false; }
            else if (c == '\\') { escaped = true; }
            else if (c == '"') { inString = false; }
            continue;
        }
        if (c == '/' && next == '/') { inLine = true; i++; continue; }
        if (c == '/' && next == '*') { inBlock = true; i++; continue; }
        if (c == '"') { inString = true; }
        sb.Append(c);
    }
    return sb.ToString();
}

/// <summary>从可执行文件位置向上找仓库根,再拼相对路径(与 NameRulesCheck 同款)。</summary>
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

/// <summary>假后端:直接触发事件,把"溢出/变更"变成可控输入。</summary>
sealed class FakeBackend : IWatcherBackend
{
    public FakeBackend(string root) => RootPath = root;

    public event Action<string>? Changed;

    public event Action<Exception>? Failed;

    public string RootPath { get; }

    public bool Started { get; private set; }

    public void Start() => Started = true;

    public void RaiseChanged(string path) => Changed?.Invoke(path);

    public void RaiseFailure(Exception ex) => Failed?.Invoke(ex);

    public void Dispose()
    {
    }
}
