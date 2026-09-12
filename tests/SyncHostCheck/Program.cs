// SyncHostCheck —— 客户端接线的**端到端**检查器(零 NuGet)。
//
// 与其它检查器的区别:它不测纯函数,而是把**真实**的登录/令牌/传输/状态库/对账
// 全部接起来,打**真实服务端**(默认本机 8080,可用 NETDISK_E2E_BASE 指向真机),
// 验证"用户会做的事":登录 → 放文件 → 自动上传 → 远端改动 → 自动下载 → 冲突副本
// → 重启后状态一致。
//
// 为什么必须打真实服务端:
//   接线的错法**几乎全都不报错** —— 令牌没注入(ApiClient 会匿名请求)、
//   WebDAV 路径拼错(服务端 404 被吞)、状态库没落盘(重启后重复上传)。
//   只对着 mem:// 或者 mock 测,这些一个都抓不到。
//
// 环境变量:
//   NETDISK_E2E_BASE   服务端基址(默认 http://127.0.0.1:8080)
//   NETDISK_E2E_USER   登录名(默认 admin)
//   NETDISK_E2E_PASS   口令(必填;不填则跳过并 exit 0,便于 CI 无凭据时跑)
//
// 退出码 0 = 全过;1 = 有断言失败;2 = 环境不具备(没有口令/连不上)。

using System.Security.Cryptography;
using NetDisk.ClientCore;
using NetDisk.SyncEngine;
using NetDisk.SyncEngine.Files;
using NetDisk.SyncEngine.Host;
using NetDisk.Transport;

var baseUrl = Environment.GetEnvironmentVariable("NETDISK_E2E_BASE") ?? "http://127.0.0.1:8080";
var user = Environment.GetEnvironmentVariable("NETDISK_E2E_USER") ?? "admin";
var pass = Environment.GetEnvironmentVariable("NETDISK_E2E_PASS");
if (string.IsNullOrWhiteSpace(pass))
{
    Console.WriteLine("SKIP: 未设置 NETDISK_E2E_PASS(端到端检查需要有账号才能跑)");
    return 0;
}

var failures = 0;
void Check(string what, bool ok, string detail = "")
{
    if (ok) { Console.WriteLine($"  ✓ {what}"); return; }
    failures++;
    Console.WriteLine($"  ✗ {what}{(detail.Length > 0 ? "  [" + detail + "]" : "")}");
}

// 临时工作区:同步根、配置、令牌密文、状态库全部隔离(绝不碰用户真实数据)
var work = Path.Combine(Path.GetTempPath(), "netdisk-synchost-" + Guid.NewGuid().ToString("N")[..8]);
var root = Path.Combine(work, "root");
Directory.CreateDirectory(root);
var cfgPath = Path.Combine(work, "client.json");
var tokenPath = Path.Combine(work, "tokens.bin");
var statePath = Path.Combine(work, "state.db");

Console.WriteLine($"SyncHostCheck —— 端到端接线检查(base={baseUrl} user={user})");
Console.WriteLine();

ITokenProvider BuildTokenSession()
{
    var store = new DpapiTokenStore(tokenPath);
    var auth = new AuthApi(new ClientOptions { BaseAddress = new Uri(baseUrl) });
    return new TokenSession(store, auth, TimeProvider.System);
}

