// REST/WebDAV 客户端封装(DE-D-05)。
//
// 这一层要替所有调用方**一次性**解决四件事,否则这四件事会在每个调用点各写一遍、
// 且总会有一处写错:
//
// ①**鉴权头**:每跳都从 <see cref="ITokenProvider"/> 取 access(它负责必要的刷新)。
// ②**`base_version` 乐观锁**:改名/移动/删除等写操作必须带客户端手上的版本号,
//   漏带就等于放弃乐观锁(服务端按 0 处理 = 不校验)—— 这种"少传一个字段"的
//   错误不会报错,只会让并发覆盖静默发生。所以这里把它做成**参数**而不是让调用方
//   自己拼 body:调用方必须显式给出 `baseVersion`,想跳过就得写 `null` 并说明理由。
// ③**409/412 的语义解析**:服务端把冲突原因放在 `details.reason`
//   (version_conflict / name_conflict / target_is_directory / root_protected /
//   dir_op_in_progress)。**这几种的处置完全不同**:版本冲突要刷新后重试、重名要
//   改名、`dir_op_in_progress` **绝不能重试**(必然还是 409,直到异步任务结束)。
//   把 HTTP 409 一把抓是这类客户端最常见的错,所以这里解析成 <see cref="ApiConflictReason"/>。
// ④**429/5xx 退避重试**:限速要尊重 `Retry-After`;5xx 指数退避 + 抖动。
//   但**只对幂等方法自动重试** —— 对 POST 自动重试可能造成重复上传/重复创建,
//   那比"失败一次"严重得多(见 <see cref="ApiClientOptions.RetryNonIdempotent"/>)。

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetDisk.Transport;

/// <summary>409 冲突的原因(与服务端 <c>details.reason</c> 一一对应,见契约 6.11/6.7)。</summary>
public enum ApiConflictReason
{
    /// <summary>服务端没给 reason(或给了未知值)——调用方应按"一般冲突"处理。</summary>
    Unknown,

    /// <summary>版本冲突:手上这份已过期,刷新后重试。</summary>
    VersionConflict,

    /// <summary>同目录重名:需要改名或覆盖确认。</summary>
    NameConflict,

    /// <summary>目标其实是目录(或反之):客户端请求语义与远端不符。</summary>
    TargetIsDirectory,

    /// <summary>根目录受保护:改名/移动/删除根目录一律拒绝。</summary>
    RootProtected,

    /// <summary>
    /// 子树上有异步目录任务在跑(6.11)。
    /// **不要重试**:重试必然还是 409,应当提示"稍后再试"并刷新列表。
    /// </summary>
    DirOpInProgress,
}

/// <summary>API 失败(带 HTTP 状态、业务码、冲突原因与 <c>request_id</c>)。</summary>
public sealed class ApiException : Exception
{
    public ApiException(HttpStatusCode status, string code, string message,
        ApiConflictReason reason = ApiConflictReason.Unknown, string? requestId = null)
        : base(message)
    {
        Status = status;
        Code = code;
        Reason = reason;
        RequestId = requestId;
    }

    public HttpStatusCode Status { get; }

    /// <summary>服务端业务码(契约 6.7 的 <c>code</c>)。</summary>
    public string Code { get; }

    /// <summary>冲突原因(仅 409 有意义)。</summary>
    public ApiConflictReason Reason { get; }

    /// <summary>可定位到那次请求的 id(R-23:排障时手上只有它)。</summary>
    public string? RequestId { get; }

    /// <summary>412 条件请求失败(WebDAV 的 If-Match,或 REST 的乐观锁)。</summary>
    public bool IsPreconditionFailed => Status == HttpStatusCode.PreconditionFailed;

    /// <summary>空间被移出/解散或冻结(403/410 <c>space_revoked</c>)——**必须保留本地数据**。</summary>
    public bool IsSpaceRevoked => Code == "space_revoked";

    /// <summary>空间已不存在(410 <c>space_gone</c>)。</summary>
    public bool IsSpaceGone => Code == "space_gone";

    /// <summary>登录过期/被吊销:交给会话层刷新或静默退出。</summary>
    public bool IsTokenExpired => Code == "token_expired";

    public bool IsTokenRevoked => Code == "token_revoked";

    /// <summary>
    /// 是否值得重试:**只认可枚举的"暂时性"状态**(429 / 500 / 502 / 503 / 504)。
    ///
    /// 为什么不用 `(int)Status >= 500` 这种范围判断(实测踩到):507 `InsufficientStorage`
    /// 落在这个范围里,而它**永远重试不好**(额度不足/磁盘水位),重试还会反复触发预留额度检查;
    /// 501(未实现)、505(版本不支持)同理 —— 重试只是把一次可读的失败变成三次。
    /// 5xx 是个"类别",不是"暂时性"的同义词,所以判定必须白名单化。
    /// </summary>
    public bool IsTransient => IsRetryableStatus(Status);

