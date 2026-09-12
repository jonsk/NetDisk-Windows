// DE-D-05 行为检查器:REST/WebDAV 客户端封装的三个"静默出错点"。
//
// 用法:`dotnet run --project desktop/tests/ApiClientCheck -c Release`
//
// 为什么是这三类断言:
//   ① **base_version 注入**:漏带它不会报错 —— 服务端按 0 处理 = 放弃乐观锁,
//      并发覆盖会静默发生。所以必须断言"发送出去的 JSON 里真的有这个字段",
//      而不是断言"客户端有个参数"。
//   ② **409 的 reason 解析**:version_conflict 要刷新后重试、name_conflict 要改名、
//      dir_op_in_progress **绝不能重试**(必须等异步任务结束)。把 409 一把抓是这类
//      客户端最常见的错,而后果是把"等一会儿就好"变成"永远失败"。
//   ③ **重试边界**:429/5xx 要退避重试并尊重 Retry-After;4xx(除 429)不重试;
//      **POST 默认不重试**(重试一次就可能建出两份资源)。

using System.Net;
using System.Text;
using System.Text.Json;
using NetDisk.Transport;

var checks = new List<(string Name, Func<Task> Run)>
{
    ("① PATCH 自动注入 base_version", CheckBaseVersionInjectedAsync),
    ("② 不带 base_version 时不得出现该字段", CheckNoBaseVersionWhenNullAsync),
    ("③ DELETE 不发明服务端不认的参数", CheckDeleteHasNoInventedParamsAsync),
    ("④ 409 version_conflict → 类型化 reason", CheckReasonVersionConflictAsync),
    ("⑤ 409 name_conflict / root_protected / target_is_directory", CheckOtherReasonsAsync),
    ("⑥ 409 dir_op_in_progress 被识别(且不重试)", CheckDirOpReasonAsync),
    ("⑦ 412 条件失败可判定", CheckPreconditionFailedAsync),
    ("⑧ 429 尊重 Retry-After 并重试到成功", CheckRetryAfterAsync),
    ("⑨ 5xx 指数退避重试,最终成功", CheckRetry5xxAsync),
    ("⑩ 400/404 不重试(只发一次)", CheckNoRetryOn4xxAsync),
    ("⑪ POST 默认不重试(避免重复建资源)", CheckPostNotRetriedAsync),
    ("⑫ 携带 Bearer 令牌", CheckAuthHeaderAsync),
    ("⑬ 401 token_expired → 刷新并重放一次", CheckReplayAfterExpiredAsync),
    ("⑭ 401 token_revoked → 不重放", CheckNoReplayAfterRevokedAsync),
    ("⑮ WebDAV If-Match → 412 结构化异常", CheckWebDavIfMatchAsync),
    ("⑯ request_id 带进异常(排障用)", CheckRequestIdAsync),
    ("⑰ 507/501 不算暂时性(白名单而非 >=500)", CheckNotRetryable5xxAsync),
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
    Console.WriteLine($"全部通过:{checks.Count} 项 DE-D-05 行为断言(base_version / 409·412 解析 / 退避重试)");
    return 0;
}
Console.Error.WriteLine($"{failed}/{checks.Count} 项失败");
return 1;

// ---------------------------------------------------------------- 检查

static async Task CheckBaseVersionInjectedAsync()
{
    var h = new Stub();
    h.Enqueue(HttpStatusCode.OK, "{\"id\":\"f1\",\"version\":8}");
    var api = h.Client();

    await api.PatchAsync<JsonElement>("/api/v1/files/f1/rename",
        new { name = "新名字.txt" }, baseVersion: 7);

    // 注意:不能断言"body 里出现中文字面量" —— System.Text.Json 会把非 ASCII 转义成
    // \uXXXX(这是**正确**的 JSON,服务端解得开)。断言要落在**解析后的字段值**上,
    // 否则测的是序列化风格而不是语义(第一次跑就是这么误报的)。
    using var doc = JsonDocument.Parse(h.LastBody());
    var root = doc.RootElement;
    Assert(root.GetProperty("base_version").GetInt64() == 7,
        $"请求体里必须带 base_version=7,实际:{h.LastBody()}");
    Assert(root.GetProperty("name").GetString() == "新名字.txt",
        $"原有的 body 字段不能被丢掉或改值,实际:{h.LastBody()}");
    Assert(root.EnumerateObject().Count() == 2,
        $"只应追加 base_version,不该多出别的字段,实际:{h.LastBody()}");
}

