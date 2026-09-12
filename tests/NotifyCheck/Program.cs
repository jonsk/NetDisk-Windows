// DE-D-18 行为检查器:四类通知 + 合并窗口 + 配额滞回 + 开机自启(真实注册表往返)。
//
// 用法:`dotnet run --project desktop/tests/NotifyCheck -c Release`
//
// 为什么这些要单测而不是"点着看":通知的两类事故都是**看不见**的 ——
//   - 通知风暴:几千个文件弹几千个气泡 → 用户关掉通知 → 真正重要的也一起丢了;
//   - 阈值抖动:配额在 80% 上下反复穿越 → 密集提醒 → 同上。
// 这两条都能被"跑一遍数一数"钉死,所以不该留给肉眼。

using Microsoft.Win32;
using NetDisk.SyncEngine.Notify;

var checks = new List<(string Name, Func<Task> Run)>
{
    ("① 四类通知都可触发(验收口径)", CheckFourKindsAsync),
    ("② 同步完成:窗口内合并成一条(不刷屏)", CheckCompletionCoalescingAsync),
    ("③ 同步完成:窗口之后再报(不静默)", CheckCompletionAfterWindowAsync),
    ("④ 冲突通知说清「需要你选哪边」", CheckConflictContentAsync),
    ("⑤ 被移出空间通知写明「本地文件不会删除」", CheckSpaceRevokedContentAsync),
    ("⑥ 配额滞回:过线报一次,抖动不重报,回落后可再报", CheckQuotaHysteresisAsync),
    ("⑦ 通知策略不自洽时直接拒绝构造", CheckSaneOptionsAsync),
    ("⑧ 开机自启:真实注册表往返(临时子键)", CheckStartupRegistryRoundTripAsync),
    ("⑨ 自启命令行必须给路径加引号(含空格也不裂)", CheckStartupQuotingAsync),
    ("⑩ 托盘适配器只是渲染层(策略在 SyncEngine)", CheckTrayIsThinAsync),
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
    Console.WriteLine($"全部通过:{checks.Count} 项 DE-D-18 行为断言(通知与自启)");
    return 0;
}
Console.Error.WriteLine($"{failed}/{checks.Count} 项失败");
return 1;

// ---------------------------------------------------------------- 检查

static Task CheckFourKindsAsync()
{
    var clock = new FakeClock();
    var center = new NotificationCenter(clock);
    var seen = new List<AppNotification>();
    center.Raised += seen.Add;

    center.NotifySyncCompleted(3, 2048);                                  // 同步完成
    center.NotifyConflict("a.txt", 2, 5, "sp-1");                          // 冲突
    center.NotifySpaceRevoked("sp-2", "你已被移出该空间");                  // 被移出空间
    center.NotifyQuota("sp-3", 85);                                        // 配额预警

    var kinds = seen.Select(n => n.Kind).ToArray();
    foreach (var expected in new[]
             {
                 NotificationKind.SyncCompleted, NotificationKind.Conflict,
                 NotificationKind.SpaceRevoked, NotificationKind.QuotaWarning,
             })
    {
        Assert(kinds.Contains(expected), $"四类通知缺 {expected}(验收要求四类都可触发)");
    }
    Assert(seen.Count == 4, $"应当是 4 条通知,实际 {seen.Count}");
    return Task.CompletedTask;
}

static Task CheckCompletionCoalescingAsync()
{
    var clock = new FakeClock();
    var center = new NotificationCenter(clock);
    var count = 0;
    long lastBytes = 0;
    center.Raised += n => { count++; };

    // 第一次立刻报(用户刚打开客户端,第一条完成通知是有价值的)
    center.NotifySyncCompleted(1, 1024);
    Assert(count == 1, $"首次完成应当立刻通知,实际 {count}");

    // 窗口内的后续完成:只累计,不弹
    for (var i = 0; i < 500; i++)
    {
        clock.Advance(TimeSpan.FromMilliseconds(10)); // 500 × 10ms = 5s < 30s 窗口
        center.NotifySyncCompleted(1, 1024);
    }
    Assert(count == 1, $"合并窗口内不该继续弹(500 个文件弹 500 次气泡会把通知关掉),实际 {count}");

    center.FlushPending();
    Assert(count == 2, "汇总应当补一条(否则用户在窗口内做的事没有任何反馈)");
    _ = lastBytes;
    return Task.CompletedTask;
}