    /// <summary>可重试的 HTTP 状态(白名单;见 <see cref="IsTransient"/> 的说明)。</summary>
    public static bool IsRetryableStatus(HttpStatusCode status) => status is
        HttpStatusCode.TooManyRequests
        or HttpStatusCode.InternalServerError
        or HttpStatusCode.BadGateway
        or HttpStatusCode.ServiceUnavailable
        or HttpStatusCode.GatewayTimeout;
}

/// <summary>客户端行为开关(退避与重试)。</summary>
public sealed record ApiClientOptions
{
    /// <summary>最大尝试次数(含首次)。</summary>
    public int MaxAttempts { get; init; } = 4;

    /// <summary>首次退避时长(之后指数增长)。</summary>
    public TimeSpan BaseBackoff { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>退避上限(避免长时间挂住同步线程)。</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>
    /// 是否对**非幂等**方法(POST/PATCH)也自动重试。默认 false。
    ///
    /// 为什么默认 false:POST /upload/create、POST /spaces 这类请求重试一次就可能
    /// 建出两份东西;而"失败一次、由上层的用例显式重试"是可控的(用例知道自己在做什么)。
    /// </summary>
    public bool RetryNonIdempotent { get; init; }

    /// <summary>退避延迟函数(测试注入零延迟用)。参数是"第几次重试(从 0 开始)"与建议延迟。</summary>
    public Func<int, TimeSpan, CancellationToken, Task>? DelayAsync { get; init; }
}

/// <summary>REST 客户端(所有 REST 调用都从这里走;TUS 见 DE-D-06,SSE 见 DE-D-13)。</summary>
public sealed class ApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly ClientOptions _options;
    private readonly ITokenProvider? _tokens;
    private readonly ApiClientOptions _retry;
    private readonly TimeProvider _clock;

    public ApiClient(
        ClientOptions options,
        ITokenProvider? tokens = null,
        HttpMessageHandler? handler = null,
        ApiClientOptions? retry = null,
        TimeProvider? clock = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _tokens = tokens;
        _retry = retry ?? new ApiClientOptions();
        _clock = clock ?? TimeProvider.System;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = options.RequestTimeout;
    }

    /// <summary>底层 HttpClient(WebDAV/TUS 复用同一条连接池)。</summary>
    public HttpClient Http => _http;