static async Task CheckNoBaseVersionWhenNullAsync()
{
    var h = new Stub();
    h.Enqueue(HttpStatusCode.OK, "{}");
    var api = h.Client();
    await api.PatchAsync<JsonElement>("/api/v1/files/f1/rename", new { name = "a.txt" }, baseVersion: null);
    var body = h.LastBody();
    Assert(!body.Contains("base_version"), $"没给版本号时不该凭空构造 base_version,实际:{body}");
}

static async Task CheckDeleteHasNoInventedParamsAsync()
{
    // 契约(6.3)的删除操作**没有** base_version,服务端也不读它。
    // 这里钉住"客户端不擅自发明参数":多带一个服务端会忽略的参数,正是订正④里
    // 那类缺陷的样子(请求看起来成功、语义却不是你以为的那个)。
    var h = new Stub();
    h.Enqueue(HttpStatusCode.OK, "{}");
    var api = h.Client();
    await api.DeleteAsync<JsonElement>("/api/v1/files/f1");
    var uri = h.LastUri();
    Assert(string.IsNullOrEmpty(uri.Query), $"DELETE 不应附加任何查询串,实际:{uri}");
}

static async Task CheckReasonVersionConflictAsync()
{
    var h = new Stub();
    h.Enqueue(HttpStatusCode.Conflict, Error("conflict", "该条目已被他人修改", "version_conflict", "req-1"));
    var api = h.Client();
    var ex = await Catch<ApiException>(() => api.PatchAsync<JsonElement>("/x", new { }, 1));
    Assert(ex.Status == HttpStatusCode.Conflict, $"状态应为 409,实际 {ex.Status}");
    Assert(ex.Reason == ApiConflictReason.VersionConflict, $"reason 应为 VersionConflict,实际 {ex.Reason}");
    Assert(ex.RequestId == "req-1", $"request_id 应被带出,实际 {ex.RequestId}");
}

static async Task CheckOtherReasonsAsync()
{
    var cases = new (string Reason, ApiConflictReason Want)[]
    {
        ("name_conflict", ApiConflictReason.NameConflict),
        ("root_protected", ApiConflictReason.RootProtected),
        ("target_is_directory", ApiConflictReason.TargetIsDirectory),
        ("未来新增的原因", ApiConflictReason.Unknown),
    };
    foreach (var (reason, want) in cases)
    {
        var h = new Stub();
        h.Enqueue(HttpStatusCode.Conflict, Error("conflict", "x", reason, null));
        var api = h.Client();
        var ex = await Catch<ApiException>(() => api.DeleteAsync<JsonElement>("/x"));
        Assert(ex.Reason == want, $"reason={reason} 应映射为 {want},实际 {ex.Reason}");
    }
}

static async Task CheckDirOpReasonAsync()
{
    var h = new Stub();
    h.Enqueue(HttpStatusCode.Conflict, Error("dir_op_in_progress", "该目录上已有进行中的目录操作", "dir_op_in_progress", null));
    var api = h.Client();
    var ex = await Catch<ApiException>(() => api.DeleteAsync<JsonElement>("/x"));
    Assert(ex.Reason == ApiConflictReason.DirOpInProgress, $"应识别 dir_op_in_progress,实际 {ex.Reason}");
    // 409 不是"可重试"的:IsTransient 只认 429/5xx —— 这条断言防止有人把 409 也归进重试
    Assert(!ex.IsTransient, "409 必须**不可**重试(重试必然还是 409)");
    Assert(h.CallCount == 1, $"不应重试,实际发了 {h.CallCount} 次");
}

static async Task CheckPreconditionFailedAsync()
{
    var h = new Stub();
    h.Enqueue(HttpStatusCode.PreconditionFailed, Error("precondition_failed", "版本不匹配", null, null));
    var api = h.Client();
    var ex = await Catch<ApiException>(() => api.PatchAsync<JsonElement>("/x", new { }, 2));
    Assert(ex.IsPreconditionFailed, $"412 应可判定,实际 status={ex.Status}");
}

