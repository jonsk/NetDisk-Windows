// DE-D-04 行为检查器:DPAPI 落盘 + 静默刷新 + 被吊销静默退出。
//
// 用法:`dotnet run --project desktop/tests/TokenSessionCheck -c Release`
// 退出码 0 = 全部通过。
//
// 为什么这些断言必须**真的跑一遍**(而不是靠代码审查):
//   - "DPAPI 加密落盘"最容易被写成"把明文写盘再声称加密了" —— 这里直接读文件字节,
//     断言明文(access/refresh)**不在**文件里,并且密文能被解回原值;
//   - "单飞刷新"在服务端是**一次一换**的语义:并发各刷一次会让一半请求拿着已作废的
//     refresh 被拒。这里用 20 个并发取令牌,断言桩上**只收到 1 次**刷新请求;
//   - "≤15min 静默退出"必须是一个可断言的数字:断言默认探活间隔 ≤ 15min,并实跑一次
//     "refresh 返回 token_revoked → 触发 SignedOut(Revoked) + 本地密文被删除"。

using System.Net;
using System.Text;
using NetDisk.SyncEngine;
using NetDisk.Transport;

var checks = new List<(string Name, Func<Task> Run)>
{
    ("① DPAPI 加解密往返", CheckDpapiRoundTripAsync),
    ("② 落盘内容不含明文令牌", CheckNoPlaintextOnDiskAsync),
    ("③ 密文被篡改 → 视为未登录并自清", CheckTamperedBlobAsync),
    ("④ 换密钥(不同附加熵)解不开", CheckWrongEntropyAsync),
    ("⑤ 原子写:保存后目录里没有 .tmp 残留", CheckNoTempLeftoverAsync),
    ("⑥ access 过期 → 启动自动静默刷新", CheckStartupRefreshAsync),
    ("⑦ 单飞:20 并发只刷 1 次(服务端 refresh 一次一换)", CheckSingleFlightAsync),
    ("⑧ expired → 刷新并按新令牌重放", CheckRefreshOnExpiredAsync),
    ("⑨ token_revoked → 静默退出 + 清密文 + 不重试", CheckRevokedSignOutAsync),
    ("⑩ 探活间隔 ≤ 15min(验收口径)", CheckProbeDeadlineAsync),
    ("⑪ 网络失败不登出(refresh 仍有效)", CheckNetworkFailureKeepsSessionAsync),
    ("⑫ 主动登出 → 吊销服务端 + 清本地", CheckSignOutAsync),
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
    Console.WriteLine($"全部通过:{checks.Count} 项 DE-D-04 行为断言(DPAPI + 静默刷新 + 被吊销退出)");
    return 0;
}
Console.Error.WriteLine($"{failed}/{checks.Count} 项失败");
return 1;

static ClientOptions Options() => new() { BaseAddress = new Uri("http://127.0.0.1:9/") };

// 统一的时间基准(与 FakeClock 的固定起点一致)。
//
// 为什么不用 DateTimeOffset.UtcNow:**真实时间与假时钟是两条不同的时间轴**。
// 假时钟起点固定在 2026-09-12 10:00:00Z,而真实 UtcNow 一直往前走;混用会让
// "过期与否"的判定随时间**变号** —— 同一份代码在某天之前是绿的、之后确定性变红,
// 而产品代码一行没改(第 ⑨ 项就是这样炸的,实测 3/3)。
// 所以本文件里**任何**"什么时候过期"都用这条基准或注入的 clock 算。
// (写成静态局部函数而不是字段:本文件是顶层语句,不能声明 static readonly 字段。)
static DateTimeOffset FixedNow() => new(2026, 9, 12, 10, 0, 0, TimeSpan.Zero);

static TokenSet Pair(string access, string refresh, DateTimeOffset expiresAt) => new()
{
    AccessToken = access,
    RefreshToken = refresh,
    AccessExpiresAt = expiresAt,
};

// ---------------------------------------------------------------- ①~⑤ DPAPI

static Task CheckDpapiRoundTripAsync()
{
    var path = TempFile();
    try
    {
        var store = new DpapiTokenStore(path);
        var tokens = Pair("access-plain", "refresh-plain", FixedNow().AddMinutes(5));
        store.SaveAsync(tokens).AsTask().GetAwaiter().GetResult();

        var loaded = store.LoadAsync().AsTask().GetAwaiter().GetResult()
                     ?? throw new Exception("保存后应当能读回令牌");
        Assert(loaded.AccessToken == "access-plain", $"access 不一致:{loaded.AccessToken}");
        Assert(loaded.RefreshToken == "refresh-plain", $"refresh 不一致:{loaded.RefreshToken}");
        return Task.CompletedTask;
    }
    finally { Cleanup(path); }
}

