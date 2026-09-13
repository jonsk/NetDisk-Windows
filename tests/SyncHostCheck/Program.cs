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
using NetDisk.SyncEngine.Onboarding;
using NetDisk.SyncEngine.Watch;
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

    // ------------------------------------------------ ⑧ 首次运行向导(用真实清单)
    // 放在**最前面**:此刻远端只有本用例自己造的东西(通常就是空的),所以这一节能跑到
    // 用户实测撞上的那条路径 —— 空清单建计划。
    // 「登录并开始同步」真的会走:拉远端清单 → 建计划 → 校验;空空间曾被误拦:
    // 「同步目录不可用:没有可检查的路径(尚未拉到远程清单?)」,永远进不去。
    // 只测"假清单"发现不了它(那段逻辑在计划层),所以必须有真机断言。
    Console.WriteLine();
    Console.WriteLine("— ⑧ 首次运行向导(真实清单)");
    {
        var tree = await files.CollectTreeAsync(space.id, null);
        var wizard = new OnboardingWizard((_, _) => Task.FromResult<IReadOnlyList<RemoteEntry>>(tree));
        var plan = await wizard.BuildAsync(FirstSyncChoice.FullWithThrottle, root);
        var problems = plan.Validate();
        Console.WriteLine($"    真实清单 {tree.Count} 条;RemoteTreeWasEmpty={plan.RemoteTreeWasEmpty}");
        Check($"⑧ 真实清单({tree.Count} 条,含 0 条)能过首次运行校验", problems.Count == 0,
            problems.Count == 0 ? "" : $"被拦下:{string.Join("|", problems)}");
        Check("⑧ 空清单被正确标记(登录页据此显示「服务器上还没有任何文件」)",
            plan.RemoteTreeWasEmpty == (tree.Count == 0),
            $"tree={tree.Count} RemoteTreeWasEmpty={plan.RemoteTreeWasEmpty}");
    }

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

        // **本地不能留下重复**:对账顺序是"先远端→本地,再本地→远端",如果下载阶段
        // 不看"这个旧名字是被改名带走的",就会把旧名字从服务端**拉回来一份** ——
        // 远端是对的(旧名已消失),本地却多出一个旧名字,下一轮又会被当成新文件传上去。
        Check("改名后本地没有留下重复的旧文件",
            File.Exists(localNew) && !File.Exists(Path.Combine(root, name2)),
            File.Exists(Path.Combine(root, name2)) ? "本地仍残留旧名字(下载阶段把它拉回来了)" : "本地缺新名字");
    }

    // ------------------------------------------------ ⑦ 删除传播(双向,无用户确认)
    // 产品决策(2026-09-13):**删除要双向传播、一致优先、两端都不弹确认**。
    // 安全性不靠"问用户",而靠**信号完整性**(见 SyncHost.PropagateDeletionsAsync):
    // 扫描截断 / 同步根不可达 / 待删文件所在目录不可枚举 → 整轮或个人不删;
    // 并且**一律按 file_id 删**(按名字删会误杀另一端刚建的同名新文件)。
    Console.WriteLine();
    Console.WriteLine("— ⑦ 删除传播(双向)");
    var nameLocalDel = $"sync-check-{DateTime.Now:HHmmss}-localdel.txt";
    var nameRemoteDel = $"sync-check-{DateTime.Now:HHmmss}-remotedel.txt";
    var nameSameNew = $"sync-check-{DateTime.Now:HHmmss}-samename.txt";
    // ⚠ ⑦ 的宿主**关掉本地监听**:这一段会手工删/建文件,而 watcher 会在后台自己触发对账,
    // 两边抢同一个同步根 + 同一个状态库 → 断言变成时序抽奖(实测:同一份代码时红时绿)。
    // 删除传播的正确性要靠**显式驱动**的对账来判定;监听路径本身由 ①~⑥ 覆盖。
    await using (var host = new SyncHost(cfg, BuildTokenSession(), api, statePath: statePath, watchLocal: false))
    {
        await host.StartAsync();
        await host.ReconcileAsync();
        await File.WriteAllTextAsync(Path.Combine(root, nameLocalDel), "local-delete-me");
        await File.WriteAllTextAsync(Path.Combine(root, nameRemoteDel), "remote-delete-me");
        await File.WriteAllTextAsync(Path.Combine(root, nameSameNew), "old-content");
        await host.ReconcileAsync();
        Check("⑦ 前提:三个文件都已上传",
            await files.FindByNameAsync(space.id, null, nameLocalDel) is not null &&
            await files.FindByNameAsync(space.id, null, nameRemoteDel) is not null &&
            await files.FindByNameAsync(space.id, null, nameSameNew) is not null);

        // ① 本地删除 → **远端也要删掉**(不再复活)
        File.Delete(Path.Combine(root, nameLocalDel));
        await host.ReconcileAsync();
        Check("⑦ 本地删除传播到远端(远端不再有该文件)",
            await files.FindByNameAsync(space.id, null, nameLocalDel) is null,
            "远端仍有该文件 = 删除没传播");
        Check("⑦ 本地删除后本地也不会被重新下载回来",
            !File.Exists(Path.Combine(root, nameLocalDel)));

        // ② 远端删除 → **本地也要删掉**
        var remoteEntry = await files.FindByNameAsync(space.id, null, nameRemoteDel);
        if (remoteEntry is not null)
        {
            using var del = await api.SendRawAsync(HttpMethod.Delete,
                $"/api/v1/files/{Uri.EscapeDataString(remoteEntry.id)}",
                contentFactory: null, headers: null, idempotent: true);
            Console.WriteLine($"    远端删除 {nameRemoteDel} → {(int)del.StatusCode}");
            await host.ReconcileAsync();
            Check("⑦ 远端删除传播到本地(本地副本被删掉)",
                !File.Exists(Path.Combine(root, nameRemoteDel)),
                "本地仍在 = 远端删除没传播");
            Check("⑦ 远端删除后本地不会被重新上传(远端仍无该文件)",
                await files.FindByNameAsync(space.id, null, nameRemoteDel) is null,
                "远端又出现了 = 客户端把已删文件复活了");
        }

        // ③ **按 id 删 + 复活当新文件**:另一端"删了又建同名"(真正的**新 file id**)时,
        //    我们的删除只能删自己知道的那一份,新文件必须活下来并被当新文件下载到本地。
        //    注意:同名**覆盖**(allow_overwrite)是原地升版本、id 不变,造不出"新 id";
        //    要造新 id 必须先删再传(这也正是另一端"删了又建"的真实形态)。
        var oldSame = await files.FindByNameAsync(space.id, null, nameSameNew);
        if (oldSame is not null)
        {
            using (var delSame = await api.SendRawAsync(HttpMethod.Delete,
                       $"/api/v1/files/{Uri.EscapeDataString(oldSame.id)}",
                       contentFactory: null, headers: null, idempotent: true))
            {
                Console.WriteLine($"    另一端先删掉 {nameSameNew} → {(int)delSame.StatusCode}");
            }
            var stagingSame = Path.Combine(work, "same-name-new.txt");
            await File.WriteAllTextAsync(stagingSame, "new-content-from-other-end");
            await files.UploadAsync(space.id, null, nameSameNew, stagingSame);
            var newSame = await files.FindByNameAsync(space.id, null, nameSameNew);
            Check("⑦ 另一端「删了又建同名」拿到了新的 file id",
                newSame is not null && newSame.id != oldSame.id,
                $"old={oldSame.id} new={newSame?.id}");

            // 本地也删掉(与另一端并发):我们只知道旧 id
            File.Delete(Path.Combine(root, nameSameNew));
            await host.ReconcileAsync();
            var afterSame = await files.FindByNameAsync(space.id, null, nameSameNew);
            Check("⑦ 删除只删自己知道的那一份(file id),不误杀同名新文件",
                afterSame is not null && afterSame.id == newSame!.id,
                afterSame is null ? "同名新文件被误删了(说明按名字删了)" : $"id 变了:{afterSame.id}");
            // 复活的一律当新文件 → 它的内容应当被下载到本地
            var localSame = Path.Combine(root, nameSameNew);
            Check("⑦ 复活的一律当新文件(本地拿到另一端的新内容)",
                File.Exists(localSame) &&
                await File.ReadAllTextAsync(localSame) == "new-content-from-other-end",
                File.Exists(localSame) ? "内容不是新那一版" : "本地没有该文件");
        }
    }

    // ④ **硬保护**:同步根不可达时(盘符变化/网络盘掉线)**绝不能**把远端删光
    //    这是删除功能最危险的一条:本机扫描返回空集,与"用户把文件全删了"完全一样。
    {
        var guardName = $"sync-check-{DateTime.Now:HHmmss}-guard.txt";
        await using (var host = new SyncHost(cfg, BuildTokenSession(), api, statePath: statePath, watchLocal: false))
        {
            await host.StartAsync();
            await host.ReconcileAsync();
            await File.WriteAllTextAsync(Path.Combine(root, guardName), "must-survive");
            await host.ReconcileAsync();
            Check("⑦ 硬保护:前提文件已上传",
                await files.FindByNameAsync(space.id, null, guardName) is not null);
        }
        var moved = root + "-moved";
        Directory.Move(root, moved);
        try
        {
            await using var host = new SyncHost(cfg, BuildTokenSession(), api, statePath: statePath, watchLocal: false);
            await host.StartAsync();
            await host.ReconcileAsync();
        }
        catch (Exception ex)
        {
            // 同步根不存在时对账报错是**可以接受**的(总比删光好);关键是下面的断言
            Console.WriteLine($"    (同步根不可达时对账报错,可接受:{ex.GetType().Name})");
        }
        Check("⑦ 硬保护:同步根不可达时**没有**把远端文件删掉",
            await files.FindByNameAsync(space.id, null, guardName) is not null,
            "远端文件被删了 = 保护失效(最危险的一类误删)");
        // 还原:同步根**可能已被 StartAsync 重建为一个空目录**(那正是这道保护的起因),
        // 于是要先清掉它再搬回来,否则 Move 会因为"目标已存在"抛异常。
        // ⚠ 必须**真的**还原:被重建的那个目录拿到了**新的 FileId**,而状态库里记的是原根身份 ——
        // 不还原的话,后续场景会被身份保护正确地挡住(实测:整批删除"没传播",其实是保护在生效)。
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
        Directory.Move(moved, root); // 还原,后续清理照常
    }

    // ⑤ **整批删除也要立即传播**(一致优先,不做"消失一大片就先等等"的延迟确认)。
    //
    // 为什么不做延迟确认(试过之后的结论):
    //   · 产品决策是"一致优先、无条件"——删 100 个文件和删 1 个文件同等对待;
    //   · 那条路本身自相矛盾:本轮"只标记不删",下载阶段又会把远端还在的文件拉回本地,
    //     下一轮便不再有候选 —— 延迟确认永远落不了地(实测:第二轮远端仍剩 5/5)。
    // 真正的保护是**同步根身份**(见上一节的硬保护):换盘/根被重建 ⇒ 新的 FileId ⇒ 整轮不删。
    {
        var massPrefix = $"sync-check-{DateTime.Now:HHmmss}-mass";
        const int massCount = 5;
        var massNames = new List<string>();
        await using (var host = new SyncHost(cfg, BuildTokenSession(), api, statePath: statePath, watchLocal: false))
        {
            await host.StartAsync();
            await host.ReconcileAsync();
            for (var i = 0; i < massCount; i++)
            {
                var n = $"{massPrefix}-{i:D3}.txt";
                massNames.Add(n);
                await File.WriteAllTextAsync(Path.Combine(root, n), $"mass-{i}");
            }
            // 前提必须**真的**成立:有界重试直到全部传上去(排队是异步的,单次对账不保证排空)
            var uploaded = 0;
            for (var attempt = 0; attempt < 10 && uploaded < massCount; attempt++)
            {
                await host.ReconcileAsync();
                uploaded = 0;
                foreach (var n in massNames)
                {
                    if (await files.FindByNameAsync(space.id, null, n) is not null)
                    {
                        uploaded++;
                    }
                }
            }
            Check($"⑦ 整批删除:前提 {massCount} 个文件都已上传", uploaded == massCount, $"实际上传 {uploaded}");

            // 整批本地删除 → 一轮对账内全部传播
            foreach (var n in massNames)
            {
                File.Delete(Path.Combine(root, n));
            }
            await host.ReconcileAsync();
            var remaining = 0;
            foreach (var n in massNames)
            {
                if (await files.FindByNameAsync(space.id, null, n) is not null)
                {
                    remaining++;
                }
            }
            Check("⑦ 整批删除立即传播(一致优先,不做延迟确认)",
                remaining == 0, $"远端还剩 {remaining}/{massCount}");
        }
    }

    // ------------------------------------------------ ⑨ 暂停 / 继续同步
    // 暂停的语义必须**可判定**,否则界面上的按钮只是装饰:
    //   · 暂停中:不再对账(连"立即同步"也跳过)、不再上传/下载新文件;
    //   · 恢复后:暂停期间攒下的改动要**自动补上**(靠恢复时的一次全量对账);
    //   · 全程不动数据(不删、不回滚、不清理状态库)。
    Console.WriteLine();
    Console.WriteLine("— ⑨ 暂停 / 继续同步");
    {
        var pausedName = $"sync-check-{DateTime.Now:HHmmss}-paused.txt";
        await using var host = new SyncHost(cfg, BuildTokenSession(), api, statePath: statePath, watchLocal: false);
        await host.StartAsync();
        await host.ReconcileAsync();
        Check("⑨ 初始为运行态", !host.IsPaused && host.IsRunning);

        await host.PauseAsync();
        Check("⑨ 暂停后 IsPaused=true 且 IsRunning=false", host.IsPaused && !host.IsRunning);

        // 暂停期间放一个新文件:对账被跳过 → 服务端不该出现它
        await File.WriteAllTextAsync(Path.Combine(root, pausedName), "created-while-paused");
        // 这一条**专门钉住"对账护栏"这一层**:暂停其实有两层保护(对账护栏 + 队列会话取消),
        // 只断言"文件没传上去"是不够的 —— 实测拆掉护栏后断言**照样通过**(队列那层挡住了),
        // 也就是说那条断言测不出护栏在不在。所以这里断言"护栏确实发了跳过通知"。
        var sawPauseSkip = false;
        void OnPauseNotice(string m)
        {
            if (m.Contains("已暂停", StringComparison.Ordinal))
            {
                sawPauseSkip = true;
            }
        }
        host.Notice += OnPauseNotice;
        await host.ReconcileAsync(); // 显式调用也必须被跳过
        host.Notice -= OnPauseNotice;
        Check("⑨ 暂停期间的对账护栏确实生效(明确报「已暂停…跳过」)", sawPauseSkip,
            sawPauseSkip ? "" : "没看到跳过通知 —— 护栏可能被拆掉,只是被队列那层掩盖了");
        var appeared = await files.FindByNameAsync(space.id, null, pausedName);
        Check("⑨ 暂停期间新文件不会被上传(手动对账也被跳过)", appeared is null,
            appeared is null ? "" : "暂停期间仍然传上去了");

        // 恢复:应当自动把暂停期间的新文件补传上去
        await host.ResumeAsync();
        Check("⑨ 恢复后 IsPaused=false 且 IsRunning=true", !host.IsPaused && host.IsRunning);
        var afterResume = await files.FindByNameAsync(space.id, null, pausedName);
        Check("⑨ 恢复后暂停期间的改动被自动补上(新文件已上传)", afterResume is not null,
            afterResume is null ? "恢复后仍未上传" : "");
        Check("⑨ 暂停/恢复不动数据(本地文件仍在)",
            File.Exists(Path.Combine(root, pausedName)));
    }

    // ------------------------------------------------ ⑩ 每文件进度百分比
    // 大文件在界面上长时间只显示"上传中",与卡住无法区分 —— 所以进度必须真的能透出来。
    // 断言方式:传一个**足够大**的文件(> 队列默认 1MB 的上报阈值,取 8MB),
    // 收集 StatusChanged 快照,要求其中**至少有一帧**该文件的进度落在 (0,100);
    // 并断言**收尾时进度被清空**(否则传完还挂着 87%,比不显示更误导)。
    Console.WriteLine();
    Console.WriteLine("— ⑩ 每文件进度百分比");
    {
        var bigName = $"sync-check-{DateTime.Now:HHmmss}-big.bin";
        var bigPath = Path.Combine(root, bigName);
        var bytes = new byte[8 * 1024 * 1024];
        Random.Shared.NextBytes(bytes);
        await File.WriteAllBytesAsync(bigPath, bytes);

        var frames = new List<double?>();
        await using (var host = new SyncHost(cfg, BuildTokenSession(), api, statePath: statePath, watchLocal: false))
        {
            host.StatusChanged += snap =>
            {
                var row = snap.FirstOrDefault(s => s.RelativePath == bigName);
                if (row is not null)
                {
                    lock (frames)
                    {
                        frames.Add(row.ProgressPercent);
                    }
                }
            };
            await host.StartAsync();
            await host.ReconcileAsync();
        }

        List<double?> snapshot;
        lock (frames)
        {
            snapshot = frames.ToList();
        }
        var mid = snapshot.Where(p => p is > 0 and < 100).ToList();
        Check("⑩ 上传过程中能看到中间进度(0 < 进度 < 100)",
            mid.Count > 0, $"共 {snapshot.Count} 帧,中间帧 {mid.Count} 帧");
        Check("⑩ 收尾时进度被清空(不留半截百分比)",
            snapshot.Count > 0 && snapshot[^1] is null, $"最后一帧={snapshot.LastOrDefault()?.ToString() ?? "null"}");
        Check("⑩ 大文件最终确实传上去了",
            await files.FindByNameAsync(space.id, null, bigName) is not null);
    }

    // ------------------------------------------------ ⑪ 保存设置后"按新配置重启同步"
    // 设置页的「保存并重启同步」做两件事:把值写进配置,然后**换掉整个运行时**。
    // 为什么必须换:同步目录、并发、限速都固化在 SyncHost/TransferQueue 的构造里,
    // 就地改只会让界面显示新值而行为还是旧的 —— 那比不支持修改更糟(用户以为改了)。
    // 这里钉住"换完确实生效",UI 本身的点击仍需人工验证(见验收清单 §5.3)。
    Console.WriteLine();
    Console.WriteLine("— ⑪ 按新配置重启同步");
    {
        var newRoot = Path.Combine(work, "root-restarted");
        Directory.CreateDirectory(newRoot);
        var cfg2Path = Path.Combine(work, "client-restarted.json");
        var cfg2 = ClientConfig.Load(cfg2Path);
        cfg2.BaseUrl = baseUrl;
        cfg2.SyncRoot = newRoot;
        cfg2.SpaceId = space.id;
        cfg2.Onboarded = true;
        cfg2.MaxConcurrency = 7;
        cfg2.UploadKbps = 512;
        cfg2.Save();

        var fresh = SyncRuntime.FromStoredToken(ClientConfig.Load(cfg2Path), tokenPath: tokenPath);
        Check("⑪ 用新配置能重新启动同步", await fresh.StartAsync());
        Check("⑪ 新配置的并发真的生效(7)",
            fresh.Host.QueueOptions.MaxConcurrency == 7,
            $"实际 {fresh.Host.QueueOptions.MaxConcurrency}");
        Check("⑪ 新配置的限速真的生效(512 KB/s)",
            fresh.Host.QueueOptions.RateLimit.UploadBytesPerSecond == 512 * 1024,
            $"实际 {fresh.Host.QueueOptions.RateLimit.UploadBytesPerSecond}");

        var restartName = $"sync-check-{DateTime.Now:HHmmss}-restart.txt";
        await File.WriteAllTextAsync(Path.Combine(newRoot, restartName), "after-restart");
        await fresh.ReconcileAsync();
        Check("⑪ 换到新同步目录后仍能正常同步(新目录里的文件已上传)",
            await files.FindByNameAsync(space.id, null, restartName) is not null);
        await fresh.DisposeAsync();
    }

    // ------------------------------------------------ ⑫ 目录级:新建 / 删除 / 改名保护
    // 目录也要参与同步(2026-09-13):用户建的空文件夹要在另一端出现,删掉的文件夹要**整棵子树**同步删。
    // 三条断言都对着"用户会做的事",另加一条**数据安全**断言(目录改名暂不支持,但绝不能误删远端子树)。
    Console.WriteLine();
    Console.WriteLine("— ⑫ 目录级:新建 / 删除 / 改名保护");
    {
        var dirPrefix = $"sync-check-{DateTime.Now:HHmmss}";
        await using var host = new SyncHost(cfg, BuildTokenSession(), api, statePath: statePath, watchLocal: false);
        if (Environment.GetEnvironmentVariable("NETDISK_E2E_VERBOSE") == "1")
        {
            host.Notice += m => Console.WriteLine($"     · {m}");
        }
        // 失败诊断:把"本地扫描 / 远端树 / 每文件状态"三份对照打出来。
        // 目录同步的失败往往表现为"某个文件没上传",而这三份数据分别回答
        // "扫描看得到吗 / 它现在在哪 / 传输层怎么记的" —— 缺任何一份都只能靠猜。
        async Task DiagAsync(string label)
        {
            if (Environment.GetEnvironmentVariable("NETDISK_E2E_VERBOSE") != "1")
            {
                return;
            }
            Console.WriteLine($"     [诊断:{label}] 本地扫描(相对同步根):");
            foreach (var e in new DirectoryScanner().Scan(root, recursive: true))
            {
                Console.WriteLine($"       {(e.IsDirectory ? "[D]" : "[F]")} {Path.GetRelativePath(root, e.Path)}");
            }
            Console.WriteLine($"     [诊断:{label}] 远端树(含 id / parent_id):");
            async Task WalkAsync(EntryView? parent, string indent)
            {
                foreach (var k in await files.ListAsync(space.id, parent?.id))
                {
                    Console.WriteLine($"       {indent}{k.name}{(k.is_dir ? "/" : "")} id={k.id[..8]} parent={k.parent_id?[..8]} v={k.version}");
                    if (k.is_dir)
                    {
                        await WalkAsync(k, indent + "  ");
                    }
                }
            }
            await WalkAsync(null, "");
            Console.WriteLine($"     [诊断:{label}] 状态表:");
            foreach (var s in host.Status)
            {
                Console.WriteLine($"       {s.RelativePath} → {s.State} | {s.Message} | v={s.Version}");
            }
        }
        await host.StartAsync();
        await host.ReconcileAsync();

        // ① 空目录也要同步(否则用户建的文件夹在另一端"不存在")
        var emptyDirName = $"{dirPrefix}-empty";
        Directory.CreateDirectory(Path.Combine(root, emptyDirName));
        await host.ReconcileAsync();
        var remoteEmpty = await files.FindByNameAsync(space.id, null, emptyDirName);
        Check("⑫ 本地新建的空目录已在远端创建", remoteEmpty is not null && remoteEmpty.is_dir,
            remoteEmpty is null ? "远端没有该目录" : $"is_dir={remoteEmpty.is_dir}");
        // **数据安全断言**(真机抓到过缺陷:本轮新建的远端目录又被本轮的"远端已无"判定
        // 当成删除,把本地目录连文件一起删了 —— 只查远端的话这个缺陷**看起来完全正常**)。
        Check("⑫ 建目录不会反过来删掉本地目录(数据安全)",
            Directory.Exists(Path.Combine(root, emptyDirName)),
            "本地目录不见了 = 本轮把刚建的目录当成远端缺失删掉了");

        // ② 带文件的目录:本地建 + 删 → 远端**整棵子树**同步删
        var dirName = $"{dirPrefix}-withfiles";
        var dirPath = Path.Combine(root, dirName);
        Directory.CreateDirectory(dirPath);
        await File.WriteAllTextAsync(Path.Combine(dirPath, "a.txt"), "a");
        await File.WriteAllTextAsync(Path.Combine(dirPath, "b.txt"), "b");
        await host.ReconcileAsync();
        var remoteDir = await files.FindByNameAsync(space.id, null, dirName);
        Check("⑫ 带文件的本地目录已在远端创建", remoteDir is not null && remoteDir.is_dir);
        Check("⑫ 目录里的两个文件在本地都还在(数据安全)",
            File.Exists(Path.Combine(dirPath, "a.txt")) && File.Exists(Path.Combine(dirPath, "b.txt")));
        var remoteChildren = remoteDir is null ? 0 : (await files.ListAsync(space.id, remoteDir.id)).Count;
        Check("⑫ 目录里的两个文件都上传了", remoteChildren == 2, $"远端子项={remoteChildren}");
        if (remoteChildren != 2)
        {
            await DiagAsync("withfiles");
        }

        // ②b 多级目录:**一次对账**就要把中间层也建出来。
        // 这条专门盯住"本轮新建的目录不参与本轮删除判定"这个修复:
        // `p/q/r` 里的 p、p/q 是 `EnsureParentDirAsync` 顺带建的(不是循环里显式建的那个),
        // 只把显式建的目录写进快照挡不住它们被当成"远端已无"而删掉本地目录。
        var deepRel = $"{dirPrefix}-deep/p/q/r";
        var deepFull = Path.Combine(root, deepRel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(deepFull);
        await File.WriteAllTextAsync(Path.Combine(deepFull, "deep.txt"), "deep");
        await host.ReconcileAsync();
        var deepParts = deepRel.Split('/');
        var deepLocalOk = true;
        var acc = "";
        foreach (var part in deepParts)
        {
            acc = acc.Length == 0 ? part : acc + "/" + part;
            deepLocalOk &= Directory.Exists(Path.Combine(root, acc.Replace('/', Path.DirectorySeparatorChar)));
        }
        Check("⑫ 多级目录一次对账建成后,本地各层都还在(数据安全)", deepLocalOk);
        // 远端逐层核对:必须**从同步根开始一层层走**(少走一层就会误判成"没建出来")
        EntryView? deepCursor = null;
        var deepRemoteOk = true;
        string? deepMissing = null;
        for (var i = 0; i < deepParts.Length; i++)
        {
            deepCursor = await files.FindByNameAsync(space.id, deepCursor?.id, deepParts[i]);
            if (deepCursor is not { is_dir: true })
            {
                deepRemoteOk = false;
                deepMissing = string.Join('/', deepParts.Take(i + 1));
                break;
            }
        }
        Check("⑫ 多级目录的中间层也建到了远端(p/q/r 四层都在)", deepRemoteOk,
            deepRemoteOk ? "" : $"远端缺:{deepMissing}");
        var deepKids = deepCursor is null ? new List<EntryView>() : (await files.ListAsync(space.id, deepCursor.id)).ToList();
        Check("⑫ 多级目录里的文件也上传了", deepKids.Count == 1,
            $"{deepKids.Count} 项:[{string.Join(",", deepKids.Select(k => k.name))}] r.id={deepCursor?.id}");
        if (deepKids.Count != 1)
        {
            await DiagAsync("deep");
        }

        Directory.Delete(dirPath, recursive: true);
        await host.ReconcileAsync();
        Check("⑫ 本地删目录 → 远端整棵子树同步删除",
            await files.FindByNameAsync(space.id, null, dirName) is null,
            "远端目录仍在 = 目录删除没传播");

        // ③ 远端删目录 → 本地目录同步删(连同子目录)
        var remoteDelName = $"{dirPrefix}-remotedel";
        var remoteDelPath = Path.Combine(root, remoteDelName);
        Directory.CreateDirectory(Path.Combine(remoteDelPath, "sub"));
        await File.WriteAllTextAsync(Path.Combine(remoteDelPath, "sub", "c.txt"), "c");
        await host.ReconcileAsync();
        var remoteDel = await files.FindByNameAsync(space.id, null, remoteDelName);
        Check("⑫ 远端删除的前提:目录已上传", remoteDel is not null);
        if (remoteDel is not null)
        {
            using var del = await api.SendRawAsync(HttpMethod.Delete,
                $"/api/v1/files/{Uri.EscapeDataString(remoteDel.id)}",
                contentFactory: null, headers: null, idempotent: true);
            Console.WriteLine($"    远端删除目录 {remoteDelName} → {(int)del.StatusCode}");
            await host.ReconcileAsync();
            Check("⑫ 远端删目录 → 本地目录(含子目录)同步删除",
                !Directory.Exists(remoteDelPath),
                Directory.Exists(remoteDelPath) ? "本地目录仍在 = 远端删除没传播" : "");
        }

        // ④ **目录改名与移动**(2026-09-13):目录没有"内容"可传,只有"位置"要改 ——
        //    必须走服务端 MOVE(同一个 file_id),而不是"建新目录 + 删旧子树 + 重传子文件"。
        //    判据是目录自己的 FileId(同卷内改名/移动不变),与文件改名同源。
        //    这一节此前只断言"不误删"(当时不支持改名);现在期望升级为"真的改过去了"。
        var renameFrom = $"{dirPrefix}-renamefrom";
        var renameTo = $"{dirPrefix}-renameto";
        var renameFromPath = Path.Combine(root, renameFrom);
        Directory.CreateDirectory(renameFromPath);
        await File.WriteAllTextAsync(Path.Combine(renameFromPath, "keep.txt"), "must-survive");
        await host.ReconcileAsync();
        var beforeRename = await files.FindByNameAsync(space.id, null, renameFrom);
        var beforeFile = beforeRename is null ? null : await files.FindByNameAsync(space.id, beforeRename.id, "keep.txt");
        Check("⑫ 目录改名的前提:目录与文件都已上传", beforeRename is { is_dir: true } && beforeFile is not null);

        Directory.Move(renameFromPath, Path.Combine(root, renameTo));
        await host.ReconcileAsync();
        var afterRename = await files.FindByNameAsync(space.id, null, renameTo);
        Check("⑫ 目录改名后在远端以新名字出现", afterRename is { is_dir: true },
            afterRename is null ? "远端没有新名字的目录" : "");
        Check("⑫ 目录改名复用同一个远端 file id(不是新建)",
            afterRename is not null && beforeRename is not null && afterRename.id == beforeRename.id,
            afterRename is null || beforeRename is null ? "缺条目" : $"旧={beforeRename.id} 新={afterRename.id}");
        Check("⑫ 目录改名后版本递增(服务端原地改,不是新建)",
            afterRename is not null && beforeRename is not null && afterRename.version > beforeRename.version,
            afterRename is null || beforeRename is null ? "缺条目" : $"旧={beforeRename.version} 新={afterRename.version}");
        Check("⑫ 目录改名后旧名字在远端消失(没有残留重复)",
            await files.FindByNameAsync(space.id, null, renameFrom) is null);
        var afterFile = afterRename is null ? null : await files.FindByNameAsync(space.id, afterRename.id, "keep.txt");
        Check("⑫ 子文件跟着目录走:同一个 file id",
            afterFile is not null && beforeFile is not null && afterFile.id == beforeFile.id,
            afterFile is null || beforeFile is null ? "缺条目" : $"旧={beforeFile.id} 新={afterFile.id}");
        // 版本**恰好 +1**:服务端移动子树时会把后代的 version 各 +1(6.11:后代的完整路径变了,
        // 而 ETag 是 version 的生成列,不 +1 的话客户端会继续用旧路径引用它)。所以"没有重传"
        // 不能断言成"版本不变"—— 那会与契约本身矛盾;能区分的是**只有这一跳**:
        // 若客户端还额外做了一次上传或逐个改名,版本会变成 +2。
        Check("⑫ 子文件只跟着 MOVE 走了这一跳(版本恰好 +1,没有额外上传/改名)",
            afterFile is not null && beforeFile is not null && afterFile.version == beforeFile.version + 1,
            afterFile is null || beforeFile is null ? "缺条目" : $"旧={beforeFile.version} 新={afterFile.version}");
        Check("⑫ 子文件没有走「逐个改名」的路径(整棵子树跟着目录一起 MOVE)",
            !host.Status.Any(s => s.RelativePath.StartsWith(renameTo + "/", StringComparison.OrdinalIgnoreCase)
                                  && s.Message.Contains("已改名(原", StringComparison.Ordinal)));
        Check("⑫ 目录改名后本地文件仍在(数据安全)",
            File.Exists(Path.Combine(root, renameTo, "keep.txt")));

        // ④b **移动进另一个目录**(换父目录,不是纯改名):走同一个 MOVE 端点,parent 变了
        var moveHost = $"{dirPrefix}-movehost";
        var moveHostPath = Path.Combine(root, moveHost);
        Directory.CreateDirectory(moveHostPath);
        await host.ReconcileAsync();
        var remoteMoveHost = await files.FindByNameAsync(space.id, null, moveHost);
        Directory.Move(Path.Combine(root, renameTo), Path.Combine(moveHostPath, renameTo));
        await host.ReconcileAsync();
        var movedDir = remoteMoveHost is null ? null : await files.FindByNameAsync(space.id, remoteMoveHost.id, renameTo);
        Check("⑫ 目录移动后出现在新父目录下(远端父子关系已改)", movedDir is { is_dir: true },
            movedDir is null ? "新父目录下没有它" : "");
        Check("⑫ 移动复用同一个远端 file id",
            movedDir is not null && beforeRename is not null && movedDir.id == beforeRename.id,
            movedDir is null || beforeRename is null ? "缺条目" : $"旧={beforeRename.id} 新={movedDir.id}");
        Check("⑫ 移动后顶层已无该目录(不是复制了一份)",
            await files.FindByNameAsync(space.id, null, renameTo) is null);
        Check("⑫ 移动后子文件仍在(内容安全)",
            movedDir is not null && (await files.ListAsync(space.id, movedDir.id)).Count == 1,
            movedDir is null ? "缺条目" : "");

        // 失败时把每文件状态表打出来(NETDISK_E2E_VERBOSE=1):失败往往是"某个文件停在
        // Failed/待处理"造成的,而状态本身不会出现在通知里 —— 没有这张表就只能靠猜。
        if (failures > 0 && Environment.GetEnvironmentVariable("NETDISK_E2E_VERBOSE") == "1")
        {
            Console.WriteLine("     [诊断] 同步状态表:");
            foreach (var s in host.Status)
            {
                Console.WriteLine($"       {s.RelativePath} → {s.State} | {s.Message} | v={s.Version} p={s.ProgressPercent}");
            }
        }
    }

    // ------------------------------------------------ ⑬ 只读浏览(仅结构)模式
    // 语义三条(少一条就不是"只读浏览"):①远端结构照搬、内容不落盘;②本地文件不上传;③两端都不删。
    // 这个模式的危险点很具体:它**看起来像**普通同步,一旦少了"不上传/不删"的闸,
    // 用户以为只是在浏览,实际却在改远端。
    Console.WriteLine();
    Console.WriteLine("— ⑬ 只读浏览(仅结构)模式");
    {
        var prefix = $"sync-check-{DateTime.Now:HHmmss}";
        // ① 先用**正常模式**在远端造出"一个目录 + 一个文件"(作为只读浏览的观察对象)
        var normalRoot = Path.Combine(work, "root-normal");
        Directory.CreateDirectory(normalRoot);
        var normalCfgPath = Path.Combine(work, "client-normal.json");
        var normalCfg = ClientConfig.Load(normalCfgPath);
        normalCfg.BaseUrl = baseUrl;
        normalCfg.SyncRoot = normalRoot;
        normalCfg.SpaceId = space.id;
        normalCfg.Onboarded = true;
        normalCfg.Save();
        var remoteDirName = $"{prefix}-dir";
        var remoteFileName = $"{prefix}-remote.txt";
        await using (var normal = SyncRuntime.FromStoredToken(ClientConfig.Load(normalCfgPath), tokenPath: tokenPath))
        {
            await normal.StartAsync();
            Directory.CreateDirectory(Path.Combine(normalRoot, remoteDirName));
            await File.WriteAllTextAsync(Path.Combine(normalRoot, remoteDirName, remoteFileName), "remote-content");
            await normal.Host.ReconcileAsync();
            // 不在 await using 作用域里再显式释放一次(那正是"二次释放"的写法;
            // 引擎侧已按幂等修好,这里也没必要制造第二次调用)
        }
        var soRemoteDir = await files.FindByNameAsync(space.id, null, remoteDirName);
        var soRemoteFile = soRemoteDir is null ? null : await files.FindByNameAsync(space.id, soRemoteDir.id, remoteFileName);
        Check("⑬ 前提:远端目录与文件都已就绪",
            soRemoteDir is { is_dir: true } && soRemoteFile is not null,
            soRemoteDir is null ? "远端没有该目录" : $"文件={(soRemoteFile is null ? "缺" : "有")}");

        // ② 切到只读浏览:**干净的本地根**(否则"没下载"会被上一次的残留文件掩盖)
        var soRoot = Path.Combine(work, "root-structure");
        Directory.CreateDirectory(soRoot);
        var soCfgPath = Path.Combine(work, "client-structure.json");
        var soCfg = ClientConfig.Load(soCfgPath);
        soCfg.BaseUrl = baseUrl;
        soCfg.SyncRoot = soRoot;
        soCfg.SpaceId = space.id;
        soCfg.Onboarded = true;
        soCfg.StructureOnly = true;
        soCfg.Save();

        await using var so = new SyncHost(ClientConfig.Load(soCfgPath), BuildTokenSession(), api,
            statePath: Path.Combine(work, "state-structure.db"), watchLocal: false);
        await so.StartAsync();
        await so.ReconcileAsync();

        Check("⑬ 只读浏览:远端**目录结构**照常建到本地",
            Directory.Exists(Path.Combine(soRoot, remoteDirName)));
        Check("⑬ 只读浏览:远端文件**不落盘**(内容不下载)",
            !File.Exists(Path.Combine(soRoot, remoteDirName, remoteFileName)));
        Check("⑬ 只读浏览:文件在状态列表里如实出现(标为「仅结构」而不是「下载中」)",
            so.Status.Any(s => s.State == SyncState.StructureOnly
                               && string.Equals(s.RelativePath, $"{remoteDirName}/{remoteFileName}", StringComparison.OrdinalIgnoreCase)),
            string.Join(",", so.Status.Select(s => $"{s.RelativePath}:{s.State}")));
        Check("⑬ 只读浏览:远端文件仍在(没有因为「本地没有」而被当成删除)",
            soRemoteDir is not null && await files.FindByNameAsync(space.id, soRemoteDir.id, remoteFileName) is not null);

        // ③ 本地新增 → 不上传;本地删目录 → 不删远端
        var localOnly = $"{prefix}-local-only.txt";
        await File.WriteAllTextAsync(Path.Combine(soRoot, localOnly), "local");
        await so.ReconcileAsync();
        Check("⑬ 只读浏览:本地文件**不上传**", await files.FindByNameAsync(space.id, null, localOnly) is null);
        Check("⑬ 只读浏览:本地多出来的文件显示为「未上传」",
            so.Status.Any(s => string.Equals(s.RelativePath, localOnly, StringComparison.OrdinalIgnoreCase)
                               && s.Message.Contains("未上传", StringComparison.Ordinal)));

        Directory.Delete(Path.Combine(soRoot, remoteDirName), recursive: true);
        await so.ReconcileAsync();
        Check("⑬ 只读浏览:本地删目录**不传播**到远端(只读语义)",
            await files.FindByNameAsync(space.id, null, remoteDirName) is not null);
        Check("⑬ 只读浏览:没有条目被标成「远端已删除」",
            !so.Status.Any(s => s.State == SyncState.PendingRemoteGone),
            string.Join(",", so.Status.Select(s => $"{s.RelativePath}:{s.State}")));
    }

    // ------------------------------------------------ ⑭ 冲突解决(逐文件三选一 + 无人值守策略)
    // 默认做法是"两份都保留"(不丢数据),但用户需要两条出路:设置里预先定好策略,
    // 或者看到「冲突」后逐个挑一次。两者**不能合并**:一个是偏好,一个是一次决定。
    Console.WriteLine();
    Console.WriteLine("— ⑭ 冲突解决:以本地为准 / 以远端为准 / 都保留 + 无人值守策略");
    {
        var prefix = $"sync-check-{DateTime.Now:HHmmss}-resolve";

        // 造一次**真实冲突**:本地改 + 远端原地覆盖(与 ④ 同一套做法)
        async Task<(string Rel, string Copy)> MakeConflictAsync(SyncHost h, string rel, string localText, string remoteText)
        {
            await File.WriteAllTextAsync(Path.Combine(root, rel), localText);
            await h.ReconcileAsync();                                   // 先传上去(v1)
            await File.WriteAllTextAsync(Path.Combine(root, rel), localText + "-edited"); // 本地又改
            var staging = Path.Combine(work, "remote-" + Guid.NewGuid().ToString("N")[..6] + ".txt");
            await File.WriteAllTextAsync(staging, remoteText);
            await files.UploadAsync(space.id, null, rel, staging, allowOverwrite: true);  // 另一端也改
            await h.ReconcileAsync();                                   // → 冲突
            var copies = Directory.GetFiles(root, Path.GetFileNameWithoutExtension(rel) + "_conflict_*");
            return (rel, copies.FirstOrDefault() ?? "");
        }

        async Task<string> ServerTextAsync(string rel)
        {
            var e = await files.FindByNameAsync(space.id, null, rel);
            if (e is null)
            {
                return "";
            }
            var tmp = Path.Combine(work, "dl-" + Guid.NewGuid().ToString("N")[..6] + ".txt");
            await files.DownloadAsync(e.id, tmp);
            return await File.ReadAllTextAsync(tmp);
        }

        var resolveCfgPath = Path.Combine(work, "client-resolve.json");
        var resolveCfg = ClientConfig.Load(resolveCfgPath);
        resolveCfg.BaseUrl = baseUrl;
        resolveCfg.SyncRoot = root;
        resolveCfg.SpaceId = space.id;
        resolveCfg.Onboarded = true;
        resolveCfg.Save();
        await using var rh = new SyncHost(ClientConfig.Load(resolveCfgPath), BuildTokenSession(), api,
            statePath: Path.Combine(work, "state-resolve.db"), watchLocal: false);
        await rh.StartAsync();

        // ① 以本地为准:把本地那一版原地覆盖到远端(同一 file_id),副本合并删除
        var (rel1, copy1) = await MakeConflictAsync(rh, $"{prefix}-local.txt", "local-A", "remote-A");
        Check("⑭ 前提:产生了冲突且本地那一版被保留成副本", copy1.Length > 0,
            copy1.Length > 0 ? Path.GetFileName(copy1) : "没有副本");
        Check("⑭ 前提:该路径被引擎识别为「可解决」", rh.CanResolveConflict(rel1));
        var idBeforeLocal = (await files.FindByNameAsync(space.id, null, rel1))?.id;
        Check("⑭ 「以本地为准」返回成功", await rh.ResolveConflictAsync(rel1, ConflictResolution.KeepLocal));
        var afterLocal = await files.FindByNameAsync(space.id, null, rel1);
        Check("⑭ 「以本地为准」后远端仍是同一个 file id(原地覆盖,不是新建)",
            afterLocal is not null && afterLocal.id == idBeforeLocal,
            $"before={idBeforeLocal} after={afterLocal?.id}");
        var serverText1 = await ServerTextAsync(rel1);
        Check("⑭ 「以本地为准」后远端内容 = 本地那一版",
            serverText1.StartsWith("local-A-edited", StringComparison.Ordinal), serverText1);
        Check("⑭ 「以本地为准」后本地副本已合并删除", !File.Exists(copy1));
        Check("⑭ 「以本地为准」后不再是冲突态",
            rh.Status.FirstOrDefault(s => s.RelativePath == rel1)?.State != SyncState.Conflict);

        // ② 以远端为准:远端那一版留在原路径,本地改动(副本)被删除 —— 用户明确选的"放弃本地"
        var (rel2, copy2) = await MakeConflictAsync(rh, $"{prefix}-remote.txt", "local-B", "remote-B");
        Check("⑭ 前提(远端为准):产生了冲突且本地那一版被保留成副本", copy2.Length > 0);
        Check("⑭ 「以远端为准」返回成功", await rh.ResolveConflictAsync(rel2, ConflictResolution.KeepRemote));
        var localText2 = await File.ReadAllTextAsync(Path.Combine(root, rel2));
        Check("⑭ 「以远端为准」后本地文件 = 远端那一版", localText2 == "remote-B", localText2);
        Check("⑭ 「以远端为准」后本地副本已删除(本地改动被放弃)", !File.Exists(copy2));

        // ③ 都保留:副本留着并作为**新文件**上传(两份都在)
        var (rel3, copy3) = await MakeConflictAsync(rh, $"{prefix}-both.txt", "local-C", "remote-C");
        Check("⑭ 前提(都保留):产生了冲突且本地那一版被保留成副本", copy3.Length > 0);
        Check("⑭ 「都保留」返回成功", await rh.ResolveConflictAsync(rel3, ConflictResolution.KeepBoth));
        Check("⑭ 「都保留」后本地副本仍在(数据不丢)", File.Exists(copy3));
        Check("⑭ 「都保留」后副本被当成新文件传到了远端",
            await files.FindByNameAsync(space.id, null, Path.GetFileName(copy3)) is not null,
            Path.GetFileName(copy3));

        // ④ 无人值守策略:配置里选 keep_local → 冲突发生时**不生成副本**,直接以本地覆盖远端
        var policyCfgPath = Path.Combine(work, "client-policy.json");
        var policyCfg = ClientConfig.Load(policyCfgPath);
        policyCfg.BaseUrl = baseUrl;
        policyCfg.SyncRoot = root;
        policyCfg.SpaceId = space.id;
        policyCfg.Onboarded = true;
        policyCfg.OnConflict = "keep_local";
        policyCfg.Save();
        await using var ph = new SyncHost(ClientConfig.Load(policyCfgPath), BuildTokenSession(), api,
            statePath: Path.Combine(work, "state-policy.db"), watchLocal: false);
        await ph.StartAsync();
        var rel4 = $"{prefix}-policy.txt";
        var (_, copy4) = await MakeConflictAsync(ph, rel4, "local-D", "remote-D");
        Check("⑭ 策略 keep_local:冲突时**不**生成副本(用户已预先决定)", copy4.Length == 0,
            copy4.Length > 0 ? Path.GetFileName(copy4) : "");
        var serverText4 = await ServerTextAsync(rel4);
        Check("⑭ 策略 keep_local:远端被本地那一版覆盖",
            serverText4.StartsWith("local-D-edited", StringComparison.Ordinal), serverText4);

        // ⑤ 解决不了的情形要**如实说**(不能显示成"已解决")
        Check("⑭ 没有任何副本记录的路径:CanResolveConflict = false",
            !rh.CanResolveConflict($"{prefix}-never-conflicted.txt"));
    }

    // ------------------------------------------------ ⑮ 远端文件浏览器(只读)
    // "看一眼服务器上有什么"绝不该改变任何一端的文件;而两个不同目录里的**同名**文件
    // 预览落点若相同,"打开"就会打开另一个文件(界面上表现为"点开是旧内容",极难查)。
    Console.WriteLine();
    Console.WriteLine("— ⑮ 远端文件浏览器(只读浏览整个空间)");
    {
        var prefix = $"sync-check-{DateTime.Now:HHmmss}-browse";
        var dirA = $"{prefix}-A";
        var dirB = $"{prefix}-B";
        // 用 API 直接造树:A/同名.txt、B/同名.txt(同名是刻意的,见上)
        var rootA = await files.CreateDirectoryAsync(space.id, null, dirA);
        var rootB = await files.CreateDirectoryAsync(space.id, null, dirB);
        var stagingA = Path.Combine(work, "browse-a.txt");
        var stagingB = Path.Combine(work, "browse-b.txt");
        await File.WriteAllTextAsync(stagingA, "content-from-A");
        await File.WriteAllTextAsync(stagingB, "content-from-B");
        await files.UploadAsync(space.id, rootA.id, "同名.txt", stagingA);
        await files.UploadAsync(space.id, rootB.id, "同名.txt", stagingB);

        var browser = new RemoteBrowser(files, space.id);
        var rootEntries = await browser.ListAsync(null, "");
        Check("⑮ 能列出空间根(看到刚建的两个目录)",
            rootEntries.Any(e => e.Name == dirA) && rootEntries.Any(e => e.Name == dirB));
        Check("⑮ 目录排在文件前面(与资源管理器一致)",
            rootEntries.TakeWhile(e => e.IsDir).All(e => e.IsDir));

        var navA = await browser.FindByPathAsync(new[] { dirA, "同名.txt" });
        Check("⑮ 按路径逐级定位到文件", navA is { IsDir: false },
            navA is null ? "没找到" : navA.Path);
        Check("⑮ 条目自带相对路径(界面不再自己拼路径)",
            navA is not null && navA.Path == $"{dirA}/同名.txt", navA?.Path ?? "");
        var levelA = await browser.ListAsync(rootA.id, dirA);
        Check("⑮ 能列出某一层(目录下的文件名正确)",
            levelA.Count == 1 && levelA[0].Name == "同名.txt" && levelA[0].SizeText.Length > 0,
            string.Join(",", levelA.Select(e => e.Name)));

        // 预览下载:落点在临时目录、且不污染同步目录
        var previewA = await browser.DownloadToTempAsync(navA!);
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        Check("⑮ 预览落点在**临时目录**(不在同步目录里)",
            Path.GetFullPath(previewA).StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
            && !Path.GetFullPath(previewA).StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase),
            previewA);
        Check("⑮ 浏览不改本地同步副本(同步目录里没有多出文件)",
            !File.Exists(Path.Combine(root, "同名.txt")) &&
            Directory.GetFiles(root, "同名.txt", SearchOption.AllDirectories).Length == 0);
        Check("⑮ 预览内容 = 远端内容", await File.ReadAllTextAsync(previewA) == "content-from-A");

        var navB = await browser.FindByPathAsync(new[] { dirB, "同名.txt" });
        var previewB = await browser.DownloadToTempAsync(navB!);
        Check("⑮ 不同目录的**同名**文件预览落点不冲突", previewA != previewB,
            $"{Path.GetFileName(previewA)} vs {Path.GetFileName(previewB)}");
        Check("⑮ 两份预览各自是正确的那一份(没有互相覆盖)",
            await File.ReadAllTextAsync(previewA) == "content-from-A"
            && await File.ReadAllTextAsync(previewB) == "content-from-B");

        // 只读:浏览/预览之后,远端条目的 id 与版本都不该变
        var afterBrowse = await files.FindByNameAsync(space.id, rootA.id, "同名.txt");
        Check("⑮ 浏览是**只读**的(远端条目 id 与版本都没变)",
            afterBrowse is not null && afterBrowse.id == navA!.Id && afterBrowse.version == navA.Version,
            $"id={afterBrowse?.id} v={afterBrowse?.version}");

        Check("⑮ 路径不存在时返回 null(而不是抛异常)",
            await browser.FindByPathAsync(new[] { $"{prefix}-does-not-exist", "同名.txt" }) is null);
    }

    // ------------------------------------------------ ⑯ 多空间:一个账号、两个空间、两棵本地树
    // 这是清单里的最后一项功能缺口。核心不是"能配两个空间",而是**互不串**:
    // 每个空间一棵本地树、一个状态库、一个宿主;一个空间的增删改不该出现在另一个空间里。
    Console.WriteLine();
    Console.WriteLine("— ⑯ 多空间(个人空间 + 团队空间,两棵本地树)");
    {
        var prefix = $"sync-check-{DateTime.Now:HHmmss}-multi";
        // ① 造一个真实的**团队空间**(契约 POST /api/v1/spaces;admin 账号有权限)
        var collab = new SpaceCollabClient(api);
        var team = await collab.CreateAsync($"{prefix}-team");
        Check("⑯ 前提:团队空间已创建", !string.IsNullOrWhiteSpace(team.id), team.id);

        // ② 两条绑定:个人空间 → root-multi-a;团队空间 → root-multi-b
        var rootA = Path.Combine(work, "root-multi-a");
        var rootB = Path.Combine(work, "root-multi-b");
        Directory.CreateDirectory(rootA);
        Directory.CreateDirectory(rootB);
        // 多空间用**独立的数据目录**:状态库里记着"同步根身份",而前面的场景用的是另一个根 ——
        // 共用一个 state.db 会被身份保护正确拦下("同步根身份变了 → 本轮不传播删除"),
        // 那是保护在正常工作,却会让这个场景测不出它想测的东西。
        var multiDir = Path.Combine(work, "multi");
        Directory.CreateDirectory(multiDir);
        var multiCfgPath = Path.Combine(multiDir, "client.json");
        var multiCfg = ClientConfig.Load(multiCfgPath);
        multiCfg.BaseUrl = baseUrl;
        multiCfg.SpaceId = space.id;                 // 主空间 = 个人空间(沿用旧字段)
        multiCfg.SyncRoot = rootA;
        multiCfg.Onboarded = true;
        multiCfg.Spaces = new List<SpaceBinding>
        {
            new() { SpaceId = space.id, SyncRoot = rootA },
            new() { SpaceId = team.id, SyncRoot = rootB },
        };
        multiCfg.Save();
        Check("⑯ 生效绑定数 = 2", ClientConfig.Load(multiCfgPath).EffectiveBindings.Count == 2,
            $"实际 {ClientConfig.Load(multiCfgPath).EffectiveBindings.Count}");

        await using var multi = SyncRuntime.FromStoredToken(ClientConfig.Load(multiCfgPath), tokenPath: tokenPath);
        if (Environment.GetEnvironmentVariable("NETDISK_E2E_VERBOSE") == "1")
        {
            multi.Notice += m => Console.WriteLine($"     · {m}");
        }
        Check("⑯ 两棵本地树各自起了宿主(主 + 次)", multi.AllHosts.Count == 2, $"宿主数={multi.AllHosts.Count}");
        Check("⑯ 启动成功", await multi.StartAsync());

        // ③ 每个空间各放一个同名文件:内容必须各归各的空间
        var shared = $"{prefix}-same-name.txt";
        await File.WriteAllTextAsync(Path.Combine(rootA, shared), "belongs-to-personal");
        await File.WriteAllTextAsync(Path.Combine(rootB, shared), "belongs-to-team");
        await multi.ReconcileAsync();

        var inPersonal = await files.FindByNameAsync(space.id, null, shared);
        var inTeam = await files.FindByNameAsync(team.id, null, shared);
        Check("⑯ 个人空间的文件出现在个人空间", inPersonal is not null);
        Check("⑯ 团队空间的文件出现在团队空间", inTeam is not null);
        Check("⑯ 两个空间里的同名文件是**两条不同记录**(没有互相覆盖)",
            inPersonal is not null && inTeam is not null && inPersonal.id != inTeam.id,
            inTeam is null || inPersonal is null ? "缺条目" : $"personal={inPersonal.id[..8]} team={inTeam.id[..8]}");

        // 内容也要各归各:把两边的远端内容取回来比对(证明"上传没有串")
        var stagingDir = Path.Combine(work, "multi-dl");
        Directory.CreateDirectory(stagingDir);
        if (inPersonal is not null && inTeam is not null)
        {
            var pa = Path.Combine(stagingDir, "p.txt");
            var pb = Path.Combine(stagingDir, "t.txt");
            await files.DownloadAsync(inPersonal.id, pa);
            await files.DownloadAsync(inTeam.id, pb);
            Check("⑯ 个人空间的文件内容 = 它自己那份",
                (await File.ReadAllTextAsync(pa)) == "belongs-to-personal", await File.ReadAllTextAsync(pa));
            Check("⑯ 团队空间的文件内容 = 它自己那份",
                (await File.ReadAllTextAsync(pb)) == "belongs-to-team", await File.ReadAllTextAsync(pb));
        }

        // ④ 只放个人空间独有的文件 → 绝不该出现在团队空间
        var onlyPersonal = $"{prefix}-only-personal.txt";
        await File.WriteAllTextAsync(Path.Combine(rootA, onlyPersonal), "personal-only");
        await multi.ReconcileAsync();
        Check("⑯ 只在个人空间存在的文件**不会**被传进团队空间",
            await files.FindByNameAsync(team.id, null, onlyPersonal) is null,
            "团队空间里出现了不该出现的文件 = 空间串了");

        // ⑤ 状态库必须按空间分开(混库正是"串数据"的温床)
        var stateMain = Path.Combine(Path.GetDirectoryName(multiCfgPath)!, "state.db");
        var stateSecond = Path.Combine(Path.GetDirectoryName(multiCfgPath)!, $"state-{team.id[..8]}.db");
        Check("⑯ 两个空间各自的**状态库文件**都存在", File.Exists(stateMain) && File.Exists(stateSecond),
            $"main={File.Exists(stateMain)} second={File.Exists(stateSecond)}");

        // ⑥ 本地删除只该影响它自己那个空间
        File.Delete(Path.Combine(rootA, shared));
        await multi.ReconcileAsync();
        Check("⑯ 删个人空间的文件 → 个人空间删掉了",
            await files.FindByNameAsync(space.id, null, shared) is null);
        Check("⑯ 删个人空间的文件 → **团队空间不受影响**",
            await files.FindByNameAsync(team.id, null, shared) is not null);

        // 收尾:团队空间里留下的测试文件清掉(空间本身由 admin 账号保留,不在这里解散)
        foreach (var e in await files.ListAsync(team.id, null))
        {
            if (e.name.StartsWith("sync-check-", StringComparison.Ordinal))
            {
                using var del = await api.SendRawAsync(HttpMethod.Delete,
                    $"/api/v1/files/{Uri.EscapeDataString(e.id)}",
                    contentFactory: null, headers: null, idempotent: true);
            }
        }
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