try
{
    // ---------------------------------------------------------------- ① 登录 + 令牌注入
    Console.WriteLine("— ① 登录与令牌注入");
    var session = (TokenSession)BuildTokenSession();
    await session.SignInAsync(user, pass!);
    Check("账密登录成功", session.IsSignedIn);

    // 可选:把登录后的**令牌密文**导出到指定路径。
    // 用途:验证真实客户端(App)的接线 —— App 的启动路径之一是"用已保存的令牌直接开跑",
    // 而它读的是 %APPDATA%\NetDisk\tokens.bin。DPAPI 按**当前用户**加密,所以同一用户下
    // 把这份文件放到那个路径就能被 App 解密,从而无需人手输入口令即可验证整条 UI 链路。
    var tokenOut = Environment.GetEnvironmentVariable("NETDISK_E2E_TOKEN_OUT");
    if (!string.IsNullOrWhiteSpace(tokenOut))
    {
        if (File.Exists(tokenPath))
        {
            File.Copy(tokenPath, tokenOut, overwrite: true);
            Console.WriteLine($"    已导出令牌密文 → {tokenOut}");
        }
        else
        {
            Console.WriteLine($"    ⚠ 未找到令牌密文 {tokenPath}(导出跳过)");
        }
    }

    var api = new ApiClient(new ClientOptions { BaseAddress = new Uri(baseUrl) }, tokens: session);
    var files = new FileApi(api);
    var spaces = await files.ListSpacesAsync();
    Check("带令牌列空间成功(证明令牌真的注入了 ApiClient)",
        spaces.spaces is { Count: > 0 }, $"spaces={spaces.spaces?.Count ?? 0}");
    var space = spaces.spaces!.First(s => s.kind == "personal");
    Console.WriteLine($"    个人空间 = {space.id}");

    // Path 只能通过 Load(path) 设定(private set):这样"配置写到哪"永远由加载来源决定,
    // 避免出现"从 A 读、往 B 写"这种把用户配置写丢的错法
    var cfg = ClientConfig.Load(cfgPath);
    cfg.BaseUrl = baseUrl;
    cfg.SyncRoot = root;
    cfg.SpaceId = space.id;
    cfg.Onboarded = true;
    cfg.Save();
    var reloaded = ClientConfig.Load(cfgPath);
    Check("配置能落盘并读回(原子写)", reloaded.BaseUrl == baseUrl && reloaded.SyncRoot == root);

    // ①b **用已保存的令牌**启动(= 真实客户端开机自动同步走的那条路)。
    // 这条路径特别值得单独测:它没有登录那一步,所以"令牌没落盘/落盘了读不回来/
    // 读回来了却没注入 ApiClient"全都只会表现为"启动后什么都没发生" ——
    // 界面上与"服务器上没有文件"完全一样。实测 App 第一次跑就是靠它才连上的。
    {
        var auto = SyncRuntime.FromStoredToken(ClientConfig.Load(cfgPath), tokenPath: tokenPath);
        var autoStarted = await auto.StartAsync();
        Check("用已保存令牌启动同步(自动启动路径)", autoStarted);
        Check("启动过程留了可补看的进展历史", auto.RecentNotices.Count > 0,
            $"notices={auto.RecentNotices.Count}");
        await auto.DisposeAsync();
    }

    var remoteNames = new HashSet<string>(StringComparer.Ordinal);

    // ---------------------------------------------------------------- ② 本地新文件 → 自动上传
    Console.WriteLine();
    Console.WriteLine("— ② 本地放文件 → 自动上传 → 服务端可见");
    var name1 = $"sync-check-{DateTime.Now:HHmmss}.txt";
    var local1 = Path.Combine(root, name1);
    var content1 = "hello-from-client-" + Guid.NewGuid().ToString("N")[..8];
    await File.WriteAllTextAsync(local1, content1);

    await using (var host = new SyncHost(cfg, BuildTokenSession(), api, statePath: statePath))
    {
        host.Notice += m => Console.WriteLine("    [notice] " + m);
        Console.WriteLine("    … StartAsync");
        var started = await host.StartAsync();
        Check("宿主启动(已登录且配置可用)", started);
        // 重新对账一次:StartAsync 内部已经对账,这里再跑一次以确保收敛
        Console.WriteLine("    … ReconcileAsync #2");
        await host.ReconcileAsync();
        Console.WriteLine("    … ListAsync(远端)");
        var remote = (await files.ListAsync(space.id, null)).FirstOrDefault(e => e.name == name1);
        Console.WriteLine("    … 已拿到远端列表");
        Check("服务端出现了该文件", remote is not null, remote?.id ?? "未找到");
        if (remote is not null)
        {
            remoteNames.Add(name1);
            var localBytes = await File.ReadAllBytesAsync(local1);
            Check("本地文件仍在(未被自己的上传覆盖)", localBytes.Length > 0);
            var status = host.Status.FirstOrDefault(s => s.RelativePath == name1);
            Check("状态列表里该文件已同步", status?.State == SyncState.InSync,
                $"state={status?.State.ToString() ?? "(无)"}");
        }
    }

    // ---------------------------------------------------------------- ③ 远端改动 → 自动下载
    Console.WriteLine();
    Console.WriteLine("— ③ 服务端新建文件 → 自动下载到本地");
    var name2 = $"remote-check-{DateTime.Now:HHmmss}.txt";
    var content2 = "hello-from-server-" + Guid.NewGuid().ToString("N")[..8];
    var staging = Path.Combine(work, name2);
    await File.WriteAllTextAsync(staging, content2);
    var up = await files.UploadAsync(space.id, null, name2, staging);
    remoteNames.Add(name2);
    Check("服务端侧建好测试文件(模拟另一端)", up.Version >= 1, $"version={up.Version}");

    await using (var host = new SyncHost(cfg, BuildTokenSession(), api, statePath: statePath))
    {
        await host.StartAsync();
        await host.ReconcileAsync();
        var local2 = Path.Combine(root, name2);
        Check("本地出现了该文件", File.Exists(local2), local2);
        if (File.Exists(local2))
        {
            var got = await File.ReadAllTextAsync(local2);
            Check("内容与服务端一致(逐字节)", got == content2);
            Check("状态列表里标记为已同步",
                host.Status.FirstOrDefault(s => s.RelativePath == name2)?.State == SyncState.InSync);
        }
    }

    // ---------------------------------------------------------------- ④ 两端同改 → 冲突副本
    Console.WriteLine();
    Console.WriteLine("— ④ 两端同改同一文件 → 本地保留冲突副本 + 拉回远端版本");
    var localPath1 = Path.Combine(root, name1);
    await File.WriteAllTextAsync(localPath1, "local-edit-" + Guid.NewGuid().ToString("N")[..8]);
    // 远端也改(用同一条上传接口覆盖同名文件)
    var staging2 = Path.Combine(work, "remote-edit.txt");
    var contentRemote = "remote-edit-" + Guid.NewGuid().ToString("N")[..8];
    await File.WriteAllTextAsync(staging2, contentRemote);
    // "另一端改了这个文件" = 同名**原地覆盖**。这里刻意不再用"先删后传":
    // 那会让远端拿到新 file id、版本号回到 1,于是"远端 version > 本地已知 version"
    // 这条冲突判据永远不成立 —— 表现就是另一端的修改被本地静默覆盖。
    // 覆盖意图现在由契约的 allow_overwrite 承载(TUS 定稿时原地生成新版本)。
    var before = (await files.FindByNameAsync(space.id, null, name1))!;
    await files.UploadAsync(space.id, null, name1, staging2, allowOverwrite: true);
    var after = (await files.FindByNameAsync(space.id, null, name1))!;
    // 覆盖必须**原地**发生。这两条是 ④ 的前提:身份不连续 / 版本倒退时,
    // 冲突检测在客户端侧是不可能正确工作的。
    Check("远端覆盖后 file id 不变(身份连续)", after.id == before.id,
        $"before={before.id} after={after.id}");
    Check("远端覆盖后版本号递增(冲突判据的基础)", after.version > before.version,
        $"before={before.version} after={after.version}");

    await using (var host = new SyncHost(cfg, BuildTokenSession(), api, statePath: statePath))
    {
        await host.StartAsync();
        await host.ReconcileAsync();
        var copies = Directory.GetFiles(root, "*_conflict_*").Select(Path.GetFileName).ToArray();
        // 两端都改过:本地这一版必须被保留成冲突副本(而不是被远端静默覆盖掉)。
        Check("本地生成了冲突副本(本地改动没被丢弃)", copies.Length > 0,
            copies.Length > 0 ? string.Join(',', copies) : "没有副本");
        if (copies.Length > 0)
        {
            var copyText = await File.ReadAllTextAsync(Path.Combine(root, copies[0]!));
            Check("冲突副本内容是**本地**那一版(不是远端覆盖后的)",
                copyText.StartsWith("local-edit-", StringComparison.Ordinal), copyText[..Math.Min(24, copyText.Length)]);
            var canonical = await File.ReadAllTextAsync(localPath1);
            Check("原路径上是**远端**那一版", canonical == contentRemote,
                canonical[..Math.Min(24, canonical.Length)]);
        }
        var st = host.Status.FirstOrDefault(s => s.RelativePath == name1);
        // 原来这里写成 `Conflict or InSync`(等于放行任何结果,断言没有信息量)。
        // 冲突已能产生副本,就把状态收紧成**必须**是 Conflict。
        Check("状态列表里该文件标注为冲突", st?.State == SyncState.Conflict,
            $"state={st?.State.ToString() ?? "(无)"} msg={st?.Message}");
    }

    // ---------------------------------------------------------------- ⑤ 重启后一致(不重复传)
    Console.WriteLine();
    Console.WriteLine("— ⑤ 重启客户端 → 状态与文件一致(状态库真的落盘了)");
    await using (var host = new SyncHost(cfg, BuildTokenSession(), api, statePath: statePath))
    {
        await host.StartAsync();
        await host.ReconcileAsync();
        Check("状态库跨进程保留(已知文件仍在列表里)",
            host.Status.Any(s => s.RelativePath == name1 || s.RelativePath == name2));
        var remoteFiles = await files.ListAsync(space.id, null);
        var dupes = remoteFiles.Where(e => e.name == name1).ToArray();
        Check("同名远端条目只有一条(没有因重启重复上传)", dupes.Length == 1,
            $"count={dupes.Length}");
    }

    // ------------------------------------------------ ⑥ 本地改名(经 FileId 复用,不重传)
    // 目标 ③ 的"改名经 FileId 不重传"。曾经**没做到**(本机扫描只按相对路径记账,
    // 改名被当成新文件整份重传、服务端还留下重复的旧条目 —— 有实测证据);
    // 现在按**本机文件身份**(DE-D-11)匹配已知条目 → 调契约的改名接口原地改,
    // 于是远端 file_id 不变、版本 +1、旧名字消失。下面的断言就是这三件事。
    Console.WriteLine();
    Console.WriteLine("— ⑥ 本地改名(经 FileId 复用,不重传)");
    var nameRenamed = $"sync-check-{DateTime.Now:HHmmss}-renamed.txt";
    await using (var host = new SyncHost(cfg, BuildTokenSession(), api, statePath: statePath))
    {
        await host.StartAsync();
        await host.ReconcileAsync();

        // 先确保 name2 已在两端一致(③ 建的),再在本地改名
        var before2 = await files.FindByNameAsync(space.id, null, name2);
        Check("改名前:该文件在服务端存在(⑥ 的前提)", before2 is not null);
        var localOld = Path.Combine(root, name2);
        var localNew = Path.Combine(root, nameRenamed);
        File.Move(localOld, localNew, overwrite: true);

        await host.ReconcileAsync();

        var afterNew = await files.FindByNameAsync(space.id, null, nameRenamed);
        var afterOld = await files.FindByNameAsync(space.id, null, name2);
        var localNewText = await File.ReadAllTextAsync(localNew);
        // 把服务端那份**下载回来**比对(不只看名字存在:名字在、内容错也是缺陷)
        string? downloaded = null;
        if (afterNew is not null)
        {
            var verifyPath = Path.Combine(work, "verify-rename.txt");
            await files.DownloadAsync(afterNew.id, verifyPath);
            downloaded = await File.ReadAllTextAsync(verifyPath);
        }

        // 数据安全必须成立:改名的文件在两端都在、内容正确
        Check("改名后新名字出现在服务端且内容一致",
            afterNew is not null && downloaded == localNewText,
            afterNew is null ? "服务端没有新名字" : "内容不一致");
        Check("本地改名后文件仍在本地(没被删)",
            File.Exists(localNew) && localNewText.Length > 0);

        // **改名不重传的硬判据**:远端 file_id 必须不变(变了就说明是"新建一份"),
        // 且旧名字必须消失(否则服务端留下了重复文件)。
        Check("改名复用了同一个远端 file id(没有重传)",
            afterNew is not null && before2 is not null && afterNew.id == before2.id,
            $"旧 id={before2?.id ?? "(无)"} 新 id={afterNew?.id ?? "(无)"}");
        Check("改名后旧名字在服务端消失(没有残留重复)",
            afterOld is null, afterOld is null ? "" : $"旧条目仍在:{afterOld.id}");
        Check("改名后远端版本递增(服务端原地改,不是新建)",
            afterNew is not null && before2 is not null && afterNew.version > before2.version,
            $"before={before2?.version} after={afterNew?.version}");
    }
}
finally
{
    // 清理服务端测试文件:失败也要清(残留会让下一次跑变成"名字冲突")
    try
    {
        var store = new DpapiTokenStore(tokenPath);
        var auth = new AuthApi(new ClientOptions { BaseAddress = new Uri(baseUrl) });
        var s = new TokenSession(store, auth, TimeProvider.System);
        if (await s.StartAsync())
        {
            var api = new ApiClient(new ClientOptions { BaseAddress = new Uri(baseUrl) }, tokens: s);
            var files = new FileApi(api);
            var spaces = await files.ListSpacesAsync();
            var space = spaces.spaces!.First(x => x.kind == "personal");
            foreach (var e in await files.ListAsync(space.id, null))
            {
                if (e.name.StartsWith("sync-check-", StringComparison.Ordinal) ||
                    e.name.StartsWith("remote-check-", StringComparison.Ordinal) ||
                    e.name.Contains("_conflict_", StringComparison.Ordinal))
                {
                    using var resp = await api.SendRawAsync(HttpMethod.Delete,
                        $"/api/v1/files/{Uri.EscapeDataString(e.id)}",
                        contentFactory: null, headers: null, idempotent: true);
                    Console.WriteLine($"  清理远端:{e.name} → {(int)resp.StatusCode}");
                }
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  (清理远端时出错,不影响结论:{ex.Message})");
    }

    try { Directory.Delete(work, recursive: true); } catch (Exception) { }
}

Console.WriteLine();
if (failures == 0)
{
    Console.WriteLine("全部通过:SyncHost 端到端接线(登录/上传/下载/冲突/重启一致)");
    return 0;
}
Console.WriteLine($"{failures} 项失败");
return 1;