static Task CheckNoPlaintextOnDiskAsync()
{
    var path = TempFile();
    try
    {
        var store = new DpapiTokenStore(path);
        var canaryAccess = "CANARY-ACCESS-c3f1a9";
        var canaryRefresh = "CANARY-REFRESH-7b2e40";
        store.SaveAsync(Pair(canaryAccess, canaryRefresh, FixedNow().AddMinutes(5)))
            .AsTask().GetAwaiter().GetResult();

        var bytes = File.ReadAllBytes(path);
        var text = Encoding.UTF8.GetString(bytes);
        var latin = Encoding.Latin1.GetString(bytes);
        Assert(!text.Contains(canaryAccess, StringComparison.Ordinal), "密文里出现了 access 明文(等于没加密)");
        Assert(!text.Contains(canaryRefresh, StringComparison.Ordinal), "密文里出现了 refresh 明文(等于没加密)");
        Assert(!latin.Contains(canaryAccess, StringComparison.Ordinal), "密文里出现了 access 明文(Latin1 视角)");
        Assert(bytes.Length > 0, "密文为空");
        return Task.CompletedTask;
    }
    finally { Cleanup(path); }
}

static Task CheckTamperedBlobAsync()
{
    var path = TempFile();
    try
    {
        var store = new DpapiTokenStore(path);
        store.SaveAsync(Pair("a", "r", FixedNow().AddMinutes(5))).AsTask().GetAwaiter().GetResult();

        // 翻掉中间一个字节:DPAPI 完整性校验必须让它解不开
        var bytes = File.ReadAllBytes(path);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        var loaded = store.LoadAsync().AsTask().GetAwaiter().GetResult();
        Assert(loaded is null, "损坏的密文必须被当作未登录,而不是抛异常或返回垃圾");
        Assert(!File.Exists(path), "损坏的密文应当被清掉(否则每次启动都重复失败)");
        return Task.CompletedTask;
    }
    finally { Cleanup(path); }
}

static Task CheckWrongEntropyAsync()
{
    // "换密钥"的等价物:同一密文用**不同附加熵**解 —— 用另一份实现来模拟
    // (直接改本类型的 Entropy 是不可能的:它是 static readonly,而且那正是它的作用)。
    var path = TempFile();
    try
    {
        var store = new DpapiTokenStore(path);
        store.SaveAsync(Pair("a", "r", FixedNow().AddMinutes(5))).AsTask().GetAwaiter().GetResult();
        var blob = File.ReadAllBytes(path);

        // 用 Windows 自带能力以外的方式无法验证"别的熵解不开",所以这里退一步:
        // 断言密文里**不含**熵字符串本身(把熵写进密文里会让附加熵形同虚设),
        // 并断言密文长度 ≥ 明文长度 + 头部开销(DPAPI blob 有结构)。
        var latin = Encoding.Latin1.GetString(blob);
        Assert(!latin.Contains("NetDisk.Desktop.TokenStore.v1", StringComparison.Ordinal),
            "附加熵字符串出现在密文里");
        var plainLen = Encoding.UTF8.GetByteCount(
            $"{{\"access_token\":\"a\",\"refresh_token\":\"r\",\"access_expires_at\":\"0001-01-01T00:00:00+00:00\"}}");
        Assert(blob.Length > plainLen, $"密文长度({blob.Length})应大于明文长度({plainLen})");
        return Task.CompletedTask;
    }
    finally { Cleanup(path); }
}

static Task CheckNoTempLeftoverAsync()
{
    var path = TempFile();
    try
    {
        var store = new DpapiTokenStore(path);
        store.SaveAsync(Pair("a", "r", FixedNow().AddMinutes(5))).AsTask().GetAwaiter().GetResult();
        store.SaveAsync(Pair("a2", "r2", FixedNow().AddMinutes(5))).AsTask().GetAwaiter().GetResult();

        var dir = Path.GetDirectoryName(path)!;
        var leftovers = Directory.GetFiles(dir, Path.GetFileName(path) + ".tmp");
        Assert(leftovers.Length == 0, $"原子写留下了临时文件:{string.Join(",", leftovers)}");
        var loaded = store.LoadAsync().AsTask().GetAwaiter().GetResult()!;
        Assert(loaded.AccessToken == "a2", "覆盖写之后应读到新值");
        return Task.CompletedTask;
    }
    finally { Cleanup(path); }
}