static async Task CheckRetryAfterAsync()
{
    var h = new Stub();
    h.RetryAfter = TimeSpan.FromSeconds(2);
    h.Enqueue(HttpStatusCode.TooManyRequests, Error("rate_limited", "太频繁", null, null));
    h.Enqueue(HttpStatusCode.OK, "{\"ok\":true}");

    var delays = new List<TimeSpan>();
    var api = h.Client(opts => opts with { DelayAsync = (_, d, _) => { delays.Add(d); return Task.CompletedTask; } });
    var res = await api.GetAsync<JsonElement>("/x");

    Assert(h.CallCount == 2, $"应重试一次,实际 {h.CallCount} 次");
    Assert(delays.Count == 1 && delays[0] == TimeSpan.FromSeconds(2),
        $"必须听服务端的 Retry-After(=2s),实际 {string.Join(",", delays)}");
    Assert(res.GetProperty("ok").GetBoolean(), "重试后应拿到成功结果");
}

static async Task CheckRetry5xxAsync()
{
    var h = new Stub();
    h.Enqueue(HttpStatusCode.BadGateway, Error("bad_gateway", "x", null, null));
    h.Enqueue(HttpStatusCode.ServiceUnavailable, Error("unavailable", "x", null, null));
    h.Enqueue(HttpStatusCode.OK, "{\"ok\":1}");

    var delays = new List<TimeSpan>();
    var api = h.Client(opts => opts with { DelayAsync = (_, d, _) => { delays.Add(d); return Task.CompletedTask; } });
    await api.GetAsync<JsonElement>("/x");

    Assert(h.CallCount == 3, $"5xx 应重试到成功,实际 {h.CallCount} 次");
    Assert(delays.Count == 2, $"应有两次退避,实际 {delays.Count}");
    Assert(delays[1] > delays[0], $"退避应当是指数增长,实际 {delays[0]} → {delays[1]}");
}

static async Task CheckNoRetryOn4xxAsync()
{
    var h = new Stub();
    h.Enqueue(HttpStatusCode.NotFound, Error("not_found", "文件不存在", null, null));
    var api = h.Client(opts => opts with { DelayAsync = (_, _, _) => Task.CompletedTask });
    await Catch<ApiException>(() => api.GetAsync<JsonElement>("/x"));
    Assert(h.CallCount == 1, $"404 不该重试,实际 {h.CallCount} 次");

    var h2 = new Stub();
    h2.Enqueue(HttpStatusCode.BadRequest, Error("invalid_argument", "参数不对", null, null));
    var api2 = h2.Client(opts => opts with { DelayAsync = (_, _, _) => Task.CompletedTask });
    await Catch<ApiException>(() => api2.PostAsync<JsonElement>("/x", new { }));
    Assert(h2.CallCount == 1, $"400 不该重试,实际 {h2.CallCount} 次");
}

static async Task CheckPostNotRetriedAsync()
{
    var h = new Stub();
    // 连续 5xx:POST 不该被自动重试(重试一次就可能建出两份资源)
    for (var i = 0; i < 5; i++)
    {
        h.Enqueue(HttpStatusCode.InternalServerError, Error("internal_error", "x", null, null));
    }
    var api = h.Client(opts => opts with { DelayAsync = (_, _, _) => Task.CompletedTask });
    await Catch<ApiException>(() => api.PostAsync<JsonElement>("/api/v1/spaces", new { name = "s" }));
    Assert(h.CallCount == 1, $"POST 默认不应重试,实际 {h.CallCount} 次");

    // 但显式打开开关后应重试(留给"服务端保证幂等"的端点)
    var h2 = new Stub();
    h2.Enqueue(HttpStatusCode.InternalServerError, Error("internal_error", "x", null, null));
    h2.Enqueue(HttpStatusCode.OK, "{\"id\":\"s1\"}");
    var api2 = h2.Client(opts => opts with
    {
        RetryNonIdempotent = true,
        DelayAsync = (_, _, _) => Task.CompletedTask,
    });
    await api2.PostAsync<JsonElement>("/api/v1/spaces", new { name = "s" });
    Assert(h2.CallCount == 2, $"显式允许后 POST 应重试,实际 {h2.CallCount} 次");
}