static Task CheckCompletionAfterWindowAsync()
{
    var clock = new FakeClock();
    var center = new NotificationCenter(clock);
    var messages = new List<string>();
    center.Raised += n => messages.Add(n.Message);

    center.NotifySyncCompleted(1, 1024);
    clock.Advance(TimeSpan.FromSeconds(31)); // 超过 30s 窗口
    center.NotifySyncCompleted(2, 2048);

    Assert(messages.Count == 2, $"窗口之后应当再报一条(否则后续同步静默),实际 {messages.Count}");
    Assert(messages[1].Contains("2 个文件"), $"应报这一轮的数量,实际 {messages[1]}");
    return Task.CompletedTask;
}

static Task CheckConflictContentAsync()
{
    var center = new NotificationCenter();
    AppNotification? n = null;
    center.Raised += x => n = x;
    center.NotifyConflict(@"C:\sync\报告.docx", 3, 7, "sp-1");

    Assert(n is not null && n.Kind == NotificationKind.Conflict, "应当是冲突通知");
    Assert(n!.Title.Contains("冲突"), $"标题应点明冲突,实际 {n.Title}");
    Assert(n.Message.Contains("报告.docx"), "应带上文件名(否则用户不知道是哪个文件)");
    Assert(n.Message.Contains("v3") && n.Message.Contains("v7"),
        $"应带上两边版本(用户据此判断),实际 {n.Message}");
    Assert(n.Message.Contains("保留") || n.Message.Contains("选"),
        $"必须告诉用户「需要你自己选」(冲突不会自动解决),实际 {n.Message}");
    Assert(n.Path is not null && n.SpaceId == "sp-1", "应带上路径与空间 id(点击跳转用)");
    return Task.CompletedTask;
}

static Task CheckSpaceRevokedContentAsync()
{
    var center = new NotificationCenter();
    AppNotification? n = null;
    center.Raised += x => n = x;
    center.NotifySpaceRevoked("sp-9", "你已被移出该空间");

    Assert(n!.Kind == NotificationKind.SpaceRevoked, "应当是被移出空间通知");
    Assert(n.Message.Contains("不会"), $"必须写明「本地文件不会被删除」(R-19:用户最担心文件没了),实际 {n.Message}");
    Assert(n.Message.Contains("恢复") || n.Message.Contains("继续"),
        $"应说明恢复访问后会继续同步,实际 {n.Message}");
    return Task.CompletedTask;
}

static Task CheckQuotaHysteresisAsync()
{
    var center = new NotificationCenter();
    var count = 0;
    center.Raised += _ => count++;

    center.NotifyQuota("sp-1", 85);
    Assert(count == 1, $"过线应报一次,实际 {count}");

    // 在阈值上下抖动:81 → 79 → 82 …(都没有回落到 70 以下)
    foreach (var p in new[] { 81, 79, 82, 80, 83 })
    {
        center.NotifyQuota("sp-1", p);
    }
    Assert(count == 1, $"滞回区间内抖动**不该重复报警**(否则会刷屏),实际 {count}");

    // 回落到解除线以下 → 重新武装
    center.NotifyQuota("sp-1", 65);
    Assert(count == 1, "回落后本身不报警(只是重新武装)");
    center.NotifyQuota("sp-1", 90);
    Assert(count == 2, $"回落后再次过线应当能再报(否则用户再也收不到预警),实际 {count}");

    // 另一个空间独立计数
    center.NotifyQuota("sp-2", 88);
    Assert(count == 3, "配额预警应当**按空间**独立(一个空间报过不影响另一个)");
    return Task.CompletedTask;
}