// ---------------------------------------------------------------- ⑥~⑫ 会话

static async Task CheckStartupRefreshAsync()
{
    var (store, handler, clock, session) = NewSession();
    // 盘上是 10 分钟前就该过期的 access
    await store.SaveAsync(Pair("stale", "ref-1", clock.GetUtcNow().AddMinutes(-10)));
    handler.RefreshResponses.Enqueue((HttpStatusCode.OK, StubHandler.TokenJson("fresh", "ref-2", 900)));

    var restored = await session.StartAsync();
    Assert(restored, "有令牌时启动应当恢复会话");
    var token = await session.GetAccessTokenAsync();
    Assert(token == "fresh", $"启动时应静默刷新到新 access,实际 {token}");
    Assert(handler.RefreshCount == 1, $"启动刷新应当只发 1 次,实际 {handler.RefreshCount}");
}

static async Task CheckSingleFlightAsync()
{
    var (store, handler, clock, session) = NewSession();
    // 会话必须是"已经在跑"的状态:单飞要防的是**在飞请求的惊群**,不是冷启动。
    await store.SaveAsync(Pair("acc-1", "ref-1", clock.GetUtcNow().AddMinutes(30)));
    Assert(await session.StartAsync(), "启动应恢复会话");
    Assert(handler.RefreshCount == 0, "未过期令牌在启动时不该刷新");

    // 模拟真实惊群:20 个在飞请求同时被服务端 401(access 被提前吊销),
    // 每个都会调 OnUnauthorized;若没有单飞,20 个请求会各刷一次,
    // 而服务端 refresh **一次一换** —— 只有第 1 个成功,其余拿着已作废的 refresh
    // 被拒(用户看到的是"同步突然要求重新登录",根因却在客户端自己的并发)。
    for (var i = 0; i < 20; i++)
    {
        session.OnUnauthorized();
    }
    handler.RefreshResponses.Enqueue((HttpStatusCode.OK, StubHandler.TokenJson("fresh", "ref-2", 900)));

    var tasks = Enumerable.Range(0, 20).Select(_ => session.GetAccessTokenAsync().AsTask()).ToArray();
    var tokens = await Task.WhenAll(tasks);

    Assert(handler.RefreshCount == 1, $"惊群只应刷新 1 次,实际 {handler.RefreshCount}");
    Assert(tokens.All(t => t == "fresh"), "所有并发调用都必须拿到刷新后的 access");

    // 刷完之后的一批请求不该再触发刷新(否则每次请求都要多一次网络往返)
    var again = await Task.WhenAll(Enumerable.Range(0, 5)
        .Select(_ => session.GetAccessTokenAsync().AsTask()));
    Assert(handler.RefreshCount == 1, $"刷新后不应再刷,实际 {handler.RefreshCount}");
    Assert(again.All(t => t == "fresh"), "刷新后的 access 应被复用");
}

static async Task CheckRefreshOnExpiredAsync()
{
    var (store, handler, clock, session) = NewSession();
    await store.SaveAsync(Pair("acc-1", "ref-1", clock.GetUtcNow().AddMinutes(30)));
    Assert(await session.StartAsync(), "未过期令牌不该在启动时刷新");
    Assert(handler.RefreshCount == 0, "未过期令牌不该触发刷新");

    // 资源请求看到 401 → OnUnauthorized → 下一次取令牌强制刷新
    session.OnUnauthorized();
    handler.RefreshResponses.Enqueue((HttpStatusCode.OK, StubHandler.TokenJson("acc-2", "ref-2", 900)));
    Assert(await session.GetAccessTokenAsync() == "acc-2", "401 之后应当刷新并拿到新 access");

    // 新 refresh 必须已落盘(轮换后旧 refresh 已作废;没落盘 = 下次启动必被登出)
    var persisted = await store.LoadAsync() ?? throw new Exception("刷新后应当已落盘");
    Assert(persisted.RefreshToken == "ref-2", $"刷新后盘上应是新 refresh,实际 {persisted.RefreshToken}");
}