static async Task CheckAuthHeaderAsync()
{
    var h = new Stub();
    h.Enqueue(HttpStatusCode.OK, "{}");
    var api = h.Client(tokens: new StaticTokens("ACCESS-123"));
    await api.GetAsync<JsonElement>("/x");
    Assert(h.LastAuthorization() == "Bearer ACCESS-123", $"应带 Bearer 令牌,实际 {h.LastAuthorization()}");
}

static async Task CheckReplayAfterExpiredAsync()
{
    var h = new Stub();
    h.Enqueue(HttpStatusCode.Unauthorized, Error("token_expired", "登录已过期", null, null));
    h.Enqueue(HttpStatusCode.OK, "{\"ok\":true}");

    var tokens = new StaticTokens("old-token");
    var api = h.Client(opts => opts with { DelayAsync = (_, _, _) => Task.CompletedTask }, tokens: tokens);
    var res = await api.GetAsync<JsonElement>("/x");

    Assert(res.GetProperty("ok").GetBoolean(), "刷新后应能拿到结果");
    Assert(tokens.UnauthorizedCalls == 1, $"应通知会话层刷新一次,实际 {tokens.UnauthorizedCalls}");
    Assert(h.CallCount == 2, $"应重放一次(且只一次),实际 {h.CallCount}");
}

static async Task CheckNoReplayAfterRevokedAsync()
{
    var h = new Stub();
    h.Enqueue(HttpStatusCode.Unauthorized, Error("token_revoked", "登录状态已失效", null, null));

    var tokens = new StaticTokens("old-token");
    var api = h.Client(opts => opts with { DelayAsync = (_, _, _) => Task.CompletedTask }, tokens: tokens);
    var ex = await Catch<ApiException>(() => api.GetAsync<JsonElement>("/x"));

    Assert(ex.IsTokenRevoked, $"应把 token_revoked 抛出来,实际 code={ex.Code}");
    Assert(tokens.UnauthorizedCalls == 0, "被吊销不该让会话层去刷新(重放已作废凭据只会再 401)");
    Assert(h.CallCount == 1, $"不该重放,实际 {h.CallCount} 次");
}

static async Task CheckWebDavIfMatchAsync()
{
    var h = new Stub();
    h.Enqueue(HttpStatusCode.PreconditionFailed, Error("precondition_failed", "ETag 不匹配", null, null));
    var api = h.Client(opts => opts with { DelayAsync = (_, _, _) => Task.CompletedTask });

    var ex = await Catch<ApiException>(() =>
        api.WebDavAsync("PUT", "/webdav/space/f.txt", ifMatch: "\"00000008-deadbeef\"",
            content: new StringContent("x")));
    Assert(ex.IsPreconditionFailed, $"WebDAV 条件失败应可判定,实际 {ex.Status}");
    Assert(h.LastIfMatch() == "\"00000008-deadbeef\"", $"If-Match 头应原样发出,实际 {h.LastIfMatch()}");
}

static async Task CheckNotRetryable5xxAsync()
{
    // `(int)Status >= 500` 这种范围判断会把 507(InsufficientStorage)、501、505
    // 一起当成"暂时性" —— 而它们**永远重试不好**,507 还会反复触发预留额度检查。
    // 这条断言把"5xx 是类别、不是暂时性的同义词"钉住(TUS 检查器先暴露的缺陷)。
    Assert(!ApiException.IsRetryableStatus(HttpStatusCode.InsufficientStorage), "507 不该被重试");
    Assert(!ApiException.IsRetryableStatus(HttpStatusCode.NotImplemented), "501 不该被重试");
    Assert(!ApiException.IsRetryableStatus(HttpStatusCode.HttpVersionNotSupported), "505 不该被重试");
    Assert(ApiException.IsRetryableStatus(HttpStatusCode.TooManyRequests), "429 该被重试");
    Assert(ApiException.IsRetryableStatus(HttpStatusCode.InternalServerError), "500 该被重试");
    Assert(ApiException.IsRetryableStatus(HttpStatusCode.BadGateway), "502 该被重试");
    Assert(ApiException.IsRetryableStatus(HttpStatusCode.ServiceUnavailable), "503 该被重试");
    Assert(ApiException.IsRetryableStatus(HttpStatusCode.GatewayTimeout), "504 该被重试");

    // 行为层也要一致:507 只发一次
    var h = new Stub();
    h.Enqueue(HttpStatusCode.InsufficientStorage, Error("storage_full", "磁盘水位", null, null));
    var api = h.Client(opts => opts with { DelayAsync = (_, _, _) => Task.CompletedTask });
    await Catch<ApiException>(() => api.GetAsync<JsonElement>("/x"));
    Assert(h.CallCount == 1, $"507 不该重试,实际发了 {h.CallCount} 次");
}