static Task CheckSaneOptionsAsync()
{
    // 解除阈值必须低于预警阈值,否则"滞回"不成立(会在同一区间反复报警)
    var bad = new NotifyOptions { QuotaWarnPercent = 80, QuotaClearPercent = 80 };
    Assert(!bad.IsSane(), "解除阈值等于预警阈值时不成立");
    var rejected = false;
    try
    {
        _ = new NotificationCenter(options: bad);
    }
    catch (ArgumentOutOfRangeException)
    {
        rejected = true;
    }
    Assert(rejected, "不自洽的通知策略必须被拒绝构造(而不是运行期悄悄刷屏)");

    var good = new NotifyOptions { QuotaWarnPercent = 80, QuotaClearPercent = 70 };
    Assert(good.IsSane(), "默认策略应当自洽");
    return Task.CompletedTask;
}

static Task CheckStartupRegistryRoundTripAsync()
{
    // 真实注册表往返,但用**临时子键**(不污染真正的开机启动项)
    var subKey = @"Software\NetDiskCheck\" + Guid.NewGuid().ToString("N");
    var reg = new RegistryStartupRegistration("NetDiskTest", subKey);
    var exe = @"C:\Program Files\NetDisk\NetDisk.App.exe";

    try
    {
        reg.Disable(); // 幂等:本来就不存在也不该报错
        Assert(!reg.IsEnabled(exe), "初始应当是未登记");

        reg.Enable(exe);
        Assert(reg.IsEnabled(exe), "登记后应当读回同样的路径");
        Assert(Registry.CurrentUser.OpenSubKey(subKey)?.GetValue("NetDiskTest") as string
               == "\"" + exe + "\"", "注册表里存的应当是可执行命令(带引号)");

        reg.Enable(exe); // 幂等:重复登记不产生第二条
        Assert(reg.IsEnabled(exe), "重复登记后仍是启用");

        reg.Disable();
        Assert(!reg.IsEnabled(exe), "取消后应当是未登记");
        reg.Disable(); // 再取消一次仍不报错
    }
    finally
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\NetDiskCheck", throwOnMissingSubKey: false); }
        catch (Exception) { /* 清理失败不影响结论 */ }
    }
    return Task.CompletedTask;
}

static Task CheckStartupQuotingAsync()
{
    // 含空格的路径**必须加引号**:否则 Windows 会把它解析成 "执行 C:\Program" + 参数,
    // 用户重启后什么都不会发生,而且没有任何报错(自启类功能最经典的失败形态)。
    var withSpace = @"C:\Program Files\NetDisk\NetDisk.App.exe";
    var cmd = RegistryStartupRegistration.CommandFor(withSpace);
    Assert(cmd == "\"" + withSpace + "\"", $"命令行必须带引号,实际 {cmd}");

    // 已经是带引号的输入不该被叠成两个引号
    Assert(RegistryStartupRegistration.CommandFor(cmd) == cmd, "重复加引号必须幂等");
    Assert(RegistryStartupRegistration.CommandFor("  " + withSpace + "  ") == cmd, "两侧空白应被去掉");
    return Task.CompletedTask;
}

static Task CheckTrayIsThinAsync()
{
    // 托盘适配器**只是渲染**:它订阅通知中心并把气泡画出来。
    // 断言:它不含"该不该通知"的策略(合并窗口/阈值),那些必须在 SyncEngine 里(可单测)。
    var path = Locate("desktop/src/NetDisk.App/Notify/TrayNotifier.cs");
    var code = File.ReadAllText(path);
    Assert(code.Contains("ShowBalloonTip", StringComparison.Ordinal), "托盘适配器应当真的显示气泡");
    Assert(code.Contains("_center.Raised +=", StringComparison.Ordinal),
        "托盘适配器必须订阅通知中心(策略在 SyncEngine,不在 UI)");

    foreach (var forbidden in new[] { "CompletionWindow", "QuotaWarnPercent", "QuotaClearPercent", "Coalesc" })
    {
        Assert(!code.Contains(forbidden, StringComparison.Ordinal),
            $"托盘适配器里出现 {forbidden}:通知策略不该写在 UI 层(那样只能靠人肉点着看)");
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
    throw new Exception("找不到 " + relative);
}

sealed class FakeClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 9, 12, 10, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now = _now.Add(delta);
}