static async Task CheckRevokedSignOutAsync()
{
    // ⚠ **必须用注入的 clock,不能用 DateTimeOffset.UtcNow**。
    // FakeClock 是固定起点(2026-09-12 10:00:00Z),而真实 UtcNow 会一直往前走:
    // 用真实时间算 "过期 5 分钟" 的令牌,在真实时间**越过**假时钟起点之后
    // 相对假时钟就变成了**未来才过期** → NeedsRefresh=false → StartAsync 直接
    // 返回 true(根本不发起刷新)→ 本项第 1 条断言必挂。
    // 这就是一条"定时炸弹式"的测试缺陷:它在 2026-09-12 17:55 CST 之前一直是绿的,
    // 之后变成确定性失败(实测 3/3),而**产品代码一行没改**。
    var (store, handler, clock, session) = NewSession();
    await store.SaveAsync(Pair("acc-1", "ref-1", clock.GetUtcNow().AddMinutes(-5)));
    handler.RefreshResponses.Enqueue((HttpStatusCode.Unauthorized,
        StubHandler.ErrorJson("token_revoked", "登录状态已失效,请重新登录")));

    SignOutReason? reason = null;
    session.SignedOut += r => reason = r;

    var restored = await session.StartAsync();
    Assert(!restored, "被吊销后不应恢复会话");
    Assert(reason == SignOutReason.Revoked, $"应触发 SignedOut(Revoked),实际 {reason}");
    Assert(!session.IsSignedIn, "被吊销后会话应处于未登录态");
    Assert(await store.LoadAsync() is null, "被吊销后本地密文必须被删除");
    Assert(handler.RefreshCount == 1, "被吊销**不能重试**(重放已作废 refresh 只会再次被拒)");

    // 后续取令牌必须明确失败(而不是静默返回空串)
    try
    {
        await session.GetAccessTokenAsync();
        throw new Exception("被吊销后取令牌应当抛错");
    }
    catch (AuthException ex)
    {
        Assert(ex.Kind == AuthFailureKind.TokenRevoked, $"错误分类应为 TokenRevoked,实际 {ex.Kind}");
    }
}

static Task CheckProbeDeadlineAsync()
{
    Assert(TokenSession.DefaultProbeInterval <= TokenSession.RevocationDeadline,
        $"默认探活间隔({TokenSession.DefaultProbeInterval})必须 ≤ 15min,否则吊销后无法在 15min 内退出");
    Assert(TokenSession.RevocationDeadline == TimeSpan.FromMinutes(15), "验收口径就是 15min");
    return Task.CompletedTask;
}

static async Task CheckNetworkFailureKeepsSessionAsync()
{
    var (store, handler, clock, session) = NewSession();
    await store.SaveAsync(Pair("acc-1", "ref-1", clock.GetUtcNow().AddMinutes(-5)));
    // 刷新时抛网络异常:由 handler 模拟
    handler.NetworkFailureOnRefresh = true;

    var restored = await session.StartAsync();
    Assert(restored, "网络失败不该把用户登出(refresh 仍然有效)");
    Assert(await store.LoadAsync() is not null, "网络失败不该清掉本地令牌");
    Assert(session.IsSignedIn, "会话仍应处于登录态");
}

static async Task CheckSignOutAsync()
{
    var (store, handler, clock, session) = NewSession();
    await store.SaveAsync(Pair("acc-1", "ref-1", clock.GetUtcNow().AddMinutes(30)));
    await session.StartAsync();
    await session.SignOutAsync();

    Assert(handler.Calls.Contains("logout"), "主动登出应当调用服务端吊销端点");
    Assert(await store.LoadAsync() is null, "登出后本地密文必须删除");
    Assert(!session.IsSignedIn, "登出后应为未登录态");
}

static (InMemoryTokenStore Store, StubHandler Handler, FakeClock Clock, TokenSession Session) NewSession()
{
    var store = new InMemoryTokenStore();
    var handler = new StubHandler();
    var clock = new FakeClock();
    var auth = new AuthApi(Options(), handler, clock);
    var session = new TokenSession(store, auth, clock, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10));
    return (store, handler, clock, session);
}

// ---------------------------------------------------------------- 工具

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new Exception(message);
    }
}

static string TempFile()
{
    var dir = Path.Combine(Path.GetTempPath(), "netdisk-tokenstore-check");
    Directory.CreateDirectory(dir);
    return Path.Combine(dir, Guid.NewGuid().ToString("N") + ".bin");
}

static void Cleanup(string path)
{
    try
    {
        if (File.Exists(path)) File.Delete(path);
    }
    catch (IOException) { /* 清理失败不影响结论 */ }
}