static async Task CheckRequestIdAsync()
{
    var h = new Stub();
    h.Enqueue(HttpStatusCode.InternalServerError, Error("internal_error", "服务器内部错误", null, "req-abc-123"));
    var api = h.Client(opts => opts with { DelayAsync = (_, _, _) => Task.CompletedTask });
    var ex = await Catch<ApiException>(() => api.GetAsync<JsonElement>("/x"));
    Assert(ex.RequestId == "req-abc-123", $"request_id 必须带出来(排障时手上只有它),实际 {ex.RequestId}");
}

// ---------------------------------------------------------------- 工具

static string Error(string code, string message, string? reason, string? requestId)
{
    var details = reason is null ? "" : ",\"details\":{\"reason\":\"" + reason + "\"}";
    var rid = requestId is null ? "" : ",\"request_id\":\"" + requestId + "\"";
    return "{\"code\":\"" + code + "\",\"message\":\"" + message + "\"" + details + rid + "}";
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new Exception(message);
    }
}

static async Task<T> Catch<T>(Func<Task> action) where T : Exception
{
    try
    {
        await action();
    }
    catch (T ex)
    {
        return ex;
    }
    throw new Exception($"预期抛出 {typeof(T).Name},但没有");
}

// ---------------------------------------------------------------- 桩

/// <summary>按脚本返回响应的 HTTP 桩,并记录每次请求的 body / 头 / URI。</summary>
sealed class Stub : HttpMessageHandler
{
    private readonly List<(HttpStatusCode Status, string Body)> _responses = new();
    private readonly List<string> _bodies = new();
    private readonly List<string> _auth = new();
    private readonly List<string> _ifMatch = new();
    private readonly List<Uri> _uris = new();

    public int CallCount { get; private set; }

    public TimeSpan? RetryAfter { get; set; }

    public void Enqueue(HttpStatusCode status, string body) => _responses.Add((status, body));

    public string LastBody() => _bodies.Count > 0 ? _bodies[^1] : "";

    public string LastAuthorization() => _auth.Count > 0 ? _auth[^1] : "";

    public string LastIfMatch() => _ifMatch.Count > 0 ? _ifMatch[^1] : "";

    public Uri LastUri() => _uris[^1];

    public ApiClient Client(Func<ApiClientOptions, ApiClientOptions>? tweak = null, ITokenProvider? tokens = null)
    {
        var opts = new ApiClientOptions { DelayAsync = (_, _, _) => Task.CompletedTask };
        if (tweak is not null)
        {
            opts = tweak(opts);
        }
        return new ApiClient(
            new ClientOptions { BaseAddress = new Uri("http://127.0.0.1:9/") },
            tokens, this, opts);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        CallCount++;
        _uris.Add(request.RequestUri!);
        _auth.Add(request.Headers.Authorization?.ToString() ?? "");
        _ifMatch.Add(request.Headers.TryGetValues("If-Match", out var v) ? string.Join(",", v) : "");
        _bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));

        var idx = Math.Min(CallCount - 1, _responses.Count - 1);
        var (status, body) = _responses.Count == 0 ? (HttpStatusCode.OK, "{}") : _responses[idx];
        var resp = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (RetryAfter is { } ra)
        {
            resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(ra);
        }
        return resp;
    }
}

/// <summary>令牌桩:记录被要求刷新的次数(DE-D-05 的"401 后只重放一次"靠它断言)。</summary>
sealed class StaticTokens : ITokenProvider
{
    private readonly string _token;

    public StaticTokens(string token) => _token = token;

    public int UnauthorizedCalls { get; private set; }

    public ValueTask<string> GetAccessTokenAsync(CancellationToken ct) => ValueTask.FromResult(_token);

    public void OnUnauthorized() => UnauthorizedCalls++;
}