    /// <summary>
    /// 原始请求(自定义请求头 + <b>可重建的 body 工厂</b>)。TUS 分片、SSE、下载都走这里。
    ///
    /// 为什么 body 是**工厂**而不是 <see cref="HttpContent"/> 实例:<see cref="HttpContent"/>
    /// 不能重复发送(重试复用同一实例会抛 InvalidOperationException,而且只会在"重试"
    /// 这条不常走的路径上暴露)。退避重试与分片续传都必须能重建 body,所以签名上就要求
    /// 一个工厂 —— 让"重试时 body 已失效"这类问题在编译期就无从发生。
    ///
    /// <paramref name="idempotent"/> 为 false 时不做自动重试(建资源类请求由调用方决定)。
    /// </summary>
    public async Task<HttpResponseMessage> SendRawAsync(
        HttpMethod method,
        string path,
        Func<HttpContent>? contentFactory = null,
        IReadOnlyDictionary<string, string>? headers = null,
        bool idempotent = true,
        CancellationToken ct = default)
    {
        var attempt = 0;
        while (true)
        {
            using var req = new HttpRequestMessage(method, new Uri(_options.BaseAddress, path));
            if (contentFactory is not null)
            {
                req.Content = contentFactory();
            }
            if (headers is not null)
            {
                foreach (var (k, v) in headers)
                {
                    req.Headers.TryAddWithoutValidation(k, v);
                }
            }
            if (_tokens is not null)
            {
                var token = await _tokens.GetAccessTokenAsync(ct).ConfigureAwait(false);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            HttpResponseMessage resp;
            try
            {
                resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt + 1 < _retry.MaxAttempts && idempotent)
            {
                await BackoffAsync(attempt, null, ct).ConfigureAwait(false);
                attempt++;
                continue;
            }

            if (!ShouldRetry(resp.StatusCode, idempotent) || attempt + 1 >= _retry.MaxAttempts)
            {
                return resp;
            }

            var retryAfter = ParseRetryAfter(resp);
            resp.Dispose();
            await BackoffAsync(attempt, retryAfter, ct).ConfigureAwait(false);
            attempt++;
        }
    }

    /// <summary>GET 并解析 JSON。</summary>
    public Task<T> GetAsync<T>(string path, CancellationToken ct = default)
        => SendJsonAsync<T>(HttpMethod.Get, path, body: null, baseVersion: null, ct);

    /// <summary>POST JSON(带可选 <paramref name="baseVersion"/> 乐观锁)。</summary>
    public Task<T> PostAsync<T>(string path, object? body, CancellationToken ct = default)
        => SendJsonAsync<T>(HttpMethod.Post, path, body, baseVersion: null, ct);

    /// <summary>PATCH JSON,自动注入 <c>base_version</c>。</summary>
    public Task<T> PatchAsync<T>(string path, object? body, long? baseVersion, CancellationToken ct = default)
        => SendJsonAsync<T>(HttpMethod.Patch, path, body, baseVersion, ct);

    /// <summary>DELETE。**刻意不接受 <c>base_version</c>**:契约(6.3)的删除操作没有这个字段,
    /// 服务端也不读它 —— 硬塞一个服务端会忽略的参数,正是订正④记录过的那类缺陷
    /// ("按自己的想象发请求,服务端静默忽略,拿到另一份结果却不报错")。
    /// 删除的并发保护由服务端自己的口径承担(子树保护 + <c>dir_op_in_progress</c> + 根目录保护);
    /// 若将来确实需要"删除的乐观锁",必须先改契约再加参数。</summary>
    public Task<T> DeleteAsync<T>(string path, CancellationToken ct = default)
        => SendJsonAsync<T>(HttpMethod.Delete, path, body: null, baseVersion: null, ct);

    /// <summary>WebDAV 请求(带 ETag 条件;412 走 <see cref="ApiException.IsPreconditionFailed"/>)。</summary>
    public async Task<HttpResponseMessage> WebDavAsync(
        string method, string path, string? ifMatch = null, HttpContent? content = null,
        CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(new HttpMethod(method), new Uri(_options.BaseAddress, path));
        req.Content = content;
        if (!string.IsNullOrEmpty(ifMatch))
        {
            // 强 ETag 是 6.6 乐观锁在 WebDAV 侧的落点:If-Match 不匹配 → 412
            req.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }
        var resp = await SendWithRetryAsync(req, idempotent: true, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode && (int)resp.StatusCode != 207) // 207 = Multi-Status(PROPFIND)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw ParseError(resp.StatusCode, body);
        }
        return resp;
    }

    private async Task<T> SendJsonAsync<T>(HttpMethod method, string path, object? body,
        long? baseVersion, CancellationToken ct)
    {
        var payload = BuildBody(body, baseVersion);
        var idempotent = method == HttpMethod.Get || method == HttpMethod.Head || method == HttpMethod.Delete;

        var req = new HttpRequestMessage(method, new Uri(_options.BaseAddress, path));
        if (payload is not null)
        {
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        }

        HttpResponseMessage resp;
        try
        {
            resp = await SendWithRetryAsync(req, idempotent, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ApiException(HttpStatusCode.ServiceUnavailable, "network", $"连接服务端失败:{ex.Message}");
        }
        using var _ = resp;

        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (resp.StatusCode == HttpStatusCode.Unauthorized && _tokens is not null)
        {
            // access 可能在服务端被提前吊销:让会话层刷新一次再重放**一次**。
            // 只重放一次是刻意的 —— 无限重放会在"服务端持续 401"时变成死循环。
            // 而 **token_revoked 一律不重放**:重放已作废的凭据只会再拿一次 401,
            // 正确动作是让会话层静默退出(DE-D-04 ③)。
            var err = TryParseError(resp.StatusCode, text);
            if (err is null || !err.IsTokenRevoked)
            {
                _tokens.OnUnauthorized();
                using var resp2 = await SendRawAsync(method, path,
                    payload is null ? null : () => new StringContent(payload, Encoding.UTF8, "application/json"),
                    headers: null, idempotent: idempotent, ct: ct).ConfigureAwait(false);
                var text2 = await resp2.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!resp2.IsSuccessStatusCode)
                {
                    throw ParseError(resp2.StatusCode, text2);
                }
                return Deserialize<T>(text2);
            }
            throw err;
        }

        if (!resp.IsSuccessStatusCode)
        {
            throw ParseError(resp.StatusCode, text);
        }
        return Deserialize<T>(text);
    }

    /// <summary>把 <c>base_version</c> 注入到 body(契约:写操作的 body 字段,不是查询串)。</summary>
    private static string? BuildBody(object? body, long? baseVersion)
    {
        if (body is null && baseVersion is null)
        {
            return null;
        }
        JsonObject obj;
        if (body is null)
        {
            obj = new JsonObject();
        }
        else
        {
            var node = JsonSerializer.SerializeToNode(body, JsonOptions);
            obj = node as JsonObject ?? throw new ArgumentException("请求体必须是 JSON 对象", nameof(body));
        }
        if (baseVersion is { } v)
        {
            obj["base_version"] = v;
        }
        return obj.ToJsonString(JsonOptions);
    }