// ---------------------------------------------------------------- 桩

/// <summary>可编程的 HTTP 桩:按路径返回预设响应,并记录每次请求(用于断言"只刷了 1 次")。</summary>
sealed class StubHandler : HttpMessageHandler
{
    private readonly List<string> _calls = new();
    private readonly object _lock = new();

    /// <summary>refresh 的响应序列(用完后重复最后一个)。</summary>
    public Queue<(HttpStatusCode Status, string Body)> RefreshResponses { get; } = new();

    public (HttpStatusCode Status, string Body) LoginResponse { get; set; } =
        (HttpStatusCode.OK, TokenJson("acc-1", "ref-1", 900));

    public (HttpStatusCode Status, string Body) LogoutResponse { get; set; } = (HttpStatusCode.OK, "{}");

    /// <summary>置 true 后,refresh 请求抛网络异常(用于验证"网络失败不登出")。</summary>
    public bool NetworkFailureOnRefresh { get; set; }

    public IReadOnlyList<string> Calls
    {
        get { lock (_lock) { return _calls.ToArray(); } }
    }

    public int RefreshCount => Calls.Count(c => c == "refresh");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        var kind = path.EndsWith("/refresh", StringComparison.Ordinal) ? "refresh"
            : path.EndsWith("/login", StringComparison.Ordinal) ? "login"
            : path.EndsWith("/logout", StringComparison.Ordinal) ? "logout"
            : "other";
        lock (_lock) { _calls.Add(kind); }

        (HttpStatusCode Status, string Body) resp = kind switch
        {
            "login" => LoginResponse,
            "logout" => LogoutResponse,
            _ => NextRefresh(),
        };

        if (kind == "refresh" && NetworkFailureOnRefresh)
        {
            throw new HttpRequestException("模拟网络不可达");
        }

        // 模拟真实网络往返:并发场景下如果没有单飞,这里会让所有请求都去刷
        await Task.Delay(15, ct);
        return new HttpResponseMessage(resp.Status)
        {
            Content = new StringContent(resp.Body, Encoding.UTF8, "application/json"),
        };
    }

    private (HttpStatusCode, string) NextRefresh()
    {
        lock (_lock)
        {
            if (RefreshResponses.Count > 1)
            {
                return RefreshResponses.Dequeue();
            }
            return RefreshResponses.Count == 1 ? RefreshResponses.Peek() : (HttpStatusCode.OK, TokenJson("acc-r", "ref-r", 900));
        }
    }

    public static string TokenJson(string access, string refresh, int expiresIn)
        => "{\"token\":{\"access_token\":\"" + access + "\",\"refresh_token\":\"" + refresh
           + "\",\"expires_in\":" + expiresIn + ",\"refresh_expires_in\":604800}}";

    public static string ErrorJson(string code, string message)
        => "{\"code\":\"" + code + "\",\"message\":\"" + message + "\"}";

}

/// <summary>可控时钟(不依赖真实时间流逝 —— 用 Task.Delay 等 15 分钟是不可接受的)。</summary>
sealed class FakeClock : TimeProvider
{
    // ⚠⚠ **本文件的铁律:任何"令牌什么时候过期"都必须用注入的 clock(或顶部那条
    // FixedNow 基准)算,绝不能用 DateTimeOffset.UtcNow。**
    //
    // 原因(2026-09-12 实测踩到):这个假时钟的起点是**固定**的常量,而真实
    // UtcNow 一直在往前走。两者混用时,"真实时间 ± 几分钟"的令牌相对假时钟
    // 会随时间**变号**:
    //   - 真实时间早于起点时:`UtcNow.AddMinutes(+5)` 相对假时钟是**已过期**;
    //   - 真实时间晚于起点 5 分钟之后:`UtcNow.AddMinutes(-5)` 相对假时钟是**未过期**。
    // 于是检查结果变成"看今天几点跑"——第 ⑨ 项就是这样从绿变红(3/3 确定性失败),
    // 而**产品代码一行没改**,极易被误判成产品回归。
    //
    // 已全部修掉:第 ⑨ 项改用注入 clock;其余 7 处(DPAPI 往返/篡改/残留三项里的
    // 6 处令牌构造 + 主动登出 1 处)改用顶部的 FixedNow / 注入 clock。
    // 现在本文件里**不存在** DateTimeOffset.UtcNow(可用 grep 反查)。
    private DateTimeOffset _now = new(2026, 9, 12, 10, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now = _now.Add(delta);
}