    private Task<HttpResponseMessage> SendWithRetryAsync(
        HttpRequestMessage template, bool idempotent, CancellationToken ct)
    {
        // 与 SendRawAsync 共用同一条退避路径(只多一步"从模板取出头与 body"),
        // 免得两处各写一遍重试策略 —— 那样必然会在某次修改后只改对一处。
        var headers = template.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value));
        var payload = template.Content is StringContent sc
            ? sc.ReadAsStringAsync().GetAwaiter().GetResult()
            : null;
        return SendRawAsync(template.Method, template.RequestUri!.PathAndQuery,
            payload is null ? null : () => new StringContent(payload, Encoding.UTF8, "application/json"),
            headers, idempotent, ct);
    }

    private bool ShouldRetry(HttpStatusCode status, bool idempotent)
    {
        if (!idempotent && !_retry.RetryNonIdempotent)
        {
            return false;
        }
        return ApiException.IsRetryableStatus(status);
    }

    private async Task BackoffAsync(int attempt, TimeSpan? retryAfter, CancellationToken ct)
    {
        // 服务端明确给了 Retry-After 就听它的(限速窗口是服务端说了算);
        // 否则指数退避 + **抖动**:没有抖动时一群客户端会在同一毫秒齐刷刷重试,
        // 把一个"缓慢恢复中"的服务端再打垮一次。
        var delay = retryAfter ?? TimeSpan.FromMilliseconds(
            Math.Min(
                _retry.MaxBackoff.TotalMilliseconds,
                _retry.BaseBackoff.TotalMilliseconds * Math.Pow(2, attempt)));

        if (retryAfter is null)
        {
            var jitter = Random.Shared.NextDouble() * 0.3; // 0~30%
            delay += TimeSpan.FromMilliseconds(delay.TotalMilliseconds * jitter);
        }

        if (_retry.DelayAsync is not null)
        {
            await _retry.DelayAsync(attempt, delay, ct).ConfigureAwait(false);
            return;
        }
        await Task.Delay(delay, _clock, ct).ConfigureAwait(false);
    }

    private TimeSpan? ParseRetryAfter(HttpResponseMessage resp)
    {
        var ra = resp.Headers.RetryAfter;
        if (ra is null)
        {
            return null;
        }
        if (ra.Delta is { } delta)
        {
            return delta;
        }
        if (ra.Date is { } date)
        {
            var diff = date - _clock.GetUtcNow();
            return diff > TimeSpan.Zero ? diff : TimeSpan.Zero;
        }
        return null;
    }

    private static T Deserialize<T>(string text)
    {
        if (typeof(T) == typeof(string))
        {
            return (T)(object)text;
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            return default!;
        }
        try
        {
            return JsonSerializer.Deserialize<T>(text, JsonOptions)!;
        }
        catch (JsonException ex)
        {
            throw new ApiException(HttpStatusCode.OK, "invalid_response", $"响应不是合法 JSON:{ex.Message}");
        }
    }

    /// <summary>解析错误响应体(契约 6.7:结构化 400 + <c>details.reason</c> + <c>request_id</c>)。</summary>
    public static ApiException ParseError(HttpStatusCode status, string body)
    {
        var err = TryParseError(status, body);
        return err ?? new ApiException(status, "unknown", $"HTTP {(int)status}", requestId: null);
    }

    private static ApiException? TryParseError(HttpStatusCode status, string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var code = root.TryGetProperty("code", out var c) ? c.GetString() ?? "unknown" : "unknown";
            var message = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            var requestId = root.TryGetProperty("request_id", out var r) ? r.GetString() : null;

            var reason = ApiConflictReason.Unknown;
            if (root.TryGetProperty("details", out var details)
                && details.ValueKind == JsonValueKind.Object
                && details.TryGetProperty("reason", out var reasonEl))
            {
                reason = MapReason(reasonEl.GetString());
            }
            return new ApiException(status, code, message, reason, requestId);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ApiConflictReason MapReason(string? reason) => reason switch
    {
        "version_conflict" => ApiConflictReason.VersionConflict,
        "name_conflict" => ApiConflictReason.NameConflict,
        "target_is_directory" => ApiConflictReason.TargetIsDirectory,
        "root_protected" => ApiConflictReason.RootProtected,
        "dir_op_in_progress" => ApiConflictReason.DirOpInProgress,
        _ => ApiConflictReason.Unknown,
    };
}
